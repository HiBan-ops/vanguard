using System;
using System.IO;
using System.Globalization;

#if SPT_CLIENT
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.OffRaid.Localization;
using Vanguard.Client.UI.OffRaid.Foundation;
using Vanguard.Client.UI.OffRaid.Panels;
using Vanguard.Client.UI.OffRaid.Inventory;
#endif

// Responsibility: owns the Off-Raid Vanguard screen lifecycle, navigation, refresh orchestration and presentation binding for Operator management workflows.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: panels render API/persistence truth and issue explicit commands; the controller does not invent career, billing, inventory, medical or contract state.
// Invariant: panel refreshes and inventory-mode transitions must restore the ordinary Tarkov menu/profile surface cleanly before normal player actions resume.

namespace Vanguard.Client.UI.OffRaid;

#if SPT_CLIENT
internal sealed class VanguardOffRaidUiController : MonoBehaviour
{
    private const string MenuButtonName = "Vanguard_OffRaid_MenuButton";
    private const string ScreenRootName = "Vanguard_OffRaid_ScreenRoot";
    private const int MaxActionButtons = 6;
    private const int MaxCardsPerPage = 8;
    private const float ContextActionFirstMinY = 0.310f;
    private const float ContextActionStepY = 0.040f;
    private const float ContextActionHeight = 0.034f;
    private const float CloseButtonBaseY = 0.045f;
    private const float TopButtonVisualMaxOffsetY = 0.050f;

    // Single visual source of truth for all Vanguard off-raid buttons.
    // Navigation buttons and contextual action buttons must use these values through CreateButton(),
    // otherwise small local overrides can make one panel diverge from the validated menu theme.
    private static readonly Color ButtonNormalBackgroundColor = new(0.045f, 0.052f, 0.044f, 0.58f);
    private static readonly Color ButtonHoverBackgroundColor = new(0.74f, 0.70f, 0.56f, 0.96f);
    private static readonly Color ButtonPressedBackgroundColor = new(0.60f, 0.56f, 0.44f, 1.00f);
    private static readonly Color ButtonSelectedBackgroundColor = new(0.74f, 0.70f, 0.56f, 0.86f);
    private static readonly Color ButtonDisabledBackgroundColor = new(0.035f, 0.040f, 0.034f, 0.46f);
    private static readonly Color ButtonNormalTextColor = new(0.92f, 0.94f, 0.84f, 1.00f);
    private static readonly Color ButtonDisabledTextColor = new(0.66f, 0.70f, 0.62f, 1.00f);
    private static readonly Color ButtonHoverTextColor = new(0.06f, 0.07f, 0.05f, 1.00f);
    private static readonly Color ButtonLineColor = new(0.68f, 0.72f, 0.62f, 0.20f);

    private readonly VanguardDashboardPanel dashboardPanel = new();
    private readonly VanguardContractsPanel contractsPanel = new();
    private readonly VanguardActiveServicePanel activeServicePanel = new();
    private readonly VanguardFieldHospitalPanel fieldHospitalPanel = new();
    private readonly VanguardBillingPanel billingPanel = new();
    private readonly VanguardOperatorDossierPanel dossierPanel = new();

    private VanguardApiClient? apiClient;
    private VanguardOperatorStateView state = VanguardOperatorStateView.Empty("not_loaded");
    private VanguardCanonicalOperatorState canonicalState = VanguardCanonicalOperatorState.Build(VanguardOperatorStateView.Empty("not_loaded"));
    private VanguardOffRaidIntegrityReport integrityReport = new();
    private VanguardOffRaidPanelKind currentPanel = VanguardOffRaidPanelKind.Dashboard;
    private string? selectedOperatorId;
    private bool actionInProgress;
    private string statusMessage = VanguardOperatorsLocalizationService.Get("general.ready");

    private readonly Dictionary<RectTransform, Vector2> originalMenuPositions = new();
    private Component? sourceButtonComponent;
    private Component? playerButtonComponent;
    private Component? tradeButtonComponent;
    private Component? hideoutButtonComponent;
    private Component? exitButtonComponent;
    private GameObject? menuButtonObject;
    private float enforceMenuLabelUntilRealtime;
    private GameObject? screenRoot;
    private GameObject? headerBandRoot;
    private GameObject? infoTableScrollRoot;
    private GameObject? infoTableRoot;
    private RectTransform? infoTableContentRect;
    private ScrollRect? infoTableScrollRect;
    private TextMeshProUGUI? titleLabel;
    private TextMeshProUGUI? subtitleLabel;
    private TextMeshProUGUI? bodyLabel;
    private TextMeshProUGUI? statusLabel;
    private TMP_FontAsset? inheritedFont;
    private GameObject? cardRoot;
    private GameObject? tooltipRoot;
    private TextMeshProUGUI? tooltipLabel;
    private GameObject? confirmationRoot;
    private TextMeshProUGUI? confirmationTitleLabel;
    private TextMeshProUGUI? confirmationBodyLabel;
    private TextMeshProUGUI? confirmationConfirmLabel;
    private Button? confirmationConfirmButton;
    private Button? confirmationCancelButton;
    private Action? pendingConfirmationAction;
    private string pendingConfirmationActionName = string.Empty;
    private readonly List<GameObject> cardObjects = new();
    private readonly List<GameObject> infoTableObjects = new();
    private readonly List<Button> actionButtons = new();
    // Direct references to the real "Label" child of each action button.
    // Do not resolve labels with GetComponentInChildren(includeInactive:true): the hover icon is also
    // a TextMeshProUGUI and can be returned first, which makes the label visible only on hover.
    private readonly List<TextMeshProUGUI> actionLabels = new();
    // Top navigation and other persistent controls survive panel re-renders. Keep their
    // localization keys so a locale change can refresh their labels without rebuilding the UI.
    private readonly Dictionary<TextMeshProUGUI, string> persistentLocalizedLabels = new();
    private readonly Dictionary<GameObject, bool> hiddenVanillaGameObjects = new();
    private readonly Dictionary<Behaviour, bool> hiddenVanillaBehaviours = new();
    private readonly Dictionary<string, Sprite?> portraitSpriteCache = new(StringComparer.Ordinal);
    private bool vanillaMenuHidden;
    private readonly Dictionary<GameObject, VanillaButtonVisualState> vanillaButtonVisualStates = new();

    // Static Operator portraits are grouped by gameplay role first and faction second. The pool shape is
    // intentionally future-proof: additional variants can be appended without changing selection semantics,
    // while a later procedural portrait generator can replace this resolver behind the same stable identity key.
    private static readonly Dictionary<string, string[]> OperatorPortraitResourcePools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["assault|bear"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_assault_bear_01.jpg" },
        ["assault|usec"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_assault_usec_01.jpg" },
        ["recon|bear"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_recon_bear_01.jpg" },
        ["recon|usec"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_recon_usec_01.jpg" },
        ["support|bear"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_support_bear_01.jpg" },
        ["support|usec"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_support_usec_01.jpg" },
        ["veteran|bear"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_veteran_bear_01.jpg" },
        ["veteran|usec"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_veteran_usec_01.jpg" },
        ["marksman|bear"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_marksman_bear_01.jpg" },
        ["marksman|usec"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_marksman_usec_01.jpg" },
        ["breacher|bear"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_breacher_bear_01.jpg" },
        ["breacher|usec"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_breacher_usec_01.jpg" },
        ["medic|bear"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_medic_bear_01.jpg" },
        ["medic|usec"] = new[] { "Vanguard.Client.Assets.Operators.Portraits.Role.vanguard_operator_medic_usec_01.jpg" }
    };

    public static void TryInitialize(object menuScreenInstance)
    {
        if (menuScreenInstance is not Component menuScreenComponent)
        {
            return;
        }

        VanguardOffRaidUiController controller = menuScreenComponent.gameObject.GetComponent<VanguardOffRaidUiController>();
        if (controller == null)
        {
            controller = menuScreenComponent.gameObject.AddComponent<VanguardOffRaidUiController>();
        }

        controller.Initialize(menuScreenComponent);
    }

    private void Initialize(Component menuScreenComponent)
    {
        try
        {
            apiClient ??= new VanguardApiClient();
            playerButtonComponent = ResolveMenuButtonComponent(menuScreenComponent, "_playerButton", "_characterButton", "_profileButton");
            tradeButtonComponent = ResolveMenuButtonComponent(menuScreenComponent, "_tradeButton", "_tradingButton", "_tradersButton", "_commerceButton");
            hideoutButtonComponent = ResolveMenuButtonComponent(menuScreenComponent, "_hideoutButton", "_stashButton", "_shelterButton", "_baseButton");
            exitButtonComponent = ResolveMenuButtonComponent(menuScreenComponent, "_exitButton", "_quitButton", "_leaveButton", "_logoutButton");
            sourceButtonComponent = playerButtonComponent ?? ResolveMenuButton(menuScreenComponent);
            if (sourceButtonComponent == null)
            {
                VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OffRaidUiStatusTag, "Menu button anchor not found; Vanguard off-raid UI not injected for this menu instance.");
                return;
            }

            inheritedFont = ResolveInheritedFont(sourceButtonComponent.gameObject);
            EnsureMenuButton(sourceButtonComponent);
            EnsureScreenRoot(menuScreenComponent.transform);
            RefreshState();
            Render();
            if (screenRoot != null)
            {
                screenRoot.SetActive(false);
            }

            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OffRaidUiStatusTag, "Vanguard off-raid UI initialized on MenuScreen; menuLayout=two_column_safe_reflow.");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Error(VanguardBuildVersion.OffRaidUiStatusTag, exception);
        }
    }

    private void EnsureMenuButton(Component sourceButton)
    {
        Transform parent = sourceButton.transform.parent;
        if (parent == null)
        {
            return;
        }

        Transform existing = parent.Find(MenuButtonName);
        if (existing != null)
        {
            menuButtonObject = existing.gameObject;
        }
        else
        {
            menuButtonObject = Instantiate(sourceButton.gameObject, parent, false);
            menuButtonObject.name = MenuButtonName;
        }

        ConfigureVanguardMenuButton(menuButtonObject);
        SetButtonClick(menuButtonObject, ShowScreen);

        if (sourceButton.transform is RectTransform sourceRect && menuButtonObject.transform is RectTransform menuRect)
        {
            CaptureOriginalMenuPositions(parent);
            ApplyTwoColumnMenuLayout(parent, sourceRect, menuRect);
        }
        else
        {
            ApplyMenuButtonLayout(sourceButton.gameObject, menuButtonObject);
        }

        menuButtonObject.SetActive(sourceButton.gameObject.activeSelf);
        enforceMenuLabelUntilRealtime = Time.realtimeSinceStartup + 2.0f;
    }

    private void LateUpdate()
    {
        if (menuButtonObject == null || Time.realtimeSinceStartup > enforceMenuLabelUntilRealtime)
        {
            return;
        }

        ConfigureVanguardMenuButton(menuButtonObject);
        if (sourceButtonComponent != null && sourceButtonComponent.transform.parent != null && sourceButtonComponent.transform is RectTransform sourceRect && menuButtonObject.transform is RectTransform menuRect)
        {
            ApplyTwoColumnMenuLayout(sourceButtonComponent.transform.parent, sourceRect, menuRect);
        }
    }

    private void EnsureScreenRoot(Transform menuRoot)
    {
        if (screenRoot != null)
        {
            return;
        }

        Transform existing = menuRoot.Find(ScreenRootName);
        if (existing != null)
        {
            screenRoot = existing.gameObject;
            return;
        }

        screenRoot = new GameObject(ScreenRootName, typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
        screenRoot.transform.SetParent(menuRoot, false);
        var rootRect = screenRoot.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.08f, 0.08f);
        rootRect.anchorMax = new Vector2(0.92f, 0.92f);
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;
        var rootImage = screenRoot.GetComponent<Image>();
        rootImage.color = new Color(0.02f, 0.025f, 0.022f, 0.00f);

        headerBandRoot = new GameObject("HeaderBand", typeof(RectTransform), typeof(Image));
        headerBandRoot.transform.SetParent(screenRoot.transform, false);
        SetRect((RectTransform)headerBandRoot.transform, 0.025f, 0.805f, 0.715f, 0.975f);
        Image headerBandImage = headerBandRoot.GetComponent<Image>();
        headerBandImage.color = new Color(0.015f, 0.020f, 0.018f, 0.72f);
        headerBandImage.raycastTarget = false;

        titleLabel = CreateText(screenRoot.transform, "Title", 28, TextAlignmentOptions.TopLeft, new Color(0.82f, 0.92f, 0.84f));
        SetRect(titleLabel.rectTransform, 0.038f, 0.885f, 0.70f, 0.965f);

        subtitleLabel = CreateText(screenRoot.transform, "Subtitle", 15, TextAlignmentOptions.TopLeft, new Color(0.66f, 0.76f, 0.70f));
        SetRect(subtitleLabel.rectTransform, 0.038f, 0.825f, 0.70f, 0.875f);

        bodyLabel = CreateText(screenRoot.transform, "Body", 15, TextAlignmentOptions.TopLeft, new Color(0.86f, 0.88f, 0.82f));
        SetRect(bodyLabel.rectTransform, 0.045f, 0.18f, 0.69f, 0.78f);
        bodyLabel.enableWordWrapping = true;
        bodyLabel.overflowMode = TextOverflowModes.Overflow;

        cardRoot = new GameObject("CardGrid", typeof(RectTransform));
        cardRoot.transform.SetParent(screenRoot.transform, false);
        SetRect((RectTransform)cardRoot.transform, 0.035f, 0.145f, 0.72f, 0.78f);

        CreateInfoTableScrollRoot();

        statusLabel = CreateText(screenRoot.transform, "Status", 14, TextAlignmentOptions.BottomLeft, new Color(0.75f, 0.80f, 0.72f));
        SetRect(statusLabel.rectTransform, 0.035f, 0.030f, 0.705f, 0.090f);

        CreateTopButton("action.refresh", 0.745f, 0.705f, () => ExecuteUiAction("refresh", RefreshStateAndRender));
        CreateTopButton("action.summary", 0.745f, 0.635f, () => ShowPanel(VanguardOffRaidPanelKind.Dashboard));
        CreateTopButton("dashboard.contracts", 0.745f, 0.565f, () => ShowPanel(VanguardOffRaidPanelKind.Contracts));
        CreateTopButton("dashboard.active", 0.745f, 0.495f, () => ShowPanel(VanguardOffRaidPanelKind.ActiveService));
        CreateTopButton("dashboard.hospital", 0.745f, 0.425f, () => ShowPanel(VanguardOffRaidPanelKind.FieldHospital));
        CreateTopButton("dashboard.billing", 0.745f, 0.355f, () => ShowPanel(VanguardOffRaidPanelKind.Billing));
        CreateTopButton("action.close", 0.745f, CloseButtonBaseY, HideScreen);

        for (int i = 0; i < MaxActionButtons; i++)
        {
            Button button = CreateButton(screenRoot.transform, $"Action_{i}", string.Empty);
            float yMin = ContextActionFirstMinY - (i * ContextActionStepY);
            SetRect((RectTransform)button.transform, 0.760f, yMin, 0.945f, yMin + ContextActionHeight);
            actionButtons.Add(button);
            TextMeshProUGUI? label = FindButtonLabel(button.gameObject);
            if (label != null)
            {
                actionLabels.Add(label);
            }

            button.gameObject.SetActive(false);
        }

        CreateTooltipRoot();
        CreateConfirmationRoot();
    }

    private void CreateInfoTableScrollRoot()
    {
        if (screenRoot == null)
        {
            return;
        }

        infoTableScrollRoot = new GameObject("InfoTableScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        infoTableScrollRoot.transform.SetParent(screenRoot.transform, false);
        SetRect((RectTransform)infoTableScrollRoot.transform, 0.035f, 0.105f, 0.705f, 0.78f);

        Image scrollSurface = infoTableScrollRoot.GetComponent<Image>();
        scrollSurface.color = new Color(0f, 0f, 0f, 0.002f);
        scrollSurface.raycastTarget = true;

        GameObject viewport = new("InfoTableViewport", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(infoTableScrollRoot.transform, false);
        RectTransform viewportRect = (RectTransform)viewport.transform;
        SetRect(viewportRect, 0f, 0f, 1f, 1f);

        infoTableRoot = new GameObject("InfoTableContent", typeof(RectTransform));
        infoTableRoot.transform.SetParent(viewport.transform, false);
        infoTableContentRect = (RectTransform)infoTableRoot.transform;
        infoTableContentRect.anchorMin = new Vector2(0f, 1f);
        infoTableContentRect.anchorMax = new Vector2(1f, 1f);
        infoTableContentRect.pivot = new Vector2(0.5f, 1f);
        infoTableContentRect.anchoredPosition = Vector2.zero;
        infoTableContentRect.sizeDelta = new Vector2(0f, 1f);

        infoTableScrollRect = infoTableScrollRoot.GetComponent<ScrollRect>();
        infoTableScrollRect.viewport = viewportRect;
        infoTableScrollRect.content = infoTableContentRect;
        infoTableScrollRect.horizontal = false;
        infoTableScrollRect.vertical = true;
        infoTableScrollRect.movementType = ScrollRect.MovementType.Clamped;
        infoTableScrollRect.inertia = true;
        infoTableScrollRect.decelerationRate = 0.12f;
        infoTableScrollRect.scrollSensitivity = 32f;
        infoTableScrollRoot.SetActive(false);
    }

    private void CreateTopButton(string localizationKey, float xMin, float yMin, Action action)
    {
        if (screenRoot == null)
        {
            return;
        }

        string label = L(localizationKey);
        Button button = CreateButton(screenRoot.transform, $"Nav_{localizationKey.Replace('.', '_')}", label);
        SetRect((RectTransform)button.transform, xMin + 0.015f, yMin + 0.005f, 0.945f, yMin + TopButtonVisualMaxOffsetY);
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => action());
        RegisterPersistentLocalizedLabel(button, localizationKey);
    }

    private void RegisterPersistentLocalizedLabel(Button button, string localizationKey)
    {
        TextMeshProUGUI? label = FindButtonLabel(button.gameObject);
        if (label != null)
        {
            persistentLocalizedLabels[label] = localizationKey;
        }
    }

    private void RefreshPersistentLocalizedLabels()
    {
        foreach (KeyValuePair<TextMeshProUGUI, string> entry in persistentLocalizedLabels)
        {
            if (entry.Key != null)
            {
                entry.Key.text = L(entry.Value);
            }
        }
    }

    private Button CreateButton(Transform parent, string name, string label)
    {
        // All off-raid buttons are built here: top navigation, close button, confirmations
        // and panel-specific actions such as "Signer factures". Keeping the style in this
        // one factory prevents isolated panels from drifting into unreadable local variants.
        var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.AddComponent<CanvasGroup>();

        var image = buttonObject.GetComponent<Image>();
        image.color = ButtonNormalBackgroundColor;
        image.raycastTarget = true;

        var button = buttonObject.GetComponent<Button>();
        button.transition = Selectable.Transition.ColorTint;
        var colors = button.colors;
        colors.normalColor = ButtonNormalBackgroundColor;
        colors.highlightedColor = ButtonHoverBackgroundColor;
        colors.pressedColor = ButtonPressedBackgroundColor;
        colors.selectedColor = ButtonSelectedBackgroundColor;
        colors.disabledColor = ButtonDisabledBackgroundColor;
        button.colors = colors;

        var hoverPlate = new GameObject("VanillaHoverPlate", typeof(RectTransform), typeof(Image));
        hoverPlate.transform.SetParent(buttonObject.transform, false);
        SetRect((RectTransform)hoverPlate.transform, 0.01f, 0.08f, 0.99f, 0.92f);
        Image hoverImage = hoverPlate.GetComponent<Image>();
        hoverImage.color = ButtonHoverBackgroundColor;
        hoverImage.raycastTarget = false;
        hoverPlate.SetActive(false);

        var upperLine = new GameObject("UpperLine", typeof(RectTransform), typeof(Image));
        upperLine.transform.SetParent(buttonObject.transform, false);
        SetRect((RectTransform)upperLine.transform, 0.03f, 0.88f, 0.97f, 0.93f);
        Image upperLineImage = upperLine.GetComponent<Image>();
        upperLineImage.color = ButtonLineColor;
        upperLineImage.raycastTarget = false;

        // HoverIcon is intentionally separate from Label. RenderActions must update Label only.
        // Updating the first TextMeshProUGUI child would hit this hidden icon and reproduce the
        // old "visible only on hover" billing-button bug.
        TextMeshProUGUI hoverIcon = CreateText(buttonObject.transform, "HoverIcon", 14, TextAlignmentOptions.Center, ButtonHoverTextColor);
        SetRect(hoverIcon.rectTransform, 0.035f, 0.08f, 0.14f, 0.92f);
        hoverIcon.text = "▸";
        hoverIcon.gameObject.SetActive(false);

        TextMeshProUGUI text = CreateText(buttonObject.transform, "Label", 15, TextAlignmentOptions.Center, ButtonNormalTextColor);
        SetRect(text.rectTransform, 0.045f, 0.05f, 0.955f, 0.95f);
        text.text = label;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        RegisterVanillaButtonVisuals(buttonObject, text, hoverPlate, hoverIcon);
        return button;
    }

    private TextMeshProUGUI CreateText(Transform parent, string name, int fontSize, TextAlignmentOptions alignment, Color color)
    {
        var textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.font = inheritedFont ?? text.font;
        text.fontSize = fontSize;
        text.enableAutoSizing = true;
        text.fontSizeMax = fontSize;
        text.fontSizeMin = Math.Max(8, fontSize - 4);
        text.alignment = alignment;
        text.color = color;
        text.text = string.Empty;
        text.raycastTarget = false;
        return text;
    }

    private void ShowScreen()
    {
        if (screenRoot == null)
        {
            return;
        }

        SetVanillaMenuVisible(false);
        screenRoot.SetActive(true);
        screenRoot.transform.SetAsLastSibling();
        RefreshStateAndRender();
    }

    private void HideScreen()
    {
        HideTooltip();
        HideConfirmation();
        if (screenRoot != null)
        {
            screenRoot.SetActive(false);
        }

        SetVanillaMenuVisible(true);
        RefreshVanguardMenuButtonAfterRestore();
    }

    private void HideScreenForDirectInventory()
    {
        HideTooltip();
        HideConfirmation();
        if (screenRoot != null)
        {
            screenRoot.SetActive(false);
        }

        // The direct Operator inventory owns its close path.  Do not restore the
        // vanilla menu while the temporary InventoryScreen is queued/open; the close
        // lifecycle rebuilds the player menu after the Operator profile has been
        // committed and the player profile has been reloaded.
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", "direct_inventory_vanguard_screen_hidden lifecycle=inventory_owns_return_to_menu");
    }

    private void OnDisable()
    {
        SetVanillaMenuVisible(true);
        RefreshVanguardMenuButtonAfterRestore();
    }

    private void OnDestroy()
    {
        SetVanillaMenuVisible(true);
        RefreshVanguardMenuButtonAfterRestore();
    }

    private void ShowPanel(VanguardOffRaidPanelKind panel)
    {
        currentPanel = panel;
        Render();
    }

    private void RefreshStateAndRender()
    {
        RefreshState();
        Render();
    }

    private void RefreshState()
    {
        try
        {
            state = apiClient?.LoadState() ?? VanguardOperatorStateView.Empty("api_client_missing");
            VanguardOperatorInventoryModeClientState.RefreshFromServerStatus();
            canonicalState = VanguardCanonicalOperatorState.Build(state);
            integrityReport = canonicalState.Analyze(state);
            statusMessage = state.Error == null
                ? BuildStatusMessageWithInventoryMode()
                : F("general.state_error", state.Error);
            VanguardClientDiagnosticsLog.Info("VANGUARD_OFFRAID_UI_FOUNDATION_STATUS", integrityReport.ToLogString());
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_IDENTITY_CANONICAL_STATUS", $"canonicalOperators={canonicalState.ByOperatorId.Count}; portraitsResolved={canonicalState.ByOperatorId.Count - integrityReport.MissingPortraitKeyCount}/{canonicalState.ByOperatorId.Count}; ok={!integrityReport.HasBlockingIssue}");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OFFRAID_BILLING_FLOW_STATUS", $"pendingSignature={state.Billing.PendingSignatureDebt}; signedPending={state.Billing.SignedPendingSettlementDebt}; paidTotal={state.Billing.PaidTotal}; openInvoices={state.Billing.OpenInvoiceCount}; flow=sign_then_auto_archive");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OFFRAID_SERVICE_STATE_LABEL_STATUS", $"active={state.ServiceProjections.Count(item => item.IsSelectedForRaid)}; rest={state.ServiceProjections.Count(item => !item.IsSelectedForRaid)}; labels=ActifRepos");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OFFRAID_UI_THEME_STATUS", $"buttonFactory=CreateButton; actionButtons={actionButtons.Count}; actionLabels={actionLabels.Count}; billingActionUsesStandardTheme=true");
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_INVENTORY_UI_ENTRY_STATUS", $"serviceCardEquipmentEntry=true; cardClickOpensDossier=true; inventoryModeActive={VanguardOperatorInventoryModeClientState.IsActive}; currentPanel={currentPanel}");
        }
        catch (Exception exception)
        {
            state = VanguardOperatorStateView.Empty(exception.Message);
            canonicalState = VanguardCanonicalOperatorState.Build(state);
            integrityReport = new VanguardOffRaidIntegrityReport();
            statusMessage = F("general.state_error", exception.Message);
            VanguardClientDiagnosticsLog.Error(VanguardBuildVersion.OffRaidUiStatusTag, exception);
        }
    }


    private string BuildStatusMessageWithInventoryMode()
    {
        string baseStatus = $"{L("general.ready").TrimEnd('.')} · Operators {state.Operators.Count}/{state.Limits.MaxHiredOperators} · {L("dashboard.contracts")} {state.Contracts.Count} · Raid {state.Limits.MaxDeployableOperators}{integrityReport.ToStatusSuffix()}";
        if (!VanguardOperatorInventoryModeClientState.IsActive)
        {
            return baseStatus;
        }

        string applied = VanguardOperatorInventoryModeClientState.OperatorProfileApplied ? L("general.equipment_screen_open") : L("general.session_technical_active");
        return F("inventory.status.session", Safe(VanguardOperatorInventoryModeClientState.OperatorDisplayName, VanguardOperatorInventoryModeClientState.OperatorId, L("general.operator")), applied, baseStatus);
    }

    private void Render()
    {
        if (titleLabel == null || subtitleLabel == null || bodyLabel == null || statusLabel == null)
        {
            return;
        }

        RefreshPersistentLocalizedLabels();
        VanguardOffRaidPanelModel model = BuildCurrentPanel();
        titleLabel.text = model.Title;
        subtitleLabel.text = model.Subtitle;
        bodyLabel.text = model.Body;
        statusLabel.text = actionInProgress ? F("general.action_in_progress", statusMessage) : statusMessage;
        RenderPanelCards(model);
        RenderInfoSections(model);
        RenderActions(ShouldShowActionColumn(currentPanel) ? model.Actions : Array.Empty<VanguardOffRaidPanelAction>());
    }

    private static bool ShouldShowActionColumn(VanguardOffRaidPanelKind panel)
    {
        return panel == VanguardOffRaidPanelKind.Billing
            || panel == VanguardOffRaidPanelKind.OperatorDossier;
    }

    private VanguardOffRaidPanelModel BuildCurrentPanel()
    {
        VanguardOffRaidPanelModel model = currentPanel switch
        {
            VanguardOffRaidPanelKind.Contracts => contractsPanel.Build(state, ConfirmHireContract),
            VanguardOffRaidPanelKind.ActiveService => activeServicePanel.Build(state, ConfirmRaidSelection, OpenDossier),
            VanguardOffRaidPanelKind.FieldHospital => fieldHospitalPanel.Build(state, ConfirmTreatOperator),
            VanguardOffRaidPanelKind.Billing => billingPanel.Build(state, ConfirmSignOpenInvoices),
            VanguardOffRaidPanelKind.OperatorDossier => dossierPanel.Build(state, selectedOperatorId, () => ShowPanel(VanguardOffRaidPanelKind.ActiveService), ConfirmOpenInventory, SetOperatorLootTargets),
            _ => dashboardPanel.Build(
                state,
                () => ShowPanel(VanguardOffRaidPanelKind.Contracts),
                () => ShowPanel(VanguardOffRaidPanelKind.ActiveService),
                () => ShowPanel(VanguardOffRaidPanelKind.FieldHospital),
                () => ShowPanel(VanguardOffRaidPanelKind.Billing))
        };

        if (VanguardOperatorInventoryModeClientState.IsActive && currentPanel == VanguardOffRaidPanelKind.OperatorDossier)
        {
            model.Actions.Insert(0, new VanguardOffRaidPanelAction { Label = L("action.exit_inventory"), Execute = ConfirmExitInventoryMode });
        }

        return model;
    }

    private void RenderPanelCards(VanguardOffRaidPanelModel model)
    {
        HideTooltip();
        ClearCards();

        if (cardRoot == null || bodyLabel == null)
        {
            return;
        }

        if (!IsCardGridPanel(currentPanel))
        {
            cardRoot.SetActive(false);
            return;
        }

        cardRoot.SetActive(true);
        List<VanguardOperatorCardModel> cards = BuildCardsForCurrentPanel();
        if (cards.Count == 0)
        {
            bodyLabel.text = model.Body;
            return;
        }

        bodyLabel.text = string.Empty;
        int count = Math.Min(cards.Count, MaxCardsPerPage);
        for (int i = 0; i < count; i++)
        {
            CreateOperatorCard(cards[i], i);
        }
    }

    private static bool IsCardGridPanel(VanguardOffRaidPanelKind panel)
    {
        return panel == VanguardOffRaidPanelKind.Contracts
            || panel == VanguardOffRaidPanelKind.ActiveService
            || panel == VanguardOffRaidPanelKind.FieldHospital;
    }

    private List<VanguardOperatorCardModel> BuildCardsForCurrentPanel()
    {
        return currentPanel switch
        {
            VanguardOffRaidPanelKind.Contracts => BuildContractCards(),
            VanguardOffRaidPanelKind.ActiveService => BuildActiveServiceCards(),
            VanguardOffRaidPanelKind.FieldHospital => BuildHospitalCards(),
            _ => new List<VanguardOperatorCardModel>()
        };
    }

    private List<VanguardOperatorCardModel> BuildContractCards()
    {
        var cards = new List<VanguardOperatorCardModel>();
        bool limitReached = state.Operators.Count >= state.Limits.MaxHiredOperators;
        foreach (VanguardOperatorContractOfferDto offer in state.Contracts.Take(MaxCardsPerPage))
        {
            VanguardCanonicalOperatorView identity = canonicalState.ResolveForContract(offer);
            bool canHire = offer.CanHire && !limitReached;
            cards.Add(new VanguardOperatorCardModel
            {
                Title = identity.DisplayName,
                Side = identity.FactionLabel,
                Role = identity.RoleLabel,
                PortraitSide = identity.Side,
                PortraitRole = identity.Role,
                Level = identity.Level,
                StateLabel = canHire ? L("general.available") : limitReached ? L("general.limit_reached") : FriendlyValue(offer.MarketStatus, L("general.unavailable")),
                AccentLabel = L("general.contract"),
                Placeholder = identity.Placeholder,
                PortraitKey = identity.PortraitKey,
                Tooltip = BuildContractTooltip(offer, identity, canHire, limitReached),
                ActionLabel = canHire ? L("action.hire_short") : L("general.unavailable"),
                Enabled = canHire,
                Execute = () => ConfirmHireContract(offer)
            });
        }

        return cards;
    }

    private List<VanguardOperatorCardModel> BuildActiveServiceCards()
    {
        var cards = new List<VanguardOperatorCardModel>();
        foreach (VanguardOperatorServiceProjectionDto projection in state.ServiceProjections.Take(MaxCardsPerPage))
        {
            VanguardCanonicalOperatorView identity = canonicalState.ResolveForOperator(
                projection.OperatorId,
                projection.DisplayName,
                projection.Side,
                projection.Role,
                projection.Specialty,
                projection.Level);
            bool selected = projection.IsSelectedForRaid;
            bool eligible = selected || string.Equals(projection.EligibilityState, "eligible", StringComparison.OrdinalIgnoreCase);
            bool inventoryModeActive = VanguardOperatorInventoryModeClientState.IsActive;
            bool inventoryModeForThisOperator = inventoryModeActive
                && string.Equals(VanguardOperatorInventoryModeClientState.OperatorId, projection.OperatorId, StringComparison.OrdinalIgnoreCase);
            string inventoryActionLabel = inventoryModeForThisOperator ? L("action.exit_inventory_short") : inventoryModeActive ? L("general.session_technical_active") : L("action.equipment");
            bool inventoryActionEnabled = !string.IsNullOrWhiteSpace(projection.OperatorId) && (!inventoryModeActive || inventoryModeForThisOperator);
            Action inventoryAction;
            if (inventoryModeForThisOperator)
            {
                inventoryAction = ConfirmExitInventoryMode;
            }
            else
            {
                inventoryAction = () => ConfirmOpenInventory(projection.OperatorId, identity.DisplayName);
            }

            cards.Add(new VanguardOperatorCardModel
            {
                Title = identity.DisplayName,
                Side = identity.FactionLabel,
                Role = identity.RoleLabel,
                PortraitSide = identity.Side,
                PortraitRole = identity.Role,
                Level = identity.Level,
                StateLabel = selected ? L("general.active") : L("general.rest"),
                AccentLabel = (selected ? L("general.active") : L("general.rest")).ToUpperInvariant(),
                Placeholder = identity.Placeholder,
                PortraitKey = identity.PortraitKey,
                Tooltip = BuildServiceTooltip(projection, identity, eligible),
                ActionLabel = selected ? L("general.rest") : L("general.active"),
                Enabled = eligible,
                Execute = () => ConfirmRaidSelection(projection.OperatorId, identity.DisplayName, !projection.IsSelectedForRaid),
                SecondaryActionLabel = inventoryActionLabel,
                SecondaryEnabled = inventoryActionEnabled,
                SecondaryExecute = inventoryAction,
                CardExecute = () => OpenDossier(projection.OperatorId)
            });
        }

        return cards;
    }

    private List<VanguardOperatorCardModel> BuildHospitalCards()
    {
        var cards = new List<VanguardOperatorCardModel>();
        foreach (VanguardOperatorMedicalProjectionDto projection in state.MedicalProjections.Take(MaxCardsPerPage))
        {
            VanguardCanonicalOperatorView identity = canonicalState.ResolveForOperator(
                projection.OperatorId,
                projection.DisplayName,
                null,
                projection.Role,
                null,
                projection.Level);
            int healthPercent = Mathf.Clamp((int)Math.Round(projection.CurrentHealthRatio * 100.0), 0, 100);
            bool canTreat = projection.HealCost > 0
                || projection.RecoveryCost > 0
                || projection.CurrentHealthRatio < 0.999
                || string.Equals(projection.RecoveryState, "recovering", StringComparison.OrdinalIgnoreCase);
            cards.Add(new VanguardOperatorCardModel
            {
                Title = identity.DisplayName,
                Side = identity.FactionLabel,
                Role = identity.RoleLabel,
                PortraitSide = identity.Side,
                PortraitRole = identity.Role,
                Level = identity.Level,
                StateLabel = $"{healthPercent}%",
                AccentLabel = FriendlyValue(projection.MedicalStatus, projection.RecoveryState, L("general.medical")).ToUpperInvariant(),
                Placeholder = identity.Placeholder,
                PortraitKey = identity.PortraitKey,
                Tooltip = BuildHospitalTooltip(projection, identity, canTreat),
                ActionLabel = canTreat ? L("action.treat") : L("general.stable"),
                Enabled = canTreat,
                Execute = () => ConfirmTreatOperator(projection)
            });
        }

        return cards;
    }

    private void CreateOperatorCard(VanguardOperatorCardModel model, int index)
    {
        if (cardRoot == null)
        {
            return;
        }

        int column = index % 4;
        int row = index / 4;
        float cardWidth = 0.205f;
        float cardHeight = 0.365f;
        float gapX = 0.032f;
        float gapY = 0.060f;
        float xMin = column * (cardWidth + gapX);
        float xMax = xMin + cardWidth;
        float yMax = 1.0f - row * (cardHeight + gapY);
        float yMin = yMax - cardHeight;

        var card = new GameObject($"OperatorCard_{index}", typeof(RectTransform), typeof(Image), typeof(Button));
        card.transform.SetParent(cardRoot.transform, false);
        SetRect((RectTransform)card.transform, xMin, yMin, xMax, yMax);
        cardObjects.Add(card);

        Image cardImage = card.GetComponent<Image>();
        cardImage.color = new Color(0.035f, 0.043f, 0.038f, 0.94f);
        Button cardButton = card.GetComponent<Button>();
        var colors = cardButton.colors;
        colors.normalColor = new Color(0.035f, 0.043f, 0.038f, 0.94f);
        colors.highlightedColor = new Color(0.115f, 0.16f, 0.125f, 0.98f);
        colors.pressedColor = new Color(0.06f, 0.09f, 0.075f, 1f);
        colors.disabledColor = new Color(0.025f, 0.028f, 0.026f, 0.55f);
        cardButton.colors = colors;

        Action cardAction = model.CardExecute ?? model.Execute;
        bool cardEnabled = (model.CardExecute != null || model.Enabled) && !actionInProgress;
        cardButton.interactable = cardEnabled;
        if (cardEnabled)
        {
            cardButton.onClick.AddListener(() => cardAction());
        }

        var portrait = new GameObject("Portrait", typeof(RectTransform), typeof(Image));
        portrait.transform.SetParent(card.transform, false);
        SetRect((RectTransform)portrait.transform, 0.075f, 0.315f, 0.925f, 0.940f);
        Image portraitImage = portrait.GetComponent<Image>();
        portraitImage.color = new Color(0.08f, 0.10f, 0.09f, 0.98f);
        portraitImage.preserveAspect = true;

        TextMeshProUGUI portraitText = CreateText(portrait.transform, "PortraitText", 24, TextAlignmentOptions.Center, new Color(0.62f, 0.74f, 0.66f));
        SetRect(portraitText.rectTransform, 0.05f, 0.10f, 0.95f, 0.88f);
        portraitText.text = model.Placeholder;

        Sprite? portraitSprite = ResolveOperatorPortraitSprite(model.PortraitKey, model.PortraitSide, model.PortraitRole);
        if (portraitSprite != null)
        {
            portraitImage.sprite = portraitSprite;
            portraitImage.color = Color.white;
            portraitText.text = string.Empty;
        }

        var levelBadge = new GameObject("LevelBadge", typeof(RectTransform), typeof(Image));
        levelBadge.transform.SetParent(card.transform, false);
        SetRect((RectTransform)levelBadge.transform, 0.02f, 0.86f, 0.18f, 0.99f);
        Image levelBackground = levelBadge.GetComponent<Image>();
        levelBackground.color = new Color(0.78f, 0.86f, 0.76f, 0.92f);
        levelBackground.raycastTarget = false;
        TextMeshProUGUI levelText = CreateText(levelBadge.transform, "Level", 12, TextAlignmentOptions.Center, new Color(0.12f, 0.16f, 0.13f));
        SetRect(levelText.rectTransform, 0.02f, 0.05f, 0.98f, 0.95f);
        levelText.text = model.Level > 0 ? model.Level.ToString() : "-";

        TextMeshProUGUI statusText = CreateText(card.transform, "Status", 10, TextAlignmentOptions.Center, new Color(0.68f, 0.82f, 0.70f));
        SetRect(statusText.rectTransform, 0.48f, 0.86f, 0.96f, 0.98f);
        statusText.text = model.AccentLabel.ToUpperInvariant();

        var nameStrip = new GameObject("NameStrip", typeof(RectTransform), typeof(Image));
        nameStrip.transform.SetParent(card.transform, false);
        SetRect((RectTransform)nameStrip.transform, 0.035f, 0.178f, 0.965f, 0.315f);
        nameStrip.GetComponent<Image>().color = new Color(0.09f, 0.075f, 0.055f, 0.96f);

        TextMeshProUGUI nameText = CreateText(nameStrip.transform, "Name", 14, TextAlignmentOptions.Center, new Color(0.88f, 0.88f, 0.78f));
        SetRect(nameText.rectTransform, 0.02f, 0.05f, 0.98f, 0.95f);
        nameText.text = model.Title;

        TextMeshProUGUI metaText = CreateText(card.transform, "Meta", 10, TextAlignmentOptions.Center, new Color(0.66f, 0.74f, 0.68f));
        SetRect(metaText.rectTransform, 0.04f, 0.138f, 0.96f, 0.175f);
        metaText.text = $"{model.Side} · {model.Role}";

        bool hasSecondary = !string.IsNullOrWhiteSpace(model.SecondaryActionLabel) && model.SecondaryExecute != null;
        if (hasSecondary)
        {
            CreateCardActionButton(card.transform, "PrimaryAction", model.ActionLabel, model.Enabled, model.Execute, 0.035f, 0.020f, 0.485f, 0.128f);
            CreateCardActionButton(card.transform, "SecondaryAction", model.SecondaryActionLabel, model.SecondaryEnabled, model.SecondaryExecute!, 0.515f, 0.020f, 0.965f, 0.128f);
        }
        else if (!string.IsNullOrWhiteSpace(model.ActionLabel))
        {
            CreateCardActionButton(card.transform, "PrimaryAction", model.ActionLabel, model.Enabled, model.Execute, 0.035f, 0.020f, 0.795f, 0.128f);
        }

        TextMeshProUGUI infoText = CreateText(card.transform, "QuestionMark", 15, TextAlignmentOptions.Center, new Color(0.64f, 0.76f, 0.86f));
        SetRect(infoText.rectTransform, 0.820f, 0.020f, 0.980f, 0.128f);
        infoText.text = hasSecondary ? string.Empty : "?";

        string tooltip = model.Tooltip;
        AddTooltipTrigger(card, tooltip, xMin, yMin, xMax, yMax);
        if (!hasSecondary)
        {
            AddTooltipTrigger(infoText.gameObject, tooltip, xMin, yMin, xMax, yMax);
        }
    }

    private void CreateCardActionButton(Transform parent, string name, string label, bool enabled, Action action, float xMin, float yMin, float xMax, float yMax)
    {
        var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        SetRect((RectTransform)buttonObject.transform, xMin, yMin, xMax, yMax);

        Image image = buttonObject.GetComponent<Image>();
        image.color = enabled ? ButtonNormalBackgroundColor : ButtonDisabledBackgroundColor;
        image.raycastTarget = true;

        Button button = buttonObject.GetComponent<Button>();
        button.transition = Selectable.Transition.ColorTint;
        var colors = button.colors;
        colors.normalColor = ButtonNormalBackgroundColor;
        colors.highlightedColor = ButtonHoverBackgroundColor;
        colors.pressedColor = ButtonPressedBackgroundColor;
        colors.selectedColor = ButtonSelectedBackgroundColor;
        colors.disabledColor = ButtonDisabledBackgroundColor;
        button.colors = colors;
        button.interactable = enabled && !actionInProgress;
        if (enabled)
        {
            button.onClick.AddListener(() => ExecuteUiAction(label, action));
        }

        TextMeshProUGUI buttonText = CreateText(buttonObject.transform, "Label", 11, TextAlignmentOptions.Center, enabled ? ButtonNormalTextColor : ButtonDisabledTextColor);
        SetRect(buttonText.rectTransform, 0.05f, 0.08f, 0.95f, 0.92f);
        buttonText.text = label;
        buttonText.enableWordWrapping = false;
        buttonText.overflowMode = TextOverflowModes.Ellipsis;
    }

    private void ClearCards()
    {
        foreach (GameObject cardObject in cardObjects)
        {
            if (cardObject != null)
            {
                Destroy(cardObject);
            }
        }

        cardObjects.Clear();
    }

    private void CreateTooltipRoot()
    {
        if (screenRoot == null || tooltipRoot != null)
        {
            return;
        }

        tooltipRoot = new GameObject("Tooltip", typeof(RectTransform), typeof(Image));
        tooltipRoot.transform.SetParent(screenRoot.transform, false);
        tooltipRoot.GetComponent<Image>().color = new Color(0.018f, 0.022f, 0.021f, 0.985f);
        tooltipLabel = CreateText(tooltipRoot.transform, "TooltipText", 12, TextAlignmentOptions.TopLeft, new Color(0.82f, 0.86f, 0.78f));
        tooltipLabel.richText = true;
        SetRect(tooltipLabel.rectTransform, 0.045f, 0.045f, 0.955f, 0.955f);
        tooltipLabel.enableWordWrapping = true;
        tooltipLabel.overflowMode = TextOverflowModes.Overflow;
        tooltipRoot.SetActive(false);
    }

    private void AddTooltipTrigger(GameObject target, string tooltip, float cardXMin, float cardYMin, float cardXMax, float cardYMax)
    {
        EventTrigger trigger = target.GetComponent<EventTrigger>() ?? target.AddComponent<EventTrigger>();
        trigger.triggers ??= new List<EventTrigger.Entry>();

        var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(_ => ShowTooltip(tooltip, cardXMin, cardYMin, cardXMax, cardYMax));
        trigger.triggers.Add(enter);

        var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
        exit.callback.AddListener(_ => HideTooltip());
        trigger.triggers.Add(exit);
    }

    private void ShowTooltip(string tooltip, float cardXMin, float cardYMin, float cardXMax, float cardYMax)
    {
        if (tooltipRoot == null || tooltipLabel == null)
        {
            return;
        }

        tooltipLabel.text = tooltip;
        // Keep contract/service details in the intentionally empty lower-center area.
        // The previous card-adjacent overlay could hide neighboring contract cards.
        const float tipXMin = 0.385f;
        const float tipXMax = 0.685f;
        const float tipYMin = 0.075f;
        const float tipYMax = 0.415f;
        SetRect((RectTransform)tooltipRoot.transform, tipXMin, tipYMin, tipXMax, tipYMax);
        tooltipRoot.SetActive(true);
        tooltipRoot.transform.SetAsLastSibling();
    }

    private void HideTooltip()
    {
        if (tooltipRoot != null)
        {
            tooltipRoot.SetActive(false);
        }
    }

    private void CreateConfirmationRoot()
    {
        if (screenRoot == null || confirmationRoot != null)
        {
            return;
        }

        confirmationRoot = new GameObject("Confirmation", typeof(RectTransform), typeof(Image));
        confirmationRoot.transform.SetParent(screenRoot.transform, false);
        SetRect((RectTransform)confirmationRoot.transform, 0.31f, 0.33f, 0.69f, 0.67f);
        confirmationRoot.GetComponent<Image>().color = new Color(0.025f, 0.032f, 0.028f, 0.985f);

        confirmationTitleLabel = CreateText(confirmationRoot.transform, "ConfirmTitle", 18, TextAlignmentOptions.TopLeft, new Color(0.82f, 0.92f, 0.84f));
        SetRect(confirmationTitleLabel.rectTransform, 0.07f, 0.74f, 0.93f, 0.93f);

        confirmationBodyLabel = CreateText(confirmationRoot.transform, "ConfirmBody", 13, TextAlignmentOptions.TopLeft, new Color(0.82f, 0.86f, 0.78f));
        SetRect(confirmationBodyLabel.rectTransform, 0.07f, 0.30f, 0.93f, 0.72f);
        confirmationBodyLabel.enableWordWrapping = true;

        Button confirmButton = CreateButton(confirmationRoot.transform, "Confirm", L("popup.yes"));
        confirmationConfirmButton = confirmButton;
        SetRect((RectTransform)confirmButton.transform, 0.09f, 0.08f, 0.47f, 0.23f);
        confirmationConfirmLabel = FindButtonLabel(confirmButton.gameObject);
        confirmButton.onClick.RemoveAllListeners();
        confirmButton.onClick.AddListener(ConfirmPendingAction);

        Button cancelButton = CreateButton(confirmationRoot.transform, "Cancel", L("popup.no"));
        confirmationCancelButton = cancelButton;
        RegisterPersistentLocalizedLabel(cancelButton, "popup.no");
        SetRect((RectTransform)cancelButton.transform, 0.53f, 0.08f, 0.91f, 0.23f);
        cancelButton.onClick.RemoveAllListeners();
        cancelButton.onClick.AddListener(HideConfirmation);

        confirmationRoot.SetActive(false);
    }

    private void ShowConfirmation(string title, string body, string confirmLabel, Action confirmedAction)
    {
        if (confirmationRoot == null || confirmationTitleLabel == null || confirmationBodyLabel == null)
        {
            confirmedAction();
            return;
        }

        pendingConfirmationAction = confirmedAction;
        pendingConfirmationActionName = confirmLabel;
        confirmationTitleLabel.text = title;
        confirmationBodyLabel.text = body;
        if (confirmationConfirmLabel != null && confirmationConfirmButton != null)
        {
            ApplyStandardButtonLabel(confirmationConfirmButton.gameObject, confirmationConfirmLabel, confirmLabel, true);
        }

        confirmationRoot.SetActive(true);
        confirmationRoot.transform.SetAsLastSibling();
    }

    private void ConfirmPendingAction()
    {
        Action? action = pendingConfirmationAction;
        string actionName = pendingConfirmationActionName;
        HideConfirmation();
        if (action == null)
        {
            return;
        }

        ExecuteUiAction(actionName, action);
    }

    private void HideConfirmation()
    {
        pendingConfirmationAction = null;
        pendingConfirmationActionName = string.Empty;
        if (confirmationRoot != null)
        {
            confirmationRoot.SetActive(false);
        }
    }

    private string BuildContractTooltip(VanguardOperatorContractOfferDto offer, VanguardCanonicalOperatorView identity, bool canHire, bool limitReached)
    {
        string status = canHire
            ? L("general.available")
            : limitReached
                ? L("general.recruitment_limit")
                : FriendlyValue(offer.MarketStatus, L("general.unavailable"));
        return BuildOperatorTooltip(
            identity,
            L("general.contract"),
            new[]
            {
                (L("label.persona"), FriendlyValue(identity.Persona, identity.Temperament, L("general.undefined"))),
                (L("label.style"), FriendlyValue(identity.CombatStyle, offer.CombatStyle, L("general.undefined"))),
                (L("label.range"), FriendlyRange(identity.EngagementRange.Length > 0 ? identity.EngagementRange : offer.EngagementRange)),
                (L("label.squad_role_short"), FriendlySquadRole(identity.SquadRole.Length > 0 ? identity.SquadRole : offer.SquadRole)),
                (L("label.traits"), FormatTraits(SelectTraits(identity.Traits, offer.Traits))),
                (L("label.hire_cost"), FormatMoney(offer.HirePrice)),
                (L("label.salary_per_raid"), FormatMoney(offer.SalaryPerRaid)),
                (L("label.status"), status),
                (L("label.portrait"), identity.PortraitSource)
            });
    }

    private string BuildServiceTooltip(VanguardOperatorServiceProjectionDto projection, VanguardCanonicalOperatorView identity, bool eligible)
    {
        string deploy = projection.IsSelectedForRaid ? L("general.active") : L("general.rest");
        return BuildOperatorTooltip(
            identity,
            L("general.active_service"),
            new[]
            {
                (L("label.persona"), FriendlyValue(identity.Persona, projection.PersonaKey, identity.Temperament, L("general.undefined"))),
                (L("label.doctrine"), FriendlyValue(identity.Doctrine, projection.Doctrine, L("general.undefined"))),
                (L("label.style"), FriendlyValue(identity.CombatStyle, L("general.undefined"))),
                (L("label.range"), FriendlyRange(identity.EngagementRange)),
                (L("label.squad_role_short"), FriendlySquadRole(identity.SquadRole)),
                (L("label.traits"), FormatTraits(SelectTraits(identity.Traits, projection.Traits))),
                (L("label.salary_per_raid"), FormatMoney(projection.SalaryPerRaid)),
                (L("label.service_state"), deploy),
                (L("label.eligibility"), eligible ? L("general.eligible") : FriendlyValue(projection.EligibilityState, L("general.blocked")))
            });
    }

    private string BuildHospitalTooltip(VanguardOperatorMedicalProjectionDto projection, VanguardCanonicalOperatorView identity, bool canTreat)
    {
        return BuildOperatorTooltip(
            identity,
            L("general.hospital"),
            new[]
            {
                (L("label.health"), VanguardUiText.HealthPercent(projection.CurrentHealthRatio)),
                (L("label.status"), FriendlyValue(projection.MedicalStatus, L("general.undefined"))),
                (L("label.recovery"), FriendlyValue(projection.RecoveryState, L("general.none_fem"))),
                (L("label.injury"), FriendlyValue(projection.InjurySummary, L("general.no_details"))),
                (L("label.heal_cost"), FormatMoney(projection.HealCost)),
                (L("label.acceleration"), FormatMoney(projection.RecoveryCost)),
                (L("label.service"), FriendlyValue(projection.ServiceStatus, L("general.unavailable"))),
                (L("label.action"), canTreat ? L("general.confirmation_required") : L("general.stable"))
            });
    }

    private static string BuildOperatorTooltip(VanguardCanonicalOperatorView identity, string context, IEnumerable<(string Label, string Value)> rows)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"<b>{identity.DisplayName}</b>");
        builder.AppendLine($"<color=#A9BDAE>{F("tooltip.level_line", identity.FactionLabel, identity.RoleLabel, identity.Level, context)}</color>");
        builder.AppendLine("<color=#49534D>────────────────────────</color>");
        builder.AppendLine($"<b>{L("tooltip.identity")}</b>");
        builder.AppendLine(F("tooltip.row", L("label.faction"), identity.FactionLabel));
        builder.AppendLine(F("tooltip.row", L("label.role"), identity.RoleLabel));
        if (!string.IsNullOrWhiteSpace(identity.VisualFamily))
        {
            builder.AppendLine(F("tooltip.row", L("label.visual_family"), FriendlyValue(identity.VisualFamily)));
        }

        builder.AppendLine("<color=#49534D>────────────────────────</color>");
        builder.AppendLine($"<b>{L("tooltip.operational_profile")}</b>");
        foreach ((string label, string value) in rows)
        {
            string safeValue = string.IsNullOrWhiteSpace(value) ? L("general.undefined") : value;
            builder.AppendLine(F("tooltip.row", label, safeValue));
        }

        return builder.ToString();
    }

    private static string ShortenTechnicalPlan(params string?[] values)
    {
        string raw = Safe(values);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string normalized = raw.ToLowerInvariant()
            .Replace("vanguard.sain.", string.Empty)
            .Replace("vanguard.tuning.", string.Empty)
            .Replace("vanguard.", string.Empty)
            .Replace('.', '_')
            .Replace('-', '_');
        return normalized;
    }

    private static string FormatRole(string? role, string? specialty)
    {
        string safeRole = FriendlyRole(role);
        string safeSpecialty = FriendlyRole(specialty);
        return string.IsNullOrWhiteSpace(safeSpecialty) ? safeRole : $"{safeRole} / {safeSpecialty}";
    }

    private static IEnumerable<string>? SelectTraits(IReadOnlyList<string>? primary, IEnumerable<string>? fallback)
    {
        return primary != null && primary.Count > 0 ? primary : fallback;
    }

    private static string FormatTraits(IEnumerable<string>? traits)
    {
        List<string> values = traits?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(4)
            .Select(value => FriendlyValue(value))
            .ToList() ?? new List<string>();
        return values.Count == 0 ? L("general.none") : string.Join(", ", values);
    }

    private static string FriendlyFaction(string? value) => VanguardUiText.Faction(value);

    private static string FriendlyRole(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : VanguardUiText.RoleToken(value);
    }

    private static string FriendlyRange(string? value) => VanguardUiText.Range(value);

    private static string FriendlySquadRole(string? value) => VanguardUiText.SquadRole(value);

    private static string FriendlyValue(params string?[] values) => VanguardUiText.Value(values);

    private static string FormatMoney(int amount)
    {
        return amount <= 0 ? "0 ₽" : $"{amount.ToString("N0", CultureInfo.InvariantCulture)} ₽";
    }

    private static string BuildPortraitPlaceholder(string displayName, string? side)
    {
        string prefix = Safe(side, "VG");
        string[] parts = displayName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        string initials = parts.Length == 0
            ? "?"
            : string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
        return $"{prefix}\n{initials}";
    }

    private void RenderInfoSections(VanguardOffRaidPanelModel model)
    {
        ClearInfoTable();
        if (infoTableScrollRoot == null || infoTableRoot == null || infoTableContentRect == null || bodyLabel == null)
        {
            return;
        }

        if (IsCardGridPanel(currentPanel))
        {
            infoTableScrollRoot.SetActive(false);
            return;
        }

        if (model.InfoSections.Count == 0)
        {
            infoTableScrollRoot.SetActive(false);
            bodyLabel.text = model.Body;
            return;
        }

        bodyLabel.text = string.Empty;
        infoTableScrollRoot.SetActive(true);

        const float topPadding = 8f;
        const float bottomPadding = 12f;
        const float headerHeight = 27f;
        const float rowHeight = 22f;
        const float sectionGap = 5f;
        float top = topPadding;

        foreach (VanguardInfoSectionModel section in model.InfoSections)
        {
            GameObject header = new("InfoSectionHeader", typeof(RectTransform), typeof(Image));
            header.transform.SetParent(infoTableRoot.transform, false);
            SetTopRect((RectTransform)header.transform, top, headerHeight);
            header.GetComponent<Image>().color = new Color(0.015f, 0.020f, 0.018f, 0.78f);
            infoTableObjects.Add(header);

            TextMeshProUGUI headerText = CreateText(header.transform, "HeaderText", 14, TextAlignmentOptions.Left, new Color(0.82f, 0.88f, 0.78f));
            SetRect(headerText.rectTransform, 0.045f, 0.06f, 0.96f, 0.94f);
            headerText.text = section.Title.ToUpperInvariant();
            top += headerHeight;

            foreach (VanguardInfoRowModel row in section.Rows)
            {
                GameObject rowObject = new("InfoRow", typeof(RectTransform), typeof(Image));
                rowObject.transform.SetParent(infoTableRoot.transform, false);
                SetTopRect((RectTransform)rowObject.transform, top, rowHeight);
                rowObject.GetComponent<Image>().color = new Color(0.020f, 0.026f, 0.023f, 0.48f);
                infoTableObjects.Add(rowObject);

                TextMeshProUGUI label = CreateText(rowObject.transform, "Label", 13, TextAlignmentOptions.Left, new Color(0.78f, 0.78f, 0.68f));
                SetRect(label.rectTransform, 0.070f, 0.04f, row.SetChecked != null ? 0.56f : 0.62f, 0.96f);
                label.text = row.Label;

                TextMeshProUGUI value = CreateText(rowObject.transform, "Value", 13, TextAlignmentOptions.Right, new Color(0.72f, 0.80f, 0.78f));
                SetRect(value.rectTransform, row.SetChecked != null ? 0.52f : 0.55f, 0.04f, row.SetChecked != null ? 0.89f : 0.95f, 0.96f);
                value.text = row.Value;

                if (row.SetChecked is Action<bool> setChecked && row.Checked is bool isChecked)
                {
                    bool enabled = row.Enabled && !actionInProgress;
                    value.color = isChecked
                        ? new Color(0.74f, 0.84f, 0.72f)
                        : new Color(0.70f, 0.66f, 0.58f);
                    CreateInfoCheckbox(rowObject.transform, row.Label, isChecked, enabled, () =>
                        ExecuteUiAction($"loot {row.Label}", () => setChecked(!isChecked)));
                }

                top += rowHeight;
            }

            top += sectionGap;
        }

        infoTableContentRect.sizeDelta = new Vector2(0f, Math.Max(1f, top + bottomPadding));
        if (infoTableScrollRect != null)
        {
            Canvas.ForceUpdateCanvases();
            infoTableScrollRect.verticalNormalizedPosition = 1f;
        }
    }

    private void CreateInfoCheckbox(Transform parent, string label, bool isChecked, bool enabled, Action action)
    {
        GameObject boxObject = new($"Checkbox_{label.Replace(" ", string.Empty)}", typeof(RectTransform), typeof(Image), typeof(Button));
        boxObject.transform.SetParent(parent, false);
        SetRect((RectTransform)boxObject.transform, 0.920f, 0.12f, 0.945f, 0.88f);

        Image image = boxObject.GetComponent<Image>();
        image.color = isChecked ? ButtonSelectedBackgroundColor : ButtonNormalBackgroundColor;
        image.raycastTarget = true;

        Button button = boxObject.GetComponent<Button>();
        button.interactable = enabled;
        button.transition = Selectable.Transition.ColorTint;
        ColorBlock colors = button.colors;
        colors.normalColor = isChecked ? ButtonSelectedBackgroundColor : ButtonNormalBackgroundColor;
        colors.highlightedColor = ButtonHoverBackgroundColor;
        colors.pressedColor = ButtonPressedBackgroundColor;
        colors.selectedColor = ButtonSelectedBackgroundColor;
        colors.disabledColor = ButtonDisabledBackgroundColor;
        button.colors = colors;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => action());

        TextMeshProUGUI mark = CreateText(boxObject.transform, "Mark", 15, TextAlignmentOptions.Center, isChecked ? ButtonHoverTextColor : ButtonDisabledTextColor);
        SetRect(mark.rectTransform, 0f, 0f, 1f, 1f);
        mark.text = isChecked ? "X" : string.Empty;
    }

    private static void SetTopRect(RectTransform rect, float top, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -top);
        rect.sizeDelta = new Vector2(0f, height);
    }

    private void ClearInfoTable()
    {
        foreach (GameObject infoObject in infoTableObjects)
        {
            if (infoObject != null)
            {
                Destroy(infoObject);
            }
        }

        infoTableObjects.Clear();
    }

    private void RenderActions(IReadOnlyList<VanguardOffRaidPanelAction> actions)
    {
        for (int i = 0; i < actionButtons.Count; i++)
        {
            Button button = actionButtons[i];
            if (i >= actions.Count)
            {
                button.gameObject.SetActive(false);
                continue;
            }

            VanguardOffRaidPanelAction action = actions[i];
            bool lifecycleBlocksEquipment = string.Equals(action.Label, L("action.equipment"), StringComparison.OrdinalIgnoreCase)
                && VanguardOperatorDirectInventoryLifecycle.IsBusy;
            bool enabled = action.Enabled && !actionInProgress && !lifecycleBlocksEquipment;
            button.gameObject.SetActive(true);
            button.interactable = enabled;

            // Use the explicit Label child captured at creation time. The button also contains
            // a hidden hover icon with TextMeshProUGUI; resolving the first text child is unsafe
            // and was the root cause of the billing action text being visible only on hover.
            TextMeshProUGUI? label = ResolveActionButtonLabel(i, button);
            if (label != null)
            {
                ApplyStandardButtonLabel(button.gameObject, label, action.Label, enabled);
            }

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => ExecuteUiAction(action.Label, action.Execute));
        }
    }

    private TextMeshProUGUI? ResolveActionButtonLabel(int index, Button button)
    {
        if (index >= 0 && index < actionLabels.Count && actionLabels[index] != null)
        {
            return actionLabels[index];
        }

        return FindButtonLabel(button.gameObject);
    }

    private static TextMeshProUGUI? FindButtonLabel(GameObject buttonObject)
    {
        foreach (TextMeshProUGUI text in buttonObject.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (text != null && string.Equals(text.gameObject.name, "Label", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
        }

        return null;
    }

    private void ApplyStandardButtonLabel(GameObject buttonObject, TextMeshProUGUI label, string text, bool enabled)
    {
        label.text = text;
        label.alignment = TextAlignmentOptions.Center;
        SetRect(label.rectTransform, 0.045f, 0.05f, 0.955f, 0.95f);
        label.enableAutoSizing = true;
        label.fontSizeMax = 15;
        label.fontSizeMin = 12;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;

        Color normalColor = enabled ? ButtonNormalTextColor : ButtonDisabledTextColor;
        label.color = normalColor;
        label.fontStyle = FontStyles.Normal;

        // Keep the hover state object synchronized with dynamic labels and disabled state.
        // Otherwise hover exit could restore an obsolete color captured when the button was empty.
        if (vanillaButtonVisualStates.TryGetValue(buttonObject, out VanillaButtonVisualState visualState))
        {
            visualState.NormalTextColor = normalColor;
            visualState.LabelText = text;
            SetVanillaButtonHover(buttonObject, false);
        }
    }


    private void ConfirmOpenInventory(string? operatorId, string? displayName)
    {
        if (!VanguardOperatorDirectInventoryLifecycle.CanOpenNow(out string lifecycleReason))
        {
            statusMessage = F("inventory.unavailable", lifecycleReason);
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_inventory_confirmation_blocked operator={operatorId ?? "<none>"}; reason={lifecycleReason}");
            Render();
            return;
        }

        string name = Safe(displayName, operatorId, "operator");
        string body = F("inventory.confirm.body", name);
        ShowConfirmation(L("inventory.confirm.title"), body, L("action.open_equipment"), () => OpenOperatorInventory(operatorId, name));
    }

    private void OpenOperatorInventory(string? operatorId, string displayName)
    {
        if (!VanguardOperatorDirectInventoryLifecycle.CanOpenNow(out string lifecycleReason))
        {
            statusMessage = F("inventory.unavailable", lifecycleReason);
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_DIRECT_EQUIPMENT_SCREEN_STATUS", $"direct_inventory_open_blocked_before_enter operator={operatorId ?? "<none>"}; reason={lifecycleReason}");
            RefreshState();
            currentPanel = VanguardOffRaidPanelKind.OperatorDossier;
            Render();
            return;
        }

        var response = VanguardOperatorInventoryModeClientState.Enter(operatorId);
        RefreshState();
        selectedOperatorId = response.OperatorId ?? operatorId ?? selectedOperatorId;
        currentPanel = VanguardOffRaidPanelKind.OperatorDossier;

        if (response.Success && response.Active)
        {
            string operatorName = Safe(response.OperatorDisplayName, displayName, operatorId, "operator");
            statusMessage = F("inventory.opening", operatorName);
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_INVENTORY_VANILLA_ENTRY_STATUS", $"enterSuccess operator={response.OperatorId ?? operatorId ?? "<none>"}; inventoryProfile={response.OperatorInventoryProfileId ?? "<none>"}; openingVanillaCharacter=true; directEquipmentEntry=true");

            if (VanguardOperatorDirectEquipmentScreenEntry.TryOpenFromCurrentMainMenu("vanguard_equipment_button", out string openReason))
            {
                statusMessage = F("inventory.opened", operatorName);
                VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_INVENTORY_VANILLA_ENTRY_STATUS", $"directOpenSuccess operator={response.OperatorId ?? operatorId ?? "<none>"}; reason={openReason}");
                HideScreenForDirectInventory();
                return;
            }

            statusMessage = F("inventory.open_failed_recovered", operatorName, FriendlyValue(openReason, L("value.no_response")));
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_VANILLA_ENTRY_STATUS", $"directOpenFailedRecovered operator={response.OperatorId ?? operatorId ?? "<none>"}; reason={openReason}");
            RefreshState();
            return;
        }

        statusMessage = F("inventory.enter_failed", displayName, FriendlyValue(response.Reason, L("value.no_response")));
        VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_INVENTORY_VANILLA_ENTRY_STATUS", $"enterFailed operator={operatorId ?? "<none>"}; reason={response.Reason ?? "no_response"}");
    }

    private void ConfirmExitInventoryMode()
    {
        string name = Safe(VanguardOperatorInventoryModeClientState.OperatorDisplayName, VanguardOperatorInventoryModeClientState.OperatorId, "operator");
        ShowConfirmation(L("action.exit_equipment_session"), F("inventory.exit_confirm", name), L("action.exit"), ExitOperatorInventoryMode);
    }

    private void ExitOperatorInventoryMode()
    {
        var response = VanguardOperatorInventoryModeClientState.Exit();
        statusMessage = response.Success
            ? L("inventory.exit_success")
            : F("inventory.exit_failed", FriendlyValue(response.Reason, L("value.no_response")));
        RefreshState();
        currentPanel = VanguardOffRaidPanelKind.OperatorDossier;
    }

    private void ConfirmHireContract(VanguardOperatorContractOfferDto offer)
    {
        string displayName = Safe(offer.DisplayName, offer.Callsign, "operator");
        string body = F(
            "confirm.contract.body",
            displayName,
            FriendlyFaction(offer.Side),
            FormatRole(offer.Role, offer.Specialty),
            offer.Level,
            FormatMoney(offer.HirePrice),
            FormatMoney(offer.SalaryPerRaid),
            FriendlyValue(offer.BasePersona, offer.Temperament, L("general.undefined")),
            FriendlyValue(offer.CombatStyle, L("general.undefined")));
        ShowConfirmation(L("confirm.contract.title"), body, L("action.hire_short"), () => HireContract(offer));
    }

    private void ConfirmRaidSelection(string? operatorId, string? displayName, bool selected)
    {
        string name = Safe(displayName, operatorId, "operator");
        string action = selected ? L("confirm.service.set_active") : L("confirm.service.set_rest");
        string body = F("confirm.service.body", action, name);
        ShowConfirmation(L("confirm.service.title"), body, selected ? L("action.select") : L("action.unselect"), () => SetRaidSelection(operatorId, displayName, selected));
    }

    private void ConfirmTreatOperator(VanguardOperatorMedicalProjectionDto projection)
    {
        string displayName = Safe(projection.DisplayName, projection.OperatorId, "operator");
        int amount = projection.HealCost + projection.RecoveryCost;
        string body = F(
            "confirm.treatment.body",
            displayName,
            FriendlyValue(projection.MedicalStatus, projection.RecoveryState, L("general.undefined")),
            FriendlyValue(projection.InjurySummary, L("general.no_details")),
            FormatMoney(amount));
        ShowConfirmation(L("confirm.treatment.title"), body, L("action.treat"), () => TreatOperator(projection));
    }

    private void ConfirmSignOpenInvoices()
    {
        int count = state.Billing.OpenInvoices?.Count ?? 0;
        string body = F("confirm.billing.body", count, FormatMoney(state.Billing.PendingSignatureDebt));
        ShowConfirmation(L("confirm.billing.title"), body, L("action.sign_short"), SignOpenInvoices);
    }


    private void HireContract(VanguardOperatorContractOfferDto offer)
    {
        string displayName = Safe(offer.DisplayName, offer.Callsign, "operator");
        var response = apiClient?.HireContract(offer.OfferId, offer.OperatorId);
        statusMessage = response?.Success == true
            ? F("status.hire_success", displayName, response.BillingDebtCreated)
            : F("status.hire_failed", displayName, FriendlyValue(response?.Reason, L("value.no_response")));
        RefreshState();
        currentPanel = VanguardOffRaidPanelKind.ActiveService;
    }

    private void SetRaidSelection(string? operatorId, string? displayName, bool selected)
    {
        var response = apiClient?.SetRaidSelection(operatorId, selected);
        string name = Safe(displayName, operatorId, "operator");
        statusMessage = response?.Success == true
            ? F("status.service_success", name, response.IsSelectedForRaid ? L("value.active") : L("value.reserve"))
            : F("status.service_failed", name, FriendlyValue(response?.Reason, L("value.no_response")));
        RefreshState();
        currentPanel = VanguardOffRaidPanelKind.ActiveService;
    }

    private void SetOperatorLootTargets(string? operatorId, bool corpsesEnabled, bool containersEnabled)
    {
        string next = corpsesEnabled && containersEnabled ? "CorpsesAndContainers"
            : corpsesEnabled ? "CorpsesOnly"
            : containersEnabled ? "ContainersOnly"
            : "Disabled";

        var response = apiClient?.SetLootTargetPolicy(operatorId, next);
        string responsePolicy = response?.LootTargetPolicy ?? string.Empty;
        string effective = response?.Success == true && !string.IsNullOrWhiteSpace(responsePolicy)
            ? responsePolicy
            : next;
        statusMessage = response?.Success == true
            ? F("status.loot_success", Safe(response?.OperatorId, operatorId, "operator"), FriendlyLootPolicy(effective))
            : F("status.loot_failed", FriendlyValue(response?.Reason, L("value.no_response")));

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorLootPolicyUiClarityStatusTag,
            $"VANGUARD_OPERATOR_LOOT_POLICY_UI_STATUS operator={Safe(response?.OperatorId, operatorId, "none")}; requested={next}; response={Safe(effective, "none")}; success={response?.Success == true}; corpses={corpsesEnabled}; containers={containersEnabled}; interaction=explicit_checkboxes; cyclicButton=false; persistentPerOperator=true");

        RefreshState();
        currentPanel = VanguardOffRaidPanelKind.OperatorDossier;
    }

    private static string FriendlyLootPolicy(string? policy)
    {
        string value = string.IsNullOrWhiteSpace(policy) ? "CorpsesOnly" : policy.Trim();
        if (string.Equals(value, "CorpsesAndContainers", StringComparison.OrdinalIgnoreCase)) return L("dossier.loot.both");
        if (string.Equals(value, "ContainersOnly", StringComparison.OrdinalIgnoreCase)) return L("dossier.loot.containers_only");
        if (string.Equals(value, "Disabled", StringComparison.OrdinalIgnoreCase)) return L("dossier.loot.disabled");
        return L("dossier.loot.corpses_only");
    }

    private void TreatOperator(VanguardOperatorMedicalProjectionDto projection)
    {
        var response = apiClient?.TreatMedical(projection.OperatorId);
        statusMessage = response?.Success == true
            ? F("status.treatment_success", projection.DisplayName, response.HealthBefore, response.HealthAfter, FormatMoney(response.Amount))
            : F("status.treatment_failed", projection.DisplayName, FriendlyValue(response?.Reason, L("value.no_response")));
        RefreshState();
        currentPanel = VanguardOffRaidPanelKind.FieldHospital;
    }

    private void SignOpenInvoices()
    {
        List<string> invoiceIds = state.Billing.OpenInvoices?
            .Where(invoice => !string.IsNullOrWhiteSpace(invoice.InvoiceId))
            .Select(invoice => invoice.InvoiceId!)
            .ToList() ?? new List<string>();

        var signResponse = apiClient?.SignBilling(invoiceIds);
        if (signResponse?.Success != true)
        {
            statusMessage = F("status.billing_failed", FriendlyValue(signResponse?.Reason, L("value.no_response")));
            RefreshState();
            currentPanel = VanguardOffRaidPanelKind.Billing;
            return;
        }

        var settlementResponse = apiClient?.ReconcileBilling();
        if (settlementResponse?.Success == true &&
            (settlementResponse.SettlementSucceeded || string.Equals(settlementResponse.Reason, "no_signed_invoice", StringComparison.OrdinalIgnoreCase)))
        {
            int invoiceCount = settlementResponse.InvoiceCount > 0 ? settlementResponse.InvoiceCount : signResponse.InvoiceCount;
            int amount = settlementResponse.Amount > 0 ? settlementResponse.Amount : signResponse.Amount;
            statusMessage = F("status.billing_success", invoiceCount, FormatMoney(amount));

            if (settlementResponse.SettlementSucceeded)
            {
                _ = ReloadPlayerProfileAfterBillingSettlementAsync();
            }
        }
        else
        {
            statusMessage = F("status.billing_failed", FriendlyValue(settlementResponse?.Reason, L("value.no_response")));
        }

        RefreshState();
        currentPanel = VanguardOffRaidPanelKind.Billing;
    }

    private async System.Threading.Tasks.Task ReloadPlayerProfileAfterBillingSettlementAsync()
    {
        bool reloaded = await VanguardOperatorInventoryModeClientState.TryReloadMainMenuProfileAfterDirectCommitAsync();
        if (reloaded)
        {
            VanguardClientDiagnosticsLog.Info("VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS", "client_profile_reload_after_settlement=ok");
        }
        else
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS", "client_profile_reload_after_settlement=failed; server_debit_remains_persisted; next_menu_or_profile_reload_will_resync");
        }
    }


    private void OpenDossier(string? operatorId)
    {
        selectedOperatorId = operatorId;
        currentPanel = VanguardOffRaidPanelKind.OperatorDossier;
        Render();
    }

    private void ExecuteUiAction(string actionName, Action action)
    {
        if (actionInProgress)
        {
            return;
        }

        try
        {
            actionInProgress = true;
            statusMessage = F("general.action_in_progress_named", actionName);
            VanguardClientDiagnosticsLog.Info("VANGUARD_OFFRAID_ACTION_GUARD_STATUS", $"action={actionName}; state=started");
            Render();
            action();
            VanguardClientDiagnosticsLog.Info("VANGUARD_OFFRAID_ACTION_GUARD_STATUS", $"action={actionName}; state=completed");
        }
        catch (Exception exception)
        {
            statusMessage = F("general.action_failed", exception.Message);
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OFFRAID_ACTION_GUARD_STATUS", $"action={actionName}; state=failed; reason={exception.Message}");
            VanguardClientDiagnosticsLog.Error(VanguardBuildVersion.OffRaidUiStatusTag, exception);
        }
        finally
        {
            actionInProgress = false;
            Render();
        }
    }

    private void RefreshVanguardMenuButtonAfterRestore()
    {
        if (menuButtonObject == null)
        {
            return;
        }

        menuButtonObject.SetActive(sourceButtonComponent == null || sourceButtonComponent.gameObject.activeSelf || !vanillaMenuHidden);
        ConfigureVanguardMenuButton(menuButtonObject);
        enforceMenuLabelUntilRealtime = Time.realtimeSinceStartup + 6.0f;
        if (sourceButtonComponent != null
            && sourceButtonComponent.transform.parent != null
            && sourceButtonComponent.transform is RectTransform sourceRect
            && menuButtonObject.transform is RectTransform menuRect)
        {
            ApplyTwoColumnMenuLayout(sourceButtonComponent.transform.parent, sourceRect, menuRect);
        }
    }

    private static void ConfigureVanguardMenuButton(GameObject buttonObject)
    {
        SetButtonLabel(buttonObject, VanguardOperatorsLocalizationService.Get("menu.button"));
        DisableTextLocalizationComponents(buttonObject);
        RestoreVanillaLikeButtonVisuals(buttonObject);
    }

    private static void DisableTextLocalizationComponents(GameObject buttonObject)
    {
        foreach (Component component in buttonObject.GetComponentsInChildren<Component>(true))
        {
            if (component == null || component is TextMeshProUGUI || component is Image || component is Button || component is CanvasRenderer || component is RectTransform)
            {
                continue;
            }

            string typeName = component.GetType().Name;
            if (!typeName.Contains("Local", StringComparison.OrdinalIgnoreCase)
                && !typeName.Contains("Translation", StringComparison.OrdinalIgnoreCase)
                && !typeName.Contains("Text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (component is Behaviour behaviour)
            {
                behaviour.enabled = false;
            }
        }
    }

    private static void RestoreVanillaLikeButtonVisuals(GameObject buttonObject)
    {
        foreach (Image image in buttonObject.GetComponentsInChildren<Image>(true))
        {
            image.enabled = true;
        }
    }

    private void RegisterVanillaButtonVisuals(GameObject buttonObject, TextMeshProUGUI label, GameObject hoverPlate, TextMeshProUGUI hoverIcon)
    {
        var state = new VanillaButtonVisualState(label, hoverPlate, hoverIcon, label.color);
        vanillaButtonVisualStates[buttonObject] = state;

        EventTrigger trigger = buttonObject.GetComponent<EventTrigger>() ?? buttonObject.AddComponent<EventTrigger>();
        trigger.triggers ??= new List<EventTrigger.Entry>();

        var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(_ => SetVanillaButtonHover(buttonObject, true));
        trigger.triggers.Add(enter);

        var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
        exit.callback.AddListener(_ => SetVanillaButtonHover(buttonObject, false));
        trigger.triggers.Add(exit);

        SetVanillaButtonHover(buttonObject, false);
    }

    private void SetVanillaButtonHover(GameObject buttonObject, bool hovering)
    {
        if (!vanillaButtonVisualStates.TryGetValue(buttonObject, out VanillaButtonVisualState state))
        {
            return;
        }

        if (state.HoverPlate != null)
        {
            state.HoverPlate.SetActive(hovering);
        }

        if (state.HoverIcon != null)
        {
            state.HoverIcon.gameObject.SetActive(hovering);
        }

        if (state.Label != null)
        {
            state.Label.color = hovering ? new Color(0.06f, 0.07f, 0.05f) : state.NormalTextColor;
            state.Label.fontStyle = hovering ? FontStyles.Bold : FontStyles.Normal;
        }
    }

    private void SetVanillaMenuVisible(bool visible)
    {
        if (visible)
        {
            RestoreVanillaMenuVisibility();
            return;
        }

        HideVanillaMenuVisibility();
    }

    private void HideVanillaMenuVisibility()
    {
        if (vanillaMenuHidden || screenRoot == null)
        {
            return;
        }

        Transform root = screenRoot.transform.parent ?? transform;
        HideFikaWarningBlocks(root);
        HideKnownMenuObject(playerButtonComponent?.gameObject);
        HideKnownMenuObject(tradeButtonComponent?.gameObject);
        HideKnownMenuObject(hideoutButtonComponent?.gameObject);
        HideKnownMenuObject(exitButtonComponent?.gameObject);
        HideKnownMenuObject(menuButtonObject);

        foreach (Button button in root.GetComponentsInChildren<Button>(true))
        {
            if (!IsInsideVanguardScreen(button.transform))
            {
                HideKnownMenuObject(button.gameObject);
            }
        }

        foreach (Selectable selectable in root.GetComponentsInChildren<Selectable>(true))
        {
            if (!IsInsideVanguardScreen(selectable.transform))
            {
                HideKnownMenuObject(selectable.gameObject);
            }
        }

        foreach (TextMeshProUGUI text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (IsInsideVanguardScreen(text.transform))
            {
                continue;
            }

            if (LooksLikeFikaWarningText(text.text))
            {
                HideKnownMenuObject(FindFikaBannerRoot(text.transform, root));
            }
            else
            {
                HideBehaviour(text);
            }
        }

        foreach (Image image in root.GetComponentsInChildren<Image>(true))
        {
            if (IsInsideVanguardScreen(image.transform))
            {
                continue;
            }

            if (LooksLikeFikaWarningImage(image))
            {
                HideKnownMenuObject(FindFikaBannerRoot(image.transform, root));
            }
        }

        vanillaMenuHidden = true;
    }

    private void RestoreVanillaMenuVisibility()
    {
        if (!vanillaMenuHidden && hiddenVanillaGameObjects.Count == 0 && hiddenVanillaBehaviours.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<Behaviour, bool> entry in hiddenVanillaBehaviours.ToList())
        {
            if (entry.Key != null)
            {
                entry.Key.enabled = entry.Value;
            }
        }

        foreach (KeyValuePair<GameObject, bool> entry in hiddenVanillaGameObjects.ToList())
        {
            if (entry.Key != null)
            {
                entry.Key.SetActive(entry.Value);
            }
        }

        hiddenVanillaBehaviours.Clear();
        hiddenVanillaGameObjects.Clear();
        vanillaMenuHidden = false;
        RefreshVanguardMenuButtonAfterRestore();
    }

    private void HideKnownMenuObject(GameObject? gameObject)
    {
        if (gameObject == null || screenRoot == null || IsInsideVanguardScreen(gameObject.transform))
        {
            return;
        }

        if (!hiddenVanillaGameObjects.ContainsKey(gameObject))
        {
            hiddenVanillaGameObjects[gameObject] = gameObject.activeSelf;
        }

        gameObject.SetActive(false);
    }

    private void HideBehaviour(Behaviour behaviour)
    {
        if (behaviour == null || screenRoot == null || IsInsideVanguardScreen(behaviour.transform))
        {
            return;
        }

        if (!hiddenVanillaBehaviours.ContainsKey(behaviour))
        {
            hiddenVanillaBehaviours[behaviour] = behaviour.enabled;
        }

        behaviour.enabled = false;
    }

    private bool IsInsideVanguardScreen(Transform candidate)
    {
        return screenRoot != null && candidate.IsChildOf(screenRoot.transform);
    }

    private static bool LooksLikeFikaWarningText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("Fika", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HORS LIGNE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Serveurs", StringComparison.OrdinalIgnoreCase)
            || text.Contains("coopération", StringComparison.OrdinalIgnoreCase);
    }

    private void HideFikaWarningBlocks(Transform root)
    {
        foreach (TextMeshProUGUI text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (!IsInsideVanguardScreen(text.transform) && LooksLikeFikaWarningText(text.text))
            {
                HideKnownMenuObject(FindFikaBannerRoot(text.transform, root));
            }
        }

        foreach (Image image in root.GetComponentsInChildren<Image>(true))
        {
            if (!IsInsideVanguardScreen(image.transform) && LooksLikeFikaWarningImage(image))
            {
                HideKnownMenuObject(FindFikaBannerRoot(image.transform, root));
            }
        }
    }

    private static bool LooksLikeFikaWarningImage(Image image)
    {
        string objectName = image.gameObject.name ?? string.Empty;
        if (objectName.Contains("Fika", StringComparison.OrdinalIgnoreCase)
            || objectName.Contains("Warning", StringComparison.OrdinalIgnoreCase)
            || objectName.Contains("Alert", StringComparison.OrdinalIgnoreCase)
            || objectName.Contains("Exclamation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Color color = image.color;
        return color.a > 0.20f && color.r > 0.55f && color.g > 0.18f && color.g < 0.65f && color.b < 0.18f;
    }

    private static GameObject FindFikaBannerRoot(Transform transformToInspect, Transform root)
    {
        Transform current = transformToInspect;
        GameObject best = transformToInspect.gameObject;
        float bestArea = 0f;
        for (int i = 0; i < 8 && current.parent != null && current.parent != root; i++)
        {
            if (current is RectTransform rect)
            {
                float width = Mathf.Abs(rect.rect.width);
                float height = Mathf.Abs(rect.rect.height);
                float area = width * height;
                if (width >= 180f && height >= 16f && height <= 180f && area >= bestArea)
                {
                    best = current.gameObject;
                    bestArea = area;
                }
            }

            current = current.parent;
        }

        return best;
    }

    private Sprite? ResolveOperatorPortraitSprite(string portraitKey, string side, string role)
    {
        string roleToken = NormalizePortraitRole(role);
        string sideToken = NormalizePortraitSide(side);
        string poolKey = roleToken.Length > 0 && sideToken.Length > 0 ? $"{roleToken}|{sideToken}" : string.Empty;

        string[] candidates;
        if (poolKey.Length > 0 && OperatorPortraitResourcePools.TryGetValue(poolKey, out string[]? exactPool))
        {
            candidates = exactPool;
        }
        else if (roleToken.Length > 0)
        {
            candidates = OperatorPortraitResourcePools
                .Where(pair => pair.Key.StartsWith(roleToken + "|", StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value)
                .ToArray();
        }
        else if (sideToken.Length > 0)
        {
            candidates = OperatorPortraitResourcePools
                .Where(pair => pair.Key.EndsWith("|" + sideToken, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value)
                .ToArray();
        }
        else
        {
            candidates = OperatorPortraitResourcePools.Values.SelectMany(pool => pool).ToArray();
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        string resourceName = candidates[StableIndex(portraitKey, candidates.Length)];
        if (portraitSpriteCache.TryGetValue(resourceName, out Sprite? cached))
        {
            return cached;
        }

        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                portraitSpriteCache[resourceName] = null;
                return null;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            byte[] bytes = memory.ToArray();
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                portraitSpriteCache[resourceName] = null;
                return null;
            }

            texture.name = Path.GetFileNameWithoutExtension(resourceName);
            texture.wrapMode = TextureWrapMode.Clamp;
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = texture.name;
            portraitSpriteCache[resourceName] = sprite;
            return sprite;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Error(VanguardBuildVersion.OffRaidUiStatusTag, exception);
            portraitSpriteCache[resourceName] = null;
            return null;
        }
    }

    private static string NormalizePortraitSide(string? side)
    {
        string token = side?.Trim().ToLowerInvariant() ?? string.Empty;
        if (token.Contains("bear"))
        {
            return "bear";
        }

        if (token.Contains("usec"))
        {
            return "usec";
        }

        return string.Empty;
    }

    private static string NormalizePortraitRole(string? role)
    {
        string token = role?.Trim().ToLowerInvariant() ?? string.Empty;
        string[] supportedRoles = { "assault", "recon", "support", "veteran", "marksman", "breacher", "medic" };
        return supportedRoles.FirstOrDefault(candidate => token.Contains(candidate)) ?? string.Empty;
    }

    private static string BuildPortraitKey(params string?[] values)
    {
        return string.Join("|", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static int StableIndex(string key, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        unchecked
        {
            uint hash = 2166136261u;
            foreach (char character in key)
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return (int)(hash % (uint)count);
        }
    }

    private static Component? ResolveMenuButton(Component menuScreenComponent)
    {
        return ResolveMenuButtonComponent(
            menuScreenComponent,
            "_playerButton",
            "_characterButton",
            "_profileButton",
            "_tradeButton",
            "_tradingButton",
            "_tradersButton",
            "_commerceButton")
            ?? menuScreenComponent.GetComponentsInChildren<Button>(true).FirstOrDefault();
    }

    private void CaptureOriginalMenuPositions(Transform parent)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name.Equals(MenuButtonName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (child is RectTransform rect && !originalMenuPositions.ContainsKey(rect) && LooksLikeButtonObject(child.gameObject))
            {
                originalMenuPositions[rect] = rect.anchoredPosition;
            }
        }
    }

    private void ApplyTwoColumnMenuLayout(Transform parent, RectTransform playerRect, RectTransform menuRect)
    {
        DisableParentAutoLayout(parent);

        List<Component> orderedButtons = ResolveOrderedOriginalMenuButtons(parent);
        Component? primaryComponent = playerButtonComponent ?? sourceButtonComponent;
        Component? tradeComponent = DistinctFrom(tradeButtonComponent, primaryComponent) ?? ComponentAtDistinct(orderedButtons, 1, primaryComponent);
        Component? hideoutComponent = DistinctFrom(hideoutButtonComponent, primaryComponent, tradeComponent) ?? ComponentAtDistinct(orderedButtons, 2, primaryComponent, tradeComponent);
        Component? exitComponent = DistinctFrom(exitButtonComponent, primaryComponent, tradeComponent, hideoutComponent) ?? ComponentAtDistinct(orderedButtons, 3, primaryComponent, tradeComponent, hideoutComponent);

        RectTransform? tradeRect = ToRectTransform(tradeComponent);
        RectTransform? hideoutRect = ToRectTransform(hideoutComponent);
        RectTransform? exitRect = ToRectTransform(exitComponent);

        Vector2 playerOriginal = ResolveOriginalMenuPosition(playerRect);
        float verticalStep = Mathf.Clamp(ResolveOriginalVerticalStep(playerRect, tradeRect), 56f, 86f);
        float columnOffset = ResolveColumnOffset(parent, playerRect);

        Vector2 rowOneLeft = playerOriginal + new Vector2(-columnOffset, 0f);
        Vector2 rowOneRight = playerOriginal + new Vector2(columnOffset, 0f);
        Vector2 rowTwoLeft = playerOriginal + new Vector2(-columnOffset, -verticalStep);
        Vector2 rowTwoRight = playerOriginal + new Vector2(columnOffset, -verticalStep);
        Vector2 rowThreeCenter = playerOriginal + new Vector2(0f, -verticalStep * 2f);

        ApplyButtonRect(playerRect, playerRect, rowOneLeft);
        if (tradeRect != null)
        {
            ApplyButtonRect(tradeRect, playerRect, rowOneRight);
        }

        if (hideoutRect != null)
        {
            ApplyButtonRect(hideoutRect, playerRect, rowTwoLeft);
        }

        ApplyButtonRect(menuRect, playerRect, rowTwoRight);
        if (exitRect != null)
        {
            ApplyButtonRect(exitRect, playerRect, rowThreeCenter);
        }

        ApplySiblingOrder(parent, primaryComponent, tradeComponent, hideoutComponent, menuButtonObject?.transform, exitComponent);
    }

    private static void DisableParentAutoLayout(Transform parent)
    {
        foreach (LayoutGroup layoutGroup in parent.GetComponents<LayoutGroup>())
        {
            if (layoutGroup != null)
            {
                layoutGroup.enabled = false;
            }
        }

        foreach (ContentSizeFitter fitter in parent.GetComponents<ContentSizeFitter>())
        {
            if (fitter != null)
            {
                fitter.enabled = false;
            }
        }
    }

    private List<Component> ResolveOrderedOriginalMenuButtons(Transform parent)
    {
        List<Component> buttons = new();
        HashSet<GameObject> seen = new();

        AddComponentIfValid(buttons, seen, playerButtonComponent);
        AddComponentIfValid(buttons, seen, tradeButtonComponent);
        AddComponentIfValid(buttons, seen, hideoutButtonComponent);
        AddComponentIfValid(buttons, seen, exitButtonComponent);

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name.Equals(MenuButtonName, StringComparison.OrdinalIgnoreCase) || !LooksLikeButtonObject(child.gameObject))
            {
                continue;
            }

            AddComponentIfValid(buttons, seen, ResolveButtonComponent(child.gameObject));
        }

        return buttons
            .Where(component => component != null && component.transform is RectTransform)
            .OrderByDescending(component => ResolveOriginalMenuPosition((RectTransform)component.transform).y)
            .ThenBy(component => ResolveOriginalMenuPosition((RectTransform)component.transform).x)
            .ToList();
    }

    private static void AddComponentIfValid(List<Component> buttons, HashSet<GameObject> seen, Component? component)
    {
        if (component == null || component.gameObject == null || !seen.Add(component.gameObject))
        {
            return;
        }

        buttons.Add(component);
    }

    private static Component? ComponentAt(IReadOnlyList<Component> components, int index)
    {
        return index >= 0 && index < components.Count ? components[index] : null;
    }

    private static Component? ComponentAtDistinct(IReadOnlyList<Component> components, int preferredIndex, params Component?[] usedComponents)
    {
        Component? preferred = ComponentAt(components, preferredIndex);
        if (DistinctFrom(preferred, usedComponents) != null)
        {
            return preferred;
        }

        foreach (Component component in components)
        {
            if (DistinctFrom(component, usedComponents) != null)
            {
                return component;
            }
        }

        return null;
    }

    private static Component? DistinctFrom(Component? candidate, params Component?[] usedComponents)
    {
        if (candidate == null || candidate.gameObject == null)
        {
            return null;
        }

        foreach (Component? used in usedComponents)
        {
            if (used != null && used.gameObject == candidate.gameObject)
            {
                return null;
            }
        }

        return candidate;
    }

    private static RectTransform? ToRectTransform(Component? component)
    {
        return component != null ? component.transform as RectTransform : null;
    }

    private Vector2 ResolveOriginalMenuPosition(RectTransform rect)
    {
        return originalMenuPositions.TryGetValue(rect, out Vector2 stored) ? stored : rect.anchoredPosition;
    }

    private static void ApplyButtonRect(RectTransform target, RectTransform template, Vector2 anchoredPosition)
    {
        target.anchorMin = template.anchorMin;
        target.anchorMax = template.anchorMax;
        target.pivot = template.pivot;
        target.sizeDelta = template.sizeDelta;
        target.localScale = template.localScale;
        target.anchoredPosition = anchoredPosition;
    }

    private static float ResolveColumnOffset(Transform parent, RectTransform playerRect)
    {
        float width = Mathf.Abs(playerRect.rect.width);
        if (width < 80f)
        {
            width = Mathf.Abs(playerRect.sizeDelta.x);
        }

        float parentWidth = parent is RectTransform parentRect ? Mathf.Abs(parentRect.rect.width) : 0f;
        float byButtonWidth = width > 0f ? width * 0.38f : 165f;
        float byParentWidth = parentWidth > 0f ? parentWidth * 0.085f : byButtonWidth;
        return Mathf.Clamp(Mathf.Max(byButtonWidth, byParentWidth), 135f, 210f);
    }

    private static void ApplySiblingOrder(Transform parent, Component? player, Component? trade, Component? hideout, Transform? menuButton, Component? exit)
    {
        int index = Mathf.Clamp(player?.transform.GetSiblingIndex() ?? 0, 0, parent.childCount - 1);
        SetSiblingIndex(player?.transform, ref index);
        SetSiblingIndex(trade?.transform, ref index);
        SetSiblingIndex(hideout?.transform, ref index);
        SetSiblingIndex(menuButton, ref index);
        SetSiblingIndex(exit?.transform, ref index);
    }

    private static void SetSiblingIndex(Transform? transformToMove, ref int index)
    {
        if (transformToMove == null)
        {
            return;
        }

        transformToMove.SetSiblingIndex(index);
        index++;
    }

    private float ResolveOriginalVerticalStep(RectTransform playerRect, RectTransform? nextRect)
    {
        if (nextRect != null)
        {
            float delta = Mathf.Abs(ResolveOriginalMenuPosition(playerRect).y - ResolveOriginalMenuPosition(nextRect).y);
            if (delta >= 28f && delta <= 180f)
            {
                return delta;
            }
        }

        float height = Mathf.Abs(playerRect.rect.height);
        return Mathf.Clamp(height + 10f, 42f, 86f);
    }

    private static Component? ResolveMenuButtonComponent(Component menuScreenComponent, params string[] fieldNames)
    {
        Type type = menuScreenComponent.GetType();
        foreach (string fieldName in fieldNames)
        {
            object? value = ReadMember(menuScreenComponent, type, fieldName);
            Component? component = ToComponent(value);
            if (component != null)
            {
                return component;
            }
        }

        return null;
    }

    private static object? ReadMember(object target, Type type, string name)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            try
            {
                return field.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            try
            {
                return property.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        Type? baseType = type.BaseType;
        return baseType != null ? ReadMember(target, baseType, name) : null;
    }

    private static object? ReadMember(object target, string name)
    {
        return ReadMember(target, target.GetType(), name);
    }

    private static Component? ToComponent(object? value)
    {
        if (value is Component component)
        {
            return component;
        }

        if (value is GameObject gameObject)
        {
            return ResolveButtonComponent(gameObject);
        }

        return null;
    }

    private static Component? ResolveButtonComponent(GameObject gameObject)
    {
        foreach (Component component in gameObject.GetComponents<Component>())
        {
            if (component == null)
            {
                continue;
            }

            string typeName = component.GetType().Name;
            if (typeName.Contains("DefaultUIButton", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("UIButton", StringComparison.OrdinalIgnoreCase)
                || ReadMember(component, "OnClick") != null
                || ReadMember(component, "onClick") != null
                || ReadMember(component, "_onClick") != null)
            {
                return component;
            }
        }

        return gameObject.GetComponent<Button>();
    }

    private static bool LooksLikeButtonObject(GameObject gameObject)
    {
        return gameObject.GetComponent<Button>() != null || ResolveButtonComponent(gameObject) != null;
    }

    private static TMP_FontAsset? ResolveInheritedFont(GameObject source)
    {
        return source.GetComponentInChildren<TextMeshProUGUI>(true)?.font;
    }

    private static void ApplyMenuButtonLayout(GameObject source, GameObject menuButton)
    {
        if (source.transform is not RectTransform sourceRect || menuButton.transform is not RectTransform menuRect)
        {
            return;
        }

        menuRect.anchorMin = sourceRect.anchorMin;
        menuRect.anchorMax = sourceRect.anchorMax;
        menuRect.sizeDelta = sourceRect.sizeDelta;
        menuRect.anchoredPosition = sourceRect.anchoredPosition + new Vector2(0f, -Mathf.Max(48f, sourceRect.rect.height + 8f));
    }

    private static void SetButtonLabel(GameObject buttonObject, string label)
    {
        foreach (TextMeshProUGUI text in buttonObject.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            text.text = label;
        }
    }

    private static void TryClearButtonIcon(GameObject buttonObject)
    {
        foreach (Image image in buttonObject.GetComponentsInChildren<Image>(true))
        {
            if (image.gameObject == buttonObject)
            {
                continue;
            }

            if (image.GetComponentInChildren<TextMeshProUGUI>(true) == null)
            {
                image.enabled = false;
            }
        }
    }

    private static void SetButtonClick(GameObject buttonObject, Action action)
    {
        UnityAction unityAction = () => action();

        Button? existingButton = buttonObject.GetComponent<Button>();
        Button button = existingButton != null ? existingButton : buttonObject.AddComponent<Button>();

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(unityAction);

        foreach (Component component in buttonObject.GetComponents<Component>())
        {
            if (component == null || component is Button)
            {
                continue;
            }

            object? clickEvent = ReadMember(component, "OnClick") ?? ReadMember(component, "onClick") ?? ReadMember(component, "_onClick");
            if (clickEvent != null)
            {
                TryReplaceUnityEventListener(clickEvent, unityAction);
            }
        }
    }

    private static void TryReplaceUnityEventListener(object clickEvent, UnityAction action)
    {
        try
        {
            MethodInfo? removeAllListeners = clickEvent.GetType().GetMethod("RemoveAllListeners", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            removeAllListeners?.Invoke(clickEvent, Array.Empty<object>());

            MethodInfo? addListener = clickEvent.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "AddListener" && method.GetParameters().Length == 1);
            addListener?.Invoke(clickEvent, new object[] { action });
        }
        catch
        {
            // The UnityEngine.UI.Button listener above remains active.
        }
    }

    private static void SetRect(RectTransform rect, float xMin, float yMin, float xMax, float yMax)
    {
        rect.anchorMin = new Vector2(xMin, yMin);
        rect.anchorMax = new Vector2(xMax, yMax);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private sealed class VanillaButtonVisualState
    {
        public VanillaButtonVisualState(TextMeshProUGUI label, GameObject hoverPlate, TextMeshProUGUI hoverIcon, Color normalTextColor)
        {
            Label = label;
            HoverPlate = hoverPlate;
            HoverIcon = hoverIcon;
            NormalTextColor = normalTextColor;
        }

        public TextMeshProUGUI Label { get; }
        public GameObject HoverPlate { get; }
        public TextMeshProUGUI HoverIcon { get; }

        // Dynamic action buttons reuse the same GameObject and receive new labels each render.
        // The normal text color must therefore be mutable so hover exit restores the current
        // enabled/disabled color instead of a stale value captured at creation.
        public Color NormalTextColor { get; set; }
        public string LabelText { get; set; } = string.Empty;
    }

    private sealed class VanguardOperatorCardModel
    {
        public string Title { get; init; } = string.Empty;
        public string Side { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string PortraitSide { get; init; } = string.Empty;
        public string PortraitRole { get; init; } = string.Empty;
        public int Level { get; init; }
        public string StateLabel { get; init; } = string.Empty;
        public string AccentLabel { get; init; } = string.Empty;
        public string Placeholder { get; init; } = string.Empty;
        public string PortraitKey { get; init; } = string.Empty;
        public string Tooltip { get; init; } = string.Empty;
        public string ActionLabel { get; init; } = string.Empty;
        public bool Enabled { get; init; } = true;
        public Action Execute { get; init; } = static () => { };
        public string SecondaryActionLabel { get; init; } = string.Empty;
        public bool SecondaryEnabled { get; init; } = true;
        public Action? SecondaryExecute { get; init; }
        public Action? CardExecute { get; init; }
    }

    private static string L(string key) => VanguardOperatorsLocalizationService.Get(key);

    private static string F(string key, params object?[] args) => VanguardOperatorsLocalizationService.Format(key, args);

    private static string Safe(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}

#else
internal sealed class VanguardOffRaidUiController
{
    public static void TryInitialize(object menuScreenInstance)
    {
    }
}
#endif

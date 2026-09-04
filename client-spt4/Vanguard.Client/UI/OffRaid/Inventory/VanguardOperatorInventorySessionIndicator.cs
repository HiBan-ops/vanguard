using Vanguard.Client.Diagnostics;
using Vanguard.Client.UI.OffRaid.Localization;

#if SPT_CLIENT
using BepInEx.Configuration;
using UnityEngine;
#endif

// Responsibility: renders and configures a persistent, globally visible identity/status marker while the client is inside or is reconciling an Operator inventory authority session.
// Flow: local F12 presentation settings define anchor/offset/scale/opacity; OnGUI reads the live Operator session state or a captured reconciliation snapshot and paints a non-interactive callout across off-raid screens until player authority is restored.
// Authority boundary: presentation configuration only; the indicator never changes navigation, inventory, economy, or session state and is never synchronized to host/headless.
// Invariant: the callout follows ACTIVE -> RECONCILING -> HIDDEN without losing the Operator identity when server exit clears the mirrored inventory-mode state before the player-menu reload has completed.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal enum VanguardOperatorInventorySessionIndicatorAnchor
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

internal static class VanguardOperatorInventorySessionIndicator
{
#if SPT_CLIENT
    private const string Section = "Vanguard - Off-Raid - Operator Session Indicator";

    private static readonly object ReconciliationGate = new();

    private static ConfigEntry<bool>? enabled;
    private static ConfigEntry<VanguardOperatorInventorySessionIndicatorAnchor>? anchor;
    private static ConfigEntry<float>? offsetX;
    private static ConfigEntry<float>? offsetY;
    private static ConfigEntry<float>? scale;
    private static ConfigEntry<float>? opacity;
    private static ConfigEntry<bool>? showSubtitle;
    private static bool isBound;

    private static bool reconciliationInProgress;
    private static string? reconciliationIdentity;
    private static string reconciliationSource = "<none>";

    private static GUIStyle? titleStyle;
    private static GUIStyle? subtitleStyle;
    private static int cachedTitleSize;
    private static int cachedSubtitleSize;

    public static bool IsReconciliationInProgress
    {
        get
        {
            lock (ReconciliationGate)
            {
                return reconciliationInProgress;
            }
        }
    }

    public static void Bind(ConfigFile config)
    {
        if (isBound)
        {
            return;
        }

        const string localOnly = "LOCAL CLIENT ONLY. Presentation setting; never committed and never synchronized to host/headless.";
        enabled = config.Bind(Section, "Enabled", true, localOnly);
        anchor = config.Bind(Section, "Anchor", VanguardOperatorInventorySessionIndicatorAnchor.TopRight, localOnly + " Position X/Y are offsets from this anchor.");
        offsetX = config.Bind(Section, "Position X", -360f, new ConfigDescription(localOnly + " Negative values move left from a right-side anchor.", new AcceptableValueRange<float>(-1920f, 1920f)));
        offsetY = config.Bind(Section, "Position Y", 12f, new ConfigDescription(localOnly + " Positive values move down from a top anchor.", new AcceptableValueRange<float>(-1080f, 1080f)));
        scale = config.Bind(Section, "Scale", 1.00f, new ConfigDescription(localOnly, new AcceptableValueRange<float>(0.65f, 1.60f)));
        opacity = config.Bind(Section, "Opacity", 0.94f, new ConfigDescription(localOnly, new AcceptableValueRange<float>(0.20f, 1.00f)));
        showSubtitle = config.Bind(Section, "Show session hint", true, localOnly + " Controls the small SESSION ACTIVE / Main Menu hint only. Reconciliation status always remains visible while player authority is being restored.");
        isBound = true;
    }

    public static void BeginPlayerReconciliation(string source)
    {
        string identity = ResolveLiveIdentity();
        lock (ReconciliationGate)
        {
            reconciliationIdentity = identity;
            reconciliationSource = string.IsNullOrWhiteSpace(source) ? "<none>" : source;
            reconciliationInProgress = true;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryExitReloadStatusTag,
            $"operator_session_indicator_reconciliation_started source={source}; identity={identity}; active={VanguardOperatorInventoryModeClientState.IsActive}");
    }

    public static void EndPlayerReconciliation(string source, bool success, string reason)
    {
        string identity;
        string startedFrom;
        lock (ReconciliationGate)
        {
            identity = reconciliationIdentity ?? "OPERATOR";
            startedFrom = reconciliationSource;
            reconciliationInProgress = false;
            reconciliationIdentity = null;
            reconciliationSource = "<none>";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorInventoryExitReloadStatusTag,
            $"operator_session_indicator_reconciliation_completed source={source}; startedFrom={startedFrom}; identity={identity}; success={success}; reason={reason}; active={VanguardOperatorInventoryModeClientState.IsActive}");
    }

    public static void Draw()
    {
        if (!(enabled?.Value ?? true))
        {
            return;
        }

        bool reconciling;
        string? capturedIdentity;
        lock (ReconciliationGate)
        {
            reconciling = reconciliationInProgress;
            capturedIdentity = reconciliationIdentity;
        }

        if (!VanguardOperatorInventoryModeClientState.IsActive && !reconciling)
        {
            return;
        }

        float resolutionScale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 1.35f);
        float userScale = Mathf.Clamp(scale?.Value ?? 1.00f, 0.65f, 1.60f);
        float effectiveScale = resolutionScale * userScale;
        int titleSize = Mathf.RoundToInt(21f * effectiveScale);
        int subtitleSize = Mathf.RoundToInt(11f * effectiveScale);
        EnsureStyles(titleSize, subtitleSize);

        string identity = reconciling
            ? FirstNonEmpty(capturedIdentity, ResolveLiveIdentity())
            : ResolveLiveIdentity();

        string title = VanguardOperatorsLocalizationService.Format("inventory.session_indicator.title", identity.ToUpperInvariant());
        bool drawSubtitle = reconciling || (showSubtitle?.Value ?? true);
        string subtitle = !drawSubtitle
            ? string.Empty
            : VanguardOperatorsLocalizationService.Get(reconciling
                ? "inventory.session_indicator.reconciling"
                : "inventory.session_indicator.active");

        float width = Mathf.Clamp(310f * effectiveScale, 210f, 500f);
        float height = drawSubtitle
            ? Mathf.Clamp(62f * effectiveScale, 44f, 100f)
            : Mathf.Clamp(42f * effectiveScale, 34f, 70f);
        float x = ResolveX(width, resolutionScale);
        float y = ResolveY(height, resolutionScale);
        Rect panel = new(x, y, width, height);

        float resolvedOpacity = Mathf.Clamp(opacity?.Value ?? 0.94f, 0.20f, 1.00f);
        float pulse = reconciling ? 0.80f + (0.20f * ((Mathf.Sin(Time.realtimeSinceStartup * 5f) + 1f) * 0.5f)) : 1f;
        Color panelColor = reconciling
            ? new Color(0.24f, 0.13f, 0.025f, resolvedOpacity)
            : new Color(0.28f, 0.035f, 0.035f, resolvedOpacity);
        Color accentColor = reconciling
            ? new Color(1.00f, 0.56f, 0.08f, resolvedOpacity * pulse)
            : new Color(0.92f, 0.15f, 0.12f, resolvedOpacity);
        Color subtitleColor = reconciling
            ? new Color(1f, 0.79f, 0.36f, resolvedOpacity)
            : new Color(1f, 0.72f, 0.68f, resolvedOpacity);

        Color previousColor = GUI.color;
        Color previousBackground = GUI.backgroundColor;
        try
        {
            GUI.backgroundColor = panelColor;
            GUI.color = Color.white;
            GUI.Box(panel, GUIContent.none);

            Rect accent = new(panel.x, panel.y, Mathf.Max(4f, 5f * effectiveScale), panel.height);
            GUI.color = accentColor;
            GUI.DrawTexture(accent, Texture2D.whiteTexture);

            GUI.color = new Color(1f, 1f, 1f, resolvedOpacity);
            float textLeft = panel.x + 18f * effectiveScale;
            float titleY = drawSubtitle ? panel.y + 8f * effectiveScale : panel.y + 6f * effectiveScale;
            GUI.Label(new Rect(textLeft, titleY, panel.width - 28f * effectiveScale, 30f * effectiveScale), title, titleStyle);
            if (drawSubtitle)
            {
                Color previousSubtitleColor = subtitleStyle!.normal.textColor;
                subtitleStyle.normal.textColor = subtitleColor;
                GUI.Label(new Rect(textLeft, panel.y + 35f * effectiveScale, panel.width - 28f * effectiveScale, 18f * effectiveScale), subtitle, subtitleStyle);
                subtitleStyle.normal.textColor = previousSubtitleColor;
            }
        }
        finally
        {
            GUI.color = previousColor;
            GUI.backgroundColor = previousBackground;
        }
    }

    private static string ResolveLiveIdentity()
    {
        return FirstNonEmpty(
            VanguardOperatorInventoryModeClientState.OperatorCallsign,
            VanguardOperatorInventoryModeClientState.OperatorDisplayName,
            VanguardOperatorInventoryModeClientState.OperatorId,
            VanguardOperatorsLocalizationService.Get("general.operator"));
    }

    private static float ResolveX(float width, float resolutionScale)
    {
        float configuredOffset = (offsetX?.Value ?? -360f) * resolutionScale;
        VanguardOperatorInventorySessionIndicatorAnchor selected = anchor?.Value ?? VanguardOperatorInventorySessionIndicatorAnchor.TopRight;
        bool fromRight = selected == VanguardOperatorInventorySessionIndicatorAnchor.TopRight
            || selected == VanguardOperatorInventorySessionIndicatorAnchor.BottomRight;
        float x = fromRight ? Screen.width - width + configuredOffset : configuredOffset;
        return Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - width));
    }

    private static float ResolveY(float height, float resolutionScale)
    {
        float configuredOffset = (offsetY?.Value ?? 12f) * resolutionScale;
        VanguardOperatorInventorySessionIndicatorAnchor selected = anchor?.Value ?? VanguardOperatorInventorySessionIndicatorAnchor.TopRight;
        bool fromBottom = selected == VanguardOperatorInventorySessionIndicatorAnchor.BottomLeft
            || selected == VanguardOperatorInventorySessionIndicatorAnchor.BottomRight;
        float y = fromBottom ? Screen.height - height + configuredOffset : configuredOffset;
        return Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - height));
    }

    private static void EnsureStyles(int titleSize, int subtitleSize)
    {
        if (titleStyle != null
            && subtitleStyle != null
            && cachedTitleSize == titleSize
            && cachedSubtitleSize == subtitleSize)
        {
            return;
        }

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = titleSize,
            fontStyle = FontStyle.Bold
        };
        titleStyle.normal.textColor = Color.white;

        subtitleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = subtitleSize,
            fontStyle = FontStyle.Bold
        };
        subtitleStyle.normal.textColor = new Color(1f, 0.72f, 0.68f, 1f);

        cachedTitleSize = titleSize;
        cachedSubtitleSize = subtitleSize;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "OPERATOR";
    }
#else
    public static bool IsReconciliationInProgress => false;

    public static void BeginPlayerReconciliation(string source)
    {
    }

    public static void EndPlayerReconciliation(string source, bool success, string reason)
    {
    }

    public static void Draw()
    {
    }
#endif
}

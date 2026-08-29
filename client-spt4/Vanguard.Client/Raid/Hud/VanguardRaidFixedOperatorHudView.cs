#if SPT_CLIENT
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Responsibility: Draws the fixed in-raid Operator HUD from already-resolved semantic rows while keeping layout and input entirely local to this client.
// Flow: Each frame receives bounded Operator HUD state, computes stable row/card geometry and visual text/icons, then renders the local overlay without querying or changing AI state.
// Authority boundary: The HUD view owns presentation only; authoritative raid facts come from the HUD resolver/telemetry path and F12 visual settings remain client-local.
// Invariant: Rendering may hide, clip or reflow stale/unavailable presentation data, but it must never create gameplay truth or send control input back into an Operator.
namespace Vanguard.Client.Raid.Hud;

internal sealed record VanguardRaidFixedOperatorHudRow(
    string Key,
    string Nickname,
    bool HealthReadable,
    int HealthPercent,
    VanguardRaidFixedOperatorHudSemanticState Semantic)
{
    public string ContentSignature => string.Join("|",
        Key,
        Nickname,
        HealthReadable,
        HealthPercent,
        Semantic.DisplaySignature);
}

/// <summary>
/// Fixed, presentation-only squad panel. It owns no gameplay state and never performs runtime probing.
/// </summary>
internal sealed class VanguardRaidFixedOperatorHudView
{
    private const float CompactWidth = 400f;
    private const float DetailedWidth = 600f;
    private const float HeaderHeight = 27f;
    private const float CompactRowHeight = 30f;
    private const float DetailedRowHeight = 44f;
    private const float SafeMargin = 8f;
    private const float HealthBarWidth = 108f;
    private const float CompactHealthBarWidth = 100f;
    private const float HealthBarHeight = 8f;

    private readonly RectTransform root;
    private readonly Image border;
    private readonly Image background;
    private readonly TextMeshProUGUI headerText;
    private readonly RectTransform rowsRoot;
    private readonly Dictionary<string, RowView> rows = new(StringComparer.Ordinal);
    private readonly TMP_FontAsset? fontAsset;
    private string lastLayoutSignature = string.Empty;
    private string lastContentSignature = string.Empty;

    private VanguardRaidFixedOperatorHudView(
        RectTransform root,
        Image border,
        Image background,
        TextMeshProUGUI headerText,
        RectTransform rowsRoot,
        TMP_FontAsset? fontAsset)
    {
        this.root = root;
        this.border = border;
        this.background = background;
        this.headerText = headerText;
        this.rowsRoot = rowsRoot;
        this.fontAsset = fontAsset;
    }

    public bool IsAlive => root != null;

    public static VanguardRaidFixedOperatorHudView Create(RectTransform parent, TMP_FontAsset? fontAsset)
    {
        var rootObject = new GameObject("VanguardFixedOperatorDecisionHud", typeof(RectTransform));
        rootObject.transform.SetParent(parent, false);
        var root = rootObject.GetComponent<RectTransform>();
        root.anchorMin = new Vector2(0f, 1f);
        root.anchorMax = new Vector2(0f, 1f);
        root.pivot = new Vector2(0f, 1f);
        root.sizeDelta = new Vector2(CompactWidth, HeaderHeight);

        var border = AddImage("Border", root, Color.white);
        Stretch(border.rectTransform, Vector2.zero, Vector2.zero);

        var background = AddImage("Background", root, Color.black);
        Stretch(background.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f));

        var headerText = AddText("Header", root, fontAsset, 12f, FontStyles.Bold);
        headerText.alignment = TextAlignmentOptions.Left;
        headerText.rectTransform.anchorMin = new Vector2(0f, 1f);
        headerText.rectTransform.anchorMax = new Vector2(1f, 1f);
        headerText.rectTransform.pivot = new Vector2(0.5f, 1f);
        headerText.rectTransform.anchoredPosition = new Vector2(0f, -4f);
        headerText.rectTransform.sizeDelta = new Vector2(-24f, 19f);

        var rowsRoot = AddRect("Rows", root);
        rowsRoot.anchorMin = new Vector2(0f, 1f);
        rowsRoot.anchorMax = new Vector2(1f, 1f);
        rowsRoot.pivot = new Vector2(0.5f, 1f);
        rowsRoot.anchoredPosition = new Vector2(0f, -HeaderHeight);
        rowsRoot.sizeDelta = Vector2.zero;

        rootObject.SetActive(false);
        return new VanguardRaidFixedOperatorHudView(root, border, background, headerText, rowsRoot, fontAsset);
    }

    public void SetActive(bool active)
    {
        if (root != null && root.gameObject.activeSelf != active)
        {
            root.gameObject.SetActive(active);
        }
    }

    public void Update(
        IReadOnlyList<VanguardRaidFixedOperatorHudRow> incomingRows,
        VanguardRaidFixedOperatorHudSettings settings)
    {
        if (root == null)
        {
            return;
        }

        if (!settings.Enabled || incomingRows.Count == 0)
        {
            SetActive(false);
            return;
        }

        SetActive(true);
        ApplyLayout(incomingRows.Count, settings);

        string contentSignature = string.Join("\n", BuildContentSignatures(incomingRows, settings));
        if (!string.Equals(contentSignature, lastContentSignature, StringComparison.Ordinal))
        {
            headerText.text = incomingRows.Count == 1
                ? "VANGUARD / 1 OPERATOR"
                : $"VANGUARD / {incomingRows.Count} OPERATORS";
            headerText.color = settings.Palette.PrimaryText;

            var liveKeys = new HashSet<string>(StringComparer.Ordinal);
            float rowHeight = settings.DisplayMode == VanguardRaidFixedOperatorHudDisplayMode.Detailed
                ? DetailedRowHeight
                : CompactRowHeight;

            for (int index = 0; index < incomingRows.Count; index++)
            {
                var rowState = incomingRows[index];
                liveKeys.Add(rowState.Key);
                if (!rows.TryGetValue(rowState.Key, out RowView? row) || row is null || !row.IsAlive)
                {
                    row = RowView.Create(rowsRoot, fontAsset);
                    rows[rowState.Key] = row;
                }

                row.Update(rowState, settings, index, rowHeight);
            }

            foreach (string staleKey in new List<string>(rows.Keys))
            {
                if (liveKeys.Contains(staleKey))
                {
                    continue;
                }

                rows[staleKey].Destroy();
                rows.Remove(staleKey);
            }

            lastContentSignature = contentSignature;
        }

        ApplyPalette(settings.Palette);
    }

    public void Destroy()
    {
        foreach (RowView row in rows.Values)
        {
            row.Destroy();
        }

        rows.Clear();
        if (root != null)
        {
            UnityEngine.Object.Destroy(root.gameObject);
        }
    }

    private void ApplyLayout(int rowCount, VanguardRaidFixedOperatorHudSettings settings)
    {
        string signature = settings.LayoutSignature + "|rows=" + rowCount;
        if (string.Equals(signature, lastLayoutSignature, StringComparison.Ordinal))
        {
            return;
        }

        bool detailed = settings.DisplayMode == VanguardRaidFixedOperatorHudDisplayMode.Detailed;
        float width = detailed ? DetailedWidth : CompactWidth;
        float rowHeight = detailed ? DetailedRowHeight : CompactRowHeight;
        float height = HeaderHeight + rowHeight * rowCount + 2f;

        ResolveAnchor(settings.Anchor, out Vector2 anchor, out Vector2 pivot);
        root.anchorMin = anchor;
        root.anchorMax = anchor;
        root.pivot = pivot;
        root.sizeDelta = new Vector2(width, height);
        root.localScale = new Vector3(settings.Scale, settings.Scale, 1f);
        root.anchoredPosition = ClampToSafeArea(
            root.parent as RectTransform,
            anchor,
            pivot,
            new Vector2(width * settings.Scale, height * settings.Scale),
            new Vector2(settings.OffsetX, settings.OffsetY));

        rowsRoot.sizeDelta = new Vector2(0f, rowHeight * rowCount);
        lastLayoutSignature = signature;
        lastContentSignature = string.Empty;
    }

    private void ApplyPalette(VanguardRaidFixedOperatorHudPalette palette)
    {
        border.color = palette.Border;
        background.color = palette.Background;
        headerText.color = palette.PrimaryText;
    }

    private static IEnumerable<string> BuildContentSignatures(
        IReadOnlyList<VanguardRaidFixedOperatorHudRow> incomingRows,
        VanguardRaidFixedOperatorHudSettings settings)
    {
        yield return settings.LayoutSignature;
        foreach (var row in incomingRows)
        {
            yield return row.ContentSignature;
        }
    }

    private static Vector2 ClampToSafeArea(
        RectTransform? parent,
        Vector2 anchor,
        Vector2 pivot,
        Vector2 scaledSize,
        Vector2 desired)
    {
        if (parent is null || parent.rect.width <= 1f || parent.rect.height <= 1f)
        {
            return desired;
        }

        float parentWidth = parent.rect.width;
        float parentHeight = parent.rect.height;
        float minX = SafeMargin - anchor.x * parentWidth + pivot.x * scaledSize.x;
        float maxX = parentWidth - SafeMargin - anchor.x * parentWidth - (1f - pivot.x) * scaledSize.x;
        float minY = SafeMargin - anchor.y * parentHeight + pivot.y * scaledSize.y;
        float maxY = parentHeight - SafeMargin - anchor.y * parentHeight - (1f - pivot.y) * scaledSize.y;

        if (minX > maxX)
        {
            float middle = (minX + maxX) * 0.5f;
            minX = middle;
            maxX = middle;
        }

        if (minY > maxY)
        {
            float middle = (minY + maxY) * 0.5f;
            minY = middle;
            maxY = middle;
        }

        return new Vector2(
            Mathf.Clamp(desired.x, minX, maxX),
            Mathf.Clamp(desired.y, minY, maxY));
    }

    private static void ResolveAnchor(
        VanguardRaidFixedOperatorHudAnchor setting,
        out Vector2 anchor,
        out Vector2 pivot)
    {
        switch (setting)
        {
            case VanguardRaidFixedOperatorHudAnchor.TopRight:
                anchor = new Vector2(1f, 1f);
                pivot = new Vector2(1f, 1f);
                return;
            case VanguardRaidFixedOperatorHudAnchor.BottomLeft:
                anchor = new Vector2(0f, 0f);
                pivot = new Vector2(0f, 0f);
                return;
            case VanguardRaidFixedOperatorHudAnchor.BottomRight:
                anchor = new Vector2(1f, 0f);
                pivot = new Vector2(1f, 0f);
                return;
            case VanguardRaidFixedOperatorHudAnchor.CenterLeft:
                anchor = new Vector2(0f, 0.5f);
                pivot = new Vector2(0f, 0.5f);
                return;
            case VanguardRaidFixedOperatorHudAnchor.CenterRight:
                anchor = new Vector2(1f, 0.5f);
                pivot = new Vector2(1f, 0.5f);
                return;
            default:
                anchor = new Vector2(0f, 1f);
                pivot = new Vector2(0f, 1f);
                return;
        }
    }

    private sealed class RowView
    {
        private readonly RectTransform root;
        private readonly Image separator;
        private readonly Image alertStrip;
        private readonly TextMeshProUGUI nameText;
        private readonly Image healthBackground;
        private readonly Image healthFill;
        private readonly RectTransform healthFillRect;
        private readonly TextMeshProUGUI healthText;
        private readonly TextMeshProUGUI activityText;
        private readonly TextMeshProUGUI alertText;
        private readonly TextMeshProUGUI detailText;

        private RowView(
            RectTransform root,
            Image separator,
            Image alertStrip,
            TextMeshProUGUI nameText,
            Image healthBackground,
            Image healthFill,
            RectTransform healthFillRect,
            TextMeshProUGUI healthText,
            TextMeshProUGUI activityText,
            TextMeshProUGUI alertText,
            TextMeshProUGUI detailText)
        {
            this.root = root;
            this.separator = separator;
            this.alertStrip = alertStrip;
            this.nameText = nameText;
            this.healthBackground = healthBackground;
            this.healthFill = healthFill;
            this.healthFillRect = healthFillRect;
            this.healthText = healthText;
            this.activityText = activityText;
            this.alertText = alertText;
            this.detailText = detailText;
        }

        public bool IsAlive => root != null;

        public static RowView Create(RectTransform parent, TMP_FontAsset? fontAsset)
        {
            var root = AddRect("OperatorRow", parent);
            root.anchorMin = new Vector2(0f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0.5f, 1f);

            var separator = AddImage("Separator", root, Color.white);
            separator.rectTransform.anchorMin = new Vector2(0f, 0f);
            separator.rectTransform.anchorMax = new Vector2(1f, 0f);
            separator.rectTransform.pivot = new Vector2(0.5f, 0f);
            separator.rectTransform.sizeDelta = new Vector2(-10f, 1f);
            separator.rectTransform.anchoredPosition = Vector2.zero;

            var alertStrip = AddImage("AlertStrip", root, Color.clear);
            alertStrip.rectTransform.anchorMin = new Vector2(0f, 0f);
            alertStrip.rectTransform.anchorMax = new Vector2(0f, 1f);
            alertStrip.rectTransform.pivot = new Vector2(0f, 0.5f);
            alertStrip.rectTransform.sizeDelta = new Vector2(3f, 0f);
            alertStrip.rectTransform.anchoredPosition = Vector2.zero;

            var nameText = AddText("Callsign", root, fontAsset, 11.5f, FontStyles.Bold);
            nameText.alignment = TextAlignmentOptions.Left;

            var healthBackground = AddImage("HealthBackground", root, new Color(0f, 0f, 0f, 0.72f));
            var healthFill = AddImage("HealthFill", healthBackground.rectTransform, Color.green);
            var healthFillRect = healthFill.rectTransform;
            healthFillRect.anchorMin = new Vector2(0f, 0.5f);
            healthFillRect.anchorMax = new Vector2(0f, 0.5f);
            healthFillRect.pivot = new Vector2(0f, 0.5f);
            healthFillRect.anchoredPosition = Vector2.zero;
            healthFillRect.sizeDelta = new Vector2(HealthBarWidth, HealthBarHeight);

            var healthText = AddText("HealthPercent", root, fontAsset, 10f, FontStyles.Normal);
            healthText.alignment = TextAlignmentOptions.Right;

            var activityText = AddText("Activity", root, fontAsset, 11f, FontStyles.Bold);
            activityText.alignment = TextAlignmentOptions.Left;

            var alertText = AddText("Alert", root, fontAsset, 10.5f, FontStyles.Bold);
            alertText.alignment = TextAlignmentOptions.Right;

            var detailText = AddText("Detail", root, fontAsset, 8.5f, FontStyles.Normal);
            detailText.alignment = TextAlignmentOptions.Left;

            return new RowView(
                root,
                separator,
                alertStrip,
                nameText,
                healthBackground,
                healthFill,
                healthFillRect,
                healthText,
                activityText,
                alertText,
                detailText);
        }

        public void Update(
            VanguardRaidFixedOperatorHudRow state,
            VanguardRaidFixedOperatorHudSettings settings,
            int index,
            float rowHeight)
        {
            bool detailed = settings.DisplayMode == VanguardRaidFixedOperatorHudDisplayMode.Detailed;
            root.anchoredPosition = new Vector2(0f, -index * rowHeight);
            root.sizeDelta = new Vector2(0f, rowHeight);

            float contentY = detailed ? -10f : -8f;
            bool showAlert = settings.ShowAlerts && !string.IsNullOrWhiteSpace(state.Semantic.AlertLabel);
            float healthBarWidth = detailed ? HealthBarWidth : CompactHealthBarWidth;

            if (detailed)
            {
                Layout(nameText.rectTransform, 14f, contentY, 102f, 17f);
                Layout(healthBackground.rectTransform, 121f, contentY - 1f, healthBarWidth, HealthBarHeight);
                Layout(healthText.rectTransform, 232f, contentY + 1f, 42f, 15f);
                Layout(activityText.rectTransform, 284f, contentY + 1f, 160f, 16f);
                Layout(alertText.rectTransform, 452f, contentY + 1f, 136f, 16f);
                Layout(detailText.rectTransform, 284f, -28f, 304f, 12f);
            }
            else
            {
                // Compact is intentionally dense: the old layout permanently reserved a 96 px
                // alert column even when no alert existed, leaving a large dead area to the right
                // of short activity labels. Alerts now consume space only when they are present.
                const float compactActivityX = 244f;
                const float compactContentRight = 390f;
                const float compactAlertGap = 6f;
                const float compactAlertWidth = 78f;

                Layout(nameText.rectTransform, 10f, contentY, 80f, 17f);
                Layout(healthBackground.rectTransform, 98f, contentY - 1f, healthBarWidth, HealthBarHeight);
                Layout(healthText.rectTransform, 202f, contentY + 1f, 34f, 15f);

                float activityWidth = compactContentRight - compactActivityX;
                if (showAlert)
                {
                    activityWidth -= compactAlertGap + compactAlertWidth;
                    Layout(alertText.rectTransform, compactContentRight - compactAlertWidth, contentY + 1f, compactAlertWidth, 16f);
                }
                else
                {
                    Layout(alertText.rectTransform, compactContentRight, contentY + 1f, 0f, 16f);
                }

                Layout(activityText.rectTransform, compactActivityX, contentY + 1f, activityWidth, 16f);
                Layout(detailText.rectTransform, compactActivityX, -28f, 0f, 12f);
            }

            nameText.text = string.IsNullOrWhiteSpace(state.Nickname) ? "OPERATOR" : state.Nickname;
            nameText.color = settings.Palette.PrimaryText;

            int health = Mathf.Clamp(state.HealthPercent, 0, 100);
            float healthFraction = state.HealthReadable ? health / 100f : 0f;
            healthFillRect.sizeDelta = new Vector2(healthBarWidth * healthFraction, HealthBarHeight);
            healthFill.color = ResolveHealthColor(settings.Palette, health, state.HealthReadable);
            healthBackground.color = WithAlpha(settings.Palette.Border, 0.32f);
            healthText.text = state.HealthReadable
                ? settings.ShowHealthPercentage ? $"{health}%" : string.Empty
                : settings.ShowHealthPercentage ? "--" : string.Empty;
            healthText.color = state.HealthReadable ? settings.Palette.SecondaryText : settings.Palette.Stale;

            activityText.text = state.Semantic.ActivityLabel;
            activityText.color = state.Semantic.Fresh ? settings.Palette.PrimaryText : settings.Palette.Stale;

            alertText.text = showAlert ? state.Semantic.AlertLabel : string.Empty;
            alertText.color = ResolveAlertColor(settings.Palette, state.Semantic.AlertSeverity);
            alertStrip.color = showAlert
                ? WithAlpha(ResolveAlertColor(settings.Palette, state.Semantic.AlertSeverity), 0.90f)
                : Color.clear;

            detailText.gameObject.SetActive(detailed);
            detailText.text = detailed ? state.Semantic.Detail : string.Empty;
            detailText.color = state.Semantic.Fresh ? settings.Palette.SecondaryText : settings.Palette.Stale;
            separator.color = WithAlpha(settings.Palette.Border, 0.36f);
        }

        public void Destroy()
        {
            if (root != null)
            {
                UnityEngine.Object.Destroy(root.gameObject);
            }
        }

        private static Color ResolveHealthColor(
            VanguardRaidFixedOperatorHudPalette palette,
            int healthPercent,
            bool readable)
        {
            if (!readable)
            {
                return palette.Stale;
            }

            if (healthPercent <= 25)
            {
                return palette.HealthLow;
            }

            if (healthPercent <= 60)
            {
                return palette.HealthMedium;
            }

            return palette.HealthGood;
        }

        private static Color ResolveAlertColor(
            VanguardRaidFixedOperatorHudPalette palette,
            VanguardRaidFixedOperatorHudAlertSeverity severity)
        {
            return severity switch
            {
                VanguardRaidFixedOperatorHudAlertSeverity.Critical => palette.Critical,
                VanguardRaidFixedOperatorHudAlertSeverity.Attention => palette.Warning,
                VanguardRaidFixedOperatorHudAlertSeverity.Stale => palette.Stale,
                _ => palette.SecondaryText,
            };
        }

        private static Color WithAlpha(Color color, float alphaMultiplier)
        {
            color.a = Mathf.Clamp01(color.a * alphaMultiplier);
            return color;
        }
    }

    private static RectTransform AddRect(string name, RectTransform parent)
    {
        var objectInstance = new GameObject(name, typeof(RectTransform));
        objectInstance.transform.SetParent(parent, false);
        return objectInstance.GetComponent<RectTransform>();
    }

    private static Image AddImage(string name, RectTransform parent, Color color)
    {
        var objectInstance = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        objectInstance.transform.SetParent(parent, false);
        var image = objectInstance.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static TextMeshProUGUI AddText(
        string name,
        RectTransform parent,
        TMP_FontAsset? fontAsset,
        float fontSize,
        FontStyles fontStyle)
    {
        var objectInstance = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        objectInstance.transform.SetParent(parent, false);
        var text = objectInstance.GetComponent<TextMeshProUGUI>();
        text.font = fontAsset;
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.color = Color.white;
        text.outlineWidth = 0.14f;
        text.outlineColor = new Color(0f, 0f, 0f, 0.92f);
        text.raycastTarget = false;
        return text;
    }

    private static void Layout(
        RectTransform rect,
        float x,
        float y,
        float width,
        float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void Stretch(RectTransform rectTransform, Vector2 minOffset, Vector2 maxOffset)
    {
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = minOffset;
        rectTransform.offsetMax = -maxOffset;
    }
}
#else
namespace Vanguard.Client.Raid.Hud;

internal sealed class VanguardRaidFixedOperatorHudView
{
}
#endif

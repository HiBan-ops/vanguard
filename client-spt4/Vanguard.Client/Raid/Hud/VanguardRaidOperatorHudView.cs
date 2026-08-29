#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Responsibility: Projects one Operator HUD entry into the world-space/floating presentation used beside a live Operator.
// Flow: Resolved semantic state is converted into label/status/alert text and screen placement, then rendered locally with visibility/range rules and cached UI resources.
// Authority boundary: Presentation only: Operator/HUD state is supplied by the resolver and raid authority; this view never changes AI, health, inventory or network state.
// Invariant: Missing or stale display data may suppress the view, but drawing a label can never be treated as evidence that the corresponding gameplay state exists.
namespace Vanguard.Client.Raid.Hud;

internal sealed class VanguardRaidOperatorHudView
{
    private const float HealthBarWidth = 86f;
    private const float HealthBarHeight = 7f;
    private const float MinVisibleBarWidth = 2f;
    private const int MaxMedicalIcons = 10;
    private const float MedicalIconSize = 16.5f;
    private const float MedicalIconSpacing = 2.5f;

    private readonly RectTransform root;
    private readonly Image background;
    private readonly TextMeshProUGUI nameText;
    private readonly Image barBackground;
    private readonly Image barFill;
    private readonly RectTransform barFillRect;
    private readonly RectTransform medicalIconRow;
    private readonly Image[] medicalIconImages;
    private readonly Image[] medicalIconOverlayImages;
    private readonly TextMeshProUGUI[] medicalIconLabels;
    private readonly TextMeshProUGUI statusRowText;
    private string lastMedicalIconBadges = string.Empty;
    private HashSet<string> lastDisplayedMedicalIconBadges = new(StringComparer.OrdinalIgnoreCase);

    private VanguardRaidOperatorHudView(
        RectTransform root,
        Image background,
        TextMeshProUGUI nameText,
        Image barBackground,
        Image barFill,
        RectTransform barFillRect,
        RectTransform medicalIconRow,
        Image[] medicalIconImages,
        Image[] medicalIconOverlayImages,
        TextMeshProUGUI[] medicalIconLabels,
        TextMeshProUGUI statusRowText)
    {
        this.root = root;
        this.background = background;
        this.nameText = nameText;
        this.barBackground = barBackground;
        this.barFill = barFill;
        this.barFillRect = barFillRect;
        this.medicalIconRow = medicalIconRow;
        this.medicalIconImages = medicalIconImages;
        this.medicalIconOverlayImages = medicalIconOverlayImages;
        this.medicalIconLabels = medicalIconLabels;
        this.statusRowText = statusRowText;
    }

    public static VanguardRaidOperatorHudView Create(RectTransform parent, TMP_FontAsset? fontAsset)
    {
        var rootObject = new GameObject("VanguardOperatorMiniHud", typeof(RectTransform));
        rootObject.transform.SetParent(parent, false);

        var root = rootObject.GetComponent<RectTransform>();
        root.sizeDelta = new Vector2(132f, 58f);
        root.anchorMin = new Vector2(0.5f, 0.5f);
        root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0.5f, 0.5f);

        var background = AddImage("BackgroundTransparent", root, new Color(0f, 0f, 0f, 0f));
        Stretch(background.rectTransform, Vector2.zero, Vector2.zero);

        var nameText = AddText("Name", root, fontAsset, 12f, FontStyles.Bold);
        nameText.alignment = TextAlignmentOptions.Center;
        nameText.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        nameText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        nameText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        nameText.rectTransform.anchoredPosition = new Vector2(0f, 12f);
        nameText.rectTransform.sizeDelta = new Vector2(126f, 16f);

        var barBackground = AddImage("HealthBarBackground", root, new Color(0f, 0f, 0f, 0.70f));
        barBackground.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.anchoredPosition = new Vector2(0f, 0f);
        barBackground.rectTransform.sizeDelta = new Vector2(HealthBarWidth, HealthBarHeight);

        var barFill = AddImage("HealthBarFillScalingWidth", barBackground.rectTransform, new Color(0.24f, 0.90f, 0.35f, 1f));
        var barFillRect = barFill.rectTransform;
        barFillRect.anchorMin = new Vector2(0f, 0.5f);
        barFillRect.anchorMax = new Vector2(0f, 0.5f);
        barFillRect.pivot = new Vector2(0f, 0.5f);
        barFillRect.anchoredPosition = new Vector2(0f, 0f);
        barFillRect.sizeDelta = new Vector2(HealthBarWidth, HealthBarHeight);

        var medicalIconRow = AddRect("MedicalIconRowOptimized", root);
        medicalIconRow.anchorMin = new Vector2(0.5f, 0.5f);
        medicalIconRow.anchorMax = new Vector2(0.5f, 0.5f);
        medicalIconRow.pivot = new Vector2(0.5f, 0.5f);
        medicalIconRow.anchoredPosition = new Vector2(0f, -23f);
        medicalIconRow.sizeDelta = new Vector2(220f, 26f);

        var medicalIconImages = new Image[MaxMedicalIcons];
        var medicalIconOverlayImages = new Image[MaxMedicalIcons];
        var medicalIconLabels = new TextMeshProUGUI[MaxMedicalIcons];

        for (int index = 0; index < medicalIconImages.Length; index++)
        {
            var icon = AddImage($"MedicalIconOptimized{index}", medicalIconRow, Color.white);
            icon.preserveAspect = true;
            icon.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            icon.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            icon.rectTransform.sizeDelta = new Vector2(MedicalIconSize, MedicalIconSize);
            icon.gameObject.SetActive(false);
            medicalIconImages[index] = icon;

            var overlay = AddImage($"BodyPartIconOverlay{index}", medicalIconRow, Color.white);
            overlay.preserveAspect = true;
            overlay.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            overlay.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            overlay.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            overlay.rectTransform.sizeDelta = new Vector2(MedicalIconSize, MedicalIconSize);
            overlay.gameObject.SetActive(false);
            medicalIconOverlayImages[index] = overlay;

            var label = AddText($"BodyPartIconLabel{index}", medicalIconRow, fontAsset, 6.5f, FontStyles.Bold);
            label.alignment = TextAlignmentOptions.Center;
            label.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            label.rectTransform.sizeDelta = new Vector2(18f, 8f);
            label.gameObject.SetActive(false);
            medicalIconLabels[index] = label;
        }

        medicalIconRow.gameObject.SetActive(false);

        var statusRowText = AddText("StatusRow", root, fontAsset, 11f, FontStyles.Bold);
        statusRowText.alignment = TextAlignmentOptions.Center;
        statusRowText.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        statusRowText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        statusRowText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        statusRowText.rectTransform.anchoredPosition = new Vector2(0f, -12f);
        statusRowText.rectTransform.sizeDelta = new Vector2(126f, 16f);

        return new VanguardRaidOperatorHudView(root, background, nameText, barBackground, barFill, barFillRect, medicalIconRow, medicalIconImages, medicalIconOverlayImages, medicalIconLabels, statusRowText);
    }

    public void SetActive(bool active)
    {
        if (root.gameObject.activeSelf != active)
        {
            root.gameObject.SetActive(active);
        }
    }

    public void UpdateContent(string nickname, int healthPercent, string statusIcons, string medicalIconBadges, VanguardRaidOperatorHudIcon[]? hudIcons)
    {
        var allIcons = SplitStatusIcons(statusIcons);
        var displayedIconBadges = ResolveDisplayedMedicalIconBadges(medicalIconBadges, hudIcons);
        var textIcons = allIcons.Where(icon => !displayedIconBadges.Contains(icon)).ToArray();

        Color healthColor = ResolveHealthColor(healthPercent);
        float normalizedHealth = Mathf.Clamp01(healthPercent / 100f);
        float visibleBarWidth = normalizedHealth <= 0f ? 0f : Mathf.Max(MinVisibleBarWidth, HealthBarWidth * normalizedHealth);

        nameText.SetText(string.IsNullOrWhiteSpace(nickname) ? "<unknown>" : nickname);

        bool hasMedicalIcons = displayedIconBadges.Count > 0;
        statusRowText.rectTransform.anchoredPosition = hasMedicalIcons
            ? new Vector2(0f, -24f)
            : new Vector2(0f, -12f);

        string statusRow = string.Join("  ", textIcons);
        statusRowText.SetText(statusRow);
        statusRowText.gameObject.SetActive(!string.IsNullOrWhiteSpace(statusRow));

        barFill.color = healthColor;
        barFillRect.sizeDelta = new Vector2(visibleBarWidth, HealthBarHeight);
        barBackground.gameObject.SetActive(true);
        barFill.gameObject.SetActive(visibleBarWidth > 0f);

        background.color = new Color(0f, 0f, 0f, 0f);
        background.gameObject.SetActive(false);
    }

    public void UpdatePosition(Vector2 canvasPosition, float scale)
    {
        root.anchoredPosition = canvasPosition;
        root.localScale = new Vector3(scale, scale, 1f);
    }

    public void Destroy()
    {
        if (root is not null)
        {
            UnityEngine.Object.Destroy(root.gameObject);
        }
    }

    private HashSet<string> ResolveDisplayedMedicalIconBadges(string medicalIconBadges, VanguardRaidOperatorHudIcon[]? hudIcons)
    {
        string normalizedBadges = string.IsNullOrWhiteSpace(medicalIconBadges)
            ? string.Empty
            : string.Join(" ", SplitStatusIcons(medicalIconBadges));
        string iconSignature = hudIcons is null || hudIcons.Length == 0
            ? "<none>"
            : string.Join("|", hudIcons.Select(icon => $"{icon.Badge}:{icon.BaseSprite?.name ?? "<null>"}:{icon.OverlaySprite?.name ?? "<null>"}:{icon.ShowLabel}"));
        string displaySignature = normalizedBadges + "|" + iconSignature;

        if (string.Equals(lastMedicalIconBadges, displaySignature, StringComparison.Ordinal))
        {
            return lastDisplayedMedicalIconBadges;
        }

        lastMedicalIconBadges = displaySignature;
        lastDisplayedMedicalIconBadges = UpdateMedicalIcons(SplitStatusIcons(normalizedBadges), hudIcons);
        return lastDisplayedMedicalIconBadges;
    }

    private HashSet<string> UpdateMedicalIcons(string[] iconBadges, VanguardRaidOperatorHudIcon[]? hudIcons)
    {
        var displayed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < medicalIconImages.Length; index++)
        {
            medicalIconImages[index].sprite = null;
            medicalIconImages[index].gameObject.SetActive(false);
            medicalIconOverlayImages[index].sprite = null;
            medicalIconOverlayImages[index].gameObject.SetActive(false);
            medicalIconLabels[index].SetText(string.Empty);
            medicalIconLabels[index].gameObject.SetActive(false);
        }

        if (hudIcons is null || hudIcons.Length == 0 || iconBadges.Length == 0)
        {
            medicalIconRow.gameObject.SetActive(false);
            return displayed;
        }

        var visibleSlotIndexes = new List<int>();
        int count = Math.Min(Math.Min(iconBadges.Length, hudIcons.Length), medicalIconImages.Length);
        for (int index = 0; index < count; index++)
        {
            string badge = iconBadges[index];
            var iconData = hudIcons[index];
            if (string.IsNullOrWhiteSpace(badge) || iconData.BaseSprite is null && iconData.OverlaySprite is null)
            {
                continue;
            }

            int slotIndex = visibleSlotIndexes.Count;
            var baseImage = medicalIconImages[slotIndex];
            var overlayImage = medicalIconOverlayImages[slotIndex];
            var label = medicalIconLabels[slotIndex];

            if (iconData.BaseSprite is not null)
            {
                baseImage.sprite = iconData.BaseSprite;
                baseImage.color = Color.white;
                baseImage.preserveAspect = true;
                baseImage.gameObject.SetActive(true);
            }

            if (iconData.OverlaySprite is not null)
            {
                overlayImage.sprite = iconData.OverlaySprite;
                overlayImage.color = Color.white;
                overlayImage.preserveAspect = true;
                overlayImage.gameObject.SetActive(true);
            }

            if (iconData.ShowLabel)
            {
                label.SetText(badge);
                label.gameObject.SetActive(true);
            }

            visibleSlotIndexes.Add(slotIndex);
            displayed.Add(badge);
            if (visibleSlotIndexes.Count >= medicalIconImages.Length)
            {
                break;
            }
        }

        for (int visibleIndex = 0; visibleIndex < visibleSlotIndexes.Count; visibleIndex++)
        {
            int slotIndex = visibleSlotIndexes[visibleIndex];
            var iconData = hudIcons[visibleIndex];
            float x = (visibleIndex - ((visibleSlotIndexes.Count - 1) * 0.5f)) * (MedicalIconSize + MedicalIconSpacing);
            float iconY = iconData.ShowLabel ? 2.5f : 0f;
            medicalIconImages[slotIndex].rectTransform.anchoredPosition = new Vector2(x, iconY);
            medicalIconOverlayImages[slotIndex].rectTransform.anchoredPosition = new Vector2(x, iconY);
            medicalIconLabels[slotIndex].rectTransform.anchoredPosition = new Vector2(x, -7f);
        }

        medicalIconRow.gameObject.SetActive(visibleSlotIndexes.Count > 0);
        return displayed;
    }

    private static string[] SplitStatusIcons(string statusIcons)
    {
        return string.IsNullOrWhiteSpace(statusIcons)
            ? Array.Empty<string>()
            : statusIcons.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(14)
                .ToArray();
    }

    private static RectTransform AddRect(string name, RectTransform parent)
    {
        var rectObject = new GameObject(name, typeof(RectTransform));
        rectObject.transform.SetParent(parent, false);
        return rectObject.GetComponent<RectTransform>();
    }

    private static Image AddImage(string name, RectTransform parent, Color color)
    {
        var imageObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        imageObject.transform.SetParent(parent, false);
        var image = imageObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static TextMeshProUGUI AddText(string name, RectTransform parent, TMP_FontAsset? fontAsset, float fontSize, FontStyles fontStyle)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);
        var text = textObject.GetComponent<TextMeshProUGUI>();
        text.font = fontAsset;
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.color = Color.white;
        text.outlineWidth = 0.18f;
        text.outlineColor = new Color(0f, 0f, 0f, 0.95f);
        text.raycastTarget = false;
        return text;
    }

    private static void Stretch(RectTransform rectTransform, Vector2 minOffset, Vector2 maxOffset)
    {
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = minOffset;
        rectTransform.offsetMax = -maxOffset;
    }

    private static Color ResolveHealthColor(int healthPercent)
    {
        if (healthPercent <= 25)
        {
            return new Color(0.90f, 0.22f, 0.22f, 1f);
        }

        if (healthPercent <= 50)
        {
            return new Color(0.98f, 0.55f, 0.14f, 1f);
        }

        if (healthPercent <= 75)
        {
            return new Color(0.96f, 0.84f, 0.18f, 1f);
        }

        return new Color(0.24f, 0.90f, 0.35f, 1f);
    }
}
#else
namespace Vanguard.Client.Raid.Hud;

internal sealed class VanguardRaidOperatorHudView
{
}
#endif

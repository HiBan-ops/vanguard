using System;

#if SPT_CLIENT
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
#endif

// Responsibility: Defines the user/configuration surface for Raid Fixed Operator Hud Options in the raid Operator HUD.
// Flow: BepInEx/F12 values are bound, normalized and exposed through getters/snapshots; raid-scoped settings are synchronized to the process that owns runtime execution.
// Authority boundary: Configuration supplies policy inputs only; changing a value does not itself perform gameplay or persistence mutation.
// Invariant: Defaults preserve the established public behavior and synchronized values remain bounded to their declared scope.
namespace Vanguard.Client.Raid.Hud;

internal enum VanguardRaidFixedOperatorHudAnchor
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
    CenterLeft = 4,
    CenterRight = 5,
}

internal enum VanguardRaidFixedOperatorHudTheme
{
    Vanguard = 0,
    Monochrome = 1,
    HighContrast = 2,
    Olive = 3,
    ColdGunmetal = 4,
    Custom = 5,
}

internal enum VanguardRaidFixedOperatorHudDisplayMode
{
    Compact = 0,
    Detailed = 1,
}

#if SPT_CLIENT
internal sealed record VanguardRaidFixedOperatorHudPalette(
    Color Background,
    Color Border,
    Color PrimaryText,
    Color SecondaryText,
    Color HealthGood,
    Color HealthMedium,
    Color HealthLow,
    Color Warning,
    Color Critical,
    Color Stale);

internal sealed record VanguardRaidFixedOperatorHudSettings(
    bool Enabled,
    VanguardRaidFixedOperatorHudAnchor Anchor,
    float OffsetX,
    float OffsetY,
    float Scale,
    float Opacity,
    VanguardRaidFixedOperatorHudTheme Theme,
    VanguardRaidFixedOperatorHudDisplayMode DisplayMode,
    bool ShowHealthPercentage,
    bool ShowAlerts,
    VanguardRaidFixedOperatorHudPalette Palette)
{
    public string LayoutSignature => string.Join("|",
        Enabled,
        Anchor,
        OffsetX.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
        OffsetY.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
        Scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        Opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        Theme,
        DisplayMode,
        ShowHealthPercentage,
        ShowAlerts,
        ColorSignature(Palette.Background),
        ColorSignature(Palette.Border),
        ColorSignature(Palette.PrimaryText),
        ColorSignature(Palette.HealthGood),
        ColorSignature(Palette.Warning),
        ColorSignature(Palette.Critical));

    private static string ColorSignature(Color color) => string.Join(",",
        color.r.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
        color.g.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
        color.b.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
        color.a.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// Client-local HUD presentation configuration. These ConfigEntries are intentionally
/// never projected into Vanguard runtime settings and never synchronized to host/headless.
/// </summary>
internal static class VanguardRaidFixedOperatorHudOptions
{
    private const string Section = "Vanguard - HUD - Fixed Operator Decision";

    private static ConfigEntry<bool>? enabled;
    private static ConfigEntry<VanguardRaidFixedOperatorHudAnchor>? anchor;
    private static ConfigEntry<float>? offsetX;
    private static ConfigEntry<float>? offsetY;
    private static ConfigEntry<float>? scale;
    private static ConfigEntry<float>? opacity;
    private static ConfigEntry<VanguardRaidFixedOperatorHudTheme>? theme;
    private static ConfigEntry<VanguardRaidFixedOperatorHudDisplayMode>? displayMode;
    private static ConfigEntry<bool>? showHealthPercentage;
    private static ConfigEntry<bool>? showAlerts;
    private static ConfigEntry<string>? customBackground;
    private static ConfigEntry<string>? customBorder;
    private static ConfigEntry<string>? customPrimaryText;
    private static ConfigEntry<string>? customHealth;
    private static ConfigEntry<string>? customWarning;
    private static ConfigEntry<string>? customCritical;
    private static bool isBound;

    public static bool BindFromOwner(MonoBehaviour owner)
    {
        if (isBound)
        {
            return true;
        }

        if (owner is not BaseUnityPlugin plugin)
        {
            return false;
        }

        const string localOnly = "LOCAL CLIENT ONLY. This setting changes presentation on this client and is never committed or synchronized to host/headless.";
        enabled = plugin.Config.Bind(Section, "Enable fixed Operator decision HUD", true, localOnly);
        anchor = plugin.Config.Bind(Section, "Anchor", VanguardRaidFixedOperatorHudAnchor.TopLeft, localOnly + " Position X/Y are offsets relative to this anchor.");
        offsetX = plugin.Config.Bind(Section, "Position X", 28f, new ConfigDescription(localOnly, new AcceptableValueRange<float>(-1920f, 1920f)));
        offsetY = plugin.Config.Bind(Section, "Position Y", -170f, new ConfigDescription(localOnly, new AcceptableValueRange<float>(-1080f, 1080f)));
        scale = plugin.Config.Bind(Section, "Scale", 1.00f, new ConfigDescription(localOnly, new AcceptableValueRange<float>(0.70f, 1.50f)));
        opacity = plugin.Config.Bind(Section, "Opacity", 0.82f, new ConfigDescription(localOnly, new AcceptableValueRange<float>(0.20f, 1.00f)));
        theme = plugin.Config.Bind(Section, "Color theme", VanguardRaidFixedOperatorHudTheme.Vanguard, localOnly);
        displayMode = plugin.Config.Bind(Section, "Display mode", VanguardRaidFixedOperatorHudDisplayMode.Compact, localOnly + " Detailed adds a small authoritative decision-source line.");
        showHealthPercentage = plugin.Config.Bind(Section, "Show health percentage", true, localOnly);
        showAlerts = plugin.Config.Bind(Section, "Show alerts", true, localOnly + " Activity remains visible even when alerts are hidden.");

        customBackground = plugin.Config.Bind(Section, "Custom - Background", "#111511", localOnly + " Used only when Color theme=Custom. HTML hex #RRGGBB.");
        customBorder = plugin.Config.Bind(Section, "Custom - Border", "#687365", localOnly + " Used only when Color theme=Custom. HTML hex #RRGGBB.");
        customPrimaryText = plugin.Config.Bind(Section, "Custom - Primary text", "#E0E4DC", localOnly + " Used only when Color theme=Custom. HTML hex #RRGGBB.");
        customHealth = plugin.Config.Bind(Section, "Custom - Health", "#7FA66F", localOnly + " Used only when Color theme=Custom. HTML hex #RRGGBB.");
        customWarning = plugin.Config.Bind(Section, "Custom - Warning", "#C08A46", localOnly + " Used only when Color theme=Custom. HTML hex #RRGGBB.");
        customCritical = plugin.Config.Bind(Section, "Custom - Critical", "#B85850", localOnly + " Used only when Color theme=Custom. HTML hex #RRGGBB.");

        isBound = true;
        return true;
    }

    public static VanguardRaidFixedOperatorHudSettings Capture()
    {
        float resolvedOpacity = Mathf.Clamp(opacity?.Value ?? 0.82f, 0.20f, 1.00f);
        var resolvedTheme = theme?.Value ?? VanguardRaidFixedOperatorHudTheme.Vanguard;
        return new VanguardRaidFixedOperatorHudSettings(
            enabled?.Value ?? true,
            anchor?.Value ?? VanguardRaidFixedOperatorHudAnchor.TopLeft,
            offsetX?.Value ?? 28f,
            offsetY?.Value ?? -170f,
            Mathf.Clamp(scale?.Value ?? 1.00f, 0.70f, 1.50f),
            resolvedOpacity,
            resolvedTheme,
            displayMode?.Value ?? VanguardRaidFixedOperatorHudDisplayMode.Compact,
            showHealthPercentage?.Value ?? true,
            showAlerts?.Value ?? true,
            ResolvePalette(resolvedTheme, resolvedOpacity));
    }

    private static VanguardRaidFixedOperatorHudPalette ResolvePalette(VanguardRaidFixedOperatorHudTheme selectedTheme, float globalOpacity)
    {
        Color background;
        Color border;
        Color primary;
        Color secondary;
        Color health;
        Color medium;
        Color low;
        Color warning;
        Color critical;
        Color stale;

        switch (selectedTheme)
        {
            case VanguardRaidFixedOperatorHudTheme.Monochrome:
                background = Hex("#101010", new Color(0.06f, 0.06f, 0.06f));
                border = Hex("#8A8A8A", Color.gray);
                primary = Hex("#E5E5E5", Color.white);
                secondary = Hex("#B0B0B0", Color.gray);
                health = Hex("#D0D0D0", Color.white);
                medium = Hex("#A8A8A8", Color.gray);
                low = Hex("#858585", Color.gray);
                warning = Hex("#D0D0D0", Color.white);
                critical = Hex("#FFFFFF", Color.white);
                stale = Hex("#777777", Color.gray);
                break;
            case VanguardRaidFixedOperatorHudTheme.HighContrast:
                background = Color.black;
                border = Color.white;
                primary = Color.white;
                secondary = Hex("#D0D0D0", Color.white);
                health = Hex("#7DFF7D", Color.green);
                medium = Hex("#FFE25E", Color.yellow);
                low = Hex("#FF6666", Color.red);
                warning = Hex("#FFD35A", Color.yellow);
                critical = Hex("#FF5A5A", Color.red);
                stale = Hex("#B8B8B8", Color.gray);
                break;
            case VanguardRaidFixedOperatorHudTheme.Olive:
                background = Hex("#15170F", new Color(0.08f, 0.09f, 0.06f));
                border = Hex("#737B4D", new Color(0.45f, 0.48f, 0.30f));
                primary = Hex("#E0DEC4", Color.white);
                secondary = Hex("#A8A783", Color.gray);
                health = Hex("#879B5A", Color.green);
                medium = Hex("#B49D54", Color.yellow);
                low = Hex("#A85C48", Color.red);
                warning = Hex("#C79C50", Color.yellow);
                critical = Hex("#B9554D", Color.red);
                stale = Hex("#777A69", Color.gray);
                break;
            case VanguardRaidFixedOperatorHudTheme.ColdGunmetal:
                background = Hex("#11171A", new Color(0.07f, 0.09f, 0.10f));
                border = Hex("#61727A", Color.gray);
                primary = Hex("#D8E0E3", Color.white);
                secondary = Hex("#91A2A8", Color.gray);
                health = Hex("#6F9A84", Color.green);
                medium = Hex("#B09356", Color.yellow);
                low = Hex("#A85550", Color.red);
                warning = Hex("#C08C4B", Color.yellow);
                critical = Hex("#B95652", Color.red);
                stale = Hex("#66757A", Color.gray);
                break;
            case VanguardRaidFixedOperatorHudTheme.Custom:
                background = Hex(customBackground?.Value, Hex("#111511", Color.black));
                border = Hex(customBorder?.Value, Hex("#687365", Color.gray));
                primary = Hex(customPrimaryText?.Value, Color.white);
                secondary = Color.Lerp(primary, background, 0.34f);
                health = Hex(customHealth?.Value, Color.green);
                medium = Color.Lerp(health, Hex("#D1A04E", Color.yellow), 0.55f);
                low = Color.Lerp(health, Hex("#B85850", Color.red), 0.75f);
                warning = Hex(customWarning?.Value, Color.yellow);
                critical = Hex(customCritical?.Value, Color.red);
                stale = Color.Lerp(primary, background, 0.55f);
                break;
            default:
                background = Hex("#111511", new Color(0.07f, 0.08f, 0.07f));
                border = Hex("#687365", Color.gray);
                primary = Hex("#E0E4DC", Color.white);
                secondary = Hex("#9BA39A", Color.gray);
                health = Hex("#7FA66F", Color.green);
                medium = Hex("#C0A052", Color.yellow);
                low = Hex("#A85848", Color.red);
                warning = Hex("#C08A46", Color.yellow);
                critical = Hex("#B85850", Color.red);
                stale = Hex("#707970", Color.gray);
                break;
        }

        return new VanguardRaidFixedOperatorHudPalette(
            Alpha(background, 0.82f * globalOpacity),
            Alpha(border, 0.78f * globalOpacity),
            Alpha(primary, globalOpacity),
            Alpha(secondary, 0.92f * globalOpacity),
            Alpha(health, globalOpacity),
            Alpha(medium, globalOpacity),
            Alpha(low, globalOpacity),
            Alpha(warning, globalOpacity),
            Alpha(critical, globalOpacity),
            Alpha(stale, 0.88f * globalOpacity));
    }

    private static Color Hex(string? value, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && ColorUtility.TryParseHtmlString(value.Trim(), out Color parsed))
        {
            parsed.a = 1f;
            return parsed;
        }

        return fallback;
    }

    private static Color Alpha(Color color, float alpha)
    {
        color.a = Mathf.Clamp01(alpha);
        return color;
    }
}
#else
internal static class VanguardRaidFixedOperatorHudOptions
{
}
#endif

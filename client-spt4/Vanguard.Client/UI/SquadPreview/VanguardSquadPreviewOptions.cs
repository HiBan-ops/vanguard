using System;

#if SPT_CLIENT
using BepInEx.Configuration;
#endif

// Responsibility: exposes local-only Squad Preview presentation tuning through BepInEx/F12.
// Flow: user changes are normalized and published immediately to the currently-owned Vanilla preview shell.
// Authority boundary: presentation policy only; no Operator, raid, equipment, selection or persistent state is mutated.
// Invariant: lighting tuning affects Vanguard-owned preview lights only and may never alter native EFT menu/weapon lighting.
namespace Vanguard.Client.UI.SquadPreview;

internal static class VanguardSquadPreviewOptions
{
#if SPT_CLIENT
    private const string Section = "Vanguard - Squad Preview";
    private const int DefaultLightingPercent = 70;
    private const int MinimumLightingPercent = 20;
    private const int MaximumLightingPercent = 140;

    private static ConfigEntry<int>? lightingIntensityPercent;
    private static bool bound;

    public static event Action? PresentationChanged;

    public static int LightingIntensityPercent => Math.Clamp(
        lightingIntensityPercent?.Value ?? DefaultLightingPercent,
        MinimumLightingPercent,
        MaximumLightingPercent);

    public static float LightingMultiplier => LightingIntensityPercent / 100f;

    public static void Bind(ConfigFile config)
    {
        if (bound)
        {
            return;
        }

        lightingIntensityPercent = config.Bind(
            Section,
            "Lighting intensity (%)",
            DefaultLightingPercent,
            new ConfigDescription(
                "Local visual setting for the Vanguard Operator Squad Preview. Changes apply live to Vanguard-owned preview lights only and never change EFT gameplay or persistent state.",
                new AcceptableValueRange<int>(MinimumLightingPercent, MaximumLightingPercent)));

        lightingIntensityPercent.SettingChanged += (_, _) => PresentationChanged?.Invoke();
        bound = true;
    }
#else
    public static int LightingIntensityPercent => 70;
    public static float LightingMultiplier => 0.70f;
    public static void Bind(object config) { }
#endif
}

using System;

#if SPT_CLIENT
using BepInEx.Configuration;
using Vanguard.Client.Compatibility;
#endif

// Responsibility: exposes exactly two F12 controls as a live editor for the currently qualified
// MainMenuPlacementProfiles.jsonc block.
// Flow: profile resolution loads its persisted X/Y into the two ConfigEntries; user changes apply live
// and are written back only to that active profile.
// Authority boundary: these ConfigEntries are an editor surface, not the persistence authority. The
// JSONC profile remains authoritative across topology changes/restarts. No-profile standalone mode ignores them.
// Invariant: the F12 editor may update only the active external placement profile; standalone Vanguard menu geometry remains governed by the historical built-in layout.
namespace Vanguard.Client.Options;

internal static class VanguardMainMenuPlacementOptions
{
#if SPT_CLIENT
    private const string Section = "Vanguard - Main Menu Button";
    private static ConfigEntry<float>? positionX;
    private static ConfigEntry<float>? positionY;
    private static bool suppressSettingChanged;
    private static string activeProfileId = string.Empty;
    private static float activeProfileSourceX = float.NaN;
    private static float activeProfileSourceY = float.NaN;

    public static void Bind(ConfigFile config)
    {
        const string explanation = "Local UI setting. Applied only when MainMenuPlacementProfiles.jsonc resolves an active GUID profile. "
            + "The standalone Vanguard menu keeps its built-in two-column layout. Changes are saved back to the active JSONC profile.";

        positionX = config.Bind(
            Section,
            "Vanguard Menu X Position (%)",
            50f,
            new ConfigDescription(
                explanation + " 0 = left edge, 100 = right edge of the stable Main Menu reference rect.",
                new AcceptableValueRange<float>(0f, 100f)));
        positionY = config.Bind(
            Section,
            "Vanguard Menu Y Position (%)",
            50f,
            new ConfigDescription(
                explanation + " 0 = bottom edge, 100 = top edge of the stable Main Menu reference rect.",
                new AcceptableValueRange<float>(0f, 100f)));

        positionX.SettingChanged += OnPlacementSettingChanged;
        positionY.SettingChanged += OnPlacementSettingChanged;
        VanguardMainMenuPlacementProfiles.Initialize();
        SynchronizeFromResolvedProfile(force: true);
    }

    public static bool TryGetActivePlacement(
        out string profileId,
        out float xPercent,
        out float yPercent)
    {
        if (!SynchronizeFromResolvedProfile(force: false)
            || positionX == null
            || positionY == null)
        {
            profileId = string.Empty;
            xPercent = 0f;
            yPercent = 0f;
            return false;
        }

        profileId = activeProfileId;
        xPercent = Math.Clamp(positionX.Value, 0f, 100f);
        yPercent = Math.Clamp(positionY.Value, 0f, 100f);
        return true;
    }

    public static bool HasAnyConfiguredPluginLoaded()
    {
        return VanguardMainMenuPlacementProfiles.HasAnyConfiguredPluginLoaded();
    }

    private static bool SynchronizeFromResolvedProfile(bool force)
    {
        if (!VanguardMainMenuPlacementProfiles.TryResolveActive(out VanguardMainMenuPlacementProfile profile))
        {
            activeProfileId = string.Empty;
            activeProfileSourceX = float.NaN;
            activeProfileSourceY = float.NaN;
            return false;
        }

        bool profileChanged = !string.Equals(activeProfileId, profile.Id, StringComparison.OrdinalIgnoreCase);
        bool sourceChanged = !NearlyEqual(activeProfileSourceX, profile.XPercent)
            || !NearlyEqual(activeProfileSourceY, profile.YPercent);
        if (force || profileChanged || sourceChanged)
        {
            activeProfileId = profile.Id;
            activeProfileSourceX = profile.XPercent;
            activeProfileSourceY = profile.YPercent;
            suppressSettingChanged = true;
            try
            {
                if (positionX != null)
                {
                    positionX.Value = Math.Clamp(profile.XPercent, 0f, 100f);
                }

                if (positionY != null)
                {
                    positionY.Value = Math.Clamp(profile.YPercent, 0f, 100f);
                }
            }
            finally
            {
                suppressSettingChanged = false;
            }
        }

        return true;
    }

    private static void OnPlacementSettingChanged(object? sender, EventArgs eventArgs)
    {
        if (suppressSettingChanged || positionX == null || positionY == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(activeProfileId)
            && !SynchronizeFromResolvedProfile(force: false))
        {
            // Standalone mode intentionally ignores these editor values and retains the historical
            // Vanguard-owned two-column menu geometry.
            return;
        }

        float x = Math.Clamp(positionX.Value, 0f, 100f);
        float y = Math.Clamp(positionY.Value, 0f, 100f);
        if (VanguardMainMenuPlacementProfiles.TrySaveCoordinates(activeProfileId, x, y))
        {
            activeProfileSourceX = x;
            activeProfileSourceY = y;
        }
    }

    private static bool NearlyEqual(float left, float right)
    {
        if (float.IsNaN(left) || float.IsNaN(right))
        {
            return false;
        }

        return Math.Abs(left - right) <= 0.0001f;
    }
#else
    public static void Bind(object config) { }
    public static bool TryGetActivePlacement(out string profileId, out float xPercent, out float yPercent)
    {
        profileId = string.Empty;
        xPercent = 0f;
        yPercent = 0f;
        return false;
    }
    public static bool HasAnyConfiguredPluginLoaded() => false;
#endif
}

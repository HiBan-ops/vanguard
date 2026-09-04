using System;
using System.Collections.Generic;

#if SPT_CLIENT
using BepInEx.Bootstrap;
using Vanguard.Client.Diagnostics;
#endif

// Responsibility: declares the small set of third-party main-menu capabilities that Vanguard must
// cooperate with beyond button positioning (visual-style ownership and visibility/rebuild lifecycle).
// Flow: the Off-Raid controller asks only for generic capabilities; BepInEx GUIDs remain isolated here.
// Authority boundary: main-menu POSITION is intentionally not resolved here. Placement is delegated to
// the external MainMenuPlacementProfiles.jsonc engine, while this registry retains only behavior that
// genuinely requires a known interoperability capability.
// Invariant: no per-mod branch or third-party identity may leak into the generic Off-Raid UI controller.
namespace Vanguard.Client.Compatibility;

[Flags]
internal enum VanguardMainMenuInteropCapability
{
    None = 0,
    ExternalVisualStyleAuthority = 1 << 0,
    ExternalVisibilityLifecycle = 1 << 1
}

internal static class VanguardMainMenuInterop
{
    // Retains the already runtime-validated optional load-order hint for Menu Overhaul. Placement itself
    // is profile-driven and does not depend on this constant.
    public const string PreferredLoadOrderPluginGuid = "com.moxopixel.menuoverhaul";

    private readonly struct Registration
    {
        public Registration(
            string pluginGuid,
            string displayName,
            VanguardMainMenuInteropCapability capabilities)
        {
            PluginGuid = pluginGuid;
            DisplayName = displayName;
            Capabilities = capabilities;
        }

        public string PluginGuid { get; }
        public string DisplayName { get; }
        public VanguardMainMenuInteropCapability Capabilities { get; }
    }

    // Third-party identities that still own a non-placement capability stay declarative and centralized.
    private static readonly Registration[] Registrations =
    {
        new(
            PreferredLoadOrderPluginGuid,
            "Menu Overhaul",
            VanguardMainMenuInteropCapability.ExternalVisualStyleAuthority),
        new(
            "com.softwyx.careerlog",
            "Career Log",
            VanguardMainMenuInteropCapability.ExternalVisibilityLifecycle)
    };

#if SPT_CLIENT
    private static bool diagnosticWritten;

    public static bool Has(VanguardMainMenuInteropCapability capability)
    {
        return (ResolveCapabilities(out _) & capability) == capability;
    }

    public static string DescribeActiveOwners(VanguardMainMenuInteropCapability capability)
    {
        ResolveCapabilities(out string owners, capability);
        return owners;
    }

    private static List<Registration> ResolveActiveRegistrations()
    {
        var active = new List<Registration>();
        foreach (Registration registration in Registrations)
        {
            if (Chainloader.PluginInfos.ContainsKey(registration.PluginGuid))
            {
                active.Add(registration);
            }
        }

        return active;
    }

    private static VanguardMainMenuInteropCapability ResolveCapabilities(
        out string owners,
        VanguardMainMenuInteropCapability ownerFilter = VanguardMainMenuInteropCapability.None)
    {
        try
        {
            VanguardMainMenuInteropCapability capabilities = VanguardMainMenuInteropCapability.None;
            var activeOwners = new List<string>();
            var filteredOwners = new List<string>();
            foreach (Registration registration in ResolveActiveRegistrations())
            {
                capabilities |= registration.Capabilities;
                activeOwners.Add(registration.DisplayName);
                if (ownerFilter == VanguardMainMenuInteropCapability.None
                    || (registration.Capabilities & ownerFilter) == ownerFilter)
                {
                    filteredOwners.Add(registration.DisplayName);
                }
            }

            string allOwners = activeOwners.Count == 0 ? "none" : string.Join(",", activeOwners);
            owners = filteredOwners.Count == 0 ? "none" : string.Join(",", filteredOwners);
            WriteDiagnosticOnce(capabilities, allOwners, "bepinex_registry_resolved");
            return capabilities;
        }
        catch (Exception exception)
        {
            owners = "none";
            WriteDiagnosticOnce(
                VanguardMainMenuInteropCapability.None,
                owners,
                $"detection_failed_{exception.GetType().Name}");
            return VanguardMainMenuInteropCapability.None;
        }
    }

    private static void WriteDiagnosticOnce(
        VanguardMainMenuInteropCapability capabilities,
        string owners,
        string reason)
    {
        if (diagnosticWritten)
        {
            return;
        }

        diagnosticWritten = true;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OffRaidUiStatusTag,
            $"Main-menu interop capabilities; owners={owners}; capabilities={capabilities}; reason={reason}; visualStyleAuthority={((capabilities & VanguardMainMenuInteropCapability.ExternalVisualStyleAuthority) != 0 ? "external" : "vanguard")}; externalVisibilityLifecycle={((capabilities & VanguardMainMenuInteropCapability.ExternalVisibilityLifecycle) != 0)}; placementAuthority=MainMenuPlacementProfiles.jsonc");
    }
#else
    public static bool Has(VanguardMainMenuInteropCapability capability) => false;
    public static string DescribeActiveOwners(VanguardMainMenuInteropCapability capability) => "none";
#endif
}

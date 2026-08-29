#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

// Responsibility: Defines data/state contracts used by the loot runtime, centered on World Loot Container Read Model.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// Canonical world-container discovery. Discovery comes from EFT GameWorld.LootList,
/// not Physics overlap scans and not Object.FindObjectsOfType. The provider is central, cached and
/// read-only so the squad does not rescan the world independently per Operator.
/// </summary>
internal static class VanguardWorldLootContainerSnapshotProvider
{
    private static readonly object Sync = new();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1.0d);
    private static readonly IReadOnlyList<VanguardWorldLootContainerSnapshot> Empty = Array.Empty<VanguardWorldLootContainerSnapshot>();

    private static IReadOnlyList<VanguardWorldLootContainerSnapshot> cached = Empty;
    private static DateTimeOffset nextRefreshAtUtc = DateTimeOffset.MinValue;
    private static string lastSignature = "none";

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            cached = Empty;
            nextRefreshAtUtc = DateTimeOffset.MinValue;
            lastSignature = "none";
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.WorldContainerReadModelStatusTag,
            $"VANGUARD_WORLD_CONTAINER_PROVIDER_RESET source={Safe(reason)}; mutation=false");
    }

    public static IReadOnlyList<VanguardWorldLootContainerSnapshot> GetSnapshot(DateTimeOffset now)
    {
        lock (Sync)
        {
            if (now < nextRefreshAtUtc)
            {
                return cached;
            }
        }

        IReadOnlyList<VanguardWorldLootContainerSnapshot> next = BuildSnapshot(out var diagnostics);
        string signature = BuildSignature(next, diagnostics);
        bool changed;

        lock (Sync)
        {
            cached = next;
            nextRefreshAtUtc = now + RefreshInterval;
            changed = !string.Equals(lastSignature, signature, StringComparison.Ordinal);
            lastSignature = signature;
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Operational(
                VanguardBuildVersion.WorldContainerReadModelStatusTag,
                () => $"VANGUARD_WORLD_CONTAINER_SNAPSHOT source=eft_gameworld_lootlist; discovered={diagnostics.Discovered}; eligible={next.Count}; inactive={diagnostics.Inactive}; locked={diagnostics.Locked}; rootMissing={diagnostics.RootMissing}; invalidId={diagnostics.InvalidId}; exceptions={diagnostics.Exceptions}; centralCached=true; perOperatorPhysicsScan=false; sceneObjectScrape=false; manifestTraversal=eager_false; opening=false; claims=false; movement=false; transactions=false");
        }

        return next;
    }

    private static IReadOnlyList<VanguardWorldLootContainerSnapshot> BuildSnapshot(out SnapshotDiagnostics diagnostics)
    {
        diagnostics = default;
        GameWorld? world;
        try
        {
            world = Singleton<GameWorld>.Instance;
        }
        catch
        {
            diagnostics.Exceptions++;
            return Empty;
        }

        if (world?.LootList == null)
        {
            return Empty;
        }

        LootableContainer[] containers;
        try
        {
            containers = world.LootList.OfType<LootableContainer>().ToArray();
        }
        catch
        {
            diagnostics.Exceptions++;
            return Empty;
        }

        diagnostics.Discovered = containers.Length;
        var result = new List<VanguardWorldLootContainerSnapshot>(containers.Length);
        foreach (LootableContainer container in containers)
        {
            try
            {
                if (container == null || !container.isActiveAndEnabled)
                {
                    diagnostics.Inactive++;
                    continue;
                }

                if (container.DoorState == EDoorState.Locked)
                {
                    diagnostics.Locked++;
                    continue;
                }

                Item? rootItem = container.ItemOwner?.RootItem;
                if (rootItem == null)
                {
                    diagnostics.RootMissing++;
                    continue;
                }

                string targetId = Normalize(container.Id);
                if (targetId == "none")
                {
                    targetId = Normalize(rootItem.Id);
                }

                if (targetId == "none")
                {
                    diagnostics.InvalidId++;
                    continue;
                }

                var target = new VanguardLootTargetSnapshot
                {
                    Kind = VanguardLootTargetKind.WorldContainer,
                    TargetId = targetId,
                    Position = container.transform.position,
                    RootItem = rootItem,
                    Source = "eft_gameworld_lootlist",
                    RequiresOpenInteraction = container.DoorState != EDoorState.Open,
                    IsOpen = container.DoorState == EDoorState.Open,
                    IsLocked = false
                };

                result.Add(new VanguardWorldLootContainerSnapshot
                {
                    Container = container,
                    Target = target,
                    DoorState = container.DoorState
                });
            }
            catch
            {
                diagnostics.Exceptions++;
            }
        }

        return result
            .OrderBy(value => value.ContainerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildSignature(IReadOnlyList<VanguardWorldLootContainerSnapshot> snapshot, SnapshotDiagnostics diagnostics)
    {
        string ids = string.Join(",", snapshot.Select(value => value.ContainerId + ":" + value.DoorState));
        return string.Join("|", diagnostics.Discovered, snapshot.Count, diagnostics.Inactive, diagnostics.Locked,
            diagnostics.RootMissing, diagnostics.InvalidId, diagnostics.Exceptions, ids);
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private struct SnapshotDiagnostics
    {
        public int Discovered;
        public int Inactive;
        public int Locked;
        public int RootMissing;
        public int InvalidId;
        public int Exceptions;
    }
}

/// <summary>
/// The persistence path activation surface is intentionally observation-only. It exercises the canonical provider on
/// the raid authority but does not score, allocate, claim, approach, open, search, or mutate containers.
/// </summary>
internal static class VanguardWorldLootContainerReadModelService
{
    public static void ResetForRaidLifecycle(string reason)
        => VanguardWorldLootContainerSnapshotProvider.ResetForRaidLifecycle(reason);

    public static void Tick()
    {
        if (!VanguardFikaCompat.IsRuntimeSettingsConsumerAuthority)
        {
            return;
        }

        _ = VanguardWorldLootContainerSnapshotProvider.GetSnapshot(DateTimeOffset.UtcNow);
    }
}
#endif

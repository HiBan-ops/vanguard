using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;

// Responsibility: Maintains the bounded state used by Raid Operator Runtime Registry in the raid-runtime state.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Raid.Runtime;

/// <summary>
/// Raid-scoped registry used by spawn, group binding and the future HUD.
/// PendingByOperatorId contains selected off-raid Operators not yet bound to an EFT BotOwner.
/// RuntimeByBotProfileId contains Operators that have completed the owner-aware bind.
/// </summary>
internal static class VanguardRaidOperatorRuntimeRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardRaidOperatorSnapshotDto> PendingByOperatorId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardRaidOperatorRuntimeRecord> RuntimeByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ExpectedBotProfileIdByOperatorId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> IneligibleLoggedOperatorIds = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> knownOwnerProfileIdsView = new(StringComparer.OrdinalIgnoreCase);
    private static string? activeRaidSessionId;

    public static string? ActiveRaidSessionId => activeRaidSessionId;

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            PendingByOperatorId.Clear();
            RuntimeByBotProfileId.Clear();
            ExpectedBotProfileIdByOperatorId.Clear();
            IneligibleLoggedOperatorIds.Clear();
            Volatile.Write(ref knownOwnerProfileIdsView, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            activeRaidSessionId = null;
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_RAID_SPAWN_STATUS", $"runtime registry reset reason={reason}");
    }

    public static void SetManifestOwners(VanguardRaidOperatorManifestForProfilesResponseDto response)
    {
        lock (Sync)
        {
            activeRaidSessionId = response.RaidSessionId;
            PendingByOperatorId.Clear();
            ExpectedBotProfileIdByOperatorId.Clear();
            foreach (var manifest in response.ManifestsByOwnerProfileId?.Values ?? Enumerable.Empty<VanguardRaidOperatorManifestResponseDto>())
            {
                foreach (var snapshot in manifest.Operators ?? new List<VanguardRaidOperatorSnapshotDto>())
                {
                    string operatorId = Normalize(snapshot.OperatorId);
                    string ownerProfileId = Normalize(snapshot.OwnerProfileId);
                    if (string.IsNullOrWhiteSpace(operatorId) || string.IsNullOrWhiteSpace(ownerProfileId))
                    {
                        continue;
                    }

                    PendingByOperatorId[operatorId] = snapshot;
                    if (snapshot.IsSelectedForRaid && !snapshot.IsEligibleForRaid && IneligibleLoggedOperatorIds.Add(operatorId))
                    {
                        VanguardClientDiagnosticsLog.Info(
                            "VANGUARD_OPERATOR_RAID_INELIGIBLE",
                            $"operator={operatorId}; owner={ownerProfileId}; selected=true; eligible=false; reason={Normalize(snapshot.EligibilityReason, "unspecified")}; pendingRuntimeBind=false; action=excluded_from_runtime_bind_scans");
                    }
                }
            }

            RefreshKnownOwnerProfileIdsViewLocked();
        }

        VanguardClientDiagnosticsLog.Info("VANGUARD_RAID_MANIFEST_OWNER", $"manifest owners registered owners={response.OwnerCount}, operators={response.OperatorCount}, raid={response.RaidSessionId ?? "<none>"}, ownership=player_owner_explicit");
    }

    public static IReadOnlyList<VanguardRaidOperatorSnapshotDto> GetPendingForAuthority()
    {
        lock (Sync)
        {
            return PendingByOperatorId.Values.ToArray();
        }
    }
    public static void MarkExpectedBotProfile(VanguardRaidOperatorSnapshotDto snapshot, string? expectedBotProfileId, string reason)
    {
        string operatorId = Normalize(snapshot.OperatorId);
        string expected = Normalize(expectedBotProfileId);
        if (string.IsNullOrWhiteSpace(operatorId) || string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        lock (Sync)
        {
            ExpectedBotProfileIdByOperatorId[operatorId] = expected;
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS",
            $"VANGUARD_RUNTIME_BIND_EXPECTED operator={operatorId}; expectedProfile={expected}; owner={Normalize(snapshot.OwnerProfileId)}; raid={Normalize(snapshot.RaidSessionId, activeRaidSessionId)}; reason={Normalize(reason)}; tag=VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS");
    }

    public static IReadOnlyList<VanguardPendingRuntimeBinding> GetPendingRuntimeBindings()
    {
        lock (Sync)
        {
            return PendingByOperatorId.Values
                .Where(snapshot => snapshot.IsSelectedForRaid && snapshot.IsEligibleForRaid)
                .Select(snapshot => new VanguardPendingRuntimeBinding(
                    snapshot,
                    ExpectedBotProfileIdByOperatorId.TryGetValue(Normalize(snapshot.OperatorId), out var expected) ? expected : string.Empty))
                .ToArray();
        }
    }

    public static int GetRuntimeCountForAuthority()
    {
        lock (Sync)
        {
            return RuntimeByBotProfileId.Count;
        }
    }

    public readonly struct VanguardPendingRuntimeBinding
    {
        public VanguardPendingRuntimeBinding(VanguardRaidOperatorSnapshotDto snapshot, string expectedBotProfileId)
        {
            Snapshot = snapshot;
            ExpectedBotProfileId = expectedBotProfileId ?? string.Empty;
        }

        public VanguardRaidOperatorSnapshotDto Snapshot { get; }
        public string ExpectedBotProfileId { get; }
    }


    public static bool AttachSpawnedOperator(VanguardRaidOperatorSnapshotDto snapshot, string botProfileId, string botNickname, bool spawnedByHeadless, bool isLocalPlayerOwner
#if SPT_CLIENT
        , EFT.BotOwner? botOwner
#endif
        )
    {
        string operatorId = Normalize(snapshot.OperatorId);
        string ownerProfileId = Normalize(snapshot.OwnerProfileId);
        if (string.IsNullOrWhiteSpace(operatorId) || string.IsNullOrWhiteSpace(ownerProfileId) || string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        var record = new VanguardRaidOperatorRuntimeRecord
        {
            OperatorId = operatorId,
            OwnerProfileId = ownerProfileId,
            BotProfileId = botProfileId,
            BotNickname = Normalize(botNickname, snapshot.Callsign, snapshot.DisplayName, operatorId),
            RaidSessionId = Normalize(snapshot.RaidSessionId, activeRaidSessionId),
            LootTargetPolicy = NormalizeLootTargetPolicy(snapshot.LootTargetPolicy),
            IsSpawnedByHeadless = spawnedByHeadless,
            IsLocalPlayerOwner = isLocalPlayerOwner,
#if SPT_CLIENT
            BotOwner = botOwner,
#endif
        };

        lock (Sync)
        {
            PendingByOperatorId.Remove(operatorId);
            ExpectedBotProfileIdByOperatorId.Remove(operatorId);
            RuntimeByBotProfileId[botProfileId] = record;
            RefreshKnownOwnerProfileIdsViewLocked();
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_OWNER_BOUND",
            $"operator={operatorId}, botProfile={botProfileId}, owner={ownerProfileId}, spawnedByHeadless={spawnedByHeadless}, localOwner={isLocalPlayerOwner}");
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_RUNTIME_REGISTERED", $"operator={operatorId}, botProfile={botProfileId}, owner={ownerProfileId}, nickname={record.BotNickname}");
        return true;
    }


    public static bool IsOperatorPending(string? operatorId)
    {
        string id = Normalize(operatorId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (Sync)
        {
            return PendingByOperatorId.ContainsKey(id);
        }
    }


    public static bool IsExpectedOperatorBotProfileId(string? botProfileId)
    {
        string id = Normalize(botProfileId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (Sync)
        {
            return ExpectedBotProfileIdByOperatorId.Values.Contains(id, StringComparer.OrdinalIgnoreCase);
        }
    }


    public static IReadOnlyList<string> GetExpectedOperatorBotProfileIds()
    {
        lock (Sync)
        {
            return ExpectedBotProfileIdByOperatorId.Values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }


    public static bool TryGetRuntimeByBotProfileId(string? botProfileId, out VanguardRaidOperatorRuntimeRecord record)
    {
        string id = Normalize(botProfileId);
        lock (Sync)
        {
            if (RuntimeByBotProfileId.TryGetValue(id, out var found))
            {
                record = found;
                return true;
            }
        }

        record = null!;
        return false;
    }


    public static IReadOnlyList<VanguardRaidOperatorRuntimeRecord> GetAllRuntimeOperators()
    {
        lock (Sync)
        {
            return RuntimeByBotProfileId.Values.ToArray();
        }
    }

    public static IReadOnlyList<VanguardRaidOperatorRuntimeRecord> GetOperatorsForOwner(string? ownerProfileId)
    {
        string owner = Normalize(ownerProfileId);
        lock (Sync)
        {
            return RuntimeByBotProfileId.Values.Where(record => string.Equals(record.OwnerProfileId, owner, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    public static IReadOnlyList<string> GetKnownOwnerProfileIds()
    {
        return Volatile.Read(ref knownOwnerProfileIdsView).ToArray();
    }

    public static bool IsKnownOwnerProfileId(string? ownerProfileId)
    {
        string owner = Normalize(ownerProfileId);
        return !string.IsNullOrWhiteSpace(owner)
            && Volatile.Read(ref knownOwnerProfileIdsView).Contains(owner);
    }

    private static void RefreshKnownOwnerProfileIdsViewLocked()
    {
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in PendingByOperatorId.Values)
        {
            string owner = Normalize(snapshot.OwnerProfileId);
            if (!string.IsNullOrWhiteSpace(owner))
            {
                next.Add(owner);
            }
        }

        foreach (var record in RuntimeByBotProfileId.Values)
        {
            string owner = Normalize(record.OwnerProfileId);
            if (!string.IsNullOrWhiteSpace(owner))
            {
                next.Add(owner);
            }
        }

        Volatile.Write(ref knownOwnerProfileIdsView, next);
    }

    private static string NormalizeLootTargetPolicy(string? value)
    {
        string raw = string.IsNullOrWhiteSpace(value) ? "CorpsesOnly" : value.Trim();
        if (string.Equals(raw, "ContainersOnly", StringComparison.OrdinalIgnoreCase)) return "ContainersOnly";
        if (string.Equals(raw, "CorpsesAndContainers", StringComparison.OrdinalIgnoreCase)) return "CorpsesAndContainers";
        if (string.Equals(raw, "Disabled", StringComparison.OrdinalIgnoreCase)) return "Disabled";
        return "CorpsesOnly";
    }

    private static string Normalize(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}

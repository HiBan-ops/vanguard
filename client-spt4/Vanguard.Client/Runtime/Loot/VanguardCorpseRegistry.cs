#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Maintains the bounded state used by Corpse Registry in the loot runtime.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Runtime.Loot;

internal sealed class VanguardCorpseRegistryEntry
{
    public string CorpseId { get; init; } = "none";
    public string VictimProfileId { get; init; } = "none";
    public string VictimName { get; init; } = "none";
    public string VictimSide { get; init; } = "none";
    public bool VictimIsAi { get; init; }
    public bool VictimWasOperator { get; init; }
    public Player Victim { get; init; } = null!;
    public Corpse Corpse { get; init; } = null!;
    public Vector3 RegisteredPosition { get; init; }
    public DateTimeOffset RegisteredAtUtc { get; init; }
    public IReadOnlyDictionary<string, VanguardCorpseHostilityEvidence> HostilityAtRegistrationByOwnerProfileId { get; init; }
        = new Dictionary<string, VanguardCorpseHostilityEvidence>(StringComparer.OrdinalIgnoreCase);
}

internal static class VanguardCorpseRegistry
{
    public const string StatusTag = "VANGUARD_CORPSE_LOOT_QUALIFICATION_STATUS";
    public const string ActiveApproachStatusTag = VanguardCorpseLootApproachDoctrine.StatusTag;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, VanguardCorpseRegistryEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(60);

    public static void ResetForRaidLifecycle(string reason)
    {
        VanguardCorpseLootOperationalTelemetry.FlushAndReset(reason);
        VanguardCorpseLootReadOnlyEvaluator.ResetForRaidLifecycle(reason);
        lock (Sync)
        {
            Entries.Clear();
        }

        VanguardClientDiagnosticsLog.Operational(StatusTag,
            () => $"VANGUARD_CORPSE_REGISTRY_RESET reason={Safe(reason)}; registryReadOnly=true; mutatesCorpse=false; approachMovement=true; claims=true; corpseInteraction=false; inventoryTransactions=false; equipmentMutation=false; tag={ActiveApproachStatusTag}");
    }

    public static void Register(Player? victim, Corpse? corpse)
    {
        if (victim == null || corpse == null)
        {
            return;
        }

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string victimProfileId = Safe(victim.ProfileId);
            string corpseId = victimProfileId + ":" + corpse.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
            IReadOnlyDictionary<string, VanguardCorpseHostilityEvidence> registrationEvidence
                = VanguardCorpseHostilityResolver.CaptureAtRegistration(victim);
            var entry = new VanguardCorpseRegistryEntry
            {
                CorpseId = corpseId,
                VictimProfileId = victimProfileId,
                VictimName = Safe(victim.Profile?.Info?.Nickname),
                VictimSide = victim.Side.ToString(),
                VictimIsAi = victim.IsAI,
                VictimWasOperator = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(victimProfileId, out _),
                Victim = victim,
                Corpse = corpse,
                RegisteredPosition = corpse.transform.position,
                RegisteredAtUtc = now,
                HostilityAtRegistrationByOwnerProfileId = registrationEvidence
            };

            bool duplicate;
            lock (Sync)
            {
                duplicate = Entries.ContainsKey(corpseId);
                if (!duplicate)
                {
                    Entries.Add(corpseId, entry);
                }
            }
            if (duplicate)
            {
                VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                    $"VANGUARD_CORPSE_REGISTER_DUPLICATE corpse={Safe(corpseId)}; victim={Safe(victimProfileId)}; ignored=true; readOnly=true");
                return;
            }

            VanguardCorpseLootOperationalTelemetry.RecordCorpseRegistered(entry);
            if (VanguardFikaCompat.IsRaidAuthority)
            {
                VanguardClientDiagnosticsLog.Operational(StatusTag, () =>
                    $"VANGUARD_CORPSE_REGISTERED corpse={Safe(corpseId)}; victim={Safe(victimProfileId)}; nick={Safe(entry.VictimName)}; side={Safe(entry.VictimSide)}; ai={entry.VictimIsAi}; operatorCorpse={entry.VictimWasOperator}; pos={Format(entry.RegisteredPosition)}; registrationHostilityOwners={registrationEvidence.Count}; authority=true; readOnly=true");
            }
            else
            {
                VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                    $"VANGUARD_CORPSE_REGISTERED_NONAUTHORITY corpse={Safe(corpseId)}; victim={Safe(victimProfileId)}; ai={entry.VictimIsAi}; authority=false; readOnly=true");
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag, () =>
                $"VANGUARD_CORPSE_REGISTER_FAILED type={exception.GetType().Name}; reason={Safe(exception.Message)}; failOpen=true");
        }
    }

    public static IReadOnlyList<VanguardCorpseRegistryEntry> GetSnapshot(DateTimeOffset now)
    {
        lock (Sync)
        {
            foreach (string key in Entries
                         .Where(pair => pair.Value.Corpse == null || now - pair.Value.RegisteredAtUtc > MaximumAge)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                Entries.Remove(key);
            }

            return Entries.Values
                .OrderByDescending(entry => entry.RegisteredAtUtc)
                .ToArray();
        }
    }

    public static bool TryGet(string corpseId, DateTimeOffset now, out VanguardCorpseRegistryEntry entry)
    {
        corpseId = string.IsNullOrWhiteSpace(corpseId) ? "none" : corpseId.Trim();
        lock (Sync)
        {
            if (Entries.TryGetValue(corpseId, out entry)
                && entry.Corpse != null
                && now - entry.RegisteredAtUtc <= MaximumAge)
            {
                return true;
            }

            if (Entries.ContainsKey(corpseId))
            {
                Entries.Remove(corpseId);
            }
        }

        entry = null!;
        return false;
    }

    private static string Format(Vector3 value)
        => $"{value.x:0.00},{value.y:0.00},{value.z:0.00}";

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

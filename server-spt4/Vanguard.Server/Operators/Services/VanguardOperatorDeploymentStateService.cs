using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Storage;

// Responsibility: Owns the server-process deployment reservation used to keep Release From Service from racing a pending/active raid manifest.
// Flow: The first access after server start clears deployment flags left by an earlier process, then eligible Operators are atomically reserved for a raid id and repeated reads of that same raid remain idempotent.
// Authority boundary: ActiveService.IsDeployed is the durable interlock while this process is alive; the in-memory raid-id map only identifies which current raid owns that flag and is never world-history authority.
// Invariant: A stale deployment flag from a dead server process cannot permanently strand an Operator, while a live reservation must make release fail closed until normal post-raid persistence clears IsDeployed.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorDeploymentStateService(
    VanguardOperatorStore store,
    ISptLogger<VanguardOperatorDeploymentStateService> logger)
{
    private const string StatusTag = "VANGUARD_OPERATOR_DEPLOYMENT_STATE_STATUS";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> ownerGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> initializedOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> currentReservations = new(StringComparer.OrdinalIgnoreCase);

    public async Task<VanguardDeploymentProcessBaselineResult> EnsureProcessBaselineAsync(string storageProfileId)
    {
        string owner = Normalize(storageProfileId);
        if (string.IsNullOrWhiteSpace(owner))
        {
            return new VanguardDeploymentProcessBaselineResult(false, "storage_profile_id_required", 0, false);
        }

        if (initializedOwners.ContainsKey(owner))
        {
            return new VanguardDeploymentProcessBaselineResult(true, "process_baseline_already_initialized", 0, false);
        }

        SemaphoreSlim gate = ownerGates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (initializedOwners.ContainsKey(owner))
            {
                return new VanguardDeploymentProcessBaselineResult(true, "process_baseline_already_initialized", 0, false);
            }

            IReadOnlyList<VanguardActiveServiceRecord> before = await store.LoadActiveServiceAsync(owner);
            int staleCount = before.Count(record => record.IsDeployed);
            if (staleCount > 0)
            {
                VanguardActiveServiceRecord[] after = before
                    .Select(record => record.IsDeployed ? record with { IsDeployed = false } : record)
                    .ToArray();
                VanguardActiveServiceAtomicWriteResult write = await store.CommitActiveServiceAtomicAsync(owner, before, after);
                if (!write.Success || !write.ReadBackVerified)
                {
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=process_baseline; owner={Safe(owner)}; state=failed; reason={Safe(write.Reason)}; staleDeploymentFlags={staleCount}; mutation=false; readback={Bool(write.ReadBackVerified)}; tag={StatusTag}"));
                    return new VanguardDeploymentProcessBaselineResult(false, write.Reason, staleCount, false);
                }

                RemoveReservationsForOwner(owner);
                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] phase=process_baseline; owner={Safe(owner)}; state=stale_flags_cleared; reason=previous_server_process_not_live; staleDeploymentFlags={staleCount}; mutation=true; readback=true; tag={StatusTag}"));
            }

            initializedOwners[owner] = 0;
            return new VanguardDeploymentProcessBaselineResult(true, staleCount > 0 ? "stale_deployment_flags_cleared" : "process_baseline_initialized", staleCount, staleCount > 0);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool IsReservedForRaid(string storageProfileId, string operatorId, string raidSessionId)
    {
        string key = BuildReservationKey(storageProfileId, operatorId);
        return currentReservations.TryGetValue(key, out string? reservedRaid)
            && string.Equals(reservedRaid, Normalize(raidSessionId), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<VanguardDeploymentReservationResult> ReserveForRaidAsync(
        string storageProfileId,
        string raidSessionId,
        IEnumerable<string> operatorIds)
    {
        string owner = Normalize(storageProfileId);
        string raid = Normalize(raidSessionId);
        string[] requestedOperators = operatorIds
            .Select(Normalize)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(raid))
        {
            return new VanguardDeploymentReservationResult(false, "owner_or_raid_required", 0, false);
        }
        if (requestedOperators.Length == 0)
        {
            return new VanguardDeploymentReservationResult(true, "no_eligible_operators_to_reserve", 0, true);
        }

        VanguardDeploymentProcessBaselineResult baseline = await EnsureProcessBaselineAsync(owner);
        if (!baseline.Success)
        {
            return new VanguardDeploymentReservationResult(false, "process_baseline_failed_" + baseline.Reason, 0, false);
        }

        SemaphoreSlim gate = ownerGates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            IReadOnlyList<VanguardActiveServiceRecord> before = await store.LoadActiveServiceAsync(owner);
            var targetSet = requestedOperators.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (VanguardActiveServiceRecord record in before.Where(record => targetSet.Contains(record.OperatorId) && record.IsDeployed))
            {
                if (!IsReservedForRaid(owner, record.OperatorId, raid))
                {
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=reserve; owner={Safe(owner)}; raid={Safe(raid)}; operator={Safe(record.OperatorId)}; state=blocked; reason=operator_reserved_by_other_live_raid; mutation=false; tag={StatusTag}"));
                    return new VanguardDeploymentReservationResult(false, "operator_reserved_by_other_live_raid", 0, false);
                }
            }

            VanguardActiveServiceRecord[] after = before
                .Select(record => targetSet.Contains(record.OperatorId) && !record.IsDeployed
                    ? record with { IsDeployed = true }
                    : record)
                .ToArray();
            int changed = after.Zip(before, (next, previous) => !Equals(next, previous)).Count(value => value);
            if (changed > 0)
            {
                VanguardActiveServiceAtomicWriteResult write = await store.CommitActiveServiceAtomicAsync(owner, before, after);
                if (!write.Success || !write.ReadBackVerified)
                {
                    logger.Warning(VanguardServerDiagnosticsLog.Present(
                        $"[{StatusTag}] phase=reserve; owner={Safe(owner)}; raid={Safe(raid)}; state=failed; reason={Safe(write.Reason)}; requested={requestedOperators.Length}; mutation=false; readback={Bool(write.ReadBackVerified)}; tag={StatusTag}"));
                    return new VanguardDeploymentReservationResult(false, write.Reason, 0, false);
                }
            }

            foreach (string operatorId in requestedOperators)
            {
                if (after.Any(record => string.Equals(record.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase) && record.IsDeployed))
                {
                    currentReservations[BuildReservationKey(owner, operatorId)] = raid;
                }
            }

            int reserved = requestedOperators.Count(operatorId => IsReservedForRaid(owner, operatorId, raid));
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=reserve; owner={Safe(owner)}; raid={Safe(raid)}; state=reserved; requested={requestedOperators.Length}; reserved={reserved}; mutation={Bool(changed > 0)}; readback=true; tag={StatusTag}"));
            return new VanguardDeploymentReservationResult(true, changed > 0 ? "deployment_reserved" : "deployment_reservation_idempotent", reserved, true);
        }
        finally
        {
            gate.Release();
        }
    }

    public int CompleteRaidReservations(string storageProfileId, string raidSessionId, IEnumerable<string> operatorIds)
    {
        string owner = Normalize(storageProfileId);
        string raid = Normalize(raidSessionId);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(raid))
        {
            return 0;
        }

        int removed = 0;
        foreach (string operatorId in operatorIds.Select(Normalize).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string key = BuildReservationKey(owner, operatorId);
            if (currentReservations.TryGetValue(key, out string? reservedRaid)
                && string.Equals(reservedRaid, raid, StringComparison.OrdinalIgnoreCase)
                && currentReservations.TryRemove(key, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=complete; owner={Safe(owner)}; raid={Safe(raid)}; state=released; reservationsCleared={removed}; durableFlagAuthority=post_raid_persistence; tag={StatusTag}"));
        }
        return removed;
    }

    private void RemoveReservationsForOwner(string owner)
    {
        string prefix = owner + "|";
        foreach (string key in currentReservations.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            currentReservations.TryRemove(key, out _);
        }
    }

    private static string BuildReservationKey(string storageProfileId, string operatorId) =>
        Normalize(storageProfileId) + "|" + Normalize(operatorId);

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ');
    private static string Bool(bool value) => value ? "true" : "false";
}

public sealed record VanguardDeploymentProcessBaselineResult(
    bool Success,
    string Reason,
    int ClearedStaleDeploymentFlags,
    bool Mutation);

public sealed record VanguardDeploymentReservationResult(
    bool Success,
    string Reason,
    int ReservedOperatorCount,
    bool ReadBackVerified);

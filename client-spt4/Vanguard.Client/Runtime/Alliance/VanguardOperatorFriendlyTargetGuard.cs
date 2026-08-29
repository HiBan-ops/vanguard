#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Interop;
using Vanguard.Client.Raid.Runtime;

// Responsibility: Prevents Vanguard Operators and allied coop players from being retained or committed as hostile targets.
// Flow: Candidate target identity is resolved across EFT profile/group/Fika allegiance evidence; friendly matches are rejected and existing hostile state can be cleared through the dedicated compatibility path.
// Authority boundary: The guard enforces Vanguard alliance safety but does not create hostility toward non-friendly actors; enemy truth still comes from awareness/SAIN/EFT.
// Invariant: Player squadmates and Vanguard Operators stay protected across respawn/bind changes, while unknown identity never becomes friendly solely by guesswork.
namespace Vanguard.Client.Runtime.Alliance;

/// <summary>
/// Event-driven per-Operator cleanup for friendly targets that slipped into EFT memory/group lists.
/// Vanguard removes the previous all-Operators/all-friendlies one-second mutation sweep.
/// AddEnemy attempts mark only the affected Operator dirty; query vetoes remain zero-mutation.
/// A bounded round-robin safety audit only clears an actually friendly GoalEnemy.
/// </summary>
internal static class VanguardOperatorFriendlyTargetGuard
{
    public const string StatusTag = "VANGUARD_COOP_FRIENDLY_TARGET_GUARD_OK";
    public const string StallGuardStatusTag = "VANGUARD_FRIENDLY_TARGET_EVENT_DRIVEN_STATUS";

    private static readonly object Sync = new();
    private static readonly HashSet<string> DirtyBotProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastRepairUtcByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MinimumRepairSpacing = TimeSpan.FromMilliseconds(250d);
    private static readonly TimeSpan PeriodicGoalEnemyAuditSpacing = TimeSpan.FromSeconds(5d);
    private static DateTimeOffset nextPeriodicGoalEnemyAuditUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastSummaryUtc = DateTimeOffset.MinValue;
    private static int periodicAuditIndex;
    private static bool globalRepairPending;
    private static int blockedSinceSummary;
    private static int dirtyQueuedSinceSummary;
    private static int repairsSinceSummary;
    private static int goalEnemiesClearedSinceSummary;
    private static int periodicAuditsSinceSummary;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            DirtyBotProfileIds.Clear();
            LastRepairUtcByBotProfileId.Clear();
            nextPeriodicGoalEnemyAuditUtc = DateTimeOffset.MinValue;
            lastSummaryUtc = DateTimeOffset.MinValue;
            periodicAuditIndex = 0;
            globalRepairPending = false;
            blockedSinceSummary = 0;
            dirtyQueuedSinceSummary = 0;
            repairsSinceSummary = 0;
            goalEnemiesClearedSinceSummary = 0;
            periodicAuditsSinceSummary = 0;
        }

        VanguardClientDiagnosticsLog.Diagnostic(
            StallGuardStatusTag,
            () => $"VANGUARD_FRIENDLY_TARGET_EVENT_DRIVEN_RESET reason={Safe(reason)}; periodicMutationSweep=false; dirtyRepair=true; queryVetoMutation=false; tag={StallGuardStatusTag}");
    }

    public static void Tick()
    {
        VanguardFriendlyIdentityRegistry.Tick();
        VanguardAllianceHostilityLogGate.Tick();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<VanguardRaidOperatorRuntimeRecord> records = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();

        VanguardRaidOperatorRuntimeRecord? dirtyRecord = ResolveNextDirtyRecord(records, now);
        if (dirtyRecord != null)
        {
            int repaired = CleanOperator(dirtyRecord, "hostility_add_attempt", repairRelations: true);
            repairsSinceSummary += repaired;
        }
        else if (now >= nextPeriodicGoalEnemyAuditUtc)
        {
            VanguardRaidOperatorRuntimeRecord? auditRecord = ResolveNextPeriodicAuditRecord(records);
            nextPeriodicGoalEnemyAuditUtc = now + PeriodicGoalEnemyAuditSpacing;
            if (auditRecord != null)
            {
                periodicAuditsSinceSummary++;
                int cleared = ClearFriendlyGoalEnemy(auditRecord, "round_robin_goal_enemy_audit");
                goalEnemiesClearedSinceSummary += cleared;
            }
        }

        EmitSummaryIfDue(now, records.Count);
    }

    /// <summary>
    /// Records a blocked friendly-hostility path. Only actual mutation attempts should request
    /// relation repair. Read-only IsEnemy query vetoes pass repairRequired=false.
    /// </summary>
    public static void OnHostilityBlocked(string? actorProfileId, string? targetProfileId, bool repairRequired)
    {
        lock (Sync)
        {
            blockedSinceSummary++;
            if (!repairRequired)
            {
                return;
            }

            string actor = Normalize(actorProfileId);
            if (!string.IsNullOrWhiteSpace(actor)
                && VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(actor, out _))
            {
                if (DirtyBotProfileIds.Add(actor))
                {
                    dirtyQueuedSinceSummary++;
                }
                return;
            }

            // Early group construction can block an AddEnemy before a concrete member can be
            // resolved. Mark a single global repair episode; Tick still repairs at most one
            // Operator per frame.
            globalRepairPending = true;
            dirtyQueuedSinceSummary++;
        }
    }

    public static void BindOperatorFriendlyRelations(BotOwner owner, string reason)
    {
        if (owner is null || string.IsNullOrWhiteSpace(owner.ProfileId))
        {
            return;
        }

        VanguardFriendlyIdentityRegistry.RefreshNow(reason);
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(owner.ProfileId, out var record))
        {
            return;
        }

        int repaired = CleanOperator(record, reason, repairRelations: true);
        if (repaired > 0)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_COOP_FRIENDLY_TARGET_BIND_CLEANUP operator={record.OperatorId}; botProfile={record.BotProfileId}; repaired={repaired}; reason={reason}; eventDriven=true");
        }
    }

    private static VanguardRaidOperatorRuntimeRecord? ResolveNextDirtyRecord(
        IReadOnlyList<VanguardRaidOperatorRuntimeRecord> records,
        DateTimeOffset now)
    {
        lock (Sync)
        {
            if (globalRepairPending)
            {
                foreach (VanguardRaidOperatorRuntimeRecord record in records)
                {
                    if (record.BotOwner == null || record.BotOwner.IsDead || string.IsNullOrWhiteSpace(record.BotProfileId))
                    {
                        continue;
                    }
                    DirtyBotProfileIds.Add(record.BotProfileId);
                }
                globalRepairPending = false;
            }

            foreach (string botProfileId in DirtyBotProfileIds.ToArray())
            {
                VanguardRaidOperatorRuntimeRecord? record = records.FirstOrDefault(candidate =>
                    string.Equals(candidate.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase));
                if (record == null || record.BotOwner == null || record.BotOwner.IsDead)
                {
                    DirtyBotProfileIds.Remove(botProfileId);
                    continue;
                }

                if (LastRepairUtcByBotProfileId.TryGetValue(botProfileId, out DateTimeOffset lastRepair)
                    && now - lastRepair < MinimumRepairSpacing)
                {
                    continue;
                }

                DirtyBotProfileIds.Remove(botProfileId);
                LastRepairUtcByBotProfileId[botProfileId] = now;
                return record;
            }
        }

        return null;
    }

    private static VanguardRaidOperatorRuntimeRecord? ResolveNextPeriodicAuditRecord(IReadOnlyList<VanguardRaidOperatorRuntimeRecord> records)
    {
        VanguardRaidOperatorRuntimeRecord[] liveRecords = records
            .Where(record => record.BotOwner != null && !record.BotOwner.IsDead)
            .OrderBy(record => record.BotProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (liveRecords.Length == 0)
        {
            periodicAuditIndex = 0;
            return null;
        }

        periodicAuditIndex = Math.Abs(periodicAuditIndex) % liveRecords.Length;
        VanguardRaidOperatorRuntimeRecord selected = liveRecords[periodicAuditIndex];
        periodicAuditIndex = (periodicAuditIndex + 1) % liveRecords.Length;
        return selected;
    }

    private static int CleanOperator(VanguardRaidOperatorRuntimeRecord record, string reason, bool repairRelations)
    {
        BotOwner? owner = record.BotOwner;
        if (owner is null)
        {
            return 0;
        }

        int changed = ClearFriendlyGoalEnemy(record, reason);
        if (!repairRelations)
        {
            return changed;
        }

        foreach (object target in VanguardFriendlyIdentityRegistry.GetKnownFriendlyPlayersForOperator(record.BotProfileId))
        {
            string? targetProfileId = VanguardEftReflection.TryResolveProfileId(target);
            if (string.IsNullOrWhiteSpace(targetProfileId))
            {
                continue;
            }

            try
            {
                VanguardEftReflection.InvokeSingleArgumentMethod(owner.Memory, "DeleteInfoAboutEnemy", target);
                VanguardEftReflection.InvokeSingleArgumentMethod(owner.BotsGroup, "RemoveEnemy", target);
                VanguardEftReflection.InvokeSingleArgumentMethod(owner.BotsGroup, "AddNeutral", target);
                VanguardEftReflection.InvokeSingleArgumentMethod(owner.BotsGroup, "AddAlly", target);
                changed++;
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_COOP_FRIENDLY_TARGET_CLEANUP_FAILED operator={record.OperatorId}; botProfile={record.BotProfileId}; target={targetProfileId}; reason={reason}; error={exception.GetType().Name}:{exception.Message}");
            }
        }

        return changed;
    }

    private static int ClearFriendlyGoalEnemy(VanguardRaidOperatorRuntimeRecord record, string reason)
    {
        BotOwner? owner = record.BotOwner;
        if (owner?.Memory is null)
        {
            return 0;
        }

        object? goalEnemy = owner.Memory.GoalEnemy;
        string? targetProfileId = VanguardEftReflection.TryResolveProfileId(goalEnemy);
        if (string.IsNullOrWhiteSpace(targetProfileId)
            || !VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(record.BotProfileId, targetProfileId))
        {
            return 0;
        }

        owner.Memory.GoalEnemy = null;
        VanguardFriendlyIdentityRegistry.TryLogBlockedHostility("clear_goal_enemy", record.BotProfileId, targetProfileId, reason);
        return 1;
    }

    private static void EmitSummaryIfDue(DateTimeOffset now, int operatorCount)
    {
        if (now - lastSummaryUtc < TimeSpan.FromSeconds(10d))
        {
            return;
        }

        int dirtyCount;
        lock (Sync)
        {
            dirtyCount = DirtyBotProfileIds.Count + (globalRepairPending ? 1 : 0);
        }

        if (operatorCount == 0
            && blockedSinceSummary == 0
            && dirtyQueuedSinceSummary == 0
            && repairsSinceSummary == 0
            && goalEnemiesClearedSinceSummary == 0)
        {
            lastSummaryUtc = now;
            return;
        }

        VanguardClientDiagnosticsLog.Diagnostic(
            StallGuardStatusTag,
            () => $"VANGUARD_FRIENDLY_TARGET_EVENT_DRIVEN_SUMMARY operators={operatorCount}; blocked={blockedSinceSummary}; dirtyQueued={dirtyQueuedSinceSummary}; dirtyPending={dirtyCount}; relationRepairs={repairsSinceSummary}; goalEnemiesCleared={goalEnemiesClearedSinceSummary}; periodicAudits={periodicAuditsSinceSummary}; periodicMutationSweep=false; maxDirtyRepairsPerFrame=1; tag={StallGuardStatusTag}");
        blockedSinceSummary = 0;
        dirtyQueuedSinceSummary = 0;
        repairsSinceSummary = 0;
        goalEnemiesClearedSinceSummary = 0;
        periodicAuditsSinceSummary = 0;
        lastSummaryUtc = now;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#else
namespace Vanguard.Client.Runtime.Alliance;

internal static class VanguardOperatorFriendlyTargetGuard
{
    public static void Tick() { }
    public static void ResetForRaidLifecycle(string reason) { }
}
#endif

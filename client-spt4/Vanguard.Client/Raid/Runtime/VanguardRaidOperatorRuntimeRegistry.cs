using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
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
    // Manifest truth survives the first successful bind. Pending is a transient bind queue only;
    // explicit runtime replacement must recover identity from this raid-scoped manifest, never from ProfileId alone.
    private static readonly Dictionary<string, VanguardRaidOperatorSnapshotDto> ManifestByOperatorId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, VanguardRaidOperatorRuntimeRecord> RuntimeByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> LastRuntimeIncarnationByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ExpectedBotProfileIdByOperatorId = new(StringComparer.OrdinalIgnoreCase);
#if SPT_CLIENT
    private static readonly Dictionary<string, VanguardRuntimeReplacementAdmission> ReplacementAdmissionByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
#endif
    private static readonly HashSet<string> IneligibleLoggedOperatorIds = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> knownOwnerProfileIdsView = new(StringComparer.OrdinalIgnoreCase);
    private static string? activeRaidSessionId;

    public static string? ActiveRaidSessionId => activeRaidSessionId;

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            PendingByOperatorId.Clear();
            ManifestByOperatorId.Clear();
            RuntimeByBotProfileId.Clear();
            LastRuntimeIncarnationByBotProfileId.Clear();
            ExpectedBotProfileIdByOperatorId.Clear();
#if SPT_CLIENT
            ReplacementAdmissionByBotProfileId.Clear();
#endif
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
            ManifestByOperatorId.Clear();
            ExpectedBotProfileIdByOperatorId.Clear();
#if SPT_CLIENT
            ReplacementAdmissionByBotProfileId.Clear();
#endif
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

                    ManifestByOperatorId[operatorId] = snapshot;
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


#if SPT_CLIENT
    private sealed class VanguardRuntimeReplacementAdmission
    {
        public VanguardRuntimeReplacementAdmission(
            string operatorId,
            string ownerProfileId,
            string botProfileId,
            string raidSessionId,
            long expectedPriorIncarnation,
            EFT.BotOwner exactNewBotOwner,
            DateTimeOffset authorizedAtUtc,
            string source)
        {
            OperatorId = operatorId;
            OwnerProfileId = ownerProfileId;
            BotProfileId = botProfileId;
            RaidSessionId = raidSessionId;
            ExpectedPriorIncarnation = expectedPriorIncarnation;
            ExactNewBotOwner = new WeakReference<EFT.BotOwner>(exactNewBotOwner);
            AuthorizedAtUtc = authorizedAtUtc;
            Source = source;
        }

        public string OperatorId { get; }
        public string OwnerProfileId { get; }
        public string BotProfileId { get; }
        public string RaidSessionId { get; }
        public long ExpectedPriorIncarnation { get; }
        public WeakReference<EFT.BotOwner> ExactNewBotOwner { get; }
        public DateTimeOffset AuthorizedAtUtc { get; }
        public string Source { get; }
    }

    /// <summary>
    /// Arms one explicit replacement only from a concrete authoritative BotSpawner.OnBotCreated candidate.
    /// ProfileId equality is necessary but never sufficient: stable manifest identity, a clean exact detach,
    /// current raid identity and the exact new BotOwner reference are all required.
    /// </summary>
    public static bool TryAuthorizeExplicitRuntimeReplacement(
        EFT.BotOwner? candidate,
        string source,
        out VanguardRaidOperatorSnapshotDto snapshot,
        out string reason)
    {
        snapshot = null!;
        reason = "unknown";
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            reason = "not_raid_authority";
            return false;
        }

        if (candidate == null || string.IsNullOrWhiteSpace(candidate.ProfileId) || candidate.IsDead)
        {
            reason = candidate == null ? "candidate_missing" : candidate.IsDead ? "candidate_dead" : "candidate_profile_missing";
            return false;
        }

        string botProfileId = Normalize(candidate.ProfileId);
        DateTimeOffset authorizedAtUtc = DateTimeOffset.UtcNow;
        VanguardRaidOperatorRuntimeRecord existing;
        VanguardRaidOperatorSnapshotDto manifest;
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(activeRaidSessionId))
            {
                reason = "active_raid_missing";
                return false;
            }

            if (!RuntimeByBotProfileId.TryGetValue(botProfileId, out existing))
            {
                reason = "no_prior_runtime_record";
                return false;
            }

            if (existing.BotOwner != null || !existing.RuntimeDetachedAtUtc.HasValue)
            {
                reason = "prior_runtime_still_present";
                return false;
            }

            if (existing.RuntimeCleanupSucceeded != true)
            {
                reason = "prior_runtime_cleanup_not_clean";
                return false;
            }

            if (existing.RuntimeWasDeadAtDetach != false)
            {
                reason = "prior_runtime_terminal_or_unknown_death_state";
                return false;
            }

            if (!string.Equals(existing.RaidSessionId, activeRaidSessionId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "prior_runtime_raid_mismatch";
                return false;
            }

            if (!ManifestByOperatorId.TryGetValue(existing.OperatorId, out manifest))
            {
                reason = "stable_manifest_missing";
                return false;
            }

            string manifestOperatorId = Normalize(manifest.OperatorId);
            string manifestOwnerProfileId = Normalize(manifest.OwnerProfileId);
            string manifestRaidSessionId = Normalize(manifest.RaidSessionId, activeRaidSessionId);
            if (!manifest.IsSelectedForRaid
                || !manifest.IsEligibleForRaid
                || !string.Equals(manifestOperatorId, existing.OperatorId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(manifestOwnerProfileId, existing.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(manifestRaidSessionId, existing.RaidSessionId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "stable_manifest_identity_or_eligibility_mismatch";
                return false;
            }

            if (ReplacementAdmissionByBotProfileId.TryGetValue(botProfileId, out VanguardRuntimeReplacementAdmission armed))
            {
                if (armed.ExpectedPriorIncarnation != existing.RuntimeIncarnation
                    || !armed.ExactNewBotOwner.TryGetTarget(out EFT.BotOwner? armedOwner)
                    || !ReferenceEquals(armedOwner, candidate))
                {
                    reason = "different_replacement_candidate_already_armed";
                    return false;
                }

                PendingByOperatorId[existing.OperatorId] = manifest;
                ExpectedBotProfileIdByOperatorId[existing.OperatorId] = botProfileId;
                snapshot = manifest;
                reason = "already_authorized_exact_candidate";
                RefreshKnownOwnerProfileIdsViewLocked();
                return true;
            }

            ReplacementAdmissionByBotProfileId[botProfileId] = new VanguardRuntimeReplacementAdmission(
                existing.OperatorId,
                existing.OwnerProfileId,
                existing.BotProfileId,
                existing.RaidSessionId,
                existing.RuntimeIncarnation,
                candidate,
                authorizedAtUtc,
                Normalize(source, "typed_bot_created"));
            PendingByOperatorId[existing.OperatorId] = manifest;
            ExpectedBotProfileIdByOperatorId[existing.OperatorId] = botProfileId;
            snapshot = manifest;
            reason = "authorized";
            RefreshKnownOwnerProfileIdsViewLocked();
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_RUNTIME_REPLACEMENT",
            $"replacement authorized operator={existing.OperatorId}; owner={existing.OwnerProfileId}; botProfile={existing.BotProfileId}; priorIncarnation={existing.RuntimeIncarnation}; raid={existing.RaidSessionId}; source={Normalize(source, "typed_bot_created")}; exactNewBotOwner=true; explicitCreationEvent=true; peerReconnectAuthority=false; staleStateInheritance=false; action=pending_exact_bind");
        return true;
    }

    public static bool IsPendingBindCandidateAuthorized(
        VanguardRaidOperatorSnapshotDto snapshot,
        EFT.BotOwner? candidate,
        out string reason)
    {
        reason = "unknown";
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.ProfileId) || candidate.IsDead)
        {
            reason = candidate == null ? "candidate_missing" : candidate.IsDead ? "candidate_dead" : "candidate_profile_missing";
            return false;
        }

        string operatorId = Normalize(snapshot.OperatorId);
        string ownerProfileId = Normalize(snapshot.OwnerProfileId);
        string botProfileId = Normalize(candidate.ProfileId);
        lock (Sync)
        {
            if (!RuntimeByBotProfileId.TryGetValue(botProfileId, out VanguardRaidOperatorRuntimeRecord existing))
            {
                reason = "initial_bind_no_prior_runtime";
                return true;
            }

            if (!string.Equals(existing.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase))
            {
                reason = "persistent_identity_mismatch";
                return false;
            }

            if (existing.BotOwner != null)
            {
                bool exactExisting = ReferenceEquals(existing.BotOwner, candidate);
                reason = exactExisting ? "idempotent_exact_existing_owner" : "different_active_owner";
                return exactExisting;
            }

            if (!existing.RuntimeDetachedAtUtc.HasValue
                || existing.RuntimeCleanupSucceeded != true
                || existing.RuntimeWasDeadAtDetach != false)
            {
                reason = "detached_runtime_not_replaceable";
                return false;
            }

            if (!ReplacementAdmissionByBotProfileId.TryGetValue(botProfileId, out VanguardRuntimeReplacementAdmission admission)
                || admission.ExpectedPriorIncarnation != existing.RuntimeIncarnation
                || !string.Equals(admission.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(admission.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                || !admission.ExactNewBotOwner.TryGetTarget(out EFT.BotOwner? admittedOwner)
                || !ReferenceEquals(admittedOwner, candidate))
            {
                reason = "exact_replacement_admission_missing_or_mismatch";
                return false;
            }

            reason = "explicit_replacement_exact_candidate";
            return true;
        }
    }

    public static bool CancelExplicitRuntimeReplacementExact(EFT.BotOwner? candidate, string reason)
    {
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.ProfileId))
        {
            return false;
        }

        string botProfileId = Normalize(candidate.ProfileId);
        VanguardRuntimeReplacementAdmission removed;
        lock (Sync)
        {
            if (!ReplacementAdmissionByBotProfileId.TryGetValue(botProfileId, out VanguardRuntimeReplacementAdmission admission)
                || !admission.ExactNewBotOwner.TryGetTarget(out EFT.BotOwner? armedOwner)
                || !ReferenceEquals(armedOwner, candidate))
            {
                return false;
            }

            ReplacementAdmissionByBotProfileId.Remove(botProfileId);
            PendingByOperatorId.Remove(admission.OperatorId);
            ExpectedBotProfileIdByOperatorId.Remove(admission.OperatorId);
            removed = admission;
            RefreshKnownOwnerProfileIdsViewLocked();
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_RUNTIME_REPLACEMENT",
            $"replacement admission cancelled operator={removed.OperatorId}; owner={removed.OwnerProfileId}; botProfile={removed.BotProfileId}; priorIncarnation={removed.ExpectedPriorIncarnation}; raid={removed.RaidSessionId}; reason={Normalize(reason, "candidate_removed_before_bind")}; exactNewBotOwner=true; peerReconnectAuthority=false; staleStateInheritance=false");
        return true;
    }
#endif


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

        VanguardRaidOperatorRuntimeRecord record;
        long runtimeIncarnation;
        bool explicitReplacement = false;
        long priorIncarnation = 0L;
        string replacementSource = "none";
        lock (Sync)
        {
            if (RuntimeByBotProfileId.TryGetValue(botProfileId, out VanguardRaidOperatorRuntimeRecord existing))
            {
                if (!string.Equals(existing.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    VanguardClientDiagnosticsLog.Warning(
                        "VANGUARD_OPERATOR_RUNTIME_REGISTERED",
                        $"runtime bind rejected botProfile={botProfileId}; incomingOperator={operatorId}; incomingOwner={ownerProfileId}; existingOperator={existing.OperatorId}; existingOwner={existing.OwnerProfileId}; reason=profile_identity_collision; exactReplacementRequired=true");
                    return false;
                }
#if SPT_CLIENT
                if (existing.BotOwner != null && !ReferenceEquals(existing.BotOwner, botOwner))
                {
                    VanguardClientDiagnosticsLog.Warning(
                        "VANGUARD_OPERATOR_RUNTIME_REGISTERED",
                        $"runtime bind deferred operator={operatorId}; botProfile={botProfileId}; existingIncarnation={existing.RuntimeIncarnation}; reason=prior_exact_botowner_still_present; action=wait_for_exact_presence_detach; staleStateInheritance=false");
                    return false;
                }

                if (existing.BotOwner != null && ReferenceEquals(existing.BotOwner, botOwner))
                {
                    PendingByOperatorId.Remove(operatorId);
                    ExpectedBotProfileIdByOperatorId.Remove(operatorId);
                    RefreshKnownOwnerProfileIdsViewLocked();
                    return true;
                }

                if (existing.BotOwner == null && existing.RuntimeDetachedAtUtc.HasValue)
                {
                    if (existing.RuntimeCleanupSucceeded != true
                        || existing.RuntimeWasDeadAtDetach != false
                        || botOwner == null
                        || botOwner.IsDead
                        || !ReplacementAdmissionByBotProfileId.TryGetValue(botProfileId, out VanguardRuntimeReplacementAdmission admission)
                        || admission.ExpectedPriorIncarnation != existing.RuntimeIncarnation
                        || !string.Equals(admission.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(admission.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(admission.RaidSessionId, existing.RaidSessionId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(existing.RaidSessionId, activeRaidSessionId, StringComparison.OrdinalIgnoreCase)
                        || !admission.ExactNewBotOwner.TryGetTarget(out EFT.BotOwner? admittedOwner)
                        || !ReferenceEquals(admittedOwner, botOwner))
                    {
                        VanguardClientDiagnosticsLog.Warning(
                            "VANGUARD_OPERATOR_RUNTIME_REGISTERED",
                            $"runtime bind rejected operator={operatorId}; botProfile={botProfileId}; existingIncarnation={existing.RuntimeIncarnation}; reason=definitive_presence_detached_without_exact_replacement_admission; cleanupSucceeded={existing.RuntimeCleanupSucceeded?.ToString() ?? "unknown"}; staleStateInheritance=false; action=require_typed_creation_admission; implicitProfileIdRebind=false; peerReconnectAuthority=false");
                        return false;
                    }

                    explicitReplacement = true;
                    priorIncarnation = existing.RuntimeIncarnation;
                    replacementSource = admission.Source;
                    ReplacementAdmissionByBotProfileId.Remove(botProfileId);
                }
#endif
            }

            LastRuntimeIncarnationByBotProfileId.TryGetValue(botProfileId, out long previousIncarnation);
            runtimeIncarnation = Math.Max(previousIncarnation, priorIncarnation) + 1L;
            LastRuntimeIncarnationByBotProfileId[botProfileId] = runtimeIncarnation;

            record = new VanguardRaidOperatorRuntimeRecord
            {
                OperatorId = operatorId,
                OwnerProfileId = ownerProfileId,
                BotProfileId = botProfileId,
                BotNickname = Normalize(botNickname, snapshot.Callsign, snapshot.DisplayName, operatorId),
                RaidSessionId = Normalize(snapshot.RaidSessionId, activeRaidSessionId),
                LootTargetPolicy = NormalizeLootTargetPolicy(snapshot.LootTargetPolicy),
                TacticalRole = Normalize(snapshot.Role),
                TacticalSpecialty = Normalize(snapshot.Specialty),
                BasePersona = Normalize(snapshot.SainRuntime?.BasePersona),
                Doctrine = Normalize(snapshot.SainRuntime?.Doctrine),
                Temperament = Normalize(snapshot.SainRuntime?.Temperament),
                CombatStyle = Normalize(snapshot.SainRuntime?.CombatStyle),
                EngagementRange = Normalize(snapshot.SainRuntime?.EngagementRange),
                SquadRole = Normalize(snapshot.SainRuntime?.SquadRole),
                IdentitySchemaVersion = snapshot.SchemaVersion,
                IsSpawnedByHeadless = spawnedByHeadless,
                IsLocalPlayerOwner = isLocalPlayerOwner,
                RuntimeIncarnation = runtimeIncarnation,
#if SPT_CLIENT
                BotOwner = botOwner,
#endif
            };

            PendingByOperatorId.Remove(operatorId);
            ExpectedBotProfileIdByOperatorId.Remove(operatorId);
            RuntimeByBotProfileId[botProfileId] = record;
            RefreshKnownOwnerProfileIdsViewLocked();
        }

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_OWNER_BOUND",
            $"operator={operatorId}, botProfile={botProfileId}, owner={ownerProfileId}, spawnedByHeadless={spawnedByHeadless}, localOwner={isLocalPlayerOwner}");
        VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_RUNTIME_REGISTERED", $"operator={operatorId}, botProfile={botProfileId}, owner={ownerProfileId}, nickname={record.BotNickname}, incarnation={record.RuntimeIncarnation}");
        if (explicitReplacement)
        {
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_RUNTIME_REPLACED",
                $"operator={operatorId}; owner={ownerProfileId}; botProfile={botProfileId}; priorIncarnation={priorIncarnation}; newIncarnation={record.RuntimeIncarnation}; raid={record.RaidSessionId}; source={Normalize(replacementSource, "typed_bot_created")}; exactNewBotOwner=true; explicitCreationEvent=true; peerReconnectAuthority=false; staleStateInheritance=false; leasesInherited=false; windowsInherited=false; bindingsInherited=false");
        }
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


#if SPT_CLIENT
    public static bool TryGetRuntimeByExactBotOwner(EFT.BotOwner? botOwner, out VanguardRaidOperatorRuntimeRecord record)
    {
        if (botOwner != null)
        {
            lock (Sync)
            {
                foreach (VanguardRaidOperatorRuntimeRecord candidate in RuntimeByBotProfileId.Values)
                {
                    if (ReferenceEquals(candidate.BotOwner, botOwner))
                    {
                        record = candidate;
                        return true;
                    }
                }
            }
        }

        record = null!;
        return false;
    }

    public static bool DetachRuntimePresenceExact(EFT.BotOwner? botOwner, long expectedIncarnation, bool wasDeadAtSealStart, bool cleanupSucceeded, string reason, out VanguardRaidOperatorRuntimeRecord record)
    {
        if (botOwner == null)
        {
            record = null!;
            return false;
        }

        DateTimeOffset detachedAtUtc = DateTimeOffset.UtcNow;
        lock (Sync)
        {
            foreach (VanguardRaidOperatorRuntimeRecord candidate in RuntimeByBotProfileId.Values)
            {
                if (!ReferenceEquals(candidate.BotOwner, botOwner))
                {
                    continue;
                }

                if (candidate.RuntimeIncarnation != expectedIncarnation)
                {
                    record = candidate;
                    return false;
                }

                candidate.RuntimeDetachedAtUtc = detachedAtUtc;
                candidate.RuntimeDetachReason = Normalize(reason, "runtime_presence_lost");
                candidate.RuntimeWasDeadAtDetach = wasDeadAtSealStart;
                candidate.RuntimeCleanupSucceeded = cleanupSucceeded;
                candidate.BotOwner = null;
                record = candidate;
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_RUNTIME_PRESENCE",
                    $"runtime presence detached operator={candidate.OperatorId}; botProfile={candidate.BotProfileId}; incarnation={candidate.RuntimeIncarnation}; detachedAt={detachedAtUtc:O}; reason={candidate.RuntimeDetachReason}; wasDeadAtSealStart={wasDeadAtSealStart}; cleanupSucceeded={cleanupSucceeded}; quarantined={(!cleanupSucceeded)}; persistentRecordRetained=true; exactBotOwner=true");
                return true;
            }
        }

        record = null!;
        return false;
    }
#endif


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

#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Medical.Execution;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Coordinates the complete single-pair Medic Support runtime: direct teammate care in combat/protected windows, or no-combat physical supply handoff followed by targeted pickup and normal patient self-care.
// Flow: read-only support relation -> exact Medic item -> reserve one pair -> choose DIRECT_CARE or SUPPLY_DROP -> rendezvous under existing movement/SAIN translation authority -> real EFT mutation -> exact outcome/readback -> release.
// Authority boundary: SAIN still owns individual combat/target/aim/fire; grenade, RunningToCover/direct lethal pressure, hard recovery and Medic self-care preempt. Ground handoff/pickup is never allowed in combat.
// Invariant: One session, one patient and one exact Medic item at a time. Supply requires exact world/ownership proof; direct care invokes native DoMedEffect at most once and scales its native duration by 0.50.
namespace Vanguard.Client.Runtime.Medical.Support;

internal static class VanguardMedicSupplyDropExecutor
{
    private static readonly object WorldSync = new();
    private static GameWorld? observedWorld;
    private static BotEventHandler.GDelegate15? throwHandler;
    private static bool bootLogged;
    private static DateTimeOffset nextAdmissionAtUtc = DateTimeOffset.MinValue;
    private static readonly object HandoffGuardSync = new();
    private static readonly Dictionary<string, PostHandoffGuard> RecentHandoffByPatient = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PostHandoffMinimumRetry = TimeSpan.FromSeconds(6.0d);
    private static readonly TimeSpan PostHandoffSelfCarePriority = TimeSpan.FromSeconds(15.0d);
    // A failed no-combat rendezvous must not immediately reserve the same two
    // actors and exact item again while the native/physical situation is unchanged.
    private static readonly Dictionary<string, DateTimeOffset> FailedSupplyRetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FailedSupplyRetryDelay = TimeSpan.FromSeconds(10d);
    private static readonly TimeSpan MaximumProgressingSupplyDuration = TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.SupplySessionProgressingAbsoluteMaxSeconds);
    private static readonly TimeSpan RecentPhysicalProgressGrace = TimeSpan.FromSeconds(6d);

    public static string StatusTag => VanguardMedicSupportDoctrine.SupplyDropStatusTag;

    public static void ResetForRaidLifecycle(string reason)
    {
        UnsubscribeWorld();
        DateTimeOffset resetAt = DateTimeOffset.UtcNow;
        if (VanguardMedicSupportSessionStore.TryGetActive(out VanguardMedicSupportSession activeSession)
            && activeSession.Mode == VanguardMedicSupportMode.SupplyDrop
            && activeSession.WorldItemObserved)
        {
            VanguardMedicTargetedPickupExecutor.ReleaseExactWorldItemReservation(activeSession, resetAt, "raid_reset:" + reason);
        }
        VanguardMedicRendezvousMovementService.ResetForRaidLifecycle(reason);
        VanguardMedicSupportSessionStore.ResetForRaidLifecycle(reason);
        bootLogged = false;
        nextAdmissionAtUtc = DateTimeOffset.MinValue;
        lock (HandoffGuardSync)
        {
            RecentHandoffByPatient.Clear();
            FailedSupplyRetryAfter.Clear();
        }
        VanguardClientDiagnosticsLog.Info(
            VanguardMedicSupportDoctrine.SessionStatusTag,
            $"VANGUARD_MEDIC_SUPPORT_RESET reason={Safe(reason)}; fullChain=true; supplyDrop=true; targetedPickup=true; directCare=true; combatRendezvous=true; directCareDurationScale={VanguardMedicSupportDoctrine.DirectCareDurationScale:0.00}; groundHandoffCombat=false");
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot>? snapshots, DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }

        EnsureWorldSubscription();
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                VanguardMedicSupportDoctrine.SessionStatusTag,
                $"VANGUARD_MEDIC_SUPPORT_BOOT enabled=true; trigger=MedicalAwaitItem; modes=SUPPLY_DROP,DIRECT_CARE; supplyPickup=targeted_exact_world_item; opportunityLoot=false; supplyCombat=false; directCareCombatCompatible=true; searchGroupSearchTranslation=true; nativeDoMedEffect=true; directCareDurationScale={VanguardMedicSupportDoctrine.DirectCareDurationScale:0.00}; selfCarePreferred=true; RestoreFullHealth=false");
        }

        OperatorDecisionSnapshot[] current = (snapshots ?? Array.Empty<OperatorDecisionSnapshot>())
            .Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .ToArray();

        if (VanguardMedicSupportSessionStore.TryGetActive(out VanguardMedicSupportSession active))
        {
            Advance(active, current, now);
            return;
        }

        if (now < nextAdmissionAtUtc)
        {
            return;
        }
        nextAdmissionAtUtc = now + TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.AdmissionIntervalSeconds);
        TryStart(current, now);
    }

    private static void TryStart(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        VanguardMedicSupportCandidateFact[] candidates = VanguardSquadMedicalSupportBoard.GetLatestFacts()
            .Where(fact => fact.AnyDryRunLaneEligible && fact.SameOwnerInitialScope)
            .Where(fact => fact.CapturedAtUtc != DateTimeOffset.MinValue
                && now - fact.CapturedAtUtc <= TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.CandidateFreshnessSeconds))
            .OrderByDescending(fact => fact.TriageScore)
            .ThenBy(fact => fact.CaregiverOperatorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(fact => fact.PatientOperatorId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (VanguardMedicSupportCandidateFact fact in candidates)
        {
            bool caregiverSnapshotOk = TryResolveSnapshot(snapshots, fact.CaregiverBotProfileId, now, out OperatorDecisionSnapshot caregiverSnapshot, out string caregiverSnapshotReason);
            bool patientSnapshotOk = TryResolveSnapshot(snapshots, fact.PatientBotProfileId, now, out OperatorDecisionSnapshot patientSnapshot, out string patientSnapshotReason);
            if (!caregiverSnapshotOk || !patientSnapshotOk)
            {
                LogAdmissionDenied(fact, "snapshot_not_current:caregiver=" + caregiverSnapshotReason + ":patient=" + patientSnapshotReason, now);
                continue;
            }

            if (!ValidatePatientNeed(fact.PatientNeed, fact.PatientTargetPart, patientSnapshot, out string needReason))
            {
                LogAdmissionDenied(fact, needReason, now);
                continue;
            }
            if (caregiverSnapshot.Medical.Need.HasAnyNeed)
            {
                LogAdmissionDenied(fact, "caregiver_self_medical_need_wins", now);
                continue;
            }
            // Only a qualified quiet supply lane may request a cooperative HardReturn yield.
            // Combat/direct-care remains under the original HardReturn veto.
            bool canYieldRegroupForSupply = fact.SupplyDropDryRunEligible
                && IsStrictNoCombatHandoff(caregiverSnapshot, patientSnapshot);
            bool caregiverSurvivalBlocked = IsHardSurvivalBlock(caregiverSnapshot, canYieldRegroupForSupply, out string caregiverSafety);
            bool patientSurvivalBlocked = IsHardSurvivalBlock(patientSnapshot, canYieldRegroupForSupply, out string patientSafety);
            if (caregiverSurvivalBlocked || patientSurvivalBlocked)
            {
                LogAdmissionDenied(fact, "survival_block:caregiver=" + caregiverSafety + ":patient=" + patientSafety, now);
                continue;
            }

            if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(fact.CaregiverBotProfileId, out VanguardRaidOperatorRuntimeRecord caregiverRuntime)
                || caregiverRuntime.BotOwner == null || caregiverRuntime.BotOwner.IsDead
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(fact.PatientBotProfileId, out VanguardRaidOperatorRuntimeRecord patientRuntime)
                || patientRuntime.BotOwner == null || patientRuntime.BotOwner.IsDead)
            {
                LogAdmissionDenied(fact, "runtime_pair_unbound_or_dead", now);
                continue;
            }

            // A patient still holding a native MedsController cannot safely
            // perform an item handoff/pickup. Do not trap the doctor in a pair
            // rendezvous while native medical hands are terminal but unreleased.
            if (VanguardMedicalHandsWatchdogService.IsMedicalAdmissionBlocked(fact.PatientBotProfileId, out string pendingHands)
                || patientRuntime.BotOwner.GetPlayer?.HandsController is Player.MedsController)
            {
                LogAdmissionDenied(fact, "patient_medical_hands_not_restored:" + Safe(pendingHands), now);
                continue;
            }

            if (!string.Equals(Normalize(caregiverRuntime.OwnerProfileId), Normalize(patientRuntime.OwnerProfileId), StringComparison.OrdinalIgnoreCase))
            {
                LogAdmissionDenied(fact, "same_owner_initial_scope_identity_changed", now);
                continue;
            }

            if (IsPostHandoffReAdmissionBlocked(fact, patientRuntime.BotOwner!, now, out string handoffGuardReason))
            {
                LogAdmissionDenied(fact, handoffGuardReason, now);
                continue;
            }

            if (!VanguardMedicSupportItemSelector.TrySelectExactItem(
                    caregiverRuntime.BotOwner,
                    fact,
                    out MedsItemClass item,
                    out float resource,
                    out float maxResource,
                    out string itemReason))
            {
                LogAdmissionDenied(fact, itemReason, now);
                continue;
            }

            string itemInstanceId = VanguardMedicalInventoryReader.ResolveItemInstanceId(item);
            bool combatContext = IsCombatContext(caregiverSnapshot) || IsCombatContext(patientSnapshot);
            bool strictNoCombat = IsStrictNoCombatHandoff(caregiverSnapshot, patientSnapshot);
            // Product doctrine: protected/combat support uses Direct Care; a genuinely quiet pair
            // uses the physical ground handoff so the patient owns the exact item and resumes its
            // existing self-care pipeline. Do not let need severity silently collapse the no-combat
            // supply lane into Direct Care.
            bool chooseDirectCare = fact.DirectCareDryRunEligible && combatContext;

            VanguardMedicSupportSession session;
            string reserveReason;
            if (chooseDirectCare)
            {
                if (!VanguardMedicSupportSessionStore.TryReserveDirectCare(fact, itemInstanceId, resource, maxResource, now, out session, out reserveReason))
                {
                    LogAdmissionDenied(fact, "direct_session_reservation_denied:" + reserveReason, now);
                    continue;
                }
                VanguardClientDiagnosticsLog.Info(
                    VanguardMedicSupportDoctrine.SessionStatusTag,
                    $"VANGUARD_MEDIC_SUPPORT_MODE_SELECTED session={Safe(session.SessionId)}; mode=DIRECT_CARE; need={fact.PatientNeed}; combatContext={Bool(combatContext)}; strictNoCombat={Bool(strictNoCombat)}; selectionPolicy=combat_direct_care; durationScale={VanguardMedicSupportDoctrine.DirectCareDurationScale:0.00}; groundHandoff=false");
                Advance(session, snapshots, now);
                return;
            }

            if (fact.SupplyDropDryRunEligible && strictNoCombat)
            {
                string retryIdentity = SupplyRetryKey(fact.CaregiverBotProfileId, fact.PatientBotProfileId,
                    fact.PatientNeed, fact.PatientTargetPart, itemInstanceId);
                lock (HandoffGuardSync)
                {
                    if (FailedSupplyRetryAfter.TryGetValue(retryIdentity, out DateTimeOffset retryAfter)
                        && now < retryAfter)
                    {
                        LogAdmissionDenied(fact, "same_pair_exact_item_failed_cooldown", now);
                        continue;
                    }
                    FailedSupplyRetryAfter.Remove(retryIdentity);
                }
                if (!VanguardMedicSupportSessionStore.TryReserveSupplyDrop(fact, itemInstanceId, resource, maxResource, now, out session, out reserveReason))
                {
                    LogAdmissionDenied(fact, "supply_session_reservation_denied:" + reserveReason, now);
                    continue;
                }
                VanguardClientDiagnosticsLog.Info(
                    VanguardMedicSupportDoctrine.SessionStatusTag,
                    $"VANGUARD_MEDIC_SUPPORT_MODE_SELECTED session={Safe(session.SessionId)}; mode=SUPPLY_DROP; need={fact.PatientNeed}; combatContext=false; strictNoCombat=true; selectionPolicy=quiet_ground_handoff; targetedPickup=true; opportunityLoot=false");
                Advance(session, snapshots, now);
                return;
            }

            if (fact.DirectCareDryRunEligible)
            {
                if (!VanguardMedicSupportSessionStore.TryReserveDirectCare(fact, itemInstanceId, resource, maxResource, now, out session, out reserveReason))
                {
                    LogAdmissionDenied(fact, "direct_session_reservation_denied:" + reserveReason, now);
                    continue;
                }
                VanguardClientDiagnosticsLog.Info(
                    VanguardMedicSupportDoctrine.SessionStatusTag,
                    $"VANGUARD_MEDIC_SUPPORT_MODE_SELECTED session={Safe(session.SessionId)}; mode=DIRECT_CARE; need={fact.PatientNeed}; combatContext={Bool(combatContext)}; strictNoCombat={Bool(strictNoCombat)}; selectionPolicy=supply_not_safe_direct_fallback; durationScale={VanguardMedicSupportDoctrine.DirectCareDurationScale:0.00}");
                Advance(session, snapshots, now);
                return;
            }

            LogAdmissionDenied(fact, combatContext ? "combat_waits_for_direct_care_safety" : "no_executable_medic_support_lane", now);
        }
    }

    private static void Advance(VanguardMedicSupportSession session, IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (session.Mode == VanguardMedicSupportMode.DirectCare)
        {
            VanguardMedicDirectCareExecutor.TryAdvance(session, snapshots, now, out bool terminal, out string outcome, out string reason);
            if (terminal)
            {
                FinishSession(session, outcome, reason, now);
            }
            return;
        }

        AdvanceSupply(session, snapshots, now);
    }

    private static void AdvanceSupply(VanguardMedicSupportSession session, IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (session.StartedAtUtc != DateTimeOffset.MinValue)
        {
            TimeSpan elapsed = now - session.StartedAtUtc;
            bool recentMeasuredMovement = session.LastPhysicalPairProgressAtUtc != DateTimeOffset.MinValue
                && now >= session.LastPhysicalPairProgressAtUtc
                && now - session.LastPhysicalPairProgressAtUtc <= RecentPhysicalProgressGrace;
            if (elapsed > MaximumProgressingSupplyDuration
                || (elapsed > TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.SupplySessionMaxSeconds)
                    && !recentMeasuredMovement))
            {
                FinishSession(session, "Timeout", recentMeasuredMovement
                    ? "supply_progressing_absolute_deadline"
                    : "supply_session_max_elapsed_without_recent_physical_progress", now);
                return;
            }
        }
        bool caregiverSnapshotOk = TryResolveSnapshot(snapshots, session.CaregiverBotProfileId, now, out OperatorDecisionSnapshot caregiverSnapshot, out string caregiverSnapshotReason);
        bool patientSnapshotOk = TryResolveSnapshot(snapshots, session.PatientBotProfileId, now, out OperatorDecisionSnapshot patientSnapshot, out string patientSnapshotReason);
        if (!caregiverSnapshotOk || !patientSnapshotOk)
        {
            FinishSession(session, "Interrupted", "supply_pair_snapshot_not_current:caregiver=" + caregiverSnapshotReason + ":patient=" + patientSnapshotReason, now);
            return;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.CaregiverBotProfileId, out VanguardRaidOperatorRuntimeRecord caregiverRuntime)
            || caregiverRuntime.BotOwner == null || caregiverRuntime.BotOwner.IsDead
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.PatientBotProfileId, out VanguardRaidOperatorRuntimeRecord patientRuntime)
            || patientRuntime.BotOwner == null || patientRuntime.BotOwner.IsDead)
        {
            FinishSession(session, "Interrupted", "supply_runtime_pair_unbound_or_dead", now);
            return;
        }
        // Admission/self-care facts are authoritative only before the item leaves the Medic. Once
        // the exact world item exists, the handoff transaction owns completion and exact patient
        // ownership must be allowed to become visible without being preempted by RequiredItemAvailable.
        if (!session.WorldItemObserved)
        {
            if (!ValidatePatientNeed(session.Need, session.TargetPart, patientSnapshot, out string needReason))
            {
                FinishSession(session, needReason.StartsWith("patient_need_resolved", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Interrupted", needReason, now);
                return;
            }
            if (caregiverSnapshot.Medical.Need.HasAnyNeed)
            {
                FinishSession(session, "Interrupted", "caregiver_self_medical_need_wins", now);
                return;
            }
        }

        if (session.WorldItemObserved)
        {
            if (session.NextDonorReadbackAtUtc != DateTimeOffset.MinValue && now < session.NextDonorReadbackAtUtc)
            {
                return;
            }

            bool ownerMatches = string.Equals(Normalize(session.WorldItemLastOwnerProfileId), Normalize(session.CaregiverBotProfileId), StringComparison.OrdinalIgnoreCase);
            bool donorStillOwns = VanguardMedicSupportItemSelector.ContainsExactItem(caregiverRuntime.BotOwner, session.ItemInstanceId);
            if (!ownerMatches)
            {
                FinishSession(session, "Failed", "world_item_last_owner_mismatch:" + Safe(session.WorldItemLastOwnerProfileId), now);
                return;
            }
            if (donorStillOwns)
            {
                if (now - session.WorldItemObservedAtUtc > TimeSpan.FromSeconds(0.75d) || session.DonorReadbackAttempts >= 3)
                {
                    FinishSession(session, "Failed", "world_item_observed_but_donor_inventory_still_owns_exact_item", now);
                }
                else
                {
                    VanguardMedicSupportSessionStore.TryDeferDonorReadback(session.SessionId, now + TimeSpan.FromSeconds(0.20d), now);
                }
                return;
            }

            if (!string.Equals(session.Phase, "AwaitPatientPickup", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(session.Phase, "PickupRequested", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(session.Phase, "AwaitPatientPickupApproach", StringComparison.OrdinalIgnoreCase))
            {
                VanguardMainIntentScheduler.TryCloseMedicSupportSupplyDrop(
                    session.CaregiverBotProfileId,
                    session.PrimaryWindowId,
                    "Completed",
                    "physical_drop_verified_handoff_to_targeted_pickup",
                    session.Summary,
                    now);
                VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "AwaitPatientPickup", now);
                VanguardClientDiagnosticsLog.Info(
                    VanguardMedicSupportDoctrine.SupplyDropWorldProofTag,
                    $"MEDIC_SUPPLY_DROP_PHYSICAL_ITEM_OK session={Safe(session.SessionId)}; caregiverBot={Safe(session.CaregiverBotProfileId)}; patientBot={Safe(session.PatientBotProfileId)}; need={session.Need}; item={Safe(session.ItemName)}; tpl={Safe(session.ItemTemplateId)}; itemInstance={Safe(session.ItemInstanceId)}; lastOwner={Safe(session.WorldItemLastOwnerProfileId)}; donorInventoryReleased=true; targetedPickupNext=true; opportunityLoot=false; healthMutation=false");
            }

            VanguardMedicTargetedPickupExecutor.TryAdvance(session, snapshots, now, out bool terminal, out string outcome, out string pickupReason);
            if (terminal)
            {
                FinishSession(session, outcome, pickupReason, now);
            }
            return;
        }

        if (!IsStrictNoCombatHandoff(caregiverSnapshot, patientSnapshot))
        {
            VanguardMedicRendezvousMovementService.ReleaseSession(session.SessionId, now, "supply_waits_for_no_combat");
            VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "WaitingForNoCombatHandoff", now);
            return;
        }

        if (session.DropRequestedAtUtc != DateTimeOffset.MinValue)
        {
            if (now - session.DropRequestedAtUtc > TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.WorldObservationTimeoutSeconds))
            {
                bool donorStillOwns = VanguardMedicSupportItemSelector.ContainsExactItem(caregiverRuntime.BotOwner, session.ItemInstanceId);
                FinishSession(
                    session,
                    donorStillOwns ? "Timeout" : "Failed",
                    donorStillOwns ? "native_drop_world_event_timeout_item_still_owned" : "native_drop_item_left_inventory_without_exact_world_event",
                    now);
            }
            return;
        }

        // A pair may not preempt a productive HOLD/TC path for an item that
        // has already disappeared or been exhausted. Exact native item is
        // checked before the atomic scheduler reservation.
        if (!VanguardMedicSupportItemSelector.TryFindExactItem(
                caregiverRuntime.BotOwner, session.ItemInstanceId, out MedsItemClass exactReservedItem,
                out float availableResource, out float availableMaximum)
            || !VanguardMedicSupportItemSelector.IsResourceViable(availableResource, availableMaximum)
            || !string.Equals(
                VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(exactReservedItem.StringTemplateId),
                VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(session.ItemTemplateId),
                StringComparison.OrdinalIgnoreCase))
        {
            FinishSession(session, "Interrupted", "reserved_exact_medical_item_missing_before_pair_commit", now);
            return;
        }

        if (!TryStabilizeSupplyPair(session, caregiverSnapshot, patientSnapshot, caregiverRuntime.BotOwner, patientRuntime.BotOwner, now, out string pairReason))
        {
            if (pairReason.StartsWith("survival_preempts", StringComparison.OrdinalIgnoreCase))
            {
                FinishSession(session, "Interrupted", pairReason, now);
            }
            return;
        }

        if (caregiverRuntime.BotOwner.ItemDropper != null
            && (caregiverRuntime.BotOwner.ItemDropper.NextTimeDrop > Time.time
                || caregiverRuntime.BotOwner.ItemDropper.ItemToDropNext != null))
        {
            VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "AwaitNativeDropperReady", now);
            return;
        }

        if (!VanguardMainIntentScheduler.TryOpenMedicSupportSupplyDrop(
                caregiverSnapshot,
                FindCurrentFact(session, caregiverSnapshot, patientSnapshot),
                session.ItemInstanceId,
                now,
                out string windowId,
                out string windowReason))
        {
            FinishSession(session, "Interrupted", "supply_primary_window_denied:" + windowReason, now);
            return;
        }
        if (!VanguardMedicSupportSessionStore.TryAttachPrimaryWindow(session.SessionId, windowId, now))
        {
            VanguardMainIntentScheduler.TryCloseMedicSupportSupplyDrop(session.CaregiverBotProfileId, windowId, "Interrupted", "session_window_attach_failed", session.Summary, now);
            FinishSession(session, "Interrupted", "session_window_attach_failed", now);
            return;
        }
        if (!TryRequestNativeDrop(session, caregiverRuntime.BotOwner, caregiverSnapshot, patientSnapshot, now, out string dropReason))
        {
            if (string.Equals(dropReason, "native_item_dropper_not_ready", StringComparison.OrdinalIgnoreCase))
            {
                // ItemDropper readiness is a short-lived native transport state, not a reason to
                // destroy the pair reservation and immediately create another Supply session.
                VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "AwaitNativeDropperReady", now);
                return;
            }
            FinishSession(session, "Interrupted", dropReason, now);
        }
    }

    private static VanguardMedicSupportCandidateFact FindCurrentFact(
        VanguardMedicSupportSession session,
        OperatorDecisionSnapshot caregiver,
        OperatorDecisionSnapshot patient)
    {
        return VanguardSquadMedicalSupportBoard.GetLatestFacts()
            .Where(fact => fact != null
                && string.Equals(fact.CaregiverBotProfileId, session.CaregiverBotProfileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fact.PatientBotProfileId, session.PatientBotProfileId, StringComparison.OrdinalIgnoreCase)
                && fact.PatientNeed == session.Need)
            .OrderByDescending(fact => fact.CapturedAtUtc)
            .FirstOrDefault()
            ?? new VanguardMedicSupportCandidateFact
            {
                CaregiverOperatorId = caregiver.OperatorId,
                CaregiverBotProfileId = caregiver.BotProfileId,
                CaregiverOwnerProfileId = caregiver.OwnerProfileId,
                PatientOperatorId = patient.OperatorId,
                PatientBotProfileId = patient.BotProfileId,
                PatientOwnerProfileId = patient.OwnerProfileId,
                PatientNeed = session.Need,
                PatientTargetPart = session.TargetPart,
                CandidateItemTemplateId = session.ItemTemplateId,
                CandidateItemName = session.ItemName,
                SupplyDropDryRunEligible = true,
                SameOwnerInitialScope = true,
                DirectDistanceMeters = Vector3.Distance(caregiver.Position, patient.Position),
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
    }

    private static bool TryStabilizeSupplyPair(
        VanguardMedicSupportSession session,
        OperatorDecisionSnapshot caregiver,
        OperatorDecisionSnapshot patient,
        BotOwner caregiverOwner,
        BotOwner patientOwner,
        DateTimeOffset now,
        out string reason)
    {
        float distance = HorizontalDistance(caregiver.Position, patient.Position);
        float caregiverSpeed = Math.Max(caregiver.RealSpeed, caregiver.Movement.RealSpeed);
        float patientSpeed = Math.Max(patient.RealSpeed, patient.Movement.RealSpeed);
        if (distance <= VanguardMedicSupportDoctrine.NativePartnerAdmissionMeters
            && caregiverSpeed <= VanguardMedicSupportDoctrine.StationarySpeedMaxMetersPerSecond
            && patientSpeed <= VanguardMedicSupportDoctrine.StationarySpeedMaxMetersPerSecond)
        {
            if (!VanguardMainIntentScheduler.TryEnsureMedicSupportPairPrimary(
                    session, caregiver, patient, now, out reason)) return false;
            VanguardMedicRendezvousMovementService.ReleaseSession(session.SessionId, now, "supply_pair_stabilized", preservePrimaryWindows: true);
            VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "SupplyPairStabilized", now);
            reason = "supply_pair_stabilized";
            return true;
        }

        if (!VanguardMedicalRendezvousPlanner.TryResolve(caregiver, patient, out VanguardMedicalRendezvousPlan plan, out string planReason))
        {
            reason = "supply_rendezvous_plan_failed:" + Safe(planReason);
            return false;
        }

        // Validate both physical routes before replacing either actor's primary.
        // A failed route cannot suspend a still-productive TA/TC movement.
        if (plan.MoveCaregiver && !VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                caregiver.Position, plan.CaregiverDestination, out _, out _, out string caregiverPathReason))
        {
            reason = "pair_caregiver_path_not_ready:" + Safe(caregiverPathReason);
            return false;
        }
        if (plan.MovePatient && !VanguardTacticalPlacementSolver.TryCalculatePreparedRuntimePath(
                patient.Position, plan.PatientDestination, out _, out _, out string patientPathReason))
        {
            reason = "pair_patient_path_not_ready:" + Safe(patientPathReason);
            return false;
        }
        if (!VanguardMainIntentScheduler.TryEnsureMedicSupportPairPrimary(
                session, caregiver, patient, now, out reason)) return false;

        bool caregiverArrived = !plan.MoveCaregiver;
        bool patientArrived = !plan.MovePatient;

        // A protected actor used as the rendezvous anchor must actually cooperate with the Medic.
        // Without a bounded hold, SAIN/TC can immediately walk the patient away while the caregiver
        // keeps chasing a moving "safe" anchor. This hold exists only in the strict no-combat Supply
        // lane and is released by the normal session/window lifecycle.
        if (!plan.MoveCaregiver
            && !VanguardMedicRendezvousMovementService.TryHoldStationary(
                session.SessionId,
                caregiver,
                caregiverOwner,
                "caregiver_rendezvous_hold",
                now,
                out string caregiverHoldReason))
        {
            reason = caregiverHoldReason;
            return false;
        }
        if (!plan.MovePatient
            && !VanguardMedicRendezvousMovementService.TryHoldStationary(
                session.SessionId,
                patient,
                patientOwner,
                "patient_rendezvous_hold",
                now,
                out string patientHoldReason))
        {
            reason = patientHoldReason;
            return false;
        }

        if (plan.MoveCaregiver
            && !VanguardMedicRendezvousMovementService.TryMove(
                session.SessionId,
                caregiver,
                caregiverOwner,
                "caregiver",
                plan.CaregiverDestination,
                1.10f,
                now,
                allowCombatTranslation: false,
                out caregiverArrived,
                out string caregiverReason))
        {
            reason = caregiverReason;
            return false;
        }
        if (plan.MovePatient
            && !VanguardMedicRendezvousMovementService.TryMove(
                session.SessionId,
                patient,
                patientOwner,
                "patient",
                plan.PatientDestination,
                1.10f,
                now,
                allowCombatTranslation: false,
                out patientArrived,
                out string patientReason))
        {
            reason = patientReason;
            return false;
        }

        // When one actor reaches a two-sided rendezvous first, keep that actor at the reached
        // anchor while the partner completes the approach. Otherwise the first arrival can resume
        // SAIN/TC locomotion immediately and turn a valid midpoint rendezvous back into a chase.
        if (plan.MoveCaregiver && caregiverArrived
            && !VanguardMedicRendezvousMovementService.TryHoldStationary(
                session.SessionId,
                caregiver,
                caregiverOwner,
                "caregiver_arrived_hold",
                now,
                out string caregiverArrivedHoldReason))
        {
            reason = caregiverArrivedHoldReason;
            return false;
        }
        if (plan.MovePatient && patientArrived
            && !VanguardMedicRendezvousMovementService.TryHoldStationary(
                session.SessionId,
                patient,
                patientOwner,
                "patient_arrived_hold",
                now,
                out string patientArrivedHoldReason))
        {
            reason = patientArrivedHoldReason;
            return false;
        }

        VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "SupplyRendezvous", now);
        reason = "supply_rendezvous_in_progress:" + Safe(plan.Source)
            + ":caregiverArrived=" + Bool(caregiverArrived)
            + ":patientArrived=" + Bool(patientArrived);
        return false;
    }

    private static bool TryRequestNativeDrop(
        VanguardMedicSupportSession session,
        BotOwner caregiver,
        OperatorDecisionSnapshot caregiverSnapshot,
        OperatorDecisionSnapshot patientSnapshot,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        if (!IsStrictNoCombatHandoff(caregiverSnapshot, patientSnapshot))
        {
            reason = "ground_handoff_forbidden_in_combat";
            return false;
        }
        if (VanguardExecutionLeaseStore.TryGetActive(session.CaregiverBotProfileId, out VanguardExecutionLeaseState activeLease))
        {
            reason = "caregiver_self_medical_execution_lease_active:" + Safe(activeLease.LeaseId);
            return false;
        }
        if (!VanguardMedicSupportItemSelector.TryFindExactItem(caregiver, session.ItemInstanceId, out MedsItemClass exactItem, out float currentResource, out float currentMaxResource))
        {
            reason = "reserved_exact_item_missing_before_drop";
            return false;
        }
        if (!string.Equals(
                VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(exactItem.StringTemplateId),
                VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(session.ItemTemplateId),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "reserved_exact_item_template_changed";
            return false;
        }
        if (!VanguardMedicSupportItemSelector.IsResourceViable(currentResource, currentMaxResource))
        {
            reason = "reserved_exact_item_resource_exhausted";
            return false;
        }
        if (session.InitialItemResource >= 0f && currentResource >= 0f && Math.Abs(session.InitialItemResource - currentResource) > 0.01f)
        {
            reason = "reserved_exact_item_resource_changed_before_commit";
            return false;
        }

        EnsureWorldSubscription();
        if (observedWorld == null || caregiver.ItemDropper == null)
        {
            reason = observedWorld == null ? "game_world_unavailable_for_exact_drop_proof" : "native_item_dropper_missing";
            return false;
        }
        if (caregiver.ItemDropper.NextTimeDrop > Time.time || caregiver.ItemDropper.ItemToDropNext != null)
        {
            reason = "native_item_dropper_not_ready";
            return false;
        }
        if (!VanguardMedicSupportSessionStore.TryMarkDropRequested(session.SessionId, now))
        {
            reason = "session_drop_request_state_lost";
            return false;
        }

        VanguardMainIntentScheduler.TryMarkMedicSupportSupplyDropProgress(session.CaregiverBotProfileId, session.PrimaryWindowId, "native_drop_requested", now);
        caregiver.ItemDropper.SetItemToDropNext(exactItem);
        caregiver.ItemDropper.TryDoDrop();
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDIC_SUPPLY_DROP_NATIVE_REQUEST session={Safe(session.SessionId)}; caregiverBot={Safe(session.CaregiverBotProfileId)}; patientBot={Safe(session.PatientBotProfileId)}; need={session.Need}; target={Safe(session.TargetPart)}; item={Safe(session.ItemName)}; tpl={Safe(session.ItemTemplateId)}; itemInstance={Safe(session.ItemInstanceId)}; resource={currentResource:0.0}/{currentMaxResource:0.0}; distance={HorizontalDistance(caregiverSnapshot.Position, patientSnapshot.Position):0.00}; nativeDropper=true; groundHandoffCombat=false; targetedPickupNext=true; worldProofPending=true");
        reason = "native_drop_requested_world_proof_pending";
        return true;
    }

    internal static void RegisterSuccessfulSupplyHandoff(VanguardMedicSupportSession session, DateTimeOffset now)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.PatientBotProfileId))
        {
            return;
        }

        var guard = new PostHandoffGuard
        {
            PatientBotProfileId = Normalize(session.PatientBotProfileId),
            Need = session.Need,
            TargetPart = Safe(session.TargetPart),
            ItemInstanceId = Normalize(session.ItemInstanceId),
            MinimumRetryAtUtc = now + PostHandoffMinimumRetry,
            SelfCarePriorityUntilUtc = now + PostHandoffSelfCarePriority
        };
        lock (HandoffGuardSync)
        {
            RecentHandoffByPatient[guard.PatientBotProfileId] = guard;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardMedicSupportDoctrine.SessionStatusTag,
            $"VANGUARD_MEDIC_POST_HANDOFF_GUARD patient={Safe(guard.PatientBotProfileId)}; need={guard.Need}; target={Safe(guard.TargetPart)}; itemInstance={Safe(guard.ItemInstanceId)}; minimumRetrySeconds={PostHandoffMinimumRetry.TotalSeconds:0.00}; selfCarePrioritySeconds={PostHandoffSelfCarePriority.TotalSeconds:0.00}; blocksDuplicateSupplyWhileExactItemOwned=true; staleCandidateSuppressed=true");
    }

    internal static bool HasPostHandoffSelfCareSettlementPriority(
        string? patientBotProfileId,
        VanguardMedicalNeed currentNeed,
        string? currentTargetPart,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        string patientKey = Normalize(patientBotProfileId);
        if (string.Equals(patientKey, "none", StringComparison.OrdinalIgnoreCase) || currentNeed == VanguardMedicalNeed.None)
        {
            return false;
        }

        PostHandoffGuard? guard;
        lock (HandoffGuardSync)
        {
            RecentHandoffByPatient.TryGetValue(patientKey, out guard);
        }
        if (guard == null
            || guard.Need != currentNeed
            || !string.Equals(Safe(guard.TargetPart), Safe(currentTargetPart), StringComparison.OrdinalIgnoreCase)
            || now > guard.SelfCarePriorityUntilUtc)
        {
            return false;
        }

        reason = "post_handoff_self_care_settlement:need=" + guard.Need
            + ":target=" + Safe(guard.TargetPart)
            + ":item=" + Safe(guard.ItemInstanceId)
            + ":until=" + guard.SelfCarePriorityUntilUtc.ToString("O", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool IsPostHandoffReAdmissionBlocked(
        VanguardMedicSupportCandidateFact fact,
        BotOwner patientOwner,
        DateTimeOffset now,
        out string reason)
    {
        reason = "none";
        string patientKey = Normalize(fact.PatientBotProfileId);
        PostHandoffGuard? guard;
        lock (HandoffGuardSync)
        {
            RecentHandoffByPatient.TryGetValue(patientKey, out guard);
        }
        if (guard == null
            || guard.Need != fact.PatientNeed
            || !string.Equals(Safe(guard.TargetPart), Safe(fact.PatientTargetPart), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool patientStillOwnsExact = VanguardMedicSupportItemSelector.ContainsExactItem(patientOwner, guard.ItemInstanceId);
        if (patientStillOwnsExact)
        {
            reason = "post_handoff_exact_item_still_owned_wait_for_self_care";
            return true;
        }
        if (now < guard.MinimumRetryAtUtc)
        {
            reason = "post_handoff_result_settle_before_retry";
            return true;
        }

        lock (HandoffGuardSync)
        {
            if (RecentHandoffByPatient.TryGetValue(patientKey, out PostHandoffGuard? current)
                && ReferenceEquals(current, guard))
            {
                RecentHandoffByPatient.Remove(patientKey);
            }
        }
        return false;
    }

    private static bool ValidatePatientNeed(VanguardMedicalNeed expectedNeed, string expectedTargetPart, OperatorDecisionSnapshot patient, out string reason)
    {
        if (!patient.Alive || !patient.Medical.Alive || !patient.Medical.Need.IsReadable || !patient.Medical.Plan.Readable)
        {
            reason = "patient_medical_truth_unreadable_or_dead";
            return false;
        }
        if (!patient.Medical.Need.HasAnyNeed)
        {
            reason = "patient_need_resolved_no_need";
            return false;
        }
        if (patient.Medical.Need.DominantNeed != expectedNeed)
        {
            reason = "patient_need_resolved_or_changed:" + patient.Medical.Need.DominantNeed;
            return false;
        }
        if (!string.Equals(Safe(patient.Medical.Need.TargetPart), Safe(expectedTargetPart), StringComparison.OrdinalIgnoreCase))
        {
            reason = "patient_target_part_changed";
            return false;
        }
        if (patient.Medical.Actionability.RequiredItemAvailable)
        {
            reason = "patient_now_has_self_treatment_item";
            return false;
        }
        bool waiting = string.Equals(patient.Medical.Plan.PlanKey, "MedicalAwaitItem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(patient.Medical.Plan.NextStep, "ObserveMedicalItemMissingReadOnly", StringComparison.OrdinalIgnoreCase);
        if (!waiting)
        {
            reason = "patient_no_longer_waiting_for_missing_item";
            return false;
        }
        reason = "patient_need_current";
        return true;
    }

    private static bool TryResolveSnapshot(
        IReadOnlyList<OperatorDecisionSnapshot> snapshots,
        string botProfileId,
        DateTimeOffset now,
        out OperatorDecisionSnapshot snapshot,
        out string reason)
    {
        snapshot = snapshots
            .Where(candidate => candidate != null && string.Equals(candidate.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.CapturedAtUtc)
            .FirstOrDefault()!;
        if (snapshot == null)
        {
            reason = "missing";
            return false;
        }
        if (!VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, now, out reason))
        {
            reason = "stale:" + reason;
            return false;
        }
        reason = "fresh";
        return true;
    }

    private static bool IsHardSurvivalBlock(OperatorDecisionSnapshot snapshot, bool allowRegroupForSupply, out string reason)
    {
        if (!snapshot.Alive)
        {
            reason = "dead";
            return true;
        }
        if (snapshot.Sain.RunningToCover == true)
        {
            reason = "sain_running_to_cover";
            return true;
        }
        bool unprotectedIncomingFire = snapshot.Medical.Safety.IncomingFireRecent
            && !snapshot.Medical.Safety.CoveredSuppressionOpportunity;
        if (snapshot.Threat.DirectThreat
            || snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.ImmediateCombatBlock
            || unprotectedIncomingFire)
        {
            reason = "direct_or_unprotected_incoming_fire";
            return true;
        }
        // Quiet, executable supply may ask the scheduler to yield geometric
        // HardReturn; admission alone never grants physical Medical authority.
        // Direct care and all danger vetoes retain their existing precedence.
        if (snapshot.PrimaryExecution.Active
            && (string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion, StringComparison.OrdinalIgnoreCase)
                || (!allowRegroupForSupply
                    && string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.HardReturnMovement, StringComparison.OrdinalIgnoreCase))))
        {
            reason = "exclusive_survival_primary:" + Safe(snapshot.PrimaryExecution.WindowKind);
            return true;
        }
        reason = "none";
        return false;
    }

    private static bool IsCombatContext(OperatorDecisionSnapshot snapshot)
        => snapshot.Sain.IsInCombat == true
            || snapshot.Threat.ResidualThreat
            || snapshot.ThreatScan.CombatContext
            || (snapshot.PrimaryExecution.Active
                && string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase));

    private static bool IsStrictNoCombatHandoff(OperatorDecisionSnapshot caregiver, OperatorDecisionSnapshot patient)
        => !IsCombatContext(caregiver)
            && !IsCombatContext(patient)
            && !caregiver.Threat.DirectThreat
            && !patient.Threat.DirectThreat
            && !caregiver.Medical.Safety.IncomingFireRecent
            && !patient.Medical.Safety.IncomingFireRecent
            && caregiver.Sain.RunningToCover != true
            && patient.Sain.RunningToCover != true;

    private static void EnsureWorldSubscription()
    {
        GameWorld? current = null;
        try { current = Singleton<GameWorld>.Instance; } catch { current = null; }
        lock (WorldSync)
        {
            if (ReferenceEquals(observedWorld, current)) return;
            if (observedWorld != null && throwHandler != null) observedWorld.OnThrowItem -= throwHandler;
            observedWorld = current;
            throwHandler = null;
            if (observedWorld != null)
            {
                throwHandler = new BotEventHandler.GDelegate15(OnWorldItemThrown);
                observedWorld.OnThrowItem += throwHandler;
            }
        }
    }

    private static void UnsubscribeWorld()
    {
        lock (WorldSync)
        {
            if (observedWorld != null && throwHandler != null) observedWorld.OnThrowItem -= throwHandler;
            observedWorld = null;
            throwHandler = null;
        }
    }

    private static void OnWorldItemThrown(LootItem lootItem)
    {
        try
        {
            if (lootItem == null || lootItem.Item == null) return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!VanguardMedicSupportSessionStore.TryObserveWorldItem(lootItem, now, out VanguardMedicSupportSession session)) return;
            string reservationSummary = "patient_runtime_unavailable";
            if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.PatientBotProfileId, out VanguardRaidOperatorRuntimeRecord patientRuntime)
                && patientRuntime.BotOwner != null && !patientRuntime.BotOwner.IsDead)
            {
                reservationSummary = VanguardMedicTargetedPickupExecutor.ProtectExactWorldItemReservation(session, patientRuntime.BotOwner, now);
            }
            VanguardMainIntentScheduler.TryMarkMedicSupportSupplyDropProgress(session.CaregiverBotProfileId, session.PrimaryWindowId, "exact_world_item_observed", now);
            VanguardClientDiagnosticsLog.Info(
                VanguardMedicSupportDoctrine.SupplyDropWorldProofTag,
                $"VANGUARD_MEDIC_SUPPLY_DROP_WORLD_ITEM_OBSERVED session={Safe(session.SessionId)}; caregiverBot={Safe(session.CaregiverBotProfileId)}; patientBot={Safe(session.PatientBotProfileId)}; itemInstance={Safe(session.ItemInstanceId)}; tpl={Safe(session.ItemTemplateId)}; lastOwner={Safe(session.WorldItemLastOwnerProfileId)}; exactItem=true; physicalWorldProof=true; targetedPickupPending=true; reservation={Safe(reservationSummary)}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardMedicSupportDoctrine.SupplyDropWorldProofTag,
                $"VANGUARD_MEDIC_SUPPLY_DROP_WORLD_OBSERVER_FAILED reason={exception.GetType().Name}:{Safe(exception.Message)}; failClosed=true");
        }
    }

    private static void FinishSession(VanguardMedicSupportSession session, string outcome, string reason, DateTimeOffset now)
    {
        if (session.Mode == VanguardMedicSupportMode.SupplyDrop && session.WorldItemObserved)
        {
            VanguardMedicTargetedPickupExecutor.ReleaseExactWorldItemReservation(session, now, "session_terminal:" + reason);
        }
        if (session.Mode == VanguardMedicSupportMode.SupplyDrop
            && !session.WorldItemObserved
            && (string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)
                || reason.IndexOf("identity_mismatch", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            lock (HandoffGuardSync)
            {
                FailedSupplyRetryAfter[SupplyRetryKey(session.CaregiverBotProfileId,
                    session.PatientBotProfileId, session.Need, session.TargetPart,
                    session.ItemInstanceId)] = now + FailedSupplyRetryDelay;
            }
        }
        VanguardMedicRendezvousMovementService.ReleaseSession(session.SessionId, now, "session_terminal:" + reason);
        if (!string.Equals(Normalize(session.PrimaryWindowId), "none", StringComparison.OrdinalIgnoreCase))
        {
            VanguardMainIntentScheduler.TryCloseMedicSupportSupplyDrop(session.CaregiverBotProfileId, session.PrimaryWindowId, outcome, reason, session.Summary, now);
        }
        VanguardMainIntentScheduler.TryCloseMedicSupportWindowsForSession(
            session.SessionId,
            session.CaregiverBotProfileId,
            session.PatientBotProfileId,
            outcome,
            reason,
            session.Summary,
            now);
        if (VanguardMedicSupportSessionStore.TryFinish(session.SessionId, outcome, reason, out VanguardMedicSupportSession finished))
        {
            VanguardClientDiagnosticsLog.Info(
                VanguardMedicSupportDoctrine.SessionStatusTag,
                $"VANGUARD_MEDIC_SUPPORT_SESSION_TERMINAL {finished.Summary}; movementReleased=true; targetedPickup={Bool(finished.PickupConfirmed)}; directCare={Bool(finished.DirectCareCommitted)}; durationScale={VanguardMedicSupportDoctrine.DirectCareDurationScale:0.00}; persistentMutation=false");
        }
    }

    private static string SupplyRetryKey(string caregiver, string patient, VanguardMedicalNeed need,
        string bodyPart, string itemInstance)
        => Normalize(caregiver) + ":" + Normalize(patient) + ":" + need
            + ":" + Normalize(bodyPart) + ":" + Normalize(itemInstance);

    private sealed class PostHandoffGuard
    {
        public string PatientBotProfileId { get; init; } = "none";
        public VanguardMedicalNeed Need { get; init; } = VanguardMedicalNeed.None;
        public string TargetPart { get; init; } = "none";
        public string ItemInstanceId { get; init; } = "none";
        public DateTimeOffset MinimumRetryAtUtc { get; init; } = DateTimeOffset.MinValue;
        public DateTimeOffset SelfCarePriorityUntilUtc { get; init; } = DateTimeOffset.MinValue;
    }

    private static void LogAdmissionDenied(VanguardMedicSupportCandidateFact fact, string reason, DateTimeOffset now)
    {
        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardMedicSupportDoctrine.SessionStatusTag,
            () => $"VANGUARD_MEDIC_SUPPORT_ADMISSION_DENIED caregiverBot={Safe(fact.CaregiverBotProfileId)}; patientBot={Safe(fact.PatientBotProfileId)}; need={fact.PatientNeed}; template={Safe(fact.CandidateItemTemplateId)}; reason={Safe(reason)}; at={now:O}");
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

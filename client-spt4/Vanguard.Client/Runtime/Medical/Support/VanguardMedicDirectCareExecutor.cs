#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Medical.Execution;

// Responsibility: Executes direct teammate treatment for one reserved Medic Support pair using the Medic's exact real EFT medical item.
// Flow: Resolve a protected pair rendezvous -> move one/both actors under Medic/SAIN-compatible authority -> stabilize -> exactly-once patient DoMedEffect with the Medic-owned item -> halve native effect duration -> verify real item resource and patient outcome.
// Authority boundary: Does not use RestoreFullHealth, does not invent wound/item truth, does not override grenade/true survival movement and fails closed if cross-actor EFT semantics reject the real item.
// Invariant: One session may invoke patient DoMedEffect at most once; the 0.50 duration scale changes only the native effect timing and never adds health/effects outside EFT's own MedEffect transaction.
namespace Vanguard.Client.Runtime.Medical.Support;

internal static class VanguardMedicDirectCareExecutor
{
    public static string StatusTag => VanguardMedicSupportDoctrine.DirectCareStatusTag;

    public static bool TryAdvance(
        VanguardMedicSupportSession session,
        IReadOnlyList<OperatorDecisionSnapshot> snapshots,
        DateTimeOffset now,
        out bool terminal,
        out string outcome,
        out string reason)
    {
        terminal = false;
        outcome = "none";
        reason = "none";
        if (session == null || session.Mode != VanguardMedicSupportMode.DirectCare)
        {
            terminal = true;
            outcome = "Failed";
            reason = "direct_care_session_mode_invalid";
            return false;
        }

        if (session.StartedAtUtc != DateTimeOffset.MinValue
            && now - session.StartedAtUtc > TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.DirectCareSessionMaxSeconds))
        {
            terminal = true;
            outcome = "Timeout";
            reason = "direct_care_session_max_elapsed";
            return false;
        }

        if (!TryResolvePair(session, snapshots, now, out OperatorDecisionSnapshot caregiverSnapshot, out OperatorDecisionSnapshot patientSnapshot,
                out VanguardRaidOperatorRuntimeRecord caregiverRuntime, out VanguardRaidOperatorRuntimeRecord patientRuntime, out reason))
        {
            terminal = true;
            outcome = "Interrupted";
            return false;
        }

        BotOwner caregiverOwner = caregiverRuntime.BotOwner!;
        BotOwner patientOwner = patientRuntime.BotOwner!;

        // Once native DoMedEffect has been committed, pre-commit admission facts are no longer
        // authoritative. The item may be consumed/removed and the dominant need may legitimately
        // change while EFT is applying the effect. From this point onward only exact transaction
        // readback may complete or fail the session.
        if (session.DirectCareCommitted)
        {
            return VerifyCommittedEffect(session, patientSnapshot, patientOwner, caregiverOwner, now, out terminal, out outcome, out reason);
        }

        // Direct Care is the protected/combat lane. Once both actors have genuinely returned to a
        // quiet no-combat state before commit, release this lane so the coordinator can re-admit the
        // same missing-item need through the physical Supply -> Pickup -> self-care path instead of
        // silently turning a stale combat session into out-of-combat Direct Care.
        if (IsStrictNoCombatHandoff(caregiverSnapshot, patientSnapshot))
        {
            terminal = true;
            outcome = "Interrupted";
            reason = "direct_care_context_became_strict_no_combat_supply_preferred";
            return false;
        }

        if (!ValidateCurrentNeed(session, patientSnapshot, out reason))
        {
            terminal = true;
            outcome = reason.StartsWith("patient_need_resolved", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Interrupted";
            return outcome == "Completed";
        }

        if (caregiverSnapshot.Medical.Need.HasAnyNeed)
        {
            terminal = true;
            outcome = "Interrupted";
            reason = "caregiver_self_medical_need_wins";
            return false;
        }

        if (patientSnapshot.Medical.Actionability.RequiredItemAvailable)
        {
            terminal = true;
            outcome = "Interrupted";
            reason = "patient_self_item_became_available_self_care_preferred";
            return false;
        }

        if (!VanguardMedicSupportItemSelector.TryFindExactItem(
                caregiverOwner,
                session.ItemInstanceId,
                out MedsItemClass exactItem,
                out float resource,
                out float maxResource))
        {
            terminal = true;
            outcome = "Interrupted";
            reason = "reserved_direct_care_item_missing";
            return false;
        }
        if (!VanguardMedicSupportItemSelector.IsResourceViable(resource, maxResource))
        {
            terminal = true;
            outcome = "Interrupted";
            reason = "reserved_direct_care_item_exhausted";
            return false;
        }

        if (!TryStabilizePair(session, caregiverSnapshot, patientSnapshot, caregiverOwner, patientOwner, now, out reason))
        {
            if (reason.StartsWith("survival_preempts", StringComparison.OrdinalIgnoreCase))
            {
                terminal = true;
                outcome = "Interrupted";
            }
            else
            {
                // Search/GroupSearch path identity and threat identity can disappear for a few frames
                // while SAIN transitions layers. Keep the reserved pair and exact item instead of
                // destroying/recreating the Medic session every admission tick.
                VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "RendezvousDeferred", now);
            }
            return false;
        }

        if (!ValidateCareSafety(session.Need, caregiverSnapshot, patientSnapshot, out reason))
        {
            return false;
        }

        if (!Enum.TryParse(session.TargetPart, ignoreCase: true, out EBodyPart targetPart))
        {
            terminal = true;
            outcome = "Failed";
            reason = "direct_care_target_body_part_unparseable:" + Safe(session.TargetPart);
            return false;
        }

        if (!EnsureDirectCareAuthority(session, caregiverSnapshot, patientSnapshot, now, out reason))
        {
            return false;
        }

        // The pair is already within reach and physically slow (TryStabilizePair), and both
        // care admissions are current. The combat sidecar does NOT own SAIN's path, stance or
        // aiming. In particular, Mover.Stop() clears the native EFT path and cannot be undone
        // by MovementResume(). Native DoMedEffect does not require that destructive operation.
        // Preserve both actors' movement/aim/pose for SAIN and let the native effect proceed.
        VanguardClientDiagnosticsLog.Diagnostic(
            StatusTag,
            () => $"VANGUARD_MEDIC_PHYSICAL_ADMISSION session={Safe(session.SessionId)}; caregiver={Safe(session.CaregiverBotProfileId)}; patient={Safe(session.PatientBotProfileId)}; kind=direct_care; pairQualified=true; physicalMutation=false; sainMovementPreserved=true; nativeCareCommitPending=true");

        if (!VanguardMedicSupportSessionStore.TryBeginDirectCareCommit(
                session.SessionId,
                patientSnapshot.Medical.Need.HealthPercent,
                resource,
                now))
        {
            terminal = true;
            outcome = "Failed";
            reason = "direct_care_exactly_once_commit_guard_rejected";
            return false;
        }

        IEffect? effect;
        try
        {
            effect = patientOwner.GetPlayer?.ActiveHealthController?.DoMedEffect(exactItem, targetPart);
        }
        catch (Exception exception)
        {
            terminal = true;
            outcome = "Failed";
            reason = "native_cross_actor_DoMedEffect_exception:" + exception.GetType().Name + ":" + Safe(exception.Message);
            return false;
        }

        if (effect == null)
        {
            terminal = true;
            outcome = "Failed";
            reason = "native_cross_actor_DoMedEffect_returned_null";
            return false;
        }

        if (!(effect is ActiveHealthController.GClass3008 mutableEffect))
        {
            terminal = true;
            outcome = "Failed";
            reason = "native_cross_actor_DoMedEffect_unexpected_effect_type:" + effect.GetType().Name;
            return false;
        }

        float originalDelay = Math.Max(0f, mutableEffect.DelayTime);
        float originalWork = Math.Max(0f, mutableEffect.WorkStateTime);
        float originalBuild = Math.Max(0f, mutableEffect.BuildUpTime);
        float scaledDelay = originalDelay * VanguardMedicSupportDoctrine.DirectCareDurationScale;
        float scaledWork = Math.Max(0.05f, originalWork * VanguardMedicSupportDoctrine.DirectCareDurationScale);
        float scaledBuild = Math.Min(scaledWork, originalBuild * VanguardMedicSupportDoctrine.DirectCareDurationScale);
        mutableEffect.DelayTime = scaledDelay;
        mutableEffect.WorkStateTime = scaledWork;
        mutableEffect.BuildUpTime = scaledBuild;

        // EFT seeds the current-state timer (Float_12) during effect creation. Updating only the
        // public duration properties would leave that already-seeded timer at the unscaled value.
        // Rebase the live timer as well so the Medic's x0.5 specialization is real rather than
        // diagnostic-only. Preserve any progress already accumulated during this same frame.
        if (mutableEffect.State == EEffectState.Added)
        {
            mutableEffect.Float_12 = Math.Max(mutableEffect.Single_0, scaledDelay);
        }
        else if (mutableEffect.State == EEffectState.Started)
        {
            mutableEffect.Float_12 = Math.Max(mutableEffect.Single_0, scaledWork);

            // If EFT had zero start-delay, MedEffect.Started() already derived its per-second
            // health/energy/hydration rates from the original WorkStateTime before DoMedEffect
            // returned. Rebase those already-live rates by the inverse scale so shortening the
            // state timer does not accidentally deliver only half of the intended treatment.
            float inverseScale = 1f / VanguardMedicSupportDoctrine.DirectCareDurationScale;
            mutableEffect.SetHealthRatesPerSecond(
                (mutableEffect.Float_8 / 60f) * inverseScale,
                (mutableEffect.Float_9 / 60f) * inverseScale,
                (mutableEffect.Float_10 / 60f) * inverseScale,
                (mutableEffect.Float_11 / 60f) * inverseScale);
        }

        float expectedEffectSeconds = Math.Max(0.10f, scaledDelay + scaledWork);

        if (!VanguardMedicSupportSessionStore.TryMarkDirectCareCommitted(session.SessionId, expectedEffectSeconds, now))
        {
            terminal = true;
            outcome = "Failed";
            reason = "direct_care_commit_state_lost_after_native_effect";
            return false;
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDIC_DIRECT_CARE_COMMITTED session={Safe(session.SessionId)}; caregiver={Safe(session.CaregiverBotProfileId)}; patient={Safe(session.PatientBotProfileId)}; need={session.Need}; target={Safe(session.TargetPart)}; item={Safe(session.ItemName)}; itemInstance={Safe(session.ItemInstanceId)}; nativeDoMedEffect=true; durationScale={VanguardMedicSupportDoctrine.DirectCareDurationScale:0.00}; delayOriginal={originalDelay:0.00}; delayScaled={mutableEffect.DelayTime:0.00}; workOriginal={originalWork:0.00}; workScaled={mutableEffect.WorkStateTime:0.00}; buildOriginal={originalBuild:0.00}; buildScaled={mutableEffect.BuildUpTime:0.00}; exactlyOnce=true; RestoreFullHealth=false; healthBefore={patientSnapshot.Medical.Need.HealthPercent:0.0}; resourceBefore={resource:0.0}/{maxResource:0.0}; runtimeGate=cross_actor_native_effect");
        reason = "direct_care_native_effect_committed";
        return true;
    }

    private static bool TryStabilizePair(
        VanguardMedicSupportSession session,
        OperatorDecisionSnapshot caregiver,
        OperatorDecisionSnapshot patient,
        BotOwner caregiverOwner,
        BotOwner patientOwner,
        DateTimeOffset now,
        out string reason)
    {
        float distance = HorizontalDistance(caregiver.Position, patient.Position);
        if (distance <= VanguardMedicSupportDoctrine.DirectCarePairMeters
            && caregiver.RealSpeed <= 0.65f
            && patient.RealSpeed <= 0.65f)
        {
            VanguardMedicRendezvousMovementService.ReleaseSession(session.SessionId, now, "pair_stabilized");
            VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "PairStabilized", now);
            reason = "pair_stabilized";
            return true;
        }

        if (!VanguardMedicalRendezvousPlanner.TryResolve(caregiver, patient, out VanguardMedicalRendezvousPlan plan, out string planReason))
        {
            reason = "pair_rendezvous_failed:" + Safe(planReason);
            return false;
        }

        bool caregiverArrived = !plan.MoveCaregiver;
        bool patientArrived = !plan.MovePatient;
        if (plan.MoveCaregiver
            && !VanguardMedicRendezvousMovementService.TryMove(
                session.SessionId,
                caregiver,
                caregiverOwner,
                "caregiver",
                plan.CaregiverDestination,
                1.15f,
                now,
                allowCombatTranslation: true,
                out caregiverArrived,
                out string caregiverMoveReason))
        {
            reason = caregiverMoveReason.StartsWith("survival_preempts", StringComparison.OrdinalIgnoreCase)
                ? caregiverMoveReason
                : "pair_rendezvous_failed:caregiver:" + caregiverMoveReason;
            return false;
        }

        if (plan.MovePatient
            && !VanguardMedicRendezvousMovementService.TryMove(
                session.SessionId,
                patient,
                patientOwner,
                "patient",
                plan.PatientDestination,
                1.15f,
                now,
                allowCombatTranslation: true,
                out patientArrived,
                out string patientMoveReason))
        {
            reason = patientMoveReason.StartsWith("survival_preempts", StringComparison.OrdinalIgnoreCase)
                ? patientMoveReason
                : "pair_rendezvous_failed:patient:" + patientMoveReason;
            return false;
        }

        VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "Rendezvous", now);
        reason = "pair_rendezvous_in_progress:" + Safe(plan.Source)
            + ":caregiverArrived=" + Bool(caregiverArrived)
            + ":patientArrived=" + Bool(patientArrived);
        return false;
    }

    private static bool EnsureDirectCareAuthority(
        VanguardMedicSupportSession session,
        OperatorDecisionSnapshot caregiver,
        OperatorDecisionSnapshot patient,
        DateTimeOffset now,
        out string reason)
    {
        if (!EnsureActorCareAuthority(session, caregiver, "caregiver_direct_care", now,
                out CareAuthorityReceipt caregiverReceipt, out string caregiverReason))
        {
            reason = "caregiver_direct_care_authority_denied:" + caregiverReason;
            return false;
        }
        if (!EnsureActorCareAuthority(session, patient, "patient_direct_care", now,
                out CareAuthorityReceipt patientReceipt, out string patientReason))
        {
            // A combat-care sidecar owns no auxiliary window. When the caregiver did acquire
            // a *new* auxiliary window, rollback only that exact window. Reused windows are
            // not owned by this attempt and must remain intact.
            RollbackNewCareWindow(caregiverReceipt, now, "direct_care_pair_second_admission_denied", patientReason);
            reason = "patient_direct_care_authority_denied:" + patientReason;
            return false;
        }

        // Recheck both exact grants after the pair is prepared. A Combat Care grant does not
        // transfer SAIN movement/aim; an auxiliary grant may have been preempted in between.
        bool caregiverCurrent = IsCareAuthorityCurrent(session, caregiverReceipt, now, out string caregiverCurrentReason);
        bool patientCurrent = IsCareAuthorityCurrent(session, patientReceipt, now, out string patientCurrentReason);
        if (!caregiverCurrent || !patientCurrent)
        {
            RollbackNewCareWindow(caregiverReceipt, now, "direct_care_pair_readback_failed", "caregiver=" + caregiverCurrentReason);
            RollbackNewCareWindow(patientReceipt, now, "direct_care_pair_readback_failed", "patient=" + patientCurrentReason);
            reason = "direct_care_pair_authority_stale:caregiver=" + caregiverCurrentReason + ":patient=" + patientCurrentReason;
            return false;
        }
        VanguardClientDiagnosticsLog.Diagnostic(
            StatusTag,
            () => $"VANGUARD_MEDIC_PAIR_HANDOFF session={Safe(session.SessionId)}; caregiver={Safe(caregiver.BotProfileId)}; patient={Safe(patient.BotProfileId)}; prepared2=true; readback2=true; physicalMutation=false; caregiverWindow={Safe(caregiverReceipt.WindowId)}; patientWindow={Safe(patientReceipt.WindowId)}; combatSidecarCaregiver={Bool(caregiverReceipt.CombatSidecar)}; combatSidecarPatient={Bool(patientReceipt.CombatSidecar)}");
        reason = "direct_care_pair_authority_ready";
        return true;
    }

    private static bool EnsureActorCareAuthority(
        VanguardMedicSupportSession session,
        OperatorDecisionSnapshot snapshot,
        string role,
        DateTimeOffset now,
        out CareAuthorityReceipt receipt,
        out string reason)
    {
        receipt = new CareAuthorityReceipt();
        bool combatContext = snapshot.Sain.IsInCombat == true
            || snapshot.Threat.ResidualThreat
            || snapshot.ThreatScan.CombatContext
            || (snapshot.PrimaryExecution.Active
                && string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase));
        if (combatContext)
        {
            string threatId = !string.IsNullOrWhiteSpace(snapshot.Threat.EnemyId)
                && !string.Equals(snapshot.Threat.EnemyId, "none", StringComparison.OrdinalIgnoreCase)
                    ? snapshot.Threat.EnemyId
                    : snapshot.ThreatScan.CurrentThreatId;
            bool granted = VanguardMainIntentScheduler.TryGrantMedicSupportCombatCareSidecar(
                snapshot,
                now,
                threatId,
                session.SessionId,
                out string combatWindow,
                out reason);
            if (granted)
            {
                receipt = new CareAuthorityReceipt(snapshot.BotProfileId, combatWindow, threatId, true, false);
            }
            return granted;
        }

        bool admitted = VanguardMainIntentScheduler.TryOpenMedicSupportAuxiliaryWindow(
            snapshot,
            session.SessionId,
            VanguardPrimaryExecutionWindowKinds.MedicSupportDirectCare,
            role,
            now,
            out string auxiliaryWindow,
            out reason);
        if (admitted)
        {
            bool opened = string.Equals(reason, "medic_support_auxiliary_window_granted", StringComparison.OrdinalIgnoreCase);
            receipt = new CareAuthorityReceipt(snapshot.BotProfileId, auxiliaryWindow, "none", false, opened);
        }
        return admitted;
    }

    private static bool IsCareAuthorityCurrent(
        VanguardMedicSupportSession session, CareAuthorityReceipt receipt, DateTimeOffset now, out string reason)
        => receipt.CombatSidecar
            ? VanguardMainIntentScheduler.IsMedicSupportCombatMovementSidecarCurrent(
                receipt.BotProfileId, now, receipt.ThreatId, receipt.WindowId, session.SessionId, out reason)
            : VanguardMainIntentScheduler.IsMedicSupportAuxiliaryWindowCurrent(
                receipt.BotProfileId, now, receipt.WindowId, session.SessionId, out reason);

    private static void RollbackNewCareWindow(
        CareAuthorityReceipt receipt, DateTimeOffset now, string reason, string summary)
    {
        if (receipt.NewlyOpened && !receipt.CombatSidecar)
        {
            VanguardMainIntentScheduler.TryCloseMedicSupportAuxiliaryWindow(
                receipt.BotProfileId, receipt.WindowId, "Interrupted", reason, summary, now);
        }
    }

    private readonly struct CareAuthorityReceipt
    {
        public string BotProfileId { get; }
        public string WindowId { get; }
        public string ThreatId { get; }
        public bool CombatSidecar { get; }
        public bool NewlyOpened { get; }

        public CareAuthorityReceipt(string botProfileId, string windowId, string threatId, bool combatSidecar, bool newlyOpened)
        {
            BotProfileId = botProfileId;
            WindowId = windowId;
            ThreatId = threatId;
            CombatSidecar = combatSidecar;
            NewlyOpened = newlyOpened;
        }
    }

    private static bool VerifyCommittedEffect(
        VanguardMedicSupportSession session,
        OperatorDecisionSnapshot patient,
        BotOwner patientOwner,
        BotOwner caregiver,
        DateTimeOffset now,
        out bool terminal,
        out string outcome,
        out string reason)
    {
        terminal = false;
        outcome = "none";
        reason = "direct_care_effect_running";

        bool itemStillPresent = VanguardMedicSupportItemSelector.TryFindExactItem(
            caregiver,
            session.ItemInstanceId,
            out _,
            out float currentResource,
            out _);
        bool resourceChanged = !itemStillPresent
            || (session.DirectCareResourceBefore >= 0f && currentResource >= 0f && currentResource < session.DirectCareResourceBefore - 0.01f);
        string outcomeProof = "resource_unchanged";
        bool originalNeedResolved = resourceChanged
            && IsOriginalTreatmentOutcomeResolved(session, patient, patientOwner, now, out outcomeProof);

        if (resourceChanged && originalNeedResolved)
        {
            terminal = true;
            outcome = "Completed";
            reason = "MEDIC_DIRECT_CARE_TRANSACTION_OK";
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"MEDIC_DIRECT_CARE_TRANSACTION_OK session={Safe(session.SessionId)}; caregiver={Safe(session.CaregiverBotProfileId)}; patient={Safe(session.PatientBotProfileId)}; need={session.Need}; target={Safe(session.TargetPart)}; resourceChanged={Bool(resourceChanged)}; itemStillPresent={Bool(itemStillPresent)}; originalNeedResolved=true; outcomeProof={Safe(outcomeProof)}; durationScale={VanguardMedicSupportDoctrine.DirectCareDurationScale:0.00}; exactlyOnce=true; nativeEffect=true");
            return true;
        }

        double expected = session.DirectCareExpectedEffectSeconds > 0f ? session.DirectCareExpectedEffectSeconds : 8.0d;
        if (session.DirectCareCommittedAtUtc != DateTimeOffset.MinValue
            && now - session.DirectCareCommittedAtUtc > TimeSpan.FromSeconds(expected + 3.0d))
        {
            terminal = true;
            outcome = "Failed";
            reason = "direct_care_outcome_not_verified_after_native_effect_window"
                + ":resourceChanged=" + Bool(resourceChanged)
                + ":originalNeedResolved=" + Bool(originalNeedResolved)
                + ":outcomeProof=" + Safe(outcomeProof);
            return false;
        }

        return true;
    }

    private static bool IsOriginalTreatmentOutcomeResolved(
        VanguardMedicSupportSession session,
        OperatorDecisionSnapshot patient,
        BotOwner patientOwner,
        DateTimeOffset now,
        out string proof)
    {
        switch (session.Need)
        {
            case VanguardMedicalNeed.HeavyBleed:
                return IsCanonicalEffectAbsent(session, patientOwner, now, "HB", out proof);
            case VanguardMedicalNeed.LightBleed:
                return IsCanonicalEffectAbsent(session, patientOwner, now, "LB", out proof);
            case VanguardMedicalNeed.Fracture:
            {
                bool effectAbsent = IsCanonicalEffectAbsent(session, patientOwner, now, "FR", out string effectProof);
                bool brokenPartAbsent = patient.Medical.Need.IsReadable && !ContainsPartToken(patient.Medical.Need.BrokenParts, session.TargetPart);
                proof = effectProof + ":brokenPartAbsent=" + Bool(brokenPartAbsent);
                return effectAbsent && brokenPartAbsent;
            }
            case VanguardMedicalNeed.SurgeryDestroyedPart:
            case VanguardMedicalNeed.BlackBroken:
            {
                bool resolved = patient.Medical.Need.IsReadable && !ContainsPartToken(patient.Medical.Need.DestroyedParts, session.TargetPart);
                proof = "destroyedPartAbsent=" + Bool(resolved);
                return resolved;
            }
            case VanguardMedicalNeed.HpHeal:
            {
                bool improved = patient.Medical.Need.IsReadable
                    && session.DirectCareHealthBefore >= 0f
                    && patient.Medical.Need.HealthPercent > session.DirectCareHealthBefore + 0.5f;
                proof = "hpImproved=" + Bool(improved);
                return improved;
            }
            default:
                proof = "unsupported_direct_care_outcome_need:" + session.Need;
                return false;
        }
    }

    private static bool IsCanonicalEffectAbsent(
        VanguardMedicSupportSession session,
        BotOwner patientOwner,
        DateTimeOffset now,
        string badge,
        out string proof)
    {
        Player? player = patientOwner.GetPlayer;
        VanguardCanonicalMedicalEffectSnapshot canonical = VanguardCanonicalMedicalStateService.Capture(
            session.PatientBotProfileId,
            player,
            player?.HealthController,
            player?.ActiveHealthController,
            now,
            "medic_direct_care_outcome_verify:" + badge,
            forceRefresh: true);
        bool trustworthy = canonical.ControllerObserved && canonical.ScanComplete && !canonical.ScanTruncated && !canonical.BudgetExceeded;
        bool matchingEffect = canonical.Observations.Any(observation
            => string.Equals(observation.Badge, badge, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Safe(observation.BodyPart), Safe(session.TargetPart), StringComparison.OrdinalIgnoreCase));
        proof = "canonical=" + badge
            + ":trustworthy=" + Bool(trustworthy)
            + ":matchingEffect=" + Bool(matchingEffect)
            + ":revision=" + canonical.Revision.ToString();
        return trustworthy && !matchingEffect;
    }

    private static bool ContainsPartToken(string? tokenList, string? expectedPart)
    {
        if (string.IsNullOrWhiteSpace(tokenList) || string.Equals(tokenList, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string expected = Safe(expectedPart);
        return tokenList.Split(',')
            .Select(token => token.Trim())
            .Any(token => string.Equals(Safe(token), expected, StringComparison.OrdinalIgnoreCase));
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

    private static bool ValidateCareSafety(
        VanguardMedicalNeed need,
        OperatorDecisionSnapshot caregiver,
        OperatorDecisionSnapshot patient,
        out string reason)
    {
        if (caregiver.Sain.RunningToCover == true || patient.Sain.RunningToCover == true)
        {
            reason = "survival_preempts_direct_care:sain_running_to_cover";
            return false;
        }
        bool caregiverUnprotectedIncoming = caregiver.Medical.Safety.IncomingFireRecent
            && !caregiver.Medical.Safety.CoveredSuppressionOpportunity;
        bool patientUnprotectedIncoming = patient.Medical.Safety.IncomingFireRecent
            && !patient.Medical.Safety.CoveredSuppressionOpportunity;
        if (caregiver.Threat.DirectThreat || patient.Threat.DirectThreat
            || caregiver.Medical.Safety.EnemyCanShoot || patient.Medical.Safety.EnemyCanShoot
            || caregiver.Medical.Safety.ImmediateCombatBlock || patient.Medical.Safety.ImmediateCombatBlock
            || caregiverUnprotectedIncoming || patientUnprotectedIncoming)
        {
            reason = "survival_preempts_direct_care:direct_or_unprotected_incoming_fire";
            return false;
        }

        bool safe = need switch
        {
            VanguardMedicalNeed.Fracture => caregiver.Medical.Safety.SafeForStationaryAid && patient.Medical.Safety.SafeForStationaryAid,
            VanguardMedicalNeed.SurgeryDestroyedPart => caregiver.Medical.Safety.SafeForStationarySurgery && patient.Medical.Safety.SafeForStationarySurgery,
            VanguardMedicalNeed.BlackBroken => caregiver.Medical.Safety.SafeForStationarySurgery && patient.Medical.Safety.SafeForStationarySurgery,
            _ => caregiver.Medical.Safety.SafeForMobileAid && patient.Medical.Safety.SafeForMobileAid
        };
        reason = safe ? "direct_care_safety_ready" : "direct_care_waiting_for_protected_window";
        return safe;
    }

    private static bool TryResolvePair(
        VanguardMedicSupportSession session,
        IReadOnlyList<OperatorDecisionSnapshot> snapshots,
        DateTimeOffset now,
        out OperatorDecisionSnapshot caregiverSnapshot,
        out OperatorDecisionSnapshot patientSnapshot,
        out VanguardRaidOperatorRuntimeRecord caregiverRuntime,
        out VanguardRaidOperatorRuntimeRecord patientRuntime,
        out string reason)
    {
        caregiverSnapshot = snapshots
            .Where(value => value != null && string.Equals(value.BotProfileId, session.CaregiverBotProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.CapturedAtUtc)
            .FirstOrDefault()!;
        patientSnapshot = snapshots
            .Where(value => value != null && string.Equals(value.BotProfileId, session.PatientBotProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.CapturedAtUtc)
            .FirstOrDefault()!;
        caregiverRuntime = null!;
        patientRuntime = null!;
        if (caregiverSnapshot == null || patientSnapshot == null)
        {
            reason = "pair_snapshot_missing_or_stale:caregiver=" + Safe(caregiverSnapshot == null ? "missing" : "present")
                + ":patient=" + Safe(patientSnapshot == null ? "missing" : "present");
            return false;
        }
        bool caregiverFreshOk = VanguardOperatorDecisionSnapshotService.IsFresh(caregiverSnapshot, now, out string caregiverFresh);
        bool patientFreshOk = VanguardOperatorDecisionSnapshotService.IsFresh(patientSnapshot, now, out string patientFresh);
        if (!caregiverFreshOk || !patientFreshOk)
        {
            reason = "pair_snapshot_missing_or_stale:caregiver=" + Safe(caregiverFresh)
                + ":patient=" + Safe(patientFresh);
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.CaregiverBotProfileId, out caregiverRuntime)
            || caregiverRuntime.BotOwner == null || caregiverRuntime.BotOwner.IsDead
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.PatientBotProfileId, out patientRuntime)
            || patientRuntime.BotOwner == null || patientRuntime.BotOwner.IsDead)
        {
            reason = "pair_runtime_unbound_or_dead";
            return false;
        }
        reason = "pair_current";
        return true;
    }

    private static bool ValidateCurrentNeed(VanguardMedicSupportSession session, OperatorDecisionSnapshot patient, out string reason)
    {
        if (!patient.Alive || !patient.Medical.Alive)
        {
            reason = "patient_dead";
            return false;
        }
        if (!patient.Medical.Need.HasAnyNeed)
        {
            reason = "patient_need_resolved_no_need";
            return false;
        }
        if (patient.Medical.Need.DominantNeed != session.Need)
        {
            reason = "patient_need_resolved_or_changed:" + patient.Medical.Need.DominantNeed;
            return false;
        }
        if (!string.Equals(Safe(patient.Medical.Need.TargetPart), Safe(session.TargetPart), StringComparison.OrdinalIgnoreCase))
        {
            reason = "patient_need_target_changed:" + Safe(patient.Medical.Need.TargetPart);
            return false;
        }
        reason = "need_current";
        return true;
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";
}
#endif

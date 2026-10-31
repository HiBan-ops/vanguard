#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Builds the read-only squad medical-support board that identifies which current Medic could materially help which current patient and why.
// Flow: Fresh decision snapshots are joined to stable raid-runtime role identity, current need-to-item capabilities and bounded safety facts, then deterministic caregiver-patient candidate facts are published for diagnostics and later support executors.
// Authority boundary: Observation only; this board cannot acquire leases, reserve items, rescan inventories, move actors, mutate health, drop/pick up items, start medical hands or alter SAIN/EFT combat authority.
// Invariant: A candidate is only eligible from current readable evidence, self-treatment remains preferred, role identity comes from the bound runtime manifest, and missing item-instance/resource/path proof stays deferred to later execution preflight rather than being fabricated here.
namespace Vanguard.Client.Runtime.Medical.Support;

internal sealed class VanguardMedicSupportCandidateFact
{
    public string CaregiverOperatorId { get; init; } = "none";
    public string CaregiverBotProfileId { get; init; } = "none";
    public string CaregiverOwnerProfileId { get; init; } = "none";
    public string CaregiverRole { get; init; } = "none";
    public string PatientOperatorId { get; init; } = "none";
    public string PatientBotProfileId { get; init; } = "none";
    public string PatientOwnerProfileId { get; init; } = "none";
    public VanguardMedicalNeed PatientNeed { get; init; } = VanguardMedicalNeed.None;
    public string PatientTargetPart { get; init; } = "none";
    public string CandidateItemTemplateId { get; init; } = "none";
    public string CandidateItemName { get; init; } = "none";
    public string CandidateItemRole { get; init; } = "none";
    public string CandidateActionKind { get; init; } = "none";
    public bool PatientWaitingForItem { get; init; }
    public bool PatientCanSelfTreatWithOwnedItem { get; init; }
    public bool CaregiverCompatibleTemplatePresent { get; init; }
    public bool SupplyDropDryRunEligible { get; init; }
    public bool DirectCareDryRunEligible { get; init; }
    public bool CaregiverOwnMedicalNeedBlocks { get; init; }
    public bool CaregiverCriticalExecutionBlocks { get; init; }
    public bool PatientCriticalExecutionBlocks { get; init; }
    public bool SafetyBlocks { get; init; }
    public bool SameOwnerInitialScope { get; init; }
    public bool ExactItemInstanceQualified { get; init; }
    public bool ItemResourceAndReserveQualified { get; init; }
    public bool PathQualified { get; init; }
    public bool CaregiverHardOutsideBubble { get; init; }
    public bool PatientHardOutsideBubble { get; init; }
    public float CaregiverSpeedMetersPerSecond { get; init; }
    public float PatientSpeedMetersPerSecond { get; init; }
    public float DirectDistanceMeters { get; init; }
    public float TriageScore { get; init; }
    public string EligibilityReason { get; init; } = "none";
    public string DirectCareReason { get; init; } = "none";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.MinValue;

    public bool AnyDryRunLaneEligible => SupplyDropDryRunEligible || DirectCareDryRunEligible;

    public string Signature => string.Join("|",
        Safe(CaregiverBotProfileId),
        Safe(PatientBotProfileId),
        PatientNeed.ToString(),
        Safe(PatientTargetPart),
        Safe(CandidateItemTemplateId),
        Bool(SupplyDropDryRunEligible),
        Bool(DirectCareDryRunEligible),
        Safe(EligibilityReason),
        Safe(DirectCareReason));

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal static class VanguardSquadMedicalSupportBoard
{
    public const string StatusTag = "VANGUARD_MEDICAL_SUPPORT_TRIAGE_DRYRUN_STATUS";

    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(1.0d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(20.0d);
    private static volatile VanguardMedicSupportCandidateFact[] latestFacts = Array.Empty<VanguardMedicSupportCandidateFact>();
    private static readonly Dictionary<string, string> LastSelectionSignatureByCaregiver = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastSummaryAtByCaregiver = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset nextEvaluationAtUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    public static IReadOnlyList<VanguardMedicSupportCandidateFact> GetLatestFacts() => latestFacts;

    public static void ResetForRaidLifecycle(string reason)
    {
        latestFacts = Array.Empty<VanguardMedicSupportCandidateFact>();
        LastSelectionSignatureByCaregiver.Clear();
        LastSummaryAtByCaregiver.Clear();
        nextEvaluationAtUtc = DateTimeOffset.MinValue;
        bootLogged = false;
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"medical support board reset reason={Safe(reason)}; readOnly=true; itemReservation=false; movement=false; healthMutation=false; handsMutation=false");
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot>? snapshots, DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority || now < nextEvaluationAtUtc)
        {
            return;
        }

        nextEvaluationAtUtc = now + EvaluationInterval;
        if (!bootLogged)
        {
            bootLogged = true;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                "VANGUARD_MEDICAL_SUPPORT_TRIAGE_DRYRUN_BOOT enabled=true; readOnly=true; roleSource=raid_runtime_manifest; patientTruth=decision_snapshot_canonical_medical; inventoryEvidence=decision_snapshot_template_ids; sameOwnerInitialScope=true; nativeSquadModel=shared_per_player_owner; additionalInventoryScan=false; exactItemReservation=false; itemResourceMutation=false; pathfinding=false; movement=false; healthMutation=false; dropPickup=false; medicalHands=false; activeIntent=false; executionLease=false");
        }

        OperatorDecisionSnapshot[] currentSnapshots = (snapshots ?? Array.Empty<OperatorDecisionSnapshot>())
            .Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .ToArray();
        var snapshotByBotProfileId = currentSnapshots
            .GroupBy(snapshot => Normalize(snapshot.BotProfileId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.CapturedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        VanguardRaidOperatorRuntimeRecord[] runtimeRecords = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators()
            .Where(record => record.IsRuntimePresent && record.BotOwner != null)
            .ToArray();
        var facts = new List<VanguardMedicSupportCandidateFact>();

        foreach (VanguardRaidOperatorRuntimeRecord caregiver in runtimeRecords
                     .Where(IsMedic)
                     .OrderBy(record => record.OwnerProfileId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(record => record.OperatorId, StringComparer.OrdinalIgnoreCase))
        {
            string caregiverBot = Normalize(caregiver.BotProfileId);
            if (!snapshotByBotProfileId.TryGetValue(caregiverBot, out OperatorDecisionSnapshot caregiverSnapshot))
            {
                EmitNoSelection(caregiver, now, "caregiver_snapshot_missing");
                continue;
            }

            if (!VanguardOperatorDecisionSnapshotService.IsFresh(caregiverSnapshot, now, out string caregiverFreshness))
            {
                EmitNoSelection(caregiver, now, "caregiver_snapshot_stale:" + Safe(caregiverFreshness));
                continue;
            }

            VanguardRaidOperatorRuntimeRecord[] patients = runtimeRecords
                .Where(record => !ReferenceEquals(record, caregiver)
                    && !string.Equals(record.BotProfileId, caregiver.BotProfileId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.OwnerProfileId, caregiver.OwnerProfileId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.OperatorId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (patients.Length == 0)
            {
                EmitNoSelection(caregiver, now, "no_same_owner_runtime_patient");
                continue;
            }

            string firstBlockReason = "no_eligible_patient";
            foreach (VanguardRaidOperatorRuntimeRecord patient in patients)
            {
                string patientBot = Normalize(patient.BotProfileId);
                if (!snapshotByBotProfileId.TryGetValue(patientBot, out OperatorDecisionSnapshot patientSnapshot))
                {
                    firstBlockReason = FirstReason(firstBlockReason, "patient_snapshot_missing");
                    continue;
                }

                if (!VanguardOperatorDecisionSnapshotService.IsFresh(patientSnapshot, now, out string patientFreshness))
                {
                    firstBlockReason = FirstReason(firstBlockReason, "patient_snapshot_stale:" + Safe(patientFreshness));
                    continue;
                }

                VanguardMedicSupportCandidateFact fact = EvaluatePair(caregiver, caregiverSnapshot, patient, patientSnapshot, now);
                facts.Add(fact);
                if (!fact.AnyDryRunLaneEligible)
                {
                    firstBlockReason = FirstReason(firstBlockReason, fact.EligibilityReason);
                }
            }

            VanguardMedicSupportCandidateFact? selected = facts
                .Where(fact => string.Equals(fact.CaregiverBotProfileId, caregiverBot, StringComparison.OrdinalIgnoreCase) && fact.AnyDryRunLaneEligible)
                .OrderByDescending(fact => fact.TriageScore)
                .ThenBy(fact => fact.PatientOperatorId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (selected == null)
            {
                EmitNoSelection(caregiver, now, firstBlockReason);
            }
            else
            {
                EmitSelection(selected, now);
            }
        }

        latestFacts = facts
            .OrderBy(fact => fact.CaregiverOperatorId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(fact => fact.TriageScore)
            .ThenBy(fact => fact.PatientOperatorId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static VanguardMedicSupportCandidateFact EvaluatePair(
        VanguardRaidOperatorRuntimeRecord caregiver,
        OperatorDecisionSnapshot caregiverSnapshot,
        VanguardRaidOperatorRuntimeRecord patient,
        OperatorDecisionSnapshot patientSnapshot,
        DateTimeOffset now)
    {
        VanguardMedicalNeed need = patientSnapshot.Medical.Need.DominantNeed;
        bool sameOwner = string.Equals(caregiver.OwnerProfileId, patient.OwnerProfileId, StringComparison.OrdinalIgnoreCase);
        bool patientWaitingForItem = string.Equals(patientSnapshot.Medical.Plan.PlanKey, "MedicalAwaitItem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(patientSnapshot.Medical.Plan.NextStep, "ObserveMedicalItemMissingReadOnly", StringComparison.OrdinalIgnoreCase);
        bool patientCanSelfTreat = patientSnapshot.Medical.Actionability.RequiredItemAvailable;
        bool caregiverOwnNeedBlocks = caregiverSnapshot.Medical.Need.IsReadable && caregiverSnapshot.Medical.Need.HasAnyNeed;
        bool caregiverCriticalExecutionBlocks = IsCriticalExecution(caregiverSnapshot);
        bool patientCriticalExecutionBlocks = IsCriticalExecution(patientSnapshot);
        bool safetyBlocks = IsHardSafetyBlocked(caregiverSnapshot) || IsHardSafetyBlocked(patientSnapshot);
        float distance = Vector3.Distance(caregiverSnapshot.Position, patientSnapshot.Position);

        VanguardMedicalItemCapability? selectedCapability = null;
        bool compatibleTemplatePresent = false;
        string capabilityReason = "caregiver_inventory_unreadable";
        if (caregiverSnapshot.Medical.Inventory.Observed)
        {
            HashSet<string> caregiverTemplates = ParseTemplateSet(caregiverSnapshot.Medical.Inventory.CandidateTemplateIds);
            selectedCapability = VanguardMedicalItemCapabilityResolver.GetCandidates(need)
                .FirstOrDefault(candidate => caregiverTemplates.Contains(VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(candidate.TemplateId)));
            compatibleTemplatePresent = selectedCapability != null;
            capabilityReason = compatibleTemplatePresent ? "compatible_template_present" : "caregiver_no_compatible_template_in_snapshot";
        }

        string eligibilityReason = ResolveEligibilityReason(
            caregiverSnapshot,
            patientSnapshot,
            sameOwner,
            patientWaitingForItem,
            patientCanSelfTreat,
            caregiverOwnNeedBlocks,
            caregiverCriticalExecutionBlocks,
            patientCriticalExecutionBlocks,
            safetyBlocks,
            compatibleTemplatePresent,
            capabilityReason);

        bool supplyEligible = string.Equals(eligibilityReason, "dryrun_support_candidate", StringComparison.OrdinalIgnoreCase);
        bool directEligible;
        string directCareReason;
        if (supplyEligible)
        {
            directEligible = IsDirectCareDryRunEligible(caregiverSnapshot, patientSnapshot, need, out directCareReason);
        }
        else
        {
            directEligible = false;
            directCareReason = "base_support_candidate_not_eligible:" + eligibilityReason;
        }

        return new VanguardMedicSupportCandidateFact
        {
            CaregiverOperatorId = Normalize(caregiver.OperatorId),
            CaregiverBotProfileId = Normalize(caregiver.BotProfileId),
            CaregiverOwnerProfileId = Normalize(caregiver.OwnerProfileId),
            CaregiverRole = Normalize(caregiver.TacticalRole),
            PatientOperatorId = Normalize(patient.OperatorId),
            PatientBotProfileId = Normalize(patient.BotProfileId),
            PatientOwnerProfileId = Normalize(patient.OwnerProfileId),
            PatientNeed = need,
            PatientTargetPart = Safe(patientSnapshot.Medical.Need.TargetPart),
            CandidateItemTemplateId = selectedCapability?.TemplateId ?? "none",
            CandidateItemName = selectedCapability?.Name ?? "none",
            CandidateItemRole = selectedCapability?.Role.ToString() ?? "none",
            CandidateActionKind = selectedCapability?.ActionKind.ToString() ?? "none",
            PatientWaitingForItem = patientWaitingForItem,
            PatientCanSelfTreatWithOwnedItem = patientCanSelfTreat,
            CaregiverCompatibleTemplatePresent = compatibleTemplatePresent,
            SupplyDropDryRunEligible = supplyEligible,
            DirectCareDryRunEligible = directEligible,
            CaregiverOwnMedicalNeedBlocks = caregiverOwnNeedBlocks,
            CaregiverCriticalExecutionBlocks = caregiverCriticalExecutionBlocks,
            PatientCriticalExecutionBlocks = patientCriticalExecutionBlocks,
            SafetyBlocks = safetyBlocks,
            SameOwnerInitialScope = sameOwner,
            ExactItemInstanceQualified = false,
            ItemResourceAndReserveQualified = false,
            PathQualified = false,
            CaregiverHardOutsideBubble = caregiverSnapshot.MovementAuthority.HardOutsideBubble,
            PatientHardOutsideBubble = patientSnapshot.MovementAuthority.HardOutsideBubble,
            CaregiverSpeedMetersPerSecond = Math.Max(caregiverSnapshot.RealSpeed, caregiverSnapshot.Movement.RealSpeed),
            PatientSpeedMetersPerSecond = Math.Max(patientSnapshot.RealSpeed, patientSnapshot.Movement.RealSpeed),
            DirectDistanceMeters = distance,
            TriageScore = Score(need, patientSnapshot.Medical.Need.HealthPercent, distance),
            EligibilityReason = eligibilityReason,
            DirectCareReason = directCareReason,
            CapturedAtUtc = now
        };
    }

    private static string ResolveEligibilityReason(
        OperatorDecisionSnapshot caregiverSnapshot,
        OperatorDecisionSnapshot patientSnapshot,
        bool sameOwner,
        bool patientWaitingForItem,
        bool patientCanSelfTreat,
        bool caregiverOwnNeedBlocks,
        bool caregiverCriticalExecutionBlocks,
        bool patientCriticalExecutionBlocks,
        bool safetyBlocks,
        bool compatibleTemplatePresent,
        string capabilityReason)
    {
        if (!sameOwner) return "outside_same_owner_initial_scope";
        if (!caregiverSnapshot.Alive || !caregiverSnapshot.Medical.Alive) return "caregiver_not_alive";
        if (!caregiverSnapshot.Medical.Need.IsReadable || !caregiverSnapshot.Medical.Plan.Readable) return "caregiver_medical_unreadable";
        if (!patientSnapshot.Alive) return "patient_dead";
        if (!patientSnapshot.Medical.Alive) return "patient_medical_not_alive";
        if (!patientSnapshot.Medical.Need.IsReadable || !patientSnapshot.Medical.Plan.Readable) return "patient_medical_unreadable";
        if (!patientSnapshot.Medical.Need.HasAnyNeed) return "patient_has_no_medical_need";
        if (patientSnapshot.Medical.Need.DominantNeed == VanguardMedicalNeed.UntreatableVitalDestroyedPart) return "patient_need_untreatable_vital";
        if (IsExtractionLike(patientSnapshot)) return "patient_extraction_or_unload_like_state";
        if (patientCanSelfTreat) return "patient_self_item_available_self_care_preferred";
        if (!patientWaitingForItem) return "patient_not_waiting_for_missing_item:" + Safe(patientSnapshot.Medical.Plan.PlanKey);
        if (caregiverOwnNeedBlocks) return "caregiver_own_medical_need_wins";
        if (caregiverCriticalExecutionBlocks) return "caregiver_critical_execution_active";
        if (patientCriticalExecutionBlocks) return "patient_critical_execution_active";
        if (safetyBlocks) return "current_combat_safety_blocks_support";
        if (!compatibleTemplatePresent) return capabilityReason;
        return "dryrun_support_candidate";
    }

    private static bool IsDirectCareDryRunEligible(
        OperatorDecisionSnapshot caregiver,
        OperatorDecisionSnapshot patient,
        VanguardMedicalNeed need,
        out string reason)
    {
        // Current doctrine: current care safety gates the final treatment commit, not pair selection.
        // A missing-item patient may therefore reserve a Medic and rendezvous toward cover while
        // Search/GroupSearch remains active. Only hard survival conditions are filtered earlier.
        if (!patient.Medical.Need.TargetKnown)
        {
            reason = "patient_target_part_not_known";
            return false;
        }

        switch (need)
        {
            case VanguardMedicalNeed.HeavyBleed:
            case VanguardMedicalNeed.LightBleed:
            case VanguardMedicalNeed.HpHeal:
                reason = caregiver.Medical.Safety.SafeForMobileAid && patient.Medical.Safety.SafeForMobileAid
                    ? "mobile_direct_care_ready_now"
                    : "mobile_direct_care_rendezvous_allowed_wait_for_safety";
                return true;

            case VanguardMedicalNeed.Fracture:
                reason = caregiver.Medical.Safety.SafeForStationaryAid && patient.Medical.Safety.SafeForStationaryAid
                    ? "stationary_direct_care_ready_now"
                    : "stationary_direct_care_rendezvous_allowed_wait_for_safety";
                return true;

            case VanguardMedicalNeed.SurgeryDestroyedPart:
            case VanguardMedicalNeed.BlackBroken:
                reason = caregiver.Medical.Safety.SafeForStationarySurgery && patient.Medical.Safety.SafeForStationarySurgery
                    ? "surgery_direct_care_ready_now"
                    : "surgery_direct_care_rendezvous_allowed_wait_for_strong_safety";
                return true;

            case VanguardMedicalNeed.PainMobility:
                // Keep the early direct-care surface narrow: pain/stims remain a supply lane until
                // their cross-actor animation/effect semantics are separately qualified.
                reason = "pain_mobility_supply_preferred_initial_scope";
                return false;

            default:
                reason = "need_not_direct_care_candidate";
                return false;
        }
    }

    private static bool IsMedic(VanguardRaidOperatorRuntimeRecord record)
    {
        return string.Equals(record.TacticalRole, "Medic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHardSafetyBlocked(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Alive
            || snapshot.Sain.RunningToCover == true
            || snapshot.MovementAuthority.HardOutsideBubble
            || snapshot.Threat.DirectThreat
            || snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.ImmediateCombatBlock)
        {
            return true;
        }

        // Covered suppression is already an established self-medical sidecar opportunity.
        // Do not classify merely-recent fire as a hard block when the current snapshot says the
        // actor is protected enough to perform mobile aid; commit safety is re-read at rendezvous.
        return snapshot.Medical.Safety.IncomingFireRecent
            && !snapshot.Medical.Safety.CoveredSuppressionOpportunity;
    }

    private static bool IsCriticalExecution(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.PrimaryExecution.Active)
        {
            return false;
        }

        string kind = snapshot.PrimaryExecution.WindowKind ?? string.Empty;
        if (string.Equals(kind, VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion, StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, VanguardPrimaryExecutionWindowKinds.HardReturnMovement, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return kind.IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0
            && kind.IndexOf("MedicSupport", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool IsExtractionLike(OperatorDecisionSnapshot snapshot)
    {
        if (!snapshot.Orbit.Active)
        {
            return false;
        }

        string text = string.Join("|", snapshot.Orbit.Status, snapshot.Orbit.Category, snapshot.Orbit.ExtractReason);
        return text.IndexOf("extract", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("unload", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static HashSet<string> ParseTemplateSet(string? encoded)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(encoded) || string.Equals(encoded.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return set;
        }

        foreach (string token in encoded.Split(','))
        {
            string normalized = VanguardMedicalItemCapabilityResolver.NormalizeTemplateId(token);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                set.Add(normalized);
            }
        }
        return set;
    }

    private static float Score(VanguardMedicalNeed need, float healthPercent, float directDistanceMeters)
    {
        float baseScore = need switch
        {
            VanguardMedicalNeed.HeavyBleed => 1000f,
            VanguardMedicalNeed.LightBleed => 800f,
            VanguardMedicalNeed.Fracture => 700f,
            VanguardMedicalNeed.HpHeal => 600f + Math.Min(80f, Math.Max(0f, 100f - healthPercent)),
            VanguardMedicalNeed.PainMobility => 400f,
            VanguardMedicalNeed.SurgeryDestroyedPart => 220f,
            VanguardMedicalNeed.BlackBroken => 210f,
            _ => 0f
        };
        return baseScore - Math.Min(100f, Math.Max(0f, directDistanceMeters) * 2f);
    }

    private static void EmitSelection(VanguardMedicSupportCandidateFact selected, DateTimeOffset now)
    {
        string caregiver = Normalize(selected.CaregiverBotProfileId);
        bool changed = !LastSelectionSignatureByCaregiver.TryGetValue(caregiver, out string previous)
            || !string.Equals(previous, selected.Signature, StringComparison.Ordinal);
        bool summaryDue = !LastSummaryAtByCaregiver.TryGetValue(caregiver, out DateTimeOffset last)
            || now - last >= SummaryInterval;
        if (!changed && !summaryDue)
        {
            return;
        }

        LastSelectionSignatureByCaregiver[caregiver] = selected.Signature;
        LastSummaryAtByCaregiver[caregiver] = now;
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDICAL_SUPPORT_TRIAGE_DRYRUN caregiverOperator={Safe(selected.CaregiverOperatorId)}; caregiverBot={Safe(selected.CaregiverBotProfileId)}; caregiverRole={Safe(selected.CaregiverRole)}; patientOperator={Safe(selected.PatientOperatorId)}; patientBot={Safe(selected.PatientBotProfileId)}; need={selected.PatientNeed}; targetPart={Safe(selected.PatientTargetPart)}; candidateItem={Safe(selected.CandidateItemName)}; candidateTemplate={Safe(selected.CandidateItemTemplateId)}; itemRole={Safe(selected.CandidateItemRole)}; actionKind={Safe(selected.CandidateActionKind)}; distance={selected.DirectDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)}; score={selected.TriageScore.ToString("0.0", CultureInfo.InvariantCulture)}; supplyDropDryRunEligible={Bool(selected.SupplyDropDryRunEligible)}; directCareDryRunEligible={Bool(selected.DirectCareDryRunEligible)}; directCareReason={Safe(selected.DirectCareReason)}; reason={Safe(selected.EligibilityReason)}; sameOwnerInitialScope={Bool(selected.SameOwnerInitialScope)}; patientSelfItem={Bool(selected.PatientCanSelfTreatWithOwnedItem)}; exactItemInstanceQualified=false; itemResourceReserveQualified=false; pathQualified=false; caregiverHardOutside={Bool(selected.CaregiverHardOutsideBubble)}; patientHardOutside={Bool(selected.PatientHardOutsideBubble)}; caregiverSpeed={selected.CaregiverSpeedMetersPerSecond.ToString("0.00", CultureInfo.InvariantCulture)}; patientSpeed={selected.PatientSpeedMetersPerSecond.ToString("0.00", CultureInfo.InvariantCulture)}; readOnly=true; activeIntent=false; lease=false; mutation=false");
    }

    private static void EmitNoSelection(VanguardRaidOperatorRuntimeRecord caregiver, DateTimeOffset now, string reason)
    {
        string caregiverBot = Normalize(caregiver.BotProfileId);
        string signature = "none|" + Safe(reason);
        bool changed = !LastSelectionSignatureByCaregiver.TryGetValue(caregiverBot, out string previous)
            || !string.Equals(previous, signature, StringComparison.Ordinal);
        bool summaryDue = !LastSummaryAtByCaregiver.TryGetValue(caregiverBot, out DateTimeOffset last)
            || now - last >= SummaryInterval;
        if (!changed && !summaryDue)
        {
            return;
        }

        LastSelectionSignatureByCaregiver[caregiverBot] = signature;
        LastSummaryAtByCaregiver[caregiverBot] = now;
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDICAL_SUPPORT_TRIAGE_DRYRUN caregiverOperator={Safe(caregiver.OperatorId)}; caregiverBot={Safe(caregiver.BotProfileId)}; caregiverRole={Safe(caregiver.TacticalRole)}; selectedPatient=none; supplyDropDryRunEligible=false; directCareDryRunEligible=false; reason={Safe(reason)}; sameOwnerInitialScope=true; exactItemInstanceQualified=false; itemResourceReserveQualified=false; pathQualified=false; readOnly=true; activeIntent=false; lease=false; mutation=false");
    }

    private static string FirstReason(string current, string candidate)
    {
        return string.Equals(current, "no_eligible_patient", StringComparison.OrdinalIgnoreCase)
            ? Safe(candidate)
            : current;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

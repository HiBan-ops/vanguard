#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Medical.Support;

// Responsibility: Promotes one current Medic Support candidate into the normal Vanguard intent competition without owning movement, inventory or health mutation.
// Flow: Read-only squad medical support relation -> one best patient -> generic MedicSupport intent; the active coordinator later chooses direct care in combat/protected pairs or no-combat supply handoff.
// Authority boundary: Proposal only. Grenade/true survival/self-medical remain higher authority; loot, automatic authoring and ordinary tactical movement may yield when the medical session actually acquires.
// Invariant: Self-care remains preferred; a Medic Support intent exists only for a canonical MedicalAwaitItem patient and a Medic with a compatible item template.
namespace Vanguard.Client.Runtime.Intents;

internal sealed class VanguardMedicSupportIntentProducer : IVanguardIntentProducer
{
    public IEnumerable<VanguardIntentCandidate> Produce(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
        {
            yield break;
        }

        bool unprotectedIncomingFire = snapshot.Medical.Safety.IncomingFireRecent
            && !snapshot.Medical.Safety.CoveredSuppressionOpportunity;
        if (!snapshot.Medical.Alive
            || snapshot.Medical.Need.HasAnyNeed
            || snapshot.MovementAuthority.HardOutsideBubble
            || snapshot.Sain.RunningToCover == true
            || snapshot.Threat.DirectThreat
            || snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Medical.Safety.ImmediateCombatBlock
            || unprotectedIncomingFire
            || (snapshot.PrimaryExecution.Active
                && (string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.HardReturnMovement, StringComparison.OrdinalIgnoreCase))))
        {
            yield break;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        VanguardMedicSupportCandidateFact? fact = VanguardSquadMedicalSupportBoard.GetLatestFacts()
            .Where(candidate => candidate.AnyDryRunLaneEligible
                && candidate.SameOwnerInitialScope
                && string.Equals(candidate.CaregiverBotProfileId, snapshot.BotProfileId, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => candidate.CapturedAtUtc != DateTimeOffset.MinValue
                && now - candidate.CapturedAtUtc <= TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.CandidateFreshnessSeconds))
            .OrderByDescending(candidate => candidate.TriageScore)
            .ThenBy(candidate => candidate.PatientOperatorId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (fact == null)
        {
            yield break;
        }

        float baseScore = fact.PatientNeed switch
        {
            VanguardMedicalNeed.HeavyBleed => 340f,
            VanguardMedicalNeed.LightBleed => 325f,
            VanguardMedicalNeed.Fracture => 315f,
            VanguardMedicalNeed.HpHeal => 300f,
            VanguardMedicalNeed.PainMobility => 285f,
            VanguardMedicalNeed.SurgeryDestroyedPart => 280f,
            VanguardMedicalNeed.BlackBroken => 280f,
            _ => 270f
        };

        string lane = fact.DirectCareDryRunEligible ? "direct_or_supply" : "supply";
        yield return new VanguardIntentCandidate
        {
            IntentKey = VanguardMedicSupportDoctrine.SupportIntentKey,
            Domain = "Medical",
            Valid = true,
            BaseScore = baseScore,
            Reason = "medic_support_missing_item_pair_ready"
                + ";patient=" + Safe(fact.PatientBotProfileId)
                + ";need=" + fact.PatientNeed
                + ";template=" + Safe(fact.CandidateItemTemplateId)
                + ";lane=" + lane
                + ";distance=" + fact.DirectDistanceMeters.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Gate = "valid_medic_support_full_chain_candidate",
            TargetKey = fact.PatientBotProfileId,
            PlanKey = "medic_support:" + fact.PatientNeed + ":" + Safe(fact.CandidateItemTemplateId),
            NextStep = "CoordinateMedicSupportPair"
        };
    }

    internal static bool IsImmediatePhysicalPocCandidate(VanguardMedicSupportCandidateFact fact)
    {
        if (fact == null || !fact.SupplyDropDryRunEligible || !fact.SameOwnerInitialScope)
        {
            return false;
        }
        if (fact.CaregiverHardOutsideBubble || fact.PatientHardOutsideBubble)
        {
            return false;
        }
        if (fact.DirectDistanceMeters > VanguardMedicSupportDoctrine.NativePartnerAdmissionMeters)
        {
            return false;
        }
        return fact.CaregiverSpeedMetersPerSecond <= VanguardMedicSupportDoctrine.StationarySpeedMaxMetersPerSecond
            && fact.PatientSpeedMetersPerSecond <= VanguardMedicSupportDoctrine.StationarySpeedMaxMetersPerSecond;
    }

    internal static bool CanMedicSupportCompeteBeside(VanguardPrimaryExecutionDecisionSnapshot primary, VanguardMedicSupportCandidateFact fact)
    {
        if (primary == null || fact == null)
        {
            return false;
        }
        string kind = primary.WindowKind ?? string.Empty;
        if (string.Equals(kind, VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion, StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, VanguardPrimaryExecutionWindowKinds.HardReturnMovement, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (kind.IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0
            && kind.IndexOf("MedicSupport", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }
        return true;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

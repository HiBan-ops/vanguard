#if SPT_CLIENT
using System;
using UnityEngine;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Produces a bounded pair rendezvous geometry for Medic Support without owning locomotion.
// Flow: Prefer a currently protected patient or caregiver position; otherwise borrow nearby authored stationary-medical topology, then fall back to a projected midpoint with collision-safe pair offsets.
// Authority boundary: Read-only geometry/path preflight; movement is executed only by VanguardMedicRendezvousMovementService after scheduler/SAIN admission.
// Invariant: The planner never relocates actors directly and never claims that an authored or midpoint anchor is safe until current medical safety is re-read at arrival.
namespace Vanguard.Client.Runtime.Medical.Support;

internal sealed class VanguardMedicalRendezvousPlan
{
    public Vector3 Center { get; init; }
    public Vector3 CaregiverDestination { get; init; }
    public Vector3 PatientDestination { get; init; }
    public bool MoveCaregiver { get; init; }
    public bool MovePatient { get; init; }
    public string Source { get; init; } = "none";
    public string Summary => "source=" + Source
        + ";moveCaregiver=" + (MoveCaregiver ? "true" : "false")
        + ";movePatient=" + (MovePatient ? "true" : "false")
        + ";center=" + Format(Center)
        + ";caregiver=" + Format(CaregiverDestination)
        + ";patient=" + Format(PatientDestination);

    private static string Format(Vector3 value) => value.x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        + "," + value.y.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        + "," + value.z.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

internal static class VanguardMedicalRendezvousPlanner
{
    public const string StatusTag = "VANGUARD_MEDIC_RENDEZVOUS_PLAN_STATUS";
    private const float PairSeparationMeters = 1.05f;
    private const float AuthoredSearchMeters = 16.0f;

    public static bool TryResolve(
        OperatorDecisionSnapshot caregiver,
        OperatorDecisionSnapshot patient,
        out VanguardMedicalRendezvousPlan plan,
        out string reason)
    {
        plan = null!;
        reason = "none";
        if (caregiver == null || patient == null || !caregiver.Alive || !patient.Alive)
        {
            reason = "pair_snapshot_missing_or_dead";
            return false;
        }

        Vector3 center;
        string source;
        bool moveCaregiver;
        bool movePatient;

        if (patient.Medical.Safety.SafeForStationaryAid || patient.Medical.Safety.CoveredOrHoldingAngle)
        {
            center = patient.Position;
            source = "patient_current_protected_position";
            moveCaregiver = true;
            movePatient = false;
        }
        else if (caregiver.Medical.Safety.SafeForStationaryAid || caregiver.Medical.Safety.CoveredOrHoldingAngle)
        {
            center = caregiver.Position;
            source = "caregiver_current_protected_position";
            moveCaregiver = false;
            movePatient = true;
        }
        else if (VanguardStationaryMedicalTacticalPlacementResolver.TryResolvePreferredPoseSlot(
                     patient,
                     AuthoredSearchMeters,
                     out VanguardStationaryMedicalTacticalPlacementCandidate authored,
                     out string authoredReason))
        {
            center = authored.Target;
            source = "authored_stationary_medical:" + authored.Source + ":" + Safe(authoredReason);
            moveCaregiver = true;
            movePatient = true;
        }
        else
        {
            Vector3 midpoint = (caregiver.Position + patient.Position) * 0.5f;
            if (!VanguardTacticalPlacementSolver.TryProjectRuntimeAnchor(midpoint, 3.0f, out center))
            {
                reason = "midpoint_projection_failed";
                return false;
            }
            source = "projected_pair_midpoint";
            moveCaregiver = true;
            movePatient = true;
        }

        Vector3 axis = patient.Position - caregiver.Position;
        axis.y = 0f;
        if (axis.sqrMagnitude < 0.01f)
        {
            axis = Vector3.right;
        }
        axis.Normalize();
        Vector3 lateral = new Vector3(-axis.z, 0f, axis.x) * (PairSeparationMeters * 0.5f);

        Vector3 caregiverDestination = center - lateral;
        Vector3 patientDestination = center + lateral;
        if (!VanguardTacticalPlacementSolver.TryProjectRuntimeAnchor(caregiverDestination, 2.0f, out caregiverDestination))
        {
            caregiverDestination = center;
        }
        if (!VanguardTacticalPlacementSolver.TryProjectRuntimeAnchor(patientDestination, 2.0f, out patientDestination))
        {
            patientDestination = center;
        }

        if (!moveCaregiver)
        {
            caregiverDestination = caregiver.Position;
        }
        if (!movePatient)
        {
            patientDestination = patient.Position;
        }

        plan = new VanguardMedicalRendezvousPlan
        {
            Center = center,
            CaregiverDestination = caregiverDestination,
            PatientDestination = patientDestination,
            MoveCaregiver = moveCaregiver,
            MovePatient = movePatient,
            Source = source
        };
        reason = "rendezvous_plan_resolved:" + source;
        return true;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

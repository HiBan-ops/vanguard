#if SPT_CLIENT
using System;
using System.Globalization;
using EFT.Interactive;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Owns one bounded in-raid Medic Support pair/session across rendezvous, supply handoff/pickup or direct teammate care.
// Flow: Read-only triage selects one pair and exact Medic-owned item; the session survives bounded movement and mutation phases until ownership/treatment is verified or a higher authority interrupts it.
// Authority boundary: Reservation/state only; movement, inventory transfer and health effects remain in their dedicated executors and scheduler/SAIN gates.
// Invariant: At most one active support pair exists in the current qualification scope, every mutation is tied to the exact item/session generation, and every terminal path releases pair movement/claims.
namespace Vanguard.Client.Runtime.Medical.Support;

internal enum VanguardMedicSupportMode
{
    SupplyDrop,
    DirectCare
}

internal static class VanguardMedicSupportDoctrine
{
    public const string SupportIntentKey = "MedicSupport";
    public const string SupplyDropIntentKey = "MedicSupportSupplyDrop";
    public const string DirectCareIntentKey = "MedicSupportDirectCare";
    public const string SupplyDropWindowKind = "MedicSupportSupplyDropMedicalWindow";
    public const string DirectCareWindowKind = "MedicSupportDirectCareMedicalWindow";
    public const string RendezvousWindowKind = "MedicSupportRendezvousMedicalWindow";
    public const string PickupWindowKind = "MedicSupportPickupMedicalWindow";
    public const string SupplyDropStatusTag = "VANGUARD_MEDIC_SUPPLY_DROP_STATUS";
    public const string SupplyDropWorldProofTag = "VANGUARD_MEDIC_SUPPLY_DROP_WORLD_PROOF";
    public const string PickupStatusTag = "VANGUARD_MEDIC_TARGETED_PICKUP_STATUS";
    public const string DirectCareStatusTag = "VANGUARD_MEDIC_DIRECT_CARE_STATUS";
    public const string SessionStatusTag = "VANGUARD_MEDIC_SUPPORT_SESSION_STATUS";
    public const float NativePartnerAdmissionMeters = 2.0f;
    public const float DirectCarePairMeters = 1.75f;
    public const float PickupReachMeters = 1.35f;
    public const float StationarySpeedMaxMetersPerSecond = 0.35f;
    public const float DirectCareDurationScale = 0.50f;
    public const double CandidateFreshnessSeconds = 2.25d;
    public const double AdmissionIntervalSeconds = 0.20d;
    public const double SupplyDropWindowMaxSeconds = 8.0d;
    public const double SupplyDropNoProgressSeconds = 4.0d;
    public const double WorldObservationTimeoutSeconds = 2.0d;
    public const double PickupWindowMaxSeconds = 12.0d;
    public const double DirectCareWindowMaxSeconds = 45.0d;
    public const double RendezvousWindowMaxSeconds = 18.0d;
    public const double SupplySessionMaxSeconds = 30.0d;
    public const double SupplySessionProgressingAbsoluteMaxSeconds = 60.0d;
    public const double DirectCareSessionMaxSeconds = 55.0d;
}

internal sealed class VanguardMedicSupportSession
{
    public string SessionId { get; init; } = "none";
    public VanguardMedicSupportMode Mode { get; init; } = VanguardMedicSupportMode.SupplyDrop;
    public string CaregiverOperatorId { get; init; } = "none";
    public string CaregiverBotProfileId { get; init; } = "none";
    public string CaregiverOwnerProfileId { get; init; } = "none";
    public string PatientOperatorId { get; init; } = "none";
    public string PatientBotProfileId { get; init; } = "none";
    public string PatientOwnerProfileId { get; init; } = "none";
    public VanguardMedicalNeed Need { get; init; } = VanguardMedicalNeed.None;
    public string TargetPart { get; init; } = "none";
    public string ItemTemplateId { get; init; } = "none";
    public string ItemInstanceId { get; init; } = "none";
    public string ItemName { get; init; } = "none";
    public float InitialItemResource { get; init; } = -1f;
    public float InitialItemMaxResource { get; init; } = -1f;
    public float InitialDistanceMeters { get; init; }
    public string Phase { get; set; } = "Reserved";
    public string PrimaryWindowId { get; set; } = "none";
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastProgressAtUtc { get; set; } = DateTimeOffset.MinValue;
    // Only genuine mover distance reduction updates this field. Phase heartbeats
    // deliberately cannot extend the pair's bounded runtime deadline.
    public DateTimeOffset LastPhysicalPairProgressAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset DropRequestedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool WorldItemObserved { get; set; }
    public DateTimeOffset WorldItemObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string WorldItemLastOwnerProfileId { get; set; } = "none";
    public Vector3 WorldItemPosition { get; set; }
    public LootItem? WorldLootItem { get; set; }
    public DateTimeOffset NextDonorReadbackAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int DonorReadbackAttempts { get; set; }
    public bool PickupRequested { get; set; }
    public DateTimeOffset PickupRequestedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool PickupConfirmed { get; set; }
    public DateTimeOffset PickupConfirmedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool DirectCareCommitStarted { get; set; }
    public bool DirectCareCommitted { get; set; }
    public DateTimeOffset DirectCareCommittedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public float DirectCareHealthBefore { get; set; } = -1f;
    public float DirectCareResourceBefore { get; set; } = -1f;
    public float DirectCareExpectedEffectSeconds { get; set; } = -1f;
    public string TerminalOutcome { get; set; } = "none";
    public string TerminalReason { get; set; } = "none";

    public string Summary => "session=" + Safe(SessionId)
        + ";mode=" + Mode
        + ";caregiverOperator=" + Safe(CaregiverOperatorId)
        + ";caregiverBot=" + Safe(CaregiverBotProfileId)
        + ";patientOperator=" + Safe(PatientOperatorId)
        + ";patientBot=" + Safe(PatientBotProfileId)
        + ";need=" + Need
        + ";target=" + Safe(TargetPart)
        + ";item=" + Safe(ItemName)
        + ";tpl=" + Safe(ItemTemplateId)
        + ";itemInstance=" + Safe(ItemInstanceId)
        + ";resource0=" + InitialItemResource.ToString("0.0", CultureInfo.InvariantCulture)
        + "/" + InitialItemMaxResource.ToString("0.0", CultureInfo.InvariantCulture)
        + ";distance0=" + InitialDistanceMeters.ToString("0.00", CultureInfo.InvariantCulture)
        + ";phase=" + Safe(Phase)
        + ";window=" + Safe(PrimaryWindowId)
        + ";worldObserved=" + Bool(WorldItemObserved)
        + ";pickupRequested=" + Bool(PickupRequested)
        + ";pickupConfirmed=" + Bool(PickupConfirmed)
        + ";directCommit=" + Bool(DirectCareCommitted)
        + ";outcome=" + Safe(TerminalOutcome)
        + ";reason=" + Safe(TerminalReason);

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal static class VanguardMedicSupportSessionStore
{
    private static readonly object Sync = new();
    private static VanguardMedicSupportSession? active;

    public static void ResetForRaidLifecycle(string reason)
    {
        VanguardMedicSupportSession? released;
        lock (Sync)
        {
            released = active;
            active = null;
        }
        VanguardClientDiagnosticsLog.Info(
            VanguardMedicSupportDoctrine.SessionStatusTag,
            $"VANGUARD_MEDIC_SUPPORT_SESSION_RESET reason={Safe(reason)}; hadActive={Bool(released != null)}; persistentMutation=false");
    }

    public static bool TryGetActive(out VanguardMedicSupportSession session)
    {
        lock (Sync)
        {
            if (active != null)
            {
                session = active;
                return true;
            }
        }
        session = null!;
        return false;
    }

    public static bool TryReserveSupplyDrop(
        VanguardMedicSupportCandidateFact fact,
        string itemInstanceId,
        float itemResource,
        float itemMaxResource,
        DateTimeOffset now,
        out VanguardMedicSupportSession session,
        out string reason)
        => TryReserve(fact, VanguardMedicSupportMode.SupplyDrop, itemInstanceId, itemResource, itemMaxResource, now, out session, out reason);

    public static bool TryReserveDirectCare(
        VanguardMedicSupportCandidateFact fact,
        string itemInstanceId,
        float itemResource,
        float itemMaxResource,
        DateTimeOffset now,
        out VanguardMedicSupportSession session,
        out string reason)
        => TryReserve(fact, VanguardMedicSupportMode.DirectCare, itemInstanceId, itemResource, itemMaxResource, now, out session, out reason);

    private static bool TryReserve(
        VanguardMedicSupportCandidateFact fact,
        VanguardMedicSupportMode mode,
        string itemInstanceId,
        float itemResource,
        float itemMaxResource,
        DateTimeOffset now,
        out VanguardMedicSupportSession session,
        out string reason)
    {
        session = null!;
        reason = "none";
        bool laneEligible = mode == VanguardMedicSupportMode.SupplyDrop ? fact?.SupplyDropDryRunEligible == true : fact?.DirectCareDryRunEligible == true;
        if (fact == null || !laneEligible)
        {
            reason = "candidate_not_lane_eligible:" + mode;
            return false;
        }
        if (string.IsNullOrWhiteSpace(itemInstanceId) || string.Equals(itemInstanceId, "none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "exact_item_instance_missing";
            return false;
        }

        lock (Sync)
        {
            if (active != null)
            {
                reason = "single_pair_session_busy:" + Safe(active.SessionId);
                return false;
            }

            session = new VanguardMedicSupportSession
            {
                SessionId = "medsup_" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                    + "_" + Safe(fact.CaregiverBotProfileId) + "_" + Safe(fact.PatientBotProfileId),
                Mode = mode,
                CaregiverOperatorId = fact.CaregiverOperatorId,
                CaregiverBotProfileId = fact.CaregiverBotProfileId,
                CaregiverOwnerProfileId = fact.CaregiverOwnerProfileId,
                PatientOperatorId = fact.PatientOperatorId,
                PatientBotProfileId = fact.PatientBotProfileId,
                PatientOwnerProfileId = fact.PatientOwnerProfileId,
                Need = fact.PatientNeed,
                TargetPart = fact.PatientTargetPart,
                ItemTemplateId = fact.CandidateItemTemplateId,
                ItemInstanceId = itemInstanceId,
                ItemName = fact.CandidateItemName,
                InitialItemResource = itemResource,
                InitialItemMaxResource = itemMaxResource,
                InitialDistanceMeters = fact.DirectDistanceMeters,
                Phase = "Reserved",
                StartedAtUtc = now,
                LastProgressAtUtc = now
            };
            active = session;
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardMedicSupportDoctrine.SessionStatusTag,
            $"VANGUARD_MEDIC_SUPPORT_SESSION_RESERVED {session.Summary}; itemReservation=true; pairReservation=true; exactlyOnce=true; persistentMutation=false");
        reason = "reserved";
        return true;
    }

    public static bool TryMarkPhysicalPairProgress(string sessionId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId)) return false;
            active.LastPhysicalPairProgressAtUtc = now;
            return true;
        }
    }

    public static bool TrySetPhase(string sessionId, string phase, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId)) return false;
            active.Phase = Normalize(phase);
            active.LastProgressAtUtc = now;
            return true;
        }
    }

    public static bool TryAttachPrimaryWindow(string sessionId, string windowId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId)) return false;
            active.PrimaryWindowId = Normalize(windowId);
            active.Phase = "PrimaryWindowGranted";
            active.LastProgressAtUtc = now;
            return true;
        }
    }

    public static bool TryMarkDropRequested(string sessionId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId)) return false;
            active.Phase = "DropRequested";
            active.DropRequestedAtUtc = now;
            active.LastProgressAtUtc = now;
            return true;
        }
    }

    public static bool TryObserveWorldItem(LootItem lootItem, DateTimeOffset now, out VanguardMedicSupportSession session)
    {
        string itemInstanceId = Normalize(lootItem?.Item?.Id);
        lock (Sync)
        {
            if (active == null || lootItem == null || !Same(active.ItemInstanceId, itemInstanceId))
            {
                session = null!;
                return false;
            }

            active.WorldItemObserved = true;
            active.WorldItemObservedAtUtc = now;
            active.WorldItemLastOwnerProfileId = Normalize(lootItem.LastOwner?.ProfileId);
            active.WorldItemPosition = lootItem.transform.position;
            active.WorldLootItem = lootItem;
            active.NextDonorReadbackAtUtc = now + TimeSpan.FromSeconds(0.10d);
            active.DonorReadbackAttempts = 0;
            active.Phase = "WorldItemObserved";
            active.LastProgressAtUtc = now;
            session = active;
            return true;
        }
    }

    public static bool TryDeferDonorReadback(string sessionId, DateTimeOffset nextAtUtc, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId)) return false;
            active.DonorReadbackAttempts = Math.Max(0, active.DonorReadbackAttempts + 1);
            active.NextDonorReadbackAtUtc = nextAtUtc;
            active.LastProgressAtUtc = now;
            return true;
        }
    }

    public static bool TryMarkPickupRequested(string sessionId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId) || active.PickupRequested) return false;
            active.PickupRequested = true;
            active.PickupRequestedAtUtc = now;
            active.Phase = "PickupRequested";
            active.LastProgressAtUtc = now;
            return true;
        }
    }

    public static bool TryMarkPickupConfirmed(string sessionId, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId)) return false;
            active.PickupConfirmed = true;
            active.PickupConfirmedAtUtc = now;
            active.Phase = "PickupConfirmed";
            active.LastProgressAtUtc = now;
            return true;
        }
    }

    public static bool TryBeginDirectCareCommit(
        string sessionId,
        float healthBefore,
        float resourceBefore,
        DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId) || active.DirectCareCommitStarted) return false;
            active.DirectCareCommitStarted = true;
            active.DirectCareHealthBefore = healthBefore;
            active.DirectCareResourceBefore = resourceBefore;
            active.Phase = "DirectCareCommitStarted";
            active.LastProgressAtUtc = now;
            return true;
        }
    }

    public static bool TryMarkDirectCareCommitted(string sessionId, float expectedEffectSeconds, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId) || !active.DirectCareCommitStarted || active.DirectCareCommitted) return false;
            active.DirectCareCommitted = true;
            active.DirectCareCommittedAtUtc = now;
            active.DirectCareExpectedEffectSeconds = expectedEffectSeconds;
            active.Phase = "DirectCareEffectRunning";
            active.LastProgressAtUtc = now;
            return true;
        }
    }

    public static bool TryFinish(string sessionId, string outcome, string reason, out VanguardMedicSupportSession finished)
    {
        lock (Sync)
        {
            if (active == null || !Same(active.SessionId, sessionId))
            {
                finished = null!;
                return false;
            }
            active.TerminalOutcome = Normalize(outcome);
            active.TerminalReason = Normalize(reason);
            active.Phase = "Terminal";
            finished = active;
            active = null;
            return true;
        }
    }

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using EFT;
using UnityEngine;

// Responsibility: Defines data/state contracts used by the grenade emergency runtime, centered on Grenade Emergency Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Grenades;

internal enum VanguardGrenadeSourceIdentityKind
{
    Unknown = 0,
    Ai = 10,
    PlayerClient = 20,
    PlayerOwner = 30,
    Operator = 40,
}

internal enum VanguardGrenadeLocalRelation
{
    Unknown = 0,
    Self = 1,
    Friendly = 2,
    Hostile = 3,
}

internal enum VanguardGrenadeEmergencyPhase
{
    None = 0,
    NativeRequested = 1,
    NativeProgress = 2,
    FallbackPlanning = 3,
    FallbackMoving = 4,
    HoldingSafety = 5,
    Terminal = 6,
}

internal enum VanguardGrenadeEmergencyTerminalKind
{
    None = 0,
    SafeDistanceReached = 1,
    SolidCoverReached = 2,
    GrenadeExplodedAndHazardCleared = 3,
    GrenadeDestroyed = 4,
    OperatorDead = 5,
    OperatorIncapacitated = 6,
    NoReachableEscape = 7,
    SupersededByHigherRiskGrenade = 8,
    RuntimeObjectLost = 9,
    WindowLost = 10,
    AbsoluteSafetyGuardExpired = 11,
    PhysicalBackendUnavailable = 12,
}

internal readonly struct VanguardGrenadeFuseProfile
{
    public VanguardGrenadeFuseProfile(
        bool known,
        string fuseClass,
        string throwType,
        float declaredFuseSeconds,
        float elapsedSeconds,
        float cookedSeconds,
        float? remainingSeconds,
        float minimumContactSeconds,
        bool contactCapable,
        bool contactArmed,
        float minimumExplosionDistance,
        float maximumExplosionDistance,
        int fragmentsCount,
        float minimumFragmentDamage,
        float maximumFragmentDamage,
        string fragmentType,
        string confidence)
    {
        Known = known;
        FuseClass = Safe(fuseClass);
        ThrowType = Safe(throwType);
        DeclaredFuseSeconds = declaredFuseSeconds;
        ElapsedSeconds = elapsedSeconds;
        CookedSeconds = cookedSeconds;
        RemainingSeconds = remainingSeconds;
        MinimumContactSeconds = minimumContactSeconds;
        ContactCapable = contactCapable;
        ContactArmed = contactArmed;
        MinimumExplosionDistance = minimumExplosionDistance;
        MaximumExplosionDistance = maximumExplosionDistance;
        FragmentsCount = fragmentsCount;
        MinimumFragmentDamage = minimumFragmentDamage;
        MaximumFragmentDamage = maximumFragmentDamage;
        FragmentType = Safe(fragmentType);
        Confidence = Safe(confidence);
    }

    public bool Known { get; }
    public string FuseClass { get; }
    public string ThrowType { get; }
    public float DeclaredFuseSeconds { get; }
    public float ElapsedSeconds { get; }
    public float CookedSeconds { get; }
    public float? RemainingSeconds { get; }
    public float MinimumContactSeconds { get; }
    public bool ContactCapable { get; }
    public bool ContactArmed { get; }
    public float MinimumExplosionDistance { get; }
    public float MaximumExplosionDistance { get; }
    public int FragmentsCount { get; }
    public float MinimumFragmentDamage { get; }
    public float MaximumFragmentDamage { get; }
    public string FragmentType { get; }
    public string Confidence { get; }

    public static VanguardGrenadeFuseProfile Unknown { get; } = new(
        false,
        "unknown",
        "unknown",
        0f,
        0f,
        0f,
        null,
        -1f,
        false,
        false,
        0f,
        0f,
        0,
        0f,
        0f,
        "none",
        "unknown");

    public string Summary => "fuseClass=" + Safe(FuseClass)
        + ";throwType=" + Safe(ThrowType)
        + ";declaredFuse=" + Seconds(DeclaredFuseSeconds)
        + ";elapsed=" + Seconds(ElapsedSeconds)
        + ";cooked=" + Seconds(CookedSeconds)
        + ";remaining=" + (RemainingSeconds.HasValue ? Seconds(RemainingSeconds.Value) : "unknown")
        + ";contactCapable=" + Bool(ContactCapable)
        + ";contactArmed=" + Bool(ContactArmed)
        + ";contactMin=" + Seconds(MinimumContactSeconds)
        + ";blastMin=" + Meters(MinimumExplosionDistance)
        + ";blastMax=" + Meters(MaximumExplosionDistance)
        + ";fragments=" + FragmentsCount.ToString(CultureInfo.InvariantCulture)
        + ";fragmentDamage=" + MinimumFragmentDamage.ToString("0.0", CultureInfo.InvariantCulture) + "-" + MaximumFragmentDamage.ToString("0.0", CultureInfo.InvariantCulture)
        + ";fragmentType=" + Safe(FragmentType)
        + ";confidence=" + Safe(Confidence);

    private static string Seconds(float value) => !IsFinite(value) || value < 0f ? "unknown" : value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Meters(float value) => !IsFinite(value) || value < 0f ? "unknown" : value.ToString("0.00", CultureInfo.InvariantCulture);
    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed class VanguardGrenadeHazardDecisionSnapshot
{
    public static VanguardGrenadeHazardDecisionSnapshot Empty { get; } = new();

    public bool HasRelevantHazard { get; init; }
    public bool ExactTrackedHazard { get; init; }
    public string GrenadeKey { get; init; } = "none";
    public int GrenadeId { get; init; }
    public string GrenadeType { get; init; } = "none";
    public string SourceProfileId { get; init; } = "none";
    public string SourceName { get; init; } = "none";
    public VanguardGrenadeSourceIdentityKind SourceIdentity { get; init; }
    public VanguardGrenadeLocalRelation SourceRelation { get; init; }
    public Vector3 GrenadePosition { get; init; }
    public Vector3 Velocity { get; init; }
    public Vector3 DangerPoint { get; init; }
    public bool DangerPointKnown { get; init; }
    public float DistanceToGrenade { get; init; } = float.PositiveInfinity;
    public float DistanceToDangerPoint { get; init; } = float.PositiveInfinity;
    public float EffectiveDistance { get; init; } = float.PositiveInfinity;
    public float VerticalDelta { get; init; }
    public bool LineOfEffectKnown { get; init; }
    public bool LineOfEffectBlocked { get; init; }
    public bool ActualLineOfEffectKnown { get; init; }
    public bool ActualLineOfEffectBlocked { get; init; }
    public bool PredictedLineOfEffectKnown { get; init; }
    public bool PredictedLineOfEffectBlocked { get; init; }
    public bool DualSolidCover { get; init; }
    public bool ApproachingOperator { get; init; }
    public float PredictedClosestDistance { get; init; } = float.PositiveInfinity;
    public bool NativeDangerPresent { get; init; }
    public bool NativeShallRunAway { get; init; }
    public float? EstimatedTimeToExplosionSeconds { get; init; }
    public string TimeConfidence { get; init; } = "unknown";
    public VanguardGrenadeFuseProfile FuseProfile { get; init; } = VanguardGrenadeFuseProfile.Unknown;
    public float NativeProbeSeconds { get; init; }
    public float RecommendedAbsoluteWindowSeconds { get; init; }
    public float NativeAddDangerThreshold { get; init; }
    public float NativeRunAwayThreshold { get; init; }
    public float AdmissionDistance { get; init; }
    public float SafeDistance { get; init; }
    public float RiskScore { get; init; }
    public bool Critical { get; init; }
    public bool Imminent { get; init; }
    public string AdmissionReason { get; init; } = "none";
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.MinValue;

    public bool IsSafetyEnvelopeSatisfied => EffectiveDistance >= SafeDistance + VanguardGrenadeEmergencyPolicy.SafeDistanceHysteresisMeters
        || DualSolidCover && EffectiveDistance >= VanguardGrenadeEmergencyPolicy.SolidCoverMinimumDistanceMeters;

    public string DecisionSignature => HasRelevantHazard
        ? GrenadeKey + ":" + (Critical ? "critical" : "relevant") + ":" + SourceRelation + ":" + (DualSolidCover ? "dual_cover" : "exposed")
        : "grenade_none";

    public string Summary => "grenade=" + Safe(GrenadeKey)
        + ";type=" + Safe(GrenadeType)
        + ";source=" + Safe(SourceProfileId)
        + ";sourceIdentity=" + SourceIdentity
        + ";sourceRelation=" + SourceRelation
        + ";distance=" + Meters(EffectiveDistance)
        + ";grenadeDistance=" + Meters(DistanceToGrenade)
        + ";dangerDistance=" + Meters(DistanceToDangerPoint)
        + ";closest=" + Meters(PredictedClosestDistance)
        + ";vertical=" + VerticalDelta.ToString("0.0", CultureInfo.InvariantCulture)
        + ";approaching=" + Bool(ApproachingOperator)
        + ";actualBlocked=" + Bool(ActualLineOfEffectKnown && ActualLineOfEffectBlocked)
        + ";predictedBlocked=" + Bool(PredictedLineOfEffectKnown && PredictedLineOfEffectBlocked)
        + ";dualCover=" + Bool(DualSolidCover)
        + ";nativeDanger=" + Bool(NativeDangerPresent)
        + ";nativeRun=" + Bool(NativeShallRunAway)
        + ";critical=" + Bool(Critical)
        + ";imminent=" + Bool(Imminent)
        + ";safeDistance=" + Meters(SafeDistance)
        + ";nativeProbe=" + NativeProbeSeconds.ToString("0.00", CultureInfo.InvariantCulture)
        + ";absoluteWindow=" + RecommendedAbsoluteWindowSeconds.ToString("0.00", CultureInfo.InvariantCulture)
        + ";risk=" + RiskScore.ToString("0.0", CultureInfo.InvariantCulture)
        + ";admission=" + Safe(AdmissionReason)
        + ";" + FuseProfile.Summary;

    private static string Meters(float value) => float.IsInfinity(value) || float.IsNaN(value)
        ? "unknown"
        : value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed class VanguardGrenadeEmergencyOperatorState
{
    public string OperatorId = "none";
    public string BotProfileId = "none";
    public string WindowId = "none";
    public string GrenadeKey = "none";
    public VanguardGrenadeEmergencyPhase Phase;
    public DateTimeOffset StartedAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset AbsoluteUntilUtc = DateTimeOffset.MinValue;
    public DateTimeOffset PhaseStartedAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset LastProgressAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset LastPlanAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset NextFallbackCycleAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset HoldingStartedAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset LastHoldingLogAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset RuntimeLostSinceUtc = DateTimeOffset.MinValue;
    public Vector3 StartPosition;
    public Vector3 LastPosition;
    public Vector3 GrenadePosition;
    public Vector3 DangerPoint;
    public bool DangerPointKnown;
    public float StartDistance = float.PositiveInfinity;
    public float LastDistance = float.PositiveInfinity;
    public float BestDistance = float.NegativeInfinity;
    public float SafeDistance;
    public float NativeProbeSeconds;
    public Vector3 FallbackDestination;
    public bool FallbackDestinationKnown;
    public List<Vector3> FailedFallbackDestinations = new();
    public long MovementGeneration;
    public int FallbackPlans;
    public int TotalFallbackPlanAttempts;
    public int TotalValidFallbackPlans;
    public int TotalFallbackCommandsIssued;
    public int FallbackCycles;
    public int PhysicalBackendRepairAttempts;
    public int WindowRecoveryAttempts;
    public int StickySameAnchorBackendResets;
    public int StickyHoldEvents;
    public DateTimeOffset LastStickyHoldLogAtUtc = DateTimeOffset.MinValue;
    public bool WindowRecoveryBudgetExhausted;
    public DateTimeOffset WindowRecoveryBudgetExhaustedAtUtc = DateTimeOffset.MinValue;
    public bool NativeProgressLogged;
    public bool HostileSourcePropagated;
    public VanguardGrenadeHazardDecisionSnapshot LastHazard = VanguardGrenadeHazardDecisionSnapshot.Empty;
}

internal readonly struct VanguardGrenadeFallbackPlan
{
    public VanguardGrenadeFallbackPlan(bool valid, Vector3 destination, float pathLength, bool solidCover, float score, string summary)
    {
        Valid = valid;
        Destination = destination;
        PathLength = pathLength;
        SolidCover = solidCover;
        Score = score;
        Summary = summary;
    }

    public bool Valid { get; }
    public Vector3 Destination { get; }
    public float PathLength { get; }
    public bool SolidCover { get; }
    public float Score { get; }
    public string Summary { get; }

    public static VanguardGrenadeFallbackPlan None(string reason) => new(false, Vector3.zero, 0f, false, float.NegativeInfinity, reason);
}
#endif

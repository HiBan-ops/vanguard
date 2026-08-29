#if SPT_CLIENT
using System;
using EFT;
using UnityEngine;

// Responsibility: Defines data/state contracts used by the grenade emergency runtime, centered on Grenade Observation Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Grenades;

internal enum VanguardGrenadeSourceRelation
{
    Unknown,
    Operator,
    PlayerOwner,
    PlayerClient,
    HostileOrNeutral,
}

internal enum VanguardGrenadeTerminalKind
{
    None,
    Exploded,
    Destroyed,
    TimedOut,
}

internal sealed class VanguardGrenadeObservation
{
    public Grenade Grenade { get; init; } = null!;
    public int GrenadeId { get; init; }
    public string GrenadeType { get; init; } = "unknown";
    public string SourceProfileId { get; set; } = "none";
    public string SourceName { get; set; } = "none";
    public VanguardGrenadeSourceRelation SourceRelation { get; set; }
    public Vector3 ThrowPosition { get; set; }
    public Vector3 ThrowForce { get; set; }
    public float ThrowMass { get; set; }
    public int CollisionCount { get; set; }
    public float LastCollisionMaxRange { get; set; }
    public Vector3 CurrentPosition { get; set; }
    public Vector3 DangerPoint { get; set; }
    public bool DangerPointKnown { get; set; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public bool DestroySubscribed { get; set; }
    public Vector3 ExplosionPosition { get; set; }
    public VanguardGrenadeTerminalKind TerminalKind { get; set; }
    public DateTimeOffset TerminalAtUtc { get; set; } = DateTimeOffset.MinValue;
    public readonly System.Collections.Generic.Dictionary<string, VanguardGrenadeOperatorObservation> Operators = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class VanguardGrenadeOperatorObservation
{
    public string OperatorId { get; init; } = "none";
    public string BotProfileId { get; init; } = "none";
    public string OwnerProfileId { get; init; } = "none";
    public string Nickname { get; init; } = "none";
    public bool Alive { get; set; }
    public bool Relevant { get; set; }
    public bool Critical { get; set; }
    public bool EverRelevant { get; set; }
    public bool EverCritical { get; set; }
    public DateTimeOffset RelevantSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastRelevantAtUtc { get; set; } = DateTimeOffset.MinValue;
    public Vector3 RelevantEntryOperatorPosition { get; set; }
    public bool RelevantEntryOperatorPositionKnown { get; set; }
    public Vector3 LastOperatorPosition { get; set; }
    public bool LastOperatorPositionKnown { get; set; }
    public float GrenadeDistance { get; set; } = float.PositiveInfinity;
    public float DangerDistance { get; set; } = float.PositiveInfinity;
    public float MinimumGrenadeDistance { get; set; } = float.PositiveInfinity;
    public float MinimumDangerDistance { get; set; } = float.PositiveInfinity;
    public float NativeAddDangerThreshold { get; set; } = VanguardGrenadeHazardPolicy.FallbackRelevantDistanceMeters;
    public float NativeRunAwayThreshold { get; set; } = VanguardGrenadeHazardPolicy.FallbackCriticalDistanceMeters;
    public float NativeRunAwaySqrValue { get; set; } = VanguardGrenadeHazardPolicy.FallbackCriticalDistanceMeters;
    public bool LineOfEffectKnown { get; set; }
    public bool LineOfEffectBlocked { get; set; }
    public DateTimeOffset NextGeometryProbeAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool SainReactionObserved { get; set; }
    public bool SainTrackerCreated { get; set; }
    public bool SainTrackerFallbackToNative { get; set; }
    public bool SainReactionReturnedWithoutTrackerOrNative { get; set; }
    public bool SainSpotted { get; set; }
    public bool SainCanReactObserved { get; set; }
    public bool NativeDangerRequestObserved { get; set; }
    public bool NativeDangerWritten { get; set; }
    public bool NativeDangerPointPresent { get; set; }
    public bool NativeDangerLogged { get; set; }
    public bool NativeRunAwayObserved { get; set; }
    public bool NativeShallRunAway { get; set; }
    public DateTimeOffset NativeShallRunAwayAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool NativeEvasionExecutionObserved { get; set; }
    public DateTimeOffset NativeEvasionExecutionAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool MovementObserved { get; set; }
    public float MaximumAwayDisplacementMeters { get; set; }
    public float MaximumOperatorDisplacementMeters { get; set; }
    public bool EvasionProgressObserved { get; set; }
    public DateTimeOffset EvasionProgressAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool ReactionMissedLogged { get; set; }
    public bool EvasionNoProgressLogged { get; set; }
    public bool SainDecisionEventObserved { get; set; }
    public bool SainAvoidGrenadeObserved { get; set; }
    public DateTimeOffset SainAvoidGrenadeAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string LastSainDecision { get; set; } = "none";
    public string LastSainSquadDecision { get; set; } = "none";
    public string LastSainSelfDecision { get; set; } = "none";
    public string LastBrainLayer { get; set; } = "none";
    public string LastBrainNode { get; set; } = "none";
    public string LastMovementAuthority { get; set; } = "none";
    public string LastMovementAuthorityReason { get; set; } = "none";
    public string LastExecutionIntent { get; set; } = "none";
    public string LastExecutionWindow { get; set; } = "none";
    public string LastMovementClassification { get; set; } = "none";
    public float LastRealSpeed { get; set; }
    public bool TerminalLogged { get; set; }
}
#endif

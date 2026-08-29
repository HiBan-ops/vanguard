#if SPT_CLIENT

// Responsibility: Encodes the deterministic rules for Grenade Hazard Policy within the grenade emergency runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Grenades;

/// <summary>
/// Read-only grenade diagnostic policy for grenade subsystem. These values classify evidence for telemetry only.
/// They do not move an Operator, alter SAIN decisions, inject targets or open execution authority.
/// </summary>
internal static class VanguardGrenadeHazardPolicy
{
    public const string StatusTag = "VANGUARD_GRENADE_HAZARD_DIAGNOSTIC_FOUNDATION_STATUS";
    public const string ThrowObservedTag = "VANGUARD_GRENADE_THROW_OBSERVED";
    public const string SourceResolvedTag = "VANGUARD_GRENADE_SOURCE_RESOLVED";
    public const string DangerUpdatedTag = "VANGUARD_GRENADE_DANGER_POINT_UPDATED";
    public const string CollisionObservedTag = "VANGUARD_GRENADE_COLLISION_OBSERVED";
    public const string RelevanceEnteredTag = "VANGUARD_GRENADE_OPERATOR_RELEVANCE_ENTERED";
    public const string RelevanceExitedTag = "VANGUARD_GRENADE_OPERATOR_RELEVANCE_EXITED";
    public const string TrackerCreatedTag = "VANGUARD_GRENADE_SAIN_TRACKER_CREATED";
    public const string SpottedTag = "VANGUARD_GRENADE_SAIN_SPOTTED";
    public const string ReactionReadyTag = "VANGUARD_GRENADE_SAIN_REACTION_READY";
    public const string NativeDangerWrittenTag = "VANGUARD_GRENADE_NATIVE_DANGER_WRITTEN";
    public const string NativeRunAwayTag = "VANGUARD_GRENADE_NATIVE_RUN_AWAY_STATE";
    public const string NativeExecutionTag = "VANGUARD_GRENADE_NATIVE_EVASION_EXECUTION";
    public const string SainDecisionTag = "VANGUARD_GRENADE_SAIN_DECISION_CHANGED";
    public const string MovementTag = "VANGUARD_GRENADE_OPERATOR_MOVEMENT_CHANGED";
    public const string ExplosionTag = "VANGUARD_GRENADE_EXPLODED";
    public const string TerminalTag = "VANGUARD_GRENADE_DIAGNOSTIC_TERMINAL";
    public const string MissedReactionTag = "VANGUARD_GRENADE_REACTION_MISSED";
    public const string EvasionNoProgressTag = "VANGUARD_GRENADE_EVASION_NO_PROGRESS";

    public const float FallbackRelevantDistanceMeters = 18f;
    public const float FallbackCriticalDistanceMeters = 10f;
    public const float ImmediatePhysicalProximityMeters = 4f;
    public const float MovementTransitionMeters = 0.35f;
    public const float EvasionProgressMeters = 0.75f;
    public const float SignificantDangerPointChangeMeters = 0.75f;
    public const float LineOfEffectProbeHeightMeters = 1.0f;
    public const float TickIntervalSeconds = 0.10f;
    public const float GeometryProbeIntervalSeconds = 0.25f;
    public const float TerminalRetentionSeconds = 6f;
    public const float DestroyedExplosionSettleSeconds = 1.0f;
    public const float MissingReactionGraceSeconds = 1.50f;
    public const float PreRelevanceReactionLookbackSeconds = 0.25f;
    public const float LongLivedGrenadeTimeoutSeconds = 30f;

}
#endif

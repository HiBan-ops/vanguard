#if SPT_CLIENT
using System;

// Responsibility: Encodes the deterministic rules for Grenade Emergency Policy within the grenade emergency runtime.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Client.Runtime.Grenades;

internal static class VanguardGrenadeEmergencyPolicy
{
    public const string StatusTag = "VANGUARD_EMERGENCY_GRENADE_EVASION_STATUS";
    public const string SafetyContinuityStatusTag = "VANGUARD_GRENADE_SAFETY_CONTINUITY_AND_FUSE_AWARENESS_STATUS";
    public const string PhysicalEvasionLeaseStatusTag = VanguardGrenadeEmergencyPhysicalDriver.StatusTag;
    public const string PhysicalLeaseCleanupStatusTag = VanguardGrenadeEmergencyPhysicalDriver.CleanupStatusTag;
    public const string PhysicalIgnitionGraceStatusTag = VanguardGrenadeEmergencyPhysicalDriver.IgnitionGraceStatusTag;
    public const string ImmediateSainCombatLeaseStatusTag = "VANGUARD_IMMEDIATE_SAIN_COMBAT_EMERGENCY_LEASE_STATUS";
    public const string AdmittedTag = "GRENADE_EMERGENCY_ADMITTED";
    public const string ActivityPreemptedTag = "GRENADE_ACTIVITY_PREEMPTED";
    public const string PathAuthorityAcquiredTag = "GRENADE_PATH_AUTHORITY_ACQUIRED";
    public const string FuseProfileTag = "GRENADE_FUSE_PROFILE";
    public const string NativeRequestedTag = "GRENADE_NATIVE_EVASION_REQUESTED";
    public const string NativeProgressTag = "GRENADE_NATIVE_EVASION_PROGRESS";
    public const string NativeFailedTag = "GRENADE_NATIVE_EVASION_FAILED";
    public const string NativeBypassedForSainTag = "GRENADE_NATIVE_BYPASSED_FOR_SAIN_LOCOMOTION";
    public const string FallbackPlannedTag = "GRENADE_FALLBACK_PLANNED";
    public const string FallbackStartedTag = "GRENADE_FALLBACK_STARTED";
    public const string SafeDistanceTag = "GRENADE_SAFE_DISTANCE_REACHED";
    public const string SolidCoverTag = "GRENADE_SOLID_COVER_REACHED";
    public const string SafetyHoldEnteredTag = "GRENADE_SAFETY_HOLD_ENTERED";
    public const string SafetyHoldMaintainedTag = "GRENADE_SAFETY_HOLD_MAINTAINED";
    public const string SafetyHoldBrokenTag = "GRENADE_SAFETY_HOLD_BROKEN";
    public const string HostileSourcePropagatedTag = "GRENADE_HOSTILE_SOURCE_PROPAGATED";
    public const string TerminalTag = "GRENADE_EMERGENCY_TERMINAL";
    public const string RequestKind = "EmergencyGrenadeEvasion";
    public const string SafetyHoldPathMarker = "holding_safety";

    public const float MinimumRelevantDistanceMeters = 12.0f;
    public const float MaximumRelevantDistanceMeters = 22.0f;
    public const float MinimumCriticalDistanceMeters = 7.5f;
    public const float ImmediateDangerDistanceMeters = 4.5f;
    public const float MinimumSafeDistanceMeters = 12.0f;
    public const float MaximumSafeDistanceMeters = 24.0f;
    public const float SolidCoverMinimumDistanceMeters = 5.5f;
    public const float ProgressPositionMeters = 0.28f;
    public const float ProgressAwayMeters = 0.22f;
    public const float ActiveSainLocomotionSpeedMetersPerSecond = 0.20f;
    public const float SafeDistanceHysteresisMeters = 0.55f;
    public const float EmergencyReachDistanceMeters = 0.75f;
    public const float UnknownFuseNativeProbeSeconds = 0.30f;
    public const float NativeStallSeconds = 0.65f;
    public const float FallbackStallSeconds = 0.85f;
    public const float PhysicalIgnitionGraceSeconds = 0.75f;
    public const float PhysicalIgnitionRetrySeconds = 0.18f;
    public const float PhysicalIgnitionPathCheckDelaySeconds = 0.30f;
    public const float FallbackReplanCooldownSeconds = 0.35f;
    public const float FallbackCycleCooldownSeconds = 0.70f;
    public const float MinimumAbsoluteEmergencySeconds = 12.0f;
    public const float MaximumAbsoluteEmergencySeconds = 35.0f;
    public const float FuseSafetyMarginSeconds = 4.0f;
    // The executor owns the functional absolute terminal. The scheduler remains alive slightly
    // longer so TickOperator can record AbsoluteSafetyGuardExpired instead of observing WindowLost.
    public const float SchedulerTerminalGraceSeconds = 1.0f;
    public const float RuntimeObjectLostGraceSeconds = 0.65f;
    public const float ServiceTickSeconds = 0.10f;
    public const float CommandLifetimeSeconds = 4.0f;
    public const float SafetyHoldLogIntervalSeconds = 1.0f;
    public const int MaximumFallbackPlansPerCycle = 3;

    public static float ResolveAdmissionDistance(float nativeAddDanger, float nativeRunAway)
    {
        float native = Math.Max(nativeAddDanger, nativeRunAway);
        return Math.Clamp(Math.Max(MinimumRelevantDistanceMeters, native), MinimumRelevantDistanceMeters, MaximumRelevantDistanceMeters);
    }

    public static float ResolveCriticalDistance(float nativeRunAway)
        => Math.Clamp(Math.Max(MinimumCriticalDistanceMeters, nativeRunAway), MinimumCriticalDistanceMeters, 12.0f);

    public static float ResolveSafeDistance(float admissionDistance, float criticalDistance, VanguardGrenadeFuseProfile fuse)
    {
        float baseline = Math.Max(MinimumSafeDistanceMeters, Math.Max(admissionDistance * 0.80f, criticalDistance + 3.5f));
        float blast = IsFinitePositive(fuse.MaximumExplosionDistance)
            ? fuse.MaximumExplosionDistance + 2.5f
            : baseline;
        float fragment = fuse.FragmentsCount > 0
            ? Math.Max(blast, MinimumSafeDistanceMeters + Math.Min(6f, fuse.FragmentsCount / 20f))
            : blast;
        if (fuse.ContactCapable)
        {
            fragment = Math.Max(fragment, MinimumSafeDistanceMeters + 2f);
        }
        return Math.Clamp(Math.Max(baseline, fragment), MinimumSafeDistanceMeters, MaximumSafeDistanceMeters);
    }

    public static float ResolveNativeProbeSeconds(VanguardGrenadeFuseProfile fuse)
    {
        float remaining = fuse.RemainingSeconds ?? float.PositiveInfinity;
        if (fuse.ContactCapable || remaining <= 1.25f)
        {
            return 0f;
        }
        if (remaining <= 2.0f || string.Equals(fuse.FuseClass, "short", StringComparison.OrdinalIgnoreCase))
        {
            return 0.08f;
        }
        if (remaining <= 4.5f || string.Equals(fuse.FuseClass, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return 0.28f;
        }
        if (fuse.Known)
        {
            return 0.45f;
        }
        return UnknownFuseNativeProbeSeconds;
    }

    public static float ResolveAbsoluteWindowSeconds(VanguardGrenadeFuseProfile fuse)
    {
        float remaining = fuse.RemainingSeconds ?? fuse.DeclaredFuseSeconds;
        float desired = IsFinitePositive(remaining)
            ? remaining + FuseSafetyMarginSeconds
            : MinimumAbsoluteEmergencySeconds;
        return Math.Clamp(Math.Max(MinimumAbsoluteEmergencySeconds, desired), MinimumAbsoluteEmergencySeconds, MaximumAbsoluteEmergencySeconds);
    }

    public static bool ShouldSkipNative(VanguardGrenadeFuseProfile fuse)
    {
        if (fuse.ContactCapable)
        {
            return true;
        }
        return fuse.RemainingSeconds.HasValue && fuse.RemainingSeconds.Value <= 1.25f;
    }

    public static bool IsSafetyEnvelopeSatisfied(VanguardGrenadeHazardDecisionSnapshot hazard)
    {
        if (hazard == null || !hazard.HasRelevantHazard)
        {
            return false;
        }
        bool distanceSafe = hazard.DistanceToGrenade >= hazard.SafeDistance + SafeDistanceHysteresisMeters
            && (!hazard.DangerPointKnown || hazard.DistanceToDangerPoint >= hazard.SafeDistance + SafeDistanceHysteresisMeters);
        bool coverSafe = hazard.DualSolidCover
            && hazard.DistanceToGrenade >= SolidCoverMinimumDistanceMeters
            && (!hazard.DangerPointKnown || hazard.DistanceToDangerPoint >= SolidCoverMinimumDistanceMeters);
        return distanceSafe || coverSafe;
    }

    private static bool IsFinitePositive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
}
#endif

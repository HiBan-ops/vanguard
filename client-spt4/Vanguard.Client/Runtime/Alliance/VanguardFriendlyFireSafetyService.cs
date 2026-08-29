#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;

// Responsibility: Provides the last geometric safety veto before an Operator fires or throws through a protected player/Operator.
// Flow: The chosen combat action is left intact while the immediate shot corridor or grenade blast volume is tested against friendly identities; only an unsafe release is vetoed.
// Authority boundary: SAIN remains target/combat authority and EFT owns ballistics; Vanguard may only block the unsafe release at this final safety boundary.
// Invariant: The guard must never select a different enemy or steer combat, and it may veto only when current geometry places a protected friendly in the danger volume.
namespace Vanguard.Client.Runtime.Alliance;

/// <summary>
/// Vanguard last-line friendly-fire geometry guard. It does not choose targets or drive combat;
/// it only vetoes a trigger/grenade release when a protected player or Operator occupies the
/// immediate fire corridor or blast volume. SAIN remains the combat driver.
/// </summary>
internal static class VanguardFriendlyFireSafetyService
{
    public const string StatusTag = "VANGUARD_FRIENDLY_FIRE_SAFETY_STATUS";
    public const string PerShotStatusTag = "VANGUARD_PER_SHOT_FRIENDLY_FIRE_STATUS";
    public const string FrameCacheStatusTag = "VANGUARD_FRIENDLY_FIRE_FRAME_CACHE_STATUS";
    public const string ProtectedBodySegmentStatusTag = "VANGUARD_PROTECTED_BODY_SEGMENT_FIRE_STATUS";
    private const float FireCorridorRadiusMeters = 0.90f;
    private const float FireCorridorStartMeters = 0.55f;
    private const float CloseOverlapSafetyRadiusMeters = 1.45f;
    private const float CloseOverlapRearToleranceMeters = 0.45f;
    private const int MuzzleOcclusionSkipLimit = 6;
    private const float MuzzleOcclusionAdvanceMeters = 0.08f;
    private const float TorsoCapsuleRadiusMeters = 0.48f;
    private const float LowerBodyCapsuleRadiusMeters = 0.42f;
    private const float GrenadeSafetyRadiusOutdoorMeters = 12.0f;
    private const float GrenadeSafetyRadiusIndoorMeters = 15.0f;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> LastLogByPair = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<FriendlyBodySample> FriendlySamples = new(16);
    private static int friendlySamplesFrame = -1;

    public static bool IsFireCorridorBlocked(ShootData? shootData, out string friendlyProfileId, out float alongMeters, out float lateralMeters)
    {
        friendlyProfileId = "none";
        alongMeters = 0f;
        lateralMeters = float.MaxValue;
        BotOwner? owner = shootData?.Owner;
        if (owner == null || owner.IsDead || !IsKnownVanguardOperatorProfile(owner.ProfileId))
        {
            return false;
        }

        Vector3 from;
        Vector3 to;
        try
        {
            from = owner.WeaponRoot != null ? owner.WeaponRoot.position : owner.Position + Vector3.up * 1.35f;
            to = owner.AimingManager?.CurrentAiming?.EndTargetPoint ?? (from + owner.LookDirection * 100f);
        }
        catch
        {
            return false;
        }

        Vector3 segment = to - from;
        float length = segment.magnitude;
        if (length < 1.0f)
        {
            return false;
        }

        return IsProtectedFriendlyInCorridor(owner.ProfileId, from, segment / length, length, "fire_corridor", out friendlyProfileId, out alongMeters, out lateralMeters);
    }

    /// <summary>
    /// The runtime final projectile veto. ShootData.Shoot only observes the beginning of a burst; this guard
    /// runs at the actual projectile creation boundary so a squadmate entering the muzzle corridor
    /// after trigger-down is still protected. It never changes target memory or SAIN decisions.
    /// </summary>
    public static bool IsActualShotBlocked(Player? shooter, Vector3 shotPosition, Vector3 shotDirection, out string friendlyProfileId, out float alongMeters, out float lateralMeters)
    {
        long started = VanguardRuntimePerformanceGuard.Begin();
        try
        {
            friendlyProfileId = "none";
            alongMeters = 0f;
            lateralMeters = float.MaxValue;
            if (shooter == null || shooter.HealthController?.IsAlive != true
                || !IsKnownVanguardOperatorProfile(shooter.ProfileId))
            {
                return false;
            }

            if (!IsFinite(shotPosition) || !IsFinite(shotDirection) || shotDirection.sqrMagnitude < 0.01f)
            {
                return false;
            }

            return IsProtectedFriendlyInCorridor(shooter.ProfileId, shotPosition, shotDirection.normalized, 120.0f, "actual_projectile", out friendlyProfileId, out alongMeters, out lateralMeters);
        }
        finally
        {
            VanguardRuntimePerformanceGuard.End("FriendlyFireProjectile", started);
        }
    }

    private static bool IsProtectedFriendlyInCorridor(string shooterProfileId, Vector3 from, Vector3 direction, float length, string logKind, out string friendlyProfileId, out float alongMeters, out float lateralMeters)
    {
        friendlyProfileId = "none";
        alongMeters = 0f;
        lateralMeters = float.MaxValue;
        RefreshFriendlySamplesForCurrentFrame();
        if (FriendlySamples.Count == 0)
        {
            return false;
        }

        string shooterId = Normalize(shooterProfileId);
        Transform? shooterRoot = null;
        for (int i = 0; i < FriendlySamples.Count; i++)
        {
            if (string.Equals(FriendlySamples[i].ProfileId, shooterId, StringComparison.OrdinalIgnoreCase))
            {
                shooterRoot = FriendlySamples[i].Root;
                break;
            }
        }

        bool conservativeTriggerGuard = string.Equals(logKind, "fire_corridor", StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < FriendlySamples.Count; i++)
        {
            FriendlyBodySample candidate = FriendlySamples[i];
            if (string.Equals(candidate.ProfileId, shooterId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryBlockBodyCapsule(from, direction, length, candidate.Pelvis, candidate.Head, TorsoCapsuleRadiusMeters, shooterRoot, candidate.Root, conservativeTriggerGuard, out var along, out var lateral, out var radius, out var sampleIndex, 3)
                || TryBlockBodyCapsule(from, direction, length, candidate.BasePosition + Vector3.up * 0.12f, candidate.Pelvis, LowerBodyCapsuleRadiusMeters, shooterRoot, candidate.Root, conservativeTriggerGuard, out along, out lateral, out radius, out sampleIndex, 4)
                || TryBlockCloseOverlap(from, direction, shooterRoot, candidate, out along, out lateral, out radius, out sampleIndex)
                || TryBlockBodySample(from, direction, length, candidate.Head, 0.42f, shooterRoot, candidate.Root, conservativeTriggerGuard, out along, out lateral, out radius, out sampleIndex, 0)
                || TryBlockBodySample(from, direction, length, candidate.Torso, 0.58f, shooterRoot, candidate.Root, conservativeTriggerGuard, out along, out lateral, out radius, out sampleIndex, 1)
                || TryBlockBodySample(from, direction, length, candidate.Pelvis, 0.50f, shooterRoot, candidate.Root, conservativeTriggerGuard, out along, out lateral, out radius, out sampleIndex, 2))
            {
                friendlyProfileId = candidate.ProfileId;
                alongMeters = along;
                lateralMeters = lateral;
                LogBlocked(logKind + "_sample_" + sampleIndex, shooterProfileId, candidate.ProfileId, along, lateral, radius);
                return true;
            }
        }

        return false;
    }

    private static void RefreshFriendlySamplesForCurrentFrame()
    {
        int frame = Time.frameCount;
        if (friendlySamplesFrame == frame)
        {
            return;
        }

        friendlySamplesFrame = frame;
        FriendlySamples.Clear();
        var world = Singleton<GameWorld>.Instance;
        if (world?.RegisteredPlayers == null)
        {
            return;
        }

        foreach (IPlayer candidate in world.RegisteredPlayers)
        {
            if (candidate == null || candidate.Transform == null || candidate.HealthController?.IsAlive != true)
            {
                continue;
            }

            string candidateId = Normalize(candidate.ProfileId);
            if (!VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(candidateId))
            {
                continue;
            }

            Transform? candidateRoot = candidate.Transform.Original;
            if (candidateRoot == null)
            {
                continue;
            }

            Vector3 basePosition = candidate.Transform.position;
            var bones = candidate.PlayerBones;
            Vector3 headSample = bones?.Head != null && IsFinite(bones.Head.position)
                ? bones.Head.position
                : basePosition + Vector3.up * 1.58f;
            Vector3 torsoSample = bones?.Ribcage != null && IsFinite(bones.Ribcage.position)
                ? bones.Ribcage.position
                : basePosition + Vector3.up * 1.08f;
            Vector3 pelvisSample = bones?.Pelvis != null && IsFinite(bones.Pelvis.position)
                ? bones.Pelvis.position
                : basePosition + Vector3.up * 0.67f;

            FriendlySamples.Add(new FriendlyBodySample(candidateId, candidateRoot, basePosition, headSample, torsoSample, pelvisSample));
        }
    }

    private static bool TryBlockBodyCapsule(
        Vector3 from,
        Vector3 direction,
        float length,
        Vector3 capsuleStart,
        Vector3 capsuleEnd,
        float baseRadius,
        Transform? shooterRoot,
        Transform candidateRoot,
        bool conservativeTriggerGuard,
        out float along,
        out float lateral,
        out float radius,
        out int sampleIndex,
        int requestedSampleIndex)
    {
        sampleIndex = requestedSampleIndex;
        radius = conservativeTriggerGuard ? Math.Max(baseRadius, FireCorridorRadiusMeters) : baseRadius;
        Vector3 shotEnd = from + direction * length;
        ClosestPointsOnSegments(from, shotEnd, capsuleStart, capsuleEnd, out var shotPoint, out var bodyPoint, out var shotT);
        along = shotT * length;
        lateral = Vector3.Distance(shotPoint, bodyPoint);
        if (along <= FireCorridorStartMeters || along >= length || lateral > radius)
        {
            return false;
        }

        // Runtime invariant: preserve world occlusion while explicitly skipping the shooter's own
        // muzzle/weapon/body colliders. The previous single Linecast could stop on self geometry
        // and return a false negative for an allied body segment at 8-10m.
        return IsLineUnobstructedToFriendly(from, bodyPoint, shooterRoot, candidateRoot);
    }

    private static void ClosestPointsOnSegments(
        Vector3 p1,
        Vector3 q1,
        Vector3 p2,
        Vector3 q2,
        out Vector3 c1,
        out Vector3 c2,
        out float segmentOneT)
    {
        const float epsilon = 0.000001f;
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        float s;
        float t;

        if (a <= epsilon && e <= epsilon)
        {
            s = 0f;
            t = 0f;
        }
        else if (a <= epsilon)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= epsilon)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denominator = a * e - b * b;
                s = denominator > epsilon ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
        segmentOneT = s;
    }

    private static bool TryBlockCloseOverlap(Vector3 from, Vector3 direction, Transform? shooterRoot, FriendlyBodySample candidate, out float along, out float lateral, out float radius, out int sampleIndex)
    {
        Vector3 nearestSample = candidate.Torso;
        float nearestDistance = Vector3.Distance(from, candidate.Torso);
        sampleIndex = 1;

        float headDistance = Vector3.Distance(from, candidate.Head);
        if (headDistance < nearestDistance)
        {
            nearestSample = candidate.Head;
            nearestDistance = headDistance;
            sampleIndex = 0;
        }

        float pelvisDistance = Vector3.Distance(from, candidate.Pelvis);
        if (pelvisDistance < nearestDistance)
        {
            nearestSample = candidate.Pelvis;
            nearestDistance = pelvisDistance;
            sampleIndex = 2;
        }

        along = Vector3.Dot(nearestSample - from, direction);
        lateral = nearestDistance;
        radius = CloseOverlapSafetyRadiusMeters;
        return nearestDistance <= CloseOverlapSafetyRadiusMeters
            && along >= -CloseOverlapRearToleranceMeters
            && (nearestDistance <= FireCorridorStartMeters
                || IsLineUnobstructedToFriendly(from, nearestSample, shooterRoot, candidate.Root));
    }

    private static bool TryBlockBodySample(Vector3 from, Vector3 direction, float length, Vector3 sample, float baseRadius, Transform? shooterRoot, Transform candidateRoot, bool conservativeTriggerGuard, out float along, out float lateral, out float radius, out int sampleIndex, int requestedSampleIndex)
    {
        sampleIndex = requestedSampleIndex;
        along = Vector3.Dot(sample - from, direction);
        lateral = float.MaxValue;
        radius = baseRadius;
        if (along <= FireCorridorStartMeters || along >= length)
        {
            return false;
        }

        Vector3 closest = from + direction * along;
        lateral = Vector3.Distance(sample, closest);
        float nearExpansion = along < 3.0f ? 0.24f : along < 7.0f ? 0.12f : 0f;
        float minimumRadius = conservativeTriggerGuard
            ? FireCorridorRadiusMeters + nearExpansion
            : FireCorridorRadiusMeters * 0.62f;
        radius = Math.Max(baseRadius + nearExpansion, minimumRadius);
        return lateral <= radius && IsLineUnobstructedToFriendly(from, sample, shooterRoot, candidateRoot);
    }

    private static bool IsKnownVanguardOperatorProfile(string? profileId)
    {
        return VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(profileId, out _)
            || VanguardRaidOperatorRuntimeRegistry.IsExpectedOperatorBotProfileId(profileId);
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    public static bool IsGrenadeBlastUnsafe(BotOwner? owner, Vector3 target, out string friendlyProfileId, out float distanceMeters)
    {
        friendlyProfileId = "none";
        distanceMeters = float.MaxValue;
        if (owner == null || owner.IsDead || !IsKnownVanguardOperatorProfile(owner.ProfileId))
        {
            return false;
        }

        bool indoors = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(owner.ProfileId, out var snapshot)
            && VanguardPrimaryExecutionContract.IsIndoor(snapshot);
        float radius = indoors ? GrenadeSafetyRadiusIndoorMeters : GrenadeSafetyRadiusOutdoorMeters;
        RefreshFriendlySamplesForCurrentFrame();
        string ownerId = Normalize(owner.ProfileId);
        for (int i = 0; i < FriendlySamples.Count; i++)
        {
            FriendlyBodySample candidate = FriendlySamples[i];
            if (string.Equals(candidate.ProfileId, ownerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float distance = Vector3.Distance(candidate.BasePosition, target);
            if (distance >= radius)
            {
                continue;
            }

            friendlyProfileId = candidate.ProfileId;
            distanceMeters = distance;
            LogBlocked("grenade_blast", owner.ProfileId, candidate.ProfileId, distance, radius, radius);
            return true;
        }

        return false;
    }

    private static bool IsLineUnobstructedToFriendly(Vector3 from, Vector3 center, Transform? shooterTransform, Transform friendlyTransform)
    {
        Vector3 segment = center - from;
        float remaining = segment.magnitude;
        if (remaining <= 0.001f)
        {
            return true;
        }

        Vector3 direction = segment / remaining;
        Vector3 origin = from;
        for (int skip = 0; skip < MuzzleOcclusionSkipLimit && remaining > 0.001f; skip++)
        {
            if (!Physics.Raycast(origin, direction, out RaycastHit hit, remaining, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                return true;
            }

            Transform? hitTransform = hit.transform;
            if (hitTransform == null || IsTransformPartOf(hitTransform, friendlyTransform))
            {
                return true;
            }

            bool shooterGeometry = shooterTransform != null && IsTransformPartOf(hitTransform, shooterTransform);
            if (!shooterGeometry)
            {
                return false;
            }

            float advance = Math.Max(MuzzleOcclusionAdvanceMeters, hit.distance + MuzzleOcclusionAdvanceMeters);
            origin += direction * advance;
            remaining -= advance;
        }

        return remaining <= 0.001f;
    }

    private static bool IsTransformPartOf(Transform candidate, Transform root)
    {
        Transform canonicalRoot = root.root;
        return candidate == root
            || candidate.root == canonicalRoot
            || candidate.IsChildOf(root)
            || root.IsChildOf(candidate);
    }

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            LastLogByPair.Clear();
        }
        FriendlySamples.Clear();
        friendlySamplesFrame = -1;
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_FRIENDLY_FIRE_SAFETY_RESET reason={Safe(reason)}; corridorGuard=true; grenadeGuard=true; combatDriver=false; tag={StatusTag}");
    }

    public static void LogBurstTriggerReleased(string? actor, string? friendly, float alongMeters, float lateralMeters)
    {
        string key = "burst_release|" + Normalize(actor) + "|" + Normalize(friendly);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (Sync)
        {
            if (LastLogByPair.TryGetValue(key, out DateTimeOffset last) && now - last < TimeSpan.FromSeconds(2.0d))
            {
                return;
            }
            LastLogByPair[key] = now;
        }

        VanguardClientDiagnosticsLog.Warning(PerShotStatusTag,
            $"VANGUARD_BURST_TRIGGER_RELEASED actor={Safe(actor)}; friendly={Safe(friendly)}; along={alongMeters:0.00}; lateral={lateralMeters:0.00}; triggerCooldown=0.16; targetMutation=false; movementMutation=false; sainAuthorityPreserved=true; frameCached=true; tag={PerShotStatusTag}; cacheTag={FrameCacheStatusTag}");
    }

    private static void LogBlocked(string kind, string? actor, string? friendly, float valueA, float valueB, float threshold)
    {
        string key = kind + "|" + Normalize(actor) + "|" + Normalize(friendly);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (Sync)
        {
            if (LastLogByPair.TryGetValue(key, out DateTimeOffset last) && now - last < TimeSpan.FromSeconds(2.0d))
            {
                return;
            }

            LastLogByPair[key] = now;
        }
        string activeTag = kind.StartsWith("actual_projectile", StringComparison.OrdinalIgnoreCase) ? PerShotStatusTag : StatusTag;
        VanguardClientDiagnosticsLog.Warning(activeTag,
            $"VANGUARD_FRIENDLY_FIRE_BLOCKED kind={Safe(kind)}; actor={Safe(actor)}; friendly={Safe(friendly)}; metricA={valueA:0.00}; metricB={valueB:0.00}; threshold={threshold:0.00}; action=veto_only; perProjectile={Bool(kind.StartsWith("actual_projectile", StringComparison.OrdinalIgnoreCase))}; protectedBodySegment=true; sainAuthorityPreserved=true; tag={activeTag}; bodySegmentTag={ProtectedBodySegmentStatusTag}; legacyTag={StatusTag}");
    }

    private readonly struct FriendlyBodySample
    {
        public FriendlyBodySample(string profileId, Transform root, Vector3 basePosition, Vector3 head, Vector3 torso, Vector3 pelvis)
        {
            ProfileId = profileId;
            Root = root;
            BasePosition = basePosition;
            Head = head;
            Torso = torso;
            Pelvis = pelvis;
        }

        public string ProfileId { get; }
        public Transform Root { get; }
        public Vector3 BasePosition { get; }
        public Vector3 Head { get; }
        public Vector3 Torso { get; }
        public Vector3 Pelvis { get; }
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#else
namespace Vanguard.Client.Runtime.Alliance;
internal static class VanguardFriendlyFireSafetyService
{
    public const string StatusTag = "VANGUARD_FRIENDLY_FIRE_SAFETY_STATUS";
    public const string PerShotStatusTag = "VANGUARD_PER_SHOT_FRIENDLY_FIRE_STATUS";
    public const string FrameCacheStatusTag = "VANGUARD_FRIENDLY_FIRE_FRAME_CACHE_STATUS";
    public const string ProtectedBodySegmentStatusTag = "VANGUARD_PROTECTED_BODY_SEGMENT_FIRE_STATUS";
    public static void Reset(string reason) { }
}
#endif

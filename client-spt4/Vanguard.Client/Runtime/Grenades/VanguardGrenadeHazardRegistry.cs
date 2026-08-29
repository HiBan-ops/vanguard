#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Alliance;

// Responsibility: Keeps the raid-scoped list of grenades Vanguard has actually observed and derives local hazard truth for each Operator.
// Flow: Event hooks register grenade identity/position, readers evaluate only those known objects against Operator position and lifecycle, and reset/pruning removes destroyed or stale entries.
// Authority boundary: EFT owns grenade objects/physics; the registry is a read model and never searches the whole world or moves an Operator.
// Invariant: Only event-observed live grenades may become hazards, and all grenade references must disappear cleanly when destroyed or when the raid resets.
namespace Vanguard.Client.Runtime.Grenades;

/// <summary>
/// Raid-scoped event registry used by grenade subsystem. It stores grenade identity once it is known and builds a
/// local hazard truth for each Operator. The registry never scans GameWorld for grenades; only objects
/// already observed by the grenade subsystem hooks are evaluated.
/// </summary>
internal static class VanguardGrenadeHazardRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Grenade, GrenadeState> ByGrenade = new(ReferenceComparer<Grenade>.Instance);
    private static readonly Dictionary<string, GrenadeState> ByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromSeconds(3.0d);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            ByGrenade.Clear();
            ByKey.Clear();
        }
    }

    public static bool HasActiveHazards
    {
        get
        {
            lock (Sync)
            {
                return ByGrenade.Values.Any(state => !state.Terminal);
            }
        }
    }

    public static void ObserveThrow(Grenade? grenade, Vector3 position, Vector3 force, float mass, DateTimeOffset now)
    {
        if (!CanObserve(grenade))
        {
            return;
        }

        GrenadeState state = GetOrCreate(grenade!, position, now);
        lock (Sync)
        {
            state.Position = SafePosition(grenade, position);
            state.Velocity = VanguardGrenadeRuntimeResolver.ReadVelocity(grenade);
            state.ThrowForce = force;
            state.ThrowMass = mass;
            state.LastObservedAtUtc = now;
            UpdateSourceLocked(state, grenade!.ProfileId);
        }
    }

    public static void ObserveCollision(Grenade? grenade, Vector3 position, DateTimeOffset now)
    {
        if (!CanObserve(grenade))
        {
            return;
        }

        GrenadeState state = GetOrCreate(grenade!, position, now);
        lock (Sync)
        {
            state.Position = SafePosition(grenade, position);
            state.Velocity = VanguardGrenadeRuntimeResolver.ReadVelocity(grenade);
            state.LastObservedAtUtc = now;
        }
    }

    public static void ObserveDangerPoint(Grenade? grenade, Vector3 dangerPoint, DateTimeOffset now)
    {
        if (!CanObserve(grenade))
        {
            return;
        }

        GrenadeState state = GetOrCreate(grenade!, SafePosition(grenade, dangerPoint), now);
        lock (Sync)
        {
            state.Position = SafePosition(grenade, state.Position);
            state.Velocity = VanguardGrenadeRuntimeResolver.ReadVelocity(grenade);
            state.DangerPoint = dangerPoint;
            state.DangerPointKnown = IsFinite(dangerPoint);
            state.LastObservedAtUtc = now;
            UpdateSourceLocked(state, grenade!.ProfileId);
        }
    }

    public static void ObserveExplosion(Vector3 position, string? sourceProfileId, int throwableId, DateTimeOffset now)
    {
        lock (Sync)
        {
            GrenadeState? state = FindForExplosionLocked(throwableId, sourceProfileId, position);
            if (state == null)
            {
                return;
            }

            UpdateSourceLocked(state, sourceProfileId);
            state.Position = position;
            state.DangerPoint = position;
            state.DangerPointKnown = true;
            state.Terminal = true;
            state.TerminalReason = VanguardGrenadeEmergencyTerminalKind.GrenadeExplodedAndHazardCleared.ToString();
            state.TerminalAtUtc = now;
            state.LastObservedAtUtc = now;
        }
    }

    public static void ObserveDestroyed(Grenade? grenade, DateTimeOffset now)
    {
        if (grenade == null)
        {
            return;
        }

        lock (Sync)
        {
            if (!ByGrenade.TryGetValue(grenade, out GrenadeState? state))
            {
                return;
            }

            state.Terminal = true;
            state.TerminalReason = VanguardGrenadeEmergencyTerminalKind.GrenadeDestroyed.ToString();
            state.TerminalAtUtc = now;
            state.LastObservedAtUtc = now;
        }
    }

    public static bool TryGetGrenade(string? grenadeKey, out Grenade grenade)
    {
        string key = Normalize(grenadeKey);
        lock (Sync)
        {
            if (ByKey.TryGetValue(key, out GrenadeState? state) && state.Grenade != null && !state.Terminal)
            {
                grenade = state.Grenade;
                return true;
            }
        }

        grenade = null!;
        return false;
    }

    public static bool TryGetTerminalReason(string? grenadeKey, out string reason)
    {
        string key = Normalize(grenadeKey);
        lock (Sync)
        {
            if (ByKey.TryGetValue(key, out GrenadeState? state) && state.Terminal)
            {
                reason = state.TerminalReason;
                return true;
            }
        }

        reason = "none";
        return false;
    }

    public static VanguardGrenadeHazardDecisionSnapshot CaptureDecisionSnapshot(
        VanguardRaidOperatorRuntimeRecord? runtime,
        Vector3 operatorPosition,
        DateTimeOffset now)
    {
        if (!CanBuildSnapshot(runtime))
        {
            return VanguardGrenadeHazardDecisionSnapshot.Empty;
        }

        GrenadeState[] active;
        lock (Sync)
        {
            CleanupLocked(now);
            active = ByGrenade.Values.Where(state => !state.Terminal).ToArray();
        }

        if (active.Length == 0)
        {
            return VanguardGrenadeHazardDecisionSnapshot.Empty;
        }

        ReadThresholds(runtime!, out float addDanger, out float runAway, out float admissionDistance, out float criticalDistance);
        VanguardGrenadeHazardDecisionSnapshot? best = null;
        foreach (GrenadeState state in active)
        {
            VanguardGrenadeHazardDecisionSnapshot candidate = BuildSnapshot(
                state,
                runtime!,
                operatorPosition,
                now,
                addDanger,
                runAway,
                admissionDistance,
                criticalDistance,
                exactTracking: false);
            if (!candidate.HasRelevantHazard)
            {
                continue;
            }
            if (best == null || candidate.RiskScore > best.RiskScore)
            {
                best = candidate;
            }
        }
        return best ?? VanguardGrenadeHazardDecisionSnapshot.Empty;
    }

    public static bool TryCaptureExactHazardSnapshot(
        string? grenadeKey,
        VanguardRaidOperatorRuntimeRecord? runtime,
        Vector3 operatorPosition,
        DateTimeOffset now,
        out VanguardGrenadeHazardDecisionSnapshot snapshot)
    {
        snapshot = VanguardGrenadeHazardDecisionSnapshot.Empty;
        if (!CanBuildSnapshot(runtime))
        {
            return false;
        }

        GrenadeState? state;
        lock (Sync)
        {
            CleanupLocked(now);
            ByKey.TryGetValue(Normalize(grenadeKey), out state);
            if (state == null || state.Terminal || state.Grenade == null)
            {
                return false;
            }
        }

        ReadThresholds(runtime!, out float addDanger, out float runAway, out float admissionDistance, out float criticalDistance);
        snapshot = BuildSnapshot(
            state,
            runtime!,
            operatorPosition,
            now,
            addDanger,
            runAway,
            admissionDistance,
            criticalDistance,
            exactTracking: true);
        return snapshot.HasRelevantHazard && string.Equals(snapshot.GrenadeKey, Normalize(grenadeKey), StringComparison.OrdinalIgnoreCase);
    }

    private static VanguardGrenadeHazardDecisionSnapshot BuildSnapshot(
        GrenadeState state,
        VanguardRaidOperatorRuntimeRecord runtime,
        Vector3 operatorPosition,
        DateTimeOffset now,
        float addDanger,
        float runAway,
        float admissionDistance,
        float criticalDistance,
        bool exactTracking)
    {
        Grenade? grenade = state.Grenade;
        if (grenade == null || VanguardGrenadeRuntimeResolver.IsSmoke(grenade))
        {
            return VanguardGrenadeHazardDecisionSnapshot.Empty;
        }

        Vector3 grenadePosition = SafePosition(grenade, state.Position);
        Vector3 velocity = VanguardGrenadeRuntimeResolver.ReadVelocity(grenade);
        Vector3 dangerPoint = state.DangerPointKnown ? state.DangerPoint : grenadePosition;
        bool dangerPointKnown = state.DangerPointKnown && IsFinite(dangerPoint);
        float grenadeDistance = Vector3.Distance(operatorPosition, grenadePosition);
        float dangerDistance = dangerPointKnown ? Vector3.Distance(operatorPosition, dangerPoint) : grenadeDistance;
        float effectiveDistance = Math.Min(grenadeDistance, dangerDistance);
        bool approaching = IsApproaching(grenadePosition, velocity, operatorPosition, out float predictedClosest);

        if (!exactTracking
            && effectiveDistance > VanguardGrenadeEmergencyPolicy.MaximumRelevantDistanceMeters
            && !approaching)
        {
            return VanguardGrenadeHazardDecisionSnapshot.Empty;
        }

        bool geometryRelevant = effectiveDistance <= admissionDistance
            || predictedClosest <= admissionDistance
            || approaching && effectiveDistance <= VanguardGrenadeEmergencyPolicy.MaximumRelevantDistanceMeters;
        if (!exactTracking && !geometryRelevant)
        {
            return VanguardGrenadeHazardDecisionSnapshot.Empty;
        }

        VanguardGrenadeFuseProfile fuse = VanguardGrenadeRuntimeResolver.ReadFuseProfile(grenade, state.FirstObservedAtUtc, now);
        float safeDistance = VanguardGrenadeEmergencyPolicy.ResolveSafeDistance(admissionDistance, criticalDistance, fuse);
        float geometryRange = Math.Max(VanguardGrenadeEmergencyPolicy.MaximumRelevantDistanceMeters, safeDistance + 6f);
        bool lineKnown = exactTracking || effectiveDistance <= geometryRange;
        bool actualBlocked = false;
        bool predictedBlocked = false;
        if (lineKnown)
        {
            ProbeSafetyGeometryCached(state, runtime.BotProfileId, grenadePosition, dangerPoint, dangerPointKnown, operatorPosition, now, out actualBlocked, out predictedBlocked);
        }
        bool dualSolidCover = lineKnown && actualBlocked && (!dangerPointKnown || predictedBlocked);

        bool nativeDanger = false;
        bool nativeRun = false;
        try
        {
            if (VanguardGrenadeRuntimeResolver.TryReadNativeDangerState(runtime.BotOwner!.BewareGrenade, out bool present, out Grenade? nativeGrenade, out _)
                && present && ReferenceEquals(nativeGrenade, grenade))
            {
                nativeDanger = true;
                nativeRun = runtime.BotOwner.BewareGrenade != null && runtime.BotOwner.BewareGrenade.ShallRunAway();
            }
        }
        catch
        {
            // Native state is supporting evidence only. Local qualification remains authoritative.
        }

        bool veryClose = effectiveDistance <= VanguardGrenadeEmergencyPolicy.ImmediateDangerDistanceMeters;
        bool fuseImminent = fuse.ContactCapable || fuse.RemainingSeconds.HasValue && fuse.RemainingSeconds.Value <= 1.25f;
        bool critical = effectiveDistance <= criticalDistance
            || predictedClosest <= criticalDistance
            || nativeRun
            || veryClose
            || fuseImminent && effectiveDistance <= admissionDistance;
        bool occlusionStrongEnough = dualSolidCover
            && effectiveDistance > VanguardGrenadeEmergencyPolicy.SolidCoverMinimumDistanceMeters
            && !approaching;
        bool relevant = exactTracking || critical || nativeDanger || geometryRelevant && !occlusionStrongEnough;
        if (!relevant)
        {
            return VanguardGrenadeHazardDecisionSnapshot.Empty;
        }

        float score = Math.Max(0f, admissionDistance - effectiveDistance) * 10f;
        if (critical) score += 80f;
        if (veryClose) score += 80f;
        if (fuseImminent) score += 55f;
        if (approaching) score += Math.Max(0f, admissionDistance - predictedClosest) * 8f + 25f;
        if (nativeDanger) score += 18f;
        if (nativeRun) score += 30f;
        if (dualSolidCover) score -= effectiveDistance > VanguardGrenadeEmergencyPolicy.SolidCoverMinimumDistanceMeters ? 30f : 8f;
        float vertical = Math.Max(Math.Abs(operatorPosition.y - grenadePosition.y), Math.Abs(operatorPosition.y - dangerPoint.y));
        if (vertical > 5f) score -= 22f;

        VanguardGrenadeLocalRelation localRelation = ResolveLocalRelation(runtime.BotOwner, runtime.BotProfileId, state.SourceProfileId);
        string reason = exactTracking
            ? "active_exact_tracking"
            : critical
                ? veryClose ? "immediate_distance" : fuseImminent ? "fuse_imminent" : nativeRun ? "native_shall_run_away" : approaching && predictedClosest <= criticalDistance ? "trajectory_closest_approach" : "critical_distance"
                : nativeDanger ? "native_danger_present" : approaching ? "approaching_trajectory" : "relevant_distance_open_line";

        lock (Sync)
        {
            state.Position = grenadePosition;
            state.Velocity = velocity;
            state.FuseProfile = fuse;
        }

        return new VanguardGrenadeHazardDecisionSnapshot
        {
            HasRelevantHazard = true,
            ExactTrackedHazard = exactTracking,
            GrenadeKey = state.Key,
            GrenadeId = state.GrenadeId,
            GrenadeType = state.GrenadeType,
            SourceProfileId = state.SourceProfileId,
            SourceName = state.SourceName,
            SourceIdentity = state.SourceIdentity,
            SourceRelation = localRelation,
            GrenadePosition = grenadePosition,
            Velocity = velocity,
            DangerPoint = dangerPoint,
            DangerPointKnown = dangerPointKnown,
            DistanceToGrenade = grenadeDistance,
            DistanceToDangerPoint = dangerDistance,
            EffectiveDistance = effectiveDistance,
            VerticalDelta = vertical,
            LineOfEffectKnown = lineKnown,
            LineOfEffectBlocked = predictedBlocked,
            ActualLineOfEffectKnown = lineKnown,
            ActualLineOfEffectBlocked = actualBlocked,
            PredictedLineOfEffectKnown = lineKnown && dangerPointKnown,
            PredictedLineOfEffectBlocked = predictedBlocked,
            DualSolidCover = dualSolidCover,
            ApproachingOperator = approaching,
            PredictedClosestDistance = predictedClosest,
            NativeDangerPresent = nativeDanger,
            NativeShallRunAway = nativeRun,
            EstimatedTimeToExplosionSeconds = fuse.RemainingSeconds,
            TimeConfidence = fuse.Confidence,
            FuseProfile = fuse,
            NativeProbeSeconds = VanguardGrenadeEmergencyPolicy.ResolveNativeProbeSeconds(fuse),
            RecommendedAbsoluteWindowSeconds = VanguardGrenadeEmergencyPolicy.ResolveAbsoluteWindowSeconds(fuse),
            NativeAddDangerThreshold = addDanger,
            NativeRunAwayThreshold = runAway,
            AdmissionDistance = admissionDistance,
            SafeDistance = safeDistance,
            RiskScore = score,
            Critical = critical,
            Imminent = veryClose || fuseImminent || approaching && predictedClosest <= VanguardGrenadeEmergencyPolicy.ImmediateDangerDistanceMeters,
            AdmissionReason = reason,
            ObservedAtUtc = state.FirstObservedAtUtc,
            CapturedAtUtc = now,
        };
    }

    private static bool CanBuildSnapshot(VanguardRaidOperatorRuntimeRecord? runtime)
        => VanguardFikaCompat.IsRaidAuthority && runtime != null && runtime.BotOwner != null && !runtime.BotOwner.IsDead;

    private static void ReadThresholds(
        VanguardRaidOperatorRuntimeRecord runtime,
        out float addDanger,
        out float runAway,
        out float admissionDistance,
        out float criticalDistance)
    {
        VanguardGrenadeRuntimeResolver.TryReadGrenadeThresholds(runtime.BotOwner, out addDanger, out runAway, out _);
        addDanger = Math.Max(1f, addDanger);
        runAway = Math.Max(1f, runAway);
        admissionDistance = VanguardGrenadeEmergencyPolicy.ResolveAdmissionDistance(addDanger, runAway);
        criticalDistance = VanguardGrenadeEmergencyPolicy.ResolveCriticalDistance(runAway);
    }

    public static bool IsCandidateSafeFromOtherHazards(Vector3 candidate, string? excludedGrenadeKey, float minimumDistance, DateTimeOffset now, out string reason)
    {
        string excluded = Normalize(excludedGrenadeKey);
        GrenadeState[] states;
        lock (Sync)
        {
            CleanupLocked(now);
            states = ByGrenade.Values.Where(state => !state.Terminal && !string.Equals(state.Key, excluded, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        foreach (GrenadeState state in states)
        {
            Vector3 actualPoint = SafePosition(state.Grenade, state.Position);
            Vector3 predictedPoint = state.DangerPointKnown ? state.DangerPoint : actualPoint;
            float actualDistance = Vector3.Distance(candidate, actualPoint);
            float predictedDistance = Vector3.Distance(candidate, predictedPoint);
            float effectiveDistance = Math.Min(actualDistance, predictedDistance);
            if (effectiveDistance < minimumDistance)
            {
                reason = "other_grenade=" + state.Key
                    + ":effective=" + effectiveDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + ":actual=" + actualDistance.ToString("0.00", CultureInfo.InvariantCulture)
                    + ":predicted=" + predictedDistance.ToString("0.00", CultureInfo.InvariantCulture);
                return false;
            }
        }

        reason = "no_other_hazard_conflict";
        return true;
    }

    private static GrenadeState GetOrCreate(Grenade grenade, Vector3 position, DateTimeOffset now)
    {
        lock (Sync)
        {
            if (ByGrenade.TryGetValue(grenade, out GrenadeState? existing))
            {
                return existing;
            }

            string key = BuildKey(grenade);
            var created = new GrenadeState
            {
                Grenade = grenade,
                Key = key,
                GrenadeId = grenade.Id,
                GrenadeType = grenade.GetType().Name,
                Position = position,
                FirstObservedAtUtc = now,
                LastObservedAtUtc = now,
            };
            UpdateSourceLocked(created, grenade.ProfileId);
            ByGrenade[grenade] = created;
            ByKey[key] = created;
            return created;
        }
    }

    private static void UpdateSourceLocked(GrenadeState state, string? profileId)
    {
        VanguardGrenadeRuntimeResolver.ResolveSource(profileId, out string sourceId, out string sourceName, out VanguardGrenadeSourceRelation relation);
        VanguardGrenadeSourceIdentityKind identity = relation switch
        {
            VanguardGrenadeSourceRelation.Operator => VanguardGrenadeSourceIdentityKind.Operator,
            VanguardGrenadeSourceRelation.PlayerOwner => VanguardGrenadeSourceIdentityKind.PlayerOwner,
            VanguardGrenadeSourceRelation.PlayerClient => VanguardGrenadeSourceIdentityKind.PlayerClient,
            VanguardGrenadeSourceRelation.HostileOrNeutral => VanguardGrenadeSourceIdentityKind.Ai,
            _ => VanguardGrenadeSourceIdentityKind.Unknown,
        };
        int confidence = (int)identity;
        bool currentUnknown = string.Equals(state.SourceProfileId, "none", StringComparison.OrdinalIgnoreCase);
        bool sameIdentity = string.Equals(state.SourceProfileId, sourceId, StringComparison.OrdinalIgnoreCase);
        // Once a physical source identity is known, a later observation may enrich that same
        // identity but may never replace it with another profile. Relation remains local per Operator.
        if (sourceId == "none" || (!currentUnknown && !sameIdentity))
        {
            return;
        }

        if (currentUnknown || sameIdentity && confidence >= state.SourceConfidence)
        {
            state.SourceProfileId = sourceId;
            state.SourceName = sourceName;
            state.SourceIdentity = identity;
            state.SourceConfidence = confidence;
        }
    }

    private static VanguardGrenadeLocalRelation ResolveLocalRelation(BotOwner? owner, string operatorProfileId, string sourceProfileId)
    {
        string source = Normalize(sourceProfileId);
        if (source == "none")
        {
            return VanguardGrenadeLocalRelation.Unknown;
        }
        if (string.Equals(operatorProfileId, source, StringComparison.OrdinalIgnoreCase))
        {
            return VanguardGrenadeLocalRelation.Self;
        }
        if (VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(operatorProfileId, source))
        {
            return VanguardGrenadeLocalRelation.Friendly;
        }
        if (owner != null && VanguardGrenadeRuntimeResolver.IsConfirmedEnemyForBot(owner, source))
        {
            return VanguardGrenadeLocalRelation.Hostile;
        }

        // A known non-friendly identity is not automatically hostile. EFT may still consider it
        // neutral, and grenade subsystem must not turn survival evidence into a fabricated combat contact.
        return VanguardGrenadeLocalRelation.Unknown;
    }


    private static void ProbeSafetyGeometryCached(
        GrenadeState state,
        string botProfileId,
        Vector3 grenadePosition,
        Vector3 dangerPoint,
        bool dangerPointKnown,
        Vector3 operatorPosition,
        DateTimeOffset now,
        out bool actualBlocked,
        out bool predictedBlocked)
    {
        string key = Normalize(botProfileId);
        lock (Sync)
        {
            if (state.GeometryByBotProfileId.TryGetValue(key, out GeometryProbeCache? cached)
                && now < cached.NextProbeAtUtc
                && (cached.GrenadePosition - grenadePosition).sqrMagnitude <= 0.36f
                && (cached.DangerPoint - dangerPoint).sqrMagnitude <= 0.36f
                && (cached.OperatorPosition - operatorPosition).sqrMagnitude <= 0.36f)
            {
                actualBlocked = cached.ActualLineBlocked;
                predictedBlocked = dangerPointKnown ? cached.PredictedLineBlocked : cached.ActualLineBlocked;
                return;
            }
        }

        actualBlocked = VanguardGrenadeRuntimeResolver.ProbeLineOfEffect(grenadePosition, operatorPosition);
        predictedBlocked = dangerPointKnown
            ? VanguardGrenadeRuntimeResolver.ProbeLineOfEffect(dangerPoint, operatorPosition)
            : actualBlocked;
        lock (Sync)
        {
            state.GeometryByBotProfileId[key] = new GeometryProbeCache
            {
                GrenadePosition = grenadePosition,
                DangerPoint = dangerPoint,
                OperatorPosition = operatorPosition,
                ActualLineBlocked = actualBlocked,
                PredictedLineBlocked = predictedBlocked,
                NextProbeAtUtc = now + TimeSpan.FromSeconds(0.20d),
            };
        }
    }

    private static bool IsApproaching(Vector3 grenadePosition, Vector3 velocity, Vector3 operatorPosition, out float closestDistance)
    {
        Vector3 toOperator = operatorPosition - grenadePosition;
        Vector3 planarVelocity = velocity;
        planarVelocity.y = 0f;
        Vector3 planarToOperator = toOperator;
        planarToOperator.y = 0f;
        float speedSqr = planarVelocity.sqrMagnitude;
        if (speedSqr < 0.04f)
        {
            closestDistance = planarToOperator.magnitude;
            return false;
        }

        float projectionSeconds = Mathf.Clamp(Vector3.Dot(planarToOperator, planarVelocity) / speedSqr, 0f, 1.75f);
        Vector3 predicted = grenadePosition + planarVelocity * projectionSeconds;
        closestDistance = HorizontalDistance(predicted, operatorPosition);
        return projectionSeconds > 0.05f && Vector3.Dot(planarVelocity.normalized, planarToOperator.normalized) > 0.20f;
    }

    private static GrenadeState? FindForExplosionLocked(int throwableId, string? sourceProfileId, Vector3 position)
    {
        GrenadeState? byId = ByGrenade.Values.FirstOrDefault(state => state.GrenadeId == throwableId);
        if (byId != null)
        {
            return byId;
        }

        string source = Normalize(sourceProfileId);
        return ByGrenade.Values
            .Where(state => !state.Terminal && (source == "none" || state.SourceProfileId == "none" || string.Equals(state.SourceProfileId, source, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(state => Vector3.Distance(state.Position, position))
            .FirstOrDefault(state => Vector3.Distance(state.Position, position) <= 8f);
    }

    private static void CleanupLocked(DateTimeOffset now)
    {
        foreach (GrenadeState state in ByGrenade.Values.ToArray())
        {
            bool expiredTerminal = state.Terminal && state.TerminalAtUtc != DateTimeOffset.MinValue && now - state.TerminalAtUtc >= TerminalRetention;
            bool staleUnknown = !state.Terminal && now - state.LastObservedAtUtc > TimeSpan.FromSeconds(30d);
            if (!expiredTerminal && !staleUnknown)
            {
                continue;
            }
            ByGrenade.Remove(state.Grenade);
            ByKey.Remove(state.Key);
        }
    }

    private static bool CanObserve(Grenade? grenade) => VanguardFikaCompat.IsRaidAuthority && grenade != null;
    private static string BuildKey(Grenade grenade) => grenade.GetType().Name + "#" + grenade.Id.ToString(CultureInfo.InvariantCulture) + "@" + RuntimeHelpers.GetHashCode(grenade).ToString(CultureInfo.InvariantCulture);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static bool IsFinite(Vector3 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
    private static Vector3 SafePosition(Grenade? grenade, Vector3 fallback)
    {
        try { return grenade != null && grenade.transform != null ? grenade.transform.position : fallback; }
        catch { return fallback; }
    }
    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private sealed class GrenadeState
    {
        public Grenade Grenade = null!;
        public string Key = "none";
        public int GrenadeId;
        public string GrenadeType = "none";
        public string SourceProfileId = "none";
        public string SourceName = "none";
        public VanguardGrenadeSourceIdentityKind SourceIdentity;
        public int SourceConfidence;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 ThrowForce;
        public float ThrowMass;
        public VanguardGrenadeFuseProfile FuseProfile = VanguardGrenadeFuseProfile.Unknown;
        public Vector3 DangerPoint;
        public bool DangerPointKnown;
        public bool Terminal;
        public string TerminalReason = "none";
        public DateTimeOffset FirstObservedAtUtc;
        public DateTimeOffset LastObservedAtUtc;
        public DateTimeOffset TerminalAtUtc;
        public Dictionary<string, GeometryProbeCache> GeometryByBotProfileId { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class GeometryProbeCache
    {
        public Vector3 GrenadePosition;
        public Vector3 DangerPoint;
        public Vector3 OperatorPosition;
        public bool ActualLineBlocked;
        public bool PredictedLineBlocked;
        public DateTimeOffset NextProbeAtUtc;
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();
        bool IEqualityComparer<T>.Equals(T? x, T? y) => ReferenceEquals(x, y);
        int IEqualityComparer<T>.GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
#endif

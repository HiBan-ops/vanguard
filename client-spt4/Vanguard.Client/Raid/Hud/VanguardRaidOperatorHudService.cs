#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EFT;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime.Fika;
using Vanguard.Client.Runtime.Medical;

using Vanguard.Client;

// Responsibility: renders the compact in-raid Operator HUD from fresh authoritative squad telemetry and presentation-only local metadata such as resolved EFT medical sprites.
// Flow: Persistent Operator identities are matched to live HUD candidates, authoritative Host/Headless telemetry supplies status, local EFT data may enrich icons, then views are created/updated at a throttled cadence and stale or unresolved entries are hidden and cleaned up.
// Authority boundary: the HUD is read-only; remote Host/Headless telemetry determines distributed Operator state and local medical inspection may only enrich icon presentation, never override remote truth.
// Invariant: stale/unavailable remote entries clear rather than masquerade as current state, and text badges remain a safe fallback when an EFT sprite cannot be resolved.

namespace Vanguard.Client.Raid.Hud;

internal static class VanguardRaidOperatorHudService
{
    private const float MaxDistanceMeters = 70f;
    private const float VerticalOffsetWorld = 2.30f;
    private const float HudStateRefreshIntervalSeconds = 1.0f;
    private const float SummaryIntervalSeconds = 12f;
    private static readonly TimeSpan MedicalTelemetryStaleAfter = TimeSpan.FromSeconds(8.0d);

    private static readonly VanguardRaidOperatorHudCandidateResolver Resolver = new();
    private static readonly Dictionary<string, VanguardRaidOperatorHudView> Views = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, VanguardRaidOperatorHudCandidate> LastStateByKey = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> NextStateRefreshByKey = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LastContentSignatureByKey = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LastAppliedContentSignatureByKey = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LastMedicalTruthSignatureByKey = new(StringComparer.Ordinal);

    private static readonly object EffectIconCacheLock = new();
    private static readonly Dictionary<Type, Sprite?> SpriteByEffectTypeCache = new();
    private static readonly Dictionary<string, Sprite> SpriteByMedicalBadgeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedMedicalIconResolutionBadges = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedMedicalIconFallbackBadges = new(StringComparer.OrdinalIgnoreCase);
    private static IDictionary? CachedEffectIconDictionary;
    private static Type? CachedEftHardSettingsType;
    private static bool EftHardSettingsTypeLookupAttempted;
    private static float NextEffectIconDictionaryResolveTime;

    private static readonly object BodyPartIconCacheLock = new();
    private static bool BodyPartIconCacheResolved;
    private static float NextBodyPartIconCacheAttemptTime;
    private static readonly Dictionary<string, Sprite?> BodyPartOverlaySpritesByBadge = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> BodyPartPrefabMemberByBadge = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HD"] = "Head",
        ["CH"] = "Chest",
        ["ST"] = "Stomach",
        ["LA"] = "LeftArm",
        ["RA"] = "RightArm",
        ["LL"] = "LeftLeg",
        ["RL"] = "RightLeg",
    };

    private static Canvas? overlayCanvas;
    private static RectTransform? overlayRoot;
    private static TMP_FontAsset? fontAsset;
    private static float nextSummaryTime;
    private static string? lastSummarySignature;

    public static void Tick(MonoBehaviour owner)
    {
        try
        {
            // Presentation is never consumed by the dedicated Fika headless process. Semantic HUD
            // telemetry is produced by VanguardFikaHudTelemetryService before this presentation tick.
            if (VanguardFikaCompat.IsActualHeadlessProcess)
            {
                return;
            }

            VanguardRaidFixedOperatorHudOptions.BindFromOwner(owner);

            var localPlayer = GamePlayerOwner.MyPlayer;
            if (owner is null || localPlayer is null)
            {
                VanguardRaidFixedOperatorHudService.Hide();
                CleanupStale(Array.Empty<string>());
                return;
            }

            if (!EnsureOverlayRoot(owner))
            {
                VanguardRaidFixedOperatorHudService.Hide();
                CleanupStale(Array.Empty<string>());
                return;
            }

            // Body-part sprite extraction traverses the Fika PlayerUI prefab and is presentation-only.
            // ResolveDestroyedBodyPartIconData initializes this cache lazily when a destroyed-part badge is actually needed,
            // avoiding an unconditional prefab traversal on the first in-raid HUD tick.

            var identities = Resolver.Resolve(localPlayer);
            var allCandidates = identities
                .Select(identity => BuildHudCandidateThrottled(identity, localPlayer))
                .OrderBy(candidate => candidate.Nickname, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            VanguardRaidFixedOperatorHudService.Tick(owner, overlayRoot!, fontAsset, allCandidates);

            var candidates = allCandidates
                .Where(candidate => candidate.HealthReadable)
                .ToArray();

            var liveKeys = new HashSet<string>(candidates.Select(candidate => candidate.Key), StringComparer.Ordinal);
            var signatures = new List<string>();
            int visibleCount = 0;

            foreach (var candidate in candidates)
            {
                string? hiddenReason = ResolveHiddenReason(candidate);
                if (hiddenReason is not null)
                {
                    SetViewActive(candidate.Key, false);
                    signatures.Add(candidate.ToSignature(hiddenReason));
                    continue;
                }

                if (!VanguardRaidOperatorHudProjection.TryProjectToCanvas(
                        candidate.AnchorWorldPosition,
                        localPlayer,
                        overlayRoot!,
                        out var canvasPosition,
                        out _))
                {
                    SetViewActive(candidate.Key, false);
                    signatures.Add(candidate.ToSignature("projectionFailed"));
                    continue;
                }

                var view = GetOrCreateView(candidate.Key);
                view.SetActive(true);

                if (ShouldUpdateContent(candidate.Key, candidate))
                {
                    view.UpdateContent(candidate.Nickname, candidate.HealthPercent, candidate.StatusIcons, candidate.MedicalIconBadges, candidate.HudIcons);
                }

                view.UpdatePosition(canvasPosition, ResolveScale(candidate.DistanceMeters));
                visibleCount++;
                signatures.Add(candidate.ToSignature("visible"));
            }

            CleanupStale(liveKeys);
            LogSummary(candidates.Length, visibleCount, signatures);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorHudStatusTag, $"hud tick failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static VanguardRaidOperatorHudCandidate BuildHudCandidateThrottled(VanguardRaidOperatorHudIdentity identity, Player localPlayer)
    {
        string key = identity.Key;
        Vector3 playerPosition = identity.Player.Transform.position;
        Vector3 localPosition = localPlayer.Transform.position;
        float distanceMeters = Vector3.Distance(localPosition, playerPosition);
        Vector3 anchorWorldPosition = playerPosition + new Vector3(0f, VerticalOffsetWorld, 0f);

        float now = Time.realtimeSinceStartup;
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        string medicalTruthSignature = ResolveMedicalTruthSignature(identity.BotProfileId, utcNow);
        bool medicalTruthChanged = !LastMedicalTruthSignatureByKey.TryGetValue(key, out string previousMedicalTruthSignature)
            || !string.Equals(previousMedicalTruthSignature, medicalTruthSignature, StringComparison.Ordinal);
        if (LastStateByKey.TryGetValue(key, out var cached)
            && NextStateRefreshByKey.TryGetValue(key, out float nextRefresh)
            && now < nextRefresh
            && !medicalTruthChanged)
        {
            return cached with
            {
                DistanceMeters = distanceMeters,
                AnchorWorldPosition = anchorWorldPosition,
            };
        }

        var fresh = BuildHudCandidate(identity, localPlayer, utcNow);
        if (!fresh.HealthReadable)
        {
            LastStateByKey.Remove(key);
            NextStateRefreshByKey.Remove(key);
            LastContentSignatureByKey.Remove(key);
            LastMedicalTruthSignatureByKey.Remove(key);
            return fresh;
        }

        LastStateByKey[key] = fresh;
        NextStateRefreshByKey[key] = now + HudStateRefreshIntervalSeconds;
        LastContentSignatureByKey[key] = BuildContentSignature(fresh);
        LastMedicalTruthSignatureByKey[key] = medicalTruthSignature;
        return fresh;
    }

    private static VanguardRaidOperatorHudCandidate BuildHudCandidate(VanguardRaidOperatorHudIdentity identity, Player localPlayer, DateTimeOffset now)
    {
        var snapshot = VanguardRaidOperatorVitalitySnapshot.Create(identity.Player);
        bool healthReadable = VanguardRaidOperatorVitalitySnapshot.TryReadCommonHealth(identity.Player, out float current, out float maximum, out bool alive);
        int healthPercent = healthReadable && maximum > 0f
            ? Mathf.Clamp(Mathf.RoundToInt((current / maximum) * 100f), 0, 100)
            : Mathf.Clamp(snapshot.HealthPercent, 0, 100);

        string[] medicalEffectBadges;
        HudIconData medicalIconData;
        if (VanguardFikaHudMedicalTelemetryStore.TryGetFreshState(identity.BotProfileId, now, MedicalTelemetryStaleAfter, out VanguardFikaHudMedicalState remoteMedical)
            && remoteMedical.Readable)
        {
            medicalEffectBadges = remoteMedical.Badges;
            medicalIconData = ResolveRemoteMedicalEffectIconData(identity, medicalEffectBadges, now);
        }
        else
        {
            var canonicalMedicalEffects = VanguardCanonicalMedicalStateService.Capture(
                identity.BotProfileId,
                identity.Player,
                identity.Player.HealthController,
                VanguardRaidHudReflection.ReadMember(identity.Player, "ActiveHealthController"),
                now,
                "operator_hud");
            medicalEffectBadges = canonicalMedicalEffects.Badges;
            medicalIconData = ResolveMedicalEffectIconData(canonicalMedicalEffects);
        }
        var bodyPartIconData = ResolveDestroyedBodyPartIconData(snapshot.BodyParts);
        var hudIconData = CombineHudIconData(bodyPartIconData, medicalIconData);
        string statusIcons = ResolveStatusIcons(snapshot, healthPercent, alive, medicalEffectBadges);
        Vector3 playerPosition = identity.Player.Transform.position;

        return new VanguardRaidOperatorHudCandidate(
            identity.Key,
            identity.OperatorId,
            identity.OwnerProfileId,
            identity.BotProfileId,
            identity.Nickname,
            healthReadable || snapshot.HealthPercent > 0,
            healthPercent,
            statusIcons,
            hudIconData.Badges,
            hudIconData.Icons,
            snapshot.BodyParts,
            snapshot.Effects,
            Vector3.Distance(localPlayer.Transform.position, playerPosition),
            playerPosition + new Vector3(0f, VerticalOffsetWorld, 0f));
    }

    private static bool ShouldUpdateContent(string key, VanguardRaidOperatorHudCandidate candidate)
    {
        string signature = LastContentSignatureByKey.TryGetValue(key, out string cachedSignature)
            ? cachedSignature
            : BuildContentSignature(candidate);
        if (LastAppliedContentSignatureByKey.TryGetValue(key, out string previousSignature)
            && string.Equals(previousSignature, signature, StringComparison.Ordinal))
        {
            return false;
        }

        LastAppliedContentSignatureByKey[key] = signature;
        return true;
    }

    private static string BuildContentSignature(VanguardRaidOperatorHudCandidate candidate)
    {
        return string.Join("|", candidate.Nickname, candidate.HealthPercent.ToString(CultureInfo.InvariantCulture), candidate.StatusIcons, candidate.MedicalIconBadges, BuildHudIconSignature(candidate.HudIcons));
    }

    private static string BuildHudIconSignature(VanguardRaidOperatorHudIcon[]? icons)
    {
        return icons is null || icons.Length == 0
            ? "<none>"
            : string.Join(";", icons.Select(icon => $"{icon.Badge}:{icon.BaseSprite?.name ?? "<null>"}:{icon.OverlaySprite?.name ?? "<null>"}:{icon.ShowLabel}"));
    }

    private sealed record HudIconData(string Badges, VanguardRaidOperatorHudIcon[] Icons)
    {
        public static readonly HudIconData Empty = new(string.Empty, Array.Empty<VanguardRaidOperatorHudIcon>());
    }

    private sealed record MedicalEffectIconCandidate(string Badge, Type EffectType);


    private static HudIconData CombineHudIconData(HudIconData bodyPartIconData, HudIconData medicalIconData)
    {
        if (bodyPartIconData.Icons.Length == 0)
        {
            return medicalIconData;
        }

        if (medicalIconData.Icons.Length == 0)
        {
            return bodyPartIconData;
        }

        var icons = bodyPartIconData.Icons.Concat(medicalIconData.Icons).Take(10).ToArray();
        return new HudIconData(string.Join(" ", icons.Select(icon => icon.Badge)), icons);
    }

    private static HudIconData ResolveDestroyedBodyPartIconData(string bodyParts)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(bodyParts))
            {
                return HudIconData.Empty;
            }

            EnsureBodyPartIconCache(null);
            if (!BodyPartIconCacheResolved)
            {
                return HudIconData.Empty;
            }

            var icons = new List<VanguardRaidOperatorHudIcon>();
            foreach (var entry in DestroyedBodyPartBadgeMap)
            {
                if (!IsDestroyedBodyPart(bodyParts, entry.Key))
                {
                    continue;
                }

                string badge = entry.Value;
                if (!BodyPartOverlaySpritesByBadge.TryGetValue(badge, out var overlaySprite) || overlaySprite is null)
                {
                    continue;
                }

                icons.Add(new VanguardRaidOperatorHudIcon(badge, null, overlaySprite, false));
            }

            return icons.Count == 0
                ? HudIconData.Empty
                : new HudIconData(string.Join(" ", icons.Select(icon => icon.Badge)), icons.Take(7).ToArray());
        }
        catch
        {
            return HudIconData.Empty;
        }
    }

    private static string ResolveMedicalTruthSignature(string botProfileId, DateTimeOffset now)
    {
        return VanguardFikaHudMedicalTelemetryStore.TryGetFreshState(botProfileId, now, MedicalTelemetryStaleAfter, out VanguardFikaHudMedicalState remoteMedical)
            && remoteMedical.Readable
            ? remoteMedical.MaterialSignature
            : "local_canonical";
    }

    private static HudIconData ResolveRemoteMedicalEffectIconData(
        VanguardRaidOperatorHudIdentity identity,
        IEnumerable<string> authoritativeBadges,
        DateTimeOffset now)
    {
        string[] badges = authoritativeBadges
            .Where(IsIconizedMedicalBadge)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        if (badges.Length == 0)
        {
            return HudIconData.Empty;
        }

        // The Fika sidecar is authoritative only for medical presence/absence.  The local observed actor
        // is consulted here exclusively for presentation metadata (the exact EFT effect CLR type used
        // to resolve the game's sprite).  Local observations are never allowed to add or clear a badge.
        VanguardCanonicalMedicalEffectSnapshot localPresentationMetadata = VanguardCanonicalMedicalStateService.Capture(
            identity.BotProfileId,
            identity.Player,
            identity.Player.HealthController,
            VanguardRaidHudReflection.ReadMember(identity.Player, "ActiveHealthController"),
            now,
            "operator_hud_remote_presentation");

        SeedMedicalBadgeSpriteCache(localPresentationMetadata);
        return ResolveMedicalEffectIconData(badges, localPresentationMetadata);
    }

    private static HudIconData ResolveMedicalEffectIconData(
        IEnumerable<string> authoritativeBadges,
        VanguardCanonicalMedicalEffectSnapshot? localPresentationMetadata = null)
    {
        try
        {
            var icons = new List<VanguardRaidOperatorHudIcon>();
            foreach (string badge in authoritativeBadges
                         .Where(IsIconizedMedicalBadge)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Take(5))
            {
                if (TryResolveMedicalBadgeSprite(badge, localPresentationMetadata, out Sprite? sprite)
                    && sprite is not null)
                {
                    icons.Add(new VanguardRaidOperatorHudIcon(badge, sprite, null, false));
                    LogMedicalIconResolutionOnce(badge, sprite);
                }
                else
                {
                    LogMedicalIconFallbackOnce(badge);
                }
            }

            return icons.Count == 0
                ? HudIconData.Empty
                : new HudIconData(string.Join(" ", icons.Select(icon => icon.Badge)), icons.ToArray());
        }
        catch
        {
            return HudIconData.Empty;
        }
    }

    private static bool TryResolveMedicalBadgeSprite(
        string badge,
        VanguardCanonicalMedicalEffectSnapshot? localPresentationMetadata,
        out Sprite? sprite)
    {
        sprite = null;
        lock (EffectIconCacheLock)
        {
            if (SpriteByMedicalBadgeCache.TryGetValue(badge, out Sprite cachedSprite) && cachedSprite is not null)
            {
                sprite = cachedSprite;
                return true;
            }
        }

        if (localPresentationMetadata is not null
            && TryResolveMedicalBadgeSpriteFromCanonical(localPresentationMetadata, badge, out sprite)
            && sprite is not null)
        {
            CacheMedicalBadgeSprite(badge, sprite);
            return true;
        }

        // Compatibility fallback for unobfuscated EFT effect types.  This is intentionally secondary:
        // the exact type from canonical local presentation metadata is preferred whenever available.
        if (TryResolveEffectTypeForBadge(badge, out Type? effectType)
            && effectType is not null
            && TryResolveCachedEffectSprite(effectType, out sprite)
            && sprite is not null)
        {
            CacheMedicalBadgeSprite(badge, sprite);
            return true;
        }

        sprite = null;
        return false;
    }

    private static bool TryResolveMedicalBadgeSpriteFromCanonical(
        VanguardCanonicalMedicalEffectSnapshot canonical,
        string badge,
        out Sprite? sprite)
    {
        sprite = null;
        foreach (VanguardCanonicalMedicalEffectObservation observation in canonical.Observations
                     .Where(observation => string.Equals(observation.Badge, badge, StringComparison.OrdinalIgnoreCase)))
        {
            if (TryResolveCanonicalEffectType(observation, out Type? effectType)
                && effectType is not null
                && TryResolveCachedEffectSprite(effectType, out sprite)
                && sprite is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static void SeedMedicalBadgeSpriteCache(VanguardCanonicalMedicalEffectSnapshot canonical)
    {
        try
        {
            foreach (VanguardCanonicalMedicalEffectObservation observation in canonical.Observations
                         .Where(observation => IsIconizedMedicalBadge(observation.Badge)))
            {
                if (TryResolveCanonicalEffectType(observation, out Type? effectType)
                    && effectType is not null
                    && TryResolveCachedEffectSprite(effectType, out Sprite? sprite)
                    && sprite is not null)
                {
                    CacheMedicalBadgeSprite(observation.Badge, sprite);
                }
            }
        }
        catch
        {
        }
    }

    private static void CacheMedicalBadgeSprite(string badge, Sprite sprite)
    {
        lock (EffectIconCacheLock)
        {
            SpriteByMedicalBadgeCache[badge] = sprite;
        }
    }

    private static void LogMedicalIconResolutionOnce(string badge, Sprite sprite)
    {
        lock (EffectIconCacheLock)
        {
            if (!LoggedMedicalIconResolutionBadges.Add(badge))
            {
                return;
            }
        }

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorHudStatusTag,
            $"medical icon resolved badge={badge}; sprite={sprite.name}; authority=remote_or_local_canonical; presentationMetadataLocalOnly=true; textFallbackAvailable=true");
    }

    private static void LogMedicalIconFallbackOnce(string badge)
    {
        lock (EffectIconCacheLock)
        {
            if (!LoggedMedicalIconFallbackBadges.Add(badge))
            {
                return;
            }
        }

        VanguardClientDiagnosticsLog.Warning(
            VanguardBuildVersion.OperatorHudStatusTag,
            $"medical icon fallback badge={badge}; reason=sprite_unresolved; authoritativeBadgePreserved=true; textFallback=true");
    }

    private static bool TryResolveEffectTypeForBadge(string badge, out Type? effectType)
    {
        effectType = null;
        try
        {
            var dictionary = ResolveCachedEffectIconDictionary();
            if (dictionary is null)
            {
                return false;
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is Type candidateType && EffectTypeMatchesBadge(candidateType, badge))
                {
                    effectType = candidateType;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool EffectTypeMatchesBadge(Type effectType, string badge)
    {
        string typeName = (effectType.FullName ?? effectType.Name) + " " + effectType.Name;
        return badge.ToUpperInvariant() switch
        {
            "HB" => typeName.Contains("HeavyBleeding", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("HeavyBleed", StringComparison.OrdinalIgnoreCase),
            "LB" => typeName.Contains("LightBleeding", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("LightBleed", StringComparison.OrdinalIgnoreCase),
            "FR" => typeName.Contains("Fracture", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("BrokenBone", StringComparison.OrdinalIgnoreCase),
            "PN" => typeName.Contains("Pain", StringComparison.OrdinalIgnoreCase),
            "TR" => typeName.Contains("Tremor", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static HudIconData ResolveMedicalEffectIconData(VanguardCanonicalMedicalEffectSnapshot canonical)
    {
        try
        {
            var candidates = canonical.Observations
                .Where(observation => IsIconizedMedicalBadge(observation.Badge))
                .Select(observation => TryResolveCanonicalEffectType(observation, out Type? effectType) && effectType is not null
                    ? new MedicalEffectIconCandidate(observation.Badge, effectType)
                    : null)
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .GroupBy(candidate => $"{candidate.Badge}|{candidate.EffectType.FullName}", StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(5)
                .ToArray();
            if (candidates.Length == 0)
            {
                return HudIconData.Empty;
            }

            var icons = new List<VanguardRaidOperatorHudIcon>();
            foreach (var candidate in candidates)
            {
                if (TryResolveCachedEffectSprite(candidate.EffectType, out var sprite) && sprite is not null)
                {
                    CacheMedicalBadgeSprite(candidate.Badge, sprite);
                    icons.Add(new VanguardRaidOperatorHudIcon(candidate.Badge, sprite, null, false));
                    LogMedicalIconResolutionOnce(candidate.Badge, sprite);
                }
                else
                {
                    LogMedicalIconFallbackOnce(candidate.Badge);
                }
            }

            return icons.Count == 0
                ? HudIconData.Empty
                : new HudIconData(string.Join(" ", icons.Select(icon => icon.Badge)), icons.ToArray());
        }
        catch
        {
            return HudIconData.Empty;
        }
    }

    private static bool TryResolveCanonicalEffectType(VanguardCanonicalMedicalEffectObservation observation, out Type? effectType)
    {
        effectType = observation.DeclaredEffectType;
        if (effectType is not null)
        {
            return true;
        }

        string typeName = observation.DeclaredEffectTypeName;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        try
        {
            var dictionary = ResolveCachedEffectIconDictionary();
            if (dictionary is null)
            {
                return false;
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is Type candidateType
                    && (string.Equals(candidateType.Name, typeName, StringComparison.Ordinal)
                        || string.Equals(candidateType.FullName, typeName, StringComparison.Ordinal)))
                {
                    effectType = candidateType;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryResolveCachedEffectSprite(Type effectType, out Sprite? sprite)
    {
        sprite = null;
        try
        {
            lock (EffectIconCacheLock)
            {
                if (SpriteByEffectTypeCache.TryGetValue(effectType, out var cachedSprite))
                {
                    sprite = cachedSprite;
                    return sprite is not null;
                }
            }

            var dictionary = ResolveCachedEffectIconDictionary();
            if (dictionary is null)
            {
                return false;
            }

            Sprite? resolvedSprite = null;
            if (dictionary.Contains(effectType) && dictionary[effectType] is Sprite directSprite)
            {
                resolvedSprite = directSprite;
            }
            else
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is Type type && type == effectType && entry.Value is Sprite entrySprite)
                    {
                        resolvedSprite = entrySprite;
                        break;
                    }
                }
            }

            lock (EffectIconCacheLock)
            {
                SpriteByEffectTypeCache[effectType] = resolvedSprite;
            }

            sprite = resolvedSprite;
            return sprite is not null;
        }
        catch
        {
            sprite = null;
            return false;
        }
    }

    private static IDictionary? ResolveCachedEffectIconDictionary()
    {
        if (CachedEffectIconDictionary is not null)
        {
            return CachedEffectIconDictionary;
        }

        try
        {
            float now = Time.realtimeSinceStartup;
            lock (EffectIconCacheLock)
            {
                if (CachedEffectIconDictionary is not null)
                {
                    return CachedEffectIconDictionary;
                }

                if (now < NextEffectIconDictionaryResolveTime)
                {
                    return null;
                }

                NextEffectIconDictionaryResolveTime = now + 2f;
                var hardSettings = ResolveEftHardSettingsInstanceCached();
                var staticIcons = VanguardRaidHudReflection.ReadMember(hardSettings, "StaticIcons");
                var effectIcons = VanguardRaidHudReflection.ReadMember(staticIcons, "EffectIcons");
                var dictionary = VanguardRaidHudReflection.ReadMember(effectIcons, "EffectIcons");
                if (dictionary is IDictionary iconDictionary)
                {
                    CachedEffectIconDictionary = iconDictionary;
                    return CachedEffectIconDictionary;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static object? ResolveEftHardSettingsInstanceCached()
    {
        var type = ResolveEftHardSettingsTypeCached();
        return type is null ? null : VanguardRaidHudReflection.ReadStaticMember(type, "Instance");
    }

    private static Type? ResolveEftHardSettingsTypeCached()
    {
        if (CachedEftHardSettingsType is not null)
        {
            return CachedEftHardSettingsType;
        }

        if (EftHardSettingsTypeLookupAttempted)
        {
            return null;
        }

        EftHardSettingsTypeLookupAttempted = true;
        foreach (string typeName in new[] { "EFTHardSettings", "EFT.EFTHardSettings" })
        {
            var type = Type.GetType(typeName, false);
            if (type is not null)
            {
                CachedEftHardSettingsType = type;
                return type;
            }
        }

        CachedEftHardSettingsType = VanguardRaidHudReflection.FindRuntimeType("EFTHardSettings");
        return CachedEftHardSettingsType;
    }

    private static bool IsIconizedMedicalBadge(string? badge)
    {
        return string.Equals(badge, "LB", StringComparison.OrdinalIgnoreCase)
               || string.Equals(badge, "HB", StringComparison.OrdinalIgnoreCase)
               || string.Equals(badge, "FR", StringComparison.OrdinalIgnoreCase)
               || string.Equals(badge, "PN", StringComparison.OrdinalIgnoreCase)
               || string.Equals(badge, "TR", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveStatusIcons(VanguardRaidOperatorVitalitySnapshot snapshot, int healthPercent, bool isAlive, IEnumerable<string> medicalEffectBadges)
    {
        var icons = new List<string>();
        if (!isAlive || string.Equals(snapshot.IsAlive, "False", StringComparison.OrdinalIgnoreCase) || healthPercent <= 0)
        {
            icons.Add("KO");
        }

        foreach (string medicalEffectBadge in medicalEffectBadges)
        {
            icons.Add(medicalEffectBadge);
        }

        if (TryReadPhysiologicalPercent(snapshot.Physiological, "Hydration", out int hydrationPercent) && hydrationPercent < 10)
        {
            icons.Add("H");
        }

        if (TryReadPhysiologicalPercent(snapshot.Physiological, "Energy", out int energyPercent) && energyPercent < 10)
        {
            icons.Add("E");
        }

        if (healthPercent <= 40 && icons.Count == 0)
        {
            icons.Add("!");
        }

        return icons.Count == 0 ? string.Empty : string.Join(" ", icons.Distinct(StringComparer.OrdinalIgnoreCase).Take(14));
    }

    private static bool TryReadPhysiologicalPercent(string physiological, string key, out int percent)
    {
        percent = 100;
        if (string.IsNullOrWhiteSpace(physiological))
        {
            return false;
        }

        var match = Regex.Match(
            physiological,
            $"{Regex.Escape(key)}=(?<current>[0-9]+(?:[\\.,][0-9]+)?)/(?<maximum>[0-9]+(?:[\\.,][0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        if (!float.TryParse(match.Groups["current"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float current)
            || !float.TryParse(match.Groups["maximum"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float maximum)
            || maximum <= 0f)
        {
            return false;
        }

        percent = Mathf.Clamp(Mathf.RoundToInt((current / maximum) * 100f), 0, 100);
        return true;
    }

    private static readonly KeyValuePair<string, string>[] DestroyedBodyPartBadgeMap =
    {
        new("Head", "HD"),
        new("Chest", "CH"),
        new("Stomach", "ST"),
        new("LeftArm", "LA"),
        new("RightArm", "RA"),
        new("LeftLeg", "LL"),
        new("RightLeg", "RL"),
    };

    private static bool IsDestroyedBodyPart(string bodyParts, string bodyPartName)
    {
        string pattern = $"{Regex.Escape(bodyPartName)}\\s*=\\s*[^;\\r\\n]*:destroyed";
        return Regex.IsMatch(bodyParts, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? ResolveHiddenReason(VanguardRaidOperatorHudCandidate candidate)
    {
        if (candidate.DistanceMeters > MaxDistanceMeters)
        {
            return "tooFar";
        }

        if (candidate.HealthPercent <= 0)
        {
            return "deadOrZeroHealth";
        }

        return null;
    }

    private static float ResolveScale(float distanceMeters)
    {
        float t = Mathf.InverseLerp(3f, MaxDistanceMeters, distanceMeters);
        return Mathf.Lerp(1.00f, 0.45f, Mathf.Clamp01(t));
    }

    private static bool EnsureOverlayRoot(MonoBehaviour owner)
    {
        if (overlayCanvas is not null && overlayRoot is not null)
        {
            return true;
        }

        var overlayCanvasObject = new GameObject("VanguardOperatorMiniHudCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        overlayCanvasObject.transform.SetParent(owner.transform, false);

        overlayCanvas = overlayCanvasObject.GetComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.overrideSorting = true;
        overlayCanvas.sortingOrder = short.MaxValue - 16;

        var canvasScaler = overlayCanvasObject.GetComponent<CanvasScaler>();
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        canvasScaler.matchWidthOrHeight = 0.5f;

        var overlayObject = new GameObject("VanguardOperatorMiniHudRoot", typeof(RectTransform));
        overlayObject.transform.SetParent(overlayCanvasObject.transform, false);
        overlayRoot = overlayObject.GetComponent<RectTransform>();
        overlayRoot.anchorMin = Vector2.zero;
        overlayRoot.anchorMax = Vector2.one;
        overlayRoot.offsetMin = Vector2.zero;
        overlayRoot.offsetMax = Vector2.zero;
        overlayRoot.pivot = new Vector2(0.5f, 0.5f);

        fontAsset = VanguardRaidOperatorHudProjection.ResolveFont();
        VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorHudStatusTag, $"hud overlay created canvas={overlayCanvasObject.name}; root={overlayObject.name}; font={(fontAsset is null ? "None" : fontAsset.name)}; visibility={VanguardRaidOperatorHudVisibilityPolicy.CurrentMode}");
        return true;
    }

    private static VanguardRaidOperatorHudView GetOrCreateView(string key)
    {
        if (Views.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var view = VanguardRaidOperatorHudView.Create(overlayRoot!, fontAsset);
        Views[key] = view;
        return view;
    }

    private static void SetViewActive(string key, bool active)
    {
        if (Views.TryGetValue(key, out var view))
        {
            view.SetActive(active);
        }
    }

    private static void CleanupStale(IEnumerable<string> liveKeys)
    {
        var live = liveKeys.ToHashSet(StringComparer.Ordinal);
        foreach (string stale in Views.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            Views[stale].Destroy();
            Views.Remove(stale);
            LastStateByKey.Remove(stale);
            NextStateRefreshByKey.Remove(stale);
            LastContentSignatureByKey.Remove(stale);
            LastAppliedContentSignatureByKey.Remove(stale);
            LastMedicalTruthSignatureByKey.Remove(stale);
        }
    }

    private static void LogSummary(int candidateCount, int visibleCount, List<string> signatures)
    {
        string signature = string.Join("\n", signatures.OrderBy(value => value, StringComparer.Ordinal));
        if (Time.realtimeSinceStartup < nextSummaryTime && string.Equals(signature, lastSummarySignature, StringComparison.Ordinal))
        {
            return;
        }

        nextSummaryTime = Time.realtimeSinceStartup + SummaryIntervalSeconds;
        lastSummarySignature = signature;
        VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorHudStatusTag, $"scan candidates={candidateCount}; visible={visibleCount}; activeViews={Views.Count}; maxDistance={MaxDistanceMeters}; layout=mini_operator_hud; visibility={VanguardRaidOperatorHudVisibilityPolicy.CurrentMode}");
    }

    private static void EnsureBodyPartIconCache(MonoBehaviour? owner)
    {
        if (BodyPartIconCacheResolved)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        lock (BodyPartIconCacheLock)
        {
            if (BodyPartIconCacheResolved || now < NextBodyPartIconCacheAttemptTime)
            {
                return;
            }

            NextBodyPartIconCacheAttemptTime = now + 2f;
            try
            {
                var loaderType = VanguardRaidHudReflection.FindRuntimeType("Fika.Core.Bundles.InternalBundleLoader");
                var loaderInstance = loaderType is null ? null : VanguardRaidHudReflection.ReadStaticMember(loaderType, "Instance");
                var assetEnumType = loaderType?.GetNestedType("EFikaAsset", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (loaderType is null || loaderInstance is null || assetEnumType is null)
                {
                    return;
                }

                var playerUiAsset = Enum.Parse(assetEnumType, "PlayerUI");
                var getAssetMethod = VanguardRaidHudReflection.FindSingleArgumentMethod(loaderType, "GetFikaAsset", assetEnumType);
                var prefab = getAssetMethod?.Invoke(loaderInstance, new[] { playerUiAsset }) as GameObject;
                if (prefab is null)
                {
                    return;
                }

                var allComponents = prefab.GetComponentsInChildren<Component>(true).Where(component => component is not null).ToArray();
                var playerPlate = allComponents.FirstOrDefault(component =>
                {
                    string fullName = component.GetType().FullName ?? string.Empty;
                    string name = component.GetType().Name ?? string.Empty;
                    return string.Equals(name, "PlayerPlateUI", StringComparison.Ordinal)
                           || string.Equals(fullName, "PlayerPlateUI", StringComparison.Ordinal)
                           || fullName.EndsWith(".PlayerPlateUI", StringComparison.Ordinal);
                });

                if (playerPlate is null)
                {
                    return;
                }

                foreach (var mapping in BodyPartPrefabMemberByBadge)
                {
                    var partObject = VanguardRaidHudReflection.ReadInstanceMember(playerPlate, mapping.Value) as GameObject
                        ?? FindChildGameObjectByName(prefab.transform, mapping.Value);
                    BodyPartOverlaySpritesByBadge[mapping.Key] = ResolvePreferredSpriteFromGameObject(partObject, mapping.Value.ToLowerInvariant());
                }

                BodyPartIconCacheResolved = BodyPartOverlaySpritesByBadge.Values.Any(sprite => sprite is not null);
                if (BodyPartIconCacheResolved)
                {
                    VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorHudStatusTag, $"body part icon cache ready overlays={BodyPartOverlaySpritesByBadge.Count}");
                }
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorHudStatusTag, $"body part icon cache failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static Sprite? ResolvePreferredSpriteFromGameObject(GameObject? gameObject, string preferredName)
    {
        if (gameObject is null)
        {
            return null;
        }

        try
        {
            var images = gameObject.GetComponentsInChildren<Image>(true);
            foreach (var image in images)
            {
                var sprite = image.sprite;
                if (sprite is not null && (string.Equals(sprite.name, preferredName, StringComparison.OrdinalIgnoreCase) || string.Equals(image.gameObject.name, preferredName, StringComparison.OrdinalIgnoreCase)))
                {
                    return sprite;
                }
            }

            foreach (var image in images)
            {
                if (image.sprite is not null)
                {
                    return image.sprite;
                }
            }

            var renderers = gameObject.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var renderer in renderers)
            {
                var sprite = renderer.sprite;
                if (sprite is not null && (string.Equals(sprite.name, preferredName, StringComparison.OrdinalIgnoreCase) || string.Equals(renderer.gameObject.name, preferredName, StringComparison.OrdinalIgnoreCase)))
                {
                    return sprite;
                }
            }

            return renderers.Select(renderer => renderer.sprite).FirstOrDefault(sprite => sprite is not null);
        }
        catch
        {
            return null;
        }
    }

    private static GameObject? FindChildGameObjectByName(Transform root, string name)
    {
        if (string.Equals(root.name, name, StringComparison.Ordinal))
        {
            return root.gameObject;
        }

        for (int index = 0; index < root.childCount; index++)
        {
            var found = FindChildGameObjectByName(root.GetChild(index), name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }


}
#else
namespace Vanguard.Client.Raid.Hud;

internal static class VanguardRaidOperatorHudService
{
    public static void Tick(object owner)
    {
    }
}
#endif

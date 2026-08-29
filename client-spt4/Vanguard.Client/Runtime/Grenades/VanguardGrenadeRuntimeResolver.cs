#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Resolves live grenade threats around an Operator into normalized emergency evidence usable by the decision and movement layers.
// Flow: It enumerates bounded grenade observations, identifies dangerous trajectories/proximity, associates relevant ownership/context and produces the current highest-priority grenade emergency snapshot.
// Authority boundary: Resolver is read-only; it does not move the Operator or override the grenade executor/scheduler authority by itself.
// Invariant: Only current live grenades can produce an emergency, stale observations expire, and uncertain reflection/evidence degrades without fabricating a threat position.
namespace Vanguard.Client.Runtime.Grenades;

/// <summary>
/// Cached, read-only integration boundary for grenade diagnostics. The resolver never invokes
/// SAIN behavior methods and never mutates EFT, SAIN, movement, targeting or execution state.
/// </summary>
internal static class VanguardGrenadeRuntimeResolver
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, MemberInfo?> MemberCache = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<object, OwnerCacheEntry> OwnerCache = new();
    private static readonly ConditionalWeakTable<object, GrenadeCacheEntry> GrenadeCache = new();
    private static readonly FieldInfo? ThrowableRigidbodyField = AccessTools.Field(typeof(Throwable), "Rigidbody");
    // EFT Grenade keeps elapsed lifetime and pre-throw cooking time in private floats. The names are
    // verified against the canonical 4.0.13 client source; reflection is fail-soft so a future EFT
    // change degrades timing confidence rather than breaking grenade admission.
    private static readonly FieldInfo? GrenadeElapsedField = AccessTools.Field(typeof(Grenade), "float_3");
    private static readonly FieldInfo? GrenadeCookedField = AccessTools.Field(typeof(Grenade), "float_4");

    public static BotOwner? ResolveBotOwner(object? instance)
    {
        if (instance == null)
        {
            return null;
        }
        if (instance is BotOwner directOwner)
        {
            return directOwner;
        }
        if (OwnerCache.TryGetValue(instance, out OwnerCacheEntry cached))
        {
            return cached.Owner;
        }

        BotOwner? resolved = ResolveBotOwner(instance, 0, new HashSet<object>(ReferenceObjectComparer.Instance));
        if (resolved != null)
        {
            try
            {
                OwnerCache.Add(instance, new OwnerCacheEntry(resolved));
            }
            catch (ArgumentException)
            {
                // Another passive hook may have cached the same runtime object first.
            }
        }
        return resolved;
    }

    public static Grenade? ResolveGrenade(object? instance)
    {
        if (instance == null)
        {
            return null;
        }
        if (instance is Grenade grenade)
        {
            return grenade;
        }
        if (GrenadeCache.TryGetValue(instance, out GrenadeCacheEntry cached))
        {
            return cached.Grenade;
        }

        object? candidate = GetMember(instance, "Grenade", "_grenade", "grenade");
        Grenade? resolved = candidate as Grenade;
        if (resolved == null)
        {
            object? dangerPoint = GetMember(instance, "GrenadeDangerPoint", "DangerGrenade", "DangerPoint");
            resolved = GetMember(dangerPoint, "Grenade", "_grenade", "grenade") as Grenade;
        }
        if (resolved != null)
        {
            try
            {
                GrenadeCache.Add(instance, new GrenadeCacheEntry(resolved));
            }
            catch (ArgumentException)
            {
                // Another passive hook may have cached the same runtime object first.
            }
        }
        return resolved;
    }

    public static bool TryReadGrenadeThresholds(BotOwner? owner, out float addDanger, out float runAway, out float runAwaySqr)
    {
        addDanger = VanguardGrenadeHazardPolicy.FallbackRelevantDistanceMeters;
        runAway = VanguardGrenadeHazardPolicy.FallbackCriticalDistanceMeters;
        runAwaySqr = VanguardGrenadeHazardPolicy.FallbackCriticalDistanceMeters;
        if (owner == null)
        {
            return false;
        }

        object? grenadeSettings = VanguardOperatorRuntimeAuditReflection.GetDeep(owner, "Settings", "FileSettings", "Grenade");
        bool any = false;
        any |= TryConvertFloat(GetMember(grenadeSettings, "ADD_GRENADE_AS_DANGER"), ref addDanger);
        any |= TryConvertFloat(GetMember(grenadeSettings, "RUN_AWAY"), ref runAway);
        any |= TryConvertFloat(GetMember(grenadeSettings, "RUN_AWAY_SQR"), ref runAwaySqr);
        return any;
    }

    public static bool TryReadTrackerState(object? tracker, out bool spotted, out bool canReact, out Vector3 dangerPoint)
    {
        spotted = ReadBool(GetMember(tracker, "Spotted"));
        canReact = ReadBool(GetMember(tracker, "CanReact"));
        dangerPoint = GetMember(tracker, "DangerPoint") is Vector3 vector ? vector : default;
        return tracker != null;
    }

    public static bool TryReadNativeDangerState(object? bewareGrenade, out bool dangerPresent, out Grenade? grenade, out Vector3 dangerPoint)
    {
        object? nativeDanger = GetMember(bewareGrenade, "GrenadeDangerPoint");
        dangerPresent = nativeDanger != null;
        grenade = ResolveGrenade(nativeDanger);
        dangerPoint = GetMember(nativeDanger, "DangerPoint") is Vector3 vector ? vector : default;
        return dangerPresent;
    }

    public static Vector3 ReadVelocity(Grenade? grenade)
    {
        if (grenade == null)
        {
            return default;
        }

        try
        {
            return ThrowableRigidbodyField?.GetValue(grenade) is Rigidbody rigidbody
                ? rigidbody.velocity
                : default;
        }
        catch
        {
            return default;
        }
    }

    public static VanguardGrenadeFuseProfile ReadFuseProfile(Grenade? grenade, DateTimeOffset firstObservedAtUtc, DateTimeOffset now)
    {
        if (grenade == null)
        {
            return VanguardGrenadeFuseProfile.Unknown;
        }

        try
        {
            ThrowWeapItemClass? weapon = grenade.WeaponSource;
            if (weapon == null)
            {
                return VanguardGrenadeFuseProfile.Unknown;
            }

            float declared = SafeNonNegative(weapon.GetExplDelay);
            float minimumContact = weapon.MinTimeToContactExplode;
            bool contactCapable = IsFinite(minimumContact) && minimumContact >= 0f;
            bool exactElapsed = TryReadPrivateFloat(grenade, GrenadeElapsedField, out float elapsed);
            bool exactCooked = TryReadPrivateFloat(grenade, GrenadeCookedField, out float cooked);
            float observedElapsed = Math.Max(0f, (float)(now - firstObservedAtUtc).TotalSeconds);
            if (!exactElapsed)
            {
                elapsed = observedElapsed;
            }
            if (!exactCooked)
            {
                cooked = 0f;
            }

            float? remaining = null;
            string confidence = "weapon_contract_only";
            if (declared > 0f)
            {
                remaining = Math.Max(0f, declared - Math.Max(0f, cooked) - Math.Max(0f, elapsed));
                confidence = exactElapsed && exactCooked
                    ? "eft_elapsed_and_cooked_exact"
                    : exactElapsed
                        ? "eft_elapsed_exact_cooked_unknown"
                        : "registry_elapsed_conservative";
            }

            bool contactArmed = contactCapable && elapsed >= minimumContact;
            string fuseClass = contactCapable
                ? "contact_capable"
                : declared > 0f && declared <= 2.0f
                    ? "short"
                    : declared > 0f && declared <= 4.5f
                        ? "standard"
                        : declared > 4.5f
                            ? "long"
                            : "unknown";

            return new VanguardGrenadeFuseProfile(
                known: declared > 0f || contactCapable,
                fuseClass: fuseClass,
                throwType: weapon.ThrowType.ToString(),
                declaredFuseSeconds: declared,
                elapsedSeconds: Math.Max(0f, elapsed),
                cookedSeconds: Math.Max(0f, cooked),
                remainingSeconds: remaining,
                minimumContactSeconds: minimumContact,
                contactCapable: contactCapable,
                contactArmed: contactArmed,
                minimumExplosionDistance: SafeNonNegative(weapon.MinExplosionDistance),
                maximumExplosionDistance: SafeNonNegative(weapon.MaxExplosionDistance),
                fragmentsCount: Math.Max(0, weapon.FragmentsCount),
                minimumFragmentDamage: SafeNonNegative(weapon.MinFragmentDamage),
                maximumFragmentDamage: SafeNonNegative(weapon.MaxFragmentDamage),
                fragmentType: weapon.FragmentType,
                confidence: confidence);
        }
        catch
        {
            return VanguardGrenadeFuseProfile.Unknown;
        }
    }

    public static bool IsSmoke(Grenade? grenade)
    {
        return grenade == null || grenade is SmokeGrenade || grenade.GetType().Name.Contains("Smoke", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ProbeLineOfEffect(Vector3 dangerPoint, Vector3 operatorPosition)
    {
        Vector3 start = dangerPoint + Vector3.up * 0.10f;
        Vector3 end = operatorPosition + Vector3.up * VanguardGrenadeHazardPolicy.LineOfEffectProbeHeightMeters;
        try
        {
            return Physics.Linecast(start, end, LayerMaskClass.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
        }
        catch
        {
            return false;
        }
    }

    public static void ResolveSource(
        string? profileId,
        out string normalizedProfileId,
        out string sourceName,
        out VanguardGrenadeSourceRelation relation)
    {
        normalizedProfileId = Normalize(profileId);
        sourceName = "none";
        relation = VanguardGrenadeSourceRelation.Unknown;
        if (normalizedProfileId == "none")
        {
            return;
        }

        if (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(normalizedProfileId, out VanguardRaidOperatorRuntimeRecord runtime))
        {
            sourceName = Normalize(runtime.BotNickname);
            relation = VanguardGrenadeSourceRelation.Operator;
            return;
        }

        if (VanguardRaidOperatorRuntimeRegistry.IsKnownOwnerProfileId(normalizedProfileId))
        {
            relation = VanguardGrenadeSourceRelation.PlayerOwner;
        }

        try
        {
            Player? player = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(normalizedProfileId);
            if (player == null)
            {
                return;
            }

            sourceName = Normalize(
                VanguardOperatorRuntimeAuditReflection.GetDeep(player, "Profile", "Nickname")?.ToString(),
                VanguardOperatorRuntimeAuditReflection.GetMember(player, "Nickname")?.ToString(),
                normalizedProfileId);

            if (relation == VanguardGrenadeSourceRelation.Unknown)
            {
                bool isAi = ReadBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "IsAI"));
                relation = isAi ? VanguardGrenadeSourceRelation.HostileOrNeutral : VanguardGrenadeSourceRelation.PlayerClient;
            }
        }
        catch
        {
            // Source identity is diagnostic metadata only. A missing player must never suppress
            // grenade danger observation or affect gameplay.
        }
    }

    public static bool IsConfirmedEnemyForBot(BotOwner? owner, string? sourceProfileId)
    {
        string source = Normalize(sourceProfileId);
        if (owner == null || source == "none" || string.Equals(owner.ProfileId, source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(owner.ProfileId, source))
        {
            return false;
        }

        try
        {
            Player? player = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(source);
            if (player == null || owner.BotsGroup == null)
            {
                return false;
            }

            // EFT.Player also implements Dissonance.IDissonancePlayer. Passing the concrete Player
            // directly to an IPlayer API makes the C# compiler resolve every implemented interface
            // and therefore requires an otherwise unused DissonanceVoip reference. Erase the concrete
            // type first, then bind only the EFT.IPlayer contract, matching the existing Vanguard
            // hostility resolvers and keeping the canonical client dependency graph unchanged.
            object rawPlayer = player;
            return rawPlayer is IPlayer target
                && (owner.BotsGroup.IsEnemy(target) || owner.BotsGroup.IsPlayerEnemy(target));
        }
        catch
        {
            // Hostile source propagation is optional evidence enrichment. Failure remains unknown,
            // never hostile-by-default, and never affects grenade survival admission.
            return false;
        }
    }

    public static string ReadSainDecision(object? decisionManager, int argumentIndex, object[]? args)
    {
        if (args != null && argumentIndex >= 0 && argumentIndex < args.Length && args[argumentIndex] != null)
        {
            return Safe(args[argumentIndex]?.ToString());
        }

        return argumentIndex switch
        {
            0 => Safe(GetMember(decisionManager, "CurrentCombatDecision")?.ToString()),
            1 => Safe(GetMember(decisionManager, "CurrentSquadDecision")?.ToString()),
            2 => Safe(GetMember(decisionManager, "CurrentSelfDecision")?.ToString()),
            _ => "none",
        };
    }

    public static object? GetMember(object? instance, params string[] names)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            MemberInfo? member = ResolveMember(type, name);
            try
            {
                if (member is PropertyInfo property)
                {
                    return property.GetValue(instance, null);
                }
                if (member is FieldInfo field)
                {
                    return field.GetValue(instance);
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static BotOwner? ResolveBotOwner(object? instance, int depth, HashSet<object> visited)
    {
        if (instance == null || depth > 5 || !visited.Add(instance))
        {
            return null;
        }
        if (instance is BotOwner owner)
        {
            return owner;
        }

        foreach (string name in new[] { "BotOwner", "BotOwner_0", "_botOwner", "botOwner", "Owner" })
        {
            object? direct = GetMember(instance, name);
            if (direct is BotOwner directOwner)
            {
                return directOwner;
            }
        }

        foreach (string name in new[] { "Bot", "_bot", "BaseClass", "DecisionClass", "GrenadeReactionClass", "ThrowWeapItemClass" })
        {
            object? nested = GetMember(instance, name);
            BotOwner? resolved = ResolveBotOwner(nested, depth + 1, visited);
            if (resolved != null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static MemberInfo? ResolveMember(Type type, string name)
    {
        string key = type.AssemblyQualifiedName + "|" + name;
        lock (CacheSync)
        {
            if (MemberCache.TryGetValue(key, out MemberInfo? cached))
            {
                return cached;
            }
        }

        Type? current = type;
        MemberInfo? resolved = null;
        while (current != null && resolved == null)
        {
            resolved = current.GetProperty(name, InstanceFlags) ?? (MemberInfo?)current.GetField(name, InstanceFlags);
            current = current.BaseType;
        }

        lock (CacheSync)
        {
            MemberCache[key] = resolved;
        }
        return resolved;
    }

    private static bool TryConvertFloat(object? value, ref float destination)
    {
        try
        {
            if (value == null)
            {
                return false;
            }
            float converted = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
            if (float.IsNaN(converted) || float.IsInfinity(converted) || converted <= 0f)
            {
                return false;
            }
            destination = converted;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadPrivateFloat(object instance, FieldInfo? field, out float value)
    {
        value = 0f;
        if (field == null)
        {
            return false;
        }
        try
        {
            object? raw = field.GetValue(instance);
            if (raw is float typed && IsFinite(typed))
            {
                value = typed;
                return true;
            }
        }
        catch
        {
            // Timing remains optional evidence.
        }
        return false;
    }

    private static float SafeNonNegative(float value) => IsFinite(value) && value >= 0f ? value : 0f;
    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool ReadBool(object? value)
    {
        try
        {
            return value is bool typed ? typed : value != null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    public static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }

    public static string Normalize(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return Safe(value);
            }
        }
        return "none";
    }

    private sealed class OwnerCacheEntry
    {
        public OwnerCacheEntry(BotOwner owner) => Owner = owner;
        public BotOwner Owner { get; }
    }

    private sealed class GrenadeCacheEntry
    {
        public GrenadeCacheEntry(Grenade grenade) => Grenade = grenade;
        public Grenade Grenade { get; }
    }

    private sealed class ReferenceObjectComparer : IEqualityComparer<object>
    {
        public static ReferenceObjectComparer Instance { get; } = new();
        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
        int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
#endif

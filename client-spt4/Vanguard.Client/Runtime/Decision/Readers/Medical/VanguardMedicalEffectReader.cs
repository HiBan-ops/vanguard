#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Reads and normalizes live evidence for Medical Effect Reader in the decision snapshot pipeline.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

/// <summary>
/// grenade subsystem medical decision facade. Critical body-part truth remains direct on every snapshot;
/// active effects come from the shared canonical service also consumed by the HUD. The canonical
/// service owns the bounded List_1/rich-signature scan and its typed compatibility fallback, so the
/// decision layer no longer maintains a divergent effect cache or enumeration path.
/// </summary>
internal static class VanguardMedicalEffectReader
{
    public const string BudgetStatusTag = "VANGUARD_MEDICAL_EFFECT_BUDGET_STATUS";

    private static readonly EBodyPart[] BodyParts =
    {
        EBodyPart.Head,
        EBodyPart.Chest,
        EBodyPart.Stomach,
        EBodyPart.LeftArm,
        EBodyPart.RightArm,
        EBodyPart.LeftLeg,
        EBodyPart.RightLeg,
    };

    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, MethodInfo?> BodyPartBoolMethodCache = new(StringComparer.Ordinal);

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (CacheSync)
        {
            BodyPartBoolMethodCache.Clear();
        }

        VanguardCanonicalMedicalStateService.ResetForRaidLifecycle(reason);
        VanguardClientDiagnosticsLog.Info(BudgetStatusTag,
            $"VANGUARD_MEDICAL_EFFECT_READER_RESET reason={Safe(reason)}; role=canonical_state_facade; criticalTruth=direct; sharedHudDecisionTruth=true; tag={BudgetStatusTag}; canonicalTag={VanguardCanonicalMedicalStateService.StatusTag}");
    }

    public static VanguardMedicalNeedSnapshot Capture(BotOwner? botOwner, object? activeHealthController, string caller, bool forceRefresh = false)
    {
        if (botOwner == null)
        {
            return new VanguardMedicalNeedSnapshot { IsReadable = false, Source = "botOwnerNull;caller=" + Safe(caller) };
        }

        try
        {
            object? player = botOwner.GetPlayer;
            object? healthController = botOwner.HealthController
                ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "HealthController")
                ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "ActiveHealthController");
            activeHealthController ??= VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(player, "ActiveHealthController") ?? healthController;

            string botProfileId = botOwner.Profile?.Id
                ?? VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "ProfileId")?.ToString()
                ?? "unknown";
            DateTimeOffset now = DateTimeOffset.UtcNow;
            VanguardCanonicalMedicalEffectSnapshot effects = VanguardCanonicalMedicalStateService.Capture(
                botProfileId,
                player,
                healthController,
                activeHealthController,
                now,
                caller,
                forceRefresh);
            var badges = new HashSet<string>(effects.Badges, StringComparer.OrdinalIgnoreCase);
            var effectTargetByBadge = new Dictionary<string, string>(effects.TargetByBadge, StringComparer.OrdinalIgnoreCase);

            var destroyedParts = new List<string>();
            var damagedParts = new List<string>();
            var brokenParts = new List<string>();
            float currentTotal = 0f;
            float maximumTotal = 0f;

            foreach (var part in BodyParts)
            {
                if (!TryReadPartState(botOwner, activeHealthController, part, out float current, out float maximum, out bool destroyed, out bool broken))
                {
                    continue;
                }

                currentTotal += Math.Max(0f, current);
                maximumTotal += Math.Max(0f, maximum);

                bool damaged = maximum > 0.5f && current < maximum - 0.5f;
                string partName = part.ToString();
                if (destroyed) destroyedParts.Add(partName);
                if (broken) brokenParts.Add(partName);
                if (damaged || destroyed || broken)
                {
                    damagedParts.Add(partName + "=" + current.ToString("0") + "/" + maximum.ToString("0")
                        + (destroyed ? ":black" : string.Empty)
                        + (broken ? ":broken" : string.Empty));
                }
            }

            int healthPercent = maximumTotal > 0f
                ? ClampPercent((int)Math.Round((currentTotal / maximumTotal) * 100f))
                : 100;
            bool hasHeavyBleed = badges.Contains("HB");
            bool hasLightBleed = badges.Contains("LB");
            bool hasPain = badges.Contains("PN");
            bool hasTremor = badges.Contains("TR");
            var operableDestroyedParts = destroyedParts.Where(VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget).ToList();
            var untreatableVitalParts = destroyedParts.Where(VanguardMedicalSurgeryTargetPolicy.IsUntreatableVitalTarget).ToList();
            bool hasDestroyedPart = destroyedParts.Count > 0;
            bool hasOperableDestroyedPart = operableDestroyedParts.Count > 0;
            bool hasUntreatableVitalDamage = untreatableVitalParts.Count > 0;
            bool hasFracture = badges.Contains("FR") || brokenParts.Count > 0;
            bool hasHpDamage = damagedParts.Any(token => token.IndexOf(":black", StringComparison.OrdinalIgnoreCase) < 0);
            bool hasBlackBroken = damagedParts.Any(token => token.IndexOf(":black", StringComparison.OrdinalIgnoreCase) >= 0
                && token.IndexOf(":broken", StringComparison.OrdinalIgnoreCase) >= 0);
            VanguardMedicalNeed dominantNeed = ResolveDominantNeed(hasHeavyBleed, hasLightBleed, hasOperableDestroyedPart, hasHpDamage, hasFracture, hasPain || hasTremor, hasUntreatableVitalDamage);
            string target = ResolveTargetPart(dominantNeed, effectTargetByBadge, operableDestroyedParts, untreatableVitalParts, brokenParts, damagedParts);

            return new VanguardMedicalNeedSnapshot
            {
                IsReadable = healthController != null || activeHealthController != null || maximumTotal > 0f,
                DominantNeed = dominantNeed,
                HealthPercent = healthPercent,
                HasHeavyBleed = hasHeavyBleed,
                HasLightBleed = hasLightBleed,
                HasFracture = hasFracture,
                HasPain = hasPain,
                HasTremor = hasTremor,
                HasDestroyedPart = hasDestroyedPart,
                HasHpDamage = hasHpDamage,
                HasBlackBroken = hasBlackBroken,
                HasOperableDestroyedPart = hasOperableDestroyedPart,
                HasUntreatableVitalDamage = hasUntreatableVitalDamage,
                UntreatableVitalPartCount = untreatableVitalParts.Count,
                UntreatableVitalParts = JoinOrNone(untreatableVitalParts),
                DestroyedPartCount = destroyedParts.Count,
                DamagedPartCount = damagedParts.Count,
                BrokenPartCount = brokenParts.Count,
                TargetKnown = !string.Equals(target, "none", StringComparison.OrdinalIgnoreCase),
                TargetPart = target,
                Badges = JoinOrNone(badges.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                DestroyedParts = JoinOrNone(destroyedParts),
                DamagedParts = JoinOrNone(damagedParts),
                BrokenParts = JoinOrNone(brokenParts),
                RawEffectNames = JoinOrNone(effects.RawSignatures.Take(18)),
                Source = "healthController_canonical_snapshot;effects=" + Safe(effects.Source)
                    + ";revision=" + effects.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ";complete=" + (effects.ScanComplete ? "true" : "false")
                    + ";caller=" + Safe(caller)
            };
        }
        catch (Exception ex)
        {
            return new VanguardMedicalNeedSnapshot
            {
                IsReadable = false,
                DominantNeed = VanguardMedicalNeed.None,
                HealthPercent = 100,
                Source = "snapshotFailed;caller=" + Safe(caller) + ";reason=" + ex.GetType().Name,
                RawEffectNames = "snapshot_exception_" + ex.GetType().Name
            };
        }
    }

    private static VanguardMedicalNeed ResolveDominantNeed(bool hasHeavyBleed, bool hasLightBleed, bool hasOperableDestroyedPart, bool hasHpDamage, bool hasFracture, bool hasPainMobility, bool hasUntreatableVitalDamage)
    {
        if (hasHeavyBleed) return VanguardMedicalNeed.HeavyBleed;
        if (hasLightBleed) return VanguardMedicalNeed.LightBleed;
        if (hasOperableDestroyedPart) return VanguardMedicalNeed.SurgeryDestroyedPart;
        if (hasFracture) return VanguardMedicalNeed.Fracture;
        if (hasHpDamage) return VanguardMedicalNeed.HpHeal;
        if (hasPainMobility) return VanguardMedicalNeed.PainMobility;
        if (hasUntreatableVitalDamage) return VanguardMedicalNeed.UntreatableVitalDestroyedPart;
        return VanguardMedicalNeed.None;
    }

    private static string ResolveTargetPart(VanguardMedicalNeed need, IReadOnlyDictionary<string, string> effectTargetByBadge, IReadOnlyList<string> operableDestroyedParts, IReadOnlyList<string> untreatableVitalParts, IReadOnlyList<string> brokenParts, IReadOnlyList<string> damagedParts)
    {
        if (need == VanguardMedicalNeed.HeavyBleed && effectTargetByBadge.TryGetValue("HB", out string heavyTarget)) return SafePart(heavyTarget);
        if (need == VanguardMedicalNeed.LightBleed && effectTargetByBadge.TryGetValue("LB", out string lightTarget)) return SafePart(lightTarget);
        if (need == VanguardMedicalNeed.Fracture)
        {
            if (effectTargetByBadge.TryGetValue("FR", out string fractureTarget)) return SafePart(fractureTarget);
            return SafePart(brokenParts.FirstOrDefault());
        }
        if (need == VanguardMedicalNeed.SurgeryDestroyedPart) return SafePart(operableDestroyedParts.FirstOrDefault());
        if (need == VanguardMedicalNeed.HpHeal)
        {
            string? healTarget = damagedParts.FirstOrDefault(token => token.IndexOf(":black", StringComparison.OrdinalIgnoreCase) < 0);
            return SafePart(ExtractPartName(healTarget));
        }
        if (need == VanguardMedicalNeed.UntreatableVitalDestroyedPart) return SafePart(untreatableVitalParts.FirstOrDefault());
        return "none";
    }

    private static bool TryReadPartState(BotOwner botOwner, object? activeHealthController, EBodyPart part, out float current, out float maximum, out bool destroyed, out bool broken)
    {
        current = 0f;
        maximum = 0f;
        destroyed = false;
        broken = false;
        try
        {
            ValueStruct health = botOwner.HealthController.GetBodyPartHealth(part, false);
            current = health.Current;
            maximum = health.Maximum;
        }
        catch { return false; }

        try
        {
            destroyed = botOwner.GetPlayer?.ActiveHealthController?.IsBodyPartDestroyed(part) == true
                || InvokeBool(activeHealthController, "IsBodyPartDestroyed", part)
                || current <= 0.5f;
        }
        catch { destroyed = current <= 0.5f; }

        try
        {
            broken = botOwner.GetPlayer?.ActiveHealthController?.IsBodyPartBroken(part) == true
                || InvokeBool(activeHealthController, "IsBodyPartBroken", part);
        }
        catch { broken = false; }
        return maximum > 0f;
    }

    private static bool InvokeBool(object? target, string methodName, EBodyPart bodyPart)
    {
        if (target == null) return false;
        try
        {
            MethodInfo? method = ResolveBodyPartBoolMethod(target.GetType(), methodName);
            return method?.Invoke(target, new object[] { bodyPart }) is bool result && result;
        }
        catch { return false; }
    }

    private static MethodInfo? ResolveBodyPartBoolMethod(Type type, string methodName)
    {
        string key = (type.AssemblyQualifiedName ?? type.FullName ?? type.Name) + "|" + methodName;
        lock (CacheSync)
        {
            if (BodyPartBoolMethodCache.TryGetValue(key, out MethodInfo? cached))
            {
                return cached;
            }
        }

        MethodInfo? resolved = null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodInfo candidate in type.GetMethods(flags))
        {
            if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(EBodyPart))
            {
                resolved = candidate;
                break;
            }
        }

        lock (CacheSync)
        {
            BodyPartBoolMethodCache[key] = resolved;
        }
        return resolved;
    }

    private static int ClampPercent(int value) => Math.Max(0, Math.Min(100, value));
    private static string JoinOrNone(IEnumerable<string> values)
    {
        string[] array = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return array.Length == 0 ? "none" : string.Join(",", array.Select(Safe));
    }
    private static string SafePart(string? value) => Safe(ExtractPartName(value));
    private static string ExtractPartName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        string token = value.Trim();
        int eq = token.IndexOf('='); if (eq > 0) token = token.Substring(0, eq);
        int colon = token.IndexOf(':'); if (colon > 0) token = token.Substring(0, colon);
        return token;
    }
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
}
#endif

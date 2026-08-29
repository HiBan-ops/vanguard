#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT;
using EFT.HealthSystem;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Builds one normalized medical picture for an Operator from EFT health, effects and inventory so every medical subsystem reasons from the same facts.
// Flow: Native body-part/effect/controller data is read, normalized into canonical injuries/actionability, compared with typed/native snapshots for divergence, and exposed to planners/executors as read-only state.
// Authority boundary: EFT health/effects/inventory are truth; this service interprets them but does not heal, consume items or grant medical execution authority.
// Invariant: The same native facts must produce a stable canonical result, and missing/ambiguous data must degrade conservatively rather than fabricate a treatable condition.
namespace Vanguard.Client.Runtime.Medical;

/// <summary>
/// grenade subsystem canonical active-effect truth. HUD and medical decisions consume the same immutable
/// observation instead of maintaining two incompatible health-effect readers. The canonical path
/// mirrors the proven HUD surface (HealthController/ActiveHealthController List_1, rich runtime
/// signature) while remaining bounded and cached. Typed GetAllActiveEffects is retained only as a
/// compatibility fallback and low-cadence divergence probe; it never overrides a readable List_1.
/// </summary>
internal static class VanguardCanonicalMedicalStateService
{
    public const string StatusTag = "VANGUARD_CANONICAL_MEDICAL_STATE_STATUS";
    public const string RefreshTag = "VANGUARD_MEDICAL_CANONICAL_REFRESH";
    public const string DivergenceTag = "VANGUARD_MEDICAL_CANONICAL_TYPED_DIVERGENCE";
    public const string ForceRefreshTag = "VANGUARD_MEDICAL_CANONICAL_FORCE_REFRESH";
    public const string ConvergenceStatusTag = "VANGUARD_CANONICAL_MEDICAL_CONVERGENCE_STATUS";
    public const string ConvergenceTag = "VANGUARD_MEDICAL_CANONICAL_CONVERGENCE";

    private sealed class CacheEntry
    {
        public VanguardCanonicalMedicalEffectSnapshot Snapshot = VanguardCanonicalMedicalEffectSnapshot.Empty;
        public DateTimeOffset NextRefreshAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset NextTypedAuditAtUtc = DateTimeOffset.MinValue;
        public string PendingForceReason = string.Empty;
        public string LastLoggedSignature = string.Empty;
        public long CanonicalScans;
        public long MaterialRevisionChanges;
        public long UnchangedRefreshes;
        public long SelectionOverlays;
        public long SelectionDeferrals;
        public long PriorityPreemptions;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static ReferenceComparer Instance { get; } = new();
        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CacheEntry> CacheByBotProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MemberInfo?> MemberCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(350d);
    private static readonly TimeSpan GlobalScanSpacing = TimeSpan.FromMilliseconds(35d);
    private static readonly TimeSpan TypedDivergenceAuditInterval = TimeSpan.FromSeconds(8d);
    private static readonly TimeSpan BudgetLogInterval = TimeSpan.FromSeconds(10d);
    private static readonly Dictionary<string, DateTimeOffset> LastBudgetLogAt = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset GlobalNextScanAtUtc = DateTimeOffset.MinValue;
    private static long NextRevision;
    private const int MaxEffects = 64;
    private const double EnumerationBudgetMilliseconds = 2.50d;

    public static void ResetForRaidLifecycle(string reason)
    {
        lock (Sync)
        {
            CacheByBotProfile.Clear();
            MemberCache.Clear();
            LastBudgetLogAt.Clear();
            GlobalNextScanAtUtc = DateTimeOffset.MinValue;
            NextRevision = 0;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_MEDICAL_CANONICAL_STATE_RESET reason={Safe(reason)}; authority=single_shared_snapshot; primary=controller_List_1; typed=fallback_and_divergence_probe; maxEffects={MaxEffects}; budgetMs={EnumerationBudgetMilliseconds:0.00}; tag={StatusTag}");
    }

    public static void RequestForceRefresh(string botProfileId, string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)) return;
        lock (Sync)
        {
            if (!CacheByBotProfile.TryGetValue(botProfileId, out CacheEntry? entry))
            {
                entry = new CacheEntry();
                CacheByBotProfile[botProfileId] = entry;
            }
            entry.PendingForceReason = Safe(reason);
            entry.NextRefreshAtUtc = DateTimeOffset.MinValue;
        }

        VanguardClientDiagnosticsLog.Diagnostic(ForceRefreshTag, () =>
            $"botProfile={Safe(botProfileId)}; reason={Safe(reason)}; nextCaptureForced=true; tag={StatusTag}");
    }

    public static VanguardCanonicalMedicalEffectSnapshot Capture(
        string botProfileId,
        object? player,
        object? healthController,
        object? activeHealthController,
        DateTimeOffset now,
        string reason,
        bool forceRefresh = false)
    {
        botProfileId = string.IsNullOrWhiteSpace(botProfileId) ? "unknown" : botProfileId;
        healthController ??= ReadMember(player, "HealthController");
        activeHealthController ??= ReadMember(player, "ActiveHealthController") ?? healthController;

        CacheEntry entry;
        string pendingForceReason;
        lock (Sync)
        {
            if (!CacheByBotProfile.TryGetValue(botProfileId, out CacheEntry? cachedEntry))
            {
                cachedEntry = new CacheEntry();
                CacheByBotProfile[botProfileId] = cachedEntry;
            }
            entry = cachedEntry;

            pendingForceReason = entry.PendingForceReason;
            bool force = forceRefresh || !string.IsNullOrWhiteSpace(pendingForceReason);
            if (!force && now < entry.NextRefreshAtUtc)
            {
                return entry.Snapshot;
            }

            if (!force && now < GlobalNextScanAtUtc && entry.Snapshot.Revision > 0)
            {
                entry.NextRefreshAtUtc = GlobalNextScanAtUtc;
                return entry.Snapshot;
            }

            GlobalNextScanAtUtc = now + GlobalScanSpacing;
            entry.PendingForceReason = string.Empty;
        }

        string refreshReason = !string.IsNullOrWhiteSpace(pendingForceReason)
            ? pendingForceReason
            : forceRefresh ? Safe(reason) + ":caller_forced" : Safe(reason);
        VanguardCanonicalMedicalEffectSnapshot previous = entry.Snapshot;
        VanguardCanonicalMedicalEffectSnapshot captured = ScanCanonical(
            botProfileId,
            healthController,
            activeHealthController,
            previous,
            now,
            refreshReason);

        lock (Sync)
        {
            entry.Snapshot = captured;
            entry.NextRefreshAtUtc = now + RefreshInterval;
            entry.CanonicalScans++;
            if (captured.Revision != previous.Revision) entry.MaterialRevisionChanges++;
            else entry.UnchangedRefreshes++;
        }

        LogRefreshIfMaterial(entry, previous, captured, refreshReason);
        long typedAuditStarted = VanguardRuntimePerformanceGuard.Begin();
        MaybeAuditTypedDivergence(entry, botProfileId, healthController, activeHealthController, captured, now);
        VanguardRuntimePerformanceGuard.End("MedicalCanonicalTypedDivergenceAudit", typedAuditStarted);
        return captured;
    }

    private static VanguardCanonicalMedicalEffectSnapshot ScanCanonical(
        string botProfileId,
        object? healthController,
        object? activeHealthController,
        VanguardCanonicalMedicalEffectSnapshot previous,
        DateTimeOffset now,
        string reason)
    {
        var observations = new List<VanguardCanonicalMedicalEffectObservation>();
        var seenControllers = new HashSet<object>(ReferenceComparer.Instance);
        var seenEffects = new HashSet<object>(ReferenceComparer.Instance);
        var controllers = new[] { healthController, activeHealthController };
        bool anyController = false;
        bool anyCanonicalCollectionReadable = false;
        bool truncated = false;
        bool budgetExceeded = false;
        int examined = 0;
        long started = Stopwatch.GetTimestamp();
        var sources = new List<string>();

        foreach (object? controller in controllers)
        {
            if (controller == null || !seenControllers.Add(controller)) continue;
            anyController = true;
            long listReadStarted = VanguardRuntimePerformanceGuard.Begin();
            object? rawCollection = ReadMember(controller, "List_1");
            VanguardRuntimePerformanceGuard.End("MedicalCanonicalListRead", listReadStarted);
            if (rawCollection == null) continue;
            anyCanonicalCollectionReadable = true;
            sources.Add(controller.GetType().Name + ".List_1");
            long enumerateStarted = VanguardRuntimePerformanceGuard.Begin();
            AppendEffects(rawCollection, observations, seenEffects, ref examined, started, ref truncated, ref budgetExceeded);
            VanguardRuntimePerformanceGuard.End("MedicalCanonicalEffectEnumeration", enumerateStarted);
            if (truncated) break;
        }

        string source;
        if (!anyCanonicalCollectionReadable)
        {
            source = AppendTypedFallback(activeHealthController as IHealthController ?? healthController as IHealthController,
                observations, seenEffects, ref examined, started, ref truncated, ref budgetExceeded)
                ? "typed_GetAllActiveEffects_fallback"
                : anyController ? "controller_present_List_1_unreadable" : "controller_missing";
        }
        else
        {
            source = string.Join("+", sources.Distinct(StringComparer.Ordinal));
        }

        if ((truncated || budgetExceeded) && previous.Revision > 0)
        {
            MergePrevious(observations, previous.Observations);
            source += ":partial_previous_merged";
        }
        else
        {
            source += ":complete";
        }

        var badges = observations
            .Select(item => item.Badge)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardCanonicalMedicalEffectObservation observation in observations)
        {
            if (!string.Equals(observation.BodyPart, "none", StringComparison.OrdinalIgnoreCase)
                && !targets.ContainsKey(observation.Badge))
            {
                targets[observation.Badge] = observation.BodyPart;
            }
        }

        var ordered = observations
            .GroupBy(item => item.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Badge, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.BodyPart, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Signature, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool scanComplete = !truncated && !budgetExceeded;
        string revisionSignature = BuildRevisionSignature(
            badges,
            targets,
            anyController,
            scanComplete,
            truncated,
            budgetExceeded);
        long revision;
        if (previous.Revision > 0 && string.Equals(previous.RevisionSignature, revisionSignature, StringComparison.Ordinal))
        {
            revision = previous.Revision;
        }
        else
        {
            lock (Sync) revision = ++NextRevision;
        }

        if (budgetExceeded) LogBudgetExceeded(botProfileId, now, examined, ElapsedMilliseconds(started));
        return new VanguardCanonicalMedicalEffectSnapshot
        {
            BotProfileId = botProfileId,
            CapturedAtUtc = now,
            Revision = revision,
            RefreshReason = reason,
            Source = source,
            ControllerObserved = anyController,
            ScanComplete = scanComplete,
            ScanTruncated = truncated,
            BudgetExceeded = budgetExceeded,
            ExaminedEffectCount = examined,
            Observations = ordered,
            Badges = badges,
            TargetByBadge = targets,
            RawSignatures = ordered.Select(item => Trim(item.Signature, 140)).Take(32).ToArray()
        };
    }

    private static void AppendEffects(
        object rawCollection,
        List<VanguardCanonicalMedicalEffectObservation> observations,
        HashSet<object> seenEffects,
        ref int examined,
        long started,
        ref bool truncated,
        ref bool budgetExceeded)
    {
        if (rawCollection is IEnumerable enumerable && rawCollection is not string)
        {
            foreach (object? raw in enumerable)
            {
                if (ReachedLimit(ref examined, started, ref truncated, ref budgetExceeded)) break;
                AppendEffect(raw, observations, seenEffects);
            }
            return;
        }

        if (!ReachedLimit(ref examined, started, ref truncated, ref budgetExceeded))
        {
            AppendEffect(rawCollection, observations, seenEffects);
        }
    }

    private static bool AppendTypedFallback(
        IHealthController? controller,
        List<VanguardCanonicalMedicalEffectObservation> observations,
        HashSet<object> seenEffects,
        ref int examined,
        long started,
        ref bool truncated,
        ref bool budgetExceeded)
    {
        if (controller == null) return false;
        try
        {
            IEnumerable effects = controller.GetAllActiveEffects();
            AppendEffects(effects, observations, seenEffects, ref examined, started, ref truncated, ref budgetExceeded);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReachedLimit(ref int examined, long started, ref bool truncated, ref bool budgetExceeded)
    {
        examined++;
        if (examined > MaxEffects)
        {
            truncated = true;
            return true;
        }
        if (ElapsedMilliseconds(started) >= EnumerationBudgetMilliseconds)
        {
            budgetExceeded = true;
            return true;
        }
        return false;
    }

    private static void AppendEffect(object? raw, List<VanguardCanonicalMedicalEffectObservation> observations, HashSet<object> seenEffects)
    {
        object? effect = UnwrapCollectionItem(raw);
        if (effect == null || !seenEffects.Add(effect)) return;
        long signatureStarted = VanguardRuntimePerformanceGuard.Begin();
        string signature = BuildRichSignature(effect);
        VanguardRuntimePerformanceGuard.End("MedicalCanonicalEffectSignature", signatureStarted);
        string? badge = ResolveMedicalBadge(signature);
        if (badge == null) return;

        long metadataStarted = VanguardRuntimePerformanceGuard.Begin();
        object? rawDeclaredType = ReadMember(effect, "Type") ?? ReadMember(effect, "EffectType");
        Type? declaredEffectType = rawDeclaredType as Type;
        string declaredEffectTypeName = declaredEffectType?.FullName
            ?? rawDeclaredType?.ToString()
            ?? string.Empty;
        string bodyPart = SafePart(ReadMember(effect, "BodyPart")?.ToString());
        VanguardRuntimePerformanceGuard.End("MedicalCanonicalEffectMetadata", metadataStarted);
        observations.Add(new VanguardCanonicalMedicalEffectObservation(
            badge,
            bodyPart,
            signature,
            effect.GetType(),
            declaredEffectType,
            declaredEffectTypeName));
    }

    private static void MergePrevious(List<VanguardCanonicalMedicalEffectObservation> target, IReadOnlyList<VanguardCanonicalMedicalEffectObservation> previous)
    {
        var keys = new HashSet<string>(target.Select(item => item.IdentityKey), StringComparer.OrdinalIgnoreCase);
        foreach (VanguardCanonicalMedicalEffectObservation observation in previous)
        {
            if (keys.Add(observation.IdentityKey)) target.Add(observation);
        }
    }

    private static void MaybeAuditTypedDivergence(
        CacheEntry entry,
        string botProfileId,
        object? healthController,
        object? activeHealthController,
        VanguardCanonicalMedicalEffectSnapshot canonical,
        DateTimeOffset now)
    {
        if (!canonical.ScanComplete || now < entry.NextTypedAuditAtUtc) return;
        entry.NextTypedAuditAtUtc = now + TypedDivergenceAuditInterval;
        IHealthController? typed = activeHealthController as IHealthController ?? healthController as IHealthController;
        if (typed == null) return;

        try
        {
            var typedBadges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            long started = Stopwatch.GetTimestamp();
            foreach (object? raw in typed.GetAllActiveEffects())
            {
                if (raw == null || ++count > 32 || ElapsedMilliseconds(started) > 1.25d) break;
                object? effect = UnwrapCollectionItem(raw);
                if (effect == null) continue;
                string? badge = ResolveMedicalBadge(BuildRichSignature(effect));
                if (badge != null) typedBadges.Add(badge);
            }

            string[] missingFromTyped = canonical.Badges.Where(badge => !typedBadges.Contains(badge)).ToArray();
            string[] typedOnly = typedBadges.Where(badge => !canonical.Badges.Contains(badge, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (missingFromTyped.Length > 0 || typedOnly.Length > 0)
            {
                VanguardClientDiagnosticsLog.Warning(DivergenceTag,
                    $"botProfile={Safe(botProfileId)}; canonicalBadges={Join(canonical.Badges)}; typedBadges={Join(typedBadges)}; missingFromTyped={Join(missingFromTyped)}; typedOnly={Join(typedOnly)}; authority=canonical_List_1; decisionUsesCanonical=true; hudUsesCanonical=true; tag={StatusTag}");
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Diagnostic(DivergenceTag, () =>
                $"botProfile={Safe(botProfileId)}; typedAudit=failed; exception={Safe(exception.GetType().Name)}; canonicalRetained=true; tag={StatusTag}");
        }
    }

    private static void LogRefreshIfMaterial(
        CacheEntry entry,
        VanguardCanonicalMedicalEffectSnapshot previous,
        VanguardCanonicalMedicalEffectSnapshot current,
        string reason)
    {
        string signature = current.MaterialSignature;
        bool changed = !string.Equals(previous.MaterialSignature, signature, StringComparison.Ordinal);
        bool forced = reason.IndexOf("force", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("selection", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("no_effect", StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("preempt", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!changed && !forced && current.ScanComplete) return;
        if (!changed && string.Equals(entry.LastLoggedSignature, signature + "|" + reason, StringComparison.Ordinal)) return;
        entry.LastLoggedSignature = signature + "|" + reason;
        VanguardClientDiagnosticsLog.Operational(RefreshTag, () =>
            $"botProfile={Safe(current.BotProfileId)}; revision={current.Revision}; reason={Safe(reason)}; source={Safe(current.Source)}; complete={Bool(current.ScanComplete)}; truncated={Bool(current.ScanTruncated)}; budgetExceeded={Bool(current.BudgetExceeded)}; examined={current.ExaminedEffectCount}; badges={Join(current.Badges)}; targets={JoinTargets(current.TargetByBadge)}; changed={Bool(changed)}; canonicalScans={entry.CanonicalScans}; materialRevisionChanges={entry.MaterialRevisionChanges}; unchangedRefreshes={entry.UnchangedRefreshes}; selectionOverlays={entry.SelectionOverlays}; selectionDeferrals={entry.SelectionDeferrals}; priorityPreemptions={entry.PriorityPreemptions}; sharedHudDecisionTruth=true; convergenceTag={ConvergenceStatusTag}; tag={StatusTag}");
    }

    public static void RecordSelectionOverlay(string botProfileId, long canonicalRevision, string summary)
    {
        RecordConvergenceEvent(botProfileId, canonicalRevision, "selection_overlay", summary, entry => entry.SelectionOverlays++);
    }

    public static void RecordSelectionDeferral(string botProfileId, long canonicalRevision, string summary)
    {
        RecordConvergenceEvent(botProfileId, canonicalRevision, "selection_deferral", summary, entry => entry.SelectionDeferrals++);
    }

    public static void RecordPriorityPreemption(string botProfileId, long canonicalRevision, string summary)
    {
        RecordConvergenceEvent(botProfileId, canonicalRevision, "priority_preemption", summary, entry => entry.PriorityPreemptions++);
    }

    private static void RecordConvergenceEvent(
        string botProfileId,
        long canonicalRevision,
        string eventKind,
        string summary,
        Action<CacheEntry> mutation)
    {
        CacheEntry entry;
        lock (Sync)
        {
            if (!CacheByBotProfile.TryGetValue(botProfileId, out CacheEntry? cachedEntry))
            {
                cachedEntry = new CacheEntry();
                CacheByBotProfile[botProfileId] = cachedEntry;
            }
            entry = cachedEntry;
            mutation(entry);
        }

        VanguardClientDiagnosticsLog.Operational(ConvergenceTag, () =>
            $"botProfile={Safe(botProfileId)}; event={Safe(eventKind)}; canonicalRevision={canonicalRevision}; canonicalScans={entry.CanonicalScans}; materialRevisionChanges={entry.MaterialRevisionChanges}; unchangedRefreshes={entry.UnchangedRefreshes}; selectionOverlays={entry.SelectionOverlays}; selectionDeferrals={entry.SelectionDeferrals}; priorityPreemptions={entry.PriorityPreemptions}; summary={Safe(summary)}; tag={ConvergenceStatusTag}");
    }

    private static string BuildRevisionSignature(
        IReadOnlyList<string> badges,
        IReadOnlyDictionary<string, string> targets,
        bool controllerObserved,
        bool scanComplete,
        bool truncated,
        bool budgetExceeded)
        => string.Join(",", badges)
            + "|" + string.Join(",", targets.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value))
            + "|controller=" + Bool(controllerObserved)
            + "|complete=" + Bool(scanComplete)
            + "|truncated=" + Bool(truncated)
            + "|budget=" + Bool(budgetExceeded);

    private static object? UnwrapCollectionItem(object? raw)
    {
        if (raw == null) return null;
        Type type = raw.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            return ReadMember(raw, "Value") ?? raw;
        }
        return raw;
    }

    private static string BuildRichSignature(object effect)
    {
        string runtimeType = effect.GetType().FullName ?? effect.GetType().Name;
        string declaredType = ReadMember(effect, "EffectType")?.ToString()
            ?? ReadMember(effect, "Type")?.ToString()
            ?? string.Empty;
        string diagnostic = SimplifyDiagnosticValue(effect);
        string textual;
        try { textual = effect.ToString() ?? string.Empty; }
        catch { textual = string.Empty; }
        return runtimeType + " " + declaredType + " " + diagnostic + " " + textual;
    }

    private static string SimplifyDiagnosticValue(object value)
    {
        try
        {
            if (value is float f) return f.ToString("0.##", CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString("0.##", CultureInfo.InvariantCulture);
            object? current = ReadMember(value, "Current") ?? ReadMember(value, "Value") ?? ReadMember(value, "CurrentStrength");
            object? maximum = ReadMember(value, "Maximum") ?? ReadMember(value, "Max") ?? ReadMember(value, "Strength");
            return current != null && maximum != null ? current + "/" + maximum : value.GetType().Name;
        }
        catch { return "unknown"; }
    }

    private static string? ResolveMedicalBadge(string signature)
    {
        if (signature.Contains("Encumbered", StringComparison.OrdinalIgnoreCase)
            || signature.Contains("MedEffect", StringComparison.OrdinalIgnoreCase)
            || signature.Contains("LowEdgeHealth", StringComparison.OrdinalIgnoreCase)) return null;
        if (signature.Contains("HeavyBleeding", StringComparison.OrdinalIgnoreCase) || signature.Contains("HeavyBleed", StringComparison.OrdinalIgnoreCase)) return "HB";
        if (signature.Contains("LightBleeding", StringComparison.OrdinalIgnoreCase) || signature.Contains("LightBleed", StringComparison.OrdinalIgnoreCase)) return "LB";
        if (signature.Contains("Fracture", StringComparison.OrdinalIgnoreCase) || signature.Contains("BrokenBone", StringComparison.OrdinalIgnoreCase)) return "FR";
        if (signature.Contains("Pain", StringComparison.OrdinalIgnoreCase)) return "PN";
        if (signature.Contains("Tremor", StringComparison.OrdinalIgnoreCase)) return "TR";
        return null;
    }

    private static object? ReadMember(object? instance, string name)
    {
        if (instance == null) return null;
        Type type = instance.GetType();
        string key = (type.AssemblyQualifiedName ?? type.FullName ?? type.Name) + "|" + name;
        MemberInfo? member;
        lock (Sync)
        {
            if (!MemberCache.TryGetValue(key, out member))
            {
                member = ResolveMember(type, name);
                MemberCache[key] = member;
            }
        }
        try
        {
            return member switch
            {
                PropertyInfo property => property.GetValue(instance),
                FieldInfo field => field.GetValue(instance),
                _ => VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(instance, name)
            };
        }
        catch { return null; }
    }

    private static MemberInfo? ResolveMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (Type? cursor = type; cursor != null; cursor = cursor.BaseType)
        {
            PropertyInfo? property = cursor.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            if (property != null) return property;
            FieldInfo? field = cursor.GetField(name, flags | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }

    private static void LogBudgetExceeded(string botProfileId, DateTimeOffset now, int examined, double elapsedMs)
    {
        lock (Sync)
        {
            if (LastBudgetLogAt.TryGetValue(botProfileId, out DateTimeOffset last) && now - last < BudgetLogInterval) return;
            LastBudgetLogAt[botProfileId] = now;
        }
        VanguardClientDiagnosticsLog.Warning(StatusTag,
            $"VANGUARD_MEDICAL_CANONICAL_BUDGET_EXCEEDED botProfile={Safe(botProfileId)}; elapsedMs={elapsedMs:0.00}; budgetMs={EnumerationBudgetMilliseconds:0.00}; examined={examined}; previousMerged=true; criticalBodyPartTruth=direct; tag={StatusTag}");
    }

    private static double ElapsedMilliseconds(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
    private static string SafePart(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_');
    private static string Trim(string value, int max) => value.Length <= max ? value : value.Substring(0, max);
    private static string Join(IEnumerable<string> values)
    {
        string[] array = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Safe).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return array.Length == 0 ? "none" : string.Join(",", array);
    }
    private static string JoinTargets(IReadOnlyDictionary<string, string> values)
        => values.Count == 0 ? "none" : string.Join(",", values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => Safe(pair.Key) + "=" + Safe(pair.Value)));
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
}

internal sealed record VanguardCanonicalMedicalEffectObservation(
    string Badge,
    string BodyPart,
    string Signature,
    Type RuntimeEffectType,
    Type? DeclaredEffectType,
    string DeclaredEffectTypeName)
{
    public string IdentityKey => Badge + "|" + BodyPart + "|" + Signature;
}

internal sealed class VanguardCanonicalMedicalEffectSnapshot
{
    public static VanguardCanonicalMedicalEffectSnapshot Empty { get; } = new();
    public string BotProfileId { get; init; } = "none";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public long Revision { get; init; }
    public string RefreshReason { get; init; } = "none";
    public string Source { get; init; } = "none";
    public bool ControllerObserved { get; init; }
    public bool ScanComplete { get; init; }
    public bool ScanTruncated { get; init; }
    public bool BudgetExceeded { get; init; }
    public int ExaminedEffectCount { get; init; }
    public VanguardCanonicalMedicalEffectObservation[] Observations { get; init; } = Array.Empty<VanguardCanonicalMedicalEffectObservation>();
    public string[] Badges { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> TargetByBadge { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string[] RawSignatures { get; init; } = Array.Empty<string>();
    public string MaterialSignature => string.Join(",", Badges) + "|" + string.Join(",", TargetByBadge.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value));
    public string RevisionSignature => MaterialSignature
        + "|controller=" + (ControllerObserved ? "true" : "false")
        + "|complete=" + (ScanComplete ? "true" : "false")
        + "|truncated=" + (ScanTruncated ? "true" : "false")
        + "|budget=" + (BudgetExceeded ? "true" : "false");
}
#endif

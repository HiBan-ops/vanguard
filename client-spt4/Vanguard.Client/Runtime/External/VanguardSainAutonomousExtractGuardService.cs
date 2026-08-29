#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vanguard.Client;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement;

// Responsibility: Stops SAIN from autonomously extracting Vanguard Operators before Vanguard has issued its own explicit extraction intent.
// Flow: Known Operator SAIN components are resolved and hardened; extraction permissions/layer/time checks are intercepted and vetoed only for Operators without Vanguard extraction authority, with bounded retries and logging.
// Authority boundary: SAIN keeps PMC combat behavior, but Vanguard owns Operator squad/extraction policy; ordinary non-Vanguard bots remain under native SAIN extraction rules.
// Invariant: The guard must fail narrowly: it may veto autonomous Operator extraction, but must not disable SAIN combat or change extraction behavior for unrelated bots.
namespace Vanguard.Client.Runtime.External;

/// <summary>
/// Vanguard Operators keep SAIN's PMC combat model, but their raid ownership and exfiltration
/// policy belong to Vanguard. Until Vanguard publishes an explicit exfil intent, SAIN's generic
/// PMC time/injury/loot extract layer is denied for Operators only.
/// </summary>
internal static class VanguardSainAutonomousExtractGuardService
{
    public const string StatusTag = VanguardAuthorityCircuitBreakerStatusTags.SainAutonomousExtractVeto;
    public const string AuthorityClassificationStatusTag = VanguardAuthorityCircuitBreakerStatusTags.SainExtractAuthorityClassification;

    private static readonly TimeSpan MissingComponentRetry = TimeSpan.FromSeconds(5.0d);
    private static readonly TimeSpan VetoLogInterval = TimeSpan.FromSeconds(10.0d);
    private static readonly Dictionary<string, object> SainComponentByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<object, string> ProfileIdBySainComponent = new(ReferenceObjectComparer.Instance);
    private static readonly Dictionary<string, DateTimeOffset> RetryAfterByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HardenedBotProfiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PermissionVetoLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LayerVetoLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExtractTimeVetoLogged = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetForRaidLifecycle(string reason)
    {
        SainComponentByBotProfileId.Clear();
        ProfileIdBySainComponent.Clear();
        RetryAfterByBotProfileId.Clear();
        LastLogAtByKey.Clear();
        HardenedBotProfiles.Clear();
        PermissionVetoLogged.Clear();
        LayerVetoLogged.Clear();
        ExtractTimeVetoLogged.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_SAIN_AUTONOMOUS_EXTRACT_GUARD_RESET reason={Safe(reason)}; operatorsOnly=true; keepsPmcCombatProfile=true; autonomousExtract=false; explicitVanguardExfilNotImplemented=true; tag={StatusTag}");
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        // The runtime resolves and hardens at most one previously unseen Operator per scheduler tick.
        // Active extract-like state bypasses this admission budget and is cleared immediately.
        int firstHardeningBudget = 1;
        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || !snapshot.Alive || string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            string key = Normalize(snapshot.BotProfileId);
            bool extractLike = VanguardMovementAuthorityDoctrine.IsSainExtractLike(snapshot);
            bool alreadyHardened = HardenedBotProfiles.Contains(key);
            if (alreadyHardened && !extractLike)
            {
                continue;
            }

            if (!alreadyHardened && !extractLike)
            {
                if (firstHardeningBudget <= 0)
                {
                    continue;
                }
                firstHardeningBudget--;
            }

            if (!TryResolveSainComponent(key, now, out var sainComponent))
            {
                continue;
            }

            string source = "runtime_guard" + (extractLike ? "_active_extract" : "_proactive_one_shot");
            if (!TryApplyVeto(sainComponent, source, now, out _, out var summary))
            {
                continue;
            }

            HardenedBotProfiles.Add(key);
            if (extractLike)
            {
                LogThrottled("active_extract|" + key, now,
                    $"VANGUARD_SAIN_AUTONOMOUS_EXTRACT_ACTIVE_STATE_CLEARED operator={Safe(snapshot.OperatorId)}; botProfile={Safe(snapshot.BotProfileId)}; brainLayer={Safe(snapshot.Brain.ActiveLayer)}; brainNode={Safe(snapshot.Brain.Node)}; sainLayer={Safe(snapshot.Sain.ActiveLayer)}; sainAction={Safe(snapshot.Sain.CurrentAction)}; {summary}; nextAuthority=vanguard_scheduler; moverStopForced=false; combatProfilePreserved=true; Tag={VanguardCombatTruthStatusTags.ExtractGuardOneShot}; tag={StatusTag}; authorityTag={AuthorityClassificationStatusTag}");
            }
        }
    }


    /// <summary>
    /// Harmony hot-path gate. Profile reflection is cached by SAIN component instance and the
    /// authoritative runtime registry is consulted on every call, so a component observed before
    /// its Vanguard bind is denied as soon as registration completes without caching a false result.
    /// </summary>
    public static bool ShouldDenyAutonomousExtract(object? source, out string botProfileId)
    {
        botProfileId = ResolveProfileIdFromAny(source);
        return source != null
            && !string.IsNullOrWhiteSpace(botProfileId)
            && (VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out _)
                || VanguardRaidOperatorRuntimeRegistry.IsExpectedOperatorBotProfileId(botProfileId));
    }

    public static void RecordPermissionVeto(object? source, string botProfileId, string gate, DateTimeOffset now)
    {
        string profile = Normalize(botProfileId);
        if (string.Equals(profile, "none", StringComparison.OrdinalIgnoreCase) || !PermissionVetoLogged.Add(profile + "|" + Safe(gate)))
        {
            return;
        }

        TryResolveSainComponentFromAny(source, profile, now, out var component);
        if (component != null)
        {
            TryApplyVeto(component, "permission_prefix:" + gate, now, out _, out _);
        }

        VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.SainLayerVeto,
            $"VANGUARD_SAIN_EXTRACT_PERMISSION_PREFIX_INVOKED botProfile={Safe(botProfileId)}; gate={Safe(gate)}; denied=true; operatorsOnly=true; tag={VanguardRuntimeConvergenceStatusTags.SainLayerVeto}");
    }

    public static bool TryVetoLayerIsActive(object? layerInstance, string gate, ref bool result)
    {
        try
        {
            if (!ShouldDenyAutonomousExtract(layerInstance, out var botProfileId))
            {
                return true;
            }

            result = false;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TryInvokeCheckActiveChangedFalse(layerInstance);
            if (TryResolveSainComponentFromAny(layerInstance, botProfileId, now, out var component) && component != null)
            {
                TryApplyVeto(component, "layer_prefix:" + gate, now, out _, out _);
            }

            string logKey = Normalize(botProfileId) + "|" + Safe(gate);
            if (LayerVetoLogged.Add(logKey))
            {
                VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.SainLayerVeto,
                    $"VANGUARD_SAIN_EXTRACT_LAYER_DENIED botProfile={Safe(botProfileId)}; gate={Safe(gate)}; result=false; activeLayerCleared=true; reentryPrevented=true; pmcCombatProfilePreserved=true; tag={VanguardRuntimeConvergenceStatusTags.SainLayerVeto}");
            }
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardRuntimeConvergenceStatusTags.SainLayerVeto,
                $"VANGUARD_SAIN_EXTRACT_LAYER_VETO_EXCEPTION gate={Safe(gate)}; type={exception.GetType().Name}; message={Safe(exception.Message)}; failOpen=true; tag={VanguardRuntimeConvergenceStatusTags.SainLayerVeto}");
            return true;
        }
    }


    public static bool TryVetoNativeExfiltrationLayer(object? layerInstance, string gate, ref bool result)
    {
        try
        {
            if (!ShouldDenyAutonomousExtract(layerInstance, out var botProfileId))
            {
                return true;
            }

            result = false;
            string key = Normalize(botProfileId) + "|native|" + Safe(gate);
            bool firstDenial = LayerVetoLogged.Add(key);
            bool nativeLeaveReset = firstDenial && TryResetNativeLeaveState(layerInstance);
            if (firstDenial)
            {
                VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.SainLayerVeto,
                    $"VANGUARD_NATIVE_EXFIL_LAYER_DENIED botProfile={Safe(botProfileId)}; gate={Safe(gate)}; layer=Exfiltration; decision=goToExfiltrationPointNode_prevented; result=false; nativeLeaveReset={(nativeLeaveReset ? "true" : "false")}; resetOneShot=true; perTickMovementMutation=false; operatorsOnly=true; globalFallbackRequired=false; pmcCombatProfilePreserved=true; tag={VanguardRuntimeConvergenceStatusTags.SainLayerVeto}");
            }
            return false;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardRuntimeConvergenceStatusTags.SainLayerVeto,
                $"VANGUARD_NATIVE_EXFIL_LAYER_VETO_EXCEPTION gate={Safe(gate)}; type={exception.GetType().Name}; message={Safe(exception.Message)}; failOpen=true; globalDisableFallbackPermitted=true; tag={VanguardRuntimeConvergenceStatusTags.SainLayerVeto}");
            return true;
        }
    }

    public static bool TryVetoExtractTimeRefresh(object? infoInstance, string gate, DateTimeOffset now)
    {
        if (!ShouldDenyAutonomousExtract(infoInstance, out var botProfileId))
        {
            return false;
        }

        TrySetMember(infoInstance, -1f, "PercentageBeforeExtract");
        if (TryResolveSainComponentFromAny(infoInstance, botProfileId, now, out var component) && component != null)
        {
            TryApplyVeto(component, "extract_time_prefix:" + gate, now, out _, out _);
        }

        string logKey = Normalize(botProfileId) + "|" + Safe(gate);
        if (ExtractTimeVetoLogged.Add(logKey))
        {
            VanguardClientDiagnosticsLog.Info(VanguardRuntimeConvergenceStatusTags.SainExtractTimeVeto,
                $"VANGUARD_SAIN_EXTRACT_TIME_REFRESH_DENIED botProfile={Safe(botProfileId)}; gate={Safe(gate)}; percentageBeforeExtract=-1; squadPropagationBlocked=true; pmcCombatProfilePreserved=true; tag={VanguardRuntimeConvergenceStatusTags.SainExtractTimeVeto}");
        }
        return true;
    }

    public static bool TryApplyVeto(object? sainBotComponent, string source, DateTimeOffset now, out string botProfileId, out string summary)
    {
        botProfileId = ResolveProfileIdCached(sainBotComponent);
        if (sainBotComponent == null || string.IsNullOrWhiteSpace(botProfileId))
        {
            summary = "veto=false;reason=sain_component_or_profile_missing";
            return false;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime))
        {
            summary = "veto=false;reason=not_vanguard_operator;botProfile=" + Safe(botProfileId);
            return false;
        }

        var mutations = new List<string>(8);
        object? info = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBotComponent, "Info");
        if (TrySetMember(info, -1f, "PercentageBeforeExtract"))
        {
            mutations.Add("percentageBeforeExtract=-1");
        }

        object? memory = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sainBotComponent, "Memory");
        object? extract = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(memory, "Extract");
        if (extract != null)
        {
            if (TrySetMember(extract, null, "ExfilPoint"))
            {
                mutations.Add("exfilPoint=null");
            }
            if (TrySetMember(extract, null, "ExfilPosition"))
            {
                mutations.Add("exfilPosition=null");
            }
            if (TryResetEnumMember(extract, "ExtractReason"))
            {
                mutations.Add("extractReason=default");
            }
            if (TryResetEnumMember(extract, "ExtractStatus"))
            {
                mutations.Add("extractStatus=default");
            }
        }

        summary = "veto=true;operator=" + Safe(runtime.OperatorId)
            + ";botProfile=" + Safe(botProfileId)
            + ";source=" + Safe(source)
            + ";mutations=" + (mutations.Count == 0 ? "none_required_or_reflection_unavailable" : string.Join(",", mutations))
            + ";autonomousExtractAllowed=false;futureVanguardExfilOwnsPolicy=true";
        string summaryForLog = summary;
        LogThrottled("veto|" + Normalize(botProfileId) + "|" + Safe(source), now,
            () => $"VANGUARD_SAIN_AUTONOMOUS_EXTRACT_VETO {summaryForLog}; tag={StatusTag}");
        return true;
    }

    private static bool TryResolveSainComponent(string botProfileId, DateTimeOffset now, out object component)
    {
        if (SainComponentByBotProfileId.TryGetValue(botProfileId, out var cached) && cached != null)
        {
            component = cached;
            return true;
        }

        if (RetryAfterByBotProfileId.TryGetValue(botProfileId, out var retryAt) && retryAt > now)
        {
            component = null!;
            return false;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime) || runtime.BotOwner == null)
        {
            RetryAfterByBotProfileId[botProfileId] = now + MissingComponentRetry;
            component = null!;
            return false;
        }

        object? resolved = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(runtime.BotOwner, "SAIN.Components.BotComponent");
        if (resolved == null)
        {
            RetryAfterByBotProfileId[botProfileId] = now + MissingComponentRetry;
            component = null!;
            return false;
        }

        SainComponentByBotProfileId[botProfileId] = resolved;
        RetryAfterByBotProfileId.Remove(botProfileId);
        component = resolved;
        return true;
    }

    private static string ResolveProfileIdCached(object? sainBotComponent)
    {
        return ResolveProfileIdFromAny(sainBotComponent);
    }

    private static string ResolveProfileIdFromAny(object? source)
    {
        return ResolveProfileIdFromAny(source, new HashSet<object>(ReferenceObjectComparer.Instance), 0);
    }

    private static string ResolveProfileIdFromAny(object? source, HashSet<object> visited, int depth)
    {
        if (source == null || depth > 5 || !visited.Add(source))
        {
            return string.Empty;
        }

        if (ProfileIdBySainComponent.TryGetValue(source, out var cached))
        {
            return cached;
        }

        string direct = Text(GetPropertyOrFieldDeep(source, "ProfileId", "ProfileID"));
        object? botOwner = GetPropertyOrFieldDeep(source, "BotOwner", "BotOwner_0");
        if (string.IsNullOrWhiteSpace(direct) && botOwner != null)
        {
            object? profile = GetPropertyOrFieldDeep(botOwner, "Profile");
            direct = Text(GetPropertyOrFieldDeep(profile, "Id", "ProfileId", "ProfileID"));
        }

        object? bot = GetPropertyOrFieldDeep(source, "Bot");
        if (string.IsNullOrWhiteSpace(direct) && bot != null)
        {
            direct = ResolveProfileIdFromAny(bot, visited, depth + 1);
        }

        object? info = GetPropertyOrFieldDeep(source, "Info");
        if (string.IsNullOrWhiteSpace(direct) && info != null)
        {
            direct = ResolveProfileIdFromAny(info, visited, depth + 1);
        }

        object? sain = GetPropertyOrFieldDeep(source, "SAIN", "BotComponent", "Component");
        if (string.IsNullOrWhiteSpace(direct) && sain != null)
        {
            direct = ResolveProfileIdFromAny(sain, visited, depth + 1);
        }

        if (!string.IsNullOrWhiteSpace(direct))
        {
            foreach (object observed in visited)
            {
                ProfileIdBySainComponent[observed] = direct;
            }
        }
        return direct;
    }

    private static bool TryResolveSainComponentFromAny(object? source, string botProfileId, DateTimeOffset now, out object? component)
    {
        component = null;
        if (source != null)
        {
            string typeName = source.GetType().FullName ?? string.Empty;
            if (typeName.IndexOf("BotComponent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                component = source;
                return true;
            }

            object? bot = GetPropertyOrFieldDeep(source, "Bot");
            if (bot != null)
            {
                component = bot;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(botProfileId) && TryResolveSainComponent(Normalize(botProfileId), now, out var resolved))
        {
            component = resolved;
            return true;
        }
        return false;
    }

    private static bool TryResetNativeLeaveState(object? layerInstance)
    {
        try
        {
            object? botOwner = GetPropertyOrFieldDeep(layerInstance, "BotOwner", "BotOwner_0");
            object? exfiltration = GetPropertyOrFieldDeep(botOwner, "Exfiltration");
            MethodInfo? reset = exfiltration?.GetType().GetMethod(
                "ResetLeaveTime",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (reset == null)
            {
                return false;
            }
            reset.Invoke(exfiltration, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryInvokeCheckActiveChangedFalse(object? layerInstance)
    {
        if (layerInstance == null)
        {
            return;
        }

        try
        {
            MethodInfo? method = layerInstance.GetType().GetMethod(
                "CheckActiveChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy,
                binder: null,
                types: new[] { typeof(bool) },
                modifiers: null);
            method?.Invoke(layerInstance, new object[] { false });
        }
        catch
        {
            // IsActive=false is authoritative. This call only clears SAIN's debug/active-layer side state.
        }
    }

    private static object? GetPropertyOrFieldDeep(object? instance, params string[] names)
    {
        if (instance == null)
        {
            return null;
        }

        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo? property = type.GetProperty(name, flags);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        return property.GetValue(instance);
                    }

                    FieldInfo? field = type.GetField(name, flags);
                    if (field != null)
                    {
                        return field.GetValue(instance);
                    }
                }
                catch
                {
                    // Continue through alternative names/base types. Exact layer veto remains fail-open
                    // only when the Operator identity cannot be proven.
                }
            }
        }

        return null;
    }

    private static bool TrySetMember(object? instance, object? value, params string[] names)
    {
        if (instance == null)
        {
            return false;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = instance.GetType();
        foreach (string name in names)
        {
            try
            {
                PropertyInfo? property = type.GetProperty(name, flags);
                if (property?.CanWrite == true)
                {
                    property.SetValue(instance, ConvertValue(value, property.PropertyType));
                    return true;
                }

                FieldInfo? field = type.GetField(name, flags);
                if (field != null)
                {
                    field.SetValue(instance, ConvertValue(value, field.FieldType));
                    return true;
                }
            }
            catch
            {
                // Integration drift is non-fatal. The Harmony veto remains authoritative.
            }
        }

        return false;
    }

    private static bool TryResetEnumMember(object? instance, string name)
    {
        if (instance == null)
        {
            return false;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = instance.GetType();
        try
        {
            PropertyInfo? property = type.GetProperty(name, flags);
            if (property?.CanWrite == true && property.PropertyType.IsEnum)
            {
                property.SetValue(instance, Enum.ToObject(property.PropertyType, 0));
                return true;
            }

            FieldInfo? field = type.GetField(name, flags);
            if (field != null && field.FieldType.IsEnum)
            {
                field.SetValue(instance, Enum.ToObject(field.FieldType, 0));
                return true;
            }
        }
        catch
        {
            // Optional cleanup only; veto is still enforced before SAIN layer activation.
        }

        return false;
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value == null)
        {
            return null;
        }

        Type effective = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effective.IsInstanceOfType(value))
        {
            return value;
        }

        return Convert.ChangeType(value, effective, CultureInfo.InvariantCulture);
    }

    private static void LogThrottled(string key, DateTimeOffset now, Func<string> messageFactory)
    {
        if (!VanguardClientDiagnosticsLog.IsEnabled(VanguardAuditLevel.Diagnostic))
        {
            return;
        }

        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < VetoLogInterval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Diagnostic(StatusTag, messageFactory);
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < VetoLogInterval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private sealed class ReferenceObjectComparer : IEqualityComparer<object>
    {
        public static ReferenceObjectComparer Instance { get; } = new();
        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? string.Empty;
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

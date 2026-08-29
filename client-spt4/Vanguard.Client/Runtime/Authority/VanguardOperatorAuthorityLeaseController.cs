#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;

// Responsibility: Coordinates the short-lived authority leases that prevent multiple Vanguard domains from driving the same Operator action at once.
// Flow: Callers request/refresh/release named leases against current raid time; the controller exposes ownership and expiry so schedulers can arbitrate without hidden static locks.
// Authority boundary: The controller owns lease bookkeeping only; each medical, movement, loot, combat or authored domain still owns the semantic decision behind its request.
// Invariant: Leases are bounded and recoverable, expired ownership cannot block newer work, and raid reset removes every outstanding claim.
namespace Vanguard.Client.Runtime.Authority;

internal static class VanguardOperatorAuthorityLeaseController
{
    public const string StatusTag = "VANGUARD_MEDICAL_AUTHORITY_LEASE_OK";
    public const string HardOrbitExitStatusTag = "VANGUARD_MEDICAL_HARD_ORBIT_EXIT_OK";
    public const string ExternalAuthorityAdapterStatusTag = VanguardExternalAuthorityAdapter.StatusTag;
    public const string ExternalMovementPreemptStatusTag = VanguardExternalAuthorityAdapter.MovementPreemptStatusTag;
    public const string CombatAwareGateStatusTag = VanguardExternalAuthorityAdapter.CombatAwareGateStatusTag;
    public const string OrbitLayerQuiesceStatusTag = VanguardExternalAuthorityAdapter.OrbitLayerQuiesceStatusTag;
    public const string CoverArrivalGrantStatusTag = VanguardExternalAuthorityAdapter.CoverArrivalGrantStatusTag;

    private static readonly TimeSpan DefaultMedicalTtl = TimeSpan.FromSeconds(42.00d);
    private static readonly TimeSpan SuppressionInterval = TimeSpan.FromSeconds(0.45d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.25d);
    private static readonly Dictionary<string, AuthorityState> StateByBotProfileId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByKey = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        StateByBotProfileId.Clear();
        LastLogAtByKey.Clear();
        VanguardExternalAuthorityAdapter.Reset(reason);
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_AUTHORITY_RESET reason={Safe(reason)}; authority=Vanguard; orbitIsTool=true; medicalBeatsOrbit=true; goCoverOnly=true; combatAwareGate=true; orbitLayerIdleQuiesce=true; coverArrivalGrant=true; externalAdapterTag={ExternalAuthorityAdapterStatusTag}; externalMovementTag={ExternalMovementPreemptStatusTag}; combatGateTag={CombatAwareGateStatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}; coverArrivalTag={CoverArrivalGrantStatusTag}; hardOrbitExitTag={HardOrbitExitStatusTag}; tag={StatusTag}");
    }

    public static string StartOrRefreshMedical(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason)
    {
        if (lease == null || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return "authority=missing_lease";
        }

        string key = Normalize(lease.BotProfileId);
        if (!StateByBotProfileId.TryGetValue(key, out var state)
            || state.ExpiresAtUtc <= now
            || !string.Equals(Normalize(state.TargetPart), Normalize(lease.TargetPart), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Normalize(state.ItemTemplateId), Normalize(lease.ItemTemplateId), StringComparison.OrdinalIgnoreCase))
        {
            state = new AuthorityState
            {
                OperatorId = lease.OperatorId,
                BotProfileId = lease.BotProfileId,
                Owner = "VanguardMedical",
                Reason = reason,
                TargetPart = lease.TargetPart,
                ItemTemplateId = lease.ItemTemplateId,
                StartedAtUtc = now,
                ExpiresAtUtc = now + DefaultMedicalTtl,
                LastSuppressionAtUtc = DateTimeOffset.MinValue
            };
            StateByBotProfileId[key] = state;
            LogThrottled(key + "|start", now, $"VANGUARD_AUTHORITY_LEASE_STARTED operator={Safe(snapshot.OperatorId)}; botProfile={key}; owner=VanguardMedical; reason={Safe(reason)}; target={Safe(lease.TargetPart)}; item={Safe(lease.ItemName)}; ttl={DefaultMedicalTtl.TotalSeconds:0.00}; orbitPermission=SuspendedByMedical; lootingPermission=SuspendedByMedical; goCoverOnly=true; hardOrbitExitTag={HardOrbitExitStatusTag}; tag={StatusTag}");
        }
        else
        {
            state.ExpiresAtUtc = now + DefaultMedicalTtl;
        }

        string suppression = "suppression=not_due";
        VanguardExternalPreemptOutcome? suppressionOutcome = null;
        if (botOwner != null && now - state.LastSuppressionAtUtc >= SuppressionInterval)
        {
            state.LastSuppressionAtUtc = now;
            long suppressStarted = VanguardRuntimePerformanceGuard.Begin();
            var suppressionResult = SuppressExternalSystems(botOwner, snapshot, now, state.Reason);
            VanguardRuntimePerformanceGuard.End("MedicalAuthoritySuppressExternal", suppressStarted);
            suppressionOutcome = suppressionResult.Outcome;
            suppression = suppressionResult.CompactSummary;
            bool combatDeferred = suppressionResult.Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner;
            bool granted = suppressionResult.Outcome == VanguardExternalPreemptOutcome.Granted;
            bool pending = suppressionResult.Outcome == VanguardExternalPreemptOutcome.Pending;
            bool failedOrbit = suppressionResult.Outcome == VanguardExternalPreemptOutcome.FailedOrbitStillActive;
            bool failedLoot = suppressionResult.Outcome == VanguardExternalPreemptOutcome.FailedLootingBotsStillActive;
            string logName = combatDeferred
                ? "VANGUARD_AUTHORITY_MEDICAL_DEFERRED_BY_COMBAT"
                : granted
                    ? "VANGUARD_AUTHORITY_EXTERNAL_GRANTED"
                    : pending
                        ? "VANGUARD_AUTHORITY_EXTERNAL_PENDING"
                        : failedOrbit
                            ? "VANGUARD_AUTHORITY_EXTERNAL_BLOCKED_ORBIT"
                            : failedLoot
                                ? "VANGUARD_AUTHORITY_EXTERNAL_BLOCKED_LOOT"
                                : "VANGUARD_AUTHORITY_EXTERNAL_FAILED";
            string permission = combatDeferred
                ? "NoMedicalMovementGrant"
                : granted
                    ? "SuspendedByMedical"
                    : "AwaitingExternalQuiesce";
            // Runtime invariant: do not run a second reflection-heavy DescribeActivity pass solely for a
            // throttled diagnostic. The typed preempt result already contains the authoritative
            // before/after ownership and blocking reason required to audit the grant.
            LogThrottled(key + "|suppress|" + logName, now, $"{logName} operator={Safe(snapshot.OperatorId)}; botProfile={key}; owner=VanguardMedical; reason={Safe(state.Reason)}; {suppression}; orbitPermission={permission}; goCoverOnly=true; externalAdapterTag={ExternalAuthorityAdapterStatusTag}; combatGateTag={CombatAwareGateStatusTag}; orbitLayerTag={OrbitLayerQuiesceStatusTag}; coverArrivalTag={CoverArrivalGrantStatusTag}; hardOrbitExitTag={HardOrbitExitStatusTag}; tag={StatusTag}");
        }

        string requestedPermission = suppressionOutcome == VanguardExternalPreemptOutcome.Granted
            ? "Granted"
            : suppressionOutcome == VanguardExternalPreemptOutcome.RejectedCombatOwner
                ? "DeferredByCombatOwner"
                : suppressionOutcome.HasValue && suppressionOutcome.Value != VanguardExternalPreemptOutcome.Pending
                    ? "AwaitingExternalQuiesce"
                    : "Requested";
        return "authority=VanguardMedical;externalPermission=" + requestedPermission + ";orbit=MedicalPreemptRequested;loot=MedicalPreemptRequested;expiresIn=" + Math.Max(0.0d, (state.ExpiresAtUtc - now).TotalSeconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ";" + suppression;
    }

    public static bool HasActiveMedicalAuthority(string? botProfileId, string? targetPart, string? itemTemplateId, DateTimeOffset now, out string summary)
    {
        string key = Normalize(botProfileId);
        summary = "authority=none";
        if (!StateByBotProfileId.TryGetValue(key, out var state) || state.ExpiresAtUtc <= now)
        {
            return false;
        }

        if (!string.Equals(Normalize(state.TargetPart), Normalize(targetPart), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Normalize(state.ItemTemplateId), Normalize(itemTemplateId), StringComparison.OrdinalIgnoreCase))
        {
            summary = "authority=stale_target";
            return false;
        }

        summary = "authority=VanguardMedical;orbit=SuspendedByMedical;loot=SuspendedByMedical;owner=" + Safe(state.Owner) + ";reason=" + Safe(state.Reason);
        return true;
    }

    public static void ReleaseMedical(VanguardExecutionLeaseState lease, BotOwner? botOwner, DateTimeOffset now, string reason)
    {
        if (lease == null)
        {
            return;
        }

        string key = Normalize(lease.BotProfileId);
        if (!StateByBotProfileId.Remove(key))
        {
            return;
        }

        string release = ReleaseExternalSystems(botOwner, lease.BotProfileId, now, reason);
        LogThrottled(key + "|release|" + Safe(reason), now, $"VANGUARD_AUTHORITY_LEASE_RELEASED botProfile={key}; owner=VanguardMedical; reason={Safe(reason)}; {release}; nextOwner=VanguardSchedulerDecision; orbitPermission=MayResumeIfSchedulerAllows; goCoverOnly=true; externalAdapterTag={ExternalAuthorityAdapterStatusTag}; hardOrbitExitTag={HardOrbitExitStatusTag}; tag={StatusTag}");
    }

    private static VanguardExternalPreemptResult SuppressExternalSystems(BotOwner botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason)
    {
        return VanguardExternalAuthorityAdapter.RequestMedicalPreempt(botOwner, snapshot, "authority_lease:" + Safe(reason), TimeSpan.FromSeconds(10.00d), now);
    }

    private static string ReleaseExternalSystems(BotOwner? botOwner, string? botProfileId, DateTimeOffset now, string reason)
    {
        return VanguardExternalAuthorityAdapter.ReleaseMedicalPreempt(botOwner, botProfileId, now, "authority_release:" + Safe(reason));
    }

    private static void LogThrottled(string key, DateTimeOffset now, string message)
    {
        if (LastLogAtByKey.TryGetValue(key, out var last) && now - last < LogInterval)
        {
            return;
        }

        LastLogAtByKey[key] = now;
        VanguardClientDiagnosticsLog.Info(StatusTag, message);
    }

    private static bool TryInvoke(object? instance, string methodName, params object?[] args)
    {
        if (instance == null)
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (var method in instance.GetType().GetMethods(flags))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.GetParameters().Length != args.Length)
                {
                    continue;
                }

                method.Invoke(instance, args);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TrySetPropertyOrField(object? instance, string name, object? value)
    {
        if (instance == null)
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = instance.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(instance, value);
                return true;
            }

            var field = type.GetField(name, flags);
            if (field != null)
            {
                field.SetValue(instance, value);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(';', '_').Replace('\n', '_').Replace('\r', '_');
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Float(float? value) => value.HasValue ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "none";

    private sealed class AuthorityState
    {
        public string OperatorId = "none";
        public string BotProfileId = "none";
        public string Owner = "VanguardMedical";
        public string Reason = "none";
        public string TargetPart = "none";
        public string ItemTemplateId = "none";
        public DateTimeOffset StartedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
        public DateTimeOffset LastSuppressionAtUtc;
    }
}
#endif

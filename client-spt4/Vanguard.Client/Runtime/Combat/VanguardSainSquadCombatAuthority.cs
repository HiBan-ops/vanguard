#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Determines when SAIN may keep primary combat control versus when Vanguard cohesion/return safety is allowed to reclaim movement authority.
// Flow: Current direct threat, target validity, engagement progress, distance/cohesion and SAIN state are evaluated into an explicit combat-authority window consumed by movement/awareness arbitration.
// Authority boundary: SAIN remains combat executor while its authority window is valid; Vanguard only suppresses/reclaims the bounded cases required by squad safety contracts.
// Invariant: True direct threats are never dropped merely for distance, stale/non-progressing pursuit cannot hold authority indefinitely, and authority state expires with its evidence.
namespace Vanguard.Client.Runtime.Combat;

/// <summary>
/// Read-only bridge between SAIN's native squad decision and Vanguard's existing combat authority route.
/// It never creates or mutates SAIN SquadInfo/decisions. The short cache only protects the BigBrain
/// movement-layer boundary from a one-frame scheduler race.
/// </summary>
internal static class VanguardSainSquadCombatAuthority
{
    public const string StatusTag = "VANGUARD_NATIVE_SAIN_SQUAD_FOUNDATION_STATUS";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2.0d);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, SquadAuthorityState> StatesByBotProfileId = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LayerYieldGrant> LayerYieldGrantsByBotProfileId = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ExecutableSquadDecisions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Regroup",
        "Suppress",
        "Search",
        "GroupSearch",
        "Help",
        "PushSuppressedEnemy",
        "Surround",
        "Retreat",
        "BoundingRetreat",
        "SpreadOut",
        "HoldPositions",
    };

    public static void ResetForRaidLifecycle(string reason)
    {
        int cleared;
        lock (Sync)
        {
            cleared = StatesByBotProfileId.Count + LayerYieldGrantsByBotProfileId.Count;
            StatesByBotProfileId.Clear();
            LayerYieldGrantsByBotProfileId.Clear();
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_SAIN_SQUAD_AUTHORITY_RESET reason={Safe(reason)}; cleared={cleared}; cacheSeconds={CacheLifetime.TotalSeconds:0.0}; tag={StatusTag}");
    }

    public static bool IsSnapshotAuthority(VanguardSainDecisionSnapshot sain, out string reason)
    {
        reason = "none";
        if (sain == null || !sain.ComponentPresent)
        {
            reason = "sain_component_missing";
            return false;
        }

        if (sain.NativeGroupMemberCount < 2
            || sain.SainSquadMemberCount < 2
            || IsNone(sain.SainSquadGuid))
        {
            reason = "sain_squad_not_multi_member_resolved";
            return false;
        }

        if (!IsNone(sain.SelfDecision))
        {
            reason = "sain_self_decision_precedes_squad";
            return false;
        }

        if (string.Equals(NormalizeDecision(sain.CombatDecision), "DogFight", StringComparison.OrdinalIgnoreCase))
        {
            reason = "sain_dogfight_precedes_squad";
            return false;
        }

        string squadDecision = NormalizeDecision(sain.SquadDecision);
        if (IsNone(squadDecision) || !ExecutableSquadDecisions.Contains(squadDecision))
        {
            reason = "sain_squad_decision_none_or_unsupported:" + Safe(squadDecision);
            return false;
        }

        reason = "sain_squad_decision_active:" + squadDecision;
        return true;
    }

    public static void Observe(string botProfileId, VanguardSainDecisionSnapshot sain, DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(botProfileId) || sain == null)
        {
            return;
        }

        bool active = IsSnapshotAuthority(sain, out var authorityReason);
        bool multiMemberResolved = sain.NativeGroupMemberCount >= 2
            && sain.SainSquadMemberCount >= 2
            && !IsNone(sain.SainSquadGuid);
        var current = new SquadAuthorityState(
            observedAtUtc,
            active,
            NormalizeDecision(sain.SquadDecision),
            authorityReason,
            sain.NativeGroupId,
            sain.NativeGroupMemberCount,
            sain.SainSquadGuid,
            sain.SainSquadMemberCount,
            sain.SainSquadLeaderId,
            sain.SainSquadReady && multiMemberResolved,
            sain.SainSquadReady);

        SquadAuthorityState? previous;
        lock (Sync)
        {
            StatesByBotProfileId.TryGetValue(botProfileId, out previous);
            StatesByBotProfileId[botProfileId] = current;
            if (!current.Active
                || (previous != null && !string.Equals(previous.Decision, current.Decision, StringComparison.OrdinalIgnoreCase)))
            {
                LayerYieldGrantsByBotProfileId.Remove(botProfileId);
            }
        }

        bool structuralChanged = previous == null
            || !string.Equals(previous.NativeGroupId, current.NativeGroupId, StringComparison.Ordinal)
            || previous.NativeMemberCount != current.NativeMemberCount
            || !string.Equals(previous.SainSquadGuid, current.SainSquadGuid, StringComparison.Ordinal)
            || previous.SainMemberCount != current.SainMemberCount
            || !string.Equals(previous.LeaderId, current.LeaderId, StringComparison.Ordinal)
            || previous.SainReadyRaw != current.SainReadyRaw;
        if (structuralChanged)
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_SAIN_SQUAD_RESOLVED botProfile={botProfileId}; nativeGroup={Safe(current.NativeGroupId)}; nativeMembers={current.NativeMemberCount}; sainSquad={Safe(current.SainSquadGuid)}; sainMembers={current.SainMemberCount}; leader={Safe(current.LeaderId)}; ready={current.Ready}; sainReadyRaw={current.SainReadyRaw}; tag={StatusTag}");
        }

        if (previous == null || previous.Ready != current.Ready)
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_SAIN_SQUAD_{(current.Ready ? "READY" : "DEGRADED")} botProfile={botProfileId}; nativeMembers={current.NativeMemberCount}; sainMembers={current.SainMemberCount}; sainSquad={Safe(current.SainSquadGuid)}; leader={Safe(current.LeaderId)}; sainReadyRaw={current.SainReadyRaw}; singleOperatorNeutral={current.NativeMemberCount < 2}; tag={StatusTag}");
        }

        bool decisionChanged = previous == null
            || previous.Active != current.Active
            || !string.Equals(previous.Decision, current.Decision, StringComparison.OrdinalIgnoreCase);
        if (decisionChanged && (current.Active || previous?.Active == true))
        {
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"VANGUARD_SAIN_SQUAD_DECISION_{(current.Active ? "STARTED" : "ENDED")} botProfile={botProfileId}; decision={Safe(current.Active ? current.Decision : previous?.Decision ?? "none")}; reason={Safe(current.AuthorityReason)}; nativeMembers={current.NativeMemberCount}; sainMembers={current.SainMemberCount}; tag={StatusTag}");
        }
    }

    public static void GrantLayerYield(
        string botProfileId,
        VanguardSainDecisionSnapshot sain,
        DateTimeOffset grantedAtUtc,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(botProfileId)
            || !IsSnapshotAuthority(sain, out _))
        {
            return;
        }

        var grant = new LayerYieldGrant(
            grantedAtUtc,
            NormalizeDecision(sain.SquadDecision),
            reason);
        lock (Sync)
        {
            LayerYieldGrantsByBotProfileId[botProfileId] = grant;
        }
    }

    public static bool TryGetCachedAuthority(
        string botProfileId,
        DateTimeOffset now,
        out string squadDecision,
        out string reason)
    {
        squadDecision = "none";
        reason = "none";
        if (string.IsNullOrWhiteSpace(botProfileId))
        {
            return false;
        }

        LayerYieldGrant? grant;
        lock (Sync)
        {
            if (!LayerYieldGrantsByBotProfileId.TryGetValue(botProfileId, out grant) || grant == null)
            {
                return false;
            }
        }

        if (now - grant.GrantedAtUtc > CacheLifetime)
        {
            lock (Sync)
            {
                if (LayerYieldGrantsByBotProfileId.TryGetValue(botProfileId, out var current)
                    && ReferenceEquals(current, grant))
                {
                    LayerYieldGrantsByBotProfileId.Remove(botProfileId);
                }
            }
            return false;
        }

        squadDecision = grant.Decision;
        reason = grant.Reason;
        return true;
    }

    public static void ReportMovementYield(string botProfileId, string squadDecision, string clearResult, string reason)
    {
        if (string.Equals(clearResult, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_SAIN_SQUAD_AUTHORITY_GRANTED botProfile={Safe(botProfileId)}; decision={Safe(squadDecision)}; movementCommand={Safe(clearResult)}; reason={Safe(reason)}; bigBrainVanguardLayerYield=true; existingCombatAuthorityRouteReused=true; tag={StatusTag}");
    }

    private static string NormalizeDecision(string value)
    {
        string normalized = Safe(value);
        int separator = normalized.LastIndexOf('.');
        return separator >= 0 && separator + 1 < normalized.Length
            ? normalized.Substring(separator + 1)
            : normalized;
    }

    private static bool IsNone(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase);
    }

    private static string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    }

    private sealed class LayerYieldGrant
    {
        public LayerYieldGrant(DateTimeOffset grantedAtUtc, string decision, string reason)
        {
            GrantedAtUtc = grantedAtUtc;
            Decision = decision;
            Reason = reason;
        }

        public DateTimeOffset GrantedAtUtc { get; }
        public string Decision { get; }
        public string Reason { get; }
    }

    private sealed class SquadAuthorityState
    {
        public SquadAuthorityState(
            DateTimeOffset observedAtUtc,
            bool active,
            string decision,
            string authorityReason,
            string nativeGroupId,
            int nativeMemberCount,
            string sainSquadGuid,
            int sainMemberCount,
            string leaderId,
            bool ready,
            bool sainReadyRaw)
        {
            ObservedAtUtc = observedAtUtc;
            Active = active;
            Decision = decision;
            AuthorityReason = authorityReason;
            NativeGroupId = nativeGroupId;
            NativeMemberCount = nativeMemberCount;
            SainSquadGuid = sainSquadGuid;
            SainMemberCount = sainMemberCount;
            LeaderId = leaderId;
            Ready = ready;
            SainReadyRaw = sainReadyRaw;
        }

        public DateTimeOffset ObservedAtUtc { get; }
        public bool Active { get; }
        public string Decision { get; }
        public string AuthorityReason { get; }
        public string NativeGroupId { get; }
        public int NativeMemberCount { get; }
        public string SainSquadGuid { get; }
        public int SainMemberCount { get; }
        public string LeaderId { get; }
        public bool Ready { get; }
        public bool SainReadyRaw { get; }
    }
}
#endif

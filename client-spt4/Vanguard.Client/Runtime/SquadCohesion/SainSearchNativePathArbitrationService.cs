#if SPT_CLIENT
using System;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;

// Responsibility: native Search-only admission / interception / immediate context release.
// Flow: SwitchSearchModes prefix supplies an exact threat and same-frame proof; SAIN
// RunToPointByWay/WalkToPointByWay receive an eligible alternate NavMeshPath only if enabled.
// Authority boundary: native SAINMover remains the sole path writer, including native replacement of existing paths.
// No veto, movement Stop, lease, aim, gun, steering or target mutation exists in this service.
// Invariant: executive mutation is hard-disabled until positive route/handoff and combat non-regression runtime evidence.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class SainSearchNativePathArbitrationService
{
    internal const string StatusTag = "VANGUARD_TC_SAIN_SEARCH_NATIVE_HANDOFF";
    // Route substitution stays disabled until native route safety, authority restoration,
    // and combat non-regression have been established by primary runtime evidence.
    internal static readonly bool ExecutiveEnabled = false;

    [ThreadStatic] private static SearchWindow? _window;
    private const float MinimumReceiptInterval = 2.0f;
    private const int MaximumReceiptsPerBot = 60;
    private static readonly ConditionalWeakTable<BotOwner, ReceiptBudget> ReceiptBudgets = new();

    internal static void BeginSearchWindow(object? search, object? enemy)
    {
        _window = null;
        if (search == null || enemy == null || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }
        try
        {
            object? botValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(search, "BotOwner")
                ?? VanguardOperatorRuntimeAuditReflection.GetDeep(search, "Bot", "BotOwner");
            if (botValue is not BotOwner bot || bot.IsDead || string.IsNullOrWhiteSpace(bot.ProfileId)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(bot.ProfileId,
                    out VanguardRaidOperatorRuntimeRecord runtime)
                || !ReferenceEquals(runtime.BotOwner, bot)
                || !MarksmanForwardRoleDisciplinePolicy.IsDesignatedMarksmanIdentity(
                    runtime.TacticalRole, runtime.TacticalSpecialty))
            {
                return;
            }
            string enemyId = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "EnemyProfileId") as string
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(enemyId))
            {
                return;
            }
            NavMeshPath? nativeEnemyPath = VanguardOperatorRuntimeAuditReflection.GetDeep(enemy,
                "Path", "PathToEnemy") as NavMeshPath;
            if (nativeEnemyPath == null || nativeEnemyPath.status != NavMeshPathStatus.PathComplete
                || nativeEnemyPath.corners.Length < 2)
            {
                return;
            }
            // Keep a read-only receipt window EVEN WITHOUT A POSITIVE CANDIDATE, so that
            // Search->native Walk/Run reachability is no longer inferred from missing logs.
            NavMeshPath? alternative = null;
            string admissionReason = "no_same_frame_standoff_candidate";
            if (VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(enemy, "IsVisible") is not bool nativeVisible
                || nativeVisible)
            {
                admissionReason = "native_visible_or_unknown";
            }
            else if (!SainSearchIntentCoordinationService.TryGetCurrentStandoffCandidate(search, enemyId,
                    out alternative) || alternative == null)
            {
                admissionReason = SainSearchIntentCoordinationService.GetCurrentAdmissionStatus(search);
            }
            else if (!VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(bot.ProfileId,
                    out OperatorDecisionSnapshot snapshot)
                || !SafeSearchSnapshot(snapshot, runtime.OwnerProfileId, enemyId))
            {
                alternative = null;
                admissionReason = "native_safety_recheck_failed";
            }
            else if (alternative.status != NavMeshPathStatus.PathComplete || alternative.corners.Length < 2)
            {
                alternative = null;
                admissionReason = "alternative_route_invalid";
            }
            else
            {
                Vector3[] nativeCorners = nativeEnemyPath.corners;
                Vector3[] alternativeCorners = alternative.corners;
                if (!Finite(nativeCorners[nativeCorners.Length - 1])
                    || !Finite(alternativeCorners[alternativeCorners.Length - 1]))
                {
                    alternative = null;
                    admissionReason = "nonfinite_native_or_alternative";
                }
                else
                {
                    admissionReason = "shadow_candidate_ready";
                }
            }
            SainSearchIntentCoordinationService.GetCurrentProbeTiming(search,
                out bool probeSampled, out float probeAgeSeconds);
            _window = new SearchWindow(bot, enemy, runtime.OwnerProfileId, runtime.BotProfileId,
                enemyId, nativeEnemyPath, alternative, Time.frameCount, admissionReason,
                probeSampled, probeAgeSeconds);
        }
        catch (Exception exception)
        {
            _window = null;
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                "BEGIN_FAILED;reason=" + exception.GetType().Name + ";nativePreserved=true");
        }
    }

    // Called at the native SAIN route-admission boundary. The method keeps running normally.
    // forceFreshPath prevents SAIN TryUpdatePath from retaining a now-superseded aggressive route.
    internal static void ReviewNativeSearchPath(object? mover, ref NavMeshPath path,
        ref bool forceFreshPath, string nativeMethod)
    {
        SearchWindow? window = _window;
        if (window == null || mover == null || path == null || window.Frame != Time.frameCount)
        {
            return;
        }
        // A nested or unrelated mover must never be attributed to this Marksman Search window.
        object? liveBot = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(mover, "BotOwner")
            ?? VanguardOperatorRuntimeAuditReflection.GetDeep(mover, "Bot", "BotOwner");
        if (!ReferenceEquals(liveBot, window.Bot))
        {
            return;
        }
        window.NativeByWayCalls++;
        if (string.Equals(nativeMethod, "WalkToPointByWay", StringComparison.Ordinal)) window.WalkByWayCalls++;
        else if (string.Equals(nativeMethod, "RunToPointByWay", StringComparison.Ordinal)) window.RunByWayCalls++;
        if (!ReferenceEquals(window.NativeEnemyPath, path))
        {
            window.OtherPathCalls++;
            return;
        }
        window.ExactNativePathCalls++;
        if (window.Alternative == null)
        {
            return;
        }
        try
        {
            if (window.Bot.IsDead
                || VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(window.NativeEnemy,
                    "IsVisible") is not bool nativeVisible || nativeVisible
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(window.BotProfileId,
                    out VanguardRaidOperatorRuntimeRecord runtime)
                || !ReferenceEquals(runtime.BotOwner, window.Bot)
                || !string.Equals(runtime.OwnerProfileId, window.OwnerProfileId, StringComparison.OrdinalIgnoreCase)
                || !MarksmanForwardRoleDisciplinePolicy.IsDesignatedMarksmanIdentity(
                    runtime.TacticalRole, runtime.TacticalSpecialty)
                || !VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(window.BotProfileId,
                    out OperatorDecisionSnapshot current)
                || !SafeSearchSnapshot(current, window.OwnerProfileId, window.EnemyId)
                || window.Alternative == null
                || window.Alternative.status != NavMeshPathStatus.PathComplete
                || window.Alternative.corners.Length < 2)
            {
                return; // Death, target flip, new action or survival authority: SAIN owns everything.
            }
            if (!window.Reported)
            {
                window.Reported = true;
                VanguardClientDiagnosticsLog.Info(StatusTag,
                    "SEARCH_NATIVE_ROUTE_ADMISSION method=" + nativeMethod
                    + ";bot=" + window.BotProfileId + ";enemy=" + window.EnemyId
                    + ";proof=positive;searchExact=true;alternativeComplete=true"
                    + ";executiveEnabled=" + (ExecutiveEnabled ? "true" : "false")
                    + ";nativeMoverIsSoleWriter=true;survivalYield=true");
            }
            if (!ExecutiveEnabled)
            {
                return; // Native SAIN retains sole ownership of the movement path.
            }
            path = window.Alternative!;
            forceFreshPath = true; // caller sets checkSameWay=false, preventing reuse of old aggressive path.
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                "ADMISSION_FAILED;reason=" + exception.GetType().Name + ";nativePreserved=true");
        }
    }

    internal static void EndSearchWindow()
    {
        SearchWindow? window = _window;
        _window = null;
        if (window == null || !VanguardFikaCompat.IsRaidAuthority)
        {
            return;
        }
        try
        {
            ReceiptBudget budget = ReceiptBudgets.GetValue(window.Bot, _ => new ReceiptBudget());
            float now = Time.time;
            if (now < budget.NextAt || budget.Count >= MaximumReceiptsPerBot)
            {
                return;
            }
            budget.NextAt = now + MinimumReceiptInterval;
            budget.Count++;
            VanguardClientDiagnosticsLog.Info(StatusTag,
                "SEARCH_WINDOW_RECEIPT bot=" + Clean(window.BotProfileId)
                + ";enemy=" + Clean(window.EnemyId)
                + ";frame=" + window.Frame
                + ";nativeByWayCalls=" + window.NativeByWayCalls
                + ";walkByWayCalls=" + window.WalkByWayCalls
                + ";runByWayCalls=" + window.RunByWayCalls
                + ";exactNativePathCalls=" + window.ExactNativePathCalls
                + ";otherPathCalls=" + window.OtherPathCalls
                + ";admission=" + Clean(window.AdmissionReason)
                + ";probeSampledCurrentInvocation=" + (window.ProbeSampled ? "true" : "false")
                + ";probeAgeSeconds=" + (float.IsNaN(window.ProbeAgeSeconds) || float.IsInfinity(window.ProbeAgeSeconds)
                    ? "unknown" : window.ProbeAgeSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                + ";candidate=" + (window.Alternative != null ? "true" : "false")
                + ";executiveEnabled=false;mutation=false;nativeSainPreserved=true");
        }
        catch
        {
            // Receipts are disposable observations: fail open, never delay a Search finalizer.
        }
    }

    private static string Clean(string? token) => string.IsNullOrWhiteSpace(token) ? "none"
        : token.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\n', '_').Replace('\r', '_');

    private static bool SafeSearchSnapshot(OperatorDecisionSnapshot snapshot, string ownerId, string enemyId)
    {
        if (snapshot == null || !snapshot.Alive
            || !string.Equals(snapshot.OwnerProfileId, ownerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.Threat.EnemyId, enemyId, StringComparison.OrdinalIgnoreCase)
            || !VanguardOperatorDecisionSnapshotService.IsFresh(snapshot, DateTimeOffset.UtcNow, out _)
            || snapshot.Sain.Active != true || snapshot.Sain.LayersActive != true
            || !string.Equals(snapshot.Sain.CurrentAction, "SearchAction", StringComparison.OrdinalIgnoreCase)
            || snapshot.Sain.RunningToCover != false || snapshot.Threat.EnemyVisible != false
            || snapshot.Threat.ShotMeRecently != false || snapshot.Threat.ShotAtMeRecently != false
            || snapshot.GrenadeHazard.HasRelevantHazard
            || SainSearchIntentCoordinationService.BlocksSearchForNoncombatPrimary(snapshot)
            || snapshot.Medical.Actionability.AnyMedicineUsing || snapshot.Medical.Actionability.GrenadeThrowing
            || snapshot.Medical.Actionability.Reloading || snapshot.MovementAuthority.HardOutsideBubble
            || !snapshot.SquadCohesion.OwnerKnown || !snapshot.SquadCohesion.OwnerReliableForActiveMovement
            || !snapshot.SquadCohesion.OwnerPosition.HasValue
            || !Finite(snapshot.SquadCohesion.OwnerPosition.Value)
            || (snapshot.ThreatScan.CandidatePosition.HasValue
                && !string.IsNullOrWhiteSpace(snapshot.ThreatScan.CandidateThreatId)
                && !string.Equals(snapshot.ThreatScan.CandidateThreatId, "none", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(snapshot.ThreatScan.CandidateThreatId, enemyId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        return true;
    }

    private static bool Finite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
        && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);

    private sealed class SearchWindow
    {
        internal readonly BotOwner Bot;
        internal readonly object NativeEnemy;
        internal readonly string OwnerProfileId;
        internal readonly string BotProfileId;
        internal readonly string EnemyId;
        internal readonly NavMeshPath NativeEnemyPath;
        internal readonly NavMeshPath? Alternative;
        internal readonly int Frame;
        internal readonly string AdmissionReason;
        internal readonly bool ProbeSampled;
        internal readonly float ProbeAgeSeconds;
        internal int NativeByWayCalls;
        internal int WalkByWayCalls;
        internal int RunByWayCalls;
        internal int ExactNativePathCalls;
        internal int OtherPathCalls;
        internal bool Reported;

        internal SearchWindow(BotOwner bot, object nativeEnemy, string owner, string botProfile, string enemy,
            NavMeshPath nativePath, NavMeshPath? alternative, int frame, string admissionReason,
            bool probeSampled, float probeAgeSeconds)
        {
            Bot = bot;
            NativeEnemy = nativeEnemy;
            OwnerProfileId = owner;
            BotProfileId = botProfile;
            EnemyId = enemy;
            NativeEnemyPath = nativePath;
            Alternative = alternative;
            Frame = frame;
            AdmissionReason = admissionReason;
            ProbeSampled = probeSampled;
            ProbeAgeSeconds = probeAgeSeconds;
        }
    }

    private sealed class ReceiptBudget
    {
        internal float NextAt;
        internal int Count;
    }
}
#endif

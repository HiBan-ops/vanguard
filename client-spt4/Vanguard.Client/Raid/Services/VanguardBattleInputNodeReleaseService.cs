using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using HarmonyLib;
using UnityEngine;
#endif

// Responsibility: Removes the proven stale EFT ProfileLoadingScreen input blocker that can survive an Off-Raid Operator inventory transition into BattleUI.
// Flow: The inventory-exit marker arms the service; actual BattleUI.Show, the native CurrentScreen.OnScreenChanged(BattleUI) event, or an exact CurrentScreen fallback starts a short, lightweight exact-BattleUI watch that closes/detaches only an active vanilla ProfileLoadingScreen still requesting ShowCursor.
// Authority boundary: EFT remains cursor/input authority; Vanguard never writes Cursor.visible, Cursor.lockState, InputManager cursor fields, ItemUiContext state, chat state, or third-party input nodes.
// Invariant: The vanilla raid path is untouched, loading/InputTree owners are never polled before effective BattleUI truth, runtime probes are cached/bounded, and destructive repair requires the exact stale node proven by runtime evidence.
namespace Vanguard.Client.Raid.Services;

/// <summary>
/// Repairs the narrow input leak observed after direct Operator inventory use.
///
/// Runtime evidence on SPT 4.0.13/Fika showed the failed transition with both
/// RootScreenType and CurrentScreenController on BattleUI while an active
/// EFT.UI.ProfileLoadingScreen remained under UIInputRoot and returned ShowCursor.
/// Because InputNode cursor results are max-aggregated, that stale node wins over
/// BattleUI's LockCursor and also keeps its input blocking behavior in the tree.
///
/// The repair therefore targets that one demonstrated ownership violation rather
/// than taking ownership of EFT's global cursor state. It is armed only by the
/// Off-Raid Operator inventory exit marker, starts only after an actual BattleUI
/// lifecycle signal (BattleUI.Show, native OnScreenChanged(BattleUI), or the exact CurrentScreen fallback),
/// requires exact BattleUI, and watches briefly for a late/re-added stale loading
/// node. Legitimate inventory/chat/Fika UI is never classified as a repair target.
/// </summary>
internal static class VanguardBattleInputNodeReleaseService
{
#if SPT_CLIENT
    private static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan BattleUiSettleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BattleUiFallbackGateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinimumExactBattleUiObservation = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StableCleanWindow = TimeSpan.FromSeconds(2);
    private const int MaxCorrectivePasses = 4;
    private const int MaxInputRootResolveMisses = 4;

    private static bool pending;
    private static bool battleUiShown;
    private static bool completed;
    private static DateTimeOffset requestedAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset battleUiShownAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastBattleUiFallbackGateUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastAttemptUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset cleanSinceUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastCorrectionAtUtc = DateTimeOffset.MinValue;
    private static int attempts;
    private static int correctivePasses;
    private static int inputRootResolveMisses;
    private static int lastObservedRemainingStale;
    private static bool observedProvenStale;
    private static bool battleUiFallbackUsed;
    private static bool battleUiPrimaryCallbackSeen;
    private static bool battleUiScreenChangedEventSeen;
    private static bool battleUiScreenChangedEventBattleUiSeen;
    private static int currentScreenProbeCount;
    private static int currentScreenSingletonResolvedCount;
    private static int currentScreenChangedEventCount;
    private static int currentScreenEventSubscriptionSuccesses;
    private static int currentScreenEventSubscriptionFailures;
    private static int currentScreenEventSubscriptionSwitches;
    private static bool rootBattleUiSeen;
    private static bool currentBattleUiSeen;
    private static bool exactBattleUiSeen;
    private static string lastScreenObservation = "<none>";
    private static string lastScreenChangedEvent = "<none>";
    private static string lastScreenEventSubscriptionError = "<none>";
    private static string pendingSource = "<none>";
    private static string pendingReason = "<none>";
    private static string battleUiActivationSource = "<none>";

    // UIInputRoot is application-lifetime state. Cache it after the first successful
    // post-BattleUI resolve so the watchdog never performs scene-wide FindObjectOfType
    // work on every probe. Unity's destroyed-object semantics are checked before reuse.
    private static object? cachedUiInputRoot;
    private static Type? cachedTarkovApplicationType;
    private static MethodInfo? cachedTarkovApplicationExistMethod;
    private static FieldInfo? cachedTarkovApplicationUiInputRootField;
    private static bool tarkovApplicationUiInputRootAccessorResolved;
    private static Type? cachedCurrentScreenSingletonType;
    private static PropertyInfo? cachedCurrentScreenSingletonInstanceProperty;
    private static FieldInfo? cachedCurrentScreenSingletonInstanceField;
    private static bool currentScreenSingletonAccessorResolved;
    private static object? currentScreenEventSubscribedInstance;
    private static EventInfo? currentScreenChangedEventInfo;
    private static Delegate? currentScreenChangedEventHandler;
    private static FieldInfo? cachedChildrenField;
    private static bool childrenFieldResolved;
    private static readonly Dictionary<Type, MethodInfo?> ShouldLockCursorMethodCache = new Dictionary<Type, MethodInfo?>();
    private static readonly Dictionary<Type, MethodInfo?> CloseMethodCache = new Dictionary<Type, MethodInfo?>();

    public static void RequestForRaidStart(string source, string reason)
    {
        UnsubscribeCurrentScreenChangedEvent();
        pending = true;
        completed = false;
        battleUiShown = false;
        battleUiFallbackUsed = false;
        battleUiPrimaryCallbackSeen = false;
        battleUiScreenChangedEventSeen = false;
        battleUiScreenChangedEventBattleUiSeen = false;
        currentScreenProbeCount = 0;
        currentScreenSingletonResolvedCount = 0;
        currentScreenChangedEventCount = 0;
        currentScreenEventSubscriptionSuccesses = 0;
        currentScreenEventSubscriptionFailures = 0;
        currentScreenEventSubscriptionSwitches = 0;
        rootBattleUiSeen = false;
        currentBattleUiSeen = false;
        exactBattleUiSeen = false;
        lastScreenObservation = "<none>";
        lastScreenChangedEvent = "<none>";
        lastScreenEventSubscriptionError = "<none>";
        requestedAtUtc = DateTimeOffset.UtcNow;
        battleUiShownAtUtc = DateTimeOffset.MinValue;
        lastBattleUiFallbackGateUtc = DateTimeOffset.MinValue;
        lastAttemptUtc = DateTimeOffset.MinValue;
        cleanSinceUtc = DateTimeOffset.MinValue;
        lastCorrectionAtUtc = DateTimeOffset.MinValue;
        attempts = 0;
        correctivePasses = 0;
        inputRootResolveMisses = 0;
        lastObservedRemainingStale = 0;
        observedProvenStale = false;
        pendingSource = source;
        pendingReason = Sanitize(reason);
        battleUiActivationSource = "<none>";
        EnsureCurrentScreenChangedSubscription();

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_input_tree_release_pending source={source}; reason={pendingReason}; pendingWindowSeconds={PendingWindow.TotalSeconds:0}; gate=actual_battle_ui_show_or_native_screen_changed_battle_ui_or_exact_current_screen_then_profile_loading_repair; fallbackGateMs={BattleUiFallbackGateInterval.TotalMilliseconds:0}; settleDelayMs={BattleUiSettleDelay.TotalMilliseconds:0}; retryMs={RetryInterval.TotalMilliseconds:0}; minimumObservationSeconds={MinimumExactBattleUiObservation.TotalSeconds:0}; stableCleanSeconds={StableCleanWindow.TotalSeconds:0}; maxCorrectivePasses={MaxCorrectivePasses}; screenObservation=native_event_plus_bounded_instance_refresh_no_scene_scan; loadingPolicy=no_input_tree_polling_before_effective_battle_ui; repairTarget=active_assembly_csharp_EFT.UI.ProfileLoadingScreen_returning_ShowCursor; cursorPolicy=no_global_cursor_or_input_manager_writes");
    }

    public static void NotifyBattleUiShown(string source)
    {
        if (!pending || completed)
        {
            return;
        }

        if (string.Equals(source, "eft_battle_ui_show_presented", StringComparison.Ordinal))
        {
            battleUiPrimaryCallbackSeen = true;
        }
        else if (string.Equals(source, "current_screen_changed_battle_ui_event", StringComparison.Ordinal))
        {
            battleUiScreenChangedEventBattleUiSeen = true;
        }

        if (battleUiShown)
        {
            return;
        }

        battleUiShownAtUtc = DateTimeOffset.UtcNow;
        battleUiShown = true;
        battleUiActivationSource = Sanitize(source);
        battleUiFallbackUsed = string.Equals(source, "exact_battle_ui_current_screen_fallback", StringComparison.Ordinal);
        cleanSinceUtc = DateTimeOffset.MinValue;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_input_tree_release_battle_ui_shown source={source}; pendingSource={pendingSource}; waitedSeconds={(battleUiShownAtUtc - requestedAtUtc).TotalSeconds:0}; fallbackUsed={battleUiFallbackUsed}; settleDelayMs={BattleUiSettleDelay.TotalMilliseconds:0}; action=armed_for_lightweight_exact_battle_ui_profile_loading_watch");
    }

    public static void Tick()
    {
        if (!pending || completed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!battleUiShown)
        {
            if (now - requestedAtUtc > PendingWindow)
            {
                AbortPending(
                    "battle_input_tree_release_expired_before_battle_ui",
                    $"waitedSeconds={(now - requestedAtUtc).TotalSeconds:0}; action=aborted_without_input_tree_polling",
                    warning: true);
                return;
            }

            // Stay completely passive with respect to InputTree/loading owners until
            // BattleUI is actually presented. The lower BattleUI.Show patch remains the
            // primary signal; the native OnScreenChanged event is an independent passive
            // signal, and this throttled CurrentScreen check is the final lifecycle fallback.
            // None of these pre-gates scans InputTree or authorizes repair by itself.
            if (lastBattleUiFallbackGateUtc == DateTimeOffset.MinValue
                || now - lastBattleUiFallbackGateUtc >= BattleUiFallbackGateInterval)
            {
                lastBattleUiFallbackGateUtc = now;
                EnsureCurrentScreenChangedSubscription();
                CurrentScreenObservation observation = ObserveCurrentScreen();
                if (observation.ExactBattleUi)
                {
                    NotifyBattleUiShown("exact_battle_ui_current_screen_fallback");
                }
            }

            return;
        }

        if (now - requestedAtUtc > PendingWindow)
        {
            AbortPending(
                "battle_input_tree_release_expired_after_battle_ui",
                $"waitedSeconds={(now - requestedAtUtc).TotalSeconds:0}; lastRemainingStale={lastObservedRemainingStale}; action=aborted_pending_window",
                warning: lastObservedRemainingStale > 0);
            return;
        }

        if (lastAttemptUtc != DateTimeOffset.MinValue && now - lastAttemptUtc < RetryInterval)
        {
            return;
        }

        TryRun(now, "battle_ui_tick_deferred");
    }

    private static void TryRun(DateTimeOffset now, string source)
    {
        lastAttemptUtc = now;

        if (!IsPlayableBattleUiReady(out string readiness))
        {
            // A legitimate queued screen (inventory, etc.) suspends the clean timer.
            // It never becomes a deletion candidate merely because BattleUI is root.
            cleanSinceUtc = DateTimeOffset.MinValue;
            return;
        }

        object? inputRoot = ResolveUiInputRoot();
        if (inputRoot == null)
        {
            inputRootResolveMisses++;
            cleanSinceUtc = DateTimeOffset.MinValue;
            if (inputRootResolveMisses >= MaxInputRootResolveMisses)
            {
                AbortPending(
                    "battle_input_tree_release_input_root_unavailable",
                    $"attempts={attempts}; inputRootResolveMisses={inputRootResolveMisses}; readiness={readiness}; action=aborted_without_cursor_mutation",
                    warning: true);
            }
            return;
        }

        inputRootResolveMisses = 0;
        attempts++;
        string releaseSource = source + ":profile_loading_targeted";

        InputNodeEntry[] entries = EnumerateInputNodeTree(inputRoot);
        InputNodeEntry[] candidates = entries
            .Where(entry => entry.Parent != null && IsProvenStaleProfileLoadingCursorBlocker(entry.Node))
            .ToArray();

        if (candidates.Length > 0)
        {
            observedProvenStale = true;
            cleanSinceUtc = DateTimeOffset.MinValue;

            if (correctivePasses >= MaxCorrectivePasses)
            {
                lastObservedRemainingStale = candidates.Length;
                AbortPending(
                    "operator_inventory_input_release_correction_budget_exhausted",
                    $"attempts={attempts}; correctivePasses={correctivePasses}; remainingStale={candidates.Length}; readiness={readiness}; inputRoot={DescribeInputRootDetailed(inputRoot)}; action=aborted_without_global_cursor_write",
                    warning: true);
                return;
            }

            correctivePasses++;
            InputNodeReleaseResult releaseResult = ReleaseProvenProfileLoadingNodes(inputRoot, candidates, releaseSource);
            lastCorrectionAtUtc = now;
            lastObservedRemainingStale = releaseResult.RemainingBlockers;

            if (releaseResult.RemainingBlockers > 0)
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                    $"operator_inventory_input_release_still_blocked source={releaseSource}; attempts={attempts}; correctivePasses={correctivePasses}/{MaxCorrectivePasses}; {releaseResult.Describe()}; readiness={readiness}; action=retry_targeted_profile_loading_release");
                return;
            }

            cleanSinceUtc = now;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"operator_inventory_input_release_corrected source={releaseSource}; attempts={attempts}; correctivePasses={correctivePasses}/{MaxCorrectivePasses}; {releaseResult.Describe()}; readiness={readiness}; nextAction=bounded_reappearance_watch");
            return;
        }

        lastObservedRemainingStale = 0;
        if (cleanSinceUtc == DateTimeOffset.MinValue)
        {
            cleanSinceUtc = now;
        }

        TimeSpan elapsedAfterBattleUi = now - battleUiShownAtUtc;
        TimeSpan cleanFor = now - cleanSinceUtc;
        TimeSpan cleanAfterLastCorrection = lastCorrectionAtUtc == DateTimeOffset.MinValue
            ? cleanFor
            : now - lastCorrectionAtUtc;

        if (elapsedAfterBattleUi >= MinimumExactBattleUiObservation
            && cleanFor >= StableCleanWindow
            && cleanAfterLastCorrection >= StableCleanWindow)
        {
            CompletePending(
                releaseSource,
                $"attempts={attempts}; correctivePasses={correctivePasses}; observedProvenStale={observedProvenStale}; elapsedAfterBattleUiSeconds={elapsedAfterBattleUi.TotalSeconds:0.0}; cleanForSeconds={cleanFor.TotalSeconds:0.0}; readiness={readiness}; finalInputRoot={DescribeInputRootSummary(inputRoot)}; finalItemUiContext={DescribeItemUiContextSummary()}; cursorPolicy=eft_authority_no_global_cursor_or_input_manager_write");
        }
    }

    private static InputNodeReleaseResult ReleaseProvenProfileLoadingNodes(object inputRoot, InputNodeEntry[] candidates, string source)
    {
        if (!CurrentScreenIsExactBattleUi())
        {
            return InputNodeReleaseResult.NotBattleUi(CountInputTreeNodes(inputRoot), CountStaleShowCursorBlockers(inputRoot));
        }

        string[] candidateDescriptions = candidates
            .Select(entry => DescribeInputNode(entry.Node, entry.Depth))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        int before = CountInputTreeNodes(inputRoot);
        int closeAttempts = 0;
        int detached = 0;
        int released = 0;

        foreach (InputNodeEntry entry in candidates.OrderByDescending(entry => entry.Depth))
        {
            if (!CurrentScreenIsExactBattleUi())
            {
                break;
            }

            object node = entry.Node;
            if (!IsProvenStaleProfileLoadingCursorBlocker(node))
            {
                continue;
            }

            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"battle_ui_input_tree_release_candidate source={source}; node={DescribeInputNode(node, entry.Depth)}; parent={FormatTypeName(entry.Parent?.GetType())}; reason=active_vanilla_profile_loading_show_cursor_while_exact_battle_ui");

            if (TryInvokeClose(node))
            {
                closeAttempts++;
            }

            bool stillListed = IsNodeStillListed(entry);
            bool stillBlocking = stillListed && IsProvenStaleProfileLoadingCursorBlocker(node);
            if (stillBlocking && entry.Parent != null && entry.ParentChildren != null && TryDetachChild(entry.Parent, entry.ParentChildren, node))
            {
                detached++;
            }

            stillListed = IsNodeStillListed(entry);
            stillBlocking = stillListed && IsProvenStaleProfileLoadingCursorBlocker(node);
            if (!stillBlocking)
            {
                released++;
            }
        }

        int remaining = CountStaleShowCursorBlockers(inputRoot);
        InputNodeReleaseResult result = new InputNodeReleaseResult
        {
            InputRootAvailable = true,
            BattleUi = CurrentScreenIsExactBattleUi(),
            Before = before,
            After = CountInputTreeNodes(inputRoot),
            CloseAttempts = closeAttempts,
            Detached = detached,
            Released = released,
            RemainingBlockers = remaining,
            Candidates = string.Join("|", candidateDescriptions),
        };

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_ui_input_tree_release_result source={source}; {result.Describe()}; repairPolicy=profile_loading_only_no_global_input_mutation");
        return result;
    }

    private static bool IsPlayableBattleUiReady(out string readiness)
    {
        bool exactBattleUi = CurrentScreenIsExactBattleUi();
        bool battleUiDelayElapsed = battleUiShownAtUtc != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - battleUiShownAtUtc >= BattleUiSettleDelay;
        object? inputRoot = battleUiDelayElapsed && exactBattleUi ? ResolveUiInputRoot() : null;
        bool inputRootAvailable = inputRoot != null;

        readiness = $"exactBattleUi={exactBattleUi}; battleUiShown={battleUiShown}; battleUiSettleDelayElapsed={battleUiDelayElapsed}; inputRoot={inputRootAvailable}; screen={DescribeCurrentScreen()}";
        return exactBattleUi && battleUiDelayElapsed && inputRootAvailable;
    }

    private static bool IsProvenStaleProfileLoadingCursorBlocker(object node)
    {
        Type type = node.GetType();
        string assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        if (!string.Equals(assemblyName, "Assembly-CSharp", StringComparison.Ordinal))
        {
            return false;
        }

        string fullName = type.FullName ?? type.Name;
        if (!string.Equals(fullName, "EFT.UI.ProfileLoadingScreen", StringComparison.Ordinal))
        {
            return false;
        }

        return IsInputNodeActive(node) && IsShowCursorSource(node);
    }

    private static int CountStaleShowCursorBlockers(object inputRoot)
    {
        return EnumerateInputNodeTree(inputRoot).Count(entry => entry.Parent != null && IsProvenStaleProfileLoadingCursorBlocker(entry.Node));
    }

    private static int CountInputTreeNodes(object inputRoot)
    {
        return EnumerateInputNodeTree(inputRoot).Length;
    }

    private static bool IsShowCursorSource(object node)
    {
        object? shouldLock = InvokeShouldLockCursor(node);
        return string.Equals(Convert.ToString(shouldLock), "ShowCursor", StringComparison.Ordinal);
    }

    private static void CompletePending(string source, string detail)
    {
        string screenDiagnostics = DescribeBattleUiObservationDiagnostics();
        pending = false;
        battleUiShown = false;
        completed = true;
        cleanSinceUtc = DateTimeOffset.MinValue;
        UnsubscribeCurrentScreenChangedEvent();
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"operator_inventory_input_release_completed source={source}; reason={pendingReason}; battleUiActivationSource={battleUiActivationSource}; fallbackUsed={battleUiFallbackUsed}; {screenDiagnostics}; {detail}");
    }

    private static void AbortPending(string eventName, string detail, bool warning)
    {
        string screenDiagnostics = DescribeBattleUiObservationDiagnostics();
        pending = false;
        battleUiShown = false;
        completed = true;
        cleanSinceUtc = DateTimeOffset.MinValue;
        UnsubscribeCurrentScreenChangedEvent();
        string message = $"{eventName} source={pendingSource}; reason={pendingReason}; battleUiActivationSource={battleUiActivationSource}; fallbackUsed={battleUiFallbackUsed}; {screenDiagnostics}; {detail}";
        if (warning)
        {
            VanguardClientDiagnosticsLog.Warning(VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag, message);
        }
        else
        {
            VanguardClientDiagnosticsLog.Info(VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag, message);
        }
    }

    private static string DescribeInputRootSummary(object inputRoot)
    {
        return $"type={FormatTypeName(inputRoot.GetType())},nodes={CountInputTreeNodes(inputRoot)},provenStaleProfileLoading={CountStaleShowCursorBlockers(inputRoot)}";
    }

    private static string DescribeInputRootDetailed(object inputRoot)
    {
        InputNodeEntry[] entries = EnumerateInputNodeTree(inputRoot).Take(40).ToArray();
        string[] childTypes = entries
            .Where(entry => entry.Depth > 0)
            .Select(entry => DescribeInputNode(entry.Node, entry.Depth))
            .Take(32)
            .ToArray();
        return $"type={FormatTypeName(inputRoot.GetType())},nodes={entries.Length},provenStaleProfileLoading={CountStaleShowCursorBlockers(inputRoot)},nodesListed={string.Join("|", childTypes)}";
    }

    private static string DescribeItemUiContextSummary()
    {
        object? itemUiContext = ResolveItemUiContextInstance();
        if (itemUiContext == null)
        {
            return "<none>";
        }

        object? shouldLock = InvokeShouldLockCursor(itemUiContext);
        return $"type={FormatTypeName(itemUiContext.GetType())},shouldLock={shouldLock ?? "<unknown>"}";
    }

    private static string DescribeCurrentScreen()
    {
        return ObserveCurrentScreen().Description;
    }

    private static bool CurrentScreenIsExactBattleUi()
    {
        return ObserveCurrentScreen().ExactBattleUi;
    }

    private static CurrentScreenObservation ObserveCurrentScreen()
    {
        currentScreenProbeCount++;
        object? singleton = ResolveCurrentScreenSingleton();
        if (singleton == null)
        {
            lastScreenObservation = "screenSingleton=<none>";
            return new CurrentScreenObservation(false, false, false, false, lastScreenObservation);
        }

        currentScreenSingletonResolvedCount++;
        string root = Convert.ToString(ResolveMember(singleton, "RootScreenType")) ?? string.Empty;
        object? current = ResolveMember(singleton, "CurrentScreenController");
        string currentType = Convert.ToString(current == null ? null : ResolveMember(current, "ScreenType")) ?? string.Empty;
        bool rootBattleUi = string.Equals(root, "BattleUI", StringComparison.Ordinal);
        bool currentIsBattleUi = string.Equals(currentType, "BattleUI", StringComparison.Ordinal);
        bool exactBattleUi = rootBattleUi && currentIsBattleUi;

        rootBattleUiSeen |= rootBattleUi;
        currentBattleUiSeen |= currentIsBattleUi;
        exactBattleUiSeen |= exactBattleUi;
        lastScreenObservation = $"root={Sanitize(root)},current={Sanitize(currentType)},controller={FormatTypeName(current?.GetType())}";
        return new CurrentScreenObservation(true, rootBattleUi, currentIsBattleUi, exactBattleUi, lastScreenObservation);
    }

    private static string DescribeBattleUiObservationDiagnostics()
    {
        return $"primaryCallbackSeen={battleUiPrimaryCallbackSeen}; screenChangedEventSeen={battleUiScreenChangedEventSeen}; screenChangedBattleUiSeen={battleUiScreenChangedEventBattleUiSeen}; screenChangedEvents={currentScreenChangedEventCount}; eventSubscriptionSuccesses={currentScreenEventSubscriptionSuccesses}; eventSubscriptionFailures={currentScreenEventSubscriptionFailures}; eventSubscriptionSwitches={currentScreenEventSubscriptionSwitches}; screenProbes={currentScreenProbeCount}; singletonResolvedProbes={currentScreenSingletonResolvedCount}; rootBattleUiSeen={rootBattleUiSeen}; currentBattleUiSeen={currentBattleUiSeen}; exactBattleUiSeen={exactBattleUiSeen}; lastScreen={lastScreenObservation}; lastScreenEvent={lastScreenChangedEvent}; lastEventSubscriptionError={lastScreenEventSubscriptionError}";
    }

    private static bool TryInvokeClose(object target)
    {
        try
        {
            Type type = target.GetType();
            if (!CloseMethodCache.TryGetValue(type, out MethodInfo? closeMethod))
            {
                closeMethod = ResolveMethod(type, "Close", 0);
                CloseMethodCache[type] = closeMethod;
            }

            if (closeMethod == null)
            {
                return false;
            }

            closeMethod.Invoke(target, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDetachChild(object parent, IList children, object child)
    {
        try
        {
            MethodInfo? removeMethod = ResolveMethod(parent.GetType(), "method_1", 1)
                ?? ResolveMethod(parent.GetType(), "Remove", 1)
                ?? ResolveMethod(parent.GetType(), "RemoveChildNode", 1);
            if (removeMethod != null)
            {
                removeMethod.Invoke(parent, new[] { child });
                return !ContainsReference(children, child);
            }
        }
        catch
        {
        }

        try
        {
            if (ContainsReference(children, child))
            {
                children.Remove(child);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsNodeStillListed(InputNodeEntry entry)
    {
        return entry.ParentChildren != null && ContainsReference(entry.ParentChildren, entry.Node);
    }

    private static bool ContainsReference(IList list, object instance)
    {
        foreach (object? item in list)
        {
            if (ReferenceEquals(item, instance))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInputNodeActive(object node)
    {
        try
        {
            if (node is Behaviour behaviour && !behaviour.enabled)
            {
                return false;
            }

            if (node is Component component && component.gameObject != null && !component.gameObject.activeSelf)
            {
                return false;
            }
        }
        catch
        {
        }

        return true;
    }

    private static string DescribeInputNode(object? node, int depth)
    {
        if (node == null)
        {
            return "<null>";
        }

        string type = FormatTypeName(node.GetType());
        object? cursor = InvokeShouldLockCursor(node);
        string enabled = "<unknown>";
        string active = "<unknown>";
        int childCount = -1;
        try
        {
            if (node is Behaviour behaviour)
            {
                enabled = behaviour.enabled.ToString();
            }

            if (node is Component component && component.gameObject != null)
            {
                active = component.gameObject.activeSelf.ToString();
            }

            IList? children = ResolveChildrenList(node);
            childCount = children?.Count ?? -1;
        }
        catch
        {
        }

        return $"depth={depth}:{type}:{cursor ?? "<unknown>"}:enabled={enabled}:active={active}:children={childCount}";
    }

    private static object? InvokeShouldLockCursor(object target)
    {
        try
        {
            Type type = target.GetType();
            if (!ShouldLockCursorMethodCache.TryGetValue(type, out MethodInfo? method))
            {
                method = ResolveMethod(type, "ShouldLockCursor", 0);
                ShouldLockCursorMethodCache[type] = method;
            }

            return method?.Invoke(target, Array.Empty<object>());
        }
        catch
        {
            return null;
        }
    }

    private static InputNodeEntry[] EnumerateInputNodeTree(object root)
    {
        List<InputNodeEntry> entries = new List<InputNodeEntry>();
        List<object> visited = new List<object>();
        VisitInputNode(root, null, null, 0, entries, visited);
        return entries.ToArray();
    }

    private static void VisitInputNode(object node, object? parent, IList? parentChildren, int depth, List<InputNodeEntry> entries, List<object> visited)
    {
        if (depth > 8 || entries.Count >= 160 || visited.Any(existing => ReferenceEquals(existing, node)))
        {
            return;
        }

        visited.Add(node);
        entries.Add(new InputNodeEntry
        {
            Node = node,
            Parent = parent,
            ParentChildren = parentChildren,
            Depth = depth,
        });

        IList? children = ResolveChildrenList(node);
        if (children == null)
        {
            return;
        }

        foreach (object? child in children.Cast<object?>().Where(child => child != null).ToArray())
        {
            if (child != null)
            {
                VisitInputNode(child, node, children, depth + 1, entries, visited);
            }
        }
    }

    private static object? ResolveUiInputRoot()
    {
        object? cached = GetCachedUiInputRootIfAlive();
        if (cached != null)
        {
            return cached;
        }

        ResolveTarkovApplicationUiInputRootAccessor();
        Type? tarkovApplicationType = cachedTarkovApplicationType;
        MethodInfo? existMethod = cachedTarkovApplicationExistMethod;
        FieldInfo? inputRootField = cachedTarkovApplicationUiInputRootField;
        if (tarkovApplicationType == null || existMethod == null || inputRootField == null)
        {
            return null;
        }

        try
        {
            object?[] arguments = { null };
            object? exists = existMethod.Invoke(null, arguments);
            if (exists is not bool success || !success || arguments[0] == null)
            {
                return null;
            }

            cachedUiInputRoot = inputRootField.GetValue(arguments[0]);
        }
        catch
        {
            cachedUiInputRoot = null;
        }

        return GetCachedUiInputRootIfAlive();
    }

    private static void ResolveTarkovApplicationUiInputRootAccessor()
    {
        if (tarkovApplicationUiInputRootAccessorResolved)
        {
            return;
        }

        tarkovApplicationUiInputRootAccessorResolved = true;
        cachedTarkovApplicationType = Type.GetType("EFT.TarkovApplication, Assembly-CSharp", throwOnError: false)
            ?? AccessTools.TypeByName("EFT.TarkovApplication");
        Type? type = cachedTarkovApplicationType;
        if (type == null)
        {
            return;
        }

        cachedTarkovApplicationExistMethod = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => string.Equals(method.Name, "Exist", StringComparison.Ordinal)
                && method.ReturnType == typeof(bool)
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType.IsByRef);

        cachedTarkovApplicationUiInputRootField = ResolveField(type, "uiinputRoot_0");
        if (cachedTarkovApplicationUiInputRootField != null)
        {
            return;
        }

        // Exact SPT 4.0.13 field name is uiinputRoot_0. Keep a non-scanning structural
        // fallback for nearby obfuscation changes: inspect TarkovApplication's own
        // fields and select the one whose declared type is UIInputRoot.
        cachedTarkovApplicationUiInputRootField = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(field => string.Equals(field.FieldType.FullName, "EFT.InputSystem.UIInputRoot", StringComparison.Ordinal)
                || string.Equals(field.FieldType.Name, "UIInputRoot", StringComparison.Ordinal));
    }

    private static object? GetCachedUiInputRootIfAlive()
    {
        object? cached = cachedUiInputRoot;
        if (cached == null)
        {
            return null;
        }

        try
        {
            if (cached is UnityEngine.Object unityObject && unityObject == null)
            {
                cachedUiInputRoot = null;
                return null;
            }
        }
        catch
        {
            cachedUiInputRoot = null;
            return null;
        }

        return cached;
    }

    private static object? ResolveCurrentScreenSingleton()
    {
        ResolveCurrentScreenSingletonAccessor();
        if (cachedCurrentScreenSingletonType == null)
        {
            return null;
        }

        try
        {
            // Do not cache the singleton instance. Menu -> raid transitions are exactly
            // where stale lifecycle references are unacceptable; re-reading Instance at
            // the bounded 250 ms gate is effectively free and survives instance swaps.
            return cachedCurrentScreenSingletonInstanceProperty?.GetValue(null)
                ?? cachedCurrentScreenSingletonInstanceField?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static void ResolveCurrentScreenSingletonAccessor()
    {
        if (currentScreenSingletonAccessorResolved)
        {
            return;
        }

        cachedCurrentScreenSingletonType = Type.GetType("CurrentScreenSingletonClass, Assembly-CSharp", throwOnError: false)
            ?? ResolveTypeByName("CurrentScreenSingletonClass");
        Type? type = cachedCurrentScreenSingletonType;
        if (type == null)
        {
            // Assembly-CSharp should already be loaded, but retain retry semantics rather
            // than permanently freezing a transient type-resolution miss.
            return;
        }

        cachedCurrentScreenSingletonInstanceProperty = AccessTools.Property(type, "Instance")
            ?? AccessTools.Property(type, "instance");
        cachedCurrentScreenSingletonInstanceField = AccessTools.Field(type, "Instance")
            ?? AccessTools.Field(type, "instance");
        currentScreenSingletonAccessorResolved = cachedCurrentScreenSingletonInstanceProperty != null
            || cachedCurrentScreenSingletonInstanceField != null;
    }

    private static void EnsureCurrentScreenChangedSubscription()
    {
        if (!pending || completed)
        {
            return;
        }

        object? singleton = ResolveCurrentScreenSingleton();
        if (singleton == null)
        {
            return;
        }

        if (ReferenceEquals(singleton, currentScreenEventSubscribedInstance)
            && currentScreenChangedEventInfo != null
            && currentScreenChangedEventHandler != null)
        {
            return;
        }

        if (currentScreenEventSubscribedInstance != null)
        {
            currentScreenEventSubscriptionSwitches++;
            UnsubscribeCurrentScreenChangedEvent();
        }

        try
        {
            EventInfo? eventInfo = ResolveEvent(singleton.GetType(), "OnScreenChanged");
            Type? handlerType = eventInfo?.EventHandlerType;
            MethodInfo? invoke = handlerType?.GetMethod("Invoke");
            ParameterInfo[] parameters = invoke?.GetParameters() ?? Array.Empty<ParameterInfo>();
            if (eventInfo == null || handlerType == null || parameters.Length != 1)
            {
                currentScreenEventSubscriptionFailures++;
                lastScreenEventSubscriptionError = "OnScreenChanged_event_or_single_argument_handler_unavailable";
                return;
            }

            MethodInfo? genericHandler = typeof(VanguardBattleInputNodeReleaseService).GetMethod(
                nameof(HandleCurrentScreenChangedGeneric),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (genericHandler == null || !genericHandler.IsGenericMethodDefinition)
            {
                currentScreenEventSubscriptionFailures++;
                lastScreenEventSubscriptionError = "generic_screen_changed_handler_unavailable";
                return;
            }

            MethodInfo closedHandler = genericHandler.MakeGenericMethod(parameters[0].ParameterType);
            Delegate handler = Delegate.CreateDelegate(handlerType, closedHandler);
            eventInfo.AddEventHandler(singleton, handler);
            currentScreenEventSubscribedInstance = singleton;
            currentScreenChangedEventInfo = eventInfo;
            currentScreenChangedEventHandler = handler;
            currentScreenEventSubscriptionSuccesses++;
            lastScreenEventSubscriptionError = "<none>";
        }
        catch (Exception exception)
        {
            currentScreenEventSubscriptionFailures++;
            lastScreenEventSubscriptionError = Sanitize($"{exception.GetType().Name}:{exception.Message}");
            currentScreenEventSubscribedInstance = null;
            currentScreenChangedEventInfo = null;
            currentScreenChangedEventHandler = null;
        }
    }

    private static void UnsubscribeCurrentScreenChangedEvent()
    {
        object? singleton = currentScreenEventSubscribedInstance;
        EventInfo? eventInfo = currentScreenChangedEventInfo;
        Delegate? handler = currentScreenChangedEventHandler;
        currentScreenEventSubscribedInstance = null;
        currentScreenChangedEventInfo = null;
        currentScreenChangedEventHandler = null;

        if (singleton == null || eventInfo == null || handler == null)
        {
            return;
        }

        try
        {
            eventInfo.RemoveEventHandler(singleton, handler);
        }
        catch
        {
            // Subscription teardown is best-effort only. The handler itself is gated by
            // pending/completed, so a failed detach cannot grant mutation authority.
        }
    }

    private static void HandleCurrentScreenChangedGeneric<TScreen>(TScreen screenType)
    {
        if (!pending || completed)
        {
            return;
        }

        battleUiScreenChangedEventSeen = true;
        currentScreenChangedEventCount++;
        string value = Convert.ToString(screenType) ?? "<null>";
        lastScreenChangedEvent = Sanitize(value);
        if (string.Equals(value, "BattleUI", StringComparison.Ordinal))
        {
            battleUiScreenChangedEventBattleUiSeen = true;
            NotifyBattleUiShown("current_screen_changed_battle_ui_event");
        }
    }

    private static object? ResolveItemUiContextInstance()
    {
        Type? itemUiContextType = Type.GetType("EFT.UI.ItemUiContext, Assembly-CSharp", throwOnError: false)
            ?? ResolveTypeByName("EFT.UI.ItemUiContext")
            ?? ResolveTypeByName("ItemUiContext");
        if (itemUiContextType == null)
        {
            return null;
        }

        try
        {
            return AccessTools.Property(itemUiContextType, "Instance")?.GetValue(null)
                ?? AccessTools.Field(itemUiContextType, "Instance")?.GetValue(null)
                ?? AccessTools.Property(itemUiContextType, "instance")?.GetValue(null)
                ?? AccessTools.Field(itemUiContextType, "instance")?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static IList? ResolveChildrenList(object inputNode)
    {
        if (!childrenFieldResolved)
        {
            cachedChildrenField = ResolveField(inputNode.GetType(), "_children");
            childrenFieldResolved = true;
        }

        try
        {
            return cachedChildrenField?.GetValue(inputNode) as IList;
        }
        catch
        {
            return null;
        }
    }

    private static object? ResolveMember(object target, string name)
    {
        try
        {
            PropertyInfo? property = AccessTools.Property(target.GetType(), name);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(target);
            }

            FieldInfo? field = AccessTools.Field(target.GetType(), name);
            return field?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static EventInfo? ResolveEvent(Type? type, string name)
    {
        while (type != null)
        {
            EventInfo? eventInfo = type.GetEvent(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (eventInfo != null)
            {
                return eventInfo;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static FieldInfo? ResolveField(Type? type, string name)
    {
        while (type != null)
        {
            FieldInfo? field = AccessTools.Field(type, name)
                ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static MethodInfo? ResolveMethod(Type? type, string name, int parameterCount)
    {
        while (type != null)
        {
            MethodInfo? method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal)
                    && candidate.GetParameters().Length == parameterCount);
            if (method != null)
            {
                return method;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static Type? ResolveTypeByName(string typeName)
    {
        Type? direct = AccessTools.TypeByName(typeName);
        if (direct != null)
        {
            return direct;
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(type => string.Equals(type.FullName, typeName, StringComparison.Ordinal)
                || string.Equals(type.Name, typeName, StringComparison.Ordinal));
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static string FormatTypeName(Type? type)
    {
        return type == null ? "<null>" : (type.FullName ?? type.Name).Replace(';', ',');
    }

    private static string Sanitize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
    }

    private readonly struct CurrentScreenObservation
    {
        public CurrentScreenObservation(bool singletonResolved, bool rootBattleUi, bool currentBattleUi, bool exactBattleUi, string description)
        {
            SingletonResolved = singletonResolved;
            RootBattleUi = rootBattleUi;
            CurrentBattleUi = currentBattleUi;
            ExactBattleUi = exactBattleUi;
            Description = description;
        }

        public bool SingletonResolved { get; }
        public bool RootBattleUi { get; }
        public bool CurrentBattleUi { get; }
        public bool ExactBattleUi { get; }
        public string Description { get; }
    }

    private sealed class InputNodeEntry
    {
        public object Node { get; init; } = new object();
        public object? Parent { get; init; }
        public IList? ParentChildren { get; init; }
        public int Depth { get; init; }
    }

    private sealed class InputNodeReleaseResult
    {
        public bool InputRootAvailable { get; init; }
        public bool BattleUi { get; init; } = true;
        public int Before { get; init; }
        public int After { get; init; }
        public int CloseAttempts { get; init; }
        public int Detached { get; init; }
        public int Released { get; init; }
        public int RemainingBlockers { get; init; }
        public string Candidates { get; init; } = string.Empty;

        public static InputNodeReleaseResult NotBattleUi(int nodeCount, int remainingBlockers)
        {
            return new InputNodeReleaseResult
            {
                InputRootAvailable = true,
                BattleUi = false,
                Before = nodeCount,
                After = nodeCount,
                RemainingBlockers = remainingBlockers,
                Candidates = string.Empty,
            };
        }

        public string Describe()
        {
            return $"inputRoot={InputRootAvailable}; battleUi={BattleUi}; beforeNodes={Before}; afterNodes={After}; closeAttempts={CloseAttempts}; detached={Detached}; released={Released}; remainingInResult={RemainingBlockers}; candidates={Candidates}";
        }
    }

#else
    public static void RequestForRaidStart(string source, string reason) { }
    public static void NotifyBattleUiShown(string source) { }
    public static void Tick() { }
#endif
}

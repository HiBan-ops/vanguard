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

// Responsibility: Removes stale EFT input blockers that can remain after leaving direct Operator inventory and then entering a raid.
// Flow: The inventory-exit marker arms the service; it waits for BattleUI to exist and settle, releases stale ShowCursor nodes through UIInputRoot, then confirms input has remained clean before stopping.
// Authority boundary: EFT UI/InputManager remain input authority; Vanguard only removes the specific stale nodes caused by its Off-Raid inventory transition and never writes cursor visibility/lock state directly.
// Invariant: The normal raid path stays untouched, expensive scene polling is forbidden during loading, and any temporary repair state expires instead of becoming permanent ownership.
namespace Vanguard.Client.Raid.Services;

/// <summary>
/// Settles EFT input after a direct Operator inventory was opened before raid.
///
/// This service is deliberately inactive on the vanilla path: it only runs when
/// the off-raid Operator inventory exit flag was consumed by raid start. the runtime path
/// keeps the runtime winning fix that recursively removes stale ShowCursor
/// blockers from UIInputRoot, but defers all expensive work until BattleUI.Show
/// has fired. There is no current-screen polling, no readiness probing, and no
/// Unity FindObjectOfType loop during TimeHasCome / FinalCountdown loading.
///
/// It still never writes Cursor.visible or Cursor.lockState. It releases stale
/// input nodes through the available UIInputRoot first, then attempts the
/// InputManager external show-cursor clear only when that runtime object can be
/// resolved after BattleUI has settled.
/// </summary>
internal static class VanguardBattleInputNodeReleaseService
{
#if SPT_CLIENT
    private static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BattleUiSettleDelay = TimeSpan.FromSeconds(2);
    private const int RequiredStableConfirmations = 2;

    private static bool pending;
    private static bool battleUiShown;
    private static bool completed;
    private static DateTimeOffset requestedAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset battleUiShownAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset lastAttemptUtc = DateTimeOffset.MinValue;
    private static int attempts;
    private static int stableConfirmations;
    private static int gameplayInputRestoreAttempts;
    private static int gameplayInputRestoreAppliedTotal;
    private static int inputManagerSettleAttempts;
    private static int inputManagerSettleAppliedTotal;
    private static string pendingSource = "<none>";
    private static string pendingReason = "<none>";

    public static void RequestForRaidStart(string source, string reason)
    {
        pending = true;
        completed = false;
        battleUiShown = false;
        requestedAtUtc = DateTimeOffset.UtcNow;
        battleUiShownAtUtc = DateTimeOffset.MinValue;
        lastAttemptUtc = DateTimeOffset.MinValue;
        attempts = 0;
        stableConfirmations = 0;
        gameplayInputRestoreAttempts = 0;
        gameplayInputRestoreAppliedTotal = 0;
        inputManagerSettleAttempts = 0;
        inputManagerSettleAppliedTotal = 0;
        pendingSource = source;
        pendingReason = Sanitize(reason);

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_input_tree_release_pending source={source}; reason={pendingReason}; pendingWindowSeconds={PendingWindow.TotalSeconds:0}; gate=battle_ui_show_then_deferred_input_tree_release; settleDelaySeconds={BattleUiSettleDelay.TotalSeconds:0.0}; loadingPolicy=no_polling_before_battle_ui_show; cursorPolicy=no_forced_cursor_visible_or_lockstate");
    }

    public static void NotifyBattleUiShown(string source)
    {
        if (!pending || completed)
        {
            return;
        }

        battleUiShown = true;
        battleUiShownAtUtc = DateTimeOffset.UtcNow;
        stableConfirmations = 0;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_input_tree_release_battle_ui_shown source={source}; pendingSource={pendingSource}; waitedSeconds={(battleUiShownAtUtc - requestedAtUtc).TotalSeconds:0}; settleDelaySeconds={BattleUiSettleDelay.TotalSeconds:0.0}; action=armed_for_deferred_input_tree_release_no_postfix_scan");
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
            // The runtime performance rule: once raid start consumes the Operator-inventory flag,
            // remain completely passive during TimeHasCome / FinalCountdown.  BattleUI.Show
            // is the only gate that arms the actual release work, so the normal loading path
            // is not slowed by current-screen polling or Unity FindObjectOfType scans.
            if (now - requestedAtUtc > PendingWindow)
            {
                pending = false;
                battleUiShown = false;
                stableConfirmations = 0;
                VanguardClientDiagnosticsLog.Warning(
                    VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                    $"battle_input_tree_release_expired_before_battle_ui source={pendingSource}; reason={pendingReason}; waitedSeconds={(now - requestedAtUtc).TotalSeconds:0}; action=aborted_without_loading_polling");
            }

            return;
        }

        if (now - requestedAtUtc > PendingWindow)
        {
            pending = false;
            battleUiShown = false;
            stableConfirmations = 0;
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"battle_input_tree_release_expired_after_battle_ui source={pendingSource}; reason={pendingReason}; waitedSeconds={(now - requestedAtUtc).TotalSeconds:0}; screen={DescribeCurrentScreen()}; action=aborted_after_battle_ui");
            return;
        }

        if (lastAttemptUtc != DateTimeOffset.MinValue && now - lastAttemptUtc < RetryInterval)
        {
            return;
        }

        TryRun("battle_ui_tick_deferred");
    }

    private static void TryRun(string source)
    {
        if (!pending || completed)
        {
            return;
        }

        lastAttemptUtc = DateTimeOffset.UtcNow;

        if (!IsPlayableBattleUiReady(out string readiness))
        {
            stableConfirmations = 0;
            return;
        }

        attempts++;
        string releaseSource = source + ":input_tree_settled_lite";

        if (stableConfirmations > 0)
        {
            ConfirmLightweightSettle(releaseSource, readiness);
            return;
        }

        InputNodeReleaseResult releaseResult = TryReleaseStaleShowCursorNodes(releaseSource);
        GameplayGateRestoreResult gateResult = TryRestoreGameplayInputGates(releaseSource);
        InputManagerSettleResult inputManagerResult = TrySettleInputManagerExternalShowFlag(releaseSource);

        int remainingStaleShowCursor = CountStaleShowCursorBlockers();
        InputManagerSnapshot finalInputManager = CaptureInputManagerSnapshot();
        bool inputManagerSettled = !finalInputManager.ExternalShowKnown || finalInputManager.ExternalShow == false;
        bool settled = remainingStaleShowCursor == 0 && inputManagerSettled;

        if (settled)
        {
            stableConfirmations = 1;
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"operator_inventory_input_release_stable_probe source={releaseSource}; mode=first_release_then_light_confirmation; attempts={attempts}; stableConfirmations={stableConfirmations}/{RequiredStableConfirmations}; {releaseResult.Describe()}; {gateResult.Describe()}; {inputManagerResult.Describe()}; remainingStaleShowCursor={remainingStaleShowCursor}; finalInputManager={finalInputManager.Describe()}; readiness={readiness}; nextAction=light_confirm_next_tick");
            return;
        }

        stableConfirmations = 0;
        VanguardClientDiagnosticsLog.Warning(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"operator_inventory_input_release_still_blocked source={releaseSource}; attempts={attempts}; {releaseResult.Describe()}; {gateResult.Describe()}; {inputManagerResult.Describe()}; remainingStaleShowCursor={remainingStaleShowCursor}; inputManagerSettled={inputManagerSettled}; finalInputManager={finalInputManager.Describe()}; readiness={readiness}; audit={DescribeInputRuntimeDetailed()}");
    }

    private static void ConfirmLightweightSettle(string source, string readiness)
    {
        int remainingStaleShowCursor = CountStaleShowCursorBlockers();
        InputManagerSnapshot finalInputManager = CaptureInputManagerSnapshot();
        bool inputManagerSettled = !finalInputManager.ExternalShowKnown || finalInputManager.ExternalShow == false;
        bool settled = remainingStaleShowCursor == 0 && inputManagerSettled;

        if (!settled)
        {
            stableConfirmations = 0;
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"operator_inventory_input_release_light_confirm_failed source={source}; attempts={attempts}; remainingStaleShowCursor={remainingStaleShowCursor}; inputManagerSettled={inputManagerSettled}; finalInputManager={finalInputManager.Describe()}; readiness={readiness}; action=retry_full_release");
            return;
        }

        stableConfirmations++;
        pending = false;
        battleUiShown = false;
        completed = true;
        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"operator_inventory_input_release_completed source={source}; mode=light_confirmation; attempts={attempts}; stableConfirmations={stableConfirmations}; remainingStaleShowCursor={remainingStaleShowCursor}; finalInputManager={finalInputManager.Describe()}; readiness={readiness}; cursorPolicy=no_forced_cursor_visible_or_lockstate; finalInputRoot={DescribeInputRootSummary()}; finalItemUiContext={DescribeItemUiContextSummary()}");
    }

    private static InputNodeReleaseResult TryReleaseStaleShowCursorNodes(string source)
    {
        object? inputRoot = ResolveUiInputRoot();
        if (inputRoot == null)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"battle_ui_input_tree_release_skipped source={source}; reason=input_root_missing; audit={DescribeInputRuntimeDetailed()}");
            return InputNodeReleaseResult.MissingInputRoot();
        }

        if (!CurrentScreenLooksLikeBattleUi())
        {
            int currentCount = CountInputTreeNodes(inputRoot);
            int remaining = CountStaleShowCursorBlockers(inputRoot);
            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"battle_ui_input_tree_release_skipped source={source}; reason=not_battle_ui; screen={DescribeCurrentScreen()}; nodes={currentCount}; staleShowCursor={remaining}");
            return InputNodeReleaseResult.NotBattleUi(currentCount, remaining);
        }

        InputNodeEntry[] entriesBefore = EnumerateInputNodeTree(inputRoot).ToArray();
        string[] candidates = entriesBefore
            .Where(entry => IsStaleShowCursorBlocker(entry.Node))
            .Select(entry => DescribeInputNode(entry.Node, entry.Depth))
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();

        int closeAttempts = 0;
        int detached = 0;
        int alreadyInactive = 0;
        int released = 0;

        foreach (InputNodeEntry entry in entriesBefore.OrderByDescending(entry => entry.Depth))
        {
            object node = entry.Node;
            if (!IsStaleShowCursorBlocker(node))
            {
                continue;
            }

            VanguardClientDiagnosticsLog.Info(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"battle_ui_input_tree_release_candidate source={source}; node={DescribeInputNode(node, entry.Depth)}; parent={FormatTypeName(entry.Parent?.GetType())}; reason={DescribeStaleReason(node)}");

            if (!IsInputNodeActive(node))
            {
                alreadyInactive++;
            }

            if (TryInvokeClose(node))
            {
                closeAttempts++;
            }

            bool stillListed = IsNodeStillListed(entry);
            bool stillStaleShowCursor = stillListed && IsStaleShowCursorBlocker(node);
            if (stillListed && stillStaleShowCursor && entry.Parent != null && entry.ParentChildren != null && TryDetachChild(entry.Parent, entry.ParentChildren, node))
            {
                detached++;
            }

            stillListed = IsNodeStillListed(entry);
            stillStaleShowCursor = stillListed && IsStaleShowCursorBlocker(node);
            if (!stillListed || !stillStaleShowCursor)
            {
                released++;
            }
        }

        int remainingBlockers = CountStaleShowCursorBlockers(inputRoot);
        int after = CountInputTreeNodes(inputRoot);
        InputNodeReleaseResult result = new InputNodeReleaseResult
        {
            InputRootAvailable = true,
            BattleUi = true,
            Before = entriesBefore.Length,
            After = after,
            CloseAttempts = closeAttempts,
            Detached = detached,
            AlreadyInactive = alreadyInactive,
            Released = released,
            RemainingBlockers = remainingBlockers,
            Candidates = string.Join("|", candidates),
        };

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_ui_input_tree_release_result source={source}; {result.Describe()}");
        return result;
    }

    private static int CountStaleShowCursorBlockers()
    {
        object? inputRoot = ResolveUiInputRoot();
        return inputRoot == null ? 0 : CountStaleShowCursorBlockers(inputRoot);
    }

    private static int CountStaleShowCursorBlockers(object inputRoot)
    {
        return EnumerateInputNodeTree(inputRoot).Count(entry => entry.Parent != null && IsStaleShowCursorBlocker(entry.Node));
    }

    private static int CountInputTreeNodes(object inputRoot)
    {
        return EnumerateInputNodeTree(inputRoot).Count();
    }

    private static bool IsStaleShowCursorBlocker(object node)
    {
        if (!IsShowCursorSource(node))
        {
            return false;
        }

        if (!IsInputNodeActive(node))
        {
            return true;
        }

        string fullName = FormatTypeName(node.GetType()).ToLowerInvariant();
        if (fullName.Contains("chatscreen"))
        {
            return true;
        }

        if (fullName.Contains("inventory") || fullName.Contains("menuscreen") || fullName.Contains("matchmaker") || fullName.Contains("timehascome") || fullName.Contains("finalcountdown") || fullName.Contains("profileloading") || fullName.Contains("loadingscreen"))
        {
            return true;
        }

        return false;
    }

    private static bool IsShowCursorSource(object node)
    {
        object? shouldLock = InvokeShouldLockCursor(node);
        return string.Equals(Convert.ToString(shouldLock), "ShowCursor", StringComparison.Ordinal);
    }

    private static string DescribeStaleReason(object node)
    {
        if (!IsInputNodeActive(node))
        {
            return "inactive_show_cursor";
        }

        string fullName = FormatTypeName(node.GetType()).ToLowerInvariant();
        if (fullName.Contains("chatscreen"))
        {
            return "chat_show_cursor_after_battle_ui";
        }

        if (fullName.Contains("inventory"))
        {
            return "inventory_show_cursor_after_battle_ui";
        }

        if (fullName.Contains("menuscreen") || fullName.Contains("matchmaker") || fullName.Contains("timehascome") || fullName.Contains("finalcountdown") || fullName.Contains("profileloading") || fullName.Contains("loadingscreen"))
        {
            return "menu_or_loading_show_cursor_after_battle_ui";
        }

        return "show_cursor_after_battle_ui";
    }

    private static GameplayGateRestoreResult TryRestoreGameplayInputGates(string source)
    {
        gameplayInputRestoreAttempts++;
        Type? ownerType = ResolveTypeByName("EFT.GamePlayerOwner") ?? ResolveTypeByName("GamePlayerOwner");
        if (ownerType == null)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
                $"battle_input_gameplay_gates_restore_skipped source={source}; reason=game_player_owner_type_missing");
            return new GameplayGateRestoreResult
            {
                OwnerType = "<missing>",
                Applied = 0,
                Missing = 3,
                Attempts = gameplayInputRestoreAttempts,
                AppliedTotal = gameplayInputRestoreAppliedTotal,
            };
        }

        int applied = 0;
        int missing = 0;
        string[] methodNames =
        {
            "SetIgnoreInput",
            "SetIgnoreInputInNPCDialog",
            "SetIgnoreInputWithKeepResetLook",
        };

        foreach (string methodName in methodNames)
        {
            if (TryInvokeStaticBooleanMethod(ownerType, methodName, false))
            {
                applied++;
            }
            else
            {
                missing++;
            }
        }

        gameplayInputRestoreAppliedTotal += applied;
        GameplayGateRestoreResult result = new GameplayGateRestoreResult
        {
            OwnerType = FormatTypeName(ownerType),
            Applied = applied,
            Missing = missing,
            Attempts = gameplayInputRestoreAttempts,
            AppliedTotal = gameplayInputRestoreAppliedTotal,
        };

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_input_gameplay_gates_restored source={source}; {result.Describe()}; policy=gameplay_input_gates_false_no_cursor_lock");
        return result;
    }

    private static InputManagerSettleResult TrySettleInputManagerExternalShowFlag(string source)
    {
        inputManagerSettleAttempts++;
        InputManagerSnapshot before = CaptureInputManagerSnapshot();
        int applied = 0;
        string eventResult = "not_attempted";
        string fieldResult = "not_attempted";

        if (TryBroadcastInputManagerExternalShow(false, out eventResult))
        {
            applied++;
        }

        if (TrySetInputManagerExternalShowField(false, out fieldResult))
        {
            applied++;
        }

        inputManagerSettleAppliedTotal += applied;
        InputManagerSnapshot after = CaptureInputManagerSnapshot();
        InputManagerSettleResult result = new InputManagerSettleResult
        {
            Before = before,
            After = after,
            EventResult = eventResult,
            FieldResult = fieldResult,
            Applied = applied,
            Attempts = inputManagerSettleAttempts,
            AppliedTotal = inputManagerSettleAppliedTotal,
        };

        VanguardClientDiagnosticsLog.Info(
            VanguardBuildVersion.OperatorBattleInputNodeReleaseStatusTag,
            $"battle_input_manager_external_show_settle source={source}; {result.Describe()}; policy=input_manager_external_show_false_no_cursor_lockstate_write");
        return result;
    }

    private static bool TryBroadcastInputManagerExternalShow(bool show, out string result)
    {
        result = "event_type_missing";
        try
        {
            Type? eventType = ResolveTypeByName("GClass3565");
            if (eventType == null)
            {
                return false;
            }

            object? handler = ResolveGlobalEventHandlerInstance();
            if (handler == null)
            {
                result = "handler_missing";
                return false;
            }

            MethodInfo? createCommonEvent = handler.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.IsGenericMethodDefinition
                    && string.Equals(method.Name, "CreateCommonEvent", StringComparison.Ordinal)
                    && method.GetParameters().Length == 0);
            if (createCommonEvent == null)
            {
                result = "create_common_event_missing";
                return false;
            }

            object? commonEvent = createCommonEvent.MakeGenericMethod(eventType).Invoke(handler, Array.Empty<object>());
            if (commonEvent == null)
            {
                result = "common_event_null";
                return false;
            }

            MethodInfo? invoke = ResolveMethod(commonEvent.GetType(), "Invoke", 1);
            if (invoke == null)
            {
                result = "invoke_missing";
                return false;
            }

            ParameterInfo[] parameters = invoke.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(bool))
            {
                result = "invoke_signature_mismatch";
                return false;
            }

            invoke.Invoke(commonEvent, new object[] { show });
            result = "event_invoked";
            return true;
        }
        catch (Exception exception)
        {
            result = "event_failed_" + exception.GetType().Name;
            return false;
        }
    }

    private static bool TrySetInputManagerExternalShowField(bool show, out string result)
    {
        result = "input_manager_missing";
        object? inputManager = ResolveInputManagerInstance();
        if (inputManager == null)
        {
            return false;
        }

        FieldInfo? field = ResolveField(inputManager.GetType(), "bool_2");
        if (field == null || field.FieldType != typeof(bool))
        {
            result = "bool_2_missing";
            return false;
        }

        try
        {
            object? before = field.GetValue(inputManager);
            field.SetValue(inputManager, show);
            object? after = field.GetValue(inputManager);
            result = $"field_set before={before};after={after}";
            return true;
        }
        catch (Exception exception)
        {
            result = "field_failed_" + exception.GetType().Name;
            return false;
        }
    }

    private static bool TryInvokeStaticBooleanMethod(Type type, string methodName, bool value)
    {
        try
        {
            MethodInfo? method = ResolveStaticMethod(type, methodName, 1);
            if (method == null)
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(bool))
            {
                return false;
            }

            method.Invoke(null, new object[] { value });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPlayableBattleUiReady(out string readiness)
    {
        bool battleUi = CurrentScreenLooksLikeBattleUi();
        bool battleUiDelayElapsed = battleUiShownAtUtc != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - battleUiShownAtUtc >= BattleUiSettleDelay;
        bool myPlayer = ResolveStaticMemberValue("EFT.GamePlayerOwner", "MyPlayer") != null
            || ResolveStaticMemberValue("GamePlayerOwner", "MyPlayer") != null;
        bool inputRoot = ResolveUiInputRoot() != null;

        readiness = $"battleUi={battleUi}; battleUiShown={battleUiShown}; battleUiSettleDelayElapsed={battleUiDelayElapsed}; myPlayer={myPlayer}; inputRoot={inputRoot}; screen={DescribeCurrentScreen()}";
        return battleUi && battleUiDelayElapsed && myPlayer && inputRoot;
    }

    private static string DescribeReadiness()
    {
        _ = IsPlayableBattleUiReady(out string readiness);
        return readiness;
    }

    private static string DescribeInputRuntimeDetailed()
    {
        return $"screen={DescribeCurrentScreen()}; inputManager={CaptureInputManagerSnapshot().Describe()}; inputRoot={DescribeInputRootDetailed()}; itemUiContext={DescribeItemUiContextDetailed()}";
    }

    private static string DescribeInputRootSummary()
    {
        object? inputRoot = ResolveUiInputRoot();
        if (inputRoot == null)
        {
            return "type=<null>,nodes=0,staleShowCursor=0";
        }

        int nodes = CountInputTreeNodes(inputRoot);
        int staleShowCursor = CountStaleShowCursorBlockers(inputRoot);
        return $"type={FormatTypeName(inputRoot.GetType())},nodes={nodes},staleShowCursor={staleShowCursor}";
    }

    private static string DescribeInputRootDetailed()
    {
        object? inputRoot = ResolveUiInputRoot();
        if (inputRoot == null)
        {
            return "type=<null>,children=<missing>";
        }

        InputNodeEntry[] entries = EnumerateInputNodeTree(inputRoot).Take(32).ToArray();
        string[] childTypes = entries
            .Where(entry => entry.Depth > 0)
            .Select(entry => DescribeInputNode(entry.Node, entry.Depth))
            .Take(24)
            .ToArray();
        return $"type={FormatTypeName(inputRoot.GetType())},nodes={entries.Length},staleShowCursor={CountStaleShowCursorBlockers(inputRoot)},nodesListed={string.Join("|", childTypes)}";
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

    private static string DescribeItemUiContextDetailed()
    {
        object? itemUiContext = ResolveItemUiContextInstance();
        if (itemUiContext == null)
        {
            return "<none>";
        }

        object? shouldLock = InvokeShouldLockCursor(itemUiContext);
        string[] cursorFields = EnumerateFields(itemUiContext.GetType())
            .Where(IsCursorResultField)
            .Select(field => $"{field.Name}={SafeFieldValue(field, itemUiContext)}")
            .Take(8)
            .ToArray();
        return $"type={FormatTypeName(itemUiContext.GetType())},shouldLock={shouldLock ?? "<unknown>"},cursorFields={string.Join("|", cursorFields)}";
    }

    private static string DescribeCurrentScreen()
    {
        object? singleton = ResolveCurrentScreenSingleton();
        if (singleton == null)
        {
            return "screenSingleton=<none>";
        }

        object? root = ResolveMember(singleton, "RootScreenType");
        object? current = ResolveMember(singleton, "CurrentScreenController");
        object? currentType = current == null ? null : ResolveMember(current, "ScreenType");
        return $"root={root ?? "<null>"},current={currentType ?? "<null>"},controller={FormatTypeName(current?.GetType())}";
    }

    private static bool CurrentScreenLooksLikeBattleUi()
    {
        object? singleton = ResolveCurrentScreenSingleton();
        if (singleton == null)
        {
            return false;
        }

        string root = Convert.ToString(ResolveMember(singleton, "RootScreenType")) ?? string.Empty;
        object? current = ResolveMember(singleton, "CurrentScreenController");
        string currentType = Convert.ToString(current == null ? null : ResolveMember(current, "ScreenType")) ?? string.Empty;
        string controller = FormatTypeName(current?.GetType());
        string combined = (root + " " + currentType + " " + controller).ToLowerInvariant();
        return combined.Contains("battleui");
    }

    private static bool TryInvokeClose(object target)
    {
        try
        {
            MethodInfo? closeMethod = ResolveMethod(target.GetType(), "Close", 0);
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
            MethodInfo? method = ResolveMethod(target.GetType(), "ShouldLockCursor", 0);
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

    private static InputManagerSnapshot CaptureInputManagerSnapshot()
    {
        object? inputManager = ResolveInputManagerInstance();
        if (inputManager == null)
        {
            return InputManagerSnapshot.Missing();
        }

        FieldInfo? externalShowField = ResolveField(inputManager.GetType(), "bool_2");
        FieldInfo? cursorResultField = ResolveField(inputManager.GetType(), "ecursorResult_0");
        bool externalShowKnown = externalShowField != null && externalShowField.FieldType == typeof(bool);
        bool? externalShow = null;
        string cursorResult = "<unknown>";

        try
        {
            if (externalShowKnown)
            {
                object? rawExternalShow = externalShowField!.GetValue(inputManager);
                if (rawExternalShow is bool externalShowValue)
                {
                    externalShow = externalShowValue;
                }
            }
        }
        catch
        {
            externalShow = null;
        }

        try
        {
            if (cursorResultField != null)
            {
                cursorResult = Convert.ToString(cursorResultField.GetValue(inputManager)) ?? "<null>";
            }
        }
        catch
        {
            cursorResult = "<unreadable>";
        }

        return new InputManagerSnapshot
        {
            Available = true,
            TypeName = FormatTypeName(inputManager.GetType()),
            ExternalShowKnown = externalShowKnown,
            ExternalShow = externalShow,
            CursorResult = cursorResult,
        };
    }

    private static bool IsCursorResultField(FieldInfo field)
    {
        Type fieldType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        return fieldType.IsEnum && (string.Equals(fieldType.Name, "ECursorResult", StringComparison.Ordinal)
            || string.Equals(fieldType.FullName, "EFT.InputSystem.ECursorResult", StringComparison.Ordinal));
    }

    private static string SafeFieldValue(FieldInfo field, object target)
    {
        try
        {
            return Convert.ToString(field.GetValue(target)) ?? "<null>";
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static object? ResolveCurrentScreenSingleton()
    {
        Type? type = ResolveTypeByName("CurrentScreenSingletonClass");
        if (type == null)
        {
            return null;
        }

        return AccessTools.Property(type, "Instance")?.GetValue(null)
            ?? AccessTools.Field(type, "Instance")?.GetValue(null)
            ?? AccessTools.Property(type, "instance")?.GetValue(null)
            ?? AccessTools.Field(type, "instance")?.GetValue(null);
    }

    private static object? ResolveUiInputRoot()
    {
        Type? inputRootType = ResolveTypeByName("EFT.InputSystem.UIInputRoot") ?? ResolveTypeByName("UIInputRoot");
        return inputRootType == null ? null : UnityEngine.Object.FindObjectOfType(inputRootType);
    }

    private static object? ResolveInputManagerInstance()
    {
        Type? inputManagerType = ResolveTypeByName("EFT.InputSystem.InputManager") ?? ResolveTypeByName("InputManager");
        return inputManagerType == null ? null : UnityEngine.Object.FindObjectOfType(inputManagerType);
    }

    private static object? ResolveAbstractGameInstance()
    {
        Type? abstractGameType = ResolveTypeByName("EFT.AbstractGame") ?? ResolveTypeByName("AbstractGame");
        return abstractGameType == null ? null : UnityEngine.Object.FindObjectOfType(abstractGameType);
    }

    private static object? ResolveGlobalEventHandlerInstance()
    {
        Type? type = ResolveTypeByName("GlobalEventHandlerClass");
        if (type == null)
        {
            return null;
        }

        return AccessTools.Property(type, "Instance")?.GetValue(null)
            ?? AccessTools.Field(type, "Instance")?.GetValue(null)
            ?? AccessTools.Property(type, "instance")?.GetValue(null)
            ?? AccessTools.Field(type, "instance")?.GetValue(null);
    }

    private static object? ResolveItemUiContextInstance()
    {
        Type? itemUiContextType = ResolveTypeByName("EFT.UI.ItemUiContext") ?? ResolveTypeByName("ItemUiContext");
        if (itemUiContextType == null)
        {
            return null;
        }

        return AccessTools.Property(itemUiContextType, "Instance")?.GetValue(null)
            ?? AccessTools.Field(itemUiContextType, "Instance")?.GetValue(null)
            ?? AccessTools.Property(itemUiContextType, "instance")?.GetValue(null)
            ?? AccessTools.Field(itemUiContextType, "instance")?.GetValue(null);
    }

    private static IList? ResolveChildrenList(object inputNode)
    {
        FieldInfo? field = ResolveField(inputNode.GetType(), "_children");
        return field?.GetValue(inputNode) as IList;
    }

    private static object? ResolveStaticMemberValue(string typeName, string memberName)
    {
        try
        {
            Type? type = ResolveTypeByName(typeName);
            if (type == null)
            {
                return null;
            }

            PropertyInfo? property = AccessTools.Property(type, memberName)
                ?? type.GetProperty(memberName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(null);
            }

            FieldInfo? field = AccessTools.Field(type, memberName)
                ?? type.GetField(memberName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(null);
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

    private static MethodInfo? ResolveStaticMethod(Type? type, string name, int parameterCount)
    {
        while (type != null)
        {
            MethodInfo? method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
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
        return AccessTools.TypeByName(typeName)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(type => string.Equals(type.FullName, typeName, StringComparison.Ordinal)
                    || string.Equals(type.Name, typeName, StringComparison.Ordinal));
    }

    private static FieldInfo[] EnumerateFields(Type type)
    {
        return EnumerateTypes(type)
            .SelectMany(current => current.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .ToArray();
    }

    private static Type[] EnumerateTypes(Type type)
    {
        return type.EnumerateBaseTypes().ToArray();
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
        public int AlreadyInactive { get; init; }
        public int Released { get; init; }
        public int RemainingBlockers { get; init; }
        public string Candidates { get; init; } = string.Empty;

        public static InputNodeReleaseResult MissingInputRoot()
        {
            return new InputNodeReleaseResult
            {
                InputRootAvailable = false,
                BattleUi = true,
                Before = 0,
                After = 0,
                RemainingBlockers = 0,
                Candidates = string.Empty,
            };
        }

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
            return $"inputRoot={InputRootAvailable}; battleUi={BattleUi}; beforeNodes={Before}; afterNodes={After}; closeAttempts={CloseAttempts}; detached={Detached}; alreadyInactive={AlreadyInactive}; released={Released}; remainingInResult={RemainingBlockers}; candidates={Candidates}";
        }
    }

    private sealed class GameplayGateRestoreResult
    {
        public string OwnerType { get; init; } = "<unknown>";
        public int Applied { get; init; }
        public int Missing { get; init; }
        public int Attempts { get; init; }
        public int AppliedTotal { get; init; }

        public string Describe()
        {
            return $"gameplayGatesOwner={OwnerType}; gameplayGatesApplied={Applied}; gameplayGatesMissing={Missing}; gameplayGateAttempts={Attempts}; gameplayGateAppliedTotal={AppliedTotal}";
        }
    }

    private sealed class InputManagerSettleResult
    {
        public InputManagerSnapshot Before { get; init; } = InputManagerSnapshot.Missing();
        public InputManagerSnapshot After { get; init; } = InputManagerSnapshot.Missing();
        public string EventResult { get; init; } = "<none>";
        public string FieldResult { get; init; } = "<none>";
        public int Applied { get; init; }
        public int Attempts { get; init; }
        public int AppliedTotal { get; init; }

        public string Describe()
        {
            return $"inputManagerBefore={Before.Describe()}; inputManagerAfter={After.Describe()}; inputManagerEvent={EventResult}; inputManagerField={FieldResult}; inputManagerApplied={Applied}; inputManagerAttempts={Attempts}; inputManagerAppliedTotal={AppliedTotal}";
        }
    }

    private sealed class InputManagerSnapshot
    {
        public bool Available { get; init; }
        public string TypeName { get; init; } = "<missing>";
        public bool ExternalShowKnown { get; init; }
        public bool? ExternalShow { get; init; }
        public string CursorResult { get; init; } = "<unknown>";

        public static InputManagerSnapshot Missing()
        {
            return new InputManagerSnapshot
            {
                Available = false,
                TypeName = "<missing>",
                ExternalShowKnown = false,
                ExternalShow = null,
                CursorResult = "<missing>",
            };
        }

        public string Describe()
        {
            return $"available={Available},type={TypeName},externalShowKnown={ExternalShowKnown},externalShow={ExternalShow?.ToString() ?? "<unknown>"},cursorResult={CursorResult}";
        }
    }

#else
    public static void RequestForRaidStart(string source, string reason) { }
    public static void NotifyBattleUiShown(string source) { }
    public static void Tick() { }
#endif
}

#if SPT_CLIENT
internal static class VanguardBattleInputNodeReleaseTypeExtensions
{
    public static System.Collections.Generic.IEnumerable<Type> EnumerateBaseTypes(this Type type)
    {
        Type? current = type;
        while (current != null)
        {
            yield return current;
            current = current.BaseType;
        }
    }
}
#endif

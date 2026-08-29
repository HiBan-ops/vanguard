#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Selects the concrete EFT medical item/action that can satisfy an already-qualified Operator medical need.
// Flow: Canonical medical need, body-part target, inventory capabilities and procedure constraints are matched to candidate items; the selector ranks compatible actions and returns a bounded execution choice to the medical executor.
// Authority boundary: Selection does not create medical need or use the item; canonical medical truth and the executor safety/commit path remain authoritative.
// Invariant: Only currently available compatible items are selected, surgery/fracture/heal roles are not conflated, and no selection is returned when the need is presently unrealizable.
namespace Vanguard.Client.Runtime.Medical.Execution;

internal sealed class VanguardMobileMedicalActionSelection
{
    public VanguardMedicalNeed Need { get; init; }
    public EBodyPart TargetPart { get; init; }
    public string TargetPartName { get; init; } = "none";
    public string ItemTemplateId { get; init; } = "none";
    public string ItemInstanceId { get; init; } = "none";
    public float ItemResource { get; init; } = -1f;
    public float ItemMaxResource { get; init; } = -1f;
    public string ItemName { get; init; } = "none";
    public MedsItemClass Item { get; init; } = null!;
    public bool RequiresStationary { get; init; }
    public bool MovementAllowed { get; init; } = true;
    public bool FollowAllowed { get; init; } = true;
    public string ExecutionLane { get; init; } = "mobile_medical";

    public string Summary => "need=" + Need
        + ";target=" + Safe(TargetPartName)
        + ";item=" + Safe(ItemName)
        + ";tpl=" + Safe(ItemTemplateId)
        + ";itemInstance=" + Safe(ItemInstanceId)
        + ";itemResource=" + ItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + "/" + ItemMaxResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + ";lane=" + Safe(ExecutionLane)
        + ";stationary=" + Bool(RequiresStationary)
        + ";movementAllowed=" + Bool(MovementAllowed);

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}

internal static class VanguardMobileMedicalActionSelector
{
    private const string SainLikeSurgerySafetyStatusTag = "VANGUARD_SAIN_LIKE_SURGERY_SAFETY_OK";
    private const string SelectorLogCompatStatusTag = "VANGUARD_SELECTOR_LOG_COMPAT_OK";
    private const string SurgeryCoverCompletionGuardStatusTag = "VANGUARD_SURGERY_COVER_COMPLETION_GUARD_OK";
    private static readonly object SurgeryLogSync = new();
    private static readonly Dictionary<string, string> LastSurgeryAreaStateByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> LastSurgeryAreaLogAtByBot = new(StringComparer.OrdinalIgnoreCase);
    public static bool TrySelect(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, out VanguardMobileMedicalActionSelection selection, out string reason)
    {
        return TrySelect(botOwner, snapshot, null, out selection, out reason);
    }

    public static bool TrySelect(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, ISet<string>? excludedItemInstanceIds, out VanguardMobileMedicalActionSelection selection, out string reason)
    {
        selection = null!;
        reason = "none";

        if (botOwner == null)
        {
            reason = "botowner_null";
            return false;
        }

        if (!snapshot.Alive)
        {
            reason = "operator_dead";
            return false;
        }

        var need = snapshot.Medical.Need;
        var actionability = snapshot.Medical.Actionability;
        var safety = snapshot.Medical.Safety;
        var plan = snapshot.Medical.Plan;

        bool activeMobileNeed = need.DominantNeed == VanguardMedicalNeed.HeavyBleed
            || need.DominantNeed == VanguardMedicalNeed.LightBleed
            || need.DominantNeed == VanguardMedicalNeed.HpHeal;
        bool activeStationaryFracture = need.DominantNeed == VanguardMedicalNeed.Fracture;
        bool activeStationarySurgery = need.DominantNeed == VanguardMedicalNeed.SurgeryDestroyedPart
            || need.DominantNeed == VanguardMedicalNeed.BlackBroken;

        if (!activeMobileNeed && !activeStationaryFracture && !activeStationarySurgery)
        {
            reason = "need_not_active_medical_scope";
            return false;
        }

        if (activeStationaryFracture || activeStationarySurgery)
        {
            if (!plan.WouldRequireStationary || plan.WouldAllowMobile || plan.WouldWait || !plan.WouldExecuteIfActive)
            {
                reason = activeStationarySurgery ? "medical_plan_not_active_stationary_surgery_ready" : "medical_plan_not_active_stationary_fracture_ready";
                return false;
            }

            if (activeStationarySurgery)
            {
                if (!IsStationarySurgerySafe(snapshot, out var surgerySafetyReason))
                {
                    reason = "stationary_surgery_safety_gate_blocked:" + surgerySafetyReason;
                    return false;
                }
            }
            else if (!IsStationaryFractureSafe(safety, out var safetyReason))
            {
                reason = "stationary_fracture_safety_gate_blocked:" + safetyReason;
                return false;
            }
        }
        else
        {
            if (!plan.WouldAllowMobile || plan.WouldRequireStationary || plan.WouldWait || !plan.WouldExecuteIfActive)
            {
                reason = "medical_plan_not_active_mobile_ready";
                return false;
            }

            if (!safety.SafeForMobileAid || safety.EnemyCanShoot || safety.ImmediateCombatBlock || (safety.ThreatScanWouldPromote && !safety.CoveredSuppressionOpportunity))
            {
                reason = "mobile_medical_safety_gate_blocked";
                return false;
            }
        }

        if (!IsExecutionControllerReady(actionability, requireSnapshotCanApply: true, out reason))
        {
            return false;
        }

        if (!TryResolveTarget(actionability, expectedTargetPartName: null, out var targetPart, out reason))
        {
            return false;
        }

        return TrySelectApplicableItem(
            botOwner,
            need.DominantNeed,
            targetPart,
            excludedItemInstanceIds,
            activeStationaryFracture,
            activeStationarySurgery,
            activeStationarySurgery ? "stationary_surgery" : activeStationaryFracture ? "stationary_fracture" : "mobile_medical",
            out selection,
            out reason);
    }

    public static bool TrySelectPreparedSurgery(BotOwner? botOwner, OperatorDecisionSnapshot snapshot, string expectedTargetPartName, out VanguardMobileMedicalActionSelection selection, out string reason)
    {
        selection = null!;
        reason = "none";
        if (botOwner == null)
        {
            reason = "botowner_null";
            return false;
        }

        if (!snapshot.Alive)
        {
            reason = "operator_dead";
            return false;
        }

        var need = snapshot.Medical.Need;
        var actionability = snapshot.Medical.Actionability;
        bool activeStationarySurgery = need.DominantNeed == VanguardMedicalNeed.SurgeryDestroyedPart
            || need.DominantNeed == VanguardMedicalNeed.BlackBroken;
        if (!activeStationarySurgery)
        {
            reason = "need_not_stationary_surgery";
            return false;
        }

        // The prepare lease already owns the patient and has completed the cover contract. Do not
        // re-evaluate MedicalPlan.WouldExecuteIfActive here: that plan may still describe the
        // prepare phase. Safety, hands idleness, exact target and exact item remain mandatory.
        if (!IsStationarySurgerySafe(snapshot, out var surgerySafetyReason))
        {
            reason = "stationary_surgery_safety_gate_blocked:" + surgerySafetyReason;
            return false;
        }

        // Runtime invariant: once Vanguard owns a committed surgery posture, the read-only snapshot
        // CanApply flag can lag behind the live EFT inventory/controller state. Keep all
        // hands, target and item gates, but let TrySelectApplicableItem perform the
        // authoritative HealthController.CanApplyItem probe on the exact refreshed item.
        if (!IsExecutionControllerReady(actionability, requireSnapshotCanApply: false, out reason))
        {
            return false;
        }

        if (!TryResolveTarget(actionability, expectedTargetPartName, out var targetPart, out reason))
        {
            return false;
        }

        return TrySelectApplicableItem(
            botOwner,
            need.DominantNeed,
            targetPart,
            excludedItemInstanceIds: null,
            requiresStationaryFracture: false,
            requiresStationarySurgery: true,
            executionLane: "stationary_surgery_direct_chain",
            out selection,
            out reason);
    }

    private static bool IsExecutionControllerReady(VanguardMedicalActionabilitySnapshot actionability, bool requireSnapshotCanApply, out string reason)
    {
        if (actionability.AnyMedicineUsing || actionability.FirstAidUsing || actionability.SurgicalKitUsing || actionability.StimulatorUsing)
        {
            reason = "medicine_controller_busy";
            return false;
        }

        if (actionability.Reloading)
        {
            reason = "blocked_reloading";
            return false;
        }

        if (actionability.GrenadeThrowing)
        {
            reason = "blocked_grenade";
            return false;
        }

        if (!actionability.RequiredItemAvailable || string.IsNullOrWhiteSpace(actionability.SelectedItemTemplateId) || actionability.SelectedItemTemplateId == "none")
        {
            reason = "selected_item_missing";
            return false;
        }

        if (requireSnapshotCanApply && actionability.CanApplyItem != true)
        {
            reason = "can_apply_not_true";
            return false;
        }

        reason = "controller_ready";
        return true;
    }

    private static bool TryResolveTarget(VanguardMedicalActionabilitySnapshot actionability, string? expectedTargetPartName, out EBodyPart targetPart, out string reason)
    {
        targetPart = default;
        if (!actionability.TargetKnown || !Enum.TryParse(actionability.TargetPart, ignoreCase: true, out targetPart))
        {
            reason = "target_part_unknown";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedTargetPartName)
            && !string.Equals(targetPart.ToString(), expectedTargetPartName, StringComparison.OrdinalIgnoreCase))
        {
            reason = "prepared_target_changed:expected=" + Safe(expectedTargetPartName) + ":current=" + Safe(targetPart.ToString());
            return false;
        }

        reason = "target_resolved";
        return true;
    }

    private static bool TrySelectApplicableItem(
        BotOwner botOwner,
        VanguardMedicalNeed need,
        EBodyPart targetPart,
        ISet<string>? excludedItemInstanceIds,
        bool requiresStationaryFracture,
        bool requiresStationarySurgery,
        string executionLane,
        out VanguardMobileMedicalActionSelection selection,
        out string reason)
    {
        selection = null!;
        long selectorInventoryStarted = VanguardRuntimePerformanceGuard.Begin();
        var inventory = VanguardMedicalInventoryReader.Capture(botOwner);
        VanguardRuntimePerformanceGuard.End("MedicalAdmissionSelectorInventoryCapture", selectorInventoryStarted);
        MedsItemClass? item = null;
        VanguardMedicalItemCapability? selectedCapability = null;
        string itemInstanceId = "none";
        float itemResource = -1f;
        float itemMaxResource = -1f;

        foreach (var capability in VanguardMedicalItemCapabilityResolver.GetCandidates(need))
        {
            if (!inventory.ItemsByTemplateId.TryGetValue(capability.TemplateId, out var templateItems))
            {
                continue;
            }

            foreach (var candidateItem in templateItems)
            {
                string candidateInstanceId = VanguardMedicalInventoryReader.ResolveItemInstanceId(candidateItem);
                if (excludedItemInstanceIds != null && excludedItemInstanceIds.Contains(candidateInstanceId))
                {
                    continue;
                }

                float candidateResource = VanguardMedicalInventoryReader.ReadItemResource(candidateItem);
                float candidateMaxResource = VanguardMedicalInventoryReader.ReadItemMaxResource(candidateItem);
                if (candidateMaxResource > 0f && candidateResource <= 0.01f)
                {
                    continue;
                }

                try
                {
                    long canApplyStarted = VanguardRuntimePerformanceGuard.Begin();
                    bool canApply;
                    try
                    {
                        canApply = botOwner.GetPlayer?.HealthController?.CanApplyItem(candidateItem, targetPart) == true;
                    }
                    finally
                    {
                        VanguardRuntimePerformanceGuard.End("MedicalAdmissionSelectorCanApplyItem", canApplyStarted);
                    }

                    if (!canApply)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                item = candidateItem;
                selectedCapability = capability;
                itemInstanceId = candidateInstanceId;
                itemResource = candidateResource;
                itemMaxResource = candidateMaxResource;
                break;
            }

            if (item != null)
            {
                break;
            }
        }

        if (item == null || selectedCapability == null)
        {
            reason = excludedItemInstanceIds != null && excludedItemInstanceIds.Count > 0
                ? "no_viable_alternative_item_instance"
                : "selected_item_not_found_or_not_applicable_in_inventory_refresh";
            return false;
        }

        selection = new VanguardMobileMedicalActionSelection
        {
            Need = need,
            TargetPart = targetPart,
            TargetPartName = targetPart.ToString(),
            ItemTemplateId = selectedCapability.TemplateId,
            ItemInstanceId = itemInstanceId,
            ItemResource = itemResource,
            ItemMaxResource = itemMaxResource,
            ItemName = selectedCapability.Name,
            Item = item,
            RequiresStationary = requiresStationaryFracture || requiresStationarySurgery,
            MovementAllowed = !requiresStationaryFracture && !requiresStationarySurgery,
            FollowAllowed = !requiresStationaryFracture && !requiresStationarySurgery,
            ExecutionLane = executionLane
        };
        reason = "selected";
        return true;
    }


    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
    }

    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";

    private static bool IsStationarySurgerySafe(OperatorDecisionSnapshot snapshot, out string reason)
    {
        var safety = snapshot.Medical.Safety;
        // Runtime invariant: CMS/Surv12 surgery is stricter than stationary aid. It must
        // not start unless the Operator is already stabilized in SAIN cover/hold
        // or in a short Vanguard-owned surgery cover slot grant, with no ORBIT,
        // loot, or movement interaction still active. The launch gate remains strict.
        if (!safety.SurgeryAreaClear || !safety.SafeForStationarySurgery)
        {
            reason = "sain_like_surgery_area_not_clear:" + Safe(safety.SurgeryAreaClearReason);
            LogSurgeryAreaCheck(snapshot, false, reason);
            return false;
        }

        bool vanguardCoverGranted = VanguardSurgeryCoverPrepareExecutor.HasRecentVanguardSurgeryCoverGrant(snapshot, out var vanguardGrantReason);
        if (!vanguardCoverGranted)
        {
            reason = "await_vanguard_cover_commit:" + Safe(vanguardGrantReason);
            LogSurgeryAreaCheck(snapshot, false, reason);
            return false;
        }

        if (IsPreStartStationarySurgeryBlocked(snapshot, out var preStartReason))
        {
            reason = "await_stationary_idle:" + preStartReason;
            LogSurgeryAreaCheck(snapshot, false, reason);
            return false;
        }

        reason = vanguardCoverGranted
            ? (string.IsNullOrWhiteSpace(safety.SurgeryAreaClearReason) ? "vanguard_cover_slot_ready" : safety.SurgeryAreaClearReason + "+vanguard_cover_slot_ready")
            : (string.IsNullOrWhiteSpace(safety.SurgeryAreaClearReason) ? "sain_like_surgery_area_clear_cover_hold_ready" : safety.SurgeryAreaClearReason + "+cover_hold_ready");
        LogSurgeryAreaCheck(snapshot, true, reason);
        return true;
    }

    private static bool IsPreStartStationarySurgeryBlocked(OperatorDecisionSnapshot snapshot, out string reason)
    {
        reason = "none";

        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        if (speed > 0.35f)
        {
            reason = "movement_speed_gt_0_35";
            return true;
        }

        if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            reason = "loot_active_before_surgery";
            return true;
        }

        string orbit = (snapshot.Orbit.Status + "|" + snapshot.Orbit.Category + "|" + snapshot.Orbit.ExtractReason).ToLowerInvariant();
        if (snapshot.Orbit.Active && (orbit.Contains("loot") || orbit.Contains("moving") || orbit.Contains("orbit_moving")))
        {
            reason = "orbit_active_before_surgery";
            return true;
        }

        string state = snapshot.Movement.PlayerState ?? string.Empty;
        if (state.IndexOf("DoorInteraction", StringComparison.OrdinalIgnoreCase) >= 0
            || state.IndexOf("Loot", StringComparison.OrdinalIgnoreCase) >= 0
            || state.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            reason = "player_state_not_stationary_before_surgery";
            return true;
        }

        return false;
    }

    private static void LogSurgeryAreaCheck(OperatorDecisionSnapshot snapshot, bool allowed, string reason)
    {
        var safety = snapshot.Medical.Safety;
        string stateKey = (allowed ? "allowed" : "waiting") + ":" + Safe(reason) + ":" + Safe(safety.SurgeryAreaClearReason);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (SurgeryLogSync)
        {
            bool unchanged = LastSurgeryAreaStateByBot.TryGetValue(snapshot.BotProfileId, out var previousState)
                && string.Equals(previousState, stateKey, StringComparison.OrdinalIgnoreCase);
            bool recent = LastSurgeryAreaLogAtByBot.TryGetValue(snapshot.BotProfileId, out var previousAt)
                && now - previousAt < TimeSpan.FromSeconds(5.0d);
            if (unchanged && recent)
            {
                return;
            }

            LastSurgeryAreaStateByBot[snapshot.BotProfileId] = stateKey;
            LastSurgeryAreaLogAtByBot[snapshot.BotProfileId] = now;
        }

        float speed = Math.Max(snapshot.RealSpeed, snapshot.Movement.RealSpeed);
        string logKind = allowed ? "VANGUARD_SURGERY_AREA_CLEAR_CHECK" : "VANGUARD_SURGERY_AWAIT_COVER";
        bool stationaryIdle = !IsPreStartStationarySurgeryBlocked(snapshot, out _);
        bool vanguardCoverGrant = VanguardSurgeryCoverPrepareExecutor.HasRecentVanguardSurgeryCoverGrant(snapshot, out var grantReason);
        VanguardClientDiagnosticsLog.Info(
            SurgeryCoverCompletionGuardStatusTag,
            $"{logKind} allowed={Bool(allowed)}; areaClear={Bool(safety.SurgeryAreaClear)}; reason={Safe(reason)}; surgeryReason={Safe(safety.SurgeryAreaClearReason)}; requiresCover=true; coverOrHold={Bool(safety.CoveredOrHoldingAngle)}; vanguardCoverGrant={Bool(vanguardCoverGrant)}; vanguardGrantReason={Safe(grantReason)}; stationaryIdle={Bool(stationaryIdle)}; speed={speed:0.00}; lootBot={Tri(snapshot.Looting.BotLooting)}; lootTask={Tri(snapshot.Looting.LootTaskRunning)}; orbitActive={Bool(snapshot.Orbit.Active)}; orbitStatus={Safe(snapshot.Orbit.Status)}; orbitCategory={Safe(snapshot.Orbit.Category)}; enemyVisible={Bool(safety.EnemyVisible)}; enemyCanShoot={Bool(safety.EnemyCanShoot)}; incomingFire={Bool(safety.IncomingFireRecent)}; seenRecent={Bool(safety.SurgeryThreatRecentlySeen)}; knownRecent={Bool(safety.SurgeryThreatRecentlyKnown)}; pathTooClose={Bool(safety.SurgeryThreatPathTooClose)}; distanceTooClose={Bool(safety.SurgeryThreatDistanceTooClose)}; scanPromote={Bool(safety.ThreatScanWouldPromote)}; tag={SainLikeSurgerySafetyStatusTag}; Tag={SurgeryCoverCompletionGuardStatusTag}; compatTag={SelectorLogCompatStatusTag}");
    }

    private static bool IsStationaryFractureSafe(VanguardMedicalSafetySnapshot safety, out string reason)
    {
        if (!safety.SafeForStationaryAid)
        {
            reason = "stationary_aid_window_false:" + safety.Reason;
            return false;
        }

        if (safety.EnemyCanShoot || safety.ImmediateCombatBlock)
        {
            reason = "enemy_can_shoot_or_immediate_block";
            return false;
        }

        if (safety.IncomingFireRecent && !safety.CoveredOrHoldingAngle)
        {
            reason = "incoming_fire_without_cover_or_hold_angle";
            return false;
        }

        if (safety.ThreatScanWouldPromote && !safety.CoveredOrHoldingAngle)
        {
            reason = "scan_promote_without_cover_or_hold_angle";
            return false;
        }

        reason = string.IsNullOrWhiteSpace(safety.Reason) ? "stationary_fracture_safe" : safety.Reason;
        return true;
    }
}
#endif

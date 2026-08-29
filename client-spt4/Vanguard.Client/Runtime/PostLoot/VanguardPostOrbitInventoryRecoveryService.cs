#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Medical;
using Vanguard.Client.Runtime.Medical.Execution;

// Responsibility: Reconciles Operator inventory/hands after external loot activity so Vanguard does not resume combat with stale equipment assumptions.
// Flow: After an external loot boundary, live inventory and hands are re-read, weapon readiness is checked, bounded recovery is attempted when native state is clearly incomplete, and downstream snapshots are refreshed.
// Authority boundary: EFT inventory/hands remain physical truth and external looting mods own their own transaction; Vanguard only repairs/re-reads its post-loot view.
// Invariant: Recovery must never duplicate items or override a valid native controller, and it must stop once current inventory/hands are coherent.
namespace Vanguard.Client.Runtime.PostLoot;

internal static class VanguardPostOrbitInventoryRecoveryService
{
    public const string StatusTag = "VANGUARD_POST_ORBIT_INVENTORY_RECOVERY_OK";
    public const string InventoryRefreshStatusTag = "VANGUARD_MEDICAL_INVENTORY_REFRESH_OK";

    private static readonly TimeSpan RecentLootWindow = TimeSpan.FromSeconds(30.0d);
    private static readonly TimeSpan HeavyBleedInventorySettle = TimeSpan.FromSeconds(1.25d);
    private static readonly TimeSpan MobileInventorySettle = TimeSpan.FromSeconds(2.75d);
    private static readonly TimeSpan StationaryInventorySettle = TimeSpan.FromSeconds(4.50d);
    private static readonly TimeSpan NoEffectRecoverySettle = TimeSpan.FromSeconds(3.50d);
    private static readonly TimeSpan GhostUsingMinDuration = TimeSpan.FromSeconds(4.25d);
    private static readonly TimeSpan GhostRecoverCooldown = TimeSpan.FromSeconds(10.0d);
    private static readonly TimeSpan PeriodicInventoryRefreshInterval = TimeSpan.FromSeconds(30.0d);
    private static readonly TimeSpan InventoryRefreshCooldown = TimeSpan.FromSeconds(2.0d);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(4.0d);

    private static readonly Dictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);
    private static bool bootLogged;

    public static void Reset(string reason)
    {
        bootLogged = false;
        States.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag, $"post-ORBIT inventory recovery reset reason={Safe(reason)}; scope=inventory_controller_medical_item_addresses_backpack_rig_pockets_slots; mutatesMedical=guarded_ghost_recover_and_cache_refresh; mutatesInventory=medical_cache_refresh_only; mutatesLoot=false; mutatesSain=false; refreshTag={InventoryRefreshStatusTag}; tag={StatusTag}");
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> snapshots)
    {
        LogBootOnce();
        var now = DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            {
                continue;
            }

            try
            {
                if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(snapshot.BotProfileId, out var record) || record.BotOwner == null)
                {
                    continue;
                }

                ObserveSnapshot(record.BotOwner, snapshot, now);
                TryApplyScheduledInventoryRefresh(record.BotOwner, snapshot, GetState(snapshot.BotProfileId), now, "tick", force: false);
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(StatusTag, $"post-orbit inventory recovery tick failed operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    public static bool ShouldDelayMedicalStart(BotOwner botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now, out string reason, out string summary)
    {
        reason = "none";
        summary = "postOrbitInventory=not_tracked";
        if (string.IsNullOrWhiteSpace(snapshot.BotProfileId) || !snapshot.Medical.Need.HasAnyNeed)
        {
            return false;
        }

        var state = GetState(snapshot.BotProfileId);
        ObserveSnapshot(botOwner, snapshot, now);
        summary = BuildStateSummary(state, now, CaptureInventorySignature(botOwner, snapshot));

        if (!IsRecentPostLoot(state, now) && state.LastNoEffectAtUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        bool itemControllerRejectedAfterLoot = snapshot.Medical.Actionability.CanApplyItem == false
            && IsRecentPostLoot(state, now)
            && (IsRecent(now, state.LastInventoryMutationAtUtc, RecentLootWindow) || IsRecent(now, state.LastLootEndedAtUtc, RecentLootWindow));
        if (itemControllerRejectedAfterLoot)
        {
            TryApplyScheduledInventoryRefresh(botOwner, snapshot, state, now, "pre_medical_controller_rejected", force: true);
            summary = BuildStateSummary(state, now, CaptureInventorySignature(botOwner, snapshot));
            reason = "post_orbit_controller_rejected_recheck";
            LogDelay(state, snapshot, now, reason, summary);
            return true;
        }

        if (snapshot.Medical.Actionability.AnyMedicineUsing && !snapshot.Medical.Plan.WouldExecuteIfActive)
        {
            TryApplyScheduledInventoryRefresh(botOwner, snapshot, state, now, "pre_medical_controller_busy", force: true);
            summary = BuildStateSummary(state, now, CaptureInventorySignature(botOwner, snapshot));
            reason = "post_orbit_medical_controller_busy_recheck";
            LogDelay(state, snapshot, now, reason, summary);
            return true;
        }

        var settle = SettleWindowFor(snapshot.Medical.Need.DominantNeed);
        if (state.LastInventoryMutationAtUtc != DateTimeOffset.MinValue && now - state.LastInventoryMutationAtUtc < settle)
        {
            reason = "post_orbit_inventory_settling";
            LogDelay(state, snapshot, now, reason, summary);
            return true;
        }

        if (state.LastNoEffectAtUtc != DateTimeOffset.MinValue && now - state.LastNoEffectAtUtc < NoEffectRecoverySettle)
        {
            reason = "post_orbit_no_effect_recovery_cooldown";
            LogDelay(state, snapshot, now, reason, summary);
            return true;
        }

        return false;
    }

    public static bool ShouldAbortMedicalGhostUse(
        VanguardExecutionLeaseState lease,
        BotOwner? botOwner,
        OperatorDecisionSnapshot snapshot,
        VanguardMedicalActionProgressSnapshot progress,
        DateTimeOffset now,
        out string reason,
        out string summary)
    {
        reason = "none";
        summary = "postOrbitInventory=not_tracked";
        if (botOwner == null || string.IsNullOrWhiteSpace(lease.BotProfileId) || !progress.FirstAidUsing || !progress.NoMedicalEffectObserved)
        {
            return false;
        }

        var state = GetState(lease.BotProfileId);
        ObserveSnapshot(botOwner, snapshot, now);
        summary = BuildStateSummary(state, now, CaptureInventorySignature(botOwner, snapshot));

        if (!IsRecentPostLoot(state, now) && state.LastNoEffectAtUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        if (!lease.ItemUseObserved)
        {
            return false;
        }

        if (now - lease.StartedAtUtc < GhostUsingMinDuration)
        {
            return false;
        }

        if (state.LastGhostRecoverAttemptAtUtc != DateTimeOffset.MinValue && now - state.LastGhostRecoverAttemptAtUtc < GhostRecoverCooldown)
        {
            return false;
        }

        bool controllerSuspicious = snapshot.Medical.Actionability.CanApplyItem == false
            || snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.FirstAidUsing
            || snapshot.Medical.Actionability.SurgicalKitUsing
            || state.LastInventoryMutationAtUtc != DateTimeOffset.MinValue;
        if (!controllerSuspicious)
        {
            return false;
        }

        reason = "PostOrbitMedicalGhostUseNoEffect";
        return true;
    }

    public static void MarkNoMedicalEffect(VanguardExecutionLeaseState lease, VanguardMedicalActionProgressSnapshot? progress, DateTimeOffset now, string reason, bool requestBoundedRetryRefresh)
    {
        if (string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return;
        }

        var state = GetState(lease.BotProfileId);
        bool recentPostLoot = IsRecentPostLoot(state, now)
            || IsRecent(now, state.LastInventoryMutationAtUtc, RecentLootWindow)
            || IsRecent(now, state.LastLootEndedAtUtc, RecentLootWindow);

        // A native cycle that visibly ended without changing the medical state is a controller/cache
        // recovery signal even when no recent loot is involved (notably for Fika remote-owner
        // Operators). Schedule one cache/controller refresh before the exact-item retry; the
        // execution circuit then blocks that instance if the bounded retry also has no effect.
        state.LastNoEffectAtUtc = now;
        state.LastNoEffectReason = Safe(reason);
        state.LastNoEffectLease = Safe(lease.IntentKey) + ":" + Safe(lease.MedicalNeed.ToString()) + ":" + Safe(lease.TargetPart);
        if (requestBoundedRetryRefresh)
        {
            RequestInventoryRefresh(state, now, "native_medical_cycle_no_effect_before_bounded_retry");
        }
        VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_POST_ORBIT_MEDICAL_NO_EFFECT operator={lease.OperatorId}; botProfile={lease.BotProfileId}; reason={Safe(reason)}; lease={Safe(lease.LeaseId)}; need={lease.MedicalNeed}; target={Safe(lease.TargetPart)}; item={Safe(lease.ItemName)}; tpl={Safe(lease.ItemTemplateId)}; effect={(progress == null ? "unknown" : progress.EffectSummary)}; recentPostLoot={Bool(recentPostLoot)}; nextMedicalRecheckDelay={NoEffectRecoverySettle.TotalSeconds:0.00}; boundedRetryRefreshRequested={Bool(requestBoundedRetryRefresh)}; mutatesMedical=false; mutatesInventory={(requestBoundedRetryRefresh ? "refresh_requested_once_before_exact_item_retry" : "none_circuit_terminal_or_nonretry")}; refreshTag={InventoryRefreshStatusTag}; tag={StatusTag}");
    }

    public static bool TryRecoverMedicalGhostUse(BotOwner? botOwner, VanguardExecutionLeaseState lease, DateTimeOffset now, string reason, out string recoverySummary)
    {
        recoverySummary = "recover=false";
        if (botOwner == null || string.IsNullOrWhiteSpace(lease.BotProfileId))
        {
            return false;
        }

        var state = GetState(lease.BotProfileId);
        if (state.LastGhostRecoverAttemptAtUtc != DateTimeOffset.MinValue && now - state.LastGhostRecoverAttemptAtUtc < GhostRecoverCooldown)
        {
            recoverySummary = "recover=false;reason=ghost_recovery_cooldown";
            return false;
        }

        state.LastGhostRecoverAttemptAtUtc = now;
        object? controller = IsSurgeryNeed(lease.MedicalNeed)
            ? VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "SurgicalKit")
            : VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "FirstAid");
        bool timeoutCancel = !IsSurgeryNeed(lease.MedicalNeed) && InvokeNoArg(controller, "method_4");
        bool refreshed = InvokeNoArg(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Medecine"), "RefreshCurMeds")
            || InvokeNoArg(controller, "Refresh")
            || InvokeNoArg(controller, "RefreshMeds");
        bool weaponRestored = InvokeNoArg(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "WeaponManager", "Selector"), "TakePrevWeapon");
        recoverySummary = $"recover=true;timeoutCancel={Bool(timeoutCancel)};refresh={Bool(refreshed)};takePrevWeapon={Bool(weaponRestored)};surgery={Bool(IsSurgeryNeed(lease.MedicalNeed))}";
        VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_POST_ORBIT_MEDICAL_GHOST_RECOVERY operator={lease.OperatorId}; botProfile={lease.BotProfileId}; reason={Safe(reason)}; {recoverySummary}; mutatesMedical=true; mutatesInventory=false; mutatesLoot=false; mutatesSain=false; refreshTag={InventoryRefreshStatusTag}; tag={StatusTag}");
        return timeoutCancel || refreshed || weaponRestored;
    }

    private static void ObserveSnapshot(BotOwner botOwner, OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        var state = GetState(snapshot.BotProfileId);
        bool lootActive = IsLootOrOrbitActive(snapshot);
        if (lootActive)
        {
            state.LastLootActiveAtUtc = now;
        }
        else if (state.WasLootActive)
        {
            state.LastLootEndedAtUtc = now;
            state.LastInventoryMutationAtUtc = now;
            state.LastInventoryMutationReason = "loot_or_orbit_ended";
            RequestInventoryRefresh(state, now, "loot_or_orbit_ended");
        }
        state.WasLootActive = lootActive;

        var signature = CaptureInventorySignature(botOwner, snapshot);
        if (string.IsNullOrWhiteSpace(state.LastInventorySignature))
        {
            state.LastInventorySignature = signature.Signature;
            return;
        }

        if (!string.Equals(state.LastInventorySignature, signature.Signature, StringComparison.Ordinal))
        {
            bool recentLoot = IsRecentPostLoot(state, now) || lootActive;
            state.LastInventorySignature = signature.Signature;
            if (recentLoot)
            {
                state.LastInventoryMutationAtUtc = now;
                state.LastInventoryMutationReason = "inventory_signature_changed_after_orbit_loot";
                RequestInventoryRefresh(state, now, state.LastInventoryMutationReason);
                if (ShouldLog(state, now, "mutation|" + signature.Signature))
                {
                    VanguardClientDiagnosticsLog.Warning(StatusTag, $"VANGUARD_POST_ORBIT_INVENTORY_MUTATION operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; nick={Safe(snapshot.Nickname)}; reason={state.LastInventoryMutationReason}; loot={Safe(snapshot.Looting.Classification)}; orbit={Safe(snapshot.Orbit.Classification)}; medNeed={snapshot.Medical.Need.DominantNeed}; medItem={Safe(snapshot.Medical.Actionability.SelectedItemName)}; medCanApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; {signature.Summary}; settleHeavy={HeavyBleedInventorySettle.TotalSeconds:0.00}; settleMobile={MobileInventorySettle.TotalSeconds:0.00}; settleStationary={StationaryInventorySettle.TotalSeconds:0.00}; mutatesInventory=refresh_requested; refreshTag={InventoryRefreshStatusTag}; tag={StatusTag}");
                }
            }
        }
    }

    private static InventorySignature CaptureInventorySignature(BotOwner botOwner, OperatorDecisionSnapshot snapshot)
    {
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? inventory = VanguardOperatorRuntimeAuditReflection.GetMember(player, "Inventory", "InventoryController", "ProfileInventory");
        object? equipment = VanguardOperatorRuntimeAuditReflection.GetMember(inventory, "Equipment", "EquipmentContainer", "EquipmentSlots");
        string backpack = SlotSignature(equipment, "Backpack");
        string rig = SlotSignature(equipment, "TacticalVest");
        string pockets = SlotSignature(equipment, "Pockets");
        string secured = SlotSignature(equipment, "SecuredContainer");
        string medical = Safe(snapshot.Medical.Inventory.AcceptableItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + ":" + Safe(snapshot.Medical.Inventory.MedicalTemplateCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + ":" + Safe(snapshot.Medical.Inventory.CandidateTemplateIds)
            + ":" + Safe(snapshot.Medical.Actionability.SelectedItemTemplateId)
            + ":" + Safe(snapshot.Medical.Actionability.TargetPart)
            + ":" + Tri(snapshot.Medical.Actionability.CanApplyItem);
        string hands = Safe(VanguardOperatorRuntimeAuditReflection.TypeName(VanguardOperatorRuntimeAuditReflection.GetMember(player, "HandsController", "_handsController", "handsController")));
        string signature = string.Join("|", backpack, rig, pockets, secured, medical, hands);
        return new InventorySignature
        {
            Signature = signature,
            Summary = $"inventorySignature={Safe(signature)};backpack={Safe(backpack)};rig={Safe(rig)};pockets={Safe(pockets)};secured={Safe(secured)};medicalTemplates={Safe(snapshot.Medical.Inventory.CandidateTemplateIds)};medicalNames={Safe(snapshot.Medical.Inventory.CandidateNames)};hands={hands}"
        };
    }

    private static string SlotSignature(object? equipment, string slotName)
    {
        object? slot = VanguardOperatorRuntimeAuditReflection.GetMember(equipment, slotName, slotName + "Slot", slotName + "Item");
        object? contained = VanguardOperatorRuntimeAuditReflection.GetMember(slot, "ContainedItem", "Item", "FirstItem", "ParentItem");
        object? item = contained ?? slot;
        string type = VanguardOperatorRuntimeAuditReflection.TypeName(item);
        string id = Safe(VanguardOperatorRuntimeAuditReflection.Text(VanguardOperatorRuntimeAuditReflection.GetMember(item, "Id", "_id")));
        string tpl = TemplateId(item);
        string grids = VanguardOperatorRuntimeAuditReflection.CountText(VanguardOperatorRuntimeAuditReflection.GetMember(item, "Grids", "Slots", "Children"));
        return Safe(slotName) + "=" + Safe(type) + ":" + Safe(tpl) + ":" + Safe(id) + ":" + Safe(grids);
    }

    private static string TemplateId(object? item)
    {
        object? template = VanguardOperatorRuntimeAuditReflection.GetMember(item, "Template", "TemplateId", "StringTemplateId", "Tpl", "_template");
        object? id = VanguardOperatorRuntimeAuditReflection.GetMember(template, "Id", "_id", "TemplateId");
        return Safe(VanguardOperatorRuntimeAuditReflection.FirstNonEmpty(VanguardOperatorRuntimeAuditReflection.Text(id), VanguardOperatorRuntimeAuditReflection.Text(template)));
    }

    private static bool InvokeNoArg(object? instance, string name)
    {
        if (instance == null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var method = instance.GetType().GetMethod(name, flags, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            method.Invoke(instance, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void LogBootOnce()
    {
        if (bootLogged)
        {
            return;
        }

        bootLogged = true;
        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_POST_ORBIT_INVENTORY_RECOVERY_BOOT enabled=true; scope=post_orbit_backpack_rig_pockets_medical_item_signature_controller_settle_ghost_use_recovery_periodic_inventory_refresh; doctrine=do_not_force_medical_through_unstable_inventory_recheck_first_refresh_medical_cache_after_loot_or_every_30s; heavySettle={HeavyBleedInventorySettle.TotalSeconds:0.00}; mobileSettle={MobileInventorySettle.TotalSeconds:0.00}; stationarySettle={StationaryInventorySettle.TotalSeconds:0.00}; noEffectSettle={NoEffectRecoverySettle.TotalSeconds:0.00}; ghostUsingMin={GhostUsingMinDuration.TotalSeconds:0.00}; periodicRefresh={PeriodicInventoryRefreshInterval.TotalSeconds:0.00}; mutatesMedical=guarded_first_aid_timeout_cancel_refresh_and_medical_cache_refresh; mutatesInventory=read_model_cache_refresh_only; mutatesLoot=false; mutatesSain=false; build={VanguardBuildVersion.BuildLabel}; refreshTag={InventoryRefreshStatusTag}; tag={StatusTag}");
    }

    private static void LogDelay(State state, OperatorDecisionSnapshot snapshot, DateTimeOffset now, string reason, string summary)
    {
        if (!ShouldLog(state, now, "delay|" + reason + "|" + snapshot.Medical.Need.DominantNeed + "|" + snapshot.Medical.Actionability.Classification))
        {
            return;
        }

        VanguardClientDiagnosticsLog.Info(StatusTag, $"VANGUARD_MEDICAL_POST_ORBIT_INVENTORY_RECHECK operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={Safe(reason)}; medNeed={snapshot.Medical.Need.DominantNeed}; medTarget={Safe(snapshot.Medical.Need.TargetPart)}; medItem={Safe(snapshot.Medical.Actionability.SelectedItemName)}; medCanApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; medicalClass={Safe(snapshot.Medical.Actionability.Classification)}; loot={Safe(snapshot.Looting.Classification)}; orbit={Safe(snapshot.Orbit.Classification)}; {summary}; mutatesMedical=false; mutatesInventory=false; next=medical_recheck; tag={StatusTag}");
    }

    private static string BuildStateSummary(State state, DateTimeOffset now, InventorySignature signature)
    {
        return $"recentPostLoot={Bool(IsRecentPostLoot(state, now))}; lootAge={Age(now, state.LastLootActiveAtUtc)}; lootEndAge={Age(now, state.LastLootEndedAtUtc)}; mutationAge={Age(now, state.LastInventoryMutationAtUtc)}; mutationReason={Safe(state.LastInventoryMutationReason)}; noEffectAge={Age(now, state.LastNoEffectAtUtc)}; noEffectReason={Safe(state.LastNoEffectReason)}; {signature.Summary}";
    }


    private static void RequestInventoryRefresh(State state, DateTimeOffset now, string reason)
    {
        state.RefreshRequestedAtUtc = now;
        state.RefreshReason = Safe(reason);
    }

    private static bool TryApplyScheduledInventoryRefresh(BotOwner botOwner, OperatorDecisionSnapshot snapshot, State state, DateTimeOffset now, string source, bool force)
    {
        if (string.IsNullOrWhiteSpace(snapshot.BotProfileId) || botOwner == null)
        {
            return false;
        }

        bool mutationRefreshDue = state.RefreshRequestedAtUtc != DateTimeOffset.MinValue
            && (state.LastInventoryRefreshAppliedAtUtc == DateTimeOffset.MinValue || state.LastInventoryRefreshAppliedAtUtc < state.RefreshRequestedAtUtc);
        bool periodicRefreshDue = state.LastInventoryRefreshAppliedAtUtc == DateTimeOffset.MinValue
            || now - state.LastInventoryRefreshAppliedAtUtc >= PeriodicInventoryRefreshInterval;
        bool suspiciousMedicalCache = snapshot.Medical.Need.HasAnyNeed
            && snapshot.Medical.Actionability.RequiredItemAvailable
            && snapshot.Medical.Actionability.CanApplyItem == false
            && (IsRecentPostLoot(state, now) || state.LastInventoryMutationAtUtc != DateTimeOffset.MinValue);

        if (!force && !mutationRefreshDue && !periodicRefreshDue && !suspiciousMedicalCache)
        {
            return false;
        }

        bool medicalLeaseActive = VanguardExecutionLeaseStore.TryGetActive(snapshot.BotProfileId, out _);
        bool nativeMedicalUsing = snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.FirstAidUsing
            || snapshot.Medical.Actionability.SurgicalKitUsing
            || botOwner.Medecine?.FirstAid?.Using == true
            || botOwner.Medecine?.SurgicalKit?.Using == true;
        if (medicalLeaseActive || nativeMedicalUsing)
        {
            if (state.RefreshRequestedAtUtc == DateTimeOffset.MinValue)
            {
                RequestInventoryRefresh(state, now, "deferred_during_medical_transaction:" + Safe(source));
            }
            if (ShouldLog(state, now, "refresh_deferred_medical_transaction|" + source))
            {
                VanguardClientDiagnosticsLog.Info(InventoryRefreshStatusTag,
                    $"VANGUARD_OPERATOR_INVENTORY_REFRESH_DEFERRED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason=medical_transaction_active; source={Safe(source)}; leaseActive={Bool(medicalLeaseActive)}; nativeUsing={Bool(nativeMedicalUsing)}; firstAidUsing={Bool(snapshot.Medical.Actionability.FirstAidUsing)}; surgicalUsing={Bool(snapshot.Medical.Actionability.SurgicalKitUsing)}; replayAfterHandsSettle=true; tag={InventoryRefreshStatusTag}");
            }
            return false;
        }

        if (state.LastInventoryRefreshAttemptAtUtc != DateTimeOffset.MinValue
            && now - state.LastInventoryRefreshAttemptAtUtc < InventoryRefreshCooldown)
        {
            if (force && ShouldLog(state, now, "refresh_skip_cooldown|" + source))
            {
                VanguardClientDiagnosticsLog.Info(InventoryRefreshStatusTag, $"VANGUARD_OPERATOR_INVENTORY_REFRESH_SKIPPED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason=refresh_cooldown; source={Safe(source)}; lastAttemptAge={Age(now, state.LastInventoryRefreshAttemptAtUtc)}; cooldown={InventoryRefreshCooldown.TotalSeconds:0.00}; medNeed={snapshot.Medical.Need.DominantNeed}; medItem={Safe(snapshot.Medical.Actionability.SelectedItemName)}; medCanApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; tag={InventoryRefreshStatusTag}");
            }

            return false;
        }

        state.LastInventoryRefreshAttemptAtUtc = now;
        var before = CaptureInventorySignature(botOwner, snapshot);
        bool refresh = TryRefreshInventoryAndMedicalCaches(botOwner, out var refreshSummary);
        var after = CaptureInventorySignature(botOwner, snapshot);
        state.LastInventoryRefreshAppliedAtUtc = now;
        state.LastInventorySignature = after.Signature;
        string reason = force ? "forced_" + Safe(source) : mutationRefreshDue ? Safe(state.RefreshReason) : periodicRefreshDue ? "periodic_30s" : "medical_cache_suspect";
        VanguardClientDiagnosticsLog.Diagnostic(InventoryRefreshStatusTag, () => $"VANGUARD_OPERATOR_INVENTORY_REFRESH_APPLIED operator={snapshot.OperatorId}; botProfile={snapshot.BotProfileId}; reason={reason}; source={Safe(source)}; forced={Bool(force)}; refreshed={Bool(refresh)}; mutationDue={Bool(mutationRefreshDue)}; periodicDue={Bool(periodicRefreshDue)}; suspiciousMedicalCache={Bool(suspiciousMedicalCache)}; medNeed={snapshot.Medical.Need.DominantNeed}; medItem={Safe(snapshot.Medical.Actionability.SelectedItemName)}; medCanApply={Tri(snapshot.Medical.Actionability.CanApplyItem)}; before={Safe(before.Signature)}; after={Safe(after.Signature)}; {refreshSummary}; mutatesMedical=cache_refresh; mutatesInventory=read_model_cache_refresh_only; tag={InventoryRefreshStatusTag}");
        return refresh;
    }

    private static bool TryRefreshInventoryAndMedicalCaches(BotOwner botOwner, out string summary)
    {
        var parts = new List<string>(16);
        object? player = VanguardOperatorRuntimeAuditReflection.GetMember(botOwner, "GetPlayer", "Player");
        object? inventory = VanguardOperatorRuntimeAuditReflection.GetMember(player, "Inventory", "InventoryController", "ProfileInventory");
        object? profileInventory = VanguardOperatorRuntimeAuditReflection.GetMember(player, "ProfileInventory");
        object? equipment = VanguardOperatorRuntimeAuditReflection.GetMember(inventory, "Equipment", "EquipmentContainer", "EquipmentSlots");
        object? itemOwner = VanguardOperatorRuntimeAuditReflection.GetMember(inventory, "ItemOwner", "Owner", "InventoryOwner");
        object? medecine = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Medecine");
        object? firstAid = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "FirstAid");
        object? surgicalKit = VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "SurgicalKit");

        bool invRefresh = InvokeNoArg(inventory, "Refresh") || InvokeNoArg(inventory, "Update") || InvokeNoArg(inventory, "OnInventoryChanged");
        bool profileRefresh = InvokeNoArg(profileInventory, "Refresh") || InvokeNoArg(profileInventory, "Update");
        bool equipmentRefresh = InvokeNoArg(equipment, "Refresh") || InvokeNoArg(equipment, "Update");
        bool ownerRefresh = InvokeNoArg(itemOwner, "Refresh") || InvokeNoArg(itemOwner, "Update") || InvokeNoArg(itemOwner, "RaiseRefresh");
        bool medRefresh = InvokeNoArg(medecine, "RefreshCurMeds") || InvokeNoArg(medecine, "Refresh") || InvokeNoArg(medecine, "RefreshMeds");
        bool firstAidRefresh = InvokeNoArg(firstAid, "Refresh") || InvokeNoArg(firstAid, "RefreshMeds");
        bool surgicalRefresh = InvokeNoArg(surgicalKit, "Refresh") || InvokeNoArg(surgicalKit, "RefreshMeds");

        parts.Add("inventoryRefresh=" + Bool(invRefresh));
        parts.Add("profileInventoryRefresh=" + Bool(profileRefresh));
        parts.Add("equipmentRefresh=" + Bool(equipmentRefresh));
        parts.Add("itemOwnerRefresh=" + Bool(ownerRefresh));
        parts.Add("medRefresh=" + Bool(medRefresh));
        parts.Add("firstAidRefresh=" + Bool(firstAidRefresh));
        parts.Add("surgicalRefresh=" + Bool(surgicalRefresh));
        summary = "refresh=" + string.Join(",", parts);
        return invRefresh || profileRefresh || equipmentRefresh || ownerRefresh || medRefresh || firstAidRefresh || surgicalRefresh;
    }

    private static bool IsLootOrOrbitActive(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Looting.BotLooting == true
            || snapshot.Looting.LootTaskRunning == true
            || snapshot.Looting.HasActiveLootable == true
            || string.Equals(snapshot.Looting.Classification, "loot_active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.Orbit.Category, "loot", StringComparison.OrdinalIgnoreCase)
            || snapshot.Orbit.Status.IndexOf("loot", StringComparison.OrdinalIgnoreCase) >= 0
            || snapshot.Orbit.Status.IndexOf("moving", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(snapshot.Orbit.Classification, "orbit_moving", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecentPostLoot(State state, DateTimeOffset now)
    {
        return IsRecent(now, state.LastLootActiveAtUtc, RecentLootWindow)
            || IsRecent(now, state.LastLootEndedAtUtc, RecentLootWindow)
            || IsRecent(now, state.LastInventoryMutationAtUtc, RecentLootWindow);
    }

    private static bool IsRecent(DateTimeOffset now, DateTimeOffset then, TimeSpan window)
    {
        return then != DateTimeOffset.MinValue && now >= then && now - then <= window;
    }

    private static TimeSpan SettleWindowFor(VanguardMedicalNeed need)
    {
        return need switch
        {
            VanguardMedicalNeed.HeavyBleed => HeavyBleedInventorySettle,
            VanguardMedicalNeed.LightBleed => MobileInventorySettle,
            VanguardMedicalNeed.HpHeal => MobileInventorySettle,
            _ => StationaryInventorySettle
        };
    }

    private static bool IsSurgeryNeed(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.SurgeryDestroyedPart || need == VanguardMedicalNeed.BlackBroken;
    }

    private static State GetState(string botProfileId)
    {
        if (!States.TryGetValue(botProfileId, out var state))
        {
            state = new State();
            States[botProfileId] = state;
        }

        return state;
    }

    private static bool ShouldLog(State state, DateTimeOffset now, string signature)
    {
        if (!string.Equals(state.LastLogSignature, signature, StringComparison.Ordinal) || now - state.LastLogAtUtc >= LogInterval)
        {
            state.LastLogSignature = signature;
            state.LastLogAtUtc = now;
            return true;
        }

        return false;
    }

    private static string Age(DateTimeOffset now, DateTimeOffset then)
    {
        return then == DateTimeOffset.MinValue ? "none" : Math.Max(0d, (now - then).TotalSeconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Tri(bool? value) => value.HasValue ? Bool(value.Value) : "unknown";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');

    private sealed class State
    {
        public bool WasLootActive;
        public DateTimeOffset LastLootActiveAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastLootEndedAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastInventoryMutationAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastNoEffectAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastGhostRecoverAttemptAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastInventoryRefreshAttemptAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastInventoryRefreshAppliedAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset RefreshRequestedAtUtc = DateTimeOffset.MinValue;
        public DateTimeOffset LastLogAtUtc = DateTimeOffset.MinValue;
        public string LastInventoryMutationReason = "none";
        public string RefreshReason = "none";
        public string LastInventorySignature = string.Empty;
        public string LastNoEffectReason = "none";
        public string LastNoEffectLease = "none";
        public string LastLogSignature = string.Empty;
    }

    private sealed class InventorySignature
    {
        public string Signature { get; init; } = "none";
        public string Summary { get; init; } = "inventorySignature=none";
    }
}
#endif

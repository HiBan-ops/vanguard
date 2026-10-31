#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;

// Responsibility: qualifies the exact carried low-magazine + assigned Better-Mag pair before any discard/replacement mutation is considered.
// Flow: LOOT-Q1 discard candidate + exact replacement squad assignment + live carried/source binding + live corpse replacement + current loot safety + simulated native atomic Swap -> bounded shadow telemetry.
// Authority boundary: observation/preflight only. It never submits an inventory operation, throws an item, transfers cartridges, moves equipment, changes weapon preference or writes target/aim/fire state.
// Invariant: a low magazine is never considered disposable merely because a better magazine exists somewhere in loot. The replacement must be assigned to this Operator and the carried candidate must still be live, low, compatible, addressable, non-secured and not installed in a weapon.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardMagazineDiscardPreflightShadowService
{
    public const string StatusTag = "VANGUARD_MAGAZINE_DISCARD_PREFLIGHT_SHADOW_STATUS";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20d);
    private const int MaxDiagnosticFieldLength = 190;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ShadowState> ByPairKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly EquipmentSlot[] StorageSlots =
    {
        EquipmentSlot.Pockets,
        EquipmentSlot.TacticalVest,
        EquipmentSlot.Backpack,
        EquipmentSlot.SecuredContainer
    };

    internal static void Observe(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardUnifiedLootReadModelObservation observation,
        IReadOnlyList<EftWeapon> currentWeapons,
        DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsRaidAuthority
            || record.BotOwner == null
            || record.BotOwner.IsDead
            || currentWeapons == null
            || currentWeapons.Count == 0)
        {
            return;
        }

        foreach (EftWeapon weapon in currentWeapons)
        {
            if (weapon == null || string.IsNullOrWhiteSpace(weapon.Id))
            {
                continue;
            }

            if (!VanguardMagazineLogisticsAuditService.TryGetLatest(record.BotProfileId, weapon.Id, now, out VanguardMagazineLogisticsAuditSnapshot audit)
                || !audit.BetterLootMagazineCandidate
                || string.IsNullOrWhiteSpace(audit.BestLootMagazineItemId)
                || string.Equals(audit.BestLootMagazineItemId, "none", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(audit.DiscardCandidateItemId)
                || string.Equals(audit.DiscardCandidateItemId, "none", StringComparison.OrdinalIgnoreCase)
                || audit.TargetKind != observation.TargetKind
                || !string.Equals(audit.TargetId, observation.TargetId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool exactReplacementAssignment = VanguardSquadLootAssignmentService.IsAssignedToBot(
                observation.OwnerProfileId,
                observation.TargetKind,
                observation.TargetId,
                audit.BestLootMagazineItemId,
                observation.BotProfileId,
                observation.ManifestRevision,
                now,
                out VanguardSquadLootItemAssignment replacementAssignment);
            bool replacementAssignmentIsMagazine = exactReplacementAssignment
                && string.Equals(replacementAssignment.Category, "magazine", StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<VanguardSquadLootItemAssignment> orderedAssignments = VanguardSquadLootAssignmentService.GetAssignmentsForBot(
                observation.OwnerProfileId,
                observation.TargetKind,
                observation.TargetId,
                observation.BotProfileId,
                observation.ManifestRevision,
                now);
            VanguardOperatorLootPermissionSnapshot permissions = VanguardOperatorLootPermissionSnapshot.CaptureRuntime(record);
            var executableAssignments = new List<VanguardSquadLootItemAssignment>(orderedAssignments.Count);
            foreach (VanguardSquadLootItemAssignment candidateAssignment in orderedAssignments)
            {
                if (candidateAssignment.InterestRevision != observation.InterestRevision
                    || !string.Equals(candidateAssignment.NeedSignature, observation.NeedSignature, StringComparison.Ordinal)
                    || !VanguardCorpseLootTransactionPreflight.IsAssignmentExecutable(candidateAssignment, permissions))
                {
                    continue;
                }

                executableAssignments.Add(candidateAssignment);
            }

            int replacementAssignmentRank = 0;
            for (int index = 0; index < executableAssignments.Count; index++)
            {
                if (string.Equals(executableAssignments[index].ItemId, audit.BestLootMagazineItemId, StringComparison.OrdinalIgnoreCase))
                {
                    replacementAssignmentRank = index + 1;
                    break;
                }
            }
            bool replacementWithinExecutorWindow = replacementAssignmentRank > 0
                && replacementAssignmentRank <= VanguardCorpseLootTransactionPreflight.MaximumCandidates;
            bool replacementFirstForCurrentSession = replacementAssignmentRank == 1;

            bool exactRuntimeBinding = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(record.BotProfileId, out VanguardRaidOperatorRuntimeRecord rebound)
                && ReferenceEquals(rebound.BotOwner, record.BotOwner)
                && rebound.RuntimeIncarnation == record.RuntimeIncarnation
                && string.Equals(rebound.OwnerProfileId, record.OwnerProfileId, StringComparison.OrdinalIgnoreCase);

            Player? player = record.BotOwner.GetPlayer;
            InventoryController? inventory = player?.InventoryController;
            bool inventoryControllerObserved = inventory != null;

            bool snapshotFound = VanguardOperatorDecisionSnapshotService.TryGetLatestSnapshot(record.BotProfileId, out OperatorDecisionSnapshot decision);
            string snapshotFreshnessReason = "decision_snapshot_missing";
            bool snapshotFresh = snapshotFound && VanguardOperatorDecisionSnapshotService.IsFresh(decision, now, out snapshotFreshnessReason);
            bool opportunisticLootWindow = snapshotFresh && decision.PrimaryExecution.IsOpportunisticLoot;
            string safetyGate = snapshotFresh
                ? VanguardCorpseLootApproachExecutor.CheckActiveSessionSafetyGate(decision, now, opportunisticLootWindow)
                : "snapshot_unavailable:" + snapshotFreshnessReason;
            bool safetyClear = snapshotFresh && string.Equals(safetyGate, "none", StringComparison.OrdinalIgnoreCase);

            Item? discardItem = inventory?.FindItem<Item>(audit.DiscardCandidateItemId);
            MagazineItemClass? discardMagazine = discardItem as MagazineItemClass;
            bool discardLiveResolvable = discardMagazine != null;
            string discardSourceSlot = ResolveStorageSlot(inventory, audit.DiscardCandidateItemId);
            bool discardSourceAllowed = string.Equals(discardSourceSlot, EquipmentSlot.Pockets.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(discardSourceSlot, EquipmentSlot.TacticalVest.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(discardSourceSlot, EquipmentSlot.Backpack.ToString(), StringComparison.OrdinalIgnoreCase);
            bool discardInSecuredContainer = string.Equals(discardSourceSlot, EquipmentSlot.SecuredContainer.ToString(), StringComparison.OrdinalIgnoreCase);
            ItemAddress? discardAddress = discardMagazine?.CurrentAddress;
            bool discardAddressObserved = discardAddress != null;
            bool discardCompatibleWithWeapon = discardMagazine != null && Fits(discardMagazine, weapon);
            bool discardStillStructurallyLow = discardMagazine != null
                && (discardMagazine.Count <= 0
                    || (discardMagazine.MaxCount > 0 && discardMagazine.Count * 4 <= discardMagazine.MaxCount));
            bool discardIsCurrentWeaponMagazine = IsInstalledMagazine(currentWeapons, audit.DiscardCandidateItemId);

            MagazineItemClass? replacementMagazine = null;
            bool replacementLiveResolvable = false;
            string replacementLiveSourceReason = "replacement_live_item_unresolved";
            if (observation.TargetKind == VanguardLootTargetKind.Corpse)
            {
                if (VanguardCorpseRegistry.TryGet(observation.TargetId, now, out VanguardCorpseRegistryEntry entry)
                    && entry.Corpse != null
                    && VanguardCorpseLootLiveItemResolver.TryResolve(
                        entry.Corpse,
                        audit.BestLootMagazineItemId,
                        out Item liveReplacement,
                        out string sourcePath,
                        out string sourceAddress))
                {
                    replacementMagazine = liveReplacement as MagazineItemClass;
                    replacementLiveResolvable = replacementMagazine != null;
                    replacementLiveSourceReason = replacementLiveResolvable
                        ? "corpse_live_item_exact:" + Safe(sourcePath) + ":" + Safe(sourceAddress)
                        : "corpse_live_item_not_magazine";
                }
                else
                {
                    replacementLiveSourceReason = "corpse_live_item_unresolved";
                }
            }
            else
            {
                bool containerFound = false;
                IReadOnlyList<VanguardWorldLootContainerSnapshot> containers = VanguardWorldLootContainerSnapshotProvider.GetSnapshot(now);
                foreach (VanguardWorldLootContainerSnapshot container in containers)
                {
                    if (container == null
                        || !string.Equals(container.ContainerId, observation.TargetId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    containerFound = true;
                    if (VanguardWorldLootContainerLiveItemResolver.TryResolve(
                        container,
                        audit.BestLootMagazineItemId,
                        out Item liveReplacement,
                        out string sourcePath,
                        out string sourceAddress))
                    {
                        replacementMagazine = liveReplacement as MagazineItemClass;
                        replacementLiveResolvable = replacementMagazine != null;
                        replacementLiveSourceReason = replacementLiveResolvable
                            ? "world_container_live_item_exact:" + Safe(sourcePath) + ":" + Safe(sourceAddress)
                            : "world_container_live_item_not_magazine";
                    }
                    else
                    {
                        replacementLiveSourceReason = "world_container_live_item_unresolved";
                    }

                    break;
                }

                if (!containerFound)
                {
                    replacementLiveSourceReason = "world_container_snapshot_unresolved";
                }
            }

            bool replacementCompatibleWithWeapon = replacementMagazine != null && Fits(replacementMagazine, weapon);
            ItemAddress? replacementAddress = replacementMagazine?.CurrentAddress;
            bool replacementAddressObserved = replacementAddress != null;

            bool pairContextReadyShadow = exactRuntimeBinding
                && inventoryControllerObserved
                && snapshotFresh
                && opportunisticLootWindow
                && safetyClear
                && exactReplacementAssignment
                && replacementAssignmentIsMagazine
                && replacementWithinExecutorWindow
                && discardLiveResolvable
                && discardSourceAllowed
                && !discardInSecuredContainer
                && discardAddressObserved
                && discardCompatibleWithWeapon
                && discardStillStructurallyLow
                && !discardIsCurrentWeaponMagazine
                && replacementLiveResolvable
                && replacementAddressObserved
                && replacementCompatibleWithWeapon;

            bool atomicSwapBuildSucceeded = false;
            bool atomicSwapDestroyRequired = false;
            bool atomicSwapCanExecute = false;
            string atomicSwapOperation = "none";
            string atomicSwapReason = "pair_context_not_ready";

            if (pairContextReadyShadow
                && inventory != null
                && discardMagazine != null
                && replacementMagazine != null
                && discardAddress != null
                && replacementAddress != null)
            {
                try
                {
                    var swap = InteractionsHandlerClass.Swap(
                        replacementMagazine,
                        discardAddress,
                        discardMagazine,
                        replacementAddress,
                        inventory,
                        simulate: true);
                    if (!swap.Succeeded || swap.Value == null)
                    {
                        atomicSwapReason = "atomic_swap_build_failed";
                    }
                    else
                    {
                        atomicSwapBuildSucceeded = true;
                        IRaiseEvents operation = swap.Value;
                        atomicSwapOperation = operation.GetType().Name;
                        atomicSwapDestroyRequired = ReadBoolean(operation, "ItemsDestroyRequired");
                        if (atomicSwapDestroyRequired)
                        {
                            atomicSwapReason = "atomic_swap_destroy_required";
                        }
                        else
                        {
                            atomicSwapCanExecute = inventory.CanExecute(operation);
                            atomicSwapReason = atomicSwapCanExecute ? "ready" : "inventory_can_execute_false";
                        }
                    }
                }
                catch (Exception exception)
                {
                    atomicSwapReason = "atomic_swap_exception:" + exception.GetType().Name;
                }
            }

            bool atomicReplacementSwapReadyShadow = pairContextReadyShadow
                && atomicSwapBuildSucceeded
                && !atomicSwapDestroyRequired
                && atomicSwapCanExecute;

            string fingerprint = string.Join("|",
                audit.DiscardCandidateItemId,
                audit.BestLootMagazineItemId,
                exactReplacementAssignment,
                replacementAssignmentIsMagazine,
                replacementAssignmentRank,
                replacementWithinExecutorWindow,
                replacementFirstForCurrentSession,
                exactRuntimeBinding,
                snapshotFresh,
                opportunisticLootWindow,
                safetyGate,
                discardLiveResolvable,
                discardSourceSlot,
                discardAddressObserved,
                discardCompatibleWithWeapon,
                discardStillStructurallyLow,
                discardIsCurrentWeaponMagazine,
                replacementLiveResolvable,
                replacementAddressObserved,
                replacementCompatibleWithWeapon,
                pairContextReadyShadow,
                atomicSwapBuildSucceeded,
                atomicSwapDestroyRequired,
                atomicSwapCanExecute,
                atomicSwapReason,
                observation.ManifestRevision,
                observation.InterestRevision);

            string key = PairKey(
                record.BotProfileId,
                observation.TargetKind,
                observation.TargetId,
                weapon.Id,
                audit.DiscardCandidateItemId,
                audit.BestLootMagazineItemId);

            bool emit;
            string emission;
            lock (Sync)
            {
                if (!ByPairKey.TryGetValue(key, out ShadowState? state))
                {
                    ByPairKey[key] = new ShadowState(fingerprint, now + HeartbeatInterval);
                    emit = true;
                    emission = "INITIAL";
                }
                else if (!string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    state.Fingerprint = fingerprint;
                    state.NextHeartbeatAtUtc = now + HeartbeatInterval;
                    emit = true;
                    emission = "MATERIAL_CHANGE";
                }
                else if (now >= state.NextHeartbeatAtUtc)
                {
                    state.NextHeartbeatAtUtc = now + HeartbeatInterval;
                    emit = true;
                    emission = "HEARTBEAT";
                }
                else
                {
                    emit = false;
                    emission = string.Empty;
                }
            }

            if (!emit)
            {
                continue;
            }

            int discardRounds = discardMagazine == null ? -1 : Math.Max(0, discardMagazine.Count);
            int discardCapacity = discardMagazine == null ? -1 : Math.Max(0, discardMagazine.MaxCount);
            int replacementRounds = replacementMagazine == null ? -1 : Math.Max(0, replacementMagazine.Count);
            int replacementCapacity = replacementMagazine == null ? -1 : Math.Max(0, replacementMagazine.MaxCount);

            VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                $"MAGAZINE_DISCARD_PREFLIGHT_SHADOW bot={Bounded(record.BotProfileId)}; owner={Bounded(record.OwnerProfileId)}; emission={emission}; targetKind={TargetKindToken(observation.TargetKind)}; target={Bounded(observation.TargetId)}; weapon={Bounded(weapon.Id)}; discardMagazine={Bounded(audit.DiscardCandidateItemId)}; discardRounds={discardRounds}; discardCapacity={discardCapacity}; replacementMagazine={Bounded(audit.BestLootMagazineItemId)}; replacementRounds={replacementRounds}; replacementCapacity={replacementCapacity}; exactReplacementAssignmentToBot={Bool(exactReplacementAssignment)}; replacementAssignmentCategoryMagazine={Bool(replacementAssignmentIsMagazine)}; replacementAssignmentReason={Bounded(exactReplacementAssignment ? replacementAssignment.Reason : "none")}; replacementAssignmentRank={replacementAssignmentRank}; executableAssignmentCount={executableAssignments.Count}; replacementWithinExecutorWindow={Bool(replacementWithinExecutorWindow)}; replacementFirstForCurrentSession={Bool(replacementFirstForCurrentSession)}; exactRuntimeBinding={Bool(exactRuntimeBinding)}; inventoryControllerObserved={Bool(inventoryControllerObserved)}; decisionSnapshotFresh={Bool(snapshotFresh)}; snapshotFreshness={Bounded(snapshotFreshnessReason)}; opportunisticLootWindow={Bool(opportunisticLootWindow)}; safetyGate={Bounded(safetyGate)}; discardLiveResolvable={Bool(discardLiveResolvable)}; discardSourceSlot={Bounded(discardSourceSlot)}; discardSourceAllowed={Bool(discardSourceAllowed)}; discardInSecuredContainer={Bool(discardInSecuredContainer)}; discardAddressObserved={Bool(discardAddressObserved)}; discardCompatibleWithWeapon={Bool(discardCompatibleWithWeapon)}; discardStillStructurallyLow={Bool(discardStillStructurallyLow)}; discardIsCurrentWeaponMagazine={Bool(discardIsCurrentWeaponMagazine)}; replacementLiveResolvable={Bool(replacementLiveResolvable)}; replacementLiveSourceReason={Bounded(replacementLiveSourceReason)}; replacementAddressObserved={Bool(replacementAddressObserved)}; replacementCompatibleWithWeapon={Bool(replacementCompatibleWithWeapon)}; pairContextReadyShadow={Bool(pairContextReadyShadow)}; atomicSwapBuildSucceeded={Bool(atomicSwapBuildSucceeded)}; atomicSwapOperation={Bounded(atomicSwapOperation)}; atomicSwapDestroyRequired={Bool(atomicSwapDestroyRequired)}; atomicSwapCanExecute={Bool(atomicSwapCanExecute)}; atomicSwapReason={Bounded(atomicSwapReason)}; atomicReplacementSwapReadyShadow={Bool(atomicReplacementSwapReadyShadow)}; preferredFuturePath=existing_executor_atomic_swap_first; throwFallbackSourceEvidence=true; throwFallbackRuntimeQualified=false; directThrowRequired=false; discardRuntimeQualified=false; mutationAllowed=false; networkTransactionSubmitted=false; itemMoved=false; itemDropped=false; cartridgeTransferred=false; weaponPreferenceMutation=false; targetAimFireMutation=false");
        }
    }

    internal static void ResetForRaidLifecycle()
    {
        lock (Sync)
        {
            ByPairKey.Clear();
        }
    }

    private static string ResolveStorageSlot(InventoryController? inventory, string itemId)
    {
        if (inventory == null || string.IsNullOrWhiteSpace(itemId))
        {
            return "none";
        }

        var scratch = new List<Item>();
        foreach (EquipmentSlot slot in StorageSlots)
        {
            scratch.Clear();
            try
            {
                inventory.GetAcceptableItemsNonAlloc(new[] { slot }, scratch);
            }
            catch
            {
                continue;
            }

            foreach (Item item in scratch)
            {
                if (item != null && string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    return slot.ToString();
                }
            }
        }

        return "none";
    }

    private static bool IsInstalledMagazine(IReadOnlyList<EftWeapon> currentWeapons, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        foreach (EftWeapon weapon in currentWeapons)
        {
            try
            {
                MagazineItemClass? magazine = weapon.GetCurrentMagazine();
                if (magazine != null && string.Equals(magazine.Id, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Fail closed at the caller: the candidate also has to resolve through the allowed storage slots.
            }
        }

        return false;
    }

    private static bool Fits(MagazineItemClass magazine, EftWeapon weapon)
    {
        try
        {
            return weapon.GetMagazineSlot()?.CheckCompatibility(magazine) == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadBoolean(object target, string propertyName)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.PropertyType == typeof(bool) && property.GetValue(target) is bool propertyValue)
            {
                return propertyValue;
            }

            FieldInfo? field = type.GetField(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.FieldType == typeof(bool) && field.GetValue(target) is bool fieldValue && fieldValue;
        }
        catch
        {
            return false;
        }
    }

    private static string PairKey(
        string? botProfileId,
        VanguardLootTargetKind targetKind,
        string? targetId,
        string? weaponItemId,
        string? discardItemId,
        string? replacementItemId)
        => Safe(botProfileId)
            + "|" + TargetKindToken(targetKind)
            + "|" + Safe(targetId)
            + "|" + Safe(weaponItemId)
            + "|" + Safe(discardItemId)
            + "|" + Safe(replacementItemId);

    private static string TargetKindToken(VanguardLootTargetKind targetKind)
        => targetKind == VanguardLootTargetKind.WorldContainer ? "world_container" : "corpse";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bounded(string? value)
    {
        string safe = Safe(value);
        return safe.Length <= MaxDiagnosticFieldLength ? safe : safe[..MaxDiagnosticFieldLength];
    }

    private sealed class ShadowState
    {
        public ShadowState(string fingerprint, DateTimeOffset nextHeartbeatAtUtc)
        {
            Fingerprint = fingerprint;
            NextHeartbeatAtUtc = nextHeartbeatAtUtc;
        }

        public string Fingerprint { get; set; }
        public DateTimeOffset NextHeartbeatAtUtc { get; set; }
    }
}
#endif

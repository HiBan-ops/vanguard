#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;

// Responsibility: Performs the final inventory-level feasibility and safety checks before a corpse item transfer is attempted.
// Flow: Live source/destination items are re-resolved, identity/slot/compatibility/capacity conditions are checked and a concrete transfer operation is prepared only when current EFT inventory state still matches the plan.
// Authority boundary: Preflight does not commit inventory changes; EFT transaction execution/readback remains the mutation authority.
// Invariant: Stale item references or incompatible destinations fail before mutation, source identity is preserved, and preflight never fabricates capacity or silently drops items.
namespace Vanguard.Client.Runtime.Loot;

/// <summary>
/// Builds exactly one native EFT operation from an item admitted by the squad loot read model and assigned to this Operator.
/// The operation remains represented through EFT's common IRaiseEvents contract so move/swap runtime identity is preserved.
/// Fika multi-count stack safety is checked before construction and again before submission; the item claim is acquired
/// only after a live operation can be built.
/// Secondary long-weapon replacement uses EFT's atomic Swap between the candidate's corpse address
/// and a raid-mutable occupant. Empty primary slots remain acquisition-capable while initial weapon items
/// are protected from implicit replacement by provenance.
/// </summary>
internal static class VanguardCorpseLootTransactionPreflight
{
    private const int MaximumCandidates = 8;

    private delegate bool LiveItemResolver(string itemId, out Item item, out string sourcePath, out string sourceAddress);

    public static bool TryPrepare(
        Corpse corpse, BotOwner botOwner, VanguardCorpseLootSessionLimits limits, VanguardCorpseLootSessionProgress progress,
        string ownerProfileId, string botProfileId, string corpseId, long manifestRevision, long interestRevision, string needSignature,
        out VanguardCorpseLootPreparedTransaction prepared, out VanguardCorpseLootTransactionPreflightResult result)
        => TryPrepareCore(
            VanguardLootTargetKind.Corpse, corpseId,
            (string itemId, out Item item, out string path, out string address) => VanguardCorpseLootLiveItemResolver.TryResolve(corpse, itemId, out item, out path, out address),
            botOwner, limits, progress, ownerProfileId, botProfileId, manifestRevision, interestRevision, needSignature, out prepared, out result);

    public static bool TryPrepareWorldContainer(
        VanguardWorldLootContainerSnapshot container, BotOwner botOwner, VanguardCorpseLootSessionLimits limits, VanguardCorpseLootSessionProgress progress,
        string ownerProfileId, string botProfileId, string containerId, long manifestRevision, long interestRevision, string needSignature,
        out VanguardCorpseLootPreparedTransaction prepared, out VanguardCorpseLootTransactionPreflightResult result)
        => TryPrepareCore(
            VanguardLootTargetKind.WorldContainer, containerId,
            (string itemId, out Item item, out string path, out string address) => VanguardWorldLootContainerLiveItemResolver.TryResolve(container, itemId, out item, out path, out address),
            botOwner, limits, progress, ownerProfileId, botProfileId, manifestRevision, interestRevision, needSignature, out prepared, out result);

    private static bool TryPrepareCore(
        VanguardLootTargetKind targetKind, string targetId, LiveItemResolver resolver, BotOwner botOwner,
        VanguardCorpseLootSessionLimits limits, VanguardCorpseLootSessionProgress progress, string ownerProfileId, string botProfileId,
        long manifestRevision, long interestRevision, string needSignature, out VanguardCorpseLootPreparedTransaction prepared,
        out VanguardCorpseLootTransactionPreflightResult result)
    {
        prepared = null!;
        if (resolver == null || botOwner == null || limits == null || progress == null || manifestRevision <= 0 || string.IsNullOrWhiteSpace(targetId))
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("utility_claim_context_missing");
            return false;
        }

        Player? player = botOwner.GetPlayer;
        InventoryController? inventory = player?.InventoryController;
        InventoryEquipment? equipment = player?.Inventory?.Equipment;
        if (inventory == null || equipment == null)
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("inventory_missing");
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        VanguardOperatorLootPermissionSnapshot permissions = ResolvePermissions(ownerProfileId, botProfileId);
        if (!VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(permissions, targetKind, out string targetPermissionReason))
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("target_permission_blocked:" + targetPermissionReason);
            return false;
        }
        VanguardOperatorLootNeedSnapshot need = VanguardOperatorLootNeedReader.Capture(botOwner);
        if (!need.Observed)
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("fresh_need_unavailable:" + need.Source);
            return false;
        }

        string expectedNeedSignature = NormalizeSignature(needSignature);
        IReadOnlyList<VanguardSquadLootItemAssignment> assignments = VanguardSquadLootAssignmentService.GetAssignmentsForBot(
                ownerProfileId, targetKind, targetId, botProfileId, manifestRevision, now)
            .Where(value => value.InterestRevision == interestRevision
                && string.Equals(NormalizeSignature(value.NeedSignature), expectedNeedSignature, StringComparison.Ordinal))
            .ToList();
        VanguardCorpseLootTransactionPreflightResult? lastRejected = null;
        int useful = 0;
        int feasible = 0;

        foreach (VanguardSquadLootItemAssignment assignment in assignments
                     .Where(value => IsAssignmentExecutable(value, permissions))
                     .OrderByDescending(value => value.Tier)
                     .ThenByDescending(value => value.UtilityScore)
                     .ThenBy(value => value.ItemId, StringComparer.OrdinalIgnoreCase)
                     .Take(MaximumCandidates))
        {
            useful++;
            if (!TryBuildPlanEntry(resolver, assignment, out VanguardCorpseLootItemPlanEntry entry))
            {
                lastRejected = VanguardCorpseLootTransactionPreflightResult.Rejected("assigned_item_missing_or_unresolvable");
                continue;
            }
            if (!progress.CanAccept(entry, limits, out string budgetReason))
            {
                lastRejected = VanguardCorpseLootTransactionPreflightResult.Rejected(budgetReason);
                continue;
            }

            if (TryPrepareAssignment(resolver, botOwner, inventory, equipment, need, assignment, entry, now, useful, out prepared, out result))
            {
                feasible++;
                return true;
            }
            lastRejected = result;
        }

        result = new VanguardCorpseLootTransactionPreflightResult
        {
            Ready = false,
            Reason = assignments.Count == 0 ? "no_current_item_assignment_for_operator" : "assigned_operation_not_preparable:" + (lastRejected?.Reason ?? "none"),
            FreshUsefulItemCount = useful, FreshFeasibleItemCount = feasible, ManifestRevision = manifestRevision, InterestRevision = interestRevision,
            NeedSignature = expectedNeedSignature, MutationAttempted = false, NetworkTransactionSubmitted = false
        };
        return false;
    }

    public static bool Revalidate(
        VanguardCorpseLootPreparedTransaction prepared,
        Corpse corpse,
        BotOwner botOwner,
        VanguardCorpseLootSessionLimits limits,
        VanguardCorpseLootSessionProgress progress,
        string ownerProfileId,
        string botProfileId,
        string corpseId,
        long manifestRevision,
        long interestRevision,
        string needSignature,
        out string reason)
        => RevalidateCore(
            prepared,
            VanguardLootTargetKind.Corpse,
            corpseId,
            (string itemId, out Item item, out string path, out string address) =>
                VanguardCorpseLootLiveItemResolver.TryResolve(corpse, itemId, out item, out path, out address),
            botOwner,
            limits,
            progress,
            ownerProfileId,
            botProfileId,
            manifestRevision,
            interestRevision,
            needSignature,
            out reason);

    public static bool RevalidateWorldContainer(
        VanguardCorpseLootPreparedTransaction prepared,
        VanguardWorldLootContainerSnapshot container,
        BotOwner botOwner,
        VanguardCorpseLootSessionLimits limits,
        VanguardCorpseLootSessionProgress progress,
        string ownerProfileId,
        string botProfileId,
        string containerId,
        long manifestRevision,
        long interestRevision,
        string needSignature,
        out string reason)
        => RevalidateCore(
            prepared,
            VanguardLootTargetKind.WorldContainer,
            containerId,
            (string itemId, out Item item, out string path, out string address) =>
                VanguardWorldLootContainerLiveItemResolver.TryResolve(container, itemId, out item, out path, out address),
            botOwner,
            limits,
            progress,
            ownerProfileId,
            botProfileId,
            manifestRevision,
            interestRevision,
            needSignature,
            out reason);

    private static bool RevalidateCore(
        VanguardCorpseLootPreparedTransaction prepared,
        VanguardLootTargetKind targetKind,
        string targetId,
        LiveItemResolver resolver,
        BotOwner botOwner,
        VanguardCorpseLootSessionLimits limits,
        VanguardCorpseLootSessionProgress progress,
        string ownerProfileId,
        string botProfileId,
        long manifestRevision,
        long interestRevision,
        string needSignature,
        out string reason)
    {
        reason = "none";
        if (prepared == null || resolver == null || botOwner == null || prepared.ItemClaim == null || string.IsNullOrWhiteSpace(targetId))
        {
            reason = "prepared_context_missing";
            return false;
        }

        try
        {
            Player? player = botOwner.GetPlayer;
            InventoryEquipment? equipment = player?.Inventory?.Equipment;
            if (player?.InventoryController == null || !ReferenceEquals(player.InventoryController, prepared.Inventory))
            {
                reason = "inventory_controller_changed";
                return false;
            }
            if (equipment == null)
            {
                reason = "inventory_equipment_missing_before_commit";
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!VanguardSquadLootAssignmentService.IsAssignedToBot(
                    ownerProfileId,
                    targetKind,
                    targetId,
                    prepared.Preflight.ItemId,
                    botProfileId,
                    manifestRevision,
                    now,
                    out VanguardSquadLootItemAssignment assignment)
                || assignment.InterestRevision != interestRevision
                || !string.Equals(NormalizeSignature(assignment.NeedSignature), NormalizeSignature(needSignature), StringComparison.Ordinal))
            {
                reason = "item_assignment_or_context_changed_before_commit";
                return false;
            }

            VanguardOperatorLootPermissionSnapshot permissions = ResolvePermissions(ownerProfileId, botProfileId);
            if (targetKind == VanguardLootTargetKind.WorldContainer
                && !VanguardOperatorLootTargetPermissionPolicy.AllowsTarget(permissions, targetKind, out string targetPermissionReason))
            {
                reason = "target_permission_changed_before_commit:" + targetPermissionReason;
                return false;
            }
            if (!VanguardUtilityLootActivationPolicy.IsExecutable(ToUtility(assignment), permissions, targetKind))
            {
                reason = "assignment_no_longer_executable_by_policy";
                return false;
            }

            if (!VanguardLootItemClaimStore.TryGet(
                    ownerProfileId,
                    targetKind,
                    targetId,
                    prepared.Preflight.ItemId,
                    now,
                    out VanguardLootItemClaim currentClaim)
                || !string.Equals(currentClaim.ClaimId, prepared.ItemClaim.ClaimId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentClaim.BotProfileId, botProfileId, StringComparison.OrdinalIgnoreCase)
                || currentClaim.ManifestRevision != manifestRevision)
            {
                reason = "item_claim_lost_before_commit";
                return false;
            }
            if (!VanguardLootItemClaimStore.Refresh(currentClaim.ClaimId, now))
            {
                reason = "item_claim_refresh_failed_before_commit";
                return false;
            }

            VanguardCorpseLootItemPlanEntry budgetEntry = new()
            {
                ItemId = prepared.Preflight.ItemId,
                Category = prepared.Preflight.Category,
                Quantity = prepared.Preflight.Quantity,
                PlacementPossible = true
            };
            if (!progress.CanAccept(budgetEntry, limits, out string budgetReason))
            {
                reason = "session_budget_changed:" + budgetReason;
                return false;
            }

            if (!resolver(prepared.Preflight.ItemId, out Item currentItem, out _, out _)
                || !ReferenceEquals(currentItem, prepared.Item))
            {
                reason = "item_missing_or_changed_before_commit";
                return false;
            }
            if (prepared.Item.CurrentAddress == null || !prepared.SourceAddress.Equals(prepared.Item.CurrentAddress))
            {
                reason = "source_changed_before_commit";
                return false;
            }
            if (!VanguardFikaStackableLootSafetyPolicy.IsSafe(prepared.Item, out string stackableSafetyReason))
            {
                reason = "network_safety_changed_before_commit:" + stackableSafetyReason;
                return false;
            }
            if (ReadBoolean(prepared.Operation, "ItemsDestroyRequired"))
            {
                reason = "items_destroy_required_before_commit";
                return false;
            }

            if (prepared.Preflight.SecondarySwap)
            {
                if (!VanguardOperatorRaidLoadoutRegistry.TryGet(botProfileId, out VanguardOperatorRaidLoadoutSnapshot loadout)
                    || !prepared.LongWeaponDestinationSlot.HasValue
                    || !VanguardOperatorRaidLoadoutRegistry.IsRaidMutableLongWeaponSlot(loadout, equipment, prepared.LongWeaponDestinationSlot.Value))
                {
                    reason = "raid_mutable_loadout_context_missing_before_swap";
                    return false;
                }
                Slot? mutableSlot = equipment.GetSlot(prepared.LongWeaponDestinationSlot.Value);
                if (mutableSlot?.ContainedItem == null
                    || prepared.DisplacedItem == null
                    || !ReferenceEquals(mutableSlot.ContainedItem, prepared.DisplacedItem)
                    || prepared.DisplacedItem.CurrentAddress == null
                    || prepared.DisplacedSourceAddress == null
                    || !prepared.DisplacedSourceAddress.Equals(prepared.DisplacedItem.CurrentAddress))
                {
                    reason = "mutable_slot_occupant_changed_before_swap";
                    return false;
                }
                if (IsInitialProtectedWeapon(loadout, prepared.DisplacedItem))
                {
                    reason = "protected_initial_weapon_swap_forbidden";
                    return false;
                }
            }
            else if (prepared.Operation is GInterface427 moveAction)
            {
                if (string.Equals(prepared.Preflight.Category, "long_weapon", StringComparison.OrdinalIgnoreCase))
                {
                    if (!VanguardOperatorRaidLoadoutRegistry.TryGet(botProfileId, out VanguardOperatorRaidLoadoutSnapshot loadout)
                        || !prepared.LongWeaponDestinationSlot.HasValue
                        || !VanguardOperatorRaidLoadoutRegistry.IsRaidMutableLongWeaponSlot(loadout, equipment, prepared.LongWeaponDestinationSlot.Value)
                        || equipment.GetSlot(prepared.LongWeaponDestinationSlot.Value)?.ContainedItem != null)
                    {
                        reason = "long_weapon_empty_destination_changed_before_commit";
                        return false;
                    }
                }
                if (moveAction.From == null || !moveAction.From.Equals(prepared.Item.CurrentAddress))
                {
                    reason = "operation_source_mismatch_before_commit";
                    return false;
                }
            }
            else
            {
                reason = "non_swap_operation_not_atomic_move:" + prepared.Operation.GetType().Name;
                return false;
            }

            if (!prepared.Inventory.CanExecute(prepared.Operation))
            {
                reason = "inventory_can_execute_false_before_commit";
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            reason = "revalidation_exception:" + exception.GetType().Name;
            return false;
        }
    }

    private static bool TryPrepareAssignment(
        LiveItemResolver resolver,
        BotOwner botOwner,
        InventoryController inventory,
        InventoryEquipment equipment,
        VanguardOperatorLootNeedSnapshot need,
        VanguardSquadLootItemAssignment assignment,
        VanguardCorpseLootItemPlanEntry planEntry,
        DateTimeOffset now,
        int usefulCount,
        out VanguardCorpseLootPreparedTransaction prepared,
        out VanguardCorpseLootTransactionPreflightResult result)
    {
        prepared = null!;
        if (!resolver(assignment.ItemId, out Item item, out string sourcePath, out string sourceAddress))
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("assigned_item_missing_or_source_address_unavailable");
            return false;
        }
        ItemAddress? originalAddress = item.CurrentAddress;
        if (originalAddress == null)
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("source_address_missing");
            return false;
        }
        if (!IsStillEligible(item, assignment, need, equipment))
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("assigned_item_no_longer_eligible:" + assignment.Category);
            return false;
        }
        if (!VanguardFikaStackableLootSafetyPolicy.IsSafe(item, out string stackableSafetyReason))
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("network_safety_blocked:" + stackableSafetyReason);
            return false;
        }

        IRaiseEvents operation;
        Item? displaced = null;
        ItemAddress? displacedAddress = null;
        EquipmentSlot? longWeaponDestinationSlot = null;
        bool secondarySwap = false;
        string destination;

        if (string.Equals(assignment.Category, "long_weapon", StringComparison.OrdinalIgnoreCase))
        {
            if (item is not EftWeapon candidate
                || !VanguardOperatorRaidLoadoutRegistry.TryGet(assignment.AssignedBotProfileId, out VanguardOperatorRaidLoadoutSnapshot loadout))
            {
                result = VanguardCorpseLootTransactionPreflightResult.Rejected("raid_mutable_weapon_context_missing");
                return false;
            }
            if (!VanguardOperatorRaidLoadoutRegistry.TryResolveRaidMutableLongWeaponSlot(
                loadout, equipment, candidate, assignment.LongWeaponDestinationSlot, out EquipmentSlot mutableSlotKind, out Slot mutableSlot))
            {
                result = VanguardCorpseLootTransactionPreflightResult.Rejected("no_compatible_free_or_raid_mutable_weapon_slot");
                return false;
            }
            longWeaponDestinationSlot = mutableSlotKind;
            if (mutableSlot.ContainedItem == null)
            {
                ItemAddress targetAddress = mutableSlot.CreateItemAddress();
                GStruct154<GClass3411> move = InteractionsHandlerClass.Move(item, targetAddress, inventory, true);
                if (move.Failed || move.Value == null || move.Value.ItemsDestroyRequired)
                {
                    result = VanguardCorpseLootTransactionPreflightResult.Rejected("raid_mutable_slot_move_build_failed");
                    return false;
                }
                operation = move.Value;
                destination = VanguardCorpseLootLiveItemResolver.Fingerprint(targetAddress);
            }
            else
            {
                displaced = mutableSlot.ContainedItem;
                if (IsInitialProtectedWeapon(loadout, displaced)
                    || displaced.CurrentAddress == null
                    || candidate.CurrentAddress == null)
                {
                    result = VanguardCorpseLootTransactionPreflightResult.Rejected("protected_or_unaddressable_mutable_slot_occupant");
                    return false;
                }
                displacedAddress = displaced.CurrentAddress;
                var swap = InteractionsHandlerClass.Swap(candidate, displaced.CurrentAddress, displaced, candidate.CurrentAddress, inventory, true);
                if (!swap.Succeeded || swap.Value == null)
                {
                    result = VanguardCorpseLootTransactionPreflightResult.Rejected("atomic_secondary_swap_build_failed");
                    return false;
                }
                operation = swap.Value;
                if (ReadBoolean(operation, "ItemsDestroyRequired"))
                {
                    result = VanguardCorpseLootTransactionPreflightResult.Rejected("atomic_secondary_swap_destroy_required");
                    return false;
                }
                secondarySwap = true;
                destination = "equipment_slot_swap:" + mutableSlotKind;
            }
        }
        else if (string.Equals(assignment.Category, "holster_weapon", StringComparison.OrdinalIgnoreCase))
        {
            Slot? holster = equipment.GetSlot(EquipmentSlot.Holster);
            if (item is not PistolItemClass pistol || holster == null || holster.ContainedItem != null || !holster.CheckCompatibility(pistol))
            {
                result = VanguardCorpseLootTransactionPreflightResult.Rejected("holster_no_longer_empty_or_compatible");
                return false;
            }
            ItemAddress targetAddress = holster.CreateItemAddress();
            GStruct154<GClass3411> move = InteractionsHandlerClass.Move(item, targetAddress, inventory, true);
            if (move.Failed || move.Value == null || move.Value.ItemsDestroyRequired)
            {
                result = VanguardCorpseLootTransactionPreflightResult.Rejected("holster_move_build_failed");
                return false;
            }
            operation = move.Value;
            destination = VanguardCorpseLootLiveItemResolver.Fingerprint(targetAddress);
        }
        else
        {
            var targets = new List<CompoundItem>(3);
            foreach (EquipmentSlot slotKind in new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack })
            {
                if (equipment.GetSlot(slotKind)?.ContainedItem is CompoundItem container) targets.Add(container);
            }
            if (targets.Count == 0)
            {
                result = VanguardCorpseLootTransactionPreflightResult.Rejected("no_target_container");
                return false;
            }
            GStruct154<GInterface424> place = InteractionsHandlerClass.QuickFindAppropriatePlace(
                item, inventory, targets, InteractionsHandlerClass.EMoveItemOrder.PickUp, simulate: true);
            if (!place.Succeeded || place.Value == null)
            {
                result = VanguardCorpseLootTransactionPreflightResult.Rejected("quick_find_no_destination");
                return false;
            }
            operation = place.Value;
            destination = ReadAddressFingerprint(operation, "To", "Destination", "Address", "ItemAddress", "TargetAddress");
            if (destination.StartsWith("operation_destination_unexposed:", StringComparison.OrdinalIgnoreCase))
            {
                result = VanguardCorpseLootTransactionPreflightResult.Rejected(destination);
                return false;
            }
        }

        if (ReadBoolean(operation, "ItemsDestroyRequired") || !inventory.CanExecute(operation))
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("operation_not_executable_or_destroy_required");
            return false;
        }
        if (!secondarySwap && operation is not GInterface427)
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("non_swap_operation_not_atomic_move:" + operation.GetType().Name);
            return false;
        }

        if (!VanguardLootItemClaimStore.TryAcquire(assignment, assignment.AssignedBotProfileId, now, out VanguardLootItemClaim itemClaim, out string claimReason))
        {
            result = VanguardCorpseLootTransactionPreflightResult.Rejected("item_claim_denied:" + claimReason);
            return false;
        }

        result = new VanguardCorpseLootTransactionPreflightResult
        {
            Ready = true,
            Reason = secondarySwap ? "atomic_secondary_swap_ready" : "utility_claim_move_ready",
            ItemId = assignment.ItemId,
            TemplateId = assignment.TemplateId,
            Category = assignment.Category,
            SelectionReason = assignment.Reason,
            SourcePath = sourcePath,
            SourceAddressFingerprint = sourceAddress,
            DestinationFingerprint = destination,
            OperationType = operation.GetType().Name,
            ItemClaimId = itemClaim.ClaimId,
            ManifestRevision = assignment.ManifestRevision,
            InterestRevision = assignment.InterestRevision,
            AssignmentTier = assignment.Tier.ToString(),
            AssignmentScore = assignment.UtilityScore,
            SecondarySwap = secondarySwap,
            DisplacedItemId = displaced?.Id ?? "none",
            CanExecute = true,
            ItemsDestroyRequired = false,
            MutationAttempted = false,
            NetworkTransactionSubmitted = false,
            Quantity = Math.Max(1, planEntry.Quantity),
            FreshUsefulItemCount = usefulCount,
            FreshFeasibleItemCount = 1,
            NeedSignature = assignment.NeedSignature
        };
        prepared = new VanguardCorpseLootPreparedTransaction
        {
            Inventory = inventory,
            Item = item,
            SourceAddress = originalAddress,
            Operation = operation,
            ItemClaim = itemClaim,
            DisplacedItem = displaced,
            DisplacedSourceAddress = displacedAddress,
            LongWeaponDestinationSlot = longWeaponDestinationSlot,
            Preflight = result
        };
        return true;
    }

    private static bool TryBuildPlanEntry(LiveItemResolver resolver, VanguardSquadLootItemAssignment assignment, out VanguardCorpseLootItemPlanEntry entry)
    {
        if (!resolver(assignment.ItemId, out Item item, out string sourcePath, out _))
        {
            entry = null!;
            return false;
        }
        int quantity = item is MagazineItemClass magazine
            ? Math.Max(1, magazine.Count)
            : item is AmmoItemClass ammo
                ? Math.Max(1, ammo.StackObjectsCount)
                : 1;
        entry = new VanguardCorpseLootItemPlanEntry
        {
            ItemId = assignment.ItemId,
            TemplateId = assignment.TemplateId,
            Name = item.LocalizedName(),
            Category = assignment.Category,
            Reason = assignment.Reason,
            SourcePath = sourcePath,
            Destination = "live_preflight",
            PlacementOperation = "assigned_native_operation",
            PlacementPossible = true,
            Quantity = quantity,
            CellCount = Math.Max(1, item.Width * item.Height),
            EstimatedWeightKg = ReadWeight(item),
            Score = assignment.ExecutionScore,
            StopCondition = "single_utility_claim_commit_then_squad_reassign"
        };
        return true;
    }

    private static bool IsAssignmentExecutable(VanguardSquadLootItemAssignment assignment, VanguardOperatorLootPermissionSnapshot permissions)
        => VanguardUtilityLootActivationPolicy.IsExecutable(ToUtility(assignment), permissions, assignment.TargetKind);

    private static VanguardLootItemUtility ToUtility(VanguardSquadLootItemAssignment assignment) => new()
    {
        ItemId = assignment.ItemId,
        TemplateId = assignment.TemplateId,
        Category = assignment.Category,
        Tier = assignment.Tier,
        Score = assignment.UtilityScore,
        Reason = assignment.Reason,
        WishlistGroup = assignment.WishlistGroup
    };

    private static bool IsStillEligible(Item item, VanguardSquadLootItemAssignment assignment, VanguardOperatorLootNeedSnapshot need, InventoryEquipment equipment)
    {
        if (string.Equals(assignment.Category, "medical", StringComparison.OrdinalIgnoreCase))
            return item is MedsItemClass meds && VanguardOperatorLootNeedReader.IsUsableMedicalItem(meds);
        if (string.Equals(assignment.Category, "magazine", StringComparison.OrdinalIgnoreCase))
            return item is MagazineItemClass magazine && magazine.Count > 0 && VanguardOperatorLootNeedReader.FitsAnyWeapon(magazine, CaptureCurrentWeapons(equipment));
        if (string.Equals(assignment.Category, "loose_ammunition", StringComparison.OrdinalIgnoreCase))
            return item is AmmoItemClass ammo && ammo.StackObjectsCount > 0 && VanguardOperatorLootNeedReader.FitsAnyWeapon(ammo, CaptureCurrentWeapons(equipment));
        if (string.Equals(assignment.Category, "grenade", StringComparison.OrdinalIgnoreCase)) return item is ThrowWeapItemClass;
        if (string.Equals(assignment.Category, "long_weapon", StringComparison.OrdinalIgnoreCase)) return item is EftWeapon && item is not PistolItemClass;
        if (string.Equals(assignment.Category, "holster_weapon", StringComparison.OrdinalIgnoreCase)) return item is PistolItemClass && need.HolsterSlotEmpty;
        if (string.Equals(assignment.Category, "generic", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(assignment.Category, "weapon_mod", StringComparison.OrdinalIgnoreCase))
            return VanguardCorpseLootManifestService.IsRaidDetachableWeaponMod(item);
        return false;
    }

    private static bool IsInitialProtectedWeapon(VanguardOperatorRaidLoadoutSnapshot loadout, Item item)
        => VanguardOperatorRaidLoadoutRegistry.IsInitialProtectedWeapon(loadout, item);

    private static List<EftWeapon> CaptureCurrentWeapons(InventoryEquipment equipment)
    {
        var result = new List<EftWeapon>(3);
        foreach (EquipmentSlot slotKind in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
        {
            if (equipment.GetSlot(slotKind)?.ContainedItem is EftWeapon weapon) result.Add(weapon);
        }
        return result;
    }

    private static float ReadWeight(Item item)
    {
        try { return Math.Max(0f, item.TotalWeight); }
        catch { return 0f; }
    }

    private static bool ReadBoolean(object target, string propertyName)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.PropertyType == typeof(bool) && property.GetValue(target) is bool propertyValue) return propertyValue;
            FieldInfo? field = type.GetField(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.FieldType == typeof(bool) && field.GetValue(target) is bool fieldValue && fieldValue;
        }
        catch { return false; }
    }

    private static VanguardOperatorLootPermissionSnapshot ResolvePermissions(string? ownerProfileId, string? botProfileId)
    {
        if (Vanguard.Client.Raid.Runtime.VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime))
        {
            return VanguardOperatorLootPermissionSnapshot.CaptureRuntime(runtime);
        }
        return VanguardOperatorLootPermissionSnapshot.CaptureRuntime(ownerProfileId);
    }

    private static string NormalizeSignature(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    private static string ReadAddressFingerprint(object target, params string[] names)
    {
        try
        {
            Type type = target.GetType();
            foreach (string name in names)
            {
                PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.GetValue(target) is ItemAddress address) return VanguardCorpseLootLiveItemResolver.Fingerprint(address);
                FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(target) is ItemAddress fieldAddress) return VanguardCorpseLootLiveItemResolver.Fingerprint(fieldAddress);
            }
            return "operation_destination_unexposed:" + type.Name;
        }
        catch (Exception exception)
        {
            return "operation_destination_unexposed:" + exception.GetType().Name;
        }
    }
}
#endif

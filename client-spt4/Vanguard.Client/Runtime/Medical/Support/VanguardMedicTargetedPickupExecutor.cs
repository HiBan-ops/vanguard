#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Patches;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.External;

// Responsibility: Transfers the exact medical world item dropped by an active Medic Support session into the intended patient's inventory.
// Flow: Reserve the exact dropped LootItem for the intended patient -> verify exact ownership first -> validate the still-current handoff -> move/pick up only in no-combat -> verify patient ownership and refresh meds.
// Authority boundary: This is not Opportunity Loot and never scans/selects arbitrary world loot; it owns only the exact session item and is disabled in combat.
// Invariant: Pickup success requires exact item-instance ownership readback on the patient; while pending, native BotItemTaker and LootingBots cannot claim that exact world item for another bot.
namespace Vanguard.Client.Runtime.Medical.Support;

internal static class VanguardMedicTargetedPickupExecutor
{
    public static string StatusTag => VanguardMedicSupportDoctrine.PickupStatusTag;

    public static bool TryAdvance(
        VanguardMedicSupportSession session,
        IReadOnlyList<OperatorDecisionSnapshot> snapshots,
        DateTimeOffset now,
        out bool terminal,
        out string outcome,
        out string reason)
    {
        terminal = false;
        outcome = "none";
        reason = "none";
        if (session == null || session.Mode != VanguardMedicSupportMode.SupplyDrop || !session.WorldItemObserved || session.WorldLootItem == null)
        {
            terminal = true;
            outcome = "Failed";
            reason = "targeted_pickup_session_or_world_item_invalid";
            return false;
        }

        if (!TryResolvePair(session, snapshots, now, out OperatorDecisionSnapshot caregiver, out OperatorDecisionSnapshot patient,
                out VanguardRaidOperatorRuntimeRecord caregiverRuntime, out VanguardRaidOperatorRuntimeRecord patientRuntime, out reason))
        {
            terminal = true;
            outcome = "Interrupted";
            return false;
        }

        // Exact ownership is the first post-transaction truth. It must be accepted even if combat
        // restarted or the medical planner already observed the newly acquired item.
        if (VanguardMedicSupportItemSelector.ContainsExactItem(patientRuntime.BotOwner!, session.ItemInstanceId))
        {
            patientRuntime.BotOwner!.Medecine?.RefreshCurMeds();
            VanguardMedicSupportSessionStore.TryMarkPickupConfirmed(session.SessionId, now);
            VanguardMedicSupplyDropExecutor.RegisterSuccessfulSupplyHandoff(session, now);
            VanguardMedicRendezvousMovementService.Release(patient.BotProfileId, session.SessionId, now, "pickup_exact_item_owned_by_patient");
            VanguardMainIntentScheduler.TryCloseMedicSupportWindowsForSession(
                session.SessionId,
                session.CaregiverBotProfileId,
                session.PatientBotProfileId,
                "Completed",
                "targeted_pickup_exact_item_owned",
                session.Summary,
                now);
            terminal = true;
            outcome = "Completed";
            reason = "MEDIC_SUPPLY_PICKUP_OWNERSHIP_TRANSFER_OK";
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"MEDIC_SUPPLY_PICKUP_OWNERSHIP_TRANSFER_OK session={Safe(session.SessionId)}; caregiver={Safe(session.CaregiverBotProfileId)}; patient={Safe(session.PatientBotProfileId)}; itemInstance={Safe(session.ItemInstanceId)}; template={Safe(session.ItemTemplateId)}; nativeManagedPickup=true; opportunityLoot=false; medicalRefresh=true; exactOwnershipReadback=true");
            return true;
        }

        if (!patient.Alive || !patient.Medical.Alive)
        {
            terminal = true;
            outcome = "Interrupted";
            reason = "pickup_patient_dead";
            return false;
        }

        var worldItem = session.WorldLootItem;
        if (worldItem == null
            || !(worldItem.Item is MedsItemClass worldMedItem)
            || !string.Equals(VanguardMedicalInventoryReader.ResolveItemInstanceId(worldMedItem), session.ItemInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            terminal = true;
            outcome = "Interrupted";
            reason = "targeted_pickup_exact_world_item_lost_or_changed";
            return false;
        }

        ProtectExactWorldItemReservation(session, patientRuntime.BotOwner!, now);

        if (!ValidatePendingHandoffNeed(session, patient, out reason))
        {
            terminal = true;
            outcome = reason.StartsWith("pickup_no_longer_needed", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Interrupted";
            return outcome == "Completed";
        }

        if (session.PickupRequested
            && session.PickupRequestedAtUtc != DateTimeOffset.MinValue
            && now - session.PickupRequestedAtUtc > TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.PickupWindowMaxSeconds))
        {
            terminal = true;
            outcome = "Failed";
            reason = "targeted_pickup_ownership_readback_timeout";
            return false;
        }

        if (!IsStrictNoCombatHandoff(caregiver, patient, out reason))
        {
            VanguardMedicRendezvousMovementService.Release(patient.BotProfileId, session.SessionId, now, "pickup_waits_for_no_combat:" + reason);
            return false;
        }

        float distance = HorizontalDistance(patient.Position, worldItem.transform.position);
        if (distance > VanguardMedicSupportDoctrine.PickupReachMeters)
        {
            if (!VanguardMedicRendezvousMovementService.TryMove(
                    session.SessionId,
                    patient,
                    patientRuntime.BotOwner!,
                    "pickup",
                    worldItem.transform.position,
                    VanguardMedicSupportDoctrine.PickupReachMeters,
                    now,
                    allowCombatTranslation: false,
                    out bool arrived,
                    out string moveReason))
            {
                reason = "targeted_pickup_move_wait:" + Safe(moveReason);
                return false;
            }
            if (!arrived)
            {
                VanguardMedicSupportSessionStore.TrySetPhase(session.SessionId, "AwaitPatientPickupApproach", now);
                reason = "targeted_pickup_approach_in_progress:" + Safe(moveReason);
                return true;
            }
        }

        if (!VanguardMainIntentScheduler.TryOpenMedicSupportAuxiliaryWindow(
                patient,
                session.SessionId,
                VanguardPrimaryExecutionWindowKinds.MedicSupportPickup,
                "patient_targeted_pickup",
                now,
                out _,
                out string windowReason))
        {
            reason = "targeted_pickup_authority_denied:" + Safe(windowReason);
            return false;
        }

        if (session.PickupRequested)
        {
            reason = "targeted_pickup_transaction_pending_readback";
            return true;
        }

        Player? player = patientRuntime.BotOwner!.GetPlayer;
        if (player == null || worldItem.LastOwner == null)
        {
            terminal = true;
            outcome = "Failed";
            reason = "targeted_pickup_player_or_last_owner_missing";
            return false;
        }

        GStruct154<GInterface424> place;
        try
        {
            place = InteractionsHandlerClass.QuickFindAppropriatePlace(
                worldItem.Item,
                player.InventoryController,
                GClass1518.ToEnumerable(player.InventoryController.Inventory.Equipment),
                InteractionsHandlerClass.EMoveItemOrder.PickUp,
                simulate: true);
        }
        catch (Exception exception)
        {
            terminal = true;
            outcome = "Failed";
            reason = "targeted_pickup_native_preflight_exception:" + exception.GetType().Name;
            return false;
        }

        if (!place.Succeeded || place.Value == null || !player.InventoryController.CanExecute(place.Value))
        {
            terminal = true;
            outcome = "Failed";
            reason = "targeted_pickup_inventory_destination_unavailable";
            return false;
        }

        if (!VanguardMedicSupportSessionStore.TryMarkPickupRequested(session.SessionId, now))
        {
            reason = "targeted_pickup_commit_guard_already_started";
            return true;
        }

        try
        {
            patientRuntime.BotOwner.ItemTaker.method_1(
                player,
                place.Value,
                worldItem.ItemOwner.RootItem,
                worldItem.LastOwner);
        }
        catch (Exception exception)
        {
            terminal = true;
            outcome = "Failed";
            reason = "targeted_pickup_native_transaction_exception:" + exception.GetType().Name + ":" + Safe(exception.Message);
            return false;
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"VANGUARD_MEDIC_TARGETED_PICKUP_REQUESTED session={Safe(session.SessionId)}; patient={Safe(session.PatientBotProfileId)}; caregiver={Safe(session.CaregiverBotProfileId)}; itemInstance={Safe(session.ItemInstanceId)}; template={Safe(session.ItemTemplateId)}; distance={distance:0.00}; exactWorldItem=true; nativeManagedPickup=true; opportunityLoot=false; combatAllowed=false; ownershipReadbackPending=true");
        reason = "targeted_pickup_native_transaction_requested";
        return true;
    }

    public static string ProtectExactWorldItemReservation(VanguardMedicSupportSession session, BotOwner patientOwner, DateTimeOffset now)
    {
        if (session == null || patientOwner == null || session.WorldLootItem == null)
        {
            return "exact_world_reservation=skipped_missing_session_patient_or_world_item";
        }

        // The reservation belongs to this exact patient. A mismatched caller must not
        // clear anybody's native claims or reserve world loot on behalf of another bot.
        if (!string.Equals(patientOwner.ProfileId, session.PatientBotProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return "exact_world_reservation=skipped_patient_identity_mismatch";
        }

        LootItem worldItem = session.WorldLootItem!;
        int nativeClaimsCleared = 0;
        foreach (BotOwner owner in SnapshotBotOwners())
        {
            if (owner == null)
            {
                continue;
            }
            // Preserve the intended patient's native BotItemTaker transaction, including
            // ItemToTake/ThrownItems while its exact-item ownership readback is pending.
            // Clearing it on every observation tick can cancel our own targeted pickup.
            // Only other bots' competing claims may be evicted.
            if (ReferenceEquals(owner, patientOwner))
            {
                continue;
            }

            try
            {
                var itemTaker = owner.ItemTaker;
                if (itemTaker == null)
                {
                    continue;
                }
                bool touched = itemTaker.PreThrownItems.Remove(worldItem);
                if (ReferenceEquals(itemTaker.ItemToTake, worldItem))
                {
                    itemTaker.method_7(worldItem);
                    touched = true;
                }
                touched |= itemTaker.ThrownItems.Remove(worldItem);
                if (touched)
                {
                    nativeClaimsCleared++;
                }
            }
            catch
            {
                // Fail closed at the next tick/readback; never broaden the handoff to arbitrary loot.
            }
        }

        string reservationKey = ResolveWorldReservationKey(session);
        string lootingBots = VanguardExternalAuthorityAdapter.RequestLootingBotsExactLootReservation(
            patientOwner,
            reservationKey,
            TimeSpan.FromSeconds(VanguardMedicSupportDoctrine.SupplySessionMaxSeconds));
        return "exact_world_reservation=held;nativeClaimsCleared=" + nativeClaimsCleared
            + ";lootingBots=" + Safe(lootingBots)
            + ";at=" + now.ToUnixTimeMilliseconds();
    }

    public static string ReleaseExactWorldItemReservation(VanguardMedicSupportSession session, DateTimeOffset now, string reason)
    {
        if (session == null)
        {
            return "exact_world_reservation_release=skipped_no_session";
        }

        VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.PatientBotProfileId, out VanguardRaidOperatorRuntimeRecord patientRuntime);
        BotOwner? patientOwner = patientRuntime?.BotOwner ?? SnapshotBotOwners()
            .FirstOrDefault(owner => string.Equals(owner.ProfileId, session.PatientBotProfileId, StringComparison.OrdinalIgnoreCase));
        string reservationKey = ResolveWorldReservationKey(session);
        string lootingBots = VanguardExternalAuthorityAdapter.ReleaseLootingBotsExactLootReservation(patientOwner, reservationKey, reason);
        bool patientOwnsExact = patientOwner != null && VanguardMedicSupportItemSelector.ContainsExactItem(patientOwner, session.ItemInstanceId);
        LootItem? worldItem = session.WorldLootItem;
        int nativeReoffered = 0;

        if (!patientOwnsExact && worldItem != null)
        {
            foreach (BotOwner owner in SnapshotBotOwners())
            {
                try
                {
                    if (owner?.ItemTaker == null)
                    {
                        continue;
                    }
                    owner.ItemTaker.method_12(worldItem);
                    nativeReoffered++;
                }
                catch
                {
                    // Release is best-effort only; EFT/LootingBots lifecycle may already own cleanup.
                }
            }
        }

        return "exact_world_reservation_release=done;patientOwnsExact=" + (patientOwnsExact ? "true" : "false")
            + ";nativeReoffered=" + nativeReoffered
            + ";lootingBots=" + Safe(lootingBots)
            + ";reason=" + Safe(reason)
            + ";at=" + now.ToUnixTimeMilliseconds();
    }

    private static bool ValidatePendingHandoffNeed(VanguardMedicSupportSession session, OperatorDecisionSnapshot patient, out string reason)
    {
        if (!patient.Alive || !patient.Medical.Alive || !patient.Medical.Need.IsReadable)
        {
            reason = "pickup_patient_medical_truth_unreadable_or_dead";
            return false;
        }
        if (!patient.Medical.Need.HasAnyNeed)
        {
            reason = "pickup_no_longer_needed_patient_need_resolved";
            return false;
        }
        if (patient.Medical.Need.DominantNeed != session.Need)
        {
            reason = "pickup_patient_need_changed:" + patient.Medical.Need.DominantNeed;
            return false;
        }
        if (!string.Equals(Safe(patient.Medical.Need.TargetPart), Safe(session.TargetPart), StringComparison.OrdinalIgnoreCase))
        {
            reason = "pickup_patient_target_part_changed";
            return false;
        }
        if (patient.Medical.Actionability.RequiredItemAvailable)
        {
            reason = "pickup_patient_other_self_treatment_item_available";
            return false;
        }
        reason = "pickup_pending_handoff_need_current";
        return true;
    }

    private static string ResolveWorldReservationKey(VanguardMedicSupportSession session)
    {
        try
        {
            string? rootItemId = session.WorldLootItem?.ItemOwner?.RootItem?.Id;
            if (!string.IsNullOrWhiteSpace(rootItemId))
            {
                return rootItemId.Trim();
            }
        }
        catch
        {
            // Fall through to the already-proven exact medical item identity.
        }
        return session.ItemInstanceId;
    }

    private static BotOwner[] SnapshotBotOwners()
    {
        try
        {
            return VanguardBotsControllerStatePatch.ActiveController?.Bots?.BotOwners?
                .Where(owner => owner != null && !owner.IsDead)
                .ToArray() ?? Array.Empty<BotOwner>();
        }
        catch
        {
            return Array.Empty<BotOwner>();
        }
    }

    private static bool IsStrictNoCombatHandoff(OperatorDecisionSnapshot caregiver, OperatorDecisionSnapshot patient, out string reason)
    {
        bool blocked = IsCombatOrSurvival(caregiver) || IsCombatOrSurvival(patient);
        reason = blocked ? "combat_or_survival_context" : "no_combat_handoff_ready";
        return !blocked;
    }

    private static bool IsCombatOrSurvival(OperatorDecisionSnapshot snapshot)
        => snapshot.Sain.IsInCombat == true
            || snapshot.Sain.RunningToCover == true
            || snapshot.Threat.DirectThreat
            || snapshot.Threat.ResidualThreat
            || snapshot.ThreatScan.CombatContext
            || snapshot.Medical.Safety.ImmediateCombatBlock
            || snapshot.Medical.Safety.IncomingFireRecent
            || (snapshot.PrimaryExecution.Active
                && (string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.HardReturnMovement, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(snapshot.PrimaryExecution.WindowKind, VanguardPrimaryExecutionWindowKinds.SainCombatRelease, StringComparison.OrdinalIgnoreCase)));

    private static bool TryResolvePair(
        VanguardMedicSupportSession session,
        IReadOnlyList<OperatorDecisionSnapshot> snapshots,
        DateTimeOffset now,
        out OperatorDecisionSnapshot caregiver,
        out OperatorDecisionSnapshot patient,
        out VanguardRaidOperatorRuntimeRecord caregiverRuntime,
        out VanguardRaidOperatorRuntimeRecord patientRuntime,
        out string reason)
    {
        caregiver = snapshots.Where(value => value != null && string.Equals(value.BotProfileId, session.CaregiverBotProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.CapturedAtUtc).FirstOrDefault()!;
        patient = snapshots.Where(value => value != null && string.Equals(value.BotProfileId, session.PatientBotProfileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.CapturedAtUtc).FirstOrDefault()!;
        caregiverRuntime = null!;
        patientRuntime = null!;
        if (caregiver == null || patient == null)
        {
            reason = "targeted_pickup_pair_snapshot_missing_or_stale:caregiver=" + Safe(caregiver == null ? "missing" : "present")
                + ":patient=" + Safe(patient == null ? "missing" : "present");
            return false;
        }
        bool caregiverFreshOk = VanguardOperatorDecisionSnapshotService.IsFresh(caregiver, now, out string caregiverFresh);
        bool patientFreshOk = VanguardOperatorDecisionSnapshotService.IsFresh(patient, now, out string patientFresh);
        if (!caregiverFreshOk || !patientFreshOk)
        {
            reason = "targeted_pickup_pair_snapshot_missing_or_stale:caregiver=" + Safe(caregiverFresh)
                + ":patient=" + Safe(patientFresh);
            return false;
        }
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.CaregiverBotProfileId, out caregiverRuntime)
            || caregiverRuntime.BotOwner == null || caregiverRuntime.BotOwner.IsDead
            || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(session.PatientBotProfileId, out patientRuntime)
            || patientRuntime.BotOwner == null || patientRuntime.BotOwner.IsDead)
        {
            reason = "targeted_pickup_pair_runtime_unbound_or_dead";
            return false;
        }
        reason = "pair_current";
        return true;
    }

    private static float HorizontalDistance(UnityEngine.Vector3 left, UnityEngine.Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return UnityEngine.Vector3.Distance(left, right);
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

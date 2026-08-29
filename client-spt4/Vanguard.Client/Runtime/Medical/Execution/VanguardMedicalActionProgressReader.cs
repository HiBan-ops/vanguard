#if SPT_CLIENT
using System;
using EFT;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Reads and normalizes live evidence for Medical Action Progress Reader in the medical runtime.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Medical.Execution;

internal static class VanguardMedicalActionProgressReader
{
    public static VanguardMedicalActionProgressSnapshot Capture(VanguardExecutionLeaseState lease, BotOwner? botOwner, OperatorDecisionSnapshot snapshot)
    {
        var terminal = VanguardMedicalTerminalTruthReader.Capture(lease.BotProfileId, botOwner, snapshot);
        if (terminal.DeadConfirmed)
        {
            return new VanguardMedicalActionProgressSnapshot
            {
                OperatorDead = true,
                TerminalDeadConfirmed = true,
                TerminalReason = terminal.Reason,
                Reason = "terminal_death:" + terminal.Reason
            };
        }

        bool surgeryNeed = IsSurgeryNeed(lease.MedicalNeed);
        bool firstAidUsing = surgeryNeed ? snapshot.Medical.Actionability.SurgicalKitUsing : snapshot.Medical.Actionability.FirstAidUsing;
        bool targetHealthReadable = TryReadTargetHealth(botOwner, lease.TargetPart, out float targetHealth, out float targetMaxHealth);
        bool targetDestroyedReadable = TryReadTargetDestroyed(botOwner, lease.TargetPart, out bool targetDestroyed);
        bool targetHealthImproved = targetHealthReadable && lease.InitialTargetHealth >= 0f && targetHealth > lease.InitialTargetHealth + 0.5f;
        bool surgeryTargetRestored = surgeryNeed
            && targetDestroyedReadable
            && !targetDestroyed
            && ((lease.InitialTargetHealth <= 0.5f && targetHealthReadable && targetHealth > 0.5f) || targetHealthImproved);

        bool needResolved = surgeryNeed ? surgeryTargetRestored : IsNeedResolved(lease, snapshot);
        bool targetResolved = surgeryNeed
            ? surgeryTargetRestored
            : IsTargetResolved(lease, snapshot);
        bool needStillPresent = surgeryNeed ? !surgeryTargetRestored : IsNeedStillPresent(lease, snapshot);
        bool targetStillPresent = surgeryNeed
            ? targetDestroyedReadable && targetDestroyed
            : IsTargetStillPresent(lease, snapshot, targetResolved);
        bool threatInterrupt = IsCriticalThreatInterrupt(snapshot);
        bool healthImproved = lease.InitialHealthPercent >= 0 && snapshot.Medical.Need.HealthPercent > lease.InitialHealthPercent;
        bool hpHealNeed = lease.MedicalNeed == VanguardMedicalNeed.HpHeal;
        bool strictConditionNeed = lease.MedicalNeed == VanguardMedicalNeed.HeavyBleed
            || lease.MedicalNeed == VanguardMedicalNeed.LightBleed
            || lease.MedicalNeed == VanguardMedicalNeed.Fracture
            || surgeryNeed;

        TryReadExactItemState(
            botOwner,
            lease.ItemInstanceId,
            out bool itemInventoryObserved,
            out bool itemInstanceFound,
            out bool itemResourceReadable,
            out float currentItemResource);
        bool exactItemAbsentFromObservedInventory = itemInventoryObserved && !itemInstanceFound;
        bool itemResourceConsumed = itemResourceReadable
            && lease.InitialItemResource >= 0f
            && currentItemResource < lease.InitialItemResource - 0.01f;
        bool resourceConsumedWithoutTargetEffect = surgeryNeed && itemResourceConsumed && !surgeryTargetRestored;

        bool rawEffectObserved = needResolved
            || targetResolved
            || (hpHealNeed && (healthImproved || targetHealthImproved));
        bool anyEffectObserved = terminal.AliveConfirmed && rawEffectObserved;
        bool noMedicalEffectObserved = terminal.AliveConfirmed && !anyEffectObserved && (needStillPresent || targetStillPresent || resourceConsumedWithoutTargetEffect);

        string reason = terminal.TerminalUnknown ? "terminal_truth_unknown:" + terminal.Reason
            : surgeryTargetRestored ? "surgery_target_restored"
            : resourceConsumedWithoutTargetEffect ? "surgery_resource_consumed_without_target_effect"
            : needResolved ? "medical_need_resolved"
            : targetResolved ? "medical_target_resolved"
            : hpHealNeed && healthImproved ? "medical_hp_improved"
            : hpHealNeed && targetHealthImproved ? "medical_target_hp_improved"
            : strictConditionNeed && (healthImproved || targetHealthImproved) ? "medical_hp_changed_but_condition_unresolved"
            : firstAidUsing ? (surgeryNeed ? "surgical_kit_using" : "first_aid_using")
            : threatInterrupt ? "threat_interrupt"
            : noMedicalEffectObserved ? "no_medical_effect_observed"
            : needStillPresent ? "medical_need_still_present" : "awaiting_progress";

        return new VanguardMedicalActionProgressSnapshot
        {
            FirstAidUsing = firstAidUsing,
            NeedResolved = needResolved,
            NeedStillPresent = needStillPresent,
            TargetResolved = targetResolved,
            TargetStillPresent = targetStillPresent,
            HealthImproved = healthImproved,
            TargetHealthImproved = targetHealthImproved,
            SurgeryTargetRestored = surgeryTargetRestored,
            TargetDestroyedReadable = targetDestroyedReadable,
            CurrentTargetDestroyed = targetDestroyedReadable && targetDestroyed,
            ItemInventoryObserved = itemInventoryObserved,
            ItemInstanceFound = itemInstanceFound,
            ExactItemAbsentFromObservedInventory = exactItemAbsentFromObservedInventory,
            ItemResourceReadable = itemResourceReadable,
            ItemResourceConsumed = itemResourceConsumed,
            ResourceConsumedWithoutTargetEffect = resourceConsumedWithoutTargetEffect,
            CurrentItemResource = itemResourceReadable ? currentItemResource : -1f,
            AnyMedicalEffectObserved = anyEffectObserved,
            NoMedicalEffectObserved = noMedicalEffectObserved,
            CurrentHealthPercent = snapshot.Medical.Need.HealthPercent,
            CurrentTargetHealth = targetHealthReadable ? targetHealth : -1f,
            CurrentTargetMaxHealth = targetHealthReadable ? targetMaxHealth : -1f,
            CurrentNeedTargetPart = snapshot.Medical.Need.TargetPart,
            ThreatInterrupt = threatInterrupt,
            OperatorDead = false,
            TerminalAliveConfirmed = terminal.AliveConfirmed,
            TerminalDeadConfirmed = terminal.DeadConfirmed,
            TerminalUnknown = terminal.TerminalUnknown,
            TerminalReason = terminal.Reason,
            Reason = reason
        };
    }

    private static bool IsSurgeryNeed(VanguardMedicalNeed need)
    {
        return need == VanguardMedicalNeed.SurgeryDestroyedPart || need == VanguardMedicalNeed.BlackBroken;
    }

    private static bool IsNeedResolved(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot)
    {
        return lease.MedicalNeed switch
        {
            VanguardMedicalNeed.HeavyBleed => !snapshot.Medical.Need.HasHeavyBleed,
            VanguardMedicalNeed.LightBleed => !snapshot.Medical.Need.HasLightBleed,
            VanguardMedicalNeed.Fracture => !snapshot.Medical.Need.HasFracture,
            VanguardMedicalNeed.HpHeal => !snapshot.Medical.Need.HasHpDamage,
            _ => false
        };
    }

    private static bool IsNeedStillPresent(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot)
    {
        return lease.MedicalNeed switch
        {
            VanguardMedicalNeed.HeavyBleed => snapshot.Medical.Need.HasHeavyBleed,
            VanguardMedicalNeed.LightBleed => snapshot.Medical.Need.HasLightBleed,
            VanguardMedicalNeed.Fracture => snapshot.Medical.Need.HasFracture,
            VanguardMedicalNeed.HpHeal => snapshot.Medical.Need.HasHpDamage,
            _ => false
        };
    }

    private static bool IsTargetResolved(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot)
    {
        if (IsNeedResolved(lease, snapshot))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(lease.TargetPart) || string.Equals(lease.TargetPart, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((lease.MedicalNeed == VanguardMedicalNeed.HeavyBleed && snapshot.Medical.Need.HasHeavyBleed)
            || (lease.MedicalNeed == VanguardMedicalNeed.LightBleed && snapshot.Medical.Need.HasLightBleed)
            || (lease.MedicalNeed == VanguardMedicalNeed.Fracture && snapshot.Medical.Need.HasFracture)
            || (lease.MedicalNeed == VanguardMedicalNeed.HpHeal && snapshot.Medical.Need.HasHpDamage))
        {
            return !string.IsNullOrWhiteSpace(snapshot.Medical.Need.TargetPart)
                && !string.Equals(snapshot.Medical.Need.TargetPart, "none", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(snapshot.Medical.Need.TargetPart, lease.TargetPart, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsTargetStillPresent(VanguardExecutionLeaseState lease, OperatorDecisionSnapshot snapshot, bool targetResolved)
    {
        if (targetResolved || !IsNeedStillPresent(lease, snapshot))
        {
            return false;
        }

        return string.Equals(snapshot.Medical.Need.TargetPart, lease.TargetPart, StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.Medical.Actionability.TargetPart, lease.TargetPart, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCriticalThreatInterrupt(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.Medical.Safety.ImmediateCombatBlock
            || snapshot.Medical.Safety.EnemyCanShoot
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.ThreatScan.CandidateCanShoot
            || (snapshot.ThreatScan.WouldPromote && !snapshot.Medical.Safety.CoveredSuppressionOpportunity);
    }

    public static bool TryReadTargetHealth(BotOwner? botOwner, string? targetPartName, out float current, out float maximum)
    {
        current = -1f;
        maximum = -1f;
        if (botOwner == null || !Enum.TryParse(targetPartName, ignoreCase: true, out EBodyPart targetPart))
        {
            return false;
        }

        try
        {
            var health = botOwner.HealthController.GetBodyPartHealth(targetPart, false);
            current = health.Current;
            maximum = health.Maximum;
            return maximum > 0f;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadTargetDestroyed(BotOwner? botOwner, string? targetPartName, out bool destroyed)
    {
        destroyed = false;
        if (botOwner == null || !Enum.TryParse(targetPartName, ignoreCase: true, out EBodyPart targetPart))
        {
            return false;
        }

        try
        {
            var healthController = botOwner.GetPlayer?.ActiveHealthController;
            if (healthController == null)
            {
                return false;
            }

            destroyed = healthController.IsBodyPartDestroyed(targetPart);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryReadExactItemState(
        BotOwner? botOwner,
        string? itemInstanceId,
        out bool inventoryObserved,
        out bool itemInstanceFound,
        out bool resourceReadable,
        out float resource)
    {
        inventoryObserved = false;
        itemInstanceFound = false;
        resourceReadable = false;
        resource = -1f;
        if (botOwner == null || string.IsNullOrWhiteSpace(itemInstanceId) || string.Equals(itemInstanceId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var inventory = VanguardMedicalInventoryReader.Capture(botOwner);
        inventoryObserved = inventory.Snapshot.Observed;
        if (!inventoryObserved)
        {
            return;
        }

        foreach (var items in inventory.ItemsByTemplateId.Values)
        {
            foreach (var item in items)
            {
                if (!string.Equals(VanguardMedicalInventoryReader.ResolveItemInstanceId(item), itemInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                itemInstanceFound = true;
                resource = VanguardMedicalInventoryReader.ReadItemResource(item);
                resourceReadable = resource >= 0f;
                return;
            }
        }
    }
}
#endif

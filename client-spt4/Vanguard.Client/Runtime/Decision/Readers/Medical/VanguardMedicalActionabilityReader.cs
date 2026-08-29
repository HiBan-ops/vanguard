#if SPT_CLIENT
using System;
using EFT;
using EFT.InventoryLogic;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Reads and normalizes live evidence for Medical Actionability Reader in the decision snapshot pipeline.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal static class VanguardMedicalActionabilityReader
{
    /// <summary>
    /// The runtime cheap activity probe used before reusing a healthy medical snapshot. It reads only
    /// the native medicine Using flags and performs no inventory scan, CanApplyItem call or
    /// body-part/effect traversal. A positive result forces the complete medical read in the
    /// same decision snapshot.
    /// </summary>
    public static bool IsMedicalActivityObserved(BotOwner? botOwner)
    {
        if (botOwner == null)
        {
            return false;
        }

        object? medicine = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Medecine");
        return ReadBool(medicine, "Using")
            || ReadBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(medicine, "FirstAid"), "Using")
            || ReadBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(medicine, "SurgicalKit"), "Using")
            || ReadBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(medicine, "Stimulators"), "Using");
    }

    public static VanguardMedicalActionabilitySnapshot Capture(BotOwner? botOwner, VanguardMedicalNeedSnapshot need, VanguardMedicalInventoryReadResult inventory)
    {
        if (botOwner == null)
        {
            return new VanguardMedicalActionabilitySnapshot { Classification = "medical_actionability_no_botowner" };
        }

        bool firstAidUsing = ReadBool(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "FirstAid"), "Using");
        bool surgicalKitUsing = ReadBool(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "SurgicalKit"), "Using");
        bool stimulatorUsing = ReadBool(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "Medecine", "Stimulators"), "Using");
        bool anyMedicineUsing = ReadBool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(botOwner, "Medecine"), "Using"))
            || firstAidUsing || surgicalKitUsing || stimulatorUsing;
        bool reloading = ReadBool(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "WeaponManager", "Reload"), "Reloading");
        bool grenadeThrowing = ReadBool(VanguardOperatorRuntimeAuditReflection.GetDeep(botOwner, "WeaponManager", "Grenades"), "ThrowindNow", "ThrowingNow");
        bool handsReady = !anyMedicineUsing && !reloading && !grenadeThrowing;

        if (!need.HasAnyNeed)
        {
            return BaseSnapshot(anyMedicineUsing, firstAidUsing, surgicalKitUsing, stimulatorUsing, reloading, grenadeThrowing, handsReady,
                anyMedicineUsing ? "medical_no_need_but_using_observed" : "medical_no_need");
        }

        string targetPartName = need.TargetPart;
        EBodyPart targetPart = EBodyPart.Common;
        bool targetKnown = need.TargetKnown && TryParseBodyPart(targetPartName, out targetPart);
        if (need.DominantNeed == VanguardMedicalNeed.UntreatableVitalDestroyedPart)
        {
            return new VanguardMedicalActionabilitySnapshot
            {
                ItemCatalogKnown = true,
                RequiredItemAvailable = false,
                PersistentCapabilityAvailable = false,
                HandsReadyForMedicalAction = handsReady,
                TargetKnown = targetKnown,
                TargetPart = targetKnown ? targetPart.ToString() : "none",
                AnyMedicineUsing = anyMedicineUsing,
                FirstAidUsing = firstAidUsing,
                SurgicalKitUsing = surgicalKitUsing,
                StimulatorUsing = stimulatorUsing,
                Reloading = reloading,
                GrenadeThrowing = grenadeThrowing,
                Classification = "medical_terminal_untreatable_vital"
            };
        }

        bool surgeryTargetKnownInvalid = VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need.DominantNeed)
            && targetKnown
            && !VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(targetPart.ToString());
        bool deferApplyProbe = handsReady == false;
        VanguardMedicalItemCapability? capability = null;
        bool itemAvailable = false;
        bool? canApply = surgeryTargetKnownInvalid ? false : null;

        if (!surgeryTargetKnownInvalid)
        {
            foreach (var candidate in VanguardMedicalItemCapabilityResolver.GetCandidates(need.DominantNeed))
            {
                if (!inventory.ItemsByTemplateId.TryGetValue(candidate.TemplateId, out var instances) || instances.Count == 0)
                {
                    continue;
                }

                foreach (MedsItemClass item in instances)
                {
                    float resource = VanguardMedicalInventoryReader.ReadItemResource(item);
                    float maxResource = VanguardMedicalInventoryReader.ReadItemMaxResource(item);
                    if (maxResource > 0f && resource <= 0.01f)
                    {
                        continue;
                    }

                    itemAvailable = true;
                    capability ??= candidate;
                    if (!targetKnown || deferApplyProbe)
                    {
                        // Candidates are priority ordered. Once a usable capability is known,
                        // a deferred/unknown-target snapshot gains no truth by scanning the rest.
                        goto SelectionComplete;
                    }

                    try
                    {
                        if (botOwner.GetPlayer?.HealthController?.CanApplyItem(item, targetPart) == true)
                        {
                            capability = candidate;
                            canApply = true;
                            goto SelectionComplete;
                        }
                        canApply ??= false;
                    }
                    catch
                    {
                        // Keep scanning other usable instances. Read-model failures never mutate gameplay.
                    }
                }
            }
        }

SelectionComplete:
        bool persistentCapability = itemAvailable && targetKnown && !surgeryTargetKnownInvalid;
        return new VanguardMedicalActionabilitySnapshot
        {
            ItemCatalogKnown = true,
            RequiredItemAvailable = itemAvailable,
            SelectedItemName = itemAvailable && capability != null ? capability.Name : "none",
            SelectedItemTemplateId = itemAvailable && capability != null ? capability.TemplateId : "none",
            SelectedItemRole = itemAvailable && capability != null ? capability.Role.ToString() : "none",
            SelectedItemActionKind = itemAvailable && capability != null ? capability.ActionKind : "none",
            SelectedItemNotes = itemAvailable && capability != null ? capability.Notes : "none",
            TargetKnown = targetKnown,
            TargetPart = targetKnown ? targetPart.ToString() : "none",
            CanApplyItem = canApply,
            PersistentCapabilityAvailable = persistentCapability,
            HandsReadyForMedicalAction = handsReady,
            CanApplyProbeDeferredByHands = deferApplyProbe && persistentCapability,
            AnyMedicineUsing = anyMedicineUsing,
            FirstAidUsing = firstAidUsing,
            SurgicalKitUsing = surgicalKitUsing,
            StimulatorUsing = stimulatorUsing,
            Reloading = reloading,
            GrenadeThrowing = grenadeThrowing,
            Classification = Classify(need, itemAvailable, targetKnown, canApply, anyMedicineUsing, reloading, grenadeThrowing, deferApplyProbe)
        };
    }

    private static VanguardMedicalActionabilitySnapshot BaseSnapshot(bool anyUsing, bool firstAid, bool surgery, bool stim, bool reloading, bool grenade, bool handsReady, string classification)
    {
        return new VanguardMedicalActionabilitySnapshot
        {
            ItemCatalogKnown = true,
            RequiredItemAvailable = false,
            PersistentCapabilityAvailable = false,
            HandsReadyForMedicalAction = handsReady,
            TargetKnown = false,
            AnyMedicineUsing = anyUsing,
            FirstAidUsing = firstAid,
            SurgicalKitUsing = surgery,
            StimulatorUsing = stim,
            Reloading = reloading,
            GrenadeThrowing = grenade,
            Classification = classification
        };
    }

    private static string Classify(VanguardMedicalNeedSnapshot need, bool itemAvailable, bool targetKnown, bool? canApply, bool anyUsing, bool reloading, bool grenadeThrowing, bool probeDeferred)
    {
        if (!need.IsReadable) return "medical_unreadable";
        if (!need.HasAnyNeed) return anyUsing ? "medical_using_without_typed_need" : "medical_no_need";
        if (need.DominantNeed == VanguardMedicalNeed.UntreatableVitalDestroyedPart) return "medical_terminal_untreatable_vital";
        if (anyUsing) return "medical_busy_using";
        if (reloading) return "medical_blocked_reloading_transient";
        if (grenadeThrowing) return "medical_blocked_grenade_transient";
        if (!targetKnown) return "medical_target_unknown";
        if (VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need.DominantNeed) && VanguardMedicalSurgeryTargetPolicy.IsUntreatableVitalTarget(need.TargetPart)) return "medical_untreatable_vital_part";
        if (VanguardMedicalSurgeryTargetPolicy.IsSurgeryNeed(need.DominantNeed) && !VanguardMedicalSurgeryTargetPolicy.IsValidSurgeryTarget(need.TargetPart)) return "medical_invalid_surgery_target";
        if (!itemAvailable) return "medical_item_missing";
        if (probeDeferred) return "medical_capability_ready_hands_transient";
        if (canApply == false) return "medical_controller_rejected";
        if (canApply == true) return "medical_item_ready";
        return "medical_item_available_unverified";
    }

    private static bool TryParseBodyPart(string? text, out EBodyPart part)
    {
        if (Enum.TryParse(text, ignoreCase: true, out part)) return true;
        part = EBodyPart.Common;
        return false;
    }

    private static bool ReadBool(object? target, params string[] names)
    {
        if (names.Length == 0) return target is bool b && b;
        object? value = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(target, names);
        return value is bool boolValue && boolValue;
    }
}
#endif

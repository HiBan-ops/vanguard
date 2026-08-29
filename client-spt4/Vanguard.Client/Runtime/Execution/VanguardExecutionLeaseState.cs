#if SPT_CLIENT
using System;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Defines data/state contracts used by the execution arbitration runtime, centered on Execution Lease State.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Execution;

internal sealed class VanguardExecutionLeaseState
{
    public string LeaseId { get; init; } = string.Empty;
    public string OperatorId { get; init; } = string.Empty;
    public string BotProfileId { get; init; } = string.Empty;
    public string IntentKey { get; init; } = string.Empty;
    public string WindowKind { get; init; } = string.Empty;
    public VanguardMedicalNeed MedicalNeed { get; init; } = VanguardMedicalNeed.None;
    public string TargetPart { get; init; } = "none";
    public string ItemTemplateId { get; init; } = "none";
    public string ItemInstanceId { get; init; } = "none";
    public float InitialItemResource { get; init; } = -1f;
    public float InitialItemMaxResource { get; init; } = -1f;
    public string ItemName { get; init; } = "none";
    public int InitialHealthPercent { get; init; } = -1;
    public float InitialTargetHealth { get; init; } = -1f;
    public float InitialTargetMaxHealth { get; init; } = -1f;
    public string InitialNeedTargetPart { get; init; } = "none";
    public string EffectSignature { get; init; } = "none";
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset MinUntilUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset MaxUntilUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AbsoluteMaxUntilUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NoProgressUntilUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastProgressAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset PostUseRecheckUntilUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset NextPostUseRecheckAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastUsingHeartbeatLogAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastControllerActivityAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastSurgeryApplyAttemptAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int SurgeryApplyAttemptCount { get; set; }
    public bool SurgeryControllerCallbackObserved { get; set; }
    public DateTimeOffset SurgeryControllerCallbackAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool SurgeryNativeHandsCommitObserved { get; set; }
    public DateTimeOffset SurgeryNativeHandsCommitObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset SurgeryNativeHandsMismatchSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastSurgeryNativeHandsSnapshotAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int SurgeryNativeHandsMismatchSnapshotCount { get; set; }
    public string LastSurgeryNativeHandsSummary { get; set; } = "none";
    public bool SurgeryStartRetryPending { get; set; }
    public DateTimeOffset SurgeryStartRetryRequestedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int SurgeryStartRetryCount { get; set; }
    public string SurgeryStartRetryReason { get; set; } = "none";
    public bool SurgeryResourceCommitObserved { get; set; }
    public DateTimeOffset SurgeryResourceCommitObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool SurgeryTerminalItemDepletionCommitObserved { get; set; }
    public DateTimeOffset SurgeryTerminalItemDepletionCommitObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset SurgeryTerminalItemAbsenceSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastSurgeryTerminalItemAbsenceSnapshotAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int SurgeryTerminalItemAbsenceSnapshotCount { get; set; }
    public bool SurgeryTargetEffectConfirmed { get; set; }
    public DateTimeOffset SurgeryTargetEffectConfirmedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public float SurgeryFallbackHealthPenalty { get; init; } = -1f;
    public bool SurgeryFallbackRepairApplied { get; set; }
    public DateTimeOffset SurgeryFallbackRepairAppliedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string SurgeryFallbackRepairReason { get; set; } = "none";
    public bool SurgeryCancellationRequested { get; set; }
    public DateTimeOffset SurgeryCancellationRequestedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string SurgeryCancellationReason { get; set; } = string.Empty;
    public string SurgeryCancellationKind { get; set; } = string.Empty;
    public bool SurgeryCancellationIsThreat { get; set; }
    public bool FirstAidCancellationRequested { get; set; }
    public DateTimeOffset FirstAidCancellationRequestedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string FirstAidCancellationReason { get; set; } = string.Empty;
    public string FirstAidCancellationKind { get; set; } = string.Empty;
    public bool FirstAidCancellationIsThreat { get; set; }
    public bool FirstAidStartStallObserved { get; set; }
    public DateTimeOffset FirstAidUsingObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool NativeStartPendingReconciliation { get; set; }
    public DateTimeOffset NativeStartPendingSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset NativeStartPendingUntilUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastNativeStartPendingSnapshotAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int NativeStartPendingSnapshotCount { get; set; }
    public bool NativeStartLateCommitObserved { get; set; }
    public DateTimeOffset NativeStartLateCommitObservedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string NativeStartPendingReason { get; set; } = "none";
    public DateTimeOffset NativeCancelHandsReadySinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastNativeCancelHandsSnapshotAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int NativeCancelHandsReadySnapshotCount { get; set; }
    public bool NativeCancelHandsRecoveryAttempted { get; set; }
    public string LastNativeCancelHandsReadiness { get; set; } = "none";
    public bool SurgeryStationaryAnchorCaptured { get; set; }
    public float SurgeryStationaryAnchorX { get; set; }
    public float SurgeryStationaryAnchorZ { get; set; }
    public DateTimeOffset SurgeryMovementViolationSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastSurgeryMovementSampleAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool LastSurgeryMovementPositionCaptured { get; set; }
    public float LastSurgeryMovementX { get; set; }
    public float LastSurgeryMovementZ { get; set; }
    public bool StationaryPostureObserved { get; set; }
    public bool Attempted { get; set; }
    public bool ItemUseObserved { get; set; }
    public bool CompletionObserved { get; set; }
    public bool ControllerUsingGraceApplied { get; set; }
    public int NoEffectConfirmationCount { get; set; }
    public DateTimeOffset LastNoEffectConfirmationAtUtc { get; set; } = DateTimeOffset.MinValue;
    public bool FirstAidEndedObserved { get; set; }
    public bool EffectResolvedAwaitingHandsRelease { get; set; }
    public DateTimeOffset HandsDrainStartedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset HandsReleasedSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastHandsDrainSnapshotAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int HandsReleasedSnapshotCount { get; set; }
    public bool HandsDrainRecoveryAttempted { get; set; }
    public int PostUseRecheckCount { get; set; }
    public bool ThreatObservedDuringLease { get; set; }
    public DateTimeOffset SurgeryPrepareReadySinceUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastSurgeryPrepareReadySnapshotAtUtc { get; set; } = DateTimeOffset.MinValue;
    public int SurgeryPrepareReadySnapshotCount { get; set; }
    public DateTimeOffset SurgeryPrepareLaunchBlockedSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public string SurgeryPrepareLaunchBlockReason { get; set; } = "none";
    public DateTimeOffset SurgeryPrepareSoftThreatSinceUtc { get; set; } = DateTimeOffset.MinValue;
    public bool SurgeryPrepareOwnerLeashBypassed { get; set; }
    public bool PrepareProgressObserved { get; set; }
    public bool MedicalIsolationAcquired { get; set; }
    public DateTimeOffset MedicalIsolationStartedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string MedicalIsolationPhase { get; set; } = "None";
    public string LastMedicalIsolationSummary { get; set; } = "none";
    public string LastProgressKind { get; set; } = "none";

    public string CooldownKey => VanguardExecutionLeaseStore.BuildCooldownKey(BotProfileId, MedicalNeed, TargetPart, ItemTemplateId, ItemInstanceId);

    public string Summary => "lease=" + Safe(LeaseId)
        + ";operator=" + Safe(OperatorId)
        + ";botProfile=" + Safe(BotProfileId)
        + ";intent=" + Safe(IntentKey)
        + ";window=" + Safe(WindowKind)
        + ";need=" + MedicalNeed
        + ";target=" + Safe(TargetPart)
        + ";item=" + Safe(ItemName)
        + ";tpl=" + Safe(ItemTemplateId)
        + ";itemInstance=" + Safe(ItemInstanceId)
        + ";itemResource0=" + InitialItemResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + "/" + InitialItemMaxResource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + ";hp0=" + InitialHealthPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";targetHp0=" + InitialTargetHealth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + "/" + InitialTargetMaxHealth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + ";initialNeedTarget=" + Safe(InitialNeedTargetPart)
        + ";effectSignature=" + Safe(EffectSignature)
        + ";attempted=" + Bool(Attempted)
        + ";usingObserved=" + Bool(ItemUseObserved)
        + ";useEnded=" + Bool(FirstAidEndedObserved)
        + ";handsDrain=" + Bool(EffectResolvedAwaitingHandsRelease)
        + ";handsReleaseSnapshots=" + HandsReleasedSnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";handsRecovery=" + Bool(HandsDrainRecoveryAttempted)
        + ";usingGrace=" + Bool(ControllerUsingGraceApplied)
        + ";absoluteMax=" + AbsoluteMaxUntilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + ";noEffectConfirmations=" + NoEffectConfirmationCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";rechecks=" + PostUseRecheckCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";threatDuring=" + Bool(ThreatObservedDuringLease)
        + ";surgeryPrepareReadySince=" + SurgeryPrepareReadySinceUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryPrepareReadySnapshots=" + SurgeryPrepareReadySnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryPrepareLaunchBlockedSince=" + SurgeryPrepareLaunchBlockedSinceUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryPrepareLaunchBlock=" + Safe(SurgeryPrepareLaunchBlockReason)
        + ";surgeryPrepareSoftThreatSince=" + SurgeryPrepareSoftThreatSinceUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryPrepareOwnerLeashBypassed=" + Bool(SurgeryPrepareOwnerLeashBypassed)
        + ";prepareProgress=" + Bool(PrepareProgressObserved)
        + ";isolation=" + Bool(MedicalIsolationAcquired)
        + ";isolationPhase=" + Safe(MedicalIsolationPhase)
        + ";lastIsolation=" + Safe(LastMedicalIsolationSummary)
        + ";lastProgress=" + Safe(LastProgressKind)
        + ";surgeryApplyAttempts=" + SurgeryApplyAttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryCallback=" + Bool(SurgeryControllerCallbackObserved)
        + ";surgeryNativeHandsCommit=" + Bool(SurgeryNativeHandsCommitObserved)
        + ";surgeryNativeHandsMismatchSnapshots=" + SurgeryNativeHandsMismatchSnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryNativeHands=" + Safe(LastSurgeryNativeHandsSummary)
        + ";surgeryStartRetryPending=" + Bool(SurgeryStartRetryPending)
        + ";surgeryStartRetryCount=" + SurgeryStartRetryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryStartRetryReason=" + Safe(SurgeryStartRetryReason)
        + ";surgeryResourceCommit=" + Bool(SurgeryResourceCommitObserved)
        + ";surgeryTerminalItemCommit=" + Bool(SurgeryTerminalItemDepletionCommitObserved)
        + ";surgeryTerminalItemAbsenceSnapshots=" + SurgeryTerminalItemAbsenceSnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryEffectConfirmed=" + Bool(SurgeryTargetEffectConfirmed)
        + ";surgeryPenalty=" + SurgeryFallbackHealthPenalty.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
        + ";surgeryRepair=" + Bool(SurgeryFallbackRepairApplied)
        + ";surgeryRepairReason=" + Safe(SurgeryFallbackRepairReason)
        + ";surgeryCancelRequested=" + Bool(SurgeryCancellationRequested)
        + ";surgeryCancelKind=" + Safe(SurgeryCancellationKind)
        + ";firstAidCancelRequested=" + Bool(FirstAidCancellationRequested)
        + ";firstAidCancelReason=" + Safe(FirstAidCancellationReason)
        + ";firstAidCancelKind=" + Safe(FirstAidCancellationKind)
        + ";firstAidCancelThreat=" + Bool(FirstAidCancellationIsThreat)
        + ";firstAidStartStall=" + Bool(FirstAidStartStallObserved)
        + ";firstAidUsingObservedAt=" + FirstAidUsingObservedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + ";nativeStartPending=" + Bool(NativeStartPendingReconciliation)
        + ";nativeStartPendingSince=" + NativeStartPendingSinceUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + ";nativeStartPendingUntil=" + NativeStartPendingUntilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + ";nativeStartPendingSnapshots=" + NativeStartPendingSnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";nativeStartLateCommit=" + Bool(NativeStartLateCommitObserved)
        + ";nativeStartPendingReason=" + Safe(NativeStartPendingReason)
        + ";nativeCancelHandsSnapshots=" + NativeCancelHandsReadySnapshotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ";nativeCancelHandsReadySince=" + NativeCancelHandsReadySinceUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        + ";nativeCancelHandsRecovery=" + Bool(NativeCancelHandsRecoveryAttempted)
        + ";nativeCancelHandsReadiness=" + Safe(LastNativeCancelHandsReadiness)
        + ";surgeryAnchor=" + Bool(SurgeryStationaryAnchorCaptured)
        + ";postureObserved=" + Bool(StationaryPostureObserved);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}
#endif

#if SPT_CLIENT
using System;
using System.Globalization;

// Responsibility: Defines data/state contracts used by the execution arbitration runtime, centered on Primary Execution Window State.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.Execution;

internal sealed class VanguardPrimaryExecutionWindowState
{
    public string WindowId = "none";
    public string OperatorId = "none";
    public string BotProfileId = "none";
    public string WindowKind = "none";
    public string State = "none";
    public string IntentKey = "none";
    public string Domain = "none";
    public string Reason = "none";
    public string TargetKey = "none";
    public string PlanKey = "none";
    public string NextStep = "none";
    public float Score;
    public DateTimeOffset StartedAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset MinUntilUtc = DateTimeOffset.MinValue;
    public DateTimeOffset MaxUntilUtc = DateTimeOffset.MinValue;
    public DateTimeOffset HardUntilUtc = DateTimeOffset.MinValue;
    public DateTimeOffset AbsoluteUntilUtc = DateTimeOffset.MinValue;
    public DateTimeOffset NoProgressUntilUtc = DateTimeOffset.MinValue;
    public DateTimeOffset LastProgressAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset LastObservedAtUtc = DateTimeOffset.MinValue;
    public string LastProgressKind = "none";
    public string BackendLeaseId = "none";
    public string BackendSummary = "none";
    public int SegmentIndex = 1;
    public int TargetGeneration = 1;
    public string PreviousTargetKey = "none";
    public DateTimeOffset PreviousTargetRetryAfterUtc = DateTimeOffset.MinValue;
    public string CommittedTargetKey = "none";
    public string CommittedTargetSource = "none";
    public string LastTargetTransitionSignature = "none";
    public DateTimeOffset LastTargetTransitionAtUtc = DateTimeOffset.MinValue;
    public string TargetApplicationState = "none";
    public DateTimeOffset LastTargetAppliedAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset LastTargetVerifiedAtUtc = DateTimeOffset.MinValue;
    public DateTimeOffset LastTargetVerificationAttemptAtUtc = DateTimeOffset.MinValue;
    public int TargetRepairAttempts;
    public DateTimeOffset TargetMissingSinceUtc = DateTimeOffset.MinValue;
    public int TargetMissingSnapshotCount;
    public string LastTargetLivenessReason = "none";

    public bool IsActive(DateTimeOffset now)
    {
        if (AbsoluteUntilUtc != DateTimeOffset.MinValue && AbsoluteUntilUtc <= now)
        {
            return false;
        }

        return MaxUntilUtc == DateTimeOffset.MinValue || MaxUntilUtc > now;
    }
    public bool IsGrenadeEmergency => string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.EmergencyGrenadeEvasion, StringComparison.OrdinalIgnoreCase);
    public bool IsHardReturn => string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.HardReturnMovement, StringComparison.OrdinalIgnoreCase);
    public bool IsTacticalMovement => string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.TacticalMovement, StringComparison.OrdinalIgnoreCase);
    public bool IsAuthoringPreview => string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.AuthoringPreviewMovement, StringComparison.OrdinalIgnoreCase);
    public bool IsCloseCohesionMovement => string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.CloseCohesionMovement, StringComparison.OrdinalIgnoreCase);
    public bool IsMedical => WindowKind.IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0;
    public bool IsCorpseLoot => string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.CorpseLoot, StringComparison.OrdinalIgnoreCase);
    public bool IsWorldContainerLoot => string.Equals(WindowKind, VanguardPrimaryExecutionWindowKinds.WorldContainerLoot, StringComparison.OrdinalIgnoreCase);
    public bool IsOpportunisticLoot => IsCorpseLoot || IsWorldContainerLoot;

    public string Summary => "window=" + Safe(WindowId)
        + ";operator=" + Safe(OperatorId)
        + ";botProfile=" + Safe(BotProfileId)
        + ";kind=" + Safe(WindowKind)
        + ";state=" + Safe(State)
        + ";intent=" + Safe(IntentKey)
        + ";domain=" + Safe(Domain)
        + ";score=" + Score.ToString("0.00", CultureInfo.InvariantCulture)
        + ";reason=" + Safe(Reason)
        + ";target=" + Safe(TargetKey)
        + ";plan=" + Safe(PlanKey)
        + ";next=" + Safe(NextStep)
        + ";backendLease=" + Safe(BackendLeaseId)
        + ";progress=" + Safe(LastProgressKind)
        + ";backend=" + Safe(BackendSummary)
        + ";segment=" + SegmentIndex.ToString(CultureInfo.InvariantCulture)
        + ";targetGeneration=" + TargetGeneration.ToString(CultureInfo.InvariantCulture)
        + ";previousTarget=" + Safe(PreviousTargetKey)
        + ";previousRetryAfter=" + FormatTime(PreviousTargetRetryAfterUtc)
        + ";committedTarget=" + Safe(CommittedTargetKey)
        + ";committedSource=" + Safe(CommittedTargetSource)
        + ";transitionSignature=" + Safe(LastTargetTransitionSignature)
        + ";transitionAt=" + FormatTime(LastTargetTransitionAtUtc)
        + ";targetApplyState=" + Safe(TargetApplicationState)
        + ";targetAppliedAt=" + FormatTime(LastTargetAppliedAtUtc)
        + ";targetVerifiedAt=" + FormatTime(LastTargetVerifiedAtUtc)
        + ";targetVerifyAttemptAt=" + FormatTime(LastTargetVerificationAttemptAtUtc)
        + ";targetRepairAttempts=" + TargetRepairAttempts.ToString(CultureInfo.InvariantCulture)
        + ";targetMissingSince=" + FormatTime(TargetMissingSinceUtc)
        + ";targetMissingCount=" + TargetMissingSnapshotCount.ToString(CultureInfo.InvariantCulture)
        + ";targetLiveness=" + Safe(LastTargetLivenessReason)
        + ";hardUntil=" + FormatTime(HardUntilUtc)
        + ";absoluteUntil=" + FormatTime(AbsoluteUntilUtc);

    private static string FormatTime(DateTimeOffset value)
    {
        return value == DateTimeOffset.MinValue ? "none" : value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}

internal static class VanguardPrimaryExecutionWindowKinds
{
    public const string EmergencyGrenadeEvasion = "EmergencyGrenadeEvasionWindow";
    public const string HardReturnMovement = "HardReturnMovementWindow";
    public const string TacticalMovement = "TacticalMovementWindow";
    public const string AuthoringPreviewMovement = "TacticalAuthoringPreviewMovementWindow";
    public const string CloseCohesionMovement = "CloseCohesionMovementWindow";
    public const string StationaryMedical = "StationaryMedicalWindow";
    public const string MobileMedical = "MobileMedicalWindow";
    public const string SainCombatRelease = "SainCombatReleaseWindow";
    public const string Rejoin = "RejoinWindow";
    public const string CorpseLoot = "CorpseLootApproachWindow";
    public const string WorldContainerLoot = "WorldContainerLootApproachWindow";
    public const string Recovery = "RecoveryWindow";
}
#endif

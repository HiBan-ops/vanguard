#if SPT_CLIENT
using System;
using UnityEngine;

// Responsibility: Defines data/state contracts used by the external-authority integration, centered on External Activity Snapshot.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.External;

internal enum VanguardExternalMovementOwner
{
    None,
    VanguardOrIdle,
    LootingBots,
    Orbit,
    ExternalPathResidue,
    SainExtract,
    SainCombat,
    Unknown
}

internal enum VanguardExternalPreemptOutcome
{
    Granted,
    Pending,
    RejectedCombatOwner,
    FailedNoAuthority,
    FailedLootingBotsStillActive,
    FailedOrbitStillActive,
    FailedPathStillActive,
    FailedMoverBusy,
    FailedBotOwnerMissing
}

internal sealed class VanguardExternalActivitySnapshot
{
    public static VanguardExternalActivitySnapshot Empty { get; } = new();

    public string OperatorId { get; init; } = "none";
    public string BotProfileId { get; init; } = "none";
    public bool BotOwnerPresent { get; init; }
    public bool LootingBotsComponentPresent { get; init; }
    public bool LootingBotsExternalApiPresent { get; init; }
    public bool LootingBotsActive { get; init; }
    public bool LootingBotsTaskRunning { get; init; }
    public bool LootingBotsHasActiveLootable { get; init; }
    public string LootingBotsClassification { get; init; } = "none";
    public bool OrbitTelemetryAvailable { get; init; }
    public bool OrbitActive { get; init; }
    public bool OrbitBrainLayerActive { get; init; }
    public bool OrbitSemanticActive { get; init; }
    public bool OrbitLayerIdleQuiesced { get; init; }
    public string OrbitStatus { get; init; } = "none";
    public string OrbitCategory { get; init; } = "none";
    public string OrbitClassification { get; init; } = "none";
    public string OrbitExtractReason { get; init; } = "none";
    public Vector3? OrbitObjective { get; init; }
    public bool EftPathActive { get; init; }
    public float? PathRemainingDistance { get; init; }
    public bool MoverMoving { get; init; }
    public float RealSpeed { get; init; }
    public bool SainExtractLikely { get; init; }
    public string SainExtractReason { get; init; } = "none";
    public bool SainCombatLikely { get; init; }
    public bool SainCombatStaleNonActionable { get; init; }
    public string SainCombatStaleReason { get; init; } = "none";
    public bool DirectThreatLikely { get; init; }
    public VanguardExternalMovementOwner MovementOwner { get; init; } = VanguardExternalMovementOwner.Unknown;
    public string BlockingReason { get; init; } = "none";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool BlocksMedicalPrepare => MovementOwner == VanguardExternalMovementOwner.LootingBots
        || MovementOwner == VanguardExternalMovementOwner.Orbit
        || MovementOwner == VanguardExternalMovementOwner.ExternalPathResidue
        || MovementOwner == VanguardExternalMovementOwner.SainExtract
        || LootingBotsActive
        || LootingBotsTaskRunning
        || LootingBotsHasActiveLootable
        || IsOrbitObjectiveResidue
        || IsPathResidue;

    public bool IsCombatOwned => MovementOwner == VanguardExternalMovementOwner.SainCombat || DirectThreatLikely || (SainCombatLikely && !SainCombatStaleNonActionable);
    public bool CanDriveMedicalMovement => BotOwnerPresent && !IsCombatOwned && !BlocksMedicalPrepare;

    public bool IsOrbitObjectiveResidue
    {
        get
        {
            string text = (OrbitStatus + "|" + OrbitCategory + "|" + OrbitClassification + "|" + OrbitExtractReason).ToLowerInvariant();
            bool finishedOrIdle = text.Contains("idle")
                || text.Contains("quiesc")
                || text.Contains("finished")
                || text.Contains("complete")
                || text.Contains("completed")
                || text.Contains("done")
                || text.Contains("success")
                || text.Contains("failed");
            return OrbitActive && !finishedOrIdle && (text.Contains("loot")
                || text.Contains("corpse")
                || text.Contains("container")
                || text.Contains("loose")
                || text.Contains("moving")
                || text.Contains("extract")
                || text.Contains("orbit_moving")
                || text.Contains("objective"));
        }
    }

    public bool IsPathResidue => EftPathActive && PathRemainingDistance.HasValue && PathRemainingDistance.Value > 1.00f;

    public string Summary => "externalActivity=botOwner=" + Bool(BotOwnerPresent)
        + ";owner=" + MovementOwner
        + ";blocking=" + Bool(BlocksMedicalPrepare)
        + ";combatOwned=" + Bool(IsCombatOwned)
        + ";lootApi=" + Bool(LootingBotsExternalApiPresent)
        + ";lootComp=" + Bool(LootingBotsComponentPresent)
        + ";lootActive=" + Bool(LootingBotsActive)
        + ";lootTask=" + Bool(LootingBotsTaskRunning)
        + ";lootable=" + Bool(LootingBotsHasActiveLootable)
        + ";lootClass=" + Safe(LootingBotsClassification)
        + ";orbitTelemetry=" + Bool(OrbitTelemetryAvailable)
        + ";orbitActive=" + Bool(OrbitActive)
        + ";orbitLayer=" + Bool(OrbitBrainLayerActive)
        + ";orbitSemantic=" + Bool(OrbitSemanticActive)
        + ";orbitLayerIdleQuiesced=" + Bool(OrbitLayerIdleQuiesced)
        + ";orbitStatus=" + Safe(OrbitStatus)
        + ";orbitCategory=" + Safe(OrbitCategory)
        + ";orbitClass=" + Safe(OrbitClassification)
        + ";path=" + Bool(EftPathActive)
        + ";pathDist=" + Float(PathRemainingDistance)
        + ";moving=" + Bool(MoverMoving)
        + ";speed=" + RealSpeed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        + ";sainExtract=" + Bool(SainExtractLikely)
        + ";sainExtractReason=" + Safe(SainExtractReason)
        + ";sainStale=" + Bool(SainCombatStaleNonActionable)
        + ";sainStaleReason=" + Safe(SainCombatStaleReason)
        + ";reason=" + Safe(BlockingReason);

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Float(float? value) => value.HasValue ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "none";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

internal readonly struct VanguardExternalPreemptResult
{
    public VanguardExternalPreemptResult(VanguardExternalPreemptOutcome outcome, VanguardExternalActivitySnapshot before, VanguardExternalActivitySnapshot after, string mutationSummary, string reason)
    {
        Outcome = outcome;
        Before = before ?? VanguardExternalActivitySnapshot.Empty;
        After = after ?? VanguardExternalActivitySnapshot.Empty;
        MutationSummary = string.IsNullOrWhiteSpace(mutationSummary) ? "none" : mutationSummary;
        Reason = string.IsNullOrWhiteSpace(reason) ? "none" : reason;
    }

    public VanguardExternalPreemptOutcome Outcome { get; }
    public VanguardExternalActivitySnapshot Before { get; }
    public VanguardExternalActivitySnapshot After { get; }
    public string MutationSummary { get; }
    public string Reason { get; }
    public bool CanDriveMovement => Outcome == VanguardExternalPreemptOutcome.Granted;
    public bool IsCombatDefer => Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner;
    public bool ShouldFail => Outcome == VanguardExternalPreemptOutcome.RejectedCombatOwner
        || Outcome == VanguardExternalPreemptOutcome.FailedNoAuthority
        || Outcome == VanguardExternalPreemptOutcome.FailedBotOwnerMissing;

    public string CompactSummary => "externalPreempt=" + Outcome
        + ";reason=" + Safe(Reason)
        + ";canDriveMovement=" + Bool(CanDriveMovement)
        + ";" + MutationSummary
        + ";beforeOwner=" + Before.MovementOwner
        + ";beforeBlocking=" + Bool(Before.BlocksMedicalPrepare)
        + ";beforeReason=" + Safe(Before.BlockingReason)
        + ";afterOwner=" + After.MovementOwner
        + ";afterBlocking=" + Bool(After.BlocksMedicalPrepare)
        + ";afterReason=" + Safe(After.BlockingReason);

    public string Summary => CompactSummary
        + ";before={" + Before.Summary + "}"
        + ";after={" + After.Summary + "}";

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

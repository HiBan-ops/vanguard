#if SPT_CLIENT
using EFT;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Reads and normalizes live evidence for Medical Terminal Truth Reader in the medical runtime.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Medical.Execution;

internal enum VanguardMedicalTerminalTruthKind
{
    AliveConfirmed = 0,
    DeadConfirmed = 1,
    TerminalUnknown = 2,
}

internal readonly struct VanguardMedicalTerminalTruthSnapshot
{
    public VanguardMedicalTerminalTruthSnapshot(VanguardMedicalTerminalTruthKind kind, string reason, bool registryBound, bool ownerAlive, bool playerAlive, bool snapshotAlive)
    {
        Kind = kind;
        Reason = reason;
        RegistryBound = registryBound;
        OwnerAlive = ownerAlive;
        PlayerAlive = playerAlive;
        SnapshotAlive = snapshotAlive;
    }

    public VanguardMedicalTerminalTruthKind Kind { get; }
    public string Reason { get; }
    public bool RegistryBound { get; }
    public bool OwnerAlive { get; }
    public bool PlayerAlive { get; }
    public bool SnapshotAlive { get; }
    public bool AliveConfirmed => Kind == VanguardMedicalTerminalTruthKind.AliveConfirmed;
    public bool DeadConfirmed => Kind == VanguardMedicalTerminalTruthKind.DeadConfirmed;
    public bool TerminalUnknown => Kind == VanguardMedicalTerminalTruthKind.TerminalUnknown;

    public string Summary => "terminal=" + Kind
        + ";terminalReason=" + Safe(Reason)
        + ";registryBound=" + Bool(RegistryBound)
        + ";ownerAlive=" + Bool(OwnerAlive)
        + ";playerAlive=" + Bool(PlayerAlive)
        + ";snapshotAlive=" + Bool(SnapshotAlive);

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');
}

/// <summary>
/// The runtime terminal truth guard. Medical completion is allowed only while the raid registry,
/// BotOwner/player health controllers and the latest decision snapshot still describe a live
/// Operator. A missing/disposed runtime is terminal; contradictory or unreadable health state is
/// conservatively non-success so a stale need disappearance can never be committed as healing.
/// </summary>
internal static class VanguardMedicalTerminalTruthReader
{
    public const string StatusTag = "VANGUARD_MEDICAL_TERMINAL_TRUTH_STATUS";

    public static VanguardMedicalTerminalTruthSnapshot Capture(string botProfileId, BotOwner? suppliedOwner, OperatorDecisionSnapshot? snapshot)
    {
        bool registryBound = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfileId, out var runtime)
            && runtime.BotOwner != null;
        BotOwner? owner = registryBound ? runtime.BotOwner : suppliedOwner;

        if (!registryBound)
        {
            return new VanguardMedicalTerminalTruthSnapshot(
                VanguardMedicalTerminalTruthKind.DeadConfirmed,
                "runtime_registry_missing",
                registryBound: false,
                ownerAlive: false,
                playerAlive: false,
                snapshotAlive: snapshot?.Alive == true);
        }

        if (owner == null)
        {
            return new VanguardMedicalTerminalTruthSnapshot(
                VanguardMedicalTerminalTruthKind.DeadConfirmed,
                "botowner_missing",
                registryBound: true,
                ownerAlive: false,
                playerAlive: false,
                snapshotAlive: snapshot?.Alive == true);
        }

        bool ownerIsDead;
        try
        {
            ownerIsDead = owner.IsDead;
        }
        catch
        {
            return Unknown(registryBound, snapshot, "botowner_terminal_read_failed");
        }

        bool? ownerHealthAlive = null;
        bool? playerHealthAlive = null;
        try
        {
            ownerHealthAlive = owner.HealthController?.IsAlive;
        }
        catch
        {
            // A second direct health source may still establish terminal truth.
        }

        try
        {
            playerHealthAlive = owner.GetPlayer?.HealthController?.IsAlive;
        }
        catch
        {
            // Conservatively handled below if no direct source remains readable.
        }

        bool snapshotAlive = snapshot?.Alive == true;
        if (ownerIsDead || ownerHealthAlive == false || playerHealthAlive == false || (snapshot != null && !snapshotAlive))
        {
            string reason = ownerIsDead ? "botowner_dead"
                : ownerHealthAlive == false ? "owner_health_controller_dead"
                : playerHealthAlive == false ? "player_health_controller_dead"
                : "decision_snapshot_dead";
            return new VanguardMedicalTerminalTruthSnapshot(
                VanguardMedicalTerminalTruthKind.DeadConfirmed,
                reason,
                registryBound: true,
                ownerAlive: ownerHealthAlive == true,
                playerAlive: playerHealthAlive == true,
                snapshotAlive: snapshotAlive);
        }

        bool directAlive = ownerHealthAlive == true || playerHealthAlive == true;
        if (directAlive && (snapshot == null || snapshotAlive))
        {
            return new VanguardMedicalTerminalTruthSnapshot(
                VanguardMedicalTerminalTruthKind.AliveConfirmed,
                "direct_health_alive",
                registryBound: true,
                ownerAlive: ownerHealthAlive == true,
                playerAlive: playerHealthAlive == true,
                snapshotAlive: snapshot == null || snapshotAlive);
        }

        return Unknown(registryBound, snapshot, "health_controller_unreadable");
    }

    private static VanguardMedicalTerminalTruthSnapshot Unknown(bool registryBound, OperatorDecisionSnapshot? snapshot, string reason)
    {
        return new VanguardMedicalTerminalTruthSnapshot(
            VanguardMedicalTerminalTruthKind.TerminalUnknown,
            reason,
            registryBound,
            ownerAlive: false,
            playerAlive: false,
            snapshotAlive: snapshot?.Alive == true);
    }
}
#endif

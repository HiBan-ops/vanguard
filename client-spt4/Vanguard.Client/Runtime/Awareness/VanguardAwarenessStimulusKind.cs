#if SPT_CLIENT

// Responsibility: Provides Awareness Stimulus Kind support for the combat-awareness runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Awareness;

internal enum VanguardAwarenessStimulusKind
{
    None = 0,
    ConfirmedCurrentThreat = 1,
    ConfirmedSecondaryThreat = 2,
    IncomingFireFresh = 3,
    IncomingFireStale = 4,
    VisibleContact = 5,
    LineOfSightContact = 6,
    CanShootContact = 7,
    SuspiciousKnownContact = 8,
    ResidualKnownThreat = 9,
    StaleThreat = 10,
    TerminalDead = 11,
    ScannerUnavailable = 12,
}
#endif

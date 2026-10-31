#if SPT_CLIENT

// Responsibility: Define the discriminants used to classify Operator combat-awareness stimuli.
// Flow: Carry distinct enum values for threats, incoming fire, visibility, stale contacts, death and scanner availability.
// Authority boundary: This enum is shared vocabulary for awareness consumers and owns no threat detection or combat decisions.
// Invariant: Existing numeric values remain stable for producers and consumers of awareness snapshots.
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

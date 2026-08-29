// Responsibility: Defines data/state contracts used by the raid persistence, centered on Career Raid Ledger Verification Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Raid.Persistence.Models;

/// <summary>
/// Read-only admission result for the physical Career ledger.
/// SourceEntries preserves all non-null physical entries while VerifiedEntries contains only
/// the unique, supported, owner-bound, semantically valid and fingerprint-valid schema-v1 facts.
/// Downstream Career projections must consume this snapshot instead of re-implementing admission.
/// </summary>
public sealed record VanguardCareerRaidLedgerVerificationSnapshot(
    string CoverageState,
    string LedgerReadState,
    bool ActiveLedgerFilePresent,
    bool QuarantineEvidencePresent,
    int SourceEntryCount,
    int VerifiedEntryCount,
    int RejectedEntryCount,
    int DuplicateEntryCount,
    int UnsupportedEntryCount,
    int IntegrityRejectedEntryCount,
    int SemanticRejectedEntryCount,
    int OwnerMismatchEntryCount,
    IReadOnlyList<VanguardCareerRaidLedgerEntry> SourceEntries,
    IReadOnlyList<VanguardCareerRaidLedgerEntry> VerifiedEntries);

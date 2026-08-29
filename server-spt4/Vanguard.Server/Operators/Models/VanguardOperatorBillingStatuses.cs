// Responsibility: Centralizes stable diagnostic/status identifiers used by the Operator persistence/domain models.
// Flow: Callers reference these constants when emitting or correlating diagnostics; no runtime action is performed here.
// Authority boundary: Diagnostic naming only; behavior and state authority remain in the subsystem that emits each status.
// Invariant: Status identifiers stay stable and side-effect free so validators and runtime evidence can correlate the same contract.
namespace Vanguard.Server.Operators.Models;

public static class VanguardOperatorBillingStatuses
{
    public const string PendingSignature = "pending_signature";
    public const string SignedPendingSettlement = "signed_pending_settlement";
    public const string Paid = "paid";
    public const string Cancelled = "cancelled";
}

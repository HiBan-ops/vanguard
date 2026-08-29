// Responsibility: Centralizes stable diagnostic/status identifiers used by the Operator persistence/domain models.
// Flow: Callers reference these constants when emitting or correlating diagnostics; no runtime action is performed here.
// Authority boundary: Diagnostic naming only; behavior and state authority remain in the subsystem that emits each status.
// Invariant: Status identifiers stay stable and side-effect free so validators and runtime evidence can correlate the same contract.
namespace Vanguard.Server.Operators.Models;

public static class VanguardOperatorServiceStatuses
{
    public const string Available = "available";
    public const string ActiveService = "active_service";
    public const string Recovering = "recovering";
    public const string Unavailable = "unavailable";
}

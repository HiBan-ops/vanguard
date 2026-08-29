// Responsibility: Centralizes stable diagnostic/status identifiers used by the Operator runtime.
// Flow: Callers reference these constants when emitting or correlating diagnostics; no runtime action is performed here.
// Authority boundary: Diagnostic naming only; behavior and state authority remain in the subsystem that emits each status.
// Invariant: Status identifiers stay stable and side-effect free so validators and runtime evidence can correlate the same contract.
namespace Vanguard.Client;

internal static class VanguardSharedContactAssistStatusTags
{
    public const string ClientBuild = "VANGUARD_CLIENT_BUILD_STATUS";
    public const string PatchStatus = "VANGUARD_SHARED_CONTACT_COMBAT_ASSIST_STATUS";
    public const string HardSoftClassification = "VANGUARD_HARD_SOFT_CONTACT_CLASSIFICATION_STATUS";
    public const string SharedTargetInjection = "VANGUARD_SHARED_TARGET_INJECTION_STATUS";
    public const string NoFakeLineOfSight = "VANGUARD_NO_FAKE_LINE_OF_SIGHT_STATUS";
    public const string SharedApplyCircuit = "VANGUARD_SHARED_APPLY_CIRCUIT_STATUS";
    public const string TargetClearProtection = "VANGUARD_SHARED_TARGET_CLEAR_PROTECTION_STATUS";
}

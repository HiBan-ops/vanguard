// Responsibility: Defines the concrete persistent EFT visual identity selected once for a Vanguard Operator.
// Flow: Visual genesis resolves faction-compatible EFT customization ids, persistence stores them on the Operator, and profile/raid projections consume the same values.
// Authority boundary: SPT/EFT customization tables and PMC appearance pools define valid candidates; this model only persists Vanguard's resolved selection and evidence.
// Invariant: A populated VisualIdentity is immutable identity truth for routine lifecycle operations and must never be rerolled by menu, inventory, raid, restart or rehire flows.
namespace Vanguard.Server.Operators.Models;

public static class VanguardOperatorVisualIdentitySchema
{
    public const int CurrentVersion = 1;
}

public sealed record VanguardOperatorVisualIdentity(
    string Head,
    string Body,
    string Feet,
    string Hands,
    string Voice,
    string ResolverPolicyId,
    int ResolverPolicyVersion,
    string SourceBotType,
    string SeedFingerprint,
    DateTimeOffset ResolvedAtUtc,
    int SchemaVersion = VanguardOperatorVisualIdentitySchema.CurrentVersion);

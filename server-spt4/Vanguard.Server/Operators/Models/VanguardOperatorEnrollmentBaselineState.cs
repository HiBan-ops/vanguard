// Responsibility: Records the one-time deterministic pre-Vanguard career baseline applied when a world Operator first enters Vanguard service.
// Flow: Recruitment creates the evidence once, profile materialization consumes the same policy/seed facts, and later progression only moves forward from that baseline.
// Authority boundary: This record is persistent evidence only; EFT/SPT remains authoritative for level/skill constraints and raid progression remains authoritative after enrollment.
// Invariant: A native enrollment baseline is generated once for a first enrollment and is never rerolled during routine load, inventory entry, raid generation, release or rehire.
namespace Vanguard.Server.Operators.Models;

public static class VanguardOperatorEnrollmentBaselineSchema
{
    public const int CurrentVersion = 1;
}

public sealed record VanguardOperatorEnrollmentBaselineState(
    string PolicyId,
    int PolicyVersion,
    string Origin,
    int CareerLevel,
    int MaximumCareerLevel,
    string Archetype,
    string Side,
    string SeedFingerprint,
    string CurveSource,
    bool CurveAuthoritative,
    string PhysicalPolicyId,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset AppliedAtUtc,
    int SchemaVersion = VanguardOperatorEnrollmentBaselineSchema.CurrentVersion);

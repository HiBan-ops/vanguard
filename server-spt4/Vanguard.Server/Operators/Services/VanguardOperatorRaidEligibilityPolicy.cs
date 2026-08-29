using Vanguard.Server.Operators.Models;

// Responsibility: Encodes the deterministic rules for Operator Raid Eligibility Policy within the Operator domain services.
// Flow: Normalized snapshots/configuration enter as inputs, pure rule evaluation selects a bounded outcome, and an executor/service consumes that outcome.
// Authority boundary: Policy decides eligibility/priority only; physical EFT actions and persisted state changes belong to their dedicated executors/services.
// Invariant: The same evidence yields the same decision, and safety/authority gates are never bypassed by policy convenience.
namespace Vanguard.Server.Operators.Services;

/// <summary>
/// One canonical off-raid eligibility decision shared by UI projections and raid manifests.
/// This does not change the runtime spawn semantics: it formalises the existing manifest rules and exposes
/// the first blocking reason so selected-but-ineligible Operators are explainable.
/// </summary>
internal static class VanguardOperatorRaidEligibilityPolicy
{
    public const double MinimumHealthRatio = 0.05d;

    public static VanguardOperatorEligibilityDecision Evaluate(
        VanguardActiveServiceRecord? active,
        VanguardOperatorMedicalRecord? medical,
        DateTimeOffset now)
    {
        double healthRatio = ClampRatio(medical?.CurrentHealthRatio ?? 1.0d);
        if (active is null)
        {
            return new(false, "not_in_active_service", healthRatio);
        }

        if (active.IsDeployed)
        {
            return new(false, "already_deployed", healthRatio);
        }

        if (string.Equals(active.Status, VanguardOperatorServiceStatuses.Unavailable, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "service_unavailable", healthRatio);
        }

        if (medical?.RecoveryUntilUtc is DateTimeOffset until && until > now)
        {
            return new(false, "medical_recovery_active", healthRatio);
        }

        if (healthRatio <= MinimumHealthRatio)
        {
            return new(false, "health_below_raid_minimum", healthRatio);
        }

        return new(true, "eligible", healthRatio);
    }

    private static double ClampRatio(double value) => Math.Max(0d, Math.Min(1d, value));
}

internal sealed record VanguardOperatorEligibilityDecision(bool IsEligible, string Reason, double HealthRatio);

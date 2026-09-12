using Vanguard.Server.Operators.Models;

// Responsibility: Materializes the durable Vanguard-world identity/profile facts that may exist before recruitment.
// Flow: Contract/world generation creates an uncontracted profile here; recruitment later promotes that same profile into ActiveService.
// Authority boundary: This factory creates domain data only. It does not create EFT inventory profiles, medical records, contacts, contracts or raid state.
// Invariant: Creating a world Operator must not imply recruitment, contract visibility, ActiveService membership or career enrollment.
namespace Vanguard.Server.Operators.Services;

public static class VanguardOperatorWorldProfileFactory
{
    public static VanguardOperatorProfile CreateUncontracted(VanguardOperatorContractOffer offer, DateTimeOffset now)
    {
        var identity = new VanguardOperatorIdentity(
            offer.OperatorId,
            offer.FirstName,
            offer.LastName,
            offer.Callsign,
            offer.DisplayName,
            offer.Side,
            offer.Side.Equals("Bear", StringComparison.OrdinalIgnoreCase) ? "ru" : "en",
            offer.VisualFamily,
            VanguardOperatorSchema.CurrentVersion);
        var persona = new VanguardOperatorPersona(
            offer.BasePersona,
            offer.Doctrine,
            offer.Temperament,
            offer.SainProfileFamily,
            offer.SainTuningPlan,
            offer.Traits,
            offer.BehaviorSummary,
            VanguardOperatorSchema.CurrentVersion,
            offer.CombatStyle,
            offer.EngagementRange,
            offer.SquadRole);
        var progression = new VanguardOperatorProgression(
            offer.Level,
            offer.Experience,
            0,
            0,
            0,
            0,
            0,
            50,
            50,
            50,
            VanguardOperatorSchema.CurrentVersion);

        return new VanguardOperatorProfile(
            offer.OperatorId,
            identity,
            offer.Role,
            offer.Specialty,
            VanguardOperatorContractStatuses.Uncontracted,
            VanguardOperatorServiceStatuses.Available,
            offer.SalaryPerRaid,
            offer.HirePrice,
            offer.CurrencyTpl,
            persona,
            progression,
            now,
            now,
            VanguardOperatorSchema.CurrentVersion,
            "CorpsesOnly",
            null);
    }
}

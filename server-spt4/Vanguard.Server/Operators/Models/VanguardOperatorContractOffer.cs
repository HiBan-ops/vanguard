// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Contract Offer.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

public sealed record VanguardOperatorContractOffer(
    string OfferId,
    string OperatorId,
    string DisplayName,
    string FirstName,
    string LastName,
    string Callsign,
    string Side,
    string Role,
    string Specialty,
    int Level,
    int Experience,
    int HirePrice,
    int SalaryPerRaid,
    string CurrencyTpl,
    string Rarity,
    string VisualFamily,
    string BasePersona,
    string Doctrine,
    string Temperament,
    string SainProfileFamily,
    string SainTuningPlan,
    IReadOnlyList<string> Traits,
    DateTimeOffset AvailableFromUtc,
    DateTimeOffset AvailableUntilUtc,
    string PoolId,
    int PlayerLevelAtGeneration,
    int SchemaVersion = VanguardOperatorSchema.CurrentVersion,
    string CombatStyle = "disciplined_fire_support",
    string EngagementRange = "medium",
    string SquadRole = "rifleman",
    string BehaviorSummary = "Generated Vanguard persona prepared for future SAIN projection.",
    bool CanHire = true,
    string MarketStatus = "dynamic_contract",
    string RelationshipSummary = "New contract offer.");

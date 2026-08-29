// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Profile.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

public sealed record VanguardOperatorProfile(
    string OperatorId,
    VanguardOperatorIdentity Identity,
    string Role,
    string Specialty,
    string ContractStatus,
    string ServiceStatus,
    int SalaryPerRaid,
    int HirePrice,
    string CurrencyTpl,
    VanguardOperatorPersona Persona,
    VanguardOperatorProgression Progression,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int SchemaVersion = VanguardOperatorSchema.CurrentVersion,
    string LootTargetPolicy = "CorpsesOnly",
    VanguardOperatorCareer? Career = null);

// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Persona.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

public sealed record VanguardOperatorPersona(
    string BasePersona,
    string Doctrine,
    string Temperament,
    string SainProfileFamily,
    string SainTuningPlan,
    IReadOnlyList<string> Traits,
    string BehaviorSummary,
    int SchemaVersion = VanguardOperatorSchema.CurrentVersion,
    string CombatStyle = "disciplined_fire_support",
    string EngagementRange = "medium",
    string SquadRole = "rifleman");

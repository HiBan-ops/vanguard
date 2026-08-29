// Responsibility: Defines data/state contracts used by the Operator persistence/domain models, centered on Operator Billing Types.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Server.Operators.Models;

public static class VanguardOperatorBillingTypes
{
    public const string ContractSignature = "contract_signature";
    public const string MedicalTreatment = "medical_treatment";
    public const string RaidSalary = "raid_salary";
}

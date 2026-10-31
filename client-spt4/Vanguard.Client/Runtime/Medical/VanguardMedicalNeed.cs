#if SPT_CLIENT

// Responsibility: Define stable medical need categories and the roles assigned to candidate treatments.
// Flow: Represent bleed, injury, healing and surgery needs as distinct enum values consumed by medical selection.
// Authority boundary: These enums are vocabulary for other components and do not initiate treatment or own health state.
// Invariant: Explicit medical need and capability role discriminants remain unchanged for existing consumers.
namespace Vanguard.Client.Runtime.Medical;

internal enum VanguardMedicalNeed
{
    None = 0,
    HeavyBleed = 10,
    LightBleed = 20,
    BlackBroken = 25,
    Fracture = 30,
    HpHeal = 40,
    PainMobility = 50,
    UntreatableVitalDestroyedPart = 55,
    SurgeryDestroyedPart = 60,
}

internal enum VanguardMedicalCapabilityRole
{
    Primary = 0,
    Fallback = 1,
    Utility = 2,
}
#endif

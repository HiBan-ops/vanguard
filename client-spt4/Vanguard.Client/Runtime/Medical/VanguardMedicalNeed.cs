#if SPT_CLIENT

// Responsibility: Provides Medical Need support for the medical runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
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

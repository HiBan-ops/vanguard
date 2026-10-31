#if !SPT_CLIENT

// Responsibility: Declare a behavior-free VanguardFikaCompat placeholder for non-SPT compilation.
// Flow: The !SPT_CLIENT branch supplies the compatibility type without implementation or external calls.
// Authority boundary: No EFT or Fika runtime authority is exercised by this compile-time-only placeholder.
// Invariant: The type remains empty and is compiled only when SPT_CLIENT is not defined.
namespace Vanguard.Client.Compatibility;

internal static class VanguardFikaCompat
{
}
#endif

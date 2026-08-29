// Copyright (c) Vanguard.
// SPDX-License-Identifier: MIT

#if NETSTANDARD2_1

// Responsibility: Provides Is External Init support for the compatibility layer.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Compatibility shim required when compiling modern C# init-only/record DTOs
    /// against netstandard2.1 for the SPT/BepInEx client target.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
#endif

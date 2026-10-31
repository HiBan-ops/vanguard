// Copyright (c) Vanguard.
// SPDX-License-Identifier: MPL-2.0

#if NETSTANDARD2_1

// Responsibility: Provide the IsExternalInit compiler compatibility marker for the netstandard2.1 target.
// Flow: The NETSTANDARD2_1 branch declares the empty System.Runtime.CompilerServices marker consumed by init-only language features.
// Authority boundary: The C# compiler uses this compatibility type; it does not invoke EFT, SPT or runtime services.
// Invariant: The shim stays conditional on NETSTANDARD2_1 and exposes no executable behavior.
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

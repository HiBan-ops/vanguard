using System.Reflection;
using System.Runtime.CompilerServices;

// Responsibility: Publish Vanguard.Client assembly identity, build metadata and test-assembly visibility.
// Flow: Assembly attributes take version and status values from VanguardBuildVersion and declare InternalsVisibleTo for client tests.
// Authority boundary: These attributes describe the compiled client assembly; runtime feature ownership stays with the relevant subsystems.
// Invariant: AssemblyVersion and AssemblyFileVersion continue to use the shared build version contract.

[assembly: AssemblyTitle("Vanguard.Client")]
[assembly: AssemblyDescription("Vanguard client foundation for SPT/Fika")]
[assembly: AssemblyCompany("Vanguard")]
[assembly: AssemblyProduct("Vanguard")]
[assembly: AssemblyVersion(Vanguard.Client.VanguardBuildVersion.AssemblyValue)]
[assembly: AssemblyFileVersion(Vanguard.Client.VanguardBuildVersion.AssemblyValue)]
[assembly: AssemblyMetadata("Vanguard.Build.Label", Vanguard.Client.VanguardBuildVersion.BuildLabel)]
[assembly: AssemblyMetadata("Vanguard.Build.CoreRuntimeStatus", Vanguard.Client.VanguardBuildVersion.CoreRuntimeStatusTag)]
[assembly: AssemblyMetadata("Vanguard.Build.OperatorPersistenceStatus", Vanguard.Client.VanguardBuildVersion.OperatorPersistenceStatusTag)]
[assembly: InternalsVisibleTo("Vanguard.Client.Tests")]

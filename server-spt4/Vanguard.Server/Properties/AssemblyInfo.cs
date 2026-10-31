using System.Reflection;
using System.Runtime.CompilerServices;

// Responsibility: Publish Vanguard.Server assembly identity, build metadata and test-assembly visibility.
// Flow: Assembly attributes take version and status values from VanguardBuildVersion and declare InternalsVisibleTo for server tests.
// Authority boundary: These attributes describe the compiled server assembly; persistence and SPT configuration remain owned by their services.
// Invariant: AssemblyVersion and AssemblyFileVersion continue to use the shared build version contract.

[assembly: AssemblyTitle("Vanguard.Server")]
[assembly: AssemblyDescription("Vanguard server foundation and Operator persistence for SPT/Fika")]
[assembly: AssemblyCompany("Vanguard")]
[assembly: AssemblyProduct("Vanguard")]
[assembly: AssemblyVersion(Vanguard.Server.VanguardBuildVersion.AssemblyValue)]
[assembly: AssemblyFileVersion(Vanguard.Server.VanguardBuildVersion.AssemblyValue)]
[assembly: AssemblyMetadata("Vanguard.Build.Label", Vanguard.Server.VanguardBuildVersion.BuildLabel)]
[assembly: AssemblyMetadata("Vanguard.Build.CoreRuntimeStatus", Vanguard.Server.VanguardBuildVersion.CoreRuntimeStatusTag)]
[assembly: AssemblyMetadata("Vanguard.Build.OperatorPersistenceStatus", Vanguard.Server.VanguardBuildVersion.OperatorPersistenceStatusTag)]
[assembly: InternalsVisibleTo("Vanguard.Server.Tests")]

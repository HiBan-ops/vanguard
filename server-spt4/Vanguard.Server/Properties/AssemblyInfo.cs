using System.Reflection;
using System.Runtime.CompilerServices;

// Responsibility: Provides Assembly Info support for the Vanguard server.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.

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

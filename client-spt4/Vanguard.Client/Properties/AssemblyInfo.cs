using System.Reflection;
using System.Runtime.CompilerServices;

// Responsibility: Provides Assembly Info support for the Vanguard client.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.

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

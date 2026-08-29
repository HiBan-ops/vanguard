#if SPT_CLIENT
using System.Threading;
using System.Threading.Tasks;

// Responsibility: Provides Unity Thread support for the raid lifecycle services.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Raid.Services;

internal static class VanguardUnityThread
{
    public static Task ResumeOnAsync(SynchronizationContext context)
    {
        var completion = new TaskCompletionSource<object?>();
        context.Post(_ => completion.TrySetResult(null), null);
        return completion.Task;
    }
}
#endif

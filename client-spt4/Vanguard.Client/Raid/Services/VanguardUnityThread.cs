#if SPT_CLIENT
using System.Threading;
using System.Threading.Tasks;

// Responsibility: Provide a synchronization-context handoff for tasks that need to resume on the Unity thread.
// Flow: Post a completion callback to the supplied SynchronizationContext and return its Task.
// Authority boundary: The supplied context schedules the callback; this helper does not manage Unity or raid lifetime.
// Invariant: The returned Task completes only after the posted callback executes.
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

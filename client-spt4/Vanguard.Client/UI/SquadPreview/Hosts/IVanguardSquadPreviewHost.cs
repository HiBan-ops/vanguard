#if SPT_CLIENT
using UnityEngine;
#endif

// Responsibility: exposes only the scene surface required by the shared Vanguard squad renderer.
// Flow: each host adapter owns or borrows one presentation shell and exposes one Vanguard-owned Operator root plus its shared camera.
// Authority boundary: host lifecycle and scene placement only; no Operator truth, selection, equipment or persistence mutation.
// Invariant: host Release destroys only Vanguard-owned resources and never destroys a third-party/native PMC or external host.
namespace Vanguard.Client.UI.SquadPreview.Hosts;

internal interface IVanguardSquadPreviewHost
{
    bool IsAvailable { get; }
    string HostKind { get; }

#if SPT_CLIENT
    Transform? RootTransform { get; }
    Camera? Camera { get; }
    bool TryAcquire(object menuScreen, out string reason);
#else
    object? RootTransform { get; }
    object? Camera { get; }
    bool TryAcquire(object menuScreen, out string reason);
#endif

    void Release(string reason);
}

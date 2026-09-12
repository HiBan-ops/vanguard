using System;
using System.Threading;
using System.Threading.Tasks;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using UnityEngine;
#endif

// Responsibility: owns the renderer lifecycle and visual fingerprint for exactly one selected Operator.
// Flow: unchanged fingerprints reposition in place; changed visuals load into a candidate PlayerModelLoader and replace the old body only after native load succeeds.
// Authority boundary: client presentation only; persistent Operator truth and selection remain external authorities.
// Invariant: failed/cancelled replacement preserves the previously committed body and stale work cannot resurrect or overwrite a newer instance.
namespace Vanguard.Client.UI.SquadPreview;

internal enum VanguardSquadPreviewApplyDisposition
{
    Unchanged = 0,
    Added = 1,
    Replaced = 2,
    Failed = 3,
    Cancelled = 4
}

internal readonly struct VanguardSquadPreviewApplyResult
{
    public VanguardSquadPreviewApplyResult(VanguardSquadPreviewApplyDisposition disposition, string reason)
    {
        Disposition = disposition;
        Reason = reason;
    }

    public VanguardSquadPreviewApplyDisposition Disposition { get; }
    public string Reason { get; }
}

internal sealed class VanguardSquadPreviewInstance : IDisposable
{
    private readonly string operatorId;
    private IVanguardSquadPreviewBodyLoader? activeLoader;
    private string visualFingerprint = string.Empty;
    private string visualRevision = string.Empty;
    private VanguardSquadPreviewPresentationState presentationState;
    private bool disposed;

    public VanguardSquadPreviewInstance(string operatorId)
    {
        this.operatorId = operatorId;
    }

    public string OperatorId => operatorId;
    public string VisualFingerprint => visualFingerprint;
    public VanguardSquadPreviewPresentationState PresentationState => presentationState;
    public bool IsLoaded => activeLoader?.IsLoaded == true;

#if SPT_CLIENT
    public async Task<VanguardSquadPreviewApplyResult> ApplyAsync(
        VanguardOperatorPreviewVisualDto projection,
        VanguardSquadPreviewPresentationState desiredPresentationState,
        object inventoryController,
        Transform host,
        Vector3 localPosition,
        VanguardSquadPreviewVisualAdapter visualAdapter,
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return new VanguardSquadPreviewApplyResult(VanguardSquadPreviewApplyDisposition.Failed, "instance_disposed");
        }

        string fingerprint = projection.VisualFingerprint?.Trim() ?? string.Empty;
        if (fingerprint.Length == 0)
        {
            return new VanguardSquadPreviewApplyResult(VanguardSquadPreviewApplyDisposition.Failed, "visual_fingerprint_missing");
        }

        if (activeLoader?.IsLoaded == true
            && string.Equals(visualFingerprint, fingerprint, StringComparison.Ordinal))
        {
            presentationState = desiredPresentationState;
            activeLoader.TrySetLocalPosition(localPosition);
            activeLoader.TryApplyPresentation(desiredPresentationState);
            return new VanguardSquadPreviewApplyResult(VanguardSquadPreviewApplyDisposition.Unchanged, "fingerprint_unchanged");
        }

        if (!visualAdapter.TryCreateNativeState(projection, out object? nativeState, out _, out string conversionReason)
            || nativeState == null)
        {
            return new VanguardSquadPreviewApplyResult(VanguardSquadPreviewApplyDisposition.Failed, "visual_conversion_" + conversionReason);
        }

        var candidate = new VanguardSquadPreviewBodyLoader();
        VanguardSquadPreviewLoadResult loadResult = await candidate.LoadAsync(
            operatorId,
            nativeState,
            inventoryController,
            host,
            localPosition,
            cancellationToken);

        if (!loadResult.Success)
        {
            candidate.Clear(loadResult.Cancelled ? "candidate_cancelled" : "candidate_load_failed");
            return new VanguardSquadPreviewApplyResult(
                loadResult.Cancelled ? VanguardSquadPreviewApplyDisposition.Cancelled : VanguardSquadPreviewApplyDisposition.Failed,
                loadResult.Reason);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            candidate.Clear("candidate_stale_before_commit");
            return new VanguardSquadPreviewApplyResult(VanguardSquadPreviewApplyDisposition.Cancelled, "cancelled_before_atomic_commit");
        }

        candidate.TryApplyPresentation(desiredPresentationState);

        IVanguardSquadPreviewBodyLoader? previous = null;
        bool replacing = false;
        bool committed = commitIfCurrent(() =>
        {
            previous = activeLoader;
            replacing = previous?.IsLoaded == true;
            activeLoader = candidate;
            visualFingerprint = fingerprint;
            visualRevision = projection.VisualRevision?.Trim() ?? string.Empty;
            presentationState = desiredPresentationState;
        });
        if (!committed)
        {
            candidate.Clear("candidate_stale_at_atomic_commit");
            return new VanguardSquadPreviewApplyResult(VanguardSquadPreviewApplyDisposition.Cancelled, "stale_generation_at_atomic_commit");
        }

        previous?.Clear("atomic_replacement_committed");

        VanguardClientDiagnosticsLog.Diagnostic(
            VanguardSquadPreviewController.ReconcileTag,
            () => $"phase=instance_commit; operator={operatorId}; disposition={(replacing ? "replaced" : "added")}; fingerprint={fingerprint[..Math.Min(16, fingerprint.Length)]}; revision={Safe(visualRevision)}; presentation={presentationState}; mutation=false");

        return new VanguardSquadPreviewApplyResult(
            replacing ? VanguardSquadPreviewApplyDisposition.Replaced : VanguardSquadPreviewApplyDisposition.Added,
            replacing ? "atomic_replacement_committed" : "initial_load_committed");
    }

    public void SetLocalPosition(Vector3 localPosition)
    {
        activeLoader?.TrySetLocalPosition(localPosition);
    }
#else
    public Task<VanguardSquadPreviewApplyResult> ApplyAsync(
        VanguardOperatorPreviewVisualDto projection,
        VanguardSquadPreviewPresentationState desiredPresentationState,
        object inventoryController,
        object host,
        object localPosition,
        VanguardSquadPreviewVisualAdapter visualAdapter,
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new VanguardSquadPreviewApplyResult(VanguardSquadPreviewApplyDisposition.Failed, "spt_client_not_defined"));
    }
#endif

    public void UpdatePresentation(VanguardSquadPreviewPresentationState desiredPresentationState)
    {
        presentationState = desiredPresentationState;
        activeLoader?.TryApplyPresentation(desiredPresentationState);
    }

    public void Clear(string reason)
    {
        activeLoader?.Clear(reason);
        activeLoader = null;
        visualFingerprint = string.Empty;
        visualRevision = string.Empty;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Clear("instance_disposed");
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

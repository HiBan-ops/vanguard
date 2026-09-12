using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using UnityEngine;
#endif

// Responsibility: reconciles the selected Operator squad into independently owned native renderer instances.
// Flow: state-bridge snapshots diff membership, the read-only preview route supplies current visual fingerprints, instances add/remove/replace atomically, and deterministic formations reposition unchanged bodies.
// Authority boundary: projection only; this controller never changes selection, ActiveService, equipment, health, profiles or menu-host ownership.
// Invariant: one body loader per committed Operator, no full-scene rebuild for ordinary changes, and cancelled generations cannot commit stale bodies.
namespace Vanguard.Client.UI.SquadPreview;

internal sealed class VanguardSquadPreviewController : IDisposable
{
    public const string ReconcileTag = "SQUAD_PREVIEW_RECONCILE";
    public const string HostReleasedTag = "SQUAD_PREVIEW_HOST_RELEASED";
    private const string SquadRootName = "VanguardSquadRoot";

    private readonly object gate = new();
    private readonly VanguardApiClient apiClient;
    private readonly VanguardSquadPreviewVisualAdapter visualAdapter;
    private readonly Dictionary<string, VanguardSquadPreviewInstance> instances = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? reconcileCts;
    private int generation;
    private bool reconciliationPaused;
    private bool disposed;

#if SPT_CLIENT
    private Transform? hostOwner;
    private GameObject? squadRootObject;
    private object? inventoryController;
    private string hostKind = "none";
#endif

    public VanguardSquadPreviewController()
        : this(new VanguardApiClient(), new VanguardSquadPreviewVisualAdapter())
    {
    }

    internal VanguardSquadPreviewController(VanguardApiClient apiClient, VanguardSquadPreviewVisualAdapter visualAdapter)
    {
        this.apiClient = apiClient;
        this.visualAdapter = visualAdapter;
        VanguardSquadPreviewStateBridge.SnapshotChanged += OnSnapshotChanged;
    }

    public int LoadedInstanceCount
    {
        get
        {
            lock (gate)
            {
                return instances.Values.Count(instance => instance.IsLoaded);
            }
        }
    }

#if SPT_CLIENT
    public bool Acquire(Transform owner, object controller, string resolvedHostKind)
    {
        if (disposed || owner == null || controller == null)
        {
            return false;
        }

        bool hostChanged;
        lock (gate)
        {
            hostChanged = hostOwner != owner || inventoryController == null || !ReferenceEquals(inventoryController, controller);
        }

        if (hostChanged)
        {
            Release("host_changed");
        }

        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            hostOwner = owner;
            inventoryController = controller;
            hostKind = string.IsNullOrWhiteSpace(resolvedHostKind) ? "unknown" : resolvedHostKind.Trim();
            if (squadRootObject == null)
            {
                squadRootObject = new GameObject(SquadRootName);
                squadRootObject.transform.SetParent(owner, false);
                squadRootObject.transform.localPosition = Vector3.zero;
                squadRootObject.transform.localRotation = Quaternion.identity;
                squadRootObject.transform.localScale = Vector3.one;
            }
        }

        VanguardSquadPreviewStateSnapshot latest = VanguardSquadPreviewStateBridge.Latest;
        if (latest.Sequence > 0)
        {
            QueueReconcile(latest);
        }

        return true;
    }
#else
    public bool Acquire(object owner, object controller, string resolvedHostKind) => false;
#endif

#if SPT_CLIENT
    public bool IsBoundTo(Transform owner, object controller, string resolvedHostKind)
    {
        if (owner == null || controller == null)
        {
            return false;
        }

        lock (gate)
        {
            return !disposed
                && squadRootObject != null
                && hostOwner == owner
                && inventoryController != null
                && ReferenceEquals(inventoryController, controller)
                && string.Equals(hostKind, resolvedHostKind, StringComparison.OrdinalIgnoreCase);
        }
    }
#else
    public bool IsBoundTo(object owner, object controller, string resolvedHostKind) => false;
#endif

    public void SetReconciliationPaused(bool paused, string source)
    {
        CancellationTokenSource? ctsToCancel = null;
        bool changed;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            changed = reconciliationPaused != paused;
            reconciliationPaused = paused;
            if (paused && reconcileCts != null)
            {
                generation++;
                ctsToCancel = reconcileCts;
                reconcileCts = null;
            }
        }

        if (ctsToCancel != null)
        {
            try
            {
                ctsToCancel.Cancel();
            }
            catch
            {
            }
            ctsToCancel.Dispose();
        }

        if (changed)
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                ReconcileTag,
                () => $"phase={(paused ? "resident_paused" : "resident_resumed")}; source={Safe(source)}; loaded={LoadedInstanceCount}; mutation=false");
        }

        if (!paused && changed)
        {
            VanguardSquadPreviewStateSnapshot latest = VanguardSquadPreviewStateBridge.Latest;
            if (latest.Sequence > 0)
            {
                QueueReconcile(latest);
            }
        }
    }

    public void Release(string reason)
    {
        CancellationTokenSource? cts;
        VanguardSquadPreviewInstance[] toClear;
#if SPT_CLIENT
        GameObject? root;
        string releasedHostKind;
#endif
        lock (gate)
        {
            generation++;
            cts = reconcileCts;
            reconcileCts = null;
            reconciliationPaused = false;
            toClear = instances.Values.ToArray();
            instances.Clear();
#if SPT_CLIENT
            root = squadRootObject;
            squadRootObject = null;
            hostOwner = null;
            inventoryController = null;
            releasedHostKind = hostKind;
            hostKind = "none";
#endif
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // Native loader cleanup below remains authoritative.
        }
        cts?.Dispose();

        foreach (VanguardSquadPreviewInstance instance in toClear)
        {
            instance.Dispose();
        }

#if SPT_CLIENT
        if (root != null)
        {
            UnityEngine.Object.Destroy(root);
        }

        if (toClear.Length > 0 || root != null)
        {
            VanguardClientDiagnosticsLog.Info(
                HostReleasedTag,
                $"hostKind={Safe(releasedHostKind)}; instancesCleared={toClear.Length}; rootDestroyed={root != null}; reason={Safe(reason)}; mutation=false");
        }
#endif
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        VanguardSquadPreviewStateBridge.SnapshotChanged -= OnSnapshotChanged;
        Release("controller_disposed");
    }

    private void OnSnapshotChanged(VanguardSquadPreviewStateSnapshot snapshot)
    {
        QueueReconcile(snapshot);
    }

    private void QueueReconcile(VanguardSquadPreviewStateSnapshot snapshot)
    {
#if SPT_CLIENT
        lock (gate)
        {
            if (disposed || reconciliationPaused || squadRootObject == null || inventoryController == null)
            {
                return;
            }
        }

        _ = ReconcileAsync(snapshot);
#endif
    }

#if SPT_CLIENT
    private async Task ReconcileAsync(VanguardSquadPreviewStateSnapshot snapshot)
    {
        CancellationTokenSource currentCts;
        int currentGeneration;
        Transform root;
        object controller;
        string currentHostKind;

        lock (gate)
        {
            if (disposed || reconciliationPaused || squadRootObject == null || inventoryController == null)
            {
                return;
            }

            try
            {
                reconcileCts?.Cancel();
            }
            catch
            {
                // Supersession is best-effort; generation checks still prevent stale commit.
            }
            reconcileCts?.Dispose();
            currentCts = new CancellationTokenSource();
            reconcileCts = currentCts;
            currentGeneration = ++generation;
            root = squadRootObject.transform;
            controller = inventoryController;
            currentHostKind = hostKind;
        }

        try
        {
            await ReconcileCoreAsync(snapshot, currentGeneration, root, controller, currentHostKind, currentCts.Token);
        }
        catch (OperationCanceledException)
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                ReconcileTag,
                () => $"phase=cancelled; generation={currentGeneration}; snapshot={snapshot.Sequence}; hostKind={Safe(currentHostKind)}; mutation=false");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                ReconcileTag,
                $"phase=failed_closed; generation={currentGeneration}; snapshot={snapshot.Sequence}; hostKind={Safe(currentHostKind)}; type={exception.GetType().Name}; reason={Safe(exception.Message)}; persistentTruthUntouched=true");
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(reconcileCts, currentCts))
                {
                    reconcileCts = null;
                }
            }
            currentCts.Dispose();
        }
    }

    private async Task ReconcileCoreAsync(
        VanguardSquadPreviewStateSnapshot snapshot,
        int currentGeneration,
        Transform root,
        object controller,
        string currentHostKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGeneration(currentGeneration);

        string[] desiredIds = snapshot.Members.Select(member => member.OperatorId).ToArray();
        var desiredSet = new HashSet<string>(desiredIds, StringComparer.OrdinalIgnoreCase);
        int removed = RemoveDeselected(desiredSet, "deselected");

        if (snapshot.Members.Count == 0)
        {
            VanguardClientDiagnosticsLog.Info(
                ReconcileTag,
                $"phase=complete; generation={currentGeneration}; snapshot={snapshot.Sequence}; hostKind={Safe(currentHostKind)}; desired=0; loaded=0; added=0; replaced=0; removed={removed}; unchanged=0; unavailable=0; mutation=false");
            return;
        }

        VanguardOperatorPreviewVisualResponseDto response = apiClient.LoadPreviewVisuals(desiredIds);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGeneration(currentGeneration);

        var visualByOperator = (response.Operators ?? new List<VanguardOperatorPreviewVisualDto>())
            .Where(item => !string.IsNullOrWhiteSpace(item.OperatorId))
            .GroupBy(item => item.OperatorId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        int added = 0;
        int replaced = 0;
        int unchanged = 0;
        int unavailable = 0;
        int failed = 0;

        for (int index = 0; index < snapshot.Members.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureGeneration(currentGeneration);

            VanguardSquadPreviewDesiredOperator desired = snapshot.Members[index];
            Vector3 position = VanguardSquadPreviewFormation.ResolveLocalPosition(index, snapshot.Members.Count, currentHostKind);
            VanguardSquadPreviewInstance? instance;
            lock (gate)
            {
                instances.TryGetValue(desired.OperatorId, out instance);
            }

            if (!visualByOperator.TryGetValue(desired.OperatorId, out var visual))
            {
                unavailable++;
                if (instance != null)
                {
                    instance.UpdatePresentation(desired.PresentationState);
                    instance.SetLocalPosition(position);
                }
                continue;
            }

            bool newInstance = instance == null;
            instance ??= new VanguardSquadPreviewInstance(desired.OperatorId);
            VanguardSquadPreviewApplyResult apply = await instance.ApplyAsync(
                visual,
                desired.PresentationState,
                controller,
                root,
                position,
                visualAdapter,
                commit => TryCommitGeneration(currentGeneration, commit),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            EnsureGeneration(currentGeneration);

            switch (apply.Disposition)
            {
                case VanguardSquadPreviewApplyDisposition.Added:
                    lock (gate)
                    {
                        if (!instances.ContainsKey(desired.OperatorId))
                        {
                            instances[desired.OperatorId] = instance;
                        }
                    }
                    added++;
                    break;
                case VanguardSquadPreviewApplyDisposition.Replaced:
                    replaced++;
                    break;
                case VanguardSquadPreviewApplyDisposition.Unchanged:
                    unchanged++;
                    break;
                case VanguardSquadPreviewApplyDisposition.Cancelled:
                    if (newInstance)
                    {
                        instance.Dispose();
                    }
                    throw new OperationCanceledException(cancellationToken);
                default:
                    failed++;
                    if (newInstance)
                    {
                        instance.Dispose();
                    }
                    break;
            }
        }

        int loaded;
        lock (gate)
        {
            loaded = instances.Values.Count(instance => instance.IsLoaded);
        }

        VanguardClientDiagnosticsLog.Info(
            ReconcileTag,
            $"phase=complete; generation={currentGeneration}; snapshot={snapshot.Sequence}; hostKind={Safe(currentHostKind)}; requested={response.RequestedOperatorCount}; returned={response.ReturnedOperatorCount}; desired={snapshot.Members.Count}; loaded={loaded}; added={added}; replaced={replaced}; removed={removed}; unchanged={unchanged}; unavailable={unavailable}; failed={failed}; responseReason={Safe(response.Reason)}; mutation=false");
    }

    private int RemoveDeselected(HashSet<string> desiredSet, string reason)
    {
        List<VanguardSquadPreviewInstance> removed = new();
        lock (gate)
        {
            foreach (string operatorId in instances.Keys.Where(id => !desiredSet.Contains(id)).ToArray())
            {
                removed.Add(instances[operatorId]);
                instances.Remove(operatorId);
            }
        }

        foreach (VanguardSquadPreviewInstance instance in removed)
        {
            instance.Clear(reason);
            instance.Dispose();
        }

        return removed.Count;
    }

    private bool TryCommitGeneration(int expectedGeneration, Action commit)
    {
        lock (gate)
        {
            if (disposed || generation != expectedGeneration || squadRootObject == null || inventoryController == null)
            {
                return false;
            }

            commit();
            return true;
        }
    }

    private void EnsureGeneration(int expectedGeneration)
    {
        lock (gate)
        {
            if (disposed || generation != expectedGeneration || squadRootObject == null || inventoryController == null)
            {
                throw new OperationCanceledException("stale_reconcile_generation");
            }
        }
    }
#endif

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

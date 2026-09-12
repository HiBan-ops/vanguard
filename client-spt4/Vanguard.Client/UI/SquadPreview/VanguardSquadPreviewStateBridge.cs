using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Diagnostics;

// Responsibility: converts canonical Off-Raid state into the selected Squad Preview membership/presentation snapshot.
// Flow: ActiveService selection owns membership, RaidProjections own eligibility, and stable fingerprints suppress duplicate reconcile work.
// Authority boundary: presentation bridge only; it never changes raid selection, medical state, ActiveService or persistent Operator data.
// Invariant: selected Operators remain represented even when ineligible; unknown eligibility fails toward Shadow rather than removing membership.
namespace Vanguard.Client.UI.SquadPreview;

internal enum VanguardSquadPreviewPresentationState
{
    Normal = 0,
    Shadow = 1
}

internal sealed class VanguardSquadPreviewDesiredOperator
{
    public string OperatorId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public VanguardSquadPreviewPresentationState PresentationState { get; init; }
    public string EligibilityReason { get; init; } = string.Empty;
}

internal sealed class VanguardSquadPreviewStateSnapshot
{
    public static readonly VanguardSquadPreviewStateSnapshot Empty = new(
        storageProfileId: string.Empty,
        members: Array.Empty<VanguardSquadPreviewDesiredOperator>(),
        fingerprint: "empty",
        source: "none",
        sequence: 0);

    public VanguardSquadPreviewStateSnapshot(
        string storageProfileId,
        IReadOnlyList<VanguardSquadPreviewDesiredOperator> members,
        string fingerprint,
        string source,
        long sequence)
    {
        StorageProfileId = storageProfileId;
        Members = members;
        Fingerprint = fingerprint;
        Source = source;
        Sequence = sequence;
    }

    public string StorageProfileId { get; }
    public IReadOnlyList<VanguardSquadPreviewDesiredOperator> Members { get; }
    public string Fingerprint { get; }
    public string Source { get; }
    public long Sequence { get; }
}

internal static class VanguardSquadPreviewStateBridge
{
    public const string StatusTag = "SQUAD_PREVIEW_STATE_BRIDGE";
    private const int MaxPreviewOperators = 4;
    private static readonly object Gate = new();
    private static VanguardSquadPreviewStateSnapshot latest = VanguardSquadPreviewStateSnapshot.Empty;
    private static long sequence;

    public static event Action<VanguardSquadPreviewStateSnapshot>? SnapshotChanged;

    public static VanguardSquadPreviewStateSnapshot Latest
    {
        get
        {
            lock (Gate)
            {
                return latest;
            }
        }
    }

    public static VanguardSquadPreviewStateSnapshot Publish(VanguardOperatorStateView? state, string source)
    {
        if (state == null || state.Error != null || string.IsNullOrWhiteSpace(state.StorageProfileId))
        {
            VanguardClientDiagnosticsLog.Diagnostic(
                StatusTag,
                () => $"publish_skipped source={Safe(source)}; reason=state_unavailable; mutation=false");
            return Latest;
        }

        var raidProjectionGroups = state.RaidProjections
            .Where(item => !string.IsNullOrWhiteSpace(item.OperatorId))
            .GroupBy(item => item.OperatorId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var selectedRecords = state.ActiveService
            .Where(record => record.IsSelectedForRaid && !string.IsNullOrWhiteSpace(record.OperatorId))
            .GroupBy(record => record.OperatorId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaxPreviewOperators + 1)
            .ToArray();

        bool selectionOverflow = selectedRecords.Length > MaxPreviewOperators;
        VanguardActiveServiceRecordDto[] admittedRecords = selectedRecords.Take(MaxPreviewOperators).ToArray();
        var members = new List<VanguardSquadPreviewDesiredOperator>(admittedRecords.Length);

        foreach (VanguardActiveServiceRecordDto record in admittedRecords)
        {
            string operatorId = record.OperatorId!.Trim();
            VanguardSquadPreviewPresentationState presentationState = VanguardSquadPreviewPresentationState.Shadow;
            string eligibilityReason = "raid_projection_missing";

            if (raidProjectionGroups.TryGetValue(operatorId, out var projections))
            {
                if (projections.Length == 1)
                {
                    VanguardOperatorRaidProjectionDto projection = projections[0];
                    presentationState = projection.IsEligibleForRaid
                        ? VanguardSquadPreviewPresentationState.Normal
                        : VanguardSquadPreviewPresentationState.Shadow;
                    eligibilityReason = string.IsNullOrWhiteSpace(projection.EligibilityReason)
                        ? projection.IsEligibleForRaid ? "eligible" : "not_eligible"
                        : projection.EligibilityReason!.Trim();
                }
                else
                {
                    eligibilityReason = "raid_projection_ambiguous";
                }
            }

            members.Add(new VanguardSquadPreviewDesiredOperator
            {
                OperatorId = operatorId,
                DisplayName = string.IsNullOrWhiteSpace(record.DisplayName) ? operatorId : record.DisplayName!.Trim(),
                PresentationState = presentationState,
                EligibilityReason = eligibilityReason
            });
        }

        string fingerprint = BuildFingerprint(state.StorageProfileId, members);
        VanguardSquadPreviewStateSnapshot snapshot;
        Action<VanguardSquadPreviewStateSnapshot>? changed;
        lock (Gate)
        {
            if (string.Equals(latest.Fingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(latest.StorageProfileId, state.StorageProfileId, StringComparison.Ordinal))
            {
                return latest;
            }

            snapshot = new VanguardSquadPreviewStateSnapshot(
                state.StorageProfileId,
                members,
                fingerprint,
                string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim(),
                ++sequence);
            latest = snapshot;
            changed = SnapshotChanged;
        }

        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"sequence={snapshot.Sequence}; source={Safe(snapshot.Source)}; storage={Safe(snapshot.StorageProfileId)}; selected={members.Count}; overflow={selectionOverflow}; normal={members.Count(item => item.PresentationState == VanguardSquadPreviewPresentationState.Normal)}; shadow={members.Count(item => item.PresentationState == VanguardSquadPreviewPresentationState.Shadow)}; fingerprint={fingerprint[..16]}; mutation=false");

        if (selectionOverflow)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"selected_operator_limit_exceeded selectedObserved={selectedRecords.Length}; admitted={MaxPreviewOperators}; action=truncate_preview_only; raidSelectionMutation=false");
        }

        if (changed != null)
        {
            foreach (Delegate subscriber in changed.GetInvocationList())
            {
                try
                {
                    ((Action<VanguardSquadPreviewStateSnapshot>)subscriber)(snapshot);
                }
                catch (Exception exception)
                {
                    VanguardClientDiagnosticsLog.Warning(
                        StatusTag,
                        $"subscriber_failed type={exception.GetType().Name}; reason={Safe(exception.Message)}; stateMutation=false");
                }
            }
        }

        return snapshot;
    }

    private static string BuildFingerprint(string storageProfileId, IReadOnlyList<VanguardSquadPreviewDesiredOperator> members)
    {
        var builder = new StringBuilder(storageProfileId.Trim());
        foreach (VanguardSquadPreviewDesiredOperator member in members)
        {
            builder.Append('|')
                .Append(member.OperatorId)
                .Append(':')
                .Append((int)member.PresentationState)
                .Append(':')
                .Append(member.EligibilityReason);
        }

        using SHA256 sha256 = SHA256.Create();
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return BitConverter.ToString(bytes).Replace("-", string.Empty);
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

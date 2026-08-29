#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime.Fika;

using Vanguard.Client;

// Responsibility: Coordinates Raid Fixed Operator Hud Service for the raid Operator HUD, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Raid.Hud;

/// <summary>
/// Passive fixed HUD orchestrator. It consumes existing HUD candidates plus local canonical snapshots
/// or the authority-resolved Fika telemetry read-model. No decision, movement, medical, loot or network authority is created here.
/// </summary>
internal static class VanguardRaidFixedOperatorHudService
{
    private const float ActivityStabilitySeconds = 0.55f;
    private const float SummaryIntervalSeconds = 12f;

    private static readonly Dictionary<string, StableSemanticState> StableStateByKey = new(StringComparer.Ordinal);

    private static VanguardRaidFixedOperatorHudView? view;
    private static int parentInstanceId;
    private static float nextSummaryTime;
    private static bool startupLogged;

    public static void Tick(
        MonoBehaviour owner,
        RectTransform parent,
        TMP_FontAsset? fontAsset,
        IReadOnlyList<VanguardRaidOperatorHudCandidate> candidates)
    {
        try
        {
            if (owner == null || parent == null)
            {
                Hide();
                return;
            }

            VanguardRaidFixedOperatorHudOptions.BindFromOwner(owner);
            var settings = VanguardRaidFixedOperatorHudOptions.Capture();
            if (!settings.Enabled)
            {
                Hide();
                CleanupSemanticState(Array.Empty<string>());
                return;
            }

            EnsureView(parent, fontAsset);
            if (view is null)
            {
                return;
            }

            if (candidates.Count == 0)
            {
                view.SetActive(false);
                CleanupSemanticState(Array.Empty<string>());
                LogSummary(settings, Array.Empty<VanguardRaidFixedOperatorHudRow>());
                return;
            }

            float now = Time.realtimeSinceStartup;
            var rows = candidates
                .OrderBy(candidate => candidate.Nickname, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => new VanguardRaidFixedOperatorHudRow(
                    candidate.Key,
                    candidate.Nickname,
                    candidate.HealthReadable,
                    candidate.HealthPercent,
                    Stabilize(candidate.Key, VanguardRaidFixedOperatorHudSemanticResolver.Resolve(candidate), now)))
                .ToArray();

            CleanupSemanticState(rows.Select(row => row.Key));
            view.Update(rows, settings);
            LogStartup(settings);
            LogSummary(settings, rows);
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardFikaHudTelemetryService.StatusTag,
                $"fixed HUD tick failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public static void Hide()
    {
        if (view is not null)
        {
            view.SetActive(false);
        }
    }

    private static void EnsureView(RectTransform parent, TMP_FontAsset? fontAsset)
    {
        int instanceId = parent.GetInstanceID();
        if (view is not null && view.IsAlive && parentInstanceId == instanceId)
        {
            return;
        }

        if (view is not null)
        {
            view.Destroy();
        }

        view = VanguardRaidFixedOperatorHudView.Create(parent, fontAsset);
        parentInstanceId = instanceId;
        StableStateByKey.Clear();
        nextSummaryTime = 0f;
        startupLogged = false;
    }

    private static VanguardRaidFixedOperatorHudSemanticState Stabilize(
        string key,
        VanguardRaidFixedOperatorHudSemanticState incoming,
        float now)
    {
        if (!StableStateByKey.TryGetValue(key, out StableSemanticState? existing))
        {
            StableStateByKey[key] = new StableSemanticState(incoming, string.Empty, 0f);
            return incoming;
        }

        if (incoming.Urgent || !incoming.Authoritative || !incoming.Fresh)
        {
            StableStateByKey[key] = new StableSemanticState(incoming, string.Empty, 0f);
            return incoming;
        }

        if (string.Equals(existing.Accepted.ActivityLabel, incoming.ActivityLabel, StringComparison.Ordinal))
        {
            StableStateByKey[key] = new StableSemanticState(incoming, string.Empty, 0f);
            return incoming;
        }

        if (!string.Equals(existing.PendingActivity, incoming.ActivityLabel, StringComparison.Ordinal))
        {
            StableStateByKey[key] = new StableSemanticState(existing.Accepted, incoming.ActivityLabel, now);
            return MergePresentation(existing.Accepted, incoming);
        }

        if (now - existing.PendingSince >= ActivityStabilitySeconds)
        {
            StableStateByKey[key] = new StableSemanticState(incoming, string.Empty, 0f);
            return incoming;
        }

        return MergePresentation(existing.Accepted, incoming);
    }

    private static VanguardRaidFixedOperatorHudSemanticState MergePresentation(
        VanguardRaidFixedOperatorHudSemanticState accepted,
        VanguardRaidFixedOperatorHudSemanticState incoming)
    {
        return accepted with
        {
            AlertLabel = incoming.AlertLabel,
            AlertSeverity = incoming.AlertSeverity,
            Detail = incoming.Detail,
            Authoritative = incoming.Authoritative,
            Fresh = incoming.Fresh,
            Urgent = incoming.Urgent,
        };
    }

    private static void CleanupSemanticState(IEnumerable<string> liveKeys)
    {
        var live = new HashSet<string>(liveKeys, StringComparer.Ordinal);
        foreach (string stale in StableStateByKey.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            StableStateByKey.Remove(stale);
        }
    }

    private static void LogStartup(VanguardRaidFixedOperatorHudSettings settings)
    {
        if (startupLogged)
        {
            return;
        }

        startupLogged = true;
        VanguardClientDiagnosticsLog.Info(
            VanguardFikaHudTelemetryService.StatusTag,
            $"VANGUARD_AUTHORITATIVE_FIKA_HUD_TELEMETRY_ACTIVE presentationAuthority=client_local; decisionTruth=canonical_snapshot_or_authoritative_fika_telemetry; remoteInference=false; fikaTransport=true; anchor={settings.Anchor}; theme={settings.Theme}; mode={settings.DisplayMode}; build={VanguardBuildVersion.BuildLabel}");
    }

    private static void LogSummary(
        VanguardRaidFixedOperatorHudSettings settings,
        IReadOnlyList<VanguardRaidFixedOperatorHudRow> rows)
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextSummaryTime)
        {
            return;
        }

        nextSummaryTime = now + SummaryIntervalSeconds;
        int authoritative = rows.Count(row => row.Semantic.Authoritative && row.Semantic.Fresh);
        int unavailable = rows.Count - authoritative;
        VanguardClientDiagnosticsLog.Info(
            VanguardFikaHudTelemetryService.StatusTag,
            $"fixed HUD rows={rows.Count}; authoritativeFresh={authoritative}; unavailableOrStale={unavailable}; clientLocalPresentation=true; headlessPresentationSync=false; fikaTelemetryTransport=true; telemetryCached={VanguardFikaHudTelemetryStore.Count}");
    }

    private sealed record StableSemanticState(
        VanguardRaidFixedOperatorHudSemanticState Accepted,
        string PendingActivity,
        float PendingSince);
}
#else
namespace Vanguard.Client.Raid.Hud;

internal static class VanguardRaidFixedOperatorHudService
{
}
#endif

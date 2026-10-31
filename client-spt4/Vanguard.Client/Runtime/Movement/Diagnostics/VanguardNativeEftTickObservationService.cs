#if SPT_CLIENT
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: observes native EFT tick execution for a qualified HardReturn, without driving or arbitrating locomotion.
// Flow: Receives native BotMover tick callbacks, verifies the live HardReturn lease and bot incarnation, then emits bounded Trace-only observations.
// Authority boundary: Diagnostic observer only; never grants movement authority, changes scheduler order, writes commands, or moves an Operator.
// Invariant: Only matching runtime, lease and generation may be sampled; position deltas do not prove EFT causality or physical progress.
// Every state transition reported here is diagnostic, not a physical movement lease grant.
namespace Vanguard.Client.Runtime.Movement.Diagnostics;

internal static class VanguardNativeEftTickObservationService
{
    internal const string StatusTag = "VANGUARD_EFT_NATIVE_TICK_OBSERVATION";
    private static readonly ConditionalWeakTable<BotOwner, ProbeState> States = new();
    private static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(1.5);
    private const int MaxEmissionsPerLease = 80;

    private sealed class ProbeState
    {
        internal string LeaseId = string.Empty;
        internal long Generation;
        internal long RuntimeIncarnation;
        internal int Calls;
        internal int OriginalRan;
        internal int OriginalSkipped;
        internal int Emissions;
        internal bool? LastOriginalRan;
        internal DateTimeOffset LastEmissionUtc = DateTimeOffset.MinValue;
        internal Vector3 PreviousObservedPosition;
        internal bool HasPreviousPosition;
    }

    internal static void Observe(BotMover mover, bool runOriginal)
    {
        // Fast exit for the normal runtime profile: no registry, no new collections, no SAIN scan.
        if (!VanguardClientDiagnosticsLog.IsEnabled(VanguardAuditLevel.Trace) || mover == null)
        {
            return;
        }

        BotOwner? bot = mover.BotOwner_0;
        if (bot == null || !ReferenceEquals(bot.Mover, mover) || bot.IsDead)
        {
            return;
        }

        string botProfile = bot.ProfileId;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!VanguardReturnMovementCommandStore.TryPeekHardReturnForNativeTickObservation(botProfile, now, out var command))
        {
            return;
        }

        // The exact BotOwner/incarnation is required: profile-ID matches are not enough after respawn.
        if (!VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(botProfile, out var runtime)
            || !runtime.IsRuntimePresent || !ReferenceEquals(runtime.BotOwner, bot)
            || !string.Equals(runtime.OperatorId, command.OperatorId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The schedulerWindowId lives in the HardReturn executor, not in the command payload.
        // Never infer it from leaseId when lookup fails.
        bool windowMatched = VanguardHardReturnMovementExecutor.TryPeekActiveWindowForNativeTickObservation(
            botProfile, command.LeaseId, out string schedulerWindowId);

        ProbeState state = States.GetValue(bot, static _ => new ProbeState());
        bool emit;
        int calls;
        int ran;
        int skipped;
        Vector3 previous;
        bool hadPrevious;
        lock (state)
        {
            if (!string.Equals(state.LeaseId, command.LeaseId, StringComparison.Ordinal)
                || state.Generation != command.Generation
                || state.RuntimeIncarnation != runtime.RuntimeIncarnation)
            {
                state.LeaseId = command.LeaseId;
                state.Generation = command.Generation;
                state.RuntimeIncarnation = runtime.RuntimeIncarnation;
                state.Calls = 0;
                state.OriginalRan = 0;
                state.OriginalSkipped = 0;
                state.Emissions = 0;
                state.LastOriginalRan = null;
                state.LastEmissionUtc = DateTimeOffset.MinValue;
                state.HasPreviousPosition = false;
            }

            state.Calls++;
            if (runOriginal) state.OriginalRan++;
            else state.OriginalSkipped++;
            emit = state.Emissions < MaxEmissionsPerLease
                && (!state.LastOriginalRan.HasValue
                    || state.LastOriginalRan.Value != runOriginal
                    || now - state.LastEmissionUtc >= SamplePeriod);
            state.LastOriginalRan = runOriginal;
            if (!emit) return;
            state.Emissions++;
            state.LastEmissionUtc = now;
            calls = state.Calls;
            ran = state.OriginalRan;
            skipped = state.OriginalSkipped;
            previous = state.PreviousObservedPosition;
            hadPrevious = state.HasPreviousPosition;
        }

        Vector3 position = bot.Position;
        float observedHorizontalDelta = hadPrevious
            ? Vector2.Distance(new Vector2(previous.x, previous.z), new Vector2(position.x, position.z))
            : -1f;
        lock (state)
        {
            // A newer generation must never inherit position baseline from this callback.
            if (string.Equals(state.LeaseId, command.LeaseId, StringComparison.Ordinal)
                && state.Generation == command.Generation
                && state.RuntimeIncarnation == runtime.RuntimeIncarnation)
            {
                state.PreviousObservedPosition = position;
                state.HasPreviousPosition = true;
            }
        }

        // Re-read the exact generation after taking the sample. Unknown/drifted identity is not a receipt.
        bool stillSameCommand = VanguardReturnMovementCommandStore.TryPeekHardReturnForNativeTickObservation(
                botProfile, DateTimeOffset.UtcNow, out var current)
            && string.Equals(current.LeaseId, command.LeaseId, StringComparison.Ordinal)
            && current.Generation == command.Generation
            && current.IssuedAtUtc == command.IssuedAtUtc;
        bool stillSameRuntime = runtime.IsRuntimePresent && ReferenceEquals(runtime.BotOwner, bot);
        if (!stillSameCommand || !stillSameRuntime) return;

        // SAIN reflection is intentionally sampled only at the throttled log boundary.
        object? sain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(
            bot, "SAIN.Components.BotComponent");
        string sainLayers = VanguardOperatorRuntimeAuditReflection.BoolText(
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "SAINLayersActive"));
        string sainLayer = VanguardOperatorRuntimeAuditReflection.Text(
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "ActiveLayer"));
        string sainAction = VanguardOperatorRuntimeAuditReflection.Text(
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "CurrentAction"));

        bool hasPath = mover.HasPathAndNoComplete;
        bool paused = mover.Pause;
        string nativeDestination = Vector(mover.RealDestPoint);
        string commandDestination = Vector(command.Anchor);
        string sampledPosition = Vector(position);
        string delta = observedHorizontalDelta < 0 ? "unknown"
            : observedHorizontalDelta.ToString("0.00", CultureInfo.InvariantCulture);

        VanguardClientDiagnosticsLog.Info(StatusTag, VanguardAuditLevel.Trace, () =>
            $"EFT_NATIVE_TICK_OBSERVED botProfile={Safe(botProfile)}; operator={Safe(runtime.OperatorId)}; owner={Safe(runtime.OwnerProfileId)}; raid={Safe(runtime.RaidSessionId)}; incarnation={runtime.RuntimeIncarnation}; lease={Safe(command.LeaseId)}; generation={command.Generation}; schedulerWindow={Safe(windowMatched ? schedulerWindowId : "unknown")}; schedulerWindowMatched={Bool(windowMatched)}; request={Safe(command.RequestKind)}; originalExecuted={Bool(runOriginal)}; postfixCalls={calls}; originalRanCount={ran}; originalSkippedCount={skipped}; sainLayers={Safe(sainLayers)}; sainActiveLayer={Safe(sainLayer)}; sainCurrentAction={Safe(sainAction)}; moverPaused={Bool(paused)}; eftHasPath={Bool(hasPath)}; eftRealDestination={nativeDestination}; commandAnchor={commandDestination}; position={sampledPosition}; horizontalDeltaSincePreviousSample={delta}; deltaNotMovementAttribution=true; acquisitionNotAsserted=true; physicalProgressNotAsserted=true; mutation=false; traceOnly=true; tag={StatusTag}");
    }

    private static string Vector(Vector3 v) => v.x.ToString("0.0", CultureInfo.InvariantCulture)
        + "," + v.y.ToString("0.0", CultureInfo.InvariantCulture)
        + "," + v.z.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Bool(bool v) => v ? "true" : "false";
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#endif

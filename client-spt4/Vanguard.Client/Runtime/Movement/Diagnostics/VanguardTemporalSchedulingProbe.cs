#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Movement.Brain;

// Responsibility: Diagnostic probe for the current Vanguard/SAIN/BigBrain temporal scheduling boundary.
// Flow: It observes existing callbacks, stamps them with monotonic diagnostic sequences and emits Trace-only evidence during bounded capture windows.
// Authority boundary: Observation only. It never grants a lease, changes scheduler order, changes AI state, moves a bot, or participates in gameplay decisions.
// Invariant: Time.frameCount is diagnostic only; correctness must remain generation/lease/epoch based and independent of callback order.
namespace Vanguard.Client.Runtime.Movement.Diagnostics;

internal static class VanguardTemporalSchedulingProbeService
{
    public const string StatusTag = "VANGUARD_TC_TA_SCHEDULING_PROBE";

    private static readonly object Sync = new();
    private static readonly HashSet<string> PassiveTrackedBots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> LastLayerFrameByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> LastConsumeFrameByBot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> LastConsumeGenerationByBot = new(StringComparer.OrdinalIgnoreCase);

    private static long eventSequence;
    private static long vanguardFrameSequence;
    private static long snapshotBatchSequence;
    private static long lastSnapshotTicks;
    private static int authorityEpoch;
    private static DateTimeOffset burstUntilUtc = DateTimeOffset.MinValue;
    private static bool bootLogged;

    private static readonly TimeSpan InitialBurst = TimeSpan.FromSeconds(6.0d);
    private static readonly TimeSpan MovementBurst = TimeSpan.FromSeconds(6.0d);
    private const int MaxPassiveTrackedBots = 2;

    public static bool Enabled => VanguardOperatorRuntimeAuditOptions.IsTrace();

    public static void BeginVanguardFrame(DateTimeOffset now)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref vanguardFrameSequence);
        EnsureBootLogged(now);
    }

    public static void ObserveAuthorityEpoch(int epoch, string source)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Sync)
        {
            authorityEpoch = epoch;
            PassiveTrackedBots.Clear();
            LastLayerFrameByBot.Clear();
            LastConsumeFrameByBot.Clear();
            LastConsumeGenerationByBot.Clear();
            lastSnapshotTicks = 0L;
            snapshotBatchSequence = 0L;
            burstUntilUtc = DateTimeOffset.UtcNow + InitialBurst;
        }

        Emit("authority_epoch", () => $"source={Safe(source)}; newAuthorityEpoch={epoch}; probeStateReset=true; tcPlanGeneration=0; phase=pre_tc_baseline");
    }

    public static void ObserveSnapshotBatch(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (!Enabled || snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        DateTimeOffset batchAtUtc = snapshots
            .Where(static snapshot => snapshot != null)
            .Select(static snapshot => snapshot.CapturedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (batchAtUtc == DateTimeOffset.MinValue)
        {
            return;
        }

        long ticks = batchAtUtc.UtcDateTime.Ticks;
        long previous = Interlocked.Read(ref lastSnapshotTicks);
        if (ticks == previous)
        {
            return;
        }

        Interlocked.Exchange(ref lastSnapshotTicks, ticks);
        long batchSequence = Interlocked.Increment(ref snapshotBatchSequence);
        lock (Sync)
        {
            // Snapshot cadence must not keep the high-volume temporal capture permanently armed.
            // A raid-authority epoch opens the initial burst; active movement opens later bursts.
            if (burstUntilUtc == DateTimeOffset.MinValue)
            {
                burstUntilUtc = now + InitialBurst;
            }
        }

        string sampleBots = string.Join(",", snapshots
            .Where(static snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
            .Select(static snapshot => Safe(snapshot.BotProfileId))
            .Take(4));
        Emit("snapshot_batch", () => $"snapshotBatchSeq={batchSequence}; capturedAtUtc={batchAtUtc:O}; count={snapshots.Count}; sampleBots={Safe(sampleBots)}; immutableReadModel=true");
    }

    public static void ObserveSchedulerPass(
        IReadOnlyList<OperatorDecisionSnapshot> snapshots,
        DateTimeOffset now,
        DateTimeOffset batchAtUtc,
        bool refreshedSquadReadModel)
    {
        if (!Enabled)
        {
            return;
        }

        ExtendInitialBurstIfNeeded(snapshots, now);
        if (!IsBurstActive(now))
        {
            return;
        }

        Emit("vanguard_scheduler_pass", () =>
            $"snapshotBatchSeq={Interlocked.Read(ref snapshotBatchSequence)}; batchAtUtc={batchAtUtc:O}; snapshotCount={snapshots?.Count ?? 0}; refreshedSquadReadModel={Bool(refreshedSquadReadModel)}; schedulerCadence=5hz_current; tcPlanGeneration=0; phase=pre_tc_baseline");
    }

    public static void ObserveSainWorldTick(float deltaTime)
    {
        if (!Enabled || !IsBurstActive(DateTimeOffset.UtcNow))
        {
            return;
        }

        Emit("sain_world_tick", () => $"deltaTime={deltaTime.ToString("0.0000", CultureInfo.InvariantCulture)}; boundary=SAIN.Components.GameWorldComponent.WorldTick_postfix; observationOnly=true");
    }

    public static void ObserveBigBrainLayerIsActive(BotOwner botOwner, bool active, VanguardReturnMovementCommand command, string reason)
    {
        if (!Enabled || botOwner == null || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool emit;
        lock (Sync)
        {
            bool passiveTracked = PassiveTrackedBots.Contains(botOwner.ProfileId);
            if (!passiveTracked && PassiveTrackedBots.Count < MaxPassiveTrackedBots)
            {
                PassiveTrackedBots.Add(botOwner.ProfileId);
                passiveTracked = true;
            }

            if (active)
            {
                if (burstUntilUtc < now + MovementBurst)
                {
                    burstUntilUtc = now + MovementBurst;
                }
                emit = true;
            }
            else
            {
                int frame = Time.frameCount;
                emit = passiveTracked && (!LastLayerFrameByBot.TryGetValue(botOwner.ProfileId, out int previousFrame) || previousFrame != frame);
                if (emit)
                {
                    LastLayerFrameByBot[botOwner.ProfileId] = frame;
                }
            }
        }

        if (!emit || !IsBurstActive(now))
        {
            return;
        }

        Emit("bigbrain_layer_is_active", () =>
            $"botProfile={Safe(botOwner.ProfileId)}; result={Bool(active)}; reason={Safe(reason)}; lease={Safe(command.LeaseId)}; commandGeneration={command.Generation}; request={Safe(command.RequestKind)}");
    }

    public static void ObserveBigBrainActionSelected(BotOwner botOwner, string actionReason)
    {
        if (!Enabled || botOwner == null)
        {
            return;
        }

        ExtendMovementBurst(DateTimeOffset.UtcNow);
        Emit("bigbrain_action_selected", () => $"botProfile={Safe(botOwner.ProfileId)}; action={nameof(VanguardReturnMovementLogic)}; reason={Safe(actionReason)}");
    }

    public static void ObserveBigBrainActionStart(BotOwner botOwner)
    {
        if (!Enabled || botOwner == null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExtendMovementBurst(now);
        VanguardReturnMovementCommand command = VanguardReturnMovementCommand.Empty;
        if (!string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            VanguardReturnMovementCommandStore.TryGetActive(botOwner.ProfileId, now, out command);
        }
        Emit("bigbrain_action_start", () => $"botProfile={Safe(botOwner.ProfileId)}; action={nameof(VanguardReturnMovementLogic)}; lease={Safe(command.LeaseId)}; commandGeneration={command.Generation}; request={Safe(command.RequestKind)}");
    }

    public static void ObserveBigBrainActionStop(BotOwner? botOwner, VanguardReturnMovementCommand command)
    {
        if (!Enabled || botOwner == null)
        {
            return;
        }

        ExtendMovementBurst(DateTimeOffset.UtcNow);
        Emit("bigbrain_action_stop", () => $"botProfile={Safe(botOwner.ProfileId)}; action={nameof(VanguardReturnMovementLogic)}; lease={Safe(command.LeaseId)}; commandGeneration={command.Generation}; request={Safe(command.RequestKind)}");
    }

    public static void ObserveMovementCommandConsume(BotOwner botOwner, VanguardReturnMovementCommand command)
    {
        if (!Enabled || botOwner == null || string.IsNullOrWhiteSpace(botOwner.ProfileId))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExtendMovementBurst(now);
        int frame = Time.frameCount;
        bool emit;
        lock (Sync)
        {
            bool frameChanged = !LastConsumeFrameByBot.TryGetValue(botOwner.ProfileId, out int previousFrame) || previousFrame != frame;
            bool generationChanged = !LastConsumeGenerationByBot.TryGetValue(botOwner.ProfileId, out long previousGeneration) || previousGeneration != command.Generation;
            emit = frameChanged || generationChanged;
            if (emit)
            {
                LastConsumeFrameByBot[botOwner.ProfileId] = frame;
                LastConsumeGenerationByBot[botOwner.ProfileId] = command.Generation;
            }
        }

        if (!emit)
        {
            return;
        }

        Emit("movement_command_consume", () =>
            $"botProfile={Safe(botOwner.ProfileId)}; lease={Safe(command.LeaseId)}; commandGeneration={command.Generation}; request={Safe(command.RequestKind)}; expiresAtUtc={command.ExpiresAtUtc:O}; activeLeaseGenerationPair=true");
    }

    private static void EnsureBootLogged(DateTimeOffset now)
    {
        bool emit = false;
        lock (Sync)
        {
            if (!bootLogged)
            {
                bootLogged = true;
                burstUntilUtc = now + InitialBurst;
                emit = true;
            }
        }

        if (!emit)
        {
            return;
        }

        Emit("probe_boot", () =>
            $"traceRequired=true; role={RuntimeRole()}; fikaInstalled={Bool(VanguardFikaCompat.IsInstalled)}; authority={Bool(VanguardFikaCompat.IsRaidAuthority)}; initialBurstSeconds={InitialBurst.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}; movementBurstSeconds={MovementBurst.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}; timeFrameCountDiagnosticOnly=true; gameplayMutation=false");
    }

    private static void ExtendInitialBurstIfNeeded(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return;
        }

        lock (Sync)
        {
            if (burstUntilUtc == DateTimeOffset.MinValue)
            {
                burstUntilUtc = now + InitialBurst;
            }
        }
    }

    private static void ExtendMovementBurst(DateTimeOffset now)
    {
        lock (Sync)
        {
            if (burstUntilUtc < now + MovementBurst)
            {
                burstUntilUtc = now + MovementBurst;
            }
        }
    }

    private static bool IsBurstActive(DateTimeOffset now)
    {
        lock (Sync)
        {
            return now <= burstUntilUtc;
        }
    }

    private static void Emit(string phase, Func<string> details)
    {
        if (!Enabled)
        {
            return;
        }

        long sequence = Interlocked.Increment(ref eventSequence);
        long frameSequence = Interlocked.Read(ref vanguardFrameSequence);
        int epoch;
        lock (Sync)
        {
            epoch = authorityEpoch;
        }

        int unityFrame = Time.frameCount;
        VanguardClientDiagnosticsLog.Trace(StatusTag, () =>
            $"eventSeq={sequence}; phase={Safe(phase)}; utc={DateTimeOffset.UtcNow:O}; unityFrame={unityFrame}; vanguardFrameSeqObserved={frameSequence}; authorityEpoch={epoch}; role={RuntimeRole()}; {details()}");
    }

    private static string RuntimeRole()
    {
        if (!VanguardFikaCompat.IsInstalled)
        {
            return "direct_spt_host";
        }
        if (VanguardFikaCompat.IsActualHeadlessProcess)
        {
            return "fika_headless";
        }
        if (VanguardFikaCompat.IsDirectPlayerRaidHost)
        {
            return "fika_local_host";
        }
        if (VanguardFikaCompat.IsClient)
        {
            return "fika_joined_client";
        }
        return VanguardFikaCompat.IsRaidAuthority ? "fika_authority_other" : "non_authority";
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    }
}

/// <summary>
/// Exact SAIN diagnostic boundary used by the temporal scheduling probe. Patching GameWorldComponent.WorldTick
/// observes SAIN after its GameWorld.DoWorldTick postfix has entered SAIN's own world update path.
/// The postfix is observation-only and enabled only when the exact runtime type/method is available.
/// </summary>
internal sealed class VanguardTemporalSchedulingProbePatch : ModulePatch
{
    private const string TargetTypeName = "SAIN.Components.GameWorldComponent";
    private const string TargetMethodName = "WorldTick";

    public static bool IsRuntimeTypeAvailable
    {
        get
        {
            Type? targetType = AccessTools.TypeByName(TargetTypeName);
            return targetType != null && AccessTools.Method(targetType, TargetMethodName, new[] { typeof(float) }) != null;
        }
    }

    protected override MethodBase GetTargetMethod()
    {
        Type targetType = AccessTools.TypeByName(TargetTypeName)
            ?? throw new InvalidOperationException(TargetTypeName + " not found for temporal scheduling probe.");
        return AccessTools.Method(targetType, TargetMethodName, new[] { typeof(float) })
            ?? throw new InvalidOperationException(TargetTypeName + "." + TargetMethodName + " not found for temporal scheduling probe.");
    }

    [PatchPostfix]
    private static void PatchPostfix(float __0)
    {
        VanguardTemporalSchedulingProbeService.ObserveSainWorldTick(__0);
    }
}
#endif

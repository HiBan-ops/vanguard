#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using Newtonsoft.Json;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Transports the small authoritative raid-state snapshots needed by player-client HUDs when gameplay truth lives on a Fika host or Headless.
// Flow: The gameplay authority publishes bounded semantic HUD and medical packets; player clients validate/store the newest packet and project it locally without running duplicate AI decisions.
// Authority boundary: Headless/direct host owns raid-state publication, Fika owns transport, and each player client owns only local HUD presentation settings.
// Invariant: Telemetry is read-only, protocol-bounded and monotonic enough to reject stale data; a missing Fika channel must never change gameplay behavior.
namespace Vanguard.Client.Raid.Runtime.Fika;

/// <summary>
/// Fika read-only telemetry bridge used by Vanguard HUD presentation.
///
/// The historical semantic HUD channel remains protocol v1 and carries only activity/alert/detail
/// data consumed by the fixed HUD. Medical truth is isolated in a second compact packet/store so a
/// medical schema change can never invalidate or oversize the fixed-HUD transport.
///
/// Fika remains optional. When Fika is absent, this bridge is inert and the floating Operator HUD
/// keeps its canonical local medical capture path. No AI authority or local F12 presentation setting
/// is accepted from either network channel.
/// </summary>
internal static class VanguardFikaHudTelemetryService
{
    public const int ProtocolVersion = 1;
    public const int MedicalProtocolVersion = 1;
    public const string StatusTag = "VANGUARD_AUTHORITATIVE_FIKA_HUD_TELEMETRY_STATUS";
    public const string MedicalStatusTag = "VANGUARD_HUD_MEDICAL_TRANSPORT_ISOLATION_STATUS";
    public const string ConvergenceStatusTag = "VANGUARD_HUD_CANONICAL_LOOT_ACTIVITY_AND_PANEL_CONVERGENCE_STATUS";

    // LiteNetLib/Fika rejects Unreliable and ReliableSequenced packets above 1023 bytes. Keep a
    // deliberate margin because the returned writer bytes are the exact bytes handed to SendToAll.
    private const int MaxSerializedPacketBytes = 1000;
    private const int MaxEntriesPerPublish = 16;

    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(0.75d);
    private static readonly TimeSpan BindRetryInterval = TimeSpan.FromSeconds(1.0d);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(12.0d);

    private static readonly Type? NetworkManagerInterfaceType = ResolveType("Fika.Core.Networking.IFikaNetworkManager", "Fika.Core");

    private static object? boundManager;
    private static object? boundPacketProcessor;
    private static DateTimeOffset nextBindAttemptUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextPublishUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextSummaryUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextWarningUtc = DateTimeOffset.MinValue;
    private static string publisherSessionId = Guid.NewGuid().ToString("N");
    private static long publisherSequence;
    private static long medicalPublisherSequence;
    private static bool bindingLogged;
    private static bool publisherLogged;
    private static bool medicalPublisherLogged;
    private static int receivedFrames;
    private static int sentFrames;
    private static int lootProjectedFrames;
    private static int lootProjectedEntries;
    private static int semanticSerializedBytesMax;
    private static int semanticSplitFrames;
    private static int semanticOversizeDrops;
    private static int medicalReceivedFrames;
    private static int medicalSentFrames;
    private static int medicalReadableEntries;
    private static int medicalEffectEntries;
    private static int medicalClearEntries;
    private static int medicalSerializedBytesMax;
    private static int medicalSplitFrames;
    private static int medicalOversizeDrops;

    public static void ResetForRaidLifecycle(string reason)
    {
        VanguardFikaHudTelemetryStore.Reset(reason);
        VanguardFikaHudMedicalTelemetryStore.Reset(reason);
        publisherSessionId = Guid.NewGuid().ToString("N");
        publisherSequence = 0;
        medicalPublisherSequence = 0;
        nextBindAttemptUtc = DateTimeOffset.MinValue;
        nextPublishUtc = DateTimeOffset.MinValue;
        nextSummaryUtc = DateTimeOffset.MinValue;
        nextWarningUtc = DateTimeOffset.MinValue;
        publisherLogged = false;
        medicalPublisherLogged = false;
        receivedFrames = 0;
        sentFrames = 0;
        lootProjectedFrames = 0;
        lootProjectedEntries = 0;
        semanticSerializedBytesMax = 0;
        semanticSplitFrames = 0;
        semanticOversizeDrops = 0;
        medicalReceivedFrames = 0;
        medicalSentFrames = 0;
        medicalReadableEntries = 0;
        medicalEffectEntries = 0;
        medicalClearEntries = 0;
        medicalSerializedBytesMax = 0;
        medicalSplitFrames = 0;
        medicalOversizeDrops = 0;
        // Keep the current Fika packet subscriptions when the manager survives a raid transition.
        // A manager replacement is detected by reference and rebound automatically on the next Tick.
    }

    public static void Tick(IReadOnlyList<OperatorDecisionSnapshot> authoritativeSnapshots, DateTimeOffset now)
    {
        if (!VanguardFikaCompat.IsInstalled)
        {
            return;
        }

        EnsureBound(now);
        if (boundManager is null || boundPacketProcessor is null)
        {
            return;
        }

        bool publisherAuthority = IsServerManager(boundManager)
            && (VanguardFikaCompat.IsActualHeadlessProcess || VanguardFikaCompat.IsDirectPlayerRaidHost);
        if (!publisherAuthority || now < nextPublishUtc || authoritativeSnapshots.Count == 0)
        {
            MaybeLogSummary(now, publisherAuthority);
            return;
        }

        nextPublishUtc = now + PublishInterval;
        PublishSemantic(authoritativeSnapshots, now);
        PublishMedical(authoritativeSnapshots, now);
        MaybeLogSummary(now, publisherAuthority);
    }

    private static void EnsureBound(DateTimeOffset now)
    {
        if (now < nextBindAttemptUtc)
        {
            return;
        }

        nextBindAttemptUtc = now + BindRetryInterval;
        object? manager = TryGetNetworkManager();
        if (manager is null)
        {
            boundManager = null;
            boundPacketProcessor = null;
            bindingLogged = false;
            return;
        }

        if (ReferenceEquals(manager, boundManager) && boundPacketProcessor is not null)
        {
            return;
        }

        try
        {
            object? packetProcessor = manager.GetType()
                .GetField("_packetProcessor", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(manager);
            if (packetProcessor is null)
            {
                return;
            }

            MethodInfo? subscribe = packetProcessor.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "SubscribeReusable"
                    && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 1
                    && method.GetParameters().Length == 1);
            if (subscribe is null)
            {
                WarnThrottled(now, "fika NetPacketProcessor.SubscribeReusable<T> unavailable; HUD telemetry disabled fail-open");
                return;
            }

            var semanticCallback = new Action<VanguardFikaHudTelemetryPacket>(OnPacketReceived);
            subscribe.MakeGenericMethod(typeof(VanguardFikaHudTelemetryPacket)).Invoke(packetProcessor, new object[] { semanticCallback });

            var medicalCallback = new Action<VanguardFikaHudMedicalTelemetryPacket>(OnMedicalPacketReceived);
            subscribe.MakeGenericMethod(typeof(VanguardFikaHudMedicalTelemetryPacket)).Invoke(packetProcessor, new object[] { medicalCallback });

            boundManager = manager;
            boundPacketProcessor = packetProcessor;
            bindingLogged = true;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"Fika HUD telemetry transport bound manager={manager.GetType().FullName}; semanticPacket={typeof(VanguardFikaHudTelemetryPacket).FullName}; semanticProtocol={ProtocolVersion}; medicalPacket={typeof(VanguardFikaHudMedicalTelemetryPacket).FullName}; medicalProtocol={MedicalProtocolVersion}; maxSerializedBytes={MaxSerializedPacketBytes}; optionalFikaDependency=true; aiAuthorityMutation=false; presentationSync=false; build={VanguardBuildVersion.BuildLabel}");
        }
        catch (Exception exception)
        {
            boundManager = null;
            boundPacketProcessor = null;
            WarnThrottled(now, $"Fika HUD telemetry bind failed fail-open: {Unwrap(exception).GetType().Name}: {Unwrap(exception).Message}");
        }
    }

    private static void PublishSemantic(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        try
        {
            var entries = snapshots
                .Where(snapshot => snapshot is not null
                    && !ReferenceEquals(snapshot, OperatorDecisionSnapshot.Empty)
                    && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
                .Take(MaxEntriesPerPublish)
                .Select(snapshot =>
                {
                    VanguardOperatorHudSemanticProjection semantic = VanguardOperatorHudSemanticProjector.Project(snapshot, now);
                    return new VanguardFikaHudTelemetryEntry
                    {
                        BotProfileId = snapshot.BotProfileId,
                        ActivityLabel = semantic.ActivityLabel,
                        AlertLabel = semantic.AlertLabel,
                        AlertSeverity = semantic.AlertSeverity,
                        Detail = semantic.Detail,
                        Urgent = semantic.Urgent,
                    };
                })
                .ToArray();

            if (entries.Length == 0)
            {
                return;
            }

            PublishSemanticEntries(entries, now, allowSplit: true);
        }
        catch (Exception exception)
        {
            WarnThrottled(now, $"Fika HUD semantic telemetry publish failed fail-open: {Unwrap(exception).GetType().Name}: {Unwrap(exception).Message}");
        }
    }

    private static void PublishSemanticEntries(VanguardFikaHudTelemetryEntry[] entries, DateTimeOffset now, bool allowSplit)
    {
        long sequence = publisherSequence + 1;
        var payload = new VanguardFikaHudTelemetryPayload
        {
            ProtocolVersion = ProtocolVersion,
            BuildLabel = VanguardBuildVersion.BuildLabel,
            SessionId = publisherSessionId,
            Sequence = sequence,
            SentAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            Entries = entries,
        };
        var packet = new VanguardFikaHudTelemetryPacket
        {
            Payload = JsonConvert.SerializeObject(payload, Formatting.None),
        };

        if (!TrySendServerPacket(packet, out int serializedBytes, out string failure))
        {
            semanticSerializedBytesMax = Math.Max(semanticSerializedBytesMax, serializedBytes);
            if (IsOversizeFailure(failure) && allowSplit && entries.Length > 1)
            {
                semanticSplitFrames++;
                int midpoint = entries.Length / 2;
                PublishSemanticEntries(entries.Take(midpoint).ToArray(), now, allowSplit: true);
                PublishSemanticEntries(entries.Skip(midpoint).ToArray(), now, allowSplit: true);
                return;
            }

            if (IsOversizeFailure(failure))
            {
                semanticOversizeDrops++;
            }

            WarnThrottled(now, $"Fika HUD semantic telemetry send skipped fail-open entries={entries.Length}; serializedBytes={serializedBytes}; maxBytes={MaxSerializedPacketBytes}; reason={failure}");
            return;
        }

        publisherSequence = sequence;
        semanticSerializedBytesMax = Math.Max(semanticSerializedBytesMax, serializedBytes);
        sentFrames++;

        int lootEntries = entries.Count(entry => string.Equals(entry.ActivityLabel, "LOOT", StringComparison.Ordinal));
        if (lootEntries > 0)
        {
            lootProjectedFrames++;
            lootProjectedEntries += lootEntries;
        }

        if (!publisherLogged)
        {
            publisherLogged = true;
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"authoritative HUD semantic publisher active cadenceMs={PublishInterval.TotalMilliseconds:0}; manager={boundManager?.GetType().Name}; fields=botProfileId,activity,alert,severity,detail,urgent,sequence,timestamp; protocol={ProtocolVersion}; medicalStateTransport=false; healthTransport=false; localF12Transport=false; sizeGuardBytes={MaxSerializedPacketBytes}; build={VanguardBuildVersion.BuildLabel}");
            VanguardClientDiagnosticsLog.Info(
                ConvergenceStatusTag,
                $"HUD semantic convergence active canonicalPrimaryExecutionLoot=true; legacyLootFallback=true; protocolUnchanged={ProtocolVersion}; medicalChannelIsolated=true; gameplayMutation=false; build={VanguardBuildVersion.BuildLabel}");
        }
    }

    private static void PublishMedical(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        try
        {
            var entries = snapshots
                .Where(snapshot => snapshot is not null
                    && !ReferenceEquals(snapshot, OperatorDecisionSnapshot.Empty)
                    && !string.IsNullOrWhiteSpace(snapshot.BotProfileId))
                .Take(MaxEntriesPerPublish)
                .Select(snapshot => new VanguardFikaHudMedicalTelemetryEntry
                {
                    BotProfileId = snapshot.BotProfileId,
                    MedicalMask = BuildMedicalMask(snapshot),
                })
                .ToArray();

            if (entries.Length == 0)
            {
                return;
            }

            PublishMedicalEntries(entries, now, allowSplit: true);
        }
        catch (Exception exception)
        {
            WarnThrottled(now, $"Fika HUD medical telemetry publish failed fail-open: {Unwrap(exception).GetType().Name}: {Unwrap(exception).Message}");
        }
    }

    private static void PublishMedicalEntries(VanguardFikaHudMedicalTelemetryEntry[] entries, DateTimeOffset now, bool allowSplit)
    {
        long sequence = medicalPublisherSequence + 1;
        var payload = new VanguardFikaHudMedicalTelemetryPayload
        {
            ProtocolVersion = MedicalProtocolVersion,
            BuildLabel = VanguardBuildVersion.BuildLabel,
            SessionId = publisherSessionId,
            Sequence = sequence,
            SentAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            Entries = entries,
        };
        var packet = new VanguardFikaHudMedicalTelemetryPacket
        {
            Payload = JsonConvert.SerializeObject(payload, Formatting.None),
        };

        if (!TrySendServerPacket(packet, out int serializedBytes, out string failure))
        {
            medicalSerializedBytesMax = Math.Max(medicalSerializedBytesMax, serializedBytes);
            if (IsOversizeFailure(failure) && allowSplit && entries.Length > 1)
            {
                medicalSplitFrames++;
                int midpoint = entries.Length / 2;
                PublishMedicalEntries(entries.Take(midpoint).ToArray(), now, allowSplit: true);
                PublishMedicalEntries(entries.Skip(midpoint).ToArray(), now, allowSplit: true);
                return;
            }

            if (IsOversizeFailure(failure))
            {
                medicalOversizeDrops++;
            }

            WarnThrottled(now, $"Fika HUD medical telemetry send skipped fail-open entries={entries.Length}; serializedBytes={serializedBytes}; maxBytes={MaxSerializedPacketBytes}; reason={failure}");
            return;
        }

        medicalPublisherSequence = sequence;
        medicalSerializedBytesMax = Math.Max(medicalSerializedBytesMax, serializedBytes);
        medicalSentFrames++;

        int readable = entries.Count(entry => (entry.MedicalMask & VanguardFikaHudMedicalMask.Readable) != 0);
        int active = entries.Count(entry => IsMedicalEffectActive(entry.MedicalMask));
        medicalReadableEntries += readable;
        medicalEffectEntries += active;
        medicalClearEntries += readable - active;

        if (!medicalPublisherLogged)
        {
            medicalPublisherLogged = true;
            VanguardClientDiagnosticsLog.Info(
                MedicalStatusTag,
                $"authoritative medical HUD sidecar active cadenceMs={PublishInterval.TotalMilliseconds:0}; manager={boundManager?.GetType().Name}; encoding=profileId+bitmask; bits=readable,HB,LB,FR,PN,TR; protocol={MedicalProtocolVersion}; semanticHudIndependent=true; localCanonicalFallback=true; sizeGuardBytes={MaxSerializedPacketBytes}; build={VanguardBuildVersion.BuildLabel}");
        }
    }

    private static byte BuildMedicalMask(OperatorDecisionSnapshot snapshot)
    {
        byte mask = 0;
        if (snapshot.Medical.Need.IsReadable) mask |= VanguardFikaHudMedicalMask.Readable;
        if (snapshot.Medical.Need.HasHeavyBleed) mask |= VanguardFikaHudMedicalMask.HeavyBleed;
        if (snapshot.Medical.Need.HasLightBleed) mask |= VanguardFikaHudMedicalMask.LightBleed;
        if (snapshot.Medical.Need.HasFracture) mask |= VanguardFikaHudMedicalMask.Fracture;
        if (snapshot.Medical.Need.HasPain) mask |= VanguardFikaHudMedicalMask.Pain;
        if (snapshot.Medical.Need.HasTremor) mask |= VanguardFikaHudMedicalMask.Tremor;
        return mask;
    }

    private static bool IsMedicalEffectActive(byte mask)
    {
        byte effectBits = (byte)(VanguardFikaHudMedicalMask.HeavyBleed
            | VanguardFikaHudMedicalMask.LightBleed
            | VanguardFikaHudMedicalMask.Fracture
            | VanguardFikaHudMedicalMask.Pain
            | VanguardFikaHudMedicalMask.Tremor);
        return (mask & VanguardFikaHudMedicalMask.Readable) != 0 && (mask & effectBits) != 0;
    }

    private static void OnPacketReceived(VanguardFikaHudTelemetryPacket packet)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            if (packet is null || string.IsNullOrWhiteSpace(packet.Payload) || packet.Payload.Length > 32767)
            {
                return;
            }

            VanguardFikaHudTelemetryPayload? payload = JsonConvert.DeserializeObject<VanguardFikaHudTelemetryPayload>(packet.Payload);
            if (payload is null)
            {
                return;
            }

            if (VanguardFikaHudTelemetryStore.TryApply(payload, now, out string reason))
            {
                receivedFrames++;
                return;
            }

            if (!string.Equals(reason, "duplicate_or_out_of_order", StringComparison.Ordinal))
            {
                WarnThrottled(now, $"Fika HUD semantic telemetry frame rejected reason={reason}");
            }
        }
        catch (Exception exception)
        {
            WarnThrottled(now, $"Fika HUD semantic telemetry receive failed fail-open: {Unwrap(exception).GetType().Name}: {Unwrap(exception).Message}");
        }
    }

    private static void OnMedicalPacketReceived(VanguardFikaHudMedicalTelemetryPacket packet)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            if (packet is null || string.IsNullOrWhiteSpace(packet.Payload) || packet.Payload.Length > 32767)
            {
                return;
            }

            VanguardFikaHudMedicalTelemetryPayload? payload = JsonConvert.DeserializeObject<VanguardFikaHudMedicalTelemetryPayload>(packet.Payload);
            if (payload is null)
            {
                return;
            }

            if (VanguardFikaHudMedicalTelemetryStore.TryApply(payload, now, out string reason))
            {
                medicalReceivedFrames++;
                return;
            }

            if (!string.Equals(reason, "duplicate_or_out_of_order", StringComparison.Ordinal))
            {
                WarnThrottled(now, $"Fika HUD medical telemetry frame rejected reason={reason}");
            }
        }
        catch (Exception exception)
        {
            WarnThrottled(now, $"Fika HUD medical telemetry receive failed fail-open: {Unwrap(exception).GetType().Name}: {Unwrap(exception).Message}");
        }
    }

    private static bool TrySendServerPacket<TPacket>(TPacket packet, out int serializedBytes, out string failure)
        where TPacket : class
    {
        serializedBytes = 0;
        failure = string.Empty;
        object? manager = boundManager;
        object? packetProcessor = boundPacketProcessor;
        if (manager is null || packetProcessor is null || !IsServerManager(manager))
        {
            failure = "server_manager_unavailable";
            return false;
        }

        object? writer = manager.GetType()
            .GetField("_dataWriter", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(manager);
        object? netServer = manager.GetType()
            .GetProperty("NetServer", BindingFlags.Instance | BindingFlags.Public)?
            .GetValue(manager);
        if (writer is null || netServer is null)
        {
            failure = "server_writer_or_netmanager_unavailable";
            return false;
        }

        MethodInfo? reset = writer.GetType().GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        MethodInfo? putByte = writer.GetType().GetMethod("Put", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte) }, null);
        MethodInfo? copyData = writer.GetType().GetMethod("CopyData", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        MethodInfo? write = packetProcessor.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "Write"
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.GetParameters().Length == 2);
        if (reset is null || putByte is null || copyData is null || write is null)
        {
            failure = "fika_serializer_surface_unavailable";
            return false;
        }

        reset.Invoke(writer, null);
        // Fika 2.3.9 NetworkUtils.EPacketType.Serializable == 0. Server packets do not carry
        // the client-side broadcast byte; FikaClient.OnNetworkReceive reads this enum first.
        putByte.Invoke(writer, new object[] { (byte)0 });
        write.MakeGenericMethod(typeof(TPacket)).Invoke(packetProcessor, new object[] { writer, packet });
        if (copyData.Invoke(writer, null) is not byte[] data || data.Length == 0)
        {
            failure = "serialized_packet_empty";
            return false;
        }

        serializedBytes = data.Length;
        if (serializedBytes > MaxSerializedPacketBytes)
        {
            failure = $"serialized_packet_oversize:{serializedBytes}>{MaxSerializedPacketBytes}";
            return false;
        }

        MethodInfo? sendToAll = netServer.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "SendToAll"
                && method.GetParameters().Length == 2
                && method.GetParameters()[0].ParameterType == typeof(byte[])
                && method.GetParameters()[1].ParameterType.IsEnum);
        if (sendToAll is null)
        {
            failure = "fika_sendtoall_surface_unavailable";
            return false;
        }

        Type deliveryType = sendToAll.GetParameters()[1].ParameterType;
        object delivery = Enum.Parse(deliveryType, "Unreliable", ignoreCase: false);
        sendToAll.Invoke(netServer, new object[] { data, delivery });
        return true;
    }

    private static bool IsOversizeFailure(string failure)
    {
        return failure.StartsWith("serialized_packet_oversize:", StringComparison.Ordinal);
    }

    private static object? TryGetNetworkManager()
    {
        Type? managerInterface = NetworkManagerInterfaceType;
        if (managerInterface is null)
        {
            return null;
        }

        try
        {
            Type singletonType = typeof(Singleton<>).MakeGenericType(managerInterface);
            PropertyInfo? instantiatedProperty = singletonType.GetProperty("Instantiated", BindingFlags.Public | BindingFlags.Static);
            if (instantiatedProperty?.GetValue(null) is bool instantiated && !instantiated)
            {
                return null;
            }

            return singletonType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsServerManager(object manager)
    {
        return string.Equals(manager.GetType().FullName, "Fika.Core.Networking.FikaServer", StringComparison.Ordinal);
    }

    private static void MaybeLogSummary(DateTimeOffset now, bool publisherAuthority)
    {
        if (now < nextSummaryUtc || (!bindingLogged && boundManager is null))
        {
            return;
        }

        nextSummaryUtc = now + SummaryInterval;
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"HUD semantic telemetry summary manager={boundManager?.GetType().Name ?? "none"}; publisherAuthority={publisherAuthority}; sentFrames={sentFrames}; receivedFrames={receivedFrames}; cachedOperators={VanguardFikaHudTelemetryStore.Count}; lootProjectedFrames={lootProjectedFrames}; lootProjectedEntries={lootProjectedEntries}; serializedBytesMax={semanticSerializedBytesMax}; splitFrames={semanticSplitFrames}; oversizeDrops={semanticOversizeDrops}; maxBytes={MaxSerializedPacketBytes}; medicalChannelIsolated=true; presentationLocalOnly=true; aiAuthorityMutation=false");
        VanguardClientDiagnosticsLog.Info(
            MedicalStatusTag,
            $"HUD medical telemetry summary manager={boundManager?.GetType().Name ?? "none"}; publisherAuthority={publisherAuthority}; sentFrames={medicalSentFrames}; receivedFrames={medicalReceivedFrames}; cachedOperators={VanguardFikaHudMedicalTelemetryStore.Count}; medicalReadableEntries={medicalReadableEntries}; medicalEffectEntries={medicalEffectEntries}; medicalClearEntries={medicalClearEntries}; serializedBytesMax={medicalSerializedBytesMax}; splitFrames={medicalSplitFrames}; oversizeDrops={medicalOversizeDrops}; maxBytes={MaxSerializedPacketBytes}; semanticHudIndependent=true; localCanonicalFallback=true; aiAuthorityMutation=false");
    }

    private static void WarnThrottled(DateTimeOffset now, string message)
    {
        if (now < nextWarningUtc)
        {
            return;
        }

        nextWarningUtc = now + TimeSpan.FromSeconds(10.0d);
        VanguardClientDiagnosticsLog.Warning(StatusTag, message);
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : exception;
    }

    private static Type? ResolveType(string fullName, string assemblyName)
    {
        return Type.GetType($"{fullName}, {assemblyName}", throwOnError: false)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type is not null);
    }
}
#else
namespace Vanguard.Client.Raid.Runtime.Fika;

internal static class VanguardFikaHudTelemetryService
{
}
#endif

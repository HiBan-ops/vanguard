#if SPT_CLIENT
using System;
using System.Collections.Generic;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Runtime.Decision;
using Vanguard.Client.Runtime.Execution;
using Vanguard.Client.Runtime.Medical;

// Responsibility: Provides Medical Native Attempt Memory support for the medical runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Medical.Execution;

/// <summary>
/// The runtime state-bound memory for native medical controller starts that never commit a resource or
/// produce a medical effect. This is deliberately separate from the existing no-effect circuit:
/// a start stall means the item was not medically tested, while a no-effect outcome means native
/// use really committed. Two identical pre-commit stalls block only the exact item instance for
/// the current need/target episode so the selector can move to a viable alternative.
/// </summary>
internal static class VanguardMedicalNativeAttemptMemory
{
    public const string StatusTag = "VANGUARD_MEDICAL_NATIVE_ATTEMPT_MEMORY_STATUS";
    private const int BlockThreshold = 2;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, AttemptRecord> Records = new(StringComparer.OrdinalIgnoreCase);

    public static void Reset(string reason)
    {
        lock (Sync) Records.Clear();
        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_MEDICAL_NATIVE_ATTEMPT_MEMORY_RESET reason={Safe(reason)}; threshold={BlockThreshold}; exactItem=true; stateBound=true; surgeryDebtSeparate=true; threatInterruptionsIgnored=true; tag={StatusTag}");
    }

    public static void ObserveSnapshots(IReadOnlyList<OperatorDecisionSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots == null || snapshots.Count == 0) return;
        lock (Sync)
        {
            foreach (OperatorDecisionSnapshot snapshot in snapshots)
            {
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.BotProfileId)) continue;
                string bot = Normalize(snapshot.BotProfileId);
                VanguardMedicalNeed currentNeed = snapshot.Medical.Need.DominantNeed;
                string currentTarget = Normalize(!string.IsNullOrWhiteSpace(snapshot.Medical.Actionability.TargetPart)
                    && !string.Equals(snapshot.Medical.Actionability.TargetPart, "none", StringComparison.OrdinalIgnoreCase)
                        ? snapshot.Medical.Actionability.TargetPart
                        : snapshot.Medical.Need.TargetPart);
                var remove = new List<string>();
                foreach (KeyValuePair<string, AttemptRecord> pair in Records)
                {
                    AttemptRecord record = pair.Value;
                    if (!string.Equals(record.BotProfileId, bot, StringComparison.OrdinalIgnoreCase)) continue;
                    bool episodeChanged = !snapshot.Alive
                        || currentNeed != record.Need
                        || !string.Equals(currentTarget, record.TargetPart, StringComparison.OrdinalIgnoreCase);
                    if (episodeChanged) remove.Add(pair.Key);
                }
                foreach (string key in remove) Records.Remove(key);
            }
        }
    }

    public static AttemptRecord RegisterPreCommitStartStall(VanguardExecutionLeaseState lease, DateTimeOffset now, string reason)
    {
        string key = BuildKey(lease.BotProfileId, lease.MedicalNeed, lease.TargetPart, lease.ItemTemplateId, lease.ItemInstanceId);
        string signature = string.IsNullOrWhiteSpace(lease.EffectSignature)
            ? BuildFallbackSignature(lease.InitialItemResource, lease.InitialItemMaxResource)
            : lease.EffectSignature;
        lock (Sync)
        {
            Records.TryGetValue(key, out AttemptRecord? previous);
            bool sameState = previous != null && string.Equals(previous.Signature, signature, StringComparison.OrdinalIgnoreCase);
            int count = sameState ? previous!.ConsecutiveStartStalls + 1 : 1;
            var record = new AttemptRecord
            {
                Key = key,
                BotProfileId = Normalize(lease.BotProfileId),
                Need = lease.MedicalNeed,
                TargetPart = Normalize(lease.TargetPart),
                ItemTemplateId = Normalize(lease.ItemTemplateId),
                ItemInstanceId = Normalize(lease.ItemInstanceId),
                Signature = signature,
                ConsecutiveStartStalls = count,
                Blocked = count >= BlockThreshold,
                RecordedAtUtc = now,
                Reason = Safe(reason)
            };
            Records[key] = record;
            VanguardClientDiagnosticsLog.Warning(StatusTag,
                $"VANGUARD_MEDICAL_NATIVE_START_STALL_RECORDED {lease.Summary}; count={count}; blocked={Bool(record.Blocked)}; threshold={BlockThreshold}; resourceCommitted=false; medicalEffect=false; threatInterruption=false; exactItem=true; episodeBound=true; reason={Safe(reason)}; tag={StatusTag}");
            return record;
        }
    }

    public static bool IsBlocked(
        string? botProfileId,
        VanguardMedicalNeed need,
        string? targetPart,
        string? itemTemplateId,
        string? itemInstanceId,
        string? effectSignature,
        out AttemptRecord record)
    {
        string key = BuildKey(botProfileId, need, targetPart, itemTemplateId, itemInstanceId);
        string signature = string.IsNullOrWhiteSpace(effectSignature)
            ? "none"
            : effectSignature.Trim();
        lock (Sync)
        {
            if (!Records.TryGetValue(key, out AttemptRecord? found))
            {
                record = null!;
                return false;
            }

            if (!string.Equals(found.Signature, signature, StringComparison.OrdinalIgnoreCase))
            {
                Records.Remove(key);
                record = null!;
                return false;
            }

            record = found;
            return found.Blocked;
        }
    }

    public static void ClearOnMedicalSuccess(VanguardExecutionLeaseState lease, string reason)
    {
        string key = BuildKey(lease.BotProfileId, lease.MedicalNeed, lease.TargetPart, lease.ItemTemplateId, lease.ItemInstanceId);
        bool removed;
        lock (Sync) removed = Records.Remove(key);
        if (removed)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"VANGUARD_MEDICAL_NATIVE_ATTEMPT_MEMORY_CLEARED {lease.Summary}; reason={Safe(reason)}; effectConfirmed=true; exactItem=true; tag={StatusTag}");
        }
    }

    private static string BuildKey(string? botProfileId, VanguardMedicalNeed need, string? targetPart, string? itemTemplateId, string? itemInstanceId)
    {
        return Normalize(botProfileId) + "|" + need + "|" + Normalize(targetPart) + "|" + Normalize(itemTemplateId) + "|" + Normalize(itemInstanceId);
    }

    private static string BuildFallbackSignature(float resource, float maximum)
    {
        return "resource=" + resource.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + "/" + maximum.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
    private static string Bool(bool value) => value ? "true" : "false";

    internal sealed class AttemptRecord
    {
        public string Key = "none";
        public string BotProfileId = "none";
        public VanguardMedicalNeed Need;
        public string TargetPart = "none";
        public string ItemTemplateId = "none";
        public string ItemInstanceId = "none";
        public string Signature = "none";
        public int ConsecutiveStartStalls;
        public bool Blocked;
        public DateTimeOffset RecordedAtUtc;
        public string Reason = "none";

        public string Summary => "count=" + ConsecutiveStartStalls
            + ";blocked=" + (Blocked ? "true" : "false")
            + ";itemInstance=" + ItemInstanceId
            + ";signature=" + Signature
            + ";reason=" + Reason;
    }
}
#endif

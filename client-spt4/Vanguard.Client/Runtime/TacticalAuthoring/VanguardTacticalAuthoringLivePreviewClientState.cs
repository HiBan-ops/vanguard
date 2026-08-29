#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vanguard.Client.Api.Dtos;

// Responsibility: Defines data/state contracts used by the tactical-authoring runtime, centered on Tactical Authoring Live Preview Client State.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Runtime.TacticalAuthoring;

/// <summary>Player-client read model for transient headless validation/placement feedback.</summary>
internal static class VanguardTacticalAuthoringLivePreviewClientState
{
    private static VanguardTacticalAuthoringHeadlessPreviewResult? latest;
    private static readonly Dictionary<string, VanguardTacticalAuthoringHeadlessSlotResult> BySlot = new(StringComparer.Ordinal);
    private static string expectedSessionId = string.Empty;
    private static string expectedMapId = string.Empty;
    private static long expectedRevision;

    public static void Expect(string sessionId, string mapId, long revision)
    {
        bool changed = !string.Equals(expectedSessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(expectedMapId, mapId, StringComparison.OrdinalIgnoreCase)
            || expectedRevision != revision;
        expectedSessionId = sessionId ?? string.Empty;
        expectedMapId = mapId ?? string.Empty;
        expectedRevision = revision;
        if (changed)
        {
            latest = null;
            BySlot.Clear();
        }
    }

    public static void Apply(VanguardTacticalAuthoringLiveHeadlessResultDto? dto, string expectedSessionId, string expectedMapId)
    {
        if (dto == null
            || !string.Equals(dto.LiveSessionId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(dto.MapId, expectedMapId, StringComparison.OrdinalIgnoreCase)
            || dto.AuthorRevision != expectedRevision
            || string.IsNullOrWhiteSpace(dto.ResultJson))
        {
            return;
        }

        VanguardTacticalAuthoringHeadlessPreviewResult? parsed;
        try
        {
            parsed = JsonConvert.DeserializeObject<VanguardTacticalAuthoringHeadlessPreviewResult>(dto.ResultJson);
        }
        catch
        {
            return;
        }

        if (parsed == null
            || !string.Equals(parsed.LiveSessionId, expectedSessionId, StringComparison.Ordinal)
            || parsed.AuthorRevision != expectedRevision)
        {
            return;
        }

        latest = parsed;
        BySlot.Clear();
        foreach (var slot in parsed.Slots ?? new List<VanguardTacticalAuthoringHeadlessSlotResult>())
        {
            if (!string.IsNullOrWhiteSpace(slot.SlotId))
            {
                BySlot[slot.SlotId] = slot;
            }
        }
    }

    public static void Clear()
    {
        latest = null;
        BySlot.Clear();
        expectedSessionId = string.Empty;
        expectedMapId = string.Empty;
        expectedRevision = 0;
    }

    public static string OverallSummary
    {
        get
        {
            if (latest == null)
            {
                return "HEADLESS preview: waiting for authority";
            }

            return $"HEADLESS preview: {latest.State} | operators={latest.OperatorCount} | validSlots={latest.HeadlessValidSlotCount}/{latest.CandidateSlotCount} | assigned={latest.AssignedOperatorCount} | reason={latest.Reason}";
        }
    }

    public static string GetSlotSummary(string? slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId) || !BySlot.TryGetValue(slotId, out var slot))
        {
            return string.Empty;
        }

        var who = string.IsNullOrWhiteSpace(slot.AssignedCallsign) ? "-" : slot.AssignedCallsign;
        return $"HEADLESS:{slot.State} | op={who} | move={slot.MovementState} | path={slot.BestPathDistanceMeters:0.0}m";
    }

    public static bool TryGetSlotAuthorityState(string? slotId, out string state)
    {
        state = string.Empty;
        if (string.IsNullOrWhiteSpace(slotId) || !BySlot.TryGetValue(slotId, out var slot))
        {
            return false;
        }

        state = slot.State ?? string.Empty;
        return state.Length > 0;
    }
}
#endif

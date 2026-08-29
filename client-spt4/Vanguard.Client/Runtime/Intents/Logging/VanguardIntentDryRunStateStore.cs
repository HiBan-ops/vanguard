#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Maintains the bounded state used by Intent Dry Run State Store in the intent production pipeline.
// Flow: Writers update normalized entries, readers query a stable view, and lifecycle/reset hooks clear or reconcile data at the appropriate boundary.
// Authority boundary: State cache/registry only; persistent or physical truth remains owned by the designated server/game subsystem unless explicitly documented otherwise.
// Invariant: Entries are scoped to their owner/raid/profile and stale state must be removable without forcing gameplay mutation.
namespace Vanguard.Client.Runtime.Intents;

internal static partial class VanguardOperatorIntentDryRunService
{
private static LastIntentLogState GetOrCreateState(string botProfileId)
    {
        lock (Sync)
        {
            if (!LastByBotProfileId.TryGetValue(botProfileId, out var state))
            {
                state = new LastIntentLogState();
                LastByBotProfileId[botProfileId] = state;
            }

            return state;
        }
    }

private static ThreatScanLogState GetOrCreateThreatScanState(string botProfileId)
    {
        lock (Sync)
        {
            if (!LastThreatScanByBotProfileId.TryGetValue(botProfileId, out var state))
            {
                state = new ThreatScanLogState();
                LastThreatScanByBotProfileId[botProfileId] = state;
            }

            return state;
        }
    }
}
#endif

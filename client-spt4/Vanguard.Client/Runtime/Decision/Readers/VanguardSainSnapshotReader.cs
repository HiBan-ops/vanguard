#if SPT_CLIENT
using System;
using System.Globalization;
using EFT;
using Vanguard.Client.Runtime.Audit;
using Vanguard.Client.Runtime.Combat;

// Responsibility: Reads and normalizes live evidence for Sain Snapshot Reader in the decision snapshot pipeline.
// Flow: Live EFT/Fika/Vanguard objects are inspected defensively, normalized into a bounded snapshot, then handed to policy/decision code.
// Authority boundary: Read-only observer; it does not create missing truth or mutate the game state it inspects.
// Invariant: Missing/stale evidence degrades explicitly and reader failures must not silently fabricate an actionable state.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private static VanguardSainDecisionSnapshot CaptureSain(BotOwner? botOwner)
    {
        if (botOwner == null)
        {
            return new VanguardSainDecisionSnapshot { Classification = "sain_no_botowner" };
        }

        string botProfileId = botOwner.ProfileId ?? string.Empty;
        string nativeGroupId = botOwner.BotsGroup?.Id.ToString() ?? "none";
        int nativeGroupMemberCount = botOwner.BotsGroup?.MembersCount ?? 0;
        bool sainTypeExists = VanguardOperatorRuntimeAuditReflection.TypeExists("SAIN.Components.BotComponent");
        object? sain = VanguardOperatorRuntimeAuditReflection.GetComponentFromBotOrPlayer(botOwner, "SAIN.Components.BotComponent");
        if (sain == null)
        {
            var missing = new VanguardSainDecisionSnapshot
            {
                TypeLoaded = sainTypeExists,
                ComponentPresent = false,
                NativeGroupId = nativeGroupId,
                NativeGroupMemberCount = nativeGroupMemberCount,
                Classification = sainTypeExists ? "sain_component_missing" : "sain_type_missing"
            };
            VanguardSainSquadCombatAuthority.Observe(botProfileId, missing, DateTimeOffset.UtcNow);
            return missing;
        }

        object? decision = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "Decision");
        object? currentAction = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "CurrentAction");
        object? squadContainer = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "Squad");
        object? squadInfo = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(squadContainer, "SquadInfo");
        string sainSquadGuid = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(squadInfo, "GUID"));
        int sainSquadMemberCount = ParseCount(VanguardOperatorRuntimeAuditReflection.CountText(
            VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(squadInfo, "Members")));
        string sainSquadLeaderId = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(squadInfo, "LeaderId"));
        bool sainSquadReady = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(squadInfo, "SquadReady")) == true;
        bool? isInCombat = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "IsInCombat"));
        bool? hasEnemy = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "HasEnemy"));
        bool? searching = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "IsSearching"));
        bool? runningToCover = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "RunningToCover"));
        string combatDecision = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentCombatDecision"));

        var result = new VanguardSainDecisionSnapshot
        {
            TypeLoaded = sainTypeExists,
            ComponentPresent = true,
            ComponentType = VanguardOperatorRuntimeAuditReflection.TypeName(sain),
            Active = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "BotActive")),
            Standby = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "BotInStandBy")),
            LayersActive = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "SAINLayersActive")),
            ActiveLayer = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(sain, "ActiveLayer")),
            IsInCombat = isInCombat,
            HasEnemy = hasEnemy,
            CurrentAction = VanguardOperatorRuntimeAuditReflection.TypeName(currentAction),
            HasDecision = Bool(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "HasDecision")),
            CombatDecision = combatDecision,
            SquadDecision = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentSquadDecision")),
            SelfDecision = Text(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "CurrentSelfDecision")),
            NativeGroupId = nativeGroupId,
            NativeGroupMemberCount = nativeGroupMemberCount,
            SainSquadGuid = sainSquadGuid,
            SainSquadMemberCount = sainSquadMemberCount,
            SainSquadLeaderId = sainSquadLeaderId,
            SainSquadReady = sainSquadReady,
            TimeSinceDecisionChange = Float(VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(decision, "TimeSinceChangeDecision")),
            RunningToCover = runningToCover,
            Searching = searching,
            Classification = ClassifySain(isInCombat, hasEnemy, searching, runningToCover, combatDecision)
        };
        VanguardSainSquadCombatAuthority.Observe(botProfileId, result, DateTimeOffset.UtcNow);
        return result;
    }

    private static int ParseCount(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static string ClassifySain(bool? isInCombat, bool? hasEnemy, bool? searching, bool? runningToCover, string combatDecision)
    {
        if (isInCombat == true && hasEnemy == true)
        {
            return "sain_direct_combat";
        }

        if (runningToCover == true)
        {
            return "sain_cover_move";
        }

        if (searching == true)
        {
            return "sain_search";
        }

        if (hasEnemy == true)
        {
            return "sain_enemy_known";
        }

        if (!string.Equals(combatDecision, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "sain_decision_present";
        }

        return "sain_idle_or_unknown";
    }
}
#endif

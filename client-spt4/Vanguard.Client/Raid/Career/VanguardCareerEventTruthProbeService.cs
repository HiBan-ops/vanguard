using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Options;
using Vanguard.Client.Raid.Runtime;

#if SPT_CLIENT
using Comfort.Common;
using EFT;
using UnityEngine;
#endif

// Responsibility: Collects raw Career evidence at native raid events so delayed deaths, kills and XP credit can be persisted without guessing after the raid.
// Flow: Kill, terminal-death, XP-credit and stop-boundary callbacks are normalized/deduplicated into owner-scoped event lists that end-of-raid persistence can serialize.
// Authority boundary: EFT/Fika callbacks are event truth and the server ledger is durable authority; this probe never changes combat, XP or Career locally.
// Invariant: Each native event is captured at most once per identity/boundary, missing evidence stays missing, and delayed/environmental death context must not be converted into a direct kill.
namespace Vanguard.Client.Raid.Career;

/// <summary>
/// Runtime Career truth collector. It captures evidence at native raid boundaries without mutating durable Career state.
/// Direct kill and raid-boundary truth are captured independently; terminal-death truth adds delayed/environmental death context;
/// EFT-aligned kill-XP credit evidence is captured at Player.OnBeenKilledByAggressor.
/// Every extension is read-only/forward-only and never mutates Operator Career locally
/// or changes combat/AI authority.
/// </summary>
internal static class VanguardCareerEventTruthProbeService
{
    public const string StatusTag = "VANGUARD_RUNTIME_CAREER_EVENT_TRUTH_PROBE_STATUS";
    public const string LedgerStatusTag = "VANGUARD_VERSIONED_CAREER_RAID_LEDGER_AND_ATOMIC_COMMIT_FOUNDATION_STATUS";

    private static readonly object Sync = new();
    private static readonly HashSet<string> SeenVictimProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SeenTerminalDeathVictimProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SeenStopBoundaryKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OperatorEventSummary> SummaryByOperatorId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<VanguardCareerRaidLedgerKillEventDto> LedgerKillEvents = new();
    private static readonly List<VanguardCareerRaidTerminalDeathTruthEventDto> LedgerTerminalDeathTruthEvents = new();
    private static readonly List<VanguardCareerRaidXpKillCreditEventDto> LedgerXpKillCreditEvents = new();
    private static readonly Dictionary<string, int> XpKillSequenceByOperatorProfile = new(StringComparer.OrdinalIgnoreCase);
    private static StopBoundarySnapshot? lastStopBoundary;
    private static long killOrdinal;
    private static long terminalDeathOrdinal;
    private static long stopOrdinal;
    private static int duplicateKillEvents;
#if SPT_CLIENT
    private static bool terminalDeathSubscriptionInstalled;
#endif

    public static void ResetForRaidLifecycle(string reason)
    {
        try
        {
#if SPT_CLIENT
            EnsureTerminalDeathSubscription();
#endif
            lock (Sync)
            {
                SeenVictimProfileIds.Clear();
                SeenTerminalDeathVictimProfileIds.Clear();
                SeenStopBoundaryKeys.Clear();
                SummaryByOperatorId.Clear();
                LedgerKillEvents.Clear();
                LedgerTerminalDeathTruthEvents.Clear();
                LedgerXpKillCreditEvents.Clear();
                XpKillSequenceByOperatorProfile.Clear();
                lastStopBoundary = null;
                killOrdinal = 0;
                terminalDeathOrdinal = 0;
                stopOrdinal = 0;
                duplicateKillEvents = 0;
            }

            VanguardClientDiagnosticsLog.Operational(
                StatusTag,
                () => $"VANGUARD_EVENT_TRUTH_RESET reason={Safe(reason)}; readOnly=true; careerMutation=false; persistenceSemanticsChanged=false");
            VanguardClientDiagnosticsLog.Operational(
                LedgerStatusTag,
                () => $"VANGUARD_LEDGER_CAPTURE_RESET reason={Safe(reason)}; bufferedKills=0; bufferedTerminalDeaths=0; bufferedXpKillCredits=0; stopBoundary=false; localCareerMutation=false; aggregateMutation=false");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_EVENT_TRUTH_RESET_FAILED type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; failOpenRaidLifecycle=true");
        }
    }

#if SPT_CLIENT
    private static void EnsureTerminalDeathSubscription()
    {
        lock (Sync)
        {
            if (terminalDeathSubscriptionInstalled)
            {
                return;
            }

            Player.OnPlayerDeadStatic += ObservePlayerDeadStatic;
            terminalDeathSubscriptionInstalled = true;
        }

        VanguardClientDiagnosticsLog.Operational(
            StatusTag,
            () => $"VANGUARD_TERMINAL_DEATH_SUBSCRIPTION source=Player.OnPlayerDeadStatic; installed=true; terminalDamageType=Player.LastDamageType; lastAggressorSemantics=context_only_not_direct_killer; readOnlyCapture=true; careerMutation=false");
    }

    private static void ObservePlayerDeadStatic(Player victim, IPlayer? lastAggressor, DamageInfoStruct damageInfo, EBodyPart bodyPart)
    {
        try
        {
            if (!ShouldObserve() || victim == null)
            {
                return;
            }

            string victimProfileId = Clean(victim.ProfileId);
            if (string.IsNullOrWhiteSpace(victimProfileId)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(victimProfileId, out VanguardRaidOperatorRuntimeRecord victimOperator))
            {
                return;
            }

            string raidSessionId = Clean(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);
            if (string.IsNullOrWhiteSpace(raidSessionId))
            {
                return;
            }

            DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
            bool duplicate;
            bool directKillObservedAtCapture;
            long ordinal;
            int terminalDamageTypeValue = (int)victim.LastDamageType;
            string terminalDamageType = Clean(victim.LastDamageType.ToString());
            int lastDamageInfoTypeValue = (int)damageInfo.DamageType;
            string lastDamageInfoType = Clean(damageInfo.DamageType.ToString());
            int bodyPartValue = (int)bodyPart;
            string bodyPartRaw = Clean(bodyPart.ToString());

            var captured = new VanguardCareerRaidTerminalDeathTruthEventDto
            {
                EventId = BuildTerminalDeathTruthEventId(raidSessionId, victimProfileId),
                RaidSessionId = raidSessionId,
                ObservedAtUtc = observedAtUtc,
                VictimProfileId = victimProfileId,
                TerminalDamageType = terminalDamageType,
                TerminalDamageTypeValue = terminalDamageTypeValue,
                LastDamageInfoType = lastDamageInfoType,
                LastDamageInfoTypeValue = lastDamageInfoTypeValue,
                LastDamageBodyPart = bodyPartRaw,
                LastDamageBodyPartValue = bodyPartValue,
                LastAggressorProfileId = Clean(lastAggressor?.ProfileId),
                LastAggressorAccountId = Clean(lastAggressor?.AccountId),
                LastAggressorName = Clean(lastAggressor?.Profile?.Info?.Nickname),
                LastAggressorSide = PlayerSideRaw(lastAggressor),
                LastAggressorRawRole = PlayerRoleRaw(lastAggressor),
                LastAggressorInfoLevel = PlayerInfoLevel(lastAggressor),
                LastAggressorInfoExperience = PlayerInfoExperience(lastAggressor),
                LastAggressorSettingsExperience = PlayerSettingsExperience(lastAggressor),
                Source = "Player.OnPlayerDeadStatic"
            };

            lock (Sync)
            {
                ordinal = ++terminalDeathOrdinal;
                duplicate = !SeenTerminalDeathVictimProfileIds.Add(victimProfileId);
                directKillObservedAtCapture = SeenVictimProfileIds.Contains(victimProfileId);
                captured.DirectKillEventObservedAtCapture = directKillObservedAtCapture;
                if (!duplicate)
                {
                    LedgerTerminalDeathTruthEvents.Add(captured);
                    OperatorEventSummary summary = GetOrCreateSummaryLocked(victimOperator.OperatorId);
                    summary.TerminalDeathsObserved++;
                    summary.LastTerminalDamageType = terminalDamageType;
                    summary.LastDamageBodyPart = bodyPartRaw;
                    summary.LastAggressorContextProfileId = Clean(lastAggressor?.ProfileId);
                    summary.LastAggressorContextName = Clean(lastAggressor?.Profile?.Info?.Nickname);
                    summary.LastAggressorContextRole = PlayerRoleRaw(lastAggressor);
                }
            }

            if (duplicate)
            {
                VanguardClientDiagnosticsLog.Operational(
                    StatusTag,
                    () => $"VANGUARD_TERMINAL_DEATH_DUPLICATE ordinal={ordinal}; raid={Safe(raidSessionId)}; operator={Safe(victimOperator.OperatorId)}; victimProfile={Safe(victimProfileId)}; ignoredForLedger=true; source=Player.OnPlayerDeadStatic; careerMutation=false");
                return;
            }

            VanguardClientDiagnosticsLog.Operational(
                StatusTag,
                () => $"VANGUARD_TERMINAL_DEATH ordinal={ordinal}; observedAtUtc={observedAtUtc:O}; raid={Safe(raidSessionId)}; operator={Safe(victimOperator.OperatorId)}; victimProfile={Safe(victimProfileId)}; terminalDamageType={Safe(terminalDamageType)}; terminalDamageTypeValue={terminalDamageTypeValue}; lastDamageInfoType={Safe(lastDamageInfoType)}; lastDamageInfoTypeValue={lastDamageInfoTypeValue}; bodyPart={Safe(bodyPartRaw)}; bodyPartValue={bodyPartValue}; directKillEventObservedAtCapture={Bool(directKillObservedAtCapture)}; lastAggressorProfile={Safe(lastAggressor?.ProfileId)}; lastAggressorName={PlayerName(lastAggressor)}; lastAggressorSide={PlayerSide(lastAggressor)}; lastAggressorRole={PlayerRole(lastAggressor)}; lastAggressorSemantics=context_only_not_direct_killer; directKillerAuthority=BotEventHandler.Kill_only; source=Player.OnPlayerDeadStatic; careerMutation=false; persistenceSemanticsChanged=false");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_TERMINAL_DEATH_CAPTURE_FAILED type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; persistenceFailOpen=true; careerMutation=false");
        }
    }

    public static void ObserveXpKillCredit(Player victim, IPlayer? aggressor, DamageInfoStruct damageInfo, EBodyPart bodyPart, EDamageType lethalDamageType)
    {
        try
        {
            if (!ShouldObserve() || aggressor == null)
            {
                return;
            }

            string xpRecipientProfileId = Clean(aggressor.ProfileId);
            string targetProfileId = Clean(victim.ProfileId);
            if (string.IsNullOrWhiteSpace(xpRecipientProfileId)
                || string.IsNullOrWhiteSpace(targetProfileId)
                || string.Equals(xpRecipientProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase)
                || !VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(xpRecipientProfileId, out VanguardRaidOperatorRuntimeRecord xpRecipientOperator))
            {
                return;
            }

            var victimProfile = victim.Profile;
            var victimInfo = victimProfile?.Info;
            var victimSettings = victimInfo?.Settings;
            if (victimProfile is null || victimInfo is null || victimSettings is null)
            {
                return;
            }

            string raidSessionId = Clean(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);
            if (string.IsNullOrWhiteSpace(raidSessionId))
            {
                return;
            }

            string eventId = BuildXpKillCreditEventId(raidSessionId, xpRecipientProfileId, targetProfileId);
            lock (Sync)
            {
                if (LedgerXpKillCreditEvents.Any(value => string.Equals(value.EventId, eventId, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }

            int killSequence;
            lock (Sync)
            {
                XpKillSequenceByOperatorProfile.TryGetValue(xpRecipientProfileId, out int previousSequence);
                killSequence = previousSequence + 1;
                XpKillSequenceByOperatorProfile[xpRecipientProfileId] = killSequence;
            }

            DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
            string targetRawRole = Clean(victimSettings.Role.ToString());
            EPlayerSide effectiveSide = victimInfo.Side;
            if (victimSettings.Role == WildSpawnType.pmcUSEC)
            {
                effectiveSide = EPlayerSide.Usec;
            }
            else if (victimSettings.Role == WildSpawnType.pmcBEAR)
            {
                effectiveSide = EPlayerSide.Bear;
            }

            string targetGroupId = Clean(victimInfo.GroupId);
            string xpRecipientGroupId = Clean(aggressor.Profile?.Info?.GroupId);
            bool sameGroup = !string.IsNullOrWhiteSpace(targetGroupId)
                && !string.IsNullOrWhiteSpace(xpRecipientGroupId)
                && string.Equals(targetGroupId, xpRecipientGroupId, StringComparison.Ordinal);

            bool xpRecipientHasMarkOfUnknown = false;
            float markPenalty = 1f;
            Player? xpRecipientPlayer = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(xpRecipientProfileId);
            if (xpRecipientPlayer != null && xpRecipientPlayer.HasMarkOfUnknown(out MarkOfUnknownItemClass markOfUnknown))
            {
                xpRecipientHasMarkOfUnknown = true;
                markPenalty = markOfUnknown.ScavKillExpPenalty;
            }

            bool calculationAvailable = false;
            bool awarded = false;
            string calculationReason = "eft_kill_config_unavailable";
            int baseXp = 0;
            int bodyPartBonusXp = 0;
            int streakBonusXp = 0;
            int subtotal = 0;

            BackendConfigSettingsClass? backend = Singleton<BackendConfigSettingsClass>.Instance;
            if (backend?.Experience?.Kill != null)
            {
                calculationAvailable = true;
                if (sameGroup)
                {
                    calculationReason = "same_group_no_xp_matches_eft_early_return";
                }
                else
                {
                    var killConfig = backend.Experience.Kill;
                    switch (effectiveSide)
                    {
                        case EPlayerSide.Usec:
                        case EPlayerSide.Bear:
                            baseXp = Math.Max(0, killConfig.VictimLevelExp);
                            break;
                        case EPlayerSide.Savage:
                            baseXp = victimSettings.Experience;
                            if (baseXp < 0)
                            {
                                baseXp = Math.Max(0, killConfig.VictimBotLevelExp);
                            }

                            bool casualScavForMarkPenalty = victimSettings.Role == WildSpawnType.assault
                                || victimSettings.Role == WildSpawnType.marksman
                                || !victim.IsAI;
                            if (casualScavForMarkPenalty && xpRecipientHasMarkOfUnknown)
                            {
                                baseXp = Mathf.CeilToInt(baseXp * markPenalty);
                            }
                            break;
                        default:
                            calculationAvailable = false;
                            calculationReason = "unsupported_target_side_" + effectiveSide;
                            break;
                    }

                    if (calculationAvailable)
                    {
                        float headshotMultiplier = bodyPart == EBodyPart.Head
                            ? effectiveSide == EPlayerSide.Savage
                                ? killConfig.BotHeadShotMult
                                : killConfig.PmcHeadShotMult
                            : 0f;
                        int streakPercent = killConfig.GetKillingBonusPercent(killSequence);
                        bodyPartBonusXp = Math.Max(0, (int)(baseXp * headshotMultiplier));
                        streakBonusXp = Math.Max(0, (int)(baseXp * (streakPercent / 100f)));
                        subtotal = Math.Max(0, baseXp + bodyPartBonusXp + streakBonusXp);
                        awarded = baseXp > 0 || bodyPartBonusXp > 0 || streakBonusXp > 0;
                        calculationReason = "exact_eft_kill_component_pre_session_multiplier";
                    }
                }
            }

            var captured = new VanguardCareerRaidXpKillCreditEventDto
            {
                EventId = eventId,
                RaidSessionId = raidSessionId,
                ObservedAtUtc = observedAtUtc,
                XpRecipientProfileId = xpRecipientProfileId,
                TargetProfileId = targetProfileId,
                KillSequence = killSequence,
                TargetSide = effectiveSide.ToString(),
                TargetRawRole = targetRawRole,
                TargetLevel = victimInfo.Level,
                KillExpInput = victimSettings.Experience,
                BodyPart = bodyPart.ToString(),
                BodyPartValue = (int)bodyPart,
                SameGroup = sameGroup,
                TargetIsAi = victim.IsAI,
                XpRecipientHasMarkOfUnknown = xpRecipientHasMarkOfUnknown,
                MarkOfUnknownScavKillExpPenalty = markPenalty,
                CalculationAvailable = calculationAvailable,
                Awarded = awarded,
                CalculationReason = calculationReason,
                BaseXp = baseXp,
                BodyPartBonusXp = bodyPartBonusXp,
                StreakBonusXp = streakBonusXp,
                KillXpSubtotal = subtotal,
                Source = "Player.OnBeenKilledByAggressor+BackendConfigSettingsClass.Experience.Kill"
            };

            bool duplicate;
            lock (Sync)
            {
                duplicate = LedgerXpKillCreditEvents.Any(value => string.Equals(value.EventId, captured.EventId, StringComparison.OrdinalIgnoreCase));
                if (!duplicate)
                {
                    LedgerXpKillCreditEvents.Add(captured);
                }
            }

            VanguardClientDiagnosticsLog.Operational(
                StatusTag,
                () => $"VANGUARD_EFT_XP_SHADOW_KILL raid={Safe(raidSessionId)}; operator={Safe(xpRecipientOperator.OperatorId)}; xpRecipientProfile={Safe(xpRecipientProfileId)}; targetProfile={Safe(targetProfileId)}; sequence={killSequence}; targetSide={effectiveSide}; targetRole={Safe(targetRawRole)}; targetLevel={victimInfo.Level}; killExpInput={victimSettings.Experience}; bodyPart={bodyPart}; lethalDamageType={lethalDamageType}; sameGroup={Bool(sameGroup)}; targetIsAi={Bool(victim.IsAI)}; markOfUnknown={Bool(xpRecipientHasMarkOfUnknown)}; calculationAvailable={Bool(calculationAvailable)}; awarded={Bool(awarded)}; baseXp={baseXp}; bodyPartBonusXp={bodyPartBonusXp}; streakBonusXp={streakBonusXp}; killXpSubtotal={subtotal}; duplicate={Bool(duplicate)}; source=Player.OnBeenKilledByAggressor_plus_EFT_Kill_Config; sessionMultiplierApplied=false; exitRewardApplied=false; nonKillXpApplied=false; careerXpMutation=false");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_EFT_XP_SHADOW_KILL_FAILED type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; failOpenCombat=true; careerXpMutation=false");
        }
    }

    public static void ObserveKill(IPlayer? killer, IPlayer? target)
    {
        try
        {
            if (!ShouldObserve())
            {
                return;
            }

            string killerProfileId = Clean(killer?.ProfileId);
            string targetProfileId = Clean(target?.ProfileId);
            bool killerIsOperator = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(killerProfileId, out VanguardRaidOperatorRuntimeRecord killerOperator);
            bool targetIsOperator = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(targetProfileId, out VanguardRaidOperatorRuntimeRecord targetOperator);
            if (!killerIsOperator && !targetIsOperator)
            {
                return;
            }

            string raidSessionId = Clean(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);
            DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
            bool duplicate = false;
            bool ledgerEligible = !string.IsNullOrWhiteSpace(raidSessionId) && !string.IsNullOrWhiteSpace(targetProfileId);
            long ordinal;
            int duplicateCount;
            lock (Sync)
            {
                ordinal = ++killOrdinal;
                if (!string.IsNullOrWhiteSpace(targetProfileId))
                {
                    duplicate = !SeenVictimProfileIds.Add(targetProfileId);
                }

                if (duplicate)
                {
                    duplicateKillEvents++;
                }

                duplicateCount = duplicateKillEvents;
                if (!duplicate)
                {
                    if (killerIsOperator)
                    {
                        OperatorEventSummary summary = GetOrCreateSummaryLocked(killerOperator.OperatorId);
                        summary.KillsObserved++;
                        summary.LastTargetProfileId = targetProfileId;
                        summary.LastTargetName = PlayerName(target);
                        summary.LastTargetRole = PlayerRole(target);
                    }

                    if (targetIsOperator)
                    {
                        OperatorEventSummary summary = GetOrCreateSummaryLocked(targetOperator.OperatorId);
                        summary.DeathEventsObserved++;
                        summary.LastKillerProfileId = killerProfileId;
                        summary.LastKillerName = PlayerName(killer);
                        summary.LastKillerRole = PlayerRole(killer);
                    }

                    if (ledgerEligible)
                    {
                        LedgerKillEvents.Add(new VanguardCareerRaidLedgerKillEventDto
                        {
                            EventId = BuildKillEventId(raidSessionId, targetProfileId),
                            RaidSessionId = raidSessionId,
                            Ordinal = ordinal,
                            ObservedAtUtc = observedAtUtc,
                            KillerProfileId = killerProfileId,
                            KillerAccountId = Clean(killer?.AccountId),
                            KillerName = Clean(killer?.Profile?.Info?.Nickname),
                            KillerSide = PlayerSideRaw(killer),
                            KillerRawRole = PlayerRoleRaw(killer),
                            KillerInfoLevel = PlayerInfoLevel(killer),
                            KillerInfoExperience = PlayerInfoExperience(killer),
                            KillerSettingsExperience = PlayerSettingsExperience(killer),
                            TargetProfileId = targetProfileId,
                            TargetAccountId = Clean(target?.AccountId),
                            TargetName = Clean(target?.Profile?.Info?.Nickname),
                            TargetSide = PlayerSideRaw(target),
                            TargetRawRole = PlayerRoleRaw(target),
                            TargetInfoLevel = PlayerInfoLevel(target),
                            TargetInfoExperience = PlayerInfoExperience(target),
                            TargetSettingsExperience = PlayerSettingsExperience(target)
                        });
                    }
                }
            }

            if (duplicate)
            {
                VanguardClientDiagnosticsLog.Operational(
                    StatusTag,
                    () => $"VANGUARD_KILL_EVENT_DUPLICATE ordinal={ordinal}; raid={Safe(raidSessionId)}; killerProfile={Safe(killerProfileId)}; targetProfile={Safe(targetProfileId)}; killerOperator={Bool(killerIsOperator)}; targetOperator={Bool(targetIsOperator)}; duplicateCount={duplicateCount}; ignoredForSummary=true; ignoredForLedger=true; source=BotEventHandler.Kill; careerMutation=false");
                return;
            }

            VanguardClientDiagnosticsLog.Operational(
                StatusTag,
                () => $"VANGUARD_KILL_EVENT ordinal={ordinal}; observedAtUtc={observedAtUtc:O}; raid={Safe(raidSessionId)}; source=BotEventHandler.Kill_to_OnKill; killerOperator={Bool(killerIsOperator)}; killerOperatorId={Safe(killerIsOperator ? killerOperator.OperatorId : null)}; killerProfile={Safe(killerProfileId)}; killerAccount={Safe(killer?.AccountId)}; killerName={PlayerName(killer)}; killerSide={PlayerSide(killer)}; killerRole={PlayerRole(killer)}; killerInfoLevel={PlayerInfoLevel(killer)}; killerInfoExperience={PlayerInfoExperience(killer)}; killerSettingsExperience={PlayerSettingsExperience(killer)}; targetOperator={Bool(targetIsOperator)}; targetOperatorId={Safe(targetIsOperator ? targetOperator.OperatorId : null)}; targetProfile={Safe(targetProfileId)}; targetAccount={Safe(target?.AccountId)}; targetName={PlayerName(target)}; targetSide={PlayerSide(target)}; targetRole={PlayerRole(target)}; targetInfoLevel={PlayerInfoLevel(target)}; targetInfoExperience={PlayerInfoExperience(target)}; targetSettingsExperience={PlayerSettingsExperience(target)}; friendlyOperatorKill={Bool(killerIsOperator && targetIsOperator)}; rawRoleIsAuthoritative=true; bossClassificationCommitted=false; ledgerEligible={Bool(ledgerEligible)}; careerMutation=false; persistenceSemanticsChanged=false");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_KILL_EVENT_PROBE_FAILED type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; failOpenBotEvent=true; careerMutation=false");
        }
    }

    public static void ObserveRaidStop(string source, string? profileId, ExitStatus exitStatus, string? exitName, float delay)
    {
        try
        {
            if (!ShouldObserve())
            {
                return;
            }

            string raidSessionId = Clean(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);
            DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
            string stopKey = string.Join("|", Safe(source), Safe(profileId), exitStatus.ToString(), Safe(exitName), delay.ToString("0.###", CultureInfo.InvariantCulture));
            long ordinal;
            bool duplicateBoundary;
            int duplicateKills;
            Dictionary<string, OperatorEventSummarySnapshot> summaries;
            lock (Sync)
            {
                ordinal = ++stopOrdinal;
                duplicateBoundary = !SeenStopBoundaryKeys.Add(stopKey);
                duplicateKills = duplicateKillEvents;
                summaries = SnapshotSummariesLocked();
                if (!duplicateBoundary)
                {
                    lastStopBoundary = new StopBoundarySnapshot(
                        raidSessionId,
                        Clean(source),
                        Clean(profileId),
                        exitStatus.ToString(),
                        Clean(exitName),
                        delay,
                        observedAtUtc);
                }
            }

            VanguardClientDiagnosticsLog.Operational(
                StatusTag,
                () => $"VANGUARD_EXIT_BOUNDARY ordinal={ordinal}; raid={Safe(raidSessionId)}; source={Safe(source)}; profileId={Safe(profileId)}; exitStatus={exitStatus}; exitName={Safe(exitName)}; delay={delay.ToString("0.###", CultureInfo.InvariantCulture)}; duplicateBoundary={Bool(duplicateBoundary)}; semantics=raw_Stop_arguments_not_per_operator_outcome; careerMutation=false; persistenceSemanticsChanged=false");

            IReadOnlyList<VanguardRaidOperatorRuntimeRecord> operators = VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators();
            foreach (VanguardRaidOperatorRuntimeRecord record in operators)
            {
                summaries.TryGetValue(record.OperatorId, out OperatorEventSummarySnapshot summary);
                VanguardClientDiagnosticsLog.Operational(
                    StatusTag,
                    () => $"VANGUARD_OPERATOR_EVENT_SUMMARY operator={Safe(record.OperatorId)}; owner={Safe(record.OwnerProfileId)}; botProfile={Safe(record.BotProfileId)}; raid={Safe(raidSessionId)}; killsObserved={summary.KillsObserved}; deathEventsObserved={summary.DeathEventsObserved}; terminalDeathsObserved={summary.TerminalDeathsObserved}; lastTargetProfile={Safe(summary.LastTargetProfileId)}; lastTargetName={Safe(summary.LastTargetName)}; lastTargetRole={Safe(summary.LastTargetRole)}; lastKillerProfile={Safe(summary.LastKillerProfileId)}; lastKillerName={Safe(summary.LastKillerName)}; lastKillerRole={Safe(summary.LastKillerRole)}; lastTerminalDamageType={Safe(summary.LastTerminalDamageType)}; lastDamageBodyPart={Safe(summary.LastDamageBodyPart)}; lastAggressorContextProfile={Safe(summary.LastAggressorContextProfileId)}; lastAggressorContextName={Safe(summary.LastAggressorContextName)}; lastAggressorContextRole={Safe(summary.LastAggressorContextRole)}; lastAggressorSemantics=context_only_not_direct_killer; duplicateKillEventsGlobal={duplicateKills}; stopExitStatus={exitStatus}; stopExitName={Safe(exitName)}; stopOutcomeApplicability=raid_boundary_observed_not_per_operator_inferred; readOnly=true; careerMutation=false");
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_EXIT_BOUNDARY_PROBE_FAILED source={Safe(source)}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; failOpenRaidStop=true; careerMutation=false");
        }
    }

    public static bool TryBuildLedgerCommitRequest(
        string expectedRaidSessionId,
        out VanguardCareerRaidLedgerCommitRequestDto? payload,
        out string reason)
    {
        payload = null;
        reason = "unknown";
        try
        {
            string expectedRaid = Clean(expectedRaidSessionId);
            VanguardCareerRaidLedgerCommitRequestDto readyPayload;
            lock (Sync)
            {
                if (lastStopBoundary is null)
                {
                    reason = "stop_boundary_not_observed";
                    return false;
                }

                StopBoundarySnapshot stop = lastStopBoundary.Value;
                if (string.IsNullOrWhiteSpace(expectedRaid)
                    || !string.Equals(stop.RaidSessionId, expectedRaid, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "raid_session_mismatch";
                    return false;
                }

                readyPayload = new VanguardCareerRaidLedgerCommitRequestDto
                {
                    RaidSessionId = stop.RaidSessionId,
                    StopSource = stop.Source,
                    StopProfileId = stop.ProfileId,
                    ExitStatus = stop.ExitStatus,
                    ExitName = stop.ExitName,
                    StopDelay = stop.Delay,
                    StopObservedAtUtc = stop.ObservedAtUtc,
                    KillEvents = LedgerKillEvents.Select(CloneKillEvent).ToList(),
                    TerminalDeathTruthEvents = LedgerTerminalDeathTruthEvents.Select(CloneTerminalDeathTruthEvent).ToList(),
                    XpKillCreditEvents = LedgerXpKillCreditEvents.Select(CloneXpKillCreditEvent).ToList(),
                    SchemaVersion = 1
                };
                payload = readyPayload;
            }

            reason = "ok";
            VanguardClientDiagnosticsLog.Operational(
                LedgerStatusTag,
                () => $"VANGUARD_LEDGER_PAYLOAD_READY raid={Safe(expectedRaidSessionId)}; exitStatus={Safe(readyPayload.ExitStatus)}; stopSource={Safe(readyPayload.StopSource)}; killEvents={readyPayload.KillEvents?.Count ?? 0}; terminalDeathTruthEvents={readyPayload.TerminalDeathTruthEvents?.Count ?? 0}; xpKillCreditEvents={readyPayload.XpKillCreditEvents?.Count ?? 0}; xpKillShadowSubtotal={readyPayload.XpKillCreditEvents?.Sum(value => value.KillXpSubtotal) ?? 0}; xpRecipientSemantics=OnBeenKilledByAggressor_aggressor_credit_recipient_not_direct_killer; terminalDeathLastAggressorSemantics=context_only_not_direct_killer; schema={readyPayload.SchemaVersion}; aggregateMutation=false; xpMutation=false; localCareerMutation=false");
            return true;
        }
        catch (Exception exception)
        {
            reason = "ledger_payload_exception_" + exception.GetType().Name;
            payload = null;
            VanguardClientDiagnosticsLog.Warning(
                LedgerStatusTag,
                $"VANGUARD_LEDGER_PAYLOAD_FAILED raid={Safe(expectedRaidSessionId)}; type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; persistenceFailOpen=true; localCareerMutation=false");
            return false;
        }
    }

    private static bool ShouldObserve()
    {
        return VanguardFikaCompat.IsRaidAuthority
            && VanguardOperatorRuntimeAuditOptions.GetOperatorPostRaidPersistenceEnabled()
            && !string.IsNullOrWhiteSpace(VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId);
    }

    private static string PlayerName(IPlayer? player) => Safe(player?.Profile?.Info?.Nickname);
    private static string PlayerSide(IPlayer? player) => Safe(PlayerSideRaw(player));
    private static string PlayerRole(IPlayer? player) => Safe(PlayerRoleRaw(player));
    private static string PlayerSideRaw(IPlayer? player) => player == null ? string.Empty : Clean(player.Side.ToString());
    private static string PlayerRoleRaw(IPlayer? player) => Clean(player?.Profile?.Info?.Settings?.Role.ToString());
    private static int PlayerInfoLevel(IPlayer? player) => player?.Profile?.Info?.Level ?? -1;
    private static int PlayerInfoExperience(IPlayer? player) => player?.Profile?.Info?.Experience ?? -1;
    private static int PlayerSettingsExperience(IPlayer? player) => player?.Profile?.Info?.Settings?.Experience ?? -1;
#endif

    private static VanguardCareerRaidLedgerKillEventDto CloneKillEvent(VanguardCareerRaidLedgerKillEventDto source) => new()
    {
        EventId = source.EventId,
        RaidSessionId = source.RaidSessionId,
        Ordinal = source.Ordinal,
        ObservedAtUtc = source.ObservedAtUtc,
        KillerProfileId = source.KillerProfileId,
        KillerAccountId = source.KillerAccountId,
        KillerName = source.KillerName,
        KillerSide = source.KillerSide,
        KillerRawRole = source.KillerRawRole,
        KillerInfoLevel = source.KillerInfoLevel,
        KillerInfoExperience = source.KillerInfoExperience,
        KillerSettingsExperience = source.KillerSettingsExperience,
        TargetProfileId = source.TargetProfileId,
        TargetAccountId = source.TargetAccountId,
        TargetName = source.TargetName,
        TargetSide = source.TargetSide,
        TargetRawRole = source.TargetRawRole,
        TargetInfoLevel = source.TargetInfoLevel,
        TargetInfoExperience = source.TargetInfoExperience,
        TargetSettingsExperience = source.TargetSettingsExperience
    };

    private static VanguardCareerRaidTerminalDeathTruthEventDto CloneTerminalDeathTruthEvent(VanguardCareerRaidTerminalDeathTruthEventDto source) => new()
    {
        EventId = source.EventId,
        RaidSessionId = source.RaidSessionId,
        ObservedAtUtc = source.ObservedAtUtc,
        VictimProfileId = source.VictimProfileId,
        TerminalDamageType = source.TerminalDamageType,
        TerminalDamageTypeValue = source.TerminalDamageTypeValue,
        LastDamageInfoType = source.LastDamageInfoType,
        LastDamageInfoTypeValue = source.LastDamageInfoTypeValue,
        LastDamageBodyPart = source.LastDamageBodyPart,
        LastDamageBodyPartValue = source.LastDamageBodyPartValue,
        DirectKillEventObservedAtCapture = source.DirectKillEventObservedAtCapture,
        LastAggressorProfileId = source.LastAggressorProfileId,
        LastAggressorAccountId = source.LastAggressorAccountId,
        LastAggressorName = source.LastAggressorName,
        LastAggressorSide = source.LastAggressorSide,
        LastAggressorRawRole = source.LastAggressorRawRole,
        LastAggressorInfoLevel = source.LastAggressorInfoLevel,
        LastAggressorInfoExperience = source.LastAggressorInfoExperience,
        LastAggressorSettingsExperience = source.LastAggressorSettingsExperience,
        Source = source.Source
    };

    private static VanguardCareerRaidXpKillCreditEventDto CloneXpKillCreditEvent(VanguardCareerRaidXpKillCreditEventDto source) => new()
    {
        EventId = source.EventId,
        RaidSessionId = source.RaidSessionId,
        ObservedAtUtc = source.ObservedAtUtc,
        XpRecipientProfileId = source.XpRecipientProfileId,
        TargetProfileId = source.TargetProfileId,
        KillSequence = source.KillSequence,
        TargetSide = source.TargetSide,
        TargetRawRole = source.TargetRawRole,
        TargetLevel = source.TargetLevel,
        KillExpInput = source.KillExpInput,
        BodyPart = source.BodyPart,
        BodyPartValue = source.BodyPartValue,
        SameGroup = source.SameGroup,
        TargetIsAi = source.TargetIsAi,
        XpRecipientHasMarkOfUnknown = source.XpRecipientHasMarkOfUnknown,
        MarkOfUnknownScavKillExpPenalty = source.MarkOfUnknownScavKillExpPenalty,
        CalculationAvailable = source.CalculationAvailable,
        Awarded = source.Awarded,
        CalculationReason = source.CalculationReason,
        BaseXp = source.BaseXp,
        BodyPartBonusXp = source.BodyPartBonusXp,
        StreakBonusXp = source.StreakBonusXp,
        KillXpSubtotal = source.KillXpSubtotal,
        Source = source.Source
    };

    private static string BuildXpKillCreditEventId(string raidSessionId, string xpRecipientProfileId, string targetProfileId)
        => $"career_xp_v1|{Clean(raidSessionId)}|xp_kill_credit|{Clean(xpRecipientProfileId)}|{Clean(targetProfileId)}";

    private static string BuildTerminalDeathTruthEventId(string raidSessionId, string victimProfileId)
        => $"terminal_death_v1|{Clean(raidSessionId)}|terminal_death|{Clean(victimProfileId)}";

    private static string BuildKillEventId(string raidSessionId, string targetProfileId)
        => $"career_kill_v1|{Clean(raidSessionId)}|kill|{Clean(targetProfileId)}";

    private static OperatorEventSummary GetOrCreateSummaryLocked(string operatorId)
    {
        string key = Safe(operatorId);
        if (!SummaryByOperatorId.TryGetValue(key, out OperatorEventSummary? summary))
        {
            summary = new OperatorEventSummary();
            SummaryByOperatorId[key] = summary;
        }

        return summary;
    }

    private static Dictionary<string, OperatorEventSummarySnapshot> SnapshotSummariesLocked()
    {
        var result = new Dictionary<string, OperatorEventSummarySnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, OperatorEventSummary> pair in SummaryByOperatorId)
        {
            OperatorEventSummary value = pair.Value;
            result[pair.Key] = new OperatorEventSummarySnapshot(
                value.KillsObserved,
                value.DeathEventsObserved,
                value.TerminalDeathsObserved,
                value.LastTargetProfileId,
                value.LastTargetName,
                value.LastTargetRole,
                value.LastKillerProfileId,
                value.LastKillerName,
                value.LastKillerRole,
                value.LastTerminalDamageType,
                value.LastDamageBodyPart,
                value.LastAggressorContextProfileId,
                value.LastAggressorContextName,
                value.LastAggressorContextRole);
        }

        return result;
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ');
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : Clean(value);
    private static string Bool(bool value) => value ? "true" : "false";

    private readonly struct StopBoundarySnapshot
    {
        public StopBoundarySnapshot(string raidSessionId, string source, string profileId, string exitStatus, string exitName, float delay, DateTimeOffset observedAtUtc)
        {
            RaidSessionId = raidSessionId;
            Source = source;
            ProfileId = profileId;
            ExitStatus = exitStatus;
            ExitName = exitName;
            Delay = delay;
            ObservedAtUtc = observedAtUtc;
        }

        public string RaidSessionId { get; }
        public string Source { get; }
        public string ProfileId { get; }
        public string ExitStatus { get; }
        public string ExitName { get; }
        public float Delay { get; }
        public DateTimeOffset ObservedAtUtc { get; }
    }

    private sealed class OperatorEventSummary
    {
        public int KillsObserved { get; set; }
        public int DeathEventsObserved { get; set; }
        public int TerminalDeathsObserved { get; set; }
        public string LastTargetProfileId { get; set; } = string.Empty;
        public string LastTargetName { get; set; } = string.Empty;
        public string LastTargetRole { get; set; } = string.Empty;
        public string LastKillerProfileId { get; set; } = string.Empty;
        public string LastKillerName { get; set; } = string.Empty;
        public string LastKillerRole { get; set; } = string.Empty;
        public string LastTerminalDamageType { get; set; } = string.Empty;
        public string LastDamageBodyPart { get; set; } = string.Empty;
        public string LastAggressorContextProfileId { get; set; } = string.Empty;
        public string LastAggressorContextName { get; set; } = string.Empty;
        public string LastAggressorContextRole { get; set; } = string.Empty;
    }

    private readonly struct OperatorEventSummarySnapshot
    {
        public OperatorEventSummarySnapshot(
            int killsObserved,
            int deathEventsObserved,
            int terminalDeathsObserved,
            string lastTargetProfileId,
            string lastTargetName,
            string lastTargetRole,
            string lastKillerProfileId,
            string lastKillerName,
            string lastKillerRole,
            string lastTerminalDamageType,
            string lastDamageBodyPart,
            string lastAggressorContextProfileId,
            string lastAggressorContextName,
            string lastAggressorContextRole)
        {
            KillsObserved = killsObserved;
            DeathEventsObserved = deathEventsObserved;
            TerminalDeathsObserved = terminalDeathsObserved;
            LastTargetProfileId = lastTargetProfileId;
            LastTargetName = lastTargetName;
            LastTargetRole = lastTargetRole;
            LastKillerProfileId = lastKillerProfileId;
            LastKillerName = lastKillerName;
            LastKillerRole = lastKillerRole;
            LastTerminalDamageType = lastTerminalDamageType;
            LastDamageBodyPart = lastDamageBodyPart;
            LastAggressorContextProfileId = lastAggressorContextProfileId;
            LastAggressorContextName = lastAggressorContextName;
            LastAggressorContextRole = lastAggressorContextRole;
        }

        public int KillsObserved { get; }
        public int DeathEventsObserved { get; }
        public int TerminalDeathsObserved { get; }
        public string LastTargetProfileId { get; }
        public string LastTargetName { get; }
        public string LastTargetRole { get; }
        public string LastKillerProfileId { get; }
        public string LastKillerName { get; }
        public string LastKillerRole { get; }
        public string LastTerminalDamageType { get; }
        public string LastDamageBodyPart { get; }
        public string LastAggressorContextProfileId { get; }
        public string LastAggressorContextName { get; }
        public string LastAggressorContextRole { get; }
    }
}

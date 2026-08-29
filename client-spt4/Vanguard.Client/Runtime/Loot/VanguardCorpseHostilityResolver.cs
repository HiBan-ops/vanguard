#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Awareness;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Provides Corpse Hostility Resolver support for the loot runtime.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Loot;

internal static class VanguardCorpseHostilityResolver
{
    public static IReadOnlyDictionary<string, VanguardCorpseHostilityEvidence> CaptureAtRegistration(Player victim)
    {
        var result = new Dictionary<string, VanguardCorpseHostilityEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (VanguardRaidOperatorRuntimeRecord record in VanguardRaidOperatorRuntimeRegistry.GetAllRuntimeOperators())
        {
            if (record.BotOwner == null || record.BotOwner.IsDead || string.IsNullOrWhiteSpace(record.OwnerProfileId))
            {
                continue;
            }

            if (TryProbeBotsGroup(record.BotOwner, victim))
            {
                result[record.OwnerProfileId] = new VanguardCorpseHostilityEvidence
                {
                    Verified = true,
                    HostileConfirmed = true,
                    RelationshipKind = "hostile_ai",
                    Source = "registration_bots_group",
                    Reason = "bots_group_enemy_at_create_corpse",
                    AgeSeconds = 0f
                };
            }
        }

        return result;
    }

    public static VanguardCorpseHostilityEvidence Resolve(
        VanguardRaidOperatorRuntimeRecord record,
        VanguardCorpseRegistryEntry entry,
        VanguardThreatDecisionSnapshot threat,
        DateTimeOffset now)
    {
        string victimProfileId = Normalize(entry.VictimProfileId);
        bool isOperatorCorpse = entry.VictimWasOperator
            || VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(victimProfileId, out _);
        if (isOperatorCorpse)
        {
            bool sameOwnerOperator = VanguardRaidOperatorRuntimeRegistry.TryGetRuntimeByBotProfileId(victimProfileId, out VanguardRaidOperatorRuntimeRecord victimRecord)
                && string.Equals(record.OwnerProfileId, victimRecord.OwnerProfileId, StringComparison.OrdinalIgnoreCase);
            bool protectedByCoopAlliance = VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(record.BotProfileId, victimProfileId);
            bool friendlyOperatorCorpse = sameOwnerOperator || protectedByCoopAlliance;
            return new VanguardCorpseHostilityEvidence
            {
                // Preserve relationship authority: this resolver only classifies friendly/non-friendly and never mutates affiliation state.
                // The persistence path keeps transaction admission in the dedicated persistence arm/gate, so identity policy and
                // post-raid durability remain separate authorities.
                Verified = true,
                DeadOperatorCorpse = true,
                FriendlyOperatorCorpse = friendlyOperatorCorpse,
                NonFriendlyOperatorCorpse = !friendlyOperatorCorpse,
                RelationshipKind = friendlyOperatorCorpse ? "friendly_operator_corpse" : "nonfriendly_operator_corpse",
                Source = "operator_identity_policy",
                Reason = sameOwnerOperator
                    ? "same_owner_operator_corpse"
                    : protectedByCoopAlliance
                        ? "coop_alliance_operator_corpse"
                        : "operator_corpse_not_protected_by_alliance",
                AgeSeconds = (float)Math.Max(0d, (now - entry.RegisteredAtUtc).TotalSeconds)
            };
        }

        if (!entry.VictimIsAi)
        {
            return new VanguardCorpseHostilityEvidence
            {
                Source = "player_policy",
                RelationshipKind = "player",
                Reason = "player_corpses_excluded_by_policy",
                AgeSeconds = 0f
            };
        }

        if (entry.HostilityAtRegistrationByOwnerProfileId.TryGetValue(record.OwnerProfileId, out VanguardCorpseHostilityEvidence captured)
            && captured.Verified)
        {
            return new VanguardCorpseHostilityEvidence
            {
                Verified = true,
                HostileConfirmed = true,
                RelationshipKind = "hostile_ai",
                Source = captured.Source,
                Reason = captured.Reason,
                AgeSeconds = (float)Math.Max(0d, (now - entry.RegisteredAtUtc).TotalSeconds)
            };
        }

        if (SameProfile(threat.EnemyId, victimProfileId) && !threat.StaleThreat)
        {
            return new VanguardCorpseHostilityEvidence
            {
                Verified = true,
                HostileConfirmed = true,
                RelationshipKind = "hostile_ai",
                Source = "operator_snapshot",
                Reason = "current_non_stale_threat_matches_victim",
                AgeSeconds = Math.Max(0f, threat.TimeSinceSeen ?? 0f)
            };
        }

        if (record.BotOwner != null && entry.Victim != null && TryProbeBotsGroup(record.BotOwner, entry.Victim))
        {
            return new VanguardCorpseHostilityEvidence
            {
                Verified = true,
                HostileConfirmed = true,
                RelationshipKind = "hostile_ai",
                Source = "runtime_bots_group",
                Reason = "bots_group_enemy_after_corpse_creation",
                AgeSeconds = (float)Math.Max(0d, (now - entry.RegisteredAtUtc).TotalSeconds)
            };
        }

        if (VanguardCombatAwarenessBridge.TryGetFreshQualifiedCorpseContact(
                record.OwnerProfileId,
                victimProfileId,
                now,
                out string contactSource,
                out float contactAge))
        {
            return new VanguardCorpseHostilityEvidence
            {
                Verified = true,
                HostileConfirmed = true,
                RelationshipKind = "hostile_ai",
                Source = contactSource,
                Reason = "fresh_qualified_vanguard_squad_contact",
                AgeSeconds = contactAge
            };
        }

        string operatorSide = Normalize(record.BotOwner?.Profile?.Side.ToString());
        string victimSide = Normalize(entry.VictimSide);
        if (IsFactionPolicyHostile(operatorSide, victimSide))
        {
            return new VanguardCorpseHostilityEvidence
            {
                Verified = true,
                HostileConfirmed = true,
                RelationshipKind = "hostile_ai",
                Source = "faction_policy",
                Reason = "ai_side_hostile_to_operator_side",
                AgeSeconds = (float)Math.Max(0d, (now - entry.RegisteredAtUtc).TotalSeconds)
            };
        }

        bool protectedFriendly = VanguardFriendlyIdentityRegistry.IsProtectedFriendlyTargetProfileId(victimProfileId);
        return new VanguardCorpseHostilityEvidence
        {
            Verified = true,
            AlliedAiEligible = true,
            RelationshipKind = protectedFriendly ? "allied_ai" : "nonhostile_ai",
            Source = protectedFriendly ? "allied_ai_policy" : "nonplayer_ai_policy",
            Reason = protectedFriendly
                ? "dead_allied_nonplayer_corpse_eligible"
                : "dead_nonplayer_ai_corpse_eligible_without_hostility_requirement",
            AgeSeconds = (float)Math.Max(0d, (now - entry.RegisteredAtUtc).TotalSeconds)
        };
    }

    private static bool TryProbeBotsGroup(BotOwner botOwner, Player victim)
    {
        try
        {
            object rawVictim = victim;
            return rawVictim is IPlayer target
                && botOwner.BotsGroup != null
                && (botOwner.BotsGroup.IsEnemy(target) || botOwner.BotsGroup.IsPlayerEnemy(target));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFactionPolicyHostile(string operatorSide, string victimSide)
    {
        if (string.Equals(operatorSide, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(victimSide, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(victimSide, EPlayerSide.Savage.ToString(), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operatorSide, EPlayerSide.Savage.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool operatorPmc = string.Equals(operatorSide, EPlayerSide.Usec.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(operatorSide, EPlayerSide.Bear.ToString(), StringComparison.OrdinalIgnoreCase);
        bool victimPmc = string.Equals(victimSide, EPlayerSide.Usec.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(victimSide, EPlayerSide.Bear.ToString(), StringComparison.OrdinalIgnoreCase);
        return operatorPmc && victimPmc && !string.Equals(operatorSide, victimSide, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameProfile(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
}
#endif

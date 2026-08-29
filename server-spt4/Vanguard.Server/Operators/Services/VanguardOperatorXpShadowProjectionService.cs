using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Diagnostics;

// Responsibility: Coordinates Operator Xp Shadow Projection Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

/// <summary>
/// Read-only EFT-aligned XP shadow projection. Only exact kill-credit components captured
/// at Player.OnBeenKilledByAggressor are summed. Match-end multipliers, exit rewards, damage,
/// loot and other XP categories remain explicitly outside the total, and Career XP is never mutated.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorXpShadowProjectionService(
    ISptLogger<VanguardOperatorXpShadowProjectionService> logger)
{
    public const string StatusTag = "VANGUARD_EFT_ALIGNED_OPERATOR_XP_SHADOW_ACCOUNTING_STATUS";

    public void Observe(
        string storageProfileId,
        IReadOnlyList<VanguardOperatorProfile> operators,
        VanguardCareerRaidLedgerVerificationSnapshot verification)
    {
        foreach (VanguardOperatorProfile profile in operators.OrderBy(value => value.OperatorId, StringComparer.OrdinalIgnoreCase))
        {
            VanguardCareerRaidLedgerEntry[] entries = verification.VerifiedEntries
                .Where(entry => string.Equals(entry.OperatorId, profile.OperatorId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            VanguardCareerRaidLedgerXpKillCredit[] credits = entries
                .SelectMany(entry => entry.XpKillCredits ?? Array.Empty<VanguardCareerRaidLedgerXpKillCredit>())
                .OrderBy(value => value.ObservedAtUtc)
                .ThenBy(value => value.EventId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            int exactCredits = credits.Count(value => value.CalculationAvailable);
            int awardedCredits = credits.Count(value => value.CalculationAvailable && value.Awarded);
            long killXpSubtotal = credits.Where(value => value.CalculationAvailable).Sum(value => (long)value.KillXpSubtotal);
            long baseXp = credits.Where(value => value.CalculationAvailable).Sum(value => (long)value.BaseXp);
            long bodyPartBonus = credits.Where(value => value.CalculationAvailable).Sum(value => (long)value.BodyPartBonusXp);
            long streakBonus = credits.Where(value => value.CalculationAvailable).Sum(value => (long)value.StreakBonusXp);
            int coveredRaids = entries.Count(entry => (entry.XpKillCredits?.Count ?? 0) > 0);

            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] owner={Safe(storageProfileId)}; operator={Safe(profile.OperatorId)}; ledgerVerifiedEntries={entries.Length}; coveredRaids={coveredRaids}; xpKillCredits={credits.Length}; exactCredits={exactCredits}; awardedCredits={awardedCredits}; baseXp={baseXp}; bodyPartBonusXp={bodyPartBonus}; streakBonusXp={streakBonus}; killXpShadowSubtotal={killXpSubtotal}; coverage=forward_only_exact_eft_kill_components; source=Player.OnBeenKilledByAggressor_plus_BackendConfigSettingsClass.Experience.Kill; sessionMultiplierApplied=false; exitRewardApplied=false; damageXpApplied=false; lootXpApplied=false; otherXpApplied=false; totalSessionExperienceClaimed=false; careerXpMutation=false; levelMutation=false; earnedSinceEnrollmentMutation=false; tag={StatusTag}"));
        }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
}

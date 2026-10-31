#if SPT_CLIENT
using System;
using Vanguard.Client.Diagnostics;

// Responsibility: Provides the single Tactical Coordination drain barrier that stronger local/runtime authorities
// cross before taking physical locomotion from Tactical Coordination.
// Flow: Resolve the exact TC contributions owned by one bot, synchronously retire their physical
// path/window/lease or translation-gate state, then return a compact proof for the acquiring caller.
// Authority boundary: This barrier never grants the new authority and never destroys squad-level
// tactical intent merely because one member yielded locally. It only makes TC physical execution CLEAN.
// Invariant: No higher authority may intentionally acquire locomotion through this barrier while an
// old TC physical lease/gate remains eligible to resume. Any later TC execution is a fresh acquisition.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class TacticalCoordinationPhysicalAuthorityBarrier
{
    public const string StatusTag = "VANGUARD_TC_PHYSICAL_AUTHORITY_BARRIER_STATUS";

    internal static TacticalCoordinationPhysicalDrainResult DrainForPresenceLoss(
        string botProfileId,
        DateTimeOffset now,
        string presenceReason)
    {
        string bot = string.IsNullOrWhiteSpace(botProfileId) ? "none" : botProfileId.Trim();
        string reason = string.IsNullOrWhiteSpace(presenceReason) ? "runtime_presence_lost" : presenceReason.Trim();
        if (string.Equals(bot, "none", StringComparison.OrdinalIgnoreCase))
        {
            return TacticalCoordinationPhysicalDrainResult.Empty(bot, reason);
        }

        bool altDrained = AltDevelopMovementService.ForgetRuntimePresence(
            bot,
            now,
            reason,
            out bool altActiveLeaseDrained,
            out bool altPendingHandoffDrained,
            out bool altManeuverEpochDrained);
        bool directDrained = DirectCommitMovementService.ForgetRuntimePresence(bot, now, reason);
        bool opportunityDrained = TacticalOpportunityMovementService.ForgetRuntimePresence(bot, now, reason);
        bool intentContinuityDrained = TacticalIntentContinuityService.ForgetRuntimePresence(bot, now, reason);
        int antiCongaReleased = AntiCongaService.ForgetRuntimePresence(bot, reason);
        int chokePlansReleased = ChokeCommitSupportService.ForgetRuntimePresence(
            bot,
            reason,
            out int chokeDirectHoldsReleased);
        bool residualPathBindingDrained = SearchTranslationGateBridge.ReleaseResidualTacticalMovementPathBindingForHigherAuthority(
            bot,
            "presence_loss:" + reason,
            out string residualPathRequestKind);

        var result = new TacticalCoordinationPhysicalDrainResult(
            bot,
            reason,
            altDrained,
            altActiveLeaseDrained,
            altPendingHandoffDrained,
            directDrained,
            opportunityDrained,
            antiCongaReleased,
            chokePlansReleased,
            chokeDirectHoldsReleased,
            residualPathBindingDrained,
            residualPathRequestKind);

        if (result.AnyTcStateDrained)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_PHYSICAL_AUTHORITY_PRESENCE_LOSS_DRAIN {result.Summary}; altManeuverEpochDrained={(altManeuverEpochDrained ? "true" : "false")}; intentContinuityDrained={(intentContinuityDrained ? "true" : "false")}; ordering=exact_presence_cleanup_before_runtime_detach; survivorCooldown=false; squadPlanMayPersist=true; staleResume=false");
        }
        return result;
    }

    internal static TacticalCoordinationPhysicalDrainResult DrainForHigherAuthority(
        string botProfileId,
        DateTimeOffset now,
        string authorityReason)
    {
        string bot = string.IsNullOrWhiteSpace(botProfileId) ? "none" : botProfileId.Trim();
        string reason = string.IsNullOrWhiteSpace(authorityReason) ? "higher_authority" : authorityReason.Trim();
        if (string.Equals(bot, "none", StringComparison.OrdinalIgnoreCase))
        {
            return TacticalCoordinationPhysicalDrainResult.Empty(bot, reason);
        }

        bool altDrained = AltDevelopMovementService.DrainForHigherAuthority(
            bot,
            now,
            reason,
            out bool altActiveLeaseDrained,
            out bool altPendingHandoffDrained);
        bool directDrained = DirectCommitMovementService.DrainForHigherAuthority(bot, now, reason);
        bool opportunityDrained = TacticalOpportunityMovementService.DrainForHigherAuthority(bot, now, reason);
        int antiCongaReleased = AntiCongaService.ReleaseForHigherAuthority(bot, now, reason);
        int chokePlansReleased = ChokeCommitSupportService.ReleaseForHigherAuthority(
            bot,
            now,
            reason,
            out int chokeDirectHoldsReleased);
        bool residualPathBindingDrained = SearchTranslationGateBridge.ReleaseResidualTacticalMovementPathBindingForHigherAuthority(
            bot,
            reason,
            out string residualPathRequestKind);

        var result = new TacticalCoordinationPhysicalDrainResult(
            bot,
            reason,
            altDrained,
            altActiveLeaseDrained,
            altPendingHandoffDrained,
            directDrained,
            opportunityDrained,
            antiCongaReleased,
            chokePlansReleased,
            chokeDirectHoldsReleased,
            residualPathBindingDrained,
            residualPathRequestKind);

        if (result.AnyTcStateDrained)
        {
            VanguardClientDiagnosticsLog.Info(StatusTag,
                $"TC_PHYSICAL_AUTHORITY_BARRIER_DRAIN {result.Summary}; ordering=tc_exact_owned_clean_before_higher_authority_acquire; squadPlanMayPersist=true; staleResume=false; freshPhysicalReacquireRequired=true");
        }
        return result;
    }
}

internal readonly struct TacticalCoordinationPhysicalDrainResult
{
    public TacticalCoordinationPhysicalDrainResult(
        string botProfileId,
        string authorityReason,
        bool altDrained,
        bool altActiveLeaseDrained,
        bool altPendingHandoffDrained,
        bool directDrained,
        bool opportunityDrained,
        int antiCongaReleased,
        int chokePlansReleased,
        int chokeDirectHoldsReleased,
        bool residualPathBindingDrained,
        string residualPathRequestKind)
    {
        BotProfileId = botProfileId;
        AuthorityReason = authorityReason;
        AltDrained = altDrained;
        AltActiveLeaseDrained = altActiveLeaseDrained;
        AltPendingHandoffDrained = altPendingHandoffDrained;
        DirectDrained = directDrained;
        OpportunityDrained = opportunityDrained;
        AntiCongaReleased = antiCongaReleased;
        ChokePlansReleased = chokePlansReleased;
        ChokeDirectHoldsReleased = chokeDirectHoldsReleased;
        ResidualPathBindingDrained = residualPathBindingDrained;
        ResidualPathRequestKind = residualPathRequestKind;
    }

    public string BotProfileId { get; }
    public string AuthorityReason { get; }
    public bool AltDrained { get; }
    public bool AltActiveLeaseDrained { get; }
    public bool AltPendingHandoffDrained { get; }
    public bool DirectDrained { get; }
    public bool OpportunityDrained { get; }
    public int AntiCongaReleased { get; }
    public int ChokePlansReleased { get; }
    public int ChokeDirectHoldsReleased { get; }
    public bool ResidualPathBindingDrained { get; }
    public string ResidualPathRequestKind { get; }
    public bool AnyTcStateDrained => AltDrained || DirectDrained || OpportunityDrained || AntiCongaReleased > 0 || ChokePlansReleased > 0 || ChokeDirectHoldsReleased > 0 || ResidualPathBindingDrained;

    public string Summary => "bot=" + Safe(BotProfileId)
        + ";authorityReason=" + Safe(AuthorityReason)
        + ";altDrained=" + Bool(AltDrained)
        + ";altActiveLeaseDrained=" + Bool(AltActiveLeaseDrained)
        + ";altPendingHandoffDrained=" + Bool(AltPendingHandoffDrained)
        + ";directDrained=" + Bool(DirectDrained)
        + ";opportunityDrained=" + Bool(OpportunityDrained)
        + ";antiCongaReleased=" + AntiCongaReleased
        + ";chokePlansReleased=" + ChokePlansReleased
        + ";chokeDirectHoldsReleased=" + ChokeDirectHoldsReleased
        + ";residualPathBindingDrained=" + Bool(ResidualPathBindingDrained)
        + ";residualPathRequest=" + Safe(ResidualPathRequestKind);

    public static TacticalCoordinationPhysicalDrainResult Empty(string botProfileId, string authorityReason)
        => new(botProfileId, authorityReason, false, false, false, false, false, 0, 0, 0, false, "none");

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static string Bool(bool value) => value ? "true" : "false";
}
#endif

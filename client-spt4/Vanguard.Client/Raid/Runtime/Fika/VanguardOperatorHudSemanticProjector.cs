#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Runtime.Decision;

// Responsibility: Projects authoritative Headless/host Operator runtime state into the compact semantic frame transported to remote HUD clients.
// Flow: Live Operator identity, health/medical, activity and alert evidence is normalized into stable semantic fields, revisioned and sent through the Fika transport; receivers render the frame without re-deriving gameplay truth.
// Authority boundary: The publisher process is runtime authority for the projected frame; remote clients are presentation consumers only.
// Invariant: Frames are monotonic/revisioned for one raid, missing evidence remains explicit, and projection must not mutate the Operator behavior it reports.
namespace Vanguard.Client.Raid.Runtime.Fika;

/// <summary>
/// Presentation-neutral read-model projected exclusively from Vanguard's canonical authoritative
/// OperatorDecisionSnapshot. The projection never reads SAIN, BigBrain, LootingBots or EFT state
/// directly and never mutates gameplay authority.
/// </summary>
internal static class VanguardOperatorHudSemanticProjector
{
    internal const int SeverityNone = 0;
    internal const int SeverityAttention = 1;
    internal const int SeverityCritical = 2;
    internal const int SeverityStale = 3;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(8.0d);

    internal static VanguardOperatorHudSemanticProjection Project(OperatorDecisionSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot is null || ReferenceEquals(snapshot, OperatorDecisionSnapshot.Empty))
        {
            return new VanguardOperatorHudSemanticProjection(
                "ETAT INDISP.",
                "TELEM.",
                SeverityStale,
                "authoritative decision snapshot unavailable",
                false,
                false,
                true);
        }

        TimeSpan age = now - snapshot.CapturedAtUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age > StaleAfter)
        {
            return new VanguardOperatorHudSemanticProjection(
                "ETAT OBSOLETE",
                "LIAISON...",
                SeverityStale,
                $"snapshot age={age.TotalSeconds:0.0}s",
                true,
                false,
                true);
        }

        if (!snapshot.Alive)
        {
            return new VanguardOperatorHudSemanticProjection(
                "HORS COMBAT",
                "KIA",
                SeverityCritical,
                BuildDetail(snapshot),
                true,
                true,
                true);
        }

        string activity = ResolveActivity(snapshot);
        (string Label, int Severity) alert = ResolveAlert(snapshot);
        bool urgent = alert.Severity == SeverityCritical
            || string.Equals(activity, "EVITE GRENADE", StringComparison.Ordinal)
            || string.Equals(activity, "SE SOIGNE", StringComparison.Ordinal)
            || string.Equals(activity, "CHIRURGIE", StringComparison.Ordinal);

        return new VanguardOperatorHudSemanticProjection(
            activity,
            alert.Label,
            alert.Severity,
            BuildDetail(snapshot),
            true,
            true,
            urgent);
    }

    private static string ResolveActivity(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.Medical.Actionability.SurgicalKitUsing)
        {
            return "CHIRURGIE";
        }

        if (snapshot.Medical.Actionability.AnyMedicineUsing
            || snapshot.Medical.Actionability.FirstAidUsing
            || snapshot.Medical.Actionability.StimulatorUsing)
        {
            return "SE SOIGNE";
        }

        if (snapshot.GrenadeHazard.HasRelevantHazard
            && (snapshot.GrenadeHazard.Critical || snapshot.GrenadeHazard.Imminent))
        {
            return "EVITE GRENADE";
        }

        if (snapshot.PrimaryExecution.IsOpportunisticLoot
            || snapshot.Looting.BotLooting == true
            || snapshot.Looting.LootTaskRunning == true)
        {
            return "LOOT";
        }

        if (snapshot.Orbit.Active && ContainsAny(new[] { snapshot.Orbit.Status, snapshot.Orbit.Category, snapshot.Orbit.ExtractReason }, "extract", "exfil"))
        {
            return "EXTRACTION";
        }

        bool combatContext = snapshot.Sain.IsInCombat == true
            || snapshot.Sain.HasEnemy == true
            || snapshot.Threat.DirectThreat
            || snapshot.Threat.EnemyVisible == true
            || snapshot.Threat.EnemyCanShoot == true
            || snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true;
        if (combatContext)
        {
            if (snapshot.Sain.RunningToCover == true
                || ContainsAny(new[] { snapshot.Sain.CurrentAction, snapshot.Sain.SelfDecision, snapshot.Sain.CombatDecision }, "runcover", "run_to_cover", "move_to_cover", "findcover", "find_cover"))
            {
                return "SE MET A COUVERT";
            }

            if (snapshot.Sain.Searching == true
                || ContainsAny(new[] { snapshot.Sain.CurrentAction, snapshot.Sain.CombatDecision, snapshot.Sain.ActiveLayer }, "search", "seek"))
            {
                return "RECHERCHE";
            }

            if (ContainsAny(new[] { snapshot.Sain.CurrentAction, snapshot.Sain.CombatDecision, snapshot.Sain.SelfDecision, snapshot.Sain.ActiveLayer }, "cover", "holdangle", "hold_angle"))
            {
                return "EN COUVERTURE";
            }

            if (snapshot.Threat.EnemyVisible == true
                || snapshot.Threat.EnemyCanShoot == true
                || ContainsAny(new[] { snapshot.Sain.CurrentAction, snapshot.Sain.CombatDecision, snapshot.Sain.ActiveLayer }, "shoot", "fight", "attack", "engage", "suppress", "combat"))
            {
                return "ENGAGE";
            }

            return "RECHERCHE CONTACT";
        }

        if (snapshot.Sain.Searching == true
            || ContainsAny(new[] { snapshot.Sain.CurrentAction, snapshot.Sain.SquadDecision, snapshot.Sain.ActiveLayer }, "search", "seek"))
        {
            return "RECHERCHE";
        }

        if (ContainsAny(
                new[]
                {
                    snapshot.MovementAuthority.CurrentAuthority,
                    snapshot.MovementAuthority.CurrentAuthorityReason,
                    snapshot.Sain.SelfDecision,
                    snapshot.Sain.SquadDecision,
                },
                "breakcontact", "break_contact", "retreat", "fallback"))
        {
            return "ROMPT CONTACT";
        }

        if (ContainsAny(
                new[]
                {
                    snapshot.MovementAuthority.CurrentAuthority,
                    snapshot.MovementAuthority.CurrentAuthorityReason,
                    snapshot.MovementAuthority.BrokerPlan.RequestKind,
                    snapshot.MovementAuthority.BrokerPlan.AnchorKind,
                    snapshot.SquadCohesion.RecommendedIntent,
                },
                "rejoin", "follow", "rally", "catchup", "catch_up", "formation"))
        {
            return "REJOINT";
        }

        if ((snapshot.MovementAuthority.HardOutsideBubble || snapshot.MovementAuthority.SoftOutsideBubble)
            && IsMoving(snapshot))
        {
            return "REJOINT";
        }

        if (IsMoving(snapshot))
        {
            return "SUIT";
        }

        if (ContainsAny(new[] { snapshot.Sain.CurrentAction, snapshot.Sain.ActiveLayer, snapshot.Brain.ActiveLayer, snapshot.Brain.Node }, "cover"))
        {
            return "EN COUVERTURE";
        }

        if (ContainsAny(new[] { snapshot.Sain.CurrentAction, snapshot.Sain.ActiveLayer, snapshot.Brain.ActiveLayer, snapshot.Brain.Node }, "hold", "guard", "ambush", "stationary"))
        {
            return "EN POSITION";
        }

        return "SURVEILLE";
    }

    private static (string Label, int Severity) ResolveAlert(OperatorDecisionSnapshot snapshot)
    {
        if (snapshot.GrenadeHazard.HasRelevantHazard)
        {
            return snapshot.GrenadeHazard.Critical || snapshot.GrenadeHazard.Imminent
                ? ("GRENADE !", SeverityCritical)
                : ("GRENADE", SeverityAttention);
        }

        if (snapshot.Threat.ShotMeRecently == true
            || snapshot.Threat.ShotAtMeRecently == true
            || snapshot.Medical.Safety.IncomingFireRecent)
        {
            return ("SOUS LE FEU", SeverityCritical);
        }

        if (snapshot.Threat.DirectThreat)
        {
            if (snapshot.Threat.Distance.HasValue && snapshot.Threat.Distance.Value <= 20f)
            {
                return ("CONTACT PROCHE", SeverityAttention);
            }

            return ("DANGER", SeverityAttention);
        }

        return (string.Empty, SeverityNone);
    }

    private static bool IsMoving(OperatorDecisionSnapshot snapshot)
    {
        return snapshot.RealSpeed > 0.12f
            || snapshot.Movement.RealSpeed > 0.12f
            || snapshot.Movement.Sprinting == true
            || snapshot.Movement.HasPath == true && snapshot.Movement.DistanceToDestination.GetValueOrDefault() > 0.75f;
    }

    private static string BuildDetail(OperatorDecisionSnapshot snapshot)
    {
        var tokens = new List<string>();
        AddDetail(tokens, "SAIN", FirstUseful(snapshot.Sain.CurrentAction, snapshot.Sain.CombatDecision, snapshot.Sain.ActiveLayer));
        AddDetail(tokens, "VGD", FirstUseful(snapshot.MovementAuthority.CurrentAuthority, snapshot.Brain.ActiveLayer));
        if (snapshot.PrimaryExecution.IsOpportunisticLoot)
        {
            AddDetail(tokens, "LOOT", FirstUseful(snapshot.PrimaryExecution.State, snapshot.PrimaryExecution.IntentKey));
        }
        else if (snapshot.Looting.BotLooting == true || snapshot.Looting.LootTaskRunning == true)
        {
            tokens.Add("LOOT:legacy");
        }

        return tokens.Count == 0 ? "authoritative snapshot" : string.Join("  ", tokens.Take(3));
    }

    private static void AddDetail(ICollection<string> tokens, string prefix, string value)
    {
        if (!IsUseful(value))
        {
            return;
        }

        string compact = value.Trim().Replace('_', ' ');
        if (compact.Length > 32)
        {
            compact = compact.Substring(0, 32);
        }

        tokens.Add(prefix + ":" + compact);
    }

    private static string FirstUseful(params string[] values)
    {
        foreach (string value in values)
        {
            if (IsUseful(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool IsUseful(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith("_unknown", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith("_unread", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(IEnumerable<string?> values, params string[] needles)
    {
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (string needle in needles)
            {
                if (!string.IsNullOrWhiteSpace(needle)
                    && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal sealed record VanguardOperatorHudSemanticProjection(
    string ActivityLabel,
    string AlertLabel,
    int AlertSeverity,
    string Detail,
    bool Authoritative,
    bool Fresh,
    bool Urgent);
#else
namespace Vanguard.Client.Raid.Runtime.Fika;

internal static class VanguardOperatorHudSemanticProjector
{
}
#endif

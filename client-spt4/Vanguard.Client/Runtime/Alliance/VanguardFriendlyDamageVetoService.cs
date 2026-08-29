#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using Vanguard.Client.Diagnostics;

// Responsibility: Coordinates Friendly Damage Veto Service for the Operator allegiance runtime, delegating specialized work to its collaborators.
// Flow: Current raid/runtime evidence is normalized, applicable guards and ownership rules are evaluated, then the service updates only its bounded runtime/UI responsibility.
// Authority boundary: Service coordinates its domain but does not fabricate server persistence truth or bypass higher-priority runtime authorities.
// Invariant: State is lifecycle-scoped, stale work is releasable, and failures degrade without leaving hidden long-lived ownership.
namespace Vanguard.Client.Runtime.Alliance;

/// <summary>
/// Final damage-boundary safety veto. Geometry and trigger guards remain the first line of
/// protection, but confirmed Vanguard Operator damage against a protected player or Operator is
/// rejected before EFT mutates health. The service never changes targets, movement or SAIN state.
/// </summary>
internal static class VanguardFriendlyDamageVetoService
{
    public const string StatusTag = "VANGUARD_FRIENDLY_DAMAGE_VETO_STATUS";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> LastLogAtByPair = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2.0d);

    public static void Reset(string reason)
    {
        lock (Sync)
        {
            LastLogAtByPair.Clear();
        }

        VanguardClientDiagnosticsLog.Info(StatusTag,
            $"VANGUARD_FRIENDLY_DAMAGE_VETO_RESET reason={Safe(reason)}; terminalDamageBoundary=true; targetMutation=false; movementMutation=false; sainMutation=false; tag={StatusTag}");
    }

    public static bool ShouldBlock(Player? victim, DamageInfoStruct damageInfo, EBodyPart bodyPart, out string summary)
    {
        summary = "blocked=false";
        if (victim == null || victim.HealthController?.IsAlive != true)
        {
            return false;
        }

        IPlayer? attacker = damageInfo.Player?.iPlayer;
        string attackerProfileId = Normalize(attacker?.ProfileId);
        string victimProfileId = Normalize(victim.ProfileId);
        if (attackerProfileId == "none"
            || victimProfileId == "none"
            || string.Equals(attackerProfileId, victimProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!VanguardFriendlyIdentityRegistry.ShouldProtectFromVanguardOperator(attackerProfileId, victimProfileId))
        {
            return false;
        }

        float reportedDamage = Math.Max(0f, damageInfo.Damage);
        summary = "blocked=true;attacker=" + Safe(attackerProfileId)
            + ";victim=" + Safe(victimProfileId)
            + ";bodyPart=" + Safe(bodyPart.ToString())
            + ";reportedDamage=" + reportedDamage.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        LogBlocked(attackerProfileId, victimProfileId, bodyPart, reportedDamage);
        return true;
    }

    private static void LogBlocked(string attackerProfileId, string victimProfileId, EBodyPart bodyPart, float reportedDamage)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string key = attackerProfileId + "|" + victimProfileId;
        lock (Sync)
        {
            if (LastLogAtByPair.TryGetValue(key, out DateTimeOffset last) && now - last < LogInterval)
            {
                return;
            }

            LastLogAtByPair[key] = now;
        }

        VanguardClientDiagnosticsLog.Warning(StatusTag, () =>
            $"VANGUARD_FRIENDLY_DAMAGE_BLOCKED attacker={Safe(attackerProfileId)}; victim={Safe(victimProfileId)}; bodyPart={Safe(bodyPart.ToString())}; reportedDamage={reportedDamage:0.0}; action=skip_Player.ApplyDamageInfo; targetMutation=false; movementMutation=false; sainMutation=false; allianceCanonical=true; tag={StatusTag}");
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    private static string Safe(string? value) => Normalize(value).Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
#else
namespace Vanguard.Client.Runtime.Alliance;
internal static class VanguardFriendlyDamageVetoService
{
    public const string StatusTag = "VANGUARD_FRIENDLY_DAMAGE_VETO_STATUS";
    public static void Reset(string reason) { }
}
#endif

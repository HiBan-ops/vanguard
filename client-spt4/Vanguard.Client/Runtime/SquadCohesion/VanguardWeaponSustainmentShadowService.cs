#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vanguard.Client.Diagnostics;

// Responsibility: exposes a per-weapon sustainment read-model from the already-captured live equipment snapshot.
// Flow: immediate rounds + loaded spare magazines + loose ammunition -> raw reload-depth and alternative-weapon evidence -> bounded Diagnostic telemetry.
// Authority boundary: shadow observation only. It does not change weapon preference, request a slot transition, mutate inventory, choose a target, aim or fire.
// Invariant: loaded magazine reserve and loose ammunition remain separate; several partial magazines are represented by their summed fill fractions rather than treated as full reloads.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class VanguardWeaponSustainmentShadowService
{
    public const string StatusTag = "VANGUARD_DWP_SUSTAINMENT_SHADOW_STATUS";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30d);
    private const int MaxDiagnosticFieldLength = 160;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ObservationState> ByBotProfileId = new(StringComparer.OrdinalIgnoreCase);

    internal static void Observe(
        string? botProfileId,
        string? ownerProfileId,
        VanguardLiveEquipmentCapabilitySnapshot? equipment,
        DateTimeOffset now)
    {
        string bot = Normalize(botProfileId);
        string owner = Normalize(ownerProfileId);
        if (bot.Length == 0 || owner.Length == 0 || equipment == null || !equipment.Observed || equipment.Weapons.Count == 0)
        {
            return;
        }

        string fingerprint = BuildFingerprint(equipment.Weapons);
        bool emit;
        string emissionReason;
        lock (Sync)
        {
            if (!ByBotProfileId.TryGetValue(bot, out ObservationState? state))
            {
                state = new ObservationState(fingerprint, now + HeartbeatInterval);
                ByBotProfileId[bot] = state;
                emit = true;
                emissionReason = "INITIAL";
            }
            else if (!string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                state.Fingerprint = fingerprint;
                state.NextHeartbeatAtUtc = now + HeartbeatInterval;
                emit = true;
                emissionReason = "MATERIAL_CHANGE";
            }
            else if (now >= state.NextHeartbeatAtUtc)
            {
                state.NextHeartbeatAtUtc = now + HeartbeatInterval;
                emit = true;
                emissionReason = "HEARTBEAT";
            }
            else
            {
                emit = false;
                emissionReason = string.Empty;
            }
        }

        if (!emit)
        {
            return;
        }

        foreach (VanguardLiveWeaponCapabilityObservation weapon in equipment.Weapons)
        {
            VanguardLiveWeaponCapabilityObservation? alternative = equipment.Weapons
                .Where(candidate => !string.Equals(candidate.ItemId, weapon.ItemId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.CompatibleMagazineReloadDepthEquivalent)
                .ThenByDescending(candidate => candidate.CompatibleMagazineAmmoCount)
                .ThenByDescending(candidate => candidate.ReadyRounds)
                .FirstOrDefault();

            string lowMagazinePressure = ResolveLowMagazinePressure(weapon);
            string alternativeSustainment = alternative == null
                ? "NONE"
                : $"slot={Safe(alternative.Slot)},item={Safe(alternative.ItemId)},ready={alternative.ReadyRounds.ToString(CultureInfo.InvariantCulture)},magReserve={alternative.CompatibleMagazineAmmoCount.ToString(CultureInfo.InvariantCulture)},looseReserve={alternative.CompatibleLooseAmmunitionCount.ToString(CultureInfo.InvariantCulture)},reloadDepth={Number(alternative.CompatibleMagazineReloadDepthEquivalent)}";

            VanguardClientDiagnosticsLog.Diagnostic(StatusTag, () =>
                $"DWP_WEAPON_SUSTAINMENT_SHADOW bot={Bounded(bot)}; owner={Bounded(owner)}; emission={emissionReason}; slot={Bounded(weapon.Slot)}; item={Bounded(weapon.ItemId)}; family={Bounded(weapon.CanonicalWeaponFamily)}; immediateReady={weapon.ReadyRounds.ToString(CultureInfo.InvariantCulture)}; magazineReserve={weapon.CompatibleMagazineAmmoCount.ToString(CultureInfo.InvariantCulture)}; looseAmmoReserve={weapon.CompatibleLooseAmmunitionCount.ToString(CultureInfo.InvariantCulture)}; reloadDepth={Number(weapon.CompatibleMagazineReloadDepthEquivalent)}; compatibleMagazineCount={weapon.CompatibleMagazineCount.ToString(CultureInfo.InvariantCulture)}; capacityKnownMagazineCount={weapon.CompatibleMagazineCapacityKnownCount.ToString(CultureInfo.InvariantCulture)}; fullMagazineCount={weapon.CompatibleFullMagazineCount.ToString(CultureInfo.InvariantCulture)}; partialMagazineCount={weapon.CompatiblePartialMagazineCount.ToString(CultureInfo.InvariantCulture)}; lowMagazinePressure={Bounded(lowMagazinePressure)}; alternativeWeaponSustainment={Bounded(alternativeSustainment)}; reserveCompatibility=magazine_plus_loose_preserved; forceSwap=false; preferenceMutation=false; inventoryMutation=false; targetAimFireMutation=false");
        }
    }

    internal static void ResetForRaidLifecycle()
    {
        lock (Sync)
        {
            ByBotProfileId.Clear();
        }
    }

    private static string ResolveLowMagazinePressure(VanguardLiveWeaponCapabilityObservation weapon)
    {
        if (weapon.CompatibleMagazineCount <= 0)
        {
            return weapon.CompatibleLooseAmmunitionCount > 0
                ? "UNRESOLVED_NO_LOADED_SPARE_MAGAZINE_LOOSE_AMMO_PRESENT"
                : "NO_LOADED_SPARE_MAGAZINE_OBSERVED";
        }

        if (weapon.CompatibleMagazineCapacityKnownCount < weapon.CompatibleMagazineCount)
        {
            return "UNRESOLVED_MAGAZINE_CAPACITY";
        }

        return weapon.CompatibleFullMagazineCount > 0
            ? "FULL_SPARE_MAGAZINE_PRESENT"
            : "PARTIAL_SPARE_MAGAZINES_ONLY";
    }

    private static string BuildFingerprint(IReadOnlyList<VanguardLiveWeaponCapabilityObservation> weapons)
    {
        var builder = new StringBuilder();
        foreach (VanguardLiveWeaponCapabilityObservation weapon in weapons.OrderBy(item => item.Slot, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(Safe(weapon.Slot)).Append('|')
                .Append(Safe(weapon.ItemId)).Append('|')
                .Append(weapon.ReadyRounds).Append('|')
                .Append(weapon.CompatibleMagazineAmmoCount).Append('|')
                .Append(weapon.CompatibleLooseAmmunitionCount).Append('|')
                .Append(weapon.CompatibleMagazineCount).Append('|')
                .Append(weapon.CompatibleMagazineCapacityKnownCount).Append('|')
                .Append(weapon.CompatibleFullMagazineCount).Append('|')
                .Append(weapon.CompatiblePartialMagazineCount).Append('|')
                .Append(weapon.CompatibleMagazineReloadDepthEquivalent.ToString("0.000", CultureInfo.InvariantCulture)).Append(';');
        }
        return builder.ToString();
    }

    private static string Number(double value)
        => double.IsNaN(value) || double.IsInfinity(value)
            ? "unknown"
            : value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_');

    private static string Bounded(string? value)
    {
        string safe = Safe(value);
        return safe.Length <= MaxDiagnosticFieldLength ? safe : safe[..MaxDiagnosticFieldLength];
    }

    private sealed class ObservationState
    {
        public ObservationState(string fingerprint, DateTimeOffset nextHeartbeatAtUtc)
        {
            Fingerprint = fingerprint;
            NextHeartbeatAtUtc = nextHeartbeatAtUtc;
        }

        public string Fingerprint { get; set; }
        public DateTimeOffset NextHeartbeatAtUtc { get; set; }
    }
}
#endif

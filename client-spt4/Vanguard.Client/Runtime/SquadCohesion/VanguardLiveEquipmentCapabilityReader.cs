#if SPT_CLIENT
using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using EftWeapon = global::EFT.InventoryLogic.Weapon;

// Responsibility: Normalizes per-weapon live material capability for RF-01 calibration without assigning tactical fitness.
// Flow: Current carried weapon slots and compatible inventory ammunition are read through typed EFT surfaces, then projected as raw per-weapon facts.
// Authority boundary: Read-only shadow evidence only. It does not consume loot utility score/price as TC authority, classify contribution equipment fit, switch weapons or mutate inventory.
// Invariant: Per-weapon ammunition and weapon facts remain raw observations; no low/critical reserve threshold, global weapon quality score or contribution winner is invented here.
namespace Vanguard.Client.Runtime.SquadCohesion;

internal static class VanguardLiveEquipmentCapabilityReader
{
    // Broad carried-support observation. EFT bot reload semantics are not uniform across these slots:
    // loose-ammo reload paths may inspect all four, while spare-magazine fast access is narrower.
    // ReserveRounds is therefore raw carried compatible material evidence, not a claim that every
    // observed reserve round is immediately reachable by every weapon/reload mode.
    private static readonly EquipmentSlot[] SupportSearchSlots =
    {
        EquipmentSlot.Pockets,
        EquipmentSlot.TacticalVest,
        EquipmentSlot.Backpack,
        EquipmentSlot.SecuredContainer,
    };

    public static VanguardLiveEquipmentCapabilitySnapshot Capture(BotOwner? botOwner)
        => CaptureCore(botOwner, includeAggregateCompatibleMaterial: false);

    internal static VanguardLiveEquipmentCapabilitySnapshot CaptureWithAggregateMaterial(BotOwner? botOwner)
        => CaptureCore(botOwner, includeAggregateCompatibleMaterial: true);

    private static VanguardLiveEquipmentCapabilitySnapshot CaptureCore(
        BotOwner? botOwner,
        bool includeAggregateCompatibleMaterial)
    {
        if (botOwner == null)
        {
            return Unobserved("bot_missing");
        }

        var player = botOwner.GetPlayer;
        if (player?.InventoryController == null)
        {
            return Unobserved("player_or_inventory_controller_missing");
        }

        InventoryEquipment? equipment = player.Inventory?.Equipment;
        if (equipment == null)
        {
            return Unobserved("equipment_missing");
        }

        try
        {
            IReadOnlyList<Item> support = CaptureOperatorInventorySupport(player);
            var weapons = new List<VanguardLiveWeaponCapabilityObservation>(3);

            AppendWeapon(weapons, equipment, EquipmentSlot.FirstPrimaryWeapon, true, support);
            AppendWeapon(weapons, equipment, EquipmentSlot.SecondPrimaryWeapon, true, support);
            AppendWeapon(weapons, equipment, EquipmentSlot.Holster, false, support);

            int compatibleMagazineCount = -1;
            int compatibleMagazineAmmoCount = -1;
            int compatibleLooseAmmunitionCount = -1;
            if (includeAggregateCompatibleMaterial)
            {
                IReadOnlyList<Item> aggregateSupport = CaptureAggregateMaterialSupport(support, equipment);
                IReadOnlyList<EftWeapon> carriedWeapons = CaptureCarriedWeapons(equipment);
                ObserveGlobalCompatibleMaterial(
                    aggregateSupport,
                    carriedWeapons,
                    out compatibleMagazineCount,
                    out compatibleMagazineAmmoCount,
                    out compatibleLooseAmmunitionCount);
            }

            int longWeapons = 0;
            int weaponsWithRounds = 0;
            int weaponsWithoutRounds = 0;
            int brickedWeapons = 0;
            foreach (VanguardLiveWeaponCapabilityObservation weapon in weapons)
            {
                if (weapon.IsLongWeapon) longWeapons++;
                if (weapon.TotalObservedRounds > 0) weaponsWithRounds++;
                else weaponsWithoutRounds++;
                if (weapon.Bricked) brickedWeapons++;
            }

            return new VanguardLiveEquipmentCapabilitySnapshot
            {
                Observed = true,
                ObservationReason = "typed_equipment_slots_plus_per_weapon_raw_material_evidence",
                WeaponCount = weapons.Count,
                LongWeaponCount = longWeapons,
                SidearmObserved = weapons.Exists(item => !item.IsLongWeapon),
                CompatibleMagazineCount = compatibleMagazineCount,
                CompatibleMagazineAmmoCount = compatibleMagazineAmmoCount,
                CompatibleLooseAmmunitionCount = compatibleLooseAmmunitionCount,
                WeaponsWithRoundsCount = weaponsWithRounds,
                WeaponsWithoutRoundsCount = weaponsWithoutRounds,
                BrickedWeaponCount = brickedWeapons,
                Weapons = weapons.ToArray()
            };
        }
        catch (Exception exception)
        {
            return Unobserved("live_equipment_capability_read_failed:" + exception.GetType().Name);
        }
    }

    private static VanguardLiveEquipmentCapabilitySnapshot Unobserved(string reason) => new()
    {
        Observed = false,
        ObservationReason = reason
    };

    private static void AppendWeapon(
        List<VanguardLiveWeaponCapabilityObservation> destination,
        InventoryEquipment equipment,
        EquipmentSlot slot,
        bool isLongWeapon,
        IReadOnlyList<Item> support)
    {
        if (equipment.GetSlot(slot)?.ContainedItem is not EftWeapon weapon)
        {
            return;
        }

        int ready = Math.Max(0, weapon.GetCurrentMagazineCount()) + Math.Max(0, weapon.ChamberAmmoCount);
        int magazineReserveRounds = 0;
        int looseAmmunitionReserveRounds = 0;
        int magazines = 0;
        int magazinesWithKnownCapacity = 0;
        int fullMagazines = 0;
        int partialMagazines = 0;
        double reloadDepthEquivalent = 0d;
        int bestPenetration = 0;
        int bestDamage = 0;

        foreach (Item item in support)
        {
            if (item is MagazineItemClass magazine && Fits(magazine, weapon))
            {
                magazines++;
                int magazineRounds = Math.Max(0, magazine.Count);
                magazineReserveRounds += magazineRounds;
                int maximumRounds = Math.Max(0, magazine.MaxCount);
                if (maximumRounds > 0)
                {
                    magazinesWithKnownCapacity++;
                    reloadDepthEquivalent += Math.Min(1d, (double)magazineRounds / maximumRounds);
                    if (magazineRounds >= maximumRounds)
                    {
                        fullMagazines++;
                    }
                    else
                    {
                        partialMagazines++;
                    }
                }

                ObserveMagazineAmmunitionFacts(magazine, ref bestPenetration, ref bestDamage);
            }

            if (item is AmmoItemClass ammunition && Fits(ammunition, weapon))
            {
                ObserveAmmunitionFacts(ammunition, ref bestPenetration, ref bestDamage);
                looseAmmunitionReserveRounds += Math.Max(0, ammunition.StackObjectsCount);
            }
        }

        int reserve = magazineReserveRounds + looseAmmunitionReserveRounds;

        // Ready-round counts already include the current magazine and chambers. Their ammunition
        // identities must also participate in the raw ballistic maxima, including mixed magazines.
        ObserveMagazineAmmunitionFacts(weapon.GetCurrentMagazine(), ref bestPenetration, ref bestDamage);
        foreach (Slot chamber in weapon.Chambers)
        {
            ObserveAmmunitionFacts(chamber.ContainedItem as AmmoItemClass, ref bestPenetration, ref bestDamage);
        }

        float durability = weapon.Repairable?.MaxDurability > 0f
            ? weapon.Repairable.Durability / weapon.Repairable.MaxDurability
            : 0f;
        int total = ready + reserve;

        string weaponClass = string.IsNullOrWhiteSpace(weapon.WeapClass) ? "unknown" : weapon.WeapClass;
        string canonicalWeaponFamily = ResolveCanonicalWeaponFamily(weaponClass);
        ObserveMountedSightFacts(
            weapon,
            out int mountedSightCount,
            out int magnifiedSightCount,
            out float maxSupportedSightZoom,
            out string sightCapabilityState);

        destination.Add(new VanguardLiveWeaponCapabilityObservation
        {
            Slot = slot.ToString(),
            ItemId = weapon.Id,
            TemplateId = weapon.StringTemplateId,
            Caliber = weapon.AmmoCaliber ?? "none",
            WeaponClass = string.IsNullOrWhiteSpace(weapon.WeapClass) ? "unknown" : weapon.WeapClass,
            CanonicalWeaponFamily = canonicalWeaponFamily,
            WeaponFamilyQualification = ResolveWeaponFamilyQualification(canonicalWeaponFamily),
            MountedSightCount = mountedSightCount,
            MagnifiedSightCount = magnifiedSightCount,
            MaxSupportedSightZoom = maxSupportedSightZoom,
            SightCapabilityState = sightCapabilityState,
            IsLongWeapon = isLongWeapon,
            ReadyRounds = ready,
            ReserveRounds = reserve,
            TotalObservedRounds = total,
            CompatibleMagazineCount = magazines,
            CompatibleMagazineAmmoCount = magazineReserveRounds,
            CompatibleLooseAmmunitionCount = looseAmmunitionReserveRounds,
            CompatibleMagazineCapacityKnownCount = magazinesWithKnownCapacity,
            CompatibleFullMagazineCount = fullMagazines,
            CompatiblePartialMagazineCount = partialMagazines,
            CompatibleMagazineReloadDepthEquivalent = reloadDepthEquivalent,
            DurabilityFraction = durability,
            BestPenetration = bestPenetration,
            BestDamage = bestDamage,
            Ergonomics = weapon.ErgonomicsTotal,
            Recoil = weapon.RecoilTotal,
            SightingRange = weapon.GetSightingRange(),
            // EFT exposes template bEffDist as the weapon's EffectiveDist attribute. Keep it
            // separate from GetSightingRange(): both are raw EFT facts and neither is a Vanguard range band.
            EffectiveDistanceMeters = weapon.Template.bEffDist,
            Bricked = total <= 0,
            RawAmmunitionPresence = total > 0 ? "ROUNDS_PRESENT" : "NO_ROUNDS_OBSERVED"
        });
    }


    private static string ResolveCanonicalWeaponFamily(string weaponClass)
    {
        if (string.IsNullOrWhiteSpace(weaponClass)
            || string.Equals(weaponClass, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "UNKNOWN";
        }

        // Complete canonical weapon-class set observed in the SPT EFT item database.
        // This is normalization only: no contribution fit, range role or preferred-family ranking.
        return weaponClass.Trim().ToLowerInvariant() switch
        {
            "smg" => "SMG",
            "assaultcarbine" => "CARBINE",
            "assaultrifle" => "ASSAULT_RIFLE",
            "shotgun" => "SHOTGUN",
            "marksmanrifle" => "MARKSMAN_RIFLE",
            "sniperrifle" => "SNIPER_RIFLE",
            "pistol" => "SIDEARM",
            "machinegun" => "MACHINE_GUN",
            "grenadelauncher" => "GRENADE_LAUNCHER",
            "specialweapon" => "SPECIAL_WEAPON",
            _ => "UNRECOGNIZED"
        };
    }

    private static string ResolveWeaponFamilyQualification(string canonicalWeaponFamily)
        => canonicalWeaponFamily switch
        {
            "UNKNOWN" => "UNKNOWN_CLASS",
            "UNRECOGNIZED" => "UNRECOGNIZED_CLASS",
            _ => "KNOWN_CANONICAL_CLASS"
        };

    private static void ObserveMountedSightFacts(
        EftWeapon weapon,
        out int mountedSightCount,
        out int magnifiedSightCount,
        out float maxSupportedSightZoom,
        out string sightCapabilityState)
    {
        mountedSightCount = 0;
        magnifiedSightCount = 0;
        maxSupportedSightZoom = 0f;
        int resolvedSightZoomCount = 0;

        foreach (Item item in weapon.GetAllVisibleItems())
        {
            if (ReferenceEquals(item, weapon) || item is not SightsItemClass sight)
            {
                continue;
            }

            mountedSightCount++;
            float[][]? zoomGroups = sight.Sight?.Template?.Zooms;
            if (zoomGroups == null)
            {
                continue;
            }

            bool sightZoomObserved = false;
            bool sightMagnified = false;
            foreach (float[]? zoomGroup in zoomGroups)
            {
                if (zoomGroup == null)
                {
                    continue;
                }

                foreach (float zoom in zoomGroup)
                {
                    if (float.IsNaN(zoom) || float.IsInfinity(zoom) || zoom <= 0f)
                    {
                        continue;
                    }

                    sightZoomObserved = true;
                    maxSupportedSightZoom = Math.Max(maxSupportedSightZoom, zoom);
                    if (zoom > 1f)
                    {
                        sightMagnified = true;
                    }
                }
            }

            if (sightZoomObserved)
            {
                resolvedSightZoomCount++;
                if (sightMagnified)
                {
                    magnifiedSightCount++;
                }
            }
        }

        sightCapabilityState = mountedSightCount == 0
            ? "NO_MOUNTED_SIGHT"
            : magnifiedSightCount > 0
                ? "MAGNIFIED_SIGHT_PRESENT"
                : resolvedSightZoomCount < mountedSightCount
                    ? "MOUNTED_SIGHT_ZOOM_UNRESOLVED"
                    : "NON_MAGNIFIED_SIGHT_ONLY";
    }

    private static IReadOnlyList<Item> CaptureOperatorInventorySupport(Player player)
    {
        var items = new List<Item>();
        player.InventoryController.GetAcceptableItemsNonAlloc(SupportSearchSlots, items);
        return items;
    }

    private static IReadOnlyList<Item> CaptureAggregateMaterialSupport(
        IReadOnlyList<Item> support,
        InventoryEquipment equipment)
    {
        // Preserve the historical aggregate loot-need surface (including direct special-equipment
        // slots) without changing the per-weapon raw material surface and without asking the
        // InventoryController for a second traversal.
        var aggregate = new List<Item>(support);
        Vanguard.Client.Runtime.Inventory.VanguardSpecialEquipmentSlotReader.AppendDirectItems(equipment, aggregate);
        return aggregate;
    }

    private static IReadOnlyList<EftWeapon> CaptureCarriedWeapons(InventoryEquipment equipment)
    {
        var weapons = new List<EftWeapon>(3);
        if (equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem is EftWeapon first) weapons.Add(first);
        if (equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem is EftWeapon second) weapons.Add(second);
        if (equipment.GetSlot(EquipmentSlot.Holster)?.ContainedItem is EftWeapon holster) weapons.Add(holster);
        return weapons;
    }

    private static void ObserveGlobalCompatibleMaterial(
        IReadOnlyList<Item> support,
        IReadOnlyList<EftWeapon> carriedWeapons,
        out int compatibleMagazineCount,
        out int compatibleMagazineAmmoCount,
        out int compatibleLooseAmmunitionCount)
    {
        compatibleMagazineCount = 0;
        compatibleMagazineAmmoCount = 0;
        compatibleLooseAmmunitionCount = 0;

        foreach (Item item in support)
        {
            if (item is MagazineItemClass magazine
                && magazine.Count > 0
                && FitsAny(magazine, carriedWeapons))
            {
                compatibleMagazineCount++;
                compatibleMagazineAmmoCount += Math.Max(0, magazine.Count);
            }

            if (item is AmmoItemClass ammunition
                && ammunition.StackObjectsCount > 0
                && FitsAny(ammunition, carriedWeapons))
            {
                compatibleLooseAmmunitionCount += Math.Max(0, ammunition.StackObjectsCount);
            }
        }
    }

    private static bool FitsAny(MagazineItemClass magazine, IReadOnlyList<EftWeapon> weapons)
    {
        foreach (EftWeapon weapon in weapons)
        {
            if (Fits(magazine, weapon)) return true;
        }
        return false;
    }

    private static bool FitsAny(AmmoItemClass ammunition, IReadOnlyList<EftWeapon> weapons)
    {
        foreach (EftWeapon weapon in weapons)
        {
            if (Fits(ammunition, weapon)) return true;
        }
        return false;
    }

    private static void ObserveMagazineAmmunitionFacts(
        MagazineItemClass? magazine,
        ref int bestPenetration,
        ref int bestDamage)
    {
        if (magazine?.Cartridges == null)
        {
            return;
        }

        foreach (Item item in magazine.Cartridges.Items)
        {
            ObserveAmmunitionFacts(item as AmmoItemClass, ref bestPenetration, ref bestDamage);
        }
    }

    private static void ObserveAmmunitionFacts(
        AmmoItemClass? ammunition,
        ref int bestPenetration,
        ref int bestDamage)
    {
        if (ammunition == null || ammunition.StackObjectsCount <= 0)
        {
            return;
        }

        // These are independent maxima. They may come from different observed rounds and must never
        // be interpreted downstream as a penetration/damage pair belonging to one ammunition type.
        bestPenetration = Math.Max(bestPenetration, ammunition.PenetrationPower);
        bestDamage = Math.Max(bestDamage, ammunition.Damage);
    }

    private static bool Fits(MagazineItemClass magazine, EftWeapon weapon)
    {
        try
        {
            return magazine.Count > 0 && weapon.GetMagazineSlot()?.CheckCompatibility(magazine) == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Fits(AmmoItemClass ammunition, EftWeapon weapon)
        => ammunition.StackObjectsCount > 0
            && !string.IsNullOrWhiteSpace(ammunition.Caliber)
            && string.Equals(ammunition.Caliber, weapon.AmmoCaliber, StringComparison.OrdinalIgnoreCase);
}

internal sealed class VanguardLiveEquipmentCapabilitySnapshot
{
    public static readonly VanguardLiveEquipmentCapabilitySnapshot Empty = new();

    public bool Observed { get; init; }
    public string ObservationReason { get; init; } = "unobserved";
    public int WeaponCount { get; init; } = -1;
    public int LongWeaponCount { get; init; } = -1;
    public bool SidearmObserved { get; init; }
    // Aggregate carried-material facts use the same support inventory capture as the per-weapon facts.
    // Each magazine/ammo stack is counted at most once when it is compatible with any carried weapon,
    // preserving the former loot-need observation semantics without a second inventory traversal.
    public int CompatibleMagazineCount { get; init; } = -1;
    public int CompatibleMagazineAmmoCount { get; init; } = -1;
    public int CompatibleLooseAmmunitionCount { get; init; } = -1;
    public int CompatibleAmmunitionCount => CompatibleMagazineAmmoCount < 0 || CompatibleLooseAmmunitionCount < 0
        ? -1
        : CompatibleMagazineAmmoCount + CompatibleLooseAmmunitionCount;
    public int WeaponsWithRoundsCount { get; init; } = -1;
    public int WeaponsWithoutRoundsCount { get; init; } = -1;
    public int BrickedWeaponCount { get; init; } = -1;
    public IReadOnlyList<VanguardLiveWeaponCapabilityObservation> Weapons { get; init; } = Array.Empty<VanguardLiveWeaponCapabilityObservation>();

    public bool EquipmentFitClassificationApplied => false;
    public bool AmmunitionThresholdClassificationApplied => false;
    public bool LootTotalScoreConsumedByTc => false;
}

internal sealed class VanguardLiveWeaponCapabilityObservation
{
    public string Slot { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string TemplateId { get; init; } = string.Empty;
    public string Caliber { get; init; } = string.Empty;
    // Canonical EFT weapon taxonomy from Weapon.WeapClass / WeaponTemplate.weapClass.
    // Raw provenance only: Vanguard does not map this value to contribution fit here.
    public string WeaponClass { get; init; } = "unknown";
    // Stable Vanguard normalization of the complete known SPT EFT weapon-class set.
    // This remains descriptive evidence; it does not assign contribution fit or weapon preference.
    public string CanonicalWeaponFamily { get; init; } = "UNKNOWN";
    public string WeaponFamilyQualification { get; init; } = "UNKNOWN_CLASS";
    // Mounted sight provenance is derived from the weapon's visible EFT item tree.
    // MagnifiedSightCount uses EFT's own >1x distinction from SightsItemClass; no Vanguard
    // "high optic" threshold or contribution-fit ranking is applied here.
    public int MountedSightCount { get; init; }
    public int MagnifiedSightCount { get; init; }
    public float MaxSupportedSightZoom { get; init; }
    public string SightCapabilityState { get; init; } = "UNKNOWN";
    public bool IsLongWeapon { get; init; }
    public int ReadyRounds { get; init; }
    public int ReserveRounds { get; init; }
    public int TotalObservedRounds { get; init; }
    public int CompatibleMagazineCount { get; init; }
    public int CompatibleMagazineAmmoCount { get; init; }
    public int CompatibleLooseAmmunitionCount { get; init; }
    public int CompatibleMagazineCapacityKnownCount { get; init; }
    public int CompatibleFullMagazineCount { get; init; }
    public int CompatiblePartialMagazineCount { get; init; }
    public double CompatibleMagazineReloadDepthEquivalent { get; init; }
    public float DurabilityFraction { get; init; }
    public int BestPenetration { get; init; }
    public int BestDamage { get; init; }
    public float Ergonomics { get; init; }
    public float Recoil { get; init; }
    public float SightingRange { get; init; }
    // Raw EFT WeaponTemplate.bEffDist / EffectiveDist attribute; not a Vanguard effective-range classification.
    public int EffectiveDistanceMeters { get; init; } = -1;
    public bool Bricked { get; init; }
    public string RawAmmunitionPresence { get; init; } = "UNKNOWN";

    public bool EquipmentFitClassificationApplied => false;
    public bool AmmunitionThresholdClassificationApplied => false;
}
#endif

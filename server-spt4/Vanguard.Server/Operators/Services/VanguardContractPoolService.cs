using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Storage;
using Vanguard.Server.Diagnostics;

// Responsibility: Owns generation and refresh of the dynamic Operator contract market, including unique identity reservation, archetype selection, pricing and compatibility with historical contacts.
// Flow: The service resolves the storage owner, reconciles existing/historical offers, reserves names/callsigns globally, deterministically generates missing offers from the seven Operator archetypes, then persists the resulting pool.
// Authority boundary: The Operator store is persistence authority; this service creates contract-domain data only and does not spawn bots or control in-raid behavior.
// Invariant: Identity collisions are rejected, active Operators are never re-offered, compatible legacy history remains readable, and generation stays deterministic enough for stable reconciliation.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardContractPoolService(
    VanguardOperatorStore store,
    VanguardEftExperienceCurveService experienceCurve,
    ISptLogger<VanguardContractPoolService> logger)
{
    private const string RoubleTemplateId = "5449016a4bdc2d6f028b456f";
    private static readonly string[] UsecFirstNames =
    [
        "Aaron", "Adam", "Adrian", "Alex", "Andrew", "Austin", "Blake", "Brandon", "Caleb", "Cameron", "Chris", "Cole",
        "Connor", "Daniel", "Derek", "Dylan", "Eric", "Ethan", "Evan", "Garrett", "Grant", "Hunter", "Ian", "Jack",
        "Jake", "James", "Jared", "Jason", "Joel", "Jordan", "Kyle", "Liam", "Logan", "Luke", "Marcus", "Mason",
        "Nathan", "Neil", "Nolan", "Owen", "Patrick", "Reid", "Ryan", "Scott", "Sean", "Seth", "Shane", "Trevor",
        "Tyler", "Wesley", "Wyatt", "Zachary", "Miles", "Gavin", "Dean", "Travis", "Colin", "Elliot", "Spencer", "Warren"
    ];
    private static readonly string[] UsecLastNames =
    [
        "Abbott", "Archer", "Baker", "Barrett", "Bishop", "Brooks", "Burke", "Carter", "Coleman", "Cross", "Dalton", "Dawson",
        "Drake", "Foster", "Graves", "Griffin", "Hale", "Hayes", "Holden", "Kane", "Keller", "Knight", "Lang", "Lawson",
        "Marlow", "Marshall", "Mercer", "Miller", "Mitchell", "Nash", "Palmer", "Parker", "Pierce", "Reed", "Rhodes", "Riley",
        "Sawyer", "Shaw", "Sloan", "Stone", "Sullivan", "Tanner", "Vance", "Walker", "Ward", "Webb", "West", "Wolfe",
        "Bennett", "Carver", "Donovan", "Ellis", "Fletcher", "Harris", "Maddox", "Morgan", "Porter", "Ramsey", "Rowe", "Turner"
    ];
    private static readonly string[] BearFirstNames =
    [
        "Aleksandr", "Aleksei", "Anatoly", "Andrei", "Anton", "Arkady", "Arseny", "Artyom", "Boris", "Daniil", "Denis", "Dmitri",
        "Eduard", "Evgeny", "Fyodor", "Gennady", "Georgy", "Gleb", "Grigory", "Ilya", "Ivan", "Kirill", "Konstantin", "Leonid",
        "Maksim", "Matvey", "Mikhail", "Nikolai", "Nikita", "Oleg", "Pavel", "Pyotr", "Roman", "Ruslan", "Sergei", "Stanislav",
        "Stepan", "Timofey", "Vadim", "Valentin", "Valery", "Vasily", "Viktor", "Vladislav", "Vladimir", "Vsevolod", "Yaroslav", "Yegor",
        "Yuri", "Zakhar", "Semyon", "Rodion", "Igor", "Lev", "Makar", "Miron", "Savely", "Taras", "Vitaly", "Vyacheslav"
    ];
    private static readonly string[] BearLastNames =
    [
        "Antonov", "Baranov", "Belov", "Belyaev", "Bogdanov", "Bondarenko", "Bykov", "Chernov", "Denisov", "Fedorov", "Filatov", "Frolov",
        "Gavrilov", "Golubev", "Gromov", "Grishin", "Gusev", "Ilyin", "Isaev", "Ivanov", "Kalinin", "Karpov", "Kazakov", "Kiselev",
        "Klimov", "Kolesnikov", "Komarov", "Kozlov", "Krylov", "Kudryavtsev", "Kuzmin", "Kuznetsov", "Lebedev", "Loginov", "Makarov", "Markov",
        "Maslov", "Medvedev", "Melnikov", "Mikhailov", "Morozov", "Nazarov", "Nikolaev", "Novikov", "Orlov", "Pavlov", "Petrov", "Polyakov",
        "Popov", "Romanov", "Semenov", "Sergeev", "Sidorov", "Smirnov", "Sokolov", "Sorokin", "Tarasov", "Titov", "Volkov", "Voronin",
        "Yakovlev", "Zaitsev", "Zhukov", "Zorin"
    ];
    private static readonly string[] Callsigns =
    [
        "Anchor", "Anvil", "Atlas", "Badger", "Bastion", "Beacon", "Bishop", "Bolt", "Breaker", "Briar", "Brim", "Cairn",
        "Cipher", "Cobalt", "Condor", "Coyote", "Crane", "Crow", "Dagger", "Delta", "Drift", "Echo", "Falcon", "Flint",
        "Forge", "Fox", "Frost", "Gale", "Ghost", "Grit", "Harrier", "Havoc", "Hawk", "Helix", "Hound", "Ibis",
        "Jackal", "Karat", "Kestrel", "Kodiak", "Lancer", "Lynx", "Mallet", "Mantis", "Mastiff", "Mica", "Nomad", "Onyx",
        "Orion", "Otter", "Palisade", "Pike", "Raven", "Razor", "Relay", "Rook", "Sable", "Scout", "Shade", "Slate",
        "Spear", "Stitch", "Stone", "Strix", "Talon", "Tango", "Thorn", "Titan", "Trace", "Vega", "Vector", "Viper",
        "Vostok", "Warden", "Wolf", "Wraith", "Yukon", "Zenith", "Aegis", "Arrow", "Ash", "Boreal", "Bronco", "Cedar",
        "Cliff", "Comet", "Crest", "Ember", "Fathom", "Gannet", "Hearth", "Keel", "Lumen", "Morrow", "North", "Quill",
        "Ridge", "Rivet", "Sierra", "Tern", "Timber", "Vale", "Whisper", "Zephyr"
    ];

    private static readonly VanguardArchetype[] Archetypes =
    [
        new("Assault", "Pointman", "Disciplined", "fire_discipline_and_squad_cohesion", "methodical", "USEC Heavy Contractor", "vanguard.sain.disciplined", "controlled_push", "short_medium", "rifleman", ["disciplined", "formation_aware", "controlled_fire"]),
        new("Recon", "Observation", "Recon", "observe_report_reposition", "patient", "USEC Recon", "vanguard.sain.recon", "recon_overwatch", "medium_long", "scout", ["observant", "low_profile", "patient"]),
        new("Support", "Sustainment", "Support", "support_preserve_formation", "steady", "Vanguard Logistics", "vanguard.sain.support", "support_fire_and_resupply", "medium", "support", ["cohesive", "steady", "supply_minded"]),
        new("Veteran", "Survival", "Veteran", "survive_hold_angle_extract", "hardened", "Veteran Tactical", "vanguard.sain.veteran", "angle_holder", "medium", "veteran", ["hardened", "survivor", "decisive"]),
        new("Marksman", "Precision overwatch", "Marksman", "hold_distance_prioritize_targets", "calm", "BEAR Forest", "vanguard.sain.marksman", "precision_overwatch", "long", "marksman", ["calm", "selective_fire", "distance_keeper"]),
        new("Breacher", "Room entry", "Aggressive", "breach_fast_secure_short_angles", "bold", "BEAR Assault", "vanguard.sain.breacher", "close_quarters_pressure", "short", "breacher", ["decisive", "close_quarters", "high_pressure"]),
        new("Medic", "Tactical recovery", "Protector", "stabilize_squad_preserve_lives", "careful", "Vanguard Logistics", "vanguard.sain.protector", "defensive_recovery", "medium", "medic", ["protective", "deliberate", "recovery_minded"]),
    ];

    private readonly SemaphoreSlim identityReservationGate = new(1, 1);
    private readonly HashSet<string> migrationStatusLoggedOwners = new(StringComparer.OrdinalIgnoreCase);
    private string lastIdentityRegistryStatusFingerprint = string.Empty;

    public async Task<IReadOnlyList<VanguardOperatorContractOffer>> EnsureContractPoolAsync(string profileId, int playerLevel)
    {
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        var existing = await store.LoadContractsAsync(storageProfileId);
        var activeService = await store.LoadActiveServiceAsync(storageProfileId);
        var operators = await store.LoadOperatorsAsync(storageProfileId);
        LogMigrationStatusOnce(storageProfileId, operators);
        var contacts = await store.LoadContactsAsync(storageProfileId);
        var now = DateTimeOffset.UtcNow;
        var normalizedLevel = Math.Max(playerLevel, 1);
        var expectedCount = GetOfferCount(normalizedLevel);

        await identityReservationGate.WaitAsync();
        try
        {
            var registry = await LoadAndReconcileIdentityRegistryAsync(now);
            var usedCallsigns = registry.Select(entry => entry.Callsign).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usedLegalNames = registry.Select(entry => BuildLegalName(entry.FirstName, entry.LastName)).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var reservedOperatorIds = activeService.Select(record => record.OperatorId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var historical = BuildHistoricalContactOffers(operators, contacts, reservedOperatorIds, now, normalizedLevel).ToList();
            var historicalIds = historical.Select(offer => offer.OperatorId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var active = existing
                .Where(offer => offer.AvailableUntilUtc > now && !reservedOperatorIds.Contains(offer.OperatorId) && !historicalIds.Contains(offer.OperatorId))
                .OrderByDescending(offer => offer.Rarity, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(offer => offer.Level)
                .ThenBy(offer => offer.AvailableUntilUtc)
                .Take(Math.Max(0, expectedCount - historical.Count))
                .ToList();

            var combined = historical.Concat(active).Take(expectedCount).ToArray();
            if (combined.Length >= Math.Min(3, expectedCount))
            {
                bool registryChanged = UpsertPermanentReservations(
                    registry,
                    storageProfileId,
                    historical.Select(offer => new IdentityCandidate(offer.OperatorId, offer.FirstName, offer.LastName, offer.Callsign, offer.DisplayName, offer.Side)),
                    now,
                    "historical_operator_contact");
                registryChanged |= UpsertOfferReservations(registry, storageProfileId, active, now, "active_contract_offer");
                if (registryChanged) await store.SaveIdentityRegistryAsync(registry);
                if (combined.Length != existing.Count) await store.SaveContractsAsync(storageProfileId, combined);
                return combined;
            }

            var dynamicCount = Math.Max(0, expectedCount - historical.Count);
            var generated = historical.Concat(GenerateContracts(
                    storageProfileId,
                    normalizedLevel,
                    now,
                    dynamicCount,
                    reservedOperatorIds,
                    usedLegalNames,
                    usedCallsigns))
                .Take(expectedCount)
                .ToArray();

            UpsertPermanentReservations(
                registry,
                storageProfileId,
                historical.Select(offer => new IdentityCandidate(offer.OperatorId, offer.FirstName, offer.LastName, offer.Callsign, offer.DisplayName, offer.Side)),
                now,
                "historical_operator_contact");
            UpsertOfferReservations(registry, storageProfileId, generated.Where(offer => string.Equals(offer.MarketStatus, "dynamic_contract", StringComparison.OrdinalIgnoreCase)), now, "contract_offer_visible");
            await store.SaveIdentityRegistryAsync(registry);
            await store.SaveContractsAsync(storageProfileId, generated);
            return generated;
        }
        finally
        {
            identityReservationGate.Release();
        }
    }

    private async Task<List<VanguardOperatorIdentityReservation>> LoadAndReconcileIdentityRegistryAsync(DateTimeOffset now)
    {
        var registry = (await store.LoadIdentityRegistryAsync()).ToList();
        int expiredRemoved = registry.RemoveAll(entry => !entry.IsPermanent && entry.ExpiresAtUtc is DateTimeOffset expires && expires <= now);
        bool changed = expiredRemoved > 0;

        foreach (string knownProfileId in store.GetKnownProfileIds())
        {
            var knownOperators = await store.LoadOperatorsAsync(knownProfileId);
            LogMigrationStatusOnce(knownProfileId, knownOperators);
            changed |= UpsertPermanentReservations(registry, knownProfileId, knownOperators.Select(profile => new IdentityCandidate(
                profile.OperatorId,
                profile.Identity.FirstName,
                profile.Identity.LastName,
                profile.Identity.Callsign,
                profile.Identity.DisplayName,
                profile.Identity.Side)), now, "operator_profile");

            var knownContracts = (await store.LoadContractsAsync(knownProfileId))
                .Where(offer => offer.AvailableUntilUtc > now)
                .ToArray();
            changed |= UpsertOfferReservations(registry, knownProfileId, knownContracts, now, "active_contract_offer_seed");
        }

        if (changed)
        {
            await store.SaveIdentityRegistryAsync(registry);
        }

        LogIdentityRegistryStatus(registry, expiredRemoved, changed);
        return registry;
    }

    private void LogMigrationStatusOnce(string ownerProfileId, IReadOnlyList<VanguardOperatorProfile> operators)
    {
        if (!migrationStatusLoggedOwners.Add(ownerProfileId))
        {
            return;
        }

        int partial = operators.Count(profile => IsPartialLegacyHistory(profile.Career?.HistoryCompleteness));
        int complete = operators.Count(profile => string.Equals(profile.Career?.HistoryCompleteness, "complete_since_enrollment", StringComparison.OrdinalIgnoreCase));
        bool backupRequired = partial > 0;
        bool backupExists = store.HasProfileNormalizationBackup(ownerProfileId);
        string message = $"[VANGUARD_MIGRATION_STATUS] owner={ownerProfileId}; operators={operators.Count}; partialHistory={partial}; completeHistory={complete}; backupRequired={backupRequired.ToString().ToLowerInvariant()}; backupExists={backupExists.ToString().ToLowerInvariant()}; operatorSchema={VanguardOperatorSchema.CurrentVersion}; careerSchema={VanguardOperatorCareerSchema.CurrentVersion}; mutation=none; tag=VANGUARD_MIGRATION_STATUS";
        if (backupRequired && !backupExists)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present(message + "; action=observe_missing_legacy_backup"));
        }
        else
        {
            logger.Info(VanguardServerDiagnosticsLog.Present(message));
        }
    }

    private void LogIdentityRegistryStatus(IReadOnlyList<VanguardOperatorIdentityReservation> registry, int expiredRemoved, bool changed)
    {
        int permanent = registry.Count(entry => entry.IsPermanent);
        int temporary = registry.Count - permanent;
        int duplicateCallsignGroups = registry
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Callsign))
            .GroupBy(entry => entry.Callsign.Trim(), StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        int duplicateLegalNameGroups = registry
            .Select(entry => BuildLegalName(entry.FirstName, entry.LastName))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        int owners = registry.Select(entry => entry.OwnerProfileId).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string fingerprint = $"{registry.Count}|{permanent}|{temporary}|{duplicateCallsignGroups}|{duplicateLegalNameGroups}|{owners}|{expiredRemoved}";
        if (string.Equals(lastIdentityRegistryStatusFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        lastIdentityRegistryStatusFingerprint = fingerprint;
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_IDENTITY_REGISTRY_STATUS] entries={registry.Count}; permanent={permanent}; temporary={temporary}; owners={owners}; expiredRemoved={expiredRemoved}; legacyDuplicateCallsignGroups={duplicateCallsignGroups}; legacyDuplicateLegalNameGroups={duplicateLegalNameGroups}; changed={changed.ToString().ToLowerInvariant()}; newGenerationPolicy=prevent_exact_callsign_and_legal_name_collisions; tag=VANGUARD_IDENTITY_REGISTRY_STATUS"));
    }

    private static bool UpsertOfferReservations(
        List<VanguardOperatorIdentityReservation> registry,
        string ownerProfileId,
        IEnumerable<VanguardOperatorContractOffer> offers,
        DateTimeOffset now,
        string source)
    {
        bool changed = false;
        foreach (var offer in offers)
        {
            var candidate = new IdentityCandidate(offer.OperatorId, offer.FirstName, offer.LastName, offer.Callsign, offer.DisplayName, offer.Side);
            DateTimeOffset expiresAtUtc = offer.AvailableUntilUtc.AddDays(7);
            changed |= UpsertReservation(registry, ownerProfileId, candidate, now, source, isPermanent: false, expiresAtUtc);
        }
        return changed;
    }

    private static bool UpsertPermanentReservations(
        List<VanguardOperatorIdentityReservation> registry,
        string ownerProfileId,
        IEnumerable<IdentityCandidate> candidates,
        DateTimeOffset now,
        string source)
    {
        bool changed = false;
        foreach (var candidate in candidates)
        {
            changed |= UpsertReservation(registry, ownerProfileId, candidate, now, source, isPermanent: true, expiresAtUtc: null);
        }
        return changed;
    }

    private static bool UpsertReservation(
        List<VanguardOperatorIdentityReservation> registry,
        string ownerProfileId,
        IdentityCandidate candidate,
        DateTimeOffset now,
        string source,
        bool isPermanent,
        DateTimeOffset? expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(candidate.OperatorId)) return false;
        int index = registry.FindIndex(entry => string.Equals(entry.OperatorId, candidate.OperatorId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var current = registry[index];
            bool effectivePermanent = current.IsPermanent || isPermanent;
            DateTimeOffset? effectiveExpiry = effectivePermanent
                ? null
                : MaxExpiry(current.ExpiresAtUtc, expiresAtUtc);
            string effectiveSource = isPermanent ? source : current.Source;
            bool metadataChanged = !string.Equals(current.OwnerProfileId, ownerProfileId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.FirstName, candidate.FirstName, StringComparison.Ordinal)
                || !string.Equals(current.LastName, candidate.LastName, StringComparison.Ordinal)
                || !string.Equals(current.Callsign, candidate.Callsign, StringComparison.Ordinal)
                || !string.Equals(current.DisplayName, candidate.DisplayName, StringComparison.Ordinal)
                || !string.Equals(current.Side, candidate.Side, StringComparison.OrdinalIgnoreCase)
                || current.IsPermanent != effectivePermanent
                || current.ExpiresAtUtc != effectiveExpiry
                || !string.Equals(current.Source, effectiveSource, StringComparison.Ordinal);
            bool refreshLastSeen = now - current.LastSeenAtUtc >= TimeSpan.FromHours(12);
            if (!metadataChanged && !refreshLastSeen) return false;

            registry[index] = current with
            {
                OwnerProfileId = ownerProfileId,
                FirstName = candidate.FirstName,
                LastName = candidate.LastName,
                Callsign = candidate.Callsign,
                DisplayName = candidate.DisplayName,
                Side = candidate.Side,
                Source = effectiveSource,
                LastSeenAtUtc = now,
                IsPermanent = effectivePermanent,
                ExpiresAtUtc = effectiveExpiry,
            };
            return true;
        }

        registry.Add(new VanguardOperatorIdentityReservation(
            candidate.OperatorId,
            ownerProfileId,
            candidate.FirstName,
            candidate.LastName,
            candidate.Callsign,
            candidate.DisplayName,
            candidate.Side,
            source,
            now,
            now,
            isPermanent,
            isPermanent ? null : expiresAtUtc));
        return true;
    }

    private static DateTimeOffset? MaxExpiry(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value >= right.Value ? left : right;
    }

    private static IEnumerable<VanguardOperatorContractOffer> BuildHistoricalContactOffers(
        IReadOnlyList<VanguardOperatorProfile> operators,
        IReadOnlyList<VanguardOperatorContactRecord> contacts,
        HashSet<string> reservedOperatorIds,
        DateTimeOffset now,
        int playerLevel)
    {
        var contactsById = contacts.ToDictionary(contact => contact.OperatorId, StringComparer.OrdinalIgnoreCase);
        foreach (var profile in operators.OrderByDescending(operatorProfile => contactsById.TryGetValue(operatorProfile.OperatorId, out var contact) ? contact.Trust + contact.Loyalty + contact.Respect - contact.Grudge : 0))
        {
            if (reservedOperatorIds.Contains(profile.OperatorId) || string.Equals(profile.ContractStatus, VanguardOperatorContractStatuses.Contracted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            contactsById.TryGetValue(profile.OperatorId, out var contactRecord);
            var relationshipSummary = contactRecord?.NarrativeSummary ?? "Known Vanguard contact available for rehire.";
            yield return new VanguardOperatorContractOffer(
                $"vanguard-contact-offer-{profile.OperatorId}",
                profile.OperatorId,
                profile.Identity.DisplayName,
                profile.Identity.FirstName,
                profile.Identity.LastName,
                profile.Identity.Callsign,
                profile.Identity.Side,
                profile.Role,
                profile.Specialty,
                profile.Progression.Level,
                profile.Progression.Experience,
                RoundTo500(Math.Max(5000, profile.HirePrice / 3 + Math.Max(0, (100 - profile.Progression.Loyalty) * 125))),
                profile.SalaryPerRaid,
                profile.CurrencyTpl,
                "contact",
                profile.Identity.VisualFamily,
                profile.Persona.BasePersona,
                profile.Persona.Doctrine,
                profile.Persona.Temperament,
                profile.Persona.SainProfileFamily,
                profile.Persona.SainTuningPlan,
                profile.Persona.Traits,
                now,
                now.AddHours(12),
                $"vanguard-contact-pool-L{Math.Max(playerLevel, 1)}",
                Math.Max(playerLevel, 1),
                VanguardOperatorSchema.CurrentVersion,
                profile.Persona.CombatStyle,
                profile.Persona.EngagementRange,
                profile.Persona.SquadRole,
                profile.Persona.BehaviorSummary,
                true,
                "historical_contact",
                relationshipSummary);
        }
    }

    public static int GetOfferCount(int playerLevel)
    {
        var level = Math.Max(playerLevel, 1);
        return level switch
        {
            <= 14 => 4,
            <= 19 => 5,
            <= 29 => 6,
            <= 39 => 7,
            _ => 8,
        };
    }

    private IEnumerable<VanguardOperatorContractOffer> GenerateContracts(
        string profileId,
        int playerLevel,
        DateTimeOffset now,
        int count,
        HashSet<string> reservedOperatorIds,
        HashSet<string> usedLegalNames,
        HashSet<string> usedCallsigns)
    {
        var poolSequence = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var seed = BuildStablePositiveSeed($"{profileId}|{playerLevel}|{poolSequence}|vanguard-offraid-reference-port");
        var random = new Random(seed);
        var poolId = $"vanguard-contract-pool-{poolSequence}-L{playerLevel}";
        var emitted = 0;
        var guard = 0;

        while (emitted < count && guard++ < 1000)
        {
            var side = random.NextDouble() >= 0.48 ? "Usec" : "Bear";
            var first = Pick(random, side == "Usec" ? UsecFirstNames : BearFirstNames);
            var last = Pick(random, side == "Usec" ? UsecLastNames : BearLastNames);
            var legalName = BuildLegalName(first, last);
            if (!usedLegalNames.Add(legalName))
            {
                continue;
            }

            var callsignFallback = $"Echo-{BuildStableIdSuffix($"{profileId}|{poolSequence}|{emitted}|callsign")}";
            var callsign = PickUnique(random, Callsigns, usedCallsigns, callsignFallback);
            var display = $"{callsign} {last}";

            var archetype = Pick(random, Archetypes);
            var level = GenerateOperatorLevel(random, playerLevel);
            var rarity = ResolveRarity(random, level, playerLevel);
            var hirePrice = ResolveHirePrice(random, level, rarity, archetype.Role);
            var salary = ResolveSalary(random, level, rarity, archetype.Role);
            var stableIdSuffix = BuildStableIdSuffix($"{profileId}|{poolSequence}|{emitted}|{display}|{level}|{archetype.Role}");
            var operatorId = $"vanguard-operator-{poolSequence}-{stableIdSuffix}";
            if (reservedOperatorIds.Contains(operatorId))
            {
                continue;
            }

            var offerId = $"vanguard-offer-{poolSequence}-{stableIdSuffix}";
            yield return new VanguardOperatorContractOffer(
                offerId,
                operatorId,
                display,
                first,
                last,
                callsign,
                side,
                archetype.Role,
                archetype.Specialty,
                level,
                experienceCurve.CreateExperienceForLevel(random, level),
                hirePrice,
                salary,
                RoubleTemplateId,
                rarity,
                ResolveVisualFamily(side, archetype.VisualFamily),
                archetype.BasePersona,
                archetype.Doctrine,
                archetype.Temperament,
                archetype.SainProfileFamily,
                $"vanguard.tuning.{archetype.BasePersona.ToLowerInvariant()}.{archetype.CombatStyle}",
                archetype.Traits,
                now,
                now.AddHours(18 + random.Next(0, 18)),
                poolId,
                playerLevel,
                VanguardOperatorSchema.CurrentVersion,
                archetype.CombatStyle,
                archetype.EngagementRange,
                archetype.SquadRole,
                BuildBehaviorSummary(archetype, level, rarity),
                true,
                "dynamic_contract",
                "New Vanguard market offer.");
            emitted++;
        }
    }

    private int GenerateOperatorLevel(Random random, int playerLevel)
    {
        int normalizedPlayerLevel = Math.Max(playerLevel, 1);

        int minimum = Math.Max(
            1,
            normalizedPlayerLevel - (normalizedPlayerLevel < 15 ? 2 : 5));

        int requestedMaximum =
            normalizedPlayerLevel + (normalizedPlayerLevel < 15 ? 4 : 8);

        if (random.NextDouble() < 0.12)
        {
            requestedMaximum += 5;
        }

        // The loaded EFT/SPT experience curve owns the representable level ceiling; never replace it with a numeric cap.
        VanguardOperatorExperienceWindow ceiling =
            experienceCurve.ResolveLevelWindow(requestedMaximum);

        int maximum = ceiling.IsAuthoritative
            ? ceiling.Level
            : requestedMaximum;

        // Keep malformed or out-of-range profile levels fail-safe without inventing a fallback ceiling.
        minimum = Math.Min(minimum, maximum);

        return random.Next(minimum, maximum + 1);
    }

    private static string ResolveRarity(Random random, int operatorLevel, int playerLevel)
    {
        var delta = operatorLevel - playerLevel;
        if (operatorLevel >= 40 || delta >= 8 || random.NextDouble() < 0.04) return "elite";
        if (operatorLevel >= 30 || delta >= 5 || random.NextDouble() < 0.12) return "veteran";
        if (operatorLevel >= 18 || delta >= 2 || random.NextDouble() < 0.25) return "experienced";
        return "standard";
    }

    private static int ResolveHirePrice(Random random, int level, string rarity, string role)
    {
        var multiplier = rarity switch { "elite" => 2.25, "veteran" => 1.65, "experienced" => 1.25, _ => 1.0 };
        var roleBonus = role is "Medic" or "Marksman" ? 9000 : role == "Breacher" ? 6000 : 0;
        return RoundTo500((int)((24000 + level * 3200 + random.Next(0, 15000) + roleBonus) * multiplier));
    }

    private static int ResolveSalary(Random random, int level, string rarity, string role)
    {
        var multiplier = rarity switch { "elite" => 1.85, "veteran" => 1.45, "experienced" => 1.18, _ => 1.0 };
        var roleBonus = role is "Medic" or "Marksman" ? 2400 : role == "Breacher" ? 1800 : 0;
        return RoundTo500((int)((8500 + level * 720 + random.Next(0, 6000) + roleBonus) * multiplier));
    }

    private static string ResolveVisualFamily(string side, string preferred) => preferred switch
    {
        "USEC Heavy Contractor" when side == "Bear" => "BEAR Assault",
        "USEC Recon" when side == "Bear" => "BEAR Forest",
        "BEAR Forest" when side == "Usec" => "USEC Recon",
        "BEAR Assault" when side == "Usec" => "USEC Heavy Contractor",
        _ => preferred,
    };

    private static string BuildBehaviorSummary(VanguardArchetype archetype, int level, string rarity)
    {
        return $"{rarity} {archetype.Role}; style={archetype.CombatStyle}; range={archetype.EngagementRange}; squadRole={archetype.SquadRole}; level={level}. SAIN projection prepared; runtime binding occurs when the Operator enters a raid.";
    }

    private static int RoundTo500(int value) => Math.Max(0, (int)Math.Round(value / 500.0, MidpointRounding.AwayFromZero) * 500);

    private static int BuildStablePositiveSeed(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
    }

    private static string BuildStableIdSuffix(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    private static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

    private static string PickUnique(Random random, IReadOnlyList<string> values, HashSet<string> used, string fallback)
    {
        for (var attempt = 0; attempt < Math.Max(20, values.Count * 2); attempt++)
        {
            var value = Pick(random, values);
            if (used.Add(value))
            {
                return value;
            }
        }

        string candidate = fallback;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{fallback}-{suffix++}";
        }
        return candidate;
    }

    private static string BuildLegalName(string? firstName, string? lastName) =>
        $"{firstName?.Trim()}|{lastName?.Trim()}".Trim('|');

    private sealed record IdentityCandidate(string OperatorId, string FirstName, string LastName, string Callsign, string DisplayName, string Side);

    private sealed record VanguardArchetype(
        string Role,
        string Specialty,
        string BasePersona,
        string Doctrine,
        string Temperament,
        string VisualFamily,
        string SainProfileFamily,
        string CombatStyle,
        string EngagementRange,
        string SquadRole,
        IReadOnlyList<string> Traits);

    private static bool IsPartialLegacyHistory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim();
        return string.Equals(normalized, "partial_from_legacy_migration", StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith("partial_from_", StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith("_migration", StringComparison.OrdinalIgnoreCase));
    }
}

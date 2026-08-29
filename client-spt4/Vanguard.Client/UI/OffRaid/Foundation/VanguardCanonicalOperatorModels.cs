using System;
using System.Collections.Generic;
using System.Linq;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Builds the canonical client-side Operator projection consumed by Off-Raid UI from contracts, active service, medical, billing and persisted Operator data.
// Flow: Multiple API projections are indexed by stable Operator identity, merged with explicit precedence, normalized into one view per Operator and checked for identity/portrait/medical/billing integrity.
// Authority boundary: The model is a client presentation truth only; server persistence remains authoritative and missing sources are represented rather than invented.
// Invariant: An Operator has one stable canonical identity, merges are deterministic, and UI integrity checks surface missing/duplicate state instead of silently substituting unrelated data.
namespace Vanguard.Client.UI.OffRaid.Foundation;

internal sealed class VanguardCanonicalOperatorView
{
    public string OperatorId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = VanguardOperatorsLocalizationService.Get("general.unknown_operator");
    public string Side { get; init; } = "PMC";
    public string Role { get; init; } = "Operator";
    public string Specialty { get; init; } = string.Empty;
    public int Level { get; init; } = 1;
    public string VisualFamily { get; init; } = "vanguard_default";
    public string PortraitKey { get; init; } = string.Empty;
    public string PortraitSource { get; init; } = "Vanguard";
    public string Placeholder { get; init; } = "VG\n?";
    public string Persona { get; init; } = string.Empty;
    public string Doctrine { get; init; } = string.Empty;
    public string Temperament { get; init; } = string.Empty;
    public string CombatStyle { get; init; } = string.Empty;
    public string EngagementRange { get; init; } = string.Empty;
    public string SquadRole { get; init; } = string.Empty;
    public string SainProfileFamily { get; init; } = string.Empty;
    public string SainTuningPlan { get; init; } = string.Empty;
    public IReadOnlyList<string> Traits { get; init; } = Array.Empty<string>();
    public int SalaryPerRaid { get; init; }
    public int HirePrice { get; init; }
    public int RaidCount { get; init; }
    public int SurvivedRaidCount { get; init; }
    public int KillCount { get; init; }
    public int Trust { get; init; }
    public int Loyalty { get; init; }
    public int Respect { get; init; }

    public string RoleLabel => VanguardUiText.Role(Role, Specialty);
    public string FactionLabel => VanguardUiText.Faction(Side);
}

internal sealed class VanguardCanonicalOperatorState
{
    private readonly Dictionary<string, VanguardCanonicalOperatorView> byOperatorId;

    private VanguardCanonicalOperatorState(Dictionary<string, VanguardCanonicalOperatorView> byOperatorId)
    {
        this.byOperatorId = byOperatorId;
    }

    public IReadOnlyDictionary<string, VanguardCanonicalOperatorView> ByOperatorId => byOperatorId;

    public static VanguardCanonicalOperatorState Build(VanguardOperatorStateView state)
    {
        var map = new Dictionary<string, VanguardCanonicalOperatorView>(StringComparer.OrdinalIgnoreCase);

        foreach (VanguardOperatorProfileDto profile in state.Operators)
        {
            string operatorId = VanguardUiText.Safe(profile.OperatorId, profile.Identity?.OperatorId);
            if (operatorId.Length == 0)
            {
                continue;
            }

            map[operatorId] = FromProfile(profile);
        }

        foreach (VanguardOperatorServiceProjectionDto service in state.ServiceProjections)
        {
            string operatorId = VanguardUiText.Safe(service.OperatorId);
            if (operatorId.Length == 0)
            {
                continue;
            }

            if (map.TryGetValue(operatorId, out VanguardCanonicalOperatorView? existing))
            {
                map[operatorId] = Merge(existing, FromService(service));
            }
            else
            {
                map[operatorId] = FromService(service);
            }
        }

        foreach (VanguardOperatorRaidProjectionDto raid in state.RaidProjections)
        {
            string operatorId = VanguardUiText.Safe(raid.OperatorId);
            if (operatorId.Length == 0)
            {
                continue;
            }

            if (!map.ContainsKey(operatorId))
            {
                map[operatorId] = FromRaid(raid);
            }
        }

        return new VanguardCanonicalOperatorState(map);
    }

    public VanguardCanonicalOperatorView ResolveForContract(VanguardOperatorContractOfferDto offer)
    {
        string operatorId = VanguardUiText.Safe(offer.OperatorId, offer.OfferId);
        if (operatorId.Length > 0 && byOperatorId.TryGetValue(operatorId, out VanguardCanonicalOperatorView? existing))
        {
            return existing;
        }

        string displayName = VanguardUiText.Safe(offer.DisplayName, offer.Callsign, VanguardOperatorsLocalizationService.Get("general.unknown_operator"));
        string side = VanguardUiText.Safe(offer.Side, "PMC");
        string role = VanguardUiText.Safe(offer.Role, "Operator");
        string specialty = VanguardUiText.Safe(offer.Specialty);
        string visualFamily = VanguardUiText.Safe(offer.VisualFamily, BuildVisualFamily(side, role, specialty));
        string stableId = VanguardUiText.Safe(offer.OperatorId, offer.OfferId, displayName);
        return new VanguardCanonicalOperatorView
        {
            OperatorId = operatorId,
            DisplayName = displayName,
            Side = side,
            Role = role,
            Specialty = specialty,
            Level = offer.Level > 0 ? offer.Level : 1,
            VisualFamily = visualFamily,
            PortraitKey = BuildPortraitKey(stableId, side, role, visualFamily),
            Placeholder = BuildPlaceholder(displayName, side),
            Persona = VanguardUiText.Safe(offer.BasePersona, offer.Temperament),
            Doctrine = VanguardUiText.Safe(offer.Doctrine),
            Temperament = VanguardUiText.Safe(offer.Temperament),
            CombatStyle = VanguardUiText.Safe(offer.CombatStyle),
            EngagementRange = VanguardUiText.Safe(offer.EngagementRange),
            SquadRole = VanguardUiText.Safe(offer.SquadRole),
            SainProfileFamily = VanguardUiText.Safe(offer.SainProfileFamily),
            SainTuningPlan = VanguardUiText.Safe(offer.SainTuningPlan),
            Traits = offer.Traits ?? new List<string>(),
            SalaryPerRaid = offer.SalaryPerRaid,
            HirePrice = offer.HirePrice
        };
    }

    public VanguardCanonicalOperatorView ResolveForOperator(string? operatorId, string? displayName = null, string? side = null, string? role = null, string? specialty = null, int level = 0)
    {
        string key = VanguardUiText.Safe(operatorId);
        if (key.Length > 0 && byOperatorId.TryGetValue(key, out VanguardCanonicalOperatorView? existing))
        {
            return existing;
        }

        string name = VanguardUiText.Safe(displayName, operatorId, VanguardOperatorsLocalizationService.Get("general.unknown_operator"));
        string safeSide = VanguardUiText.Safe(side, "PMC");
        string safeRole = VanguardUiText.Safe(role, "Operator");
        string safeSpecialty = VanguardUiText.Safe(specialty);
        string visualFamily = BuildVisualFamily(safeSide, safeRole, safeSpecialty);
        return new VanguardCanonicalOperatorView
        {
            OperatorId = key,
            DisplayName = name,
            Side = safeSide,
            Role = safeRole,
            Specialty = safeSpecialty,
            Level = level > 0 ? level : 1,
            VisualFamily = visualFamily,
            PortraitKey = BuildPortraitKey(VanguardUiText.Safe(key, name), safeSide, safeRole, visualFamily),
            Placeholder = BuildPlaceholder(name, safeSide)
        };
    }

    public VanguardOffRaidIntegrityReport Analyze(VanguardOperatorStateView state)
    {
        int selectedForRaid = state.ServiceProjections.Count(projection => projection.IsSelectedForRaid);
        int duplicateOperatorIds = state.Operators
            .Where(profile => !string.IsNullOrWhiteSpace(profile.OperatorId))
            .GroupBy(profile => profile.OperatorId!, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        int missingPortraitKeys = byOperatorId.Values.Count(view => string.IsNullOrWhiteSpace(view.PortraitKey));
        int missingMedicalIdentity = state.MedicalProjections.Count(projection =>
            string.IsNullOrWhiteSpace(projection.OperatorId)
            || !byOperatorId.ContainsKey(projection.OperatorId!));
        int invalidBillingEntries = (state.Billing.OpenInvoices ?? new List<VanguardOperatorBillingInvoiceDto>())
            .Count(invoice => string.IsNullOrWhiteSpace(invoice.InvoiceId) || invoice.Amount <= 0);
        int invalidSelectedCount = selectedForRaid > state.Limits.MaxDeployableOperators ? selectedForRaid - state.Limits.MaxDeployableOperators : 0;

        return new VanguardOffRaidIntegrityReport
        {
            OperatorCount = state.Operators.Count,
            ContractCount = state.Contracts.Count,
            ActiveServiceCount = state.ServiceProjections.Count,
            MedicalProjectionCount = state.MedicalProjections.Count,
            SelectedForRaidCount = selectedForRaid,
            MaxDeployableOperators = state.Limits.MaxDeployableOperators,
            DuplicateOperatorIdCount = duplicateOperatorIds,
            MissingPortraitKeyCount = missingPortraitKeys,
            MissingMedicalIdentityCount = missingMedicalIdentity,
            InvalidBillingEntryCount = invalidBillingEntries,
            InvalidSelectedCount = invalidSelectedCount
        };
    }

    private static VanguardCanonicalOperatorView FromProfile(VanguardOperatorProfileDto profile)
    {
        string operatorId = VanguardUiText.Safe(profile.OperatorId, profile.Identity?.OperatorId);
        string displayName = VanguardUiText.Safe(profile.Identity?.DisplayName, profile.Identity?.Callsign, operatorId, VanguardOperatorsLocalizationService.Get("general.unknown_operator"));
        string side = VanguardUiText.Safe(profile.Identity?.Side, "PMC");
        string role = VanguardUiText.Safe(profile.Role, "Operator");
        string specialty = VanguardUiText.Safe(profile.Specialty);
        string visualFamily = VanguardUiText.Safe(profile.Identity?.VisualFamily, BuildVisualFamily(side, role, specialty));
        return new VanguardCanonicalOperatorView
        {
            OperatorId = operatorId,
            DisplayName = displayName,
            Side = side,
            Role = role,
            Specialty = specialty,
            Level = profile.Progression is not null && profile.Progression.Level > 0 ? profile.Progression.Level : 1,
            VisualFamily = visualFamily,
            PortraitKey = BuildPortraitKey(operatorId, side, role, visualFamily),
            Placeholder = BuildPlaceholder(displayName, side),
            Persona = VanguardUiText.Safe(profile.Persona?.BasePersona),
            Doctrine = VanguardUiText.Safe(profile.Persona?.Doctrine),
            Temperament = VanguardUiText.Safe(profile.Persona?.Temperament),
            CombatStyle = VanguardUiText.Safe(profile.Persona?.CombatStyle),
            EngagementRange = VanguardUiText.Safe(profile.Persona?.EngagementRange),
            SquadRole = VanguardUiText.Safe(profile.Persona?.SquadRole),
            SainProfileFamily = VanguardUiText.Safe(profile.Persona?.SainProfileFamily),
            SainTuningPlan = VanguardUiText.Safe(profile.Persona?.SainTuningPlan),
            Traits = profile.Persona?.Traits ?? new List<string>(),
            SalaryPerRaid = profile.SalaryPerRaid,
            HirePrice = profile.HirePrice,
            RaidCount = profile.Progression?.RaidCount ?? 0,
            SurvivedRaidCount = profile.Progression?.SurvivedRaidCount ?? 0,
            KillCount = profile.Progression?.KillCount ?? 0,
            Trust = profile.Progression?.Trust ?? 0,
            Loyalty = profile.Progression?.Loyalty ?? 0,
            Respect = profile.Progression?.Respect ?? 0
        };
    }

    private static VanguardCanonicalOperatorView FromService(VanguardOperatorServiceProjectionDto service)
    {
        string operatorId = VanguardUiText.Safe(service.OperatorId);
        string displayName = VanguardUiText.Safe(service.DisplayName, operatorId, VanguardOperatorsLocalizationService.Get("general.unknown_operator"));
        string side = VanguardUiText.Safe(service.Side, "PMC");
        string role = VanguardUiText.Safe(service.Role, "Operator");
        string specialty = VanguardUiText.Safe(service.Specialty);
        string visualFamily = VanguardUiText.Safe(service.VisualFamily, BuildVisualFamily(side, role, specialty));
        return new VanguardCanonicalOperatorView
        {
            OperatorId = operatorId,
            DisplayName = displayName,
            Side = side,
            Role = role,
            Specialty = specialty,
            Level = service.Level > 0 ? service.Level : 1,
            VisualFamily = visualFamily,
            PortraitKey = BuildPortraitKey(operatorId, side, role, visualFamily),
            Placeholder = BuildPlaceholder(displayName, side),
            Persona = VanguardUiText.Safe(service.PersonaKey),
            Doctrine = VanguardUiText.Safe(service.Doctrine),
            Temperament = VanguardUiText.Safe(service.Temperament),
            SainProfileFamily = VanguardUiText.Safe(service.SainProfileFamily),
            SainTuningPlan = VanguardUiText.Safe(service.SainTuningPlan),
            Traits = service.Traits ?? new List<string>(),
            SalaryPerRaid = service.SalaryPerRaid,
            RaidCount = service.RaidCount,
            SurvivedRaidCount = service.SurvivedRaidCount,
            KillCount = service.KillCount,
            Trust = service.Trust,
            Loyalty = service.Loyalty
        };
    }

    private static VanguardCanonicalOperatorView FromRaid(VanguardOperatorRaidProjectionDto raid)
    {
        string operatorId = VanguardUiText.Safe(raid.OperatorId);
        string displayName = VanguardUiText.Safe(raid.DisplayName, operatorId, VanguardOperatorsLocalizationService.Get("general.unknown_operator"));
        string side = VanguardUiText.Safe(raid.Side, "PMC");
        string role = VanguardUiText.Safe(raid.Role, "Operator");
        string specialty = VanguardUiText.Safe(raid.Specialty);
        string visualFamily = BuildVisualFamily(side, role, specialty);
        return new VanguardCanonicalOperatorView
        {
            OperatorId = operatorId,
            DisplayName = displayName,
            Side = side,
            Role = role,
            Specialty = specialty,
            Level = raid.Level > 0 ? raid.Level : 1,
            VisualFamily = visualFamily,
            PortraitKey = BuildPortraitKey(operatorId, side, role, visualFamily),
            Placeholder = BuildPlaceholder(displayName, side),
            Persona = VanguardUiText.Safe(raid.Persona),
            Traits = raid.Traits ?? new List<string>(),
            SainProfileFamily = VanguardUiText.Safe(raid.SainProfileFamily),
            SainTuningPlan = VanguardUiText.Safe(raid.SainTuningPlan)
        };
    }

    private static VanguardCanonicalOperatorView Merge(VanguardCanonicalOperatorView primary, VanguardCanonicalOperatorView fallback)
    {
        return new VanguardCanonicalOperatorView
        {
            OperatorId = VanguardUiText.Safe(primary.OperatorId, fallback.OperatorId),
            DisplayName = VanguardUiText.Safe(primary.DisplayName, fallback.DisplayName),
            Side = VanguardUiText.Safe(primary.Side, fallback.Side, "PMC"),
            Role = VanguardUiText.Safe(primary.Role, fallback.Role, "Operator"),
            Specialty = VanguardUiText.Safe(primary.Specialty, fallback.Specialty),
            Level = primary.Level > 0 ? primary.Level : fallback.Level,
            VisualFamily = VanguardUiText.Safe(primary.VisualFamily, fallback.VisualFamily),
            PortraitKey = VanguardUiText.Safe(primary.PortraitKey, fallback.PortraitKey),
            PortraitSource = VanguardUiText.Safe(primary.PortraitSource, fallback.PortraitSource, "Vanguard"),
            Placeholder = VanguardUiText.Safe(primary.Placeholder, fallback.Placeholder),
            Persona = VanguardUiText.Safe(primary.Persona, fallback.Persona),
            Doctrine = VanguardUiText.Safe(primary.Doctrine, fallback.Doctrine),
            Temperament = VanguardUiText.Safe(primary.Temperament, fallback.Temperament),
            CombatStyle = VanguardUiText.Safe(primary.CombatStyle, fallback.CombatStyle),
            EngagementRange = VanguardUiText.Safe(primary.EngagementRange, fallback.EngagementRange),
            SquadRole = VanguardUiText.Safe(primary.SquadRole, fallback.SquadRole),
            SainProfileFamily = VanguardUiText.Safe(primary.SainProfileFamily, fallback.SainProfileFamily),
            SainTuningPlan = VanguardUiText.Safe(primary.SainTuningPlan, fallback.SainTuningPlan),
            Traits = primary.Traits.Count > 0 ? primary.Traits : fallback.Traits,
            SalaryPerRaid = primary.SalaryPerRaid > 0 ? primary.SalaryPerRaid : fallback.SalaryPerRaid,
            HirePrice = primary.HirePrice > 0 ? primary.HirePrice : fallback.HirePrice,
            RaidCount = primary.RaidCount > 0 ? primary.RaidCount : fallback.RaidCount,
            SurvivedRaidCount = primary.SurvivedRaidCount > 0 ? primary.SurvivedRaidCount : fallback.SurvivedRaidCount,
            KillCount = primary.KillCount > 0 ? primary.KillCount : fallback.KillCount,
            Trust = primary.Trust != 0 ? primary.Trust : fallback.Trust,
            Loyalty = primary.Loyalty != 0 ? primary.Loyalty : fallback.Loyalty,
            Respect = primary.Respect != 0 ? primary.Respect : fallback.Respect
        };
    }

    private static string BuildVisualFamily(string side, string role, string specialty)
    {
        return $"{side}_{role}_{specialty}".Trim('_').Replace(' ', '_').ToLowerInvariant();
    }

    private static string BuildPortraitKey(string stableId, string side, string role, string visualFamily)
    {
        return $"operator:{VanguardUiText.Safe(stableId, "unknown")}|{side}|{role}|{visualFamily}";
    }

    private static string BuildPlaceholder(string displayName, string? side)
    {
        string prefix = VanguardUiText.Safe(side, "VG").ToUpperInvariant();
        string[] parts = displayName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        string initials = parts.Length == 0
            ? "?"
            : string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
        return $"{prefix}\n{initials}";
    }
}

internal sealed class VanguardOffRaidIntegrityReport
{
    public int OperatorCount { get; init; }
    public int ContractCount { get; init; }
    public int ActiveServiceCount { get; init; }
    public int MedicalProjectionCount { get; init; }
    public int SelectedForRaidCount { get; init; }
    public int MaxDeployableOperators { get; init; }
    public int DuplicateOperatorIdCount { get; init; }
    public int MissingPortraitKeyCount { get; init; }
    public int MissingMedicalIdentityCount { get; init; }
    public int InvalidBillingEntryCount { get; init; }
    public int InvalidSelectedCount { get; init; }

    public bool HasBlockingIssue => DuplicateOperatorIdCount > 0
        || MissingPortraitKeyCount > 0
        || MissingMedicalIdentityCount > 0
        || InvalidBillingEntryCount > 0
        || InvalidSelectedCount > 0;

    public string ToLogString()
    {
        return $"operators={OperatorCount}; contracts={ContractCount}; active={ActiveServiceCount}; medical={MedicalProjectionCount}; selected={SelectedForRaidCount}/{MaxDeployableOperators}; duplicateIds={DuplicateOperatorIdCount}; missingPortraitKeys={MissingPortraitKeyCount}; missingMedicalIdentity={MissingMedicalIdentityCount}; invalidBilling={InvalidBillingEntryCount}; invalidSelected={InvalidSelectedCount}; ok={!HasBlockingIssue}";
    }

    public string ToStatusSuffix()
    {
        int issueCount = DuplicateOperatorIdCount + MissingPortraitKeyCount + MissingMedicalIdentityCount + InvalidBillingEntryCount + InvalidSelectedCount;
        return HasBlockingIssue
            ? " · " + VanguardOperatorsLocalizationService.Format("general.integrity_warning", issueCount)
            : " · " + VanguardOperatorsLocalizationService.Get("general.integrity_ok");
    }
}

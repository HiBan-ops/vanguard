using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.UI.OffRaid.Foundation;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Presents and coordinates Contracts Panel in the Off-Raid Operator UI.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Panels;

internal sealed class VanguardContractsPanel
{
    public VanguardOffRaidPanelModel Build(
        VanguardOperatorStateView state,
        Action<VanguardOperatorContractOfferDto> hireContract)
    {
        var body = new StringBuilder();
        body.AppendLine(L("contracts.body.title"));
        body.AppendLine();

        var actions = new List<VanguardOffRaidPanelAction>();
        if (state.Contracts.Count == 0)
        {
            body.AppendLine(L("empty.contracts"));
        }
        else
        {
            foreach (VanguardOperatorContractOfferDto offer in state.Contracts.Take(6))
            {
                string displayName = Safe(offer.DisplayName, offer.Callsign, L("general.unknown_operator"));
                body.AppendLine($"• {displayName} · {VanguardUiText.Faction(offer.Side)} · {VanguardUiText.Role(offer.Role, offer.Specialty)}");
                body.AppendLine(F("contracts.body.offer_level", offer.Level, VanguardUiText.Value(offer.Rarity), VanguardUiText.Money(offer.HirePrice), VanguardUiText.Money(offer.SalaryPerRaid)));
                body.AppendLine(F("contracts.body.offer_persona", VanguardUiText.Value(offer.BasePersona), VanguardUiText.Value(offer.Temperament), VanguardUiText.Value(offer.VisualFamily)));
                body.AppendLine(F("contracts.body.offer_style", VanguardUiText.Value(offer.CombatStyle), VanguardUiText.Range(offer.EngagementRange), VanguardUiText.SquadRole(offer.SquadRole)));
                body.AppendLine(F("contracts.body.offer_traits", VanguardUiText.Traits(offer.Traits)));
                body.AppendLine();

                actions.Add(new VanguardOffRaidPanelAction
                {
                    Label = F("contracts.action.hire_named", displayName),
                    Hint = offer.OfferId,
                    Enabled = offer.CanHire && state.Operators.Count < state.Limits.MaxHiredOperators,
                    Execute = () => hireContract(offer)
                });
            }
        }

        if (state.Operators.Count >= state.Limits.MaxHiredOperators)
        {
            body.AppendLine(L("contracts.limit"));
        }

        return new VanguardOffRaidPanelModel
        {
            Title = L("contracts.title"),
            Subtitle = L("contracts.subtitle"),
            Body = body.ToString(),
            Actions = actions
        };
    }

    private static string Safe(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string L(string key) => VanguardOperatorsLocalizationService.Get(key);
    private static string F(string key, params object?[] args) => VanguardOperatorsLocalizationService.Format(key, args);
}

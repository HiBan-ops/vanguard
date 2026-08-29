using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.UI.OffRaid.Foundation;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Presents and coordinates Active Service Panel in the Off-Raid Operator UI.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Panels;

internal sealed class VanguardActiveServicePanel
{
    public VanguardOffRaidPanelModel Build(
        VanguardOperatorStateView state,
        Action<string?, string?, bool> setRaidSelection,
        Action<string?> openDossier)
    {
        var body = new StringBuilder();
        body.AppendLine(L("service.body.intro"));
        body.AppendLine(L("service.body.explain"));
        body.AppendLine();

        var actions = new List<VanguardOffRaidPanelAction>();
        if (state.ServiceProjections.Count == 0)
        {
            body.AppendLine(L("service.empty"));
        }
        else
        {
            foreach (VanguardOperatorServiceProjectionDto projection in state.ServiceProjections.Take(8))
            {
                string displayName = Safe(projection.DisplayName, L("general.unknown_operator"));
                string serviceState = projection.IsSelectedForRaid ? L("general.active") : L("general.rest");
                string eligible = string.Equals(projection.EligibilityState, "eligible", StringComparison.OrdinalIgnoreCase)
                    ? L("general.eligible")
                    : L("general.ineligible");
                body.AppendLine($"• {displayName} · {VanguardUiText.Faction(projection.Side)} · {L("label.level_short")}{projection.Level} · {VanguardUiText.Role(projection.Role, projection.Specialty)}");
                body.AppendLine(F("service.body.state", serviceState, eligible, VanguardUiText.Money(projection.SalaryPerRaid)));
                body.AppendLine(F("service.body.persona",
                    VanguardUiText.Value(projection.PersonaKey, projection.Temperament, L("general.undefined")),
                    VanguardUiText.Value(projection.Doctrine, L("general.undefined_fem"))));
                body.AppendLine(F("service.body.sain",
                    VanguardUiText.Value(projection.SainProfileFamily, L("general.none")),
                    VanguardUiText.Traits(projection.Traits)));
                body.AppendLine();

                actions.Add(new VanguardOffRaidPanelAction
                {
                    Label = projection.IsSelectedForRaid
                        ? F("service.action.rest_named", displayName)
                        : F("service.action.active_named", displayName),
                    Enabled = projection.IsSelectedForRaid || string.Equals(projection.EligibilityState, "eligible", StringComparison.OrdinalIgnoreCase),
                    Execute = () => setRaidSelection(projection.OperatorId, displayName, !projection.IsSelectedForRaid)
                });
                actions.Add(new VanguardOffRaidPanelAction
                {
                    Label = F("service.action.dossier_named", displayName),
                    Execute = () => openDossier(projection.OperatorId)
                });
            }
        }

        return new VanguardOffRaidPanelModel
        {
            Title = L("service.title"),
            Subtitle = L("service.subtitle"),
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

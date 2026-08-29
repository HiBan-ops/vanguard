using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.UI.OffRaid.Foundation;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Presents and coordinates Field Hospital Panel in the Off-Raid Operator UI.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Panels;

internal sealed class VanguardFieldHospitalPanel
{
    public VanguardOffRaidPanelModel Build(
        VanguardOperatorStateView state,
        Action<VanguardOperatorMedicalProjectionDto> treatOperator)
    {
        var body = new StringBuilder();
        body.AppendLine(L("hospital.body.intro"));
        body.AppendLine();

        var actions = new List<VanguardOffRaidPanelAction>();
        if (state.MedicalProjections.Count == 0)
        {
            body.AppendLine(L("hospital.empty"));
        }
        else
        {
            foreach (VanguardOperatorMedicalProjectionDto projection in state.MedicalProjections.Take(6))
            {
                string displayName = Safe(projection.DisplayName, L("general.unknown_operator"));
                int healthPercent = (int)Math.Round(projection.CurrentHealthRatio * 100.0);
                body.AppendLine($"• {displayName} · {L("label.level_short")}{projection.Level} · {VanguardUiText.Role(projection.Role)}");
                body.AppendLine(F("hospital.body.status",
                    VanguardUiText.Value(projection.MedicalStatus, L("general.undefined")),
                    healthPercent,
                    VanguardUiText.Value(projection.RecoveryState, L("general.none_fem"))));
                body.AppendLine(F("hospital.body.injury",
                    VanguardUiText.Value(projection.InjurySummary, L("general.no_details")),
                    VanguardUiText.Money(projection.HealCost),
                    VanguardUiText.Money(projection.RecoveryCost)));
                body.AppendLine();

                actions.Add(new VanguardOffRaidPanelAction
                {
                    Label = F("hospital.action.treat_named", displayName),
                    Enabled = projection.HealCost > 0 || projection.RecoveryCost > 0 || projection.CurrentHealthRatio < 0.999 || string.Equals(projection.RecoveryState, "recovering", StringComparison.OrdinalIgnoreCase),
                    Execute = () => treatOperator(projection)
                });
            }
        }

        return new VanguardOffRaidPanelModel
        {
            Title = L("hospital.title"),
            Subtitle = L("hospital.subtitle"),
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

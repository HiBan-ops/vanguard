using System;
using System.Collections.Generic;
using System.Text;
using Vanguard.Client.Api;
using Vanguard.Client.UI.OffRaid.Foundation;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Presents and coordinates Dashboard Panel in the Off-Raid Operator UI.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Panels;

internal sealed class VanguardDashboardPanel
{
    public VanguardOffRaidPanelModel Build(
        VanguardOperatorStateView state,
        Action showContracts,
        Action showActiveService,
        Action showFieldHospital,
        Action showBilling)
    {
        int selectedForRaid = state.RaidProjections.FindAll(projection => projection.IsSelectedForRaid).Count;
        int readyForRaid = state.RaidProjections.FindAll(projection => projection.IsEligibleForRaid).Count;
        int recovering = state.MedicalProjections.FindAll(projection => projection.RecoveryUntilUtc.HasValue && projection.RecoveryUntilUtc.Value > DateTimeOffset.UtcNow).Count;
        var body = new StringBuilder();
        body.AppendLine(L("dashboard.body.title"));
        body.AppendLine();
        body.AppendLine(F("dashboard.body.player_level", state.Limits.PlayerLevel, state.Limits.Tier));
        body.AppendLine(F("dashboard.body.contracts", state.Contracts.Count));
        body.AppendLine(F("dashboard.body.operators", state.Operators.Count, state.Limits.MaxHiredOperators));
        body.AppendLine(F("dashboard.body.active_service", state.ActiveService.Count));
        body.AppendLine(F("dashboard.body.raid_ready", readyForRaid));
        body.AppendLine(F("dashboard.body.raid_active", selectedForRaid, state.Limits.MaxDeployableOperators));
        body.AppendLine(F("dashboard.body.recovering", recovering));
        body.AppendLine(F("dashboard.body.debt", VanguardUiText.Money(state.Billing.OutstandingDebt)));
        body.AppendLine();
        body.AppendLine(L("dashboard.body.note"));

        return new VanguardOffRaidPanelModel
        {
            Title = L("dashboard.title"),
            Subtitle = L("dashboard.subtitle"),
            Body = body.ToString(),
            InfoSections = new List<VanguardInfoSectionModel>
            {
                new()
                {
                    Title = L("dashboard.section.summary"),
                    Rows = new List<VanguardInfoRowModel>
                    {
                        new() { Label = L("label.player_level"), Value = $"{state.Limits.PlayerLevel} ({state.Limits.Tier})" },
                        new() { Label = L("label.contracts_available"), Value = state.Contracts.Count.ToString() },
                        new() { Label = L("label.operators_hired"), Value = $"{state.Operators.Count} / {state.Limits.MaxHiredOperators}" },
                        new() { Label = L("label.active_service"), Value = state.ActiveService.Count.ToString() },
                        new() { Label = L("label.raid_ready"), Value = readyForRaid.ToString() },
                        new() { Label = L("label.raid_active"), Value = $"{selectedForRaid} / {state.Limits.MaxDeployableOperators}" },
                        new() { Label = L("label.recovering_operators"), Value = recovering.ToString() },
                        new() { Label = L("label.outstanding_debt"), Value = VanguardUiText.Money(state.Billing.OutstandingDebt) }
                    }
                }
            },
            Actions = new List<VanguardOffRaidPanelAction>
            {
                new() { Label = L("dashboard.contracts"), Execute = showContracts },
                new() { Label = L("dashboard.active"), Execute = showActiveService },
                new() { Label = L("dashboard.hospital"), Execute = showFieldHospital },
                new() { Label = L("dashboard.billing"), Execute = showBilling }
            }
        };
    }

    private static string L(string key) => VanguardOperatorsLocalizationService.Get(key);
    private static string F(string key, params object?[] args) => VanguardOperatorsLocalizationService.Format(key, args);
}

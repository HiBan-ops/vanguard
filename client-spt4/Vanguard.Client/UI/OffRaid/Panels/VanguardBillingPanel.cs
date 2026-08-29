using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.UI.OffRaid.Foundation;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Presents and coordinates Billing Panel in the Off-Raid Operator UI.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Panels;

internal sealed class VanguardBillingPanel
{
    public VanguardOffRaidPanelModel Build(
        VanguardOperatorStateView state,
        Action signOpenInvoices)
    {
        var billing = state.Billing;
        var openInvoices = billing.OpenInvoices ?? new List<VanguardOperatorBillingInvoiceDto>();
        var pendingInvoices = openInvoices
            .Where(invoice => HasStatus(invoice, "pending_signature") || string.IsNullOrWhiteSpace(invoice.Status))
            .ToList();
        var signedPendingInvoices = openInvoices
            .Where(invoice => HasStatus(invoice, "signed_pending_settlement") || HasStatus(invoice, "signed"))
            .ToList();
        var recentPaidInvoices = billing.RecentPaidInvoices ?? new List<VanguardOperatorBillingInvoiceDto>();
        var notifications = billing.Notifications ?? new List<VanguardOperatorBillingNotificationDto>();

        var body = new StringBuilder();
        body.AppendLine(L("billing.body.intro"));
        body.AppendLine(L("billing.body.flow"));
        body.AppendLine(L("billing.body.no_live_debit"));
        body.AppendLine();
        body.AppendLine(F("billing.body.outstanding", VanguardUiText.Money(billing.OutstandingDebt)));
        body.AppendLine(F("billing.body.to_sign", VanguardUiText.Money(billing.PendingSignatureDebt)));
        body.AppendLine(F("billing.body.accepted", VanguardUiText.Money(billing.SignedPendingSettlementDebt)));

        var sections = new List<VanguardInfoSectionModel>
        {
            new()
            {
                Title = L("billing.title"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new() { Label = L("label.outstanding_debt"), Value = VanguardUiText.Money(billing.OutstandingDebt) },
                    new() { Label = L("label.invoices_to_sign"), Value = $"{pendingInvoices.Count} · {VanguardUiText.Money(billing.PendingSignatureDebt)}" },
                    new() { Label = L("label.signed_auto_archive"), Value = $"{signedPendingInvoices.Count} · {VanguardUiText.Money(billing.SignedPendingSettlementDebt)}" },
                    new() { Label = L("label.paid_history"), Value = VanguardUiText.Money(billing.PaidTotal) },
                    new() { Label = L("label.flow"), Value = L("billing.flow.value") }
                }
            }
        };

        sections.Add(new VanguardInfoSectionModel
        {
            Title = L("billing.section.to_sign"),
            Rows = pendingInvoices.Count == 0
                ? new List<VanguardInfoRowModel> { new() { Label = L("label.status"), Value = L("billing.empty.pending") } }
                : pendingInvoices.Take(8)
                    .Select(invoice => new VanguardInfoRowModel
                    {
                        Label = ResolveInvoiceLabel(invoice),
                        Value = $"{VanguardUiText.Value(invoice.Status, L("value.pending"))} · {VanguardUiText.Money(invoice.Amount)}"
                    })
                    .ToList()
        });

        if (signedPendingInvoices.Count > 0)
        {
            sections.Add(new VanguardInfoSectionModel
            {
                Title = L("billing.section.accepted"),
                Rows = signedPendingInvoices.Take(6)
                    .Select(invoice => new VanguardInfoRowModel
                    {
                        Label = ResolveInvoiceLabel(invoice),
                        Value = F("billing.auto_archive_refresh", VanguardUiText.Money(invoice.Amount))
                    })
                    .ToList()
            });
        }

        sections.Add(new VanguardInfoSectionModel
        {
            Title = L("billing.section.history"),
            Rows = recentPaidInvoices.Count == 0
                ? new List<VanguardInfoRowModel> { new() { Label = L("label.history"), Value = L("billing.empty.history") } }
                : recentPaidInvoices.Take(8)
                    .Select(invoice => new VanguardInfoRowModel
                    {
                        Label = ResolveInvoiceLabel(invoice),
                        Value = $"{VanguardUiText.Money(invoice.Amount)} · {FormatAppliedDate(invoice)}"
                    })
                    .ToList()
        });

        if (notifications.Count > 0)
        {
            sections.Add(new VanguardInfoSectionModel
            {
                Title = L("label.notifications"),
                Rows = notifications.Take(4)
                    .Select(notification => BuildNotificationRow(notification))
                    .ToList()
            });
        }

        return new VanguardOffRaidPanelModel
        {
            Title = L("billing.title"),
            Subtitle = L("billing.subtitle"),
            Body = body.ToString(),
            InfoSections = sections,
            Actions = new List<VanguardOffRaidPanelAction>
            {
                new() { Label = L("action.sign"), Enabled = pendingInvoices.Count > 0 || signedPendingInvoices.Count > 0, Execute = signOpenInvoices, Hint = L("billing.action.hint") }
            }
        };
    }

    private static bool HasStatus(VanguardOperatorBillingInvoiceDto invoice, string status)
    {
        return string.Equals(invoice.Status, status, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatAppliedDate(VanguardOperatorBillingInvoiceDto invoice)
    {
        return invoice.AppliedAtUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? VanguardUiText.Value(invoice.Status, L("value.paid"));
    }

    private static string ResolveInvoiceLabel(VanguardOperatorBillingInvoiceDto invoice)
    {
        string operatorName = VanguardUiText.Safe(invoice.OperatorName, invoice.OperatorId);
        string type = ResolveInvoiceType(invoice.Type);
        return string.IsNullOrWhiteSpace(operatorName) ? type : $"{operatorName} · {type}";
    }

    private static string ResolveInvoiceType(string? type)
    {
        return (type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "contract_signature" => L("billing.type.contract_signature"),
            "medical_treatment" => L("billing.type.medical_treatment"),
            "raid_salary" => L("billing.type.raid_salary"),
            _ => VanguardUiText.Value(type, L("billing.type.invoice"))
        };
    }

    private static VanguardInfoRowModel BuildNotificationRow(VanguardOperatorBillingNotificationDto notification)
    {
        if (string.Equals(notification.Kind, "debt_added", StringComparison.OrdinalIgnoreCase))
        {
            return new VanguardInfoRowModel
            {
                Label = L("billing.notification.debt_added_title"),
                Value = F("billing.notification.debt_added_message", VanguardUiText.Money(notification.Amount))
            };
        }

        return new VanguardInfoRowModel
        {
            Label = VanguardUiText.Safe(notification.Title, notification.Kind, L("label.notification")),
            Value = VanguardUiText.Safe(notification.Message, VanguardUiText.Money(notification.Amount))
        };
    }

    private static string L(string key) => VanguardOperatorsLocalizationService.Get(key);
    private static string F(string key, params object?[] args) => VanguardOperatorsLocalizationService.Format(key, args);
}

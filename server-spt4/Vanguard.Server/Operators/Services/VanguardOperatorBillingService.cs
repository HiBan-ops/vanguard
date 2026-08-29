using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using Vanguard.Server.Operators.Inventory.Services;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Operators.Storage;

using Vanguard.Server.Diagnostics;

// Responsibility: Computes and records the raid salary cost owed for deployed Operators using the verified raid outcome and contract terms.
// Flow: Persisted Operator/contract state and the completed raid roster are reconciled, billable participation is determined, deterministic invoice lines are created, and the owning profile/store receives the resulting transaction.
// Authority boundary: The server owns contract/economy persistence; billing consumes verified deployment facts and never decides in-raid behavior.
// Invariant: Only Operators proven to belong to the billed player and raid may generate salary, and retries must not create duplicate charges.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorBillingService(
    VanguardOperatorStore store,
    SaveServer saveServer,
    PaymentService paymentService,
    JsonUtil jsonUtil,
    VanguardOperatorInventoryModeService inventoryModeService,
    ISptLogger<VanguardOperatorBillingService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> billingLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<VanguardOperatorBillingSnapshot> GetBillingSnapshotAsync(string profileId)
    {
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        var now = DateTimeOffset.UtcNow;
        var ledger = await store.LoadBillingLedgerAsync(storageProfileId);

        // Read paths are deliberately side-effect free. A billing read must never settle debt.
        return BuildSnapshot(ledger, now);
    }

    public async Task<VanguardOperatorBillingInvoice> CreateInvoiceAsync(
        string profileId,
        string type,
        string operatorId,
        string operatorName,
        string? contractId,
        int amount,
        string currencyTpl,
        string narrative)
    {
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        SemaphoreSlim billingLock = GetBillingLock(storageProfileId);
        await billingLock.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var ledger = await store.LoadBillingLedgerAsync(storageProfileId);
            var invoice = new VanguardOperatorBillingInvoice(
                $"vanguard-invoice-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..52],
                type,
                VanguardOperatorBillingStatuses.PendingSignature,
                operatorId,
                operatorName,
                contractId,
                Math.Max(amount, 0),
                string.IsNullOrWhiteSpace(currencyTpl) ? "5449016a4bdc2d6f028b456f" : currencyTpl,
                now,
                null,
                null,
                narrative,
                VanguardOperatorSchema.CurrentVersion);

            var notification = new VanguardOperatorBillingNotification(
                $"vanguard-notification-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..57],
                "debt_added",
                "Vanguard invoice created",
                $"{operatorName}: {narrative}",
                invoice.Amount,
                invoice.CurrencyTpl,
                now,
                false,
                VanguardOperatorSchema.CurrentVersion);

            var updated = ledger with
            {
                Invoices = ledger.Invoices.Concat(new[] { invoice }).ToArray(),
                Notifications = ledger.Notifications.Concat(new[] { notification }).ToArray(),
                UpdatedAtUtc = now,
            };
            await store.SaveBillingLedgerAsync(storageProfileId, updated);
            return invoice;
        }
        finally
        {
            billingLock.Release();
        }
    }

    public async Task<VanguardRaidSalaryInvoiceEnsureResult> EnsureRaidSalaryInvoiceAsync(
        string profileId,
        string raidSessionId,
        string operatorId,
        string operatorName,
        int amount,
        string currencyTpl)
    {
        string storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        string normalizedRaidSessionId = NormalizeRequired(raidSessionId, nameof(raidSessionId));
        string normalizedOperatorId = NormalizeRequired(operatorId, nameof(operatorId));
        string normalizedOperatorName = string.IsNullOrWhiteSpace(operatorName) ? normalizedOperatorId : operatorName.Trim();
        string normalizedCurrencyTpl = string.IsNullOrWhiteSpace(currencyTpl) ? "5449016a4bdc2d6f028b456f" : currencyTpl.Trim();
        int normalizedAmount = Math.Max(amount, 0);
        string invoiceId = BuildRaidSalaryArtifactId("invoice", storageProfileId, normalizedRaidSessionId, normalizedOperatorId);
        string notificationId = BuildRaidSalaryArtifactId("notification", storageProfileId, normalizedRaidSessionId, normalizedOperatorId);

        SemaphoreSlim billingLock = GetBillingLock(storageProfileId);
        await billingLock.WaitAsync();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            VanguardOperatorBillingLedger ledger = await store.LoadBillingLedgerAsync(storageProfileId);
            VanguardOperatorBillingInvoice? existing = ledger.Invoices.FirstOrDefault(invoice =>
                string.Equals(invoice.InvoiceId, invoiceId, StringComparison.OrdinalIgnoreCase));

            bool invoiceCreated = false;
            VanguardOperatorBillingInvoice invoice;
            if (existing is null)
            {
                invoice = new VanguardOperatorBillingInvoice(
                    invoiceId,
                    VanguardOperatorBillingTypes.RaidSalary,
                    VanguardOperatorBillingStatuses.PendingSignature,
                    normalizedOperatorId,
                    normalizedOperatorName,
                    null,
                    normalizedAmount,
                    normalizedCurrencyTpl,
                    now,
                    null,
                    null,
                    "Raid salary",
                    VanguardOperatorSchema.CurrentVersion);
                invoiceCreated = true;
            }
            else
            {
                if (!string.Equals(existing.Type, VanguardOperatorBillingTypes.RaidSalary, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.OperatorId, normalizedOperatorId, StringComparison.OrdinalIgnoreCase)
                    || existing.Amount != normalizedAmount
                    || !string.Equals(existing.CurrencyTpl, normalizedCurrencyTpl, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("raid_salary_invoice_idempotency_conflict");
                }

                invoice = existing;
            }

            bool invoiceRollbackEligible = invoiceCreated
                || string.Equals(invoice.Status, VanguardOperatorBillingStatuses.PendingSignature, StringComparison.OrdinalIgnoreCase);
            bool notificationExists = ledger.Notifications.Any(notification =>
                string.Equals(notification.NotificationId, notificationId, StringComparison.OrdinalIgnoreCase));
            bool notificationCreated = !notificationExists;
            bool notificationRollbackEligible = invoiceRollbackEligible && (notificationCreated || notificationExists);
            VanguardOperatorBillingNotification? notification = notificationCreated
                ? new VanguardOperatorBillingNotification(
                    notificationId,
                    "debt_added",
                    "Vanguard invoice created",
                    $"{normalizedOperatorName}: raid salary",
                    invoice.Amount,
                    invoice.CurrencyTpl,
                    now,
                    false,
                    VanguardOperatorSchema.CurrentVersion)
                : null;

            if (invoiceCreated || notificationCreated)
            {
                VanguardOperatorBillingLedger updated = ledger with
                {
                    Invoices = invoiceCreated ? ledger.Invoices.Concat(new[] { invoice }).ToArray() : ledger.Invoices,
                    Notifications = notificationCreated && notification is not null
                        ? ledger.Notifications.Concat(new[] { notification }).ToArray()
                        : ledger.Notifications,
                    UpdatedAtUtc = now,
                };
                await store.SaveBillingLedgerAsync(storageProfileId, updated);
            }

            return new VanguardRaidSalaryInvoiceEnsureResult(
                storageProfileId,
                invoiceId,
                notificationId,
                invoiceCreated,
                notificationCreated,
                invoiceRollbackEligible,
                notificationRollbackEligible,
                invoice);
        }
        finally
        {
            billingLock.Release();
        }
    }

    public async Task<bool> RollbackRaidSalaryInvoiceAsync(VanguardRaidSalaryInvoiceEnsureResult ensured)
    {
        if (!ensured.InvoiceRollbackEligible && !ensured.NotificationRollbackEligible)
        {
            return true;
        }

        SemaphoreSlim billingLock = GetBillingLock(ensured.StorageProfileId);
        await billingLock.WaitAsync();
        try
        {
            VanguardOperatorBillingLedger ledger = await store.LoadBillingLedgerAsync(ensured.StorageProfileId);
            VanguardOperatorBillingInvoice? currentInvoice = ledger.Invoices.FirstOrDefault(invoice =>
                string.Equals(invoice.InvoiceId, ensured.InvoiceId, StringComparison.OrdinalIgnoreCase));

            if (ensured.InvoiceRollbackEligible && currentInvoice is not null)
            {
                bool receiptOwnsInvoice = ledger.PendingSettlement?.InvoiceIds?.Any(invoiceId =>
                    string.Equals(invoiceId, ensured.InvoiceId, StringComparison.OrdinalIgnoreCase)) == true;
                if (receiptOwnsInvoice || !string.Equals(currentInvoice.Status, VanguardOperatorBillingStatuses.PendingSignature, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            IReadOnlyList<VanguardOperatorBillingInvoice> invoices = ensured.InvoiceRollbackEligible
                ? ledger.Invoices.Where(invoice => !string.Equals(invoice.InvoiceId, ensured.InvoiceId, StringComparison.OrdinalIgnoreCase)).ToArray()
                : ledger.Invoices;
            IReadOnlyList<VanguardOperatorBillingNotification> notifications = ensured.NotificationRollbackEligible
                ? ledger.Notifications.Where(notification => !string.Equals(notification.NotificationId, ensured.NotificationId, StringComparison.OrdinalIgnoreCase)).ToArray()
                : ledger.Notifications;

            if (invoices.Count == ledger.Invoices.Count && notifications.Count == ledger.Notifications.Count)
            {
                return true;
            }

            await store.SaveBillingLedgerAsync(ensured.StorageProfileId, ledger with
            {
                Invoices = invoices,
                Notifications = notifications,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            return true;
        }
        finally
        {
            billingLock.Release();
        }
    }

    public async Task<VanguardOperatorBillingActionResponse> SignOutstandingInvoicesAsync(string profileId, IReadOnlyList<string>? invoiceIds)
    {
        var requestedProfileId = profileId;
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        SemaphoreSlim billingLock = GetBillingLock(storageProfileId);
        await billingLock.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var ledger = await store.LoadBillingLedgerAsync(storageProfileId);
            var selectedIds = invoiceIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var processed = new List<VanguardOperatorBillingInvoice>();
            var invoices = ledger.Invoices
                .Select(invoice =>
                {
                    var matches = invoice.Status == VanguardOperatorBillingStatuses.PendingSignature
                        && (selectedIds is null || selectedIds.Count == 0 || selectedIds.Contains(invoice.InvoiceId));
                    if (!matches)
                    {
                        return invoice;
                    }

                    var signed = invoice with
                    {
                        Status = VanguardOperatorBillingStatuses.SignedPendingSettlement,
                        SignedAtUtc = now,
                    };
                    processed.Add(signed);
                    return signed;
                })
                .ToArray();

            var updated = ledger with { Invoices = invoices, UpdatedAtUtc = now };
            await store.SaveBillingLedgerAsync(storageProfileId, updated);

            var amount = processed.Sum(invoice => invoice.Amount);
            return new VanguardOperatorBillingActionResponse(
                true,
                requestedProfileId,
                storageProfileId,
                processed.Count == 0 ? "no_pending_invoice" : "signed_pending_settlement",
                processed.Count,
                amount,
                false,
                false,
                processed,
                BuildSnapshot(updated, now),
                now,
                VanguardBuildVersion.BuildLabel);
        }
        finally
        {
            billingLock.Release();
        }
    }

    public async Task<VanguardOperatorBillingActionResponse> ReconcileSignedInvoicesAsync(string profileId)
    {
        var requestedProfileId = profileId;
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        SemaphoreSlim billingLock = GetBillingLock(storageProfileId);
        await billingLock.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var ledger = await store.LoadBillingLedgerAsync(storageProfileId);

            MongoId sessionId;
            try
            {
                sessionId = new MongoId(requestedProfileId);
            }
            catch (Exception exception)
            {
                logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=admission; result=failed; reason=invalid_profile_id; requested={requestedProfileId}; type={exception.GetType().Name}"));
                return BuildActionResponse(false, requestedProfileId, storageProfileId, "invalid_profile_id", Array.Empty<VanguardOperatorBillingInvoice>(), ledger, now, true, false);
            }

            // Billing always targets the real player EFT profile. Operator equipment mode can
            // temporarily redirect SaveServer access; explicitly bypass that redirect for the
            // complete settlement so admission, debit, persistence and readback share one authority.
            using IDisposable profileRedirectBypass = inventoryModeService.SuppressProfileRedirects();
            var profile = saveServer.GetProfile(sessionId);
            PmcData? pmcData = profile?.CharacterData?.PmcData;
            if (profile == null || pmcData?.Inventory?.Items == null)
            {
                logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=admission; result=failed; reason=player_profile_unavailable; requested={requestedProfileId}; storage={storageProfileId}"));
                return BuildActionResponse(false, requestedProfileId, storageProfileId, "player_profile_unavailable", Array.Empty<VanguardOperatorBillingInvoice>(), ledger, now, true, false);
            }

            VanguardOperatorBillingSettlementReceipt? receipt = ledger.PendingSettlement;
            IReadOnlyList<VanguardOperatorBillingInvoice> receiptInvoices;

            if (receipt is null)
            {
                var signedInvoices = ledger.Invoices
                    .Where(invoice => invoice.Status == VanguardOperatorBillingStatuses.SignedPendingSettlement)
                    .ToArray();

                if (signedInvoices.Length == 0)
                {
                    return BuildActionResponse(
                        true,
                        requestedProfileId,
                        storageProfileId,
                        "no_signed_invoice",
                        Array.Empty<VanguardOperatorBillingInvoice>(),
                        ledger,
                        now,
                        false,
                        false);
                }

                var paymentGroups = BuildPaymentGroups(signedInvoices);
                var preflightPmc = ClonePmcData(pmcData);
                foreach (BillingPaymentGroup group in paymentGroups)
                {
                    var preflightOutput = CreatePaymentOutput(sessionId);
                    paymentService.AddPaymentToOutput(preflightPmc, new MongoId(group.CurrencyTpl), group.Amount, sessionId, preflightOutput);
                    if (preflightOutput.Warnings?.Count > 0)
                    {
                        logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=preflight; result=failed; reason=insufficient_or_rejected_payment; requested={requestedProfileId}; storage={storageProfileId}; currency={group.CurrencyTpl}; amount={group.Amount:0}; warnings={preflightOutput.Warnings.Count}"));
                        return BuildActionResponse(false, requestedProfileId, storageProfileId, "insufficient_funds_or_payment_rejected", signedInvoices, ledger, now, true, false);
                    }
                }

                var currencies = paymentGroups
                    .Select(group =>
                    {
                        double before = GetCurrencyBalance(pmcData, group.CurrencyTpl);
                        return new VanguardOperatorBillingSettlementCurrency(
                            group.CurrencyTpl,
                            group.Amount,
                            before,
                            before - group.Amount);
                    })
                    .ToArray();

                receipt = new VanguardOperatorBillingSettlementReceipt(
                    $"vanguard-settlement-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..55],
                    signedInvoices.Select(invoice => invoice.InvoiceId).ToArray(),
                    currencies,
                    now,
                    VanguardOperatorSchema.CurrentVersion);

                ledger = ledger with { PendingSettlement = receipt, UpdatedAtUtc = now };
                await store.SaveBillingLedgerAsync(storageProfileId, ledger);
                receiptInvoices = signedInvoices;

                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=prepare; result=ok; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; invoices={receipt.InvoiceIds.Count}; amount={receiptInvoices.Sum(invoice => invoice.Amount)}"));
            }
            else
            {
                if (!TryResolveReceiptInvoices(ledger, receipt, out receiptInvoices, out string validationReason))
                {
                    logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=resume; result=failed; reason={validationReason}; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}"));
                    return BuildActionResponse(false, requestedProfileId, storageProfileId, validationReason, Array.Empty<VanguardOperatorBillingInvoice>(), ledger, now, true, false);
                }

                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=resume; result=prepared_receipt_found; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; invoices={receipt.InvoiceIds.Count}"));
            }

            if (receipt.Currencies.Count == 0)
            {
                var zeroSettlementLedger = FinalizeReceipt(ledger, receipt, now, out var zeroSettledInvoices);
                await store.SaveBillingLedgerAsync(storageProfileId, zeroSettlementLedger);
                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=commit; result=ok; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; invoices={zeroSettledInvoices.Count}; amount=0; zeroValue=true"));
                return BuildActionResponse(true, requestedProfileId, storageProfileId, "settled_player_economy", zeroSettledInvoices, zeroSettlementLedger, now, true, true);
            }

            var currentBalances = receipt.Currencies.ToDictionary(
                currency => currency.CurrencyTpl,
                currency => GetCurrencyBalance(pmcData, currency.CurrencyTpl),
                StringComparer.OrdinalIgnoreCase);

            bool atBefore = BalancesMatch(receipt.Currencies, currentBalances, useExpected: false);
            bool atExpected = BalancesMatch(receipt.Currencies, currentBalances, useExpected: true);

            if (atExpected)
            {
                var resumedLedger = FinalizeReceipt(ledger, receipt, now, out var resumedInvoices);
                await store.SaveBillingLedgerAsync(storageProfileId, resumedLedger);
                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=resume; result=debit_already_persisted_finalize_only; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; invoices={resumedInvoices.Count}; amount={resumedInvoices.Sum(invoice => invoice.Amount)}; doubleDebitPrevented=true"));
                return BuildActionResponse(true, requestedProfileId, storageProfileId, "settled_after_persisted_debit_resume", resumedInvoices, resumedLedger, now, true, true);
            }

            if (!atBefore)
            {
                string observed = string.Join(",", receipt.Currencies.Select(currency => $"{currency.CurrencyTpl}:{currentBalances[currency.CurrencyTpl]:0}/{currency.BalanceBefore:0}->{currency.ExpectedBalanceAfter:0}"));
                logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=resume; result=failed_closed; reason=ambiguous_player_balance; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; balances={observed}"));
                return BuildActionResponse(false, requestedProfileId, storageProfileId, "ambiguous_player_balance_fail_closed", receiptInvoices, ledger, now, true, false);
            }

            PmcData originalPmc = ClonePmcData(pmcData);
            try
            {
                foreach (VanguardOperatorBillingSettlementCurrency currency in receipt.Currencies)
                {
                    var paymentOutput = CreatePaymentOutput(sessionId);
                    paymentService.AddPaymentToOutput(pmcData, new MongoId(currency.CurrencyTpl), currency.Amount, sessionId, paymentOutput);
                    if (paymentOutput.Warnings?.Count > 0)
                    {
                        profile.CharacterData!.PmcData = originalPmc;
                        logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=debit; result=failed; reason=live_payment_rejected_after_preflight; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; currency={currency.CurrencyTpl}; amount={currency.Amount:0}"));
                        return BuildActionResponse(false, requestedProfileId, storageProfileId, "live_payment_rejected_after_preflight", receiptInvoices, ledger, now, true, false);
                    }
                }

                await saveServer.SaveProfileAsync(sessionId);
            }
            catch (Exception exception)
            {
                // The durable prepared receipt remains authoritative. Restore only the in-memory
                // profile image; never issue a compensating save whose success could be ambiguous.
                profile.CharacterData!.PmcData = originalPmc;
                logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=profile_commit; result=failed; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; receiptPreserved=true; compensationSave=false; error={exception.GetType().Name}:{exception.Message}"));
                return BuildActionResponse(false, requestedProfileId, storageProfileId, "profile_commit_failed_receipt_preserved", receiptInvoices, ledger, now, true, false);
            }

            var persistedProfile = saveServer.GetProfile(sessionId);
            PmcData? persistedPmc = persistedProfile?.CharacterData?.PmcData;
            if (persistedPmc?.Inventory?.Items == null)
            {
                logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=profile_postsave_readback; result=failed; reason=player_profile_postsave_readback_unavailable; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; receiptPreserved=true"));
                return BuildActionResponse(false, requestedProfileId, storageProfileId, "player_profile_postsave_readback_unavailable_receipt_preserved", receiptInvoices, ledger, now, true, false);
            }

            var persistedBalances = receipt.Currencies.ToDictionary(
                currency => currency.CurrencyTpl,
                currency => GetCurrencyBalance(persistedPmc, currency.CurrencyTpl),
                StringComparer.OrdinalIgnoreCase);
            if (!BalancesMatch(receipt.Currencies, persistedBalances, useExpected: true))
            {
                string observed = string.Join(",", receipt.Currencies.Select(currency => $"{currency.CurrencyTpl}:{persistedBalances[currency.CurrencyTpl]:0}/{currency.ExpectedBalanceAfter:0}"));
                logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=profile_postsave_readback; result=failed_closed; reason=balance_invariant_mismatch; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; receiptPreserved=true; balances={observed}"));
                return BuildActionResponse(false, requestedProfileId, storageProfileId, "profile_balance_invariant_mismatch_receipt_preserved", receiptInvoices, ledger, now, true, false);
            }

            foreach (VanguardOperatorBillingSettlementCurrency currency in receipt.Currencies)
            {
                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=profile_postsave_readback; result=ok; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; currency={currency.CurrencyTpl}; balanceBefore={currency.BalanceBefore:0}; amount={currency.Amount:0}; balanceAfter={persistedBalances[currency.CurrencyTpl]:0}; expected={currency.ExpectedBalanceAfter:0}"));
            }

            try
            {
                var settledLedger = FinalizeReceipt(ledger, receipt, now, out var settledInvoices);
                await store.SaveBillingLedgerAsync(storageProfileId, settledLedger);
                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=commit; result=ok; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; invoices={settledInvoices.Count}; amount={settledInvoices.Sum(invoice => invoice.Amount)}; ledgerPaid=true; profilePersisted=true; receiptCleared=true"));
                return BuildActionResponse(true, requestedProfileId, storageProfileId, "settled_player_economy", settledInvoices, settledLedger, now, true, true);
            }
            catch (Exception exception)
            {
                // The debit is already durable. Keep the prepared receipt on disk so the next
                // reconcile observes ExpectedBalanceAfter and finalizes without charging twice.
                logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OFFRAID_BILLING_SETTLEMENT_STATUS] phase=ledger_commit; result=failed; requested={requestedProfileId}; storage={storageProfileId}; settlement={receipt.SettlementId}; debitPersisted=true; receiptPreserved=true; doubleDebitGuard=resume_expected_balance; error={exception.GetType().Name}:{exception.Message}"));
                return BuildActionResponse(false, requestedProfileId, storageProfileId, "ledger_commit_failed_debit_persisted_receipt_preserved", receiptInvoices, ledger, now, true, false);
            }
        }
        finally
        {
            billingLock.Release();
        }
    }

    private static string BuildRaidSalaryArtifactId(string kind, string storageProfileId, string raidSessionId, string operatorId)
    {
        string source = $"raid_salary|{kind}|{storageProfileId}|{raidSessionId}|{operatorId}";
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return $"vanguard-salary-{kind}-{digest[..24]}";
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    private SemaphoreSlim GetBillingLock(string storageProfileId) =>
        billingLocks.GetOrAdd(storageProfileId, _ => new SemaphoreSlim(1, 1));

    private static IReadOnlyList<BillingPaymentGroup> BuildPaymentGroups(IReadOnlyList<VanguardOperatorBillingInvoice> invoices) =>
        invoices
            .Where(invoice => invoice.Amount > 0)
            .GroupBy(
                invoice => string.IsNullOrWhiteSpace(invoice.CurrencyTpl) ? "5449016a4bdc2d6f028b456f" : invoice.CurrencyTpl,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new BillingPaymentGroup(group.Key, group.Sum(invoice => (double)invoice.Amount)))
            .ToArray();

    private static bool TryResolveReceiptInvoices(
        VanguardOperatorBillingLedger ledger,
        VanguardOperatorBillingSettlementReceipt receipt,
        out IReadOnlyList<VanguardOperatorBillingInvoice> invoices,
        out string reason)
    {
        var byId = ledger.Invoices.ToDictionary(invoice => invoice.InvoiceId, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<VanguardOperatorBillingInvoice>(receipt.InvoiceIds.Count);
        foreach (string invoiceId in receipt.InvoiceIds)
        {
            if (!byId.TryGetValue(invoiceId, out VanguardOperatorBillingInvoice? invoice))
            {
                invoices = Array.Empty<VanguardOperatorBillingInvoice>();
                reason = "settlement_receipt_invoice_missing_fail_closed";
                return false;
            }

            if (invoice.Status is not (VanguardOperatorBillingStatuses.SignedPendingSettlement or VanguardOperatorBillingStatuses.Paid))
            {
                invoices = Array.Empty<VanguardOperatorBillingInvoice>();
                reason = "settlement_receipt_invoice_state_mismatch_fail_closed";
                return false;
            }

            resolved.Add(invoice);
        }

        invoices = resolved;
        reason = "ok";
        return true;
    }

    private static VanguardOperatorBillingLedger FinalizeReceipt(
        VanguardOperatorBillingLedger ledger,
        VanguardOperatorBillingSettlementReceipt receipt,
        DateTimeOffset now,
        out IReadOnlyList<VanguardOperatorBillingInvoice> settledInvoices)
    {
        var receiptIds = receipt.InvoiceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settled = new List<VanguardOperatorBillingInvoice>(receiptIds.Count);
        var invoices = ledger.Invoices
            .Select(invoice =>
            {
                if (!receiptIds.Contains(invoice.InvoiceId))
                {
                    return invoice;
                }

                var paid = invoice.Status == VanguardOperatorBillingStatuses.Paid
                    ? invoice
                    : invoice with
                    {
                        Status = VanguardOperatorBillingStatuses.Paid,
                        AppliedAtUtc = now,
                    };
                settled.Add(paid);
                return paid;
            })
            .ToArray();

        settledInvoices = settled;
        return ledger with
        {
            Invoices = invoices,
            PendingSettlement = null,
            UpdatedAtUtc = now,
        };
    }

    private static bool BalancesMatch(
        IReadOnlyList<VanguardOperatorBillingSettlementCurrency> currencies,
        IReadOnlyDictionary<string, double> currentBalances,
        bool useExpected)
    {
        foreach (VanguardOperatorBillingSettlementCurrency currency in currencies)
        {
            if (!currentBalances.TryGetValue(currency.CurrencyTpl, out double current))
            {
                return false;
            }

            double expected = useExpected ? currency.ExpectedBalanceAfter : currency.BalanceBefore;
            if (Math.Abs(current - expected) > 0.001d)
            {
                return false;
            }
        }

        return true;
    }

    private static ItemEventRouterResponse CreatePaymentOutput(MongoId sessionId)
    {
        return new ItemEventRouterResponse
        {
            Warnings = new List<Warning>(),
            ProfileChanges = new Dictionary<MongoId, ProfileChange>
            {
                [sessionId] = new ProfileChange
                {
                    Id = sessionId.ToString(),
                    Items = new ItemChanges
                    {
                        NewItems = new List<SPTarkov.Server.Core.Models.Eft.Common.Tables.Item>(),
                        ChangedItems = new List<SPTarkov.Server.Core.Models.Eft.Common.Tables.Item>(),
                        DeletedItems = new List<DeletedItem>(),
                    },
                },
            },
        };
    }

    private static double GetCurrencyBalance(PmcData pmcData, string currencyTpl)
    {
        if (pmcData.Inventory?.Items == null)
        {
            return 0d;
        }

        var template = new MongoId(currencyTpl);
        double total = 0d;
        foreach (var item in pmcData.Inventory.Items)
        {
            if (item.Template != template)
            {
                continue;
            }

            total += item.Upd?.StackObjectsCount ?? 1d;
        }

        return total;
    }

    private PmcData ClonePmcData(PmcData source)
    {
        string serialized = jsonUtil.Serialize(source, indented: false)
            ?? throw new InvalidOperationException("Unable to serialize SPT PMC profile for Vanguard billing settlement.");
        return jsonUtil.Deserialize<PmcData>(serialized)
            ?? throw new InvalidOperationException("Unable to deserialize SPT PMC profile for Vanguard billing settlement.");
    }

    private static VanguardOperatorBillingActionResponse BuildActionResponse(
        bool success,
        string requestedProfileId,
        string storageProfileId,
        string reason,
        IReadOnlyList<VanguardOperatorBillingInvoice> processedInvoices,
        VanguardOperatorBillingLedger ledger,
        DateTimeOffset now,
        bool settlementAttempted,
        bool settlementSucceeded)
    {
        return new VanguardOperatorBillingActionResponse(
            success,
            requestedProfileId,
            storageProfileId,
            reason,
            processedInvoices.Count,
            processedInvoices.Sum(invoice => invoice.Amount),
            settlementAttempted,
            settlementSucceeded,
            processedInvoices,
            BuildSnapshot(ledger, now),
            now,
            VanguardBuildVersion.BuildLabel);
    }

    public static VanguardOperatorBillingSnapshot BuildSnapshot(VanguardOperatorBillingLedger ledger, DateTimeOffset now)
    {
        var open = ledger.Invoices
            .Where(invoice => invoice.Status is VanguardOperatorBillingStatuses.PendingSignature or VanguardOperatorBillingStatuses.SignedPendingSettlement)
            .OrderByDescending(invoice => invoice.CreatedAtUtc)
            .ToArray();
        var paid = ledger.Invoices
            .Where(invoice => invoice.Status == VanguardOperatorBillingStatuses.Paid)
            .OrderByDescending(invoice => invoice.AppliedAtUtc ?? invoice.CreatedAtUtc)
            .Take(10)
            .ToArray();

        return new VanguardOperatorBillingSnapshot(
            open.Sum(invoice => invoice.Amount),
            open.Where(invoice => invoice.Status == VanguardOperatorBillingStatuses.PendingSignature).Sum(invoice => invoice.Amount),
            open.Where(invoice => invoice.Status == VanguardOperatorBillingStatuses.SignedPendingSettlement).Sum(invoice => invoice.Amount),
            ledger.Invoices.Where(invoice => invoice.Status == VanguardOperatorBillingStatuses.Paid).Sum(invoice => invoice.Amount),
            open.Length,
            open,
            paid,
            ledger.Notifications.OrderByDescending(notification => notification.CreatedAtUtc).Take(20).ToArray(),
            now);
    }

    private sealed record BillingPaymentGroup(string CurrencyTpl, double Amount);
}

public sealed record VanguardRaidSalaryInvoiceEnsureResult(
    string StorageProfileId,
    string InvoiceId,
    string NotificationId,
    bool InvoiceCreated,
    bool NotificationCreated,
    bool InvoiceRollbackEligible,
    bool NotificationRollbackEligible,
    VanguardOperatorBillingInvoice Invoice);

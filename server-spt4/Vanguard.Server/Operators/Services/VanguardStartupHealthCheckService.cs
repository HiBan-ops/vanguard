using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Inventory.Services;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Raid.Persistence.Services;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Operators.Storage;

// Responsibility: Performs a bounded startup health check of the release-safety locks and isolated release transaction mechanics.
// Flow: Startup exercises the raid-persistence lease, Operator Inventory external mutation lease, and an OS-temp release transaction/readback.
// Authority boundary: The health check never enters Operator Inventory mode and never writes Vanguard production profile state; all transaction writes use an OS-temp VanguardOperatorStore instance.
// Invariant: Startup validation is self-cleaning, fail-soft, operator-readable in console, detailed in vanguard-server.log, and always reports productionMutation=false.
namespace Vanguard.Server.Operators.Services;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public sealed class VanguardStartupHealthCheckService(
    VanguardRaidOperatorPersistenceService raidPersistenceService,
    VanguardOperatorInventoryModeService inventoryModeService,
    ISptLogger<VanguardStartupHealthCheckService> logger)
    : IOnLoad
{
    public const string StatusTag = "VANGUARD_STARTUP_HEALTH_CHECK_STATUS";

    private const string FixtureOperatorId = "__vanguard_startup_health_fixture_operator__";
    private const string FixtureStorageProfileId = "__vanguard_startup_health_fixture_profile__";

    public async Task OnLoad()
    {
        string? sandboxRoot = null;
        int checks = 0;
        bool raidPersistenceInitial = false;
        bool raidPersistenceContention = false;
        bool raidPersistenceRecovery = false;
        bool inventoryMutationInitial = false;
        bool inventoryMutationContention = false;
        bool inventoryMutationRecovery = false;
        bool releaseTransactionReadback = false;
        bool expectedBeforeReject = false;
        bool sandboxCleaned = false;

        try
        {
            using (IDisposable? firstRaidLease = await raidPersistenceService.TryAcquireExternalMutationLeaseAsync())
            {
                raidPersistenceInitial = firstRaidLease is not null;
                Require(raidPersistenceInitial, "raid_persistence_initial_lease_unavailable");
                checks++;

                using IDisposable? nestedRaidLease = await raidPersistenceService.TryAcquireExternalMutationLeaseAsync();
                raidPersistenceContention = nestedRaidLease is null;
                Require(raidPersistenceContention, "raid_persistence_nested_lease_not_rejected");
                checks++;
            }

            using (IDisposable? recoveredRaidLease = await raidPersistenceService.TryAcquireExternalMutationLeaseAsync())
            {
                raidPersistenceRecovery = recoveredRaidLease is not null;
                Require(raidPersistenceRecovery, "raid_persistence_gate_did_not_recover");
                checks++;
            }

            using (IDisposable? firstInventoryLease = await inventoryModeService.TryAcquireExternalMutationLeaseAsync(
                FixtureStorageProfileId,
                new[] { FixtureOperatorId }))
            {
                inventoryMutationInitial = firstInventoryLease is not null;
                Require(inventoryMutationInitial, "inventory_mutation_initial_lease_unavailable");
                checks++;

                using IDisposable? nestedInventoryLease = await inventoryModeService.TryAcquireExternalMutationLeaseAsync(
                    FixtureStorageProfileId,
                    new[] { FixtureOperatorId });
                inventoryMutationContention = nestedInventoryLease is null;
                Require(inventoryMutationContention, "inventory_mutation_nested_lease_not_rejected");
                checks++;
            }

            using (IDisposable? recoveredInventoryLease = await inventoryModeService.TryAcquireExternalMutationLeaseAsync(
                FixtureStorageProfileId,
                new[] { FixtureOperatorId }))
            {
                inventoryMutationRecovery = recoveredInventoryLease is not null;
                Require(inventoryMutationRecovery, "inventory_mutation_gate_did_not_recover");
                checks++;
            }

            sandboxRoot = Path.Combine(Path.GetTempPath(), "Vanguard_StartupHealth_" + Guid.NewGuid().ToString("N"));
            var sandboxStore = new VanguardOperatorStore(sandboxRoot);
            await sandboxStore.EnsureProfileStorageInitializedAsync(FixtureStorageProfileId);

            VanguardOperatorReleaseWriteResult commit = await sandboxStore.CommitReleaseFromServiceAsync(
                FixtureStorageProfileId,
                Array.Empty<VanguardOperatorProfile>(),
                Array.Empty<VanguardOperatorProfile>(),
                Array.Empty<VanguardActiveServiceRecord>(),
                Array.Empty<VanguardActiveServiceRecord>(),
                Array.Empty<VanguardOperatorContactRecord>(),
                Array.Empty<VanguardOperatorContactRecord>());
            releaseTransactionReadback = commit.Success && commit.ReadBackVerified && !commit.RolledBack;
            Require(releaseTransactionReadback, "sandbox_release_transaction_readback_failed_" + commit.Reason);
            checks++;

            var impossibleExpectedActiveService = new[]
            {
                new VanguardActiveServiceRecord(
                    FixtureOperatorId,
                    "Fixture Operator",
                    "Usec",
                    "Assault",
                    "Startup Health Check",
                    VanguardOperatorServiceStatuses.ActiveService,
                    false,
                    false,
                    DateTimeOffset.UnixEpoch,
                    0,
                    null,
                    null),
            };

            VanguardOperatorReleaseWriteResult rejected = await sandboxStore.CommitReleaseFromServiceAsync(
                FixtureStorageProfileId,
                Array.Empty<VanguardOperatorProfile>(),
                Array.Empty<VanguardOperatorProfile>(),
                impossibleExpectedActiveService,
                Array.Empty<VanguardActiveServiceRecord>(),
                Array.Empty<VanguardOperatorContactRecord>(),
                Array.Empty<VanguardOperatorContactRecord>());
            expectedBeforeReject = !rejected.Success
                && !rejected.ReadBackVerified
                && !rejected.RolledBack
                && string.Equals(rejected.Reason, "active_service_changed_since_release_read", StringComparison.Ordinal);
            Require(expectedBeforeReject, "sandbox_expected_before_rejection_failed_" + rejected.Reason);
            checks++;

            Directory.Delete(sandboxRoot, recursive: true);
            sandboxCleaned = !Directory.Exists(sandboxRoot);
            Require(sandboxCleaned, "sandbox_cleanup_failed");
            sandboxRoot = null;
            checks++;

            WriteDetailedStatus(
                "passed",
                checks,
                "none",
                raidPersistenceInitial,
                raidPersistenceContention,
                raidPersistenceRecovery,
                inventoryMutationInitial,
                inventoryMutationContention,
                inventoryMutationRecovery,
                releaseTransactionReadback,
                expectedBeforeReject,
                sandboxCleaned);

            WriteReadableSummary(
                passed: true,
                checks,
                raidLocksOk: raidPersistenceInitial && raidPersistenceContention && raidPersistenceRecovery,
                inventoryLocksOk: inventoryMutationInitial && inventoryMutationContention && inventoryMutationRecovery,
                releaseTransactionOk: releaseTransactionReadback,
                conflictProtectionOk: expectedBeforeReject,
                sandboxCleanupOk: sandboxCleaned,
                failureReason: null);
        }
        catch (Exception exception)
        {
            string reason = exception.GetType().Name + "_" + exception.Message;
            WriteDetailedStatus(
                "failed",
                checks,
                reason,
                raidPersistenceInitial,
                raidPersistenceContention,
                raidPersistenceRecovery,
                inventoryMutationInitial,
                inventoryMutationContention,
                inventoryMutationRecovery,
                releaseTransactionReadback,
                expectedBeforeReject,
                sandboxCleaned);

            WriteReadableSummary(
                passed: false,
                checks,
                raidLocksOk: raidPersistenceInitial && raidPersistenceContention && raidPersistenceRecovery,
                inventoryLocksOk: inventoryMutationInitial && inventoryMutationContention && inventoryMutationRecovery,
                releaseTransactionOk: releaseTransactionReadback,
                conflictProtectionOk: expectedBeforeReject,
                sandboxCleanupOk: sandboxCleaned,
                failureReason: exception.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sandboxRoot) && Directory.Exists(sandboxRoot))
            {
                try
                {
                    Directory.Delete(sandboxRoot, recursive: true);
                }
                catch
                {
                    // A stale OS-temp health-check directory is preferable to touching production storage during cleanup recovery.
                }
            }
        }
    }

    private void WriteReadableSummary(
        bool passed,
        int checks,
        bool raidLocksOk,
        bool inventoryLocksOk,
        bool releaseTransactionOk,
        bool conflictProtectionOk,
        bool sandboxCleanupOk,
        string? failureReason)
    {
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_STARTUP] Health check - Raid persistence locks: {PassFail(raidLocksOk)}"));
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_STARTUP] Health check - Inventory mutation locks: {PassFail(inventoryLocksOk)}"));
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_STARTUP] Health check - Release transaction: {PassFail(releaseTransactionOk)}"));
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_STARTUP] Health check - Conflict protection: {PassFail(conflictProtectionOk)}"));
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_STARTUP] Health check - Sandbox cleanup: {PassFail(sandboxCleanupOk)}"));

        if (passed)
        {
            logger.Success(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_STARTUP] Health check result: PASS - {checks}/9 checks passed; production data modified: NO"));
            return;
        }

        logger.Error(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_STARTUP] Health check result: FAIL - {checks}/9 checks passed; production data modified: NO; reason={ReadableReason(failureReason)}"));
    }

    private static void WriteDetailedStatus(
        string state,
        int checks,
        string reason,
        bool raidPersistenceInitial,
        bool raidPersistenceContention,
        bool raidPersistenceRecovery,
        bool inventoryMutationInitial,
        bool inventoryMutationContention,
        bool inventoryMutationRecovery,
        bool releaseTransactionReadback,
        bool expectedBeforeReject,
        bool sandboxCleaned)
    {
        _ = VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=startup_health_check; state={Safe(state)}; checks={checks}; reason={Safe(reason)}; " +
            $"raidPersistenceInitial={Bool(raidPersistenceInitial)}; raidPersistenceContention={Bool(raidPersistenceContention)}; raidPersistenceRecovery={Bool(raidPersistenceRecovery)}; " +
            $"inventoryMutationInitial={Bool(inventoryMutationInitial)}; inventoryMutationContention={Bool(inventoryMutationContention)}; inventoryMutationRecovery={Bool(inventoryMutationRecovery)}; " +
            $"releaseTransactionReadback={Bool(releaseTransactionReadback)}; expectedBeforeReject={Bool(expectedBeforeReject)}; sandboxCleaned={Bool(sandboxCleaned)}; " +
            "productionMutation=false; inventorySessionMutation=false; sandboxOnlyWrites=true; consolePresentation=readable; tag=" + StatusTag);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private static string PassFail(bool value) => value ? "PASS" : "FAIL";

    private static string ReadableReason(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}

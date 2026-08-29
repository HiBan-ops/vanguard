using SPTarkov.DI.Annotations;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Responses;
using Vanguard.Server.Operators.Storage;

// Responsibility: Coordinates Operator Medical Recovery Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorMedicalRecoveryService(
    VanguardOperatorStore store,
    VanguardOperatorBillingService billingService)
{
    public async Task<VanguardOperatorMedicalTreatmentResponse> TreatOperatorAsync(string profileId, string? operatorId, bool confirmTreatment)
    {
        var requestedProfileId = profileId;
        var storageProfileId = await store.ResolveStorageProfileIdAsync(profileId);
        var state = await store.LoadStateAsync(storageProfileId);
        var now = DateTimeOffset.UtcNow;

        if (!confirmTreatment)
        {
            var billing = await billingService.GetBillingSnapshotAsync(storageProfileId);
            return new VanguardOperatorMedicalTreatmentResponse(false, requestedProfileId, storageProfileId, "treatment_not_confirmed", operatorId, string.Empty, 0, 0, 0, null, billing, now, VanguardBuildVersion.BuildLabel);
        }

        if (string.IsNullOrWhiteSpace(operatorId))
        {
            var billing = await billingService.GetBillingSnapshotAsync(storageProfileId);
            return new VanguardOperatorMedicalTreatmentResponse(false, requestedProfileId, storageProfileId, "operator_id_required", operatorId, string.Empty, 0, 0, 0, null, billing, now, VanguardBuildVersion.BuildLabel);
        }

        var medical = state.Medical.FirstOrDefault(item => string.Equals(item.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        var profile = state.Operators.FirstOrDefault(item => string.Equals(item.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        if (medical is null || profile is null)
        {
            var billing = await billingService.GetBillingSnapshotAsync(storageProfileId);
            return new VanguardOperatorMedicalTreatmentResponse(false, requestedProfileId, storageProfileId, "operator_medical_record_not_found", operatorId, string.Empty, 0, 0, 0, null, billing, now, VanguardBuildVersion.BuildLabel);
        }

        var before = Math.Clamp(medical.CurrentHealthRatio, 0.0, 1.0);
        var recoveryExpired = medical.RecoveryUntilUtc is DateTimeOffset until && until <= now;
        if (before >= 0.999 && (medical.RecoveryUntilUtc is null || recoveryExpired))
        {
            var alreadyRecovered = medical with
            {
                Status = VanguardOperatorServiceStatuses.Available,
                CurrentHealthRatio = 1.0,
                RecoveryUntilUtc = null,
                HealCost = 0,
                RecoveryCost = 0,
                DiedInLastRaid = false,
                InjurySummary = "No treatment required.",
                UpdatedAtUtc = now,
            };
            await SaveMedicalAndActiveAsync(storageProfileId, state, alreadyRecovered, now, VanguardOperatorServiceStatuses.ActiveService);
            var billing = await billingService.GetBillingSnapshotAsync(storageProfileId);
            return new VanguardOperatorMedicalTreatmentResponse(true, requestedProfileId, storageProfileId, "already_recovered", medical.OperatorId, medical.DisplayName, before, 1.0, 0, null, billing, now, VanguardBuildVersion.BuildLabel);
        }

        var amount = CalculateAccelerationCost(medical, profile, now);
        var updatedMedical = medical with
        {
            Status = VanguardOperatorServiceStatuses.Available,
            CurrentHealthRatio = 1.0,
            RecoveryUntilUtc = null,
            HealCost = 0,
            RecoveryCost = 0,
            DiedInLastRaid = false,
            InjurySummary = "Recovered after paid field hospital acceleration.",
            UpdatedAtUtc = now,
        };

        await SaveMedicalAndActiveAsync(storageProfileId, state, updatedMedical, now, VanguardOperatorServiceStatuses.ActiveService);

        VanguardOperatorBillingInvoice? invoice = null;
        if (amount > 0)
        {
            invoice = await billingService.CreateInvoiceAsync(
                storageProfileId,
                VanguardOperatorBillingTypes.MedicalTreatment,
                medical.OperatorId,
                medical.DisplayName,
                null,
                amount,
                profile.CurrencyTpl,
                $"Field hospital treatment and recovery acceleration for {medical.DisplayName} recorded as Vanguard deferred billing.");
        }

        var billingSnapshot = await billingService.GetBillingSnapshotAsync(storageProfileId);
        return new VanguardOperatorMedicalTreatmentResponse(true, requestedProfileId, storageProfileId, "treated_and_recovered", medical.OperatorId, medical.DisplayName, before, updatedMedical.CurrentHealthRatio, amount, invoice, billingSnapshot, now, VanguardBuildVersion.BuildLabel);
    }

    private async Task SaveMedicalAndActiveAsync(string storageProfileId, VanguardOperatorStorageState state, VanguardOperatorMedicalRecord updatedMedical, DateTimeOffset now, string activeStatus)
    {
        var medicalRecords = state.Medical.Select(item => string.Equals(item.OperatorId, updatedMedical.OperatorId, StringComparison.OrdinalIgnoreCase) ? updatedMedical : item).ToArray();
        var activeService = state.ActiveService.Select(item =>
            string.Equals(item.OperatorId, updatedMedical.OperatorId, StringComparison.OrdinalIgnoreCase)
                ? item with { Status = activeStatus, RecoveryUntilUtc = updatedMedical.RecoveryUntilUtc }
                : item).ToArray();

        await store.SaveMedicalAsync(storageProfileId, medicalRecords);
        await store.SaveActiveServiceAsync(storageProfileId, activeService);
    }

    public static int CalculateAccelerationCost(VanguardOperatorMedicalRecord medical, VanguardOperatorProfile profile, DateTimeOffset now)
    {
        var missingHealthCost = (int)Math.Round(Math.Max(0.0, 1.0 - medical.CurrentHealthRatio) * 45000.0, MidpointRounding.AwayFromZero);
        var recoverySeconds = medical.RecoveryUntilUtc is DateTimeOffset until && until > now
            ? Math.Max(0, (int)(until - now).TotalSeconds)
            : 0;
        var recoveryCost = (recoverySeconds / 60) * 350;
        var deathCost = medical.DiedInLastRaid ? 35000 : 0;
        var levelCost = Math.Max(0, profile.Progression.Level - 1) * 420;
        var explicitCost = Math.Max(medical.HealCost + medical.RecoveryCost, 0);
        var computed = missingHealthCost + recoveryCost + deathCost + levelCost;
        return RoundTo500(Math.Max(explicitCost, computed));
    }

    public static VanguardOperatorMedicalRecord CreateRecoveryRecordFromRaidDamage(VanguardOperatorProfile profile, double healthRatio, bool died, DateTimeOffset now)
    {
        var clampedHealth = Math.Clamp(healthRatio, 0.05, 1.0);
        var severity = Math.Max(0.0, 1.0 - clampedHealth);
        var recoveryMinutes = died ? 180 : (int)Math.Ceiling(30 + severity * 160);
        var recoveryUntil = severity <= 0.001 && !died ? (DateTimeOffset?)null : now.AddMinutes(recoveryMinutes);
        return new VanguardOperatorMedicalRecord(
            profile.OperatorId,
            profile.Identity.DisplayName,
            recoveryUntil is null ? VanguardOperatorServiceStatuses.Available : VanguardOperatorServiceStatuses.Recovering,
            clampedHealth,
            recoveryUntil,
            RoundTo500((int)(severity * 45000)),
            recoveryUntil is null ? 0 : RoundTo500(recoveryMinutes * 350),
            died,
            died ? "Operator reported KIA and entered long recovery." : "Operator requires off-raid recovery.",
            now,
            VanguardOperatorSchema.CurrentVersion);
    }

    private static int RoundTo500(int value) => Math.Max(0, (int)Math.Round(value / 500.0, MidpointRounding.AwayFromZero) * 500);
}

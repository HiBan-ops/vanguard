using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Common.Extensions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Utils;
using Vanguard.Server.Operators.Inventory.Models;
using Vanguard.Server.Operators.Inventory.Responses;
using Vanguard.Server.Operators.Raid.Persistence.Models;
using Vanguard.Server.Operators.Models;
using Vanguard.Server.Operators.Storage;
using Vanguard.Server.Diagnostics;

// Responsibility: owns the temporary server-side inventory editing session that maps one selected Operator into the player inventory UI and commits validated changes back to the Operator profile.
// Flow: Enter snapshots the real player profile and exposes one Operator equipment tree through SPT’s native inventory routes; commits validate and copy only allowed changes back; exit restores the player profile and closes the session even after a recoverable commit failure.
// Authority boundary: the Operator store is persistence authority; SPT profile services are used only to present/restore the player editing surface, and wallet/economy state is never synthesized here.
// Invariant: enter/commit/exit are profile-locked and recoverable; a successful exit must converge the editing session inactive even when the best-effort direct commit reports a separate failure.

namespace Vanguard.Server.Operators.Inventory.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorInventoryModeService(
    VanguardOperatorStore operatorStore,
    SaveServer saveServer,
    VanguardSpt40LostOnDeathConfigProvider lostOnDeathConfigProvider,
    PaymentHelper paymentHelper,
    JsonUtil jsonUtil,
    ISptLogger<VanguardOperatorInventoryModeService> logger)
{
    private readonly ConcurrentDictionary<string, VanguardOperatorInventoryModeSession> activeSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> profileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MongoId>> playerPurchaseCurrencyAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<int> redirectBypassDepth = new();
    private readonly LostOnDeathConfig lostOnDeathConfig = lostOnDeathConfigProvider.Value;

    public bool IsRedirectBypassed => redirectBypassDepth.Value > 0;

    public async Task<VanguardOperatorInventoryModeResponse> EnterAsync(MongoId requestedProfileId, string? operatorId, bool confirm)
    {
        string requested = requestedProfileId.ToString();
        string storageProfileId = await operatorStore.ResolveStorageProfileIdAsync(requested);
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return Failure(requested, storageProfileId, operatorId, "operator_id_required");
        }

        if (!confirm)
        {
            return Failure(requested, storageProfileId, operatorId, "confirmation_required");
        }

        var operators = await operatorStore.LoadOperatorsAsync(storageProfileId);
        VanguardOperatorProfile? operatorProfile = operators.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        if (operatorProfile == null)
        {
            return Failure(requested, storageProfileId, operatorId, "operator_not_found");
        }

        var activeService = await operatorStore.LoadActiveServiceAsync(storageProfileId);
        if (!activeService.Any(record => string.Equals(record.OperatorId, operatorProfile.OperatorId, StringComparison.OrdinalIgnoreCase)))
        {
            return Failure(requested, storageProfileId, operatorProfile.OperatorId, "operator_not_in_active_service");
        }

        SemaphoreSlim guard = GetProfileLock(storageProfileId, operatorProfile.OperatorId);
        await guard.WaitAsync();
        try
        {
            SptProfile operatorPersistentProfile = await LoadOrCreateOperatorProfileAsync(requestedProfileId, storageProfileId, operatorProfile);
            ValidateOperatorProfileOrThrow(operatorPersistentProfile, storageProfileId, operatorProfile.OperatorId, "persistent_enter");

            JsonObject sessionProfileNode = BuildEquipmentSessionProfileNode(requestedProfileId, storageProfileId, operatorProfile, operatorPersistentProfile);
            SptProfile sessionProfile = NodeToProfile(sessionProfileNode);
            ValidateOperatorProfileOrThrow(sessionProfile, storageProfileId, operatorProfile.OperatorId, "session_enter");

            string inventoryProfileId = ResolveInventoryProfileId(operatorPersistentProfile, storageProfileId, operatorProfile.OperatorId);
            string profilePath = operatorStore.GetOperatorInventoryProfilePath(storageProfileId, operatorProfile.OperatorId);
            string displayName = ResolveDisplayName(operatorProfile);
            var session = new VanguardOperatorInventoryModeSession(
                requestedProfileId,
                storageProfileId,
                operatorProfile.OperatorId,
                displayName,
                FirstNonEmpty(operatorProfile.Identity.Callsign, displayName),
                inventoryProfileId,
                profilePath,
                sessionProfile,
                sessionProfileNode,
                DateTimeOffset.UtcNow);
            playerPurchaseCurrencyAliases.TryRemove(requested, out _);
            activeSessions.AddOrUpdate(requested, session, (_, _) => session);

            await CommitSessionAsync(session);
            VanguardOperatorInventorySummary summary = BuildSummary(session.OperatorId, session.OperatorDisplayName, session.OperatorInventoryProfileId, session.ProfilePath, operatorPersistentProfile);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_EQUIPMENT_SESSION_STATUS] enter requested={requested}; storage={storageProfileId}; operator={session.OperatorId}; sessionProfile=operator-equipment-player-stash; inventoryProfile={session.OperatorInventoryProfileId}; path={session.ProfilePath}"));
            return new VanguardOperatorInventoryModeResponse
            {
                Success = true,
                Reason = "entered_equipment_session",
                Active = true,
                RequestedProfileId = requested,
                StorageProfileId = storageProfileId,
                OperatorId = session.OperatorId,
                OperatorDisplayName = session.OperatorDisplayName,
                OperatorCallsign = session.OperatorCallsign,
                OperatorInventoryProfileId = session.OperatorInventoryProfileId,
                Summary = summary
            };
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_EQUIPMENT_SESSION_STATUS] enter failed requested={requested}; storage={storageProfileId}; operator={operatorId}; type={exception.GetType().Name}; message={exception.Message}"));
            return Failure(requested, storageProfileId, operatorId, "equipment_session_enter_failed_" + exception.GetType().Name);
        }
        finally
        {
            guard.Release();
        }
    }

    public async Task<VanguardOperatorInventoryModeResponse> ExitAsync(MongoId requestedProfileId, string? operatorId)
    {
        string requested = requestedProfileId.ToString();
        string storageProfileId = await operatorStore.ResolveStorageProfileIdAsync(requested);
        if (!activeSessions.TryGetValue(requested, out VanguardOperatorInventoryModeSession? session))
        {
            return new VanguardOperatorInventoryModeResponse
            {
                Success = true,
                Reason = "already_in_player_profile",
                Active = false,
                RequestedProfileId = requested,
                StorageProfileId = storageProfileId,
                OperatorId = operatorId
            };
        }

        if (!string.IsNullOrWhiteSpace(operatorId) && !string.Equals(session.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(requested, storageProfileId, operatorId, "active_operator_mismatch");
        }

        SemaphoreSlim guard = GetProfileLock(session.StorageProfileId, session.OperatorId);
        await guard.WaitAsync();
        try
        {
            await CommitSessionAsync(session);
            activeSessions.TryRemove(requested, out _);
            playerPurchaseCurrencyAliases.TryRemove(requested, out _);
            SptProfile? operatorProfile = await TryLoadInventoryProfileAsync(session.ProfilePath);
            VanguardOperatorInventorySummary summary = BuildSummary(session.OperatorId, session.OperatorDisplayName, session.OperatorInventoryProfileId, session.ProfilePath, operatorProfile);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_EQUIPMENT_SESSION_STATUS] exit requested={requested}; storage={session.StorageProfileId}; operator={session.OperatorId}; inventoryProfile={session.OperatorInventoryProfileId}; committed=true"));
            return new VanguardOperatorInventoryModeResponse
            {
                Success = true,
                Reason = "exited_equipment_session_committed",
                Active = false,
                RequestedProfileId = requested,
                StorageProfileId = session.StorageProfileId,
                OperatorId = session.OperatorId,
                OperatorDisplayName = session.OperatorDisplayName,
                OperatorCallsign = session.OperatorCallsign,
                OperatorInventoryProfileId = session.OperatorInventoryProfileId,
                Summary = summary
            };
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_EQUIPMENT_SESSION_STATUS] exit commit failed requested={requested}; storage={session.StorageProfileId}; operator={session.OperatorId}; type={exception.GetType().Name}; message={exception.Message}"));
            return Failure(requested, session.StorageProfileId, session.OperatorId, "equipment_session_exit_commit_failed_" + exception.GetType().Name);
        }
        finally
        {
            guard.Release();
        }
    }


    public async Task<VanguardOperatorInventoryModeResponse> DirectCommitAsync(MongoId requestedProfileId, string? operatorId, bool confirm, string? profileDescriptorJson, string? snapshotSource, int clientItemCount)
    {
        string requested = requestedProfileId.ToString();
        string storageProfileId = await operatorStore.ResolveStorageProfileIdAsync(requested);
        if (!confirm)
        {
            return Failure(requested, storageProfileId, operatorId, "confirmation_required");
        }

        if (!activeSessions.TryGetValue(requested, out VanguardOperatorInventoryModeSession? session))
        {
            return Failure(requested, storageProfileId, operatorId, "direct_commit_no_active_session");
        }

        if (!string.IsNullOrWhiteSpace(operatorId) && !string.Equals(session.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(requested, storageProfileId, operatorId, "active_operator_mismatch");
        }

        if (!TryExtractDirectCommitInventory(profileDescriptorJson, out JsonObject? snapshotInventory, out string snapshotReason) || snapshotInventory == null)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS] direct_commit_snapshot_rejected requested={requested}; storage={session.StorageProfileId}; operator={session.OperatorId}; reason={snapshotReason}; source={snapshotSource ?? "<none>"}; clientItems={clientItemCount}"));
            return Failure(requested, session.StorageProfileId, session.OperatorId, "direct_commit_snapshot_invalid_" + snapshotReason);
        }

        SemaphoreSlim guard = GetProfileLock(session.StorageProfileId, session.OperatorId);
        await guard.WaitAsync();
        try
        {
            JsonObject sessionNode = ProfileToNode(session.Profile);
            JsonObject sessionPmc = GetPmcObject(sessionNode);
            ReplaceInventory(sessionPmc, CloneObject(snapshotInventory));
            NormalizeCompleteSessionProfileNode(sessionNode, session.StorageProfileId, session.OperatorId, "direct_commit_snapshot");

            JsonObject normalizedInventory = GetInventoryObject(sessionNode);
            string audit = AuditInventoryTree(normalizedInventory);
            if (audit != "ok")
            {
                logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS] direct_commit_snapshot_audit_failed requested={requested}; storage={session.StorageProfileId}; operator={session.OperatorId}; audit={audit}; source={snapshotSource ?? "<none>"}; clientItems={clientItemCount}"));
                return Failure(requested, session.StorageProfileId, session.OperatorId, "direct_commit_snapshot_audit_" + audit);
            }

            SptProfile updatedSessionProfile = NodeToProfile(sessionNode);
            VanguardOperatorInventoryModeSession updatedSession = session with
            {
                Profile = updatedSessionProfile,
                ClientSessionProfileNode = CloneObject(sessionNode)
            };
            activeSessions[requested] = updatedSession;

            await CommitSessionAsync(updatedSession);
            SptProfile? operatorProfile = await TryLoadInventoryProfileAsync(updatedSession.ProfilePath);
            VanguardOperatorInventorySummary summary = BuildSummary(updatedSession.OperatorId, updatedSession.OperatorDisplayName, updatedSession.OperatorInventoryProfileId, updatedSession.ProfilePath, operatorProfile);
            int snapshotItems = GetItemsArray(normalizedInventory).Count;
            string equipmentId = GetString(normalizedInventory, "equipment") ?? "<none>";
            int equipmentTreeItems = CountTreeItems(normalizedInventory, equipmentId);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS] direct_commit_operator_equipment_tree_saved requested={requested}; storage={updatedSession.StorageProfileId}; operator={updatedSession.OperatorId}; inventoryProfile={updatedSession.OperatorInventoryProfileId}; source={snapshotSource ?? "<none>"}; clientItems={clientItemCount}; snapshotItems={snapshotItems}; equipment={equipmentId}; equipmentTreeItems={equipmentTreeItems}; path={updatedSession.ProfilePath}"));
            return new VanguardOperatorInventoryModeResponse
            {
                Success = true,
                Reason = "direct_commit_saved",
                Active = true,
                RequestedProfileId = requested,
                StorageProfileId = updatedSession.StorageProfileId,
                OperatorId = updatedSession.OperatorId,
                OperatorDisplayName = updatedSession.OperatorDisplayName,
                OperatorCallsign = updatedSession.OperatorCallsign,
                OperatorInventoryProfileId = updatedSession.OperatorInventoryProfileId,
                Summary = summary
            };
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS] direct_commit_failed requested={requested}; storage={session.StorageProfileId}; operator={session.OperatorId}; type={exception.GetType().Name}; message={exception.Message}"));
            return Failure(requested, session.StorageProfileId, session.OperatorId, "direct_commit_failed_" + exception.GetType().Name);
        }
        finally
        {
            guard.Release();
        }
    }

    public bool TryPrepareRaidInventorySnapshot(
        string? profileDescriptorJson,
        out VanguardRaidInventoryPreparedSnapshot? prepared,
        out string reason)
    {
        prepared = null;
        if (!TryExtractDirectCommitInventory(profileDescriptorJson, out JsonObject? snapshotInventory, out string snapshotReason) || snapshotInventory == null)
        {
            reason = snapshotReason;
            return false;
        }

        string equipmentId = GetString(snapshotInventory, "equipment") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(equipmentId))
        {
            reason = "equipment_root_missing";
            return false;
        }

        string audit = AuditRaidEquipmentSnapshot(snapshotInventory, equipmentId);
        if (audit != "ok")
        {
            reason = audit;
            return false;
        }

        JsonArray items = GetItemsArray(snapshotInventory);
        HashSet<string> treeIds = CollectTreeIds(items, equipmentId);
        string[] equipmentItemIds = treeIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        prepared = new VanguardRaidInventoryPreparedSnapshot(
            CloneObject(snapshotInventory),
            equipmentId,
            equipmentItemIds,
            items.Count,
            equipmentItemIds.Length,
            ComputeEquipmentTreeFingerprint(snapshotInventory, equipmentId));
        reason = "ok";
        return true;
    }

    /// <summary>
    /// Captures EFT's final Skills descriptor as a separate progression truth.
    /// The snapshot is observational until the surrounding raid batch is admitted.
    /// </summary>
    public bool TryPrepareRaidSkillSnapshot(
        string? profileDescriptorJson,
        out VanguardRaidSkillPreparedSnapshot? prepared,
        out string reason)
    {
        prepared = null;
        if (!TryExtractProfileDescriptorField(profileDescriptorJson, "Skills", out JsonObject? skills, out string source) || skills == null)
        {
            reason = source;
            return false;
        }

        if (!TryGetArray(skills, "Common", out JsonArray? common) || common == null)
        {
            reason = "skills_common_missing";
            return false;
        }
        if (!TryGetArray(skills, "Mastering", out JsonArray? mastering) || mastering == null)
        {
            reason = "skills_mastering_missing";
            return false;
        }

        var commonIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in common)
        {
            if (node is not JsonObject skill)
            {
                reason = "skills_common_entry_not_object";
                return false;
            }

            string id = GetString(skill, "Id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || !commonIds.Add(id))
            {
                reason = string.IsNullOrWhiteSpace(id) ? "skills_common_id_missing" : "skills_common_duplicate_id_" + id;
                return false;
            }
            if (!TryReadFiniteDouble(skill, "Progress", out double progress) || progress < 0.0 || progress > 5100.0001)
            {
                reason = "skills_common_progress_invalid_" + id;
                return false;
            }
            if (!TryReadFiniteDouble(skill, "PointsEarnedDuringSession", out double points) || points < 0.0)
            {
                reason = "skills_common_session_points_invalid_" + id;
                return false;
            }
            if (!TryReadNonNegativeInt64(skill, "LastAccess", out _))
            {
                reason = "skills_common_last_access_invalid_" + id;
                return false;
            }
        }

        var masteringIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in mastering)
        {
            if (node is not JsonObject skill)
            {
                reason = "skills_mastering_entry_not_object";
                return false;
            }

            string id = GetString(skill, "Id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || !masteringIds.Add(id))
            {
                reason = string.IsNullOrWhiteSpace(id) ? "skills_mastering_id_missing" : "skills_mastering_duplicate_id_" + id;
                return false;
            }
            if (!TryReadFiniteDouble(skill, "Progress", out double progress) || progress < 0.0)
            {
                reason = "skills_mastering_progress_invalid_" + id;
                return false;
            }
        }

        prepared = new VanguardRaidSkillPreparedSnapshot(
            CloneObject(skills),
            common.Count,
            mastering.Count,
            ComputeSkillStateFingerprint(skills, includeSessionPoints: true));
        reason = source;
        return true;
    }

    /// <summary>
    /// Apply the same equipment-loss policy SPT 4.0 uses for a dead PMC to a
    /// runtime Operator snapshot.  The runtime snapshot remains the
    /// forensic truth used to reconcile the actual corpse before this method is
    /// called; the returned snapshot is the distinct post-death persistence truth.
    ///
    /// SPT semantics mirrored here:
    /// - inventory roots are kept;
    /// - direct Equipment children use LostOnDeathConfig.Equipment by slot id;
    /// - contents of Pockets are removed when PocketItems is configured lost;
    /// - removing a container removes its entire child tree;
    /// - everything not selected by those SPT loss rules remains untouched.
    /// </summary>
    public bool TryPrepareKiaRaidInventorySnapshot(
        VanguardRaidInventoryPreparedSnapshot runtimeSnapshot,
        out VanguardRaidInventoryPreparedSnapshot? prepared,
        out string reason)
    {
        prepared = null;
        JsonObject snapshotInventory = CloneObject(runtimeSnapshot.SnapshotInventory);
        string equipmentId = GetString(snapshotInventory, "equipment") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(equipmentId)
            || !string.Equals(equipmentId, runtimeSnapshot.EquipmentId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "kia_equipment_root_mismatch";
            return false;
        }

        JsonArray items = GetItemsArray(snapshotInventory);
        HashSet<string> equipmentTreeIds = CollectTreeIds(items, equipmentId);
        var deleteRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonObject item in items.OfType<JsonObject>())
        {
            string? id = GetItemId(item);
            if (string.IsNullOrWhiteSpace(id)
                || string.Equals(id, equipmentId, StringComparison.OrdinalIgnoreCase)
                || !equipmentTreeIds.Contains(id))
            {
                continue;
            }

            string? parentId = GetString(item, "parentId");
            string slotId = GetString(item, "slotId") ?? string.Empty;

            // Mirrors InRaidHelper.IsItemKeptAfterDeath for direct Equipment slots:
            // true in LostOnDeathConfig means discard; missing/false means keep.
            if (string.Equals(parentId, equipmentId, StringComparison.OrdinalIgnoreCase))
            {
                bool discard = lostOnDeathConfig.Equipment.GetByJsonProperty<bool>(slotId);
                if (discard)
                {
                    deleteRoots.Add(id);
                }
                continue;
            }

            // SPT treats the Pockets container as kept, while each pocket payload
            // is independently lost when PocketItems=true.  RemoveItem then
            // recursively removes children of the selected pocket payload.
            if (slotId.StartsWith("pocket", StringComparison.OrdinalIgnoreCase)
                && lostOnDeathConfig.Equipment.PocketItems)
            {
                deleteRoots.Add(id);
            }
        }

        if (deleteRoots.Count > 0)
        {
            var deleteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string deleteRoot in deleteRoots)
            {
                deleteIds.UnionWith(CollectTreeIds(items, deleteRoot));
            }

            for (int index = items.Count - 1; index >= 0; index--)
            {
                if (items[index] is not JsonObject item)
                {
                    continue;
                }

                string? id = GetItemId(item);
                if (!string.IsNullOrWhiteSpace(id) && deleteIds.Contains(id))
                {
                    items.RemoveAt(index);
                }
            }
        }

        // SPT clears PMC FastPanel on death after pruning the inventory.  Keep the
        // Operator persistence profile aligned so no quick-slot can reference an
        // item that was removed by LostOnDeathConfig.
        snapshotInventory[FindPropertyName(snapshotInventory, "fastPanel") ?? "fastPanel"] = new JsonObject();

        string audit = AuditRaidEquipmentSnapshot(snapshotInventory, equipmentId);
        if (audit != "ok")
        {
            reason = "kia_snapshot_" + audit;
            return false;
        }

        HashSet<string> persistentTreeIds = CollectTreeIds(items, equipmentId);
        string[] persistentItemIds = persistentTreeIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        prepared = new VanguardRaidInventoryPreparedSnapshot(
            snapshotInventory,
            equipmentId,
            persistentItemIds,
            items.Count,
            persistentItemIds.Length,
            ComputeEquipmentTreeFingerprint(snapshotInventory, equipmentId));
        reason = "ok";
        return true;
    }

    public async Task<VanguardRaidInventoryCommitResult> CommitRaidInventorySnapshotAsync(
        string storageProfileId,
        string operatorId,
        VanguardRaidInventoryPreparedSnapshot prepared)
    {
        string profilePath = operatorStore.GetOperatorInventoryProfilePath(storageProfileId, operatorId);
        SemaphoreSlim guard = GetProfileLock(storageProfileId, operatorId);
        await guard.WaitAsync();
        try
        {
            SptProfile? operatorProfile = await TryLoadInventoryProfileAsync(profilePath);
            if (operatorProfile == null)
            {
                return new VanguardRaidInventoryCommitResult(false, "persistent_inventory_profile_missing", storageProfileId, operatorId, 0, string.Empty, profilePath);
            }

            JsonObject operatorNode = ProfileToNode(operatorProfile);
            JsonObject operatorInventory = GetInventoryObject(operatorNode);
            JsonObject mergedInventory = BuildOperatorInventoryForCommit(operatorInventory, prepared.SnapshotInventory);
            string mergedAudit = AuditInventoryTree(mergedInventory);
            if (mergedAudit != "ok")
            {
                return new VanguardRaidInventoryCommitResult(false, "merged_inventory_audit_" + mergedAudit, storageProfileId, operatorId, 0, string.Empty, profilePath);
            }

            ReplaceInventory(GetPmcObject(operatorNode), mergedInventory);
            SptProfile updatedOperatorProfile = NodeToProfile(operatorNode);

            // SPT's strongly typed profile serializer is the persistence boundary.  It may normalize
            // optional/default JSON members even when the Equipment item graph is semantically
            // unchanged.  Establish the expected read-back fingerprint only after that canonical
            // round-trip, while preserving the runtime snapshot's exact Equipment ItemId set as the
            // non-negotiable anti-loss/anti-duplication invariant.
            JsonObject normalizedOperatorNode = ProfileToNode(updatedOperatorProfile);
            JsonObject normalizedExpectedInventory = GetInventoryObject(normalizedOperatorNode);
            string normalizedAudit = AuditInventoryTree(normalizedExpectedInventory);
            if (normalizedAudit != "ok")
            {
                return new VanguardRaidInventoryCommitResult(false, "normalized_inventory_audit_" + normalizedAudit, storageProfileId, operatorId, 0, string.Empty, profilePath);
            }

            string normalizedEquipmentId = GetString(normalizedExpectedInventory, "equipment") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedEquipmentId)
                || !string.Equals(normalizedEquipmentId, prepared.EquipmentId, StringComparison.OrdinalIgnoreCase))
            {
                return new VanguardRaidInventoryCommitResult(false, "normalized_equipment_root_mismatch", storageProfileId, operatorId, 0, string.Empty, profilePath);
            }

            HashSet<string> normalizedEquipmentItemIds = CollectTreeIds(GetItemsArray(normalizedExpectedInventory), normalizedEquipmentId);
            if (normalizedEquipmentItemIds.Count != prepared.EquipmentItemCount
                || !normalizedEquipmentItemIds.SetEquals(prepared.EquipmentItemIds))
            {
                return new VanguardRaidInventoryCommitResult(false, "normalized_equipment_item_ids_mismatch", storageProfileId, operatorId, normalizedEquipmentItemIds.Count, string.Empty, profilePath);
            }

            string normalizedSemanticAudit = AuditEquipmentIdentityAndTopologyPreserved(
                prepared.SnapshotInventory,
                normalizedExpectedInventory,
                prepared.EquipmentId);
            if (normalizedSemanticAudit != "ok")
            {
                return new VanguardRaidInventoryCommitResult(false, "normalized_equipment_semantic_" + normalizedSemanticAudit, storageProfileId, operatorId, normalizedEquipmentItemIds.Count, string.Empty, profilePath);
            }

            string expectedNormalizedFingerprint = ComputeEquipmentTreeFingerprint(normalizedExpectedInventory, normalizedEquipmentId);
            bool normalizationChanged = !string.Equals(expectedNormalizedFingerprint, prepared.EquipmentFingerprint, StringComparison.OrdinalIgnoreCase);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_PERSISTENCE_NORMALIZATION_STATUS] prewrite storage={storageProfileId}; operator={operatorId}; equipment={normalizedEquipmentId}; equipmentItems={normalizedEquipmentItemIds.Count}; runtimeFingerprint={prepared.EquipmentFingerprint}; normalizedFingerprint={expectedNormalizedFingerprint}; normalizationChanged={normalizationChanged.ToString().ToLowerInvariant()}; exactRuntimeItemIdsPreserved=true; writeStarted=false"));

            await SaveProfileToPathAsync(profilePath, updatedOperatorProfile);

            SptProfile? readBackProfile = await TryLoadInventoryProfileAsync(profilePath);
            if (readBackProfile == null)
            {
                return new VanguardRaidInventoryCommitResult(false, "readback_profile_missing", storageProfileId, operatorId, 0, string.Empty, profilePath);
            }

            JsonObject readBackInventory = GetInventoryObject(ProfileToNode(readBackProfile));
            string readBackAudit = AuditInventoryTree(readBackInventory);
            if (readBackAudit != "ok")
            {
                return new VanguardRaidInventoryCommitResult(false, "readback_inventory_audit_" + readBackAudit, storageProfileId, operatorId, 0, string.Empty, profilePath);
            }

            string readBackEquipmentId = GetString(readBackInventory, "equipment") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(readBackEquipmentId)
                || !string.Equals(readBackEquipmentId, normalizedEquipmentId, StringComparison.OrdinalIgnoreCase))
            {
                return new VanguardRaidInventoryCommitResult(false, "readback_equipment_root_mismatch", storageProfileId, operatorId, 0, string.Empty, profilePath);
            }

            HashSet<string> readBackEquipmentItemIds = CollectTreeIds(GetItemsArray(readBackInventory), readBackEquipmentId);
            int readBackEquipmentItemCount = readBackEquipmentItemIds.Count;
            string readBackFingerprint = ComputeEquipmentTreeFingerprint(readBackInventory, readBackEquipmentId);
            if (readBackEquipmentItemCount != prepared.EquipmentItemCount
                || !readBackEquipmentItemIds.SetEquals(prepared.EquipmentItemIds))
            {
                return new VanguardRaidInventoryCommitResult(false, "readback_equipment_item_ids_mismatch", storageProfileId, operatorId, readBackEquipmentItemCount, readBackFingerprint, profilePath);
            }

            if (!string.Equals(readBackFingerprint, expectedNormalizedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return new VanguardRaidInventoryCommitResult(false, "readback_equipment_tree_mismatch_after_spt_normalization", storageProfileId, operatorId, readBackEquipmentItemCount, readBackFingerprint, profilePath);
            }

            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_PERSISTENCE_STATUS] operator_inventory_commit storage={storageProfileId}; operator={operatorId}; equipment={readBackEquipmentId}; equipmentItems={readBackEquipmentItemCount}; runtimeFingerprint={prepared.EquipmentFingerprint}; normalizedFingerprint={expectedNormalizedFingerprint}; readbackFingerprint={readBackFingerprint}; normalizationChanged={normalizationChanged.ToString().ToLowerInvariant()}; exactRuntimeItemIdsPreserved=true; path={profilePath}; readback=true"));
            return new VanguardRaidInventoryCommitResult(true, "committed_readback_verified_spt_normalized", storageProfileId, operatorId, readBackEquipmentItemCount, readBackFingerprint, profilePath);
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[VANGUARD_RAID_PERSISTENCE_STATUS] operator_inventory_commit_failed storage={storageProfileId}; operator={operatorId}; type={exception.GetType().Name}; message={exception.Message}"));
            return new VanguardRaidInventoryCommitResult(false, "commit_exception_" + exception.GetType().Name, storageProfileId, operatorId, 0, string.Empty, profilePath);
        }
        finally
        {
            guard.Release();
        }
    }

    /// <summary>
    /// Persists the final EFT skill state with forward-only guards and read-back verification.
    /// Common PointsEarnedDuringSession is evidence for the completed raid and is reset to zero
    /// before the profile can seed another raid.  No skill value is synthesized from Career XP.
    /// </summary>
    public async Task<VanguardRaidSkillCommitResult> CommitRaidSkillSnapshotAsync(
        string storageProfileId,
        string operatorId,
        VanguardRaidSkillPreparedSnapshot prepared)
    {
        const double tolerance = 0.0001;
        string profilePath = operatorStore.GetOperatorInventoryProfilePath(storageProfileId, operatorId);
        SemaphoreSlim guard = GetProfileLock(storageProfileId, operatorId);
        await guard.WaitAsync();
        try
        {
            SptProfile? operatorProfile = await TryLoadInventoryProfileAsync(profilePath);
            if (operatorProfile == null)
            {
                return SkillCommitFailure("persistent_operator_profile_missing", storageProfileId, operatorId, prepared, profilePath);
            }

            JsonObject operatorNode = ProfileToNode(operatorProfile);
            JsonObject pmc = GetPmcObject(operatorNode);
            JsonObject persistentSkills = GetOrCreateObject(pmc, "Skills");
            JsonArray persistentCommon = GetOrCreateArray(persistentSkills, "Common");
            JsonArray persistentMastering = GetOrCreateArray(persistentSkills, "Mastering");
            JsonArray runtimeCommon = GetOrCreateArray(prepared.SnapshotSkills, "Common");
            JsonArray runtimeMastering = GetOrCreateArray(prepared.SnapshotSkills, "Mastering");

            Dictionary<string, JsonObject> persistentCommonById = BuildSkillMap(persistentCommon);
            Dictionary<string, JsonObject> persistentMasteringById = BuildSkillMap(persistentMastering);
            int commonProgressed = 0;
            int masteringProgressed = 0;
            double commonDelta = 0.0;
            double masteringDelta = 0.0;

            foreach (JsonObject runtimeSkill in runtimeCommon.OfType<JsonObject>())
            {
                string id = GetString(runtimeSkill, "Id") ?? string.Empty;
                if (!TryReadFiniteDouble(runtimeSkill, "Progress", out double runtimeProgress)
                    || !TryReadFiniteDouble(runtimeSkill, "PointsEarnedDuringSession", out _)
                    || !TryReadNonNegativeInt64(runtimeSkill, "LastAccess", out long runtimeLastAccess))
                {
                    return SkillCommitFailure("runtime_common_semantics_invalid_" + id, storageProfileId, operatorId, prepared, profilePath);
                }

                if (!persistentCommonById.TryGetValue(id, out JsonObject? persistentSkill))
                {
                    persistentSkill = CloneObject(runtimeSkill);
                    SetJsonNumber(persistentSkill, "PointsEarnedDuringSession", 0.0);
                    persistentCommon.Add(persistentSkill);
                    persistentCommonById[id] = persistentSkill;
                    if (runtimeProgress > tolerance)
                    {
                        commonProgressed++;
                        commonDelta += runtimeProgress;
                    }
                    continue;
                }

                if (!TryReadFiniteDouble(persistentSkill, "Progress", out double persistentProgress) || persistentProgress < 0.0 || persistentProgress > 5100.0001)
                {
                    return SkillCommitFailure("persistent_common_progress_invalid_" + id, storageProfileId, operatorId, prepared, profilePath);
                }
                if (!TryReadNonNegativeInt64(persistentSkill, "LastAccess", out long persistentLastAccess))
                {
                    return SkillCommitFailure("persistent_common_last_access_invalid_" + id, storageProfileId, operatorId, prepared, profilePath);
                }
                if (runtimeProgress + tolerance < persistentProgress)
                {
                    return SkillCommitFailure("common_progress_regression_" + id, storageProfileId, operatorId, prepared, profilePath);
                }
                if (runtimeLastAccess < persistentLastAccess)
                {
                    return SkillCommitFailure("common_last_access_regression_" + id, storageProfileId, operatorId, prepared, profilePath);
                }

                double delta = Math.Max(0.0, runtimeProgress - persistentProgress);
                if (delta > tolerance)
                {
                    commonProgressed++;
                    commonDelta += delta;
                }
                SetJsonNumber(persistentSkill, "Progress", runtimeProgress);
                SetJsonValue(persistentSkill, "LastAccess", runtimeLastAccess);
                SetJsonNumber(persistentSkill, "PointsEarnedDuringSession", 0.0);
            }

            foreach (JsonObject runtimeSkill in runtimeMastering.OfType<JsonObject>())
            {
                string id = GetString(runtimeSkill, "Id") ?? string.Empty;
                if (!TryReadFiniteDouble(runtimeSkill, "Progress", out double runtimeProgress))
                {
                    return SkillCommitFailure("runtime_mastering_semantics_invalid_" + id, storageProfileId, operatorId, prepared, profilePath);
                }

                if (!persistentMasteringById.TryGetValue(id, out JsonObject? persistentSkill))
                {
                    persistentSkill = CloneObject(runtimeSkill);
                    persistentMastering.Add(persistentSkill);
                    persistentMasteringById[id] = persistentSkill;
                    if (runtimeProgress > tolerance)
                    {
                        masteringProgressed++;
                        masteringDelta += runtimeProgress;
                    }
                    continue;
                }

                if (!TryReadFiniteDouble(persistentSkill, "Progress", out double persistentProgress) || persistentProgress < 0.0)
                {
                    return SkillCommitFailure("persistent_mastering_progress_invalid_" + id, storageProfileId, operatorId, prepared, profilePath);
                }
                if (runtimeProgress + tolerance < persistentProgress)
                {
                    return SkillCommitFailure("mastering_progress_regression_" + id, storageProfileId, operatorId, prepared, profilePath);
                }

                double delta = Math.Max(0.0, runtimeProgress - persistentProgress);
                if (delta > tolerance)
                {
                    masteringProgressed++;
                    masteringDelta += delta;
                }
                SetJsonNumber(persistentSkill, "Progress", runtimeProgress);
            }

            // Session points are never a durable seed. Reset every persisted Common entry, including
            // legacy/extra entries that were not emitted by this runtime descriptor.
            foreach (JsonObject persistentSkill in persistentCommon.OfType<JsonObject>())
            {
                SetJsonNumber(persistentSkill, "PointsEarnedDuringSession", 0.0);
            }

            SptProfile updatedOperatorProfile = NodeToProfile(operatorNode);
            JsonObject normalizedNode = ProfileToNode(updatedOperatorProfile);
            JsonObject normalizedSkills = GetOrCreateObject(GetPmcObject(normalizedNode), "Skills");
            string expectedFingerprint = ComputeSkillStateFingerprint(normalizedSkills, includeSessionPoints: false);

            await SaveProfileToPathAsync(profilePath, updatedOperatorProfile);
            SptProfile? readBackProfile = await TryLoadInventoryProfileAsync(profilePath);
            if (readBackProfile == null)
            {
                return SkillCommitFailure("readback_profile_missing", storageProfileId, operatorId, prepared, profilePath);
            }

            JsonObject readBackSkills = GetOrCreateObject(GetPmcObject(ProfileToNode(readBackProfile)), "Skills");
            string readBackFingerprint = ComputeSkillStateFingerprint(readBackSkills, includeSessionPoints: false);
            if (!string.Equals(readBackFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return new VanguardRaidSkillCommitResult(false, "readback_skill_state_mismatch", storageProfileId, operatorId,
                    prepared.CommonSkillCount, commonProgressed, commonDelta, prepared.MasteringSkillCount, masteringProgressed,
                    masteringDelta, prepared.RuntimeFingerprint, readBackFingerprint, profilePath);
            }

            if (!RuntimeSkillIdsPersistedWithSessionReset(prepared.SnapshotSkills, readBackSkills, tolerance, out string semanticReason))
            {
                return new VanguardRaidSkillCommitResult(false, "readback_" + semanticReason, storageProfileId, operatorId,
                    prepared.CommonSkillCount, commonProgressed, commonDelta, prepared.MasteringSkillCount, masteringProgressed,
                    masteringDelta, prepared.RuntimeFingerprint, readBackFingerprint, profilePath);
            }

            logger.Info(VanguardServerDiagnosticsLog.Present($"[{VanguardBuildVersion.OperatorSkillAndMasteryPersistenceStatusTag}] phase=skill_commit; storage={storageProfileId}; operator={operatorId}; common={prepared.CommonSkillCount}; commonProgressed={commonProgressed}; commonDelta={commonDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}; mastering={prepared.MasteringSkillCount}; masteringProgressed={masteringProgressed}; masteringDelta={masteringDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}; sessionPointsReset=true; runtimeFingerprint={prepared.RuntimeFingerprint}; persistentFingerprint={readBackFingerprint}; readback=true; forwardOnly=true; tag={VanguardBuildVersion.OperatorSkillAndMasteryPersistenceStatusTag}"));
            return new VanguardRaidSkillCommitResult(true, "committed_readback_verified_forward_only", storageProfileId, operatorId,
                prepared.CommonSkillCount, commonProgressed, commonDelta, prepared.MasteringSkillCount, masteringProgressed,
                masteringDelta, prepared.RuntimeFingerprint, readBackFingerprint, profilePath);
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present($"[{VanguardBuildVersion.OperatorSkillAndMasteryPersistenceStatusTag}] phase=skill_commit; storage={storageProfileId}; operator={operatorId}; success=false; type={exception.GetType().Name}; message={exception.Message}; tag={VanguardBuildVersion.OperatorSkillAndMasteryPersistenceStatusTag}"));
            return SkillCommitFailure("commit_exception_" + exception.GetType().Name, storageProfileId, operatorId, prepared, profilePath);
        }
        finally
        {
            guard.Release();
        }
    }

    public async Task<VanguardOperatorInventoryModeResponse> GetStatusAsync(MongoId requestedProfileId)
    {
        string requested = requestedProfileId.ToString();
        string storageProfileId = await operatorStore.ResolveStorageProfileIdAsync(requested);
        if (!activeSessions.TryGetValue(requested, out VanguardOperatorInventoryModeSession? session))
        {
            return new VanguardOperatorInventoryModeResponse
            {
                Success = true,
                Reason = "player_profile",
                Active = false,
                RequestedProfileId = requested,
                StorageProfileId = storageProfileId
            };
        }

        SptProfile? operatorProfile = await TryLoadInventoryProfileAsync(session.ProfilePath);
        VanguardOperatorInventorySummary summary = BuildSummary(session.OperatorId, session.OperatorDisplayName, session.OperatorInventoryProfileId, session.ProfilePath, operatorProfile);
        return new VanguardOperatorInventoryModeResponse
        {
            Success = true,
            Reason = "operator_equipment_session",
            Active = true,
            RequestedProfileId = requested,
            StorageProfileId = session.StorageProfileId,
            OperatorId = session.OperatorId,
            OperatorDisplayName = session.OperatorDisplayName,
            OperatorCallsign = session.OperatorCallsign,
            OperatorInventoryProfileId = session.OperatorInventoryProfileId,
            Summary = summary
        };
    }

    public async Task<VanguardOperatorInventorySummaryResponse> GetSummaryAsync(MongoId requestedProfileId)
    {
        string requested = requestedProfileId.ToString();
        string storageProfileId = await operatorStore.ResolveStorageProfileIdAsync(requested);
        var operators = await operatorStore.LoadOperatorsAsync(storageProfileId);
        var summaries = new List<VanguardOperatorInventorySummary>();
        foreach (VanguardOperatorProfile operatorProfile in operators)
        {
            string displayName = ResolveDisplayName(operatorProfile);
            string profilePath = operatorStore.GetOperatorInventoryProfilePath(storageProfileId, operatorProfile.OperatorId);
            SptProfile? profile = await TryLoadInventoryProfileAsync(profilePath);
            string inventoryProfileId = profile == null
                ? BuildStableInventoryProfileId(storageProfileId, operatorProfile.OperatorId)
                : ResolveInventoryProfileId(profile, storageProfileId, operatorProfile.OperatorId);
            summaries.Add(BuildSummary(operatorProfile.OperatorId, displayName, inventoryProfileId, profilePath, profile));
        }

        return new VanguardOperatorInventorySummaryResponse
        {
            RequestedProfileId = requested,
            StorageProfileId = storageProfileId,
            Summaries = summaries,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public async Task<VanguardOperatorInventorySummary> EnsurePersistentOperatorProfileAsync(MongoId requestedProfileId, string storageProfileId, VanguardOperatorProfile operatorProfile)
    {
        SemaphoreSlim guard = GetProfileLock(storageProfileId, operatorProfile.OperatorId);
        await guard.WaitAsync();
        try
        {
            SptProfile profile = await LoadOrCreateOperatorProfileAsync(requestedProfileId, storageProfileId, operatorProfile);
            ValidateOperatorProfileOrThrow(profile, storageProfileId, operatorProfile.OperatorId, "hire_foundation");
            string profilePath = operatorStore.GetOperatorInventoryProfilePath(storageProfileId, operatorProfile.OperatorId);
            await SaveProfileToPathAsync(profilePath, profile);
            string displayName = ResolveDisplayName(operatorProfile);
            string inventoryProfileId = ResolveInventoryProfileId(profile, storageProfileId, operatorProfile.OperatorId);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_FOUNDATION_STATUS] ensured requested={requestedProfileId}; storage={storageProfileId}; operator={operatorProfile.OperatorId}; inventoryProfile={inventoryProfileId}; path={profilePath}"));
            return BuildSummary(operatorProfile.OperatorId, displayName, inventoryProfileId, profilePath, profile);
        }
        finally
        {
            guard.Release();
        }
    }

    public bool TryGetActiveInventoryProfile(MongoId requestedProfileId, out SptProfile? profile)
    {
        profile = null;
        if (!activeSessions.TryGetValue(requestedProfileId.ToString(), out VanguardOperatorInventoryModeSession? session))
        {
            return false;
        }

        profile = session.Profile;
        return profile != null;
    }

    public bool IsActive(MongoId requestedProfileId) => activeSessions.ContainsKey(requestedProfileId.ToString());

    public IDisposable? BeginPlayerPurchaseProfileAccess(
        MongoId requestedProfileId,
        string operation,
        out PmcData? playerPmcData,
        out string? operatorId)
    {
        playerPmcData = null;
        operatorId = null;
        if (!activeSessions.TryGetValue(requestedProfileId.ToString(), out VanguardOperatorInventoryModeSession? session))
        {
            return null;
        }

        // The Operator inventory screen is a composite projection. Native commerce must
        // execute against the real player profile so SPT's own payment service counts
        // the player's wallet instead of a temporary copy that may have gone stale.
        redirectBypassDepth.Value++;
        try
        {
            SptProfile? playerProfile = saveServer.GetProfile(requestedProfileId);
            playerPmcData = playerProfile?.CharacterData?.PmcData;
            if (playerPmcData == null)
            {
                throw new InvalidOperationException($"Real player PMC unavailable for native purchase: {requestedProfileId}");
            }

            operatorId = session.OperatorId;
            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_profile_route_begin operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; inventoryProfile={session.OperatorInventoryProfileId}; redirectBypassDepth={redirectBypassDepth.Value}; playerWalletAuthority=true; operatorEquipmentAuthority=preserved"));
            return new PlayerPurchaseRedirectBypassScope(this, requestedProfileId, session.OperatorId, operation);
        }
        catch (Exception exception)
        {
            redirectBypassDepth.Value = Math.Max(0, redirectBypassDepth.Value - 1);
            logger.Error(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_profile_route_failed operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; type={exception.GetType().Name}; message={exception.Message}; playerWalletAuthority=true; fallbackToComposite=false"));
            throw;
        }
    }

    public void CanonicalizePlayerPurchasePaymentReferences(
        MongoId requestedProfileId,
        ProcessBuyTradeRequestData request,
        PmcData playerPmcData,
        string operation)
    {
        if (!activeSessions.TryGetValue(requestedProfileId.ToString(), out VanguardOperatorInventoryModeSession? session)
            || request.SchemeItems == null
            || request.SchemeItems.Count == 0)
        {
            return;
        }

        try
        {
            // EFT's open Equipment Builds flow can retain the id of a specific currency
            // stack after an earlier native purchase fully consumed that stack. SPT's
            // PaymentService interprets an unknown scheme id as a currency template id; a
            // stale stack id therefore becomes a bogus currency template and the native
            // balance check reports zero even though the player still owns money elsewhere.
            // Canonicalize only proven currency references to their template id, which is an
            // input form already understood by SPT. PaymentService already aggregates a
            // resolved money-stack id by its template before selecting the actual stacks to
            // debit, so this preserves SPT's native stack-selection semantics. Counts and all
            // native payment checks stay untouched, and non-currency barter references are
            // never substituted.
            ConcurrentDictionary<string, MongoId> aliases = playerPurchaseCurrencyAliases.GetOrAdd(
                requestedProfileId.ToString(),
                _ => new ConcurrentDictionary<string, MongoId>(StringComparer.OrdinalIgnoreCase));

            var canonicalizations = new List<(IdWithCount Reference, MongoId CurrencyTpl)>();
            int aliasHits = 0;
            int unresolvedMissing = 0;
            foreach (IdWithCount paymentReference in request.SchemeItems)
            {
                MongoId originalId = paymentReference.Id;
                if (paymentHelper.IsMoneyTpl(originalId))
                {
                    continue;
                }

                MongoId? currencyTpl = null;
                var playerItem = playerPmcData.Inventory?.Items?.FirstOrDefault(item => item.Id == originalId);
                if (playerItem != null && paymentHelper.IsMoneyTpl(playerItem.Template))
                {
                    currencyTpl = playerItem.Template;
                    aliases[originalId.ToString()] = playerItem.Template;
                }
                else
                {
                    var sessionItem = session.Profile.CharacterData?.PmcData?.Inventory?.Items?.FirstOrDefault(item => item.Id == originalId);
                    if (sessionItem != null && paymentHelper.IsMoneyTpl(sessionItem.Template))
                    {
                        currencyTpl = sessionItem.Template;
                        aliases[originalId.ToString()] = sessionItem.Template;
                    }
                    else if (aliases.TryGetValue(originalId.ToString(), out MongoId aliasedCurrencyTpl)
                        && paymentHelper.IsMoneyTpl(aliasedCurrencyTpl))
                    {
                        currencyTpl = aliasedCurrencyTpl;
                        aliasHits++;
                    }
                }

                if (currencyTpl.HasValue)
                {
                    canonicalizations.Add((paymentReference, currencyTpl.Value));
                    continue;
                }

                if (playerItem == null)
                {
                    unresolvedMissing++;
                }
            }

            foreach ((IdWithCount reference, MongoId currencyTpl) in canonicalizations)
            {
                reference.Id = currencyTpl;
            }

            if (canonicalizations.Count > 0)
            {
                logger.Info(VanguardServerDiagnosticsLog.Present(
                    $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_payment_references_canonicalized operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; schemeItems={request.SchemeItems.Count}; canonicalized={canonicalizations.Count}; aliasHits={aliasHits}; unresolvedMissing={unresolvedMissing}; strategy=native_money_tpl; playerWalletAuthority=true; nativePaymentChecksPreserved=true"));
            }
            else if (unresolvedMissing > 0)
            {
                logger.Warning(VanguardServerDiagnosticsLog.Present(
                    $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_payment_reference_unresolved operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; schemeItems={request.SchemeItems.Count}; unresolvedMissing={unresolvedMissing}; action=left_native; nonCurrencySubstitution=false"));
            }
        }
        catch (Exception exception)
        {
            // Canonicalization is a compatibility guard around the native transaction, not
            // an alternative payment authority. Failure therefore falls through to SPT with
            // the original request rather than inventing a balance or replaying payment.
            logger.Warning(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_payment_reference_canonicalization_failed operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; type={exception.GetType().Name}; message={exception.Message}; action=left_native; playerWalletAuthority=true; nativePaymentChecksPreserved=true"));
        }
    }

    public void CompletePlayerPurchaseProfileAccess(
        MongoId requestedProfileId,
        string expectedOperatorId,
        string operation,
        ItemEventRouterResponse output,
        Exception? nativeException)
    {
        if (!activeSessions.TryGetValue(requestedProfileId.ToString(), out VanguardOperatorInventoryModeSession? session))
        {
            return;
        }

        if (!string.Equals(session.OperatorId, expectedOperatorId, StringComparison.OrdinalIgnoreCase))
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_projection_delta_skipped operation={operation}; requested={requestedProfileId}; expectedOperator={expectedOperatorId}; activeOperator={session.OperatorId}; reason=active_operator_changed"));
            return;
        }

        try
        {
            if (output.ProfileChanges == null || !output.ProfileChanges.TryGetValue(requestedProfileId, out ProfileChange? profileChange))
            {
                logger.Warning(VanguardServerDiagnosticsLog.Present(
                    $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_projection_delta_skipped operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; reason=profile_change_missing; nativeResult={(nativeException == null ? "completed" : "faulted")}"));
                return;
            }

            // SPT has already mutated the real player profile and described that mutation
            // in the item-event response. Apply only those native deltas to the temporary
            // composite instead of replacing the whole stash; unrelated Operator-session
            // edits therefore remain untouched.
            SptProfile? playerProfile = saveServer.GetProfile(requestedProfileId);
            if (playerProfile == null)
            {
                logger.Error(VanguardServerDiagnosticsLog.Present(
                    $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_projection_delta_failed operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; reason=player_profile_missing"));
                return;
            }

            JsonObject sessionNode = ProfileToNode(session.Profile);
            JsonObject playerNode = ProfileToNode(playerProfile);
            JsonObject sessionPmc = GetPmcObject(sessionNode);
            JsonObject playerPmc = GetPmcObject(playerNode);
            NativePurchaseDeltaResult delta = ApplyNativePurchaseInventoryDelta(
                GetInventoryObject(sessionNode),
                GetInventoryObject(playerNode),
                profileChange.Items);

            // Trader state belongs to the player's economy as well. Copy it from the
            // native player transaction so later Flea loyalty checks in this same
            // Operator session cannot observe an older composite value.
            CopyPlayerOwnedDescriptorField(playerPmc, sessionPmc, "TradersInfo");
            NormalizeCompleteSessionProfileNode(sessionNode, session.StorageProfileId, session.OperatorId, "native_purchase_delta");

            SptProfile refreshedProfile = NodeToProfile(sessionNode);
            activeSessions[requestedProfileId.ToString()] = session with
            {
                Profile = refreshedProfile,
                ClientSessionProfileNode = CloneObject(sessionNode)
            };

            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_projection_delta_applied operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; nativeResult={(nativeException == null ? "completed" : "faulted")}; warnings={output.Warnings?.Count ?? 0}; new={delta.NewItems}; changed={delta.ChangedItems}; deleted={delta.DeletedItems}; playerWalletAuthority=true; operatorEquipmentAuthority=preserved"));
        }
        catch (Exception exception)
        {
            // Projection reconciliation must never compensate or replay a completed SPT
            // transaction. The real player profile remains the native economy truth and
            // normal exit/reload still provides the final convergence path.
            logger.Error(VanguardServerDiagnosticsLog.Present(
                $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_projection_delta_failed operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; type={exception.GetType().Name}; message={exception.Message}; nativeResult={(nativeException == null ? "completed" : "faulted")}"));
        }
    }

    private void EndPlayerPurchaseProfileAccess(MongoId requestedProfileId, string operatorId, string operation)
    {
        redirectBypassDepth.Value = Math.Max(0, redirectBypassDepth.Value - 1);
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] player_profile_route_end operation={operation}; requested={requestedProfileId}; operator={operatorId}; redirectBypassDepth={redirectBypassDepth.Value}; playerWalletAuthority=true"));
    }

    public IDisposable? BeginPlayerUserBuildProfileAccess(MongoId requestedProfileId, string operation)
    {
        if (!activeSessions.TryGetValue(requestedProfileId.ToString(), out VanguardOperatorInventoryModeSession? session))
        {
            return null;
        }

        redirectBypassDepth.Value++;
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_OPERATOR_USER_BUILDS_STATUS] player_profile_route_begin operation={operation}; requested={requestedProfileId}; operator={session.OperatorId}; inventoryProfile={session.OperatorInventoryProfileId}; redirectBypassDepth={redirectBypassDepth.Value}; playerUserBuildDataAuthority=true"));
        return new PlayerUserBuildRedirectBypassScope(this, requestedProfileId, session.OperatorId, operation);
    }

    private void EndPlayerUserBuildProfileAccess(MongoId requestedProfileId, string operatorId, string operation)
    {
        redirectBypassDepth.Value = Math.Max(0, redirectBypassDepth.Value - 1);
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_OPERATOR_USER_BUILDS_STATUS] player_profile_route_end operation={operation}; requested={requestedProfileId}; operator={operatorId}; redirectBypassDepth={redirectBypassDepth.Value}; playerUserBuildDataAuthority=true"));
    }

    public bool TryGetActiveInventoryProfileId(MongoId requestedProfileId, out MongoId inventoryProfileId)
    {
        inventoryProfileId = default;
        if (!activeSessions.TryGetValue(requestedProfileId.ToString(), out VanguardOperatorInventoryModeSession? session)
            || string.IsNullOrWhiteSpace(session.OperatorInventoryProfileId))
        {
            return false;
        }

        inventoryProfileId = new MongoId(session.OperatorInventoryProfileId);
        return true;
    }

    public async Task<long> SaveActiveInventoryProfileAsync(MongoId requestedProfileId)
    {
        if (!activeSessions.TryGetValue(requestedProfileId.ToString(), out VanguardOperatorInventoryModeSession? session))
        {
            return 0L;
        }

        SemaphoreSlim guard = GetProfileLock(session.StorageProfileId, session.OperatorId);
        await guard.WaitAsync();
        try
        {
            await CommitSessionAsync(session);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_REDIRECT_STATUS] save redirected requested={requestedProfileId}; storage={session.StorageProfileId}; operator={session.OperatorId}; inventoryProfile={session.OperatorInventoryProfileId}; splitCommit=true"));
            return File.Exists(session.ProfilePath) ? new FileInfo(session.ProfilePath).Length : 1L;
        }
        finally
        {
            guard.Release();
        }
    }

    public string GetProfileDescriptorsJsonForClient(MongoId requestedProfileId)
    {
        if (!activeSessions.TryGetValue(requestedProfileId.ToString(), out VanguardOperatorInventoryModeSession? session))
        {
            return "[]";
        }

        JsonObject profileNode = CloneObject(session.ClientSessionProfileNode);
        NormalizeCompleteSessionProfileNode(profileNode, session.StorageProfileId, session.OperatorId, "profiles_route");

        JsonObject pmc = GetPmcObject(profileNode);
        JsonObject scav = GetScavObject(profileNode);
        var descriptors = new JsonArray
        {
            pmc.DeepClone()
        };

        if (scav.Count > 0)
        {
            NormalizeCompleteProfileDescriptor(scav, session.StorageProfileId, session.OperatorId, "scav_profiles_route");
            descriptors.Add(scav.DeepClone());
        }

        string json = descriptors.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS] descriptors requested={requestedProfileId}; operator={session.OperatorId}; count={descriptors.Count}; bytes={json.Length}; source=raw_client_safe_json"));
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS] descriptors requested={requestedProfileId}; operator={session.OperatorId}; count={descriptors.Count}; rawJson=true"));
        return json;
    }

    public IReadOnlyList<object> GetProfileDescriptorsForClient(MongoId requestedProfileId)
    {
        string json = GetProfileDescriptorsJsonForClient(requestedProfileId);
        JsonArray? array = JsonNode.Parse(json) as JsonArray;
        return array?.Where(node => node != null).Select(node => (object)node!).ToArray() ?? Array.Empty<object>();
    }

    private async Task<SptProfile> LoadOrCreateOperatorProfileAsync(MongoId requestedProfileId, string storageProfileId, VanguardOperatorProfile operatorProfile)
    {
        string profilePath = operatorStore.GetOperatorInventoryProfilePath(storageProfileId, operatorProfile.OperatorId);
        SptProfile? existing = await TryLoadInventoryProfileAsync(profilePath);
        if (existing != null)
        {
            JsonObject existingNode = ProfileToNode(existing);
            ApplyOperatorIdentity(existingNode, storageProfileId, operatorProfile);
            EnsureOperatorProfileHasRequiredRoots(existingNode, storageProfileId, operatorProfile);
            NormalizeCompleteSessionProfileNode(existingNode, storageProfileId, operatorProfile.OperatorId, "persistent_existing");
            SptProfile normalized = NodeToProfile(existingNode);
            await SaveProfileToPathAsync(profilePath, normalized);
            return normalized;
        }

        SptProfile? playerProfile;
        using (SuppressProfileRedirects())
        {
            playerProfile = saveServer.GetProfile(requestedProfileId);
        }

        if (playerProfile == null)
        {
            throw new InvalidOperationException($"Unable to load player profile {requestedProfileId} for Vanguard operator profile creation.");
        }

        JsonObject operatorNode = ProfileToNode(playerProfile);
        ApplyOperatorIdentity(operatorNode, storageProfileId, operatorProfile);
        ResetProgressionAndRuntimeState(operatorNode, operatorProfile);
        ReplaceInventory(GetPmcObject(operatorNode), BuildDefaultOperatorInventory(operatorNode, storageProfileId, operatorProfile));

        NormalizeCompleteSessionProfileNode(operatorNode, storageProfileId, operatorProfile.OperatorId, "persistent_create");
        SptProfile created = NodeToProfile(operatorNode);
        await SaveProfileToPathAsync(profilePath, created);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_PROFILE_FOUNDATION_STATUS] created storage={storageProfileId}; operator={operatorProfile.OperatorId}; path={profilePath}; source=player-template; equipment=operator-owned; stash=operator-minimal"));
        return created;
    }

    private JsonObject BuildEquipmentSessionProfileNode(MongoId requestedProfileId, string storageProfileId, VanguardOperatorProfile operatorProfile, SptProfile operatorPersistentProfile)
    {
        SptProfile? playerProfile;
        using (SuppressProfileRedirects())
        {
            playerProfile = saveServer.GetProfile(requestedProfileId);
        }

        if (playerProfile == null)
        {
            throw new InvalidOperationException($"Unable to load player profile {requestedProfileId} for Vanguard equipment session.");
        }

        // Keep an untouched player descriptor beside the mutable Operator projection.
        // The direct equipment screen needs Operator identity/equipment/health/skills,
        // but every market entitlement must continue to describe the player.
        // This explicit split prevents Operator career level from leaking into Flea
        // minimum-level checks or trader loyalty/quest assortment evaluation.
        JsonObject playerAuthorityNode = ProfileToNode(playerProfile);
        JsonObject sessionNode = CloneObject(playerAuthorityNode);
        JsonObject operatorNode = ProfileToNode(operatorPersistentProfile);
        JsonObject playerAuthorityPmc = GetPmcObject(playerAuthorityNode);
        ApplyOperatorIdentity(sessionNode, storageProfileId, operatorProfile);
        JsonObject playerInventory = GetInventoryObject(sessionNode);
        JsonObject operatorInventory = GetInventoryObject(operatorNode);
        JsonObject sessionInventory = BuildSessionInventory(playerInventory, operatorInventory, storageProfileId, operatorProfile.OperatorId);
        JsonObject sessionPmc = GetPmcObject(sessionNode);
        JsonObject operatorPmc = GetPmcObject(operatorNode);
        ReplaceInventory(sessionPmc, sessionInventory);
        CopyOperatorOwnedDescriptorField(operatorPmc, sessionPmc, "Health");
        CopyOperatorOwnedDescriptorField(operatorPmc, sessionPmc, "Skills");
        CopyOperatorOwnedDescriptorField(operatorPmc, sessionPmc, "Stats");
        CopyOperatorOwnedDescriptorField(operatorPmc, sessionPmc, "Customization");
        ApplyPlayerMarketAuthorityProjection(playerAuthorityPmc, sessionPmc, operatorProfile);
        NormalizeCompleteSessionProfileNode(sessionNode, storageProfileId, operatorProfile.OperatorId, "session_build");
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_EQUIPMENT_SESSION_STATUS] built storage={storageProfileId}; operator={operatorProfile.OperatorId}; model=player-market-authority_operator-equipment-player-stash; items={GetItemsArray(sessionInventory).Count}"));
        return sessionNode;
    }

    private async Task CommitSessionAsync(VanguardOperatorInventoryModeSession session)
    {
        JsonObject sessionNode = ProfileToNode(session.Profile);
        JsonObject sessionInventory = GetInventoryObject(sessionNode);

        SptProfile? operatorProfile = await TryLoadInventoryProfileAsync(session.ProfilePath);
        if (operatorProfile == null)
        {
            throw new InvalidOperationException($"Operator profile missing during equipment session commit: {session.ProfilePath}");
        }

        JsonObject operatorNode = ProfileToNode(operatorProfile);
        JsonObject operatorInventory = GetInventoryObject(operatorNode);
        ReplaceInventory(GetPmcObject(operatorNode), BuildOperatorInventoryForCommit(operatorInventory, sessionInventory));
        SptProfile updatedOperatorProfile = NodeToProfile(operatorNode);
        await SaveProfileToPathAsync(session.ProfilePath, updatedOperatorProfile);

        SptProfile? playerProfile;
        using (SuppressProfileRedirects())
        {
            playerProfile = saveServer.GetProfile(session.PlayerProfileId);
        }

        if (playerProfile == null)
        {
            throw new InvalidOperationException($"Player profile missing during equipment session commit: {session.PlayerProfileId}");
        }

        JsonObject playerNode = ProfileToNode(playerProfile);
        JsonObject playerInventory = GetInventoryObject(playerNode);
        ReplaceInventory(GetPmcObject(playerNode), BuildPlayerInventoryForCommit(playerInventory, sessionInventory));
        SptProfile updatedPlayerProfile = NodeToProfile(playerNode);
        CopyProfileMembers(updatedPlayerProfile, playerProfile);
        using (SuppressProfileRedirects())
        {
            await saveServer.SaveProfileAsync(session.PlayerProfileId);
        }

        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_EQUIPMENT_SESSION_STATUS] commit storage={session.StorageProfileId}; operator={session.OperatorId}; operatorPath={session.ProfilePath}; split=operatorEquipment_playerStash"));
    }

    private async Task<SptProfile?> TryLoadInventoryProfileAsync(string profilePath)
    {
        if (!File.Exists(profilePath))
        {
            return null;
        }

        try
        {
            return await jsonUtil.DeserializeFromFileAsync<SptProfile>(profilePath);
        }
        catch (Exception exception)
        {
            string quarantinePath = profilePath + $".invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(profilePath, quarantinePath, overwrite: false);
            }
            catch
            {
                // A broken inventory profile must never prevent the server from continuing.
            }

            logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_PROFILE_MODE_STATUS] invalid inventory profile quarantined path={profilePath}; reason={exception.GetType().Name}: {exception.Message}"));
            return null;
        }
    }

    private async Task SaveProfileToPathAsync(string path, SptProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string serialized = jsonUtil.Serialize(profile, indented: true) ?? throw new InvalidOperationException("Unable to serialize Vanguard Operator profile.");
        string temporary = path + ".vanguard-write-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, serialized);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private JsonObject ProfileToNode(SptProfile profile)
    {
        string serialized = jsonUtil.Serialize(profile, indented: true) ?? throw new InvalidOperationException("Unable to serialize SPT profile to JSON.");
        return JsonNode.Parse(serialized)?.AsObject() ?? throw new InvalidOperationException("Unable to parse serialized SPT profile JSON.");
    }

    private SptProfile NodeToProfile(JsonObject node)
    {
        string serialized = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return jsonUtil.Deserialize<SptProfile>(serialized) ?? throw new InvalidOperationException("Unable to deserialize SPT profile JSON.");
    }

    internal IDisposable SuppressProfileRedirects()
    {
        redirectBypassDepth.Value++;
        return new RedirectBypassScope(this);
    }

    private sealed class RedirectBypassScope(VanguardOperatorInventoryModeService owner) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.redirectBypassDepth.Value = Math.Max(0, owner.redirectBypassDepth.Value - 1);
        }
    }

    private sealed record NativePurchaseDeltaResult(int NewItems, int ChangedItems, int DeletedItems);

    private sealed class PlayerPurchaseRedirectBypassScope(
        VanguardOperatorInventoryModeService owner,
        MongoId requestedProfileId,
        string operatorId,
        string operation) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.EndPlayerPurchaseProfileAccess(requestedProfileId, operatorId, operation);
        }
    }

    private sealed class PlayerUserBuildRedirectBypassScope(
        VanguardOperatorInventoryModeService owner,
        MongoId requestedProfileId,
        string operatorId,
        string operation) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.EndPlayerUserBuildProfileAccess(requestedProfileId, operatorId, operation);
        }
    }

    private void ApplyOperatorIdentity(JsonObject profile, string storageProfileId, VanguardOperatorProfile operatorProfile)
    {
        string displayName = ResolveDisplayName(operatorProfile);
        string nickname = string.IsNullOrWhiteSpace(operatorProfile.Identity.Callsign) ? displayName : operatorProfile.Identity.Callsign;
        string lowerNickname = nickname.ToLowerInvariant();
        string profileId = BuildStableInventoryProfileId(storageProfileId, operatorProfile.OperatorId);
        string scavId = BuildStableId(storageProfileId, operatorProfile.OperatorId, "scav");
        string side = string.IsNullOrWhiteSpace(operatorProfile.Identity.Side) ? "Usec" : operatorProfile.Identity.Side;

        JsonObject info = GetOrCreateObject(profile, "info");
        SetJsonValue(info, "id", profileId);
        SetJsonValue(info, "scavId", scavId);
        SetJsonValue(info, "username", nickname);

        JsonObject pmc = GetPmcObject(profile);
        SetJsonValue(pmc, "_id", profileId);
        SetJsonValue(pmc, "sessionId", profileId);
        SetJsonValue(pmc, "savage", scavId);
        JsonObject pmcInfo = GetOrCreateObject(pmc, "Info");
        SetJsonValue(pmcInfo, "Nickname", nickname);
        SetJsonValue(pmcInfo, "LowerNickname", lowerNickname);
        SetJsonValue(pmcInfo, "Side", side);
        SetJsonValue(pmcInfo, "Level", operatorProfile.Progression.Level);
        SetJsonValue(pmcInfo, "Experience", operatorProfile.Progression.Experience);
        SetJsonValue(pmcInfo, "MainProfileNickname", displayName);

        JsonObject scav = GetScavObject(profile);
        SetJsonValue(scav, "_id", scavId);
        SetJsonValue(scav, "sessionId", profileId);
        JsonObject scavInfo = GetOrCreateObject(scav, "Info");
        SetJsonValue(scavInfo, "Nickname", nickname);
        SetJsonValue(scavInfo, "LowerNickname", lowerNickname);
        SetJsonValue(scavInfo, "MainProfileNickname", displayName);
    }

    private static void ResetProgressionAndRuntimeState(JsonObject profile, VanguardOperatorProfile operatorProfile)
    {
        JsonObject pmc = GetPmcObject(profile);
        JsonObject info = GetOrCreateObject(pmc, "Info");
        SetJsonValue(info, "Level", operatorProfile.Progression.Level);
        SetJsonValue(info, "Experience", operatorProfile.Progression.Experience);
        SetJsonValue(info, "RegistrationDate", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        JsonObject skills = GetOrCreateObject(pmc, "Skills");
        if (TryGetArray(skills, "Common", out JsonArray? commonSkills) && commonSkills is not null)
        {
            foreach (JsonNode? node in commonSkills)
            {
                if (node is not JsonObject skill)
                {
                    continue;
                }

                SetJsonValue(skill, "Progress", 0);
                SetJsonValue(skill, "PointsEarnedDuringSession", 0);
                SetJsonValue(skill, "LastAccess", 0);
            }
        }

        if (TryGetArray(skills, "Mastering", out JsonArray? masteringSkills) && masteringSkills is not null)
        {
            foreach (JsonNode? node in masteringSkills)
            {
                if (node is JsonObject masteringSkill)
                {
                    SetJsonNumber(masteringSkill, "Progress", 0.0);
                }
            }
        }

        SetJsonValue(skills, "Points", 0);

        JsonObject stats = GetOrCreateObject(pmc, "Stats");
        JsonObject eft = GetOrCreateObject(stats, "Eft");
        SetJsonValue(eft, "TotalSessionExperience", 0);
        SetJsonValue(eft, "LastSessionDate", 0);
        SetJsonValue(eft, "TotalInGameTime", 0);
        SetJsonValue(eft, "SurvivorClass", "Unknown");
        eft["CarriedQuestItems"] = new JsonArray();
        eft["Victims"] = new JsonArray();
        eft["DroppedItems"] = new JsonArray();
        eft["FoundInRaidItems"] = new JsonArray();
        eft["SessionCounters"] = new JsonObject { ["Items"] = new JsonArray() };
        eft["OverallCounters"] = new JsonObject { ["Items"] = new JsonArray() };
    }

    private void EnsureOperatorProfileHasRequiredRoots(JsonObject profile, string storageProfileId, VanguardOperatorProfile operatorProfile)
    {
        JsonObject inventory = GetInventoryObject(profile);
        JsonArray items = GetItemsArray(inventory);
        string? equipmentId = GetString(inventory, "equipment");
        string? stashId = GetString(inventory, "stash");
        string audit = AuditInventoryTree(inventory);
        if (!string.IsNullOrWhiteSpace(equipmentId)
            && ItemExists(items, equipmentId)
            && !string.IsNullOrWhiteSpace(stashId)
            && ItemExists(items, stashId)
            && audit == "ok")
        {
            return;
        }

        logger.Warning(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_TREE_REPAIR_STATUS] persistent_operator_inventory_repair operator={operatorProfile.OperatorId}; reason={audit}; equipment={equipmentId ?? "<none>"}; stash={stashId ?? "<none>"}; items={items.Count}"));
        ReplaceInventory(GetPmcObject(profile), BuildDefaultOperatorInventory(profile, storageProfileId, operatorProfile));
    }

    private static JsonObject BuildDefaultOperatorInventory(JsonObject templateProfile, string storageProfileId, VanguardOperatorProfile operatorProfile)
    {
        JsonObject templateInventory = GetInventoryObject(templateProfile);
        JsonArray templateItems = GetItemsArray(templateInventory);

        string equipmentId = BuildStableId(storageProfileId, operatorProfile.OperatorId, "equipment");
        string stashId = BuildStableId(storageProfileId, operatorProfile.OperatorId, "stash");
        string sortingTableId = BuildStableId(storageProfileId, operatorProfile.OperatorId, "sorting-table");
        string questRaidId = BuildStableId(storageProfileId, operatorProfile.OperatorId, "quest-raid");
        string questStashId = BuildStableId(storageProfileId, operatorProfile.OperatorId, "quest-stash");
        string hideoutCustomizationId = BuildStableId(storageProfileId, operatorProfile.OperatorId, "hideout-customization");

        string templateEquipmentId = FirstNonEmpty(GetString(templateInventory, "equipment"), FindFirstRootItemId(templateItems, "55d7217a4bdc2d86028b456d"));
        JsonArray items = BuildVanillaEquipmentScaffoldItems(templateItems, templateEquipmentId, equipmentId, storageProfileId, operatorProfile.OperatorId);

        items.Add(CreateRootItem(stashId, ResolveRootTemplate(templateInventory, templateItems, "stash", "5811ce772459770e9e5f9532")));
        items.Add(CreateRootItem(sortingTableId, ResolveRootTemplate(templateInventory, templateItems, "sortingTable", "602543c13fee350cd564d032")));
        items.Add(CreateRootItem(questRaidId, ResolveRootTemplate(templateInventory, templateItems, "questRaidItems", "5963866286f7747bf429b572")));
        items.Add(CreateRootItem(questStashId, ResolveRootTemplate(templateInventory, templateItems, "questStashItems", "5963866b86f7747bfa1c4462")));
        items.Add(CreateRootItem(hideoutCustomizationId, ResolveRootTemplate(templateInventory, templateItems, "hideoutCustomizationStashId", "673c7b00cbf4b984b5099181")));

        return new JsonObject
        {
            ["items"] = items,
            ["equipment"] = equipmentId,
            ["stash"] = stashId,
            ["sortingTable"] = sortingTableId,
            ["questRaidItems"] = questRaidId,
            ["questStashItems"] = questStashId,
            ["hideoutAreaStashes"] = new JsonObject(),
            ["fastPanel"] = new JsonObject(),
            ["favoriteItems"] = new JsonArray(),
            ["hideoutCustomizationStashId"] = hideoutCustomizationId
        };
    }

    private static JsonArray BuildVanillaEquipmentScaffoldItems(JsonArray templateItems, string? templateEquipmentId, string equipmentId, string storageProfileId, string operatorId)
    {
        var result = new JsonArray();
        JsonObject? templateEquipment = FindItemById(templateItems, templateEquipmentId) ?? FindFirstRootItemByTpl(templateItems, "55d7217a4bdc2d86028b456d");
        if (templateEquipment == null)
        {
            result.Add(CreateRootItem(equipmentId, "55d7217a4bdc2d86028b456d"));
        }
        else
        {
            result.Add(CloneItemAs(templateEquipment, equipmentId, null, null));
        }

        JsonObject? templatePockets = FindDirectChildBySlot(templateItems, templateEquipmentId, "Pockets")
            ?? FindFirstItemBySlot(templateItems, "Pockets");
        string pocketsId = BuildStableId(storageProfileId, operatorId, "pockets");
        if (templatePockets != null)
        {
            result.Add(CloneItemAs(templatePockets, pocketsId, equipmentId, "Pockets"));
        }

        return result;
    }

    private static bool IsEmptyOperatorEquipmentTree(JsonArray items, HashSet<string> treeIds, string equipmentId)
    {
        if (string.IsNullOrWhiteSpace(equipmentId) || treeIds.Count == 0)
        {
            return false;
        }

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            string? id = GetItemId(item);
            if (string.IsNullOrWhiteSpace(id) || !treeIds.Contains(id))
            {
                continue;
            }

            if (string.Equals(id, equipmentId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(GetString(item, "slotId"), "Pockets", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string AuditEquipmentTree(JsonArray items, HashSet<string> treeIds, string equipmentId)
    {
        if (string.IsNullOrWhiteSpace(equipmentId) || !ItemExists(items, equipmentId))
        {
            return "equipment_root_missing";
        }

        if (treeIds.Count == 0)
        {
            return "equipment_tree_empty";
        }

        var ids = new HashSet<string>(items.OfType<JsonObject>().Select(GetItemId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!), StringComparer.OrdinalIgnoreCase);
        bool hasPockets = false;
        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            string? id = GetItemId(item);
            if (string.IsNullOrWhiteSpace(id) || !treeIds.Contains(id))
            {
                continue;
            }

            string? parentId = GetString(item, "parentId");
            if (!string.IsNullOrWhiteSpace(parentId) && !ids.Contains(parentId))
            {
                return "missing_parent_" + id;
            }

            string? slot = GetString(item, "slotId");
            string? tpl = GetString(item, "_tpl");
            if (string.Equals(slot, "Pockets", StringComparison.OrdinalIgnoreCase))
            {
                hasPockets = true;
                if (string.Equals(tpl, "a8edfb0bce53d103d3f62b9b", StringComparison.OrdinalIgnoreCase))
                {
                    // VANGUARD_OPERATOR_COMMIT_KEEP_STATUS
                    // Runtime invariant: a legacy generated pockets template is unsafe only for an empty scaffold.
                    // If the equipment tree already contains user payload, keeping the tree is safer
                    // than rebuilding an empty scaffold and deleting the committed Operator equipment.
                    return HasEquipmentPayload(items, treeIds, equipmentId)
                        ? "legacy_pockets_with_payload"
                        : "legacy_generated_pockets_template";
                }
            }
        }

        return hasPockets ? "ok" : "pockets_missing";
    }

    private static bool HasEquipmentPayload(JsonArray items, HashSet<string> treeIds, string equipmentId)
    {
        if (string.IsNullOrWhiteSpace(equipmentId) || treeIds.Count == 0)
        {
            return false;
        }

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            string? id = GetItemId(item);
            if (string.IsNullOrWhiteSpace(id) || !treeIds.Contains(id))
            {
                continue;
            }

            if (string.Equals(id, equipmentId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(GetString(item, "parentId"), equipmentId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(GetString(item, "slotId"), "Pockets", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static string AuditInventoryTree(JsonObject inventory)
    {
        JsonArray items = GetItemsArray(inventory);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject item in items.OfType<JsonObject>())
        {
            string? id = GetItemId(item);
            if (string.IsNullOrWhiteSpace(id))
            {
                return "item_id_missing";
            }

            if (!ids.Add(id))
            {
                return "duplicate_item_id_" + id;
            }
        }

        foreach (JsonObject item in items.OfType<JsonObject>())
        {
            string? parentId = GetString(item, "parentId");
            if (!string.IsNullOrWhiteSpace(parentId) && !string.Equals(parentId, "hideout", StringComparison.OrdinalIgnoreCase) && !ids.Contains(parentId))
            {
                return "missing_parent_" + (GetItemId(item) ?? "<unknown>");
            }
        }

        foreach (string field in new[] { "equipment", "stash" })
        {
            string? id = GetString(inventory, field);
            if (string.IsNullOrWhiteSpace(id) || !ids.Contains(id))
            {
                return "missing_root_" + field;
            }
        }

        return "ok";
    }

    private static string AuditRaidEquipmentSnapshot(JsonObject inventory, string equipmentId)
    {
        JsonArray items = GetItemsArray(inventory);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject item in items.OfType<JsonObject>())
        {
            string? id = GetItemId(item);
            if (string.IsNullOrWhiteSpace(id))
            {
                return "item_id_missing";
            }

            if (!ids.Add(id))
            {
                return "duplicate_item_id_" + id;
            }
        }

        if (!ids.Contains(equipmentId))
        {
            return "equipment_root_item_missing";
        }

        HashSet<string> treeIds = CollectTreeIds(items, equipmentId);
        if (treeIds.Count == 0)
        {
            return "equipment_tree_empty";
        }

        foreach (JsonObject item in items.OfType<JsonObject>())
        {
            string? id = GetItemId(item);
            if (string.IsNullOrWhiteSpace(id) || !treeIds.Contains(id) || string.Equals(id, equipmentId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? parentId = GetString(item, "parentId");
            if (string.IsNullOrWhiteSpace(parentId) || !treeIds.Contains(parentId))
            {
                return "equipment_tree_missing_parent_" + id;
            }
        }

        return "ok";
    }

    private static string AuditEquipmentIdentityAndTopologyPreserved(
        JsonObject runtimeInventory,
        JsonObject normalizedInventory,
        string equipmentId)
    {
        JsonArray runtimeItems = GetItemsArray(runtimeInventory);
        JsonArray normalizedItems = GetItemsArray(normalizedInventory);
        HashSet<string> runtimeTreeIds = CollectTreeIds(runtimeItems, equipmentId);
        HashSet<string> normalizedTreeIds = CollectTreeIds(normalizedItems, equipmentId);
        if (!runtimeTreeIds.SetEquals(normalizedTreeIds))
        {
            return "item_ids_mismatch";
        }

        Dictionary<string, JsonObject> runtimeById = runtimeItems
            .OfType<JsonObject>()
            .Where(item => runtimeTreeIds.Contains(GetItemId(item) ?? string.Empty))
            .ToDictionary(item => GetItemId(item)!, item => item, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonObject> normalizedById = normalizedItems
            .OfType<JsonObject>()
            .Where(item => normalizedTreeIds.Contains(GetItemId(item) ?? string.Empty))
            .ToDictionary(item => GetItemId(item)!, item => item, StringComparer.OrdinalIgnoreCase);

        foreach (string itemId in runtimeTreeIds)
        {
            if (!runtimeById.TryGetValue(itemId, out JsonObject? runtimeItem)
                || !normalizedById.TryGetValue(itemId, out JsonObject? normalizedItem))
            {
                return "item_missing_" + itemId;
            }

            string runtimeTemplate = GetString(runtimeItem, "_tpl") ?? string.Empty;
            string normalizedTemplate = GetString(normalizedItem, "_tpl") ?? string.Empty;
            if (!string.Equals(runtimeTemplate, normalizedTemplate, StringComparison.OrdinalIgnoreCase))
            {
                return "template_mismatch_" + itemId;
            }

            string runtimeParent = GetString(runtimeItem, "parentId") ?? string.Empty;
            string normalizedParent = GetString(normalizedItem, "parentId") ?? string.Empty;
            if (!string.Equals(runtimeParent, normalizedParent, StringComparison.OrdinalIgnoreCase))
            {
                return "parent_mismatch_" + itemId;
            }

            string runtimeSlot = GetString(runtimeItem, "slotId") ?? string.Empty;
            string normalizedSlot = GetString(normalizedItem, "slotId") ?? string.Empty;
            if (!string.Equals(runtimeSlot, normalizedSlot, StringComparison.Ordinal))
            {
                return "slot_mismatch_" + itemId;
            }

            string runtimeValueAudit = AuditRuntimeItemStatePreserved(runtimeItem, normalizedItem);
            if (runtimeValueAudit != "ok")
            {
                return "runtime_value_mismatch_" + itemId + "_" + runtimeValueAudit;
            }
        }

        return "ok";
    }

    private static string AuditRuntimeItemStatePreserved(JsonObject runtimeItem, JsonObject normalizedItem)
    {
        foreach ((string key, JsonNode? runtimeValue) in runtimeItem)
        {
            if (runtimeValue == null
                || key.Equals("_id", StringComparison.OrdinalIgnoreCase)
                || key.Equals("_tpl", StringComparison.OrdinalIgnoreCase)
                || key.Equals("parentId", StringComparison.OrdinalIgnoreCase)
                || key.Equals("slotId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!normalizedItem.TryGetPropertyValue(key, out JsonNode? normalizedValue) || normalizedValue == null)
            {
                return "item." + key + "_missing";
            }

            string childAudit = AuditRuntimeJsonSubsetPreserved(runtimeValue, normalizedValue, "item." + key);
            if (childAudit != "ok")
            {
                return childAudit;
            }
        }

        return "ok";
    }

    private static string AuditRuntimeJsonSubsetPreserved(JsonNode? runtimeNode, JsonNode? normalizedNode, string path)
    {
        if (runtimeNode == null)
        {
            // Null runtime members may legitimately disappear when SPT omits null/default JSON.
            return "ok";
        }

        if (normalizedNode == null)
        {
            return path + "_missing";
        }

        if (runtimeNode is JsonObject runtimeObject)
        {
            if (normalizedNode is not JsonObject normalizedObject)
            {
                return path + "_kind_mismatch";
            }

            foreach ((string key, JsonNode? runtimeValue) in runtimeObject)
            {
                if (runtimeValue == null)
                {
                    continue;
                }

                if (!normalizedObject.TryGetPropertyValue(key, out JsonNode? normalizedValue) || normalizedValue == null)
                {
                    return path + "." + key + "_missing";
                }

                string childAudit = AuditRuntimeJsonSubsetPreserved(runtimeValue, normalizedValue, path + "." + key);
                if (childAudit != "ok")
                {
                    return childAudit;
                }
            }

            return "ok";
        }

        if (runtimeNode is JsonArray runtimeArray)
        {
            if (normalizedNode is not JsonArray normalizedArray)
            {
                return path + "_kind_mismatch";
            }

            if (runtimeArray.Count != normalizedArray.Count)
            {
                return path + "_count_mismatch";
            }

            for (int index = 0; index < runtimeArray.Count; index++)
            {
                string childAudit = AuditRuntimeJsonSubsetPreserved(runtimeArray[index], normalizedArray[index], path + "[" + index + "]");
                if (childAudit != "ok")
                {
                    return childAudit;
                }
            }

            return "ok";
        }

        return JsonNode.DeepEquals(runtimeNode, normalizedNode)
            ? "ok"
            : path + "_value_mismatch";
    }

    private static string ComputeEquipmentTreeFingerprint(JsonObject inventory, string equipmentId)
    {
        JsonArray items = GetItemsArray(inventory);
        HashSet<string> treeIds = CollectTreeIds(items, equipmentId);
        string canonical = string.Join("\n", items.OfType<JsonObject>()
            .Where(item => treeIds.Contains(GetItemId(item) ?? string.Empty))
            .OrderBy(item => GetItemId(item), StringComparer.OrdinalIgnoreCase)
            .Select(CanonicalizeJson));
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return BitConverter.ToString(hash).Replace("-", string.Empty);
    }

    private static string CanonicalizeJson(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            return "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + CanonicalizeJson(pair.Value))) + "}";
        }

        if (node is JsonArray array)
        {
            return "[" + string.Join(",", array.Select(CanonicalizeJson)) + "]";
        }

        return node?.ToJsonString() ?? "null";
    }

    private static void RemoveItemsWithMissingParents(JsonArray items)
    {
        bool changed;
        do
        {
            changed = false;
            var ids = new HashSet<string>(items.OfType<JsonObject>().Select(GetItemId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!), StringComparer.OrdinalIgnoreCase);
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is not JsonObject item)
                {
                    items.RemoveAt(i);
                    changed = true;
                    continue;
                }

                string? id = GetItemId(item);
                string? parentId = GetString(item, "parentId");
                if (string.IsNullOrWhiteSpace(id) || (!string.IsNullOrWhiteSpace(parentId) && !string.Equals(parentId, "hideout", StringComparison.OrdinalIgnoreCase) && !ids.Contains(parentId)))
                {
                    items.RemoveAt(i);
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static void SanitizeInventoryReferences(JsonObject inventory)
    {
        JsonArray items = GetItemsArray(inventory);
        var ids = new HashSet<string>(items.OfType<JsonObject>().Select(GetItemId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!), StringComparer.OrdinalIgnoreCase);

        JsonObject fastPanel = GetOrCreateObject(inventory, "fastPanel");
        foreach (string key in fastPanel.Select(property => property.Key).ToArray())
        {
            string? value = NodeToString(fastPanel[key]);
            if (string.IsNullOrWhiteSpace(value) || !ids.Contains(value))
            {
                fastPanel.Remove(key);
            }
        }

        JsonArray favorites = GetOrCreateArray(inventory, "favoriteItems");
        for (int i = favorites.Count - 1; i >= 0; i--)
        {
            string? value = NodeToString(favorites[i]);
            if (string.IsNullOrWhiteSpace(value) || !ids.Contains(value))
            {
                favorites.RemoveAt(i);
            }
        }

        JsonObject hideoutAreaStashes = GetOrCreateObject(inventory, "hideoutAreaStashes");
        foreach (string key in hideoutAreaStashes.Select(property => property.Key).ToArray())
        {
            string? value = NodeToString(hideoutAreaStashes[key]);
            if (string.IsNullOrWhiteSpace(value) || !ids.Contains(value))
            {
                hideoutAreaStashes.Remove(key);
            }
        }
    }

    private static int CountObjectProperties(JsonObject obj) => obj.Count;

    private static JsonArray GetOrCreateArray(JsonObject parent, string name)
    {
        string actualName = FindPropertyName(parent, name) ?? name;
        if (parent[actualName] is JsonArray array)
        {
            return array;
        }

        array = new JsonArray();
        parent[actualName] = array;
        return array;
    }

    private static JsonObject? FindItemById(JsonArray items, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return items.OfType<JsonObject>().FirstOrDefault(item => string.Equals(GetItemId(item), id, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject? FindFirstRootItemByTpl(JsonArray items, string tpl)
    {
        return items.OfType<JsonObject>().FirstOrDefault(item => string.Equals(GetString(item, "_tpl"), tpl, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(GetString(item, "parentId")));
    }

    private static JsonObject? FindDirectChildBySlot(JsonArray items, string? parentId, string slotId)
    {
        if (string.IsNullOrWhiteSpace(parentId))
        {
            return null;
        }

        return items.OfType<JsonObject>().FirstOrDefault(item => string.Equals(GetString(item, "parentId"), parentId, StringComparison.OrdinalIgnoreCase) && string.Equals(GetString(item, "slotId"), slotId, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject? FindFirstItemBySlot(JsonArray items, string slotId)
    {
        return items.OfType<JsonObject>().FirstOrDefault(item => string.Equals(GetString(item, "slotId"), slotId, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject CloneItemAs(JsonObject source, string id, string? parentId, string? slotId)
    {
        JsonObject clone = CloneObject(source);
        SetJsonValue(clone, "_id", id);
        if (string.IsNullOrWhiteSpace(parentId))
        {
            clone.Remove("parentId");
        }
        else
        {
            SetJsonValue(clone, "parentId", parentId);
        }

        if (string.IsNullOrWhiteSpace(slotId))
        {
            clone.Remove("slotId");
        }
        else
        {
            SetJsonValue(clone, "slotId", slotId);
        }

        clone.Remove("location");
        return clone;
    }

    private static NativePurchaseDeltaResult ApplyNativePurchaseInventoryDelta(
        JsonObject sessionInventory,
        JsonObject authoritativePlayerInventory,
        ItemChanges? changes)
    {
        if (changes == null)
        {
            return new NativePurchaseDeltaResult(0, 0, 0);
        }

        JsonArray sessionItems = GetItemsArray(sessionInventory);
        JsonArray playerItems = GetItemsArray(authoritativePlayerInventory);
        int newCount = 0;
        int changedCount = 0;
        int deletedCount = 0;

        foreach (var newItem in changes.NewItems ?? [])
        {
            string itemId = newItem.Id.ToString();
            JsonObject? authoritativeItem = FindItemById(playerItems, itemId);
            if (authoritativeItem == null)
            {
                continue;
            }

            JsonObject? existingItem = FindItemById(sessionItems, itemId);
            if (existingItem != null)
            {
                ReplaceJsonObject(existingItem, authoritativeItem);
            }
            else
            {
                sessionItems.Add(authoritativeItem.DeepClone());
            }

            newCount++;
        }

        foreach (var changedItem in changes.ChangedItems ?? [])
        {
            string itemId = changedItem.Id.ToString();
            JsonObject? authoritativeItem = FindItemById(playerItems, itemId);
            if (authoritativeItem == null)
            {
                continue;
            }

            JsonObject? sessionItem = FindItemById(sessionItems, itemId);
            if (sessionItem == null)
            {
                sessionItems.Add(authoritativeItem.DeepClone());
                changedCount++;
                continue;
            }

            // Native purchase changes on existing inventory items are predominantly
            // stack-state updates such as currency debits or stacking a bought item.
            // Copy `upd` from the real player item while keeping the active composite's
            // parent/location so unrelated stash layout edits are not rolled back.
            string? authoritativeUpdName = FindPropertyName(authoritativeItem, "upd");
            if (authoritativeUpdName != null && authoritativeItem[authoritativeUpdName] != null)
            {
                sessionItem[FindPropertyName(sessionItem, "upd") ?? "upd"] = authoritativeItem[authoritativeUpdName]!.DeepClone();
            }

            changedCount++;
        }

        foreach (DeletedItem deletedItem in changes.DeletedItems ?? [])
        {
            string itemId = deletedItem.Id.ToString();
            JsonObject? sessionItem = FindItemById(sessionItems, itemId);
            if (sessionItem != null)
            {
                sessionItems.Remove(sessionItem);
                deletedCount++;
            }
        }

        SanitizeInventoryReferences(sessionInventory);
        EnsureRootFieldsReferenceExistingItems(sessionInventory);
        return new NativePurchaseDeltaResult(newCount, changedCount, deletedCount);
    }

    private static void ReplaceJsonObject(JsonObject target, JsonObject source)
    {
        target.Clear();
        foreach (KeyValuePair<string, JsonNode?> property in source)
        {
            target[property.Key] = property.Value?.DeepClone();
        }
    }

    private void ApplyPlayerMarketAuthorityProjection(JsonObject playerPmc, JsonObject sessionPmc, VanguardOperatorProfile operatorProfile)
    {
        // The composite profile is a presentation/editing shell, never an economic
        // identity. Preserve the player-owned inputs that EFT/SPT can consult while
        // browsing or buying through trader/Flea screens opened from Equipment Builds.
        // Operator Health/Skills/Stats/Customization and the hybrid inventory remain
        // untouched, so this does not collapse the Operator into the player profile.
        JsonObject playerInfo = GetOrCreateObject(playerPmc, "Info");
        JsonObject sessionInfo = GetOrCreateObject(sessionPmc, "Info");
        CopyPlayerOwnedDescriptorField(playerInfo, sessionInfo, "Level");
        CopyPlayerOwnedDescriptorField(playerInfo, sessionInfo, "Experience");

        // These nodes already originate from the player template today. Re-applying
        // them explicitly documents and enforces the authority boundary so future
        // Operator-profile expansion cannot silently make market visibility, trader
        // loyalty, quest unlocks, examined-item state or Flea metadata Operator-owned.
        foreach (string fieldName in new[]
        {
            "TradersInfo",
            "Quests",
            "TaskConditionCounters",
            "RagfairInfo",
            "UnlockedInfo",
            "Encyclopedia",
            "Bonuses",
            "WishList"
        })
        {
            CopyPlayerOwnedDescriptorField(playerPmc, sessionPmc, fieldName);
        }

        double playerLevel = ReadFiniteDoubleOrDefault(playerInfo, "Level", -1.0);
        double effectiveLevel = ReadFiniteDoubleOrDefault(sessionInfo, "Level", -1.0);
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[VANGUARD_OPERATOR_MARKET_AUTHORITY_STATUS] session_projection operator={operatorProfile.OperatorId}; playerLevel={playerLevel:0}; operatorCareerLevel={operatorProfile.Progression.Level}; effectiveMarketLevel={effectiveLevel:0}; levelAuthority=player; traderAuthority=player; questUnlockAuthority=player; ragfairAuthority=player; examinedItemAuthority=player; bonusAuthority=player; operatorEquipmentAuthority=preserved; operatorSkillsAuthority=preserved"));
    }

    private static void CopyPlayerOwnedDescriptorField(JsonObject playerPmc, JsonObject sessionPmc, string fieldName)
    {
        string? sourceName = FindPropertyName(playerPmc, fieldName);
        if (sourceName == null || playerPmc[sourceName] == null)
        {
            return;
        }

        sessionPmc[FindPropertyName(sessionPmc, fieldName) ?? fieldName] = playerPmc[sourceName]!.DeepClone();
    }

    private static void CopyOperatorOwnedDescriptorField(JsonObject operatorPmc, JsonObject sessionPmc, string fieldName)
    {
        string? sourceName = FindPropertyName(operatorPmc, fieldName);
        if (sourceName == null || operatorPmc[sourceName] == null)
        {
            return;
        }

        sessionPmc[FindPropertyName(sessionPmc, fieldName) ?? fieldName] = operatorPmc[sourceName]!.DeepClone();
    }


    private bool TryExtractDirectCommitInventory(string? profileDescriptorJson, out JsonObject? inventory, out string reason)
    {
        inventory = null;
        reason = "unknown";
        if (string.IsNullOrWhiteSpace(profileDescriptorJson))
        {
            reason = "empty_profile_descriptor_json";
            return false;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(profileDescriptorJson);
        }
        catch (Exception exception)
        {
            reason = "json_parse_" + exception.GetType().Name;
            return false;
        }

        if (parsed is not JsonObject root)
        {
            reason = "profile_descriptor_not_object";
            return false;
        }

        if (TryGetObject(root, "Inventory", out JsonObject? descriptorInventory) && descriptorInventory is not null)
        {
            inventory = CloneObject(descriptorInventory);
            reason = "descriptor_inventory";
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS] direct_commit_snapshot_built source=descriptor_inventory; items={GetItemsArray(inventory).Count}; audit={AuditInventoryTree(inventory)}"));
            return true;
        }

        if (TryGetObject(root, "characters", out JsonObject? characters)
            && characters is not null
            && TryGetObject(characters, "pmc", out JsonObject? pmc)
            && pmc is not null
            && TryGetObject(pmc, "Inventory", out JsonObject? profileInventory)
            && profileInventory is not null)
        {
            inventory = CloneObject(profileInventory);
            reason = "profile_characters_pmc_inventory";
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_DIRECT_COMMIT_STATUS] direct_commit_snapshot_built source=profile_characters_pmc_inventory; items={GetItemsArray(inventory).Count}; audit={AuditInventoryTree(inventory)}"));
            return true;
        }

        reason = "inventory_node_not_found";
        return false;
    }

    private bool TryExtractProfileDescriptorField(string? profileDescriptorJson, string fieldName, out JsonObject? field, out string reason)
    {
        field = null;
        reason = "unknown";
        if (string.IsNullOrWhiteSpace(profileDescriptorJson))
        {
            reason = "empty_profile_descriptor_json";
            return false;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(profileDescriptorJson);
        }
        catch (Exception exception)
        {
            reason = "json_parse_" + exception.GetType().Name;
            return false;
        }

        if (parsed is not JsonObject root)
        {
            reason = "profile_descriptor_not_object";
            return false;
        }

        if (TryGetObject(root, fieldName, out JsonObject? direct) && direct is not null)
        {
            field = CloneObject(direct);
            reason = "descriptor_" + fieldName.ToLowerInvariant();
            return true;
        }

        if (TryGetObject(root, "characters", out JsonObject? characters)
            && characters is not null
            && TryGetObject(characters, "pmc", out JsonObject? pmc)
            && pmc is not null
            && TryGetObject(pmc, fieldName, out JsonObject? nested)
            && nested is not null)
        {
            field = CloneObject(nested);
            reason = "profile_characters_pmc_" + fieldName.ToLowerInvariant();
            return true;
        }

        reason = fieldName.ToLowerInvariant() + "_node_not_found";
        return false;
    }

    private static Dictionary<string, JsonObject> BuildSkillMap(JsonArray array)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject skill in array.OfType<JsonObject>())
        {
            string id = GetString(skill, "Id") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(id) && !result.ContainsKey(id))
            {
                result[id] = skill;
            }
        }
        return result;
    }

    private static bool TryReadFiniteDouble(JsonObject obj, string name, out double value)
    {
        value = 0.0;
        string? actualName = FindPropertyName(obj, name);
        JsonNode? node = actualName == null ? null : obj[actualName];
        if (node == null)
        {
            return false;
        }
        try
        {
            value = JsonSerializer.Deserialize<double>(node.ToJsonString());
            return double.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    private static double ReadFiniteDoubleOrDefault(JsonObject obj, string name, double fallback)
        => TryReadFiniteDouble(obj, name, out double value) ? value : fallback;

    private static bool TryReadNonNegativeInt64(JsonObject obj, string name, out long value)
    {
        value = 0;
        string? actualName = FindPropertyName(obj, name);
        JsonNode? node = actualName == null ? null : obj[actualName];
        if (node == null)
        {
            return false;
        }
        try
        {
            value = JsonSerializer.Deserialize<long>(node.ToJsonString());
            return value >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static long ReadNonNegativeInt64OrDefault(JsonObject obj, string name, long fallback)
        => TryReadNonNegativeInt64(obj, name, out long value) ? value : fallback;

    private static void SetJsonNumber(JsonObject obj, string name, double value)
        => obj[FindPropertyName(obj, name) ?? name] = JsonValue.Create(value);

    private static string ComputeSkillStateFingerprint(JsonObject skills, bool includeSessionPoints)
    {
        var lines = new List<string>();
        if (TryGetArray(skills, "Common", out JsonArray? common) && common != null)
        {
            foreach (JsonObject skill in common.OfType<JsonObject>().OrderBy(value => GetString(value, "Id"), StringComparer.OrdinalIgnoreCase))
            {
                string id = GetString(skill, "Id") ?? string.Empty;
                double progress = ReadFiniteDoubleOrDefault(skill, "Progress", double.NaN);
                long lastAccess = ReadNonNegativeInt64OrDefault(skill, "LastAccess", -1);
                double points = includeSessionPoints ? ReadFiniteDoubleOrDefault(skill, "PointsEarnedDuringSession", double.NaN) : 0.0;
                lines.Add("C|" + id + "|" + progress.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + "|" + lastAccess.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "|" + points.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (TryGetArray(skills, "Mastering", out JsonArray? mastering) && mastering != null)
        {
            foreach (JsonObject skill in mastering.OfType<JsonObject>().OrderBy(value => GetString(value, "Id"), StringComparer.OrdinalIgnoreCase))
            {
                string id = GetString(skill, "Id") ?? string.Empty;
                double progress = ReadFiniteDoubleOrDefault(skill, "Progress", double.NaN);
                lines.Add("M|" + id + "|" + progress.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        using SHA256 sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    private static bool RuntimeSkillIdsPersistedWithSessionReset(
        JsonObject runtimeSkills,
        JsonObject persistedSkills,
        double tolerance,
        out string reason)
    {
        JsonArray runtimeCommon = GetOrCreateArray(runtimeSkills, "Common");
        JsonArray runtimeMastering = GetOrCreateArray(runtimeSkills, "Mastering");
        Dictionary<string, JsonObject> persistedCommon = BuildSkillMap(GetOrCreateArray(persistedSkills, "Common"));
        Dictionary<string, JsonObject> persistedMastering = BuildSkillMap(GetOrCreateArray(persistedSkills, "Mastering"));

        foreach (JsonObject runtime in runtimeCommon.OfType<JsonObject>())
        {
            string id = GetString(runtime, "Id") ?? string.Empty;
            if (!persistedCommon.TryGetValue(id, out JsonObject? persisted))
            {
                reason = "common_skill_missing_" + id;
                return false;
            }
            double expected = ReadFiniteDoubleOrDefault(runtime, "Progress", double.NaN);
            double actual = ReadFiniteDoubleOrDefault(persisted, "Progress", double.NaN);
            long expectedLastAccess = ReadNonNegativeInt64OrDefault(runtime, "LastAccess", -1);
            long actualLastAccess = ReadNonNegativeInt64OrDefault(persisted, "LastAccess", -1);
            double sessionPoints = ReadFiniteDoubleOrDefault(persisted, "PointsEarnedDuringSession", double.NaN);
            if (!double.IsFinite(expected) || !double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            {
                reason = "common_progress_mismatch_" + id;
                return false;
            }
            if (expectedLastAccess < 0 || actualLastAccess != expectedLastAccess)
            {
                reason = "common_last_access_mismatch_" + id;
                return false;
            }
            if (!double.IsFinite(sessionPoints) || Math.Abs(sessionPoints) > tolerance)
            {
                reason = "common_session_points_not_reset_" + id;
                return false;
            }
        }

        foreach (JsonObject persisted in persistedCommon.Values)
        {
            string id = GetString(persisted, "Id") ?? string.Empty;
            double sessionPoints = ReadFiniteDoubleOrDefault(persisted, "PointsEarnedDuringSession", double.NaN);
            if (!double.IsFinite(sessionPoints) || Math.Abs(sessionPoints) > tolerance)
            {
                reason = "persistent_common_session_points_not_reset_" + id;
                return false;
            }
        }

        foreach (JsonObject runtime in runtimeMastering.OfType<JsonObject>())
        {
            string id = GetString(runtime, "Id") ?? string.Empty;
            if (!persistedMastering.TryGetValue(id, out JsonObject? persisted))
            {
                reason = "mastering_skill_missing_" + id;
                return false;
            }
            double expected = ReadFiniteDoubleOrDefault(runtime, "Progress", double.NaN);
            double actual = ReadFiniteDoubleOrDefault(persisted, "Progress", double.NaN);
            if (!double.IsFinite(expected) || !double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            {
                reason = "mastering_progress_mismatch_" + id;
                return false;
            }
        }

        reason = "ok";
        return true;
    }

    private static VanguardRaidSkillCommitResult SkillCommitFailure(
        string reason,
        string storageProfileId,
        string operatorId,
        VanguardRaidSkillPreparedSnapshot prepared,
        string profilePath)
        => new(false, reason, storageProfileId, operatorId, prepared.CommonSkillCount, 0, 0.0,
            prepared.MasteringSkillCount, 0, 0.0, prepared.RuntimeFingerprint, string.Empty, profilePath);

    private static int CountTreeItems(JsonObject inventory, string? rootId)
    {
        if (string.IsNullOrWhiteSpace(rootId))
        {
            return 0;
        }

        JsonArray items = GetItemsArray(inventory);
        return CollectTreeIds(items, rootId).Count;
    }

    private JsonObject BuildSessionInventory(JsonObject playerInventory, JsonObject operatorInventory, string storageProfileId, string operatorId)
    {
        JsonArray playerItems = GetItemsArray(playerInventory);
        JsonArray operatorItems = GetItemsArray(operatorInventory);
        string playerEquipmentId = RequireString(playerInventory, "equipment", "player_inventory_equipment_missing");
        string operatorEquipmentId = RequireString(operatorInventory, "equipment", "operator_inventory_equipment_missing");
        HashSet<string> playerEquipmentTree = CollectTreeIds(playerItems, playerEquipmentId);
        HashSet<string> operatorEquipmentTree = CollectTreeIds(operatorItems, operatorEquipmentId);

        JsonArray merged = new();
        AddClonedItems(merged, playerItems, item => !playerEquipmentTree.Contains(GetItemId(item) ?? string.Empty));

        string sessionEquipmentId = operatorEquipmentId;
        string repairReason = AuditEquipmentTree(operatorItems, operatorEquipmentTree, operatorEquipmentId);
        if (repairReason == "ok" || repairReason == "legacy_pockets_with_payload")
        {
            AddClonedItems(merged, operatorItems, item => operatorEquipmentTree.Contains(GetItemId(item) ?? string.Empty));
            if (repairReason == "legacy_pockets_with_payload")
            {
                RepairLegacyPocketsTemplate(merged, sessionEquipmentId, playerItems, playerEquipmentId, operatorId);
                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_COMMIT_KEEP_STATUS] legacy_pockets_tolerated_with_payload operator={operatorId}; result=operator_equipment_tree_kept; equipment={operatorEquipmentId}; treeItems={operatorEquipmentTree.Count}"));
            }
            else if (IsEmptyOperatorEquipmentTree(operatorItems, operatorEquipmentTree, operatorEquipmentId))
            {
                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_EMPTY_EQUIPMENT_BASELINE_STATUS] empty_operator_equipment_baseline operator={operatorId}; result=kept_empty_vanilla_scaffold; equipment={operatorEquipmentId}; treeItems={operatorEquipmentTree.Count}"));
            }
            else
            {
                logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_TREE_REPAIR_STATUS] inventory_tree_audit operator={operatorId}; result=kept_operator_tree; equipment={operatorEquipmentId}; treeItems={operatorEquipmentTree.Count}"));
            }
        }
        else
        {
            sessionEquipmentId = FirstNonEmpty(operatorEquipmentId, BuildStableId(storageProfileId, operatorId, "equipment"));
            JsonArray scaffold = BuildVanillaEquipmentScaffoldItems(playerItems, playerEquipmentId, sessionEquipmentId, storageProfileId, operatorId);
            AddClonedItems(merged, scaffold, _ => true);
            logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_EMPTY_EQUIPMENT_BASELINE_STATUS] empty_operator_equipment_baseline operator={operatorId}; result=rebuilt_empty_vanilla_scaffold; reason={repairReason}; equipment={sessionEquipmentId}; scaffoldItems={scaffold.Count}"));
        }

        RemoveItemsWithMissingParents(merged);
        JsonObject sessionInventory = CloneObject(playerInventory);
        sessionInventory["items"] = merged;
        SetJsonValue(sessionInventory, "equipment", sessionEquipmentId);
        SanitizeInventoryReferences(sessionInventory);
        EnsureRootFieldsReferenceExistingItems(sessionInventory);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_INVENTORY_TREE_REPAIR_STATUS] inventory_tree_audit operator={operatorId}; sessionEquipment={sessionEquipmentId}; items={merged.Count}; fastPanel={CountObjectProperties(GetOrCreateObject(sessionInventory, "fastPanel"))}; favorites={GetOrCreateArray(sessionInventory, "favoriteItems").Count}"));
        return sessionInventory;
    }

    private static void RepairLegacyPocketsTemplate(JsonArray items, string equipmentId, JsonArray templateItems, string templateEquipmentId, string operatorId)
    {
        JsonObject? pockets = FindDirectChildBySlot(items, equipmentId, "Pockets");
        if (pockets == null)
        {
            return;
        }

        string? currentTpl = GetString(pockets, "_tpl");
        if (!string.Equals(currentTpl, "a8edfb0bce53d103d3f62b9b", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        JsonObject? templatePockets = FindDirectChildBySlot(templateItems, templateEquipmentId, "Pockets")
            ?? FindFirstItemBySlot(templateItems, "Pockets");
        string? templateTpl = templatePockets == null ? null : GetString(templatePockets, "_tpl");
        if (string.IsNullOrWhiteSpace(templateTpl) || string.Equals(templateTpl, currentTpl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetJsonValue(pockets, "_tpl", templateTpl);
    }

    private static JsonObject BuildOperatorInventoryForCommit(JsonObject operatorInventory, JsonObject sessionInventory)
    {
        JsonArray operatorItems = GetItemsArray(operatorInventory);
        JsonArray sessionItems = GetItemsArray(sessionInventory);
        string operatorEquipmentId = RequireString(operatorInventory, "equipment", "operator_inventory_equipment_missing");
        string sessionEquipmentId = RequireString(sessionInventory, "equipment", "session_inventory_equipment_missing");
        HashSet<string> oldOperatorEquipmentTree = CollectTreeIds(operatorItems, operatorEquipmentId);
        HashSet<string> sessionEquipmentTree = CollectTreeIds(sessionItems, sessionEquipmentId);

        JsonArray merged = new();
        AddClonedItems(merged, operatorItems, item => !oldOperatorEquipmentTree.Contains(GetItemId(item) ?? string.Empty));
        AddClonedItems(merged, sessionItems, item => sessionEquipmentTree.Contains(GetItemId(item) ?? string.Empty));

        JsonObject result = CloneObject(operatorInventory);
        result["items"] = merged;
        SetJsonValue(result, "equipment", sessionEquipmentId);

        // The runtime CompleteProfileDescriptor is the final truth for Operator
        // equipment quick-slots too.  Copy it for survivors; KIA preparation
        // explicitly clears it to mirror SPT death handling.
        if (TryGetObject(sessionInventory, "fastPanel", out JsonObject? fastPanel) && fastPanel is not null)
        {
            result[FindPropertyName(result, "fastPanel") ?? "fastPanel"] = fastPanel.DeepClone();
        }

        EnsureRootFieldsReferenceExistingItems(result);
        SanitizeInventoryReferences(result);
        return result;
    }

    private static JsonObject BuildPlayerInventoryForCommit(JsonObject playerInventory, JsonObject sessionInventory)
    {
        JsonArray playerItems = GetItemsArray(playerInventory);
        JsonArray sessionItems = GetItemsArray(sessionInventory);
        string playerEquipmentId = RequireString(playerInventory, "equipment", "player_inventory_equipment_missing");
        string sessionEquipmentId = RequireString(sessionInventory, "equipment", "session_inventory_equipment_missing");
        HashSet<string> playerEquipmentTree = CollectTreeIds(playerItems, playerEquipmentId);
        HashSet<string> sessionEquipmentTree = CollectTreeIds(sessionItems, sessionEquipmentId);

        JsonArray merged = new();
        AddClonedItems(merged, playerItems, item => playerEquipmentTree.Contains(GetItemId(item) ?? string.Empty));
        AddClonedItems(merged, sessionItems, item => !sessionEquipmentTree.Contains(GetItemId(item) ?? string.Empty));

        JsonObject result = CloneObject(playerInventory);
        result["items"] = merged;
        foreach (string field in new[] { "stash", "sortingTable", "questRaidItems", "questStashItems", "hideoutCustomizationStashId" })
        {
            string? value = GetString(sessionInventory, field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetJsonValue(result, field, value);
            }
        }

        if (TryGetObject(sessionInventory, "hideoutAreaStashes", out JsonObject? hideoutAreaStashes) && hideoutAreaStashes is not null)
        {
            result["hideoutAreaStashes"] = hideoutAreaStashes.DeepClone();
        }

        if (TryGetObject(sessionInventory, "fastPanel", out JsonObject? fastPanel) && fastPanel is not null)
        {
            result["fastPanel"] = fastPanel.DeepClone();
        }

        if (TryGetArray(sessionInventory, "favoriteItems", out JsonArray? favoriteItems) && favoriteItems is not null)
        {
            result["favoriteItems"] = favoriteItems.DeepClone();
        }

        SetJsonValue(result, "equipment", playerEquipmentId);
        EnsureRootFieldsReferenceExistingItems(result);
        return result;
    }


    private void NormalizeCompleteSessionProfileNode(JsonObject profile, string storageProfileId, string operatorId, string context)
    {
        JsonObject pmc = GetPmcObject(profile);
        NormalizeCompleteProfileDescriptor(pmc, storageProfileId, operatorId, context + ":pmc");

        JsonObject scav = GetScavObject(profile);
        if (scav.Count > 0)
        {
            NormalizeCompleteProfileDescriptor(scav, storageProfileId, operatorId, context + ":scav");
        }
    }

    private void NormalizeCompleteProfileDescriptor(JsonObject descriptor, string storageProfileId, string operatorId, string context)
    {
        string profileId = FirstNonEmpty(GetString(descriptor, "_id"), BuildStableInventoryProfileId(storageProfileId, operatorId));
        SetJsonValue(descriptor, "_id", profileId);
        EnsureString(descriptor, "aid", storageProfileId);
        EnsureString(descriptor, "savage", BuildStableId(storageProfileId, operatorId, "scav"));
        EnsureNumber(descriptor, "karmaValue", 0);

        JsonObject info = GetOrCreateObject(descriptor, "Info");
        EnsureString(info, "Nickname", operatorId);
        EnsureString(info, "LowerNickname", GetString(info, "Nickname")?.ToLowerInvariant() ?? operatorId.ToLowerInvariant());
        EnsureString(info, "Side", "Usec");
        EnsureString(info, "Voice", "usec_1");
        EnsureNumber(info, "Level", 1);
        EnsureNumber(info, "Experience", 0);
        EnsureNumber(info, "RegistrationDate", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        EnsureObject(descriptor, "Customization");
        EnsureObject(descriptor, "Encyclopedia");
        EnsureProfileHealth(descriptor);
        EnsureInventoryDescriptor(descriptor, storageProfileId, operatorId, context);
        EnsureArray(descriptor, "InsuredItems");
        EnsureSkillsDescriptor(descriptor);
        EnsureObject(descriptor, "Notes");
        EnsureObject(descriptor, "TaskConditionCounters");
        EnsureArray(descriptor, "Quests");
        EnsureObject(descriptor, "Achievements");
        EnsureObject(descriptor, "Prestige");
        EnsureObject(descriptor, "Variables");
        JsonObject unlocked = EnsureObject(descriptor, "UnlockedInfo");
        EnsureArray(unlocked, "unlockedProductionRecipe");
        JsonObject transferLimit = EnsureObject(descriptor, "moneyTransferLimitData");
        EnsureNumber(transferLimit, "nextResetTime", 0);
        EnsureNumber(transferLimit, "remainingLimit", 0);
        EnsureNumber(transferLimit, "totalLimit", 0);
        EnsureNumber(transferLimit, "resetInterval", 0);
        EnsureArray(descriptor, "Bonuses");
        EnsureObject(descriptor, "Hideout");
        EnsureObject(descriptor, "RagfairInfo");
        EnsureObject(descriptor, "WishList");
        JsonObject stats = EnsureObject(descriptor, "Stats");
        EnsureEftStats(stats);
        EnsureObject(descriptor, "CheckedMagazines");
        EnsureArray(descriptor, "CheckedChambers");
        EnsureObject(descriptor, "TradersInfo");

        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS] normalized context={context}; storage={storageProfileId}; operator={operatorId}; profile={profileId}; inventoryItems={GetItemsArray(GetOrCreateObject(descriptor, "Inventory")).Count}"));
    }

    private static void EnsureProfileHealth(JsonObject descriptor)
    {
        JsonObject health = EnsureObject(descriptor, "Health");
        JsonObject bodyParts = EnsureObject(health, "BodyParts");
        EnsureBodyPart(bodyParts, "Head", 35);
        EnsureBodyPart(bodyParts, "Chest", 85);
        EnsureBodyPart(bodyParts, "Stomach", 70);
        EnsureBodyPart(bodyParts, "LeftArm", 60);
        EnsureBodyPart(bodyParts, "RightArm", 60);
        EnsureBodyPart(bodyParts, "LeftLeg", 65);
        EnsureBodyPart(bodyParts, "RightLeg", 65);
        EnsureValueInfo(health, "Energy", 100, 0, 100);
        EnsureValueInfo(health, "Hydration", 100, 0, 100);
        EnsureValueInfo(health, "Temperature", 36.6, 0, 100);
        EnsureValueInfo(health, "Poison", 0, 0, 100);
        EnsureNumber(health, "UpdateTime", 0);
    }

    private static void EnsureBodyPart(JsonObject bodyParts, string name, double maximum)
    {
        JsonObject part = EnsureObject(bodyParts, name);
        EnsureValueInfo(part, "Health", maximum, 0, maximum);
        EnsureObject(part, "Effects");
    }

    private static void EnsureValueInfo(JsonObject parent, string name, double current, double minimum, double maximum)
    {
        JsonObject value = EnsureObject(parent, name);
        EnsureNumber(value, "Current", current);
        EnsureNumber(value, "Minimum", minimum);
        EnsureNumber(value, "Maximum", maximum);
        EnsureNumber(value, "OverDamageReceivedMultiplier", 0);
        EnsureNumber(value, "EnvironmentDamageMultiplier", 0);
    }

    private void EnsureInventoryDescriptor(JsonObject descriptor, string storageProfileId, string operatorId, string context)
    {
        JsonObject inventory = GetOrCreateObject(descriptor, "Inventory");
        JsonArray items = GetItemsArray(inventory);
        if (items.Count == 0)
        {
            string equipmentFallbackId = BuildStableId(storageProfileId, operatorId, "equipment");
            string stashFallbackId = BuildStableId(storageProfileId, operatorId, "stash");
            inventory["items"] = new JsonArray
            {
                CreateRootItem(equipmentFallbackId, "55d7217a4bdc2d86028b456d"),
                CreateRootItem(stashFallbackId, "5811ce772459770e9e5f9532")
            };
            items = GetItemsArray(inventory);
            SetJsonValue(inventory, "equipment", equipmentFallbackId);
            SetJsonValue(inventory, "stash", stashFallbackId);
        }

        string equipmentId = FirstNonEmpty(GetString(inventory, "equipment"), FindFirstRootItemId(items, "55d7217a4bdc2d86028b456d"), BuildStableId(storageProfileId, operatorId, "equipment"));
        if (!ItemExists(items, equipmentId))
        {
            items.Add(CreateRootItem(equipmentId, "55d7217a4bdc2d86028b456d"));
        }

        string stashId = FirstNonEmpty(GetString(inventory, "stash"), FindFirstRootItemId(items, "5811ce772459770e9e5f9532"), BuildStableId(storageProfileId, operatorId, "stash"));
        if (!ItemExists(items, stashId))
        {
            items.Add(CreateRootItem(stashId, "5811ce772459770e9e5f9532"));
        }

        SetJsonValue(inventory, "equipment", equipmentId);
        SetJsonValue(inventory, "stash", stashId);
        EnsureInventoryRoot(inventory, items, "sortingTable", storageProfileId, operatorId, "sorting-table", "602543c13fee350cd564d032");
        EnsureInventoryRoot(inventory, items, "questRaidItems", storageProfileId, operatorId, "quest-raid", "5963866286f7747bf429b572");
        EnsureInventoryRoot(inventory, items, "questStashItems", storageProfileId, operatorId, "quest-stash", "5963866b86f7747bfa1c4462");
        EnsureInventoryRoot(inventory, items, "hideoutCustomizationStashId", storageProfileId, operatorId, "hideout-customization", "673c7b00cbf4b984b5099181");
        EnsureObject(inventory, "hideoutAreaStashes");
        EnsureObject(inventory, "fastPanel");
        EnsureArray(inventory, "favoriteItems");
        RemoveItemsWithMissingParents(items);
        SanitizeInventoryReferences(inventory);
        logger.Info(VanguardServerDiagnosticsLog.Present($"[VANGUARD_OPERATOR_SESSION_PROFILE_NORMALIZATION_STATUS] inventory normalized context={context}; equipment={equipmentId}; stash={stashId}; items={items.Count}; treeAudit={AuditInventoryTree(inventory)}"));
    }

    private static void EnsureInventoryRoot(JsonObject inventory, JsonArray items, string fieldName, string storageProfileId, string operatorId, string stableSuffix, string fallbackTpl)
    {
        string id = FirstNonEmpty(GetString(inventory, fieldName), FindFirstRootItemId(items, fallbackTpl), BuildStableId(storageProfileId, operatorId, stableSuffix));
        if (!ItemExists(items, id))
        {
            items.Add(CreateRootItem(id, fallbackTpl));
        }

        SetJsonValue(inventory, fieldName, id);
    }

    private static string? FindFirstRootItemId(JsonArray items, string tpl)
    {
        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            if (string.Equals(GetString(item, "_tpl"), tpl, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(GetString(item, "parentId")))
            {
                return GetItemId(item);
            }
        }

        return null;
    }

    private static void EnsureSkillsDescriptor(JsonObject descriptor)
    {
        JsonObject skills = EnsureObject(descriptor, "Skills");
        EnsureArray(skills, "Common");
        EnsureArray(skills, "Mastering");
        EnsureNumber(skills, "Points", 0);
    }

    private static void EnsureEftStats(JsonObject stats)
    {
        JsonObject eft = EnsureObject(stats, "Eft");
        JsonObject sessionCounters = EnsureObject(eft, "SessionCounters");
        EnsureArray(sessionCounters, "Items");
        JsonObject overallCounters = EnsureObject(eft, "OverallCounters");
        EnsureArray(overallCounters, "Items");
        EnsureNumber(eft, "SessionExperienceMult", 0);
        EnsureNumber(eft, "ExperienceBonusMult", 0);
        EnsureNumber(eft, "TotalSessionExperience", 0);
        EnsureNumber(eft, "LastSessionDate", 0);
        EnsureArray(eft, "DroppedItems");
        EnsureArray(eft, "FoundInRaidItems");
        EnsureArray(eft, "Victims");
        EnsureArray(eft, "CarriedQuestItems");
        EnsureNumber(eft, "TotalInGameTime", 0);
        EnsureString(eft, "SurvivorClass", "Unknown");
    }

    private static JsonObject EnsureObject(JsonObject parent, string name)
    {
        string actualName = FindPropertyName(parent, name) ?? name;
        if (parent[actualName] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        parent[actualName] = obj;
        return obj;
    }

    private static JsonArray EnsureArray(JsonObject parent, string name)
    {
        string actualName = FindPropertyName(parent, name) ?? name;
        if (parent[actualName] is JsonArray array)
        {
            return array;
        }

        array = new JsonArray();
        parent[actualName] = array;
        return array;
    }

    private static void EnsureString(JsonObject parent, string name, string value)
    {
        string? existing = GetString(parent, name);
        if (string.IsNullOrWhiteSpace(existing))
        {
            SetJsonValue(parent, name, value);
        }
    }

    private static void EnsureNumber(JsonObject parent, string name, double value)
    {
        string actualName = FindPropertyName(parent, name) ?? name;
        if (parent[actualName] == null)
        {
            parent[actualName] = JsonValue.Create(value);
        }
    }

    private void ValidateOperatorProfileOrThrow(SptProfile profile, string storageProfileId, string operatorId, string context)
    {
        JsonObject node = ProfileToNode(profile);
        JsonObject inventory = GetInventoryObject(node);
        JsonArray items = GetItemsArray(inventory);
        string equipmentId = RequireString(inventory, "equipment", "equipment_root_missing");
        string stashId = RequireString(inventory, "stash", "stash_root_missing");
        if (!ItemExists(items, equipmentId))
        {
            throw new InvalidOperationException($"operator_profile_invalid_{context}_equipment_root_item_missing_{storageProfileId}_{operatorId}");
        }

        if (!ItemExists(items, stashId))
        {
            throw new InvalidOperationException($"operator_profile_invalid_{context}_stash_root_item_missing_{storageProfileId}_{operatorId}");
        }

        if (!HasSlotItem(node, "Pockets"))
        {
            throw new InvalidOperationException($"operator_profile_invalid_{context}_pockets_missing_{storageProfileId}_{operatorId}");
        }
    }

    private static VanguardOperatorInventoryModeResponse Failure(string requestedProfileId, string storageProfileId, string? operatorId, string reason)
    {
        return new VanguardOperatorInventoryModeResponse
        {
            Success = false,
            Reason = reason,
            Active = false,
            RequestedProfileId = requestedProfileId,
            StorageProfileId = storageProfileId,
            OperatorId = operatorId,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string ResolveDisplayName(VanguardOperatorProfile profile)
    {
        return FirstNonEmpty(profile.Identity.DisplayName, profile.Identity.Callsign, profile.OperatorId, "Unknown Operator");
    }

    private static string ResolveInventoryProfileId(SptProfile profile, string storageProfileId, string operatorId)
    {
        object? profileInfoId = ResolveNestedProperty(profile, "ProfileInfo", "ProfileId");
        string? value = profileInfoId?.ToString();
        return string.IsNullOrWhiteSpace(value) ? BuildStableInventoryProfileId(storageProfileId, operatorId) : value!;
    }

    private static VanguardOperatorInventorySummary BuildSummary(string operatorId, string displayName, string inventoryProfileId, string profilePath, SptProfile? profile)
    {
        bool exists = File.Exists(profilePath);
        int itemCount = CountInventoryItems(profile);
        bool hasPrimary = HasSlotItem(profile, "FirstPrimaryWeapon") || HasSlotItem(profile, "SecondPrimaryWeapon") || HasSlotItem(profile, "Holster");
        bool hasBackpack = HasSlotItem(profile, "Backpack");
        bool hasRig = HasSlotItem(profile, "TacticalVest");
        bool hasArmor = HasSlotItem(profile, "ArmorVest");
        string readiness = !exists ? "profile_missing"
            : !hasPrimary ? "missing_weapon"
            : "ready";
        DateTimeOffset? lastSaved = exists ? new DateTimeOffset(File.GetLastWriteTimeUtc(profilePath), TimeSpan.Zero) : null;
        return new VanguardOperatorInventorySummary(operatorId, displayName, inventoryProfileId, exists, itemCount, hasPrimary, hasBackpack, hasRig, hasArmor, readiness, lastSaved);
    }

    private static int CountInventoryItems(SptProfile? profile)
    {
        object? items = ResolveNestedProperty(profile, "CharacterData", "PmcData", "Inventory", "Items");
        if (items is System.Collections.IEnumerable enumerable)
        {
            int count = 0;
            foreach (object _ in enumerable)
            {
                count++;
            }

            return count;
        }

        return 0;
    }

    private static bool HasSlotItem(SptProfile? profile, string slotId)
    {
        object? items = ResolveNestedProperty(profile, "CharacterData", "PmcData", "Inventory", "Items");
        if (items is not System.Collections.IEnumerable enumerable)
        {
            return false;
        }

        foreach (object item in enumerable)
        {
            object? slotValue = ResolveMember(item, "SlotId") ?? ResolveMember(item, "slotId");
            if (slotValue != null && string.Equals(slotValue.ToString(), slotId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSlotItem(JsonObject profile, string slotId)
    {
        JsonArray items = GetItemsArray(GetInventoryObject(profile));
        foreach (JsonNode? node in items)
        {
            if (node is JsonObject item && string.Equals(GetString(item, "slotId"), slotId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SemaphoreSlim GetProfileLock(string storageProfileId, string operatorId)
    {
        string key = storageProfileId + ":" + operatorId;
        return profileLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private static object? ResolveNestedProperty(object? target, params string[] path)
    {
        object? current = target;
        foreach (string segment in path)
        {
            current = ResolveMember(current, segment);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    private static object? ResolveMember(object? target, string name)
    {
        if (target == null)
        {
            return null;
        }

        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (property != null)
        {
            return property.GetValue(target);
        }

        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        return field?.GetValue(target);
    }

    private static void CopyProfileMembers(SptProfile source, SptProfile target)
    {
        Type type = typeof(SptProfile);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            try
            {
                property.SetValue(target, property.GetValue(source));
            }
            catch
            {
                // Best-effort runtime profile copy. SaveServer owns the instance; unsupported members are ignored.
            }
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            try
            {
                field.SetValue(target, field.GetValue(source));
            }
            catch
            {
                // Best-effort runtime profile copy. SaveServer owns the instance; unsupported members are ignored.
            }
        }
    }

    private static JsonObject GetPmcObject(JsonObject profile)
    {
        JsonObject characters = GetOrCreateObject(profile, "characters");
        return GetOrCreateObject(characters, "pmc");
    }

    private static JsonObject GetScavObject(JsonObject profile)
    {
        JsonObject characters = GetOrCreateObject(profile, "characters");
        return GetOrCreateObject(characters, "scav");
    }

    private static JsonObject GetInventoryObject(JsonObject profile)
    {
        return GetOrCreateObject(GetPmcObject(profile), "Inventory");
    }

    private static void ReplaceInventory(JsonObject pmc, JsonObject inventory)
    {
        pmc["Inventory"] = inventory;
    }

    private static JsonArray GetItemsArray(JsonObject inventory)
    {
        if (TryGetArray(inventory, "items", out JsonArray? items) && items is not null)
        {
            return items;
        }

        var created = new JsonArray();
        inventory["items"] = created;
        return created;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        string actualName = FindPropertyName(parent, name) ?? name;
        if (parent[actualName] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        parent[actualName] = obj;
        return obj;
    }

    private static bool TryGetObject(JsonObject parent, string name, out JsonObject? value)
    {
        value = null;
        string? actualName = FindPropertyName(parent, name);
        if (actualName != null && parent[actualName] is JsonObject obj)
        {
            value = obj;
            return true;
        }

        return false;
    }

    private static bool TryGetArray(JsonObject parent, string name, out JsonArray? value)
    {
        value = null;
        string? actualName = FindPropertyName(parent, name);
        if (actualName != null && parent[actualName] is JsonArray array)
        {
            value = array;
            return true;
        }

        return false;
    }

    private static string? FindPropertyName(JsonObject parent, string name)
    {
        foreach (KeyValuePair<string, JsonNode?> property in parent)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return null;
    }

    private static string? NodeToString(JsonNode? node)
    {
        if (node == null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return node.ToString();
        }
    }

    private static string? GetString(JsonObject parent, string name)
    {
        string? actualName = FindPropertyName(parent, name);
        if (actualName == null || parent[actualName] == null)
        {
            return null;
        }

        try
        {
            return parent[actualName]!.GetValue<string>();
        }
        catch
        {
            return parent[actualName]!.ToString();
        }
    }

    private static string RequireString(JsonObject parent, string name, string reason)
    {
        string? value = GetString(parent, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(reason);
        }

        return value;
    }

    private static void SetJsonValue(JsonObject parent, string name, string value) => parent[FindPropertyName(parent, name) ?? name] = JsonValue.Create(value);

    private static void SetJsonValue(JsonObject parent, string name, int value) => parent[FindPropertyName(parent, name) ?? name] = JsonValue.Create(value);

    private static void SetJsonValue(JsonObject parent, string name, long value) => parent[FindPropertyName(parent, name) ?? name] = JsonValue.Create(value);

    private static JsonObject CloneObject(JsonObject source) => source.DeepClone().AsObject();

    private static JsonObject CreateRootItem(string id, string tpl)
    {
        return new JsonObject
        {
            ["_id"] = id,
            ["_tpl"] = tpl
        };
    }

    private static string ResolveRootTemplate(JsonObject inventory, JsonArray items, string fieldName, string fallbackTpl)
    {
        string? rootId = GetString(inventory, fieldName);
        if (!string.IsNullOrWhiteSpace(rootId))
        {
            foreach (JsonNode? node in items)
            {
                if (node is JsonObject item && string.Equals(GetItemId(item), rootId, StringComparison.OrdinalIgnoreCase))
                {
                    string? tpl = GetString(item, "_tpl");
                    if (!string.IsNullOrWhiteSpace(tpl))
                    {
                        return tpl;
                    }
                }
            }
        }

        return fallbackTpl;
    }

    private static HashSet<string> CollectTreeIds(JsonArray items, string? rootId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rootId))
        {
            return result;
        }

        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            string? id = GetItemId(item);
            string? parentId = GetString(item, "parentId");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(parentId))
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(parentId, out List<string>? children))
            {
                children = new List<string>();
                childrenByParent[parentId] = children;
            }

            children.Add(id);
        }

        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!result.Add(current))
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(current, out List<string>? children))
            {
                continue;
            }

            foreach (string child in children)
            {
                queue.Enqueue(child);
            }
        }

        return result;
    }

    private static void AddClonedItems(JsonArray target, JsonArray source, Func<JsonObject, bool> predicate)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? existing in target)
        {
            if (existing is JsonObject existingItem)
            {
                string? existingId = GetItemId(existingItem);
                if (!string.IsNullOrWhiteSpace(existingId))
                {
                    seen.Add(existingId);
                }
            }
        }

        foreach (JsonNode? node in source)
        {
            if (node is not JsonObject item || !predicate(item))
            {
                continue;
            }

            string? id = GetItemId(item);
            if (!string.IsNullOrWhiteSpace(id) && !seen.Add(id))
            {
                continue;
            }

            target.Add(item.DeepClone());
        }
    }

    private static string? GetItemId(JsonObject item) => GetString(item, "_id") ?? GetString(item, "Id");

    private static bool ItemExists(JsonArray items, string itemId)
    {
        foreach (JsonNode? node in items)
        {
            if (node is JsonObject item && string.Equals(GetItemId(item), itemId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureRootFieldsReferenceExistingItems(JsonObject inventory)
    {
        JsonArray items = GetItemsArray(inventory);
        foreach (string field in new[] { "equipment", "stash", "sortingTable", "questRaidItems", "questStashItems", "hideoutCustomizationStashId" })
        {
            string? id = GetString(inventory, field);
            if (!string.IsNullOrWhiteSpace(id) && !ItemExists(items, id))
            {
                throw new InvalidOperationException("inventory_root_missing_" + field);
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string BuildStableInventoryProfileId(string storageProfileId, string operatorId)
    {
        return BuildStableId(storageProfileId, operatorId, "profile");
    }

    private static string BuildStableId(string storageProfileId, string operatorId, string purpose)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(storageProfileId + ":" + operatorId + ":" + purpose));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }
}

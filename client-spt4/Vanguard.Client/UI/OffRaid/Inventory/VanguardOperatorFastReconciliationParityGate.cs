using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vanguard.Client.Diagnostics;

// Responsibility: admission gate for bounded Operator -> player fast reconciliation.
// Flow: capture the retained player graph at Operator-session entry, compare it after authoritative inventory-mode exit, inspect the candidate
//       player-equipment + Operator-stash spatial graph, and return a strict PARITY decision before any fast-path mutation is attempted.
// Authority boundary: read-only gate. Mutations are owned by VanguardOperatorFastReconciliationExecutor and only PARITY_OK may enter it.
// Invariant: any missing/changed/ambiguous invariant yields PARITY_UNSAFE_OR_UNKNOWN and the proven MainMenuController.method_21 fallback remains authoritative.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorFastReconciliationParityGate
{
    private const string StatusTag = "VANGUARD_OPERATOR_FAST_RECONCILIATION_STATUS";
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal sealed class Baseline
    {
        public bool Captured { get; init; }
        public string Reason { get; init; } = "unknown";
        public string OperatorId { get; init; } = "<none>";
        public string OperatorProfileId { get; init; } = "<none>";
        public int Generation { get; init; }
        public object? Session { get; init; }
        public object? PlayerProfile { get; init; }
        public object? PlayerInventoryController { get; init; }
        public object? OperatorInventoryController { get; init; }
        public object? PlayerEquipment { get; init; }
        public object? QuestController { get; init; }
        public object? AchievementController { get; init; }
        public object? PrestigeController { get; init; }
        public object? HealthController { get; init; }
        public object? BonusController { get; init; }
        public object? NewsHub { get; init; }
        public object? StatisticsSession { get; init; }
        public string PlayerProfileId { get; init; } = "<none>";
        public InventoryFingerprint PlayerFingerprint { get; init; } = InventoryFingerprint.Invalid("not_captured");
    }

    internal sealed record InventoryFingerprint(
        bool Valid,
        string Reason,
        string Equipment,
        string QuestRaidItems,
        string QuestStashItems,
        string HideoutAreaStashes,
        string HideoutCustomizationStash,
        string FastPanel,
        string FavoriteItems,
        string DiscardLimits,
        bool DuplicateItemIds,
        string Stash,
        string SortingTable)
    {
        public static InventoryFingerprint Invalid(string reason) => new(
            false,
            reason,
            "<unknown>",
            "<unknown>",
            "<unknown>",
            "<unknown>",
            "<unknown>",
            "<unknown>",
            "<unknown>",
            "<unknown>",
            false,
            "<unknown>",
            "<unknown>");
    }

    private readonly record struct SpatialAudit(bool Known, int OverlapCount, int OutOfBoundsCount, int UniqueBadItemCount, string Reason)
    {
        public bool Clean => Known && UniqueBadItemCount == 0;
    }

    internal sealed record ExitParityResult(string Decision, string Reason)
    {
        public bool IsStrictFastPathEligible => string.Equals(Decision, "PARITY_OK", StringComparison.Ordinal);
    }

    public static Baseline CaptureBaseline(
        object mainMenuController,
        object? session,
        object? operatorProfile,
        object? operatorInventoryController,
        string? operatorProfileId,
        string? operatorId,
        int generation)
    {
        try
        {
            object? effectiveSession = session ?? ResolveMember(mainMenuController, "ISession");
            object? playerProfile = effectiveSession == null ? null : ResolveMember(effectiveSession, "Profile");
            object? playerInventoryController = ResolveMember(mainMenuController, "InventoryController");
            object? playerInventory = playerInventoryController == null ? null : ResolveMember(playerInventoryController, "Inventory");
            object? playerEquipment = playerInventory == null ? null : ResolveMember(playerInventory, "Equipment");
            string playerProfileId = ResolveProfileId(playerProfile);

            InventoryFingerprint fingerprint = TryBuildInventoryFingerprint(playerProfile, effectiveSession, playerInventory, out string fingerprintReason)
                ?? InventoryFingerprint.Invalid(fingerprintReason);

            bool captured = effectiveSession != null
                && playerProfile != null
                && playerInventoryController != null
                && playerEquipment != null
                && operatorInventoryController != null
                && !ReferenceEquals(playerInventoryController, operatorInventoryController)
                && fingerprint.Valid;

            string reason = captured ? "ok" : BuildCaptureFailureReason(
                effectiveSession,
                playerProfile,
                playerInventoryController,
                playerEquipment,
                operatorInventoryController,
                fingerprintReason);

            Baseline baseline = new()
            {
                Captured = captured,
                Reason = reason,
                OperatorId = Normalize(operatorId),
                OperatorProfileId = Normalize(operatorProfileId ?? ResolveProfileId(operatorProfile)),
                Generation = generation,
                Session = effectiveSession,
                PlayerProfile = playerProfile,
                PlayerInventoryController = playerInventoryController,
                OperatorInventoryController = operatorInventoryController,
                PlayerEquipment = playerEquipment,
                QuestController = ResolveMember(mainMenuController, "LocalQuestControllerClass"),
                AchievementController = ResolveMember(mainMenuController, "AbstractAchievementControllerClass"),
                PrestigeController = ResolveMember(mainMenuController, "AbstractPrestigeControllerClass"),
                HealthController = ResolveMember(mainMenuController, "HealthControllerClass") ?? ResolveMember(mainMenuController, "HealthController"),
                BonusController = ResolveMember(mainMenuController, "BonusController_0"),
                NewsHub = ResolveMember(mainMenuController, "NewsHubClass"),
                StatisticsSession = ResolveMember(mainMenuController, "Gclass2270_0"),
                PlayerProfileId = playerProfileId,
                PlayerFingerprint = fingerprint
            };

            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"fast_gate_baseline_captured captured={captured}; reason={reason}; operator={baseline.OperatorId}; operatorProfile={baseline.OperatorProfileId}; generation={generation}; playerProfile={playerProfileId}; playerController={Describe(playerInventoryController)}; operatorController={Describe(operatorInventoryController)}; controllerDistinct={!ReferenceEquals(playerInventoryController, operatorInventoryController)}; fingerprintValid={fingerprint.Valid}; duplicateItemIds={fingerprint.DuplicateItemIds}; mode=strict_gate");

            return baseline;
        }
        catch (Exception exception)
        {
            string reason = exception.GetType().Name + ":" + exception.Message;
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"fast_gate_baseline_capture_failed reason={reason}; operator={Normalize(operatorId)}; operatorProfile={Normalize(operatorProfileId)}; generation={generation}; mode=strict_gate");
            return new Baseline
            {
                Captured = false,
                Reason = reason,
                OperatorId = Normalize(operatorId),
                OperatorProfileId = Normalize(operatorProfileId),
                Generation = generation,
                Session = session,
                OperatorInventoryController = operatorInventoryController
            };
        }
    }

    public static ExitParityResult EvaluateAfterAuthoritativeExit(
        Baseline? baseline,
        object mainMenuController,
        object? session,
        object? operatorProfile,
        object? operatorInventoryController,
        bool directCommitSucceeded,
        bool exitSucceeded,
        string source)
    {
        try
        {
            if (baseline == null)
            {
                LogDecision(
                    "PARITY_UNSAFE_OR_UNKNOWN",
                    "baseline_missing",
                    source,
                    directCommitSucceeded,
                    exitSucceeded,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    "unknown",
                    0,
                    0,
                    0,
                    false,
                    false,
                    false,
                    false,
                    false,
                    "<unknown>",
                    "<unknown>");
                return new ExitParityResult("PARITY_UNSAFE_OR_UNKNOWN", "baseline_missing");
            }

            object? effectiveSession = session ?? ResolveMember(mainMenuController, "ISession");
            object? playerProfile = effectiveSession == null ? null : ResolveMember(effectiveSession, "Profile");
            object? playerInventoryController = ResolveMember(mainMenuController, "InventoryController");
            object? playerInventory = playerInventoryController == null ? null : ResolveMember(playerInventoryController, "Inventory");
            object? playerEquipment = playerInventory == null ? null : ResolveMember(playerInventory, "Equipment");

            InventoryFingerprint playerFingerprint = TryBuildInventoryFingerprint(playerProfile, effectiveSession, playerInventory, out string playerFingerprintReason)
                ?? InventoryFingerprint.Invalid(playerFingerprintReason);
            object? operatorInventory = operatorInventoryController == null ? null : ResolveMember(operatorInventoryController, "Inventory");
            InventoryFingerprint operatorFingerprint = TryBuildInventoryFingerprint(operatorProfile, effectiveSession, operatorInventory, out string operatorFingerprintReason)
                ?? InventoryFingerprint.Invalid(operatorFingerprintReason);

            bool sessionStable = ReferenceEquals(baseline.Session, effectiveSession);
            bool playerProfileReferenceStable = ReferenceEquals(baseline.PlayerProfile, playerProfile);
            bool playerControllerStable = ReferenceEquals(baseline.PlayerInventoryController, playerInventoryController);
            bool playerIdentityStable = string.Equals(baseline.PlayerProfileId, ResolveProfileId(playerProfile), StringComparison.Ordinal);
            bool operatorControllerDistinct = playerInventoryController != null
                && operatorInventoryController != null
                && !ReferenceEquals(playerInventoryController, operatorInventoryController)
                && !ReferenceEquals(baseline.PlayerInventoryController, operatorInventoryController);
            bool playerEquipmentReferenceStable = ReferenceEquals(baseline.PlayerEquipment, playerEquipment);

            bool equipmentUnchanged = FingerprintEqual(baseline.PlayerFingerprint.Equipment, playerFingerprint.Equipment);
            bool questRaidUnchanged = FingerprintEqual(baseline.PlayerFingerprint.QuestRaidItems, playerFingerprint.QuestRaidItems);
            bool questStashUnchanged = FingerprintEqual(baseline.PlayerFingerprint.QuestStashItems, playerFingerprint.QuestStashItems);
            bool hideoutAreaUnchanged = FingerprintEqual(baseline.PlayerFingerprint.HideoutAreaStashes, playerFingerprint.HideoutAreaStashes);
            bool hideoutCustomizationUnchanged = FingerprintEqual(baseline.PlayerFingerprint.HideoutCustomizationStash, playerFingerprint.HideoutCustomizationStash);
            bool fastPanelUnchanged = FingerprintEqual(baseline.PlayerFingerprint.FastPanel, playerFingerprint.FastPanel);
            bool favoritesUnchanged = FingerprintEqual(baseline.PlayerFingerprint.FavoriteItems, playerFingerprint.FavoriteItems);
            bool discardLimitsUnchanged = FingerprintEqual(baseline.PlayerFingerprint.DiscardLimits, playerFingerprint.DiscardLimits);

            bool questControllerStable = ReferenceEquals(baseline.QuestController, ResolveMember(mainMenuController, "LocalQuestControllerClass"));
            bool achievementControllerStable = ReferenceEquals(baseline.AchievementController, ResolveMember(mainMenuController, "AbstractAchievementControllerClass"));
            bool prestigeControllerStable = ReferenceEquals(baseline.PrestigeController, ResolveMember(mainMenuController, "AbstractPrestigeControllerClass"));
            bool healthControllerStable = ReferenceEquals(baseline.HealthController, ResolveMember(mainMenuController, "HealthControllerClass") ?? ResolveMember(mainMenuController, "HealthController"));
            bool bonusControllerStable = ReferenceEquals(baseline.BonusController, ResolveMember(mainMenuController, "BonusController_0"));
            bool newsHubStable = ReferenceEquals(baseline.NewsHub, ResolveMember(mainMenuController, "NewsHubClass"));
            bool statisticsSessionStable = ReferenceEquals(baseline.StatisticsSession, ResolveMember(mainMenuController, "Gclass2270_0"));

            SpatialAudit spatial = AuditProjectedSpatialGraph(playerInventory, operatorInventory);
            bool method6Available = HasMethod(mainMenuController, "method_6", 0, requireTaskReturn: true);
            bool replaceInventoryAvailable = HasMethod(playerInventoryController, "ReplaceInventory", 1, requireTaskReturn: false);
            bool clearProfileUpdatersAvailable = HasMethod(effectiveSession, "ClearProfileUpdaters", 0, requireTaskReturn: false);
            bool updaterBindAvailable = HasStaticBindMethod("GClass2331");
            bool showMenuAvailable = HasMethod(mainMenuController, "ShowMenuScreenSync", 0, requireTaskReturn: false);

            bool retainedPlayerGraphStable = sessionStable
                && playerProfileReferenceStable
                && playerControllerStable
                && playerIdentityStable
                && operatorControllerDistinct
                && playerEquipmentReferenceStable
                && playerFingerprint.Valid
                && operatorFingerprint.Valid
                && !playerFingerprint.DuplicateItemIds
                && !operatorFingerprint.DuplicateItemIds
                && equipmentUnchanged
                && questRaidUnchanged
                && questStashUnchanged
                && hideoutAreaUnchanged
                && hideoutCustomizationUnchanged
                && fastPanelUnchanged
                && favoritesUnchanged
                && discardLimitsUnchanged
                && questControllerStable
                && achievementControllerStable
                && prestigeControllerStable
                && healthControllerStable
                && bonusControllerStable
                && newsHubStable
                && statisticsSessionStable;

            bool implementationPrimitivesAvailable = method6Available
                && replaceInventoryAvailable
                && clearProfileUpdatersAvailable
                && updaterBindAvailable
                && showMenuAvailable;

            bool candidate = baseline.Captured
                && directCommitSucceeded
                && exitSucceeded
                && retainedPlayerGraphStable
                && implementationPrimitivesAvailable
                && spatial.Known;

            string parityClass;
            string reason;
            if (!candidate)
            {
                parityClass = "PARITY_UNSAFE_OR_UNKNOWN";
                reason = ResolveUnsafeReason(
                    baseline,
                    directCommitSucceeded,
                    exitSucceeded,
                    retainedPlayerGraphStable,
                    implementationPrimitivesAvailable,
                    spatial,
                    playerFingerprint,
                    operatorFingerprint,
                    playerFingerprintReason,
                    operatorFingerprintReason);
            }
            else if (spatial.Clean)
            {
                parityClass = "PARITY_OK";
                reason = "retained_player_graph_stable_and_projected_spatial_graph_clean";
            }
            else
            {
                parityClass = "PARITY_RECOVERABLE";
                reason = "projected_spatial_graph_requires_native_method_6_repair";
            }

            LogDecision(
                parityClass,
                reason,
                source,
                directCommitSucceeded,
                exitSucceeded,
                baseline.Captured,
                sessionStable,
                playerProfileReferenceStable,
                playerControllerStable,
                playerIdentityStable,
                operatorControllerDistinct,
                playerEquipmentReferenceStable,
                equipmentUnchanged,
                questRaidUnchanged,
                questStashUnchanged,
                hideoutAreaUnchanged,
                hideoutCustomizationUnchanged,
                fastPanelUnchanged,
                favoritesUnchanged,
                discardLimitsUnchanged,
                questControllerStable && achievementControllerStable && prestigeControllerStable && healthControllerStable && bonusControllerStable && newsHubStable && statisticsSessionStable,
                spatial.Known ? (spatial.Clean ? "clean" : "bad_items_detected") : "unknown",
                spatial.OverlapCount,
                spatial.OutOfBoundsCount,
                spatial.UniqueBadItemCount,
                method6Available,
                replaceInventoryAvailable,
                clearProfileUpdatersAvailable,
                updaterBindAvailable,
                showMenuAvailable,
                operatorFingerprint.Stash,
                operatorFingerprint.SortingTable);
            return new ExitParityResult(parityClass, reason);
        }
        catch (Exception exception)
        {
            string reason = exception.GetType().Name + ":" + exception.Message;
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"exit_parity_evaluation_failed source={Normalize(source)}; reason={reason}; directCommitSafe={directCommitSucceeded}; exitSuccess={exitSucceeded}; decision=PARITY_UNSAFE_OR_UNKNOWN; actualAction=hard_reconciliation_fallback; method21Required=True; mode=strict_gate");
            return new ExitParityResult("PARITY_UNSAFE_OR_UNKNOWN", reason);
        }
    }

    private static void LogDecision(
        string parityClass,
        string reason,
        string source,
        bool directCommitSucceeded,
        bool exitSucceeded,
        bool baselineCaptured,
        bool sessionStable,
        bool playerProfileReferenceStable,
        bool playerControllerStable,
        bool playerIdentityStable,
        bool operatorControllerDistinct,
        bool playerEquipmentReferenceStable,
        bool equipmentUnchanged,
        bool questRaidUnchanged,
        bool questStashUnchanged,
        bool hideoutAreaUnchanged,
        bool hideoutCustomizationUnchanged,
        bool fastPanelUnchanged,
        bool favoritesUnchanged,
        bool discardLimitsUnchanged,
        bool retainedControllersStable,
        string spatialAudit,
        int overlaps,
        int outOfBounds,
        int uniqueBadItems,
        bool method6Available,
        bool replaceInventoryAvailable,
        bool clearProfileUpdatersAvailable,
        bool updaterBindAvailable,
        bool showMenuAvailable,
        string operatorStashFingerprint,
        string operatorSortingFingerprint)
    {
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"exit_parity_evaluated source={Normalize(source)}; decision={parityClass}; reason={reason}; directCommitSafe={directCommitSucceeded}; exitSuccess={exitSucceeded}; baselineCaptured={baselineCaptured}; sessionReferenceStable={sessionStable}; playerProfileReferenceStable={playerProfileReferenceStable}; playerControllerStable={playerControllerStable}; playerIdentityStable={playerIdentityStable}; operatorControllerDistinct={operatorControllerDistinct}; playerEquipmentReferenceStable={playerEquipmentReferenceStable}; playerEquipmentUnchanged={equipmentUnchanged}; questRaidUnchanged={questRaidUnchanged}; questStashUnchanged={questStashUnchanged}; hideoutAreaStashesUnchanged={hideoutAreaUnchanged}; hideoutCustomizationStashUnchanged={hideoutCustomizationUnchanged}; fastPanelUnchanged={fastPanelUnchanged}; favoritesUnchanged={favoritesUnchanged}; discardLimitsUnchanged={discardLimitsUnchanged}; retainedControllersStable={retainedControllersStable}; spatialAudit={spatialAudit}; overlaps={overlaps}; outOfBounds={outOfBounds}; uniqueBadItems={uniqueBadItems}; method6Available={method6Available}; replaceInventoryAvailable={replaceInventoryAvailable}; clearProfileUpdatersAvailable={clearProfileUpdatersAvailable}; updaterBindAvailable={updaterBindAvailable}; showMenuAvailable={showMenuAvailable}; operatorStashFingerprint={Short(operatorStashFingerprint)}; operatorSortingFingerprint={Short(operatorSortingFingerprint)}; actualAction=pending_strict_gate; method21FallbackAvailable=True; mode=strict_gate");
    }

    private static InventoryFingerprint? TryBuildInventoryFingerprint(object? profile, object? session, object? runtimeInventory, out string reason)
    {
        reason = "unknown";
        if (profile == null)
        {
            reason = "profile_missing";
            return null;
        }

        if (!TryBuildProfileDescriptor(profile, session, out JObject? descriptor, out reason) || descriptor == null)
        {
            return null;
        }

        JObject? inventory = GetObjectCaseInsensitive(descriptor, "Inventory");
        if (inventory == null)
        {
            reason = "inventory_descriptor_missing";
            return null;
        }

        JArray? items = GetTokenCaseInsensitive(inventory, "items") as JArray;
        if (items == null)
        {
            reason = "inventory_items_missing";
            return null;
        }

        List<JObject> itemObjects = items.OfType<JObject>().ToList();
        bool duplicateItemIds = itemObjects
            .Select(GetItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

        string equipmentRoot = GetScalar(inventory, "equipment");
        string stashRoot = GetScalar(inventory, "stash");
        string questRaidRoot = GetScalar(inventory, "questRaidItems");
        string questStashRoot = GetScalar(inventory, "questStashItems");
        string sortingRoot = GetScalar(inventory, "sortingTable");
        string hideoutCustomizationRoot = GetScalar(inventory, "hideoutCustomizationStashId");

        string equipment = FingerprintItemBranch(itemObjects, equipmentRoot);
        string stash = FingerprintItemBranch(itemObjects, stashRoot);
        string questRaid = FingerprintItemBranch(itemObjects, questRaidRoot);
        string questStash = FingerprintItemBranch(itemObjects, questStashRoot);
        string sorting = FingerprintItemBranch(itemObjects, sortingRoot);
        string hideoutCustomization = FingerprintItemBranch(itemObjects, hideoutCustomizationRoot);

        JToken? hideoutRootsToken = GetTokenCaseInsensitive(inventory, "hideoutAreaStashes");
        string hideoutAreas = FingerprintMultipleBranches(itemObjects, hideoutRootsToken);
        string fastPanel = FingerprintToken(GetTokenCaseInsensitive(inventory, "fastPanel"));
        string favorites = FingerprintToken(GetTokenCaseInsensitive(inventory, "favoriteItems"));
        string discardLimits = FingerprintDictionary(runtimeInventory == null ? null : ResolveMember(runtimeInventory, "DiscardLimits"));

        reason = duplicateItemIds ? "duplicate_item_ids_in_descriptor" : "ok";
        return new InventoryFingerprint(
            !duplicateItemIds,
            reason,
            equipment,
            questRaid,
            questStash,
            hideoutAreas,
            hideoutCustomization,
            fastPanel,
            favorites,
            discardLimits,
            duplicateItemIds,
            stash,
            sorting);
    }

    private static bool TryBuildProfileDescriptor(object profile, object? session, out JObject? descriptor, out string reason)
    {
        descriptor = null;
        reason = "unknown";
        try
        {
            Type? descriptorType = ResolveTypeByName("CompleteProfileDescriptorClass");
            if (descriptorType == null)
            {
                reason = "complete_profile_descriptor_type_not_found";
                return false;
            }

            object? searchController = ResolveSearchController(session);
            if (searchController == null)
            {
                reason = "search_controller_not_found";
                return false;
            }

            ConstructorInfo? constructor = descriptorType.GetConstructors(InstanceFlags)
                .FirstOrDefault(ctor =>
                {
                    ParameterInfo[] parameters = ctor.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType.IsAssignableFrom(profile.GetType())
                        && parameters[1].ParameterType.IsAssignableFrom(searchController.GetType());
                });
            if (constructor == null)
            {
                reason = "complete_profile_descriptor_constructor_not_found";
                return false;
            }

            object value = constructor.Invoke(new[] { profile, searchController });
            descriptor = JObject.Parse(JsonConvert.SerializeObject(value));
            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = "descriptor_" + root.GetType().Name;
            return false;
        }
    }

    private static SpatialAudit AuditProjectedSpatialGraph(object? playerInventory, object? operatorInventory)
    {
        if (playerInventory == null || operatorInventory == null)
        {
            return new SpatialAudit(false, 0, 0, 0, "inventory_missing");
        }

        try
        {
            List<object> roots = new();
            AddIfNotNull(roots, ResolveMember(playerInventory, "Equipment"));
            AddIfNotNull(roots, ResolveMember(operatorInventory, "Stash"));
            AddDictionaryValues(roots, ResolveMember(playerInventory, "HideoutAreaStashes"));

            if (roots.Count == 0)
            {
                return new SpatialAudit(false, 0, 0, 0, "projected_roots_missing");
            }

            int overlaps = 0;
            int outOfBounds = 0;
            HashSet<string> badItems = new(StringComparer.Ordinal);
            foreach (object root in roots)
            {
                object? grids = ResolveMember(root, "Grids");
                foreach (object grid in Enumerate(grids))
                {
                    foreach (object item in Enumerate(ResolveMember(grid, "OverlappingItems")))
                    {
                        overlaps++;
                        badItems.Add(ResolveItemIdentity(item));
                    }
                    foreach (object item in Enumerate(ResolveMember(grid, "OutOfBoundsItems")))
                    {
                        outOfBounds++;
                        badItems.Add(ResolveItemIdentity(item));
                    }
                }
            }

            object? sortingTable = ResolveMember(operatorInventory, "SortingTable");
            object? sortingGrid = sortingTable == null ? null : ResolveMember(sortingTable, "Grid");
            if (sortingTable == null || sortingGrid == null)
            {
                return new SpatialAudit(false, overlaps, outOfBounds, badItems.Count, "operator_sorting_table_missing");
            }

            return new SpatialAudit(true, overlaps, outOfBounds, badItems.Count, "ok");
        }
        catch (Exception exception)
        {
            return new SpatialAudit(false, 0, 0, 0, exception.GetType().Name + ":" + exception.Message);
        }
    }

    private static string ResolveUnsafeReason(
        Baseline baseline,
        bool directCommitSucceeded,
        bool exitSucceeded,
        bool retainedPlayerGraphStable,
        bool implementationPrimitivesAvailable,
        SpatialAudit spatial,
        InventoryFingerprint playerFingerprint,
        InventoryFingerprint operatorFingerprint,
        string playerFingerprintReason,
        string operatorFingerprintReason)
    {
        if (!baseline.Captured) return "baseline_not_captured:" + baseline.Reason;
        if (!directCommitSucceeded) return "direct_commit_not_safe";
        if (!exitSucceeded) return "authoritative_exit_failed";
        if (!playerFingerprint.Valid) return "player_inventory_fingerprint_invalid:" + playerFingerprintReason;
        if (!operatorFingerprint.Valid) return "operator_inventory_fingerprint_invalid:" + operatorFingerprintReason;
        if (!retainedPlayerGraphStable) return "retained_player_graph_changed_or_ambiguous";
        if (!implementationPrimitivesAvailable) return "required_native_fast_path_primitives_missing";
        if (!spatial.Known) return "projected_spatial_audit_unknown:" + spatial.Reason;
        return "unknown_parity_rejection";
    }

    private static string BuildCaptureFailureReason(
        object? session,
        object? playerProfile,
        object? playerInventoryController,
        object? playerEquipment,
        object? operatorInventoryController,
        string fingerprintReason)
    {
        if (session == null) return "session_missing";
        if (playerProfile == null) return "player_profile_missing";
        if (playerInventoryController == null) return "player_inventory_controller_missing";
        if (playerEquipment == null) return "player_equipment_missing";
        if (operatorInventoryController == null) return "operator_inventory_controller_missing";
        if (ReferenceEquals(playerInventoryController, operatorInventoryController)) return "player_operator_controller_not_distinct";
        return "player_inventory_fingerprint_invalid:" + fingerprintReason;
    }

    private static string FingerprintItemBranch(List<JObject> items, string rootId)
    {
        if (string.IsNullOrWhiteSpace(rootId)) return "<none>";

        HashSet<string> selectedIds = new(StringComparer.Ordinal) { rootId };
        bool changed;
        do
        {
            changed = false;
            foreach (JObject item in items)
            {
                string id = GetItemId(item);
                string parentId = GetScalar(item, "parentId");
                if (!string.IsNullOrWhiteSpace(id) && selectedIds.Contains(parentId) && selectedIds.Add(id))
                {
                    changed = true;
                }
            }
        } while (changed);

        JArray canonical = new(
            items.Where(item => selectedIds.Contains(GetItemId(item)))
                .OrderBy(GetItemId, StringComparer.Ordinal)
                .Select(Canonicalize));
        return FingerprintToken(canonical);
    }

    private static string FingerprintMultipleBranches(List<JObject> items, JToken? rootsToken)
    {
        if (rootsToken is not JObject rootsObject || !rootsObject.Properties().Any()) return "<none>";
        JObject combined = new();
        foreach (JProperty property in rootsObject.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            string rootId = property.Value.Type == JTokenType.Null ? string.Empty : property.Value.ToString();
            combined[property.Name] = FingerprintItemBranch(items, rootId);
        }
        return FingerprintToken(combined);
    }

    private static string FingerprintDictionary(object? dictionary)
    {
        if (dictionary == null) return "<none>";
        try
        {
            List<string> entries = new();
            foreach (object entry in Enumerate(dictionary))
            {
                object? key = ResolveMember(entry, "Key");
                object? value = ResolveMember(entry, "Value");
                entries.Add($"{Normalize(key?.ToString())}={Normalize(value?.ToString())}");
            }
            entries.Sort(StringComparer.Ordinal);
            return Hash(string.Join("|", entries));
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string FingerprintToken(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return "<none>";
        return Hash(Canonicalize(token).ToString(Formatting.None));
    }

    private static JToken Canonicalize(JToken token)
    {
        return token switch
        {
            JObject obj => new JObject(obj.Properties()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => new JProperty(property.Name, Canonicalize(property.Value)))),
            JArray array => new JArray(array.Select(Canonicalize)),
            _ => token.DeepClone()
        };
    }

    private static string Hash(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToString(bytes).Replace("-", string.Empty);
    }

    private static bool FingerprintEqual(string expected, string actual)
    {
        return !string.Equals(expected, "<unknown>", StringComparison.Ordinal)
            && !string.Equals(actual, "<unknown>", StringComparison.Ordinal)
            && string.Equals(expected, actual, StringComparison.Ordinal);
    }

    private static object? ResolveSearchController(object? session)
    {
        object? fromSession = session == null ? null : ResolveMember(session, "SearchController") ?? ResolveMember(session, "SearchControllerClass");
        if (fromSession != null) return fromSession;

        Type? controllerType = ResolveTypeByName("GClass2240");
        if (controllerType == null) return null;
        object? instance = controllerType.GetField("Instance", StaticFlags)?.GetValue(null)
            ?? controllerType.GetProperty("Instance", StaticFlags)?.GetValue(null)
            ?? controllerType.GetField("instance", StaticFlags)?.GetValue(null)
            ?? controllerType.GetProperty("instance", StaticFlags)?.GetValue(null);
        if (instance != null) return instance;
        return controllerType.GetConstructor(Type.EmptyTypes)?.Invoke(Array.Empty<object>());
    }

    private static Type? ResolveTypeByName(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? direct = assembly.GetType(name, throwOnError: false, ignoreCase: false);
                if (direct != null) return direct;
                Type? bySimpleName = assembly.GetTypes().FirstOrDefault(type => string.Equals(type.Name, name, StringComparison.Ordinal));
                if (bySimpleName != null) return bySimpleName;
            }
            catch (ReflectionTypeLoadException exception)
            {
                Type? partial = exception.Types.FirstOrDefault(type => type != null && string.Equals(type.Name, name, StringComparison.Ordinal));
                if (partial != null) return partial;
            }
            catch
            {
            }
        }
        return null;
    }

    private static bool HasStaticBindMethod(string typeName)
    {
        Type? type = ResolveTypeByName(typeName);
        return type != null && type.GetMethods(StaticFlags).Any(method => string.Equals(method.Name, "Bind", StringComparison.Ordinal) && method.GetParameters().Length == 4);
    }

    private static bool HasMethod(object? target, string name, int parameterCount, bool requireTaskReturn)
    {
        if (target == null) return false;
        return target.GetType().GetMethods(InstanceFlags).Any(method =>
            string.Equals(method.Name, name, StringComparison.Ordinal)
            && method.GetParameters().Length == parameterCount
            && (!requireTaskReturn || typeof(System.Threading.Tasks.Task).IsAssignableFrom(method.ReturnType)));
    }

    private static object? ResolveMember(object target, string name)
    {
        try
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(name, InstanceFlags);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target);
            FieldInfo? field = type.GetField(name, InstanceFlags);
            return field?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveProfileId(object? profile)
    {
        if (profile == null) return "<none>";
        object? id = ResolveMember(profile, "Id") ?? ResolveMember(profile, "ProfileId") ?? ResolveMember(profile, "_id");
        return Normalize(id?.ToString());
    }

    private static JObject? GetObjectCaseInsensitive(JObject obj, string name)
    {
        return GetTokenCaseInsensitive(obj, name) as JObject;
    }

    private static JToken? GetTokenCaseInsensitive(JObject obj, string name)
    {
        JProperty? property = obj.Properties().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
        return property?.Value;
    }

    private static string GetScalar(JObject obj, string name)
    {
        JToken? token = GetTokenCaseInsensitive(obj, name);
        return token == null || token.Type == JTokenType.Null ? string.Empty : token.ToString();
    }

    private static string GetItemId(JObject item) => GetScalar(item, "_id");

    private static IEnumerable<object> Enumerate(object? value)
    {
        if (value is not IEnumerable enumerable || value is string) yield break;
        foreach (object? item in enumerable)
        {
            if (item != null) yield return item;
        }
    }

    private static void AddDictionaryValues(List<object> destination, object? dictionary)
    {
        foreach (object entry in Enumerate(dictionary))
        {
            object? value = ResolveMember(entry, "Value");
            AddIfNotNull(destination, value);
        }
    }

    private static void AddIfNotNull(List<object> destination, object? value)
    {
        if (value != null) destination.Add(value);
    }

    private static string ResolveItemIdentity(object item)
    {
        object? id = ResolveMember(item, "Id") ?? ResolveMember(item, "_id");
        if (id != null) return id.ToString() ?? "<unknown>";
        return item.GetType().FullName + "#" + item.GetHashCode();
    }

    private static string Describe(object? value)
    {
        return value == null ? "<none>" : value.GetType().FullName ?? value.GetType().Name;
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private static string Short(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<none>";
        return value.Length <= 12 ? value : value.Substring(0, 12);
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } target)
        {
            exception = target.InnerException;
        }
        return exception;
    }
}

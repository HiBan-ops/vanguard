using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vanguard.Client.Diagnostics;

// Responsibility: executes the bounded Operator -> player fast reconciliation after the strict parity gate has returned PARITY_OK.
// Flow: preserve the retained player equipment/quest/hideout branches, clone only the live Operator stash/sorting roots through EFTInventoryClass,
//       install the projected Inventory on the retained player Profile/controller, restore the player profile updater, run native method_6 spatial repair,
//       refresh insurance/profile notifications, and show the retained Main Menu.
// Authority boundary: client graph/presentation reconciliation only. Server inventory-mode exit has already committed authoritative persistence.
// Invariant: every ambiguity/exception returns failure; the caller must immediately fall back to the proven MainMenuController.method_21 reload.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorFastReconciliationExecutor
{
    private const string StatusTag = "VANGUARD_OPERATOR_FAST_RECONCILIATION_STATUS";
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal sealed record ExecutionResult(bool Success, string Reason)
    {
        public static ExecutionResult Fail(string reason) => new(false, reason);
        public static ExecutionResult Ok(string reason) => new(true, reason);
    }

    public static async Task<ExecutionResult> TryExecuteAsync(
        VanguardOperatorFastReconciliationParityGate.Baseline baseline,
        object mainMenuController,
        object? session,
        object? operatorInventoryController,
        string source)
    {
        object? effectiveSession = session ?? ResolveMember(mainMenuController, "ISession");
        object? playerProfile = effectiveSession == null ? null : ResolveMember(effectiveSession, "Profile");
        object? playerController = ResolveMember(mainMenuController, "InventoryController");
        object? playerInventory = playerController == null ? null : ResolveMember(playerController, "Inventory");
        object? operatorInventory = operatorInventoryController == null ? null : ResolveMember(operatorInventoryController, "Inventory");

        if (effectiveSession == null || playerProfile == null || playerController == null || playerInventory == null || operatorInventory == null)
        {
            return Fail(source, "required_runtime_object_missing");
        }

        try
        {
            if (!ReferenceEquals(baseline.Session, effectiveSession)
                || !ReferenceEquals(baseline.PlayerProfile, playerProfile)
                || !ReferenceEquals(baseline.PlayerInventoryController, playerController)
                || ReferenceEquals(playerController, operatorInventoryController))
            {
                return Fail(source, "retained_player_identity_changed_after_gate");
            }

            // method_21 begins by force-closing the complete screen stack before rebuilding player
            // presentation.  The retained-player fast path must preserve that authority-transition
            // hygiene even though it deliberately skips GetProfiles()/method_5().  Without this,
            // cached EquipmentBuilds/EditBuild controllers created while consulting an Operator can
            // survive into the Player context and never hit their constructors again, leaving a
            // perfectly valid Player inventory behind an Operator-era presentation object.
            if (!TryCloseAllScreensForced(out string screenResetReason))
            {
                return Fail(source, "screen_stack_reset_failed:" + screenResetReason);
            }

            if (!TryBuildProjectedInventory(
                    effectiveSession,
                    playerInventory,
                    operatorInventory,
                    out object? projectedInventory,
                    out string projectionReason)
                || projectedInventory == null)
            {
                return Fail(source, "projection_failed:" + projectionReason);
            }

            object? retainedEquipment = ResolveMember(playerInventory, "Equipment");
            object? projectedEquipment = ResolveMember(projectedInventory, "Equipment");
            object? projectedStash = ResolveMember(projectedInventory, "Stash");
            object? projectedSorting = ResolveMember(projectedInventory, "SortingTable");
            if (retainedEquipment == null
                || projectedStash == null
                || projectedSorting == null
                || !ReferenceEquals(retainedEquipment, projectedEquipment))
            {
                return Fail(source, "projected_inventory_retained_equipment_invariant_failed");
            }

            if (!TrySetMember(playerProfile, "Inventory", projectedInventory))
            {
                return Fail(source, "player_profile_inventory_assignment_failed");
            }

            if (!TryInvokeInstance(playerController, "ReplaceInventory", new[] { projectedInventory }, out _, out string replaceReason))
            {
                return Fail(source, "replace_inventory_failed:" + replaceReason);
            }

            object? controllerInventory = ResolveMember(playerController, "Inventory");
            if (!ReferenceEquals(controllerInventory, projectedInventory))
            {
                return Fail(source, "replace_inventory_did_not_bind_projected_inventory");
            }

            // GClass3387's native constructor explicitly rebinds the CurrentAddress of every
            // player-owned inventory root to the newly-created controller. ReplaceInventory()
            // does not reproduce that constructor contract: it only assigns Inventory and may
            // create a Stash address when the address is null. The fast-path deliberately keeps
            // the existing player GClass3388 while introducing freshly cloned shared roots
            // (notably Stash/SortingTable), so their pre-existing addresses may still belong to
            // the clone/operator graph. Reassert the same root ownership contract that a fresh
            // native GClass3388 receives before any downstream view (Equipment Builds included)
            // is allowed to consume the projected graph.
            if (!TryRebindPlayerInventoryRootAddresses(playerController, projectedInventory, out string rootAddressReason))
            {
                return Fail(source, "player_inventory_root_address_rebind_failed:" + rootAddressReason);
            }

            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"fast_reconciliation_root_addresses_rebound source={Normalize(source)}; equipment=True; stash=True; sortingTable=True; questRoots=best_effort_present_only; hideoutRoots=best_effort_present_only; nativeGClass3387ConstructorParity=true; fallbackAvailable=True");

            TryRefreshStashBonuses(playerProfile, projectedInventory);

            if (!TryInvokeInstance(effectiveSession, "ClearProfileUpdaters", Array.Empty<object>(), out _, out string clearReason))
            {
                return Fail(source, "clear_profile_updaters_failed:" + clearReason);
            }

            object? questController = ResolveMember(mainMenuController, "LocalQuestControllerClass");
            if (!TryBindPlayerProfileUpdater(playerProfile, effectiveSession, playerController, questController, out string bindReason))
            {
                return Fail(source, "player_profile_updater_bind_failed:" + bindReason);
            }

            // Native TarkovApplication.method_26 refreshes all build storages through RequestBuilds(),
            // while MainMenuController.method_5 then rehydrates ItemFactory/WeaponBuildsStorage item presets.
            // An Operator EquipmentBuilds view uses the same player ISession and can leave these global
            // build stores carrying Operator-era view state even after controller/profile parity is restored.
            // Reproduce only those narrow native build-storage steps; any failure falls back to method_21.
            if (!TryInvokeTask(effectiveSession, "RequestBuilds", out Task? requestBuildsTask, out string requestBuildsReason)
                || requestBuildsTask == null)
            {
                return Fail(source, "request_builds_failed:" + requestBuildsReason);
            }
            await requestBuildsTask;

            if (!TryRehydrateWeaponBuildsStorage(effectiveSession, playerController, out string buildsReason, out int eligiblePresetCount))
            {
                return Fail(source, "weapon_builds_storage_rehydrate_failed:" + buildsReason);
            }

            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"fast_reconciliation_build_storages_rehydrated source={Normalize(source)}; requestBuilds=True; eligibleItemPresets={eligiblePresetCount}; itemFactoryWeaponStorageRebound=True; nativeMethod26AndMethod5ParitySteps=true; equipmentBuildsPlayerAuthorityPrepared=true; fallbackAvailable=True");

            if (!TryInvokeTask(mainMenuController, "method_6", out Task? spatialTask, out string method6Reason) || spatialTask == null)
            {
                return Fail(source, "native_method_6_invoke_failed:" + method6Reason);
            }
            await spatialTask;

            SpatialAudit audit = AuditPlayerSpatialGraph(projectedInventory);
            if (!audit.Known || audit.UniqueBadItems != 0)
            {
                return Fail(
                    source,
                    $"post_method_6_spatial_audit_failed:known={audit.Known},overlaps={audit.Overlaps},outOfBounds={audit.OutOfBounds},bad={audit.UniqueBadItems},reason={audit.Reason}");
            }

            if (!TryRefreshInsurance(effectiveSession, playerProfile, projectedInventory, out string insuranceReason))
            {
                return Fail(source, "insurance_refresh_failed:" + insuranceReason);
            }

            if (!TryInvokeInstance(playerController, "ReportProfileUpdate", Array.Empty<object>(), out _, out string reportReason))
            {
                return Fail(source, "report_profile_update_failed:" + reportReason);
            }

            // Native method_5 performs this lightweight state reset before showing Main Menu.
            // It is safe to preserve when present, but its absence is not an authority failure.
            TryInvokeInstance(mainMenuController, "method_28", Array.Empty<object>(), out _, out _);

            if (!TryInvokeInstance(mainMenuController, "ShowMenuScreenSync", Array.Empty<object>(), out _, out string showReason))
            {
                return Fail(source, "show_menu_failed:" + showReason);
            }

            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                $"fast_reconciliation_completed source={Normalize(source)}; decision=PARITY_OK; screenStackReset=True; projectedInventoryBound=True; retainedPlayerEquipment=True; buildStoragesRehydrated=True; nativeMethod6=True; spatialAudit=clean; overlaps=0; outOfBounds=0; insuranceRefreshed=True; playerUpdaterRebound=True; method21Called=False; fallbackAvailable=True; runtimeBehaviorChanged=True");
            return ExecutionResult.Ok("fast_reconciliation_completed");
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            return Fail(source, root.GetType().Name + ":" + root.Message);
        }
    }

    private static bool TryBuildProjectedInventory(
        object session,
        object playerInventory,
        object operatorInventory,
        out object? projectedInventory,
        out string reason)
    {
        projectedInventory = null;
        reason = "unknown";
        try
        {
            object? searchController = ResolveSearchController(session);
            if (searchController == null)
            {
                reason = "search_controller_not_found";
                return false;
            }

            if (!TryCreateInventoryDescriptor(playerInventory, searchController, out object? playerDescriptor, out reason)
                || playerDescriptor == null)
            {
                reason = "player_descriptor_" + reason;
                return false;
            }

            if (!TryCreateInventoryDescriptor(operatorInventory, searchController, out object? operatorDescriptor, out reason)
                || operatorDescriptor == null)
            {
                reason = "operator_descriptor_" + reason;
                return false;
            }

            if (!TryInvokeInstance(operatorDescriptor, "ToInventory", Array.Empty<object>(), out object? operatorCloneInventory, out string cloneReason)
                || operatorCloneInventory == null)
            {
                reason = "operator_inventory_clone_failed:" + cloneReason;
                return false;
            }

            object? playerEquipment = ResolveMember(playerInventory, "Equipment");
            object? operatorStash = ResolveMember(operatorCloneInventory, "Stash");
            object? playerQuestRaid = ResolveMember(playerInventory, "QuestRaidItems");
            object? playerQuestStash = ResolveMember(playerInventory, "QuestStashItems");
            object? operatorSorting = ResolveMember(operatorCloneInventory, "SortingTable");
            object? playerHideoutCustomization = ResolveMember(playerInventory, "HideoutCustomizationStash");
            object? playerHideoutAreaStashes = ResolveMember(playerInventory, "HideoutAreaStashes");
            object? playerFastAccess = ResolveMember(playerDescriptor, "FastAccess");
            object? playerDiscardLimits = ResolveMember(playerInventory, "DiscardLimits");
            object? playerFavorites = ResolveMember(playerDescriptor, "FavoriteItemsStorage");
            object? playerErrors = ResolveMember(playerInventory, "DeserializationErrors");
            object? playerCheckHash = ResolveMember(playerInventory, "CheckHash");

            if (playerEquipment == null || operatorStash == null || operatorSorting == null
                || playerHideoutAreaStashes == null || playerFastAccess == null
                || playerDiscardLimits == null || playerFavorites == null || playerErrors == null
                || playerCheckHash is not bool)
            {
                reason = "projected_inventory_constructor_input_missing";
                return false;
            }

            object?[] args =
            {
                playerEquipment,
                operatorStash,
                playerQuestRaid,
                playerQuestStash,
                operatorSorting,
                playerHideoutCustomization,
                playerHideoutAreaStashes,
                playerFastAccess,
                playerDiscardLimits,
                playerFavorites,
                playerErrors,
                playerCheckHash
            };

            ConstructorInfo? constructor = playerInventory.GetType().GetConstructors(InstanceFlags)
                .FirstOrDefault(ctor =>
                {
                    ParameterInfo[] parameters = ctor.GetParameters();
                    if (parameters.Length != args.Length) return false;
                    for (int index = 0; index < parameters.Length; index++)
                    {
                        object? arg = args[index];
                        if (arg == null)
                        {
                            if (parameters[index].ParameterType.IsValueType
                                && Nullable.GetUnderlyingType(parameters[index].ParameterType) == null)
                            {
                                return false;
                            }
                            continue;
                        }

                        if (!parameters[index].ParameterType.IsInstanceOfType(arg))
                        {
                            return false;
                        }
                    }
                    return true;
                });

            if (constructor == null)
            {
                reason = "inventory_constructor_not_found";
                return false;
            }

            projectedInventory = constructor.Invoke(args);
            reason = "ok";
            return projectedInventory != null;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            return false;
        }
    }

    private static bool TryCreateInventoryDescriptor(
        object inventory,
        object searchController,
        out object? descriptor,
        out string reason)
    {
        descriptor = null;
        reason = "unknown";
        Type? descriptorType = ResolveTypeByName("EFTInventoryClass");
        if (descriptorType == null)
        {
            reason = "eft_inventory_descriptor_type_not_found";
            return false;
        }

        ConstructorInfo? constructor = descriptorType.GetConstructors(InstanceFlags)
            .FirstOrDefault(ctor =>
            {
                ParameterInfo[] parameters = ctor.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType.IsAssignableFrom(inventory.GetType())
                    && parameters[1].ParameterType.IsAssignableFrom(searchController.GetType());
            });

        if (constructor == null)
        {
            reason = "eft_inventory_descriptor_constructor_not_found";
            return false;
        }

        descriptor = constructor.Invoke(new[] { inventory, searchController });
        reason = descriptor == null ? "descriptor_null" : "ok";
        return descriptor != null;
    }

    private static bool TryRebindPlayerInventoryRootAddresses(
        object playerController,
        object projectedInventory,
        out string reason)
    {
        reason = "unknown";
        try
        {
            string[] directRootNames =
            {
                "Equipment",
                "QuestStashItems",
                "QuestRaidItems",
                "Stash",
                "SortingTable",
                "HideoutCustomizationStash"
            };

            foreach (string rootName in directRootNames)
            {
                object? root = ResolveMember(projectedInventory, rootName);
                if (root == null)
                {
                    // Optional roots are legitimately absent on some profiles. Equipment,
                    // Stash and SortingTable are already positively required by the projection
                    // gate before this helper is entered.
                    if (string.Equals(rootName, "Equipment", StringComparison.Ordinal)
                        || string.Equals(rootName, "Stash", StringComparison.Ordinal)
                        || string.Equals(rootName, "SortingTable", StringComparison.Ordinal))
                    {
                        reason = "required_root_missing:" + rootName;
                        return false;
                    }

                    continue;
                }

                if (!TryCreateAndAssignRootAddress(playerController, root, rootName, out reason))
                {
                    return false;
                }
            }

            object? hideoutAreaStashes = ResolveMember(projectedInventory, "HideoutAreaStashes");
            if (hideoutAreaStashes is IEnumerable enumerable)
            {
                int index = 0;
                foreach (object? entry in enumerable)
                {
                    object? value = entry == null ? null : ResolveMember(entry, "Value");
                    if (value != null
                        && !TryCreateAndAssignRootAddress(
                            playerController,
                            value,
                            "HideoutAreaStashes[" + index + "]",
                            out reason))
                    {
                        return false;
                    }

                    index++;
                }
            }

            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            return false;
        }
    }

    private static bool TryCreateAndAssignRootAddress(
        object playerController,
        object rootItem,
        string rootName,
        out string reason)
    {
        reason = "unknown";
        if (!TryInvokeInstance(
                playerController,
                "CreateItemAddress",
                Array.Empty<object>(),
                out object? address,
                out string createReason)
            || address == null)
        {
            reason = "create_address_failed:" + rootName + ":" + createReason;
            return false;
        }

        if (!TrySetMember(rootItem, "CurrentAddress", address))
        {
            reason = "assign_address_failed:" + rootName;
            return false;
        }

        object? readback = ResolveMember(rootItem, "CurrentAddress");
        if (!ReferenceEquals(readback, address))
        {
            reason = "address_readback_mismatch:" + rootName;
            return false;
        }

        reason = "ok";
        return true;
    }

    private static void TryRefreshStashBonuses(object playerProfile, object projectedInventory)
    {
        try
        {
            object? stash = ResolveMember(projectedInventory, "Stash");
            object? bonusController = ResolveMember(playerProfile, "BonusController");
            if (stash == null || bonusController == null) return;
            TryInvokeInstance(stash, "UpdateBonuses", new[] { bonusController }, out _, out _);
        }
        catch
        {
        }
    }

    private static bool TryBindPlayerProfileUpdater(
        object playerProfile,
        object session,
        object playerController,
        object? questController,
        out string reason)
    {
        reason = "unknown";
        try
        {
            Type? updaterType = ResolveTypeByName("GClass2331");
            if (updaterType == null)
            {
                reason = "updater_type_not_found";
                return false;
            }

            object?[] args = { playerProfile, session, playerController, questController };
            MethodInfo? bind = updaterType.GetMethods(StaticFlags)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "Bind", StringComparison.Ordinal)) return false;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 4) return false;
                    for (int index = 0; index < parameters.Length; index++)
                    {
                        object? arg = args[index];
                        if (arg == null) continue;
                        if (!parameters[index].ParameterType.IsInstanceOfType(arg)) return false;
                    }
                    return true;
                });

            if (bind == null)
            {
                reason = "bind_method_not_found";
                return false;
            }

            bind.Invoke(null, args);
            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            return false;
        }
    }

    private static bool TryRefreshInsurance(
        object session,
        object playerProfile,
        object projectedInventory,
        out string reason)
    {
        reason = "unknown";
        try
        {
            object? insurance = ResolveMember(session, "InsuranceCompany");
            if (insurance == null)
            {
                reason = "insurance_company_missing";
                return false;
            }

            if (!TryInvokeInstance(insurance, "ClearInsuredItems", Array.Empty<object>(), out _, out string clearReason))
            {
                reason = "clear_failed:" + clearReason;
                return false;
            }

            MethodInfo? getPlayerItems = projectedInventory.GetType().GetMethods(InstanceFlags)
                .FirstOrDefault(method => string.Equals(method.Name, "GetPlayerItems", StringComparison.Ordinal)
                    && method.GetParameters().Length == 1);
            if (getPlayerItems == null)
            {
                reason = "get_player_items_missing";
                return false;
            }

            Type maskType = getPlayerItems.GetParameters()[0].ParameterType;
            object nonQuestItems = Enum.Parse(maskType, "NonQuestItems");
            object? inventoryItems = getPlayerItems.Invoke(projectedInventory, new[] { nonQuestItems });
            object? insuredItems = ResolveMember(playerProfile, "InsuredItems");
            if (insuredItems == null || inventoryItems == null)
            {
                reason = "insurance_inputs_missing";
                return false;
            }

            MethodInfo? register = insurance.GetType().GetMethods(InstanceFlags)
                .FirstOrDefault(method => string.Equals(method.Name, "RegisterInsuredItems", StringComparison.Ordinal)
                    && method.GetParameters().Length == 2);
            if (register == null)
            {
                reason = "register_insured_items_missing";
                return false;
            }

            register.Invoke(insurance, new[] { insuredItems, inventoryItems });
            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            return false;
        }
    }

    private readonly record struct SpatialAudit(bool Known, int Overlaps, int OutOfBounds, int UniqueBadItems, string Reason);

    private static SpatialAudit AuditPlayerSpatialGraph(object inventory)
    {
        try
        {
            List<object> roots = new();
            AddIfNotNull(roots, ResolveMember(inventory, "Equipment"));
            AddIfNotNull(roots, ResolveMember(inventory, "Stash"));
            AddDictionaryValues(roots, ResolveMember(inventory, "HideoutAreaStashes"));

            if (roots.Count == 0)
            {
                return new SpatialAudit(false, 0, 0, 0, "roots_missing");
            }

            int overlaps = 0;
            int outOfBounds = 0;
            HashSet<string> badItems = new(StringComparer.Ordinal);
            foreach (object root in roots)
            {
                foreach (object grid in Enumerate(ResolveMember(root, "Grids")))
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

            object? sortingTable = ResolveMember(inventory, "SortingTable");
            object? sortingGrid = sortingTable == null ? null : ResolveMember(sortingTable, "Grid");
            if (sortingTable == null || sortingGrid == null)
            {
                return new SpatialAudit(false, overlaps, outOfBounds, badItems.Count, "sorting_table_missing");
            }

            return new SpatialAudit(true, overlaps, outOfBounds, badItems.Count, "ok");
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            return new SpatialAudit(false, 0, 0, 0, root.GetType().Name + ":" + root.Message);
        }
    }

    private static object? ResolveSearchController(object session)
    {
        object? fromSession = ResolveMember(session, "SearchController") ?? ResolveMember(session, "SearchControllerClass");
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

    private static bool TryCloseAllScreensForced(out string reason)
    {
        reason = "unknown";
        try
        {
            Type? screenSingletonType = ResolveTypeByName("CurrentScreenSingletonClass");
            if (screenSingletonType == null)
            {
                reason = "current_screen_singleton_type_not_found";
                return false;
            }

            object? instance = screenSingletonType.GetProperty("Instance", StaticFlags)?.GetValue(null)
                ?? screenSingletonType.GetField("Instance", StaticFlags)?.GetValue(null)
                ?? screenSingletonType.GetProperty("instance", StaticFlags)?.GetValue(null)
                ?? screenSingletonType.GetField("instance", StaticFlags)?.GetValue(null);
            if (instance == null)
            {
                reason = "current_screen_singleton_instance_not_found";
                return false;
            }

            if (!TryInvokeInstance(instance, "CloseAllScreensForced", Array.Empty<object>(), out _, out string closeReason))
            {
                reason = "close_all_screens_forced_failed:" + closeReason;
                return false;
            }

            reason = "ok";
            VanguardClientDiagnosticsLog.Info(
                StatusTag,
                "fast_reconciliation_screen_stack_reset source=operator_to_player_authority_transition; nativeCloseAllScreensForced=True; cachedOperatorViewsInvalidated=True; method21Called=False; fallbackAvailable=True");
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            return false;
        }
    }

    private static bool TryRehydrateWeaponBuildsStorage(
        object session,
        object playerController,
        out string reason,
        out int eligiblePresetCount)
    {
        reason = "unknown";
        eligiblePresetCount = 0;
        try
        {
            object? storage = ResolveMember(session, "WeaponBuildsStorage");
            if (storage == null)
            {
                reason = "weapon_builds_storage_missing";
                return false;
            }

            Type? itemFactoryType = ResolveTypeByName("ItemFactoryClass");

            // EFT uses Comfort.Common.Singleton<ItemFactoryClass> here.  Do not resolve a generic
            // Singleton by simple name: Assembly-CSharp also contains RootMotion.Singleton<T>, whose
            // self-referential generic constraint rejects ItemFactoryClass and throws
            // ArgumentException("Invalid generic arguments") at MakeGenericType().  Resolve the exact
            // native singleton type by full name and fail closed if it is unavailable.
            Type? singletonOpenType = ResolveTypeByName("Comfort.Common.Singleton`1");
            if (itemFactoryType == null || singletonOpenType == null || !singletonOpenType.IsGenericTypeDefinition)
            {
                reason = "comfort_common_item_factory_singleton_type_missing";
                return false;
            }

            Type singletonType = singletonOpenType.MakeGenericType(itemFactoryType);
            object? itemFactory = singletonType.GetProperty("Instance", StaticFlags)?.GetValue(null)
                ?? singletonType.GetField("Instance", StaticFlags)?.GetValue(null);
            if (itemFactory == null)
            {
                reason = "item_factory_singleton_instance_missing";
                return false;
            }

            if (!TryInvokeInstance(itemFactory, "SetWeaponBuildsStorage", new[] { storage }, out _, out string setStorageReason))
            {
                reason = "item_factory_weapon_builds_storage_rebind_failed:" + setStorageReason;
                return false;
            }

            object? savedPresetsValue = ResolveMember(itemFactory, "SavedPresets");
            List<object> savedPresets = Enumerate(savedPresetsValue).ToList();

            MethodInfo? addItemPresets = storage.GetType().GetMethods(InstanceFlags)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "AddItemPresets", StringComparison.Ordinal)) return false;
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsArray;
                });
            if (addItemPresets == null)
            {
                reason = "add_item_presets_method_missing";
                return false;
            }

            Type? presetElementType = addItemPresets.GetParameters()[0].ParameterType.GetElementType();
            if (presetElementType == null)
            {
                reason = "item_preset_element_type_missing";
                return false;
            }

            var eligible = new List<object>();
            foreach (object preset in savedPresets)
            {
                object? saveAsPresetValue = ResolveMember(preset, "SaveAsPreset");
                if (saveAsPresetValue is not bool saveAsPreset || !saveAsPreset)
                {
                    continue;
                }

                object? item = ResolveMember(preset, "Item");
                if (item == null)
                {
                    continue;
                }

                if (!TryInvokeInstance(playerController, "Examined", new[] { item }, out object? examinedValue, out _)
                    || examinedValue is not bool examined
                    || !examined)
                {
                    continue;
                }

                if (presetElementType.IsInstanceOfType(preset))
                {
                    eligible.Add(preset);
                }
            }

            Array argument = Array.CreateInstance(presetElementType, eligible.Count);
            for (int index = 0; index < eligible.Count; index++)
            {
                argument.SetValue(eligible[index], index);
            }

            addItemPresets.Invoke(storage, new object[] { argument });
            eligiblePresetCount = eligible.Count;
            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            return false;
        }
    }

    private static bool TryInvokeTask(object target, string methodName, out Task? task, out string reason)
    {
        task = null;
        if (!TryInvokeInstance(target, methodName, Array.Empty<object>(), out object? result, out reason))
        {
            return false;
        }

        task = result as Task;
        if (task == null)
        {
            reason = "return_value_not_task";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool TryInvokeInstance(
        object target,
        string methodName,
        object?[] args,
        out object? result,
        out string reason)
    {
        result = null;
        reason = "unknown";
        try
        {
            MethodInfo? method = target.GetType().GetMethods(InstanceFlags)
                .FirstOrDefault(candidate =>
                {
                    if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)) return false;
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (parameters.Length != args.Length) return false;
                    for (int index = 0; index < parameters.Length; index++)
                    {
                        object? arg = args[index];
                        if (arg == null) continue;
                        if (!parameters[index].ParameterType.IsInstanceOfType(arg)) return false;
                    }
                    return true;
                });
            if (method == null)
            {
                reason = "method_not_found";
                return false;
            }

            result = method.Invoke(target, args);
            reason = "ok";
            return true;
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            reason = root.GetType().Name + ":" + root.Message;
            return false;
        }
    }

    private static bool TrySetMember(object target, string name, object value)
    {
        try
        {
            PropertyInfo? property = target.GetType().GetProperty(name, InstanceFlags);
            if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(target, value);
                return true;
            }

            FieldInfo? field = target.GetType().GetField(name, InstanceFlags);
            if (field != null)
            {
                field.SetValue(target, value);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static object? ResolveMember(object target, string name)
    {
        try
        {
            PropertyInfo? property = target.GetType().GetProperty(name, InstanceFlags);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target);
            return target.GetType().GetField(name, InstanceFlags)?.GetValue(target);
        }
        catch
        {
            return null;
        }
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

    private static IEnumerable<object> Enumerate(object? value)
    {
        if (value is not IEnumerable enumerable) yield break;
        foreach (object? item in enumerable)
        {
            if (item != null) yield return item;
        }
    }

    private static void AddIfNotNull(List<object> list, object? value)
    {
        if (value != null) list.Add(value);
    }

    private static void AddDictionaryValues(List<object> list, object? dictionary)
    {
        foreach (object entry in Enumerate(dictionary))
        {
            object? value = ResolveMember(entry, "Value");
            if (value != null) list.Add(value);
        }
    }

    private static string ResolveItemIdentity(object item)
    {
        return Normalize((ResolveMember(item, "Id") ?? ResolveMember(item, "_id"))?.ToString());
    }

    private static ExecutionResult Fail(string source, string reason)
    {
        VanguardClientDiagnosticsLog.Warning(
            StatusTag,
            $"fast_reconciliation_failed source={Normalize(source)}; reason={reason}; method21Called=False; fallbackRequired=True; runtimeBehaviorChanged=False");
        return ExecutionResult.Fail(reason);
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException
            : exception;
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');
    }
}

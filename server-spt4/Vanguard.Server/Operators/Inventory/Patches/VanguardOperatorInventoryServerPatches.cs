using System.Reflection;
using HarmonyLib;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using Vanguard.Server.Operators.Inventory.Services;

using Vanguard.Server.Diagnostics;

// Responsibility: Bridges EFT/SPT/Fika callbacks into Operator Inventory Server Patches for the server Operator inventory mode.
// Flow: A narrowly-scoped patch observes/intercepts the host callback, translates the event into Vanguard state/service input, then returns control to the original lifecycle.
// Authority boundary: Patch code is an integration boundary, not a parallel gameplay authority; original behavior is preserved unless the documented Vanguard contract explicitly supersedes it.
// Invariant: Hooks remain fail-safe, bounded to the intended process/lifecycle, and avoid unrelated side effects.
namespace Vanguard.Server.Operators.Inventory.Patches;

public static class VanguardOperatorInventoryServerPatches
{
    private const string HarmonyId = "com.hiban.vanguard.operator-inventory-profile-mode";
    private static readonly Harmony Harmony = new(HarmonyId);
    private static bool enabled;
    private static VanguardOperatorInventoryModeService? inventoryModeService;

    public static void Enable<TLogger>(ISptLogger<TLogger> logger, VanguardOperatorInventoryModeService service)
    {
        if (enabled)
        {
            return;
        }

        inventoryModeService = service ?? throw new ArgumentNullException(nameof(service));

        MethodInfo? getProfile = AccessTools.Method(typeof(SaveServer), nameof(SaveServer.GetProfile));
        MethodInfo? saveProfile = AccessTools.Method(typeof(SaveServer), nameof(SaveServer.SaveProfileAsync));
        MethodInfo? handleEvents = AccessTools.Method(typeof(ItemEventRouter), nameof(ItemEventRouter.HandleEvents));

        if (getProfile == null || saveProfile == null || handleEvents == null)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present("[VANGUARD_OPERATOR_INVENTORY_PROFILE_REDIRECT_STATUS] patch registration incomplete; SaveServer or ItemEventRouter method not found."));
            return;
        }

        Harmony.Patch(getProfile, prefix: new HarmonyMethod(typeof(VanguardOperatorInventoryServerPatches), nameof(GetProfilePrefix)));
        Harmony.Patch(saveProfile, prefix: new HarmonyMethod(typeof(VanguardOperatorInventoryServerPatches), nameof(SaveProfileAsyncPrefix)));
        Harmony.Patch(handleEvents, postfix: new HarmonyMethod(typeof(VanguardOperatorInventoryServerPatches), nameof(ItemEventRouterPostfix)));

        // Native purchases are player-economy operations even while the inventory UI
        // is presenting an Operator. Patch only the common SPT buy boundary so trader
        // and Flea purchases debit the real player profile without changing sales or
        // normal Operator item events.
        PatchPlayerPurchaseMethod(AccessTools.Method(typeof(TradeHelper), nameof(TradeHelper.BuyItem)), logger);

        PatchPlayerUserBuildMethod(AccessTools.Method(typeof(BuildController), nameof(BuildController.GetUserBuilds)), logger);
        PatchPlayerUserBuildMethod(AccessTools.Method(typeof(BuildController), nameof(BuildController.SaveEquipmentBuild)), logger);
        PatchPlayerUserBuildMethod(AccessTools.Method(typeof(BuildController), nameof(BuildController.SaveWeaponBuild)), logger);
        PatchPlayerUserBuildMethod(AccessTools.Method(typeof(BuildController), nameof(BuildController.CreateMagazineTemplate)), logger);
        PatchPlayerUserBuildMethod(AccessTools.Method(typeof(BuildController), nameof(BuildController.RemoveBuild)), logger);

        enabled = true;
    }


    private static bool PatchPlayerPurchaseMethod<TLogger>(MethodInfo? method, ISptLogger<TLogger> logger)
    {
        if (method == null)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present("[VANGUARD_OPERATOR_NATIVE_PURCHASE_AUTHORITY_STATUS] native purchase authority patch target missing; compatibility fallback leaves TradeHelper.BuyItem native."));
            return false;
        }

        Harmony.Patch(
            method,
            prefix: new HarmonyMethod(typeof(VanguardOperatorInventoryServerPatches), nameof(PlayerPurchaseAccessPrefix)),
            finalizer: new HarmonyMethod(typeof(VanguardOperatorInventoryServerPatches), nameof(PlayerPurchaseAccessFinalizer)));
        return true;
    }

    private static void PlayerPurchaseAccessPrefix(
        ref PmcData __0,
        MongoId __2,
        MethodBase __originalMethod,
        out PlayerPurchaseAccessState? __state)
    {
        __state = null;
        VanguardOperatorInventoryModeService? service = ResolveService();
        if (service == null || !service.IsActive(__2))
        {
            return;
        }

        IDisposable? scope = service.BeginPlayerPurchaseProfileAccess(
            __2,
            __originalMethod.Name,
            out PmcData? playerPmcData,
            out string? operatorId);
        if (scope == null || playerPmcData == null || string.IsNullOrWhiteSpace(operatorId))
        {
            return;
        }

        // TradeHelper must receive the real player PMC. The active composite remains
        // the UI projection only; the native item-event delta is mirrored back after
        // SPT finishes the purchase.
        __0 = playerPmcData;
        __state = new PlayerPurchaseAccessState(scope, __2, operatorId, __originalMethod.Name);
    }

    private static Exception? PlayerPurchaseAccessFinalizer(
        Exception? __exception,
        ItemEventRouterResponse __4,
        PlayerPurchaseAccessState? __state)
    {
        if (__state == null)
        {
            return __exception;
        }

        try
        {
            ResolveService()?.CompletePlayerPurchaseProfileAccess(
                __state.RequestedProfileId,
                __state.OperatorId,
                __state.Operation,
                __4,
                __exception);
        }
        finally
        {
            __state.Scope.Dispose();
        }

        return __exception;
    }


    private static bool PatchPlayerUserBuildMethod<TLogger>(MethodInfo? method, ISptLogger<TLogger> logger)
    {
        if (method == null)
        {
            logger.Warning(VanguardServerDiagnosticsLog.Present("[VANGUARD_OPERATOR_USER_BUILDS_STATUS] player user-build authority patch target missing; compatibility fallback leaves this BuildController method native."));
            return false;
        }

        Harmony.Patch(
            method,
            prefix: new HarmonyMethod(typeof(VanguardOperatorInventoryServerPatches), nameof(PlayerUserBuildAccessPrefix)),
            finalizer: new HarmonyMethod(typeof(VanguardOperatorInventoryServerPatches), nameof(PlayerUserBuildAccessFinalizer)));
        return true;
    }

    private static void PlayerUserBuildAccessPrefix(MongoId __0, MethodBase __originalMethod, out IDisposable? __state)
    {
        __state = null;
        VanguardOperatorInventoryModeService? service = ResolveService();
        if (service == null || !service.IsActive(__0))
        {
            return;
        }

        __state = service.BeginPlayerUserBuildProfileAccess(__0, __originalMethod.Name);
    }

    private static Exception? PlayerUserBuildAccessFinalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }

    private static bool GetProfilePrefix(MongoId sessionId, ref SptProfile __result)
    {
        VanguardOperatorInventoryModeService? service = ResolveService();
        if (service == null || service.IsRedirectBypassed || !service.TryGetActiveInventoryProfile(sessionId, out SptProfile? profile) || profile == null)
        {
            return true;
        }

        __result = profile;
        return false;
    }

    private static bool SaveProfileAsyncPrefix(MongoId sessionID, ref Task<long> __result)
    {
        VanguardOperatorInventoryModeService? service = ResolveService();
        if (service == null || service.IsRedirectBypassed || !service.IsActive(sessionID))
        {
            return true;
        }

        __result = service.SaveActiveInventoryProfileAsync(sessionID);
        return false;
    }

    private static void ItemEventRouterPostfix(MongoId sessionID, ref ValueTask<ItemEventRouterResponse> __result)
    {
        VanguardOperatorInventoryModeService? service = ResolveService();
        if (service == null || service.IsRedirectBypassed || !service.IsActive(sessionID))
        {
            return;
        }

        if (service.TryGetActiveInventoryProfileId(sessionID, out MongoId inventoryProfileId))
        {
            __result = ReplaceProfileChangesKeyAsync(__result, sessionID, inventoryProfileId);
        }
    }

    private static async ValueTask<ItemEventRouterResponse> ReplaceProfileChangesKeyAsync(ValueTask<ItemEventRouterResponse> originalTask, MongoId originalKey, MongoId newKey)
    {
        ItemEventRouterResponse response = await originalTask;
        if (response.ProfileChanges != null && response.ProfileChanges.TryGetValue(originalKey, out var profileChange))
        {
            response.ProfileChanges.Remove(originalKey);
            profileChange.Id = newKey;
            response.ProfileChanges[newKey] = profileChange;
        }

        return response;
    }

    private sealed record PlayerPurchaseAccessState(
        IDisposable Scope,
        MongoId RequestedProfileId,
        string OperatorId,
        string Operation);

    private static VanguardOperatorInventoryModeService? ResolveService() => inventoryModeService;
}

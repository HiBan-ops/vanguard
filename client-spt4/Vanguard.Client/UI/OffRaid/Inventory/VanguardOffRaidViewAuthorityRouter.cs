using System;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
#endif

// Responsibility: resolves the presentation authority for native off-raid inventory-bound views.
// Flow: each semantic view boundary asks for the currently consulted Player/Operator context, then binds only the view-facing controller/health/equipment/ItemUiContext while preserving native player session/economy authority.
// Authority boundary: this router does not own persistence, commerce or navigation; it only normalizes presentation dependencies at native view-construction/show boundaries.
// Invariant: Operator views use one coherent Operator controller/health/equipment set; Player views use one coherent retained MainMenu player set; unresolved/ambiguous contexts are never guessed.
namespace Vanguard.Client.UI.OffRaid.Inventory;

#if SPT_CLIENT
internal enum VanguardOffRaidViewAuthority
{
    Player,
    Operator
}

internal readonly struct VanguardOffRaidViewContext
{
    public VanguardOffRaidViewContext(
        VanguardOffRaidViewAuthority authority,
        ISession session,
        object profile,
        GClass3387 inventoryController,
        IHealthController healthController,
        InventoryEquipment equipment,
        string? operatorId,
        string? profileId,
        int generation)
    {
        Authority = authority;
        Session = session;
        Profile = profile;
        InventoryController = inventoryController;
        HealthController = healthController;
        Equipment = equipment;
        OperatorId = operatorId;
        ProfileId = profileId;
        Generation = generation;
    }

    public VanguardOffRaidViewAuthority Authority { get; }
    public ISession Session { get; }
    public object Profile { get; }
    public GClass3387 InventoryController { get; }
    public IHealthController HealthController { get; }
    public InventoryEquipment Equipment { get; }
    public string? OperatorId { get; }
    public string? ProfileId { get; }
    public int Generation { get; }
    public bool IsOperator => Authority == VanguardOffRaidViewAuthority.Operator;
    public string AuthorityName => IsOperator ? "operator" : "player";
}

internal static class VanguardOffRaidViewAuthorityRouter
{
    public const string StatusTag = "VANGUARD_OFFRAID_VIEW_AUTHORITY_STATUS";

    public static bool TryResolve(string view, string source, out VanguardOffRaidViewContext context, out string reason)
    {
        context = default;
        reason = "unknown";

        if (VanguardOperatorInventoryModeClientState.IsActive)
        {
            if (!VanguardOperatorDirectEquipmentScreenEntry.TryGetActiveOperatorRuntimeContext(
                    out _,
                    out object? sessionObject,
                    out object? operatorProfile,
                    out object? inventoryControllerObject,
                    out object? healthControllerObject,
                    out string? operatorProfileId,
                    out string? operatorId,
                    out int generation,
                    out bool completionStarted))
            {
                reason = "operator_session_active_but_runtime_context_unavailable";
                LogResolution(view, source, false, "operator", reason, null);
                return false;
            }

            if (completionStarted)
            {
                reason = "operator_session_completion_in_progress";
                LogResolution(view, source, false, "operator", reason, null);
                return false;
            }

            object? inventoryObject = inventoryControllerObject == null
                ? null
                : VanguardOperatorInventoryProfileLoader.ResolveMember(inventoryControllerObject, "Inventory");
            inventoryObject ??= operatorProfile == null
                ? null
                : VanguardOperatorInventoryProfileLoader.ResolveMember(operatorProfile, "Inventory");
            object? equipmentObject = inventoryObject == null
                ? null
                : VanguardOperatorInventoryProfileLoader.ResolveMember(inventoryObject, "Equipment");

            if (sessionObject is not ISession session
                || operatorProfile == null
                || inventoryControllerObject is not GClass3387 inventoryController
                || healthControllerObject is not IHealthController healthController
                || equipmentObject is not InventoryEquipment equipment)
            {
                reason = "operator_view_context_type_or_member_mismatch";
                LogResolution(view, source, false, "operator", reason, null);
                return false;
            }

            context = new VanguardOffRaidViewContext(
                VanguardOffRaidViewAuthority.Operator,
                session,
                operatorProfile,
                inventoryController,
                healthController,
                equipment,
                operatorId,
                operatorProfileId,
                generation);
            reason = "operator_context_resolved";
            LogResolution(view, source, true, "operator", reason, context);
            return true;
        }

        if (!VanguardOperatorDirectEquipmentScreenEntry.TryGetCurrentPlayerRuntimeContext(
                out _,
                out object? playerSessionObject,
                out object? playerProfile,
                out object? playerInventoryControllerObject,
                out object? playerHealthControllerObject,
                out object? playerEquipmentObject,
                out string playerReason)
            || playerSessionObject is not ISession playerSession
            || playerProfile == null
            || playerInventoryControllerObject is not GClass3387 playerInventoryController
            || playerHealthControllerObject is not IHealthController playerHealthController
            || playerEquipmentObject is not InventoryEquipment playerEquipment)
        {
            reason = "player_view_context_unavailable:" + playerReason;
            LogResolution(view, source, false, "player", reason, null);
            return false;
        }

        string? playerProfileId = VanguardOperatorInventoryProfileLoader.ResolveMember(playerProfile, "Id")?.ToString();
        context = new VanguardOffRaidViewContext(
            VanguardOffRaidViewAuthority.Player,
            playerSession,
            playerProfile,
            playerInventoryController,
            playerHealthController,
            playerEquipment,
            null,
            playerProfileId,
            0);
        reason = "player_context_resolved";
        LogResolution(view, source, true, "player", reason, context);
        return true;
    }

    public static bool TryReassertItemUiContext(in VanguardOffRaidViewContext context, string source, out string reason)
    {
        if (context.IsOperator)
        {
            return VanguardOperatorDirectEquipmentScreenEntry.TryReassertActiveOperatorItemUiContext(source, out reason);
        }

        return VanguardOperatorDirectEquipmentScreenEntry.TryReassertCurrentPlayerItemUiContext(source, out reason);
    }

    public static void RecordEffectiveBinding(
        string view,
        string source,
        in VanguardOffRaidViewContext expected,
        object? suppliedController,
        object? suppliedHealth,
        object? suppliedEquipment,
        bool itemUiContextReasserted,
        string itemUiContextReason,
        string detail)
    {
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"view_binding_effective view={view}; source={source}; authority={expected.AuthorityName}; profile={expected.ProfileId ?? "<none>"}; operator={expected.OperatorId ?? "<none>"}; generation={expected.Generation}; controllerMatches={ReferenceEquals(suppliedController, expected.InventoryController)}; healthMatches={ReferenceEquals(suppliedHealth, expected.HealthController)}; equipmentMatches={ReferenceEquals(suppliedEquipment, expected.Equipment)}; itemUiContextReasserted={itemUiContextReasserted}; itemUiContextReason={itemUiContextReason}; detail={detail}");
    }

    private static void LogResolution(string view, string source, bool success, string authority, string reason, VanguardOffRaidViewContext? context)
    {
        VanguardClientDiagnosticsLog.Info(
            StatusTag,
            $"view_context_resolved view={view}; source={source}; success={success}; authority={authority}; reason={reason}; profile={context?.ProfileId ?? "<none>"}; operator={context?.OperatorId ?? "<none>"}; generation={context?.Generation ?? 0}; operatorSessionActive={VanguardOperatorInventoryModeClientState.IsActive}");
    }
}
#else
internal static class VanguardOffRaidViewAuthorityRouter
{
}
#endif

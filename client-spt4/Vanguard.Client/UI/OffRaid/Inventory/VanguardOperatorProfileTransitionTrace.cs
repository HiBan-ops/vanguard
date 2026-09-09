using System;
using System.Threading;
using Vanguard.Client.Diagnostics;

// Responsibility: records semantic profile/controller transitions while an Operator off-raid inventory session is active.
// Flow: navigation boundaries and nested native screens emit one ordered trace carrying the expected authority plus the actually supplied player/Operator objects.
// Authority boundary: trace/support diagnostics only; this tracker never changes navigation, profiles, controllers, inventory or persistence.
// Invariant: profile-affecting paths are traced by semantic transition points rather than global raw Unity click interception, avoiding unrelated UI noise and mod conflicts.
namespace Vanguard.Client.UI.OffRaid.Inventory;

internal static class VanguardOperatorProfileTransitionTrace
{
    private static long sequence;
    private static readonly object Gate = new();
    private static string currentRoute = "<none>";
    private static int tracedGeneration;

    public static void BeginSession(string? operatorId, string? operatorProfileId, int generation, string source)
    {
        lock (Gate)
        {
            tracedGeneration = generation;
            currentRoute = "Player";
        }

        Record(
            phase: "session_begin",
            route: "Player->OperatorInventory",
            source: source,
            expectedAuthority: "operator",
            detail: $"operator={operatorId ?? "<none>"}; operatorProfile={operatorProfileId ?? "<none>"}; generation={generation}");
    }

    public static void RecordNavigationDecision(string? route, bool turnOn, string disposition, string reason, string source)
    {
        string normalizedRoute = Normalize(route);
        string previous;
        lock (Gate)
        {
            previous = currentRoute;
            if (turnOn)
            {
                currentRoute = normalizedRoute;
            }
        }

        string expectedAuthority = normalizedRoute.Equals("MainMenu", StringComparison.OrdinalIgnoreCase)
            ? "player"
            : normalizedRoute.Equals("Hideout", StringComparison.OrdinalIgnoreCase)
                ? "player_boundary"
                : "operator_or_preserved_player_session";

        Record(
            phase: turnOn ? "route_request_on" : "route_request_off",
            route: previous + "->" + normalizedRoute,
            source: source,
            expectedAuthority: expectedAuthority,
            detail: $"disposition={disposition}; reason={reason}; turnOn={turnOn}");
    }

    public static void RecordNestedBoundary(string route, string source, string expectedAuthority, string detail)
    {
        string previous;
        lock (Gate)
        {
            previous = currentRoute;
            currentRoute = Normalize(route);
        }

        Record(
            phase: "nested_boundary",
            route: previous + "->" + Normalize(route),
            source: source,
            expectedAuthority: expectedAuthority,
            detail: detail);
    }

    public static void RecordReturnToOperatorInventory(string source, string priorRoute)
    {
        lock (Gate)
        {
            currentRoute = "Player";
        }

        Record(
            phase: "return_to_operator_inventory",
            route: Normalize(priorRoute) + "->OperatorInventory",
            source: source,
            expectedAuthority: "operator",
            detail: "session_preserved=true");
    }

    public static void CompleteSession(string source, bool fastPath, bool success, string reason)
    {
        Record(
            phase: "session_complete",
            route: "OperatorSession->MainMenu",
            source: source,
            expectedAuthority: "player",
            detail: $"fastPath={fastPath}; success={success}; reason={reason}");

        lock (Gate)
        {
            currentRoute = "MainMenu";
            tracedGeneration = 0;
        }
    }

    public static void Record(
        string phase,
        string route,
        string source,
        string expectedAuthority,
        object? suppliedInventoryController = null,
        object? suppliedHealthController = null,
        object? suppliedEquipment = null,
        string? detail = null)
    {
        long seq = Interlocked.Increment(ref sequence);

        bool hasContext = VanguardOperatorDirectEquipmentScreenEntry.TryGetActiveOperatorRuntimeContext(
            out object? mainMenuController,
            out object? session,
            out object? operatorProfile,
            out object? operatorInventoryController,
            out object? operatorHealthController,
            out string? operatorProfileId,
            out string? operatorId,
            out int generation,
            out bool completionStarted);

        object? playerProfile = session == null ? null : VanguardOperatorInventoryProfileLoader.ResolveMember(session, "Profile");
        object? playerProfileId = playerProfile == null ? null : VanguardOperatorInventoryProfileLoader.ResolveMember(playerProfile, "Id");
        object? playerController = mainMenuController == null ? null : VanguardOperatorInventoryProfileLoader.ResolveMember(mainMenuController, "InventoryController");
        string playerControllerProfile = ResolveControllerProfileId(playerController);
        string operatorControllerProfile = ResolveControllerProfileId(operatorInventoryController);
        string suppliedControllerProfile = ResolveControllerProfileId(suppliedInventoryController);
        object? operatorEquipment = ResolveEquipment(operatorInventoryController, operatorProfile);

        bool suppliedControllerMatchesOperator = suppliedInventoryController != null
            && operatorInventoryController != null
            && ReferenceEquals(suppliedInventoryController, operatorInventoryController);
        bool suppliedHealthMatchesOperator = suppliedHealthController != null
            && operatorHealthController != null
            && ReferenceEquals(suppliedHealthController, operatorHealthController);
        bool suppliedEquipmentMatchesOperator = suppliedEquipment != null
            && operatorEquipment != null
            && ReferenceEquals(suppliedEquipment, operatorEquipment);

        VanguardClientDiagnosticsLog.Trace(
            "VANGUARD_OPERATOR_PROFILE_TRANSITION_TRACE_STATUS",
            () => $"seq={seq}; phase={phase}; route={route}; source={source}; expectedAuthority={expectedAuthority}; contextAvailable={hasContext}; operator={operatorId ?? "<none>"}; operatorProfile={operatorProfileId ?? "<none>"}; generation={(hasContext ? generation : tracedGeneration)}; completionStarted={completionStarted}; sessionProfile={playerProfileId ?? "<none>"}; playerControllerProfile={playerControllerProfile}; operatorControllerProfile={operatorControllerProfile}; suppliedControllerProfile={suppliedControllerProfile}; suppliedControllerMatchesOperator={suppliedControllerMatchesOperator}; suppliedHealthMatchesOperator={suppliedHealthMatchesOperator}; suppliedEquipmentMatchesOperator={suppliedEquipmentMatchesOperator}; suppliedEquipment={DescribeItem(suppliedEquipment)}; operatorEquipment={DescribeItem(operatorEquipment)}; detail={detail ?? "<none>"}");
    }

    private static object? ResolveEquipment(object? inventoryController, object? profile)
    {
        object? inventory = inventoryController == null ? null : VanguardOperatorInventoryProfileLoader.ResolveMember(inventoryController, "Inventory");
        inventory ??= profile == null ? null : VanguardOperatorInventoryProfileLoader.ResolveMember(profile, "Inventory");
        return inventory == null ? null : VanguardOperatorInventoryProfileLoader.ResolveMember(inventory, "Equipment");
    }

    private static string ResolveControllerProfileId(object? controller)
    {
        if (controller == null)
        {
            return "<none>";
        }

        object? profile = VanguardOperatorInventoryProfileLoader.ResolveMember(controller, "Profile");
        object? id = profile == null ? null : VanguardOperatorInventoryProfileLoader.ResolveMember(profile, "Id");
        return id?.ToString() ?? "<unresolved>";
    }

    private static string DescribeItem(object? item)
    {
        if (item == null)
        {
            return "<none>";
        }

        object? id = VanguardOperatorInventoryProfileLoader.ResolveMember(item, "Id");
        return (id?.ToString() ?? "<no-id>") + "@" + (item.GetType().FullName ?? item.GetType().Name);
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<unknown>" : value.Trim();
    }
}

// Responsibility: Provides I Request Dispatcher support for the client API transport.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Api;

internal interface IRequestDispatcher
{
    string GetJson(string route);

    string PostJson(string route, string body);
}

internal sealed class NoopRequestDispatcher : IRequestDispatcher
{
    public string GetJson(string route)
    {
        return "{}";
    }

    public string PostJson(string route, string body)
    {
        return "{}";
    }
}

#if SPT_CLIENT
internal sealed class RequestHandlerDispatcher : IRequestDispatcher
{
    public string GetJson(string route)
    {
        return SPT.Common.Http.RequestHandler.GetJson(route);
    }

    public string PostJson(string route, string body)
    {
        return SPT.Common.Http.RequestHandler.PostJson(route, body);
    }
}
#endif

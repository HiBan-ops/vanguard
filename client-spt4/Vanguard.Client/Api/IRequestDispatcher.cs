// Responsibility: Define GET/POST JSON transport adapters used by the Vanguard client API.
// Flow: Callers use IRequestDispatcher; NoopRequestDispatcher returns empty JSON and the SPT_CLIENT adapter forwards to SPT RequestHandler.
// Authority boundary: SPT RequestHandler owns network requests; the no-op adapter performs no transport operation.
// Invariant: Both adapters implement the same GetJson and PostJson signatures without changing route or body inputs.
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

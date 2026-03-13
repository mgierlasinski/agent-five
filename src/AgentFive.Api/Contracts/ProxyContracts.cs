namespace AgentFive.Api.Contracts;

public record ProxyRequest(string SessionID, string Msg);

public record ProxyResponse(string Msg);

public static class ProxyRequestValidator
{
    public static string? Validate(ProxyRequest? request)
    {
        if (request is null)
        {
            return "Request body is required.";
        }

        if (string.IsNullOrWhiteSpace(request.SessionID))
        {
            return "sessionID is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Msg))
        {
            return "msg is required.";
        }

        return null;
    }
}
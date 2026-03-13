using System.Text.Json.Serialization;

namespace AgentFive.Api.Contracts;

public record ProxyRequest(
    [property: JsonPropertyName("sessionID")] string SessionID, 
    [property: JsonPropertyName("msg")] string Msg);

public record ProxyResponse([property: JsonPropertyName("msg")] string Msg);

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
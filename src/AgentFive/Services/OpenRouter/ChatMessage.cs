using System.Text.Json.Serialization;

namespace AgentFive.Services.OpenRouter;

public record ChatMessage(string Role, string Content);

public record ResponseFormat(
    [property: JsonPropertyName("type")] string Type, 
    [property: JsonPropertyName("json_schema")] object JsonSchema);

public record ChatPayload(
    [property: JsonPropertyName("model")] string Model, 
    [property: JsonPropertyName("messages")] ChatMessage[] Messages, 
    [property: JsonPropertyName("temperature")] double Temperature, 
    [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat = null);

public record OpenRouterResponse(List<Choice>? choices);
public record Choice(Message? message, string? content);
public record Message(string? role, string? content);

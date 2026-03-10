using System.Text.Json.Serialization;

namespace AgentFive.Services.OpenRouter;

public record ChatPayload(
    [property: JsonPropertyName("model")] string Model, 
    [property: JsonPropertyName("messages")] ChatMessage[] Messages, 
    [property: JsonPropertyName("temperature")] double Temperature, 
    [property: JsonPropertyName("response_format")] JsonResponseFormat? ResponseFormat = null);

public record ChatMessage(
    [property: JsonPropertyName("role")] string Role, 
    [property: JsonPropertyName("content")] string Content);

public record JsonResponseFormat(
    [property: JsonPropertyName("type")] string Type, 
    [property: JsonPropertyName("json_schema")] JsonSchemaObject JsonSchema);

public record JsonSchemaObject(
    [property: JsonPropertyName("name")] string Name, 
    [property: JsonPropertyName("strict")] bool Strict, 
    [property: JsonPropertyName("schema")] object Schema);

public record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);

public record Choice(
    [property: JsonPropertyName("message")] Message? Message,
    [property: JsonPropertyName("content")] string? Content);

public record Message(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);

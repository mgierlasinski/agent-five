using System.Text.Json.Serialization;

namespace AgentFive.Services.OpenRouter;

public record ChatPayload(
    [property: JsonPropertyName("model")] string Model, 
    [property: JsonPropertyName("messages")] ChatMessage[] Messages, 
    [property: JsonPropertyName("temperature")] double Temperature, 
    [property: JsonPropertyName("response_format")] JsonResponseFormat? ResponseFormat = null,
    [property: JsonPropertyName("tools")] ChatToolDefinition[]? Tools = null,
    [property: JsonPropertyName("tool_choice")] string? ToolChoice = null);

public record ChatMessage(
    [property: JsonPropertyName("role")] string Role, 
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] List<ChatToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

public record JsonResponseFormat(
    [property: JsonPropertyName("type")] string Type, 
    [property: JsonPropertyName("json_schema")] JsonSchemaObject JsonSchema);

public record JsonSchemaObject(
    [property: JsonPropertyName("name")] string Name, 
    [property: JsonPropertyName("strict")] bool Strict, 
    [property: JsonPropertyName("schema")] object Schema);

public record ChatToolDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ChatFunctionDefinition Function);

public record ChatFunctionDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] object Parameters);

public record ChatToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ChatToolFunction Function);

public record ChatToolFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

public record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);

public record Choice(
    [property: JsonPropertyName("message")] Message? Message,
    [property: JsonPropertyName("content")] string? Content);

public record Message(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] List<ChatToolCall>? ToolCalls);

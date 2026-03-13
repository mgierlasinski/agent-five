using System.Text.Json.Serialization;
using AgentFive.Services.OpenRouter;

namespace AgentFive.Api.Services;

public record SessionTranscript(
    [property: JsonPropertyName("messages")] List<SessionMessage> Messages);

public record SessionMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] List<ChatToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null)
{
    public ChatMessage ToChatMessage() => new(Role, Content, ToolCalls, ToolCallId);

    public static SessionMessage FromChatMessage(ChatMessage message) =>
        new(message.Role, message.Content, message.ToolCalls, message.ToolCallId);
}
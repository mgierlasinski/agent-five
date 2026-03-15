using AgentFive.Services.OpenRouter;

namespace AgentFive.Tasks.Railway.Tools;

public static class RailwayToolProvider
{
    public static IReadOnlyCollection<ChatToolDefinition> CreateTools(RailwayHelpDocument helpDocument)
    {
        ArgumentNullException.ThrowIfNull(helpDocument);

        var actionNames = helpDocument.Actions
            .Select(action => action.Action)
            .Where(action => !string.Equals(action, "help", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var setStatusValues = helpDocument.Actions
            .FirstOrDefault(action => string.Equals(action.Action, "setstatus", StringComparison.OrdinalIgnoreCase))?
            .AllowedValues
            .ToArray() ?? Array.Empty<string>();

        var executeActionSchema = new
        {
            oneOf = new object[]
            {
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { @const = "getstatus" },
                        route = new { type = "string", pattern = "^[A-Za-z]-[0-9]{1,2}$" }
                    },
                    required = new[] { "action", "route" },
                    additionalProperties = false
                },
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { @const = "reconfigure" },
                        route = new { type = "string", pattern = "^[A-Za-z]-[0-9]{1,2}$" }
                    },
                    required = new[] { "action", "route" },
                    additionalProperties = false
                },
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { @const = "save" },
                        route = new { type = "string", pattern = "^[A-Za-z]-[0-9]{1,2}$" }
                    },
                    required = new[] { "action", "route" },
                    additionalProperties = false
                },
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { @const = "setstatus" },
                        route = new { type = "string", pattern = "^[A-Za-z]-[0-9]{1,2}$" },
                        value = new { type = "string", @enum = setStatusValues }
                    },
                    required = new[] { "action", "route", "value" },
                    additionalProperties = false
                }
            }
        };

        return new List<ChatToolDefinition>
        {
            new(
                "function",
                new ChatFunctionDefinition(
                    "get_cached_help",
                    "Returns the first successful railway help response cached for the current run. Use this instead of calling help again.",
                    new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    })),
            new(
                "function",
                new ChatFunctionDefinition(
                    "execute_documented_action",
                    $"Executes one documented railway API action through the hub client. Use one of: {string.Join(", ", actionNames)}. Pass route and value as separate JSON fields, never inside the action string.",
                    executeActionSchema)),
            new(
                "function",
                new ChatFunctionDefinition(
                    "get_execution_history",
                    "Returns the execution history summary for all railway hub calls and wait events already recorded in this run.",
                    new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    })),
            new(
                "function",
                new ChatFunctionDefinition(
                    "finish_with_result",
                    "Registers the final railway result after a flag has been found. Call this once before returning the final structured JSON.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            finalFlag = new { type = "string" },
                            summary = new { type = "string" },
                            lastAction = new { type = "string" }
                        },
                        required = new[] { "finalFlag", "summary", "lastAction" },
                        additionalProperties = false
                    }))
        };
    }
}
using AgentFive.Services.OpenRouter;

namespace AgentFive.Tasks.Railway.Tools;

public static class RailwayToolProvider
{
    public static IReadOnlyCollection<ChatToolDefinition> CreateTools(RailwayHelpDocument helpDocument)
    {
        ArgumentNullException.ThrowIfNull(helpDocument);

        var setStatusValues = helpDocument.Actions
            .FirstOrDefault(action => string.Equals(action.Action, "setstatus", StringComparison.OrdinalIgnoreCase))?
            .AllowedValues
            .ToArray() ?? Array.Empty<string>();

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
                    "get_route_status",
                    "Calls the documented getstatus action for a route. Use this to inspect the current route mode and status.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            route = new { type = "string", description = "Route identifier like X-01." }
                        },
                        required = new[] { "route" },
                        additionalProperties = false
                    })),
            new(
                "function",
                new ChatFunctionDefinition(
                    "enable_reconfigure_mode",
                    "Calls the documented reconfigure action for a route. Use this before changing route status.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            route = new { type = "string", description = "Route identifier like X-01." }
                        },
                        required = new[] { "route" },
                        additionalProperties = false
                    })),
            new(
                "function",
                new ChatFunctionDefinition(
                    "set_route_status",
                    "Calls the documented setstatus action for a route while in reconfigure mode. Use RTOPEN to activate the route unless the hub explicitly instructs a different value.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            route = new { type = "string", description = "Route identifier like X-01." },
                            value = new { type = "string", description = $"One of: {string.Join(", ", setStatusValues)}." }
                        },
                        required = new[] { "route", "value" },
                        additionalProperties = false
                    })),
            new(
                "function",
                new ChatFunctionDefinition(
                    "save_route_configuration",
                    "Calls the documented save action for a route to persist the reconfiguration and exit reconfigure mode.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            route = new { type = "string", description = "Route identifier like X-01." }
                        },
                        required = new[] { "route" },
                        additionalProperties = false
                    })),
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
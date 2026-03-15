using System.Text.Json;
using AgentFive.Services.OpenRouter;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.Railway.Tools;

public sealed class RailwayToolHandler
{
    private readonly RailwayHubClient _hubClient;
    private readonly RailwayExecutionTranscript _transcript;
    private readonly RailwayHubResponse _cachedHelpResponse;
    private readonly RailwayHelpDocument _helpDocument;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public RailwayToolHandler(
        RailwayHubClient hubClient,
        RailwayExecutionTranscript transcript,
        RailwayHubResponse cachedHelpResponse,
        RailwayHelpDocument helpDocument,
        ILogger logger)
    {
        _hubClient = hubClient;
        _transcript = transcript;
        _cachedHelpResponse = cachedHelpResponse;
        _helpDocument = helpDocument;
        _logger = logger;
    }

    public RailwayCompletionDraft? CompletionDraft { get; private set; }

    public async Task<string> HandleToolCallAsync(ChatToolCall toolCall)
    {
        try
        {
            return toolCall.Function.Name switch
            {
                "get_cached_help" => Serialize(BuildCachedHelpResult()),
                "execute_documented_action" => await HandleExecuteDocumentedActionAsync(toolCall.Function.Arguments).ConfigureAwait(false),
                "get_execution_history" => Serialize(BuildExecutionHistory()),
                "finish_with_result" => HandleFinish(toolCall.Function.Arguments),
                _ => Serialize(new { error = $"Unknown railway tool: {toolCall.Function.Name}" })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Railway tool call failed: {ToolName}", toolCall.Function.Name);
            return Serialize(new { error = ex.Message, tool = toolCall.Function.Name });
        }
    }

    private async Task<string> HandleExecuteDocumentedActionAsync(string argumentsJson)
    {
        var args = JsonSerializer.Deserialize<ExecuteDocumentedActionArgs>(argumentsJson, _jsonOptions)
            ?? throw new InvalidOperationException("Unable to parse execute_documented_action arguments.");

        if (string.IsNullOrWhiteSpace(args.Action))
        {
            throw new InvalidOperationException("execute_documented_action requires a concrete action name.");
        }

        if (string.Equals(args.Action, "help", StringComparison.OrdinalIgnoreCase))
        {
            return Serialize(new
            {
                error = "The help action is already cached for this run. Use get_cached_help instead of spending another hub call.",
                cached = true,
                response = BuildHubResponseView(_cachedHelpResponse)
            });
        }

        var normalizedAction = args.Action.Trim();
        if (normalizedAction.Contains(' ') || normalizedAction.Contains('='))
        {
            throw new InvalidOperationException("Action must be a bare action name like getstatus, reconfigure, setstatus, or save. Pass route and value as separate fields.");
        }

        var actionDefinition = _helpDocument.Actions.FirstOrDefault(action => string.Equals(action.Action, normalizedAction, StringComparison.OrdinalIgnoreCase));
        if (actionDefinition == null)
        {
            throw new InvalidOperationException($"Unknown documented action: {normalizedAction}");
        }

        var parameters = BuildParameters(actionDefinition, args);
        var response = await _hubClient.ExecuteActionAsync(normalizedAction, parameters).ConfigureAwait(false);
        _transcript.Actions.Add(RailwayActionAttempt.FromResponse(response));
        _transcript.TotalHttpCalls++;
        _transcript.TotalRetries += response.RetryCount;

        return Serialize(new
        {
            response = BuildHubResponseView(response),
            shouldStop = !string.IsNullOrWhiteSpace(response.ExtractedFlag)
        });
    }

    private string HandleFinish(string argumentsJson)
    {
        var args = JsonSerializer.Deserialize<FinishArgs>(argumentsJson, _jsonOptions)
            ?? throw new InvalidOperationException("Unable to parse finish_with_result arguments.");

        if (string.IsNullOrWhiteSpace(args.FinalFlag) || !args.FinalFlag.StartsWith("{FLG:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("finish_with_result requires a valid flag in the format {FLG:...}.");
        }

        CompletionDraft = new RailwayCompletionDraft
        {
            FinalFlag = args.FinalFlag,
            Summary = args.Summary,
            LastAction = args.LastAction
        };

        return Serialize(new { accepted = true, finalFlag = args.FinalFlag });
    }

    private object BuildCachedHelpResult()
    {
        return new
        {
            cached = true,
            documentation = new
            {
                routeFormat = _helpDocument.RouteFormat,
                notes = _helpDocument.Notes,
                actions = _helpDocument.Actions
            },
            response = BuildHubResponseView(_cachedHelpResponse)
        };
    }

    private object BuildExecutionHistory()
    {
        return new
        {
            runId = _transcript.RunId,
            totalHttpCalls = _transcript.TotalHttpCalls,
            totalRetries = _transcript.TotalRetries,
            totalWaitMilliseconds = _transcript.TotalWaitMilliseconds,
            waits = _transcript.Waits.Select(wait => new
            {
                wait.TimestampUtc,
                wait.Action,
                wait.AttemptNumber,
                wait.Source,
                wait.Reason,
                wait.DurationMilliseconds
            }),
            actions = _transcript.Actions.Select(action => new
            {
                action.Sequence,
                action.Action,
                action.StatusCode,
                action.IsSuccessStatusCode,
                action.IsOverloaded,
                action.RetryCount,
                action.ElapsedMilliseconds,
                action.TimestampUtc,
                action.ExtractedFlag,
                responseSnippet = action.RawBody.Length > 800 ? action.RawBody[..800] : action.RawBody
            })
        };
    }

    private static Dictionary<string, object?> BuildParameters(RailwayHelpActionDefinition actionDefinition, ExecuteDocumentedActionArgs args)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var required in actionDefinition.Requires)
        {
            if (string.Equals(required, "route", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(args.Route))
                {
                    throw new InvalidOperationException($"Action {actionDefinition.Action} requires route.");
                }

                parameters["route"] = args.Route;
                continue;
            }

            if (string.Equals(required, "value", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(args.Value))
                {
                    throw new InvalidOperationException($"Action {actionDefinition.Action} requires value.");
                }

                if (actionDefinition.AllowedValues.Count > 0 && !actionDefinition.AllowedValues.Contains(args.Value, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Action {actionDefinition.Action} requires one of: {string.Join(", ", actionDefinition.AllowedValues)}.");
                }

                parameters["value"] = args.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(args.Route) && !parameters.ContainsKey("route"))
        {
            parameters["route"] = args.Route;
        }

        if (!string.IsNullOrWhiteSpace(args.Value) && !parameters.ContainsKey("value"))
        {
            parameters["value"] = args.Value;
        }

        return parameters;
    }

    private static object BuildHubResponseView(RailwayHubResponse response)
    {
        return new
        {
            response.Sequence,
            response.Action,
            statusCode = (int)response.StatusCode,
            response.IsSuccessStatusCode,
            response.IsOverloaded,
            response.RetryCount,
            response.ExtractedFlag,
            response.ElapsedMilliseconds,
            response.TimestampUtc,
            response.RawBody,
            response.ParsedJson,
            response.Headers,
            response.RateLimit,
            artifacts = new
            {
                response.RequestArtifactPath,
                response.ResponseArtifactPath,
                response.HeadersArtifactPath
            }
        };
    }

    private string Serialize(object value)
    {
        return JsonSerializer.Serialize(value, _jsonOptions);
    }

    private sealed class ExecuteDocumentedActionArgs
    {
        public string Action { get; init; } = string.Empty;
        public string? Route { get; init; }
        public string? Value { get; init; }
    }

    private sealed class FinishArgs
    {
        public string FinalFlag { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string LastAction { get; init; } = string.Empty;
    }
}
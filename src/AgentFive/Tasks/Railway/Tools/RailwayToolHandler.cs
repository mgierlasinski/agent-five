using System.Text.Json;
using System.Text.RegularExpressions;
using AgentFive.Services.OpenRouter;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.Railway.Tools;

public sealed class RailwayToolHandler
{
    private static readonly Regex RouteRegex = new("^[A-Za-z]-[0-9]{1,2}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
                "get_route_status" => await HandleRouteOnlyActionAsync(toolCall.Function.Arguments, "getstatus").ConfigureAwait(false),
                "enable_reconfigure_mode" => await HandleRouteOnlyActionAsync(toolCall.Function.Arguments, "reconfigure").ConfigureAwait(false),
                "set_route_status" => await HandleSetRouteStatusAsync(toolCall.Function.Arguments).ConfigureAwait(false),
                "save_route_configuration" => await HandleRouteOnlyActionAsync(toolCall.Function.Arguments, "save").ConfigureAwait(false),
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

    private async Task<string> HandleRouteOnlyActionAsync(string argumentsJson, string action)
    {
        var args = JsonSerializer.Deserialize<RouteArgs>(argumentsJson, _jsonOptions)
            ?? throw new InvalidOperationException($"Unable to parse {action} arguments.");

        var route = NormalizeAndValidateRoute(args.Route);
        return await ExecuteDocumentedActionAsync(action, route, null).ConfigureAwait(false);
    }

    private async Task<string> HandleSetRouteStatusAsync(string argumentsJson)
    {
        var args = JsonSerializer.Deserialize<SetRouteStatusArgs>(argumentsJson, _jsonOptions)
            ?? throw new InvalidOperationException("Unable to parse set_route_status arguments.");

        var route = NormalizeAndValidateRoute(args.Route);
        var value = NormalizeAndValidateStatusValue(args.Value);
        return await ExecuteDocumentedActionAsync("setstatus", route, value).ConfigureAwait(false);
    }

    private async Task<string> ExecuteDocumentedActionAsync(string action, string route, string? value)
    {
        if (string.Equals(action, "help", StringComparison.OrdinalIgnoreCase))
        {
            return Serialize(new
            {
                error = "The help action is already cached for this run. Use get_cached_help instead of spending another hub call.",
                cached = true,
                response = BuildHubResponseView(_cachedHelpResponse)
            });
        }

        var actionDefinition = _helpDocument.Actions.FirstOrDefault(candidate => string.Equals(candidate.Action, action, StringComparison.OrdinalIgnoreCase));
        if (actionDefinition == null)
        {
            throw new InvalidOperationException($"Unknown documented action: {action}");
        }

        var parameters = BuildParameters(actionDefinition, route, value);
        var response = await _hubClient.ExecuteActionAsync(action, parameters).ConfigureAwait(false);
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

    private Dictionary<string, object?> BuildParameters(RailwayHelpActionDefinition actionDefinition, string route, string? value)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var required in actionDefinition.Requires)
        {
            if (string.Equals(required, "route", StringComparison.OrdinalIgnoreCase))
            {
                parameters["route"] = route;
                continue;
            }

            if (string.Equals(required, "value", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException($"Action {actionDefinition.Action} requires value.");
                }

                if (actionDefinition.AllowedValues.Count > 0 && !actionDefinition.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Action {actionDefinition.Action} requires one of: {string.Join(", ", actionDefinition.AllowedValues)}.");
                }

                parameters["value"] = value;
            }
        }

        return parameters;
    }

    private static string NormalizeAndValidateRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            throw new InvalidOperationException("The route argument is required.");
        }

        var normalizedRoute = route.Trim();
        if (!RouteRegex.IsMatch(normalizedRoute))
        {
            throw new InvalidOperationException("Route must match the documented format [a-z]-[0-9]{1,2}, for example X-01.");
        }

        return normalizedRoute;
    }

    private string NormalizeAndValidateStatusValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The value argument is required for set_route_status.");
        }

        var normalizedValue = value.Trim().ToUpperInvariant();
        var allowedValues = _helpDocument.Actions
            .FirstOrDefault(action => string.Equals(action.Action, "setstatus", StringComparison.OrdinalIgnoreCase))?
            .AllowedValues ?? new List<string>();

        if (allowedValues.Count > 0 && !allowedValues.Contains(normalizedValue, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Value must be one of: {string.Join(", ", allowedValues)}.");
        }

        return normalizedValue;
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

    private sealed class RouteArgs
    {
        public string? Route { get; init; }
    }

    private sealed class SetRouteStatusArgs
    {
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
using System.Text.Json;
using AgentFive.Configuration;
using AgentFive.Services.OpenRouter;
using AgentFive.Tasks.Railway.Tools;
using AgentFive.Utils;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.Railway;

public class RailwayTask
{
    private const string TargetRoute = "X-01";
    private const int MaxAgentIterations = 14;

    private readonly HubSettings _hubSettings;
    private readonly OpenRouterSettings _openRouterSettings;
    private readonly ILogger _logger;

    public RailwayTask(HubSettings hubSettings, OpenRouterSettings openRouterSettings, ILogger logger)
    {
        _hubSettings = hubSettings;
        _openRouterSettings = openRouterSettings;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        ValidateSettings();

        var runId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var artifactDirectory = Path.Combine(FileHelper.BasePath, "Artifacts", "railway", runId);
        Directory.CreateDirectory(artifactDirectory);

        var transcript = new RailwayExecutionTranscript
        {
            RunId = runId,
            TaskName = "railway",
            TargetRoute = TargetRoute,
            SelectedModel = OpenRouterModels.Gpt5Mini,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        RailwayHubClient? hubClient = null;
        OpenRouterService? openRouter = null;

        try
        {
            _logger.LogInformation("Starting railway task for route {Route}. Artifacts: {ArtifactDirectory}", TargetRoute, artifactDirectory);

            hubClient = new RailwayHubClient(_hubSettings, artifactDirectory, _logger, waitEvent =>
            {
                transcript.Waits.Add(waitEvent);
                transcript.TotalWaitMilliseconds += waitEvent.DurationMilliseconds;
            });
            openRouter = new OpenRouterService(_openRouterSettings, _logger);

            var helpResponse = await hubClient.RequestHelpAsync().ConfigureAwait(false);
            transcript.Actions.Add(RailwayActionAttempt.FromResponse(helpResponse));
            transcript.TotalHttpCalls++;
            transcript.TotalRetries += helpResponse.RetryCount;

            if (!helpResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Railway help request failed with status {(int)helpResponse.StatusCode}: {helpResponse.RawBody}");
            }

            await PersistHelpArtifactAsync(artifactDirectory, helpResponse).ConfigureAwait(false);
            var helpDocument = ParseHelpDocument(helpResponse);

            if (!string.IsNullOrWhiteSpace(helpResponse.ExtractedFlag))
            {
                var immediateResult = new RailwayAgentResult
                {
                    Completed = true,
                    FinalFlag = helpResponse.ExtractedFlag,
                    Summary = "The flag was returned directly by the help action.",
                    LastAction = "help",
                    NextStepAssessment = "No additional action was required."
                };

                transcript.FinalOutcome = new RailwayFinalOutcome
                {
                    Success = true,
                    FinalFlag = immediateResult.FinalFlag,
                    Summary = immediateResult.Summary,
                    LastAction = immediateResult.LastAction
                };
                transcript.CompletedAtUtc = DateTimeOffset.UtcNow;
                await PersistOutputsAsync(artifactDirectory, transcript, immediateResult, transcript.Actions.LastOrDefault()).ConfigureAwait(false);
                LogCompletion(transcript);
                return;
            }

            var toolHandler = new RailwayToolHandler(hubClient, transcript, helpResponse, helpDocument, _logger);
            RailwayAgentResult? agentResult;

            try
            {
                agentResult = await openRouter.RunToolConversationAsync<RailwayAgentResult>(
                    BuildSystemPrompt(helpDocument),
                    BuildUserPrompt(helpResponse, helpDocument, transcript),
                    RailwayToolProvider.CreateTools(helpDocument),
                    toolHandler.HandleToolCallAsync,
                    OpenRouterModels.Gpt5Mini,
                    BuildResponseSchema(),
                    0.0,
                    MaxAgentIterations).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("maximum number of iterations", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(ex, "Railway agent exhausted iterations. Falling back to deterministic protocol execution.");
                agentResult = await RunDeterministicFallbackAsync(hubClient, transcript).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Railway agent invocation failed. Falling back to deterministic protocol execution.");
                agentResult = await RunDeterministicFallbackAsync(hubClient, transcript).ConfigureAwait(false);
            }

            if (agentResult == null)
            {
                _logger.LogWarning("Railway agent returned no final result. Falling back to deterministic protocol execution.");
                agentResult = await RunDeterministicFallbackAsync(hubClient, transcript).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(agentResult.FinalFlag)
                && string.IsNullOrWhiteSpace(toolHandler.CompletionDraft?.FinalFlag)
                && transcript.Actions.All(action => string.IsNullOrWhiteSpace(action.ExtractedFlag)))
            {
                _logger.LogWarning("Railway agent completed without a flag. Falling back to deterministic protocol execution.");
                agentResult = await RunDeterministicFallbackAsync(hubClient, transcript).ConfigureAwait(false);
            }

            var finalFlag = FirstNonEmpty(
                agentResult.FinalFlag,
                toolHandler.CompletionDraft?.FinalFlag,
                transcript.Actions.LastOrDefault(action => !string.IsNullOrWhiteSpace(action.ExtractedFlag))?.ExtractedFlag);

            if (string.IsNullOrWhiteSpace(finalFlag))
            {
                throw new InvalidOperationException("Railway execution ended without discovering a final flag.");
            }

            var lastAction = FirstNonEmpty(
                agentResult.LastAction,
                toolHandler.CompletionDraft?.LastAction,
                transcript.Actions.LastOrDefault()?.Action,
                "help");

            var summary = FirstNonEmpty(
                agentResult.Summary,
                toolHandler.CompletionDraft?.Summary,
                "Railway task completed successfully.");

            var normalizedResult = new RailwayAgentResult
            {
                Completed = true,
                FinalFlag = finalFlag,
                Summary = summary,
                LastAction = lastAction,
                NextStepAssessment = agentResult.NextStepAssessment
            };

            transcript.FinalOutcome = new RailwayFinalOutcome
            {
                Success = true,
                FinalFlag = finalFlag,
                Summary = summary,
                LastAction = lastAction
            };
            transcript.CompletedAtUtc = DateTimeOffset.UtcNow;

            var lastSuccessResponse = transcript.Actions.LastOrDefault(action => action.IsSuccessStatusCode);
            await PersistOutputsAsync(artifactDirectory, transcript, normalizedResult, lastSuccessResponse).ConfigureAwait(false);
            LogCompletion(transcript);
        }
        catch (Exception ex)
        {
            transcript.CompletedAtUtc = DateTimeOffset.UtcNow;
            transcript.FinalOutcome ??= new RailwayFinalOutcome
            {
                Success = false,
                FinalFlag = null,
                Summary = ex.Message,
                LastAction = transcript.Actions.LastOrDefault()?.Action
            };

            var failedResult = new RailwayAgentResult
            {
                Completed = false,
                FinalFlag = string.Empty,
                Summary = ex.Message,
                LastAction = transcript.Actions.LastOrDefault()?.Action ?? string.Empty,
                NextStepAssessment = "Inspect the persisted transcript and hub artifacts before the next run."
            };

            await PersistOutputsAsync(artifactDirectory, transcript, failedResult, transcript.Actions.LastOrDefault()).ConfigureAwait(false);
            _logger.LogError(ex, "Railway task failed.");
        }
        finally
        {
            hubClient?.Dispose();
            openRouter?.Dispose();
        }
    }

    private void ValidateSettings()
    {
        ArgumentNullException.ThrowIfNull(_hubSettings);
        ArgumentNullException.ThrowIfNull(_openRouterSettings);
        ArgumentNullException.ThrowIfNull(_logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(_hubSettings.HubUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(_hubSettings.HubApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(_openRouterSettings.OpenRouterUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(_openRouterSettings.OpenRouterApiKey);
    }

    private async Task PersistHelpArtifactAsync(string artifactDirectory, RailwayHubResponse helpResponse)
    {
        var helpExtension = helpResponse.ParsedJson.HasValue ? "json" : "txt";
        var helpPath = Path.Combine(artifactDirectory, $"railway-help-response.{helpExtension}");
        await File.WriteAllTextAsync(helpPath, helpResponse.RawBody).ConfigureAwait(false);
    }

    private async Task PersistOutputsAsync(
        string artifactDirectory,
        RailwayExecutionTranscript transcript,
        RailwayAgentResult result,
        RailwayActionAttempt? lastRelevantResponse)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        var transcriptPath = Path.Combine(artifactDirectory, "railway-transcript.json");
        await File.WriteAllTextAsync(transcriptPath, JsonSerializer.Serialize(transcript, jsonOptions)).ConfigureAwait(false);

        var finalResultPath = Path.Combine(artifactDirectory, "railway-final-result.json");
        await File.WriteAllTextAsync(finalResultPath, JsonSerializer.Serialize(result, jsonOptions)).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result.FinalFlag))
        {
            var flagPath = Path.Combine(artifactDirectory, "railway-flag.txt");
            await File.WriteAllTextAsync(flagPath, result.FinalFlag).ConfigureAwait(false);
        }

        if (lastRelevantResponse != null)
        {
            var lastResponsePath = Path.Combine(artifactDirectory, "railway-last-response.txt");
            await File.WriteAllTextAsync(lastResponsePath, lastRelevantResponse.RawBody).ConfigureAwait(false);
        }
    }

    private string BuildSystemPrompt(RailwayHelpDocument helpDocument)
    {
        var actions = string.Join(", ", helpDocument.Actions.Select(action => action.Action));
        return string.Join(Environment.NewLine,
            "You are an autonomous agent solving the railway task against a self-documenting hub API.",
            "Your goal is to activate route X-01, which means the route must end in the open state unless the hub explicitly says otherwise.",
            "You must begin from the cached help response and trust only hub outputs.",
            $"The documented actions are: {actions}.",
            "Rules:",
            "1. Never invent undocumented action names or undocumented fields.",
            "2. Use get_cached_help before deciding if you need to re-read the documentation.",
            "3. Use only these action tools: get_route_status, enable_reconfigure_mode, set_route_status, and save_route_configuration.",
            "4. Always pass route as a separate tool argument, for example X-01. Never put route or value inside an action string.",
            "5. If the route is already open, do not close it. Never choose RTCLOSE unless the hub explicitly instructs you to do so.",
            "6. If a status change is needed to activate the route, prefer RTOPEN.",
            "7. Minimize the number of hub calls because the API is heavily rate-limited.",
            "8. Treat business errors as protocol guidance and adapt only from the returned response.",
            "9. If any tool response contains extractedFlag, stop immediately.",
            "10. After the flag is found, call finish_with_result once and then return only the final JSON matching the schema.",
            "11. If you are uncertain, inspect get_execution_history instead of guessing.",
            "12. Do not include markdown in the final response.");
    }

    private string BuildUserPrompt(RailwayHubResponse helpResponse, RailwayHelpDocument helpDocument, RailwayExecutionTranscript transcript)
    {
        var payload = new
        {
            task = "railway",
            targetRoute = TargetRoute,
            model = OpenRouterModels.Gpt5Mini,
            runId = transcript.RunId,
            constraints = new[]
            {
                "The first hub request has already been executed with action help and its result is cached.",
                "The hub may return 503 overload responses and strict rate-limit headers; transport control is handled by tools.",
                "Stop as soon as the response body contains a flag in the format {FLG:...}.",
                "Do not guess action ordering or parameters. Follow the documentation returned by help and later hub responses exactly.",
                "The business goal is to activate route X-01, so the desired final route status is open."
            },
            cachedHelp = new
            {
                statusCode = (int)helpResponse.StatusCode,
                rawBody = helpResponse.RawBody,
                parsedJson = helpResponse.ParsedJson,
                headers = helpResponse.Headers
            },
            parsedHelp = new
            {
                routeFormat = helpDocument.RouteFormat,
                notes = helpDocument.Notes,
                actions = helpDocument.Actions
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
    }

    private static JsonSchemaObject BuildResponseSchema()
    {
        return new JsonSchemaObject(
            "railway_result",
            true,
            new
            {
                type = "object",
                properties = new
                {
                    completed = new { type = "boolean" },
                    finalFlag = new { type = "string" },
                    summary = new { type = "string" },
                    lastAction = new { type = "string" },
                    nextStepAssessment = new { type = "string" }
                },
                required = new[] { "completed", "finalFlag", "summary", "lastAction", "nextStepAssessment" },
                additionalProperties = false
            });
    }

    private void LogCompletion(RailwayExecutionTranscript transcript)
    {
        _logger.LogInformation(
            "Railway finished. Success={Success}, Calls={Calls}, Retries={Retries}, WaitMs={WaitMs}, Flag={Flag}",
            transcript.FinalOutcome?.Success ?? false,
            transcript.TotalHttpCalls,
            transcript.TotalRetries,
            transcript.TotalWaitMilliseconds,
            transcript.FinalOutcome?.FinalFlag ?? string.Empty);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static RailwayHelpDocument ParseHelpDocument(RailwayHubResponse helpResponse)
    {
        var envelope = JsonSerializer.Deserialize<RailwayHelpResponseEnvelope>(helpResponse.RawBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (envelope?.Help == null || envelope.Help.Actions.Count == 0)
        {
            throw new InvalidOperationException("Railway help response did not contain a usable action catalog.");
        }

        return envelope.Help;
    }

    private async Task<RailwayAgentResult> RunDeterministicFallbackAsync(RailwayHubClient hubClient, RailwayExecutionTranscript transcript)
    {
        _logger.LogInformation("Running deterministic fallback for railway route {Route}.", TargetRoute);

        var getStatus = await ExecuteFallbackActionAsync(hubClient, transcript, "getstatus", new Dictionary<string, object?> { ["route"] = TargetRoute.ToLowerInvariant() }).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(getStatus.ExtractedFlag))
        {
            return BuildFallbackResult(getStatus.ExtractedFlag, "getstatus", "The initial status response already contained the flag.");
        }

        var mode = TryGetString(getStatus.ParsedJson, "mode");
        var status = TryGetString(getStatus.ParsedJson, "status");

        if (!string.Equals(mode, "reconfigure", StringComparison.OrdinalIgnoreCase))
        {
            var reconfigure = await ExecuteFallbackActionAsync(hubClient, transcript, "reconfigure", new Dictionary<string, object?> { ["route"] = TargetRoute.ToLowerInvariant() }).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reconfigure.ExtractedFlag))
            {
                return BuildFallbackResult(reconfigure.ExtractedFlag, "reconfigure", "The flag was returned when reconfigure mode was enabled.");
            }
        }

        if (!string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
        {
            var setStatus = await ExecuteFallbackActionAsync(hubClient, transcript, "setstatus", new Dictionary<string, object?>
            {
                ["route"] = TargetRoute.ToLowerInvariant(),
                ["value"] = "RTOPEN"
            }).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(setStatus.ExtractedFlag))
            {
                return BuildFallbackResult(setStatus.ExtractedFlag, "setstatus", "The flag was returned when setting the route to RTOPEN.");
            }
        }

        var save = await ExecuteFallbackActionAsync(hubClient, transcript, "save", new Dictionary<string, object?> { ["route"] = TargetRoute.ToLowerInvariant() }).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(save.ExtractedFlag))
        {
            return BuildFallbackResult(save.ExtractedFlag, "save", "The flag was returned when saving the route configuration.");
        }

        var finalStatus = await ExecuteFallbackActionAsync(hubClient, transcript, "getstatus", new Dictionary<string, object?> { ["route"] = TargetRoute.ToLowerInvariant() }).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(finalStatus.ExtractedFlag))
        {
            return BuildFallbackResult(finalStatus.ExtractedFlag, "getstatus", "The flag was returned after confirming the final route status.");
        }

        throw new InvalidOperationException("Deterministic fallback completed the documented route activation sequence but no flag was returned.");
    }

    private async Task<RailwayHubResponse> ExecuteFallbackActionAsync(
        RailwayHubClient hubClient,
        RailwayExecutionTranscript transcript,
        string action,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var response = await hubClient.ExecuteActionAsync(action, parameters).ConfigureAwait(false);
        transcript.Actions.Add(RailwayActionAttempt.FromResponse(response));
        transcript.TotalHttpCalls++;
        transcript.TotalRetries += response.RetryCount;

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Fallback action {action} failed with status {(int)response.StatusCode}: {response.RawBody}");
        }

        return response;
    }

    private static RailwayAgentResult BuildFallbackResult(string finalFlag, string lastAction, string summary)
    {
        return new RailwayAgentResult
        {
            Completed = true,
            FinalFlag = finalFlag,
            Summary = summary,
            LastAction = lastAction,
            NextStepAssessment = "The deterministic fallback completed the documented protocol."
        };
    }

    private static string? TryGetString(JsonElement? root, string propertyName)
    {
        if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return root.Value.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }
}

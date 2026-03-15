using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFive.Tasks.Railway;

public sealed class RailwayExecutionTranscript
{
    public string RunId { get; init; } = string.Empty;
    public string TaskName { get; init; } = string.Empty;
    public string TargetRoute { get; init; } = string.Empty;
    public string SelectedModel { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public List<RailwayActionAttempt> Actions { get; } = new();
    public List<RailwayWaitEvent> Waits { get; } = new();
    public RailwayFinalOutcome? FinalOutcome { get; set; }
    public int TotalHttpCalls { get; set; }
    public int TotalRetries { get; set; }
    public long TotalWaitMilliseconds { get; set; }
}

public sealed class RailwayActionAttempt
{
    public int Sequence { get; init; }
    public string Action { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public bool IsSuccessStatusCode { get; init; }
    public bool IsOverloaded { get; init; }
    public int RetryCount { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public string RequestJson { get; init; } = string.Empty;
    public string RawBody { get; init; } = string.Empty;
    public string? ExtractedFlag { get; init; }
    public Dictionary<string, string[]> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public RailwayRateLimitInfo? RateLimit { get; init; }
    public string RequestArtifactPath { get; init; } = string.Empty;
    public string ResponseArtifactPath { get; init; } = string.Empty;
    public string HeadersArtifactPath { get; init; } = string.Empty;

    public static RailwayActionAttempt FromResponse(RailwayHubResponse response)
    {
        return new RailwayActionAttempt
        {
            Sequence = response.Sequence,
            Action = response.Action,
            StatusCode = (int)response.StatusCode,
            IsSuccessStatusCode = response.IsSuccessStatusCode,
            IsOverloaded = response.IsOverloaded,
            RetryCount = response.RetryCount,
            ElapsedMilliseconds = response.ElapsedMilliseconds,
            TimestampUtc = response.TimestampUtc,
            RequestJson = response.RequestJson,
            RawBody = response.RawBody,
            ExtractedFlag = response.ExtractedFlag,
            Headers = response.Headers,
            RateLimit = response.RateLimit,
            RequestArtifactPath = response.RequestArtifactPath,
            ResponseArtifactPath = response.ResponseArtifactPath,
            HeadersArtifactPath = response.HeadersArtifactPath
        };
    }
}

public sealed class RailwayHubResponse
{
    public int Sequence { get; init; }
    public string Action { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HttpStatusCode StatusCode { get; init; }
    public bool IsSuccessStatusCode { get; init; }
    public bool IsOverloaded { get; init; }
    public int RetryCount { get; init; }
    public string RawBody { get; init; } = string.Empty;
    public JsonElement? ParsedJson { get; init; }
    public Dictionary<string, string[]> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public RailwayRateLimitInfo? RateLimit { get; init; }
    public string? ExtractedFlag { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public string RequestJson { get; init; } = string.Empty;
    public string RequestArtifactPath { get; init; } = string.Empty;
    public string ResponseArtifactPath { get; init; } = string.Empty;
    public string HeadersArtifactPath { get; init; } = string.Empty;
}

public sealed class RailwayHelpResponseEnvelope
{
    public bool Ok { get; init; }
    public string Action { get; init; } = string.Empty;
    public RailwayHelpDocument Help { get; init; } = new();
}

public sealed class RailwayHelpDocument
{
    public List<RailwayHelpActionDefinition> Actions { get; init; } = new();
    public string RouteFormat { get; init; } = string.Empty;
    public Dictionary<string, string> StatusValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; init; } = new();
}

public sealed class RailwayHelpActionDefinition
{
    public string Action { get; init; } = string.Empty;
    public List<string> Requires { get; init; } = new();
    public List<string> Optional { get; init; } = new();
    public string About { get; init; } = string.Empty;
    public List<string> AllowedValues { get; init; } = new();
}

public sealed class RailwayRateLimitInfo
{
    public int? Remaining { get; init; }
    public string? RemainingHeader { get; init; }
    public DateTimeOffset? ResetUtc { get; init; }
    public string? ResetHeader { get; init; }
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public string? RetryAfterHeader { get; init; }
    public long? AppliedWaitMilliseconds { get; init; }
}

public sealed class RailwayWaitEvent
{
    public DateTimeOffset TimestampUtc { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public long DurationMilliseconds { get; init; }
    public string Action { get; init; } = string.Empty;
    public int AttemptNumber { get; init; }
}

public sealed class RailwayFinalOutcome
{
    public bool Success { get; init; }
    public string? FinalFlag { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? LastAction { get; init; }
}

public sealed class RailwayAgentResult
{
    public bool Completed { get; init; }
    public string FinalFlag { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string LastAction { get; init; } = string.Empty;
    public string NextStepAssessment { get; init; } = string.Empty;
}

public sealed class RailwayCompletionDraft
{
    public string FinalFlag { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string LastAction { get; init; } = string.Empty;
}
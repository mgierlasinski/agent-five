using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentFive.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.Railway;

public sealed class RailwayHubClient : IDisposable
{
    private const int Max503Retries = 5;
    private const int Max429Retries = 6;
    private const int InitialBackoffMilliseconds = 800;
    private const int MaxJitterMilliseconds = 350;

    private static readonly Regex FlagRegex = new(@"\{FLG:[^}]+\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly HubSettings _settings;
    private readonly ILogger _logger;
    private readonly string _artifactDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly Action<RailwayWaitEvent>? _waitRecorder;
    private readonly Random _random = new();
    private int _callSequence;
    private bool _disposed;

    public RailwayHubClient(HubSettings settings, string artifactDirectory, ILogger logger, Action<RailwayWaitEvent>? waitRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.HubUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.HubApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);

        _settings = settings;
        _artifactDirectory = artifactDirectory;
        _logger = logger;
        _waitRecorder = waitRecorder;
        Directory.CreateDirectory(_artifactDirectory);

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(settings.HubUrl, UriKind.Absolute)
        };
    }

    public Task<RailwayHubResponse> RequestHelpAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteActionAsync("help", null, cancellationToken);
    }

    public async Task<RailwayHubResponse> ExecuteActionAsync(
        string action,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var answer = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = action
        };

        if (parameters != null)
        {
            foreach (var pair in parameters)
            {
                if (string.Equals(pair.Key, "action", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                answer[pair.Key] = pair.Value;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["apikey"] = _settings.HubApiKey,
            ["task"] = "railway",
            ["answer"] = answer
        };

        var requestJson = JsonSerializer.Serialize(payload, _jsonOptions);
        var retryCount = 0;
    var rateLimitRetryCount = 0;

        while (true)
        {
            var attemptNumber = retryCount + 1;
            var sequence = Interlocked.Increment(ref _callSequence);
            var requestArtifactPath = Path.Combine(_artifactDirectory, $"call-{sequence:D3}-{SanitizeFileSegment(action)}-request.json");
            var responseArtifactPath = Path.Combine(_artifactDirectory, $"call-{sequence:D3}-{SanitizeFileSegment(action)}-response.txt");
            var headersArtifactPath = Path.Combine(_artifactDirectory, $"call-{sequence:D3}-{SanitizeFileSegment(action)}-headers.json");
            await File.WriteAllTextAsync(requestArtifactPath, requestJson, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Railway hub call {Sequence}: action={Action}, attempt={Attempt}", sequence, action, attemptNumber);

            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "verify") { Content = content };
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            await File.WriteAllTextAsync(responseArtifactPath, responseBody, cancellationToken).ConfigureAwait(false);

            var headers = CollectHeaders(response);
            var headersJson = JsonSerializer.Serialize(headers, _jsonOptions);
            await File.WriteAllTextAsync(headersArtifactPath, headersJson, cancellationToken).ConfigureAwait(false);

            var rateLimit = ParseRateLimitInfo(headers, response.StatusCode);
            var extractedFlag = ExtractFlag(responseBody);
            var parsedJson = TryParseJson(responseBody);

            var railwayResponse = new RailwayHubResponse
            {
                Sequence = sequence,
                Action = action,
                StatusCode = response.StatusCode,
                IsSuccessStatusCode = response.IsSuccessStatusCode,
                IsOverloaded = response.StatusCode == HttpStatusCode.ServiceUnavailable,
                RetryCount = retryCount,
                RawBody = responseBody,
                ParsedJson = parsedJson,
                Headers = headers,
                RateLimit = rateLimit,
                ExtractedFlag = extractedFlag,
                TimestampUtc = DateTimeOffset.UtcNow,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                RequestJson = requestJson,
                RequestArtifactPath = requestArtifactPath,
                ResponseArtifactPath = responseArtifactPath,
                HeadersArtifactPath = headersArtifactPath
            };

            _logger.LogInformation(
                "Railway hub response {Sequence}: status={StatusCode}, overloaded={IsOverloaded}, retryCount={RetryCount}, flagFound={FlagFound}",
                sequence,
                (int)railwayResponse.StatusCode,
                railwayResponse.IsOverloaded,
                railwayResponse.RetryCount,
                !string.IsNullOrWhiteSpace(railwayResponse.ExtractedFlag));

            var mandatoryWait = ComputeMandatoryWait(rateLimit, railwayResponse.StatusCode);

            if (railwayResponse.IsOverloaded && retryCount < Max503Retries)
            {
                var backoff = ComputeBackoffDelay(retryCount);
                var delay = mandatoryWait.HasValue ? TimeSpan.FromMilliseconds(Math.Max(backoff.TotalMilliseconds, mandatoryWait.Value.TotalMilliseconds)) : backoff;
                await WaitAsync(action, attemptNumber, delay, railwayResponse.IsOverloaded ? "503 overload retry" : "transient retry", "backoff", cancellationToken).ConfigureAwait(false);
                retryCount++;
                continue;
            }

            if (railwayResponse.StatusCode == HttpStatusCode.TooManyRequests && mandatoryWait.HasValue && mandatoryWait.Value > TimeSpan.Zero && rateLimitRetryCount < Max429Retries)
            {
                await WaitAsync(action, attemptNumber, mandatoryWait.Value, "rate limit reset", "rate-limit", cancellationToken).ConfigureAwait(false);
                rateLimitRetryCount++;
                continue;
            }

            if (mandatoryWait.HasValue && mandatoryWait.Value > TimeSpan.Zero)
            {
                var updatedRateLimit = new RailwayRateLimitInfo
                {
                    Remaining = rateLimit?.Remaining,
                    RemainingHeader = rateLimit?.RemainingHeader,
                    ResetUtc = rateLimit?.ResetUtc,
                    ResetHeader = rateLimit?.ResetHeader,
                    RetryAfterUtc = rateLimit?.RetryAfterUtc,
                    RetryAfterHeader = rateLimit?.RetryAfterHeader,
                    AppliedWaitMilliseconds = (long)mandatoryWait.Value.TotalMilliseconds
                };

                railwayResponse = new RailwayHubResponse
                {
                    Sequence = railwayResponse.Sequence,
                    Action = railwayResponse.Action,
                    StatusCode = railwayResponse.StatusCode,
                    IsSuccessStatusCode = railwayResponse.IsSuccessStatusCode,
                    IsOverloaded = railwayResponse.IsOverloaded,
                    RetryCount = railwayResponse.RetryCount,
                    RawBody = railwayResponse.RawBody,
                    ParsedJson = railwayResponse.ParsedJson,
                    Headers = railwayResponse.Headers,
                    RateLimit = updatedRateLimit,
                    ExtractedFlag = railwayResponse.ExtractedFlag,
                    TimestampUtc = railwayResponse.TimestampUtc,
                    ElapsedMilliseconds = railwayResponse.ElapsedMilliseconds,
                    RequestJson = railwayResponse.RequestJson,
                    RequestArtifactPath = railwayResponse.RequestArtifactPath,
                    ResponseArtifactPath = railwayResponse.ResponseArtifactPath,
                    HeadersArtifactPath = railwayResponse.HeadersArtifactPath
                };

                await WaitAsync(action, attemptNumber, mandatoryWait.Value, "rate limit reset", "rate-limit", cancellationToken).ConfigureAwait(false);
            }

            return railwayResponse;
        }
    }

    private TimeSpan ComputeBackoffDelay(int retryCount)
    {
        var multiplier = Math.Pow(2, retryCount);
        var baseDelay = InitialBackoffMilliseconds * multiplier;
        var jitter = _random.Next(0, MaxJitterMilliseconds + 1);
        return TimeSpan.FromMilliseconds(baseDelay + jitter);
    }

    private async Task WaitAsync(
        string action,
        int attemptNumber,
        TimeSpan delay,
        string reason,
        string source,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        _logger.LogInformation("Railway waiting {DelayMs} ms for action={Action}, reason={Reason}", (long)delay.TotalMilliseconds, action, reason);
        _waitRecorder?.Invoke(new RailwayWaitEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Reason = reason,
            Source = source,
            DurationMilliseconds = (long)delay.TotalMilliseconds,
            Action = action,
            AttemptNumber = attemptNumber
        });

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string[]> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in response.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        return headers;
    }

    private static RailwayRateLimitInfo? ParseRateLimitInfo(IReadOnlyDictionary<string, string[]> headers, HttpStatusCode statusCode)
    {
        int? remaining = null;
        string? remainingHeader = null;
        DateTimeOffset? resetUtc = null;
        string? resetHeader = null;
        DateTimeOffset? retryAfterUtc = null;
        string? retryAfterHeader = null;

        foreach (var candidate in new[] { "x-ratelimit-remaining", "ratelimit-remaining", "x-rate-limit-remaining" })
        {
            if (!headers.TryGetValue(candidate, out var values))
            {
                continue;
            }

            var raw = values.FirstOrDefault();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                remaining = parsed;
                remainingHeader = candidate;
                break;
            }
        }

        foreach (var candidate in new[] { "retry-after" })
        {
            if (!headers.TryGetValue(candidate, out var values))
            {
                continue;
            }

            var raw = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                retryAfterUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
                retryAfterHeader = candidate;
                break;
            }

            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                retryAfterUtc = parsedDate.ToUniversalTime();
                retryAfterHeader = candidate;
                break;
            }
        }

        foreach (var candidate in new[] { "x-ratelimit-reset", "ratelimit-reset", "x-rate-limit-reset", "x-ratelimit-reset-after", "ratelimit-reset-after" })
        {
            if (!headers.TryGetValue(candidate, out var values))
            {
                continue;
            }

            var raw = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parsedReset = ParseResetValue(candidate, raw);
            if (parsedReset.HasValue)
            {
                resetUtc = parsedReset.Value;
                resetHeader = candidate;
                break;
            }
        }

        if (remaining == null && resetUtc == null && retryAfterUtc == null && statusCode != HttpStatusCode.TooManyRequests)
        {
            return null;
        }

        return new RailwayRateLimitInfo
        {
            Remaining = remaining,
            RemainingHeader = remainingHeader,
            ResetUtc = resetUtc,
            ResetHeader = resetHeader,
            RetryAfterUtc = retryAfterUtc,
            RetryAfterHeader = retryAfterHeader
        };
    }

    private static DateTimeOffset? ParseResetValue(string headerName, string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var absoluteDate))
        {
            return absoluteDate.ToUniversalTime();
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return null;
        }

        if (headerName.Contains("after", StringComparison.OrdinalIgnoreCase))
        {
            return DateTimeOffset.UtcNow.AddSeconds(numeric);
        }

        if (numeric >= 1_000_000_000)
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(numeric));
        }

        return DateTimeOffset.UtcNow.AddSeconds(numeric);
    }

    private static TimeSpan? ComputeMandatoryWait(RailwayRateLimitInfo? rateLimit, HttpStatusCode statusCode)
    {
        if (rateLimit == null)
        {
            return null;
        }

        var candidates = new List<TimeSpan>();
        var now = DateTimeOffset.UtcNow;

        if (rateLimit.RetryAfterUtc.HasValue)
        {
            var retryAfterDelay = rateLimit.RetryAfterUtc.Value - now;
            if (retryAfterDelay > TimeSpan.Zero)
            {
                candidates.Add(retryAfterDelay);
            }
        }

        if ((rateLimit.Remaining.HasValue && rateLimit.Remaining.Value <= 0) || statusCode == HttpStatusCode.TooManyRequests)
        {
            if (rateLimit.ResetUtc.HasValue)
            {
                var resetDelay = rateLimit.ResetUtc.Value - now;
                if (resetDelay > TimeSpan.Zero)
                {
                    candidates.Add(resetDelay);
                }
            }
        }

        return candidates.Count == 0 ? null : candidates.Max();
    }

    private static JsonElement? TryParseJson(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractFlag(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        var match = FlagRegex.Match(responseBody);
        return match.Success ? match.Value : null;
    }

    private static string SanitizeFileSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        return builder.ToString().ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
    }
}
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFive.Configuration;
using Microsoft.Extensions.Options;

namespace AgentFive.Api.Services;

public class PackageHubClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HubSettings _settings;
    private readonly ILogger<PackageHubClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PackageHubClient(IHttpClientFactory httpClientFactory, IOptions<HubSettings> options, ILogger<PackageHubClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = options.Value;
        _logger = logger;
    }

    public Task<PackageCheckResponse> CheckPackageAsync(string packageId, CancellationToken cancellationToken) =>
        PostAsync<PackageCheckResponse>(new PackageCheckRequest(_settings.HubApiKey, "check", packageId), cancellationToken);

    public Task<PackageRedirectResponse> RedirectPackageAsync(string packageId, string destination, string code, CancellationToken cancellationToken) =>
        PostAsync<PackageRedirectResponse>(new PackageRedirectRequest(_settings.HubApiKey, "redirect", packageId, destination, code), cancellationToken);

    private async Task<TResponse> PostAsync<TResponse>(object payload, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(PackageHubClient));
        client.BaseAddress = new Uri(_settings.HubUrl);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        _logger.LogInformation("Sending package API request: {Payload}", json);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("api/packages", content, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Received package API response: {Response}", responseBody);
        response.EnsureSuccessStatusCode();

        var parsed = JsonSerializer.Deserialize<TResponse>(responseBody, _jsonOptions);
        if (parsed is null)
        {
            throw new InvalidOperationException("Package API returned an empty or invalid response.");
        }

        return parsed;
    }
}

public record PackageCheckRequest(
    [property: JsonPropertyName("apikey")] string ApiKey,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("packageid")] string PackageId);

public record PackageRedirectRequest(
    [property: JsonPropertyName("apikey")] string ApiKey,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("packageid")] string PackageId,
    [property: JsonPropertyName("destination")] string Destination,
    [property: JsonPropertyName("code")] string Code);

public record PackageCheckResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("packageid")] string? PackageId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("location")] string? Location);

public record PackageRedirectResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("packageid")] string? PackageId,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("confirmation")] string? Confirmation,
    [property: JsonPropertyName("destination")] string? Destination);
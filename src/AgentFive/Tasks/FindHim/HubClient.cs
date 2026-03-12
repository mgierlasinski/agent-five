using System.Text;
using System.Text.Json;
using AgentFive.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.FindHim;

public class HubClient : IDisposable
{
	private readonly HttpClient _httpClient;
	private readonly AppSettings _settings;
	private readonly ILogger _logger;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};
	private bool _disposed;

	public HubClient(AppSettings settings, ILogger logger)
	{
		ArgumentException.ThrowIfNullOrEmpty(settings.HubUrl, nameof(settings.HubUrl));
		ArgumentException.ThrowIfNullOrEmpty(settings.HubApiKey, nameof(settings.HubApiKey));

		_settings = settings;
		_logger = logger;
		_httpClient = new HttpClient
		{
			BaseAddress = new Uri(settings.HubUrl)
		};
	}

	public async Task<List<PowerPlantDefinition>> GetPowerPlantsAsync()
	{
		var relativeUrl = $"data/{_settings.HubApiKey}/findhim_locations.json";
		using var document = await GetJsonDocumentAsync(relativeUrl).ConfigureAwait(false);
		return ParsePowerPlants(document.RootElement);
	}

	public async Task<List<CoordinateArgs>> GetPersonLocationsAsync(string name, string surname)
	{
		var payload = new
		{
			apikey = _settings.HubApiKey,
			name,
			surname
		};

		var locations = await PostJsonAsync<List<CoordinateArgs>>("api/location", payload).ConfigureAwait(false);
		return locations ?? new List<CoordinateArgs>();
	}

	public async Task<JsonElement> GetAccessLevelAsync(string name, string surname, int birthYear)
	{
		var payload = new
		{
			apikey = _settings.HubApiKey,
			name,
			surname,
			birthYear
		};

		return await PostJsonElementAsync("api/accesslevel", payload).ConfigureAwait(false);
	}

	public async Task<JsonElement> VerifyAsync(VerifyRequest payload)
	{
		return await PostJsonElementAsync("verify", payload).ConfigureAwait(false);
	}

	private async Task<JsonDocument> GetJsonDocumentAsync(string relativeUrl)
	{
		_logger.LogInformation("Sending hub request: GET {Url}", relativeUrl);

		using var response = await _httpClient.GetAsync(relativeUrl).ConfigureAwait(false);
		var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
		_logger.LogInformation("Received hub response from {Url}: {Response}", relativeUrl, responseBody);
		response.EnsureSuccessStatusCode();

		return JsonDocument.Parse(responseBody);
	}

	private async Task<TResponse?> PostJsonAsync<TResponse>(string relativeUrl, object payload)
	{
		var responseBody = await PostRawJsonAsync(relativeUrl, payload).ConfigureAwait(false);
		return JsonSerializer.Deserialize<TResponse>(responseBody, _jsonOptions);
	}

	private async Task<JsonElement> PostJsonElementAsync(string relativeUrl, object payload)
	{
		var responseBody = await PostRawJsonAsync(relativeUrl, payload).ConfigureAwait(false);
		using var document = JsonDocument.Parse(responseBody);
		return document.RootElement.Clone();
	}

	private async Task<string> PostRawJsonAsync(string relativeUrl, object payload)
	{
		var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
		_logger.LogInformation("Sending hub request: POST {Url} {Payload}", relativeUrl, json);

		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var response = await _httpClient.PostAsync(relativeUrl, content).ConfigureAwait(false);
		var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
		_logger.LogInformation("Received hub response from {Url}: {Response}", relativeUrl, responseBody);
		response.EnsureSuccessStatusCode();

		return responseBody;
	}

	private static List<PowerPlantDefinition> ParsePowerPlants(JsonElement rootElement)
	{
		if (!rootElement.TryGetProperty("power_plants", out var powerPlantsElement) || powerPlantsElement.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidOperationException("Invalid power plant response: missing power_plants object.");
		}

		var results = new List<PowerPlantDefinition>();
		foreach (var property in powerPlantsElement.EnumerateObject())
		{
			var code = property.Value.TryGetProperty("code", out var codeElement)
				? codeElement.GetString() ?? string.Empty
				: string.Empty;

			results.Add(new PowerPlantDefinition(
				property.Name,
				code,
				TryGetDouble(property.Value, "latitude", "lat"),
				TryGetDouble(property.Value, "longitude", "lon", "lng")));
		}

		if (results.Count == 0)
		{
			throw new InvalidOperationException("Power plant catalog is empty.");
		}

		return results;
	}

	private static double? TryGetDouble(JsonElement element, params string[] propertyNames)
	{
		foreach (var propertyName in propertyNames)
		{
			if (!element.TryGetProperty(propertyName, out var value))
			{
				continue;
			}

			if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
			{
				return number;
			}

			if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed))
			{
				return parsed;
			}
		}

		return null;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_httpClient.Dispose();
		_disposed = true;
	}
}
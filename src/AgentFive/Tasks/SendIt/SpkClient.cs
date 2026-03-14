using System.Text;
using System.Text.Json;
using AgentFive.Configuration;
using AgentFive.Tasks.SendIt.Models;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.SendIt;

public class SpkClient : IDisposable
{
	private const string VerifyRequestFile = "sendit_verify_request.json";
	private const string VerifyResponseFile = "sendit_verify_response.json";

	private readonly HttpClient _httpClient;
	private readonly HubSettings _settings;
	private readonly ILogger _logger;
	private bool _disposed;

	public SpkClient(HubSettings settings, ILogger logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(settings.HubUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(settings.HubApiKey);

		_settings = settings;
		_logger = logger;
		_httpClient = new HttpClient
		{
			BaseAddress = new Uri(settings.HubUrl)
		};
	}

	public async Task<JsonElement> VerifyDeclarationAsync(string declaration, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(declaration);

		var payload = new VerificationRequest(_settings.HubApiKey, "sendit", new VerificationAnswer(declaration));
		var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
		await File.WriteAllTextAsync(VerifyRequestFile, json, cancellationToken).ConfigureAwait(false);

		_logger.LogInformation("Sending sendit verification payload to hub.");
		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var response = await _httpClient.PostAsync("verify", content, cancellationToken).ConfigureAwait(false);
		var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		await File.WriteAllTextAsync(VerifyResponseFile, responseBody, cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			_logger.LogError("Hub verification failed with status {StatusCode}. Request: {Request}. Response: {Response}", (int)response.StatusCode, json, responseBody);
			response.EnsureSuccessStatusCode();
		}

		_logger.LogInformation("Hub verification response: {Response}", responseBody);
		using var document = JsonDocument.Parse(responseBody);
		return document.RootElement.Clone();
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
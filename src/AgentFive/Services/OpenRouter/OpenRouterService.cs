using System.Text.Json;
using System.Text;
using AgentFive.Configuration;

namespace AgentFive.Services.OpenRouter;

public class OpenRouterService : IDisposable
{
	private readonly HttpClient _http;
	private readonly string _apiKey;
	private bool _disposed;

	public OpenRouterService(AppSettings settings)
	{
        if (string.IsNullOrWhiteSpace(settings.OpenRouterApiKey))
			throw new ArgumentException("OpenRouter API key not configured in AppSettings.OpenRouterApiKey");
		if (string.IsNullOrWhiteSpace(settings.OpenRouterUrl))
			throw new ArgumentException("OpenRouter URL not configured in AppSettings.OpenRouterUrl");

		_apiKey = settings.OpenRouterApiKey;
		_http = new HttpClient
		{
			BaseAddress = new Uri(settings.OpenRouterUrl)
		};
		_http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
	}

	public async Task<TResponse?> GetStructuredResponseAsync<TResponse>(string systemPrompt, string userPrompt, JsonSchemaObject? jsonSchema = null, string model = "gpt-4o-mini", double temperature = 0.0) where TResponse : class
	{
		var payload = BuildChatPayload(model, systemPrompt, userPrompt, temperature, jsonSchema);
		var respText = await SendRequestAsync(payload).ConfigureAwait(false);
		var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

		// Try parse OpenRouter response envelope first (choices -> message.content / content)
		try
		{
			// local records to match expected OpenRouter shape
			var or = JsonSerializer.Deserialize<ChatResponse>(respText, opts);
			if (or?.Choices != null && or.Choices.Count > 0)
			{
				var first = or.Choices[0];
				var assistantContent = first?.Message?.Content ?? first?.Content;
				if (!string.IsNullOrWhiteSpace(assistantContent))
				{
					// If assistantContent is a quoted JSON string, unquote it
					if (assistantContent.Length > 0 && assistantContent[0] == '"')
					{
						try
						{
							assistantContent = JsonSerializer.Deserialize<string>(assistantContent, opts) ?? assistantContent;
						}
						catch
						{
							// ignore and continue with original content
						}
					}

					try
					{
						var parsed = JsonSerializer.Deserialize<TResponse>(assistantContent, opts);
						if (parsed != null)
							return parsed;
					}
					catch (JsonException)
					{
						// fall through to other parsing attempts
					}
				}
			}
		}
		catch (JsonException)
		{
			// ignore and try direct deserialization below
		}

		// Try to deserialize the raw response text directly into the target type
		try
		{
			var direct = JsonSerializer.Deserialize<TResponse>(respText, opts);
			if (direct != null)
				return direct;
		}
		catch (JsonException)
		{
			// ignore
		}

		return default;
	}

	private ChatPayload BuildChatPayload(string model, string systemPrompt, string userPrompt, double temperature = 0.0, JsonSchemaObject? jsonSchema = null)
	{
		var messages = new[]
		{
			new ChatMessage("system", systemPrompt),
			new ChatMessage("user", userPrompt)
		};

		JsonResponseFormat? responseFormat = jsonSchema != null ? new JsonResponseFormat("json_schema", jsonSchema) : null;

		return new ChatPayload(model, messages, temperature, responseFormat);
	}

	private async Task<string> SendRequestAsync(ChatPayload payload)
	{
		var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var resp = await _http.PostAsync("v1/chat/completions", content).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
		var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
		return respText;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_http.Dispose();
		_disposed = true;
	}
}

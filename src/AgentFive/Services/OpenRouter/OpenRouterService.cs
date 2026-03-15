using System.Text.Json;
using System.Text;
using AgentFive.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentFive.Services.OpenRouter;

public class OpenRouterService : IDisposable
{
	private readonly HttpClient _httpClient;
	private readonly OpenRouterSettings _settings;
    private readonly ILogger _logger;
	private readonly JsonSerializerOptions _deserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private bool _disposed;

	public OpenRouterService(OpenRouterSettings settings, ILogger logger)
	{
		ArgumentException.ThrowIfNullOrEmpty(settings.OpenRouterApiKey, nameof(settings.OpenRouterApiKey));
		ArgumentException.ThrowIfNullOrEmpty(settings.OpenRouterUrl, nameof(settings.OpenRouterUrl));

		_settings = settings;
        _logger = logger;
        _httpClient = new HttpClient
		{
			BaseAddress = new Uri(settings.OpenRouterUrl)
		};
		_httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenRouterApiKey);
	}

	private async Task<string> SendCompletionsRequest(string json, CancellationToken cancellationToken = default)
	{
		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var response = await _httpClient.PostAsync("v1/chat/completions", content, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		
		return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<ChatResponse?> GetResponseAsync(ChatPayload payload, CancellationToken cancellationToken = default)
	{
		LogChatPayloadCompact(payload);

		var jsonRequest = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
		var jsonResponse = await SendCompletionsRequest(jsonRequest, cancellationToken).ConfigureAwait(false);
		return JsonSerializer.Deserialize<ChatResponse>(jsonResponse, _deserializeOptions);
	}

	public async Task<TResponse?> GetStructuredResponseAsync<TResponse>(
		string systemPrompt, 
		string userPrompt, 
		string model,
		JsonSchemaObject? jsonSchema = null,
		double temperature = 0.0) where TResponse : class
	{
		var payload = BuildChatPayload(model, systemPrompt, userPrompt, temperature, jsonSchema);
		var response = await GetResponseAsync(payload).ConfigureAwait(false);

		return ExtractStructuredResponse<TResponse>(response);
	}

	public async Task<TResponse?> RunToolConversationAsync<TResponse>(
		string systemPrompt,
		string userPrompt,
		IReadOnlyCollection<ChatToolDefinition> tools,
		Func<ChatToolCall, Task<string>> toolHandler,
		string model,
		JsonSchemaObject? jsonSchema = null,
		double temperature = 0.0,
		int maxIterations = 10) where TResponse : class
	{
		ArgumentNullException.ThrowIfNull(tools);
		ArgumentNullException.ThrowIfNull(toolHandler);

		var messages = new List<ChatMessage>
		{
			new("system", systemPrompt),
			new("user", userPrompt)
		};

		var responseFormat = jsonSchema != null ? new JsonResponseFormat("json_schema", jsonSchema) : null;

		for (var iteration = 0; iteration < maxIterations; iteration++)
		{
			var payload = new ChatPayload(
				model,
				messages.ToArray(),
				temperature,
				responseFormat,
				tools.ToArray(),
				"auto");

			var response = await GetResponseAsync(payload).ConfigureAwait(false);
			var assistantMessage = response?.Choices?.FirstOrDefault()?.Message;

			if (assistantMessage == null)
			{
				throw new InvalidOperationException("OpenRouter did not return an assistant message.");
			}

			messages.Add(new ChatMessage("assistant", assistantMessage.Content, assistantMessage.ToolCalls));

			if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
			{
				return DeserializeAssistantContent<TResponse>(assistantMessage.Content);
			}

			foreach (var toolCall in assistantMessage.ToolCalls)
			{
				var toolResult = await toolHandler(toolCall).ConfigureAwait(false);
				messages.Add(new ChatMessage("tool", toolResult, ToolCallId: toolCall.Id));
			}
		}

		throw new InvalidOperationException($"Tool conversation exceeded the maximum number of iterations: {maxIterations}.");
	}

	public async Task<string?> AnalyzeImageAsync(
		string prompt,
		string imageBase64,
		string mimeType,
		string model,
		double temperature = 0.0,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
		ArgumentException.ThrowIfNullOrWhiteSpace(imageBase64);
		ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
		ArgumentException.ThrowIfNullOrWhiteSpace(model);

		var payload = new
		{
			model,
			temperature,
			messages = new object[]
			{
				new
				{
					role = "user",
					content = new object[]
					{
						new { type = "text", text = prompt },
						new
						{
							type = "image_url",
							image_url = new
							{
								url = $"data:{mimeType};base64,{imageBase64}"
							}
						}
					}
				}
			}
		};

		var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
		_logger.LogInformation("AnalyzeImageAsync payload: {Payload}", json);
		var responseText = await SendCompletionsRequest(json, cancellationToken).ConfigureAwait(false);
		_logger.LogInformation("AnalyzeImageAsync response: {Response}", responseText);

		VisionResponse? responseObj;
		try
		{
			responseObj = JsonSerializer.Deserialize<VisionResponse>(responseText, _deserializeOptions);
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to deserialize vision response");
			return null;
		}
		
		var responseContent = responseObj?.Choices?.FirstOrDefault()?.Message?.Content;
		if (responseContent is string strContent)
		{
			return strContent;
		}
		else if (responseContent is JsonElement jsonContent && jsonContent.ValueKind == JsonValueKind.Array)
		{
			var builder = new StringBuilder();
			foreach (var item in jsonContent.EnumerateArray())
			{
				if (item.TryGetProperty("text", out var textElement))
				{
					builder.Append(textElement.GetString());
				}
			}
			return builder.ToString();
		}

		return null;
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

	private TResponse? ExtractStructuredResponse<TResponse>(ChatResponse? response) where TResponse : class
	{
		try
		{
			var assistantContent = response?.Choices?.FirstOrDefault()?.Message?.Content ?? response?.Choices?.FirstOrDefault()?.Content;

			if (!string.IsNullOrWhiteSpace(assistantContent))
			{
				var parsed = DeserializeAssistantContent<TResponse>(assistantContent);
				if (parsed != null)
				{
					return parsed;
				}
			}
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to parse OpenRouter response envelope");
		}

		return default;
	}

	private TResponse? DeserializeAssistantContent<TResponse>(string? assistantContent) where TResponse : class
	{
		if (string.IsNullOrWhiteSpace(assistantContent))
		{
			return default;
		}

		var normalizedContent = assistantContent;
		if (normalizedContent.Length > 0 && normalizedContent[0] == '"')
		{
			try
			{
				normalizedContent = JsonSerializer.Deserialize<string>(normalizedContent, _deserializeOptions) ?? normalizedContent;
			}
			catch
			{
				// Ignore and use the original content.
			}
		}

		try
		{
			_logger.LogInformation("Deserializing assistant content into target type {Type}: {Content}", typeof(TResponse).Name, normalizedContent);
			return JsonSerializer.Deserialize<TResponse>(normalizedContent, _deserializeOptions);
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to deserialize assistant content into target type");
			return default;
		}
	}

	private void LogChatPayloadCompact(ChatPayload payload)
	{
		try
		{
			var compact = new
			{
				model = payload.Model,
				temperature = payload.Temperature,
				messages = payload.Messages?.Select(m => new
				{
					role = m.Role,
					snippet = string.IsNullOrEmpty(m.Content) ? string.Empty : (m.Content.Length > 120 ? m.Content[..120] + "…" : m.Content)
				}).ToArray(),
				tools = payload.Tools?.Select(t => t.Function?.Name).ToArray(),
				tool_choice = payload.ToolChoice
			};

			var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
			var compactJson = JsonSerializer.Serialize(compact, opts);
			_logger.LogInformation("OpenRouter payload (compact): {Payload}", compactJson);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to format OpenRouter payload for logging");
		}
	}

	public void Dispose()
	{
		if (_disposed) 
			return;
		_httpClient.Dispose();
		_disposed = true;
	}
}

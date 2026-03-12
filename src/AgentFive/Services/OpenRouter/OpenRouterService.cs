using System.Text.Json;
using System.Text;
using AgentFive.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentFive.Services.OpenRouter;

public class OpenRouterService : IDisposable
{
	private const string DefaultModel = "gpt-4o-mini";

	private readonly HttpClient _httpClient;
	private readonly AppSettings _settings;
    private readonly ILogger _logger;
	private readonly JsonSerializerOptions _deserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private bool _disposed;

	public OpenRouterService(AppSettings settings, ILogger logger)
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

	public async Task<TResponse?> GetStructuredResponseAsync<TResponse>(
		string systemPrompt, 
		string userPrompt, 
		JsonSchemaObject? jsonSchema = null, 
		string model = DefaultModel, 
		double temperature = 0.0) where TResponse : class
	{
		var payload = BuildChatPayload(model, systemPrompt, userPrompt, temperature, jsonSchema);
		var respText = await SendRequestAsync(payload).ConfigureAwait(false);

		return DeserializeResponse<TResponse>(respText);
	}

	public async Task<TResponse?> RunToolConversationAsync<TResponse>(
		string systemPrompt,
		string userPrompt,
		IReadOnlyCollection<ChatToolDefinition> tools,
		Func<ChatToolCall, Task<string>> toolHandler,
		JsonSchemaObject? jsonSchema = null,
		string model = DefaultModel,
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

			var respText = await SendRequestAsync(payload).ConfigureAwait(false);
			var response = JsonSerializer.Deserialize<ChatResponse>(respText, _deserializeOptions);
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

	private TResponse? DeserializeResponse<TResponse>(string respText) where TResponse : class
	{
		try
		{
			var response = JsonSerializer.Deserialize<ChatResponse>(respText, _deserializeOptions);
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

		try
		{
			var direct = JsonSerializer.Deserialize<TResponse>(respText, _deserializeOptions);
			if (direct != null)
			{
				return direct;
			}
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to deserialize OpenRouter response directly into target type");
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
			return JsonSerializer.Deserialize<TResponse>(normalizedContent, _deserializeOptions);
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to deserialize assistant content into target type");
			return default;
		}
	}

	private async Task<string> SendRequestAsync(ChatPayload payload)
	{
		var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
		_logger.LogInformation("Sending request to OpenRouter: {Payload}", json);

		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var resp = await _httpClient.PostAsync("v1/chat/completions", content).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();

		var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
		_logger.LogInformation("Received response from OpenRouter: {Response}", respText);

		return respText;
	}

	public void Dispose()
	{
		if (_disposed) 
			return;
		_httpClient.Dispose();
		_disposed = true;
	}
}

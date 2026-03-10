using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace AgentFive.Services.OpenRouter;

public class OpenRouterService : IDisposable
{
	private readonly HttpClient _http;
	private readonly string _apiKey;
	private bool _disposed;

	public OpenRouterService(string apiKey)
	{
		_apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
		_http = new HttpClient
		{
			BaseAddress = new Uri("https://openrouter.ai/api/")
		};
		_http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
	}

	public async Task<string> SendChatCompletionAsync(object payload)
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

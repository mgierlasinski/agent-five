using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentFive.Services.OpenRouter;
using AgentFive.Tasks.SendIt.Models;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.SendIt.Tools;

public class SpkToolHandler
{
	private const string DocumentationRoot = "https://hub.ag3nts.org/dane/doc/";

	private readonly OpenRouterService _openRouter;
	private readonly DeclarationService _declarationService;
	private readonly SpkClient _spkClient;
	private readonly DeclarationRequest _shipment;
	private readonly ILogger _logger;
	private readonly HttpClient _httpClient;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};
	private readonly string _cacheDirectory;

	public SpkToolHandler(
		OpenRouterService openRouter,
		DeclarationService declarationService,
		SpkClient spkClient,
		DeclarationRequest shipment,
		ILogger logger)
	{
		_openRouter = openRouter;
		_declarationService = declarationService;
		_spkClient = spkClient;
		_shipment = shipment;
		_logger = logger;
		_httpClient = new HttpClient();
		_cacheDirectory = Path.Combine(AppContext.BaseDirectory, "Tasks", "SendIt", "Cache");
		Directory.CreateDirectory(_cacheDirectory);
	}

	public async Task<string> HandleToolCallAsync(ChatToolCall toolCall)
	{
		try
		{
			return toolCall.Function.Name switch
			{
				"fetch_text_content" => await HandleFetchTextContentAsync(toolCall.Function.Arguments).ConfigureAwait(false),
				"fetch_image_content" => await HandleFetchImageContentAsync(toolCall.Function.Arguments).ConfigureAwait(false),
				"analyze_image_with_vision" => await HandleAnalyzeImageWithVisionAsync(toolCall.Function.Arguments).ConfigureAwait(false),
				"find_route_code" => await HandleFindRouteCodeAsync(toolCall.Function.Arguments).ConfigureAwait(false),
				"submit_declaration" => await HandleSubmitDeclarationAsync(toolCall.Function.Arguments).ConfigureAwait(false),
				_ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolCall.Function.Name}" }, _jsonOptions)
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "SendIt tool call failed: {ToolName}", toolCall.Function.Name);
			return JsonSerializer.Serialize(new { error = ex.Message, tool = toolCall.Function.Name }, _jsonOptions);
		}
	}

	private async Task<string> HandleFetchTextContentAsync(string argumentsJson)
	{
		var args = DeserializeArguments<UrlArgs>(argumentsJson, "fetch_text_content");
		var resolvedUrl = ResolveUrl(args.Url);
		var result = await GetTextContentAsync(resolvedUrl).ConfigureAwait(false);
		return JsonSerializer.Serialize(new
		{
			url = resolvedUrl,
			cachePath = result.CachePath,
			content = result.Content
		}, _jsonOptions);
	}

	private async Task<string> HandleFetchImageContentAsync(string argumentsJson)
	{
		var args = DeserializeArguments<UrlArgs>(argumentsJson, "fetch_image_content");
		var resolvedUrl = ResolveUrl(args.Url);
		var result = await GetImageContentAsync(resolvedUrl).ConfigureAwait(false);
		return JsonSerializer.Serialize(result, _jsonOptions);
	}

	private async Task<string> HandleAnalyzeImageWithVisionAsync(string argumentsJson)
	{
		var args = DeserializeArguments<VisionArgs>(argumentsJson, "analyze_image_with_vision");
		var mimeType = string.IsNullOrWhiteSpace(args.MimeType) ? "image/png" : args.MimeType;
		var analysis = await _openRouter.AnalyzeImageAsync(args.Prompt, args.ImageBase64, mimeType, OpenRouterModels.Gpt4oMini).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(analysis))
		{
			throw new InvalidOperationException("Vision analysis returned an empty response.");
		}

		return JsonSerializer.Serialize(new { analysis }, _jsonOptions);
	}

	private async Task<string> HandleFindRouteCodeAsync(string argumentsJson)
	{
		var args = DeserializeArguments<RouteArgs>(argumentsJson, "find_route_code");
		var route = await FindRouteCodeAsync(args.Origin, args.Destination).ConfigureAwait(false);
		return JsonSerializer.Serialize(route, _jsonOptions);
	}

	private async Task<string> HandleSubmitDeclarationAsync(string argumentsJson)
	{
		var args = DeserializeArguments<SubmitArgs>(argumentsJson, "submit_declaration");
		var normalizedDeclaration = _declarationService.ValidateAndNormalizeForSubmission(args.DeclarationString, _shipment);
		var response = await _spkClient.VerifyDeclarationAsync(normalizedDeclaration).ConfigureAwait(false);
		return JsonSerializer.Serialize(response, _jsonOptions);
	}

	private async Task<RouteResolution> FindRouteCodeAsync(string origin, string destination)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(origin);
		ArgumentException.ThrowIfNullOrWhiteSpace(destination);

		var indexContent = await GetTextContentAsync(ResolveUrl("index.md")).ConfigureAwait(false);
		var routeSegments = ParseActiveRouteSegments(indexContent.Content);
		var directSegment = routeSegments.FirstOrDefault(segment => MatchesEndpoints(segment, origin, destination));

		if (directSegment != null)
		{
			return new RouteResolution(
				directSegment.Code,
				directSegment.DistanceKm,
				0,
				false,
				"Active documented route.",
				"index.md");
		}

		if (origin.Equals("Gdańsk", StringComparison.OrdinalIgnoreCase) && destination.Equals("Żarnowiec", StringComparison.OrdinalIgnoreCase))
		{
			return await ResolveZarnowiecRouteAsync(origin, destination).ConfigureAwait(false);
		}

		throw new InvalidOperationException($"No direct route code found for {origin} -> {destination}.");
	}

	private async Task<RouteResolution> ResolveZarnowiecRouteAsync(string origin, string destination)
	{
		var image = await GetImageContentAsync(ResolveUrl("trasy-wylaczone.png")).ConfigureAwait(false);
		var prompt = $"Identify the excluded route code connecting {origin} and {destination} on this SPK network image. Return strict JSON with properties routeCode (string), distanceKm (integer), confidence (string), and evidence (string). If the exact code is unreadable, infer the most likely X-## code from visible labels and say so in evidence.";
		var rawAnalysis = await _openRouter.AnalyzeImageAsync(prompt, image.ImageBase64, image.MimeType, OpenRouterModels.Gpt4oMini).ConfigureAwait(false);

		if (string.IsNullOrWhiteSpace(rawAnalysis))
		{
			throw new InvalidOperationException("Unable to resolve Żarnowiec route code from the excluded-route image.");
		}

		var parsed = TryDeserialize<ExcludedRouteResult>(rawAnalysis)
			?? TryDeserialize<ExcludedRouteResult>(ExtractJsonBlock(rawAnalysis));

		if (parsed == null || string.IsNullOrWhiteSpace(parsed.RouteCode))
		{
			throw new InvalidOperationException($"Vision analysis did not produce a route code. Raw response: {rawAnalysis}");
		}

		return new RouteResolution(
			parsed.RouteCode,
			parsed.DistanceKm,
			0,
			true,
			"Żarnowiec routes are excluded from regular use and may be used only for category A or B shipments.",
			$"trasy-wylaczone.png ({parsed.Confidence})");
	}

	private async Task<TextContentResult> GetTextContentAsync(string url)
	{
		var cachePath = GetCachePath(url, ".txt");
		if (File.Exists(cachePath))
		{
			return new TextContentResult(await File.ReadAllTextAsync(cachePath).ConfigureAwait(false), cachePath);
		}

		_logger.LogInformation("Downloading text documentation from {Url}", url);
		using var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
		var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await File.WriteAllTextAsync(cachePath, content).ConfigureAwait(false);
		return new TextContentResult(content, cachePath);
	}

	private async Task<ImageContentResult> GetImageContentAsync(string url)
	{
		var extension = Path.GetExtension(new Uri(url).AbsolutePath);
		if (string.IsNullOrWhiteSpace(extension))
		{
			extension = ".img";
		}

		var cachePath = GetCachePath(url, extension);
		byte[] bytes;
		string mimeType;

		if (File.Exists(cachePath))
		{
			bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
			mimeType = GetMimeTypeFromExtension(extension);
		}
		else
		{
			_logger.LogInformation("Downloading image documentation from {Url}", url);
			using var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
			bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
			mimeType = response.Content.Headers.ContentType?.MediaType ?? GetMimeTypeFromExtension(extension);
		}

		return new ImageContentResult(url, cachePath, mimeType, Convert.ToBase64String(bytes));
	}

	private static List<RouteSegment> ParseActiveRouteSegments(string content)
	{
		var matches = Regex.Matches(content, @"\|\s*(?<code>[MRL]-\d{2})\s*\|\s*(?<origin>[^|\n]+?)\s*-\s*(?<destination>[^|\n]+?)\s*\|\s*(?<distance>\d+)\s*\|", RegexOptions.CultureInvariant);
		var results = new List<RouteSegment>();
		foreach (Match match in matches)
		{
			if (!match.Success)
			{
				continue;
			}

			results.Add(new RouteSegment(
				match.Groups["code"].Value.Trim(),
				match.Groups["origin"].Value.Trim(),
				match.Groups["destination"].Value.Trim(),
				int.Parse(match.Groups["distance"].Value, System.Globalization.CultureInfo.InvariantCulture)));
		}

		return results;
	}

	private static bool MatchesEndpoints(RouteSegment segment, string origin, string destination)
	{
		return (segment.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase) && segment.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase))
			|| (segment.Origin.Equals(destination, StringComparison.OrdinalIgnoreCase) && segment.Destination.Equals(origin, StringComparison.OrdinalIgnoreCase));
	}

	private string ResolveUrl(string url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
		{
			return absolute.ToString();
		}

		return new Uri(new Uri(DocumentationRoot), url.TrimStart('/')).ToString();
	}

	private string GetCachePath(string url, string extension)
	{
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
		return Path.Combine(_cacheDirectory, hash + extension);
	}

	private static string GetMimeTypeFromExtension(string extension)
	{
		return extension.ToLowerInvariant() switch
		{
			".png" => "image/png",
			".jpg" or ".jpeg" => "image/jpeg",
			".gif" => "image/gif",
			_ => MediaTypeNames.Application.Octet
		};
	}

	private TArgs DeserializeArguments<TArgs>(string argumentsJson, string toolName)
	{
		var args = JsonSerializer.Deserialize<TArgs>(argumentsJson, _jsonOptions);
		if (args == null)
		{
			throw new InvalidOperationException($"Unable to deserialize arguments for {toolName}: {argumentsJson}");
		}

		return args;
	}

	private static T? TryDeserialize<T>(string? json) where T : class
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
		}
		catch
		{
			return null;
		}
	}

	private static string ExtractJsonBlock(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}

		var start = value.IndexOf('{');
		var end = value.LastIndexOf('}');
		return start >= 0 && end > start ? value[start..(end + 1)] : value;
	}

	private sealed record UrlArgs(string Url);
	private sealed record VisionArgs(string ImageBase64, string Prompt, string? MimeType);
	private sealed record RouteArgs(string Origin, string Destination);
	private sealed record SubmitArgs(string DeclarationString);
	private sealed record TextContentResult(string Content, string CachePath);
	private sealed record ImageContentResult(string Url, string CachePath, string MimeType, string ImageBase64);
	private sealed record RouteSegment(string Code, string Origin, string Destination, int DistanceKm);
	private sealed record ExcludedRouteResult(string RouteCode, int DistanceKm, string Confidence, string Evidence);
}
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFive.Configuration;
using AgentFive.Services.OpenRouter;
using AgentFive.Tasks.People;
using AgentFive.Utils;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.FindHim;

public class FindHimTask
{
	private const int MaxAgentIterations = 12;

	private readonly HubSettings _hubSettings;
	private readonly OpenRouterSettings _openRouterSettings;
	private readonly ILogger _logger;
	private readonly OpenRouterService _openRouter;
	private readonly HubClient _hubClient;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public FindHimTask(HubSettings hubSettings, OpenRouterSettings openRouterSettings, ILogger logger)
	{
		_hubSettings = hubSettings;
		_openRouterSettings = openRouterSettings;
		_logger = logger;
		_openRouter = new OpenRouterService(openRouterSettings, logger);
		_hubClient = new HubClient(hubSettings, logger);
	}

	public async Task RunAsync(string suspectsPath)
	{
		try
		{
			var suspects = LoadSuspects(suspectsPath);
			var powerPlants = await GetPowerPlantsAsync().ConfigureAwait(false);
			var result = await RunAgentAsync(suspects, powerPlants).ConfigureAwait(false);

			if (result == null)
			{
				throw new InvalidOperationException("FindHim agent did not return a final result.");
			}

			var payload = new VerifyRequest(
				_hubSettings.HubApiKey,
				"findhim",
				new VerifyAnswer(result.Name, result.Surname, result.AccessLevel, result.PowerPlant));

			await VerifyAsync(payload).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "FindHim task failed");
		}
		finally
		{
			_hubClient.Dispose();
			_openRouter.Dispose();
		}
	}

	private List<TaggedPerson> LoadSuspects(string suspectsPath)
	{
		if (!File.Exists(suspectsPath))
		{
			throw new FileNotFoundException($"Missing people transport file: {suspectsPath}", suspectsPath);
		}

		var json = File.ReadAllText(suspectsPath);
		var suspects = JsonSerializer.Deserialize<List<TaggedPerson>>(json, _jsonOptions);

		if (suspects == null || suspects.Count == 0)
		{
			throw new InvalidOperationException($"Missing or invalid suspects file: {suspectsPath}");
		}

		_logger.LogInformation("Loaded {Count} suspects from {Path}", suspects.Count, suspectsPath);
		return suspects;
	}

	private async Task<List<PowerPlantDefinition>> GetPowerPlantsAsync()
	{
		var powerPlants = await _hubClient.GetPowerPlantsAsync().ConfigureAwait(false);
		if (powerPlants.Any(x => !x.Latitude.HasValue || !x.Longitude.HasValue))
		{
			powerPlants = await EnrichPowerPlantsWithCoordinatesAsync(powerPlants).ConfigureAwait(false);
		}

		return powerPlants;
	}

	private async Task<FindHimAgentResult?> RunAgentAsync(IReadOnlyCollection<TaggedPerson> suspects, IReadOnlyCollection<PowerPlantDefinition> powerPlants)
	{
		var systemPrompt = BuildSystemPrompt();
		var userPrompt = JsonSerializer.Serialize(new
		{
			suspectCount = suspects.Count,
			powerPlantCount = powerPlants.Count,
			instructions = "Use tools to inspect every suspect, compare each location against the nearest power plant, choose the globally closest case, then fetch access level for the winning suspect and return the final JSON only."
		}, _jsonOptions);

		var tools = BuildTools();
		var schema = new JsonSchemaObject(
			"findhim_result",
			true,
			new
			{
				type = "object",
				properties = new
				{
					name = new { type = "string" },
					surname = new { type = "string" },
					accessLevel = new { type = "integer" },
					powerPlant = new { type = "string" },
					powerPlantName = new { type = "string" },
					distanceKm = new { type = "number" },
					reasoning = new { type = "string" }
				},
				required = new[] { "name", "surname", "accessLevel", "powerPlant", "powerPlantName", "distanceKm", "reasoning" },
				additionalProperties = false
			});

		return await _openRouter.RunToolConversationAsync<FindHimAgentResult>(
			systemPrompt,
			userPrompt,
			tools,
			toolCall => HandleToolCallAsync(toolCall, suspects, powerPlants),
			OpenRouterModels.Gpt41Mini,
			schema,
			0.0,
			MaxAgentIterations).ConfigureAwait(false);
	}

	private string BuildSystemPrompt()
	{
		var builder = new StringBuilder();
		builder.AppendLine("You are an autonomous investigation agent solving the task 'findhim'.");
		builder.AppendLine("You must work strictly through tool calls and return the final result as JSON matching the schema.");
		builder.AppendLine("Rules:");
		builder.AppendLine("1. First inspect the suspect list and the power plant catalog.");
		builder.AppendLine("2. For each suspect, call evaluate_suspect exactly once.");
		builder.AppendLine("3. The evaluate_suspect tool already checks all locations and returns the best match for that suspect.");
		builder.AppendLine("4. Select the globally closest suspect-location-power-plant match across the whole dataset.");
		builder.AppendLine("5. Only after selecting the winning suspect, fetch that suspect's access level.");
		builder.AppendLine("6. Return only the final JSON object. Do not include markdown.");
		builder.AppendLine("7. If a tool returns an error payload, adapt and continue without retry loops.");
		builder.AppendLine("8. Minimize duplicate tool calls.");
		return builder.ToString();
	}

	private List<ChatToolDefinition> BuildTools()
	{
		return new List<ChatToolDefinition>
		{
			new(
				"function",
				new ChatFunctionDefinition(
					"get_suspects",
					"Returns the list of suspects loaded from people_transport.json.",
					new
					{
						type = "object",
						properties = new { },
						required = Array.Empty<string>(),
						additionalProperties = false
					})),
			new(
				"function",
				new ChatFunctionDefinition(
					"get_power_plants",
					"Returns the power plant catalog with names, codes and optional coordinates.",
					new
					{
						type = "object",
						properties = new { },
						required = Array.Empty<string>(),
						additionalProperties = false
					})),
			new(
				"function",
				new ChatFunctionDefinition(
					"evaluate_suspect",
					"Fetches all observed coordinates for a suspect, compares them against the power plant catalog, and returns the best match for that suspect.",
					new
					{
						type = "object",
						properties = new
						{
							name = new { type = "string" },
							surname = new { type = "string" }
						},
						required = new[] { "name", "surname" },
						additionalProperties = false
					})),
			new(
				"function",
				new ChatFunctionDefinition(
					"get_access_level",
					"Fetches the suspect's access level from /api/accesslevel.",
					new
					{
						type = "object",
						properties = new
						{
							name = new { type = "string" },
							surname = new { type = "string" },
							birthYear = new { type = "integer" }
						},
						required = new[] { "name", "surname", "birthYear" },
						additionalProperties = false
					}))
		};
	}

	private async Task<string> HandleToolCallAsync(ChatToolCall toolCall, IReadOnlyCollection<TaggedPerson> suspects, IReadOnlyCollection<PowerPlantDefinition> powerPlants)
	{
		try
		{
			return toolCall.Function.Name switch
			{
				"get_suspects" => JsonSerializer.Serialize(suspects, _jsonOptions),
				"get_power_plants" => JsonSerializer.Serialize(powerPlants, _jsonOptions),
				"evaluate_suspect" => await HandleEvaluateSuspectAsync(toolCall.Function.Arguments, suspects, powerPlants).ConfigureAwait(false),
				"get_access_level" => await HandleGetAccessLevelAsync(toolCall.Function.Arguments).ConfigureAwait(false),
				_ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolCall.Function.Name}" }, _jsonOptions)
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Tool call failed: {ToolName}", toolCall.Function.Name);
			return JsonSerializer.Serialize(new { error = ex.Message, tool = toolCall.Function.Name }, _jsonOptions);
		}
	}

	private async Task<string> HandleEvaluateSuspectAsync(string argumentsJson, IReadOnlyCollection<TaggedPerson> suspects, IReadOnlyCollection<PowerPlantDefinition> powerPlants)
	{
		var args = DeserializeToolArguments<PersonLocationArgs>(argumentsJson, "evaluate_suspect");
		var suspect = suspects.FirstOrDefault(x =>
			string.Equals(x.Name, args.Name, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(x.Surname, args.Surname, StringComparison.OrdinalIgnoreCase));

		if (suspect == null)
		{
			throw new InvalidOperationException($"Unknown suspect: {args.Name} {args.Surname}");
		}

        var locations = await _hubClient.GetPersonLocationsAsync(args.Name, args.Surname).ConfigureAwait(false);
		if (locations == null || locations.Count == 0)
		{
			return JsonSerializer.Serialize(new
			{
				name = suspect.Name,
				surname = suspect.Surname,
				born = suspect.Born,
				observations = 0,
				error = "No locations returned for suspect."
			}, _jsonOptions);
		}

		var matches = new List<MatchedObservation>();
		foreach (var location in locations)
		{
			var matched = powerPlants.All(x => x.Latitude.HasValue && x.Longitude.HasValue)
				? FindNearestByDistance(location.Latitude, location.Longitude, powerPlants)
				: await FindNearestWithAiAsync(location.Latitude, location.Longitude, powerPlants).ConfigureAwait(false);

			matches.Add(new MatchedObservation(location.Latitude, location.Longitude, matched.PowerPlantName, matched.PowerPlantCode, matched.DistanceKm, matched.Reasoning));
		}

		var bestMatch = matches.OrderBy(x => x.DistanceKm).First();
		return JsonSerializer.Serialize(new
		{
			name = suspect.Name,
			surname = suspect.Surname,
			born = suspect.Born,
			observations = locations.Count,
			bestMatch
		}, _jsonOptions);
	}

	private async Task<string> HandleGetAccessLevelAsync(string argumentsJson)
	{
		var args = DeserializeToolArguments<AccessLevelArgs>(argumentsJson, "get_access_level");
		var accessLevel = await _hubClient.GetAccessLevelAsync(args.Name, args.Surname, args.BirthYear).ConfigureAwait(false);
		return JsonSerializer.Serialize(accessLevel, _jsonOptions);
	}

	private TArgs DeserializeToolArguments<TArgs>(string argumentsJson, string toolName)
	{
		var args = JsonSerializer.Deserialize<TArgs>(argumentsJson, _jsonOptions);
		if (args == null)
		{
			throw new InvalidOperationException($"Failed to deserialize arguments for {toolName}: {argumentsJson}");
		}

		return args;
	}

	private MatchedPowerPlant FindNearestByDistance(double latitude, double longitude, IReadOnlyCollection<PowerPlantDefinition> powerPlants)
	{
		var nearest = powerPlants
			.Select(powerPlant => new MatchedPowerPlant(
				powerPlant.Name,
				powerPlant.Code,
				Haversine(latitude, longitude, powerPlant.Latitude!.Value, powerPlant.Longitude!.Value),
				"Computed directly from power plant coordinates using the Haversine formula."))
			.OrderBy(x => x.DistanceKm)
			.First();

		return nearest;
	}

	private async Task<MatchedPowerPlant> FindNearestWithAiAsync(double latitude, double longitude, IReadOnlyCollection<PowerPlantDefinition> powerPlants)
	{
		var schema = new JsonSchemaObject(
			"power_plant_match",
			true,
			new
			{
				type = "object",
				properties = new
				{
					powerPlantName = new { type = "string" },
					powerPlantCode = new { type = "string" },
					distanceKm = new { type = "number" },
					reasoning = new { type = "string" }
				},
				required = new[] { "powerPlantName", "powerPlantCode", "distanceKm", "reasoning" },
				additionalProperties = false
			});

		var systemPrompt = "Map a single observed coordinate to the closest nuclear power plant from the provided list. Use your geographic knowledge. You must choose only from the provided plants and estimate the air distance in kilometers. Return JSON only.";
		var userPrompt = JsonSerializer.Serialize(new
		{
			coordinate = new { latitude, longitude },
			powerPlants = powerPlants.Select(x => new { x.Name, x.Code })
		}, _jsonOptions);

		var response = await _openRouter.GetStructuredResponseAsync<PowerPlantAiMatch>(
			systemPrompt,
			userPrompt,
			OpenRouterModels.Gpt41Mini,
			schema,
			0.0).ConfigureAwait(false);

		if (response == null)
		{
			throw new InvalidOperationException("AI power plant matching returned no result.");
		}

		return new MatchedPowerPlant(response.PowerPlantName, response.PowerPlantCode, response.DistanceKm, response.Reasoning);
	}

	private async Task<List<PowerPlantDefinition>> EnrichPowerPlantsWithCoordinatesAsync(IReadOnlyCollection<PowerPlantDefinition> powerPlants)
	{
		var schema = new JsonSchemaObject(
			"power_plant_coordinates",
			true,
			new
			{
				type = "object",
				properties = new
				{
					results = new
					{
						type = "array",
						items = new
						{
							type = "object",
							properties = new
							{
								name = new { type = "string" },
								code = new { type = "string" },
								latitude = new { type = "number" },
								longitude = new { type = "number" }
							},
							required = new[] { "name", "code", "latitude", "longitude" },
							additionalProperties = false
						}
					}
				},
				required = new[] { "results" },
				additionalProperties = false
			});

		var systemPrompt = "Return approximate city-center coordinates for each Polish location from the provided list. Use the best geographic coordinates for the city or town name in Poland. Return JSON only.";
		var userPrompt = JsonSerializer.Serialize(new
		{
			powerPlants = powerPlants.Select(x => new { x.Name, x.Code })
		}, _jsonOptions);

		var response = await _openRouter.GetStructuredResponseAsync<PowerPlantCoordinateResponse>(
			systemPrompt,
			userPrompt,
			OpenRouterModels.Gpt41Mini,
			schema,
			0.0).ConfigureAwait(false);

		if (response?.Results == null || response.Results.Count == 0)
		{
			throw new InvalidOperationException("Failed to enrich power plants with coordinates.");
		}

		var lookup = response.Results.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
		var enriched = powerPlants
			.Select(powerPlant => lookup.TryGetValue(powerPlant.Code, out var matched)
				? powerPlant with { Latitude = matched.Latitude, Longitude = matched.Longitude }
				: powerPlant)
			.ToList();

		_logger.LogInformation("Enriched power plant coordinates: {PowerPlants}", JsonSerializer.Serialize(enriched, _jsonOptions));
		return enriched;
	}

	private async Task VerifyAsync(VerifyRequest payload)
	{
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
		await FileHelper.WriteArtifactAsync("findhim", "verify_request.json", json).ConfigureAwait(false);

		var response = await _hubClient.VerifyAsync(payload).ConfigureAwait(false);
        json = JsonSerializer.Serialize(response, _jsonOptions);
		await FileHelper.WriteArtifactAsync("findhim", "verify_response.json", json).ConfigureAwait(false);
	}

	private static double Haversine(double latitude1, double longitude1, double latitude2, double longitude2)
	{
		const double earthRadiusKm = 6371.0;
		var deltaLatitude = DegreesToRadians(latitude2 - latitude1);
		var deltaLongitude = DegreesToRadians(longitude2 - longitude1);

		var a = Math.Pow(Math.Sin(deltaLatitude / 2), 2) +
				Math.Cos(DegreesToRadians(latitude1)) * Math.Cos(DegreesToRadians(latitude2)) *
				Math.Pow(Math.Sin(deltaLongitude / 2), 2);

		var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
		return earthRadiusKm * c;
	}

	private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}

public record MatchedPowerPlant(
	[property: JsonPropertyName("powerPlantName")] string PowerPlantName,
	[property: JsonPropertyName("powerPlantCode")] string PowerPlantCode,
	[property: JsonPropertyName("distanceKm")] double DistanceKm,
	[property: JsonPropertyName("reasoning")] string Reasoning);

public record FindHimAgentResult(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("surname")] string Surname,
	[property: JsonPropertyName("accessLevel")] int AccessLevel,
	[property: JsonPropertyName("powerPlant")] string PowerPlant,
	[property: JsonPropertyName("powerPlantName")] string PowerPlantName,
	[property: JsonPropertyName("distanceKm")] double DistanceKm,
	[property: JsonPropertyName("reasoning")] string Reasoning);

public record PersonLocationArgs(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("surname")] string Surname);

public record AccessLevelArgs(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("surname")] string Surname,
	[property: JsonPropertyName("birthYear")] int BirthYear);

public record PowerPlantAiMatch(
	[property: JsonPropertyName("powerPlantName")] string PowerPlantName,
	[property: JsonPropertyName("powerPlantCode")] string PowerPlantCode,
	[property: JsonPropertyName("distanceKm")] double DistanceKm,
	[property: JsonPropertyName("reasoning")] string Reasoning);

public record PowerPlantCoordinateResponse(
	[property: JsonPropertyName("results")] List<PowerPlantCoordinate>? Results);

public record PowerPlantCoordinate(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("code")] string Code,
	[property: JsonPropertyName("latitude")] double Latitude,
	[property: JsonPropertyName("longitude")] double Longitude);

public record MatchedObservation(
	[property: JsonPropertyName("latitude")] double Latitude,
	[property: JsonPropertyName("longitude")] double Longitude,
	[property: JsonPropertyName("powerPlantName")] string PowerPlantName,
	[property: JsonPropertyName("powerPlantCode")] string PowerPlantCode,
	[property: JsonPropertyName("distanceKm")] double DistanceKm,
	[property: JsonPropertyName("reasoning")] string Reasoning);

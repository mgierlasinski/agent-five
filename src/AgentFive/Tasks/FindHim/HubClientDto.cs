using System.Text.Json.Serialization;

namespace AgentFive.Tasks.FindHim;

public record PowerPlantDefinition(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("code")] string Code,
	[property: JsonPropertyName("latitude")] double? Latitude,
	[property: JsonPropertyName("longitude")] double? Longitude);

public record CoordinateArgs(
	[property: JsonPropertyName("latitude")] double Latitude,
	[property: JsonPropertyName("longitude")] double Longitude);
    
public record VerifyRequest(
	[property: JsonPropertyName("apikey")] string ApiKey,
	[property: JsonPropertyName("task")] string Task,
	[property: JsonPropertyName("answer")] VerifyAnswer Answer);

public record VerifyAnswer(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("surname")] string Surname,
	[property: JsonPropertyName("accessLevel")] int AccessLevel,
	[property: JsonPropertyName("powerPlant")] string PowerPlant);

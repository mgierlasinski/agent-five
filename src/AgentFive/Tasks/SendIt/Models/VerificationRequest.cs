using System.Text.Json.Serialization;

namespace AgentFive.Tasks.SendIt.Models;

public sealed record VerificationRequest(
	[property: JsonPropertyName("apikey")] string ApiKey,
	[property: JsonPropertyName("task")] string Task,
	[property: JsonPropertyName("answer")] VerificationAnswer Answer);

public sealed record VerificationAnswer(
	[property: JsonPropertyName("declaration")] string Declaration);
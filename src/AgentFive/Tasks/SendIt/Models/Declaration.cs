using System.Text.Json.Serialization;

namespace AgentFive.Tasks.SendIt.Models;

public sealed record DeclarationRequest(
	[property: JsonPropertyName("senderId")] string SenderId,
	[property: JsonPropertyName("origin")] string Origin,
	[property: JsonPropertyName("destination")] string Destination,
	[property: JsonPropertyName("weightKg")] decimal WeightKg,
	[property: JsonPropertyName("budgetPp")] decimal BudgetPp,
	[property: JsonPropertyName("contentDescription")] string ContentDescription,
	[property: JsonPropertyName("specialNotes")] string SpecialNotes);

public sealed record RouteResolution(
	[property: JsonPropertyName("routeCode")] string RouteCode,
	[property: JsonPropertyName("totalDistanceKm")] int TotalDistanceKm,
	[property: JsonPropertyName("regionalBoundariesCrossed")] int RegionalBoundariesCrossed,
	[property: JsonPropertyName("isExcluded")] bool IsExcluded,
	[property: JsonPropertyName("restrictionSummary")] string RestrictionSummary,
	[property: JsonPropertyName("source")] string Source);

public sealed record TransportDeclaration(
	[property: JsonPropertyName("date")] DateOnly Date,
	[property: JsonPropertyName("origin")] string Origin,
	[property: JsonPropertyName("senderId")] string SenderId,
	[property: JsonPropertyName("destination")] string Destination,
	[property: JsonPropertyName("routeCode")] string RouteCode,
	[property: JsonPropertyName("category")] string Category,
	[property: JsonPropertyName("contentDescription")] string ContentDescription,
	[property: JsonPropertyName("weightKg")] decimal WeightKg,
	[property: JsonPropertyName("additionalWagons")] int AdditionalWagons,
	[property: JsonPropertyName("specialNotes")] string SpecialNotes,
	[property: JsonPropertyName("amountDuePp")] decimal AmountDuePp);

public sealed record SendItAgentResult(
	[property: JsonPropertyName("declaration")] string Declaration,
	[property: JsonPropertyName("category")] string Category,
	[property: JsonPropertyName("routeCode")] string RouteCode,
	[property: JsonPropertyName("amountDuePp")] decimal AmountDuePp,
	[property: JsonPropertyName("additionalWagons")] int AdditionalWagons,
	[property: JsonPropertyName("submitted")] bool Submitted,
	[property: JsonPropertyName("verificationResponse")] string VerificationResponse,
	[property: JsonPropertyName("summary")] string Summary);
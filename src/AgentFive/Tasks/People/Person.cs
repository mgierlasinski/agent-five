using System.Text.Json.Serialization;

namespace AgentFive.Tasks.People;

public record Person(
	[property: JsonPropertyName("firstName")] string FirstName,
	[property: JsonPropertyName("lastName")] string LastName,
	[property: JsonPropertyName("gender")] string Gender,
	[property: JsonPropertyName("dateOfBirth")] DateTime DateOfBirth,
	[property: JsonPropertyName("city")] string City,
	[property: JsonPropertyName("country")] string Country,
	[property: JsonPropertyName("description")] string Description);

public record TaggedPerson(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("surname")] string Surname,
	[property: JsonPropertyName("gender")] string Gender,
	[property: JsonPropertyName("born")] int Born,
	[property: JsonPropertyName("city")] string City,
	[property: JsonPropertyName("tags")] List<string> Tags);

public record IndexTag(
	[property: JsonPropertyName("index")] int Index, 
	[property: JsonPropertyName("tags")] List<string> Tags);

public record IndexTagResponse([property: JsonPropertyName("results")] List<IndexTag>? Results);
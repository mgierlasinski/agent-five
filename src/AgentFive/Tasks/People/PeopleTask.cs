using System.Text.Json;
using AgentFive.Configuration;
using AgentFive.Services.OpenRouter;

namespace AgentFive.Tasks.People;

public class PeopleTask
{
	private DateTime Today => DateTime.Today;
	private readonly AppSettings? _settings;
	private readonly OpenRouterService? _openRouter;

	public PeopleTask(AppSettings settings)
	{
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_openRouter = new OpenRouterService(_settings);
	}

    public List<Person> GetMalesAged20To40FromGrudziadz(string filePath)
	{
		var people = CsvHelper.ParsePeopleFromCsv(filePath);
		var referenceDate = Today;
		var result = new List<Person>();

		foreach (var p in people)
		{
			if (p == null)
				continue;

			var gender = (p.Gender ?? string.Empty).Trim();
			if (string.IsNullOrEmpty(gender) || char.ToLowerInvariant(gender[0]) != 'm')
				continue;

			var city = (p.City ?? string.Empty).Trim();
			if (!string.Equals(city, "Grudziądz", StringComparison.OrdinalIgnoreCase))
				continue;

			var age = referenceDate.Year - p.DateOfBirth.Year;
			if (p.DateOfBirth > referenceDate.AddYears(-age))
				age--;

			if (age >= 20 && age <= 40)
				result.Add(p);
		}

		return result;
	}

	public async Task<List<TaggedPerson>> TagPeopleWithOpenRouterAsync(List<Person> people)
	{
		if (_openRouter == null)
			throw new InvalidOperationException("OpenRouterService not initialized. Use PeopleTask(AppSettings) constructor.");

		var allowedTags = new[] { "IT", "transport", "edukacja", "medycyna", "praca z ludźmi", "praca z pojazdami", "praca fizyczna" };
		var records = new List<object>();

		foreach (var p in people)
		{
			if (p == null) 
				continue;

			records.Add(new {
				name = p.FirstName,
				surname = p.LastName,
				gender = string.IsNullOrWhiteSpace(p.Gender) ? "" : p.Gender.Substring(0,1).ToUpperInvariant(),
				born = p.DateOfBirth.Year,
				city = p.City,
				description = p.Description
			});
		}

		var systemPrompt = $"Twoim zadaniem jest otagowanie zawodów na podstawie opisu stanowiska.\n" +
				"Masz do dyspozycji następujące tagi: " + string.Join(", ", allowedTags) + ".\n" +
				"Dla każdej osoby zwróć rekord JSON z polami: name, surname, gender, born, city, tags (lista tagów).\n" +
				"Zwróć wyłącznie listę rekordów w formacie JSON (żaden dodatkowy tekst).\n" +
				"Jeżeli opis sugeruje kilka tagów, przypisz wszystkie pasujące. Używaj tylko tagów z powyższej listy.";

		var userContent = JsonSerializer.Serialize(new { people = records }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

		var jsonSchema = new {
			name = "PeopleTagging",
			strict = true,
			schema = new {
				type = "object",
				properties = new {
					people = new {
						type = "array",
						items = new {
							type = "object",
							properties = new {
								name = new { type = "string" },
								surname = new { type = "string" },
								gender = new { type = "string" },
								born = new { type = "integer" },
								city = new { type = "string" },
								tags = new { type = "array", items = new { type = "string" } }
							},
							required = new[] { "name", "surname", "gender", "born", "city", "tags" },
							additionalProperties = false
						}
					}
				},
				required = new[] { "people" },
				additionalProperties = false
			}
		};

		var responseFormat = new ResponseFormat("json_schema", jsonSchema);

		var messages = new[] 
		{
			new ChatMessage("system", systemPrompt),
			new ChatMessage("user", userContent)
		};

		var payload = new ChatPayload("gpt-4o-mini", messages, 0.0, responseFormat);

		var wrapper = await _openRouter.SendChatCompletionAndParseAsync<TaggedPeopleResponse>(payload).ConfigureAwait(false);
		if (wrapper?.people != null && wrapper.people.Count > 0)
			return wrapper.people;

		return new List<TaggedPerson>();
	}
}

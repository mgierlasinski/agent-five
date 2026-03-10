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

	public async Task RunAsync()
	{
		try
		{
			var people = GetMalesAged20To40FromGrudziadz();
			var opts = new JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			};

			var tagged = await TagPeopleWithAIAsync(people);
			File.WriteAllText("tagged_people.json", JsonSerializer.Serialize(tagged, opts));

			var transportOnly = tagged.Where(x => x.tags.Contains("transport")).ToList();
			var transportJson = JsonSerializer.Serialize(transportOnly, opts);
			File.WriteAllText("transport_people.json", transportJson);

			Console.WriteLine("Tagged people:");
			Console.WriteLine(transportJson);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"OpenRouter tagging skipped or failed: {ex.Message}");
		}
	}

	private List<Person> GetMalesAged20To40FromGrudziadz()
	{
		var people = CsvHelper.ParsePeopleFromCsv("Tasks/People/people.csv");
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

	private async Task<List<TaggedPerson>> TagPeopleWithAIAsync(IEnumerable<Person> people)
	{
		if (_openRouter == null)
			throw new InvalidOperationException("OpenRouterService not initialized. Use PeopleTask(AppSettings) constructor.");

		var allowedTags = new[] { "IT", "transport", "edukacja", "medycyna", "praca z ludźmi", "praca z pojazdami", "praca fizyczna" };
		var records = new List<object>();

		foreach (var p in people)
		{
			if (p == null)
				continue;

			records.Add(new
			{
				name = p.FirstName,
				surname = p.LastName,
				gender = string.IsNullOrWhiteSpace(p.Gender) ? "" : p.Gender.Substring(0, 1).ToUpperInvariant(),
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

		var jsonSchema = new JsonSchemaObject(
			"PeopleTagging",
			true,
			new
			{
				type = "object",
				properties = new
				{
					people = new
					{
						type = "array",
						items = new
						{
							type = "object",
							properties = new
							{
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
		);

		var response = await _openRouter.GetStructuredResponseAsync<TaggedPeopleResponse>(systemPrompt, userContent, jsonSchema).ConfigureAwait(false);
		if (response?.people != null && response.people.Count > 0)
			return response.people;

		return new List<TaggedPerson>();
	}
}

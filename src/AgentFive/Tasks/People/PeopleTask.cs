using System.Text.Json;
using AgentFive.Configuration;
using AgentFive.Services.OpenRouter;
using Microsoft.Extensions.Logging;

namespace AgentFive.Tasks.People;

public class PeopleTask
{
	public const string PeopleCsv = "Tasks/People/people.csv";
    public const string PeopleTagged = "Artifacts/people_tagged.json";
    public const string PeopleTransport = "Artifacts/people_transport.json";

	private DateTime Today => DateTime.Today;
	private readonly ILogger _logger;
    private readonly OpenRouterService? _openRouter;

	public PeopleTask(OpenRouterSettings openRouterSettings, ILogger logger)
	{
        _logger = logger;
        _openRouter = new OpenRouterService(openRouterSettings, logger);
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
			File.WriteAllText(PeopleTagged, JsonSerializer.Serialize(tagged, opts));

			var transportOnly = tagged.Where(x => x.Tags.Contains("transport")).ToList();
			var transportJson = JsonSerializer.Serialize(transportOnly, opts);
			File.WriteAllText(PeopleTransport, transportJson);
			
			_logger.LogInformation("People tagged with transport: {People}", transportJson);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "People task failed");
		}
	}

	private List<Person> GetMalesAged20To40FromGrudziadz()
	{
		var people = CsvHelper.ParsePeopleFromCsv(PeopleCsv);
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
			throw new InvalidOperationException("OpenRouterService not initialized. Use PeopleTask(HubSettings, OpenRouterSettings, ILogger) constructor.");

		var allowedTags = new[] { "IT", "transport", "edukacja", "medycyna", "praca z ludźmi", "praca z pojazdami", "praca fizyczna" };

		// Convert to list to preserve ordering/indexes
		var personsList = people?.Where(p => p != null).ToList() ?? new List<Person>();

		// Prepare minimal payload: numbered descriptions only
		var descriptions = personsList.Select((p, idx) => new { index = idx, description = p.Description ?? string.Empty }).ToList();

		var systemPrompt = $"Twoim zadaniem jest otagowanie zawodów na podstawie opisu stanowiska.\n" +
			"Masz do dyspozycji następujące tagi: " + string.Join(", ", allowedTags) + ".\n" +
			"Dla każdej pozycji w liście zwróć obiekt z polami: index (numer rekordu odpowiadający przesłanej liście, 0-based) oraz tags (lista tagów).\n" +
			"Zwróć wyłącznie JSON o postaci: { \"results\": [ { \"index\": 0, \"tags\": [\"tag1\"] }, ... ] } i NIE DODAWAJ żadnego dodatkowego tekstu.\n" +
			"Używaj tylko tagów z powyższej listy. Jeżeli nie pasuje żaden tag, zwróć pustą listę tags.";

		var userContent = JsonSerializer.Serialize(new { descriptions = descriptions }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

		var jsonSchema = new JsonSchemaObject(
			"IndexTagging",
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
							description = "Obiekt zawierający numer rekordu oraz przypisane tagi; pozwala powiązać wynik z elementem wejściowym.",
							properties = new
							{
								index = new 
								{ 
									type = "integer", 
									description = "Numer rekordu odpowiadający przesłanej liście (0-based). Używany do mapowania wyników do oryginalnych osób)." 
								},
								tags = new 
								{ 
									type = "array", 
									items = new { type = "string" }, 
									description = "Lista dopasowanych tagów (używaj tylko tagów z dozwolonej listy)." 
								}
							},
							required = new[] { "index", "tags" },
							additionalProperties = false
						}
					}
				},
				required = new[] { "results" },
				additionalProperties = false
			}
		);

		var response = await _openRouter.GetStructuredResponseAsync<IndexTagResponse>(systemPrompt, userContent, OpenRouterModels.Gpt41Mini, jsonSchema).ConfigureAwait(false);

		var result = new List<TaggedPerson>();
		if (response?.Results == null || response.Results.Count == 0)
			return result;

		foreach (var r in response.Results)
		{
			if (r == null)
				continue;

			if (r.Index < 0 || r.Index >= personsList.Count)
				continue;

			var p = personsList[r.Index];
			var tags = r.Tags ?? new List<string>();

			result.Add(new TaggedPerson(
				p.FirstName,
				p.LastName,
				string.IsNullOrWhiteSpace(p.Gender) ? string.Empty : p.Gender.Substring(0, 1).ToUpperInvariant(),
				p.DateOfBirth.Year,
				p.City,
				tags
			));
		}

		return result;
	}
}

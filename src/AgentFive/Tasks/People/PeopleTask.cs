using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentFive.Configuration;
using AgentFive.Services.OpenRouter;

namespace AgentFive.Tasks.People;

public class PeopleTask
{
	private DateTime Today => DateTime.Today;
	private readonly AppSettings? _settings;
	private readonly OpenRouterService? _openRouter;

	public record OpenRouterResponse(List<Choice>? choices);
	public record Choice(Message? message, string? content);
	public record Message(string? role, string? content);
	public record OpenRouterPeopleWrapper(List<TaggedPerson>? people);
	public PeopleTask(AppSettings settings)
	{
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		if (string.IsNullOrWhiteSpace(_settings.OpenRouterApiKey))
			throw new ArgumentException("OpenRouter API key not configured in AppSettings.OpenRouterApiKey");
		_openRouter = new OpenRouterService(_settings.OpenRouterApiKey);
	}

    public List<Person> GetMalesAged20To40FromGrudziadz(string filePath)
	{
		var people = ParsePeopleFromCsv(filePath);
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

	public void DebugPrintPeople(List<Person> people)
	{
		if (people == null)
		{
			Console.WriteLine("Brak danych do wyświetlenia.");
			return;
		}

        var index = 1;
		var referenceDate = Today;

		foreach (var p in people)
		{
			if (p == null)
				continue;

			var age = referenceDate.Year - p.DateOfBirth.Year;
			if (p.DateOfBirth > referenceDate.AddYears(-age))
				age--;

			Console.WriteLine($"{index++}. Imię: {p.FirstName}, Nazwisko: {p.LastName}, Płeć: {p.Gender}, Wiek: {age}, Miejsce urodzenia: {p.City}");
		}
	}
    
	private List<Person> ParsePeopleFromCsv(string filePath)
	{
		var result = new List<Person>();
		if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
		{
			return result;
		}

		foreach (var line in File.ReadLines(filePath))
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;

			var fields = SplitCsvLine(line);
			if (fields.Count < 7)
				continue;

			var firstName = fields[0];
			var lastName = fields[1];
			var gender = fields[2];

			DateTime dob;
			if (!DateTime.TryParseExact(fields[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dob))
			{
				DateTime.TryParse(fields[3], out dob);
			}

			var city = fields[4];
			var country = fields[5];
			var description = fields[6];

			result.Add(new Person(firstName, lastName, gender, dob, city, country, description));
		}

		return result;
	}

	private static List<string> SplitCsvLine(string line)
	{
		var fields = new List<string>();
		var sb = new StringBuilder();
		bool inQuotes = false;

		for (int i = 0; i < line.Length; i++)
		{
			var c = line[i];

			if (c == '"')
			{
				if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
				{
					sb.Append('"');
					i++; // skip escaped quote
				}

				else
				{
					inQuotes = !inQuotes;
				}
			}
			else if (c == ',' && !inQuotes)
			{
				fields.Add(sb.ToString());
				sb.Clear();
			}
			else
			{
				sb.Append(c);
			}
		}

		fields.Add(sb.ToString());

		for (int i = 0; i < fields.Count; i++)
		{
			var f = fields[i].Trim();
			if (f.Length >= 2 && f[0] == '"' && f[f.Length - 1] == '"')
			{
				f = f.Substring(1, f.Length - 2).Replace("\"\"", "\"");
			}
			fields[i] = f;
		}

		return fields;
	}

	public async Task<List<TaggedPerson>> TagPeopleWithOpenRouterAsync(List<Person> people)
	{
		if (_openRouter == null)
			throw new InvalidOperationException("OpenRouterService not initialized. Use PeopleTask(AppSettings) constructor.");

		var allowedTags = new[] { "IT", "transport", "edukacja", "medycyna", "praca z ludźmi", "praca z pojazdami", "praca fizyczna" };

		var records = new List<object>();
		foreach (var p in people)
		{
			if (p == null) continue;
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

		var userPrompt = new {
			role = "user",
			content = JsonSerializer.Serialize(new { people = records }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
		};

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

		var response_format = new {
			type = "json_schema",
			json_schema = jsonSchema
		};

		var payload = new {
			model = "gpt-4o-mini",
			messages = new[] {
				new { role = "system", content = systemPrompt },
				userPrompt
			},
			temperature = 0.0,
			response_format
		};

		var respText = await _openRouter.SendChatCompletionAsync(payload).ConfigureAwait(false);

		// Try to deserialize the response into known models first, then fall back to scanning for a JSON array.
		var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

		// Prefer parsing the OpenRouter response shape (choices -> message.content)
		try
		{
			var or = JsonSerializer.Deserialize<OpenRouterResponse>(respText, opts);
			if (or?.choices != null && or.choices.Count > 0)
			{
				var first = or.choices[0];
				var assistantContent = first?.message?.content ?? first?.content;
				if (!string.IsNullOrWhiteSpace(assistantContent))
				{
					// If assistantContent is a quoted JSON string, unquote it
					if (assistantContent.Length > 0 && assistantContent[0] == '"')
					{
						try
						{
							assistantContent = JsonSerializer.Deserialize<string>(assistantContent, opts) ?? assistantContent;
						}
						catch
						{
							// ignore and continue with original content
						}
					}

					try
					{
						// Prefer wrapper object { people: [...] } as in the sample
						var wrapper = JsonSerializer.Deserialize<OpenRouterPeopleWrapper>(assistantContent, opts);
						if (wrapper?.people != null && wrapper.people.Count > 0)
							return wrapper.people;

						// Fallback: try as array of TaggedPerson
						var parsed = JsonSerializer.Deserialize<List<TaggedPerson>>(assistantContent, opts);
						if (parsed != null && parsed.Count > 0)
							return parsed;
					}
					catch (JsonException)
					{
						// fall through to content scanning
					}
				}
			}
		}
		catch (JsonException)
		{
			// ignore and fall back
		}

		// fallback: try to find any JSON array in the raw response text
		return ParseTaggedPersonsFromContent(respText);
	}

	private static List<TaggedPerson> ParseTaggedPersonsFromContent(string content)
	{
		if (string.IsNullOrWhiteSpace(content)) return new List<TaggedPerson>();

		// attempt to locate first JSON array in the text
		var start = content.IndexOf('[');
		var end = content.LastIndexOf(']');
		if (start >= 0 && end > start)
		{
			var json = content.Substring(start, end - start + 1);
			try
			{
				var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
				var list = JsonSerializer.Deserialize<List<TaggedPerson>>(json, opts);
				return list ?? new List<TaggedPerson>();
			}
			catch (JsonException)
			{
				return new List<TaggedPerson>();
			}
		}

		return new List<TaggedPerson>();
	}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AgentFive.Tasks.People;

public class PeopleTask
{
    private DateTime Today => DateTime.Today.AddDays(-1);

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
}

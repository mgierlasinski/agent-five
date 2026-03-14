using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentFive.Tasks.SendIt.Models;

namespace AgentFive.Tasks.SendIt;

public class DeclarationService
{
	private const decimal StandardTrainCapacityKg = 1000m;
	private const decimal AdditionalWagonCapacityKg = 500m;
	private static readonly string[] EmptyNoteValues = [string.Empty, "-", "brak", "none", "n/a", "nie dotyczy"];
	private static readonly Regex DeclarationRegex = new(
		@"^SYSTEM PRZESYŁEK\r?\nKONDUKTORSKICH - DEKLARACJA\r?\nZAWARTOŚCI\r?\n=+\r?\nDATA: (?<date>\d{4}-\d{2}-\d{2})\r?\nPUNKT NADAWCZY: (?<origin>.+)\r?\n-+\r?\nNADAWCA: (?<sender>.+)\r?\nPUNKT DOCELOWY: (?<destination>.+)\r?\nTRASA: (?<route>.+)\r?\n-+\r?\nKATEGORIA PRZESYŁKI:\r?\n(?<category>[A-E])\r?\n-+\r?\nOPIS ZAWARTOŚCI \(max 200 znaków\):\r?\n(?<content>.*)\r?\n-+\r?\nDEKLAROWANA MASA \(kg\):\r?\n(?<weight>.+)\r?\n-+\r?\nWDP:\r?\n(?<wdp>\d+)\r?\n-+\r?\nUWAGI SPECJALNE:\r?\n(?<notes>.*)\r?\n-+\r?\nKWOTA DO ZAPŁATY:\r?\n(?<amount>.+)\r?\n-+\r?\nOŚWIADCZAM, ŻE PODANE INFORMACJE SĄ\r?\nPRAWDZIWE\.\r?\nBIORĘ NA SIEBIE KONSEKWENCJĘ ZA FAŁSZYWE\r?\nOŚWIADCZENIE\.\r?\n=+\s*$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public TransportDeclaration ParseDeclaration(string declarationText)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(declarationText);

		var match = DeclarationRegex.Match(NormalizeLineEndings(declarationText));
		if (!match.Success)
		{
			throw new InvalidOperationException("Declaration does not match the required SPK template.");
		}

		return new TransportDeclaration(
			DateOnly.ParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
			match.Groups["origin"].Value.Trim(),
			match.Groups["sender"].Value.Trim(),
			match.Groups["destination"].Value.Trim(),
			match.Groups["route"].Value.Trim(),
			match.Groups["category"].Value.Trim(),
			match.Groups["content"].Value.Trim(),
			ParseDecimal(match.Groups["weight"].Value),
			int.Parse(match.Groups["wdp"].Value, CultureInfo.InvariantCulture),
			match.Groups["notes"].Value.Trim(),
			ParseDecimal(match.Groups["amount"].Value.Replace("PP", string.Empty, StringComparison.OrdinalIgnoreCase)));
	}

	public string RenderDeclaration(TransportDeclaration declaration)
	{
		ArgumentNullException.ThrowIfNull(declaration);

		var builder = new StringBuilder();
		builder.AppendLine("SYSTEM PRZESYŁEK");
		builder.AppendLine("KONDUKTORSKICH - DEKLARACJA");
		builder.AppendLine("ZAWARTOŚCI");
		builder.AppendLine("======================================================");
		builder.AppendLine($"DATA: {declaration.Date:yyyy-MM-dd}");
		builder.AppendLine($"PUNKT NADAWCZY: {declaration.Origin}");
		builder.AppendLine("------------------------------------------------------");
		builder.AppendLine($"NADAWCA: {declaration.SenderId}");
		builder.AppendLine($"PUNKT DOCELOWY: {declaration.Destination}");
		builder.AppendLine($"TRASA: {declaration.RouteCode}");
		builder.AppendLine("------------------------------------------------------");
		builder.AppendLine("KATEGORIA PRZESYŁKI:");
		builder.AppendLine(declaration.Category);
		builder.AppendLine("------------------------------------------------------");
		builder.AppendLine("OPIS ZAWARTOŚCI (max 200 znaków):");
		builder.AppendLine(declaration.ContentDescription);
		builder.AppendLine("------------------------------------------------------");
		builder.AppendLine("DEKLAROWANA MASA (kg):");
		builder.AppendLine(FormatNumber(declaration.WeightKg));
		builder.AppendLine("------------------------------------------------------");
		builder.AppendLine("WDP:");
		builder.AppendLine(declaration.AdditionalWagons.ToString(CultureInfo.InvariantCulture));
		builder.AppendLine("------------------------------------------------------");
		builder.AppendLine("UWAGI SPECJALNE:");
		builder.AppendLine(declaration.SpecialNotes);
		builder.AppendLine("------------------------------------------------------");
		builder.AppendLine("KWOTA DO ZAPŁATY:");
		builder.AppendLine($"{FormatNumber(declaration.AmountDuePp)} PP");
		builder.AppendLine("------------------------------------------------------");
		builder.AppendLine("OŚWIADCZAM, ŻE PODANE INFORMACJE SĄ");
		builder.AppendLine("PRAWDZIWE.");
		builder.AppendLine("BIORĘ NA SIEBIE KONSEKWENCJĘ ZA FAŁSZYWE");
		builder.AppendLine("OŚWIADCZENIE.");
		builder.Append("======================================================");
		return builder.ToString();
	}

	public string ValidateAndNormalizeForSubmission(string declarationText, DeclarationRequest expectedRequest)
	{
		ArgumentNullException.ThrowIfNull(expectedRequest);

		var declaration = TryParseStrict(declarationText) ?? ParseLenient(declarationText);

		if (string.IsNullOrWhiteSpace(declaration.RouteCode))
		{
			throw new InvalidOperationException("Route code is missing from the declaration.");
		}

		var expectedDate = DateOnly.FromDateTime(DateTime.Today);
		if (declaration.Date != expectedDate)
		{
			throw new InvalidOperationException($"Declaration date must be today's date: {expectedDate:yyyy-MM-dd}.");
		}

		if (!string.Equals(declaration.Origin, expectedRequest.Origin, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"Unexpected origin: {declaration.Origin}.");
		}

		if (!string.Equals(declaration.Destination, expectedRequest.Destination, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"Unexpected destination: {declaration.Destination}.");
		}

		if (!string.Equals(declaration.SenderId, expectedRequest.SenderId, StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"Unexpected sender id: {declaration.SenderId}.");
		}

		if (!string.Equals(declaration.ContentDescription, expectedRequest.ContentDescription, StringComparison.Ordinal)
			&& !LooksLikeExpectedContent(declaration.ContentDescription, expectedRequest.ContentDescription))
		{
			throw new InvalidOperationException("Declaration content description does not match the task input.");
		}

		if (declaration.ContentDescription.Length > 200)
		{
			throw new InvalidOperationException("Content description exceeds 200 characters.");
		}

		if (declaration.WeightKg != expectedRequest.WeightKg)
		{
			throw new InvalidOperationException($"Unexpected declared weight: {declaration.WeightKg}.");
		}

		if (declaration.WeightKg is < 0.1m or > 4000m)
		{
			throw new InvalidOperationException("Declared weight is outside SPK limits.");
		}

		var expectedAdditionalWagons = CalculateAdditionalWagons(declaration.WeightKg);
		if (declaration.AdditionalWagons != expectedAdditionalWagons)
		{
			throw new InvalidOperationException($"Expected WDP={expectedAdditionalWagons}, received {declaration.AdditionalWagons}.");
		}

		var notesAreAcceptable = IsEffectivelyEmpty(declaration.SpecialNotes)
			|| string.IsNullOrWhiteSpace(expectedRequest.SpecialNotes);
		if (!notesAreAcceptable)
		{
			throw new InvalidOperationException("Special notes must remain empty for this shipment.");
		}

		if (string.Equals(expectedRequest.Destination, "Żarnowiec", StringComparison.OrdinalIgnoreCase)
			&& declaration.Category is not ("A" or "B"))
		{
			throw new InvalidOperationException("Shipments to Żarnowiec must use category A or B.");
		}

		var expectedAmount = CalculateAmountDue(declaration.Category, declaration.WeightKg, 0, declaration.AdditionalWagons);
		if (declaration.AmountDuePp != expectedAmount)
		{
			throw new InvalidOperationException($"Expected payment amount {expectedAmount} PP, received {declaration.AmountDuePp} PP.");
		}

		if (declaration.AmountDuePp > expectedRequest.BudgetPp)
		{
			throw new InvalidOperationException($"Declaration exceeds budget: {declaration.AmountDuePp} PP > {expectedRequest.BudgetPp} PP.");
		}

		var canonical = declaration with
		{
			Date = expectedDate,
			Origin = expectedRequest.Origin,
			SenderId = expectedRequest.SenderId,
			Destination = expectedRequest.Destination,
			Category = declaration.Category.Trim().ToUpperInvariant(),
			ContentDescription = expectedRequest.ContentDescription,
			WeightKg = expectedRequest.WeightKg,
			AdditionalWagons = expectedAdditionalWagons,
			SpecialNotes = string.Empty,
			AmountDuePp = expectedAmount
		};

		return RenderDeclaration(canonical);
	}

	public int CalculateAdditionalWagons(decimal weightKg)
	{
		if (weightKg <= StandardTrainCapacityKg)
		{
			return 0;
		}

		return (int)Math.Ceiling((double)((weightKg - StandardTrainCapacityKg) / AdditionalWagonCapacityKg));
	}

	public decimal CalculateAmountDue(string category, decimal weightKg, int regionalBoundariesCrossed, int additionalWagons)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(category);

		if (category is "A" or "B")
		{
			return 0m;
		}

		var baseFee = category switch
		{
			"C" => 2m,
			"D" => 5m,
			"E" => 10m,
			_ => throw new InvalidOperationException($"Unsupported category: {category}")
		};

		var weightFee = weightKg switch
		{
			<= 5m => weightKg * 0.5m,
			<= 25m => weightKg * 1m,
			<= 100m => weightKg * 2m,
			<= 500m => weightKg * 3m,
			<= 1000m => weightKg * 5m,
			_ => weightKg * 7m
		};

		var distanceMultiplier = regionalBoundariesCrossed switch
		{
			<= 0 => 1m,
			1 => 2m,
			_ => 3m
		};

		var wagonFee = additionalWagons * 55m;
		return baseFee + weightFee + wagonFee + distanceMultiplier;
	}

	private static string NormalizeLineEndings(string value)
	{
		return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
	}

	private static bool IsEffectivelyEmpty(string value)
	{
		return EmptyNoteValues.Contains((value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
	}

	private static bool LooksLikeExpectedContent(string actual, string expected)
	{
		var normalizedActual = NormalizeLetters(actual);
		var normalizedExpected = NormalizeLetters(expected);
		return normalizedActual.Contains("kaset", StringComparison.Ordinal)
			&& normalizedActual.Contains("reaktor", StringComparison.Ordinal)
			&& normalizedExpected.Contains("kaset", StringComparison.Ordinal)
			&& normalizedExpected.Contains("reaktor", StringComparison.Ordinal);
	}

	private static string NormalizeLetters(string value)
	{
		return (value ?? string.Empty).Trim().ToLowerInvariant();
	}

	private TransportDeclaration? TryParseStrict(string declarationText)
	{
		try
		{
			return ParseDeclaration(declarationText);
		}
		catch
		{
			return null;
		}
	}

	private TransportDeclaration ParseLenient(string declarationText)
	{
		var normalized = NormalizeLineEndings(declarationText)
			.Replace("```", string.Empty, StringComparison.Ordinal)
			.Trim();

		var date = ExtractValue(normalized, @"DATA:\s*(?<value>\d{4}-\d{2}-\d{2})");
		var origin = ExtractValue(normalized, @"PUNKT NADAWCZY:\s*(?<value>.+)");
		var sender = ExtractValue(normalized, @"NADAWCA:\s*(?<value>.+)");
		var destination = ExtractValue(normalized, @"PUNKT DOCELOWY:\s*(?<value>.+)");
		var route = ExtractValue(normalized, @"TRASA:\s*(?<value>.+)");
		var category = ExtractValue(normalized, @"KATEGORIA PRZESYŁKI:\s*(?<value>[A-E](?:\s*-\s*[^\n]+)?)");
		var content = ExtractValue(normalized, @"OPIS ZAWARTOŚCI \(max 200 znaków\):\s*(?<value>.+?)(?:\n(?:-+|DEKLAROWANA MASA))", true);
		var weight = ExtractValue(normalized, @"DEKLAROWANA MASA \(kg\):\s*(?<value>[0-9.,]+)");
		var wdp = ExtractValue(normalized, @"WDP:\s*(?<value>\d+)");
		var notes = TryExtractValue(normalized, @"UWAGI SPECJALNE:\s*(?<value>.*?)(?:\n(?:-+|KWOTA DO ZAPŁATY))", true) ?? string.Empty;
		var amount = ExtractValue(normalized, @"KWOTA DO ZAPŁATY:\s*(?<value>[0-9.,]+)(?:\s*PP)?");

		return new TransportDeclaration(
			DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
			origin,
			sender,
			destination,
			route,
			category[..1],
			content.Trim(),
			ParseDecimal(weight),
			int.Parse(wdp, CultureInfo.InvariantCulture),
			notes.Trim(),
			ParseDecimal(amount));
	}

	private static string ExtractValue(string input, string pattern, bool singleLine = false)
	{
		return TryExtractValue(input, pattern, singleLine)
			?? throw new InvalidOperationException($"Declaration is missing a required field for pattern: {pattern}");
	}

	private static string? TryExtractValue(string input, string pattern, bool singleLine = false)
	{
		var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
		if (singleLine)
		{
			options |= RegexOptions.Singleline;
		}

		var match = Regex.Match(input, pattern, options);
		if (!match.Success)
		{
			return null;
		}

		return match.Groups["value"].Value.Trim();
	}

	private static decimal ParseDecimal(string value)
	{
		var normalized = value.Trim().Replace(',', '.');
		if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
		{
			throw new InvalidOperationException($"Unable to parse numeric value '{value}'.");
		}

		return result;
	}

	private static string FormatNumber(decimal value)
	{
		return decimal.Truncate(value) == value
			? value.ToString("0", CultureInfo.InvariantCulture)
			: value.ToString("0.##", CultureInfo.InvariantCulture);
	}
}
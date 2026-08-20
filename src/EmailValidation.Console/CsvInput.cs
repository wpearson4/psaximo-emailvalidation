using System.Text;

namespace EmailValidation.ConsoleApp;

internal static class CsvInput
{
    internal static IReadOnlyList<int> DetectEmailColumns(IReadOnlyList<string> headers)
    {
        var matches = new List<int>();
        for (var index = 0; index < headers.Count; index++)
        {
            var normalized = NormalizeHeader(headers[index]);
            if (normalized == "email" || normalized == "emailaddress" ||
                normalized.EndsWith("email", StringComparison.Ordinal))
                matches.Add(index);
        }
        return matches;
    }

    internal static int ResolveEmailColumn(IReadOnlyList<string> headers, string? explicitColumn)
    {
        if (explicitColumn is not null)
        {
            var matches = Enumerable.Range(0, headers.Count)
                .Where(index => string.Equals(headers[index].Trim(), explicitColumn.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1)
                throw new InvalidDataException($"Column '{explicitColumn}' occurs more than once in the CSV header.");
            throw new InvalidDataException($"Column '{explicitColumn}' was not found in the CSV header.");
        }

        var detected = DetectEmailColumns(headers);
        if (detected.Count == 1) return detected[0];
        if (detected.Count == 0)
            throw new InvalidDataException("No email column was found.\nUse --column <column-name> to specify the email field.");

        var names = string.Join(Environment.NewLine, detected.Select(index => headers[index]));
        throw new InvalidDataException(
            $"Multiple email columns were detected:{Environment.NewLine}{names}{Environment.NewLine}Specify the column using --column.");
    }

    private static string NormalizeHeader(string header)
    {
        var builder = new StringBuilder(header.Length);
        foreach (var character in header)
        {
            if (character is ' ' or '_' or '-') continue;
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}

using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;

namespace EmailValidation.Application;

public sealed class PurchasedEmailColumnNotFoundException(string columnName)
    : Exception($"The purchased result does not contain the requested email column '{columnName}'.");

public sealed class PurchasedEmailColumnEmptyException(string columnName)
    : Exception($"The purchased email column '{columnName}' contains no values.");

public sealed class PurchasedEmailLimitExceededException(int maximumItems)
    : Exception($"The purchased email column contains more than {maximumItems} values.");

public sealed record PurchasedEmailData(
    IReadOnlyList<string> Emails,
    IReadOnlyList<int> SourcePositions);

public interface IPurchasedEmailDataReader
{
    Task<PurchasedEmailData> ReadAsync(
        Stream content,
        string fileName,
        string emailColumn,
        int maximumItems,
        CancellationToken cancellationToken = default);
}

public sealed class PurchasedEmailDataReader : IPurchasedEmailDataReader
{
    public async Task<PurchasedEmailData> ReadAsync(
        Stream content,
        string fileName,
        string emailColumn,
        int maximumItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(emailColumn);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1);
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return await ReadJsonAsync(
                content, emailColumn.Trim(), maximumItems, cancellationToken).ConfigureAwait(false);
        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Purchased Email Validation supports CSV and JSON results.");

        using var textReader = new StreamReader(content, leaveOpen: true);
        using var csv = new CsvReader(textReader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            HeaderValidated = null,
            MissingFieldFound = null,
            MaxFieldSize = 1_048_576
        });

        if (!await csv.ReadAsync().ConfigureAwait(false))
            throw new InvalidDataException("The purchased result is empty.");

        var requestedColumn = emailColumn.Trim();
        var headers = csv.Parser.Record ?? [];
        var matches = headers
            .Select((header, index) => new { Header = header?.Trim(), Index = index })
            .Where(item => string.Equals(item.Header, requestedColumn, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Index)
            .ToArray();
        if (matches.Length != 1)
            throw new PurchasedEmailColumnNotFoundException(requestedColumn);

        var columnIndex = matches[0];
        var emails = new List<string>();
        var sourcePositions = new List<int>();
        var position = 0;
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = csv.Parser.Record ?? [];
            var email = columnIndex < row.Length ? row[columnIndex]?.Trim() : null;
            if (!string.IsNullOrWhiteSpace(email))
            {
                if (emails.Count == maximumItems)
                    throw new PurchasedEmailLimitExceededException(maximumItems);
                emails.Add(email);
                sourcePositions.Add(position);
            }
            position++;
        }

        if (emails.Count == 0)
            throw new PurchasedEmailColumnEmptyException(requestedColumn);
        return new PurchasedEmailData(emails, sourcePositions);
    }

    private static async Task<PurchasedEmailData> ReadJsonAsync(
        Stream content,
        string requestedColumn,
        int maximumItems,
        CancellationToken cancellationToken)
    {
        var emails = new List<string>();
        var sourcePositions = new List<int>();
        var position = 0;
        var columnFound = false;
        try
        {
            await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<Dictionary<string, JsonElement>>(
                               content, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (record is null) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var matches = record
                    .Where(field => string.Equals(field.Key, requestedColumn, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length > 1)
                    throw new PurchasedEmailColumnNotFoundException(requestedColumn);
                if (matches.Length == 1)
                {
                    columnFound = true;
                    var email = JsonValue(matches[0].Value).Trim();
                    if (email.Length > 0)
                    {
                        if (emails.Count == maximumItems)
                            throw new PurchasedEmailLimitExceededException(maximumItems);
                        emails.Add(email);
                        sourcePositions.Add(position);
                    }
                }
                position++;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The purchased JSON result is invalid.", exception);
        }

        if (!columnFound)
            throw new PurchasedEmailColumnNotFoundException(requestedColumn);
        if (emails.Count == 0)
            throw new PurchasedEmailColumnEmptyException(requestedColumn);
        return new PurchasedEmailData(emails, sourcePositions);
    }

    private static string JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.ToString()
    };
}

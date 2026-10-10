using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using EmailValidation.Application;
using EmailValidation.Core;

namespace EmailValidation.Api;

public sealed class ValidationJobCsvExporter(IValidationJobService jobs)
{
    private static readonly string[] ValidationHeaders =
    [
        "Email Validation Status",
        "Email Validation Detail",
        "Email Validation Confidence Reason",
        "Email Validation Domain Behavior",
        "Email Validation Date"
    ];

    public async Task WriteAsync(
        Stream source,
        string sourceFileName,
        ValidationJobSnapshot job,
        Stream destination,
        bool removeInvalidEmails = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(destination);
        await using var output = new StreamWriter(
            destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        await using var writer = new CsvWriter(output, CultureInfo.InvariantCulture);
        var results = new ValidationResultCursor(jobs, job.JobId);

        if (sourceFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            await WriteJsonAsync(source, writer, results, job.EmailColumn, removeInvalidEmails, cancellationToken).ConfigureAwait(false);
        else if (sourceFileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            await WriteCsvAsync(source, writer, results, job.EmailColumn, removeInvalidEmails, cancellationToken).ConfigureAwait(false);
        else
            throw new InvalidDataException("Validated file download supports CSV and JSON source files.");

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCsvAsync(
        Stream source,
        CsvWriter writer,
        ValidationResultCursor results,
        string? emailColumn,
        bool removeInvalidEmails,
        CancellationToken cancellationToken)
    {
        using var input = new StreamReader(
            source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(input, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            HeaderValidated = null,
            MissingFieldFound = null,
            MaxFieldSize = 1_048_576
        });

        if (!await csv.ReadAsync().ConfigureAwait(false))
            throw new InvalidDataException("The source file is empty.");

        var sourceHeaders = csv.Parser.Record ?? [];
        var emailIndex = ResolveEmailColumn(sourceHeaders, emailColumn, removeInvalidEmails);
        var headers = CreateUniqueHeaders(sourceHeaders);
        foreach (var header in headers.Concat(ValidationHeaders))
            writer.WriteField(header);
        await writer.NextRecordAsync().ConfigureAwait(false);

        var position = 0;
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = csv.Parser.Record ?? [];
            var item = await results.ReadAtAsync(position, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < headers.Count; index++)
            {
                var value = index < row.Length ? row[index] : string.Empty;
                writer.WriteField(index == emailIndex && ShouldRemove(value, item) ? string.Empty : value);
            }

            WriteValidationFields(writer, item);
            await writer.NextRecordAsync().ConfigureAwait(false);
            position++;
        }
    }

    private static async Task WriteJsonAsync(
        Stream source,
        CsvWriter writer,
        ValidationResultCursor results,
        string? emailColumn,
        bool removeInvalidEmails,
        CancellationToken cancellationToken)
    {
        await using var records = JsonSerializer.DeserializeAsyncEnumerable<Dictionary<string, JsonElement>>(
                source, cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        Dictionary<string, JsonElement>? record = null;
        while (record is null && await records.MoveNextAsync().ConfigureAwait(false))
            record = records.Current;
        if (record is null)
            throw new InvalidDataException("The source JSON file contains no data rows.");

        var sourceHeaders = record.Keys.ToArray();
        var emailIndex = ResolveEmailColumn(sourceHeaders, emailColumn, removeInvalidEmails);
        var outputHeaders = CreateUniqueHeaders(sourceHeaders);
        foreach (var header in outputHeaders.Concat(ValidationHeaders))
            writer.WriteField(header);
        await writer.NextRecordAsync().ConfigureAwait(false);

        var position = 0;
        while (record is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Keys.Any(key => !sourceHeaders.Contains(key, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("The source JSON file contains inconsistent object fields.");
            var item = await results.ReadAtAsync(position, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < sourceHeaders.Length; index++)
            {
                var header = sourceHeaders[index];
                var field = record.FirstOrDefault(item =>
                    string.Equals(item.Key, header, StringComparison.OrdinalIgnoreCase));
                var value = string.IsNullOrEmpty(field.Key) ? string.Empty : JsonValue(field.Value);
                writer.WriteField(index == emailIndex && ShouldRemove(value, item) ? string.Empty : value);
            }
            WriteValidationFields(writer, item);
            await writer.NextRecordAsync().ConfigureAwait(false);
            position++;

            record = null;
            while (record is null && await records.MoveNextAsync().ConfigureAwait(false))
                record = records.Current;
        }
    }

    private static int ResolveEmailColumn(string[] headers, string? emailColumn, bool removeInvalidEmails)
    {
        if (!removeInvalidEmails) return -1;
        var matches = headers.Select((header, index) => (header, index))
            .Where(item => string.Equals(item.header.Trim(), emailColumn?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index).ToArray();
        if (string.IsNullOrWhiteSpace(emailColumn) || matches.Length != 1)
            throw new InvalidDataException("The validated email column could not be uniquely identified.");
        return matches[0];
    }

    private static bool ShouldRemove(string value, ValidationJobItem? item) =>
        item is { State: ValidationJobItemState.Completed } &&
        InvalidEmailRemovalPolicy.CanRemove(item.Result) &&
        string.Equals(value.Trim(), item.Email.Trim(), StringComparison.Ordinal);

    private static string JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.ToString()
    };

    private static List<string> CreateUniqueHeaders(string[] source)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headers = new List<string>(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var basis = string.IsNullOrWhiteSpace(source[index]) ? $"Column {index + 1}" : source[index].Trim();
            var candidate = basis;
            var suffix = 2;
            while (!used.Add(candidate)) candidate = $"{basis} ({suffix++})";
            headers.Add(candidate);
        }
        return headers;
    }

    private static void WriteValidationFields(CsvWriter writer, ValidationJobItem? item)
    {
        var validation = item?.Result is null ? null : ApiContractMapper.Map(item.Result);
        writer.WriteField(validation?.Status ?? item?.State.ToString() ?? string.Empty);
        writer.WriteField(validation?.UnknownContext?.Summary ??
            validation?.SubStatus ?? validation?.ConfidenceReason ?? string.Empty);
        writer.WriteField(validation?.ConfidenceReason ?? string.Empty);
        writer.WriteField(validation?.Checks.RecipientBehavior ?? string.Empty);
        writer.WriteField(validation?.ValidatedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private sealed class ValidationResultCursor(IValidationJobService jobs, string jobId)
    {
        private const int PageSize = 100;
        private IReadOnlyList<ValidationJobItem> _page = [];
        private int _pageIndex;
        private int _skip;
        private bool _complete;

        public async Task<ValidationJobItem?> ReadAtAsync(
            int position,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var current = await PeekAsync(cancellationToken).ConfigureAwait(false);
                if (current is null || current.Position > position) return null;
                _pageIndex++;
                if (current.Position == position) return current;
            }
        }

        private async Task<ValidationJobItem?> PeekAsync(CancellationToken cancellationToken)
        {
            if (_pageIndex < _page.Count) return _page[_pageIndex];
            if (_complete) return null;

            _page = await jobs.GetResultsAsync(jobId, _skip, PageSize, cancellationToken)
                .ConfigureAwait(false);
            _pageIndex = 0;
            _skip += _page.Count;
            _complete = _page.Count == 0;
            return _page.Count == 0 ? null : _page[0];
        }
    }
}

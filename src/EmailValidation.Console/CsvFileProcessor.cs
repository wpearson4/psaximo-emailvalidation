using System.Diagnostics;
using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailValidation.ConsoleApp;

internal sealed record CsvProcessingResult(
    string FilePath,
    int RowsProcessed,
    IReadOnlyDictionary<EmailValidationStatus, int> StatusCounts,
    TimeSpan Duration);

internal sealed class CsvFileProcessor
{
    internal static readonly string[] ResultColumns =
        ["Status", "Confidence", "Confidence Reason", "Validation Date/Time"];

    private readonly IDomainValidationScheduler _scheduler;
    private readonly IOptions<EmailValidationOptions> _options;
    private readonly ILogger<CsvFileProcessor> _logger;
    private int Concurrency => Math.Max(1, _options.Value.Scheduling.GlobalConcurrency > 0
        ? _options.Value.Scheduling.GlobalConcurrency
        : _options.Value.Smtp.GlobalConcurrency);

    [ActivatorUtilitiesConstructor]
    public CsvFileProcessor(
        IDomainValidationScheduler scheduler,
        IOptions<EmailValidationOptions> options,
        ILogger<CsvFileProcessor> logger)
    {
        _scheduler = scheduler;
        _options = options;
        _logger = logger;
    }

    internal CsvFileProcessor(
        IEmailValidator validator,
        IOptions<EmailValidationOptions> options,
        ILogger<CsvFileProcessor> logger)
        : this(new DomainValidationScheduler(
            validator, new EmailNormalizer(), options,
            NullLogger<DomainValidationScheduler>.Instance), options, logger)
    { }

    public async Task<CsvProcessingResult> ProcessAsync(
        string path,
        string? explicitColumn,
        bool live,
        bool verbose,
        TextWriter progress,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("CSV input file was not found.", fullPath);

        CsvFileMetadata metadata;
        try
        {
            metadata = await InspectAsync(fullPath, explicitColumn, cancellationToken);
        }
        catch (CsvHelperException exception)
        {
            throw new InvalidDataException($"CSV could not be parsed: {exception.Message}", exception);
        }

        _logger.LogInformation("CSV file opened with {Rows} data rows", metadata.RowCount);
        _logger.LogInformation("CSV email column selected: {Column}", metadata.Headers[metadata.EmailColumnIndex]);
        await progress.WriteLineAsync($"Validating {Path.GetFileName(fullPath)}");
        await progress.WriteLineAsync($"Email column: {metadata.Headers[metadata.EmailColumnIndex]}");

        var temporaryPath = fullPath + ".validation.tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        var stopwatch = Stopwatch.StartNew();
        var counts = Enum.GetValues<EmailValidationStatus>().ToDictionary(status => status, _ => 0);

        try
        {
            _logger.LogInformation("Creating temporary CSV output");
            await WriteValidatedFileAsync(
                fullPath, temporaryPath, metadata, live, verbose, counts, progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var verified = await InspectAsync(temporaryPath, metadata.Headers[metadata.EmailColumnIndex], cancellationToken);
            if (verified.RowCount != metadata.RowCount)
                throw new IOException("Temporary CSV verification failed because the row count changed.");
            if (ResultColumns.Any(required => !verified.Headers.Any(
                    header => string.Equals(header.Trim(), required, StringComparison.OrdinalIgnoreCase))))
                throw new IOException("Temporary CSV verification failed because a result column is missing.");

            PreserveUnixMode(fullPath, temporaryPath);
            File.Move(temporaryPath, fullPath, overwrite: true);
            stopwatch.Stop();
            _logger.LogInformation("CSV replacement completed after {Duration}", stopwatch.Elapsed);
            return new(fullPath, metadata.RowCount, counts, stopwatch.Elapsed);
        }
        catch (CsvHelperException exception)
        {
            throw new InvalidDataException($"CSV could not be parsed or written: {exception.Message}", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException cleanupException)
                {
                    _logger.LogWarning(cleanupException, "Could not remove temporary CSV output");
                }
            }
        }
    }

    private async Task WriteValidatedFileAsync(
        string sourcePath,
        string temporaryPath,
        CsvFileMetadata metadata,
        bool live,
        bool verbose,
        Dictionary<EmailValidationStatus, int> counts,
        TextWriter progress,
        CancellationToken cancellationToken)
    {
        var outputHeaders = metadata.Headers.ToList();
        foreach (var column in ResultColumns)
        {
            if (!outputHeaders.Any(header => string.Equals(header.Trim(), column, StringComparison.OrdinalIgnoreCase)))
                outputHeaders.Add(column);
        }
        var resultIndexes = ResultColumns.ToDictionary(
            column => column,
            column => outputHeaders.FindIndex(header => string.Equals(header.Trim(), column, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        await using var inputStream = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var inputReader = new StreamReader(inputStream, new UTF8Encoding(false, true), true, 64 * 1024, leaveOpen: true);
        using var csvReader = new CsvReader(inputReader, CreateConfiguration());
        await using var outputStream = new FileStream(
            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var outputWriter = new StreamWriter(
            outputStream, new UTF8Encoding(metadata.HasUtf8Bom), 64 * 1024, leaveOpen: true);
        await using var csvWriter = new CsvWriter(outputWriter, CreateConfiguration());

        if (!await csvReader.ReadAsync() || !csvReader.ReadHeader())
            throw new InvalidDataException("The CSV file does not contain a header row.");
        foreach (var header in outputHeaders) csvWriter.WriteField(header);
        await csvWriter.NextRecordAsync();

        var batchSize = Math.Max(16, Math.Min(
            Math.Max(1, _options.Value.Scheduling.MaxActiveDomains) * Concurrency,
            8192));
        var batch = new List<CsvRowInput>(batchSize);
        var sequence = 0;
        while (await csvReader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = Enumerable.Range(0, metadata.Headers.Count)
                .Select(index => csvReader.GetField(index) ?? string.Empty)
                .ToArray();
            batch.Add(new(sequence++, fields, fields[metadata.EmailColumnIndex]));
            if (batch.Count < batchSize) continue;

            await ValidateAndWriteBatchAsync(
                batch, csvWriter, outputHeaders.Count, resultIndexes, live, verbose,
                counts, metadata.RowCount, progress, cancellationToken);
            batch.Clear();
        }
        if (batch.Count > 0)
            await ValidateAndWriteBatchAsync(
                batch, csvWriter, outputHeaders.Count, resultIndexes, live, verbose,
                counts, metadata.RowCount, progress, cancellationToken);

        await csvWriter.FlushAsync();
        await outputWriter.FlushAsync(cancellationToken);
        outputStream.Flush(flushToDisk: true);
    }

    private async Task ValidateAndWriteBatchAsync(
        IReadOnlyList<CsvRowInput> rows,
        CsvWriter writer,
        int outputFieldCount,
        Dictionary<string, int> resultIndexes,
        bool live,
        bool verbose,
        Dictionary<EmailValidationStatus, int> counts,
        int totalRows,
        TextWriter progress,
        CancellationToken cancellationToken)
    {
        var work = rows.Select(row => new ValidationWorkItem(
            row.Sequence, row.Email, new EmailValidationRequest(live, verbose))).ToArray();
        var inputs = rows.ToDictionary(row => (long)row.Sequence);
        var pending = new SortedDictionary<long, CsvRowResult>();
        var nextSequence = (long)rows[0].Sequence;
        var writtenCount = 0;
        await foreach (var result in _scheduler.ScheduleStreamingAsync(work, cancellationToken))
        {
            pending[result.Sequence] = new(inputs[result.Sequence], result.Result, result.CompletedAt);
            var wroteRows = false;
            while (pending.Remove(nextSequence, out var row))
            {
                await WriteRowAsync(writer, row, outputFieldCount, resultIndexes);
                counts[row.Result.Status]++;
                nextSequence++;
                writtenCount++;
                wroteRows = true;
            }
            if (!wroteRows) continue;

            await writer.FlushAsync();
            await WriteProgressAsync(counts, totalRows, progress);
        }
        if (pending.Count != 0 || writtenCount != rows.Count)
            throw new IOException("Validation scheduling completed without producing every CSV row in sequence.");

    }

    private async Task WriteProgressAsync(
        IReadOnlyDictionary<EmailValidationStatus, int> counts,
        int totalRows,
        TextWriter progress)
    {
        var processed = counts.Values.Sum();
        var percent = totalRows == 0 ? 100 : processed * 100d / totalRows;
        await progress.WriteLineAsync(
            $"{processed:N0} / {totalRows:N0} ({percent:0.0}%) | " +
            string.Join("  ", Enum.GetValues<EmailValidationStatus>()
                .Select(status => $"{status}: {counts[status]:N0}")));
        _logger.LogInformation("CSV rows processed: {Processed}/{Total}", processed, totalRows);
    }

    private static async Task WriteRowAsync(
        CsvWriter writer,
        CsvRowResult row,
        int outputFieldCount,
        Dictionary<string, int> resultIndexes)
    {
        var values = new string[outputFieldCount];
        Array.Copy(row.Input.Fields, values, row.Input.Fields.Length);
        values[resultIndexes["Status"]] = row.Result.Status.ToString();
        values[resultIndexes["Confidence"]] = FormatConfidence(row.Result.Confidence);
        values[resultIndexes["Confidence Reason"]] =
            row.Result.ConfidenceReason ?? "No confidence explanation was available.";
        values[resultIndexes["Validation Date/Time"]] = row.CompletedAt.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        foreach (var value in values) writer.WriteField(value ?? string.Empty);
        await writer.NextRecordAsync();
    }

    private static string FormatConfidence(double confidence)
    {
        var percentage = Math.Clamp(confidence, 0, 1) * 100;
        return $"{Math.Round(percentage, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";
    }

    private static async Task<CsvFileMetadata> InspectAsync(
        string path,
        string? explicitColumn,
        CancellationToken cancellationToken)
    {
        var hasBom = HasUtf8Bom(path);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 64 * 1024, leaveOpen: true);
        using var csv = new CsvReader(reader, CreateConfiguration());
        if (!await csv.ReadAsync() || !csv.ReadHeader() || csv.HeaderRecord is null || csv.HeaderRecord.Length == 0)
            throw new InvalidDataException("The CSV file does not contain a header row.");

        var headers = csv.HeaderRecord.ToArray();
        var emailColumnIndex = CsvInput.ResolveEmailColumn(headers, explicitColumn);
        var rowCount = 0;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (csv.Parser.Count <= emailColumnIndex)
                throw new InvalidDataException($"Row {rowCount + 2} does not contain column {emailColumnIndex + 1}.");
            if (csv.Parser.Count > headers.Length)
                throw new InvalidDataException($"Row {rowCount + 2} contains more fields than the CSV header.");
            rowCount++;
        }
        return new(headers, emailColumnIndex, rowCount, hasBom);
    }

    private static CsvConfiguration CreateConfiguration() => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        IgnoreBlankLines = false,
        DetectDelimiter = false,
        TrimOptions = TrimOptions.None,
        BadDataFound = context => throw new InvalidDataException(
            $"Invalid CSV data was found near row {context.Context.Parser?.Row ?? 0}.")
    };

    private static bool HasUtf8Bom(string path)
    {
        Span<byte> prefix = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.Read(prefix) == prefix.Length &&
            prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    private static void PreserveUnixMode(string sourcePath, string temporaryPath)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(sourcePath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed record CsvFileMetadata(
        IReadOnlyList<string> Headers,
        int EmailColumnIndex,
        int RowCount,
        bool HasUtf8Bom);

    private sealed record CsvRowInput(int Sequence, string[] Fields, string Email);
    private sealed record CsvRowResult(CsvRowInput Input, EmailValidationResult Result, DateTimeOffset CompletedAt);
}

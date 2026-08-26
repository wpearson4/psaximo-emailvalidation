using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Application;

public enum DetectedColumnType { Unknown, Email }

public sealed record ColumnProfile(
    string ColumnName,
    int SampleCount,
    int NonEmptySampleCount,
    int EmailLikeCount,
    int InvalidEmailLikeCount);

public sealed record ColumnTypeDetectionResult(
    string ColumnName,
    DetectedColumnType DetectedType,
    double Confidence,
    double EmailRatio,
    int SampleCount,
    int NonEmptySampleCount,
    int EmailLikeCount,
    int InvalidEmailLikeCount);

public sealed record FileColumnProfileResult(
    IReadOnlyList<ColumnTypeDetectionResult> Columns,
    int RowsInspected,
    bool InspectionLimitReached);

public interface IColumnTypeDetectionPolicy
{
    ColumnTypeDetectionResult Evaluate(ColumnProfile profile);
}

public interface IFileColumnProfiler
{
    Task<FileColumnProfileResult> ProfileAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default);
}

public sealed class EmailColumnTypeDetectionPolicy(IOptions<EmailValidationOptions> options)
    : IColumnTypeDetectionPolicy
{
    private readonly EmailColumnDetectionOptions _options = options.Value.ColumnDetection;

    public ColumnTypeDetectionResult Evaluate(ColumnProfile profile)
    {
        var evidence = profile.EmailLikeCount +
            profile.InvalidEmailLikeCount * _options.InvalidEmailShapeWeight;
        var ratio = profile.NonEmptySampleCount == 0 ? 0 : evidence / profile.NonEmptySampleCount;
        var hasHeaderSupport = IsEmailHeader(profile.ColumnName);
        var threshold = hasHeaderSupport
            ? _options.HeaderSupportedMinimumEmailRatio
            : _options.MinimumEmailRatio;
        var enoughData = profile.NonEmptySampleCount >= _options.MinimumNonEmptySamples;
        var enoughEmailEvidence = profile.EmailLikeCount + profile.InvalidEmailLikeCount >=
            _options.MinimumEmailLikeSamples;
        var detected = enoughData && enoughEmailEvidence && ratio >= threshold;
        var confidence = ratio == 0 ? 0 : Math.Min(1, ratio +
            (hasHeaderSupport ? _options.HeaderConfidenceBoost : 0));

        return new(
            profile.ColumnName,
            detected ? DetectedColumnType.Email : DetectedColumnType.Unknown,
            confidence,
            ratio,
            profile.SampleCount,
            profile.NonEmptySampleCount,
            profile.EmailLikeCount,
            profile.InvalidEmailLikeCount);
    }

    private static bool IsEmailHeader(string header)
    {
        var normalized = new string(header
            .Where(character => char.IsLetterOrDigit(character))
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.Contains("email", StringComparison.Ordinal);
    }
}

public sealed class EmailFileColumnProfiler(
    IEmailNormalizer normalizer,
    IColumnTypeDetectionPolicy policy,
    IOptions<EmailValidationOptions> options) : IFileColumnProfiler
{
    private readonly EmailColumnDetectionOptions _options = options.Value.ColumnDetection;

    public Task<FileColumnProfileResult> ProfileAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return ProfileJsonAsync(content, cancellationToken);
        return ProfileCsvAsync(content, cancellationToken);
    }

    private async Task<FileColumnProfileResult> ProfileCsvAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        using var textReader = new StreamReader(content, leaveOpen: true);
        using var csv = new CsvReader(textReader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            HeaderValidated = null,
            MissingFieldFound = null
        });

        if (!await csv.ReadAsync().ConfigureAwait(false))
            throw new InvalidDataException("The selected CSV is empty.");

        var rawHeaders = csv.Parser.Record ?? [];
        var profiles = CreateProfiles(rawHeaders, rawHeaders.Length);
        var rowsInspected = 0;
        while (rowsInspected < _options.MaximumRowsInspected &&
               await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowsInspected++;
            var row = csv.Parser.Record ?? [];
            EnsureColumns(profiles, rawHeaders, row.Length);
            SampleRow(profiles, row);
            if (profiles.Count > 0 && profiles.All(profile =>
                    profile.NonEmptySampleCount >= _options.MaximumNonEmptySamplesPerColumn))
                break;
        }

        if (rowsInspected == 0)
            throw new InvalidDataException("The selected CSV contains no data rows.");

        return BuildResult(profiles, rowsInspected,
            rowsInspected >= _options.MaximumRowsInspected);
    }

    private async Task<FileColumnProfileResult> ProfileJsonAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        var profiles = new List<MutableColumnProfile>();
        var profileIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rowsInspected = 0;
        try
        {
            await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<Dictionary<string, JsonElement>>(
                               content, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (record is null) continue;
                rowsInspected++;
                foreach (var field in record)
                {
                    if (!profileIndexes.TryGetValue(field.Key, out var index))
                    {
                        index = profiles.Count;
                        profileIndexes[field.Key] = index;
                        profiles.Add(new MutableColumnProfile(field.Key));
                    }
                    SampleValue(profiles[index], JsonValue(field.Value));
                }
                if (rowsInspected >= _options.MaximumRowsInspected) break;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The selected JSON file is invalid.", exception);
        }

        if (rowsInspected == 0)
            throw new InvalidDataException("The selected JSON file contains no data rows.");
        return BuildResult(profiles, rowsInspected,
            rowsInspected >= _options.MaximumRowsInspected);
    }

    private void SampleRow(List<MutableColumnProfile> profiles, string[] row)
    {
        for (var index = 0; index < profiles.Count; index++)
            SampleValue(profiles[index], index < row.Length ? row[index] : string.Empty);
    }

    private void SampleValue(MutableColumnProfile profile, string value)
    {
        profile.SampleCount++;
        var candidate = value.Trim();
        if (candidate.Length == 0 ||
            profile.NonEmptySampleCount >= _options.MaximumNonEmptySamplesPerColumn)
            return;

        profile.NonEmptySampleCount++;
        var normalization = normalizer.Normalize(candidate);
        if (normalization.IsValid)
            profile.EmailLikeCount++;
        else if (HasMalformedEmailShape(candidate))
            profile.InvalidEmailLikeCount++;
    }

    private FileColumnProfileResult BuildResult(
        IEnumerable<MutableColumnProfile> profiles,
        int rowsInspected,
        bool inspectionLimitReached)
    {
        var results = profiles.Select(profile => policy.Evaluate(new ColumnProfile(
            profile.ColumnName,
            profile.SampleCount,
            profile.NonEmptySampleCount,
            profile.EmailLikeCount,
            profile.InvalidEmailLikeCount))).ToArray();
        return new(results, rowsInspected, inspectionLimitReached);
    }

    private static List<MutableColumnProfile> CreateProfiles(
        IReadOnlyList<string> rawHeaders,
        int columnCount)
    {
        var profiles = new List<MutableColumnProfile>(columnCount);
        EnsureColumns(profiles, rawHeaders, columnCount);
        return profiles;
    }

    private static void EnsureColumns(
        List<MutableColumnProfile> profiles,
        IReadOnlyList<string> rawHeaders,
        int columnCount)
    {
        var used = profiles.Select(profile => profile.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = profiles.Count; index < columnCount; index++)
        {
            var baseName = index < rawHeaders.Count && !string.IsNullOrWhiteSpace(rawHeaders[index])
                ? rawHeaders[index].Trim()
                : $"Column {index + 1}";
            var name = baseName;
            for (var suffix = 2; !used.Add(name); suffix++) name = $"{baseName} ({suffix})";
            profiles.Add(new MutableColumnProfile(name));
        }
    }

    private static bool HasMalformedEmailShape(string value)
    {
        if (value.Length > 320 || value.Any(char.IsWhiteSpace)) return false;
        var separator = value.IndexOf('@');
        return separator > 0 && separator == value.LastIndexOf('@') && separator < value.Length - 1;
    }

    private static string JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.ToString()
    };

    private sealed class MutableColumnProfile(string columnName)
    {
        public string ColumnName { get; } = columnName;
        public int SampleCount { get; set; }
        public int NonEmptySampleCount { get; set; }
        public int EmailLikeCount { get; set; }
        public int InvalidEmailLikeCount { get; set; }
    }
}

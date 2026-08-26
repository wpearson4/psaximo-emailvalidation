using System.Globalization;
using System.Text;
using EmailValidation.Application;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class EmailColumnProfilingTests
{
    [Theory]
    [InlineData("Contact")]
    [InlineData("RandomName")]
    public async Task GenericHeader_WithEmailData_IsDetected(string header)
    {
        var result = await ProfileCsv($"{header},Name\njohn@example.com,John\njane@example.org,Jane\nbob@example.net,Bob\n");

        var detected = Assert.Single(result.Columns,
            column => column.DetectedType == DetectedColumnType.Email);
        Assert.Equal(header, detected.ColumnName);
        Assert.Equal(1, detected.EmailRatio);
    }

    [Fact]
    public async Task MisleadingEmailHeader_WithNumericData_IsNotDetected()
    {
        var result = await ProfileCsv("Email\n12345\n67890\nABC123\n");

        var profile = Assert.Single(result.Columns);
        Assert.Equal(DetectedColumnType.Unknown, profile.DetectedType);
        Assert.Equal(0, profile.Confidence);
    }

    [Fact]
    public async Task MixedValidBlankAndMalformedValues_RemainDetectable()
    {
        var result = await ProfileCsv(
            "Customer Contact\n" +
            "john@example.com\n\n" +
            "jane@example.com\n" +
            "bob@gmail\n" +
            "alice@exampl.com\n" +
            "invalid-address\n");

        var profile = Assert.Single(result.Columns);
        Assert.Equal(DetectedColumnType.Email, profile.DetectedType);
        Assert.Equal(3, profile.EmailLikeCount);
        Assert.Equal(1, profile.InvalidEmailLikeCount);
    }

    [Fact]
    public async Task SparseColumn_ContinuesPastBlankEarlyRows()
    {
        var csv = new StringBuilder("Contact,Name\n");
        for (var index = 0; index < 50; index++) csv.Append(",Blank\n");
        csv.Append("one@example.com,One\n");
        csv.Append("two@example.com,Two\n");
        csv.Append("three@example.com,Three\n");

        var result = await ProfileCsv(csv.ToString());

        Assert.Equal(DetectedColumnType.Email, result.Columns[0].DetectedType);
        Assert.Equal(3, result.Columns[0].NonEmptySampleCount);
        Assert.True(result.RowsInspected > 50);
    }

    [Fact]
    public async Task IncidentalEmails_InFreeTextColumn_AreNotDetected()
    {
        var csv = new StringBuilder("Notes\n");
        for (var index = 0; index < 98; index++) csv.Append("ordinary customer note\n");
        csv.Append("one@example.com\n");
        csv.Append("two@example.com\n");

        var result = await ProfileCsv(csv.ToString());

        Assert.Equal(DetectedColumnType.Unknown, Assert.Single(result.Columns).DetectedType);
    }

    [Fact]
    public async Task MultipleEmailColumns_AreReturnedWithoutUnrelatedColumns()
    {
        var result = await ProfileCsv(
            "BusinessContact,PersonalContact,CustomerName,Phone\n" +
            "one@business.com,one@personal.com,Ada,5551112222\n" +
            "two@business.com,two@personal.com,Grace,5552223333\n" +
            "three@business.com,three@personal.com,Linus,5553334444\n");

        Assert.Equal(
            ["BusinessContact", "PersonalContact"],
            result.Columns.Where(column => column.DetectedType == DetectedColumnType.Email)
                .Select(column => column.ColumnName));
    }

    [Fact]
    public async Task SamplingAndInspection_AreBounded()
    {
        var settings = new EmailValidationOptions
        {
            ColumnDetection = new EmailColumnDetectionOptions
            {
                MaximumNonEmptySamplesPerColumn = 5,
                MaximumRowsInspected = 20,
                MinimumNonEmptySamples = 3,
                MinimumEmailLikeSamples = 2
            }
        };
        var csv = new StringBuilder("Contact,Sparse\n");
        for (var index = 0; index < 1000; index++)
            csv.Append(CultureInfo.InvariantCulture, $"person{index}@example.com,\n");

        var result = await ProfileCsv(csv.ToString(), settings);

        Assert.Equal(20, result.RowsInspected);
        Assert.True(result.InspectionLimitReached);
        Assert.Equal(5, result.Columns[0].NonEmptySampleCount);
    }

    [Fact]
    public async Task JsonArray_IsProfiledWithoutChangingDetectionRules()
    {
        const string json = """
            [
              { "Contact": "one@example.com", "Name": "One" },
              { "Contact": "two@example.com", "Name": "Two" },
              { "Contact": "three@example.com", "Name": "Three" }
            ]
            """;
        var options = Options.Create(new EmailValidationOptions());
        var profiler = new EmailFileColumnProfiler(
            new EmailNormalizer(),
            new EmailColumnTypeDetectionPolicy(options),
            options);

        var result = await profiler.ProfileAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(json)),
            "customers.json");

        var detected = Assert.Single(result.Columns,
            column => column.DetectedType == DetectedColumnType.Email);
        Assert.Equal("Contact", detected.ColumnName);
    }

    private static Task<FileColumnProfileResult> ProfileCsv(
        string csv,
        EmailValidationOptions? settings = null)
    {
        var options = Options.Create(settings ?? new EmailValidationOptions());
        var profiler = new EmailFileColumnProfiler(
            new EmailNormalizer(),
            new EmailColumnTypeDetectionPolicy(options),
            options);
        return profiler.ProfileAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(csv)),
            "customers.csv");
    }
}

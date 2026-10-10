using System.Globalization;
using System.Text;
using CsvHelper;
using EmailValidation.Api;
using EmailValidation.Application;
using EmailValidation.Core;

namespace EmailValidation.Api.Tests;

public sealed class ValidationJobCsvExporterTests
{
    private static readonly string[] ExpectedHeaders = ["Contact", "Backup", "Name", "Email Validation Status",
        "Email Validation Detail", "Email Validation Confidence Reason", "Email Validation Domain Behavior", "Email Validation Date"];

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cleaning_PreservesRowsOtherColumnsAndUncertainEmails(bool clean, bool json)
    {
        var rows = new[] { "bad@example.test", "unknown@example.test", "likely@example.test", "pending@example.test", "", "changed@example.test" };
        var states = new[] { EmailValidationStatus.Invalid, EmailValidationStatus.Unknown, EmailValidationStatus.LikelyInvalid,
            EmailValidationStatus.Invalid, EmailValidationStatus.Invalid, EmailValidationStatus.Invalid };
        var items = rows.Select((email, position) => new ValidationJobItem("job", position,
            position == 5 ? "previous@example.test" : email, ValidationJobItemState.Completed,
            new EmailValidationResult { ValidationId = "v-" + position, Email = email, Status = states[position], Checks = new(),
                ResultState = position == 3 ? ValidationResultState.Provisional : ValidationResultState.Final })).ToArray();
        var service = new ResultsService(items);
        var exporter = new ValidationJobCsvExporter(service);
        var job = new ValidationJobSnapshot("job", DateTimeOffset.UtcNow, ValidationJobState.Completed,
            rows.Length, rows.Length, rows.Length - 1, 1, 0, DateTimeOffset.UtcNow, EmailColumn: "Contact");
        var sourceText = json ? System.Text.Json.JsonSerializer.Serialize(rows.Select(email => new { Contact = email, Backup = "keep@example.test", Name = "Smith, Jane" }))
            : "Contact,Backup,Name\r\n" + string.Join("\r\n", rows.Select(email => email + ",keep@example.test,\"Smith, Jane\""));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(sourceText));
        using var output = new MemoryStream();
        await exporter.WriteAsync(source, json ? "file.json" : "file.csv", job, output, clean);
        output.Position = 0;
        using var reader = new StreamReader(output);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        await csv.ReadAsync(); csv.ReadHeader();
        Assert.Equal(ExpectedHeaders, csv.HeaderRecord);
        var count = 0;
        while (await csv.ReadAsync())
        {
            Assert.Equal(csv.HeaderRecord!.Length, csv.Parser.Count);
            Assert.Equal(clean && count == 0 ? "" : rows[count], csv.GetField("Contact"));
            Assert.Equal("keep@example.test", csv.GetField("Backup"));
            Assert.Equal("Smith, Jane", csv.GetField("Name"));
            Assert.Equal(states[count].ToString(), csv.GetField("Email Validation Status"));
            count++;
        }
        Assert.Equal(rows.Length, count);
        Assert.Equal("bad@example.test", items[0].Email);
    }

    private sealed class ResultsService(ValidationJobItem[] items) : IValidationJobService
    {
        public Task<ValidationJobSnapshot> CreateAsync(CreateValidationJobRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ValidationJobSnapshot?> GetBySourceFileIdAsync(string sourceFileId, string? tenantId = null, CancellationToken cancellationToken = default) => Task.FromResult<ValidationJobSnapshot?>(null);
        public Task<ValidationJobSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<ValidationJobSnapshot?>(null);
        public Task<IReadOnlyList<ValidationJobItem>> GetResultsAsync(string jobId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ValidationJobItem>>(items.Skip(skip).Take(take).ToArray());
    }
}

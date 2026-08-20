using System.Text;
using EmailValidation.ConsoleApp;
using EmailValidation.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class CsvFileProcessorTests
{
    [Theory]
    [InlineData("email")]
    [InlineData("EMAIL")]
    [InlineData("Email Address")]
    [InlineData("email_address")]
    [InlineData("BUSINESS_EMAIL")]
    [InlineData("Business Email")]
    [InlineData("e-mail")]
    public void AutomaticColumnDetection_RecognizesCommonNames(string header)
    {
        Assert.Equal(0, CsvInput.ResolveEmailColumn([header], null));
    }

    [Fact]
    public void ExplicitColumn_TakesPrecedenceOverMultiplePlausibleColumns()
    {
        Assert.Equal(1, CsvInput.ResolveEmailColumn(
            ["Personal Email", "BUSINESS_EMAIL"], "business_email"));
    }

    [Fact]
    public async Task AmbiguousColumns_LeaveOriginalFileUnchanged()
    {
        await using var fixture = await CsvFixture.CreateAsync(
            "Business Email,Personal Email\na@example.com,b@example.com\n");
        var original = await File.ReadAllBytesAsync(fixture.Path);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Processor.ProcessAsync(fixture.Path, null, false, false, TextWriter.Null, default));

        Assert.Contains("Multiple email columns", exception.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Fact]
    public async Task MissingColumn_LeavesOriginalFileUnchanged()
    {
        await using var fixture = await CsvFixture.CreateAsync("Name,State\nJohn,TX\n");
        var original = await File.ReadAllBytesAsync(fixture.Path);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Processor.ProcessAsync(fixture.Path, null, false, false, TextWriter.Null, default));

        Assert.Contains("No email column", exception.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Fact]
    public async Task Processing_AddsResultColumns_PreservesOrderUnicodeAndQuotesReason()
    {
        await using var fixture = await CsvFixture.CreateAsync(
            "Name,Business Email,Note\n\"Smith, John\",slow@example.com,\"line one\nline two\"\nZoë,fast@example.com,café\n",
            new DeterministicValidator(delayByAddress: true), hasBom: true,
            concurrency: 2);

        var result = await fixture.Processor.ProcessAsync(
            fixture.Path, null, false, false, TextWriter.Null, default);
        var bytes = await File.ReadAllBytesAsync(fixture.Path);
        var output = Encoding.UTF8.GetString(bytes);

        Assert.Equal(2, result.RowsProcessed);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.StartsWith("\uFEFFName,Business Email,Note,Status,Confidence,Confidence Reason,Validation Date/Time", output, StringComparison.Ordinal);
        Assert.True(output.IndexOf("slow@example.com", StringComparison.Ordinal) < output.IndexOf("fast@example.com", StringComparison.Ordinal));
        Assert.Contains("\"Mailbox accepted, but catch-all behavior was detected.\"", output, StringComparison.Ordinal);
        Assert.Contains("95%", output, StringComparison.Ordinal);
        Assert.Contains("Zoë", output, StringComparison.Ordinal);
        Assert.Contains("\"line one\nline two\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingResultColumns_AreUpdatedWithoutDuplicates()
    {
        await using var fixture = await CsvFixture.CreateAsync(
            "Email,status,Confidence,Confidence Reason,Validation Date/Time\n" +
            "john@example.com,Unknown,20%,Old result,2026-01-01T00:00:00Z\n");

        await fixture.Processor.ProcessAsync(fixture.Path, null, false, false, TextWriter.Null, default);
        var lines = await File.ReadAllLinesAsync(fixture.Path);

        Assert.Equal(1, CountOccurrences(lines[0], "Confidence Reason"));
        Assert.Contains("LikelyValid,95%", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Old result", lines[1], StringComparison.Ordinal);
        Assert.Matches(@"20\d\d-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z", lines[1]);
    }

    [Fact]
    public async Task OutputWriteFailure_LeavesOriginalFileUnchanged()
    {
        await using var fixture = await CsvFixture.CreateAsync("Email\njohn@example.com\n");
        var original = await File.ReadAllBytesAsync(fixture.Path);
        var temporaryPath = fixture.Path + ".validation.tmp";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Processor.ProcessAsync(
                fixture.Path, null, false, false, TextWriter.Null, default));
            Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        }
        finally
        {
            Directory.Delete(temporaryPath);
        }
    }

    [Fact]
    public async Task Cancellation_LeavesOriginalFileUnchangedAndRemovesTemporaryFile()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await CsvFixture.CreateAsync(
            "Email\none@example.com\ntwo@example.com\nthree@example.com\n",
            new CancellingValidator(cancellation), concurrency: 1);
        var original = await File.ReadAllBytesAsync(fixture.Path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.ProcessAsync(
            fixture.Path, null, false, false, TextWriter.Null, cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        Assert.False(File.Exists(fixture.Path + ".validation.tmp"));
    }

    private static int CountOccurrences(string value, string match) =>
        (value.Length - value.Replace(match, string.Empty, StringComparison.OrdinalIgnoreCase).Length) / match.Length;

    private sealed class DeterministicValidator(bool delayByAddress = false) : IEmailValidator
    {
        public async Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (delayByAddress)
                await Task.Delay(email.StartsWith("slow", StringComparison.Ordinal) ? 50 : 1, cancellationToken);
            return Result(email);
        }
    }

    private sealed class CancellingValidator(CancellationTokenSource cancellation) : IEmailValidator
    {
        private int _calls;

        public Task<EmailValidationResult> ValidateAsync(
            string email,
            EmailValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 2) cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result(email));
        }
    }

    private static EmailValidationResult Result(string email) => new()
    {
        Email = email,
        NormalizedEmail = email,
        Status = EmailValidationStatus.LikelyValid,
        Confidence = 0.95,
        ConfidenceReason = "Mailbox accepted, but catch-all behavior was detected.",
        Checks = new EmailValidationChecks { SyntaxValid = true, DomainExists = true, MxPresent = true }
    };

    private sealed class CsvFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private CsvFixture(string directory, string path, CsvFileProcessor processor)
        {
            _directory = directory;
            Path = path;
            Processor = processor;
        }

        public string Path { get; }
        public CsvFileProcessor Processor { get; }

        public static async Task<CsvFixture> CreateAsync(
            string content,
            IEmailValidator? validator = null,
            bool hasBom = false,
            int concurrency = 2)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"email-validation-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "contacts.csv");
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(hasBom));
            var processor = new CsvFileProcessor(
                validator ?? new DeterministicValidator(),
                Options.Create(new EmailValidationOptions
                {
                    Smtp = new SmtpOptions { GlobalConcurrency = concurrency }
                }),
                NullLogger<CsvFileProcessor>.Instance);
            return new(directory, path, processor);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}

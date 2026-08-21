using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.ConsoleApp;

internal sealed class ConsoleApplication(
    IEmailValidator validator,
    IDnsMailResolver dnsResolver,
    IOptions<EmailValidationOptions> options,
    IProbeSenderPool senderPool,
    CsvFileProcessor csvFileProcessor)
{
    private readonly EmailValidationOptions _options = options.Value;

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var parsed = CliOptions.Parse(args);
            if (parsed.Live)
            {
                await senderPool.InitializeAsync(cancellationToken);
                if (parsed.Verbose) await WriteProbeSenderDiagnosticsAsync(senderPool.GetSnapshot());
            }
            return parsed.Command switch
            {
                "validate" => await ValidateCommandAsync(parsed, cancellationToken),
                "file" => await FileCommandAsync(parsed, cancellationToken),
                "interactive" => await InteractiveCommandAsync(parsed, cancellationToken),
                "diagnostics" => await DiagnosticsCommandAsync(parsed, cancellationToken),
                "help" => ShowHelp(),
                _ => throw new ArgumentException($"Unknown command: {parsed.Command}")
            };
        }
        catch (OperationCanceledException)
        {
            await System.Console.Error.WriteLineAsync("Operation cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await System.Console.Error.WriteLineAsync($"Error: {exception.Message}");
            return 2;
        }
    }

    private async Task WriteProbeSenderDiagnosticsAsync(ProbeSenderPoolSnapshot snapshot)
    {
        await System.Console.Error.WriteLineAsync(string.Join(Environment.NewLine,
        [
            "Probe Sender Pool",
            $"Source: {snapshot.Source}",
            $"Index: {snapshot.Index}",
            $"Query Limit: {snapshot.QueryLimit}",
            $"Candidates Retrieved: {snapshot.CandidatesRetrieved}",
            $"Usable: {snapshot.Usable}",
            $"Active Sender: {snapshot.ActiveSender ?? "None"}",
            $"Rotation Policy: maximum {_options.ProbeSenderRotation.MaxValidationsPerSender} validations / {_options.ProbeSenderRotation.MaxActiveMinutes} minutes",
            $"Elasticsearch Query Duration: {snapshot.LastQueryDuration.TotalMilliseconds:0} ms"
        ]));
    }

    private async Task<int> ValidateCommandAsync(CliOptions parsed, CancellationToken cancellationToken)
    {
        if (parsed.Values.Count == 0) throw new ArgumentException("Provide at least one email address.");
        var results = await ValidateBatchAsync(parsed.Values, parsed, showProgress: false, cancellationToken);
        await WriteOutputAsync(ResultFormatter.Format(results, parsed.Format, results.Count == 1), parsed.OutputPath, cancellationToken);
        return results.Any(result => result.Status == EmailValidationStatus.Invalid) ? 1 : 0;
    }

    private async Task<int> FileCommandAsync(CliOptions parsed, CancellationToken cancellationToken)
    {
        if (parsed.Values.Count != 1) throw new ArgumentException("Usage: file <path.csv> [--column name]");
        try
        {
            var result = await csvFileProcessor.ProcessAsync(
                parsed.Values[0], parsed.ColumnSpecified ? parsed.Column ?? string.Empty : null,
                parsed.Live, parsed.Verbose,
                System.Console.Error, cancellationToken);
            await System.Console.Error.WriteLineAsync(FormatFileSummary(result));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await System.Console.Error.WriteLineAsync("Validation cancelled. Original CSV was not modified.");
            return 130;
        }
    }

    private async Task<int> InteractiveCommandAsync(CliOptions parsed, CancellationToken cancellationToken)
    {
        await System.Console.Out.WriteLineAsync("Email Validation interactive mode. Enter an address, or 'quit'.");
        while (!cancellationToken.IsCancellationRequested)
        {
            await System.Console.Out.WriteAsync("> ");
            var input = await System.Console.In.ReadLineAsync(cancellationToken);
            if (input is null || string.Equals(input.Trim(), "quit", StringComparison.OrdinalIgnoreCase)) break;
            if (string.IsNullOrWhiteSpace(input)) continue;
            var result = await validator.ValidateAsync(input, new EmailValidationRequest(parsed.Live, parsed.Verbose), cancellationToken);
            await System.Console.Out.WriteLineAsync(ResultFormatter.Format([result], parsed.Format, true));
        }
        return 0;
    }

    private async Task<int> DiagnosticsCommandAsync(CliOptions parsed, CancellationToken cancellationToken)
    {
        if (parsed.Values.Count != 1 || !string.Equals(parsed.Values[0], "smtp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Usage: diagnostics smtp");

        const string diagnosticDomain = "gmail.com";
        var dns = await dnsResolver.ResolveAsync(diagnosticDomain, cancellationToken);
        var mx = dns.MxRecords.OrderBy(record => record.Preference).FirstOrDefault()?.Host;
        if (mx is null)
        {
            await System.Console.Out.WriteLineAsync($"Outbound SMTP connection unavailable: could not resolve a diagnostic MX ({dns.Status}).");
            return 1;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.Smtp.ConnectionTimeoutSeconds)));
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(mx, 25, timeout.Token);
            stopwatch.Stop();
            await System.Console.Out.WriteLineAsync($"Outbound SMTP connectivity available ({mx}:25, {stopwatch.ElapsedMilliseconds} ms). No mailbox was probed.");
            return 0;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            await System.Console.Out.WriteLineAsync($"Outbound SMTP connection blocked / unavailable ({exception.Message}).");
            return 1;
        }
    }

    private async Task<IReadOnlyList<EmailValidationResult>> ValidateBatchAsync(
        IReadOnlyList<string> emails,
        CliOptions parsed,
        bool showProgress,
        CancellationToken cancellationToken)
    {
        var results = new EmailValidationResult[emails.Count];
        var concurrency = Math.Max(1, _options.Smtp.GlobalConcurrency);
        using var gate = new SemaphoreSlim(concurrency);
        var completed = 0;
        var tasks = emails.Select(async (email, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[index] = await validator.ValidateAsync(email, new EmailValidationRequest(parsed.Live, parsed.Verbose), cancellationToken);
                if (showProgress)
                {
                    var current = Interlocked.Increment(ref completed);
                    await System.Console.Error.WriteLineAsync($"Validated {current}/{emails.Count}");
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task WriteOutputAsync(string output, string? path, CancellationToken cancellationToken)
    {
        if (path is null) await System.Console.Out.WriteLineAsync(output);
        else
        {
            await File.WriteAllTextAsync(path, output + Environment.NewLine, cancellationToken);
            await System.Console.Error.WriteLineAsync($"Results written to {Path.GetFullPath(path)}");
        }
    }

    private static string FormatSummary(IReadOnlyList<EmailValidationResult> results, TimeSpan elapsed, bool verbose)
    {
        var domains = results.Select(result => result.NormalizedEmail?.Split('@').LastOrDefault())
            .Where(domain => domain is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var counts = Enum.GetValues<EmailValidationStatus>().ToDictionary(
            status => status,
            status => results.Count(result => result.Status == status));
        var smtpProbes = results.Count(result => result.Checks.Mailbox != SmtpMailboxStatus.NotAttempted);
        var catchAllProbes = results.Where(result => result.Checks.CatchAll != CatchAllStatus.NotAttempted)
            .Select(result => result.NormalizedEmail?.Split('@').LastOrDefault())
            .Where(domain => domain is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var cacheHits = results.Count(result => result.Diagnostics?.DomainCacheHit == true);
        if (cacheHits == 0) cacheHits = Math.Max(0, results.Count(result => result.NormalizedEmail is not null) - domains);
        var rate = elapsed.TotalSeconds <= 0 ? results.Count : results.Count / elapsed.TotalSeconds;

        var lines = new List<string>
        {
            "Validation complete",
            $"Emails:              {results.Count,8}",
            $"Valid:               {counts[EmailValidationStatus.Valid],8}",
            $"Likely Valid:        {counts[EmailValidationStatus.LikelyValid],8}",
            $"Catch-All:           {counts[EmailValidationStatus.CatchAll],8}",
            $"Risky:               {counts[EmailValidationStatus.Risky],8}",
            $"Likely Invalid:      {counts[EmailValidationStatus.LikelyInvalid],8}",
            $"Invalid:             {counts[EmailValidationStatus.Invalid],8}",
            $"Unknown:             {counts[EmailValidationStatus.Unknown],8}",
            $"Domains:             {domains,8}",
            $"DNS cache hits:      {cacheHits,8}",
            $"Catch-all probes:    {catchAllProbes,8}",
            $"SMTP probes:         {smtpProbes,8}",
            $"Duration:          {elapsed:hh\\:mm\\:ss}",
            $"Emails/sec:          {rate.ToString("0.00", CultureInfo.InvariantCulture),8}"
        };
        if (verbose)
        {
            lines.Add($"Microsoft domains:   {CountDomains(MailProvider.Microsoft365) + CountDomains(MailProvider.MicrosoftConsumer),8}");
            lines.Add($"Google domains:      {CountDomains(MailProvider.GoogleWorkspace),8}");
            lines.Add($"Proofpoint domains:  {CountDomains(MailProvider.Proofpoint),8}");
            lines.Add($"Mimecast domains:    {CountDomains(MailProvider.Mimecast),8}");
            lines.Add($"Generic providers:   {CountDomains(MailProvider.GenericSmtp),8}");
            lines.Add($"Catch-all likely:    {results.Count(result => result.Checks.CatchAll == CatchAllStatus.LikelyCatchAll),8}");
            lines.Add($"Verification blocked:{results.Count(result => result.ProviderValidation?.EffectiveCategory == SmtpResponseCategory.VerificationBlocked),8}");
            lines.Add($"Temporary failures:  {results.Count(result => result.ProviderValidation?.EffectiveCategory is SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited),8}");
        }
        return string.Join(Environment.NewLine, lines);

        int CountDomains(MailProvider provider) => results
            .Where(result => result.MailProvider == provider)
            .Select(result => result.NormalizedEmail?.Split('@').LastOrDefault())
            .Where(domain => domain is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static string FormatFileSummary(CsvProcessingResult result)
    {
        var counts = result.StatusCounts;
        return string.Join(Environment.NewLine,
        [
            "Validation complete",
            $"File: {result.FilePath}",
            $"Rows processed:      {result.RowsProcessed,8:N0}",
            $"Valid:              {counts[EmailValidationStatus.Valid],8:N0}",
            $"LikelyValid:        {counts[EmailValidationStatus.LikelyValid],8:N0}",
            $"CatchAll:           {counts[EmailValidationStatus.CatchAll],8:N0}",
            $"Risky:              {counts[EmailValidationStatus.Risky],8:N0}",
            $"Unknown:            {counts[EmailValidationStatus.Unknown],8:N0}",
            $"LikelyInvalid:      {counts[EmailValidationStatus.LikelyInvalid],8:N0}",
            $"Invalid:            {counts[EmailValidationStatus.Invalid],8:N0}",
            $"Duration:          {result.Duration:hh\\:mm\\:ss}",
            "Updated:",
            result.FilePath
        ]);
    }

    private static int ShowHelp()
    {
        System.Console.WriteLine("""
            Email Validation Service prototype

            Commands:
              validate <email> [email...] [--format text|json|csv] [--output path] [--verbose] [--live]
              file <emails.csv> [--column name] [--verbose] [--live]
              interactive [--verbose] [--live]
              diagnostics smtp

            DNS/MX checks run normally. SMTP and catch-all probes only run with --live.
            Live probing never sends message content and stops after RCPT TO.
            The file command automatically detects common email headers and safely updates the source CSV.
            """);
        return 0;
    }
}

using System.Diagnostics;
using System.Net.Sockets;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class SmtpMailboxProbe : ISmtpMailboxProbe
{
    private readonly SmtpOptions _options;
    private readonly ProbeSenderRotationOptions _senderOptions;
    private readonly ILogger<SmtpMailboxProbe> _logger;
    private readonly ISmtpProbeThrottle _throttle;
    private readonly ISmtpResponseClassifier _responseClassifier;
    private readonly IProbeSenderPool _senderPool;
    private readonly IProbeSenderAffinityStore _affinityStore;
    private readonly ISmtpSessionBudget _sessionBudget;
    private readonly IProviderPolicyResolver _providerPolicyResolver;

    public SmtpMailboxProbe(
        IOptions<EmailValidationOptions> options,
        ILogger<SmtpMailboxProbe> logger,
        ISmtpProbeThrottle throttle,
        ISmtpResponseClassifier responseClassifier,
        IProbeSenderPool senderPool,
        IProbeSenderAffinityStore affinityStore,
        ISmtpSessionBudget sessionBudget,
        IProviderPolicyResolver providerPolicyResolver)
    {
        _options = options.Value.Smtp;
        _senderOptions = options.Value.ProbeSenderRotation;
        _logger = logger;
        _throttle = throttle;
        _responseClassifier = responseClassifier;
        _senderPool = senderPool;
        _affinityStore = affinityStore;
        _sessionBudget = sessionBudget;
        _providerPolicyResolver = providerPolicyResolver;
    }

    public Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, CancellationToken cancellationToken = default) =>
        ProbeAsync(mxHost, recipient, MailProvider.Unknown, cancellationToken);

    public async Task<SmtpProbeResult> ProbeAsync(
        string mxHost,
        string recipient,
        MailProvider provider,
        CancellationToken cancellationToken = default)
    {
        var domain = recipient[(recipient.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();
        var throttleContext = new SmtpThrottleContext(domain, mxHost, provider);
        var availability = _throttle.GetAvailability(throttleContext);
        if (!availability.CanProbe)
            return CooldownActive(mxHost, provider, availability, attempts: 0);
        var affinity = _affinityStore.GetAffinity(domain);
        var excludedSenders = new HashSet<string>(
            _affinityStore.GetIncompatibleSenders(domain), StringComparer.OrdinalIgnoreCase);
        var sessions = 0;
        var sessionHistory = new List<SmtpSessionEvidence>();
        SmtpProbeResult? lastResult = null;
        string? previousSenderForDomainChange = null;
        var maximumSenders = Math.Max(1, _senderOptions.MaxSenderAttemptsPerValidation);
        var maximumRetries = EffectiveRetryLimit(
            _options.RetryCount, _providerPolicyResolver.Resolve(provider));
        for (var senderAttempt = 0; senderAttempt < maximumSenders; senderAttempt++)
        {
            var selected = await _senderPool.GetSenderAsync(new ProbeSenderContext(
                excludedSenders, domain, affinity?.Sender), cancellationToken);
            if (selected is null) break;
            if (affinity is null || !string.Equals(affinity.Sender, selected.Sender, StringComparison.OrdinalIgnoreCase))
            {
                var previous = affinity?.Sender ?? previousSenderForDomainChange;
                _affinityStore.SetAffinity(domain, selected.Sender);
                affinity = _affinityStore.GetAffinity(domain);
                previousSenderForDomainChange = null;
                if (previous is null)
                    _logger.LogDebug("Sender affinity created: {Domain} -> {ProbeSender}", domain, selected.Sender);
                else
                    _logger.LogInformation(
                        "Probe sender changed for {Domain}: {PreviousSender} -> {ProbeSender}. Reason: sender-specific MAIL FROM rejection",
                        domain, previous, selected.Sender);
            }

            var transientAttempt = 0;
            do
            {
                await using var throttleLease = await _throttle.AcquireAsync(throttleContext, cancellationToken);
                if (!throttleLease.Acquired)
                    return lastResult ?? CooldownActive(
                        mxHost, provider,
                        new(false, throttleLease.RetryAfter, throttleLease.Reason),
                        sessions);
                if (!_sessionBudget.TryConsume())
                {
                    _logger.LogWarning("SMTP session budget exhausted before probing {Domain}", domain);
                    return lastResult ?? BudgetExhausted(mxHost, provider);
                }

                transientAttempt++;
                sessions++;
                lastResult = await ProbeOnceAsync(
                    mxHost, recipient, provider, sessions, selected.Sender, cancellationToken);
                _throttle.RecordOutcome(throttleContext, lastResult);
                if (lastResult.SessionEvidence is not null)
                    sessionHistory.Add(lastResult.SessionEvidence);
                lastResult = lastResult with
                {
                    Attempts = sessions,
                    SessionHistory = sessionHistory.ToArray()
                };
                if (SmtpSenderFailureClassifier.ShouldTryAlternate(lastResult) ||
                    !IsTransient(lastResult.Status)) break;
                if (transientAttempt > maximumRetries)
                {
                    _throttle.RecordProviderRetry(provider, exhausted: true);
                    break;
                }

                _throttle.RecordProviderRetry(provider, exhausted: false);
                _logger.LogWarning(
                    "Transient SMTP result {Result}; domain/provider backoff will apply before retry {Attempt}",
                    lastResult.Status, transientAttempt);
            } while (true);

            var senderOutcome = SmtpSenderFailureClassifier.Classify(lastResult);
            var failureScope = SmtpSenderFailureClassifier.Scope(lastResult);
            await _senderPool.RecordOutcomeAsync(
                new ProbeSenderOutcome(selected.Sender, senderOutcome, lastResult, domain, failureScope),
                cancellationToken);
            if (!SmtpSenderFailureClassifier.ShouldTryAlternate(lastResult) ||
                !_senderOptions.RotateOnSenderSpecificFailure) return lastResult;

            _affinityStore.Remove(domain);
            _affinityStore.MarkIncompatible(domain, selected.Sender);
            previousSenderForDomainChange = selected.Sender;
            affinity = null;
            excludedSenders.Add(selected.Sender);
            _logger.LogWarning(
                "Probe sender {ProbeSender} was rejected for {Domain} at MAIL FROM; trying one healthy alternate sender",
                selected.Sender, domain);
        }

        return lastResult ?? BudgetExhausted(mxHost, provider);
    }

    private async Task<SmtpProbeResult> ProbeOnceAsync(
        string mxHost,
        string recipient,
        MailProvider provider,
        int attempt,
        string probeSender,
        CancellationToken cancellationToken)
    {
        var operationWatch = Stopwatch.StartNew();
        var connectionWatch = Stopwatch.StartNew();
        var currentCommand = SmtpCommand.Connect;
        var stages = new List<SmtpStageResult>();
        string? banner = null;
        string? ehloHost = null;
        var tlsAdvertised = false;
        try
        {
            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ConnectionTimeoutSeconds)));
            using var client = new TcpClient();
            await client.ConnectAsync(mxHost, 25, connectionTimeout.Token);
            connectionWatch.Stop();
            stages.Add(new SmtpStageResult(
                SmtpCommand.Connect, null, null, SmtpResponseCategory.Accepted,
                SmtpResponseTextClassification.Success, connectionWatch.Elapsed));
            currentCommand = SmtpCommand.Greeting;
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };
            var stageWatch = Stopwatch.StartNew();
            var greeting = await ReadResponseWithTimeoutAsync(reader, cancellationToken);
            stageWatch.Stop();
            banner = greeting.Text;
            var greetingEvidence = RecordStage(SmtpCommand.Greeting, greeting, stageWatch.Elapsed, provider, mxHost, attempt, stages);
            if (greeting.Code / 100 != 2)
                return BuildResult(greetingEvidence, connectionWatch.Elapsed, operationWatch.Elapsed,
                    provider, mxHost, attempt, stages, SmtpCommand.Greeting, banner, ehloHost, tlsAdvertised, probeSender);

            currentCommand = SmtpCommand.Ehlo;
            ehloHost = probeSender.Split('@').LastOrDefault();
            if (string.IsNullOrWhiteSpace(ehloHost)) ehloHost = $"{Environment.MachineName}.local";
            stageWatch.Restart();
            var ehlo = await CommandAsync(writer, reader, $"EHLO {ehloHost}", cancellationToken);
            stageWatch.Stop();
            var ehloEvidence = RecordStage(SmtpCommand.Ehlo, ehlo, stageWatch.Elapsed, provider, mxHost, attempt, stages);
            tlsAdvertised = ehlo.Text.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase);
            if (ehlo.Code / 100 != 2)
            {
                currentCommand = SmtpCommand.Helo;
                stageWatch.Restart();
                ehlo = await CommandAsync(writer, reader, $"HELO {ehloHost}", cancellationToken);
                stageWatch.Stop();
                ehloEvidence = RecordStage(SmtpCommand.Helo, ehlo, stageWatch.Elapsed, provider, mxHost, attempt, stages);
            }
            if (ehlo.Code / 100 != 2)
                return BuildResult(ehloEvidence, connectionWatch.Elapsed, operationWatch.Elapsed,
                    provider, mxHost, attempt, stages, currentCommand, banner, ehloHost, tlsAdvertised, probeSender);

            currentCommand = SmtpCommand.MailFrom;
            stageWatch.Restart();
            var sender = await CommandAsync(writer, reader, $"MAIL FROM:<{probeSender}>", cancellationToken);
            stageWatch.Stop();
            var senderEvidence = RecordStage(SmtpCommand.MailFrom, sender, stageWatch.Elapsed, provider, mxHost, attempt, stages);
            if (sender.Code / 100 != 2)
                return BuildResult(senderEvidence, connectionWatch.Elapsed, operationWatch.Elapsed,
                    provider, mxHost, attempt, stages, SmtpCommand.MailFrom, banner, ehloHost, tlsAdvertised, probeSender);

            currentCommand = SmtpCommand.RcptTo;
            stageWatch.Restart();
            var recipientResponse = await CommandAsync(writer, reader, $"RCPT TO:<{recipient}>", cancellationToken);
            stageWatch.Stop();
            var recipientEvidence = RecordStage(SmtpCommand.RcptTo, recipientResponse, stageWatch.Elapsed, provider, mxHost, attempt, stages);
            try
            {
                currentCommand = SmtpCommand.Rset;
                stageWatch.Restart();
                var reset = await CommandAsync(writer, reader, "RSET", cancellationToken);
                stageWatch.Stop();
                RecordStage(SmtpCommand.Rset, reset, stageWatch.Elapsed, provider, mxHost, attempt, stages);
                currentCommand = SmtpCommand.Quit;
                stageWatch.Restart();
                var quit = await CommandAsync(writer, reader, "QUIT", cancellationToken);
                stageWatch.Stop();
                RecordStage(SmtpCommand.Quit, quit, stageWatch.Elapsed, provider, mxHost, attempt, stages);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
            return BuildResult(recipientEvidence, connectionWatch.Elapsed, operationWatch.Elapsed,
                provider, mxHost, attempt, stages,
                recipientEvidence.Category == SmtpResponseCategory.Accepted ? null : SmtpCommand.RcptTo,
                banner, ehloHost, tlsAdvertised, probeSender);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExceptionalResult(SmtpResponseCategory.Timeout, currentCommand, "SMTP operation timed out", connectionWatch.Elapsed, operationWatch.Elapsed, provider, mxHost, attempt, stages, banner, ehloHost, tlsAdvertised, probeSender);
        }
        catch (SocketException exception)
        {
            return ExceptionalResult(SmtpResponseCategory.ConnectionRejected, currentCommand, exception.Message, connectionWatch.Elapsed, operationWatch.Elapsed, provider, mxHost, attempt, stages, banner, ehloHost, tlsAdvertised, probeSender);
        }
        catch (IOException exception)
        {
            return ExceptionalResult(SmtpResponseCategory.ProtocolFailure, currentCommand, exception.Message, connectionWatch.Elapsed, operationWatch.Elapsed, provider, mxHost, attempt, stages, banner, ehloHost, tlsAdvertised, probeSender);
        }
    }

    private async Task<SmtpResponse> CommandAsync(
        StreamWriter writer,
        StreamReader reader,
        string command,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.CommandTimeoutSeconds)));
        await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
        return await ReadResponseAsync(reader, timeout.Token);
    }

    private async Task<SmtpResponse> ReadResponseWithTimeoutAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.CommandTimeoutSeconds)));
        return await ReadResponseAsync(reader, timeout.Token);
    }

    private static async Task<SmtpResponse> ReadResponseAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        int? code = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("SMTP server closed the connection");
            lines.Add(line);
            if (line.Length < 3 || !int.TryParse(line[..3], out var parsed))
                throw new IOException("Malformed SMTP response");
            code ??= parsed;
            if (line.Length < 4 || line[3] != '-') break;
        }
        return new(code.Value, string.Join(" | ", lines));
    }

    internal static SmtpProbeResult Categorize(int code, string response)
    {
        var classifier = new SmtpResponseClassifier();
        var evidence = classifier.Classify(SmtpCommand.RcptTo, code, response, TimeSpan.Zero, MailProvider.Unknown, "test", 1);
        return SmtpResponseClassifier.ToProbeResult(evidence, TimeSpan.Zero);
    }

    private SmtpEvidence RecordStage(
        SmtpCommand command,
        SmtpResponse response,
        TimeSpan duration,
        MailProvider provider,
        string mxHost,
        int attempt,
        List<SmtpStageResult> stages)
    {
        var evidence = _responseClassifier.Classify(command, response.Code, response.Text, duration, provider, mxHost, attempt);
        stages.Add(ToStageResult(evidence, duration));
        return evidence;
    }

    private static SmtpProbeResult BuildResult(
        SmtpEvidence evidence,
        TimeSpan connectionDuration,
        TimeSpan elapsed,
        MailProvider provider,
        string mxHost,
        int attempt,
        IReadOnlyList<SmtpStageResult> stages,
        SmtpCommand? failedStage,
        string? banner,
        string? ehloHost,
        bool tlsAdvertised,
        string probeSender)
    {
        var session = new SmtpSessionEvidence(
            failedStage, stages.ToArray(), mxHost, elapsed, probeSender,
            SanitizeSessionText(banner), ehloHost, tlsAdvertised, false);
        return new SmtpProbeResult(
            SmtpResponseClassifier.ToMailboxStatus(evidence.Category),
            evidence.ResponseCode,
            evidence.SanitizedResponse,
            connectionDuration,
            attempt,
            evidence,
            session);
    }

    private SmtpProbeResult ExceptionalResult(
        SmtpResponseCategory category,
        SmtpCommand command,
        string detail,
        TimeSpan connectionDuration,
        TimeSpan elapsed,
        MailProvider provider,
        string mxHost,
        int attempt,
        List<SmtpStageResult> stages,
        string? banner,
        string? ehloHost,
        bool tlsAdvertised,
        string probeSender)
    {
        var classified = _responseClassifier.Classify(command, null, detail, elapsed, provider, mxHost, attempt);
        var evidence = classified with { Category = category };
        stages.Add(ToStageResult(evidence, elapsed));
        var session = new SmtpSessionEvidence(
            command, stages.ToArray(), mxHost, elapsed, probeSender,
            SanitizeSessionText(banner), ehloHost, tlsAdvertised, false);
        return new(
            SmtpResponseClassifier.ToMailboxStatus(category),
            null,
            evidence.SanitizedResponse,
            connectionDuration,
            attempt,
            evidence,
            session);
    }

    private static SmtpStageResult ToStageResult(SmtpEvidence evidence, TimeSpan duration) => new(
        evidence.Command,
        evidence.ResponseCode,
        evidence.EnhancedStatusCode,
        evidence.Category,
        evidence.TextClassification,
        duration,
        evidence.SanitizedResponse);

    private static string? SanitizeSessionText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : value.Length <= 300 ? value : value[..300];

    private static bool IsTransient(SmtpMailboxStatus status) =>
        status is SmtpMailboxStatus.TemporaryFailure or SmtpMailboxStatus.Timeout or SmtpMailboxStatus.ConnectionFailure;

    internal static int EffectiveRetryLimit(int globalRetryCount, ProviderPolicy policy) =>
        Math.Min(Math.Max(0, globalRetryCount), policy.MaxRetries);

    private SmtpProbeResult CooldownActive(
        string mxHost,
        MailProvider provider,
        ProviderThrottleAvailability availability,
        int attempts)
    {
        var detail = availability.RetryAfter is null
            ? "Provider policy cooldown is active; no SMTP session was attempted."
            : $"Provider policy cooldown is active until {availability.RetryAfter.Value:O}; no SMTP session was attempted.";
        var evidence = _responseClassifier.Classify(
            SmtpCommand.Connect, null, detail, TimeSpan.Zero, provider, mxHost, attempts) with
        {
            Category = SmtpResponseCategory.VerificationBlocked,
            TextClassification = SmtpResponseTextClassification.VerificationUnavailable
        };
        _logger.LogDebug(
            "SMTP probe skipped for {Provider}; provider cooldown remains active until {RetryAfter}",
            provider, availability.RetryAfter);
        return new SmtpProbeResult(
            SmtpMailboxStatus.Blocked, null, evidence.SanitizedResponse, TimeSpan.Zero,
            attempts, evidence);
    }

    private SmtpProbeResult BudgetExhausted(string mxHost, MailProvider provider)
    {
        var evidence = _responseClassifier.Classify(
            SmtpCommand.Connect, null, "SMTP session budget exhausted", TimeSpan.Zero,
            provider, mxHost, 0) with
        {
            Category = SmtpResponseCategory.Unknown
        };
        return new(SmtpMailboxStatus.Unknown, null, evidence.SanitizedResponse, TimeSpan.Zero, 0, evidence);
    }

    private sealed record SmtpResponse(int Code, string Text);
}

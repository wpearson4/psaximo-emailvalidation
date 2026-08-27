using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class SmtpMailboxProbe : ISmtpMailboxProbe
{
    private static readonly Meter Meter = new("EmailValidation.OutboundSmtp", "1.0.0");
    private static readonly Counter<long> BindFailures = Meter.CreateCounter<long>("outbound_identity_bind_failure_total");
    private readonly SmtpOptions _options;
    private readonly ProbeSenderRotationOptions _senderOptions;
    private readonly ILogger<SmtpMailboxProbe> _logger;
    private readonly ISmtpProbeThrottle _throttle;
    private readonly ISmtpResponseClassifier _responseClassifier;
    private readonly IProbeSenderPool _senderPool;
    private readonly IProbeSenderAffinityStore _affinityStore;
    private readonly ISmtpSessionBudget _sessionBudget;
    private readonly IProviderPolicyResolver _providerPolicyResolver;
    private readonly IOutboundIdentitySelector? _outboundIdentitySelector;
    private readonly IOutboundIdentityHealthStore? _outboundIdentityHealthStore;
    private readonly ISmtpConnectionFactory _connectionFactory;
    private readonly ISmtpReputationProtection? _reputationProtection;
    private readonly OutboundIdentityOptions _outboundIdentityOptions;
    private readonly SmtpReputationProtectionOptions _reputationOptions;
    private readonly string _strategyVersion;
    private readonly string _classificationVersion;
    private readonly SmtpResponseIntelligenceMode _intelligenceMode;

    public SmtpMailboxProbe(
        IOptions<EmailValidationOptions> options,
        ILogger<SmtpMailboxProbe> logger,
        ISmtpProbeThrottle throttle,
        ISmtpResponseClassifier responseClassifier,
        IProbeSenderPool senderPool,
        IProbeSenderAffinityStore affinityStore,
        ISmtpSessionBudget sessionBudget,
        IProviderPolicyResolver providerPolicyResolver,
        IOutboundIdentitySelector? outboundIdentitySelector = null,
        IOutboundIdentityHealthStore? outboundIdentityHealthStore = null,
        ISmtpConnectionFactory? connectionFactory = null,
        ISmtpReputationProtection? reputationProtection = null)
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
        _outboundIdentitySelector = outboundIdentitySelector;
        _outboundIdentityHealthStore = outboundIdentityHealthStore;
        _connectionFactory = connectionFactory ?? new SmtpConnectionFactory();
        _reputationProtection = reputationProtection;
        _outboundIdentityOptions = options.Value.OutboundIdentities;
        _reputationOptions = options.Value.SmtpReputationProtection;
        _strategyVersion = options.Value.Policy.ProviderStrategyVersion;
        _classificationVersion = options.Value.SmtpResponseIntelligence.ClassificationVersion;
        _intelligenceMode = options.Value.SmtpResponseIntelligence.Mode;
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
        OutboundIdentity? outboundIdentity = null;
        if (_outboundIdentityOptions.Enabled)
        {
            var selection = await (_outboundIdentitySelector ?? throw new InvalidOperationException(
                "Outbound identity selection is enabled but no selector is registered.")).SelectAsync(
                new(domain, provider), cancellationToken).ConfigureAwait(false);
            if (!selection.Selected)
                return IdentityUnavailable(mxHost, provider, selection);
            outboundIdentity = selection.Identity;
            _logger.LogInformation(
                "Outbound identity {OutboundIdentityId} selected for provider {Provider} domain {Domain}",
                outboundIdentity!.IdentityId, provider, domain);
        }
        throttleContext = throttleContext with { OutboundIp = outboundIdentity?.Address.ToString() };
        var reputationContext = new SmtpReputationBudgetContext(
            recipient.Trim().ToLowerInvariant(), domain, provider,
            outboundIdentity?.IdentityId, outboundIdentity?.Address.ToString(), mxHost);
        SmtpReputationEvidence? reputation = null;
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
                if (reputation is null && _reputationProtection is not null)
                {
                    reputation = await EvaluateReputationSafelyAsync(
                        reputationContext, cancellationToken).ConfigureAwait(false);
                    if (reputation.SuppressSmtp)
                        return ReputationDeferred(mxHost, provider, reputation);
                }
                if (!_sessionBudget.TryConsume())
                {
                    _logger.LogWarning("SMTP session budget exhausted before probing {Domain}", domain);
                    return lastResult ?? BudgetExhausted(mxHost, provider);
                }

                transientAttempt++;
                sessions++;
                lastResult = await ProbeOnceAsync(
                    mxHost, recipient, provider, sessions, selected.Sender, outboundIdentity, cancellationToken);
                if (reputation is not null)
                    lastResult = WithReputation(lastResult, reputation);
                if (_reputationProtection is not null)
                {
                    await _reputationProtection.RecordAsync(new SmtpReputationObservation(
                        reputationContext,
                        lastResult.Evidence?.Category ?? SmtpResponseCategory.Unknown,
                        lastResult.Evidence?.Intelligence?.Reason,
                        ConnectionAttempted: true,
                        RcptAttempted: lastResult.SessionEvidence?.Stages.Any(
                            stage => stage.Stage == SmtpCommand.RcptTo) == true,
                        DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                }
                if (outboundIdentity is not null)
                    await RecordOutboundIdentityOutcomeAsync(
                        outboundIdentity, provider, lastResult, cancellationToken).ConfigureAwait(false);
                _throttle.RecordOutcome(throttleContext, lastResult);
                if (lastResult.SessionEvidence is not null)
                    sessionHistory.Add(lastResult.SessionEvidence);
                lastResult = lastResult with
                {
                    Attempts = sessions,
                    SessionHistory = sessionHistory.ToArray()
                };
                if (SmtpSenderFailureClassifier.ShouldTryAlternate(lastResult) ||
                    !IsTransient(lastResult.Status) || IsProviderPolicyOutcome(lastResult)) break;
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
        OutboundIdentity? outboundIdentity,
        CancellationToken cancellationToken)
    {
        var operationWatch = Stopwatch.StartNew();
        var connectionWatch = Stopwatch.StartNew();
        var currentCommand = SmtpCommand.Connect;
        var stages = new List<SmtpStageResult>();
        var observation = ObservationContext(recipient, probeSender, outboundIdentity);
        string? banner = null;
        string? ehloHost = null;
        string? actualBoundSourceIp = null;
        var tlsAdvertised = false;
        try
        {
            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ConnectionTimeoutSeconds)));
            await using var connection = await _connectionFactory.ConnectAsync(
                mxHost, 25, outboundIdentity?.Address ?? IPAddress.Any, connectionTimeout.Token)
                .ConfigureAwait(false);
            actualBoundSourceIp = connection.LocalAddress;
            if (outboundIdentity is not null &&
                (!IPAddress.TryParse(actualBoundSourceIp, out var actualAddress) ||
                 !actualAddress.Equals(outboundIdentity.Address)))
            {
                connectionWatch.Stop();
                BindFailures.Add(1,
                    new("provider", provider.ToString()),
                    new("identity_id", outboundIdentity.IdentityId));
                return ExceptionalResult(SmtpResponseCategory.ConnectionRejected, SmtpCommand.Connect,
                    "The connected socket did not use the selected local source address",
                    connectionWatch.Elapsed, operationWatch.Elapsed, provider, mxHost, attempt, stages,
                    banner, ehloHost, tlsAdvertised, probeSender, outboundIdentity, observation,
                    localBindFailure: true, actualBoundSourceIp: actualBoundSourceIp);
            }
            connectionWatch.Stop();
            stages.Add(new SmtpStageResult(
                SmtpCommand.Connect, null, null, SmtpResponseCategory.Accepted,
                SmtpResponseTextClassification.Success, connectionWatch.Elapsed));
            currentCommand = SmtpCommand.Greeting;
            var stream = connection.Stream;
            var utf8 = new System.Text.UTF8Encoding(false, true);
            using var reader = new StreamReader(stream, utf8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, utf8, 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };
            var stageWatch = Stopwatch.StartNew();
            var greeting = await ReadResponseWithTimeoutAsync(reader, cancellationToken);
            stageWatch.Stop();
            banner = greeting.Text;
            var greetingEvidence = RecordStage(SmtpCommand.Greeting, greeting, stageWatch.Elapsed, provider, mxHost, attempt, stages, observation);
            if (greeting.Code / 100 != 2)
                return BuildResult(greetingEvidence, connectionWatch.Elapsed, operationWatch.Elapsed,
                    provider, mxHost, attempt, stages, SmtpCommand.Greeting, banner, ehloHost,
                    tlsAdvertised, probeSender, outboundIdentity, actualBoundSourceIp: actualBoundSourceIp);

            currentCommand = SmtpCommand.Ehlo;
            ehloHost = outboundIdentity?.EhloHostName ?? probeSender.Split('@').LastOrDefault();
            if (string.IsNullOrWhiteSpace(ehloHost)) ehloHost = $"{Environment.MachineName}.local";
            stageWatch.Restart();
            var ehlo = await CommandAsync(writer, reader, $"EHLO {ehloHost}", cancellationToken);
            stageWatch.Stop();
            var ehloEvidence = RecordStage(SmtpCommand.Ehlo, ehlo, stageWatch.Elapsed, provider, mxHost, attempt, stages, observation);
            tlsAdvertised = ehlo.Text.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase);
            var smtpUtf8Advertised = HasEhloCapability(ehlo.Text, "SMTPUTF8");
            if (ehlo.Code / 100 != 2)
            {
                currentCommand = SmtpCommand.Helo;
                stageWatch.Restart();
                ehlo = await CommandAsync(writer, reader, $"HELO {ehloHost}", cancellationToken);
                stageWatch.Stop();
                ehloEvidence = RecordStage(SmtpCommand.Helo, ehlo, stageWatch.Elapsed, provider, mxHost, attempt, stages, observation);
            }
            if (ehlo.Code / 100 != 2)
                return BuildResult(ehloEvidence, connectionWatch.Elapsed, operationWatch.Elapsed,
                    provider, mxHost, attempt, stages, currentCommand, banner, ehloHost,
                    tlsAdvertised, probeSender, outboundIdentity, actualBoundSourceIp: actualBoundSourceIp);

            var requiresSmtpUtf8 = recipient.Any(character => !char.IsAscii(character));
            if (requiresSmtpUtf8 && !smtpUtf8Advertised)
            {
                var unsupported = ehloEvidence with
                {
                    Category = SmtpResponseCategory.SmtpUtf8Unsupported,
                    TextClassification = SmtpResponseTextClassification.VerificationUnavailable,
                    SanitizedResponse = "The destination MX did not advertise SMTPUTF8; the internationalized recipient was not probed."
                };
                return BuildResult(unsupported, connectionWatch.Elapsed, operationWatch.Elapsed,
                    provider, mxHost, attempt, stages, SmtpCommand.RcptTo, banner, ehloHost,
                    tlsAdvertised, probeSender, outboundIdentity, smtpUtf8Advertised, requiresSmtpUtf8,
                    actualBoundSourceIp);
            }

            currentCommand = SmtpCommand.MailFrom;
            stageWatch.Restart();
            var sender = await CommandAsync(writer, reader,
                MailFromCommand(probeSender, requiresSmtpUtf8), cancellationToken);
            stageWatch.Stop();
            var senderEvidence = RecordStage(SmtpCommand.MailFrom, sender, stageWatch.Elapsed, provider, mxHost, attempt, stages, observation);
            if (sender.Code / 100 != 2)
                return BuildResult(senderEvidence, connectionWatch.Elapsed, operationWatch.Elapsed,
                    provider, mxHost, attempt, stages, SmtpCommand.MailFrom, banner, ehloHost,
                    tlsAdvertised, probeSender, outboundIdentity, actualBoundSourceIp: actualBoundSourceIp);

            currentCommand = SmtpCommand.RcptTo;
            stageWatch.Restart();
            var recipientResponse = await CommandAsync(writer, reader, $"RCPT TO:<{recipient}>", cancellationToken);
            stageWatch.Stop();
            var recipientEvidence = RecordStage(SmtpCommand.RcptTo, recipientResponse, stageWatch.Elapsed, provider, mxHost, attempt, stages, observation);
            try
            {
                currentCommand = SmtpCommand.Rset;
                stageWatch.Restart();
                var reset = await CommandAsync(writer, reader, "RSET", cancellationToken);
                stageWatch.Stop();
                RecordStage(SmtpCommand.Rset, reset, stageWatch.Elapsed, provider, mxHost, attempt, stages, observation);
                currentCommand = SmtpCommand.Quit;
                stageWatch.Restart();
                var quit = await CommandAsync(writer, reader, "QUIT", cancellationToken);
                stageWatch.Stop();
                RecordStage(SmtpCommand.Quit, quit, stageWatch.Elapsed, provider, mxHost, attempt, stages, observation);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
            return BuildResult(recipientEvidence, connectionWatch.Elapsed, operationWatch.Elapsed,
                provider, mxHost, attempt, stages,
                recipientEvidence.Category == SmtpResponseCategory.Accepted ? null : SmtpCommand.RcptTo,
                banner, ehloHost, tlsAdvertised, probeSender, outboundIdentity,
                smtpUtf8Advertised, requiresSmtpUtf8, actualBoundSourceIp);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExceptionalResult(SmtpResponseCategory.Timeout, currentCommand, "SMTP operation timed out",
                connectionWatch.Elapsed, operationWatch.Elapsed, provider, mxHost, attempt, stages,
                banner, ehloHost, tlsAdvertised, probeSender, outboundIdentity, observation,
                actualBoundSourceIp: actualBoundSourceIp);
        }
        catch (OutboundIdentityBindException)
        {
            BindFailures.Add(1,
                new("provider", provider.ToString()),
                new("identity_id", outboundIdentity?.IdentityId ?? "unbound"));
            return ExceptionalResult(SmtpResponseCategory.ConnectionRejected, SmtpCommand.Connect,
                "The configured local source address could not be bound", connectionWatch.Elapsed,
                operationWatch.Elapsed, provider, mxHost, attempt, stages, banner, ehloHost,
                tlsAdvertised, probeSender, outboundIdentity, observation, localBindFailure: true,
                actualBoundSourceIp: actualBoundSourceIp);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressNotAvailable)
        {
            BindFailures.Add(1,
                new("provider", provider.ToString()),
                new("identity_id", outboundIdentity?.IdentityId ?? "unbound"));
            return ExceptionalResult(SmtpResponseCategory.ConnectionRejected, currentCommand,
                "The configured local source address could not be bound", connectionWatch.Elapsed,
                operationWatch.Elapsed, provider, mxHost, attempt, stages, banner, ehloHost,
                tlsAdvertised, probeSender, outboundIdentity, observation, localBindFailure: true,
                actualBoundSourceIp: actualBoundSourceIp);
        }
        catch (SocketException exception)
        {
            return ExceptionalResult(SmtpResponseCategory.ConnectionRejected, currentCommand, exception.Message,
                connectionWatch.Elapsed, operationWatch.Elapsed, provider, mxHost, attempt, stages,
                banner, ehloHost, tlsAdvertised, probeSender, outboundIdentity, observation,
                actualBoundSourceIp: actualBoundSourceIp);
        }
        catch (IOException exception)
        {
            return ExceptionalResult(SmtpResponseCategory.ProtocolFailure, currentCommand, exception.Message,
                connectionWatch.Elapsed, operationWatch.Elapsed, provider, mxHost, attempt, stages,
                banner, ehloHost, tlsAdvertised, probeSender, outboundIdentity, observation,
                actualBoundSourceIp: actualBoundSourceIp);
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

    private SmtpEvidence RecordStage(
        SmtpCommand command,
        SmtpResponse response,
        TimeSpan duration,
        MailProvider provider,
        string mxHost,
        int attempt,
        List<SmtpStageResult> stages,
        SmtpResponseObservationContext observation)
    {
        var evidence = _responseClassifier.Classify(
            command, response.Code, response.Text, duration, provider, mxHost, attempt, observation);
        stages.Add(ToStageResult(evidence, duration));
        return evidence;
    }

    private SmtpProbeResult BuildResult(
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
        string probeSender,
        OutboundIdentity? outboundIdentity,
        bool smtpUtf8Advertised = false,
        bool smtpUtf8Required = false,
        string? actualBoundSourceIp = null)
    {
        var session = new SmtpSessionEvidence(
            failedStage, stages.ToArray(), mxHost, elapsed, probeSender,
            SanitizeSessionText(banner), ehloHost, tlsAdvertised, false,
            smtpUtf8Advertised, smtpUtf8Required,
            outboundIdentity?.IdentityId, outboundIdentity?.Address.ToString(),
            outboundIdentity?.InterfaceName,
            outboundIdentity is null ? null : _outboundIdentityOptions.SelectionAlgorithmVersion,
            ConfiguredSourceIp: outboundIdentity?.Address.ToString(),
            ActualBoundSourceIp: actualBoundSourceIp,
            ExpectedPtrHostName: outboundIdentity?.ExpectedPtrHostName,
            FcrDnsState: outboundIdentity?.DnsReadiness?.DnsState ?? outboundIdentity?.FcrDnsState,
            FcrDnsEvaluatedAtUtc: outboundIdentity?.DnsReadiness?.EvaluatedAtUtc,
            FcrDnsPolicyVersion: outboundIdentity?.DnsReadiness?.ValidationPolicyVersion);
        return new SmtpProbeResult(
            SmtpResponseClassifier.ToMailboxStatus(evidence.Category),
            evidence.ResponseCode,
            evidence.SanitizedResponse,
            connectionDuration,
            attempt,
            evidence,
            session)
        {
            Disposition = evidence.Category is SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.RateLimited or
                SmtpResponseCategory.SmtpUtf8Unsupported
                ? SmtpProbeDisposition.RemoteBlocked
                : SmtpProbeDisposition.Completed
        };
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
        string probeSender,
        OutboundIdentity? outboundIdentity,
        SmtpResponseObservationContext observation,
        bool localBindFailure = false,
        string? actualBoundSourceIp = null)
    {
        var classified = _responseClassifier.Classify(
            command, null, detail, elapsed, provider, mxHost, attempt, observation);
        var evidence = classified with { Category = category };
        stages.Add(ToStageResult(evidence, elapsed));
        var session = new SmtpSessionEvidence(
            command, stages.ToArray(), mxHost, elapsed, probeSender,
            SanitizeSessionText(banner), ehloHost, tlsAdvertised, false,
            OutboundIdentityId: outboundIdentity?.IdentityId,
            SourceAddress: outboundIdentity?.Address.ToString(),
            InterfaceName: outboundIdentity?.InterfaceName,
            SelectionAlgorithmVersion: outboundIdentity is null
                ? null
                : _outboundIdentityOptions.SelectionAlgorithmVersion,
            ConfiguredSourceIp: outboundIdentity?.Address.ToString(),
            ActualBoundSourceIp: actualBoundSourceIp,
            ExpectedPtrHostName: outboundIdentity?.ExpectedPtrHostName,
            FcrDnsState: outboundIdentity?.DnsReadiness?.DnsState ?? outboundIdentity?.FcrDnsState,
            FcrDnsEvaluatedAtUtc: outboundIdentity?.DnsReadiness?.EvaluatedAtUtc,
            FcrDnsPolicyVersion: outboundIdentity?.DnsReadiness?.ValidationPolicyVersion);
        return new SmtpProbeResult(
            SmtpResponseClassifier.ToMailboxStatus(category),
            null,
            evidence.SanitizedResponse,
            connectionDuration,
            attempt,
            evidence,
            session)
        {
            LocalBindFailure = localBindFailure,
            Disposition = category is SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.RateLimited
                ? SmtpProbeDisposition.RemoteBlocked
                : SmtpProbeDisposition.Completed
        };
    }

    private static SmtpStageResult ToStageResult(SmtpEvidence evidence, TimeSpan duration) => new(
        evidence.Command,
        evidence.ResponseCode,
        evidence.EnhancedStatusCode,
        evidence.Category,
        evidence.TextClassification,
        duration,
        evidence.SanitizedResponse,
        evidence.Intelligence,
        evidence.Decision,
        evidence.IntelligenceMode);

    private static string? SanitizeSessionText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : value.Length <= 300 ? value : value[..300];

    private SmtpResponseObservationContext ObservationContext(
        string recipient,
        string probeSender,
        OutboundIdentity? outboundIdentity)
    {
        var separator = recipient.LastIndexOf('@');
        var recipientDomain = separator >= 0 && separator < recipient.Length - 1
            ? recipient[(separator + 1)..].Trim().ToLowerInvariant()
            : null;
        var senderHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(probeSender.Trim().ToLowerInvariant()))).ToLowerInvariant();
        return new(RecipientDomain: recipientDomain, OutboundIdentityId: outboundIdentity?.IdentityId,
            SenderIdentityId: senderHash,
            ObservedAtUtc: DateTimeOffset.UtcNow, StrategyVersion: _strategyVersion);
    }

    internal static bool HasEhloCapability(string response, string capability) =>
        response.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 4 && int.TryParse(line[..3], out _) ? line[4..].Trim() : line.Trim())
            .Any(line => string.Equals(
                line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                capability,
                StringComparison.OrdinalIgnoreCase));

    internal static string MailFromCommand(string sender, bool requiresSmtpUtf8) =>
        $"MAIL FROM:<{sender}>{(requiresSmtpUtf8 ? " SMTPUTF8" : string.Empty)}";

    private static bool IsTransient(SmtpMailboxStatus status) =>
        status is SmtpMailboxStatus.TemporaryFailure or SmtpMailboxStatus.Timeout or SmtpMailboxStatus.ConnectionFailure;

    private static bool IsProviderPolicyOutcome(SmtpProbeResult result) =>
        result.Evidence?.Category is SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.RateLimited;

    private async Task RecordOutboundIdentityOutcomeAsync(
        OutboundIdentity identity,
        MailProvider provider,
        SmtpProbeResult result,
        CancellationToken cancellationToken)
    {
        var decision = result.Evidence?.Decision;
        var scope = result.LocalBindFailure
            ? SmtpCooldownScope.SourceIp
            : decision?.CooldownScope ?? SmtpCooldownScope.None;
        var impact = result.LocalBindFailure
            ? SmtpHealthImpact.PermanentFailure
            : decision?.HealthImpact ?? (result.Evidence?.Category == SmtpResponseCategory.Accepted
                ? SmtpHealthImpact.Success
                : SmtpHealthImpact.None);
        await (_outboundIdentityHealthStore ?? throw new InvalidOperationException(
            "Outbound identity selection is enabled but no health store is registered.")).RecordAsync(new OutboundIdentityOutcome(
            identity.IdentityId,
            provider,
            result.Evidence?.Category ?? SmtpResponseCategory.Unknown,
            scope,
            impact,
            DateTimeOffset.UtcNow,
            result.RetryAfter,
            result.LocalBindFailure
                ? "LocalBindFailure"
                : result.Evidence?.Intelligence?.Reason.ToString(),
            result.LocalBindFailure), cancellationToken).ConfigureAwait(false);
    }

    private SmtpProbeResult IdentityUnavailable(
        string mxHost,
        MailProvider provider,
        OutboundIdentitySelectionResult selection)
    {
        var detail = $"No eligible outbound identity is available for provider group '{selection.ProviderGroup}' ({selection.Reason}).";
        var evidence = _responseClassifier.Classify(
            SmtpCommand.Connect, null, detail, TimeSpan.Zero, provider, mxHost, 0) with
        {
            Category = SmtpResponseCategory.LocalCooldown,
            TextClassification = SmtpResponseTextClassification.VerificationUnavailable
        };
        var normalizedReason = selection.Reason switch
        {
            OutboundIdentitySelectionReason.NoDnsReadyIdentities =>
                SmtpNormalizedReason.OutboundIdentityDnsNotReady,
            OutboundIdentitySelectionReason.InvalidIdentityConfiguration =>
                SmtpNormalizedReason.OutboundIdentityConfigurationInvalid,
            _ => SmtpNormalizedReason.NoEligibleOutboundIdentity
        };
        var intelligence = evidence.Intelligence is { } classified
            ? classified with { Reason = normalizedReason }
            : new SmtpResponseIntelligence(
                SmtpCommand.Connect, null, null, null, normalizedReason,
                SmtpEvidenceStrength.High, provider, _classificationVersion,
                normalizedReason.ToString(), evidence.SanitizedResponse,
                ObservedAtUtc: DateTimeOffset.UtcNow, StrategyVersion: _strategyVersion);
        evidence = evidence with
        {
            Intelligence = intelligence,
            IntelligenceMode = _intelligenceMode
        };
        return new(SmtpMailboxStatus.NotAttempted, null, evidence.SanitizedResponse,
            TimeSpan.Zero, 0, evidence)
        {
            Disposition = SmtpProbeDisposition.LocalCooldown,
            RetryAfter = DateTimeOffset.UtcNow.AddMinutes(5)
        };
    }

    private SmtpProbeResult ReputationDeferred(
        string mxHost,
        MailProvider provider,
        SmtpReputationEvidence reputation)
    {
        var detail = $"Live SMTP was deferred by reputation policy ({reputation.SuppressionReason ?? reputation.Decision.ToString()}).";
        var evidence = _responseClassifier.Classify(
            SmtpCommand.Connect, null, detail, TimeSpan.Zero, provider, mxHost, 0) with
        {
            Category = SmtpResponseCategory.LocalCooldown,
            TextClassification = SmtpResponseTextClassification.VerificationUnavailable,
            Reputation = reputation
        };
        var intelligence = evidence.Intelligence is { } classified
            ? classified with { Reason = SmtpNormalizedReason.ReputationPolicyDeferred }
            : new SmtpResponseIntelligence(
                SmtpCommand.Connect, null, null, null, SmtpNormalizedReason.ReputationPolicyDeferred,
                SmtpEvidenceStrength.High, provider, _classificationVersion,
                "smtp-reputation-policy-deferred", evidence.SanitizedResponse,
                ObservedAtUtc: DateTimeOffset.UtcNow, StrategyVersion: _strategyVersion);
        evidence = evidence with { Intelligence = intelligence, IntelligenceMode = _intelligenceMode };
        return new(SmtpMailboxStatus.NotAttempted, null, evidence.SanitizedResponse,
            TimeSpan.Zero, 0, evidence)
        {
            Disposition = SmtpProbeDisposition.LocalCooldown,
            RetryAfter = reputation.RetryAtUtc
        };
    }

    private static SmtpProbeResult WithReputation(
        SmtpProbeResult result,
        SmtpReputationEvidence reputation) => result with
    {
        Evidence = result.Evidence is null ? null : result.Evidence with { Reputation = reputation }
    };

    private async Task<SmtpReputationEvidence> EvaluateReputationSafelyAsync(
        SmtpReputationBudgetContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _reputationProtection!.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception,
                "SMTP reputation policy evaluation failed; applying mode-appropriate safe fallback");
            var enforced = _reputationOptions.Mode == SmtpReputationProtectionMode.Enforced;
            var now = DateTimeOffset.UtcNow;
            return new SmtpReputationEvidence
            {
                Decision = enforced ? SmtpProbeBudgetDecision.SafeFallback : SmtpProbeBudgetDecision.Allow,
                WouldDecision = SmtpProbeBudgetDecision.SafeFallback,
                Mode = _reputationOptions.Mode,
                CircuitState = SmtpReputationState.Degraded,
                RetryAtUtc = now.AddMinutes(Math.Max(1, _reputationOptions.FailureFallbackMinutes)),
                SuppressionReason = "ReputationPolicyEvaluationFailed",
                WouldHaveUsedIdentityId = context.OutboundIdentityId,
                EvaluatedAtUtc = now,
                PolicyVersion = _reputationOptions.PolicyVersion
            };
        }
    }

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
            Category = SmtpResponseCategory.LocalCooldown,
            TextClassification = SmtpResponseTextClassification.VerificationUnavailable
        };
        _logger.LogDebug(
            "SMTP probe skipped for {Provider}; provider cooldown remains active until {RetryAfter}",
            provider, availability.RetryAfter);
        return new SmtpProbeResult(
            SmtpMailboxStatus.NotAttempted, null, evidence.SanitizedResponse, TimeSpan.Zero,
            attempts, evidence)
        {
            Disposition = SmtpProbeDisposition.LocalCooldown,
            RetryAfter = availability.RetryAfter
        };
    }

    private SmtpProbeResult BudgetExhausted(string mxHost, MailProvider provider)
    {
        var evidence = _responseClassifier.Classify(
            SmtpCommand.Connect, null, "SMTP session budget exhausted", TimeSpan.Zero,
            provider, mxHost, 0) with
        {
            Category = SmtpResponseCategory.Unknown
        };
        return new(SmtpMailboxStatus.Unknown, null, evidence.SanitizedResponse, TimeSpan.Zero, 0, evidence)
        {
            Disposition = SmtpProbeDisposition.SessionBudgetExhausted
        };
    }

    private sealed record SmtpResponse(int Code, string Text);
}

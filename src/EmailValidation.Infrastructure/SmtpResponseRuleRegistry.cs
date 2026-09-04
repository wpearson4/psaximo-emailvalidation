using System.Text.RegularExpressions;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

internal sealed class SmtpResponseRuleRegistry
{
    private readonly TimeSpan _timeout;
    private readonly int _maximumResponseCharacters;
    private readonly CompiledRule[] _providerRules;
    private readonly CompiledRule[] _genericRules;
    private readonly Regex _email;
    private readonly Regex _ipv4;
    private readonly Regex _ipv6;
    private readonly Regex _timestamp;
    private readonly Regex _enhancedStatus;

    public SmtpResponseRuleRegistry(IOptions<EmailValidationOptions> options)
    {
        var settings = options.Value.SmtpResponseIntelligence;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(settings.RegexTimeoutMilliseconds, 10, 1000));
        _maximumResponseCharacters = Math.Clamp(settings.MaximumResponseCharacters, 256, 16_384);
        _email = Compile(@"[^\s<>]+@[^\s<>]+");
        _ipv4 = Compile(@"(?:\d{1,3}\.){3}\d{1,3}");
        _ipv6 = Compile(@"(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}");
        _timestamp = Compile(@"\b(?:\d{4}-\d{2}-\d{2}(?:[t ]\d{2}:\d{2}(?::\d{2})?(?:z|[+-]\d{2}:?\d{2})?)?|\d{1,2}:\d{2}:\d{2})\b");
        _enhancedStatus = Compile(@"([245]\.\d{1,3}\.\d{1,3})");

        _providerRules = CompileRules(ProviderRules());
        _genericRules = CompileRules(GenericRules());
        Validate(_providerRules.Concat(_genericRules));
    }

    public string? EnhancedStatus(string boundedResponse, out bool timedOut)
    {
        timedOut = false;
        try
        {
            var match = _enhancedStatus.Match(boundedResponse);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return null;
        }
    }

    public string Sanitize(string? response, out bool timedOut)
    {
        timedOut = false;
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;
        var bounded = response.Length <= _maximumResponseCharacters
            ? response
            : response[.._maximumResponseCharacters];
        try
        {
            var value = _email.Replace(bounded, "<redacted-email>");
            value = _timestamp.Replace(value, "<redacted-time>");
            value = _ipv4.Replace(value, "<redacted-ip>");
            return _ipv6.Replace(value, "<redacted-ip>");
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return "<response-redacted-regex-timeout>";
        }
    }

    public RuleMatch? MatchProvider(SmtpResponseClassificationContext context, string sanitized) =>
        Match(_providerRules, context, sanitized);

    public RuleMatch? MatchGeneric(SmtpResponseClassificationContext context, string sanitized) =>
        Match(_genericRules, context, sanitized);

    private static RuleMatch? Match(
        IReadOnlyList<CompiledRule> rules,
        SmtpResponseClassificationContext context,
        string sanitized)
    {
        foreach (var rule in rules)
        {
            if (rule.Stages.Count > 0 && !rule.Stages.Contains(context.Stage)) continue;
            if (rule.Providers.Count > 0 && !rule.Providers.Contains(context.Provider)) continue;
            try
            {
                if (rule.Pattern.IsMatch(sanitized))
                    return new(rule.Id, rule.Fingerprint, rule.Reason, rule.Strength);
            }
            catch (RegexMatchTimeoutException)
            {
                return new("regex_timeout", "unknown-provider-response",
                    SmtpNormalizedReason.UnknownProviderResponse, SmtpEvidenceStrength.None);
            }
        }
        return null;
    }

    private static CompiledRule[] CompileRules(IEnumerable<RuleDefinition> definitions) =>
        definitions.OrderByDescending(rule => rule.Stages.Count > 0)
            .ThenByDescending(rule => rule.Priority).ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .Select(rule => new CompiledRule(
                rule.Id, rule.Fingerprint, rule.Priority, rule.Reason, rule.Strength,
                rule.Stages, rule.Providers, CompileRule(rule.Pattern))).ToArray();

    private Regex Compile(string pattern) => new(
        pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        _timeout);

    private static Regex CompileRule(string pattern) => new(
        pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        // Rule input is capped at 16,384 characters and NonBacktracking guarantees
        // linear-time evaluation. A wall-clock timeout here can therefore only turn
        // host scheduling pauses into incorrect classifications.
        Regex.InfiniteMatchTimeout);

    private static void Validate(IEnumerable<CompiledRule> rules)
    {
        var duplicate = rules.GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate SMTP response intelligence rule id '{duplicate.Key}'.");
    }

    private static IEnumerable<RuleDefinition> ProviderRules()
    {
        yield return Rule("microsoft_rcpt_541_not_found", "microsoft-mailbox-not-found", SmtpNormalizedReason.MailboxNotFound,
            SmtpEvidenceStrength.High, @"recipient (?:address )?rejected|recipient not found|recipient does not exist",
            [SmtpCommand.RcptTo], [MailProvider.Microsoft365, MailProvider.MicrosoftConsumer], priority: 200);
        yield return Rule("google_4723_ip_policy", "google-ip-policy-block", SmtpNormalizedReason.IpPolicyBlock,
            SmtpEvidenceStrength.High, @"4\.7\.23|(?:sending|source) ip|ptr record|reverse dns",
            [], [MailProvider.GoogleWorkspace]);
        yield return Rule("google_4728_rate_limit", "google-rate-limit", SmtpNormalizedReason.ProviderRateLimit,
            SmtpEvidenceStrength.High, @"4\.7\.28|unusual rate|rate limit|too many",
            [], [MailProvider.GoogleWorkspace]);
        yield return Rule("microsoft_rate_limit", "microsoft-rate-limit", SmtpNormalizedReason.ProviderRateLimit,
            SmtpEvidenceStrength.High, @"rate limit|throttl|too many messages",
            [], [MailProvider.Microsoft365, MailProvider.MicrosoftConsumer]);
        yield return Rule("microsoft_connection_limit", "microsoft-connection-limit", SmtpNormalizedReason.ProviderConnectionLimit,
            SmtpEvidenceStrength.High, @"too many (?:concurrent )?connections|connection (?:frequency|limit)",
            [], [MailProvider.Microsoft365, MailProvider.MicrosoftConsumer]);
        yield return Rule("microsoft_policy", "microsoft-policy-block", SmtpNormalizedReason.PolicyBlock,
            SmtpEvidenceStrength.High, @"access denied|blocked by policy|tenant.*(?:block|policy)",
            [], [MailProvider.Microsoft365, MailProvider.MicrosoftConsumer]);
        yield return Rule("microsoft_sender", "microsoft-sender-rejected", SmtpNormalizedReason.SenderPolicyRejected,
            SmtpEvidenceStrength.High, @"sender.*(?:rejected|denied|blocked)",
            [SmtpCommand.MailFrom], [MailProvider.Microsoft365, MailProvider.MicrosoftConsumer]);
        yield return Rule("yahoo_ts01", "yahoo-ts01", SmtpNormalizedReason.ProviderRateLimit,
            SmtpEvidenceStrength.High, @"ts01", [], [MailProvider.Yahoo], priority: 200);
        yield return Rule("yahoo_ts02", "yahoo-ts02", SmtpNormalizedReason.ProviderRateLimit,
            SmtpEvidenceStrength.High, @"ts02", [], [MailProvider.Yahoo], priority: 200);
        yield return Rule("yahoo_ts03", "yahoo-ts03", SmtpNormalizedReason.ReputationBlocked,
            SmtpEvidenceStrength.High, @"ts03", [], [MailProvider.Yahoo], priority: 200);
        yield return Rule("yahoo_gl01", "yahoo-gl01", SmtpNormalizedReason.Greylisted,
            SmtpEvidenceStrength.High, @"gl01", [], [MailProvider.Yahoo], priority: 200);
        yield return Rule("yahoo_temporary", "yahoo-temporary-deferral", SmtpNormalizedReason.TemporaryFailure,
            SmtpEvidenceStrength.High, @"temporarily deferred|resources temporarily unavailable",
            [], [MailProvider.Yahoo]);
        yield return Rule("google_temporary", "google-temporary-deferral", SmtpNormalizedReason.TemporaryFailure,
            SmtpEvidenceStrength.High, @"temporarily deferred|try again later",
            [], [MailProvider.GoogleWorkspace]);
        yield return Rule("proofpoint_policy", "proofpoint-policy-rejection", SmtpNormalizedReason.PolicyBlock,
            SmtpEvidenceStrength.High, @"proofpoint|blocked by email protection policy|sender denied",
            [], [MailProvider.Proofpoint]);
        yield return Rule("mimecast_policy", "mimecast-policy-rejection", SmtpNormalizedReason.PolicyBlock,
            SmtpEvidenceStrength.High, @"mimecast|administrative prohibition|rejected by security policy",
            [], [MailProvider.Mimecast]);
    }

    private static IEnumerable<RuleDefinition> GenericRules()
    {
        yield return Rule("generic_greylist", "generic-greylist", SmtpNormalizedReason.Greylisted, SmtpEvidenceStrength.High,
            @"grey\s*list|gray\s*list|try again later.*grey");
        yield return Rule("generic_rate_limit", "generic-rate-limit", SmtpNormalizedReason.ProviderRateLimit, SmtpEvidenceStrength.Medium,
            @"rate[- ]?limit|too many (?:messages|requests)|throttl");
        yield return Rule("generic_connection_limit", "generic-connection-limit", SmtpNormalizedReason.ProviderConnectionLimit, SmtpEvidenceStrength.Medium,
            @"too many (?:concurrent )?connections|connection (?:frequency|limit)");
        yield return Rule("generic_routing_no_answer", "generic-routing-failure", SmtpNormalizedReason.RoutingPermanentFailure,
            SmtpEvidenceStrength.Medium, @"no answer from (?:host|destination)|unable to route", [SmtpCommand.RcptTo]);
        yield return Rule("generic_mailbox_full", "generic-mailbox-full", SmtpNormalizedReason.MailboxFull, SmtpEvidenceStrength.High,
            @"mailbox (?:is )?full|over quota|quota exceeded|storage allocation exceeded", [SmtpCommand.RcptTo]);
        yield return Rule("generic_mailbox_not_found", "generic-mailbox-not-found", SmtpNormalizedReason.MailboxNotFound, SmtpEvidenceStrength.High,
            @"user unknown|unknown user|no such user|does not exist|invalid recipient|unrouteable address", [SmtpCommand.RcptTo]);
        yield return Rule("generic_mailbox_disabled", "generic-mailbox-disabled", SmtpNormalizedReason.MailboxDisabled, SmtpEvidenceStrength.High,
            @"mailbox (?:is )?disabled|account disabled", [SmtpCommand.RcptTo]);
        yield return Rule("generic_mailbox_inactive", "generic-mailbox-inactive", SmtpNormalizedReason.MailboxInactive, SmtpEvidenceStrength.Medium,
            @"mailbox inactive|account inactive", [SmtpCommand.RcptTo]);
        yield return Rule("generic_sender_invalid", "generic-sender-invalid", SmtpNormalizedReason.SenderInvalid, SmtpEvidenceStrength.High,
            @"(?:sender|mail from|from address|return-path?).*(?:invalid|unknown|does not exist)", [SmtpCommand.MailFrom]);
        yield return Rule("generic_sender_policy", "generic-sender-policy-rejected", SmtpNormalizedReason.SenderPolicyRejected, SmtpEvidenceStrength.High,
            @"(?:sender|mail from|from address|return-path?).*(?:policy|not permitted|rejected|blocked)", [SmtpCommand.MailFrom]);
        yield return Rule("generic_ip_policy", "generic-ip-policy-block", SmtpNormalizedReason.IpPolicyBlock, SmtpEvidenceStrength.High,
            @"(?:source|sending|your) ip|ip address.*(?:blocked|denied)|reverse dns|ptr record");
        yield return Rule("generic_reputation", "generic-reputation-blocked", SmtpNormalizedReason.ReputationBlocked, SmtpEvidenceStrength.High,
            @"reputation|blacklist|spamhaus|block list|blocklist");
        yield return Rule("generic_relay_policy", "generic-policy-block", SmtpNormalizedReason.PolicyBlock, SmtpEvidenceStrength.Medium,
            @"relay(?:ing)? denied|unable to relay|authentication required|access denied|rejected by policy");
        yield return Rule("generic_verification_refused", "generic-verification-refused", SmtpNormalizedReason.VerificationRefused, SmtpEvidenceStrength.Medium,
            @"cannot verify|verification unavailable|vrfy disabled|will not verify");
        yield return Rule("generic_provider_unavailable", "generic-provider-unavailable", SmtpNormalizedReason.ProviderUnavailable, SmtpEvidenceStrength.Medium,
            @"service unavailable|server unavailable|system unavailable|temporarily unavailable");
        yield return Rule("generic_connection_refused", "generic-connection-refused", SmtpNormalizedReason.ConnectionFailure, SmtpEvidenceStrength.High,
            @"connection refused|actively refused|refused the connection", [SmtpCommand.Connect]);
        yield return Rule("generic_timeout", "generic-connection-timeout", SmtpNormalizedReason.ConnectionTimeout, SmtpEvidenceStrength.High,
            @"timed? out|timeout");
        yield return Rule("generic_tls_failure", "generic-tls-failure", SmtpNormalizedReason.TlsFailure, SmtpEvidenceStrength.High,
            @"tls|ssl|certificate|handshake");
        yield return Rule("generic_dns_failure", "generic-dns-failure", SmtpNormalizedReason.DnsFailure, SmtpEvidenceStrength.High,
            @"dns|name or service not known|host not found");
        yield return Rule("generic_protocol_failure", "generic-protocol-failure", SmtpNormalizedReason.ProtocolFailure, SmtpEvidenceStrength.Medium,
            @"malformed smtp response|protocol (?:error|failure)|closed the connection");
        yield return Rule("generic_temporary", "generic-temporary-failure", SmtpNormalizedReason.TemporaryFailure, SmtpEvidenceStrength.Low,
            @"try again|temporary|temporarily|transient");
    }

    private static RuleDefinition Rule(
        string id,
        string fingerprint,
        SmtpNormalizedReason reason,
        SmtpEvidenceStrength strength,
        string pattern,
        IReadOnlyCollection<SmtpCommand>? stages = null,
        IReadOnlyCollection<MailProvider>? providers = null,
        int priority = 100) =>
        new(id, fingerprint, priority, reason, strength, pattern,
            stages ?? Array.Empty<SmtpCommand>(), providers ?? Array.Empty<MailProvider>());

    internal sealed record RuleMatch(
        string Id, string Fingerprint, SmtpNormalizedReason Reason, SmtpEvidenceStrength Strength);
    private sealed record RuleDefinition(
        string Id,
        string Fingerprint,
        int Priority,
        SmtpNormalizedReason Reason,
        SmtpEvidenceStrength Strength,
        string Pattern,
        IReadOnlyCollection<SmtpCommand> Stages,
        IReadOnlyCollection<MailProvider> Providers);
    private sealed record CompiledRule(
        string Id,
        string Fingerprint,
        int Priority,
        SmtpNormalizedReason Reason,
        SmtpEvidenceStrength Strength,
        IReadOnlyCollection<SmtpCommand> Stages,
        IReadOnlyCollection<MailProvider> Providers,
        Regex Pattern);
}

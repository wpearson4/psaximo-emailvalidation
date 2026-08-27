using System.Text.RegularExpressions;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed partial class SmtpResponseClassifier : ISmtpResponseIntelligenceClassifier, ISmtpResponseClassifier
{
    private readonly SmtpResponseRuleRegistry _rules;
    private readonly string _classificationVersion;

    public SmtpResponseClassifier() : this(Options.Create(new EmailValidationOptions())) { }

    public SmtpResponseClassifier(IOptions<EmailValidationOptions> options)
    {
        _rules = new SmtpResponseRuleRegistry(options);
        _classificationVersion = options.Value.SmtpResponseIntelligence.ClassificationVersion;
    }

    public SmtpResponseIntelligence Classify(SmtpResponseClassificationContext context)
    {
        var sanitized = _rules.Sanitize(context.Response, out var sanitizationTimedOut);
        var enhanced = _rules.EnhancedStatus(sanitized, out var enhancedStatusTimedOut);
        var structured = ClassifyStructured(context, enhanced);
        var matched = structured ?? _rules.MatchProvider(context, sanitized) ?? _rules.MatchGeneric(context, sanitized);
        matched = ConstrainByReplyClass(context, matched);
        var reason = matched?.Reason ?? ClassifyReplyFallback(context, enhanced);
        var strength = matched?.Strength ?? (reason == SmtpNormalizedReason.UnknownProviderResponse
            ? SmtpEvidenceStrength.None
            : SmtpEvidenceStrength.Low);
        var fingerprint = matched?.Fingerprint ?? FallbackRuleId(reason);
        var observation = context.Observation;
        return new(context.Stage, context.ReplyCode, context.ReplyCode / 100, enhanced, reason, strength,
            context.Provider, _classificationVersion, fingerprint,
            string.IsNullOrEmpty(sanitized) ? null : sanitized,
            observation?.ValidationId, observation?.RecipientDomain, observation?.MxTopologyFingerprint,
            observation?.OutboundIdentityId, observation?.SenderIdentityId,
            observation?.ObservedAtUtc, observation?.StrategyVersion,
            sanitizationTimedOut || enhancedStatusTimedOut || matched?.Id == "regex_timeout");
    }

    public SmtpEvidence Classify(
        SmtpCommand command,
        int? responseCode,
        string? response,
        TimeSpan elapsed,
        MailProvider provider,
        string mxHost,
        int attempt = 1,
        SmtpResponseObservationContext? observation = null)
    {
        var sanitized = Sanitize(response);
        var enhanced = response is null ? null : EnhancedStatusRegex().Match(response).Groups[1].Value;
        if (enhanced?.Length == 0) enhanced = null;
        var text = sanitized?.ToLowerInvariant() ?? string.Empty;
        var textClass = IsMicrosoftRecipientRejection(command, responseCode, enhanced, text, provider)
            ? SmtpResponseTextClassification.RecipientDoesNotExist
            : ClassifyText(text, enhanced);
        var category = ClassifyCategory(command, responseCode, enhanced, textClass, provider);
        return new(
            command,
            responseCode,
            enhanced,
            category,
            textClass,
            (long)elapsed.TotalMilliseconds,
            provider,
            mxHost,
            attempt,
            DateTimeOffset.UtcNow,
            sanitized);
    }

    internal static SmtpProbeResult ToProbeResult(SmtpEvidence evidence, TimeSpan connectionDuration) => new(
        ToMailboxStatus(evidence.Category),
        evidence.ResponseCode,
        evidence.SanitizedResponse,
        connectionDuration,
        evidence.Attempt,
        evidence);

    internal static SmtpMailboxStatus ToMailboxStatus(SmtpResponseCategory category) => category switch
    {
        SmtpResponseCategory.Accepted or SmtpResponseCategory.GatewayAccepted => SmtpMailboxStatus.Accepted,
        SmtpResponseCategory.RecipientRejected => SmtpMailboxStatus.Rejected,
        SmtpResponseCategory.MailboxFull => SmtpMailboxStatus.MailboxFull,
        SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited => SmtpMailboxStatus.TemporaryFailure,
        SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.SmtpUtf8Unsupported => SmtpMailboxStatus.Blocked,
        SmtpResponseCategory.ConnectionRejected => SmtpMailboxStatus.ConnectionFailure,
        SmtpResponseCategory.Timeout => SmtpMailboxStatus.Timeout,
        _ => SmtpMailboxStatus.Unknown
    };

    private static SmtpResponseCategory ClassifyCategory(
        SmtpCommand command,
        int? code,
        string? enhanced,
        SmtpResponseTextClassification textClass,
        MailProvider provider)
    {
        if (code is null) return SmtpResponseCategory.Unknown;
        if ((provider is MailProvider.GoogleWorkspace or MailProvider.MicrosoftConsumer) &&
            (code is >= 400 and < 500 || enhanced?.StartsWith("4.", StringComparison.Ordinal) == true) &&
            enhanced?.StartsWith("4.7.", StringComparison.Ordinal) == true)
            return SmtpResponseCategory.RateLimited;
        if (command != SmtpCommand.RcptTo)
        {
            if (code is >= 200 and < 300) return SmtpResponseCategory.Accepted;
            if (code is >= 400 and < 500) return SmtpResponseCategory.TemporaryFailure;
            if (code is >= 500 and < 600)
                return command == SmtpCommand.Greeting
                    ? SmtpResponseCategory.ConnectionRejected
                    : SmtpResponseCategory.VerificationBlocked;
        }
        if (code == 252) return SmtpResponseCategory.MailboxUnknown;
        if (textClass == SmtpResponseTextClassification.Greylisting) return SmtpResponseCategory.Greylisted;
        if (textClass == SmtpResponseTextClassification.RateLimit) return SmtpResponseCategory.RateLimited;
        if (textClass == SmtpResponseTextClassification.MailboxFull) return SmtpResponseCategory.MailboxFull;
        if (textClass is SmtpResponseTextClassification.PolicyRejection or
            SmtpResponseTextClassification.RelayDenied or
            SmtpResponseTextClassification.AntiAbuse or
            SmtpResponseTextClassification.VerificationUnavailable)
            return SmtpResponseCategory.VerificationBlocked;
        if (textClass is SmtpResponseTextClassification.RecipientDoesNotExist or
            SmtpResponseTextClassification.MailboxUnavailable)
            return SmtpResponseCategory.RecipientRejected;
        if (code is 250 or 251) return SmtpResponseCategory.Accepted;
        if (code is >= 400 and < 500 || enhanced?.StartsWith("4.", StringComparison.Ordinal) == true)
            return SmtpResponseCategory.TemporaryFailure;
        if (code is >= 500 and < 600 || enhanced?.StartsWith("5.", StringComparison.Ordinal) == true)
            return SmtpResponseCategory.VerificationBlocked;
        return SmtpResponseCategory.Unknown;
    }

    private static SmtpResponseRuleRegistry.RuleMatch? ClassifyStructured(
        SmtpResponseClassificationContext context,
        string? enhanced)
    {
        var code = context.ReplyCode;
        if (context.Stage == SmtpCommand.RcptTo && code == 252)
            return Match("rcpt_cannot_verify", "generic-verification-refused",
                SmtpNormalizedReason.VerificationRefused, SmtpEvidenceStrength.High);
        if (code is >= 200 and < 300)
            return context.Stage == SmtpCommand.RcptTo
                ? Match("rcpt_accepted", "recipient-accepted", SmtpNormalizedReason.RecipientAccepted, SmtpEvidenceStrength.High)
                : Match("command_accepted", "smtp-command-accepted", SmtpNormalizedReason.CommandAccepted, SmtpEvidenceStrength.High);
        if (context.Stage == SmtpCommand.RcptTo)
        {
            if (enhanced is "5.1.0" or "5.1.1" or "5.1.3" or "5.1.4")
                return Match("enhanced_mailbox_not_found", "generic-mailbox-not-found", SmtpNormalizedReason.MailboxNotFound, SmtpEvidenceStrength.High);
            if (enhanced == "5.2.1")
                return Match("enhanced_mailbox_disabled", "generic-mailbox-disabled", SmtpNormalizedReason.MailboxDisabled, SmtpEvidenceStrength.Medium);
            if (enhanced is "4.2.2" or "5.2.2")
                return Match("enhanced_mailbox_full", "generic-mailbox-full", SmtpNormalizedReason.MailboxFull, SmtpEvidenceStrength.High);
            if (enhanced?.StartsWith("4.4.", StringComparison.Ordinal) == true)
                return Match("enhanced_routing_temporary", "generic-routing-temporary", SmtpNormalizedReason.RoutingTemporaryFailure, SmtpEvidenceStrength.Medium);
            if (enhanced?.StartsWith("5.4.", StringComparison.Ordinal) == true && enhanced != "5.4.1")
                return Match("enhanced_routing_permanent", "generic-routing-failure", SmtpNormalizedReason.RoutingPermanentFailure, SmtpEvidenceStrength.Medium);
            if (enhanced == "5.1.2")
                return Match("enhanced_bad_destination_system", "generic-routing-failure", SmtpNormalizedReason.RoutingPermanentFailure, SmtpEvidenceStrength.High);
        }

        if (context.Stage == SmtpCommand.Greeting && code is >= 500 and < 600)
            return Match("greeting_rejected", "generic-greeting-rejected", SmtpNormalizedReason.GreetingRejected, SmtpEvidenceStrength.High);
        if (context.Stage is SmtpCommand.Ehlo or SmtpCommand.Helo && code is >= 500 and < 600)
            return Match("ehlo_rejected", "generic-ehlo-rejected", SmtpNormalizedReason.EhloRejected, SmtpEvidenceStrength.High);
        return null;
    }

    private static SmtpNormalizedReason ClassifyReplyFallback(
        SmtpResponseClassificationContext context,
        string? enhanced)
    {
        if (enhanced?.StartsWith("5.7.", StringComparison.Ordinal) == true)
            return SmtpNormalizedReason.PolicyBlock;
        if (context.ReplyCode is >= 400 and < 500 || enhanced?.StartsWith("4.", StringComparison.Ordinal) == true)
            return SmtpNormalizedReason.TemporaryFailure;
        if (context.Stage == SmtpCommand.Connect && context.ReplyCode is null)
            return SmtpNormalizedReason.ConnectionFailure;
        return SmtpNormalizedReason.UnknownProviderResponse;
    }

    private static SmtpResponseRuleRegistry.RuleMatch? ConstrainByReplyClass(
        SmtpResponseClassificationContext context,
        SmtpResponseRuleRegistry.RuleMatch? matched)
    {
        if (matched is null) return null;
        if (context.ReplyCode is >= 400 and < 500 && matched.Reason is
            SmtpNormalizedReason.MailboxNotFound or SmtpNormalizedReason.MailboxDisabled or
            SmtpNormalizedReason.MailboxInactive or SmtpNormalizedReason.RecipientRejected or
            SmtpNormalizedReason.RoutingPermanentFailure)
            return Match("reply_class_temporary_constraint", "generic-temporary-failure",
                SmtpNormalizedReason.TemporaryFailure, SmtpEvidenceStrength.Medium);
        if (context.ReplyCode is >= 500 and < 600 && matched.Reason is
            SmtpNormalizedReason.Greylisted or SmtpNormalizedReason.TemporaryFailure or
            SmtpNormalizedReason.ProviderUnavailable or SmtpNormalizedReason.ProviderRateLimit or
            SmtpNormalizedReason.ProviderConnectionLimit or SmtpNormalizedReason.RoutingTemporaryFailure)
            return Match("reply_class_permanent_policy_constraint", "generic-policy-block",
                SmtpNormalizedReason.PolicyBlock, SmtpEvidenceStrength.Medium);
        return matched;
    }

    private static string FallbackRuleId(SmtpNormalizedReason reason) => reason switch
    {
        SmtpNormalizedReason.TemporaryFailure => "generic-temporary-failure",
        SmtpNormalizedReason.ConnectionFailure => "generic-connection-failure",
        SmtpNormalizedReason.PolicyBlock => "generic-policy-block",
        _ => "unknown-provider-response"
    };

    private static SmtpResponseRuleRegistry.RuleMatch Match(
        string id,
        string fingerprint,
        SmtpNormalizedReason reason,
        SmtpEvidenceStrength strength) => new(id, fingerprint, reason, strength);

    private static SmtpResponseTextClassification ClassifyText(string text, string? enhanced)
    {
        if (ContainsAny(text, "greylist", "graylist", "grey list", "gray list"))
            return SmtpResponseTextClassification.Greylisting;
        if (ContainsAny(text, "rate limit", "rate-limit", "too many", "throttl", "connection frequency"))
            return SmtpResponseTextClassification.RateLimit;
        if (enhanced is "4.2.2" or "5.2.2" ||
            ContainsAny(text, "mailbox full", "mailbox is full", "over quota", "quota exceeded", "storage allocation exceeded"))
            return SmtpResponseTextClassification.MailboxFull;
        if (enhanced is "5.1.0" or "5.1.1" or "5.1.3" or "5.1.4" ||
            ContainsAny(text, "user unknown", "unknown user", "no such user", "does not exist", "invalid recipient", "unrouteable address"))
            return SmtpResponseTextClassification.RecipientDoesNotExist;
        if (enhanced?.StartsWith("5.2.", StringComparison.Ordinal) == true || ContainsAny(text, "mailbox unavailable", "mailbox disabled"))
            return SmtpResponseTextClassification.MailboxUnavailable;
        if (ContainsAny(text, "relay denied", "relaying denied", "unable to relay"))
            return SmtpResponseTextClassification.RelayDenied;
        if (enhanced?.StartsWith("5.7.", StringComparison.Ordinal) == true ||
            ContainsAny(text, "policy", "access denied", "not permitted", "spam", "anti-abuse", "blacklist", "blocked"))
            return SmtpResponseTextClassification.PolicyRejection;
        if (ContainsAny(text, "cannot verify", "verification unavailable", "vrfy disabled"))
            return SmtpResponseTextClassification.VerificationUnavailable;
        if (enhanced?.StartsWith("4.", StringComparison.Ordinal) == true || ContainsAny(text, "try again", "temporarily", "temporary"))
            return SmtpResponseTextClassification.TemporaryCondition;
        if (enhanced?.StartsWith("2.", StringComparison.Ordinal) == true)
            return SmtpResponseTextClassification.Success;
        return text.Length == 0 ? SmtpResponseTextClassification.None : SmtpResponseTextClassification.Unknown;
    }

    private static bool IsMicrosoftRecipientRejection(
        SmtpCommand command,
        int? code,
        string? enhanced,
        string text,
        MailProvider provider) =>
        (provider is MailProvider.Microsoft365 or MailProvider.MicrosoftConsumer) &&
        command == SmtpCommand.RcptTo &&
        code == 550 &&
        enhanced == "5.4.1" &&
        ContainsAny(text,
            "recipient address rejected",
            "recipient rejected",
            "recipient not found",
            "recipient does not exist");

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static string? Sanitize(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return response;
        var redacted = EmailRegex().Replace(response, "<redacted-email>");
        return redacted.Length <= 300 ? redacted : redacted[..300];
    }

    [GeneratedRegex(@"(?<!\d)([245]\.\d{1,3}\.\d{1,3})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex EnhancedStatusRegex();

    [GeneratedRegex(@"[^\s<>]+@[^\s<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}

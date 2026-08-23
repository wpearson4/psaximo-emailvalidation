using System.Text.RegularExpressions;
using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

public sealed partial class SmtpResponseClassifier : ISmtpResponseClassifier
{
    public SmtpEvidence Classify(
        SmtpCommand command,
        int? responseCode,
        string? response,
        TimeSpan elapsed,
        MailProvider provider,
        string mxHost,
        int attempt = 1)
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

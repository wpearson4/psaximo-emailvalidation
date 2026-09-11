namespace EmailValidation.Core;

/// <summary>
/// Defines the minimum SMTP provenance required before a response can be
/// interpreted as recipient evidence. Connection, greeting, EHLO, and MAIL
/// FROM success are not evidence that a recipient was accepted.
/// </summary>
public static class SmtpRecipientEvidencePolicy
{
    public static bool HasRecipientAcceptance(SmtpProbeResult result)
    {
        return result.SessionEvidence is { } session &&
            session.MailFromSucceeded && session.RcptTo is
            {
                ResponseCode: >= 200 and < 300,
                Category: SmtpResponseCategory.Accepted or SmtpResponseCategory.GatewayAccepted
            };
    }

    public static bool HasStrongRecipientRejection(SmtpProbeResult result)
    {
        return result.SessionEvidence?.HasStrongRecipientRejection == true;
    }

    public static bool HasRecipientMailboxFull(SmtpProbeResult result) =>
        result.SessionEvidence is
        {
            MailFromSucceeded: true, RcptTo:
            {
                ResponseCode: >= 400 and < 600,
                Category: SmtpResponseCategory.MailboxFull
            }
        };

    public static DateTimeOffset? RecipientObservedAt(SmtpProbeResult result) =>
        result.Evidence is { Command: SmtpCommand.RcptTo } evidence
            ? evidence.Timestamp
            : null;

    public static string? MxHost(SmtpProbeResult result)
    {
        var host = result.SessionEvidence?.MxHost ?? result.Evidence?.MxHost;
        return string.IsNullOrWhiteSpace(host) ? null : NormalizeHost(host);
    }

    public static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();
}

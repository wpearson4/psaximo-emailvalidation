using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailValidation.Core;

namespace EmailValidation.ConsoleApp;

internal static class ResultFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Format(IReadOnlyList<EmailValidationResult> results, OutputFormat format, bool single) => format switch
    {
        OutputFormat.Json => single
            ? JsonSerializer.Serialize(results.SingleOrDefault(), JsonOptions)
            : JsonSerializer.Serialize(results, JsonOptions),
        OutputFormat.Csv => ToCsv(results),
        _ => string.Join(Environment.NewLine + Environment.NewLine, results.Select(ToText))
    };

    private static string ToText(EmailValidationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Email);
        Add(builder, "Normalized:", result.NormalizedEmail ?? "—");
        Add(builder, "Status:", result.Status.ToString());
        Add(builder, "Detailed Status:", result.DetailedStatus.ToString());
        Add(builder, "Classification Confidence:", result.ClassificationConfidence.ToString("P0", CultureInfo.InvariantCulture));
        Add(builder, "Evidence Quality:", result.EvidenceQuality.ToString());
        Add(builder, "Confidence Type:", result.ConfidenceType.ToString());
        Add(builder, "Deliverability Probability:", result.DeliverabilityProbability?.ToString("P0", CultureInfo.InvariantCulture) ?? "Not calibrated");
        Add(builder, "Catch-All Classification:", result.CatchAllClassification.ToString());
        Add(builder, "Probe Attempted:", result.ProbeAttempted ? "Yes" : "No");
        Add(builder, "Probe Disposition:", result.ProbeDisposition.ToString());
        Add(builder, "Retry After:", result.RetryAfter?.ToString("O", CultureInfo.InvariantCulture) ?? "—");
        if (result.ConfidenceReason is not null) Add(builder, "Confidence Reason:", result.ConfidenceReason);
        Add(builder, "Syntax:", result.Checks.SyntaxValid ? "Valid" : "Invalid");
        Add(builder, "Domain:", result.Checks.DomainExists ? "Valid" : "Not verified");
        Add(builder, "MX:", result.Checks.MxPresent ? "Valid" : "Not found");
        Add(builder, "MX Routing:", !result.Checks.MxPresent
            ? "None"
            : result.UsedImplicitMxFallback
                ? "Implicit A/AAAA fallback (no explicit MX)"
                : "Explicit MX");
        Add(builder, "MX Hosts:", result.MxRecords.Count == 0 ? "—" : string.Join(", ", result.MxRecords.Select(record => $"{record.Preference} {record.Host}")));
        Add(builder, "Provider:", result.Provider?.Family is not null and not ProviderFamily.Unknown
            ? result.Provider.Family.ToString()
            : result.MailProvider.ToString());
        Add(builder, "Gateway:", GatewayName(result.Provider?.GatewayProvider ?? GatewayProvider.Unknown));
        Add(builder, "Mailbox Provider:", (result.Provider?.MailboxProvider ?? MailProvider.Unknown).ToString());
        Add(builder, "Mailbox:", result.Checks.Mailbox.ToString());
        Add(builder, "Catch-All:", result.Checks.CatchAll.ToString());
        Add(builder, "Verification Reliability:", result.ProviderValidation is null
            ? VerificationReliabilityLevel.Unknown.ToString()
            : $"{result.ProviderValidation.VerificationReliabilityLevel} ({result.ProviderValidation.VerificationReliability:P0})");
        Add(builder, "Disposable:", result.Checks.DisposableDomain ? "Yes" : "No");
        Add(builder, "Role Account:", result.Checks.RoleAccount ? "Yes" : "No");
        Add(builder, "Free Email:", result.DomainIntelligence?.FreeEmailProvider == true ? "Yes" : "No");
        Add(builder, "Disposable Status:", result.DomainIntelligence?.DisposableIntelligence.Status.ToString() ?? DisposableDomainStatus.Unknown.ToString());
        Add(builder, "Toxic Domain:", result.DomainIntelligence?.ToxicDomain.Status.ToString() ?? ToxicDomainStatus.Unknown.ToString());
        Add(builder, "Mail Infrastructure:", result.DomainIntelligence?.MailInfrastructure.Status.ToString() ?? MailInfrastructureStatus.Unknown.ToString());
        Add(builder, "MX Forward:", result.DomainIntelligence?.MxForward.Status.ToString() ?? MxForwardStatus.Unknown.ToString());
        Add(builder, "Domain Age:", result.DomainIntelligence?.DomainAge.DomainAgeDays is int age ? $"{age} days" : "Unknown");
        Add(builder, "Typo Detected:", result.AddressIntelligence?.Typo.TypoDetected == true ? "Yes" : "No");
        if (result.AddressIntelligence?.Typo.SuggestedEmail is not null)
            Add(builder, "Did You Mean:", result.AddressIntelligence.Typo.SuggestedEmail);
        Add(builder, "Spam-Trap Risk:", result.AddressIntelligence?.SpamTrapRisk.Status.ToString() ?? SpamTrapRiskStatus.Unknown.ToString());
        Add(builder, "Abuse Risk:", result.AddressIntelligence?.AbuseRisk.Status.ToString() ?? AbuseRiskStatus.Unknown.ToString());
        Add(builder, "Suppression:", result.AddressIntelligence?.Suppression.Status.ToString() ?? SuppressionStatus.Unknown.ToString());
        Add(builder, "Bounce Risk:", result.Risk?.BounceRisk.ToString() ?? BounceRisk.Unknown.ToString());
        Add(builder, "Recommended Send:", result.Recommendation?.Send switch { true => "Yes", false => "No", _ => "Unknown" });
        Add(builder, "Recommendation Risk:", result.Recommendation?.Risk.ToString() ?? RecommendationRisk.Unknown.ToString());
        if (result.Recommendation?.Reasons.Count > 0)
            Add(builder, "Recommendation Why:", string.Join(", ", result.Recommendation.Reasons));
        Add(builder, "Reasons:", result.ReasonCodes.Count == 0 ? "None" : string.Join(", ", result.ReasonCodes));
        Add(builder, "Duration:", $"{result.DurationMs} ms");
        if (result.Diagnostics is not null)
        {
            Add(builder, "Persistent Mailbox:", result.Diagnostics.PersistentMailboxFound ? "Found" : "Not found");
            Add(builder, "Persistent Domain:", result.Diagnostics.PersistentDomainFound ? "Found" : "Not found");
            Add(builder, "Persistent Freshness:", result.Diagnostics.PersistentMailboxFresh ? "Fresh" : "Not reusable");
            if (result.Diagnostics.PersistentIntelligenceDecision is not null)
                Add(builder, "Persistence Decision:", result.Diagnostics.PersistentIntelligenceDecision);
            Add(builder, "Provider Confidence:", (result.Provider?.Confidence ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Evidence Confidence:", result.EvidenceConfidence.ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Detailed Statuses:", result.DetailedStatuses.Count == 0 ? "None" : string.Join(", ", result.DetailedStatuses));
            Add(builder, "Provider Signature:", result.Provider?.MatchedSignature ?? "—");
            Add(builder, "Cache Hit:", result.Diagnostics.DomainCacheHit ? "Yes" : "No");
            Add(builder, "Selected MX:", result.Diagnostics.SelectedMx ?? "—");
            Add(builder, "MX Attempted:", result.Diagnostics.MxHostsAttempted.Count == 0
                ? "—" : string.Join(", ", result.Diagnostics.MxHostsAttempted));
            Add(builder, "MX Consensus:", result.Diagnostics.MxConsensus.ToString());
            Add(builder, "Probe Sender:", result.Diagnostics.ProbeSender is null ? "Not configured" : "Configured");
            Add(builder, "Sender Domain Health:", result.Diagnostics.SenderDomainHealth.ToString());
            Add(builder, "DNS Duration:", $"{result.Diagnostics.DnsDurationMs} ms");
            Add(builder, "SMTP Connect:", $"{result.Diagnostics.SmtpConnectionDurationMs} ms");
            Add(builder, "SMTP Attempts:", result.Diagnostics.SmtpAttempts.ToString(CultureInfo.InvariantCulture));
            Add(builder, "Catch-All Probes:", result.Diagnostics.CatchAllProbes.ToString(CultureInfo.InvariantCulture));
            Add(builder, "Catch-All Confidence:", (result.CatchAllEvidence?.Confidence ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Catch-All Accepted:", result.Diagnostics.CatchAllAccepted.ToString(CultureInfo.InvariantCulture));
            Add(builder, "Catch-All Rejected:", result.Diagnostics.CatchAllRejected.ToString(CultureInfo.InvariantCulture));
            Add(builder, "Catch-All Ambig.:", result.Diagnostics.CatchAllAmbiguous.ToString(CultureInfo.InvariantCulture));
            if (result.Diagnostics.CatchAllDetail is not null) Add(builder, "Catch-All Detail:", result.Diagnostics.CatchAllDetail);
            if (result.SmtpEvidence is not null)
            {
                Add(builder, "SMTP Command:", result.SmtpEvidence.Command.ToString());
                Add(builder, "SMTP Category:", result.ProviderValidation?.EffectiveCategory.ToString() ?? result.SmtpEvidence.Category.ToString());
                Add(builder, "SMTP Response Code:", result.SmtpEvidence.ResponseCode?.ToString(CultureInfo.InvariantCulture) ?? "—");
                Add(builder, "Enhanced Status:", result.SmtpEvidence.EnhancedStatusCode ?? "—");
                Add(builder, "Response Class:", result.SmtpEvidence.TextClassification.ToString());
            }
            if (result.SmtpSessionEvidence is not null)
            {
                Add(builder, "Server Banner:", result.SmtpSessionEvidence.ServerBanner ?? "—");
                Add(builder, "EHLO Host:", result.SmtpSessionEvidence.EhloHost ?? "—");
                Add(builder, "TLS Advertised:", result.SmtpSessionEvidence.TlsAdvertised ? "Yes" : "No");
                Add(builder, "TLS Used:", result.SmtpSessionEvidence.TlsUsed ? "Yes" : "No");
                Add(builder, "MAIL FROM Address:", result.SmtpSessionEvidence.ProbeSender);
                Add(builder, "MAIL FROM Result:", result.SmtpSessionEvidence.MailFrom?.Category.ToString() ?? "Not Attempted");
                Add(builder, "MAIL FROM Code:", result.SmtpSessionEvidence.MailFrom?.ResponseCode?.ToString(CultureInfo.InvariantCulture) ?? "—");
                Add(builder, "MAIL FROM Enhanced:", result.SmtpSessionEvidence.MailFrom?.EnhancedStatusCode ?? "—");
                Add(builder, "RCPT TO Result:", result.SmtpSessionEvidence.RcptTo?.Category.ToString() ?? "Not Attempted");
                Add(builder, "RCPT TO Code:", result.SmtpSessionEvidence.RcptTo?.ResponseCode?.ToString(CultureInfo.InvariantCulture) ?? "—");
                Add(builder, "RCPT TO Enhanced:", result.SmtpSessionEvidence.RcptTo?.EnhancedStatusCode ?? "—");
                Add(builder, "Failed SMTP Stage:", result.SmtpSessionEvidence.FailedStage?.ToString() ?? "None");
                Add(builder, "Greylisting Suspected:", result.SmtpSessionEvidence.Stages.Any(stage => stage.Category == SmtpResponseCategory.Greylisted) ? "Yes" : "No");
                Add(builder, "Rate Limit Suspected:", result.SmtpSessionEvidence.Stages.Any(stage => stage.Category == SmtpResponseCategory.RateLimited) ? "Yes" : "No");
                Add(builder, "Policy Block Suspected:", result.ReasonCodes.Any(reason => reason is ReasonCode.PolicyBlock or ReasonCode.SenderIdentityRejected or ReasonCode.ProviderVerificationBlocked) ? "Yes" : "No");
                foreach (var stage in result.SmtpSessionEvidence.Stages)
                    Add(builder, "SMTP Stage:", $"{stage.Stage} → {stage.ResponseCode?.ToString(CultureInfo.InvariantCulture) ?? "—"} {stage.EnhancedStatusCode ?? ""} ({stage.Category}, {stage.Duration.TotalMilliseconds:0} ms)".TrimEnd());
            }
            if (result.CatchAllEvidence?.ProbeResults.Count > 0)
            {
                foreach (var probe in result.CatchAllEvidence.ProbeResults)
                    Add(builder, "Catch-All Probe:", $"{probe.SessionEvidence?.MxHost ?? probe.Evidence?.MxHost ?? "—"} → {probe.Status} ({probe.ResponseCode?.ToString(CultureInfo.InvariantCulture) ?? "—"})");
            }
            Add(builder, "Historical Obs.:", (result.HistoricalEvidence?.ObservationCount ?? 0).ToString(CultureInfo.InvariantCulture));
            Add(builder, "Target Accept Rate:", (result.HistoricalEvidence?.TargetAcceptanceRate ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Random Accept Rate:", (result.HistoricalEvidence?.RandomAcceptanceRate ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Recipient Reject Rate:", (result.HistoricalEvidence?.RecipientRejectionRate ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Temporary Fail Rate:", (result.HistoricalEvidence?.TemporaryFailureRate ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Rate Limit Rate:", (result.HistoricalEvidence?.RateLimitRate ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Gateway Accept Rate:", (result.HistoricalEvidence?.GatewayAcceptanceRate ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Greylisting Probability:", (result.HistoricalEvidence?.GreylistingProbability ?? 0).ToString("P0", CultureInfo.InvariantCulture));
            Add(builder, "Intelligence Duration:", $"{result.Diagnostics.IntelligenceLookupDurationMs} ms");
            Add(builder, "Infrastructure DNS:", $"{result.Diagnostics.MailInfrastructureDurationMs} ms");
            if (result.ProviderValidation is not null) Add(builder, "Provider Evaluation:", result.ProviderValidation.Explanation);
            foreach (var contribution in result.ConfidenceEvidence)
            {
                var sign = contribution.Weight >= 0 ? "+" : string.Empty;
                Add(builder, "Confidence Evidence:", $"{sign}{contribution.Weight:0.00} {contribution.Evidence} — {contribution.Explanation}");
            }
            foreach (var evidence in result.Evidence)
                Add(builder, "Evidence:", $"{evidence.Signal} [{evidence.Source}, {evidence.Confidence:P0}] — {evidence.Detail}");
            if (result.Diagnostics.Detail is not null) Add(builder, "Diagnostic:", result.Diagnostics.Detail);
        }
        return builder.ToString().TrimEnd();
    }

    private static string ToCsv(IReadOnlyList<EmailValidationResult> results)
    {
        var builder = new StringBuilder("email,normalizedEmail,status,classificationConfidence,syntaxValid,domainExists,mxPresent,implicitMxFallback,mxHosts,provider,mailbox,catchAll,disposableDomain,roleAccount,reasonCodes,durationMs,providerFamily,gatewayProvider,mailboxProvider,verificationReliability,verificationReliabilityLevel,providerConfidence,catchAllConfidence,smtpCategory,enhancedStatusCode,detailedStatus,detailedStatuses,freeEmailProvider,disposableStatus,toxicDomainStatus,mailInfrastructureStatus,mxForwardStatus,domainAgeDays,typoDetected,suggestedEmail,spamTrapRisk,abuseRisk,suppressionStatus,bounceRisk,recommendedSend,recommendationRisk,recommendationReasons,confidenceType,evidenceConfidence,confidenceReason,failedSmtpStage,mxHostsAttempted,mxConsensus,probeSenderHealth,evidenceQuality,deliverabilityProbability,catchAllClassification,probeAttempted,probeDisposition,retryAfter\n");
        foreach (var result in results)
        {
            var fields = new[]
            {
                result.Email, result.NormalizedEmail ?? string.Empty, result.Status.ToString(),
                result.Confidence.ToString("0.00", CultureInfo.InvariantCulture), result.Checks.SyntaxValid.ToString(),
                result.Checks.DomainExists.ToString(), result.Checks.MxPresent.ToString(),
                result.UsedImplicitMxFallback.ToString(),
                string.Join(';', result.MxRecords.Select(record => $"{record.Preference} {record.Host}")),
                result.MailProvider.ToString(), result.Checks.Mailbox.ToString(), result.Checks.CatchAll.ToString(),
                result.Checks.DisposableDomain.ToString(), result.Checks.RoleAccount.ToString(),
                string.Join(';', result.ReasonCodes), result.DurationMs.ToString(CultureInfo.InvariantCulture),
                result.Provider?.Family.ToString() ?? string.Empty,
                result.Provider?.GatewayProvider.ToString() ?? string.Empty,
                result.Provider?.MailboxProvider.ToString() ?? string.Empty,
                (result.ProviderValidation?.VerificationReliability ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
                result.ProviderValidation?.VerificationReliabilityLevel.ToString() ?? VerificationReliabilityLevel.Unknown.ToString(),
                (result.Provider?.Confidence ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
                (result.CatchAllEvidence?.Confidence ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
                result.ProviderValidation?.EffectiveCategory.ToString() ?? result.SmtpEvidence?.Category.ToString() ?? string.Empty,
                result.SmtpEvidence?.EnhancedStatusCode ?? string.Empty,
                result.DetailedStatus.ToString(),
                string.Join(';', result.DetailedStatuses),
                (result.DomainIntelligence?.FreeEmailProvider ?? false).ToString(),
                result.DomainIntelligence?.DisposableIntelligence.Status.ToString() ?? DisposableDomainStatus.Unknown.ToString(),
                result.DomainIntelligence?.ToxicDomain.Status.ToString() ?? ToxicDomainStatus.Unknown.ToString(),
                result.DomainIntelligence?.MailInfrastructure.Status.ToString() ?? MailInfrastructureStatus.Unknown.ToString(),
                result.DomainIntelligence?.MxForward.Status.ToString() ?? MxForwardStatus.Unknown.ToString(),
                result.DomainIntelligence?.DomainAge.DomainAgeDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                (result.AddressIntelligence?.Typo.TypoDetected ?? false).ToString(),
                result.AddressIntelligence?.Typo.SuggestedEmail ?? string.Empty,
                result.AddressIntelligence?.SpamTrapRisk.Status.ToString() ?? SpamTrapRiskStatus.Unknown.ToString(),
                result.AddressIntelligence?.AbuseRisk.Status.ToString() ?? AbuseRiskStatus.Unknown.ToString(),
                result.AddressIntelligence?.Suppression.Status.ToString() ?? SuppressionStatus.Unknown.ToString(),
                result.Risk?.BounceRisk.ToString() ?? BounceRisk.Unknown.ToString(),
                result.Recommendation?.Send?.ToString() ?? string.Empty,
                result.Recommendation?.Risk.ToString() ?? RecommendationRisk.Unknown.ToString(),
                result.Recommendation is null ? string.Empty : string.Join(';', result.Recommendation.Reasons),
                result.ConfidenceType.ToString(),
                result.EvidenceConfidence.ToString("0.00", CultureInfo.InvariantCulture),
                result.ConfidenceReason ?? string.Empty,
                result.SmtpSessionEvidence?.FailedStage?.ToString() ?? string.Empty,
                result.MxValidation is null ? string.Empty : string.Join(';', result.MxValidation.HostsAttempted),
                result.MxValidation?.Consensus.ToString() ?? string.Empty,
                result.ProbeSenderHealth?.Status.ToString() ?? string.Empty,
                result.EvidenceQuality.ToString(),
                result.DeliverabilityProbability?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                result.CatchAllClassification.ToString(),
                result.ProbeAttempted.ToString(),
                result.ProbeDisposition.ToString(),
                result.RetryAfter?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty
            };
            builder.AppendLine(string.Join(',', fields.Select(Escape)));
        }
        return builder.ToString().TrimEnd();
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void Add(StringBuilder builder, string label, string value) =>
        builder.AppendLine(label.Length >= 22 ? $"{label} {value}" : $"{label,-22}{value}");

    private static string GatewayName(GatewayProvider provider) => provider switch
    {
        GatewayProvider.MicrosoftExchangeOnlineProtection => "Exchange Online Protection",
        GatewayProvider.Unknown => "Unknown",
        _ => provider.ToString()
    };
}

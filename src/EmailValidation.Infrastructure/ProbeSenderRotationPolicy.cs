using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class ProbeSenderRotationPolicy(IOptions<EmailValidationOptions> options) : IProbeSenderRotationPolicy
{
    private readonly ProbeSenderRotationOptions _options = options.Value.ProbeSenderRotation;

    public ProbeSenderRotationDecision Evaluate(
        ProbeSenderRuntimeStatistics sender,
        int validationThreshold,
        DateTimeOffset now,
        bool alternateAvailable)
    {
        if (!alternateAvailable) return ProbeSenderRotationDecision.Keep;
        if (sender.State is ProbeSenderCandidateState.Invalid or ProbeSenderCandidateState.Retired or
            ProbeSenderCandidateState.CoolingDown or ProbeSenderCandidateState.Degraded)
            return new(true, $"current sender is {sender.State}");
        if (sender.ActiveValidationCount >= validationThreshold)
            return new(true, $"scheduled rotation after {sender.ActiveValidationCount} validations");
        if (sender.ActiveSince is { } activeSince &&
            now - activeSince >= TimeSpan.FromMinutes(_options.MaxActiveMinutes))
            return new(true, $"scheduled rotation after {_options.MaxActiveMinutes} active minutes");
        if (sender.ActiveCompletedCount >= _options.MinimumSuccessRateSampleSize &&
            (double)sender.MailFromSuccessCount / sender.ActiveCompletedCount < _options.MinimumMailFromSuccessRate)
            return new(true, "sender MAIL FROM success rate fell below the configured threshold");
        return ProbeSenderRotationDecision.Keep;
    }
}

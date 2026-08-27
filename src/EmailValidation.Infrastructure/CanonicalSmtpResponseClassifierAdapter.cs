using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

/// <summary>
/// Temporary comparison adapter that preserves the pre-intelligence canonical interpretation
/// while Shadow mode measures the candidate policy against the same observation.
/// </summary>
public sealed class CanonicalSmtpResponseClassifierAdapter : ICanonicalSmtpResponseClassifier
{
    private readonly SmtpResponseClassifier _classifier = new();

    public SmtpEvidence Classify(SmtpResponseClassificationContext context) => _classifier.Classify(
        context.Stage,
        context.ReplyCode,
        context.Response,
        context.Elapsed,
        context.Provider,
        context.MxHost,
        context.Attempt,
        context.Observation);
}

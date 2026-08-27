using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class DomainBackoffPolicy(IOptions<EmailValidationOptions> options) : IDomainBackoffPolicy
{
    private readonly SchedulingOptions _options = options.Value.Scheduling;

    public DomainBackoffDecision Evaluate(
        MailProvider provider,
        SmtpResponseCategory category,
        int consecutiveTemporaryFailures,
        DateTimeOffset now)
    {
        if (category is not (SmtpResponseCategory.TemporaryFailure or
            SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited or
            SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.ConnectionRejected or
            SmtpResponseCategory.Timeout or SmtpResponseCategory.MailboxFull))
            return new(now, TimeSpan.Zero);

        var exponent = Math.Clamp(consecutiveTemporaryFailures - 1, 0, 10);
        var baseDelay = Math.Max(1, _options.TemporaryFailureBackoffMilliseconds);
        var maximum = Math.Max(baseDelay, _options.MaximumBackoffMilliseconds);
        var milliseconds = Math.Min(maximum, baseDelay * Math.Pow(2, exponent));
        var cooldown = TimeSpan.FromMilliseconds(milliseconds);
        return new(now.Add(cooldown), cooldown);
    }
}

public sealed class DomainPacingJitter : IDomainPacingJitter
{
    public TimeSpan Apply(TimeSpan interval, int maximumJitterMilliseconds)
    {
        var maximum = Math.Max(0, maximumJitterMilliseconds);
        if (maximum == 0) return interval;
        var adjustment = Random.Shared.Next(-maximum, maximum + 1);
        return TimeSpan.FromMilliseconds(Math.Max(0, interval.TotalMilliseconds + adjustment));
    }
}

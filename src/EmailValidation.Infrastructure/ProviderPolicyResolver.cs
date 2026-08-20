using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class ProviderPolicyResolver(IOptions<EmailValidationOptions> options) : IProviderPolicyResolver
{
    private readonly SchedulingOptions _scheduling = options.Value.Scheduling;
    private readonly SmtpOptions _smtp = options.Value.Smtp;

    public ProviderPolicy Resolve(MailProvider provider)
    {
        var providerKey = Normalize(provider);
        var configured = Find(providerKey, provider) ?? _scheduling.DefaultProviderPolicy;
        if (configured is null)
            return new ProviderPolicy(
                providerKey,
                _scheduling.PerProviderConcurrency > 0
                    ? _scheduling.PerProviderConcurrency
                    : _smtp.PerProviderConcurrency,
                _scheduling.ProviderMinIntervalMilliseconds,
                15,
                _smtp.RetryCount);
        return new ProviderPolicy(
            providerKey,
            configured.PerProviderConcurrency,
            configured.MinIntervalMilliseconds ?? configured.DelayMilliseconds,
            configured.PolicyBlockCooldownMinutes,
            configured.MaxRetries,
            configured.PerDomainConcurrency);
    }

    internal static string Normalize(MailProvider provider) => provider switch
    {
        MailProvider.Microsoft365 => "Microsoft365",
        MailProvider.MicrosoftConsumer => "MicrosoftConsumer",
        MailProvider.GoogleWorkspace => "Google",
        MailProvider.Yahoo => "Yahoo",
        MailProvider.AppleICloud => "AppleICloud",
        MailProvider.Comcast => "Comcast",
        MailProvider.Proton => "Proton",
        MailProvider.Fastmail => "Fastmail",
        MailProvider.Zoho => "Zoho",
        MailProvider.Unknown or MailProvider.GenericSmtp or MailProvider.AmazonSes => "Generic",
        _ => provider.ToString()
    };

    private ProviderPolicyOptions? Find(string providerKey, MailProvider provider)
    {
        foreach (var entry in _scheduling.ProviderPolicies)
        {
            if (string.Equals(entry.Key, providerKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Key, provider.ToString(), StringComparison.OrdinalIgnoreCase) ||
                ((provider is MailProvider.Microsoft365 or MailProvider.MicrosoftConsumer) &&
                 string.Equals(entry.Key, "Microsoft", StringComparison.OrdinalIgnoreCase)))
                return entry.Value;
        }
        return null;
    }
}

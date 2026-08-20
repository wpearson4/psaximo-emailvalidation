using EmailValidation.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EmailValidation.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmailValidation(this IServiceCollection services)
    {
        services.AddSingleton<IEmailNormalizer, EmailNormalizer>();
        services.AddSingleton<IDnsMailResolver, MxDnsResolver>();
        services.AddSingleton<IDisposableEmailDetector, DisposableEmailDetector>();
        services.AddSingleton<IDisposableDomainIntelligenceProvider>(provider =>
            (DisposableEmailDetector)provider.GetRequiredService<IDisposableEmailDetector>());
        services.AddSingleton<IRoleAccountDetector, RoleAccountDetector>();
        services.AddSingleton<IEmailTypoDetector, EmailTypoDetector>();
        services.AddSingleton<IFreeEmailProviderDetector, FreeEmailProviderDetector>();
        services.AddSingleton<IToxicDomainDetector, ToxicDomainDetector>();
        services.AddSingleton<ISpamTrapRiskDetector, SpamTrapRiskDetector>();
        services.AddSingleton<IAbuseRiskProvider, AbuseRiskProvider>();
        services.AddSingleton<ISuppressionIntelligenceProvider, SuppressionIntelligenceProvider>();
        services.AddSingleton<IMxForwardDetector, MxForwardDetector>();
        services.AddSingleton<IDomainAgeProvider, UnavailableDomainAgeProvider>();
        services.AddSingleton<IMailInfrastructureInspector, MailInfrastructureInspector>();
        services.AddSingleton<IEmailIdentityIntelligenceProvider, UnknownEmailIdentityIntelligenceProvider>();
        services.AddSingleton<IEmailIntelligenceEvaluator, EmailIntelligenceEvaluator>();
        services.AddSingleton<IDomainIntelligenceEvaluator, DomainIntelligenceEvaluator>();
        services.AddSingleton<IMailProviderDetector, MailProviderDetector>();
        services.AddSingleton<ISmtpProbeThrottle, DomainSmtpProbeThrottle>();
        services.AddSingleton<ISmtpResponseClassifier, SmtpResponseClassifier>();
        services.AddSingleton<ProbeSenderHealthChecker>();
        services.AddSingleton<IProbeSenderHealthChecker>(provider => provider.GetRequiredService<ProbeSenderHealthChecker>());
        services.AddSingleton<IProbeSenderPool>(provider => provider.GetRequiredService<ProbeSenderHealthChecker>());
        services.AddSingleton<ISmtpSessionBudget, SmtpSessionBudget>();
        services.AddSingleton<ISmtpMailboxProbe, SmtpMailboxProbe>();
        services.AddSingleton<ICatchAllDetector, CatchAllDetector>();
        services.AddSingleton<IDomainValidationCache, InMemoryDomainValidationCache>();
        services.AddSingleton<IEmailClassificationEngine, EmailClassificationEngine>();
        services.AddSingleton<IResultEvaluator, ResultEvaluator>();
        services.AddSingleton<IMailProviderStrategy, Microsoft365Strategy>();
        services.AddSingleton<IMailProviderStrategy, GoogleWorkspaceStrategy>();
        services.AddSingleton<IMailProviderStrategy, ProofpointStrategy>();
        services.AddSingleton<IMailProviderStrategy, MimecastStrategy>();
        services.AddSingleton<IMailProviderStrategy, GenericSmtpStrategy>();
        services.AddSingleton<IMailProviderStrategyResolver, MailProviderStrategyResolver>();
        services.AddSingleton<IValidationObservationStore, InMemoryValidationObservationStore>();
        services.AddSingleton<IHistoricalSignalAggregator, HistoricalSignalAggregator>();
        services.AddSingleton<IDeliveryOutcomeRecorder, InMemoryDeliveryOutcomeRecorder>();
        services.AddSingleton<IEmailValidator, EmailValidator>();
        return services;
    }
}

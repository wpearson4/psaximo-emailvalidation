using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using EmailValidation.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace EmailValidation.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmailValidation(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<EmailValidationOptions>, EmailValidationOptionsValidator>();
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
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDomainPacingJitter, DomainPacingJitter>();
        services.AddSingleton<IDomainBackoffPolicy, DomainBackoffPolicy>();
        services.AddSingleton<IProviderPolicyResolver, ProviderPolicyResolver>();
        services.AddSingleton<IProbeSenderAffinityStore, ProbeSenderAffinityStore>();
        services.AddSingleton<IProbeSenderJitter, ProbeSenderJitter>();
        services.AddSingleton<IProbeSenderRotationPolicy, ProbeSenderRotationPolicy>();
        services.AddSingleton(provider =>
        {
            var source = provider.GetRequiredService<IOptions<EmailValidationOptions>>().Value.ProbeSenderSource;
            var settings = new ElasticsearchClientSettings(new Uri(source.Endpoint));
            if (!string.IsNullOrWhiteSpace(source.ApiKey))
                settings.Authentication(new ApiKey(source.ApiKey));
            else if (!string.IsNullOrWhiteSpace(source.Username))
                settings.Authentication(new BasicAuthentication(source.Username, source.Password));
            return new ElasticsearchClient(settings);
        });
        services.AddSingleton<IElasticsearchSearchClient, ElasticsearchSearchClient>();
        services.AddSingleton<IProbeSenderSource, ElasticsearchProbeSenderSource>();
        services.AddSingleton<ProbeSenderHealthChecker>();
        services.AddSingleton<IProbeSenderHealthChecker>(provider => provider.GetRequiredService<ProbeSenderHealthChecker>());
        services.AddSingleton<IProbeSenderPool>(provider => provider.GetRequiredService<ProbeSenderHealthChecker>());
        services.AddSingleton<ISmtpSessionBudget, SmtpSessionBudget>();
        services.AddSingleton<ISmtpMailboxProbe, SmtpMailboxProbe>();
        services.AddSingleton<ICatchAllDetector, CatchAllDetector>();
        services.AddSingleton<IValidationPersistenceMetrics, ValidationPersistenceMetrics>();
        services.AddSingleton<JsonValidationIntelligenceStore>();
        services.AddSingleton<IMongoClient>(provider =>
        {
            var persistence = provider.GetRequiredService<IOptions<EmailValidationOptions>>().Value.Persistence;
            var settings = MongoClientSettings.FromConnectionString(persistence.ConnectionString);
            settings.ApplicationName = "EmailValidation";
            return new MongoClient(settings);
        });
        services.AddSingleton<MongoValidationIntelligenceStore>();
        services.AddSingleton<NoOpEmailValidationPersistenceInitializer>();
        services.AddSingleton<IValidationIntelligenceStore>(provider => IsMongo(provider)
            ? provider.GetRequiredService<MongoValidationIntelligenceStore>()
            : provider.GetRequiredService<JsonValidationIntelligenceStore>());
        services.AddSingleton<IValidationObservationStore>(provider => IsMongo(provider)
            ? provider.GetRequiredService<MongoValidationIntelligenceStore>()
            : provider.GetRequiredService<JsonValidationIntelligenceStore>());
        services.AddSingleton<IEmailValidationPersistenceInitializer>(provider => IsMongo(provider)
            ? provider.GetRequiredService<MongoValidationIntelligenceStore>()
            : provider.GetRequiredService<NoOpEmailValidationPersistenceInitializer>());
        services.AddSingleton<IDeliveryOutcomeStore>(provider =>
            provider.GetRequiredService<JsonValidationIntelligenceStore>());
        services.AddSingleton<IDeliveryOutcomeRecorder>(provider =>
            provider.GetRequiredService<JsonValidationIntelligenceStore>());
        services.AddSingleton<IGlobalSuppressionStore>(provider =>
            provider.GetRequiredService<JsonValidationIntelligenceStore>());
        services.AddSingleton<IDomainValidationCache, PersistentDomainValidationCache>();
        services.AddSingleton<IEmailClassificationEngine, EmailClassificationEngine>();
        services.AddSingleton<IResultEvaluator, ResultEvaluator>();
        services.AddSingleton<IMailProviderStrategy, Microsoft365Strategy>();
        services.AddSingleton<IMailProviderStrategy, GoogleWorkspaceStrategy>();
        services.AddSingleton<IMailProviderStrategy, ProofpointStrategy>();
        services.AddSingleton<IMailProviderStrategy, MimecastStrategy>();
        services.AddSingleton<IMailProviderStrategy, GenericSmtpStrategy>();
        services.AddSingleton<IMailProviderStrategyResolver, MailProviderStrategyResolver>();
        services.AddSingleton<IHistoricalSignalAggregator, HistoricalSignalAggregator>();
        services.AddSingleton<IValidationResultCache, InMemoryValidationResultCache>();
        services.AddSingleton<IValidationSingleFlight, ValidationSingleFlight>();
        services.AddSingleton<IValidationResultReusePolicy, ValidationResultReusePolicy>();
        services.AddSingleton<IValidationPlanBuilder, ValidationPlanBuilder>();
        services.AddSingleton<IConfidenceCalibrationService, ConfidenceCalibrationService>();
        services.AddSingleton<IRiskDataSource, ExistingIntelligenceRiskDataSource>();
        services.AddSingleton<IRiskDataSource, PersistentSuppressionRiskDataSource>();
        services.AddSingleton<IEmailRiskIntelligence, EmailRiskIntelligence>();
        services.AddSingleton<IValidationQualityMetrics, ValidationQualityMetrics>();
        services.AddSingleton<RevalidationMetrics>();
        services.AddSingleton<IRevalidationMetrics>(provider => provider.GetRequiredService<RevalidationMetrics>());
        services.AddSingleton<IRevalidationPolicy, RevalidationPolicy>();
        services.AddSingleton<IRevalidationSchedulePolicy, RevalidationSchedulePolicy>();
        services.AddSingleton<IRevalidationMessageSerializer, JsonRevalidationMessageSerializer>();
        services.AddSingleton<MongoValidationLifecycleStore>();
        services.AddSingleton<NoOpValidationLifecycleStore>();
        services.AddSingleton<IValidationLifecycleStore>(provider => IsRevalidationEnabled(provider)
            ? provider.GetRequiredService<MongoValidationLifecycleStore>()
            : provider.GetRequiredService<NoOpValidationLifecycleStore>());
        services.AddSingleton<IRevalidationOutbox>(provider => IsRevalidationEnabled(provider)
            ? provider.GetRequiredService<MongoValidationLifecycleStore>()
            : provider.GetRequiredService<NoOpValidationLifecycleStore>());
        services.AddSingleton<IRevalidationPersistenceInitializer>(provider => IsRevalidationEnabled(provider)
            ? provider.GetRequiredService<MongoValidationLifecycleStore>()
            : provider.GetRequiredService<NoOpValidationLifecycleStore>());
        services.AddSingleton<AzureServiceBusRevalidationScheduler>();
        services.AddSingleton<DisabledRevalidationScheduler>();
        services.AddSingleton<IRevalidationScheduler>(provider => IsRevalidationEnabled(provider)
            ? provider.GetRequiredService<AzureServiceBusRevalidationScheduler>()
            : provider.GetRequiredService<DisabledRevalidationScheduler>());
        services.AddSingleton<IRevalidationOutboxDispatcher, RevalidationOutboxDispatcher>();
        services.AddSingleton<IValidationLifecycleCoordinator, ValidationLifecycleCoordinator>();
        services.AddSingleton<IEmailRevalidationProcessor, EmailRevalidationProcessor>();
        services.AddSingleton<IRevalidationInfrastructureInitializer, RevalidationInfrastructureInitializer>();
        services.AddSingleton<EmailValidator>();
        services.AddSingleton<IEmailValidationExecutor>(provider => provider.GetRequiredService<EmailValidator>());
        services.AddSingleton<IntelligenceEmailValidator>();
        services.AddSingleton<IEmailValidationService>(provider => provider.GetRequiredService<IntelligenceEmailValidator>());
        services.AddSingleton<IEmailValidator, LifecycleEmailValidator>();
        services.AddOptions<EmailValidationOptions>().ValidateOnStart();
        return services;
    }

    private static bool IsMongo(IServiceProvider provider)
    {
        var persistence = provider.GetRequiredService<IOptions<EmailValidationOptions>>().Value.Persistence;
        return persistence.Enabled && string.Equals(
            persistence.Provider,
            "MongoDB",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRevalidationEnabled(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<EmailValidationOptions>>().Value.Revalidation.Enabled;
}

using System.Security.Cryptography;
using System.Text;
using EmailValidation.Core;

namespace EmailValidation.Application;

public static class EmailValidationScopes
{
    public const string Validate = "emailvalidation.validate";
    public const string Read = "emailvalidation.read";
    public const string JobsWrite = "emailvalidation.jobs.write";
    public const string JobsRead = "emailvalidation.jobs.read";
    public const string Stream = "emailvalidation.stream";
    public const string Admin = "emailvalidation.admin";

    public static readonly IReadOnlyList<string> All =
        [Validate, Read, JobsWrite, JobsRead, Stream, Admin];
}

public static class EmailValidationPolicies
{
    public const string Validate = "EmailValidation.Validate";
    public const string Read = "EmailValidation.Read";
    public const string JobsWrite = "EmailValidation.JobsWrite";
    public const string JobsRead = "EmailValidation.JobsRead";
    public const string Stream = "EmailValidation.Stream";
    public const string Admin = "EmailValidation.Admin";
}

public sealed record CurrentConsumer(
    string SubjectId,
    string? TenantId,
    IReadOnlySet<string> Scopes)
{
    public string PrincipalKey => string.IsNullOrWhiteSpace(TenantId)
        ? $"subject:{SubjectId}"
        : $"tenant:{TenantId}:subject:{SubjectId}";
}

public interface ICurrentConsumerContext
{
    CurrentConsumer GetRequiredConsumer();
}

public enum OwnedResourceType
{
    Validation,
    ValidationJob
}

public sealed record ResourceOwnership(
    OwnedResourceType ResourceType,
    string ResourceId,
    string PrincipalKey,
    string SubjectId,
    string? TenantId,
    DateTimeOffset CreatedAtUtc);

public sealed record IdempotentOperation(
    string PrincipalKey,
    string Operation,
    string Key,
    string RequestHash,
    string ResourceId,
    DateTimeOffset CreatedAtUtc);

public interface ICommercialResourceStore
{
    Task GrantAsync(ResourceOwnership ownership, CancellationToken cancellationToken = default);
    Task<bool> HasAccessAsync(
        OwnedResourceType resourceType,
        string resourceId,
        string principalKey,
        CancellationToken cancellationToken = default);
    Task<IdempotentOperation?> GetIdempotentOperationAsync(
        string principalKey,
        string operation,
        string key,
        CancellationToken cancellationToken = default);
    Task<bool> TrySaveIdempotentOperationAsync(
        IdempotentOperation operation,
        CancellationToken cancellationToken = default);
}

public interface ICommercialResourceInfrastructureInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public static class IdempotencyRequestHasher
{
    public static string HashJobRequest(IReadOnlyList<string> emails, bool enableSmtp)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, enableSmtp ? "smtp:1\n" : "smtp:0\n");
        foreach (var email in emails)
            Append(hash, $"{email.Trim().ToLowerInvariant()}\n");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}

public interface IValidationJobAccessPolicy
{
    Task<bool> CanAccessAsync(
        string jobId,
        CurrentConsumer consumer,
        CancellationToken cancellationToken = default);
}

public sealed class CommercialValidationAccessPolicy(ICommercialResourceStore resources) : IValidationAccessPolicy
{
    public Task<bool> CanAccessAsync(
        string validationId,
        ValidationAccessContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Scopes?.Contains(EmailValidationScopes.Admin) == true) return Task.FromResult(true);
        var principalKey = CurrentConsumerKey.Create(context.Subject, context.TenantId);
        return principalKey is null
            ? Task.FromResult(false)
            : resources.HasAccessAsync(OwnedResourceType.Validation, validationId, principalKey, cancellationToken);
    }
}

public sealed class CommercialValidationJobAccessPolicy(ICommercialResourceStore resources) : IValidationJobAccessPolicy
{
    public Task<bool> CanAccessAsync(
        string jobId,
        CurrentConsumer consumer,
        CancellationToken cancellationToken = default) =>
        consumer.Scopes.Contains(EmailValidationScopes.Admin)
            ? Task.FromResult(true)
            : resources.HasAccessAsync(
                OwnedResourceType.ValidationJob, jobId, consumer.PrincipalKey, cancellationToken);
}

public static class CurrentConsumerKey
{
    public static string? Create(string? subjectId, string? tenantId) =>
        !string.IsNullOrWhiteSpace(subjectId) && !string.IsNullOrWhiteSpace(tenantId)
            ? $"tenant:{tenantId}:subject:{subjectId}"
            : !string.IsNullOrWhiteSpace(subjectId) ? $"subject:{subjectId}" : null;
}

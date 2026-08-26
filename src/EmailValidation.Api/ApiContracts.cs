using EmailValidation.Application;
using EmailValidation.Core;

namespace EmailValidation.Api;

public sealed record ValidateEmailV1Request(
    string Email,
    bool EnableSmtp = true,
    bool Verbose = false,
    string? ValidationId = null);

public sealed record CreateValidationJobV1Request(
    IReadOnlyList<string> Emails,
    bool EnableSmtp = true,
    string? SourceFileId = null,
    string? SourceFileName = null,
    string? EmailColumn = null);

public sealed record ValidationChecksV1(
    bool SyntaxValid,
    bool DomainExists,
    bool MxPresent,
    bool Disposable,
    bool RoleAddress,
    string CatchAll);

public sealed record UnknownValidationContextV1(
    string Cause,
    string Summary,
    bool Retryable,
    string RecommendedAction,
    string SmtpCategory,
    string? FailedStage,
    int? ResponseCode,
    string? EnhancedStatusCode,
    string? MxHost,
    DateTimeOffset? RetryAfterUtc);

public sealed record EmailValidationV1Response(
    string ValidationId,
    string Email,
    string LifecycleState,
    string ResultState,
    string Status,
    string SubStatus,
    double Confidence,
    string? ConfidenceReason,
    UnknownValidationContextV1? UnknownContext,
    string Provider,
    DateTimeOffset? ValidatedAtUtc,
    string Source,
    bool RetryScheduled,
    DateTimeOffset? RetryAtUtc,
    int AttemptNumber,
    int MaxAttempts,
    DateTimeOffset? FinalizedAtUtc,
    ValidationChecksV1 Checks);

public sealed record ValidationStatusV1Response(
    string ValidationId,
    string? Email,
    long Sequence,
    string LifecycleState,
    string ResultState,
    string? Status,
    string? SubStatus,
    double? Confidence,
    string? ConfidenceReason,
    UnknownValidationContextV1? UnknownContext,
    int AttemptNumber,
    int MaxAttempts,
    bool RetryScheduled,
    DateTimeOffset? RetryAtUtc,
    string? Provider,
    string? StatusMessage,
    DateTimeOffset? RequestedAtUtc,
    DateTimeOffset? ValidatedAtUtc,
    DateTimeOffset? FinalizedAtUtc);

public sealed record ValidationJobV1Response(
    string JobId,
    DateTimeOffset CreatedAtUtc,
    string State,
    int TotalItems,
    int ProcessedItems,
    int FinalItems,
    int ProvisionalItems,
    int FailedItems,
    DateTimeOffset UpdatedAtUtc,
    bool EnableSmtp,
    string? SourceFileId,
    string? SourceFileName,
    string? EmailColumn);

public sealed record ValidationJobPageV1Response(
    int Skip,
    int Take,
    IReadOnlyList<ValidationJobV1Response> Items,
    int? NextSkip);

public sealed record ValidationJobResultV1Response(
    int Position,
    string Email,
    string State,
    EmailValidationV1Response? Validation,
    string? Error);

public sealed record ValidationJobResultsPageV1Response(
    string JobId,
    int Skip,
    int Take,
    IReadOnlyList<ValidationJobResultV1Response> Items,
    int? NextSkip);

public static class ApiContractMapper
{
    public static EmailValidationV1Response Map(EmailValidationResult result) => new(
        result.ValidationId ?? throw new InvalidOperationException("The validation engine did not return a validation identifier."),
        result.Email,
        ResolveLifecycle(result),
        result.ResultState.ToString(),
        result.Status.ToString(),
        result.SubStatus.ToString(),
        result.Confidence,
        result.ConfidenceReason,
        Map(result.UnknownContext),
        result.MailProvider.ToString(),
        result.Metadata?.ValidatedAt ?? result.LastValidatedAt,
        result.Metadata?.ResultSource.ToString() ?? ValidationResultSource.LiveValidation.ToString(),
        result.RetryScheduled,
        result.RetryAfter,
        result.AttemptNumber,
        result.MaximumAttempts,
        result.FinalizedAt,
        new ValidationChecksV1(
            result.Checks.SyntaxValid,
            result.Checks.DomainExists,
            result.Checks.MxPresent,
            result.Checks.DisposableDomain,
            result.Checks.RoleAccount,
            result.Checks.CatchAll.ToString()));

    public static ValidationStatusV1Response Map(ValidationStatusSnapshot snapshot) => new(
        snapshot.ValidationId,
        snapshot.Email,
        snapshot.Sequence,
        snapshot.LifecycleState.ToString(),
        snapshot.ResultState.ToString(),
        snapshot.Status?.ToString(),
        snapshot.SubStatus?.ToString(),
        snapshot.Confidence,
        snapshot.ConfidenceReason,
        Map(snapshot.UnknownContext),
        snapshot.AttemptNumber,
        snapshot.MaximumAttempts,
        snapshot.RetryScheduled,
        snapshot.RetryAt,
        snapshot.Provider,
        snapshot.StatusMessage,
        snapshot.RequestedAt,
        snapshot.LastUpdatedAt ?? snapshot.FirstValidatedAt,
        snapshot.FinalizedAt);

    public static ValidationJobV1Response Map(ValidationJobSnapshot job) => new(
        job.JobId,
        job.CreatedAtUtc,
        job.State.ToString(),
        job.TotalItems,
        job.ProcessedItems,
        job.FinalItems,
        job.ProvisionalItems,
        job.FailedItems,
        job.UpdatedAtUtc,
        job.EnableSmtp,
        job.SourceFileId,
        job.SourceFileName,
        job.EmailColumn);

    public static ValidationJobResultV1Response Map(ValidationJobItem item) => new(
        item.Position,
        item.Email,
        item.State.ToString(),
        item.Result is null ? null : Map(item.Result),
        item.Error is null ? null : "Validation failed.");

    private static string ResolveLifecycle(EmailValidationResult result) =>
        result.ResultState == ValidationResultState.Final
            ? ValidationLifecycleState.Final.ToString()
            : result.RetryScheduled
                ? ValidationLifecycleState.RetryScheduled.ToString()
                : ValidationLifecycleState.Provisional.ToString();

    private static UnknownValidationContextV1? Map(UnknownValidationContext? context) => context is null
        ? null
        : new(
            context.Cause.ToString(),
            context.Summary,
            context.Retryable,
            context.RecommendedAction,
            context.SmtpCategory.ToString(),
            context.FailedStage?.ToString(),
            context.ResponseCode,
            context.EnhancedStatusCode,
            context.MxHost,
            context.RetryAfter);
}

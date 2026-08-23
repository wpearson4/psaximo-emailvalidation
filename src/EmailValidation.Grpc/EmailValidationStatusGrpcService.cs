using System.Diagnostics.Metrics;
using System.Security.Claims;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Status.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace EmailValidation.Grpc;

public sealed class EmailValidationStatusGrpcService(
    IValidationStatusQueryService query,
    IValidationStatusSubscription subscription,
    IValidationAccessPolicy accessPolicy,
    TimeProvider timeProvider,
    ILogger<EmailValidationStatusGrpcService> logger) :
    Status.V1.EmailValidationStatus.EmailValidationStatusBase
{
    private static readonly Meter Meter = new("EmailValidation.Grpc");
    private static readonly UpDownCounter<long> ActiveStreams =
        Meter.CreateUpDownCounter<long>("email_validation.grpc.active_streams");
    private static readonly Counter<long> StreamEvents =
        Meter.CreateCounter<long>("email_validation.grpc.stream_events");
    private static readonly Counter<long> StreamLifecycle =
        Meter.CreateCounter<long>("email_validation.grpc.subscriptions");
    private static readonly Histogram<double> PublicationLatency =
        Meter.CreateHistogram<double>("email_validation.grpc.publication_latency", "ms");

    [Authorize(Policy = EmailValidationPolicies.Read)]
    public override async Task<ValidationStatusResponse> GetValidationStatus(
        GetValidationStatusRequest request,
        ServerCallContext context)
    {
        ValidateId(request.ValidationId);
        await AuthorizeAsync(request.ValidationId, context).ConfigureAwait(false);
        var snapshot = await query.GetAsync(request.ValidationId, context.CancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.NotFound, "Validation was not found."));
        return ValidationStatusGrpcMapper.Map(snapshot, timeProvider.GetUtcNow());
    }

    [Authorize(Policy = EmailValidationPolicies.Stream)]
    public override async Task WatchValidationStatus(
        WatchValidationStatusRequest request,
        IServerStreamWriter<ValidationStatusResponse> responseStream,
        ServerCallContext context)
    {
        ValidateId(request.ValidationId);
        if (request.AfterSequence < 0)
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.InvalidArgument, "after_sequence cannot be negative."));
        await AuthorizeAsync(request.ValidationId, context).ConfigureAwait(false);
        if (await query.GetAsync(request.ValidationId, context.CancellationToken).ConfigureAwait(false) is null)
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.NotFound, "Validation was not found."));

        ActiveStreams.Add(1);
        StreamLifecycle.Add(1, new KeyValuePair<string, object?>("event", "opened"));
        var completed = false;
        logger.LogInformation("gRPC status subscriber connected for validation {ValidationId}", request.ValidationId);
        try
        {
            await foreach (var status in subscription.SubscribeAsync(
                request.ValidationId, request.AfterSequence, context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(ValidationStatusGrpcMapper.Map(status), context.CancellationToken)
                    .ConfigureAwait(false);
                StreamEvents.Add(1, new KeyValuePair<string, object?>("lifecycle_state", status.LifecycleState.ToString()));
                PublicationLatency.Record(Math.Max(0, (timeProvider.GetUtcNow() - status.OccurredAt).TotalMilliseconds));
                if (status.LifecycleState is global::EmailValidation.Core.ValidationLifecycleState.Final or
                    global::EmailValidation.Core.ValidationLifecycleState.Failed)
                {
                    completed = true;
                    StreamLifecycle.Add(1, new KeyValuePair<string, object?>("event", "completed"));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            StreamLifecycle.Add(1, new KeyValuePair<string, object?>("event", "disconnected"));
            logger.LogInformation("gRPC status subscriber disconnected for validation {ValidationId}", request.ValidationId);
        }
        finally
        {
            ActiveStreams.Add(-1);
            logger.LogInformation("gRPC status subscription {Outcome} for validation {ValidationId}",
                completed ? "completed" : "closed", request.ValidationId);
        }
    }

    private async Task AuthorizeAsync(string validationId, ServerCallContext context)
    {
        var user = context.GetHttpContext().User;
        var accessContext = new ValidationAccessContext(
            user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"),
            user.FindFirstValue("tenant_id") ?? user.FindFirstValue("tid"),
            user.FindAll("scope").Concat(user.FindAll("scp"))
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToHashSet(StringComparer.Ordinal));
        if (!await accessPolicy.CanAccessAsync(validationId, accessContext, context.CancellationToken)
                .ConfigureAwait(false))
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.PermissionDenied, "Validation is not accessible to this caller."));
    }

    private static void ValidateId(string validationId)
    {
        if (string.IsNullOrWhiteSpace(validationId) || validationId.Length > 128 ||
            validationId.Any(character => character is not
                (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.InvalidArgument,
                "validation_id is required and contains unsupported characters or is too long."));
    }
}

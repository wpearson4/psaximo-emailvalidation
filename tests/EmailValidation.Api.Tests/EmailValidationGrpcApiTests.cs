using EmailValidation.Application;
using EmailValidation.Status.V1;
using EmailValidation.V1;
using Grpc.Core;
using Grpc.Net.Client;

namespace EmailValidation.Api.Tests;

public sealed class EmailValidationGrpcApiTests : IClassFixture<EmailValidationApiFactory>
{
    private readonly EmailValidationApiFactory _factory;

    public EmailValidationGrpcApiTests(EmailValidationApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ValidateEmail_RequiresAuthenticationAndValidateScope()
    {
        using var channel = CreateChannel();
        var client = new EmailValidationService.EmailValidationServiceClient(channel);

        var unauthenticated = await Assert.ThrowsAsync<RpcException>(() =>
            client.ValidateEmailAsync(new ValidateEmailRequest { Email = "valid@example.com" }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, unauthenticated.StatusCode);

        var forbidden = await Assert.ThrowsAsync<RpcException>(() => client.ValidateEmailAsync(
            new ValidateEmailRequest { Email = "valid@example.com" }, Headers([])).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, forbidden.StatusCode);

        var response = await client.ValidateEmailAsync(
            new ValidateEmailRequest { Email = "valid@example.com", EnableSmtp = true },
            Headers([EmailValidationScopes.Validate]));
        Assert.Equal("validation-final", response.ValidationId);
        Assert.Equal("Final", response.LifecycleState);
    }

    [Fact]
    public async Task GetAndWatchStatus_EnforceScopesOwnershipAndDeliverCanonicalStateFirst()
    {
        using var channel = CreateChannel();
        var validation = new EmailValidationService.EmailValidationServiceClient(channel);
        var status = new EmailValidationStatus.EmailValidationStatusClient(channel);

        var read = await validation.GetValidationAsync(
            new GetValidationRequest { ValidationId = "known" },
            Headers([EmailValidationScopes.Read]));
        Assert.Equal("known", read.ValidationId);

        var crossTenant = await Assert.ThrowsAsync<RpcException>(() => validation.GetValidationAsync(
            new GetValidationRequest { ValidationId = "known" },
            Headers([EmailValidationScopes.Read], "tenant-b")).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, crossTenant.StatusCode);

        using var stream = status.WatchValidationStatus(
            new WatchValidationStatusRequest { ValidationId = "known" },
            Headers([EmailValidationScopes.Stream]));
        Assert.True(await stream.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal("known", stream.ResponseStream.Current.ValidationId);
        Assert.Equal(ValidationLifecycleState.Final, stream.ResponseStream.Current.LifecycleState);
        Assert.False(await stream.ResponseStream.MoveNext(CancellationToken.None));
    }

    [Fact]
    public async Task StreamDisconnect_DoesNotAlterCanonicalValidation()
    {
        using var channel = CreateChannel();
        var status = new EmailValidationStatus.EmailValidationStatusClient(channel);
        using var cancellation = new CancellationTokenSource();
        using var stream = status.WatchValidationStatus(
            new WatchValidationStatusRequest { ValidationId = "validation-provisional" },
            Headers([EmailValidationScopes.Stream]), cancellationToken: cancellation.Token);
        Assert.True(await stream.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(ValidationLifecycleState.RetryWaiting, stream.ResponseStream.Current.LifecycleState);
        cancellation.Cancel();

        var current = await status.GetValidationStatusAsync(
            new GetValidationStatusRequest { ValidationId = "validation-provisional" },
            Headers([EmailValidationScopes.Read]));
        Assert.Equal(ValidationLifecycleState.RetryWaiting, current.LifecycleState);
        Assert.True(current.RetryScheduled);
    }

    private GrpcChannel CreateChannel() => GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = _factory.Server.CreateHandler()
    });

    private static Metadata Headers(IReadOnlyList<string> scopes, string tenant = "tenant-a") =>
    [
        new Metadata.Entry("x-test-auth", "valid"),
        new Metadata.Entry("x-test-subject", "consumer-a"),
        new Metadata.Entry("x-test-tenant", tenant),
        new Metadata.Entry("x-test-scopes", string.Join(' ', scopes))
    ];
}

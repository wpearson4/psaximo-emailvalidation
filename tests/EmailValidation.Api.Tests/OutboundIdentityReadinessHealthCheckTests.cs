using System.Net;
using EmailValidation.Api;
using EmailValidation.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EmailValidation.Api.Tests;

public sealed class OutboundIdentityReadinessHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_IsDegradedWhenDnsIsReadyButProviderPoolIsExhausted()
    {
        var now = new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero);
        var root = new EmailValidationOptions
        {
            OutboundIdentities = new OutboundIdentityOptions
            {
                Enabled = true,
                DnsReadiness = new OutboundIdentityDnsReadinessOptions
                {
                    Enabled = true,
                    Mode = OutboundIdentityDnsReadinessMode.Enforced
                },
                Identities =
                [
                    new OutboundIdentityConfiguration { IdentityId = "smtp-173", Enabled = true },
                    new OutboundIdentityConfiguration { IdentityId = "smtp-174", Enabled = true }
                ],
                IdentityGroups = new() { ["General"] = ["smtp-173", "smtp-174"] },
                ProviderGroups = new() { [MailProvider.GenericSmtp.ToString()] = "General" }
            }
        };
        var dns = new FixedReadiness("smtp-173", "smtp-174");
        var health = new FixedHealthStore(now.AddHours(1));
        var check = new OutboundIdentityReadinessHealthCheck(
            Options.Create(root), dns, health, new FixedTimeProvider(now));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    private sealed class FixedReadiness(params string[] identityIds) : IForwardConfirmedReverseDnsValidator
    {
        public Task<ForwardConfirmedReverseDnsState> ValidateAsync(
            OutboundIdentity identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ForwardConfirmedReverseDnsState.Valid);

        public Task<IReadOnlyList<OutboundIdentityDnsReadiness>> GetAllAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboundIdentityDnsReadiness>>(identityIds.Select((identityId, index) => new OutboundIdentityDnsReadiness
            {
                IdentityId = identityId,
                Address = IPAddress.Parse($"64.182.22.{173 + index}"),
                ExpectedHostName = $"outbound-{173 + index}.email.digitalwarehouse.io",
                EhloHostName = $"outbound-{173 + index}.email.digitalwarehouse.io",
                State = ForwardConfirmedReverseDnsState.Valid,
                DnsState = ForwardConfirmedReverseDnsState.Valid,
                IsEligible = true,
                ValidationPolicyVersion = "test"
            }).ToArray());
    }

    private sealed class FixedHealthStore(DateTimeOffset cooldownUntil) : IOutboundIdentityHealthStore
    {
        public Task<OutboundIdentityHealth> GetAsync(
            string identityId,
            MailProvider provider,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(provider == MailProvider.Unknown
                ? new OutboundIdentityHealth(identityId, provider, OutboundIdentityHealthState.Cooldown, cooldownUntil)
                : new OutboundIdentityHealth(identityId, provider, OutboundIdentityHealthState.Healthy));

        public Task RecordAsync(
            OutboundIdentityOutcome outcome,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

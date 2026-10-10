using System.Text.Json;
using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class RoutingAttestationTests
{
    [Fact]
    public void SignedAuthorizedConfiguration_IsVerifiedAgainAfterSerialization()
    {
        var domain = EndpointEvidenceTests.Domain();
        var signed = TestRoutingAttestations.Sign(domain);
        var options = new RoutingAttestationOptions { Authorities = [TestRoutingAttestations.Authority], Attestations = [signed] };
        var verified = RoutingAttestationPolicy.Apply(domain, options, DateTimeOffset.UtcNow);
        Assert.True(verified.CatchAll.HasIndependentRoutingEvidence);
        var persisted = JsonSerializer.Deserialize<DomainIntelligence>(JsonSerializer.Serialize(verified))!;
        Assert.False(persisted.CatchAll.HasIndependentRoutingEvidence);
        var merged = DomainIntelligenceMerge.Merge(persisted, verified);
        Assert.Equal(signed, merged.CatchAll.RoutingAttestation);
        options.Attestations = [];
        Assert.True(RoutingAttestationPolicy.Apply(persisted, options, DateTimeOffset.UtcNow).CatchAll.HasIndependentRoutingEvidence);
        options.RevokedAttestationIds = [signed.AttestationId];
        Assert.False(RoutingAttestationPolicy.Apply(verified, options, DateTimeOffset.UtcNow).CatchAll.HasIndependentRoutingEvidence);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("domain")]
    [InlineData("topology")]
    [InlineData("unauthorized")]
    [InlineData("unknown-source")]
    [InlineData("revoked")]
    public void UnverifiableRouting_IsNeverPromoted(string failure)
    {
        var domain = EndpointEvidenceTests.Domain();
        var now = DateTimeOffset.UtcNow;
        var signed = TestRoutingAttestations.Sign(domain,
            failure == "expired" ? now.AddHours(-3) : failure == "future" ? now.AddHours(1) : null);
        var authority = TestRoutingAttestations.Authority;
        if (failure == "unauthorized") authority.AuthorizedDomains = ["other.test"];
        var options = new RoutingAttestationOptions { Authorities = failure == "unknown-source" ? [] : [authority] };
        signed = failure switch
        {
            "signature" => signed with { Signature = "not-base64" },
            "domain" => signed with { Domain = "changed.test" },
            "topology" => signed with { TopologyFingerprint = "changed" },
            _ => signed
        };
        if (failure == "revoked") options.RevokedAttestationIds = [signed.AttestationId];
        options.Attestations = [signed];
        Assert.False(RoutingAttestationPolicy.Verify(signed, domain, options, now));
        Assert.False(RoutingAttestationPolicy.Apply(domain, options, now).CatchAll.HasIndependentRoutingEvidence);
    }

    [Fact]
    public void LegacyLabelAndSerializedVerifiedFlag_AreNotAuthenticatedEvidence()
    {
        var domain = EndpointEvidenceTests.Domain() with { CatchAll = new(CatchAllStatus.LikelyCatchAll, 2, 2, 0, 0)
        {
            ReasonCode = CatchAllReasonCode.IndependentRoutingEvidence,
            RecipientBehavior = DomainRecipientBehavior.CatchAll, RoutingAttestationVerified = true
        } };
        var checkedDomain = RoutingAttestationPolicy.Apply(domain, new(), DateTimeOffset.UtcNow);
        Assert.False(checkedDomain.CatchAll.HasIndependentRoutingEvidence);
        Assert.Equal(CatchAllStatus.NotAttempted, checkedDomain.CatchAll.Status);
    }
}

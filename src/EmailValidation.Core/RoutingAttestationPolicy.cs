using System.Security.Cryptography;
using System.Text.Json;

namespace EmailValidation.Core;

/// <summary>Signed, domain-authorized configuration is the sole routing attestation ingestion surface.</summary>
public static class RoutingAttestationPolicy
{
    // Stable, versioned JSON array signed using RSA SHA-256 / PKCS#1 v1.5. No private keys enter this service.
    public static byte[] SigningPayload(SignedRoutingAttestation attestation) => JsonSerializer.SerializeToUtf8Bytes(
        new[] { "routing-attestation-v1", attestation.AuthorityId, attestation.AttestationId,
            attestation.Domain, attestation.TopologyFingerprint,
            attestation.IssuedAt.ToUniversalTime().ToString("O"), attestation.ExpiresAt.ToUniversalTime().ToString("O") });

    public static bool Verify(SignedRoutingAttestation attestation, DomainIntelligence domain,
        RoutingAttestationOptions options, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(attestation.AttestationId) ||
            attestation.Domain != domain.Domain || attestation.IssuedAt > now || attestation.ExpiresAt <= now ||
            attestation.ExpiresAt <= attestation.IssuedAt ||
            attestation.TopologyFingerprint != EndpointControlEvidencePolicy.TopologyFingerprint(domain) ||
            options.RevokedAttestationIds.Contains(attestation.AttestationId, StringComparer.Ordinal)) return false;
        var authorities = options.Authorities.Where(authority => authority.Id == attestation.AuthorityId).ToArray();
        if (authorities.Length != 1 ||
            !authorities[0].AuthorizedDomains.Contains(domain.Domain, StringComparer.Ordinal)) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(authorities[0].PublicKeyPem);
            return rsa.KeySize >= 2048 && rsa.VerifyData(SigningPayload(attestation),
                Convert.FromBase64String(attestation.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    public static DomainIntelligence Apply(DomainIntelligence domain, RoutingAttestationOptions options, DateTimeOffset now)
    {
        var candidates = options.Attestations.AsEnumerable();
        if (domain.CatchAll.RoutingAttestation is { } persisted) candidates = candidates.Append(persisted);
        var verified = candidates.Where(item => Verify(item, domain, options, now))
            .OrderByDescending(item => item.IssuedAt).FirstOrDefault();
        if (verified is not null)
            return domain with
            {
                CatchAll = new(CatchAllStatus.LikelyCatchAll, 0, 0, 0, 0,
                    $"Authorized routing attestation {verified.AttestationId} from {verified.AuthorityId}.", 0.99)
                {
                    RoutingAttestation = verified, RoutingAttestationVerified = true,
                    RecipientBehavior = DomainRecipientBehavior.CatchAll,
                    ReasonCode = CatchAllReasonCode.IndependentRoutingEvidence,
                    ObservedAt = verified.IssuedAt, EvidenceExpiresAt = verified.ExpiresAt,
                    StrategyVersion = domain.StrategyVersion
                }
            };
        var controls = domain.CatchAll with { RoutingAttestationVerified = false };
        if (controls.RoutingAttestation is not null || controls.ReasonCode == CatchAllReasonCode.IndependentRoutingEvidence)
            controls = new(CatchAllStatus.NotAttempted, 0, 0, 0, 0,
                "Independent routing evidence is missing, expired, revoked, or unauthorized; fresh evidence is required.");
        return domain with { CatchAll = controls };
    }
}

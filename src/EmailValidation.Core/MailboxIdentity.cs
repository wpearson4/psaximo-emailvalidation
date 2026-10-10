using System.Security.Cryptography;
using System.Text;

namespace EmailValidation.Core;

/// <summary>
/// Versioned mailbox identity. Domain normalization is shared with transport validation;
/// the exact-local-v1 policy never infers equivalence from MX/provider detection.
/// Any future equivalence policy must have a distinct version and contract tests.
/// </summary>
public sealed record MailboxIdentity(string Address, string Key)
{
    public const string KeyVersion = "mailbox-v2";
    public const string PolicyVersion = "exact-local-v1";
    public const string KeyPrefix = KeyVersion + ":" + PolicyVersion + ":";

    public static MailboxIdentity? TryCreate(string email)
    {
        var normalized = new EmailNormalizer().Normalize(email);
        if (!normalized.IsValid) return null;
        var address = normalized.NormalizedEmail!;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address))).ToLowerInvariant();
        return new(address, KeyPrefix + digest);
    }

    public static MailboxIdentity Create(string email) => TryCreate(email) ??
        throw new ArgumentException("A valid mailbox address is required.", nameof(email));

    public static bool Matches(string? key, string? email) => key is not null && email is not null &&
        string.Equals(key, TryCreate(email)?.Key, StringComparison.Ordinal);

    public static string NormalizeOrOriginal(string email) => TryCreate(email)?.Address ?? email.Trim();
}

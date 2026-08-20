using System.Globalization;
using System.Net.Mail;

namespace EmailValidation.Core;

public sealed class EmailNormalizer : IEmailNormalizer
{
    public NormalizationResult Normalize(string input)
    {
        var original = input ?? string.Empty;
        var trimmed = original.Trim();
        if (trimmed.Length == 0)
            return Failure(original, ReasonCode.EmptyInput);

        var separator = trimmed.LastIndexOf('@');
        if (separator < 0)
            return Failure(original, ReasonCode.MissingDomain);
        if (separator == 0)
            return Failure(original, ReasonCode.MissingLocalPart);
        if (separator == trimmed.Length - 1)
            return Failure(original, ReasonCode.MissingDomain);

        var local = trimmed[..separator];
        var rawDomain = trimmed[(separator + 1)..];
        string domain;
        try
        {
            domain = new IdnMapping().GetAscii(rawDomain).ToLowerInvariant().TrimEnd('.');
        }
        catch (ArgumentException)
        {
            return Failure(original, ReasonCode.InvalidDomain);
        }

        if (!IsDomainValid(domain))
            return Failure(original, ReasonCode.InvalidDomain);

        var normalized = $"{local}@{domain}";
        try
        {
            var parsed = new MailAddress(normalized);
            if (!string.Equals(parsed.Address, normalized, StringComparison.Ordinal))
                return Failure(original, ReasonCode.InvalidSyntax);
        }
        catch (FormatException)
        {
            return Failure(original, ReasonCode.InvalidSyntax);
        }

        return new(true, original, normalized, local, domain, null);
    }

    private static bool IsDomainValid(string domain)
    {
        if (domain.Length is 0 or > 253 || !domain.Contains('.')) return false;
        foreach (var label in domain.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) return false;
        }
        return true;
    }

    private static NormalizationResult Failure(string original, ReasonCode reason) =>
        new(false, original, null, null, null, reason);
}

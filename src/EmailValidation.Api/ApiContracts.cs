namespace EmailValidation.Api;

public sealed record ValidateEmailApiRequest(
    string Email,
    bool EnableSmtp = true,
    bool Verbose = false,
    string? ValidationId = null);

public sealed record CreateValidationJobApiRequest(
    IReadOnlyList<string> Emails,
    bool EnableSmtp = true);

namespace EmailValidation.Core;

/// <summary>Cleaning removes only definitive, finalized invalidity; uncertainty preserves the address.</summary>
public static class InvalidEmailRemovalPolicy
{
    public static bool CanRemove(EmailValidationResult? result) =>
        result is { Status: EmailValidationStatus.Invalid, ResultState: ValidationResultState.Final };
}

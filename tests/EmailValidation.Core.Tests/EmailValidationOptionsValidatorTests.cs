using EmailValidation.Core;
using EmailValidation.Infrastructure;

namespace EmailValidation.Core.Tests;

public sealed class EmailValidationOptionsValidatorTests
{
    [Fact]
    public void InvalidSenderSourceConfiguration_ReturnsActionableFailures()
    {
        var options = new EmailValidationOptions
        {
            ProbeSenderSource = new ProbeSenderSourceOptions
            {
                Endpoint = "not-a-uri",
                Index = "",
                EmailField = "",
                QueryLimit = 6_000,
                RefreshThreshold = 6_000,
                QueryJson = ""
            }
        };

        var result = new EmailValidationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Endpoint", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Index", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("EmailField", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("QueryLimit", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Query is required", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultOperationalConfiguration_IsAccepted()
    {
        var options = new EmailValidationOptions
        {
            ProbeSenderSource = new ProbeSenderSourceOptions
            {
                Index = "authorized-senders",
                QueryJson = "{\"match_all\":{}}"
            }
        };

        var result = new EmailValidationOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }
}

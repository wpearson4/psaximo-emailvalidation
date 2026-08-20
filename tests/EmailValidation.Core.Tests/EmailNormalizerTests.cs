using EmailValidation.Core;

namespace EmailValidation.Core.Tests;

public sealed class EmailNormalizerTests
{
    private readonly EmailNormalizer _normalizer = new();

    [Fact]
    public void Normalize_TrimsAndLowercasesOnlyDomain()
    {
        var result = _normalizer.Normalize("  First.Last@EXAMPLE.COM  ");

        Assert.True(result.IsValid);
        Assert.Equal("First.Last@example.com", result.NormalizedEmail);
        Assert.Equal("First.Last", result.LocalPart);
    }

    [Fact]
    public void Normalize_ConvertsUnicodeDomainToIdn()
    {
        var result = _normalizer.Normalize("user@bücher.example");

        Assert.True(result.IsValid);
        Assert.Equal("user@xn--bcher-kva.example", result.NormalizedEmail);
    }

    [Theory]
    [InlineData("", ReasonCode.EmptyInput)]
    [InlineData("user", ReasonCode.MissingDomain)]
    [InlineData("@example.com", ReasonCode.MissingLocalPart)]
    [InlineData("user@", ReasonCode.MissingDomain)]
    [InlineData("user@-bad.example", ReasonCode.InvalidDomain)]
    [InlineData("user@example", ReasonCode.InvalidDomain)]
    [InlineData("first last@example.com", ReasonCode.InvalidSyntax)]
    public void Normalize_ReturnsSpecificReasonForMalformedInput(string input, ReasonCode reason)
    {
        var result = _normalizer.Normalize(input);

        Assert.False(result.IsValid);
        Assert.Equal(reason, result.FailureReason);
    }
}

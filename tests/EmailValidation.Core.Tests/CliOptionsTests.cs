using EmailValidation.ConsoleApp;

namespace EmailValidation.Core.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void ValidateCommand_PreservesPositionalAddressAndExistingSwitches()
    {
        var options = CliOptions.Parse(
            ["validate", "john@example.com", "--verbose", "--live", "--format", "json", "--output", "result.json"]);

        Assert.Equal("validate", options.Command);
        Assert.Equal(["john@example.com"], options.Values);
        Assert.True(options.Verbose);
        Assert.True(options.Live);
        Assert.Equal(OutputFormat.Json, options.Format);
        Assert.Equal("result.json", options.OutputPath);
    }

    [Fact]
    public void DiagnosticsCommand_PreservesSmtpPositionalArgument()
    {
        var options = CliOptions.Parse(["diagnostics", "smtp"]);

        Assert.Equal("diagnostics", options.Command);
        Assert.Equal(["smtp"], options.Values);
    }

    [Fact]
    public void FileCommand_DistinguishesAutomaticAndExplicitColumnSelection()
    {
        var automatic = CliOptions.Parse(["file", "emails.csv"]);
        var explicitSelection = CliOptions.Parse(["file", "emails.csv", "--column", "BUSINESS_EMAIL"]);

        Assert.False(automatic.ColumnSpecified);
        Assert.Null(automatic.Column);
        Assert.True(explicitSelection.ColumnSpecified);
        Assert.Equal("BUSINESS_EMAIL", explicitSelection.Column);
    }
}

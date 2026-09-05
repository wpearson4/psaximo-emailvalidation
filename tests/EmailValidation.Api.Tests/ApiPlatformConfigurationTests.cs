using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace EmailValidation.Api.Tests;

public sealed class ApiPlatformConfigurationTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5058")]
    [InlineData("http://localhost:5058")]
    public void ProductionRejectsLoopbackOpenMetaOrigins(string origin)
    {
        Assert.False(ApiPlatformExtensions.IsUsableOpenMetaOrigin(
            origin, new TestHostEnvironment(Environments.Production)));
    }

    [Fact]
    public void ProductionAcceptsThePublicOpenMetaOrigin()
    {
        Assert.True(ApiPlatformExtensions.IsUsableOpenMetaOrigin(
            "https://api.digitalwarehouse.io", new TestHostEnvironment(Environments.Production)));
    }

    [Fact]
    public void DevelopmentAllowsTheLocalOpenMetaOrigin()
    {
        Assert.True(ApiPlatformExtensions.IsUsableOpenMetaOrigin(
            "http://127.0.0.1:5058", new TestHostEnvironment(Environments.Development)));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "EmailValidation.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

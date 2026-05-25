namespace Dmon.Extensions.Omlx.Tests;

public sealed class OmlxConfigTests
{
    [Fact]
    public void FromEnvironment_ReturnsDefaults_WhenEnvVarsAbsent()
    {
        string? savedBaseUrl = Environment.GetEnvironmentVariable("OMLX_BASE_URL");
        string? savedApiKey = Environment.GetEnvironmentVariable("OMLX_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OMLX_BASE_URL", null);
            Environment.SetEnvironmentVariable("OMLX_API_KEY", null);

            OmlxConfig config = OmlxConfig.FromEnvironment();

            Assert.Equal("http://localhost:8666", config.BaseUrl);
            Assert.Equal(string.Empty, config.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMLX_BASE_URL", savedBaseUrl);
            Environment.SetEnvironmentVariable("OMLX_API_KEY", savedApiKey);
        }
    }

    [Fact]
    public void FromEnvironment_OverridesBaseUrl_WhenEnvVarSet()
    {
        string? savedBaseUrl = Environment.GetEnvironmentVariable("OMLX_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("OMLX_BASE_URL", "http://localhost:9999");

            OmlxConfig config = OmlxConfig.FromEnvironment();

            Assert.Equal("http://localhost:9999", config.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMLX_BASE_URL", savedBaseUrl);
        }
    }

    [Fact]
    public void FromEnvironment_OverridesApiKey_WhenEnvVarSet()
    {
        string? savedApiKey = Environment.GetEnvironmentVariable("OMLX_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OMLX_API_KEY", "test-secret-key");

            OmlxConfig config = OmlxConfig.FromEnvironment();

            Assert.Equal("test-secret-key", config.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMLX_API_KEY", savedApiKey);
        }
    }
}

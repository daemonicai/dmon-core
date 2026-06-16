using Dmon.Memory.Meko;

namespace Dmon.Memory.Meko.Tests.Meko;

public sealed class MekoLongTermOptionsTests
{
    [Fact]
    public void DefaultEndpoint_IsProductionMekoMcp()
    {
        var options = new MekoLongTermOptions();
        Assert.Equal(new Uri("https://mcp.mekodata.ai/mcp"), options.Endpoint);
    }

    [Fact]
    public void DefaultApiKey_IsEmpty()
    {
        var options = new MekoLongTermOptions();
        Assert.Equal(string.Empty, options.ApiKey);
    }

    [Fact]
    public void ApiKey_IsNotExposedInToString()
    {
        // MekoLongTermOptions must not accidentally leak the API key via ToString().
        // It is a plain class (not a record), so ToString() returns the type name.
        var options = new MekoLongTermOptions { ApiKey = "mko_tkn_secret" };
        string str = options.ToString() ?? string.Empty;
        Assert.DoesNotContain("mko_tkn_secret", str);
    }
}

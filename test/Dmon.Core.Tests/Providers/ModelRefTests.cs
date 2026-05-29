using Dmon.Abstractions.Providers;

namespace Dmon.Core.Tests.Providers;

public sealed class ModelRefTests
{
    // --- parse: standard provider/model ---

    [Fact]
    public void Parse_ProviderSlashModel_ReturnsBothSegments()
    {
        ModelRef? result = ModelRef.Parse("gemini/gemini-3.1-flash-lite");

        Assert.NotNull(result);
        Assert.Equal("gemini", result.Provider);
        Assert.Equal("gemini-3.1-flash-lite", result.Model);
    }

    [Fact]
    public void Parse_MultiSlashModel_ProviderIsBeforeFirstSlash_ModelIsRemainder()
    {
        ModelRef? result = ModelRef.Parse("ollama/deepseek/deepseek-v4-pro");

        Assert.NotNull(result);
        Assert.Equal("ollama", result.Provider);
        Assert.Equal("deepseek/deepseek-v4-pro", result.Model);
    }

    // --- parse: provider-only ---

    [Fact]
    public void Parse_ProviderOnly_ReturnsNullModel()
    {
        ModelRef? result = ModelRef.Parse("gemini");

        Assert.NotNull(result);
        Assert.Equal("gemini", result.Provider);
        Assert.Null(result.Model);
    }

    // --- parse: null / empty / whitespace ---

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(ModelRef.Parse(null));
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(ModelRef.Parse(string.Empty));
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsNull()
    {
        Assert.Null(ModelRef.Parse("   "));
    }

    // --- parse: empty provider segment ---

    [Fact]
    public void Parse_LeadingSlash_EmptyProvider_ReturnsNull()
    {
        // "/foo" has an empty provider segment.
        Assert.Null(ModelRef.Parse("/foo"));
    }

    // --- parse: trailing slash (empty model segment) ---

    [Fact]
    public void Parse_TrailingSlash_EmptyModel_ReturnsNullModel()
    {
        ModelRef? result = ModelRef.Parse("gemini/");

        Assert.NotNull(result);
        Assert.Equal("gemini", result.Provider);
        Assert.Null(result.Model);
    }

    // --- ToString ---

    [Fact]
    public void ToString_WithModel_ReturnsCombinedString()
    {
        ModelRef r = new("gemini", "gemini-3.1-flash-lite");
        Assert.Equal("gemini/gemini-3.1-flash-lite", r.ToString());
    }

    [Fact]
    public void ToString_NullModel_ReturnsProviderOnly()
    {
        ModelRef r = new("anthropic", null);
        Assert.Equal("anthropic", r.ToString());
    }

    [Fact]
    public void ToString_MultiSlashModel_PreservesSlashes()
    {
        ModelRef r = new("ollama", "deepseek/deepseek-v4-pro");
        Assert.Equal("ollama/deepseek/deepseek-v4-pro", r.ToString());
    }

    // --- round-trip ---

    [Theory]
    [InlineData("gemini/gemini-3.1-flash-lite")]
    [InlineData("ollama/deepseek/deepseek-v4-pro")]
    [InlineData("anthropic/claude-opus-4-5")]
    public void RoundTrip_ParseThenToString_ReturnsOriginal(string input)
    {
        ModelRef? parsed = ModelRef.Parse(input);
        Assert.NotNull(parsed);
        Assert.Equal(input, parsed.ToString());
    }
}

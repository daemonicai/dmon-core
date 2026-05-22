using Daemon.Core.Extensions;

namespace Daemon.Core.Tests.Extensions;

public sealed class ParsedExtensionSourceTests
{
    [Theory]
    [InlineData("nuget:MyPackage", "nuget", "MyPackage", null)]
    [InlineData("nuget:MyPackage/1.2.3", "nuget", "MyPackage", "1.2.3")]
    [InlineData("nuget:Some.Package/2.0.0-preview", "nuget", "Some.Package", "2.0.0-preview")]
    [InlineData("NuGet:Package", "nuget", "Package", null)]
    public void Parse_NuGet_ParsedCorrectly(string source, string kind, string value, string? version)
    {
        ParsedExtensionSource parsed = ParsedExtensionSource.Parse(source);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(value, parsed.Value);
        Assert.Equal(version, parsed.Version);
    }

    [Theory]
    [InlineData("myext.csx", "script")]
    [InlineData("/path/to/script.csx", "script")]
    public void Parse_Script_ParsedCorrectly(string source, string kind)
    {
        ParsedExtensionSource parsed = ParsedExtensionSource.Parse(source);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(source, parsed.Value);
    }

    [Theory]
    [InlineData("/path/to/myext.dll", "assembly")]
    [InlineData("C:\\extensions\\myext.dll", "assembly")]
    [InlineData("./extension.dll", "assembly")]
    [InlineData("../lib/myext.dll", "assembly")]
    public void Parse_DllPath_ParsedAsAssembly(string source, string kind)
    {
        ParsedExtensionSource parsed = ParsedExtensionSource.Parse(source);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(source, parsed.Value);
    }

    [Fact]
    public void Parse_BarePackageName_ParsedAsNuGet()
    {
        ParsedExtensionSource parsed = ParsedExtensionSource.Parse("SomeExtension");

        Assert.Equal("nuget", parsed.Kind);
        Assert.Equal("SomeExtension", parsed.Value);
        Assert.Null(parsed.Version);
    }

    [Fact]
    public void Parse_BarePackageNameWithSlash_NotParsedAsNuGet()
    {
        // A bare name with path separators is treated as an assembly path.
        ParsedExtensionSource parsed = ParsedExtensionSource.Parse("lib/SomeExtension");

        Assert.Equal("assembly", parsed.Kind);
    }
}

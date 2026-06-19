using System.Reflection;
using System.Reflection.Emit;
using Dmon.Core.Rpc;

namespace Dmon.Core.Tests.Rpc;

public sealed class RpcHostedServiceVersionTests
{
    [Fact]
    public void ResolveCoreVersion_WithInformationalAttribute_ReturnsInformationalVersion()
    {
        // Arrange — build a dynamic assembly that carries an AssemblyInformationalVersionAttribute.
        AssemblyName assemblyName = new("TestAssembly.Stamped");
        AssemblyBuilder builder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);

        string expected = "0.2.0-preview.23+abc1234";
        builder.SetCustomAttribute(
            new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
                [expected]));

        // Act
        string result = RpcHostedService.ResolveCoreVersion(builder);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveCoreVersion_WithoutInformationalAttribute_FallsBackToNumericVersion()
    {
        // Arrange — dynamic assembly with no informational attribute; AssemblyName carries a version.
        AssemblyName assemblyName = new("TestAssembly.NoInfo") { Version = new Version(1, 2, 3, 4) };
        AssemblyBuilder builder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);

        // Act
        string result = RpcHostedService.ResolveCoreVersion(builder);

        // Assert — must equal the numeric version string, not empty, not "0.0.0".
        Assert.Equal("1.2.3.4", result);
    }

    [Fact]
    public void ResolveCoreVersion_WithoutAnyVersionInfo_ReturnsNonEmptyString()
    {
        // Arrange — dynamic assembly with no informational attribute and no version on the name.
        // AssemblyName.Version defaults to 0.0.0.0 for dynamic assemblies when not set,
        // so the numeric fallback returns "0.0.0.0"; the "0.0.0" sentinel is only reached
        // when Version is genuinely null (not constructable via AssemblyBuilder).
        AssemblyName assemblyName = new("TestAssembly.Bare");
        AssemblyBuilder builder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);

        // Act
        string result = RpcHostedService.ResolveCoreVersion(builder);

        // Assert — must not be null or empty; the value is never blank under any code path.
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void ResolveCoreVersion_ExecutingAssembly_IsNotEmpty()
    {
        // Smoke: the real executing assembly (Dmon.Core) must always produce a non-empty version,
        // regardless of whether this test runs from a tagged or untagged build.
        string result = RpcHostedService.ResolveCoreVersion(Assembly.GetExecutingAssembly());

        Assert.False(string.IsNullOrEmpty(result));
    }
}

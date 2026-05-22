using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Daemon.Extensions;
using Microsoft.Extensions.AI;

namespace Daemon.Extensions.Tests;

public class DaemonAIFunctionFactoryTests
{
    [Fact]
    public void Create_FromDelegate_SetsName()
    {
        var fn = DaemonAIFunctionFactory.Create(
            () => 42,
            "GetAnswer");

        Assert.Equal("GetAnswer", fn.Name);
    }

    [Fact]
    public void Create_FromDelegate_SetsDescription()
    {
        var fn = DaemonAIFunctionFactory.Create(
            () => 42,
            "GetAnswer",
            "Returns the answer to life, the universe, and everything.");

        Assert.Equal(
            "Returns the answer to life, the universe, and everything.",
            fn.Description);
    }

    [Fact]
    public void Create_FromDelegate_WithNoDescription_HasEmptyDescription()
    {
        var fn = DaemonAIFunctionFactory.Create(
            () => 42,
            "GetAnswer");

        Assert.Equal(string.Empty, fn.Description);
    }

    [Fact]
    public async Task Create_FromDelegate_CanInvoke_Parameterless()
    {
        var fn = DaemonAIFunctionFactory.Create(
            () => 42,
            "GetAnswer");

        var args = new AIFunctionArguments();
        var result = await fn.InvokeAsync(args);

        Assert.Equal(42, ((JsonElement)result!).GetInt32());
    }

    [Fact]
    public async Task Create_FromDelegate_CanInvoke_WithParameters()
    {
        var fn = DaemonAIFunctionFactory.Create(
            (int a, int b) => a + b,
            "AddNumbers",
            "Adds two integers.");

        var args = new AIFunctionArguments
        {
            ["a"] = 3,
            ["b"] = 7
        };
        var result = await fn.InvokeAsync(args);

        Assert.Equal(10, ((JsonElement)result!).GetInt32());
    }

    [Fact]
    public async Task Create_FromDelegate_CanInvoke_StringParameters()
    {
        var fn = DaemonAIFunctionFactory.Create(
            (string name, int age) => $"Hello {name}, you are {age}",
            "GreetPerson");

        var args = new AIFunctionArguments
        {
            ["name"] = "Alice",
            ["age"] = 30
        };
        var result = await fn.InvokeAsync(args);

        Assert.Equal("Hello Alice, you are 30", ((JsonElement)result!).GetString());
    }

    [Fact]
    public void Create_FromDelegate_GeneratesJsonSchema()
    {
        var fn = DaemonAIFunctionFactory.Create(
            (int a, int b) => a + b,
            "AddNumbers");

        var schema = ((AIFunctionDeclaration)fn).JsonSchema;
        // Schema should be a JSON object with properties for "a" and "b"
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
    }

    [Fact]
    public void Create_FromDelegate_WithJsonOptions_UsesOptions()
    {
        var options = JsonSerializerOptions.Default;

        var fn = DaemonAIFunctionFactory.Create(
            (int someValue, string displayName) => $"{displayName}={someValue}",
            "FormatValue",
            "Formats a value with a name.",
            options);

        var schema = ((AIFunctionDeclaration)fn).JsonSchema;
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
    }

    [Fact]
    public void Create_FromDelegate_NullMethod_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            DaemonAIFunctionFactory.Create((Delegate)null!, "Name"));

        Assert.Equal("method", ex.ParamName);
    }

    [Fact]
    public void Create_FromDelegate_NullName_UsesDelegateMethodName()
    {
        // AIFunctionFactory may use the delegate's method name as fallback
        // We test that null name doesn't throw.
        var fn = DaemonAIFunctionFactory.Create(
            () => 42,
            null!);

        // Should still have a non-empty name (falls back to delegate method name)
        Assert.False(string.IsNullOrEmpty(fn.Name));
    }

    [Fact]
    public void Create_FromDelegate_WithOptions_SetsNameFromOptions()
    {
        var options = new AIFunctionFactoryOptions
        {
            Name = "CustomName",
            Description = "Custom description"
        };

        var fn = DaemonAIFunctionFactory.Create(() => 42, options);

        Assert.Equal("CustomName", fn.Name);
        Assert.Equal("Custom description", fn.Description);
    }

    [Fact]
    public async Task Create_FromMethodInfo_StaticMethod_CanInvoke()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.StaticAdd))!;

        var fn = DaemonAIFunctionFactory.Create(
            method,
            target: null,
            "AddNumbers",
            "Adds two integers.");

        var args = new AIFunctionArguments
        {
            ["a"] = 5,
            ["b"] = 12
        };
        var result = await fn.InvokeAsync(args);

        Assert.Equal(17, ((JsonElement)result!).GetInt32());
    }

    [Fact]
    public async Task Create_FromMethodInfo_InstanceMethod_CanInvoke()
    {
        var instance = new TestMethods { Multiplier = 10 };
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.Scale))!;

        var fn = DaemonAIFunctionFactory.Create(
            method,
            target: instance,
            "Scale",
            "Scales a number by a multiplier.");

        var args = new AIFunctionArguments
        {
            ["value"] = 7
        };
        var result = await fn.InvokeAsync(args);

        Assert.Equal(70, ((JsonElement)result!).GetInt32());
    }

    [Fact]
    public void Create_FromMethodInfo_NullMethod_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            DaemonAIFunctionFactory.Create((MethodInfo)null!, target: null, "Name"));

        Assert.Equal("method", ex.ParamName);
    }

    [Fact]
    public void Create_FromMethodInfo_NullMethod_WithOptions_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            DaemonAIFunctionFactory.Create(
                (MethodInfo)null!,
                target: null,
                new AIFunctionFactoryOptions()));

        Assert.Equal("method", ex.ParamName);
    }

    [Fact]
    public void Create_FromOptions_WithNullDelegate_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            DaemonAIFunctionFactory.Create(
                (Delegate)null!,
                new AIFunctionFactoryOptions()));

        Assert.Equal("method", ex.ParamName);
    }

    [Fact]
    public async Task Create_ParameterDescriptions_AreIncludedInSchema()
    {
        var fn = DaemonAIFunctionFactory.Create(
            ([Description("The first number to add")] int a,
             [Description("The second number to add")] int b) => a + b,
            "AddNumbers",
            "Adds two numbers.");

        var schema = ((AIFunctionDeclaration)fn).JsonSchema;
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);

        var schemaJson = schema.GetRawText();
        Assert.Contains("first number", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("second number", schemaJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_MultipleFunctions_HaveUniqueNames()
    {
        var fn1 = DaemonAIFunctionFactory.Create(() => 1, "Func1");
        var fn2 = DaemonAIFunctionFactory.Create(() => 2, "Func2");

        Assert.NotEqual(fn1.Name, fn2.Name);
    }

    /// <summary>
    /// Test methods for MethodInfo-based creation.
    /// </summary>
    private sealed class TestMethods
    {
        public int Multiplier { get; set; }

        [Description("Adds two integers and returns the sum.")]
        public static int StaticAdd(int a, int b) => a + b;

        [Description("Multiplies a value by the configured multiplier.")]
        public int Scale(int value) => value * Multiplier;
    }
}
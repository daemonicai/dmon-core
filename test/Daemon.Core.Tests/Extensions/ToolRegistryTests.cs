using Daemon.Core.Extensions;
using Microsoft.Extensions.AI;

namespace Daemon.Core.Tests.Extensions;

public sealed class ToolRegistryTests
{
    private static AIFunction MakeFunction(string name) =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(
            () => name,
            name,
            $"Test function {name}");

    [Fact]
    public void Register_AddsToolCount_ToGetSnapshot()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("ext1", [MakeFunction("fn1"), MakeFunction("fn2")]);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Single(snapshot);
        Assert.Equal("ext1", snapshot[0].Name);
        Assert.Equal(2, snapshot[0].ToolCount);
    }

    [Fact]
    public void Register_ReplacesExistingExtension()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("ext", [MakeFunction("old")]);
        registry.Register("ext", [MakeFunction("new1"), MakeFunction("new2"), MakeFunction("new3")]);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Single(snapshot);
        Assert.Equal(3, snapshot[0].ToolCount);
    }

    [Fact]
    public void Unregister_RemovesExtension()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("ext", [MakeFunction("fn")]);
        registry.Unregister("ext");

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Empty(snapshot);
    }

    [Fact]
    public void Unregister_DoesNotThrow_WhenExtensionNotFound()
    {
        IToolRegistry registry = new ToolRegistry();

        // Should not throw.
        registry.Unregister("nonexistent");
    }

    [Fact]
    public void GetAll_ReturnsAllToolsFromAllExtensions()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("a", [MakeFunction("fa1"), MakeFunction("fa2")]);
        registry.Register("b", [MakeFunction("fb1")]);

        IReadOnlyList<AIFunction> all = registry.GetAll();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, f => f.Name == "fa1");
        Assert.Contains(all, f => f.Name == "fa2");
        Assert.Contains(all, f => f.Name == "fb1");
    }

    [Fact]
    public void GetAll_ReturnsEmpty_WhenNoExtensionsRegistered()
    {
        IToolRegistry registry = new ToolRegistry();

        IReadOnlyList<AIFunction> all = registry.GetAll();

        Assert.Empty(all);
    }

    [Fact]
    public void Clear_RemovesAllExtensions()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("a", [MakeFunction("f")]);
        registry.Register("b", [MakeFunction("f")]);

        registry.Clear();

        Assert.Empty(registry.GetSnapshot());
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void GetSnapshot_AfterClear_ReturnsEmpty()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("a", [MakeFunction("f")]);
        registry.Clear();

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Empty(snapshot);
    }

    [Fact]
    public void Register_MultipleExtensions_AllInSnapshot()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("alpha", [MakeFunction("f1")]);
        registry.Register("beta", [MakeFunction("f2"), MakeFunction("f3")]);
        registry.Register("gamma", [MakeFunction("f4"), MakeFunction("f5"), MakeFunction("f6")]);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Contains(snapshot, s => s.Name == "alpha" && s.ToolCount == 1);
        Assert.Contains(snapshot, s => s.Name == "beta" && s.ToolCount == 2);
        Assert.Contains(snapshot, s => s.Name == "gamma" && s.ToolCount == 3);
    }

    [Fact]
    public void Register_CaseInsensitive_ExtensionName()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("MyExt", [MakeFunction("f1")]);
        registry.Register("myext", [MakeFunction("f2")]); // Same name, different case — replaces.

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Single(snapshot);
        Assert.Equal(1, snapshot[0].ToolCount);
    }
}

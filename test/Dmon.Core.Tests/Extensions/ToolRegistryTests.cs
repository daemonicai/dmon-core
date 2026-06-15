using Dmon.Core.Extensions;
using Dmon.Abstractions.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Extensions;

public sealed class ToolRegistryTests
{
    private static AIFunction MakeFunction(string name) =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(
            () => name,
            name,
            $"Test function {name}");

    private static IToolExtension MakeExtension(string name) => new StubExtension(name);

    private sealed class StubExtension(string name) : IToolExtension
    {
        public string Name => name;
        public string Description => $"Stub extension {name}";
        public IEnumerable<AIFunction> Tools => [];
    }

    [Fact]
    public void Register_AddsToolCount_ToGetSnapshot()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("ext1", MakeExtension("ext1"), [MakeFunction("fn1"), MakeFunction("fn2")]);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Single(snapshot);
        Assert.Equal("ext1", snapshot[0].Name);
        Assert.Equal(2, snapshot[0].ToolCount);
    }

    [Fact]
    public void Register_ReplacesExistingExtension()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("ext", MakeExtension("ext"), [MakeFunction("old")]);
        registry.Register("ext", MakeExtension("ext"), [MakeFunction("new1"), MakeFunction("new2"), MakeFunction("new3")]);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Single(snapshot);
        Assert.Equal(3, snapshot[0].ToolCount);
    }

    [Fact]
    public void Unregister_RemovesExtension()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("ext", MakeExtension("ext"), [MakeFunction("fn")]);
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
        registry.Register("a", MakeExtension("a"), [MakeFunction("fa1"), MakeFunction("fa2")]);
        registry.Register("b", MakeExtension("b"), [MakeFunction("fb1")]);

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
        registry.Register("a", MakeExtension("a"), [MakeFunction("f")]);
        registry.Register("b", MakeExtension("b"), [MakeFunction("f")]);

        registry.Clear();

        Assert.Empty(registry.GetSnapshot());
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void GetSnapshot_AfterClear_ReturnsEmpty()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("a", MakeExtension("a"), [MakeFunction("f")]);
        registry.Clear();

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Empty(snapshot);
    }

    [Fact]
    public void Register_MultipleExtensions_AllInSnapshot()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("alpha", MakeExtension("alpha"), [MakeFunction("f1")]);
        registry.Register("beta", MakeExtension("beta"), [MakeFunction("f2"), MakeFunction("f3")]);
        registry.Register("gamma", MakeExtension("gamma"), [MakeFunction("f4"), MakeFunction("f5"), MakeFunction("f6")]);

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
        registry.Register("MyExt", MakeExtension("MyExt"), [MakeFunction("f1")]);
        registry.Register("myext", MakeExtension("myext"), [MakeFunction("f2")]); // Same name, different case — replaces.

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();

        Assert.Single(snapshot);
        Assert.Equal(1, snapshot[0].ToolCount);
    }

    [Fact]
    public void FindExtension_ReturnsExtension_WhenToolRegistered()
    {
        IToolRegistry registry = new ToolRegistry();
        IToolExtension ext = MakeExtension("myext");
        registry.Register("myext", ext, [MakeFunction("tool1")]);

        IToolExtension? found = registry.FindExtension("tool1");

        Assert.Same(ext, found);
    }

    [Fact]
    public void FindExtension_ReturnsNull_WhenToolNotRegistered()
    {
        IToolRegistry registry = new ToolRegistry();

        IToolExtension? found = registry.FindExtension("nonexistent");

        Assert.Null(found);
    }

    [Fact]
    public void FindExtension_ReturnsNull_AfterUnregister()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("ext", MakeExtension("ext"), [MakeFunction("fn")]);
        registry.Unregister("ext");

        IToolExtension? found = registry.FindExtension("fn");

        Assert.Null(found);
    }

    [Fact]
    public void FindExtension_ReturnsNull_AfterClear()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("ext", MakeExtension("ext"), [MakeFunction("fn")]);
        registry.Clear();

        Assert.Null(registry.FindExtension("fn"));
    }

    [Fact]
    public void FindExtension_ReturnsNewExtension_AfterReplace_OldToolReturnsNull()
    {
        IToolRegistry registry = new ToolRegistry();
        IToolExtension original = MakeExtension("v1");
        IToolExtension replacement = MakeExtension("v2");

        registry.Register("ext", original, [MakeFunction("old_tool")]);
        registry.Register("ext", replacement, [MakeFunction("new_tool")]);

        Assert.Same(replacement, registry.FindExtension("new_tool"));
        Assert.Null(registry.FindExtension("old_tool"));
    }
}

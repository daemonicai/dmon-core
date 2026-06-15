using System.IO.Pipelines;
using System.Text.Json;
using Dmon.Abstractions.Profiles;
using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions;
using Dmon.Core.Providers;
using Dmon.Extensions;
using Dmon.Hosting;
using Dmon.Protocol;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Hosting;

/// <summary>
/// Golden-path tests: verify that a host built via
/// <see cref="DmonHost.CreateBuilder(string[])"/> emits <c>agentReady</c>
/// with the correct protocol version before entering the command loop,
/// and that builder overrides (model, profile, extensions) take effect.
/// The host is driven entirely in-process via injected stdio streams.
/// </summary>
public sealed class DmonHostGoldenPathTests
{
    /// <summary>
    /// A host built with <see cref="DmonHostBuilder.WithStdio"/> emits <c>agentReady</c>
    /// on its stdout with the correct <c>protocolVersion</c>.
    /// </summary>
    [Fact]
    public async Task RunAsync_EmitsAgentReadyWithCorrectProtocolVersion()
    {
        // Arrange: pipe for stdout (host writes) and a CTS that the test cancels as soon as
        // agentReady is found — this stops the host without waiting the full default timeout.
        Pipe outPipe = new();
        await using StreamWriter stdout = new(outPipe.Writer.AsStream(), leaveOpen: true);
        using TextReader stdin = new StringReader(string.Empty);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        DmonBuiltHost host = DmonHost.CreateBuilder([])
            .WithStdio(stdin, stdout)
            .WithoutTelemetry()
            .Build();

        // Run the host on a background task; read from the pipe concurrently so we can
        // cancel the host as soon as agentReady arrives, without waiting for the CTS timeout.
        Task runTask = host.RunAsync(cts.Token);

        string? agentReadyLine = await ReadUntilAgentReadyAsync(outPipe.Reader, cts);

        // Signal the host to stop now that we have what we need.
        await cts.CancelAsync();
        await runTask;

        // Complete the write end so the reader gets EOF.
        await outPipe.Writer.CompleteAsync();

        Assert.NotNull(agentReadyLine);

        JsonElement root = JsonDocument.Parse(agentReadyLine).RootElement;
        Assert.Equal("agentReady", root.GetProperty("type").GetString());
        Assert.Equal(ProtocolVersion.Current, root.GetProperty("protocolVersion").GetString());
    }

    /// <summary>
    /// An extension registered via <see cref="DmonHostBuilder.AddExtension"/> has its tool
    /// present in <see cref="IToolRegistry"/> after <c>Build()</c>.
    /// </summary>
    [Fact]
    public void Build_ExtensionRegisteredViaBuilder_ToolLandsInRegistry()
    {
        using TextReader stdin = new StringReader(string.Empty);
        using StreamWriter stdout = new(Stream.Null);

        DmonBuiltHost host = DmonHost.CreateBuilder([])
            .WithStdio(stdin, stdout)
            .WithoutTelemetry()
            .AddExtension<TooledExtension>()
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        IReadOnlyList<AIFunction> allTools = registry.GetAll();

        Assert.Contains(allTools, t => t.Name == TooledExtension.ToolName);
    }

    /// <summary>
    /// A host built with an extension via <see cref="DmonHostBuilder.AddExtension"/> emits
    /// <c>agentReady</c>, confirming the builder extension-registration path does not break
    /// the core protocol.
    /// </summary>
    [Fact]
    public async Task RunAsync_ExtensionRegisteredViaBuilder_AgentReadyStillEmitted()
    {
        Pipe outPipe = new();
        await using StreamWriter stdout = new(outPipe.Writer.AsStream(), leaveOpen: true);
        using TextReader stdin = new StringReader(string.Empty);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        DmonBuiltHost host = DmonHost.CreateBuilder([])
            .WithStdio(stdin, stdout)
            .WithoutTelemetry()
            .AddExtension<TooledExtension>()
            .Build();

        Task runTask = host.RunAsync(cts.Token);

        string? agentReadyLine = await ReadUntilAgentReadyAsync(outPipe.Reader, cts);

        await cts.CancelAsync();
        await runTask;
        await outPipe.Writer.CompleteAsync();

        Assert.True(
            agentReadyLine is not null,
            "agentReady was not emitted when an extension was registered via the builder.");
    }

    /// <summary>
    /// <see cref="DmonHostBuilder.WithModel"/> writes the canonical scalar key so that
    /// <see cref="IActiveModelStore.Load"/> returns the overridden value.
    /// </summary>
    [Fact]
    public void Build_WithModel_ActiveModelStoreReturnsOverride()
    {
        using TextReader stdin = new StringReader(string.Empty);
        using StreamWriter stdout = new(Stream.Null);

        DmonBuiltHost host = DmonHost.CreateBuilder([])
            .WithStdio(stdin, stdout)
            .WithoutTelemetry()
            .WithModel("anthropic", "claude-3-5-sonnet")
            .Build();

        IActiveModelStore store = host.Services.GetRequiredService<IActiveModelStore>();
        ModelRef? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("anthropic", loaded.Provider);
        Assert.Equal("claude-3-5-sonnet", loaded.Model);
    }

    /// <summary>
    /// <see cref="DmonHostBuilder.WithProfile"/> causes the resolver to return the
    /// named profile when no per-session profile is requested.
    /// The built-in "coding" profile is used here because it requires no config files.
    /// </summary>
    [Fact]
    public async Task Build_WithProfile_ResolverReturnsOverriddenProfile()
    {
        using TextReader stdin = new StringReader(string.Empty);
        using StreamWriter stdout = new(Stream.Null);

        DmonBuiltHost host = DmonHost.CreateBuilder([])
            .WithStdio(stdin, stdout)
            .WithoutTelemetry()
            .WithProfile("coding")
            .Build();

        IAgentProfileResolver resolver = host.Services.GetRequiredService<IAgentProfileResolver>();

        // Pass null as the per-session requestedProfile — the builder override should supply it.
        AgentProfile profile = await resolver.ResolveAsync(null, CancellationToken.None);

        Assert.Equal("coding", profile.Name, StringComparer.OrdinalIgnoreCase);
    }

    // Reads lines from the pipe until agentReady is found or the CTS fires.
    // Returns the agentReady line, or null if the CTS expired first.
    private static async Task<string?> ReadUntilAgentReadyAsync(
        PipeReader pipeReader,
        CancellationTokenSource cts)
    {
        using StreamReader reader = new(pipeReader.AsStream(leaveOpen: true));
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            {
                if (line.Contains("\"agentReady\"", StringComparison.Ordinal))
                {
                    return line;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // CTS fired before agentReady — return null (assertion will fail with useful message).
        }

        return null;
    }
}

/// <summary>
/// An <see cref="IDmonExtension"/> with one real <see cref="AIFunction"/>, used to verify
/// that builder-registered extensions land in <see cref="IToolRegistry"/>.
/// </summary>
file sealed class TooledExtension : IDmonExtension
{
    internal const string ToolName = "stub_ping";

    public string Name => "stub";
    public string Description => "Extension with one tool for registry wiring tests.";
    public IEnumerable<AIFunction> Tools =>
        [AIFunctionFactory.Create(() => "pong", ToolName, "Returns pong.")];
}

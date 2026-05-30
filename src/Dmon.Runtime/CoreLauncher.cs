using System.Text.Json;
using Dmon.Protocol;
using Dmon.Protocol.Events;

namespace Dmon.Runtime;

/// <summary>
/// Resolves, starts, and protocol-gates dmoncore.
/// This is the single entry point both host surfaces use for core bootstrap.
/// </summary>
public sealed class CoreLauncher
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly CoreResolver _resolver;

    /// <summary>
    /// Creates a <see cref="CoreLauncher"/> backed by the live NuGet.Protocol source.
    /// </summary>
    public CoreLauncher()
        : this(new CoreResolver(new NuGetCoreAcquisitionSource())) { }

    /// <summary>
    /// Creates a <see cref="CoreLauncher"/> with an injected resolver (for testing).
    /// </summary>
    internal CoreLauncher(CoreResolver resolver)
    {
        _resolver = resolver;
    }

    /// <summary>
    /// Resolves the core, starts it, reads its <c>agentReady</c> event,
    /// and verifies the protocol version.
    /// </summary>
    /// <param name="corePathOverride">
    /// Value of the <c>--core-path</c> CLI argument, or <see langword="null"/>.
    /// </param>
    /// <param name="workingDirectory">Optional working directory for the core process.</param>
    /// <param name="onStderrLine">Optional callback for stderr lines (e.g. for tests).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="CoreSession"/> whose <c>Process.StandardOutput</c> is positioned immediately
    /// after the consumed <c>agentReady</c> line, ready for the host's RPC event loop.
    /// </returns>
    /// <exception cref="CoreAcquisitionException">
    /// The core could not be resolved or downloaded.
    /// </exception>
    /// <exception cref="ProtocolMismatchException">
    /// The core's reported protocol version does not match the host's.
    /// </exception>
    public async Task<CoreSession> StartProtocolCompatibleCoreAsync(
        string? corePathOverride = null,
        string? workingDirectory = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
    {
        ResolvedCore resolved = await _resolver
            .ResolveAsync(corePathOverride, cancellationToken)
            .ConfigureAwait(false);

        CoreProcessManager process = new(resolved, workingDirectory, onStderrLine);
        await process.StartAsync().ConfigureAwait(false);

        AgentReadyEvent ready;
        try
        {
            ready = await ReadAgentReadyAsync(process.StandardOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await process.StopAsync().ConfigureAwait(false);
            process.Dispose();
            throw;
        }

        string? coreMajorMinor = ProtocolVersion.MajorMinor(ready.ProtocolVersion);
        string? hostMajorMinor = ProtocolVersion.MajorMinor(ProtocolVersion.Current);

        if (coreMajorMinor != hostMajorMinor)
        {
            await process.StopAsync().ConfigureAwait(false);
            process.Dispose();
            throw new ProtocolMismatchException(ready.ProtocolVersion, ProtocolVersion.Current);
        }

        return new CoreSession(process, ready);
    }

    /// <summary>
    /// Stops the old core, starts a fresh one on the same
    /// <see cref="CoreProcessManager"/> instance, and re-runs the protocol gate.
    /// The returned <see cref="CoreSession"/> wraps the same process manager as
    /// <paramref name="current"/>; do NOT dispose <paramref name="current"/> —
    /// doing so would tear down the live process that the new session depends on.
    /// </summary>
    public async Task<CoreSession> RestartAsync(
        CoreSession current,
        CancellationToken cancellationToken = default)
    {
        // RestartAsync on the existing manager: stops the old process, starts a new one.
        await current.Process.RestartAsync().ConfigureAwait(false);

        AgentReadyEvent ready;
        try
        {
            ready = await ReadAgentReadyAsync(current.Process.StandardOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await current.Process.StopAsync().ConfigureAwait(false);
            current.Process.Dispose();
            throw;
        }

        string? coreMajorMinor = ProtocolVersion.MajorMinor(ready.ProtocolVersion);
        string? hostMajorMinor = ProtocolVersion.MajorMinor(ProtocolVersion.Current);

        if (coreMajorMinor != hostMajorMinor)
        {
            await current.Process.StopAsync().ConfigureAwait(false);
            current.Process.Dispose();
            throw new ProtocolMismatchException(ready.ProtocolVersion, ProtocolVersion.Current);
        }

        return new CoreSession(current.Process, ready);
    }

    internal static async Task<AgentReadyEvent> ReadAgentReadyAsync(
        TextReader stdout,
        CancellationToken cancellationToken)
    {
        // Read lines until we see agentReady; skip any non-JSON lines (e.g. dotnet startup noise).
        while (true)
        {
            string? line = await stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
                throw new InvalidOperationException(
                    "Core process closed stdout without emitting agentReady.");

            if (string.IsNullOrWhiteSpace(line))
                continue;

            Event? evt;
            try
            {
                evt = JsonSerializer.Deserialize<Event>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // Skip non-JSON lines (e.g. from dotnet runtime startup).
                continue;
            }

            if (evt is AgentReadyEvent ready)
                return ready;

            // Any other event before agentReady is unexpected but harmless to skip.
        }
    }
}

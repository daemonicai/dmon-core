using Dmon.Protocol;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Runtime;

/// <summary>
/// Framing seam between a host surface and the dmoncore process.
///
/// Framing contract (ADR-003):
///   - Commands are serialized as the polymorphic base type <see cref="Command"/> using
///     <see cref="WireSerializerOptions.Default"/> so that the <c>"type"</c> discriminator
///     is always emitted.
///   - Each frame is exactly one JSON object followed by a single U+000A LINE FEED (<c>\n</c>).
///     Carriage return (<c>\r</c>) is never emitted.
///   - Events are deserialized as the polymorphic base type <see cref="Event"/> using the
///     same <see cref="WireSerializerOptions.Default"/> options.
/// </summary>
public interface IRpcTransport
{
    /// <summary>
    /// Sends a command to the core process.
    /// Serializes <paramref name="command"/> as the base <see cref="Command"/> type with
    /// <see cref="WireSerializerOptions.Default"/>, writes <c>json + "\n"</c>, and flushes.
    /// </summary>
    Task SendAsync(Command command, CancellationToken cancellationToken);

    /// <summary>
    /// An async stream of events received from the core process.
    /// Each line from the core's standard output is deserialized as the polymorphic base
    /// <see cref="Event"/> type with <see cref="WireSerializerOptions.Default"/>.
    /// Blank or whitespace-only lines are silently skipped.
    /// Lines that fail to deserialize emit a parse diagnostic via the configured handler
    /// and are skipped — the stream does not fault.
    /// The stream completes normally when the core's standard output is closed.
    /// </summary>
    IAsyncEnumerable<Event> Events { get; }
}

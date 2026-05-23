using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dmon.Core.Telemetry;

namespace Dmon.Core.Session;

public interface IMessageAppender
{
    /// <summary>
    /// Appends <paramref name="message"/> as a single LF-terminated JSON line to the session's messages.jsonl.
    /// Not safe for concurrent calls to the same session — callers must serialise writes externally.
    /// </summary>
    Task AppendAsync(string sessionId, object message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a <see cref="CompactionMessage"/> to the session's messages.jsonl.
    /// Not safe for concurrent calls to the same session — callers must serialise writes externally.
    /// </summary>
    Task AppendCompactionAsync(string sessionId, CompactionMessage compaction, CancellationToken cancellationToken = default);
}

public sealed class MessageAppender : IMessageAppender
{
    private readonly ISessionDirectoryResolver _resolver;

    public MessageAppender(ISessionDirectoryResolver resolver)
    {
        _resolver = resolver;
    }

    public Task AppendAsync(string sessionId, object message, CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(message);
        return WriteLineAsync(sessionId, json, cancellationToken);
    }

    public async Task AppendCompactionAsync(string sessionId, CompactionMessage compaction, CancellationToken cancellationToken = default)
    {
        using Activity? activity = DmonTelemetry.Source.StartActivity("session.compact");

        string json = JsonSerializer.Serialize(compaction);
        await WriteLineAsync(sessionId, json, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string sessionId, string json, CancellationToken cancellationToken)
    {
        string root = _resolver.Resolve(Environment.CurrentDirectory);
        string messagesPath = System.IO.Path.Combine(root, sessionId, "messages.jsonl");

        await using FileStream stream = new(
            messagesPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        byte[] line = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
    }
}

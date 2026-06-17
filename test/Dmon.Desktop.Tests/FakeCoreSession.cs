using System.Collections.Generic;
using System.Reactive.Subjects;
using Dmon.Desktop;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Test double for <see cref="ICoreSession"/>. Driven by a <see cref="Subject{T}"/> so tests
/// can push arbitrary events and assert which commands were sent.
/// </summary>
internal sealed class FakeCoreSession : ICoreSession
{
    private readonly Subject<Event> _subject = new();

    public IObservable<Event> Events => _subject;

    public List<Command> SentCommands { get; } = [];

    public bool ReloadCalled { get; private set; }

    public Task SendAsync(Command command, CancellationToken cancellationToken = default)
    {
        SentCommands.Add(command);
        return Task.CompletedTask;
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        ReloadCalled = true;
        return Task.CompletedTask;
    }

    /// <summary>Push an event into the stream (before scheduler advance).</summary>
    public void Push(Event evt) => _subject.OnNext(evt);

    public void Complete() => _subject.OnCompleted();
}

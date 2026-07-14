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

    /// <summary>
    /// Optional per-command fault selector. When it returns a non-null exception for a given
    /// command, <see cref="SendAsync"/> returns a faulted task and does NOT record the command —
    /// letting one handler's send fail while another succeeds (proves handler independence).
    /// </summary>
    public Func<Command, Exception?>? SendFault { get; set; }

    public Task SendAsync(Command command, CancellationToken cancellationToken = default)
    {
        Exception? fault = SendFault?.Invoke(command);
        if (fault is not null)
        {
            return Task.FromException(fault);
        }

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

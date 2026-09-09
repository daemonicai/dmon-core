using Dmon.Desktop;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;
using Microsoft.Reactive.Testing;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Tasks 7.1/7.2 (lazy-session-creation) — <see cref="SessionViewModel"/> must track a session
/// the core creates on its own initiative (<see cref="SessionStartedEvent"/>), so that a
/// subsequent <see cref="SessionViewModel.Reload"/> can re-open it (desktop-host spec,
/// "Implicitly created session is re-opened on reload").
///
/// <see cref="SessionViewModel.TrackActiveSession"/> is a C# <c>switch</c> STATEMENT (not an
/// expression) over concrete event types, with no <c>default</c> arm. This test drives the real
/// handler (via <see cref="FakeCoreSession"/>, exactly as production wires
/// <c>session.Events.Subscribe</c>) rather than merely constructing the event, and proves two
/// things: (1) pushing a <see cref="SessionStartedEvent"/> does not throw — an unhandled
/// exception from an <c>IObserver.OnNext</c> would propagate synchronously out of
/// <see cref="FakeCoreSession.Push"/>, so an untended throw shows up as this test failing, not
/// passing; (2) it DOES become the tracked active session — proven end-to-end by driving the
/// real <see cref="SessionViewModel.Reload"/> command and asserting that the
/// <see cref="SessionLoadCommand"/> it sends to the core carries that session's id, i.e. reload
/// actually re-opens the implicitly created session rather than creating a second one.
/// </summary>
public sealed class SessionStartedEventToleranceTests : IClassFixture<ReactiveUiTestFixture>
{
    [Fact]
    public async Task SessionStartedEvent_DoesNotThrow_AndBecomesTheActiveSessionReopenedOnReload()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        // The core creates a session on its own initiative and announces it. If
        // TrackActiveSession's switch ever threw on it, this Push call would throw
        // synchronously and fail the test right here.
        session.Push(new SessionStartedEvent
        {
            Session = new SessionMeta
            {
                Id       = "session-implicit",
                Created  = DateTimeOffset.UtcNow,
                Modified = DateTimeOffset.UtcNow,
            },
        });
        scheduler.AdvanceBy(1);

        sut.Reload.Execute().Subscribe();
        scheduler.AdvanceBy(1);

        // Allow the async Reload body to complete — same idiom as the other Reload tests in
        // Group6Tests, since outputScheduler is a TestScheduler and the CreateFromTask body
        // runs on the real thread pool.
        await Task.Delay(50);

        Assert.True(session.ReloadCalled);

        // Reload must re-open the implicitly created session — proving it was tracked as
        // active — rather than leaving no active session to re-open.
        SessionLoadCommand? loadCmd = session.SentCommands
            .OfType<SessionLoadCommand>()
            .FirstOrDefault();
        Assert.NotNull(loadCmd);
        Assert.Equal("session-implicit", loadCmd.Path);
    }

    /// <summary>
    /// <see cref="SessionViewModel.TrackActiveSession"/>'s switch has no <c>default</c> arm, so
    /// any event type outside its five handled cases must fall through silently rather than
    /// throw. <see cref="SessionUpdatedEvent"/> is a fair stand-in: it is a real,
    /// session-lifecycle-shaped event that arrives on this same <c>session.Events</c>
    /// subscription (a title-rename notification), yet — unlike
    /// <see cref="SessionStartedEvent"/> since task 7.1 — carries no <c>case</c> arm at all. This
    /// proves the switch's fall-through safety for genuinely unhandled event types is still
    /// covered, distinct from the handled-case coverage in the test above.
    /// </summary>
    [Fact]
    public async Task UnrecognisedEvent_DoesNotThrow_AndDoesNotDisturbActiveSession()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        // A recognised lifecycle event establishes the active session first.
        session.Push(new SessionCreatedResultEvent
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Session   = new SessionMeta
            {
                Id       = "session-known",
                Created  = DateTimeOffset.UtcNow,
                Modified = DateTimeOffset.UtcNow,
            },
        });
        scheduler.AdvanceBy(1);

        // A genuinely unrecognised event arrives next. If TrackActiveSession's switch ever
        // threw on it, this Push call would throw synchronously and fail the test right here.
        session.Push(new SessionUpdatedEvent
        {
            SessionId = "session-known",
            Title     = "Renamed mid-flight",
        });
        scheduler.AdvanceBy(1);

        sut.Reload.Execute().Subscribe();
        scheduler.AdvanceBy(1);

        await Task.Delay(50);

        Assert.True(session.ReloadCalled);

        // The tracked active session is still the one from the recognised event — the
        // unrecognised SessionUpdatedEvent did not overwrite or clear it.
        SessionLoadCommand? loadCmd = session.SentCommands
            .OfType<SessionLoadCommand>()
            .FirstOrDefault();
        Assert.NotNull(loadCmd);
        Assert.Equal("session-known", loadCmd.Path);
    }
}

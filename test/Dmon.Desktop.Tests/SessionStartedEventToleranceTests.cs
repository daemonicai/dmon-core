using Dmon.Desktop;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;
using Microsoft.Reactive.Testing;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Task 5.2 (lazy-session-creation) — <see cref="SessionViewModel"/> must be unaffected by
/// the additive <see cref="SessionStartedEvent"/>.
///
/// <see cref="SessionViewModel.TrackActiveSession"/> is a C# <c>switch</c> STATEMENT (not an
/// expression) over concrete event types, with no <c>case SessionStartedEvent</c> arm and no
/// <c>default</c> arm. A non-matching switch statement falls through silently — it does not
/// throw — so a <see cref="SessionStartedEvent"/> pushed through <c>ICoreSession.Events</c> is
/// dropped without special-casing. This test drives that real handler (via
/// <see cref="FakeCoreSession"/>, exactly as production wires <c>session.Events.Subscribe</c>)
/// rather than merely constructing the event, and proves two things: (1) pushing it does not
/// throw — an unhandled exception from an <c>IObserver.OnNext</c> would propagate synchronously
/// out of <see cref="FakeCoreSession.Push"/>, so an untended throw shows up as this test failing,
/// not passing; (2) it does not mutate the tracked active-session state — proven indirectly
/// through <see cref="SessionViewModel.Reload"/>, the only observable consumer of that private
/// state, which must still resolve to the session set by the last recognised lifecycle event.
/// </summary>
public sealed class SessionStartedEventToleranceTests : IClassFixture<ReactiveUiTestFixture>
{
    [Fact]
    public async Task SessionStartedEvent_IsIgnored_DoesNotThrow_DoesNotDisturbActiveSession()
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

        // The additive, unrecognised event arrives next. If TrackActiveSession's switch ever
        // threw on it, this Push call would throw synchronously and fail the test right here.
        session.Push(new SessionStartedEvent
        {
            Session = new SessionMeta
            {
                Id       = "session-mid-flight-unrelated",
                Created  = DateTimeOffset.UtcNow,
                Modified = DateTimeOffset.UtcNow,
            },
        });
        scheduler.AdvanceBy(1);

        sut.Reload.Execute().Subscribe();
        scheduler.AdvanceBy(1);

        await Task.Delay(50);

        Assert.True(session.ReloadCalled);

        // The tracked active session is still the one from the recognised event — the
        // unrecognised SessionStartedEvent did not overwrite or clear it.
        SessionLoadCommand? loadCmd = session.SentCommands
            .OfType<SessionLoadCommand>()
            .FirstOrDefault();
        Assert.NotNull(loadCmd);
        Assert.Equal("session-known", loadCmd.Path);
    }
}

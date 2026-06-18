using System.Text.Json;
using Dmon.Desktop;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Microsoft.Reactive.Testing;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Group 6 — input, commands, interactions, and reload guard.
///
/// 6.2: Tool-confirm interaction — VM raises it and maps the result to the correct
///      <see cref="ToolConfirmResponseCommand"/> (including Deny → Confirmed=false, Scope=null).
///
/// 6.3: UI-input interaction — VM raises it and maps the result to
///      <see cref="UiInputResponseCommand"/>.
///
/// 6.4: Reload between-turns guard — Reload.CanExecute is false while streaming and
///      true when idle; executing calls ReloadAsync and re-sends SessionLoadCommand when
///      an active session id has been tracked.
/// </summary>
public sealed class Group6Tests : IClassFixture<ReactiveUiTestFixture>
{
    // =========================================================================
    // 6.2 — ToolConfirm interaction
    // =========================================================================

    [Fact]
    public async Task ToolConfirm_AllowProject_SendsCorrectCommand()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        // Register a handler that returns AllowProject.
        sut.ToolConfirmInteraction.RegisterHandler(context =>
            context.SetOutput(new ToolConfirmResult(ToolConfirmChoice.AllowProject)));

        // Push a ToolConfirmRequestEvent.
        ToolConfirmRequestEvent evt = new()
        {
            ConfirmId = "confirm-abc",
            Name      = "bash",
            Args      = MakeJsonElement(new { cmd = "rm -rf /tmp/x" }),
            Risk      = RiskLevel.High
        };
        session.Push(evt);

        // Allow async void handler to complete.
        await Task.Delay(50);

        // Assert: one ToolConfirmResponseCommand sent with correct mapping.
        Assert.Single(session.SentCommands);
        ToolConfirmResponseCommand response = Assert.IsType<ToolConfirmResponseCommand>(session.SentCommands[0]);
        Assert.Equal("confirm-abc", response.Id);
        Assert.True(response.Confirmed);
        Assert.False(response.Cancelled);
        Assert.Equal("project", response.Scope);
    }

    [Fact]
    public async Task ToolConfirm_Deny_SendsConfirmedFalseAndNullScope()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        sut.ToolConfirmInteraction.RegisterHandler(context =>
            context.SetOutput(new ToolConfirmResult(ToolConfirmChoice.Deny)));

        ToolConfirmRequestEvent evt = new()
        {
            ConfirmId = "confirm-xyz",
            Name      = "write_file",
            Args      = MakeJsonElement(new { path = "/etc/hosts" }),
            Risk      = RiskLevel.Medium
        };
        session.Push(evt);

        await Task.Delay(50);

        Assert.Single(session.SentCommands);
        ToolConfirmResponseCommand response = Assert.IsType<ToolConfirmResponseCommand>(session.SentCommands[0]);
        Assert.Equal("confirm-xyz", response.Id);
        Assert.False(response.Confirmed);
        Assert.False(response.Cancelled);
        Assert.Null(response.Scope);
    }

    [Fact]
    public async Task ToolConfirm_AllowOnce_SendsScopeOnce()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        sut.ToolConfirmInteraction.RegisterHandler(context =>
            context.SetOutput(new ToolConfirmResult(ToolConfirmChoice.AllowOnce)));

        session.Push(new ToolConfirmRequestEvent
        {
            ConfirmId = "confirm-once",
            Name      = "read_file",
            Args      = MakeJsonElement(new { path = "/tmp/foo.txt" }),
            Risk      = RiskLevel.Low
        });

        await Task.Delay(50);

        ToolConfirmResponseCommand response = Assert.IsType<ToolConfirmResponseCommand>(session.SentCommands[0]);
        Assert.True(response.Confirmed);
        Assert.Equal("once", response.Scope);
    }

    [Fact]
    public async Task ToolConfirm_AllowGlobal_SendsScopeGlobal()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        sut.ToolConfirmInteraction.RegisterHandler(context =>
            context.SetOutput(new ToolConfirmResult(ToolConfirmChoice.AllowGlobal)));

        session.Push(new ToolConfirmRequestEvent
        {
            ConfirmId = "confirm-global",
            Name      = "read_file",
            Args      = MakeJsonElement(new { path = "/tmp/foo.txt" }),
            Risk      = RiskLevel.None
        });

        await Task.Delay(50);

        ToolConfirmResponseCommand response = Assert.IsType<ToolConfirmResponseCommand>(session.SentCommands[0]);
        Assert.True(response.Confirmed);
        Assert.Equal("global", response.Scope);
    }

    // =========================================================================
    // 6.3 — UiInput interaction
    // =========================================================================

    [Fact]
    public async Task UiInput_Submitted_SendsValueAndNotCancelled()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        sut.UiInputInteraction.RegisterHandler(context =>
            context.SetOutput(new UiInputResult("my-api-key", Cancelled: false)));

        UiInputRequestEvent evt = new()
        {
            EventId = "input-001",
            Kind    = UiInputKind.Secret,
            Prompt  = "Enter API key:"
        };
        session.Push(evt);

        await Task.Delay(50);

        Assert.Single(session.SentCommands);
        UiInputResponseCommand response = Assert.IsType<UiInputResponseCommand>(session.SentCommands[0]);
        Assert.Equal("input-001", response.Id);
        Assert.Equal("my-api-key", response.Value);
        Assert.False(response.Cancelled);
    }

    [Fact]
    public async Task UiInput_Cancelled_SendsCancelledTrue()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        sut.UiInputInteraction.RegisterHandler(context =>
            context.SetOutput(new UiInputResult(null, Cancelled: true)));

        UiInputRequestEvent evt = new()
        {
            EventId = "input-002",
            Kind    = UiInputKind.Text,
            Prompt  = "Enter name:"
        };
        session.Push(evt);

        await Task.Delay(50);

        UiInputResponseCommand response = Assert.IsType<UiInputResponseCommand>(session.SentCommands[0]);
        Assert.Equal("input-002", response.Id);
        Assert.Null(response.Value);
        Assert.True(response.Cancelled);
    }

    // =========================================================================
    // 6.4 — Reload between-turns guard
    // =========================================================================

    [Fact]
    public void Reload_CanExecute_FalseWhileStreaming()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        scheduler.AdvanceBy(1);

        bool? canBeforeStream = null;
        sut.Reload.CanExecute.Subscribe(v => canBeforeStream = v);
        scheduler.AdvanceBy(1);
        Assert.True(canBeforeStream);

        // Start streaming.
        session.Push(new TurnStartEvent());
        scheduler.AdvanceBy(1);

        bool? canDuringStream = null;
        sut.Reload.CanExecute.Subscribe(v => canDuringStream = v);
        scheduler.AdvanceBy(1);
        Assert.False(canDuringStream);
    }

    [Fact]
    public void Reload_CanExecute_TrueAfterTurnEnd()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        // Enter streaming state.
        session.Push(new TurnStartEvent());
        scheduler.AdvanceBy(1);

        // End streaming.
        session.Push(new TurnEndEvent { Message = new object(), ToolResults = [] });
        scheduler.AdvanceBy(1);

        bool? canAfterStream = null;
        sut.Reload.CanExecute.Subscribe(v => canAfterStream = v);
        scheduler.AdvanceBy(1);
        Assert.True(canAfterStream);
    }

    [Fact]
    public async Task Reload_Execute_CallsReloadAsync()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        scheduler.AdvanceBy(1);

        sut.Reload.Execute().Subscribe();
        scheduler.AdvanceBy(1);

        // Allow async body to complete.
        await Task.Delay(50);

        Assert.True(session.ReloadCalled);
    }

    [Fact]
    public async Task Reload_WithActiveSession_SendsSessionLoadCommand()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        // Simulate a session having been created — feed a SessionCreatedResultEvent.
        session.Push(new SessionCreatedResultEvent
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Session   = new Dmon.Protocol.Sessions.SessionMeta
            {
                Id       = "session-dir-path",
                Created  = DateTimeOffset.UtcNow,
                Modified = DateTimeOffset.UtcNow
            }
        });

        scheduler.AdvanceBy(1);

        sut.Reload.Execute().Subscribe();
        scheduler.AdvanceBy(1);

        await Task.Delay(50);

        Assert.True(session.ReloadCalled);

        // Assert a SessionLoadCommand was sent with the tracked path.
        SessionLoadCommand? loadCmd = session.SentCommands
            .OfType<SessionLoadCommand>()
            .FirstOrDefault();
        Assert.NotNull(loadCmd);
        Assert.Equal("session-dir-path", loadCmd.Path);
    }

    [Fact]
    public void Reload_WhileStreaming_CanExecuteIsFalse()
    {
        // This test proves the guard by observing CanExecute directly, which is the
        // authoritative contract — ReactiveCommand simply does not execute when CanExecute
        // is false, so asserting CanExecute=false is the definitive guard proof.
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        // Drain initial state.
        scheduler.AdvanceBy(1);

        // Verify idle allows reload.
        bool? canWhenIdle = null;
        sut.Reload.CanExecute.Subscribe(v => canWhenIdle = v);
        scheduler.AdvanceBy(1);
        Assert.True(canWhenIdle, "Reload should be allowed when idle");

        // Start streaming.
        session.Push(new TurnStartEvent());
        scheduler.AdvanceBy(1);

        // CanExecute must now be false.
        bool? canWhenStreaming = null;
        sut.Reload.CanExecute.Subscribe(v => canWhenStreaming = v);
        scheduler.AdvanceBy(1);
        Assert.False(canWhenStreaming, "Reload must be blocked while streaming");
    }

    // =========================================================================
    // Helper
    // =========================================================================

    private static object MakeJsonElement(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }
}

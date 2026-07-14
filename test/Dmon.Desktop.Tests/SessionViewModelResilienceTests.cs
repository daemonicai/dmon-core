using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dmon.Desktop;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Microsoft.Reactive.Testing;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Task 4.3 — resilience of the async-void interaction handlers.
///
/// A failure at any step of <c>HandleToolConfirmAsync</c> or <c>HandleUiInputAsync</c>
/// (raising/awaiting the ReactiveUI <see cref="ReactiveUI.Interaction{TInput, TOutput}"/>,
/// or <c>SendAsync</c> against a dead core) MUST be caught, surfaced on
/// <see cref="SessionViewModel.HandlerErrors"/>, and MUST NOT crash the process or
/// suppress the sibling handler.
/// </summary>
public sealed class SessionViewModelResilienceTests : IClassFixture<ReactiveUiTestFixture>
{
    // Scenario 1: dead-core send failure on ToolConfirm is contained; the sibling
    // UiInput handler still sends its response (proves independence + survival).
    [Fact]
    public async Task DeadCoreSend_OnToolConfirm_IsContained_UiInputStillSends()
    {
        FakeCoreSession session = new()
        {
            SendFault = cmd => cmd is ToolConfirmResponseCommand ? new IOException("core dead") : null
        };
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        List<SessionViewModel.HandlerError> errors = [];
        sut.HandlerErrors.Subscribe(errors.Add);

        sut.ToolConfirmInteraction.RegisterHandler(ctx =>
            ctx.SetOutput(new ToolConfirmResult(ToolConfirmChoice.AllowOnce)));
        sut.UiInputInteraction.RegisterHandler(ctx =>
            ctx.SetOutput(new UiInputResult("value", Cancelled: false)));

        session.Push(new ToolConfirmRequestEvent
        {
            ConfirmId = "confirm-1",
            Name      = "bash",
            Args      = JsonSerializer.SerializeToElement(new { cmd = "ls" }),
            Risk      = RiskLevel.High
        });
        session.Push(new UiInputRequestEvent
        {
            EventId = "input-1",
            Kind    = UiInputKind.Text,
            Prompt  = "Enter name:"
        });

        await Task.Delay(50);

        // Sibling handler survived: its response was sent even though ToolConfirm's send faulted.
        Assert.Single(session.SentCommands);
        Assert.IsType<UiInputResponseCommand>(session.SentCommands[0]);

        // The ToolConfirm failure was surfaced, not thrown.
        SessionViewModel.HandlerError error = Assert.Single(errors);
        Assert.Equal("ToolConfirm", error.Handler);
        Assert.IsType<IOException>(error.Exception);
    }

    // Scenario 2: the ToolConfirm interaction handler itself throws; it is contained and
    // the UiInput handler is unaffected.
    [Fact]
    public async Task InteractionThrows_OnToolConfirm_IsContained_UiInputUnaffected()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        List<SessionViewModel.HandlerError> errors = [];
        sut.HandlerErrors.Subscribe(errors.Add);

        sut.ToolConfirmInteraction.RegisterHandler(ctx =>
            throw new InvalidOperationException("no handler available"));
        sut.UiInputInteraction.RegisterHandler(ctx =>
            ctx.SetOutput(new UiInputResult("value", Cancelled: false)));

        session.Push(new ToolConfirmRequestEvent
        {
            ConfirmId = "confirm-2",
            Name      = "write_file",
            Args      = JsonSerializer.SerializeToElement(new { path = "/etc/hosts" }),
            Risk      = RiskLevel.Medium
        });
        session.Push(new UiInputRequestEvent
        {
            EventId = "input-2",
            Kind    = UiInputKind.Text,
            Prompt  = "Enter name:"
        });

        await Task.Delay(50);

        // UiInput handler unaffected by the ToolConfirm interaction failure.
        Assert.Single(session.SentCommands);
        Assert.IsType<UiInputResponseCommand>(session.SentCommands[0]);

        SessionViewModel.HandlerError error = Assert.Single(errors);
        Assert.Equal("ToolConfirm", error.Handler);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    // Symmetric case: a dead-core send failure on UiInput is contained; ToolConfirm still sends.
    [Fact]
    public async Task DeadCoreSend_OnUiInput_IsContained_ToolConfirmStillSends()
    {
        FakeCoreSession session = new()
        {
            SendFault = cmd => cmd is UiInputResponseCommand ? new IOException("core dead") : null
        };
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);

        List<SessionViewModel.HandlerError> errors = [];
        sut.HandlerErrors.Subscribe(errors.Add);

        sut.ToolConfirmInteraction.RegisterHandler(ctx =>
            ctx.SetOutput(new ToolConfirmResult(ToolConfirmChoice.AllowOnce)));
        sut.UiInputInteraction.RegisterHandler(ctx =>
            ctx.SetOutput(new UiInputResult("value", Cancelled: false)));

        session.Push(new UiInputRequestEvent
        {
            EventId = "input-3",
            Kind    = UiInputKind.Text,
            Prompt  = "Enter name:"
        });
        session.Push(new ToolConfirmRequestEvent
        {
            ConfirmId = "confirm-3",
            Name      = "bash",
            Args      = JsonSerializer.SerializeToElement(new { cmd = "ls" }),
            Risk      = RiskLevel.Low
        });

        await Task.Delay(50);

        Assert.Single(session.SentCommands);
        Assert.IsType<ToolConfirmResponseCommand>(session.SentCommands[0]);

        SessionViewModel.HandlerError error = Assert.Single(errors);
        Assert.Equal("UiInput", error.Handler);
        Assert.IsType<IOException>(error.Exception);
    }
}

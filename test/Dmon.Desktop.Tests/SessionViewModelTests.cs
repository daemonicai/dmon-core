using System.Reactive.Subjects;
using Dmon.Desktop;
using Dmon.Protocol.Events;
using Microsoft.Reactive.Testing;
using ReactiveUI;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Verifies <see cref="SessionViewModel"/> routing initialisation. Tests run without
/// Avalonia platform init — construction is pure ReactiveObject/RoutingState mechanics.
///
/// Updated for Group 6: <see cref="SessionViewModel"/> now takes an <see cref="ICoreSession"/>
/// and scheduler. Tests use <see cref="FakeCoreSession"/> + <see cref="TestScheduler"/>.
/// </summary>
public sealed class SessionViewModelTests : IClassFixture<ReactiveUiTestFixture>
{
    private static (SessionViewModel Sut, FakeCoreSession Session, TestScheduler Scheduler) Build()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        SessionViewModel sut = new(session, scheduler);
        return (sut, session, scheduler);
    }

    [Fact]
    public void Constructor_NavigatesTo_ConversationViewModel()
    {
        (SessionViewModel sut, _, _) = Build();

        IRoutableViewModel? current = sut.Router.GetCurrentViewModel();

        Assert.IsType<ConversationViewModel>(current);
    }

    [Fact]
    public void ConversationViewModel_HostScreen_IsSessionViewModel()
    {
        (SessionViewModel sut, _, _) = Build();

        ConversationViewModel conversation = Assert.IsType<ConversationViewModel>(
            sut.Router.GetCurrentViewModel());

        Assert.Same(sut, conversation.HostScreen);
    }

    [Fact]
    public void ConversationViewModel_UrlPathSegment_IsConversation()
    {
        (SessionViewModel sut, _, _) = Build();

        ConversationViewModel conversation = Assert.IsType<ConversationViewModel>(
            sut.Router.GetCurrentViewModel());

        Assert.Equal("conversation", conversation.UrlPathSegment);
    }
}

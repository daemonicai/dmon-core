using System.Reactive.Subjects;
using Dmon.Protocol.Events;
using Microsoft.Reactive.Testing;
using ReactiveUI;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Verifies <see cref="SessionViewModel"/> routing initialisation. Tests run without
/// Avalonia platform init — construction is pure ReactiveObject/RoutingState mechanics.
///
/// Updated for Group 5: <see cref="SessionViewModel"/> now requires an event stream and
/// scheduler (or a <see cref="CoreSessionService"/>). Tests use the testable constructor
/// overload that accepts a <c>Subject&lt;Event&gt;</c> and <see cref="TestScheduler"/>
/// directly, keeping them hermetically isolated from any live core process.
/// </summary>
public sealed class SessionViewModelTests
{
    private static SessionViewModel Build()
    {
        Subject<Event> events = new();
        TestScheduler scheduler = new();
        return new SessionViewModel(events, scheduler);
    }

    [Fact]
    public void Constructor_NavigatesTo_ConversationViewModel()
    {
        SessionViewModel sut = Build();

        IRoutableViewModel? current = sut.Router.GetCurrentViewModel();

        Assert.IsType<ConversationViewModel>(current);
    }

    [Fact]
    public void ConversationViewModel_HostScreen_IsSessionViewModel()
    {
        SessionViewModel sut = Build();

        ConversationViewModel conversation = Assert.IsType<ConversationViewModel>(
            sut.Router.GetCurrentViewModel());

        Assert.Same(sut, conversation.HostScreen);
    }

    [Fact]
    public void ConversationViewModel_UrlPathSegment_IsConversation()
    {
        SessionViewModel sut = Build();

        ConversationViewModel conversation = Assert.IsType<ConversationViewModel>(
            sut.Router.GetCurrentViewModel());

        Assert.Equal("conversation", conversation.UrlPathSegment);
    }
}

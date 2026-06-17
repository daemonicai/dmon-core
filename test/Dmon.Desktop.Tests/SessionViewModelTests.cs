using ReactiveUI;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Verifies <see cref="SessionViewModel"/> routing initialisation. Tests run without
/// Avalonia platform init — construction is pure ReactiveObject/RoutingState mechanics.
/// </summary>
public sealed class SessionViewModelTests
{
    [Fact]
    public void Constructor_NavigatesTo_ConversationViewModel()
    {
        SessionViewModel sut = new();

        IRoutableViewModel? current = sut.Router.GetCurrentViewModel();

        Assert.IsType<ConversationViewModel>(current);
    }

    [Fact]
    public void ConversationViewModel_HostScreen_IsSessionViewModel()
    {
        SessionViewModel sut = new();

        ConversationViewModel conversation = Assert.IsType<ConversationViewModel>(
            sut.Router.GetCurrentViewModel());

        Assert.Same(sut, conversation.HostScreen);
    }

    [Fact]
    public void ConversationViewModel_UrlPathSegment_IsConversation()
    {
        SessionViewModel sut = new();

        ConversationViewModel conversation = Assert.IsType<ConversationViewModel>(
            sut.Router.GetCurrentViewModel());

        Assert.Equal("conversation", conversation.UrlPathSegment);
    }
}

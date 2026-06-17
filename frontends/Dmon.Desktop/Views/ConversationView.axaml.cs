using Avalonia;
using Avalonia.Controls;
using ReactiveUI;

namespace Dmon.Desktop.Views;

/// <summary>
/// Placeholder view for <see cref="ConversationViewModel"/>. Group 5 adds content.
/// Registered explicitly as <see cref="IViewFor{ConversationViewModel}"/> in the DI
/// container so <see cref="RoutedViewHost"/> resolves it through the Splat/MEDI bridge.
/// </summary>
public partial class ConversationView : UserControl, IViewFor<ConversationViewModel>
{
    public static readonly StyledProperty<ConversationViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ConversationView, ConversationViewModel?>(nameof(ViewModel));

    public ConversationViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    object? IViewFor.ViewModel
    {
        get => ViewModel;
        set => ViewModel = value as ConversationViewModel;
    }

    public ConversationView()
    {
        InitializeComponent();
    }
}

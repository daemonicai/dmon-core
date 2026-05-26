namespace Dmon.Abstractions.Wizard;

/// <summary>
/// A step asking the user to pick exactly one option from a list.
/// Setting <see cref="SelectedIndex"/> flips <see cref="WizardStep.IsAnswered"/>.
/// </summary>
public sealed class ChooseOneStep : WizardStep
{
    public required IReadOnlyList<WizardOption> Options { get; init; }

    private int? _selectedIndex;

    public int? SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = value;
            IsAnswered = value.HasValue;
        }
    }
}

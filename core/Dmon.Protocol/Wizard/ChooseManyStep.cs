using System.Text.Json.Serialization;

namespace Dmon.Protocol.Wizard;

/// <summary>
/// A step asking the user to select one or more options.
/// Setting <see cref="SelectedIndices"/> flips <see cref="WizardStep.IsAnswered"/>
/// once at least <see cref="MinSelections"/> items are chosen.
/// </summary>
public sealed class ChooseManyStep : WizardStep
{
    public required IReadOnlyList<WizardOption> Options { get; init; }
    public int MinSelections { get; init; }

    private IReadOnlyList<int>? _selectedIndices;

    [JsonIgnore]
    public IReadOnlyList<int>? SelectedIndices
    {
        get => _selectedIndices;
        set
        {
            _selectedIndices = value;
            IsAnswered = value is not null && value.Count >= MinSelections;
        }
    }
}

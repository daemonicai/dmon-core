namespace Dmon.Abstractions.Wizard;

/// <summary>
/// A step asking for free-form text. Setting <see cref="Value"/> to a non-null string
/// flips <see cref="WizardStep.IsAnswered"/>. Secret input is masked in the terminal.
/// </summary>
public sealed class TextInputStep : WizardStep
{
    public string? Default { get; init; }
    public bool Secret { get; init; }
    public bool Required { get; init; }

    private string? _value;

    public string? Value
    {
        get => _value;
        set
        {
            _value = value;
            IsAnswered = value is not null;
        }
    }
}

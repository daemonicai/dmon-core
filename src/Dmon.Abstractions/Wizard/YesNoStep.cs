namespace Dmon.Abstractions.Wizard;

/// <summary>
/// A yes/no confirmation step. Setting <see cref="Answer"/> flips
/// <see cref="WizardStep.IsAnswered"/>.
/// </summary>
public sealed class YesNoStep : WizardStep
{
    public bool Default { get; init; }

    private bool? _answer;

    public bool? Answer
    {
        get => _answer;
        set
        {
            _answer = value;
            IsAnswered = value.HasValue;
        }
    }
}

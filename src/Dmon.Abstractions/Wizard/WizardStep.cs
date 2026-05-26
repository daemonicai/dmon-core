namespace Dmon.Abstractions.Wizard;

/// <summary>
/// Abstract base for all wizard steps. The question portion (<see cref="Id"/>,
/// <see cref="Prompt"/>) is immutable; the answer portion is settable by each subclass
/// and flips <see cref="IsAnswered"/> to <c>true</c> when set.
/// </summary>
public abstract class WizardStep
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public bool IsAnswered { get; protected set; }
}

namespace Dmon.Abstractions.Wizard;

/// <summary>
/// Terminal marker returned by a factory when setup is complete.
/// Carries a confirmation <see cref="Message"/> for the user; has no answerable field.
/// </summary>
public sealed class WizardCompletedStep : WizardStep
{
    public required string Message { get; init; }
}

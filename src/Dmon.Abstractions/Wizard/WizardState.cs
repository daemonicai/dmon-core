namespace Dmon.Abstractions.Wizard;

/// <summary>
/// Accumulates the ordered list of answered <see cref="WizardStep"/> objects.
/// Index 0 is the adapter-selection step; subsequent entries are factory-produced.
/// </summary>
public sealed record WizardState(IReadOnlyList<WizardStep> Steps)
{
    public static readonly WizardState Empty = new([]);
}

namespace Dmon.Protocol.Wizard;

/// <summary>
/// An option presented in a selection step. <see cref="Label"/> is shown in the UI;
/// <see cref="Value"/> is stored as the answer.
/// </summary>
public sealed record WizardOption(string Label, string Value, string? Description = null);

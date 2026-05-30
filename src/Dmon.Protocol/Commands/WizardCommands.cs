using System.Text.Json.Serialization;
using Dmon.Protocol.Enums;

namespace Dmon.Protocol.Commands;

/// <summary>
/// Sent by the host to start the provider setup wizard.
/// The core responds by emitting a sequence of <c>wizard.step</c> events.
/// </summary>
public sealed record WizardStartCommand : Command;

/// <summary>
/// Sent by the host to answer (or navigate away from) the current wizard step.
/// </summary>
/// <remarks>
/// <para><see cref="Value"/> carries the answer when <see cref="Outcome"/> is
/// <see cref="WizardAnswerOutcome.Answered"/>. For single-selection steps
/// (ChooseOne, YesNo, TextInput) the value is the raw string. For multi-selection
/// steps (ChooseMany) the value is a comma-separated list of zero-based option
/// indices, e.g. <c>"0,2"</c>.</para>
/// <para>When <see cref="Outcome"/> is <see cref="WizardAnswerOutcome.Back"/> or
/// <see cref="WizardAnswerOutcome.Cancel"/>, <see cref="Value"/> is ignored.</para>
/// </remarks>
public sealed record WizardAnswerCommand : Command
{
    [JsonPropertyName("wizardId")]
    public required string WizardId { get; init; }

    [JsonPropertyName("outcome")]
    public WizardAnswerOutcome Outcome { get; init; }

    /// <summary>
    /// The answer string. Non-null only when <see cref="Outcome"/> is
    /// <see cref="WizardAnswerOutcome.Answered"/>.
    /// For ChooseMany steps, encode selected indices as comma-separated integers.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

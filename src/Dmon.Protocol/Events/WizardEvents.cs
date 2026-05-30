using System.Text.Json.Serialization;
using Dmon.Protocol.Wizard;

namespace Dmon.Protocol.Events;

/// <summary>
/// Emitted by the core to present one wizard step to the host.
/// The host reflects <see cref="WizardId"/> in the subsequent
/// <c>wizard.answer</c> command.
/// </summary>
public sealed record WizardStepEvent : Event
{
    [JsonPropertyName("wizardId")]
    public required string WizardId { get; init; }

    [JsonPropertyName("step")]
    public required WizardStep Step { get; init; }
}

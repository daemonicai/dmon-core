using System.Text.Json.Serialization;

namespace Dmon.Protocol.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WizardAnswerOutcome
{
    Answered,
    Back,
    Cancel
}

using System.Text.Json.Serialization;

namespace Dmon.Protocol.Wizard;

/// <summary>
/// Abstract base for all wizard steps. The question portion (<see cref="Id"/>,
/// <see cref="Prompt"/>) is immutable; the answer portion is settable by each subclass
/// and flips <see cref="IsAnswered"/> to <c>true</c> when set.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChooseOneStep), typeDiscriminator: "wizard.chooseOne")]
[JsonDerivedType(typeof(ChooseManyStep), typeDiscriminator: "wizard.chooseMany")]
[JsonDerivedType(typeof(TextInputStep), typeDiscriminator: "wizard.textInput")]
[JsonDerivedType(typeof(YesNoStep), typeDiscriminator: "wizard.yesNo")]
[JsonDerivedType(typeof(InfoStep), typeDiscriminator: "wizard.info")]
[JsonDerivedType(typeof(WizardCompletedStep), typeDiscriminator: "wizard.completed")]
public abstract class WizardStep
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public bool IsAnswered { get; protected set; }
}

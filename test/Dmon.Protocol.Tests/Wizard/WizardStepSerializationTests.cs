using System.Text.Json;
using System.Text.Json.Nodes;
using Dmon.Protocol.Wizard;

namespace Dmon.Protocol.Tests.Wizard;

/// <summary>
/// Verifies [JsonPolymorphic] / [JsonDerivedType] round-trip behaviour and that
/// answer fields are omitted from the serialised wire form.
/// </summary>
public sealed class WizardStepSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ //
    //  ChooseOneStep round-trip
    // ------------------------------------------------------------------ //

    [Fact]
    public void ChooseOneStep_RoundTrips_PolymorphicallyAsWizardStep()
    {
        WizardStep original = new ChooseOneStep
        {
            Id = "model",
            Prompt = "Pick a model",
            Options =
            [
                new WizardOption("Alpha", "alpha", "First model"),
                new WizardOption("Beta",  "beta",  null),
            ],
        };

        string json = JsonSerializer.Serialize(original, Options);
        WizardStep? deserialized = JsonSerializer.Deserialize<WizardStep>(json, Options);

        ChooseOneStep concrete = Assert.IsType<ChooseOneStep>(deserialized);
        Assert.Equal("model", concrete.Id);
        Assert.Equal("Pick a model", concrete.Prompt);
        Assert.Equal(2, concrete.Options.Count);
        Assert.Equal("Alpha", concrete.Options[0].Label);
        Assert.Equal("alpha", concrete.Options[0].Value);
        Assert.Equal("First model", concrete.Options[0].Description);
        Assert.Equal("Beta", concrete.Options[1].Label);
        Assert.Equal("beta", concrete.Options[1].Value);
        Assert.Null(concrete.Options[1].Description);
    }

    [Fact]
    public void ChooseOneStep_Serialized_ContainsCorrectTypeDiscriminator()
    {
        WizardStep step = new ChooseOneStep
        {
            Id = "x",
            Prompt = "p",
            Options = [new WizardOption("L", "v")],
        };

        string json = JsonSerializer.Serialize(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.chooseOne", node?["type"]?.GetValue<string>());
    }

    [Fact]
    public void ChooseOneStep_Serialized_DoesNotContainSelectedIndex()
    {
        ChooseOneStep step = new()
        {
            Id = "x",
            Prompt = "p",
            Options = [new WizardOption("L", "v")],
        };
        step.SelectedIndex = 0;

        string json = JsonSerializer.Serialize<WizardStep>(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Null(node?["selectedIndex"]);
        Assert.Null(node?["SelectedIndex"]);
    }

    // ------------------------------------------------------------------ //
    //  TextInputStep round-trip and answer-field omission
    // ------------------------------------------------------------------ //

    [Fact]
    public void TextInputStep_RoundTrips_PolymorphicallyAsWizardStep()
    {
        WizardStep original = new TextInputStep
        {
            Id = "api-key",
            Prompt = "Enter API key",
            Secret = true,
            Required = true,
            Default = null,
        };

        string json = JsonSerializer.Serialize(original, Options);
        WizardStep? deserialized = JsonSerializer.Deserialize<WizardStep>(json, Options);

        TextInputStep concrete = Assert.IsType<TextInputStep>(deserialized);
        Assert.Equal("api-key", concrete.Id);
        Assert.Equal("Enter API key", concrete.Prompt);
        Assert.True(concrete.Secret);
        Assert.True(concrete.Required);
        Assert.Null(concrete.Default);
    }

    [Fact]
    public void TextInputStep_Serialized_ContainsCorrectTypeDiscriminator()
    {
        WizardStep step = new TextInputStep { Id = "x", Prompt = "p" };

        string json = JsonSerializer.Serialize(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.textInput", node?["type"]?.GetValue<string>());
    }

    [Fact]
    public void TextInputStep_AnswerField_AbsentFromSerializedJson()
    {
        TextInputStep step = new()
        {
            Id = "api-key",
            Prompt = "Enter API key",
            Secret = true,
            Required = true,
        };
        step.Value = "sk-secret-value";

        // Answer is set — IsAnswered must be true.
        Assert.True(step.IsAnswered);

        string json = JsonSerializer.Serialize<WizardStep>(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        // The answer field must not appear on the wire.
        Assert.Null(node?["value"]);
        Assert.Null(node?["Value"]);
    }

    // ------------------------------------------------------------------ //
    //  IsAnswered behavioural assertions
    // ------------------------------------------------------------------ //

    [Fact]
    public void TextInputStep_SettingValue_FlipsIsAnswered()
    {
        TextInputStep step = new() { Id = "x", Prompt = "p" };

        Assert.False(step.IsAnswered);

        step.Value = "hello";

        Assert.True(step.IsAnswered);
    }

    [Fact]
    public void ChooseOneStep_SettingSelectedIndex_FlipsIsAnswered()
    {
        ChooseOneStep step = new()
        {
            Id = "x",
            Prompt = "p",
            Options = [new WizardOption("L", "v")],
        };

        Assert.False(step.IsAnswered);

        step.SelectedIndex = 0;

        Assert.True(step.IsAnswered);
    }

    [Fact]
    public void YesNoStep_SettingAnswer_FlipsIsAnswered()
    {
        YesNoStep step = new() { Id = "x", Prompt = "p", Default = false };

        Assert.False(step.IsAnswered);

        step.Answer = true;

        Assert.True(step.IsAnswered);
    }

    // ------------------------------------------------------------------ //
    //  Other discriminators present in serialised output
    // ------------------------------------------------------------------ //

    [Fact]
    public void ChooseManyStep_Serialized_ContainsCorrectTypeDiscriminator()
    {
        WizardStep step = new ChooseManyStep
        {
            Id = "x",
            Prompt = "p",
            Options = [new WizardOption("L", "v")],
            MinSelections = 1,
        };

        string json = JsonSerializer.Serialize(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.chooseMany", node?["type"]?.GetValue<string>());
    }

    [Fact]
    public void YesNoStep_Serialized_ContainsCorrectTypeDiscriminator()
    {
        WizardStep step = new YesNoStep { Id = "x", Prompt = "p", Default = true };

        string json = JsonSerializer.Serialize(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.yesNo", node?["type"]?.GetValue<string>());
    }

    [Fact]
    public void InfoStep_Serialized_ContainsCorrectTypeDiscriminator()
    {
        WizardStep step = new InfoStep { Id = "x", Prompt = "p" };

        string json = JsonSerializer.Serialize(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.info", node?["type"]?.GetValue<string>());
    }

    [Fact]
    public void WizardCompletedStep_Serialized_ContainsCorrectTypeDiscriminator()
    {
        WizardStep step = new WizardCompletedStep { Id = "x", Prompt = string.Empty, Message = "Done!" };

        string json = JsonSerializer.Serialize(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.completed", node?["type"]?.GetValue<string>());
    }

    [Fact]
    public void ChooseManyStep_AnswerField_AbsentFromSerializedJson()
    {
        ChooseManyStep step = new()
        {
            Id = "x",
            Prompt = "p",
            Options = [new WizardOption("L", "v")],
            MinSelections = 1,
        };
        step.SelectedIndices = [0];

        string json = JsonSerializer.Serialize<WizardStep>(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Null(node?["selectedIndices"]);
        Assert.Null(node?["SelectedIndices"]);
    }

    [Fact]
    public void YesNoStep_AnswerField_AbsentFromSerializedJson()
    {
        YesNoStep step = new() { Id = "x", Prompt = "p", Default = false };
        step.Answer = true;

        string json = JsonSerializer.Serialize<WizardStep>(step, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Null(node?["answer"]);
        Assert.Null(node?["Answer"]);
    }
}

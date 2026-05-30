using System.Text.Json;
using System.Text.Json.Nodes;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Wizard;

namespace Dmon.Protocol.Tests.Wizard;

/// <summary>
/// Verifies round-trip serialisation for the three wizard RPC carriers:
/// WizardStartCommand, WizardAnswerCommand, and WizardStepEvent.
/// </summary>
public sealed class WizardRpcSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ //
    //  WizardStartCommand
    // ------------------------------------------------------------------ //

    [Fact]
    public void WizardStartCommand_RoundTrips_PolymorphicallyAsCommand()
    {
        Command original = new WizardStartCommand { Id = "cmd-1" };

        string json = JsonSerializer.Serialize(original, Options);
        Command? deserialized = JsonSerializer.Deserialize<Command>(json, Options);

        WizardStartCommand concrete = Assert.IsType<WizardStartCommand>(deserialized);
        Assert.Equal("cmd-1", concrete.Id);
    }

    [Fact]
    public void WizardStartCommand_Serialized_ContainsCorrectTypeDiscriminator()
    {
        Command cmd = new WizardStartCommand { Id = "cmd-1" };

        string json = JsonSerializer.Serialize(cmd, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.start", node?["type"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------ //
    //  WizardAnswerCommand
    // ------------------------------------------------------------------ //

    [Fact]
    public void WizardAnswerCommand_RoundTrips_PolymorphicallyAsCommand_WhenAnswered()
    {
        Command original = new WizardAnswerCommand
        {
            Id = "cmd-2",
            WizardId = "wiz-abc",
            Outcome = WizardAnswerOutcome.Answered,
            Value = "anthropic",
        };

        string json = JsonSerializer.Serialize(original, Options);
        Command? deserialized = JsonSerializer.Deserialize<Command>(json, Options);

        WizardAnswerCommand concrete = Assert.IsType<WizardAnswerCommand>(deserialized);
        Assert.Equal("cmd-2", concrete.Id);
        Assert.Equal("wiz-abc", concrete.WizardId);
        Assert.Equal(WizardAnswerOutcome.Answered, concrete.Outcome);
        Assert.Equal("anthropic", concrete.Value);
    }

    [Fact]
    public void WizardAnswerCommand_RoundTrips_WhenBack()
    {
        Command original = new WizardAnswerCommand
        {
            Id = "cmd-3",
            WizardId = "wiz-abc",
            Outcome = WizardAnswerOutcome.Back,
            Value = null,
        };

        string json = JsonSerializer.Serialize(original, Options);
        Command? deserialized = JsonSerializer.Deserialize<Command>(json, Options);

        WizardAnswerCommand concrete = Assert.IsType<WizardAnswerCommand>(deserialized);
        Assert.Equal(WizardAnswerOutcome.Back, concrete.Outcome);
        Assert.Null(concrete.Value);
    }

    [Fact]
    public void WizardAnswerCommand_RoundTrips_WhenCancel()
    {
        Command original = new WizardAnswerCommand
        {
            Id = "cmd-4",
            WizardId = "wiz-abc",
            Outcome = WizardAnswerOutcome.Cancel,
            Value = null,
        };

        string json = JsonSerializer.Serialize(original, Options);
        Command? deserialized = JsonSerializer.Deserialize<Command>(json, Options);

        WizardAnswerCommand concrete = Assert.IsType<WizardAnswerCommand>(deserialized);
        Assert.Equal(WizardAnswerOutcome.Cancel, concrete.Outcome);
    }

    [Fact]
    public void WizardAnswerCommand_Serialized_ContainsCorrectTypeDiscriminator()
    {
        Command cmd = new WizardAnswerCommand
        {
            Id = "cmd-5",
            WizardId = "wiz-abc",
            Outcome = WizardAnswerOutcome.Answered,
            Value = "val",
        };

        string json = JsonSerializer.Serialize(cmd, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.answer", node?["type"]?.GetValue<string>());
    }

    [Fact]
    public void WizardAnswerOutcome_SerializesAsString()
    {
        Command cmd = new WizardAnswerCommand
        {
            Id = "cmd-6",
            WizardId = "wiz-abc",
            Outcome = WizardAnswerOutcome.Answered,
            Value = null,
        };

        string json = JsonSerializer.Serialize(cmd, Options);
        JsonNode? node = JsonNode.Parse(json);

        // Must be the string "Answered", not an integer.
        Assert.Equal("Answered", node?["outcome"]?.GetValue<string>());
    }

    [Fact]
    public void WizardAnswerCommand_ChooseMany_ValueEncoding_IsCommaSeparated()
    {
        // ChooseMany answers are encoded as comma-separated zero-based indices.
        Command cmd = new WizardAnswerCommand
        {
            Id = "cmd-7",
            WizardId = "wiz-abc",
            Outcome = WizardAnswerOutcome.Answered,
            Value = "0,2",
        };

        string json = JsonSerializer.Serialize(cmd, Options);
        Command? deserialized = JsonSerializer.Deserialize<Command>(json, Options);

        WizardAnswerCommand concrete = Assert.IsType<WizardAnswerCommand>(deserialized);
        Assert.Equal("0,2", concrete.Value);
    }

    // ------------------------------------------------------------------ //
    //  WizardStepEvent
    // ------------------------------------------------------------------ //

    [Fact]
    public void WizardStepEvent_RoundTrips_PolymorphicallyAsEvent_WithChooseOneStep()
    {
        Event original = new WizardStepEvent
        {
            WizardId = "wiz-abc",
            Step = new ChooseOneStep
            {
                Id = "provider",
                Prompt = "Choose a provider",
                Options =
                [
                    new WizardOption("Anthropic", "anthropic", "Claude models"),
                    new WizardOption("OpenAI",    "openai",    null),
                ],
            },
        };

        string json = JsonSerializer.Serialize(original, Options);
        Event? deserialized = JsonSerializer.Deserialize<Event>(json, Options);

        WizardStepEvent evt = Assert.IsType<WizardStepEvent>(deserialized);
        Assert.Equal("wiz-abc", evt.WizardId);

        ChooseOneStep step = Assert.IsType<ChooseOneStep>(evt.Step);
        Assert.Equal("provider", step.Id);
        Assert.Equal("Choose a provider", step.Prompt);
        Assert.Equal(2, step.Options.Count);
        Assert.Equal("Anthropic", step.Options[0].Label);
        Assert.Equal("anthropic", step.Options[0].Value);
        Assert.Equal("Claude models", step.Options[0].Description);
        Assert.Equal("OpenAI", step.Options[1].Label);
        Assert.Equal("openai", step.Options[1].Value);
        Assert.Null(step.Options[1].Description);
    }

    [Fact]
    public void WizardStepEvent_Serialized_ContainsCorrectEventTypeDiscriminator()
    {
        Event evt = new WizardStepEvent
        {
            WizardId = "wiz-abc",
            Step = new TextInputStep { Id = "key", Prompt = "API key", Secret = true },
        };

        string json = JsonSerializer.Serialize(evt, Options);
        JsonNode? node = JsonNode.Parse(json);

        Assert.Equal("wizard.step", node?["type"]?.GetValue<string>());
    }

    [Fact]
    public void WizardStepEvent_Serialized_ContainsNestedStepTypeDiscriminator()
    {
        Event evt = new WizardStepEvent
        {
            WizardId = "wiz-abc",
            Step = new ChooseOneStep
            {
                Id = "x",
                Prompt = "p",
                Options = [new WizardOption("L", "v")],
            },
        };

        string json = JsonSerializer.Serialize(evt, Options);
        JsonNode? node = JsonNode.Parse(json);

        // The nested "step" object must carry the WizardStep discriminator.
        Assert.Equal("wizard.chooseOne", node?["step"]?["type"]?.GetValue<string>());
    }

    [Fact]
    public void WizardStepEvent_RoundTrips_WithTextInputStep()
    {
        Event original = new WizardStepEvent
        {
            WizardId = "wiz-def",
            Step = new TextInputStep
            {
                Id = "api-key",
                Prompt = "Enter your API key",
                Secret = true,
                Required = true,
            },
        };

        string json = JsonSerializer.Serialize(original, Options);
        Event? deserialized = JsonSerializer.Deserialize<Event>(json, Options);

        WizardStepEvent evt = Assert.IsType<WizardStepEvent>(deserialized);
        TextInputStep step = Assert.IsType<TextInputStep>(evt.Step);
        Assert.Equal("api-key", step.Id);
        Assert.True(step.Secret);
        Assert.True(step.Required);
    }

    [Fact]
    public void WizardStepEvent_Step_DoesNotLeakAnswerField()
    {
        // Answer fields on WizardStep subtypes are [JsonIgnore] — verify they
        // are absent from the nested step object in the event's JSON.
        ChooseOneStep chooseOne = new()
        {
            Id = "x",
            Prompt = "p",
            Options = [new WizardOption("L", "v")],
        };
        chooseOne.SelectedIndex = 0;

        Event evt = new WizardStepEvent { WizardId = "wiz-abc", Step = chooseOne };

        string json = JsonSerializer.Serialize(evt, Options);
        JsonNode? stepNode = JsonNode.Parse(json)?["step"];

        Assert.Null(stepNode?["selectedIndex"]);
        Assert.Null(stepNode?["SelectedIndex"]);
    }
}

using Dmon.Abstractions.Wizard;
using Dmon.Providers;

namespace Dmon.Providers.Tests;

/// <summary>
/// Verifies the three-step wizard flow (api-key → model → completed) for each
/// built-in provider factory, using empty/null keys so no network call is made.
/// </summary>
public sealed class ProviderFactoryWizardTests
{
    // ------------------------------------------------------------------ //
    //  AnthropicProviderFactory
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task AnthropicFactory_FirstStep_WhenEnvVarSet_UsesDefault()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-key");
        try
        {
            AnthropicProviderFactory factory = new();
            WizardStep step = await factory.GetNextStepAsync(WizardState.Empty);
            TextInputStep textStep = Assert.IsType<TextInputStep>(step);
            Assert.Equal("api-key", textStep.Id);
            Assert.False(textStep.Required);
            Assert.Equal("test-key", textStep.Default);
            Assert.True(textStep.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    [Fact]
    public async Task AnthropicFactory_FirstStep_WhenEnvVarNotSet_IsRequired()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            AnthropicProviderFactory factory = new();
            WizardStep step = await factory.GetNextStepAsync(WizardState.Empty);
            TextInputStep textStep = Assert.IsType<TextInputStep>(step);
            Assert.Equal("api-key", textStep.Id);
            Assert.True(textStep.Required);
            Assert.Null(textStep.Default);
            Assert.True(textStep.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    [Fact]
    public async Task Anthropic_SecondStep_IsModelChoiceWithAtLeastOneOption()
    {
        AnthropicProviderFactory factory = new();

        WizardState state = StateWithAnsweredApiKey();
        WizardStep step = await factory.GetNextStepAsync(state);

        ChooseOneStep modelStep = Assert.IsType<ChooseOneStep>(step);
        Assert.Equal("model", modelStep.Id);
        Assert.NotEmpty(modelStep.Options);
    }

    [Fact]
    public async Task Anthropic_ThirdStep_IsCompleted()
    {
        AnthropicProviderFactory factory = new();

        WizardState state = StateWithAnsweredApiKeyAndModel(factory);
        WizardStep step = await factory.GetNextStepAsync(state);

        Assert.IsType<WizardCompletedStep>(step);
    }

    // ------------------------------------------------------------------ //
    //  OpenAiProviderFactory
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task OpenAiFactory_FirstStep_WhenEnvVarSet_UsesDefault()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            OpenAiProviderFactory factory = new();
            WizardStep step = await factory.GetNextStepAsync(WizardState.Empty);
            TextInputStep textStep = Assert.IsType<TextInputStep>(step);
            Assert.Equal("api-key", textStep.Id);
            Assert.False(textStep.Required);
            Assert.Equal("test-key", textStep.Default);
            Assert.True(textStep.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public async Task OpenAiFactory_FirstStep_WhenEnvVarNotSet_IsRequired()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            OpenAiProviderFactory factory = new();
            WizardStep step = await factory.GetNextStepAsync(WizardState.Empty);
            TextInputStep textStep = Assert.IsType<TextInputStep>(step);
            Assert.Equal("api-key", textStep.Id);
            Assert.True(textStep.Required);
            Assert.Null(textStep.Default);
            Assert.True(textStep.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public async Task OpenAi_SecondStep_IsModelChoiceWithAtLeastOneOption()
    {
        OpenAiProviderFactory factory = new();

        WizardState state = StateWithAnsweredApiKey();
        WizardStep step = await factory.GetNextStepAsync(state);

        ChooseOneStep modelStep = Assert.IsType<ChooseOneStep>(step);
        Assert.Equal("model", modelStep.Id);
        Assert.NotEmpty(modelStep.Options);
    }

    [Fact]
    public async Task OpenAi_ThirdStep_IsCompleted()
    {
        OpenAiProviderFactory factory = new();

        WizardState state = StateWithAnsweredApiKeyAndModel(factory);
        WizardStep step = await factory.GetNextStepAsync(state);

        Assert.IsType<WizardCompletedStep>(step);
    }

    // ------------------------------------------------------------------ //
    //  GeminiProviderFactory
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task GeminiFactory_FirstStep_WhenEnvVarSet_UsesDefault()
    {
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "test-key");
        try
        {
            GeminiProviderFactory factory = new();
            WizardStep step = await factory.GetNextStepAsync(WizardState.Empty);
            TextInputStep textStep = Assert.IsType<TextInputStep>(step);
            Assert.Equal("api-key", textStep.Id);
            Assert.False(textStep.Required);
            Assert.Equal("test-key", textStep.Default);
            Assert.True(textStep.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        }
    }

    [Fact]
    public async Task GeminiFactory_FirstStep_WhenEnvVarNotSet_IsRequired()
    {
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        try
        {
            GeminiProviderFactory factory = new();
            WizardStep step = await factory.GetNextStepAsync(WizardState.Empty);
            TextInputStep textStep = Assert.IsType<TextInputStep>(step);
            Assert.Equal("api-key", textStep.Id);
            Assert.True(textStep.Required);
            Assert.Null(textStep.Default);
            Assert.True(textStep.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Gemini_SecondStep_IsModelChoiceWithAtLeastOneOption()
    {
        GeminiProviderFactory factory = new();

        WizardState state = StateWithAnsweredApiKey();
        WizardStep step = await factory.GetNextStepAsync(state);

        ChooseOneStep modelStep = Assert.IsType<ChooseOneStep>(step);
        Assert.Equal("model", modelStep.Id);
        Assert.NotEmpty(modelStep.Options);
    }

    [Fact]
    public async Task Gemini_ThirdStep_IsCompleted()
    {
        GeminiProviderFactory factory = new();

        WizardState state = StateWithAnsweredApiKeyAndModel(factory);
        WizardStep step = await factory.GetNextStepAsync(state);

        Assert.IsType<WizardCompletedStep>(step);
    }

    // ------------------------------------------------------------------ //
    //  Shared helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Builds a WizardState containing an answered api-key step (with no real key,
    /// so the factory falls back to its static model list).
    /// </summary>
    private static WizardState StateWithAnsweredApiKey()
    {
        TextInputStep apiKeyStep = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = true,
            Required = true,
        };
        // Empty string: factory treats this as "no key supplied" and returns FallbackModels.
        apiKeyStep.Value = string.Empty;

        return new WizardState([apiKeyStep]);
    }

    /// <summary>
    /// Builds a WizardState with answered api-key step plus the first model option selected,
    /// using the factory's own fallback model list so no network call is made.
    /// </summary>
    private static WizardState StateWithAnsweredApiKeyAndModel(
        Dmon.Abstractions.Providers.IProviderFactory factory)
    {
        TextInputStep apiKeyStep = new()
        {
            Id       = "api-key",
            Prompt   = "API key",
            Secret   = true,
            Required = true,
        };
        apiKeyStep.Value = string.Empty;

        // Ask the factory for the model step so we use its real options list.
        WizardState withKey = new([apiKeyStep]);
        WizardStep modelStepRaw = factory.GetNextStepAsync(withKey).GetAwaiter().GetResult();
        ChooseOneStep modelStep = (ChooseOneStep)modelStepRaw;
        modelStep.SelectedIndex = 0;

        return new WizardState([apiKeyStep, modelStep]);
    }
}

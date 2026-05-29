## ADDED Requirements

### Requirement: Turn specifies the active model id

On each provider call, the agent core SHALL set `ChatOptions.ModelId` to the active model id before invoking the chat pipeline, so model resolution does not depend on a provider-specific baked-in default. The active model id SHALL be the registry's current model (`IProviderRegistry.GetCurrentModelId()`), falling back to the active provider's configured default model. When no model id is resolvable (both null or empty), the core SHALL leave `ChatOptions.ModelId` unset and rely on the provider client's default.

#### Scenario: Model id set on each turn

- **WHEN** the core executes a turn against the active provider
- **THEN** the `ChatOptions` passed to the provider call has `ModelId` set to the active model id, so providers whose adapter requires an explicit model (e.g. Gemini) complete the turn without throwing `Model ID must be specified`

#### Scenario: In-session model switch honoured on the next turn

- **WHEN** the host switches the model mid-session and then submits a new turn
- **THEN** the `ChatOptions.ModelId` for that turn reflects the switched-to model (`GetCurrentModelId()`), not only the static configured default

#### Scenario: No model configured leaves ModelId unset

- **WHEN** neither the registry's current model nor the configured default model resolves to a non-empty value
- **THEN** the core leaves `ChatOptions.ModelId` unset, preserving the provider client's baked-in default behaviour

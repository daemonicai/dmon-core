## MODIFIED Requirements

### Requirement: ProviderName is stable and unique
`ProviderName` SHALL be a human-readable string that is unique per provider (e.g. `"Ollama"`, `"LM Studio"`, `"llama.cpp"`) and stable across versions of the extension package. It is used as `ProviderConfig.Name` in the registry and passed to `SetProvider(name)`.

#### Scenario: ProviderName used as registry key
- **WHEN** a provider extension is registered
- **THEN** the registry stores the provider under the key equal to `extension.ProviderName`

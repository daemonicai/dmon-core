## ADDED Requirements

### Requirement: Provider config schema is validated at load

The `providers` block SHALL be a map keyed by provider name. `ProviderConfigLoader` SHALL validate this shape at load time and throw `InvalidOperationException` with an actionable message when the block is malformed, rather than producing providers with index-derived or empty names. The error message SHALL name the offending entry and state the canonical map-keyed schema (`adapter`, optional `defaultModelId`/`baseUrl`, and `auth.type`/`auth.envVar`).

A provider name derived from the configuration section key SHALL be rejected when it is purely numeric (the shape produced when `providers` is written as a YAML sequence) or empty/whitespace.

#### Scenario: Providers written as a YAML sequence

- **WHEN** the `providers` block is authored as a YAML sequence (`- provider: anthropic …`), so the configuration binder keys the entries `0`, `1`, `2`, …
- **THEN** `ProviderConfigLoader.Load` throws `InvalidOperationException` whose message names the numeric key and states that `providers` must be a map keyed by provider name

#### Scenario: Empty or whitespace provider name

- **WHEN** a provider entry's key is empty or whitespace
- **THEN** `ProviderConfigLoader.Load` throws `InvalidOperationException` identifying the offending entry

#### Scenario: Missing adapter

- **WHEN** a map-keyed provider entry omits the required `adapter` field
- **THEN** `ProviderConfigLoader.Load` throws `InvalidOperationException` naming the provider and the missing `adapter` field

#### Scenario: Valid map-keyed providers load

- **WHEN** the `providers` block is a map keyed by name, each entry carrying `adapter` and optionally `defaultModelId`, `baseUrl`, and an `auth` block
- **THEN** `ProviderConfigLoader.Load` returns one `ProviderConfig` per entry whose `Name` is the map key, with `auth.type` defaulting to `none` when the `auth` block is omitted

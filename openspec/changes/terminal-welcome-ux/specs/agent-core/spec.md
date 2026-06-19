## ADDED Requirements

### Requirement: `agentReady.coreVersion` is the stamped informational version

The `coreVersion` field of the `agentReady` event SHALL be the core assembly's stamped informational/package version (from `AssemblyInformationalVersionAttribute`, as produced by the build's version stamping), not the numeric four-part `AssemblyVersion`. When the informational version is unavailable, the core SHALL fall back to the numeric assembly version and then to `"0.0.0"`, so the value is never empty. The `agentReady` wire shape (`{protocolVersion, coreVersion}`) SHALL be unchanged; only the `coreVersion` value's derivation changes.

#### Scenario: Readiness reports the stamped version

- **WHEN** the agent core emits its `agentReady` event from a build whose assembly carries a stamped `AssemblyInformationalVersionAttribute`
- **THEN** `coreVersion` equals that informational version string (e.g. `0.2.0-preview.23`) and is not the numeric `0.0.0.0`

#### Scenario: Fallback when no informational version is stamped

- **WHEN** the core assembly has no `AssemblyInformationalVersionAttribute`
- **THEN** `coreVersion` falls back to the numeric assembly version, and to `"0.0.0"` if that too is absent, and is never empty

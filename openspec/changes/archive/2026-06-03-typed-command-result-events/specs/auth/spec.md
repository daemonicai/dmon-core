## MODIFIED Requirements

### Requirement: auth.status query
The system SHALL respond to `auth.status` with the authentication state of all configured providers, as a typed `auth.statusResult` event derived from the `ResultEvent` correlation base and carrying the originating command's `id` (serialized as `id`). The response SHALL NOT use the generic `{type:"response", data}` envelope.

#### Scenario: Status shows which providers have credentials
- **WHEN** the host sends `auth.status` with `id` `"req-1"`
- **THEN** the core emits an `auth.statusResult` event with `id` = `"req-1"` whose `providers` field lists all configured providers, each with `authenticated: true/false`

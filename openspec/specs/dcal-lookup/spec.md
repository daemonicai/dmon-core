# dcal-lookup

## Purpose

The `dcal-lookup` capability defines the agent-facing tools that let the driving agent query a personal calendar without reasoning over raw event lists. The `Dmon.Tools.Dcal` package exposes `lookup_calendar` and `list_upcoming_events` AI tools that delegate matching to a calendar server's SQLite FTS5 index over HTTP, and participates in scope-gated tool manifests (ADR-027) so triage-enabled agents receive calendar tools only on personal-scope turns.

## Requirements

### Requirement: lookup_calendar tool finds the next matching event
The `lookup_calendar` AI tool SHALL accept a free-text `term` and an optional ISO-8601 `after` datetime, delegate matching to the calendar server's SQLite FTS5 index, and return the next upcoming event whose title or description contains the term. The model MUST NOT receive a list of events to reason over; the store performs the match and returns at most one result.

#### Scenario: Matching event found
- **WHEN** `lookup_calendar` is called with `term="eyebrow"` and the store contains a future event with "Eyebrow appointment" in its title
- **THEN** the tool returns a single `CalendarEvent` with the correct `title`, `startUtc`, `endUtc`, `location`, and `uid`

#### Scenario: No matching event
- **WHEN** `lookup_calendar` is called with a term that matches no future event
- **THEN** the tool returns a message indicating no event was found (not an error)

#### Scenario: after parameter filters past events
- **WHEN** `lookup_calendar` is called with `after` set to a future datetime
- **THEN** only events starting at or after that datetime are considered

#### Scenario: after defaults to current time
- **WHEN** `lookup_calendar` is called without an `after` parameter
- **THEN** the search window starts from the time of the call (past events are excluded)

#### Scenario: returned time matches store row exactly
- **WHEN** `lookup_calendar` returns an event
- **THEN** the `startUtc` and `endUtc` values are byte-for-byte identical to the values stored in SQLite (no reformatting by the model)

### Requirement: list_upcoming_events tool returns the next N events
The `list_upcoming_events` AI tool SHALL return the next N calendar events (default 5, max 20) starting from an optional `after` datetime, ordered by `start_utc` ascending.

#### Scenario: Returns events in chronological order
- **WHEN** `list_upcoming_events` is called with `maxResults=3`
- **THEN** the response contains at most 3 events ordered by ascending `startUtc`

#### Scenario: Empty calendar returns empty list
- **WHEN** the store contains no future events
- **THEN** `list_upcoming_events` returns an empty list (not an error)

### Requirement: DcalExtension declares scope "personal" via IAbilityProvider
`DcalAbilityProvider : IAbilityProvider` SHALL expose `Scope => "personal"` (the Phase 1 opaque string scope label) and the same `lookup_calendar` and `list_upcoming_events` AITools. When registered via `AddDcalAbilities()`, it participates in `AbilityRegistry.ForScope("personal")` so that triage-enabled agents receive calendar tools only on personal-scope turns.

#### Scenario: Calendar tools appear in personal-scope manifest
- **WHEN** `AddDcalAbilities()` is registered and `AbilityRegistry.ForScope("personal")` is called
- **THEN** `lookup_calendar` and `list_upcoming_events` are present in the returned tool list

#### Scenario: Calendar tools absent from world-scope manifest
- **WHEN** `AddDcalAbilities()` is registered and `AbilityRegistry.ForScope("world")` is called
- **THEN** `lookup_calendar` and `list_upcoming_events` are NOT present in the returned tool list

### Requirement: DcalExtension HTTP client honours DCAL_BASE_URL and DCAL_API_KEY
The parameterless constructor of `DcalExtension` SHALL read `DCAL_BASE_URL` (default `http://localhost:5280`) and `DCAL_API_KEY` (optional) from environment variables.

#### Scenario: Default base URL used when env var absent
- **WHEN** `DCAL_BASE_URL` is not set and `DcalExtension` is constructed via parameterless constructor
- **THEN** requests are sent to `http://localhost:5280`

#### Scenario: API key sent as X-Api-Key header
- **WHEN** `DCAL_API_KEY` is set and a tool is invoked
- **THEN** the HTTP request includes an `X-Api-Key` header with that value

#### Scenario: Missing calendar server returns graceful error message
- **WHEN** the calendar server is unreachable and a tool is invoked
- **THEN** the tool returns a string error message (not an exception propagated to the model)

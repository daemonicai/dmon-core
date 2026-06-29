## ADDED Requirements

### Requirement: Provider stop lifecycle
The provider extension lifecycle SHALL include a `StopAsync(cancellationToken)` operation that complements the attach-first `EnsureRunningAsync`. A provider that spawned a server it owns SHALL terminate that server on `StopAsync`. Providers that only attach to externally-managed servers, or that are start-only, SHALL provide a default no-op `StopAsync` so existing providers are unaffected.

#### Scenario: Owning provider stops its server
- **WHEN** `StopAsync` is called on a provider that spawned and owns a server process
- **THEN** the provider terminates that process and releases its resources

#### Scenario: Start-only provider default is a no-op
- **WHEN** `StopAsync` is called on a provider that does not own a spawned process
- **THEN** the default implementation is a no-op and no external process is affected

#### Scenario: Stop after attach leaves external server running
- **WHEN** a provider attached to an already-running external server and `StopAsync` is later called
- **THEN** the external server is left running

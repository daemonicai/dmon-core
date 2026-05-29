## ADDED Requirements

### Requirement: Pending provider/model switch is committed before the turn runs

The agent core SHALL commit any pending provider/model switch at the start of a turn, before the active provider's `IChatClient` is resolved for that turn. As a result, a switch queued between turns SHALL take effect on the next turn (the turn uses the newly selected provider and model), while a switch queued during an in-flight turn SHALL defer to the following turn. When a switch is committed, the core SHALL emit `providerSwitched` with `effectiveNextTurn` reflecting whether the switch applies to the turn now starting (`false`) or a later turn.

#### Scenario: Between-turns selection used on the next turn

- **WHEN** the host sends `model.set` (provider and/or model) while no turn is running, and then submits a turn
- **THEN** the submitted turn resolves and calls the newly selected provider's client — not the previously active provider — because the pending switch is committed before the provider client is resolved

#### Scenario: Mid-turn switch still defers

- **WHEN** the host sends `model.set` while a turn is in flight
- **THEN** the in-flight turn completes on the previous provider and the new selection takes effect on the next turn, with `providerSwitched {..., effectiveNextTurn: true}` emitted

#### Scenario: No pending switch leaves the active provider unchanged

- **WHEN** a turn starts and no provider/model switch is pending
- **THEN** the turn resolves the already-active provider client and no `providerSwitched` event is emitted

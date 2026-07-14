## ADDED Requirements

### Requirement: Host-directed interaction handlers surface failures as an error state

The desktop host's fire-and-forget handlers for host-directed input events — the `tool.confirmRequest` handler and the UI-input handler — SHALL NOT allow an exception to escape as an unhandled exception on the UI thread. Each handler SHALL guard its full body so that a failure at any step (raising the ReactiveUI `Interaction`, or relaying the response to the core via `SendAsync` when the core is dead or the client is faulted) is caught and surfaced as an error state — logged, and shown where a surface exists — rather than crashing the process. The two handlers SHALL be independent: a failure in one SHALL NOT suppress or short-circuit the other, and the successful (non-failing) path SHALL be unchanged.

#### Scenario: Dead-core send failure does not crash the host
- **WHEN** the tool-confirmation or UI-input handler relays its response and the send fails (for example the core has died and the underlying client faults)
- **THEN** the failure is caught and surfaced as an error state (logged / shown), and no unhandled exception reaches the UI thread

#### Scenario: Interaction failure is contained
- **WHEN** raising or awaiting the confirmation or input `Interaction` throws
- **THEN** the handler catches it and surfaces an error state instead of propagating an unhandled exception, leaving the other handler unaffected

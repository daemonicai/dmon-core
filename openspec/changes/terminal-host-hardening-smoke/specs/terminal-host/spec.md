## ADDED Requirements

### Requirement: Hardening behaviors verified against a live core

The terminal host's two user-visible hardening behaviors SHALL be confirmed against a live `Dmon.Core` process — not only via the tier-A/tier-B proxy tests, which approximate the `Program.cs` orchestration because the host exposes no injection seam. The live acceptance is: (1) the goodbye separator renders on Ctrl+C exit while `dcli`'s fixed region is still alive; (2) rapid-fire `/reload` submitted during the restart window restarts the core exactly once. This acceptance is a precondition for treating the hardening as field-verified; it is gated on `Dmon.Core` starting cleanly and a provider being configured per ADR-005.

#### Scenario: Goodbye separator visible on live Ctrl+C

- **WHEN** `dmon` is launched against a live core with a configured provider, the user interacts, and then presses Ctrl+C
- **THEN** the `── goodbye ──` separator appears in the terminal scrollback before the process exits with code 0

#### Scenario: Rapid /reload restarts once in a live session

- **WHEN** the user submits `/reload` twice in quick succession during the restart window in a live session
- **THEN** exactly one `[Reload] Core restarted.` system line appears (the core is restarted once, not twice)

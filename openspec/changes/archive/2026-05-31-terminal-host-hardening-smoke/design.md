## Context

`terminal-host-hardening` (archived) shipped four fixes to the terminal host. Three are invisible to the user (`volatile IsLocked`, `DrainAsync` exception handling, the `HandleAsync` rename) and are fully covered by automated tests. Two are user-visible — the goodbye separator on Ctrl+C exit and single-shot `/reload` — and the hardening change could only cover them with *proxy* tests: `Program.cs` is top-level statements that spawn a concrete, inline `CoreProcessManager` with no injection seam, so `RunSessionAsync_RapidReload_OnlyRestartsOnce` was implemented as a hand-mirrored TCS de-dup simulation and the goodbye test asserts the renderer contract rather than the real shutdown ordering.

The hardening change's task 5.1 was the live end-to-end smoke that closes that gap. It is blocked on two external conditions and was deferred so the hardening could archive.

## Goals / Non-Goals

**Goals:**

- Track the deferred live smoke as a discoverable, standalone change rather than a stale unticked box in an archived change.
- Record an explicit `terminal-host` acceptance criterion for the two user-visible behaviors, verified against a live core.

**Non-Goals:**

- Fixing the `Dmon.Core` MCP/`Microsoft.Extensions.AI` startup crash (a separate concern; this change is gated on it, not responsible for it).
- Any production code change. If the smoke reveals a defect, it is fixed under a new change.
- Re-verifying the three non-user-visible fixes — automated tests already cover them.

## Decisions

### 1. Carve the smoke into a standalone change rather than hold the hardening open

The hardening change is functionally complete with green gates. Blocking its archive on an externally-gated manual step would leave it open indefinitely. A standalone verification change keeps the debt tracked without coupling it to the hardening's lifecycle.

### 2. Add an acceptance requirement, not new behavior

The spec delta ADDs a `terminal-host` requirement capturing the *live-observable* acceptance of behaviors already specified (under "Ctrl+C exits cleanly" and "Host lifecycle hardening"). This is the honest spec-driven home for a verification: it states what "verified against a live core" means in observable terms, giving the change a real delta without inventing new runtime behavior.

## Risks / Trade-offs

- **Risk: the change stays open as long as `Dmon.Core` is uncrashed-startup-blocked.** *Mitigation:* that is the point — it is the visible tracker for the verification debt. It archives once the smoke passes.
- **Risk: the live smoke surfaces a real defect the proxy tests missed.** *Mitigation:* desirable outcome — that is exactly what an end-to-end smoke is for; the fix lands as a new change and this one archives once the recipe passes.

## Open Questions

None. The recipe and acceptance criteria are fully specified; the only blocker is external (core startup + provider key).

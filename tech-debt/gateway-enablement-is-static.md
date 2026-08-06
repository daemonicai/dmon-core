# Gateway enablement is a build-time constant

**Status:** open — correct for the current spec, will not survive section 6
**Where:** `home/Sources/Supervisor/HostRuntime.swift` (`isGatewayEnabled`), `home/Sources/Power/GatewayActivityPolicy.swift`
**Surfaced:** 2026-08-06, by the section-5 supervisor
**Severity:** low — a product question, not a defect

## What

`HostRuntime.isGatewayEnabled` is `nonisolated`, derived from the descriptor set
fixed at `init`. So "the gateway is enabled" can only ever mean **build/config
time** — it can never mean "actually serving". `GatewayActivityPolicy` is
applied once at launch and once at termination.

Both `isGatewayEnabled` and the policy also hard-code *the network gateway* as
the one thing that drives the activity assertion, while `ChildInventory` already
carries five other, currently-disabled children.

## Why this is not a defect today

It matches the spec's wording exactly — *"while the gateway is enabled … release
it when the gateway is disabled"* — and only one child is enabled, so there is
no second candidate and no runtime toggle. Building a dynamic driver now would
be speculative machinery for a state the system cannot enter.

## What to do

Answer two product questions before or during **section 6**, which is where they
first bite:

1. **Should a gateway client that fails, or disconnects, release the assertion?**
   Once there is a real client, "enabled" and "serving" genuinely diverge, and
   holding a sleep-preventing assertion for a gateway that is down is a
   user-visible cost (a Mac that will not sleep for no reason).
2. **Should a second always-on child also hold it?** If the mlx runtimes become
   always-on, the assertion arguably tracks "any always-on child" rather than
   "the gateway".

If the answer to either is yes, the change is to drive the policy from a live
signal rather than a constant — the `Bool`-driven policy shape was chosen partly
so that swap costs nothing in `Power`.

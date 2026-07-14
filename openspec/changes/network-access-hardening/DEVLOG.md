# DEVLOG — network-access-hardening

Cross-block memory for the architect. Newest block first.

## Block 2 — Browser-Origin allowlist on the /ws upgrade (#17) — tasks 3.1, 3.2, 4.2

**Status:** DONE, reviewer approved, gates green. **Final code block — change is complete (all 12 tasks ticked).**

### What landed
- `frontends/Dmon.Network/NetworkOptions.cs` — new `public string[] AllowedOrigins { get; set; } = [];` (bound from `Network` section, default empty ⇒ all Origin-bearing upgrades rejected; browser access is opt-in). Documented with exact-ordinal-match / `~/.dmon/network` home notes.
- `frontends/Dmon.Network/NetworkConnectionEndpoint.cs` — in `HandleAsync`, **ahead of** the block-1 empty-set 401 gate: read `context.Request.Headers.Origin`; `string.IsNullOrEmpty(origin)` ⇒ proceed (absent header or empty value = native client); present + not an exact ordinal match (`Array.IndexOf(options.AllowedOrigins, origin)`) ⇒ **HTTP 403** and `return` before `AcceptWebSocketAsync`. Reuses the already-resolved `options` (no second `.CurrentValue`). Block-1's 401 gate left byte-identical. Load-bearing comment at `:177–182` documents the 403-vs-401 split + defence-in-depth ordering.
- `test/Dmon.Network.Tests/AuthAndBindTests.cs` — new `3.2 — Origin allowlist` section, 4 tests mapping 1:1 to 4.2(a)–(d): no-Origin proceeds; Origin+empty allowlist⇒403; Origin+exact match proceeds; allowlisted Origin+non-empty set+bad token⇒still 401 (independence). Network.Tests now 222.

### Decisions / notes
- **Gate order in `HandleAsync`: Origin 403 gate FIRST, then empty-set 401 gate, then `Authenticate` 401.** This gives 4.2(d) for free (allowlisted origin never short-circuits auth). Reverse (good token + unlisted origin ⇒ 403) holds by construction — Origin gate returns before auth is consulted.
- Multiple `Origin` headers ⇒ comma-joined `StringValues` string ⇒ matches nothing ⇒ 403 (fail-safe). Reviewer noted this as a positive.
- Reviewer optional nits (NOT applied): a dedicated reverse-independence test (good token + unlisted origin ⇒ 403) and an explicit empty-string-Origin test would document intent; 4.2(a)–(d) as specified are all present, so these are extras not gaps.

## Block 1 — Empty key set fails closed on a non-loopback bind (#6) — tasks 2.1, 2.2, 2.3, 4.1

**Commit:** (this block) · **Status:** DONE, reviewer approved, gates green.

### What landed
- `frontends/Dmon.Network/NetworkConnectionEndpoint.cs` — `HandleAsync` now snapshots the key set **once** into a local `keySet` (closes the empty-check/`Authenticate` TOCTOU), computes `bool nonLoopbackBind = NetworkBindPolicy.IsNonLoopbackWithOptIn(options.BindAddress, options.AllowNonLoopbackBind)` from `_options.CurrentValue`, and if `keySet.IsEmpty && nonLoopbackBind` sets `Response.StatusCode = 401` and **returns before `AcceptWebSocketAsync`** (with a warning log). The gate sits at `~:179–184`, accept at `~:202`. The non-empty `DeviceKeyAuthenticator.Authenticate(authHeader, keySet)` path is byte-identical — only substituted the local snapshot for the inline `.Current` read. `DeviceKeyAuthenticator.cs`/`DeviceAuthResult.cs` untouched.
- `frontends/Dmon.Network/Program.cs` — hoisted `IsNonLoopbackWithOptIn` into a local `effectiveNonLoopback` (reused for the existing non-loopback warning **and** the new log); added a conditional startup log after the key set resolves: "FAIL-CLOSED …" when empty+non-loopback, "disabled …" when empty+loopback. **No ILogger in Program.cs** — it uses `Console.WriteLine`. The old "auth disabled" reference at ~line 53 was only a *comment*, never a runtime log — so 2.3 *added* a log, didn't edit one.
- `test/Dmon.Network.Tests/AuthAndBindTests.cs` — added `MakeEndpoint(DeviceKeySet, NetworkOptions)` overload + `NonLoopbackOptions()` helper (`BindAddress="http://100.64.0.1:5500"`, `AllowNonLoopbackBind=true` — CGNAT range, a real non-loopback-with-opt-in). 6 new `HandleAsync` tests: empty+non-loopback⇒401 (with & without Authorization header), empty+loopback⇒authorized (NotEqual 401), non-empty+non-loopback⇒{matching NotEqual 401, missing 401, wrong 401}.

### Decisions / notes for the next block
- **401 is #6's status; 403 is reserved for block 2's Origin rejection.** Keep them distinct.
- Test assertion idiom: authorized paths assert `NotEqual(401)` on a `DefaultHttpContext` (falls through to 400 `IsWebSocketRequest`), which proves no socket opened. Reviewer nit (optional, not applied): `Assert.Equal(400, …)` would pin the fall-through more tightly.
- `_options.CurrentValue` is read per-request inside `HandleAsync` (consistent with existing `HandleCreateAsync`); honours hot-reload, though the effective bind can't change post-`UseUrls`.
- Empty+loopback still flows through `DeviceKeyAuthenticator.Authenticate` (empty set authorizes) rather than short-circuiting at the endpoint — keeps "empty authorizes" semantics owned in one place; endpoint only adds the bind fence.

### Block 2 (remaining) — Browser-Origin allowlist (#17) — tasks 3.1, 3.2, 4.2
- 3.1: add `AllowedOrigins` (`string[]`, bound from `Network` section, default empty) to `frontends/Dmon.Network/NetworkOptions.cs`. Default empty ⇒ all browser `Origin`s rejected.
- 3.2: in `HandleAsync`, **before `AcceptWebSocketAsync` and independent of the device-key check**, read the `Origin` request header: absent ⇒ proceed (native clients send none); present ⇒ **403** unless it exactly matches an `AllowedOrigins` entry. Both Origin and device-key checks must pass; neither waives the other. Order the two gates so a bad-token + allowlisted-origin still 401s (see 4.2(d)).
- 4.2 tests: no Origin⇒proceeds; Origin+empty allowlist⇒403; Origin+exact match⇒proceeds; allowlisted Origin + bad device-key (non-empty set)⇒still 401 (independence).
- Spec deltas for both #6 and #17 already authored in `specs/remote-session-gateway/spec.md`.
- Gate 5.3 (spec-matches-implementation sign-off) lands at change end after block 2.

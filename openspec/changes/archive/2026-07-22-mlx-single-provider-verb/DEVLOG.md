# DEVLOG — mlx-single-provider-verb

## NEXT

Change complete — all tasks (1.1–3.1) done, reviewer approved, gates green. Ready for `/opsx:archive` once merged.

---

## Block 1 — `UseMlx` active-provider verb + tests (tasks 1.1–3.1)

Single block covering the whole change (architect confirmed: too small to split — a public verb and its tests are one coherent, independently gate-passing deliverable).

**Delivered**
- `providers/Dmon.Providers.Mlx/UseMlxExtensions.cs` (new) — `namespace Dmon.Hosting`, two public methods:
  - `UseMlx<T>(this T, MlxRuntimeOptions)` = `AddProvider(new MlxProviderExtension(options)).UseModel("mlx", options.ModelId)` — line-for-line the `UseLlamaCpp(options)` overload with `llamacpp`→`mlx`.
  - `UseMlx<T>(this T, string modelId, int port = 8666)` — builds `new MlxRuntimeOptions { ModelId = modelId, Port = port }` directly.
- `test/Dmon.Providers.Mlx.Tests/UseMlxTests.cs` (new) — 8 tests, reusing the existing `FakeProviderRegistration`.

**Decisions locked (from design D1–D4)**
- **Port default 8666** (user choice) — deliberately off the daemon's fixed firstline (8800) / escalation (8810) ports. MLX is fixed-port attach-first, so a shared default would silently attach a standalone agent to the daemon's firstline chat model. The convenience overload sets `Port = port` explicitly because `MlxRuntimeOptions`'s own record default is 8800.
- **No silent model default** — caller supplies the model id; the overload does NOT route through `Firstline()/Escalation()` (those carry chat-model defaults + nvfp4 validation wrong for a general active provider).
- **ADR-027 honoured, no ADR change** — `UseMlx` is a separate non-triage path; `AddMlxExtensions.cs` (keyed router backends) untouched. Isolation test proves the keyed runtimes don't leak into `GetServices<IProviderExtension>()`.

**Reviewer** — Approve, no blockers. One nit (a default-overridability test leans on `ConfigurationManager` last-source-wins semantics; harmless, left as-is).

**Gates** — `make build` 0W/0E; `env -u MEKO_API_KEY make test` all projects green (Mlx.Tests 85/85); `openspec validate --strict` valid.

**Out of scope (follow-up):** wiring the `sandbox-code/Dmon.cs` consumer to `.UseMlx(...).AddBuiltinTools()`. Not part of this change.

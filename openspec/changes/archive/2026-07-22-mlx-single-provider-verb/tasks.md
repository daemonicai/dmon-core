## 1. UseMlx composition verb

- [x] 1.1 Add `providers/Dmon.Providers.Mlx/UseMlxExtensions.cs` — `namespace Dmon.Hosting`, `public static class UseMlxExtensions`. Implement `UseMlx<T>(this T registration, MlxRuntimeOptions options) where T : IProviderRegistration` as `registration.AddProvider(new MlxProviderExtension(options)).UseModel("mlx", options.ModelId)`, mirroring `UseLlamaCppExtensions`.
- [x] 1.2 Add the convenience overload `UseMlx<T>(this T registration, string modelId, int port = 8666) where T : IProviderRegistration` that builds `new MlxRuntimeOptions { ModelId = modelId, Port = port }` and delegates to 1.1. No silent model default. XML doc: explain the 8666 default (distinct from firstline 8800 / escalation 8810 fixed ports), the attach-first collision rationale, and that the caller must supply an explicit `mlx-community` model id (no chat/coding default).

## 2. Tests

- [x] 2.1 In `test/Dmon.Providers.Mlx.Tests`, add tests asserting `UseMlx(modelId)` registers an active `IProviderExtension` (an `MlxProviderExtension`) and sets the default active model to `mlx/<modelId>` — mirroring the `UseLlamaCpp` registration tests.
- [x] 2.2 Add tests asserting the port flows into `MlxRuntimeOptions.Port`: the convenience overload defaults to `8666`, an explicit `port:` argument overrides it, and the `UseMlx(options)` overload preserves `options.Port`/`options.ModelId` unchanged.
- [x] 2.3 Add a test asserting the keyed verbs are unaffected — `AddMlxFirstline`/`AddMlxEscalation` still register keyed `MlxProviderExtension` singletons and do NOT register an active-provider candidate.

## 3. Gates

- [x] 3.1 `make build` clean (TreatWarningsAsErrors), `env -u MEKO_API_KEY make test` green, `openspec validate mlx-single-provider-verb --strict` passes.

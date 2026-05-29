## 1. Loader validation

- [ ] 1.1 In `ProviderConfigLoader.Load`, reject an empty/whitespace provider section key with `InvalidOperationException` naming the offending entry.
- [ ] 1.2 Reject a purely-numeric provider section key (`^\d+$` — the YAML-sequence signature) with `InvalidOperationException` that names the numeric key and states `providers` must be a map keyed by provider name.
- [ ] 1.3 Ensure the validation order per entry is: empty/whitespace key → numeric key → missing `adapter`; keep the existing missing-`adapter` throw and make its message point at the canonical map schema.
- [ ] 1.4 Make the rejection messages actionable: name the failing entry and embed the canonical `providers:` map-form snippet (matching `BootstrapService`'s template / `docs/configuration.md`).

## 2. Tests

- [ ] 2.1 Add a test: numeric-keyed providers (`providers:0:adapter`, `providers:1:adapter`, …, the binder's sequence representation) cause `Load` to throw `InvalidOperationException`.
- [ ] 2.2 Add a test: an empty/whitespace provider key causes `Load` to throw `InvalidOperationException`.
- [ ] 2.3 Add a test: a map-keyed provider with no `auth` block loads with `Auth.Type == "none"` and `Auth.EnvVar == null`.
- [ ] 2.4 Confirm the existing happy-path / multi-provider / `baseUrl` / missing-`adapter` tests still pass unchanged.

## 3. Gates

- [ ] 3.1 `make build` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 3.2 `make test` green (new and existing tests).
- [ ] 3.3 `openspec validate provider-config-validation --strict` passes.

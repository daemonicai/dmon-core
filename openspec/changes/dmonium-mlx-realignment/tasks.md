## 1. dmonium settings surface (SettingsView.swift)

- [x] 1.1 Remove the E2B and Reasoner settings fields and their state (`e2bURL`, `reasonerURL`, `e2bModel`, `reasonerModel`) plus their `TextField`/`help` UI, and the load (`DMON_E2B_URL`/`DMON_REASONER_URL`/`DMON_E2B_MODEL`/`DMON_REASONER_MODEL` reads) and save (writes of those keys) wiring. Keep the egress model (`DMON_EGRESS_MODEL`), egress threshold (`DMON_EGRESS_THRESHOLD`), Gemini key, Dmail/Calendar fields, sync interval, and the Dcal/Dmail server-path fields.

## 2. dmonium health surface (DaemonController.swift)

- [x] 2.1 Remove the `e2bProbe` ("E2B Endpoint", `DMON_E2B_URL`) and `reasonerProbe` ("Reasoner Endpoint", `DMON_REASONER_URL`) endpoint health probes, their `.start()` calls, and their `healthRegistry.register(...)` registrations. Keep the egress endpoint probe and all other components (Dcal/Dmail/Tailscale/calendar-sync/Network/Mail).
- [x] 2.2 Renumber the remaining `healthRegistry.register(order:)` indices to close the gap left by the two removed rows, and update the ordering comment to match.

## 3. dmonium tests (ConfigStoreTests.swift)

- [x] 3.1 Drop the assertions and fixtures that reference `DMON_E2B_URL`/`DMON_REASONER_URL`/`DMON_E2B_MODEL`/`DMON_REASONER_MODEL`; keep config round-trip coverage for the retained keys (egress model/threshold, server paths). Add/adjust a test asserting the removed keys are no longer surfaced by the settings model if such a test seam exists.

## 4. Daemon docs

- [x] 4.1 Add a prominent historical/superseded banner to the top of `daemon/BRIEF.md` noting its `Tier`/`Reasoner`/`AddReasoner`/Ollama design was superseded by ADR-032 (think_harder self-escalation) and ADR-034 (mlx runtime), and pointing to the current standing specs (`triage-routing`, `daemon-composition-root`, `mlx-provider`). Leave the body intact.
- [x] 4.2 Update `daemon/Daemon.App/README.md` health/settings prose to drop the dead `DMON_E2B_URL`/`DMON_REASONER_URL` endpoint names and the "three `DMON_*_MODEL` IDs" claim (only `DMON_EGRESS_MODEL` remains); reflect that dmonium no longer probes local inference endpoints.

## 5. Gates

- [x] 5.1 `make daemon-app` builds the Swift app and `DaemonAppTests` are green (the Swift toolchain is required; if unavailable this gate is human-verified, not skipped).
- [x] 5.2 `openspec validate dmonium-mlx-realignment --strict` passes; `make build` clean and `env -u MEKO_API_KEY make test` green (the .NET side is unaffected — confirm no regression).

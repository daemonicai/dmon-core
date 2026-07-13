# DEVLOG — release-trusted-publishing

## NEXT
Groups 1–2 are complete and committed. Only **Group 3 (maintainer, human-in-the-loop live publish)** remains — it is NOT worker-implementable and stays unticked until the maintainer confirms real nuget.org receipts:
- **3.1** set `NUGET_USER` repo secret (nuget.org profile name); confirm the Trusted Publishing policy (Owner `daemonicai`, Repo `dmon-core`, Workflow File `release.yml`, **Environment blank**) and that the policy owner owns the `Dmon.*` IDs.
- **3.2** smoke: push `core/protocol-v0.2.0`, confirm OIDC login + `Dmon.Protocol 0.2.0` on nuget.org.
- **3.3** full wave: `make release-wave VERSION=0.2 PUSH=1`, confirm all 18 at `0.2.0`.
Caveat: private-repo Trusted Publishing policies have a 7-day pending-activation window before the first publish is allowed.

---

## Group 1–2 — keyless OIDC publishing (tasks 1.1–1.4, 2.1–2.2)

**Block:** one commit; single-file YAML edit to `.github/workflows/release.yml`. Groups 1 and 2 cut as one block (Group 2 only verifies the Group 1 edit; splitting would leave 2.x a no-op verification of an unshipped change).

**What changed:** the `release` job swapped from a long-lived `secrets.NUGET_API_KEY` to keyless nuget.org Trusted Publishing (OIDC):
- Job-level `permissions: { contents: read, id-token: write }` added to the `release` job only (top-level stays `contents: read`; `app-artifact` job untouched).
- `NuGet/login@v1` step (`id: login`, `user: ${{ secrets.NUGET_USER }}`) inserted immediately after pack, immediately before the push (so the 1-hour temp key can't expire — design D1).
- Both `*.nupkg`/`*.snupkg` pushes use `--api-key ${{ steps.login.outputs.NUGET_API_KEY }}`; the `env: NUGET_API_KEY` block removed.
- Header comment documents the keyless path + the maintainer-side policy invariant (Environment **blank** — required because the job declares no `environment:`, design D3).

**Decisions carried:** no GitHub Environment/approval gate (maintainer decision); `NUGET_USER` = nuget.org profile name not email (design D2); spec delta confined to the "Tag-driven release pipeline" requirement.

**Gates:** YAML parses; `grep NUGET_API_KEY` shows no `secrets.NUGET_API_KEY` (only the permitted `steps.login.outputs.NUGET_API_KEY`); `dotnet pack core/Dmon.Protocol` → `Dmon.Protocol.0.2.0-alpha.0.*.nupkg` + `.snupkg`; `make release-wave VERSION=0.2` dry-run emits the 18-tag set with no push; `openspec validate --strict` valid. `make build`/`make test` add no signal (YAML-only, no C#).

**Review:** reviewer signed off — no blockers, no nits. `app-artifact` job confirmed byte-for-byte unchanged.

**Non-blocking notes (already in design.md, not for this change):** `NuGet/login@v1` pinned to a moving major tag not a SHA (supply-chain hardening follow-up); the `Dmon.` ID prefix is unreserved on nuget.org (squattable until reserved).

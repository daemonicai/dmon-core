# DEVLOG — release-trusted-publishing

## NEXT
**ALL TASKS DONE (11/11).** All 18 NuGet-family packages published to nuget.org at `0.2.0` via keyless OIDC on 2026-07-13. Ready for `/opsx:archive` (which syncs the `package-publishing` delta into the standing spec).

## Group 3 — live publish (DONE 2026-07-13)
- **3.1** prereqs confirmed working (the successful OIDC exchange proves `NUGET_USER=emmzrendle` + the policy are correct). nuget.org **org = `daemonic`** (policy Package Owner; packages are org-owned); GitHub org = `daemonicai` (policy Repository Owner) — **different names, don't cross them.** Environment blank. Login `user:` is the INDIVIDUAL nuget.org username (`emmzrendle`), not the org — per the NuGet/login docs.
- **3.2** smoke `core/protocol-v0.2.0` — after the 1.5 LFS fix, the re-run went green: OIDC login ✓, push ✓, `Dmon.Protocol 0.2.0` live (page HTTP 200).
- **3.3** full wave — published the other 17. All 18 verified live at `0.2.0` (flat-container/search). `frontends/dmon` publishes as PackageId **`dmon`** (the tool), not `Dmon.Terminal`.

## Task 1.6 — release-wave.sh batching (surfaced by 3.3)
`make release-wave VERSION=0.2 PUSH=1` created + pushed all 18 tags but **fired ZERO release runs**: `release-wave.sh` pushed all 18 in a single `git push`, and **GitHub triggers no workflow run when >3 tags are pushed at once**. Workaround this session: delete the tags, re-push in batches of ≤3 (6 pushes) — then all 17 (+ already-live protocol) triggered and published. Fix (1.6): the script now pushes in batches of ≤3 (`for ((i=0;i<${#TAGS[@]};i+=3)); git push origin "${TAGS[@]:i:3}"`). Reviewer signed off. LESSON for next cycle: the bulk-release path now works, but never push >3 tags in one `git push`.

## Task 1.5 — LFS checkout gap (surfaced by the 3.2 smoke, 2026-07-13)

The first smoke tag (`core/protocol-v0.2.0`) triggered the release run, which **failed at `Run tests`** — not the OIDC path (Build passed; login/push were skipped, nothing published). 3 `Dmail.Tests.ApiKeyAuthIntegrationTests` failed with `OnnxRuntimeException: InvalidProtobuf`. Root cause: `release.yml`'s Checkout lacked `lfs: true`, so the Git-LFS-tracked Dmail ONNX model was a pointer stub. `ci.yml` already had `lfs: true` (the `services-security-lockdown` L1 fix) but it was never mirrored into `release.yml`; the gap was latent because no tag had ever fired the release job, and #89's merge CI skipped the .NET suite (path-filtered workflow/openspec-only change). Fix: added `lfs: true` to the release job's Checkout via hotfix branch `fix/release-checkout-lfs`. After merge, re-tag `core/protocol-v0.2.0` at the fixed commit and re-run the smoke. Lesson: the release job runs the FULL `make test` unconditionally — any test needing LFS assets or live services must be satisfiable in that job's environment.

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

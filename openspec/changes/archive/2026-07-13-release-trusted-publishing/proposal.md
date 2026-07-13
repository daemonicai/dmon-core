## Why

The NuGet-family release pipeline (`.github/workflows/release.yml`) publishes with a long-lived `NUGET_API_KEY` repo secret — the last standing long-lived publishing credential, whose leak would let an attacker push any `Dmon.*` package. nuget.org now supports keyless **Trusted Publishing (OIDC)**, and the maintainer has enabled a trusted-publishing policy for `daemonicai/dmon-core` (workflow file `release.yml`). This change swaps the release job onto that keyless path and then performs the **first real publish** — closing `release-matrix` task 6.3, which was deferred at archive time specifically to route the first receipt through OIDC rather than a long-lived key.

## What Changes

- **Release job → keyless OIDC.** In `release.yml`, the `release` job (NuGet family, `ubuntu-latest`):
  - Adds `id-token: write` to its permissions (alongside `contents: read`) so GitHub issues an OIDC token.
  - Adds a `NuGet/login@v1` step (`id: login`, `user: ${{ secrets.NUGET_USER }}` — the nuget.org **profile name**, not email) placed immediately before the push, so the 1-hour temp key is fresh.
  - Pushes both `*.nupkg` and `*.snupkg` with `--api-key ${{ steps.login.outputs.NUGET_API_KEY }}`.
  - **Removes** the `NUGET_API_KEY` env/secret reference. **BREAKING** (publishing config): the `NUGET_API_KEY` secret is no longer used; the repo instead needs a `NUGET_USER` value and the nuget.org policy.
- **No approval gate.** No GitHub Environment / manual-approval step (maintainer decision); the version-tag push remains the sole intentional trigger. The nuget.org policy's Environment field must therefore stay blank to match.
- **`app-artifact` job unchanged.** It publishes to GitHub Releases via the auto-provisioned `GITHUB_TOKEN`, not nuget.org.
- **First live publish (task 6.3, human-run).** A single-package smoke (`core/protocol-v0.2.0`) proves the OIDC handshake and nuget.org receipt end-to-end; the full `0.2.0` cycle wave (all 18 NuGet-family packages) follows. Protocol is at 0.2, so the first cycle marker is `X.Y.0 = 0.2.0` (ADR-024/035). These live steps are maintainer-run, not CI gates.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `package-publishing`: the NuGet-family publish requirement changes from "push with a stored API key" to "mint a short-lived key per run via nuget.org Trusted Publishing (OIDC); no long-lived publishing credential in CI." Per-package tag-driven trigger, tag→project resolution, and protocol-lockstep versioning are unchanged.

## Impact

- **Workflow:** `.github/workflows/release.yml` (`release` job only).
- **Repo config (maintainer, out-of-tree):** add `NUGET_USER` secret; the nuget.org trusted-publishing policy (Repository Owner `daemonicai`, Repository `dmon-core`, Workflow File `release.yml`, Environment blank); the now-unused `NUGET_API_KEY` secret can be deleted.
- **Spec:** `openspec/specs/package-publishing/spec.md` (delta).
- **Not touched:** the `app-artifact` job, tag→project resolution, the cycle-wave script, `ci.yml`.
- **ADRs:** conforms to ADR-011/023/024/035 (per-package tags, protocol-lockstep NuGet family, two release families); the publishing-credential mechanism was previously unspecified, so no ADR is contradicted and none is added.

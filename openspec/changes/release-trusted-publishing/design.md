## Context

`.github/workflows/release.yml` (from the `release-matrix` change, ADR-035) has two jobs: the `release` job publishes the NuGet family to nuget.org on `<area>/<name>-v*` tags using a long-lived `secrets.NUGET_API_KEY`; the `app-artifact` job attaches macOS bundles to GitHub Releases on `app/*-v*` tags using `GITHUB_TOKEN`. No package has been published to nuget.org yet — `release-matrix` task 6.3 (the first live publish) was deferred precisely so the first receipt could go through keyless publishing.

nuget.org Trusted Publishing (OIDC) is now available and the maintainer has enabled a policy for this repo. Trusted Publishing exchanges a GitHub-signed OIDC token for a **1-hour** nuget.org API key at run time (one token → one key), removing the need to store any publishing secret. The policy is **owner-scoped** — it applies to every package the owning account owns, so a single policy covers all 18 NuGet-family IDs, including brand-new ones (nuget.org lets the temp key create new IDs under that account).

## Goals / Non-Goals

**Goals:**
- Replace the long-lived `NUGET_API_KEY` push with keyless OIDC Trusted Publishing in the `release` job.
- Keep every other release behaviour identical (per-package tag resolution, pack, `--skip-duplicate`, `.snupkg` handling, cycle wave).
- Close task 6.3 by performing the first real publish and confirming a nuget.org receipt, via a maintainer-run recipe.

**Non-Goals:**
- No change to the `app-artifact` job, tag→project resolution, the cycle-wave script, or `ci.yml`.
- No GitHub Environment / manual-approval gate (maintainer decision).
- No package signing/notarization, no reserved-ID-prefix automation (out of scope; noted as a follow-up).

## Decisions

**D1 — `NuGet/login@v1` mints the key immediately before push.** Add to the `release` job: `permissions: { contents: read, id-token: write }`, then a step `uses: NuGet/login@v1` with `id: login` and `user: ${{ secrets.NUGET_USER }}`. Both `dotnet nuget push` calls (`.nupkg` and `.snupkg`) use `--api-key ${{ steps.login.outputs.NUGET_API_KEY }}`. The login step sits directly before the push, after `make build`/`make test`/pack, so the 1-hour key never risks expiry (build+test+pack is minutes).

**D2 — `NUGET_USER` is the nuget.org profile name, stored as a secret by convention.** The docs recommend `${{ secrets.NUGET_USER }}` even though the username is not truly secret. It is NOT the email. The maintainer sets this repo secret out-of-tree; it is not committed. The `NUGET_API_KEY` secret is removed from the workflow and may be deleted from the repo.

**D3 — No environment gate; policy Environment must be blank.** The job declares no `environment:`. For the OIDC exchange to succeed, the nuget.org policy's Environment field must be left blank (an environment-scoped policy would reject a job that presents no environment claim). This is a maintainer configuration invariant, recorded here and in the verification recipe.

**D4 — First publish is a single-package smoke, then the full wave.** Task 6.3 closes via a maintainer-run recipe (not a CI gate): push `core/protocol-v0.2.0` alone, confirm the run's OIDC login succeeds and `Dmon.Protocol` `0.2.0` appears on nuget.org, then run `make release-wave VERSION=0.2 PUSH=1` for the remaining 17. Protocol is at `0.2`, so the first cycle marker is `0.2.0` (ADR-024/035). `--skip-duplicate` keeps re-runs safe.

**D5 — Spec delta is confined to "Tag-driven release pipeline".** Only that requirement named the `NUGET_API_KEY` secret; it becomes "mint a short-lived key via Trusted Publishing (OIDC); no long-lived publishing secret," with a new scenario asserting the keyless path. All other `package-publishing` requirements are unchanged.

## Risks / Trade-offs

- **Policy/workflow mismatch fails the first run.** If the policy's Repository Owner/Repository/Workflow File/Environment don't exactly match (`daemonicai` / `dmon-core` / `release.yml` / blank), the exchange 401s. Mitigated by the single-package smoke — it surfaces the mismatch cheaply before the wave, and the failure is at the push step only (pack already succeeded).
- **`NuGet/login@v1` is a third-party-ish action pinned to a moving major tag.** It is NuGet-owned (the vendor's own action); pinning `@v1` follows the official docs. A future hardening could pin to a commit SHA.
- **First publish of a new ID under an unreserved prefix.** No `Dmon` prefix reservation exists yet; the owner-scoped policy still permits creating new IDs, so the first publish succeeds, but the `Dmon.` prefix is squattable until reserved — noted as a follow-up, not blocking.
- **Live steps aren't gate-verifiable.** The OIDC handshake needs real GitHub Actions + nuget.org, so task 6.3 stays a human-in-the-loop step with a copy-pasteable recipe; automated gates only prove the YAML parses and pack succeeds.

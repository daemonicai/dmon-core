## 1. Switch the release job to Trusted Publishing (OIDC)

- [x] 1.1 In `.github/workflows/release.yml`, add `id-token: write` to the `release` job's `permissions` block (alongside `contents: read`). Leave the top-level `permissions: contents: read` and the `app-artifact` job's `permissions: contents: write` untouched.
- [x] 1.2 Insert a `NuGet/login@v1` step (`id: login`) immediately before the "Push to nuget.org" step, with `user: ${{ secrets.NUGET_USER }}` (nuget.org profile name, not email).
- [x] 1.3 Change both `dotnet nuget push` calls (the `*.nupkg` push and the conditional `*.snupkg` push) to authenticate with `--api-key ${{ steps.login.outputs.NUGET_API_KEY }}`, and remove the `env: NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}` block from that step. No other step changes.
- [x] 1.4 Update the workflow's header comment to describe the keyless OIDC path (replacing the `secrets.NUGET_API_KEY` mention) and note the policy invariant (Repository Owner `daemonicai`, Repository `dmon-core`, Workflow File `release.yml`, Environment blank).
- [x] 1.5 Add `lfs: true` to the `release` job's Checkout so `make test` (the Dmail app-boot ONNX integration tests) can load the Git-LFS-tracked model; without it the model is a pointer stub and OnnxRuntime throws `InvalidProtobuf`. Mirrors the `ci.yml` L1 fix. Surfaced by the 3.2 smoke (pre-existing `release.yml` gap, never hit because no tag had fired the release job before).
- [x] 1.6 Fix `scripts/release-wave.sh` to push tags in batches of at most 3 (GitHub does not trigger any workflow run when more than 3 tags are pushed in a single `git push`, so the single bulk push at line ~115 silently fired zero release runs during the 3.3 wave). Preserve dry-run behaviour and the skew guard; the tag-creation loop is unchanged. Surfaced by the 3.3 wave.

## 2. Verify the change (automated gates)

- [x] 2.1 Confirm the workflow YAML parses and the `release` job still resolves a tag to exactly one project (re-run the task-6.2-style local checks: `dotnet pack` on a mapped project succeeds; the wave script emits the correct tag set). No secret is exercised.
- [x] 2.2 `openspec validate release-trusted-publishing --strict` passes.

## 3. First live publish — close task 6.3 (maintainer, human-in-the-loop)

- [x] 3.1 Maintainer prerequisite check (record confirmation in DEVLOG): the `NUGET_USER` repo secret is set to the nuget.org profile name; the nuget.org trusted-publishing policy exists with Repository Owner `daemonicai`, Repository `dmon-core`, Workflow File `release.yml`, and a **blank** Environment; the policy owner (individual vs `daemonicai` org) owns the `Dmon.*` IDs.
- [x] 3.2 Single-package smoke: push `core/protocol-v0.2.0`, watch the `release` run, and confirm the `NuGet/login` step succeeded (OIDC exchange) and `Dmon.Protocol` `0.2.0` is live on nuget.org (`dotnet package search Dmon.Protocol --exact-match`, allowing index lag; or the package page). Provide the exact command sequence in DEVLOG.
- [x] 3.3 Full cycle wave: run `make release-wave VERSION=0.2 PUSH=1` to tag and publish the remaining 17 NuGet-family packages at `0.2.0`; confirm all appear on nuget.org (`--skip-duplicate` makes re-runs safe). Tick only after the receipt is confirmed.

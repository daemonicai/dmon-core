# DEVLOG — release-matrix

Cross-block memory for the architect. Newest block last.

## Pinned facts (read every block)

- **Change scope:** implement ADR-035 (all decisions) — build the full granular release matrix. Lands after `ci-hardening` (merged; created the shared area map) and after ADR-035 (accepted).
- **Hard constraint:** this change must **NOT touch `.github/workflows/ci.yml`** (proposal). It rewrites `.github/workflows/release.yml` and adds project/Makefile metadata.
- **Shared area map** already exists at `.github/area-map.yml` (from `ci-hardening`); it is **consumed** by `release.yml` in Section 3, not edited here (task 2.1).
- **`ProtocolVersion.Current == 0.2`**; `MinVerMinimumMajorMinor == 0.2`. Do not change these. Untagged local packs → MinVer prerelease `0.2.0-alpha.0.N`.
- **Skew-guard** = target `CheckProtocolVersionSkew` in root `Directory.Build.props` (`AfterTargets="MinVer"`, condition `IsPackable==true and IsProtocolKeyedPackage != 'false'`). Already present and covers all packable projects.
- **Human-in-the-loop tasks (stop-and-ask when reached):** 6.3 (push real tags to nuget.org + `app/*` to a GitHub Release — needs `NUGET_API_KEY` + maintainer). Produce the copy-paste recipe, then WAIT for maintainer confirmation before ticking.
- **Deferred scope (ADR-035 Open Questions):** app-artifact signing/notarization (5.1 ships dmonium unsigned+labelled); Desktop `dotnet publish` bundle (5.2 — defer to follow-up if non-trivial, log it); `services/Dcal`/`Dmail` server deploy story.

## The canonical `MinVerTagPrefix` map (set in Block 1 — Section 3 consumes these)

Every NuGet-family packable project → its exact tag prefix. **No prefix is a string-prefix of another** (Section 3 tag→project resolution depends on this):

| Project | PackageId | Prefix |
|---|---|---|
| core/Dmon.Protocol | Dmon.Protocol | `core/protocol-v` |
| core/Dmon.Abstractions | Dmon.Abstractions | `core/abstractions-v` |
| core/Dmon.Core | dmoncore | `core/dmoncore-v` |
| providers/Dmon.Providers.Anthropic | … | `providers/anthropic-v` |
| providers/Dmon.Providers.OpenAI | … | `providers/openai-v` |
| providers/Dmon.Providers.Gemini | … | `providers/gemini-v` |
| providers/Dmon.Providers.Ollama | … | `providers/ollama-v` |
| providers/Dmon.Providers.LlamaCpp | … | `providers/llamacpp-v` |
| providers/Dmon.Providers.Mtplx | … | `providers/mtplx-v` |
| providers/Dmon.Providers.Mlx | … | `providers/mlx-v` |
| tools/Dmon.Tools.Builtin | … | `tools/builtin-v` |
| tools/Dmon.Tools.WebSearch | … | `tools/websearch-v` |
| tools/Dmon.Tools.Dcal | … | `tools/dcal-v` |
| tools/Dmon.Tools.Dmail | … | `tools/dmail-v` |
| memory/Dmon.Memory | Dmon.Memory | `memory/memory-v` |
| memory/Dmon.Memory.Meko | Dmon.Memory.Meko | `memory/meko-v` |
| frontends/Dmon.Terminal | (dmon tool) | `frontends/dmon-v` |
| frontends/Dmon.Network | Dmon.Network (ndmon tool) | `frontends/ndmon-v` |

`samples/Dmon.SampleExtension` (`sample-`) is **NOT** NuGet-family (ADR-035 D7 "not published") — excluded, version-injected via `pack-core.sh -p:MinVerVersionOverride`. Reviewer flagged: confirm in a later change whether the sample stays deliberately excluded from the D7 scheme (non-blocking).

## Block 1 — tasks 1.1–1.4 (DONE, committed)

Version-metadata prerequisites (ADR-035 D4/D5/D7).

- **1.1** `memory/Dmon.Memory/Dmon.Memory.csproj` made packable: `IsPackable=true`, `PackageId=Dmon.Memory`, `MinVerTagPrefix=memory/memory-v`, one-line `Description`. Mirrors `Dmon.Memory.Meko`; central metadata inherited from root props (no duplication); no `PackageReadmeFile` (packs fine without).
- **1.2** Skew-guard verify-only — root `Directory.Build.props` **unchanged** (correct outcome). Guard already covers the newly-packable `Dmon.Memory` and un-exempted `Dmon.Network`.
- **1.3** All 18 NuGet-family `.csproj` set to the D7 prefixes above. **`Dmon.Network` un-exempted (D4):** removed `IsProtocolKeyedPackage=false` + `MinVerVersionOverride=0.1.0`, added `frontends/ndmon-v`, rewrote stale "exempt/independently-versioned" comments to the ADR-035 D4 framing (NuGet-family dotnet tool on the protocol lockstep). Decision: full per-project prefix can't be factored into per-area props (each `<name>` differs) → set per-`.csproj`, no per-area props added.
- **1.4** `make network`: `--version 0.1.0` → `--prerelease` (untagged pack is now a MinVer prerelease; `dotnet tool install` skips prereleases without the flag).
- **Gates:** `make build` clean (0 warn); `env -u MEKO_API_KEY make test` all 20 assemblies pass; `openspec validate release-matrix --strict` valid; `make network` live-verified installing `Dmon.Network 0.2.0-alpha.0.259`.
- **Reviewer:** SIGN-OFF, no nits.
- **Note:** the `coreVersion:"0.1.0"` strings in `test/Dmon.Network.Tests` / `test/Dmon.Runtime.Tests` are **agentReady handshake fixtures, NOT package versions** — left untouched (do not "fix" them in later blocks).

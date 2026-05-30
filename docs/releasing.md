# Releasing dmon

This document covers the three independent release lines, how to cut a release, the protocol-keying rule, the compatibility contract, and the one-time manual steps needed before the first real nuget.org push.

See [ADR-011](adrs/ADR-011-distribution-model.md) for the full rationale.

---

## Release lines and tag prefixes

| Line | Tag prefix | Package(s) published |
|------|-----------|----------------------|
| SDK  | `sdk-`    | `Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions` |
| Host | `dmon-`   | `Dmon.Terminal` (the `dmon` global tool) |
| Core | `core-`   | `dmoncore` (the runnable agent core) |

Each line has its own version counter and can be released independently. The `release.yml` workflow selects which projects to pack based on the pushed tag's prefix and pushes only those packages to nuget.org. PR CI (`ci.yml`) never publishes.

---

## Cutting a release

### SDK line

```bash
# tag the commit you want to release
git tag sdk-0.1.0
git push origin sdk-0.1.0
```

This publishes `Dmon.Protocol`, `Dmon.Abstractions`, and `Dmon.Extensions` to nuget.org.

### dmon host

```bash
git tag dmon-0.1.0
git push origin dmon-0.1.0
```

This publishes the `Dmon.Terminal` NuGet global tool (`dmon`).

### dmoncore

```bash
git tag core-0.1.0
git push origin core-0.1.0
```

This publishes the `dmoncore` runnable-closure package.

The workflow runs `make build` and `make test` before packing; a broken tag never results in a publish.

---

## Protocol-keyed versioning

Versions follow `Major.Minor.Patch`:

- `Major.Minor` equals the wire-protocol contract version (first protocol: `0.1`).
- `Patch` is each component's independent release counter.

The single source of truth is `Dmon.Protocol.ProtocolVersion.Current` (currently `"0.1"`). An MSBuild target (`CheckProtocolVersionSkew` in `Directory.Build.props`) runs on every `dotnet pack` and fails if the packed version's `Major.Minor` differs from `ProtocolVersion.Current`. This means a mis-keyed tag — e.g. pushing `core-0.2.0` while the protocol constant is still `"0.1"` — causes the pack step to fail before anything is pushed.

### Bumping the protocol

1. Edit `src/Dmon.Protocol/ProtocolVersion.cs` — set `Current` to the new value (e.g. `"0.2"`).
2. Update the host's compatibility gate in `Dmon.Runtime` if the handshake logic changes.
3. Tag all three lines with the new `Major.Minor`:

```bash
git tag sdk-0.2.0
git tag dmon-0.2.0
git tag core-0.2.0
git push origin sdk-0.2.0 dmon-0.2.0 core-0.2.0
```

Each push triggers its respective release job independently; the skew guard ensures all three move to the same `Major.Minor`.

---

## Compatibility contract

`dmon 0.1.*` acquires the newest `dmoncore` whose `Major.Minor` is `0.1` (i.e. the range `[0.1.0, 0.2.0)`).

**Never unlist old `dmoncore 0.1.*` versions on nuget.org.** If a `dmon 0.1.x` host fetches on first run and the only available `dmoncore 0.1.*` version has been unlisted, the host cannot start. Unlisting stale patch versions of the core strands any host that has not yet cached a compatible version.

Unlisting earlier `Dmon.Protocol`/`Dmon.Abstractions`/`Dmon.Extensions` patch versions is safe — those are library references pinned by consuming projects.

---

## One-time manual steps (required before the first nuget.org push)

These steps involve nuget.org and GitHub and cannot be automated from within the repository. Perform them once before pushing the first release tag.

### Step 1 — Reserve the `Dmon.*` package ID prefix on nuget.org

1. Sign in to [nuget.org](https://www.nuget.org) with the `daemonicai` account.
2. Go to **Organization → Manage Package ID Prefixes** (URL: `https://www.nuget.org/organization/daemonicai/packages`; for a personal account the equivalent path is `https://www.nuget.org/account/packages`).
3. Click **Add prefix** and submit `Dmon.` — this reserves all IDs matching that glob.

The five published IDs to cover:

| Package ID       | Covered by `Dmon.*` glob? |
|------------------|---------------------------|
| `Dmon.Protocol`  | yes |
| `Dmon.Abstractions` | yes |
| `Dmon.Extensions` | yes |
| `Dmon.Terminal`  | yes |
| `dmoncore`       | **no** — lowercase, does not match `Dmon.*` |

Because `dmoncore` is lowercase and the glob `Dmon.*` is case-sensitive on nuget.org, it must be handled separately. Two options:

- **Option A (recommended):** push `dmoncore` manually the first time via `dotnet nuget push` — nuget.org then associates the ID with your account, and no prefix reservation is needed for a single-ID package.
- **Option B:** also reserve the exact prefix `dmoncore` (no wildcard) under Account → Package ID Prefixes.

### Step 2 — `NUGET_API_KEY` secret (already configured)

`NUGET_API_KEY` is **already provisioned as an organization secret on the `daemonicai` GitHub org**, so there is normally nothing to create here. The `release.yml` workflow reads it as `secrets.NUGET_API_KEY` (org secrets resolve the same way as repository secrets); it is never echoed in logs.

The only thing to confirm: the org secret's **repository access list must include this repo** (`daemonicai/dmon-core`) — otherwise the workflow sees an empty value and the push step fails. Org → Settings → Secrets and variables → Actions → `NUGET_API_KEY` → Repository access.

If the key ever needs rotating, regenerate it on nuget.org (**Account → API Keys**, scope **Push new packages and package versions**, packages glob `Dmon.*` **plus** `dmoncore` since that id does not match the glob — or `*` restricted by owner; set an expiry) and update the org secret value.

### Step 3 — Verify with a dry run (optional but recommended)

Before pushing a real tag you can smoke-test packing locally against a local feed:

```bash
# pack everything locally
dotnet pack src/Dmon.Protocol/Dmon.Protocol.csproj  -c Release -o /tmp/dmon-local
dotnet pack src/Dmon.Abstractions/Dmon.Abstractions.csproj -c Release -o /tmp/dmon-local
dotnet pack src/Dmon.Extensions/Dmon.Extensions.csproj -c Release -o /tmp/dmon-local
dotnet pack src/Dmon.Terminal/Dmon.Terminal.csproj  -c Release -o /tmp/dmon-local
dotnet pack src/Dmon.Core/Dmon.Core.csproj         -c Release -o /tmp/dmon-local

# inspect the packages
ls /tmp/dmon-local

# push to a local NuGet feed for testing
mkdir -p /tmp/dmon-feed
dotnet nuget push "/tmp/dmon-local/*.nupkg" --source /tmp/dmon-feed
dotnet tool install dmon --global --version <version> --add-source /tmp/dmon-feed
```

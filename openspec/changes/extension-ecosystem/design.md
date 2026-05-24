# Design: Extension Ecosystem

## Tool surface

Three built-in tools, registered as part of the daemon's built-in extension set:

```
extension.search  <query>         → ranked shortlist (≤5), readme_available flag per result
extension.readme  <package-id>    → README excerpt (~500 chars), requires gh
extension.load    <package-id>    → source fetch → analysis → report → confirm → install
```

## extension.search

### Inputs
- `query` (string) — free-text search, passed to NuGet search API

### Pipeline

1. Query NuGet search API: `q={query}&tags=dmon-extension&prerelease=false`
2. Filter: source-available packages only. The NuGet search API does not expose source availability; the catalog entry `repository` field is unreliably empty. Source availability is determined by fetching the `.nupkg` for each candidate result and parsing the `<repository url="..." commit="...">` element from the nuspec. A non-empty `url` attribute indicates source is available. This requires one `.nupkg` download per candidate (acceptable: search is an agent-initiated call, not a hot path). Packages with no `<repository>` element are excluded.
3. Enrich with GitHub signals if `gh` is available:
   - Extract `<repository url>` from nuspec
   - If `github.com`: `gh api /repos/{owner}/{repo} --jq '{stars: .stargazers_count, pushed: .pushed_at, archived: .archived}'`
   - Archived repos: excluded from results
   - Non-GitHub repos: no star/activity signal, neutral recency score
4. Rank using composite score:
   ```
   recency_score:
     pushed < 7 days   → 1.0
     pushed < 30 days  → 0.8
     pushed < 90 days  → 0.6
     pushed < 365 days → 0.3
     pushed > 365 days → 0.1
     no data           → 0.5

   score = log10(downloads + 1) * 0.5
         + log10(stars + 1)     * 0.3   (0 if no gh data)
         + recency_score        * 0.2
   ```
5. Return top 5, formatted for LLM consumption

### Output shape (to LLM)

```
Found N extensions matching "{query}":

1. {Id} v{Version} — "{Description}"
   📦 {downloads} downloads  ★ {stars}  🟢/🟡/🔴 {activity label}
   readme_available: true/false

...
```

### readme_available flag

`true` iff `gh` is active AND the package has a `<repository url="github.com/...">` in its nuspec. The flag tells the agent it can call `extension.readme` for more detail.

## extension.readme

### Inputs
- `package-id` (string) — as returned by `extension.search`

### Behaviour

Requires `gh` to be available. If not, returns an error explaining why.

1. Look up the cached NuGet metadata for `package-id` from the most recent search (or re-fetch if not cached)
2. Extract `<repository url>` → derive `{owner}/{repo}`
3. `gh api /repos/{owner}/{repo}/readme --jq '.content' | base64 -d`
4. Strip leading badge lines (lines starting with `[![`) and blank lines until substantive content
5. Return first ~500 characters of cleaned text

### Output

Plain text excerpt. The agent uses this to disambiguate between similar-looking extensions before calling `extension.load`.

## extension.load

### Inputs
- `package-id` (string) — package ID, optionally `@version` suffix (defaults to latest stable)

### Pipeline

#### Stage 1: Source fetch

> **Spike result (2026-05-24):** `.snupkg` files are not available on the NuGet flat container (all 404). The NuGet symbol server requires authentication outside the ADR-005 scope. Even when PDBs are embedded in a `.nupkg`, they contain Source Link URL templates, not source code; parsing them requires a third-party library. The `.snupkg` path is dropped for V1.

Source fetch procedure:
1. Download the `.nupkg` for `{package-id}@{version}`; extract and parse the `.nuspec`.
2. Read `<repository url="..." commit="...">` from the nuspec. If absent or `url` is empty: refuse load with message explaining source requirement.
3. Derive `owner/repo` from the `url` attribute. If the URL is `github.com`:
   - Fetch the repository tree at `{commit}` via `gh api /repos/{owner}/{repo}/git/trees/{commit}?recursive=1`
   - Fetch each `.cs` source file via `https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{path}` (no auth required for public repos; `gh` required for private repos)
4. Non-GitHub repository URLs: fetch source via `gh api` if the host is reachable with the configured `gh` auth; otherwise refuse load with an explanation.
5. If source cannot be fetched: refuse load with message explaining source requirement.

#### Stage 2: LLM security analysis
Pass extracted source to LLM with a security-analysis prompt. Structured output:
```json
{
  "risk_level": "low" | "medium" | "high",
  "findings": [
    { "severity": "info" | "warn" | "risk", "description": "..." }
  ],
  "summary": "..."
}
```

Analysis checks for:
- Filesystem access outside CWD subtree
- Outbound network calls (flagged as `info` for extensions where this is expected, `risk` for unexpected)
- Process spawning
- Dynamic assembly loading / reflection abuse
- Credential or environment variable harvesting
- Obfuscated or generated code that resists inspection

The report notes explicitly: "Analysis covers extension source only — transitive NuGet dependencies are not analysed."

#### Stage 3: Report to user
Display full analysis report before the confirmation prompt. Format:

```
Analysing {package-id} v{version}...

✅ No concerns found — source looks clean.
   [or]
⚠️  1 finding:
   [warn] Makes outbound HTTP calls. Expected for a scraping tool.
   [or]
🚨 2 findings:
   [risk] Reads environment variables matching *_KEY, *_TOKEN patterns.
   [risk] Spawns child processes.

Note: analysis covers extension source only; transitive dependencies are not analysed.
```

#### Stage 4: Confirmation
Standard four-option permission prompt (allow once / allow for project / allow globally / deny), as per ADR-006. "Allow for project" and "allow globally" store `{package-id}@{version}` as approved — a version bump resets approval.

#### Stage 5: Install and load
- Resolve and download the `.nupkg` to `~/.dmon/extensions/{package-id}/{version}/`
- Load assembly via collectible `AssemblyLoadContext`
- Discover `IDaemonExtension` implementations via reflection
- Register all tools in the session tool registry

## gh capability probe

```csharp
// Checked once per session, result cached
bool ghAvailable = await CheckGhAvailableAsync();

static async Task<bool> CheckGhAvailableAsync() {
    // run: gh auth status
    // returns true iff exit code is 0
}
```

Called lazily on first `extension.search` or `extension.readme` invocation.

## Interaction with ADR-006

Extension loading is a new permission tier (added to ADR-006 as part of this change). Key properties:
- Always `risk: high`
- Source fetch and analysis are mandatory and cannot be bypassed by stored approvals
- Stored approvals suppress the confirmation prompt for the same `package@version` only
- A version bump always re-runs analysis and re-prompts

## Relation to existing built-in tools

`extension.search`, `extension.readme`, and `extension.load` are registered as built-in tools alongside the existing file, bash, and web tools. They are part of `Daemon.Core.BuiltinTools` (the subject of the `daemon-builtin-tools` change, now archived).

The `extension.load` tool wraps the existing extension loading infrastructure (ADR-002) with the new security pipeline. The underlying `IExtensionLoader` and `IToolRegistry` interfaces are unchanged.

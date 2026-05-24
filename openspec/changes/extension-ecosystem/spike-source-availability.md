# Spike: Source Availability Detection

**Date:** 2026-05-24  
**Status:** Complete

## Questions

1. Which NuGet API fields indicate source availability without a per-package secondary request?
2. Is `.snupkg` embedded source extraction feasible with available .NET APIs?
3. Is the Source Link fallback path via `gh api` / direct fetch feasible?

---

## Finding 1 — NuGet Search API does not expose source availability

**Method:** `GET https://azuresearch-usnc.nuget.org/query?q=Serilog&prerelease=false&take=1`

The V3 search response includes these fields per package:

```
@id, @type, registration, id, version, description, summary, title,
iconUrl, licenseUrl, projectUrl, tags[], authors[], owners[],
totalDownloads, verified, packageTypes[{name}], versions[{version, downloads, @id}]
```

**No `repository` field is present in the search response.** `packageTypes` only distinguishes `Dependency` from `DotnetTool` etc. — it does not indicate source availability. `projectUrl` is present but may point to a project website, not a source repository.

**Conclusion: Source availability cannot be determined from the search response alone.** A secondary request to the nuspec inside the `.nupkg` is required.

---

## Finding 2 — The `<repository>` element in the nuspec is the reliable signal

**Method:** Download `.nupkg` from `https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg`, extract the `.nuspec` file (a zip entry), parse XML.

Serilog 4.3.1 nuspec contains:

```xml
<repository type="git" url="https://github.com/serilog/serilog"
            branch="refs/heads/main"
            commit="5625030e8f8fceca2e3180575aba174751ef7add" />
```

This element provides:
- `type` — always `"git"` for modern packages
- `url` — the repository root URL (github.com, gitlab.com, etc.)
- `commit` — the exact commit SHA used to build the package

The NuGet catalog entry at `https://api.nuget.org/v3/catalog0/data/.../{id}.{version}.json` contains a `repository` field but it is an **empty string** for Serilog — the catalog does not reliably propagate this value. The nuspec inside the `.nupkg` is authoritative.

**Conclusion: Source availability = presence of a non-empty `<repository url="...">` element in the nuspec. Requires one `.nupkg` download per package inspected. The catalog entry `repository` field is unreliable and should not be used.**

---

## Finding 3 — `.snupkg` is not fetchable without credentials

**Method:** `HEAD https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.snupkg` for Serilog, Polly, Microsoft.Extensions.AI, MinVer, FluentAssertions, Humanizer.

**All return HTTP 404.** The NuGet flat container does not serve `.snupkg` files at the standard path.

The NuGet symbol server at `https://symbols.nuget.org/` returns **HTTP 403** (Forbidden) for unauthenticated access to `.snupkg` files. Symbol server download requires a `SymbolChecksum` header and authentication.

When the 404 URL is fetched as a file, the response body is a 215-byte XML error document — not a zip. Any code that attempts to open this as a zip/stream will fail.

**Conclusion: `.snupkg` extraction is NOT feasible in V1.** The NuGet symbol server requires credentials that are outside the ADR-005 scope (API keys for LLM providers only). This path must be dropped.

---

## Finding 4 — PDBs embedded in `.nupkg` contain Source Link, not source code

**Method:** Polly 8.5.2 includes PDB files directly in its `.nupkg` under `lib/{tfm}/Polly.pdb`. Extracted and inspected with `strings`.

The PDB contains a Source Link JSON document:

```json
{"documents":{"/_/*":"https://raw.githubusercontent.com/App-vNext/Polly/a953ba8119074d7f63cc69643d4ef1d1de52ee3f/*"}}
```

This is a URL template — no source code is embedded in the PDB. The PDB maps source file paths to `raw.githubusercontent.com` URLs. Serilog does not embed PDBs in its `.nupkg` at all (only DLLs and XML docs).

Reading embedded source from PDBs would require a third-party library (e.g. `Microsoft.DiaSymReader`) to parse the Portable PDB format and extract the Source Link document. This is not feasible without a dependency.

**Conclusion: Even when PDBs are present in the `.nupkg`, they do not contain embedded source — only Source Link URL templates. The `.snupkg`/PDB path provides no source code for security analysis.**

---

## Finding 5 — Source Link fetch via raw.githubusercontent.com is feasible without `gh`

**Method:** The Source Link document in the Polly PDB gives:

```
https://raw.githubusercontent.com/App-vNext/Polly/{commit}/{file-path}
```

Fetching `https://raw.githubusercontent.com/App-vNext/Polly/a953ba8119074d7f63cc69643d4ef1d1de52ee3f/src/Polly/Policy.cs` returns the full C# source file directly over HTTPS with no authentication for public repos.

`gh api /repos/{owner}/{repo}/contents/{path}?ref={sha}` also works and returns file metadata + base64 content. Both paths are feasible.

**Conclusion: Source fetch for public GitHub repos requires only:**
1. Extract `<repository url="..." commit="...">` from the nuspec
2. Derive owner/repo from the URL
3. Fetch the repo tree at `{commit}` via `gh api /repos/{owner}/{repo}/git/trees/{commit}?recursive=1`
4. Fetch each source file via `raw.githubusercontent.com/{owner}/{repo}/{commit}/{path}`

Step 3–4 can be done without `gh` for public repos (plain HTTPS). The `gh` CLI is only needed for private repos and for the GitHub signals (stars, push date, archived status).

---

## Finding 6 — GitHub API signals work cleanly

**Method:** `gh api /repos/serilog/serilog --jq '{stars: .stargazers_count, pushed: .pushed_at, archived: .archived}'`

Returns:
```json
{"archived": false, "pushed": "2026-05-11T22:17:56Z", "stars": 7968}
```

`gh api /repos/JamesNK/Newtonsoft.Json` similarly returns `{"archived": false, "pushed": "2026-04-09T04:32:35Z", "stars": 11293}`.

The `--jq` flag produces exactly the fields needed for the ranking formula. `gh auth status` exit code is a reliable probe for `gh` availability.

---

## Summary Table

| Path | Feasible? | Notes |
|------|-----------|-------|
| Source availability from search API | No | No `repository` field in search response |
| Source availability from catalog entry | No | `repository` field is empty string |
| Source availability from nuspec `<repository>` | Yes | Requires one `.nupkg` download per package |
| `.snupkg` download from flat container | No | All 404; symbol server requires auth |
| Embedded source extraction from PDB | No | PDBs contain Source Link URLs, not source; parsing requires third-party lib |
| Source fetch via raw.githubusercontent.com | Yes | Works for public repos, no auth needed |
| Source fetch via `gh api` contents endpoint | Yes | Requires `gh` auth; works for private repos |
| GitHub signals via `gh api /repos/{owner}/{repo}` | Yes | stars, pushed_at, archived — clean output |

---

## Implications for Design

The `.snupkg` → embedded source path described in `design.md` Stage 1 must be replaced. See `design.md` for the updated pipeline.

The source availability check in `extension.search` requires fetching and parsing the nuspec. This adds one HTTP round-trip per result candidate beyond the initial search response. Given that `extension.search` is already an agent-initiated tool call (not a hot path), this is acceptable.

The source fetch for `extension.load` uses the nuspec `<repository url commit>` fields to reconstruct Source Link-style URLs directly, without requiring the `gh` CLI for public GitHub repos.

# ADR-006: Permission Model

**Date:** 2026-05-22
**Status:** Accepted

## Context

daemon needs a conservative permission model — closer to Claude Code than to Pi, which has minimal guardrails. The model must be safe by default, low-friction for routine operations, and configurable without ever allowing truly dangerous operations to bypass safeguards.

## Decision

### Permission tiers

#### Read

| Scope | Default |
|-------|---------|
| CWD subtree at invocation | Implicitly allowed — no prompt |
| Any path outside CWD subtree | Prompt required |

Path grants are **tree-based**: approving `/Users/rendle/foo` implicitly allows all reads under `/Users/rendle/foo/`. Paths are always normalised (symlinks resolved, `..` collapsed) before checking.

#### Write / Edit / Delete

No implicit allows anywhere, including within CWD.

Grants are tree-based: approving a path for write/edit/delete covers the entire subtree. Each novel path root requires a prompt.

#### Bash

Two categories, handled differently:

**Simple commands** — no pipes (`|`), semicolons (`;`), `&&`, `||`, command substitution (`$(...)` or `` ` ``), or process substitution:
- Stored approvals use glob patterns (e.g., `git *`, `dotnet build*`)
- When prompting, the UI suggests a reasonable pattern alongside the exact command string; the user chooses which to approve
- Stored approvals are checked against the command string before prompting

**Composite commands** — anything that is not a single simple command per the Bash grammar. Concretely, the command is treated as composite if it contains any of:

- Pipes: `|`, `|&`
- Command separators: `;`, `&&`, `||`, newlines (multi-line scripts), backgrounding `&`
- Command substitution: `$(...)`, backticks `` `...` ``
- Process substitution: `<(...)`, `>(...)`
- Redirects: `>`, `>>`, `<`, `<<` (heredoc), `<<<` (here-string), `2>`, `&>`, `>&`
- Subshells / groups: `( ... )`, `{ ... ; }`
- Inline environment assignments preceding the command (`FOO=bar cmd`)

The classifier runs on the *raw* string the agent submits — not on a shell-expanded form. If parsing is ambiguous, the command is treated as composite (fail safe).
- **Always prompt**, regardless of stored approvals
- The entire shell string is the approval unit for "allow once" only
- Composites cannot be stored as persistent approvals — each occurrence requires explicit user confirmation

#### Network / HTTP

HTTP calls from tools require per-domain approval, stored at **project level only** (`.daemon/settings.yaml`). Domain approvals are not user-global: a domain approved in one project is not approved in another. Subdomains require their own approval (`api.example.com` ≠ `example.com`).

### Denylist

A hardcoded denylist of dangerous command patterns is checked **before any approval lookup**. Matches are rejected unconditionally — no approval level can override them. The denylist covers patterns including but not limited to:

- `rm` targeting root or system directories (`/`, `/etc`, `/usr`, `/bin`, etc.)
- Disk format and wipe commands (`mkfs`, `dd if=/dev/zero`, `shred`)
- Commands that disable or bypass security mechanisms (`chmod -R 777 /`, `chattr -i /`)
- Fork bombs and resource exhaustion patterns
- Anything invoking `sudo` or `su` (daemon never runs as root and never elevates)

The denylist is not user-configurable.

### Permission persistence

Every prompt offers four options:

| Option | Scope | Storage |
|--------|-------|---------|
| Allow once | This invocation only | Not stored |
| Allow for project | All sessions in this project | `.daemon/settings.yaml` |
| Allow globally | All projects | `~/.daemon/settings.yaml` |
| Deny | Rejected, reported to agent | Not stored |

Project settings take precedence over global settings. Denying does not store a permanent deny rule — the agent may ask again in a future session.

### Grant precedence

Within a single scope (project or global), the most-specific rule wins:

- Allows and denies coexist in `settings.yaml` under `permissions.<tier>.allow` and `permissions.<tier>.deny`.
- For path-based tiers (read/write/edit/delete), the longest matching path prefix decides. So `allow: /Users/me/work` + `deny: /Users/me/work/secrets` is valid — reads under `secrets/` are blocked while the rest of `work/` is allowed.
- For bash globs, an explicit `deny` pattern beats an `allow` pattern; ties resolve to deny.
- Across scopes, project wins over global (existing rule).

#### Extension loading

Extension loading is a distinct permission tier with a mandatory multi-step gate — not a simple prompt. The gate is a pipeline that cannot be short-circuited by stored approvals:

1. **Source fetch** — mandatory. The source for the exact package version being loaded must be retrievable. The mechanism: the `.nupkg` is downloaded and its `.nuspec` is parsed for a `<repository url="..." commit="...">` element; a non-empty `url` is required. Source files are then fetched at the recorded commit (from `raw.githubusercontent.com` for public GitHub repos without authentication; via the `gh` CLI for private repos). If the nuspec has no `<repository>` element, or if source cannot be fetched, the load is **refused unconditionally** — no approval can override this. The `gh` CLI is not required for public-repo source fetches; it is used only for private repos and for GitHub enrichment signals.

2. **Source analysis** — the daemon's LLM performs a security pass over the extension source, inspecting for suspicious patterns:
   - Filesystem access outside the CWD subtree
   - Outbound network calls (flagged with context — expected for some extension types)
   - Process spawning
   - Reflection abuse (dynamic assembly loading)
   - Credential or environment variable harvesting
   - Obfuscated or machine-generated code that resists inspection

   The analysis produces a structured report: `risk_level` (`low` / `medium` / `high`), a list of findings (empty if clean), and a plain-language summary. The report notes explicitly that it covers the extension's own source only, not transitive NuGet dependencies.

3. **Report to user** — the analysis report is presented in full before any confirmation prompt. The user sees what the analysis found (or that nothing was found) before being asked whether to proceed.

4. **Confirmation** — standard four-option prompt (allow once / allow for project / allow globally / deny), presented after the report.

**Stored approvals for extension versions.** "Allow for project" and "allow globally" suppress the confirmation prompt on subsequent loads of the same `package-id@version`. They do **not** suppress the source fetch and analysis — analysis always runs on the first load of a given version in a given installation. A patch-version bump resets the approval for that version.

**Why source availability is non-negotiable.** The source analysis is the primary safety mechanism for extension loading. An extension whose source cannot be fetched cannot be analysed and cannot be trusted. This is not a configurable behaviour.

Extension loads always carry `risk: high` regardless of stored approvals.

### Risk levels

The `risk` field on `tool.confirmRequest` (ADR-003) is set as follows:

| Risk | Conditions |
|------|-----------|
| `none` | Read within CWD subtree — no prompt issued |
| `low` | Read outside CWD; write/edit/delete to an already-approved path; simple bash already in allowlist |
| `high` | Write/edit/delete to a new path; novel bash command; composite bash command; HTTP call |

Hosts use `risk` to decide how to present the confirmation. The Avalonia host may show a visual diff for `high`-risk file operations (planned for V1.5+).

### Settings file shape

```yaml
# .daemon/settings.yaml (project) or ~/.daemon/settings.yaml (global)
permissions:
  read:
    allow:
      - /Users/rendle/other-project
  write:
    allow:
      - /Users/rendle/other-project
    deny:
      - /Users/rendle/other-project/secrets
  bash:
    allow:
      - "git *"
      - "dotnet build*"
      - "dotnet test*"
      - "ls *"
    deny:
      - "git push --force*"
  http:
    allow:
      - api.github.com
      - registry.npmjs.org
```

## Consequences

- **Safer than Pi by design.** No tool executes silently; every novel operation surfaces to the user.
- **Low friction for routine operations.** Read within CWD requires no interaction. Common bash commands and domains can be pre-approved at project or global level.
- **Composite commands always surface.** Shell composition cannot be used to smuggle approved command fragments into unapproved operations.
- **Denylist is non-negotiable.** Genuinely dangerous operations cannot be approved regardless of user preference or configuration.
- **HTTP permissions are scoped to projects.** Domain approvals don't leak across projects.
- **The permission gate sits in the `IChatClient` middleware pipeline** (ADR-002), before `FunctionInvokingChatClient` dispatches tool calls. All tools — built-in and extension — pass through the same gate.

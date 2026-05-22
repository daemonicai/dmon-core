# ADR-005: Provider Authentication

**Date:** 2026-05-22
**Status:** Accepted

## Context

daemon needs to authenticate with multiple LLM providers, ranging from local servers (no auth) to cloud APIs (API key or OAuth). The brief mentioned OAuth as a possibility; the actual provider landscape was investigated before committing to a design.

### Current provider auth landscape (as of May 2026)

| Provider   | Auth type         | Notes |
|------------|-------------------|-------|
| Anthropic  | API key only      | OAuth for third-party apps was disabled February 20, 2026. Only `sk-ant-api03-*` keys via `ANTHROPIC_API_KEY` work. `ANTHROPIC_AUTH_TOKEN` is a bearer token for proxy/gateway use — mechanically identical to an API key. |
| OpenAI     | API key           | `OPENAI_API_KEY` |
| Gemini     | API key           | `GEMINI_API_KEY` via Google AI Studio. OAuth only relevant for Vertex AI (enterprise path; out of scope for V1). |
| oMLX       | API key           | Set at oMLX startup; required when oMLX is exposed over a network. |
| Ollama     | none / optional   | No auth on localhost. Optional key if exposed over a network. |
| llama.cpp  | none / optional   | No auth on localhost. Optional key if exposed over a network. |

OAuth is not a practical auth mechanism for any V1 provider. It is noted as a stretch goal for Gemini via Vertex AI only.

## Decision

### Auth types

Two auth types are supported:

- **`apiKey`** — an opaque string sent as `x-api-key` or `Authorization: Bearer` depending on the provider's requirement. Covers all cloud providers and network-exposed local providers.
- **`none`** — no auth. Default for locally-bound Ollama and llama.cpp instances.

`ANTHROPIC_AUTH_TOKEN` (bearer token for proxy/gateway scenarios) is handled as `apiKey` with `headerStyle: bearer`. No distinct auth type is needed.

### Provider config shape

```yaml
providers:
  anthropic:
    adapter: anthropic
    auth:
      type: apiKey
      envVar: ANTHROPIC_API_KEY     # checked first; no value here in config
  omlx-local:
    adapter: openai
    baseUrl: http://localhost:1337
    auth:
      type: apiKey
      envVar: OMLX_API_KEY
  ollama:
    adapter: openai
    baseUrl: http://localhost:11434/v1
    auth:
      type: none
```

### Credential resolution order

For each provider, credentials are resolved in this order:

1. **Environment variable** named in `auth.envVar` — CI-friendly, takes precedence
2. **Credentials file** `~/.daemon/credentials/<provider>.json` — set by `/login`
3. **Interactive prompt** via the UI input mechanism (ADR-003 `ui.inputRequest` with `kind: secret`) — stores result in the credentials file. Credential prompts use the dedicated `ui.inputRequest` channel rather than `tool.confirmRequest` (the latter is reserved for tool execution gating).

Credentials are **always user-global** (`~/.daemon/credentials/`). They are never stored in the project-local `.daemon/` directory to prevent accidental inclusion in version control.

### Credentials file format

Each provider has its own file: `~/.daemon/credentials/<provider>.json`. The credentials directory is created with mode `0700`; each file with mode `0600` on POSIX. On Windows, the directory ACL is restricted to the current user.

```json
{
  "provider": "anthropic",
  "type": "apiKey",
  "apiKey": "sk-ant-api03-...",
  "headerStyle": "x-api-key",
  "createdAt": "2026-05-22T10:30:00Z",
  "updatedAt": "2026-05-22T10:30:00Z"
}
```

Fields:
- `provider` — provider name (matches `providers.<name>` key in config)
- `type` — `apiKey` for V1; reserved for `oauth` in future
- `apiKey` — the secret string
- `headerStyle` — `x-api-key` (Anthropic) or `bearer` (OpenAI, Gemini, generic bearer-token endpoints)
- `createdAt` / `updatedAt` — ISO 8601 timestamps

For future OAuth support, additional fields will be defined: `accessToken`, `refreshToken`, `expiresAt`, `scope`. Readers MUST ignore unknown fields.

### `/login` and `/logout` commands

Auth is managed via slash commands in the agent UI, which map to RPC messages (ADR-003):

```
/login <provider>
  Host → Core: auth.login {provider}
  Core checks what's needed:
    - API key → emits ui.inputRequest {id, kind: "secret", prompt: "API key for <provider>"}
                Host collects key, sends ui.inputResponse {id, value}
                Core stores in ~/.daemon/credentials/<provider>.json (mode 0600)
  Core → Host: auth.loginComplete {provider}

/logout <provider>
  Host → Core: auth.logout {provider}
  Core deletes ~/.daemon/credentials/<provider>.json
  Core → Host: auth.logoutComplete {provider}
```

RPC additions to ADR-003:

| Message | Direction | Payload |
|---------|-----------|---------|
| `auth.login` | Host → Core | `provider` |
| `auth.logout` | Host → Core | `provider` |
| `auth.loginComplete` | Core → Host | `provider` |
| `auth.logoutComplete` | Core → Host | `provider` |
| `auth.loginFailed` | Core → Host | `provider`, `error` |
| `auth.status` | Host → Core | — |
| `auth.statusResult` | Core → Host | `providers[]` with auth state |

### OAuth (future)

If Gemini via Vertex AI is added in a future version, the device code flow will be used:

- Core emits `auth.loginPending {provider, url, code, expiresIn}`
- Host displays the code and URL
- Core polls in background
- Core stores access token + refresh token in `~/.daemon/credentials/<provider>.json`
- Core refreshes automatically before expiry

No OAuth implementation is required for V1.

## Consequences

- **Auth model is simple for V1.** All providers use API keys; the implementation is a single credential type with a consistent resolution order.
- **Credentials are never in the project directory.** No risk of accidentally committing API keys.
- **`/login` is interactive and explicit.** Users are never surprised by a credential prompt mid-turn; auth setup is a deliberate act.
- **CI/CD is straightforward.** Set the appropriate environment variable; no interactive flow needed.
- **Anthropic OAuth is explicitly not supported.** This is a policy constraint, not a technical one. If Anthropic re-enables third-party OAuth in future, the device code flow path already designed for Gemini applies.

Sources:
- [Anthropic disabled OAuth tokens for third-party apps · Issue #28091 · anthropics/claude-code](https://github.com/anthropics/claude-code/issues/28091)
- [Authentication - Claude Code Docs](https://code.claude.com/docs/en/authentication)

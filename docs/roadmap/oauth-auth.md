# OAuth Authentication

**Status:** 💭 Idea  
**Depends on:** API key auth (V1) proven; provider OAuth flows investigated

---

## What

OAuth-based authentication for LLM providers that offer it — primarily as a stretch goal for providers like Gemini/Vertex that support OAuth flows, and potentially for Claude/ChatGPT subscription accounts.

## Why

API keys work well for developers who have them. OAuth opens up dmon to users who have a *subscription* to a provider (e.g. Gemini Advanced, ChatGPT Plus) but haven't set up API access. It also enables per-user auth in multi-user or shared-agent scenarios.

## Ideas for what it includes

- **OAuth device flow** — `dmon auth login --provider gemini` opens a browser, user authenticates, token stored securely.
- **Token storage** — OS keychain integration (Windows Credential Manager, macOS Keychain, Secret Service on Linux).
- **Token refresh** — handle expiry and refresh transparently.
- **Provider-specific flows** — Google/Vertex have well-documented OAuth. Anthropic and OpenAI are API-key-only today; watch for changes.
- **Multi-account** — ability to authenticate with multiple accounts per provider and switch between them.

## Notes

- V1 is API keys only (ADR-005). This is explicitly a post-V1 stretch goal.
- `Microsoft.Extensions.AI` may give us some of this for free — check before building.
- Don't design the OAuth flow until the provider landscape is clearer. Provider auth stories change fast.
- Gemini/Vertex is the most likely first candidate given Google's OAuth-first API design.

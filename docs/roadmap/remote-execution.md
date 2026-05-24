# Remote Execution

**Status:** 💭 Idea  
**Depends on:** Stable RPC surface, auth model clarified

---

## What

Running the dmon agent core on a remote machine — a server, a cloud VM, a dev container — while the host UI runs locally. The RPC surface moves from stdio to a network transport.

## Why

Some codebases are too large to work with locally. Some teams want a shared agent instance with a persistent session. Some workflows live entirely in cloud environments where you don't have a local checkout.

## Ideas for what it includes

- **Network transport for RPC** — swap stdio for WebSockets or HTTP/SSE without changing the message protocol (ADR-003 shape stays the same).
- **Session persistence on the remote** — the session directory lives on the remote; local host just renders it.
- **Auth for the RPC surface** — some form of token or key to prevent unauthorised access to the remote agent.
- **Reconnect / resume** — if the local host disconnects, the agent keeps running and the host can reconnect to the same session.
- **Self-hosted server mode** — `dmon serve` starts the agent as a network-accessible service.

## Notes

- The process-isolated architecture is specifically designed to make this possible later without re-architecting. Don't undo that.
- The RPC message shape (ADR-003) must not change to support this — only the transport layer changes.
- Auth is the hard part. API keys for the RPC surface is the obvious starting point.
- Sandboxing becomes more important when the agent is network-accessible.
- Explicitly out of scope for V1.

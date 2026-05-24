# Lifecycle Hooks

**Status:** 💭 Idea  
**Depends on:** Extension model stable, `IDaemonExtension` surface settled

---

## What

A hook system that lets extensions observe and intercept events at key points in the agent lifecycle — session boundaries, turn boundaries, and tool calls.

Two distinct hook types:

- **Observers** — passive subscribers. Receive event data, return nothing. Good for logging, telemetry, notifications, audit trails.
- **Interceptors** — active middleware. Receive event data, can mutate it, and return the (potentially modified) payload before the agent continues. Think ASP.NET middleware, but for agent events.

---

## Event categories

### Session events
- `OnSessionStarted` — a new session is created. Payload: session metadata, CWD, provider.
- `OnSessionResumed` — an existing session is resumed from disk.
- `OnSessionForked` — a session is forked. Payload: parent session ID, new session ID.
- `OnSessionEnded` — session is closing. Payload: session metadata, turn count, duration.

### Turn events
- `OnTurnStarted` — user message received, before the agent processes it. Interceptors can modify or annotate the message.
- `OnTurnCompleted` — agent has produced a response, before it's sent to the host. Interceptors can modify the response.

### Tool events
- `OnToolCalling` — a tool is about to be invoked. Interceptors can cancel, modify arguments, or substitute a different result.
- `OnToolCalled` — a tool has returned its result. Interceptors can modify the result before it's added to context.

---

## Ideas for what it includes

- **Hook registration in `IDaemonExtension`** — extensions declare which hooks they implement; the runtime discovers and wires them.
- **Ordered interceptor pipeline** — interceptors run in a defined order (registration order, or an explicit priority). Each gets the output of the previous.
- **Cancellation support** — interceptors can cancel an operation (e.g. block a tool call) and return an error or alternative result instead.
- **Async throughout** — hooks are `async`; the pipeline is awaitable end-to-end.
- **Error isolation** — a failing observer must not take down the agent. A failing interceptor should fail the operation, not the process.

## Example use cases

- **Logging extension** — observes every turn and tool call, writes a structured audit log.
- **Cost tracker** — observes `OnTurnCompleted`, reads token counts from the response, accumulates and displays session cost.
- **Context injector** — intercepts `OnTurnStarted`, appends project-specific context to every user message automatically.
- **Tool guard** — intercepts `OnToolCalling`, checks tool arguments against a policy, blocks or warns on unsafe calls.
- **Notification extension** — observes `OnSessionEnded`, sends a push notification when a long-running session finishes.

---

## Notes

- The interceptor model is essentially a middleware pipeline — look at how `IMiddleware` works in ASP.NET Core as a reference shape.
- `IChatClient` in M.E.AI already has a middleware/delegation pattern (`DelegatingChatClient`). The tool-call hooks may be expressible as a wrapping `IChatClient` without a separate hook system — worth investigating.
- Observers should be fire-and-forget where possible to avoid adding latency to the hot path.
- Permission implications: interceptors that can modify tool arguments are powerful. Consider whether interceptor extensions need a higher trust level.
- Don't define the full event schema until the agent loop is stable — the right hook points emerge from real usage.

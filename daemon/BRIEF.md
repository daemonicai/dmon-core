# BRIEF: dmon tiered-inference triage router

## Goal

Add tiered local inference to the `dmon` agent. A small, always-warm model
(`gemma4:e2b-it-qat`) triages every user turn and either answers it directly,
handles it with a local tool, or escalates it to a bigger local reasoning model
(`gemma4:26b-a4b-it-qat`). A cloud model (Gemini) is reachable only for
impersonal world-knowledge queries, behind a hard privacy gate.

The whole design is `IChatClient` composition. You are not inventing new
infrastructure; you are adding one router middleware and wiring three backends.

## Existing harness (starting point — do not rebuild)

- .NET 10.0 SDK.
- Microsoft.Extensions.AI 10.x — `IChatClient`, `AIFunction`,
  `ChatClientBuilder`, `UseFunctionInvocation`, `DelegatingChatClient`,
  structured output via `GetResponseAsync<T>`.
- Configuration-as-code in `.cs` files via a fluent host:
  `DmonHost.CreateBuilder().UseGemini().Build().Run();`

Today `.UseGemini()` builds the single terminal `IChatClient`. After this work,
Gemini is **demoted** to one registered, gated backend, and a `TriageRouter`
becomes the terminal client that `.Run()` drives.

## Two orthogonal routing axes

Routing has two independent dimensions. Do not collapse them into one switch.

| | **World scope** (may egress, impersonal only) | **Personal scope** (never leaves device) |
|---|---|---|
| **e2b tier** (direct) | "Capital of France?" → e2b answers, no search, single pass | "When's my eyebrow appt?" → calendar tool lookup, e2b formats, local |
| **26B tier** (reasoner) | "What's the latest on X?" → WebSearch → 26B synthesises; Gemini only if needed | "Fit eyebrows in Thu?" → plan over calendar + memory, multi-step, may act, local |

- **Scope** decides the tool manifest and the privacy boundary.
- **Tier** decides how much model the response needs (reasoning depth,
  generation quality, side-effect risk) — **not** whether the query is personal.

The load-bearing point: *personal ≠ big*. A calendar lookup is the cheapest job
in the system and belongs at e2b tier. The 26B earns its cost only on
multi-step reasoning, planning, or quality-sensitive generation.

## Milestone scope for this task

Build and prove **end-to-end**:

1. The `TriageRouter` skeleton with the classify → manifest → dispatch flow.
2. The **personal-scope / e2b-tier** path fully working: a `lookup_calendar`
   ability, deterministic lookup, e2b formats the result, no egress, no 26B load.
3. The other three cells **wired but stubbed**: dispatch reaches the reasoner /
   egress clients, but their tool sets and synthesis logic can be minimal.

Out of scope for this task (follow-on, leave clean seams):
- Background-agent scheduling and foreground-yield logic.
- The side-effecting action path (writes / confirmations) beyond a stub.
- Semantic recall via ChromaDB (this milestone is structured lookup only).
- WebSearch + 26B synthesis on the world/26B cell.

## Components to build

### 1. Three backend `IChatClient`s

```csharp
// e2b triage head — OllamaSharp's client implements IChatClient directly.
// (If targeting mlx_lm.server / llama-server instead, use the OpenAI-compatible
//  path below — both expose an OpenAI /v1 endpoint.)
IChatClient e2bRaw = new OllamaApiClient(new Uri("http://localhost:11434"), "gemma4:e2b-it-qat");

// 26B local reasoner — local server speaks OpenAI-compatible /v1.
IChatClient reasoner = new OpenAIClient(
        new ApiKeyCredential("not-needed"),
        new OpenAIClientOptions { Endpoint = new Uri("http://localhost:8080/v1") })
    .GetChatClient("gemma4-26b-a4b").AsIChatClient();

// egress — whatever .UseGemini() builds today, now just one registered option.
IChatClient gemini = /* existing */;
```

The e2b head appears in **two** pipeline configurations over the **same warm
model**:

```csharp
// classifier: no tools, structured-output only, used for the cheap classify pass
IChatClient classifier = e2bRaw;

// answer client: same model + function invocation + local manifest applied per turn
IChatClient e2bWithTools = e2bRaw.AsBuilder().UseFunctionInvocation().Build();
```

Keep the model resident with Ollama `keep_alive: -1` (or the MLX/llama-server
equivalent) so the classify pass is sub-second.

### 2. `RouteDecision` (the classifier contract)

```csharp
record RouteDecision(Scope Scope, Tier Tier, bool Impersonal, float Confidence);
enum Scope { Personal, World }
enum Tier  { Direct, Reasoner }
```

Produced by a structured-output call on the classifier:
`await classifier.GetResponseAsync<RouteDecision>(messages, ct)`.

### 3. `TriageRouter : DelegatingChatClient`

```csharp
sealed class TriageRouter(
    IChatClient classifier,
    IChatClient e2bWithTools,
    IChatClient reasoner,
    IChatClient egress,
    AbilityRegistry abilities)
    : DelegatingChatClient(classifier)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default)
    {
        // 1. classify — schema-constrained, no tools, warm model
        var route = (await classifier.GetResponseAsync<RouteDecision>(messages, cancellationToken: ct)).Result;

        // 2. build the per-turn manifest FROM scope. Personal scope returns
        //    calendar/memory tools and NOT any egress/search tool.
        var turnOptions = new ChatOptions { Tools = abilities.ForScope(route.Scope) };

        // 3. dispatch by tier. Privacy is enforced by what is absent from Tools.
        IChatClient target = route switch
        {
            { Scope: Scope.World, Impersonal: true, Confidence: > 0.8f } => egress,
            { Tier: Tier.Reasoner }                                      => reasoner,
            _                                                            => e2bWithTools,
        };
        return await target.GetResponseAsync(messages, turnOptions, ct);
    }

    // Streaming override: stream an immediate ack ("checking your calendar…")
    // before/while the target spins up, then forward target's stream.
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default)
        => /* implement: classify, then yield ack updates, then forward target stream */;
}
```

### 4. `AbilityRegistry.ForScope`

Returns the `IList<AITool>` allowed for a scope. This method **is** the privacy
gate. Personal scope must never include an egress/web tool.

```csharp
sealed class AbilityRegistry(CalendarAbilities calendar /*, MemoryAbilities, ... */)
{
    public IList<AITool> ForScope(Scope scope) => scope switch
    {
        Scope.Personal => [ AIFunctionFactory.Create(calendar.Lookup) /*, memory… */ ],
        Scope.World    => [ /* search_web etc. — only here */ ],
        _              => [],
    };
}
```

### 5. `CalendarAbilities` (the worked path)

```csharp
sealed class CalendarAbilities(ICalendarStore store)
{
    [Description("Find the next calendar event matching a search term.")]
    public CalendarEvent? Lookup(
        [Description("Free-text term, e.g. 'eyebrow'")] string term,
        [Description("Earliest start, ISO-8601; defaults to now")] string? after = null)
        => store.FindNext(term, after);   // deterministic match lives HERE
}
```

The model extracts `term` / `after` and later formats the returned fields. It
must never reason over a dump of events to "find" the appointment — the matching
is the store's job (SQLite `LIKE`/FTS, or fetch-upcoming-and-filter).

### 6. Fluent API extensions

Extend the existing builder; `Build()` composes `TriageRouter` as terminal.

```csharp
DmonHost.CreateBuilder()
    .UseTriage("gemma4:e2b-it-qat")     // registers classifier + e2bWithTools
    .AddReasoner("gemma4-26b-a4b")       // local, OpenAI-compatible endpoint
    .UseGemini()                         // demoted to gated egress backend
    .AddAbilities<CalendarAbilities>()   // AIFunctionFactory.Create over its methods
    .Build()
    .Run();
```

## Invariants (must hold — these are the point of the design)

1. **Privacy is enforced in C#, not in a prompt.** A personal-scoped turn must
   never have an egress/web tool in `ChatOptions.Tools`. The function-invocation
   middleware will not invoke a function that is not in the manifest, so even a
   hallucinated `search_web` call is inert. Do not rely on the model "choosing"
   to stay local.
2. **Egress requires high-confidence-impersonal.** Only `Scope.World` +
   `Impersonal == true` + `Confidence > threshold` may reach Gemini.
3. **Bias to local/personal on uncertainty.** Misroute costs are asymmetric:
   personal-mistaken-for-world leaks data; world-mistaken-for-personal only
   wastes compute. Default ambiguous classifications to Personal.
4. **Matching is deterministic, in the tool.** The model only populates tool
   params and formats returned fields verbatim. Constrain the format step so it
   cannot mangle dates/times.
5. **Triage runs per turn, not per session.** Intent drifts within a session
   ("when's my appt" → "move it" → "weather Thursday").
6. **One warm model, two wrappers.** The classify pass (no tools) and the answer
   pass (`UseFunctionInvocation` + manifest) hit the same resident e2b model.
7. **Escalation trip-wire.** A retrieval-plus-reasoning turn ("…and does it
   clash with anything?") must classify as `Tier.Reasoner`, not be attempted at
   e2b tier.

## Acceptance criteria

- [ ] "Capital of France?" → answered by e2b directly, **no** WebSearch call,
      single model pass.
- [ ] "When's my eyebrow appointment?" → a single `lookup_calendar` call →
      deterministic store lookup → e2b formats the returned fields → reply.
      Assert: no outbound Gemini call; no 26B load; returned time matches the
      store row exactly.
- [ ] For any `Scope.Personal` turn, assert the egress/web tool is absent from
      `ChatOptions.Tools` passed downstream.
- [ ] "When's my eyebrow appointment, and does it clash with anything Thursday?"
      → classifies as `Tier.Reasoner` and dispatches to the local reasoner.
- [ ] Streaming path emits an ack before the target's first token.
- [ ] A metric/log records the **personal→world misclassification rate**
      specifically (the leak case), separate from overall accuracy.

## Open decisions — surface these, do not silently pick

- **Calendar data source:** local SQLite mirror (offline, sub-ms) vs the user's
  Google Calendar via their own credentials (live, adds a round-trip). Either
  satisfies "local" in the privacy sense — no third-party *model* sees the
  content. Implement behind `ICalendarStore` and leave the choice configurable.
- **Local model server:** Ollama vs llama-server vs `mlx_lm.server`. All expose
  an OpenAI-compatible endpoint; the OpenAI `AsIChatClient()` path works for the
  last two and keeps the existing MLX/Metal/MTP tuning untouched.
- **Confidence threshold** for egress (0.8 is a placeholder; tune against the
  misroute metric).
- **Classifier mechanism:** structured-output classify pass (chosen here:
  debuggable, gates cleanly) vs route-token-as-tool-call (more elegant but
  fights `UseFunctionInvocation` ordering — keep routing functions out of the
  manifest and inspect `response.Messages` yourself). Default to structured
  output; only revisit if the classify-pass latency shows up in profiling.

## Packages

- `Microsoft.Extensions.AI`
- `Microsoft.Extensions.AI.OpenAI` (for the OpenAI-compatible local endpoint and/or Gemini shim)
- `OllamaSharp` (only if using Ollama as the local backend)

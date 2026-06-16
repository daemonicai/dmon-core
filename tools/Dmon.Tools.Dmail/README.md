# Dmon.Tools.Dmail

A [dmon](https://github.com/daemonicai/dmon-core) extension that gives an agent access to
the user's email via the [Dmail](https://github.com/daemonicai/dmail) HTTP API.

## Tools

| Tool | Purpose |
|---|---|
| `search_email` | Hybrid keyword + semantic search over subject, sender and body. Returns a ranked shortlist (uid, date, sender, subject, snippet). |
| `check_new_messages` | Count and list recent mail since a timestamp (defaults to the last 24 hours), newest first. |
| `get_email` | Fetch the full body of one message by uid. |

`search_email` and `check_new_messages` are allowed without prompting (metadata + snippets);
`get_email` prompts, since it exposes complete private content.

## Configuration

Read from the environment by the parameterless constructor:

| Variable | Default | Notes |
|---|---|---|
| `DMAIL_BASE_URL` | `http://localhost:8080` | Base URL of the Dmail instance. |
| `DMAIL_API_KEY` | — | Sent as the `X-Api-Key` header. Use the key Dmail logs at startup. |

## Wiring it into a composition root

```csharp
#:package dmoncore@0.2.*
#:package Dmon.Tools.Dmail@0.2.*

using Dmon.Hosting;
using Dmon.Tools.Dmail;

await DmonHost.CreateBuilder(args)
    .AddExtension<DmailExtension>()              // configured from DMAIL_* env vars
    .Build()
    .RunAsync();
```

For explicit configuration instead of environment variables:

```csharp
.AddExtension(new DmailExtension("http://localhost:8080", apiKey))
```

## Versioning

Package `Major.Minor` tracks the dmon protocol version (currently `0.2`).

## Why

The Dmail **server** — the deployable that `tools/Dmon.Tools.Dmail` talks to over HTTP — still lives in the standalone `../dmail` repo. Phase 2 (`graft-dmail`) deliberately grafted only the tool extension and left the server behind because no monorepo bucket fit a standalone service yet. ADR-028 has since created the `services/` bucket and names **`services/Dmail` as the future home of the Dmail server**. This change completes that move: it brings the server in-repo so the Dmail capability (server + tool) lives in one place, builds under `Everything.slnx`, and shares the monorepo's conventions.

## What Changes

- Graft the standalone Dmail ASP.NET Core server (`Microsoft.NET.Sdk.Web` — IMAP ingestion, ONNX embeddings, hybrid SQLite-vec search, OAuth2 + API-key auth, admin dashboard) from `../dmail` `src/Dmail/` into **`services/Dmail/`**, **with git history preserved** via the established `git filter-repo` graft recipe (third application after Phases 1–3).
- Graft the server-coupled tests (`../dmail` `test/Dmail.Tests/`) into **`test/Dmail.Tests/`**, dropping `DmailExtensionTests.cs` (already grafted to `test/Dmon.Tools.Dmail.Tests/` in Phase 2).
- Bring the server's runtime assets: the ONNX model dir (`bge-micro-v2.onnx` + `vocab.txt`), the `Dockerfile` + `docker-compose.yml` + `.env.example`/`.dockerignore`, and the `wwwroot/` admin dashboard.
- Wire to `services/` conventions (per `services/README.md`, using `services/Dcal` as the template): `Sdk="Microsoft.NET.Sdk.Web"`, `IsPackable=false`, `TreatWarningsAsErrors=true`, the root `Directory.Build.props`/CPM apply (no `services/Directory.Build.props`), and add the server to `services.slnx` + `Everything.slnx` with its test under `/test/`.
- Rename the project to the `services/` convention: `AssemblyName`/`RootNamespace` `Daemonic.Dmail` → `Dmail`; central-package-manage its dependencies (MailKit/MimeKit, SemanticKernel Onnx/SqliteVec, VectorData, Microsoft.Data.Sqlite — the last already pinned).
- **App-versioned, NOT protocol-locked**: per ADR-024/028 the server is an app artifact off the NuGet protocol-lockstep train; it carries no `MinVerTagPrefix`/`PackageId` and is never packed.
- Record `../dmail` as **fully absorbed** (DEVLOG + memory note; optional local `absorbed-into-dmon-core` git tag) once the graft verifies. The server has **no** `ProjectReference` to the dmon extension — it is reached only over HTTP via `DMAIL_BASE_URL`/`DMAIL_API_KEY`.

## Capabilities

### New Capabilities
- `dmail-server`: The behavioural contract of the `Dmail` server that backs `tools/Dmon.Tools.Dmail` — IMAP ingestion of subscribed mailboxes, ONNX embedding + hybrid (vector + FTS) search over stored mail, the agent-facing HTTP API (`search_email`/`check_new_messages`/`get_email`), API-key + OAuth2 auth, encrypted token storage, and the admin dashboard. Mirrors the `dcal-sync` single-server-capability precedent.

### Modified Capabilities
<!-- None. `dmail-tool` (the tool contract) is unchanged. `monorepo-layout`'s
     services-bucket enumeration is owned by the in-flight daemon-app change and
     is deliberately not touched here to avoid a cross-change collision. -->

## Impact

- **New code:** `services/Dmail/` (server, with history), `test/Dmail.Tests/` (server tests, with history), `services/Dmail/models/` (ONNX assets), `services/Dmail/Dockerfile` + `docker-compose.yml` + `.env.example` + `.dockerignore`.
- **Build/solution:** `services.slnx` + `Everything.slnx` gain the server and its test; `Directory.Packages.props` gains the server's external dependency pins.
- **No changes** to `core/`, `providers/`, `tools/` (incl. `Dmon.Tools.Dmail`), `frontends/`, the RPC protocol, or any existing dmon-core behaviour. No production migration (the server is a separately deployed app; clean break on the project rename is fine).
- **Source repo:** `../dmail` becomes fully absorbed (left intact on disk; not deleted).

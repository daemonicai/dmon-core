# Dmon.Memory — sqlite-vec native dependency notes

## Supported RID matrix

The `HiraokaHyperTools.sqlite-vec` 0.1.9 package ships pre-built `vec0` loadables for:

| RID | Asset |
|-----|-------|
| `osx-arm64` | `runtimes/osx-arm64/native/vec0.dylib` |
| `osx-x64`   | `runtimes/osx-x64/native/vec0.dylib`   |
| `linux-x64` | `runtimes/linux-x64/native/vec0.so`    |
| `linux-arm64` | `runtimes/linux-arm64/native/vec0.so` |
| `win-x64`   | `runtimes/win-x64/native/vec0.dll`     |

## sqlite-vec loadable provenance

`HiraokaHyperTools.sqlite-vec` 0.1.9 is a third-party NuGet repackaging of
[asg017/sqlite-vec](https://github.com/asg017/sqlite-vec), the official upstream project.
The upstream project does not publish a first-party .NET NuGet package with per-RID native
assets; this package fills that gap by embedding the upstream pre-built shared libraries.

The loadable runs native code in-process (same native-dependency posture already accepted
for LlamaSharp — see design.md D3). Pin the exact version: **0.1.9**. Do not float the
version — the vec0 virtual-table wire format is tied to the version used when `index.db`
was first created; a version bump requires an index rebuild.

**Risk:** as a third-party repackage, the package may lag behind upstream releases or be
abandoned. If that occurs, the loadable assets can be replaced by extracting them from the
upstream GitHub release and placing them in `runtimes/<rid>/native/` manually (the
resolution logic in `SqliteVecLoader` is unaffected).

## Runtime resolution

`SqliteVecLoader` probes candidate paths in this order (first existing file wins):

1. `<assembly-dir>/runtimes/<RuntimeInformation.RuntimeIdentifier>/native/<loadable>` —
   primary location under a plain `dotnet build` or `dotnet test`.  Note: the native asset
   materializes in the **consuming runnable project's** output directory (test project or
   host executable), not in a library-only build of `Dmon.Memory` itself — NuGet copies
   `runtimes/<rid>/native/` assets only when building an executable or test runner.
2. `<assembly-dir>/runtimes/<computed-os-arch-rid>/native/<loadable>` — fallback when the
   runtime identifier is a portable or shortened form.
3. Same two RID-qualified paths under the current working directory.
4. Enumeration of all `<assembly-dir>/runtimes/*/native/<loadable>` paths — catches extra
   RIDs shipped by the package without explicit probing.
5. `<assembly-dir>/<loadable>` and `<cwd>/<loadable>` — self-contained publish flattens
   the native asset alongside the managed DLLs.

A `dotnet publish --self-contained -r <rid>` flattens all native assets next to the output
DLL, so path 5 also resolves correctly for self-contained deployments.

## A2 caveat — `PlatformTarget` conflict on host Exe projects

`HiraokaHyperTools.sqlite-vec` 0.1.9 emits a hard build **Error** for non-x64/x86 targets
when a host `Exe` project sets an explicit `PlatformTarget` property (e.g.
`<PlatformTarget>arm64</PlatformTarget>` in a self-contained arm64 publish profile).

This is a known limitation of the package's `.targets` file. When wiring `Dmon.Memory` into
a host project (`Dmon.Terminal` or a future Avalonia host) with an arm64 self-contained
publish, suppress or remove the `PlatformTarget` property from the host `.csproj` (let the
RID drive the target architecture instead of setting `PlatformTarget` explicitly). The
library project itself is unaffected — this only surfaces on `Exe`-outputting projects.

## Composition / DI

### Basic wiring (short-term only)

```csharp
services.AddDmonCore()
        .AddDmonMemory();
```

`AddDmonMemory()` registers:

- `ModelResolver` (singleton, parameterless) — local embedding model file resolver.
- `IEmbeddingGenerator<string, Embedding<float>>` → `LocalEmbeddingGenerator` (singleton).
- `IMessageAppender` → `MessageAppender` (singleton) — appends turns to `messages.jsonl`.
- `IShortTermMemory` → `ShortTermMemory` (singleton).
- `IMemory` → `Memory` facade (singleton).

All registrations use `TryAddSingleton`, so a host or test can pre-register substitutes
without them being overwritten.

### Session identity — host responsibility

`ShortTermMemory` requires a `MemoryContext` (record `(DatapackId, AgentId, ConversationId)`)
that is session-scoped. The host must register it before resolving `IShortTermMemory`:

```csharp
services.AddSingleton(new MemoryContext(datapackId, agentId, conversationId));
```

Additionally, `ShortTermMemory.InitializeAsync()` **must** be awaited before calling
`SearchAsync` or `RecordAsync`. DI cannot perform async initialisation — this is a host
responsibility (e.g. call it in a hosted-service `StartAsync` or an application startup
hook).

### Enabling durable (long-term) memory

Long-term memory is opt-in and provided by the separate `Dmon.Memory.Meko` package.
Register it before or after `AddDmonMemory()` — order does not matter:

```csharp
services.AddDmonCore()
        .AddDmonMemory()
        .AddMekoLongTermMemory(...);   // from Dmon.Memory.Meko
```

The `Memory` facade resolves `ILongTermMemory` via `GetService` (nullable) at construction.
When no long-term store is registered, `IMemory.LongTerm` is `null` and dmon runs in
short-term-only mode.

`Dmon.Memory` has **no** package or project reference to `Dmon.Memory.Meko`.

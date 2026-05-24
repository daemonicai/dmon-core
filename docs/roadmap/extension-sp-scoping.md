# Extension IServiceProvider scoping

**Status:** 💭 Idea  
**Context:** Added after V1 extension loading implementation

## Background

When `NuGetExtensionLoader` activates an extension that declares an `IServiceProvider` constructor, it passes the host's **full** service provider. This gives extensions access to every service registered in the DI container — including internal ones like `IProviderRegistry`, `IToolRegistry`, `ISessionService`, etc.

This is intentional for V1: it's simple and covers the common cases (injecting `ILogger<T>`, `HttpClient`, `IConfiguration`).

## The concern

A malicious or poorly-written extension could resolve and misuse internal services. For example, an extension could resolve `IToolRegistry` and register arbitrary tools, or resolve `IProviderRegistry` and switch providers without going through the normal permission gate.

The security analysis step (Group 5 of `extension-ecosystem`) checks for this, but the check is LLM-based and not guaranteed to catch everything.

## Post-V1 options

**Option A: Curated facade.**  
Build an `IExtensionServices` interface exposing only the services extensions are intended to use (e.g. `ILogger`, `HttpClient`, `IConfiguration`, maybe `IChatClient`). Pass that instead of the full SP. Extensions that want the SP constructor would inject `IExtensionServices`.

Downside: extensions that legitimately need internal services (e.g. a tool-bridge extension that needs `IToolRegistry`) can't get them.

**Option B: Allow-list by service type.**  
Wrap the host SP in a decorator that logs or blocks resolution of internal service types. Extensions still get `IServiceProvider`, but calls to resolve `IToolRegistry` etc. either return null or throw.

Downside: fragile — any new internal service needs to be added to the deny-list.

**Option C: Separate extension DI scope.**  
Register a child DI container for extensions, seeded only with approved services. Requires more infrastructure but is the cleanest long-term answer.

## Recommendation for V1.1+

Revisit after observing which services real extensions actually need. If the pattern is consistently "just ILogger + HttpClient + IConfiguration", Option A is straightforward and should be preferred.

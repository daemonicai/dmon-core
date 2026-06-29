## REMOVED Requirements

### Requirement: Platform applicability check
**Reason**: The oMLX GUI-app provider is superseded by `mlx-provider`, a headless `mlx_lm.server` runtime that keeps two models resident on separate ports.
**Migration**: Use `Dmon.Providers.Mlx` and its composition verbs. Applicability is now arm64/macOS + `uv` on PATH (see `mlx-provider`).

### Requirement: Server identity probe
**Reason**: oMLX-specific identity probing is replaced by the mlx readiness probe (completion/`/health`).
**Migration**: See `mlx-provider` "Readiness via completion or health, not model listing".

### Requirement: Server lifecycle management
**Reason**: The single oMLX GUI app (one model) cannot hold two models resident; replaced by per-model `mlx_lm.server` processes with attach-first start and `StopAsync`.
**Migration**: See `mlx-provider` "Attach-first start lifecycle" and "Stop lifecycle".

### Requirement: Model listing with capability heuristic
**Reason**: `/v1/models` lists cached (not resident) models and is no longer used for readiness or capability inference; capability is set by the tool-calling probe.
**Migration**: See `mlx-provider` "Tool-calling capability probe".

### Requirement: Custom auth header injection
**Reason**: The local mlx_lm runtime is launched headless on localhost; oMLX-specific auth header injection is not carried forward.
**Migration**: None required for the local headless runtime.

### Requirement: Configuration from environment variables
**Reason**: Configuration is superseded by the mlx provider's keyed-runtime configuration (model ids, ports, idle window).
**Migration**: See `mlx-provider` "Two keyed runtimes with default model pairing".

### Requirement: UseOmlx composition verb
**Reason**: Replaced by the mlx composition verbs for first-line and escalation runtimes.
**Migration**: Replace `UseOmlx` with the mlx verbs (see `mlx-provider` "Mlx composition verbs").

### Requirement: Per-model client construction for non-active use
**Reason**: Per-model client construction is provided by the mlx provider's keyed runtimes.
**Migration**: See `mlx-provider` "Mlx composition verbs" and "Two keyed runtimes with default model pairing".

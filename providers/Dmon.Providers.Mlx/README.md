# Dmon.Providers.Mlx

Apple-Silicon MLX local-runtime provider for the dmon coding agent.

Manages `mlx_lm` server processes via `uv`, exposes them as `Microsoft.Extensions.AI` `IChatClient` instances.

## Requirements

- macOS on Apple Silicon (arm64)
- `uv` on PATH (`curl -LsSf https://astral.sh/uv/install.sh | sh`)

## Default model pairing

| Runtime    | Model                                        | Port |
|------------|----------------------------------------------|------|
| First-line | `mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit`  | 8800 |
| Escalation | `mlx-community/gemma-4-26B-A4B-it-qat-nvfp4`   | 8810 |

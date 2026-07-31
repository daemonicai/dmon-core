# Pi coding agent — specific instructions

The Pi agent (`earendil-works/pi`) uses the skills in `.pi/skills/` and prompts in `.pi/prompts/`. These mirror the Claude Code OpenSpec skills.

The project-wide rules in the repo root [`CLAUDE.md`](../CLAUDE.md) apply to Pi as well — this file adds only what is Pi-specific.

## What the Pi agent should do

- Use `/opsx:explore` to think through a problem before proposing.
- Use `/opsx:propose` to create a new OpenSpec change.
- Use `/opsx:apply` to implement tasks from an active change.
- Use `/opsx:archive` to finalise a completed change.

## What the Pi agent must not do

- Must not modify accepted ADRs without writing a superseding ADR first.
- Must not implement features outside an active OpenSpec change (except trivial single-line fixes).
- Must not `git push` or create PRs without explicit user instruction.
- Must not skip build warnings or disable `TreatWarningsAsErrors`.
- Must not take a dependency on Microsoft Agent Framework (MAF) — ADR-001 rules this out.

## Session and RPC

- The Pi agent communicates over JSONL/stdio (ADR-003 shape). Commands use `{"id": "...", "type": "...", ...params}`. Events use `{"id": "...", "event": "...", ...payload}`.
- Do not invent new RPC message types without updating the spec in `openspec/specs/`.

## Provider configuration

- Provider credentials are read from env vars (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`) or a config file. Do not hard-code keys or commit them.

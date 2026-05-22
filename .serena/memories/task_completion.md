# Task Completion Checklist

When a coding task is done:

1. `dotnet build Daemon.slnx` — must be 0 warnings, 0 errors
2. `dotnet test Daemon.slnx` — all tests must pass
3. Mark tasks complete in `openspec/changes/daemon-core/tasks.md`
4. Summarise changes and request `reviewer` agent audit
5. Do NOT `git push` or open PRs unless explicitly asked

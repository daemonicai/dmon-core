# Suggested Commands

## Build
```
dotnet build Daemon.slnx
```

## Test
```
dotnet test Daemon.slnx --logger "console;verbosity=normal"
```

## Run console host (not yet wired)
```
dotnet run --project src/Daemon.Console
```

## Git
Standard git; branch from main, Conventional Commits format.
Commit scope = component: `feat(session):`, `fix(rpc):`, `docs(adr):`.

# Dmon.Terminal

The `dmon` console host — a .NET global tool that launches and communicates with the dmon agent core.

Install as a dotnet global tool:

```
dotnet tool install -g Dmon.Terminal
dmon
```

The tool spawns `dmoncore` (acquired on demand from NuGet) over JSONL/stdio and presents an interactive terminal UI.

Licensed under the [Mozilla Public License 2.0](https://www.mozilla.org/en-US/MPL/2.0/).

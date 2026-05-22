# Code Conventions

- C# 13, .NET 10, file-scoped namespaces (`namespace Foo.Bar;`)
- `record` for immutable data, `class` for mutable state
- `sealed` on all concrete classes and records unless inheritance is required
- Async methods end in `Async`; `CancellationToken` always last param, always named `cancellationToken`
- No `var` unless type obvious from RHS (e.g. `new SomeType()`, literals)
- JSON property names: always explicit `[JsonPropertyName("camelCase")]`
- No restatement comments; only non-obvious constraints
- Interfaces prefixed `I`, no other prefixes/suffixes
- `TreatWarningsAsErrors` is on — zero warnings allowed
- Test classes: `sealed`, implement `IDisposable` for temp dir cleanup
- Temp dirs in tests: `Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())`
- No `xUnit` theory data inline where a simple fact suffices
- Test method names: `Subject_Condition_ExpectedResult`

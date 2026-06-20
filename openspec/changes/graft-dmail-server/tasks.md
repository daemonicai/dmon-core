## 1. Filtered import (history-preserving)

- [ ] 1.1 Clone `../dmail` to a throwaway path and `git checkout main`; confirm `src/Dmail/`, `models/`, the Docker/env artifacts, and `test/Dmail.Tests/` are present.
- [ ] 1.2 Pass 1 — `uvx git-filter-repo` keep-list (`--path src/Dmail/ --path models/ --path Dockerfile --path docker-compose.yml --path .env.example --path .dockerignore --path test/Dmail.Tests/`) with the `--path-rename`s to `services/Dmail/...`, `services/Dmail/models/`, and `test/Dmail.Tests/`.
- [ ] 1.3 Pass 2 — `git filter-repo --invert-paths --path test/Dmail.Tests/DmailExtensionTests.cs` to drop the already-grafted extension test; verify `git ls-files` shows the server subtree present and `DmailExtensionTests.cs` absent.
- [ ] 1.4 Merge the throwaway clone into `change/graft-dmail-server` with `--allow-unrelated-histories` as a single import commit; remove the temp remote and delete the clone. Leave `../dmail` intact.

## 2. Project rename + service conventions

- [ ] 2.1 Set `services/Dmail/Dmail.csproj` to the `services/Dcal` shape: `AssemblyName`/`RootNamespace` = `Dmail`, `IsPackable=false`, `TreatWarningsAsErrors=true`, `Nullable`/`ImplicitUsings` enable, and the `InternalsVisibleTo` `Dmail.Tests` attribute.
- [ ] 2.2 Rewrite C# namespaces `Daemonic.Dmail` → `Dmail` (incl. `.Data`/`.Services`/`.Models`) across all `services/Dmail/` source files; a repo-wide grep (excluding `bin/obj`) for `Daemonic.Dmail` returns nothing.
- [ ] 2.3 Re-root the ONNX model reference: rewrite the csproj `Content Include="..\..\models\..."` to `models\...` and confirm the model + vocab land in `services/Dmail/models/`.

## 3. Tests

- [ ] 3.1 Re-point `test/Dmail.Tests/Dmail.Tests.csproj` `ProjectReference` to `..\..\services\Dmail\Dmail.csproj`; set `RootNamespace=Dmail.Tests`, `IsPackable=false`; strip any inline `Version=` and any reference to the extension.
- [ ] 3.2 Rewrite `Daemonic.Dmail.Tests` → `Dmail.Tests` namespaces across the remaining test files; confirm no test references `DmailExtension` or any extension type.

## 4. Central package management

- [ ] 4.1 Strip inline `Version=` from the server's `PackageReference`s; add concrete `<PackageVersion>` pins to root `Directory.Packages.props` for `MailKit`, `MimeKit` (if referenced directly), `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.SemanticKernel.Connectors.Onnx`, `Microsoft.SemanticKernel.Connectors.SqliteVec`, and any DataProtection package not transitive — resolved from a clean restore (no floating prereleases). Reuse the existing `Microsoft.Data.Sqlite` pin.

## 5. Deployment artifacts

- [ ] 5.1 Re-root the `Dockerfile` paths (`COPY src/Dmail/...` → `services/Dmail/...`, `COPY models/...` → `services/Dmail/models/...`) and the `docker-compose.yml` `build.context`/`dockerfile` for a repo-root build context; keep both under `services/Dmail/`.
- [ ] 5.2 Add/refresh a `services/Dmail/README.md` documenting env vars (`DMAIL_*`), the repo-root `docker build`/`docker compose` invocation, and that it backs `tools/Dmon.Tools.Dmail` over HTTP.

## 6. Solution wiring

- [ ] 6.1 Add `services/Dmail/Dmail.csproj` (under `/services/`) and `test/Dmail.Tests/Dmail.Tests.csproj` (under `/test/`) to `services.slnx` and to `Everything.slnx`.

## 7. Gates + provenance

- [ ] 7.1 Build `services/Dmail` alone, fixing any warnings surfaced by `TreatWarningsAsErrors` without suppressing analyzers (scope a narrow `#pragma`/`NoWarn` only where the source already does, e.g. `SKEXP0070`).
- [ ] 7.2 Gates green: `make build` (warnings-as-errors clean), `env -u MEKO_API_KEY make test` (all tests incl. the new `Dmail.Tests`), `openspec validate graft-dmail-server --strict`.
- [ ] 7.3 Verify history preserved: `git log --follow services/Dmail/Program.cs` shows pre-graft commits; `git ls-files` confirms ONNX model binaries imported intact.
- [ ] 7.4 Record `../dmail` as fully absorbed in DEVLOG + memory (optional local `absorbed-into-dmon-core` tag on `../dmail` `main`).

# Dmon.Core

The `dmoncore` agent core — a .NET coding agent that communicates over JSONL/stdio.

This package is published as a runnable framework-dependent publish closure. It is not a library; it is acquired at runtime by the `dmon` host tool and launched as a subprocess.

**Do not add this as a `PackageReference` in your project.** Use the `Dmon.Terminal` tool (`dmon`) which acquires and manages `dmoncore` automatically.

Licensed under the [Mozilla Public License 2.0](https://www.mozilla.org/en-US/MPL/2.0/).

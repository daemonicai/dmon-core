# Dmon.Protocol

Wire protocol DTOs and constants for the dmon JSONL/stdio agent protocol (ADR-003).

Provides:
- `ProtocolVersion.Current` — the single source of truth for the protocol `Major.Minor` version
- Strongly-typed request/response/event record types for the Pi-compatible JSONL protocol

**Version scheme:** `Major.Minor` tracks the wire-protocol contract version; `Patch` is an independent release counter. A `dmon` host and `dmoncore` agent are compatible when their `Major.Minor` values are equal.

Licensed under the [Mozilla Public License 2.0](https://www.mozilla.org/en-US/MPL/2.0/).

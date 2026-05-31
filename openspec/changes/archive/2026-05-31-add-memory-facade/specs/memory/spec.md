## ADDED Requirements

### Requirement: Memory facade fans out writes and fuses reads

The system SHALL provide an `IMemory` facade (`Memory`) exposing read-only `ShortTerm` (`IShortTermMemory`, never null) and `LongTerm` (`ILongTermMemory`, null when long-term is not configured). `IMemory.RecordAsync` SHALL dispatch to `ShortTerm.RecordAsync` always and to `LongTerm.RecordAsync` when long-term is configured. `IMemory.FlushAsync` SHALL flush every configured tier. `IMemory.SearchAsync` SHALL return results fused from both tiers.

#### Scenario: Record reaches both tiers
- **WHEN** `IMemory.RecordAsync(turns)` is called and long-term is configured
- **THEN** the turns are passed to both `ShortTerm.RecordAsync` and `LongTerm.RecordAsync` (each tier then applies its own policy)

#### Scenario: Explicit single-tier access
- **WHEN** a caller needs exactly one tier
- **THEN** it uses `memory.ShortTerm` or `memory.LongTerm` directly, bypassing fusion

#### Scenario: Flush spans configured tiers
- **WHEN** `IMemory.FlushAsync()` completes
- **THEN** `ShortTerm.FlushAsync` has completed and, if configured, `LongTerm.FlushAsync` has been invoked

### Requirement: Cross-tier read fusion is rank-based with provenance

`IMemory.SearchAsync` SHALL query both configured tiers and fuse their result lists using Reciprocal Rank Fusion over each result's rank within its own tier — raw cross-tier `Score` values SHALL NOT be compared directly. Each returned `MemoryHit` SHALL retain its originating `Source`. The facade SHALL de-duplicate near-identical hits that surface from both tiers, keeping a single fused entry.

#### Scenario: Results are attributable to a tier
- **WHEN** `IMemory.SearchAsync` returns fused results
- **THEN** each `MemoryHit.Source` indicates whether it came from short-term or long-term

#### Scenario: Cross-tier scores are fused by rank, not magnitude
- **WHEN** short-term and long-term both return candidates
- **THEN** the fused order is computed from each hit's rank within its own tier (RRF), not from the raw `Score` values (which come from different models)

#### Scenario: A duplicate fact from both tiers appears once
- **WHEN** the same fact is returned by both short-term and long-term
- **THEN** the fused result contains a single de-duplicated entry rather than two near-identical hits

#### Scenario: One tier empty
- **WHEN** only one tier returns candidates (the other is empty)
- **THEN** `SearchAsync` returns that tier's results, fused/ranked, without error

### Requirement: Long-term tier is optional and decoupled

The facade SHALL function with long-term memory absent: when no `ILongTermMemory` is configured, `LongTerm` is null, writes go only to short-term, `SearchAsync` returns short-term results only, and no long-term operation is attempted. The short-term/facade assembly SHALL NOT depend on any specific long-term implementation package; the long-term tier SHALL be supplied independently through dependency injection.

#### Scenario: Short-term-only operation
- **WHEN** the facade is configured without a long-term tier
- **THEN** record/search/flush operate on short-term alone and `LongTerm` is null

#### Scenario: Long-term supplied independently is picked up
- **WHEN** a long-term implementation is registered separately in the container
- **THEN** the facade resolves it and fans out / fuses across both tiers — without the core assembly referencing that implementation's package

#### Scenario: A failing long-term tier degrades, not breaks
- **WHEN** a configured long-term search fails or times out
- **THEN** `IMemory.SearchAsync` still returns the short-term results (the failure is contained, not propagated)

### Requirement: Memory composition via dependency injection

The system SHALL provide an `AddDmonMemory()` `IServiceCollection` extension that registers the local embedding generator, the short-term memory store, and the `IMemory` facade. The facade registration SHALL consume an `ILongTermMemory` from the container if one is present and otherwise wire long-term as absent.

#### Scenario: One call wires the short-term-capable facade
- **WHEN** a host calls `AddDmonMemory()`
- **THEN** `IMemory` resolves to a facade backed by a working short-term tier (embedder + index)

#### Scenario: Adding long-term is a second, independent call
- **WHEN** a host calls `AddDmonMemory()` and also registers a long-term tier (e.g. via the Meko package's own extension)
- **THEN** the resolved `IMemory` fuses both tiers, regardless of the order the two registrations were made

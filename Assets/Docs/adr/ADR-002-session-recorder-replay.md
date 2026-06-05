# ADR-002: Optional session recorder + replay source

**Status:** Proposed (accepted, deferred) · **Date:** 2026-05-18 · **Depends on:** ADR-001

## Context

Audit risk R3: state is fully transient; restart wipes everything. No replay, no
post-session analytics, no run comparison, no export — despite a sophisticated offline
`AutoSim` harness that proves the team values analysis. Once `ITileSource` (ADR-001)
exists, persistence and a deterministic demo/test source are small, infra-free additions.

## Decision

1. **`TileRecorder`** — subscribes to the active `ITileSource`; appends each `TilePose`
   and removal with a UTC timestamp to a JSON-lines file under
   `Application.persistentDataPath` (per instance, per session). Buffered/async append to
   keep the ingest path cheap. Opt-in, disabled by default.
2. **`ReplayTileSource : ITileSource`** — re-emits a recorded JSON-lines file at original
   cadence. A drop-in source for demos without a physical table and for deterministic
   regression runs.

No database, no server — JSON-lines on local disk only.

## Options Considered

| Option | Verdict |
|--------|---------|
| JSON-lines file in `persistentDataPath` | **Chosen** — zero infra, matches low-churn / no-infra constraint |
| Embedded DB (SQLite) | Rejected for now — infra + dependency not justified at current scale |
| Remote telemetry service | Rejected — out of scope, violates ship constraint |

## Consequences

- **Easier:** durable session artifact; deterministic repro feeding the existing
  `AutoSim` / test culture; demo-without-table.
- **Harder:** disk I/O on the ingest path — mitigate with buffered/async append.
- **Sequencing:** land after ADR-001 **and** after the current UI-polish milestone ships.

## Verification

Record a session → confirm a JSON-lines artifact appears in `persistentDataPath`. Feed it
through `ReplayTileSource` with no physical table → final QOL matches the recorded run.

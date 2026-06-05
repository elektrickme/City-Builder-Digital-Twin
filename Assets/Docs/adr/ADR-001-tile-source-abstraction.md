# ADR-001: Introduce `ITileSource` ingestion abstraction

**Status:** Accepted · **Date:** 2026-05-18 · **Deciders:** Project owner

## Context

The product is branded a *digital twin* but the data layer is a physical-tabletop ↔ screen
mirror. There are exactly two data inputs (TUIO/OSC fiducials, static `game_config.json`)
and no durable output. TUIO ingestion is fused into `TileTrackingManager`
(`[RequireComponent(OSCReceiver)]` + OSC parse + event surface in one class), so any second
data path (replay, mock, network sync, live real-world feed) would require forking that
class. Constraint: ship-focused / low churn — the shipping path must not change behavior.

The existing seam is already clean: `TileTrackingManager` → `event Action<TilePose>` →
`GameInstanceCoordinator` → `SimulationEngine` public API. `TilePose` is already a
transport-agnostic DTO. So a source abstraction is a *pure addition*.

## Decision

Extract the input contract into `CityTwin.Input.ITileSource`:

```csharp
public interface ITileSource {
    event Action<TilePose> OnTileUpdated;
    event Action<string>   OnTileRemoved;
    void ClearSessions();
}
```

- `TileTrackingManager : MonoBehaviour, ITileSource` — no body change; it already declares
  all three members.
- `GameInstanceCoordinator` holds `private ITileSource tileTracking` (no longer
  `[SerializeField]`; an interface is not Inspector-serializable) and resolves it with
  `GetComponent<ITileSource>()` in `Awake`. `TileTrackingManager` is on the same GameObject
  as the coordinator (per `SCENE_SETUP.md` §4 and the prior `GetComponent` fallback), so
  runtime resolution is behavior-identical.

## Options Considered

| Option | Complexity | Cost | Verdict |
|--------|-----------|------|---------|
| **A. Interface extraction** | Low | 1 file added, ~3 lines changed, no functional delta | **Chosen** |
| B. Full event-bus / message broker | High | Reroute all subsystem events | Rejected — violates low-churn, destabilizes UI-polish work |
| C. Do nothing | None | — | Rejected — permanently blocks the digital-twin-data goal |

## Consequences

- **Easier:** add replay / mock / live-data sources by implementing `ITileSource`, with no
  changes to the coordinator, simulation, or OSC code.
- **Harder:** nothing material. Inspector wiring of the source now relies on `GetComponent`
  (same-GameObject) instead of a draggable field.
- **Revisit:** the interface may grow `Health` / `LastPacketUtc` members when ingestion
  observability (audit risk R4) is addressed.

## Verification

Behavior must be zero-delta: run one Game Instance, drive `/tuio/2Dobj`, confirm
add / move / remove, budget block, over-budget marker, and `RestartGame()` behave exactly
as before. Run the NUnit suite in `Assets/Tests/`.

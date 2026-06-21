# Playtest Menu (Secret / Debug Menu)

The in-game debug overlay used for balancing. It lives in `MouseBuildingTester.cs` and opens on the backquote key (`` ` ``, top-left of most keyboards).

## Opening and closing
- Press `` ` `` to toggle the menu. While open, game input is paused so you can't place by accident.
- The "Close" button (top of the left panel) also hides it.
- Use the "Menu scale" bar at the very top to zoom the whole menu if the text is too small or large.
- Keys 1-4 pick a building; click the table to place; drag to move; hold ESC and click to delete.

## Changes are live - save them when you're happy
Every control takes effect immediately. They are NOT auto-saved: if you just close the menu, the tweaks reset on the next restart / relaunch.

When a configuration feels good, use the **Save to file** / **Export** / **Import** buttons (top of the right panel). All three carry the same values: scoring, impact radii, QOL penalty/cap, bus stop spacing, session length, starting budget, inactivity timeout, the QOL pass bands, per-building scores, and the halo multipliers (master + per-size).

- **Save to file** - writes `StreamingAssets/game_config.json` (+ a `.bak`). Editor and desktop builds only; not available on web.
- **Export** - downloads the current config as a `game_config.json` file. Use this on **web** (and anywhere you want a shareable copy).
- **Import** - loads a config from a `.json` file you pick and applies it live. The same JSON works everywhere, so a config exported on the web opens in the editor and vice-versa.

## Left panel - Building Picker
- Filter box + a list of every building (name, id, price, halo size, stop radius).
- Click "Pick" or a row, or press keys 1-4, to select the active building.
- The selected building shows read-only readouts (halo radius, stop-search radius). Tune its scores and the per-size halo in the right panel.

## Right panel - Playtesting
- **Starting budget** - money each round begins with (saved). Also tops up your current budget so you can test immediately.
- **Session length / Time remaining** - round duration and the live countdown. Set "Time remaining" to 0 to jump straight to the end screen.
- **Bus stop spacing** - distance between stops along roads. Lower = denser. Stop count is roughly road length / spacing; the current stop count is shown in the label.
- **Map select (A/B/C/D)** - placeholder, not wired up yet (maps still to be built).

### Quality of Life
- **QOL Inequality Penalty** - penalizes uneven cities (a big gap between the best and worst hub).
- **QOL Maximum Cap** - hard ceiling on the QOL score. QOL can never exceed this, so any pass band above the cap is unreachable.

### Building reach (impact radius)
- **Impact Radius Small / Medium / Large** - how far each building size searches for bus stops to score through.

### Building halo
- **Halo - Master (all)** - global multiplier applied on top of the per-size values; scales every building's halo at once.
- **Halo - Small / Medium / Large** - per-size halo multiplier: marker size, connection reach, and footprint. Applies to ALL buildings of that size, including already-placed ones. "Reset halos" (left panel) returns master and all three to 1x.

### Selected building
- **Environment / Economy / Health & Safety / Culture & Edu** - the picked building's contribution to each QOL pillar. Raising these makes the completion score easier to hit.

### QOL pass thresholds
- The final QOL when the timer ends selects an end-screen "band" (the completion score). All bands are listed, and the one matching the live QOL is highlighted.
- Drag a "Cutoff" slider to move where the next band begins.

## Advanced (dev)
Collapsed by default. Scoring-curve internals (Norm, Building Strength, decay curve, distance scale, road reach, etc.). These are already dialed in - leave them alone unless you know the scoring model.

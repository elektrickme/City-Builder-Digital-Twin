# ADR-003: Replace hand-rolled localization JSON parsing with Newtonsoft

**Status:** Proposed (recommended, gated) · **Date:** 2026-05-18

## Context

Audit risk R2: `GameConfigLoader` uses a two-pass parse — `JsonUtility` plus a
hand-written brace-walking parser (`StripLocalization` / `ParseLocalization`,
`GameConfigLoader.cs:160-237`) for the nested localization dictionary. Schema drift, an
escaped brace inside a localized string, or a comment can cause a silent partial parse that
falls back to an empty building catalog with only a `Debug.LogWarning`. Newtonsoft is not
in the package manifest.

## Decision

Add `com.unity.nuget.newtonsoft-json` (first-party Unity package, low risk). Deserialize
the whole config — including nested dictionaries — with Newtonsoft; delete
`StripLocalization` and `ParseLocalization`. On parse failure, log an **error** (not a
warning) and fail fast instead of silently degrading to an empty catalog.

## Options Considered

| Option | Verdict |
|--------|---------|
| Newtonsoft (`com.unity.nuget.newtonsoft-json`) | **Chosen** — ubiquitous, correct nested-dict parsing, deletes brittle code |
| Keep hand-rolled parser, add validation | Rejected — still fragile, more code to maintain |
| `System.Text.Json` | Rejected — weaker Unity/IL2CPP track record than the Unity-blessed Newtonsoft package |

## Consequences

- **Easier:** removes a whole silent-failure class; correct localization parsing; loud,
  fail-fast errors.
- **Harder:** adds one dependency; touches a *working* path during UI-polish.
- **Gating:** sequence this so it does **not** ride with risky UI commits. Gate behind the
  existing config-loader tests in `Assets/Tests/`; land just after the polish milestone.

## Verification

Corrupt a brace in `game_config.json` → expect a loud error and fail-fast (not a silent
empty catalog). A valid config with localized strings parses identically across EN / RU /
KZ. Existing config-loader tests stay green.

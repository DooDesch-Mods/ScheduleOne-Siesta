# Changelog

All notable changes to Siesta are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-06-20

Initial release.

### Added
- Distance/visibility LOD for all NPCs (base game + any NPC-adding mod) via the game NPC registry.
- Three tiers: Full, Cosmetic (renderer hidden, AI untouched), Deep (movement + schedule paused).
- Clean wake-up: schedule re-enable + `EnforceState` time catch-up, movement resume, NavMesh repair,
  then reveal - no T-pose, no off-NavMesh agents (failed wakes are kept Full for the session).
- Functionality-preservation exemptions: dealers/employees/customers mid-task, story/`IsImportant`,
  in-dialogue/in-vehicle/KO'd/panicking NPCs, and anyone near any player are never deep-culled.
- Multiplayer safety: cosmetic culling is local per-client; deep culling is host-authoritative only.
- Budgeted, allocation-free re-evaluation loop with distance hysteresis.
- `RestoreAll` before save / scene change / quit so NPCs are never persisted in a culled state.
- Configurable distances, tier toggles, on-screen guard and an optional FPS counter (MelonPreferences).
- Optional, opt-in compatibility shim for the CrossCompat/Mono *Fannso's MoreNPCs* build on standard
  IL2CPP (`MoreNpcsCompatShim`, default off).
- Debug build only: on-screen HUD, hotkeys (F6-F10) and a `siesta ...` dev-console bridge.

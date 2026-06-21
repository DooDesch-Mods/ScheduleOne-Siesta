# Changelog

All notable changes to Siesta are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-06-21

A correctness pass so NPCs feel like vanilla under heavy population (MoreNPCs), plus a real per-NPC saving.

### Fixed
- NPCs no longer freeze mid-action when they go far off-screen. Police (pursuit/patrol/checkpoint/sentry), and
  civilians who are fleeing, in combat, cowering, calling the police, or buying are now exempt from the deep cull, so
  they keep behaving. This fixes the "NPC stood frozen until I walked up / interacted" reports.
- Waking a culled NPC is now reliable: the navmesh agent is restored and re-seated before the schedule re-evaluates,
  so NPCs resume their action immediately instead of standing inert, and the "not on navmesh" log spam is gone.
- Cosmetic culling no longer freezes a roaming NPC mid-route (the host re-enables its nav agent after hiding it).
- NPC models no longer pop on inside a building or vehicle when restored.
- NPCs in a conversation, and NPCs indoors, are no longer deep-culled.
- A one-off wake failure no longer pins an NPC to Full for the whole session (it retries after a short backoff).

### Added
- Awareness throttle on deep-culled NPCs: their 10Hz vision sweep is paused (the same call the game makes when an NPC
  enters a building), recovering a real per-NPC cost. Local and multiplayer-safe.
- Instant promote pass: a far NPC that suddenly comes near or on-screen is restored the same frame, so fast turns in a
  dense (MoreNPCs) crowd no longer leave a visibly frozen or hidden NPC.
- Daily reconcile on the natural midnight rollover so distant culled NPCs are never left a day behind.

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

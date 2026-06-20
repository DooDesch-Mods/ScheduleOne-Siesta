# Siesta - NPC Performance for Schedule I

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

> **Distant NPCs take a siesta so your FPS doesn't.** Siesta puts off-screen, far-away NPCs to
> sleep - hiding them and pausing their movement/schedule - then wakes them cleanly as you approach.
> Works on every NPC in the world, including ones added by other mods.

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)

## Features

- **Distance + visibility LOD for all NPCs.** Off-screen NPCs beyond a configurable distance are hidden
  (no render/animation cost); far, idle, non-essential ones also have their movement and schedule paused
  (the NavMeshAgent is the biggest per-NPC cost).
- **Functionality-first.** Dealers attending a deal, employees mid-task, customers in a deal, story/quest
  NPCs, anyone in dialogue, in a vehicle, KO'd or near any player are **never** paused - only ever hidden.
- **Clean wake-up.** A woken NPC re-enables its schedule, catches up to the current in-game time, resumes
  movement and repairs its NavMesh placement **before** it is shown again - no T-pose, no stuck agents.
- **Multiplayer safe.** Hiding is purely local per client; the movement/schedule pausing runs only on the
  host who owns NPC simulation, so there is no desync.
- **Cheap and self-tuning.** A small, fixed number of NPCs is re-checked per frame, so the mod's own cost
  stays flat regardless of how many NPCs exist.
- **MoreNPCs auto-compat.** Auto-detects an incompatible *Fannso's MoreNPCs* build on prefixed IL2CPP and
  stabilizes it so the game stops crash-spamming (opt-out).

## Requirements

- **Schedule I** (IL2CPP) with **MelonLoader 0.7.3+**.
- **S1API** (pulled in as a dependency).
- Optional: **Mod Manager & Phone App** for the in-game settings UI.

## How much FPS will I gain?

The win scales with how many NPCs are far from you - it's biggest in NPC-dense saves (e.g. with NPC-adding
mods) and small in sparse spots, because NPC simulation isn't always the dominant frame cost. Siesta only
ever removes work for NPCs you aren't looking at, so it's a safe, free-when-idle optimization.

## Settings

`EnableLod`, `EnableInMultiplayer`, `CosmeticDistance` (40), `DeepDistance` (80), `Hysteresis` (8),
`BudgetPerFrame` (32), `UseCosmeticCull`, `UseDeepCull`, `RespectOnScreen`, `ShowFpsCounter`,
`MoreNpcsAutoCompat`. Editable in the Mod Manager & Phone App UI or `UserData/MelonPreferences.cfg`.

## License

MIT. See the included LICENSE.md.

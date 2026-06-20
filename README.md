# Siesta - Let Distant NPCs Nap for More FPS

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

> Big crowds tanking your frames? Siesta puts off-screen, far-away NPCs to sleep - hiding them
> and pausing their movement and schedule - then wakes them cleanly as you get close. Works on
> every NPC in the world, including ones added by other mods. Built on
> [S1API](https://github.com/ifBars/S1API).

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)
![Status](https://img.shields.io/badge/status-working-brightgreen)

## Features

- **Distance + visibility LOD for every NPC.** Off-screen NPCs beyond a configurable distance are
  hidden (no render/animation cost); far, idle, non-essential ones also have their movement and
  schedule paused - the NavMeshAgent is the biggest per-NPC cost.
- **Functionality first.** Dealers attending a deal, employees mid-task, customers in a deal,
  story/quest NPCs, anyone in dialogue, in a vehicle, KO'd or near any player are **never** paused -
  only ever hidden. Nothing essential is skipped.
- **Clean wake-up.** A woken NPC re-enables its schedule, **catches up to the current in-game time**,
  resumes movement and repairs its NavMesh placement **before** it is shown again - no T-pose, no
  stuck agents. If a wake ever fails, that NPC is left fully running for the session.
- **Multiplayer safe.** Hiding is purely local per client; the movement/schedule pausing runs **only
  on the host** (who owns NPC simulation), so there is no desync.
- **Cheap and self-tuning.** A small, fixed number of NPCs is re-checked per frame, so the mod's own
  cost stays flat regardless of how many NPCs exist.
- **MoreNPCs auto-compat.** Auto-detects an incompatible *Fannso's MoreNPCs* build on prefixed IL2CPP
  and stabilizes it so the game stops crash-spamming (opt-out).
- **Works with anything that adds NPCs** - base game, employee mods, NPC-overhaul mods. The win scales
  with how many NPCs are far from you, so it shines in crowded saves.

## Requirements

| Component | Version / Source |
|-----------|------------------|
| Schedule I | IL2CPP (current Steam public build) |
| MelonLoader | `0.7.3+` |
| S1API | [ifBars/S1API_Forked](https://thunderstore.io/c/schedule-i/p/ifBars/S1API_Forked/) (NPC registry, save lifecycle) |
| Mod Manager & Phone App | [Prowiler, Nexus mods/397](https://www.nexusmods.com/schedule1/mods/397) - optional, for the in-game settings UI |

## Installation

### Recommended: a Thunderstore mod manager

Install with a mod manager (r2modman / Gale) from the Schedule I community; the dependencies
(MelonLoader, S1API) are pulled in automatically.

### Manual

1. Install **MelonLoader 0.7.3** for Schedule I.
2. Install **S1API** (its DLLs go in `Mods/` and `Plugins/` per its own instructions).
3. Drop **`Siesta.dll`** into your Schedule I `Mods/` folder.
4. (Optional) Install **Mod Manager & Phone App** for the in-game settings UI.

## Configuration

Settings live in the **Mod Manager & Phone App** UI in-game, or in `UserData/MelonPreferences.cfg`
under `Siesta_01_Main`. Changes apply live.

| Setting | Default | What it does |
|---|---|---|
| `EnableLod` | `true` | Master on/off. Off = fully vanilla (everything restored). |
| `EnableInMultiplayer` | `true` | Off = the mod does nothing in co-op. |
| `CosmeticDistance` | `40` | Off-screen NPCs beyond this (m) are hidden. |
| `DeepDistance` | `80` | Off-screen, idle, non-essential NPCs beyond this (m) also pause (host only). |
| `Hysteresis` | `8` | Dead-zone (m) around boundaries so NPCs don't flicker between states. |
| `BudgetPerFrame` | `32` | NPCs re-evaluated per frame (keeps the mod's cost flat). |
| `UseCosmeticCull` | `true` | Enable the hide tier. |
| `UseDeepCull` | `true` | Enable the pause-movement/schedule tier. |
| `RespectOnScreen` | `true` | Never cull an NPC roughly in view. |
| `ShowFpsCounter` | `false` | Tiny on-screen FPS readout (top-right). |
| `MoreNpcsAutoCompat` | `true` | Auto-detect MoreNPCs and stabilize it if its build is incompatible (see Compatibility). |

An on-screen HUD, hotkeys (F6-F10) and a `siesta ...` dev console exist only in development builds and
are not shipped in the release.

## How it works

Install it and play - there is nothing to do. Every NPC is continuously sorted into one of three tiers
by its distance to the nearest player and whether it's on screen:

- **Full** - near, on screen, or doing something important: vanilla, nothing changed.
- **Cosmetic** - off screen and mid-distance: renderer hidden (saves animation/draw), AI keeps running.
- **Deep** - off screen and far, and safe to pause: also pauses movement + schedule (host only).

The win scales with how many NPCs are far from you - it's biggest in NPC-dense saves and small in a
sparse spot, because NPC simulation isn't always the dominant frame cost. Siesta only ever removes work
for NPCs you aren't looking at, so it's a safe, free-when-idle optimization.

## Compatibility

- Works with any mod that adds NPCs (they all register in the same game NPC registry).
- **MoreNPCs auto-compat** (`MoreNpcsAutoCompat`, on by default): the CrossCompat/Mono build of
  *Fannso's MoreNPCs* throws a `TypeLoadException` every frame on a standard (prefixed) IL2CPP install
  (its NPCs still spawn). Siesta auto-detects this and - **only when that build would actually crash on
  your install** - neutralizes the crashing per-frame watcher so the game stays stable. MoreNPCs' NPCs
  are unaffected; it's a no-op when MoreNPCs is absent or already compatible. Turn it off to never apply.
- IL2CPP build only (current Steam public branch).

## Credits

- **DooDesch** - mod author.
- **[ifBars/S1API](https://github.com/ifBars/S1API)** - the modding API this is built on.
- **Prowiler** - Mod Manager & Phone App (in-game settings UI).

## License

Provided as-is under the [MIT License](LICENSE.md).

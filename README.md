# Siesta - Let Distant NPCs Nap for More FPS

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/siesta](https://support.doodesch.de/siesta).

📖 **Documentation:** [docs.doodesch.de/mods/siesta/](https://docs.doodesch.de/mods/siesta/)

> Big crowds tanking your frames? Siesta puts off-screen, far-away NPCs to sleep - hiding them
> and pausing their movement and schedule - then wakes them cleanly as you get close. Works on
> every NPC in the world, including ones added by other mods. Built on
> [S1API](https://github.com/ifBars/S1API).

![Version](https://img.shields.io/badge/version-1.2.2-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)
![Status](https://img.shields.io/badge/status-working-brightgreen)
[![Polyfill](https://polyfill.doomods.com/badge/siesta.svg)](https://polyfill.doomods.com/mod/siesta)

[![MLVScan Siesta.dll](https://mlvscan.com/attestations/att_D_bT7gsgOwmg7m4rM4R1Rrgn/badge.svg)](https://mlvscan.com/attestations/att_D_bT7gsgOwmg7m4rM4R1Rrgn)

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
- **Works with anything that adds NPCs** - base game, employee mods, NPC-overhaul mods. The win scales
  with how many NPCs are far from you, so it shines in crowded saves.

## Requirements

| Component | Version / Source |
|-----------|------------------|
| Schedule I | IL2CPP (current Steam public build) |
| MelonLoader | `0.7.3+` |
| S1API | [ifBars/S1API_Forked](https://thunderstore.io/c/schedule-i/p/ifBars/S1API_Forked/) (NPC registry, save lifecycle) |

## Installation

### Recommended: a Thunderstore mod manager

Install with a mod manager (r2modman / Gale) from the Schedule I community; the dependencies
(MelonLoader, S1API) are pulled in automatically.

### Manual

1. Install **MelonLoader 0.7.3** for Schedule I.
2. Install **S1API** (its DLLs go in `Mods/` and `Plugins/` per its own instructions).
3. Drop **`Siesta.dll`** into your Schedule I `Mods/` folder.

## Configuration

Settings live in `UserData/MelonPreferences.cfg`
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
| `ThrottleFarMovement` | `true` | Far NPCs, and NPCs stuck on a spot they cannot reach, check their walk 5 times a second instead of every physics step (host only). |
| `ShowFpsCounter` | `false` | Tiny on-screen FPS readout (top-right). |

An on-screen HUD, hotkeys (F6-F10) and a `siesta ...` dev console exist only in development builds and
are not shipped in the release.

## How it works

Install it and play - there is nothing to do. Every NPC is continuously sorted into one of three tiers
by its distance to the nearest player and whether it's on screen:

- **Full** - near, on screen, or doing something important: vanilla, nothing changed.
- **Cosmetic** - off screen and mid-distance: renderer hidden (saves animation/draw), AI keeps running.
- **Deep** - off screen and far, and safe to pause: also pauses movement + schedule (host only).

Two fixes run for every NPC, whatever its tier (host only):

- An NPC that is far away, or that stands on a spot it cannot reach, checks its walk 5 times a second
  instead of every physics step. A stuck NPC plans its whole path again in each check.
- An NPC on its way into a building does not ask for its path to the door again while that path is
  still being worked out.

The win scales with how many NPCs are far from you - it's biggest in NPC-dense saves and small in a
sparse spot, because NPC simulation isn't always the dominant frame cost. Siesta only ever removes work
for NPCs you aren't looking at, so it's a safe, free-when-idle optimization.

## Compatibility

- Works with any mod that adds NPCs (they all register in the same game NPC registry).
- If a mod's NPCs do not appear at all, that is not the LOD layer: it hides and pauses NPCs by
  distance and restores them as you approach, and it never prevents one from spawning. Set
  `EnableLod = false` in `UserData/MelonPreferences.cfg` to rule it out in one restart.
- **Fannso's MoreNPCs**: that mod ships a separate build per backend, and its Mono/CrossCompat build
  has one watcher that cannot run on a standard IL2CPP game. It sits in a per-frame loop, so it
  throws thousands of times a minute and fills the log. Siesta routes that single call through a
  guard and stops calling it once it has failed. Everything else MoreNPCs does is untouched. On a
  matching build nothing ever fails and the watcher runs normally.
- **More NPCs / More NPCs Forked**: it looks up its dealers and its office listing boards by searching every
  object of the scene, which froze the game for up to 6 s. Siesta hands it the same dealers from the
  game's NPC list and finds the boards with one quick lookup. Its 110 extra NPCs also made the stuck-NPC
  path planning above cost most of each frame. Right after loading a save the game still runs slowly for
  up to a minute while the extra NPCs settle in.
- IL2CPP build only (current Steam public branch).

## Credits

- **DooDesch** - mod author.
- **[ifBars/S1API](https://github.com/ifBars/S1API)** - the modding API this is built on.

## License

Provided as-is under the [MIT License](LICENSE.md).

# Siesta

**Distant NPCs take a siesta so your FPS doesn't.**

Repo: <https://github.com/DooDesch/ScheduleOne-Siesta>

Siesta is a performance mod for **Schedule I** (MelonLoader, IL2CPP). It applies distance- and
visibility-based level-of-detail (LOD) to **every** NPC in the world - base-game citizens, employees,
dealers, customers, and NPCs added by other mods - so the ones you can't see stop spending frame time.
Everything is fully reversible and designed to never break NPC behaviour.

## What it does

Each NPC is continuously sorted into one of three tiers based on its distance to the nearest player and
whether it's on screen:

| Tier | When | What happens |
|------|------|--------------|
| **Full** | near, on screen, or doing something important | vanilla - nothing changed |
| **Cosmetic** | off screen and mid-distance | renderer hidden (`NPC.SetVisible`) - saves animation/draw cost; AI keeps running |
| **Deep** | off screen and far, and safe to pause | also pauses movement + schedule (the NavMeshAgent is the biggest per-NPC cost) |

When you come back, a deep-culled NPC is woken cleanly: its schedule is re-enabled and **caught up to the
current in-game time** (`EnforceState`), its movement resumes, and its NavMesh placement is repaired before
it is shown again - so it ends up where it should be, no T-pose, no stuck agents.

## Functionality first

Deep culling (the only tier that touches AI) is **never** applied to an NPC that is doing something that
matters off screen. Protected NPCs are only ever hidden, never paused:

- dealers attending/holding a contract, employees mid-task, customers in or awaiting a deal
- story / unique / quest NPCs (`IsImportant`), NPCs in dialogue or a vehicle, KO'd or panicking NPCs
- anyone near **any** player

If a wake ever fails to place an NPC back on the NavMesh, that NPC is permanently kept Full for the
session instead of risking a broken state.

## Multiplayer

Siesta is co-op safe. Cosmetic culling is purely local (each client hides distant NPCs on its own screen,
nothing is networked). The deeper movement/schedule culling runs **only on the host**, who owns NPC
simulation - clients never pause a replica, so there's no desync.

## Performance expectations

The win scales with how many NPCs are far from you. In NPC-dense saves (e.g. with NPC-adding mods) the
gain is meaningful; in a sparse spot it can be small, because NPC simulation isn't always the dominant
frame cost. Siesta only ever removes work for NPCs you aren't looking at, so it's a safe, free-when-idle
optimization rather than a magic FPS doubler. It is most effective combined with other client-side
performance mods.

## Install

1. Install [MelonLoader](https://melonloader.co/) (IL2CPP) for Schedule I and run the game once.
2. Drop `Siesta.dll` into `Schedule I/Mods/`.
3. Launch. Settings live in `UserData/MelonPreferences.cfg` (category `Siesta_01_Main`) and apply on save;
   they also appear in the in-game Mod Manager & Phone App settings UI if you have it.

## Configuration

| Setting | Default | Meaning |
|---------|---------|---------|
| `EnableLod` | `true` | Master switch. Off = fully vanilla (everything restored). |
| `EnableInMultiplayer` | `true` | Off = the mod does nothing in co-op. |
| `CosmeticDistance` | `40` | Off-screen NPCs beyond this are hidden. |
| `DeepDistance` | `80` | Off-screen, idle, non-essential NPCs beyond this also pause (host only). |
| `Hysteresis` | `8` | Dead-zone (m) around boundaries to stop flicker. |
| `BudgetPerFrame` | `32` | NPCs re-evaluated per frame (flat cost). |
| `UseCosmeticCull` | `true` | Enable the hide tier. |
| `UseDeepCull` | `true` | Enable the pause-movement/schedule tier. |
| `RespectOnScreen` | `true` | Never cull an NPC roughly in view. |
| `ShowFpsCounter` | `false` | Tiny on-screen FPS readout. |
| `MoreNpcsAutoCompat` | `true` | Auto-detect MoreNPCs and stabilize it if its build is incompatible - see Compatibility. |

## Compatibility

Siesta works with any mod that adds NPCs (they all register in the same game registry).

**MoreNPCs auto-compat** (`MoreNpcsAutoCompat`, on by default): the CrossCompat/Mono build of *Fannso's
MoreNPCs* throws a `TypeLoadException` every frame on a standard (prefixed) IL2CPP install (its NPCs still
spawn via S1API). Siesta auto-detects this and - **only when that build would actually crash on your
install** - neutralizes the crashing per-frame watcher (`MoreNPCs.Core.OnUpdate`) so the game stays stable.
MoreNPCs' NPCs are unaffected. It is a no-op when MoreNPCs is absent or already compatible; turn the setting
off to never apply it. Restart to take effect.

## Building from source

Requires the .NET SDK and the Schedule I IL2CPP managed assemblies. References resolve from
`../Workspace/lib/il2cpp` (game + MelonLoader + S1API). Then:

```
dotnet build -c Release   # ships only the LOD layer
dotnet build -c Debug     # adds the on-screen HUD, hotkeys and the "siesta ..." dev console
```

The post-build step copies the DLL into the game's `Mods` folder.

## Credits & License

Created by DooDesch. Released under the [MIT License](LICENSE). Built with
[MelonLoader](https://melonloader.co/), [HarmonyX](https://github.com/BepInEx/HarmonyX) and
[S1API](https://github.com/ifBars/S1API).

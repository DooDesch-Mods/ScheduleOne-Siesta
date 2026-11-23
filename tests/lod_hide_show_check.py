# Checks Siesta's hide/show of ordinary NPCs in a running game, and that NPCs Siesta did not hide stay as they are.
# The slot must run a save with Siesta loaded (session.ps1 ... -Mods Siesta or -Build _wt-Siesta-...).
#
#     python -I tests/lod_hide_show_check.py --slot mptest2
#
# 1. "siesta full": every NPC body the game draws is counted (baseline).
# 2. "siesta auto" with the player at a fixed spot: NPCs far away must lose their body (Siesta hid them), the
#    near ones keep it.
# 3. The player walks up to one NPC Siesta hid: its body must come back (Show).
# 4. "siesta full" again: every body of the baseline is back (restore undoes exactly what Siesta changed).
# 5. NPCs whose body was off in the baseline (hidden by the game or by the mod that added them) must stay off in
#    every step.
# Prints PASS or FAIL per check, ends with "RESULT {json}" and exits 1 when any check fails.
import argparse
import json
import math
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "Workspace", "tools", "bridge"))
import s1  # noqa: E402

STAND = (58.0, 1.0, 18.0)   # in front of the town hall: NPCs near and far at noon
results = []


def check(name, ok, detail=""):
    results.append({"check": name, "ok": bool(ok), "detail": detail})
    print("%s %s%s" % ("PASS" if ok else "FAIL", name, " - " + detail if detail else ""), flush=True)


def world(game, cmd, args, timeout=30000):
    answer = game.world(cmd, args, timeout_ms=timeout)
    if not answer.get("ok", True) and "result" not in answer:
        raise SystemExit("bridge %s failed: %s" % (cmd, json.dumps(answer)[:400]))
    return answer.get("result", answer)


def strings(node):
    """Every string inside a bridge answer, whatever shape its captured log lines have."""
    if isinstance(node, str):
        yield node
    elif isinstance(node, dict):
        for v in node.values():
            yield from strings(v)
    elif isinstance(node, list):
        for v in node:
            yield from strings(v)


def siesta(game, sub):
    answer = game.run("siesta " + sub, settle_ms=1000)
    hits = [s.split("[siesta] ", 1)[1] for s in strings(answer.get("result", answer)) if "[siesta] " in s]
    print("  siesta %s -> %s" % (sub, hits[-1] if hits else "no [siesta] line"), flush=True)
    return hits


def bodies(game):
    """Per NPC root below @Managers/@NPCs: (drawn, position). Drawn = an active skinned body or impostor."""
    every = world(game, "find_world_objects", {"under": "@NPCs", "kind": "renderer", "limit": 6000}, 60000)
    active = world(game, "find_world_objects", {"under": "@NPCs", "kind": "renderer", "limit": 6000,
                                                "includeInactive": False}, 60000)
    out = {}
    for o in every.get("objects", []):
        parts = o.get("path", "").split("/")
        if len(parts) > 2 and parts[1] == "@NPCs" and o.get("type") == "SkinnedMeshRenderer":
            out.setdefault(parts[2], [False, o.get("bounds", {}).get("center")])
    for o in active.get("objects", []):
        parts = o.get("path", "").split("/")
        if len(parts) > 2 and parts[1] == "@NPCs" and (o.get("type") == "SkinnedMeshRenderer" or o.get("name") == "Impostor"):
            if parts[2] in out:
                out[parts[2]] = [True, o.get("bounds", {}).get("center") or out[parts[2]][1]]
    return out


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--slot", default="mptest2")
    p.add_argument("--settle", type=float, default=8.0, help="seconds for the LOD controller after each change")
    a = p.parse_args()
    game = s1.Game(a.slot)

    world(game, "set_time", {"time": 1200}, 8000)
    world(game, "teleport", {"x": STAND[0], "y": STAND[1], "z": STAND[2]}, 8000)
    answered = siesta(game, "status")
    check("siesta answers", bool(answered), answered[-1] if answered else "no [siesta] line - is Siesta loaded?")

    siesta(game, "full")
    time.sleep(a.settle)
    base = bodies(game)
    drawn_base = {n for n, (d, _) in base.items() if d}
    off_base = {n for n, (d, _) in base.items() if not d}
    print("  baseline: %d NPC roots, %d drawn, %d off" % (len(base), len(drawn_base), len(off_base)), flush=True)
    check("baseline has drawn NPCs", len(drawn_base) > 10, "%d drawn" % len(drawn_base))

    siesta(game, "auto")
    time.sleep(a.settle)
    status = siesta(game, "status")
    auto = bodies(game)
    hidden = sorted(n for n in drawn_base if n in auto and not auto[n][0])
    check("far NPCs hidden in auto", len(hidden) > 0, "%d of %d drawn NPCs hidden (%s); %s" % (
        len(hidden), len(drawn_base), ", ".join(hidden[:12]), status[-1] if status else ""))
    woke = [n for n in off_base if n in auto and auto[n][0]]
    check("NPCs off in the baseline stay off in auto", not woke, ", ".join(sorted(woke)))

    target = next((n for n in hidden if base[n][1]), None)
    if target:
        t = base[target][1]
        auto_pos = auto[target][1] or t
        world(game, "teleport", {"x": auto_pos[0] + 2.0, "y": auto_pos[1] + 1.0, "z": auto_pos[2] + 2.0}, 8000)
        time.sleep(a.settle)
        close = bodies(game)
        check("hidden NPC shown when the player comes close", close.get(target, [False])[0],
              "%s at %s" % (target, auto_pos))
        # Every NPC of the baseline within 20 m of the player keeps or gets back its body.
        near = [n for n in drawn_base if n in close and close[n][1]
                and math.dist((close[n][1][0], close[n][1][2]), (auto_pos[0], auto_pos[2])) < 20]
        near_drawn = [n for n in near if close[n][0]]
        check("near NPCs keep their body", near and len(near_drawn) == len(near),
              "%d of %d within 20 m drawn (%s)" % (len(near_drawn), len(near), ", ".join(sorted(near))))
        woke = [n for n in off_base if n in close and close[n][0]]
        check("NPCs off in the baseline stay off up close", not woke, ", ".join(sorted(woke)))
    else:
        check("hidden NPC shown when the player comes close", False, "no hidden NPC with a position")

    siesta(game, "full")
    time.sleep(a.settle)
    after = bodies(game)
    missing = sorted(n for n in drawn_base if n in after and not after[n][0])
    check("full restores every body of the baseline", not missing, ", ".join(missing[:10]))
    siesta(game, "auto")

    print("RESULT " + json.dumps({"slot": a.slot, "baselineDrawn": len(drawn_base), "baselineOff": len(off_base),
                                  "hiddenInAuto": len(hidden), "target": target, "checks": results}))
    sys.exit(0 if all(r["ok"] for r in results) else 1)


if __name__ == "__main__":
    main()

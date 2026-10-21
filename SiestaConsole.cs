using System;
using HarmonyLib;
using UnityEngine.AI;   // NavMeshHit (for the gather test command)
using Siesta.Compat;
using Siesta.Config;
using Siesta.Lod;

namespace Siesta
{
    /// <summary>
    /// DEBUG-only dev-console bridge (excluded from Release) so the Schedule1 MCP / run_console_command can
    /// drive A/B measurement headlessly. Commands are namespaced "siesta ...":
    ///   off|full   -> pin all NPCs to Full (no culling) - the baseline
    ///   auto       -> normal distance-based culling
    ///   cosmetic   -> pin all eligible to Cosmetic (hidden, AI untouched)
    ///   deep       -> pin all eligible to Deep (movement + schedule paused, host-gated)
    ///   restore    -> restore everything to vanilla and hold at Full
    ///   status     -> log tier counts + fps + mode
    ///   bounds     -> how many avatars still have a live LOD-bounds refresh timer
    /// </summary>
    internal static class SiestaConsole
    {
        private static int _lastFrame = -1;
        private static string _lastSig = "";

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;
            string[] p = new string[args.Count];
            for (int i = 0; i < args.Count; i++) p[i] = args[i];
            return Dispatch(p);
        }

        private static bool Dispatch(string[] p)
        {
            if (p.Length == 0 || !p[0].Equals("siesta", StringComparison.OrdinalIgnoreCase))
            {
                return false;   // not ours
            }

            // Both SubmitCommand overloads fire - dedupe the same command within one frame.
            string sig = string.Join(" ", p);
            int frame = Time.frameCount;
            if (frame == _lastFrame && sig == _lastSig) return true;
            _lastFrame = frame; _lastSig = sig;

            string cmd = p.Length > 1 ? p[1].ToLowerInvariant() : "status";
            try
            {
                switch (cmd)
                {
                    case "off":
                    case "full": LodController.Mode = LodController.Control.ForceFull; Log("mode = ForceFull (no culling / baseline)"); break;
                    case "auto": LodController.Mode = LodController.Control.Auto; Log("mode = Auto (distance culling)"); break;
                    case "cosmetic": LodController.Mode = LodController.Control.ForceCosmetic; Log("mode = ForceCosmetic"); break;
                    case "deep": LodController.Mode = LodController.Control.ForceDeep; Log("mode = ForceDeep (host-gated per NPC)"); break;
                    case "restore": LodController.RestoreAll("console restore"); LodController.Mode = LodController.Control.ForceFull; Log("restored all -> ForceFull"); break;
                    case "status": Status(); break;
                    case "why": Log("deep-cull reasons: " + LodRegistry.ReasonTally()); break;
                    case "bounds": BoundsUpdaters(); break;
                    case "bex": Exemptions.ExemptOnAnyBehaviour = BoolArg(p, 2, !Exemptions.ExemptOnAnyBehaviour); Log("ExemptOnAnyBehaviour = " + Exemptions.ExemptOnAnyBehaviour); break;
                    case "gather": Gather(p); break;
                    case "move": MoveCost(p); break;
                    case "throttle":
                        MoveThrottle.Off = !BoolArg(p, 2, MoveThrottle.Off);
                        Log($"movement throttle {(MoveThrottle.Off ? "OFF" : "ON")} (throttled now {MoveThrottle.Count})");
                        break;
                    case "mnf": MoreNpcsScanCompat.Probe(Log); break;
                    case "sib": Log(StayInBuildingRepath.TakeStats()); break;
                    case "agents": Agents(); break;
                    case "types": Types(); break;
                    case "bisect":
                        {
                            // siesta bisect <frames> [TypeA TypeB+TypeC ...]: one group per argument, '+' joins types.
                            int bf = p.Length > 2 && int.TryParse(p[2], out int v) ? v : 6;
                            string[][] groups = BisectGroups;
                            if (p.Length > 3)
                            {
                                groups = new string[p.Length - 3][];
                                for (int g = 3; g < p.Length; g++) groups[g - 3] = p[g].Split('+');
                            }
                            MelonLoader.MelonCoroutines.Start(Bisect(bf, groups));
                        }
                        break;
                    case "sibfix":
                        StayInBuildingRepath.Off = !BoolArg(p, 2, StayInBuildingRepath.Off);
                        Log($"stay-in-building repath fix {(StayInBuildingRepath.Off ? "OFF" : "ON")}");
                        break;
                    case "mnffix":
                        MoreNpcsScanCompat.Off = !BoolArg(p, 2, MoreNpcsScanCompat.Off);
                        Log($"More NPCs scan replacement {(MoreNpcsScanCompat.Off ? "OFF" : "ON")}");
                        break;
                    default: Log($"unknown '{cmd}'. Use: off|auto|cosmetic|deep|restore|status|why|bounds|bex|gather <m>|move [n]|throttle on|off|mnf|mnffix on|off|sib|sibfix on|off|agents|types|bisect <frames> [groups]"); break;
                }
            }
            catch (Exception e)
            {
                Log("error: " + e.Message);
            }
            return true;
        }

        /// <summary>
        /// DEBUG perf-test helper: teleport EVERY NPC onto a ring at a chosen distance around the local player, so the
        /// per-NPC cost can be measured cleanly per distance band (e.g. `siesta gather 10` = all near/Full = max cost;
        /// `siesta gather 60` = cosmetic band; `siesta gather 120` = deep band). Restores all NPCs to vanilla first so a
        /// paused/agent-disabled NPC warps cleanly, then warps onto the navmesh. Leaves the LOD mode unchanged - set the
        /// mode (siesta full|auto|deep) afterwards to A/B the cull benefit at that distance.
        /// </summary>
        private static void Gather(string[] p)
        {
            float dist = 10f;
            if (p.Length > 2 && float.TryParse(p[2], out float d)) dist = Mathf.Clamp(d, 2f, 300f);

            Player local = null;
            try { local = Player.Local; } catch { }
            if (local == null || local.transform == null) { Log("gather: no local player"); return; }
            Vector3 center = local.transform.position;

            var reg = NPCManager.NPCRegistry;
            if (reg == null) { Log("gather: no NPC registry"); return; }

            // Make every NPC vanilla (agent live, not paused/hidden) so the warp lands cleanly.
            LodController.RestoreAll("gather");

            int n;
            try { n = reg.Count; } catch { Log("gather: registry count failed"); return; }
            int moved = 0, failed = 0;
            for (int i = 0; i < n; i++)
            {
                NPC npc;
                try { npc = reg[i]; } catch { continue; }
                if (npc == null) continue;
                float ang = (i / (float)(n < 1 ? 1 : n)) * 6.2831853f;
                float r = dist + (i % 6) * 0.6f;   // slight radial spread so they don't all stack on one point
                Vector3 target = center + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                try
                {
                    NPCMovement mv = npc.Movement;
                    if (mv == null) { failed++; continue; }
                    if (mv.SmartSampleNavMesh(target, out NavMeshHit hit)) mv.Warp(hit.position);
                    else mv.Warp(target);
                    // HOLD them: live NPCs immediately path back to their schedule and disperse within seconds, so a
                    // clean per-distance measurement is impossible. PauseMovement freezes them at the ring (still
                    // rendered + animated + perceiving = the bulk of the per-NPC cost), so FPS reflects the cost of N
                    // NPCs at this distance. Release with `siesta restore` or `siesta auto`.
                    try { mv.SpeedController?.AddSpeedControl("siesta_gather", 0f, int.MaxValue, SpeedControl.EType.Absolute); }
                    catch (Exception e) { Core.Log?.Warning("gather: hold failed: " + e.Message); }
                    moved++;
                }
                catch { failed++; }
            }
            // Pin to Full so Siesta keeps them all rendered (no culling) for the held cost measurement.
            LodController.Mode = LodController.Control.ForceFull;
            Log($"gather: warped + HELD {moved}/{n} NPCs at a ~{dist:F0}m ring (failed {failed}), pinned Full. FPS now reflects {moved} NPCs at {dist:F0}m. Release: siesta restore | auto.");
        }

        /// <summary>
        /// How many avatars still have a running LOD-bounds refresh.
        ///
        /// <c>AvatarLODBoundsUpdater</c> sits on the avatar GameObject and arms its only timer in Awake
        /// (<c>InvokeRepeating("InfrequentUpdate", ..., 1f)</c>), and Awake does not run a second time. Since
        /// the cosmetic tier hides an NPC by deactivating exactly that object, the obvious worry is that the
        /// timer dies with it and the bounds then never refresh again - which would show up only when a
        /// corpse is dragged, because the updater ignores anything that is not a moved ragdoll.
        ///
        /// IT DOES NOT. Two full hide/show cycles over 134 avatars, 128 of them deactivated at once, left
        /// `active-DEAD` at zero every time - the invoke survives deactivation and keeps running afterwards.
        /// The command stays because that answer is worth being able to re-check on a new game build rather
        /// than re-deriving from forum lore, which is what pointed the wrong way in the first place.
        /// </summary>
        private static void BoundsUpdaters()
        {
            // includeInactive: the hidden avatars are the whole question, and the default overload skips them.
            var updaters = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.AvatarFramework.AvatarLODBoundsUpdater>(true);
            int liveActive = 0, deadActive = 0, inactive = 0;

            for (int i = 0; i < updaters.Length; i++)
            {
                var u = updaters[i];
                if (u == null) continue;
                if (!u.gameObject.activeInHierarchy) { inactive++; continue; }
                if (u.IsInvoking("InfrequentUpdate")) liveActive++; else deadActive++;
            }

            Log($"bounds updaters: total={updaters.Length} active-armed={liveActive} active-DEAD={deadActive} hidden={inactive}");
        }

        /// <summary>
        /// What one physics step of <c>NPCMovement.FixedUpdate</c> costs per NPC. Runs the step once for every
        /// registered NPC under a stopwatch and lists the most expensive ones with the state that decides the cost:
        /// an NPC that has a destination, is not moving, has no pending path and can move re-runs
        /// <c>UpdateDestination</c> -> <c>GetClosestReachablePoint</c> -> <c>NavMesh.CalculatePath</c> in every step
        /// ("repath"). Running the step once more than the engine would only advances the NPC's own timers.
        /// </summary>
        private static void MoveCost(string[] p)
        {
            int top = 10;
            if (p.Length > 2 && int.TryParse(p[2], out int t)) top = Mathf.Clamp(t, 1, 50);
            var reg = NPCManager.NPCRegistry;
            if (reg == null) { Log("move: no NPC registry"); return; }
            int n = reg.Count;
            var rows = new System.Collections.Generic.List<(double us, string line)>(n);
            var sw = new System.Diagnostics.Stopwatch();
            double total = 0;
            int repath = 0, disabled = 0, failed = 0;
            for (int i = 0; i < n; i++)
            {
                NPC npc;
                try { npc = reg[i]; } catch { failed++; continue; }
                if (npc == null) continue;
                try
                {
                    NPCMovement mv = npc.Movement;
                    if (mv == null) { failed++; continue; }
                    if (!mv.enabled) disabled++;
                    var agent = mv._agent;
                    bool hasDest = mv._hasDestination;
                    bool moving = mv.IsMoving;
                    bool pending = agent != null && agent.pathPending;
                    bool canMove = mv.CanMove;
                    bool onMesh = agent != null && agent.enabled && agent.isOnNavMesh;
                    bool stuck = hasDest && !moving && !pending && canMove;
                    if (stuck) repath++;
                    MoveThrottle.Bypass = true;
                    sw.Restart();
                    try { mv.FixedUpdate(); }
                    finally { sw.Stop(); MoveThrottle.Bypass = false; }
                    double us = sw.Elapsed.TotalMilliseconds * 1000.0;
                    total += us;
                    float destDist = hasDest ? Vector3.Distance(mv.FootPosition, mv.CurrentDestination) : 0f;
                    rows.Add((us, $"{us,7:F0}us {npc.ID} dest={(hasDest ? destDist.ToString("F1") + "m" : "-")} moving={moving} " +
                                  $"pending={pending} canMove={canMove} onMesh={onMesh} repath={stuck} throttled={MoveThrottle.IsThrottled(mv)}"));
                }
                catch (Exception e) { failed++; Core.Log?.Warning($"move: NPC {i} threw {e.GetType().Name}: {e.Message}"); }
            }
            rows.Sort((a, b) => b.us.CompareTo(a.us));
            Log($"move: {rows.Count} NPCs, one step = {total / 1000.0:F2} ms, repath-state={repath}, component-disabled={disabled}, failed={failed}");
            for (int i = 0; i < rows.Count && i < top; i++) Log("  " + rows[i].line);
        }

        // Component groups for the bisection, by IL2CPP type name.
        private static readonly string[][] BisectGroups =
        {
            new[] { "Animator" },
            new[] { "NavMeshAgent", "NPCMovement" },
            new[] { "VisionCone", "NPCAwareness" },
            new[] { "NPCScheduleManager", "NPCBehaviour" },
            new[] { "Avatar", "AvatarLookController", "AvatarAnimation" },
        };

        /// <summary>Per component group: mean frame time over a few frames with the group on, then with it disabled
        /// on every NPC; the group is re-enabled before the next one.</summary>
        private static System.Collections.IEnumerator Bisect(int frames, string[][] groups)
        {
            var reg = NPCManager.NPCRegistry;
            if (reg == null) { Log("bisect: no NPC registry"); yield break; }
            var all = new System.Collections.Generic.List<Behaviour>();
            for (int i = 0; i < reg.Count; i++)
            {
                try
                {
                    NPC npc = reg[i];
                    if (npc == null) continue;
                    foreach (Behaviour b in npc.GetComponentsInChildren<Behaviour>(true)) all.Add(b);
                }
                catch (Exception e) { Core.Log?.Warning($"bisect: NPC {i} threw {e.GetType().Name}: {e.Message}"); }
            }
            Log($"bisect: {all.Count} behaviours on {reg.Count} NPCs, {frames} frames per sample");
            foreach (string[] group in groups)
            {
                float on = 0f;
                for (int f = 0; f < frames; f++) { yield return null; on += Time.unscaledDeltaTime; }
                var off = new System.Collections.Generic.List<Behaviour>();
                foreach (Behaviour b in all)
                {
                    try
                    {
                        if (b == null || !b.enabled) continue;
                        if (Array.IndexOf(group, b.GetIl2CppType().Name) < 0) continue;
                        b.enabled = false;
                        off.Add(b);
                    }
                    catch (Exception e) { Core.Log?.Warning("bisect: disable threw " + e.Message); }
                }
                yield return null; yield return null;
                float dis = 0f;
                for (int f = 0; f < frames; f++) { yield return null; dis += Time.unscaledDeltaTime; }
                foreach (Behaviour b in off)
                {
                    try { if (b != null) b.enabled = true; }
                    catch (Exception e) { Core.Log?.Warning("bisect: restore threw " + e.Message); }
                }
                Log($"bisect {string.Join("+", group)}: {off.Count} off, frame {on / frames * 1000f:F0} ms on -> {dis / frames * 1000f:F0} ms off");
                yield return null; yield return null;
            }
            Log("bisect: done");
        }

        /// <summary>Enabled Behaviour types on all NPCs, most frequent first.</summary>
        private static void Types()
        {
            var reg = NPCManager.NPCRegistry;
            if (reg == null) { Log("types: no NPC registry"); return; }
            var count = new System.Collections.Generic.Dictionary<string, int>();
            for (int i = 0; i < reg.Count; i++)
            {
                try
                {
                    NPC npc = reg[i];
                    if (npc == null) continue;
                    foreach (Behaviour b in npc.GetComponentsInChildren<Behaviour>(false))
                    {
                        if (b == null || !b.enabled) continue;
                        string n = b.GetIl2CppType().Name;
                        count.TryGetValue(n, out int c);
                        count[n] = c + 1;
                    }
                }
                catch (Exception e) { Core.Log?.Warning($"types: NPC {i} threw {e.GetType().Name}: {e.Message}"); }
            }
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(count);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder("types (enabled, active):");
            for (int i = 0; i < list.Count && i < 40; i++) sb.Append(' ').Append(list[i].Key).Append('=').Append(list[i].Value);
            Log(sb.ToString());
        }

        /// <summary>Why agents stand still: tally of agent state over all NPCs, plus the NavMesh obstacles of the scene.</summary>
        private static void Agents()
        {
            var reg = NPCManager.NPCRegistry;
            if (reg == null) { Log("agents: no NPC registry"); return; }
            int on = 0, offMesh = 0, stopped = 0, hasPath = 0, pending = 0, complete = 0, partial = 0, invalid = 0,
                speed0 = 0, moving = 0, hasDest = 0, canMove = 0;
            for (int i = 0; i < reg.Count; i++)
            {
                try
                {
                    NPCMovement mv = reg[i]?.Movement;
                    NavMeshAgent a = mv?._agent;
                    if (a == null || !a.enabled) continue;
                    on++;
                    if (!a.isOnNavMesh) { offMesh++; continue; }
                    if (a.isStopped) stopped++;
                    if (a.hasPath) hasPath++;
                    if (a.pathPending) pending++;
                    switch (a.pathStatus)
                    {
                        case NavMeshPathStatus.PathComplete: complete++; break;
                        case NavMeshPathStatus.PathPartial: partial++; break;
                        default: invalid++; break;
                    }
                    if (a.speed < 0.01f) speed0++;
                    if (a.velocity.sqrMagnitude > 0.01f) moving++;
                    if (mv._hasDestination) hasDest++;
                    if (mv.CanMove) canMove++;
                }
                catch (Exception e) { Core.Log?.Warning($"agents: NPC {i} threw {e.GetType().Name}: {e.Message}"); }
            }
            int obs = 0, carving = 0, carvingMoving = 0;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<NavMeshObstacle>();
                foreach (NavMeshObstacle o in all)
                {
                    obs++;
                    if (o.carving) { carving++; if (!o.carveOnlyStationary) carvingMoving++; }
                }
            }
            catch (Exception e) { Core.Log?.Warning("agents: obstacle scan threw " + e.Message); }
            Log($"agents: enabled={on} offMesh={offMesh} stopped={stopped} hasPath={hasPath} pending={pending} " +
                $"complete={complete} partial={partial} invalid={invalid} speed0={speed0} moving={moving} " +
                $"hasDestination={hasDest} canMove={canMove} timeScale={Time.timeScale}; obstacles={obs} carving={carving} carveWhileMoving={carvingMoving}");
        }

        private static void Status()
        {
            LodRegistry.CountByTier(out int f, out int c, out int d);
            float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
            Log($"mode={LodController.Mode} fps={fps:F1} tracked={f + c + d} full={f} cosmetic={c} deep={d} " +
                $"mp={Net.IsMultiplayer()} host={Net.IsAuthoritative()} cos@{Preferences.CosmeticDistance:F0} deep@{Preferences.DeepDistance:F0}");
        }

        private static bool BoolArg(string[] p, int idx, bool toggleDefault)
        {
            if (p.Length <= idx) return toggleDefault;   // no arg => toggle
            string v = p[idx].ToLowerInvariant();
            if (v == "on" || v == "true" || v == "1" || v == "yes") return true;
            if (v == "off" || v == "false" || v == "0" || v == "no") return false;
            return toggleDefault;
        }

        private static void Log(string msg) => Core.Log?.Msg("[siesta] " + msg);
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), "SubmitCommand", new System.Type[] { typeof(string) })]
    internal static class Siesta_Console_SubmitCommand_String_Patch
    {
        private static bool Prefix(string args)
        {
            try { return !SiestaConsole.TryHandle(args); } catch { return true; }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), "SubmitCommand", new System.Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class Siesta_Console_SubmitCommand_List_Patch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try { return !SiestaConsole.TryHandle(args); } catch { return true; }
        }
    }
}

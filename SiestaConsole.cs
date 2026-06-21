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
                    case "bex": Exemptions.ExemptOnAnyBehaviour = BoolArg(p, 2, !Exemptions.ExemptOnAnyBehaviour); Log("ExemptOnAnyBehaviour = " + Exemptions.ExemptOnAnyBehaviour); break;
                    case "gather": Gather(p); break;
                    default: Log($"unknown '{cmd}'. Use: off|auto|cosmetic|deep|restore|status|why|bex|gather <m>"); break;
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
                    try { mv.PauseMovement(); } catch { }
                    moved++;
                }
                catch { failed++; }
            }
            // Pin to Full so Siesta keeps them all rendered (no culling) for the held cost measurement.
            LodController.Mode = LodController.Control.ForceFull;
            Log($"gather: warped + HELD {moved}/{n} NPCs at a ~{dist:F0}m ring (failed {failed}), pinned Full. FPS now reflects {moved} NPCs at {dist:F0}m. Release: siesta restore | auto.");
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

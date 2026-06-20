using System;
using HarmonyLib;
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
                    default: Log($"unknown '{cmd}'. Use: off|auto|cosmetic|deep|restore|status|why|bex"); break;
                }
            }
            catch (Exception e)
            {
                Log("error: " + e.Message);
            }
            return true;
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

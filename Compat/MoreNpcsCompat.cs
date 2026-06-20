using System;
using System.Reflection;
using HarmonyLib;

namespace Siesta.Compat
{
    /// <summary>
    /// Auto-detecting compatibility shim for "Fannso's MoreNPCs". Its CrossCompat/Mono build references game types
    /// as <c>ScheduleOne.*</c>, which only exist on a de-prefixed Il2Cpp interop. On a STANDARD (prefixed) install
    /// the interop is <c>Il2CppScheduleOne.*</c>, so MoreNPCs' per-frame watcher
    /// <c>PPHylandHandoverWarning.RefreshThrottled()</c> throws a TypeLoadException (HandoverScreen) every frame,
    /// spamming the log and starving the bridge. That method cannot be Harmony-patched (patching reads its body,
    /// re-triggering the type load), so we patch its caller <c>MoreNPCs.Core.OnUpdate()</c> - whose IL is only method
    /// calls (no broken type tokens) and is patchable. Skipping OnUpdate disables MoreNPCs' per-frame watchers, but
    /// its NPCs spawn via the backend-neutral S1API on save load regardless, so the mod runs stably.
    ///
    /// AUTO-DETECTION (scan-free, so it never triggers AccessTools' full-domain type-load warnings): the shim is
    /// applied ONLY when (a) the MoreNPCs assembly is loaded AND (b) the exact game type MoreNPCs hard-references
    /// (<c>ScheduleOne.UI.Handover.HandoverScreen</c>) does NOT resolve in the loaded Assembly-CSharp - i.e. the
    /// build would actually crash here. When MoreNPCs is absent, or already compatible (de-prefixed install), this
    /// is a no-op and MoreNPCs is left fully intact.
    /// </summary>
    internal static class MoreNpcsCompat
    {
        // The type MoreNPCs' per-frame watcher hard-references. If this resolves, MoreNPCs runs fine and we do nothing.
        private const string ProbeType = "ScheduleOne.UI.Handover.HandoverScreen";

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                // Cheap, direct assembly lookup by name - avoids AccessTools' full-domain type scan (which logs
                // ReflectionTypeLoadException warnings for some IL2CPP assemblies). No-op if MoreNPCs isn't installed.
                Assembly moreAsm = FindAssembly("MoreNPCs");
                if (moreAsm == null)
                {
                    return;
                }

                // Does the exact game type MoreNPCs hard-references resolve here? If yes, it works natively -> leave it.
                Assembly gameAsm = FindAssembly("Assembly-CSharp");
                if (gameAsm != null && gameAsm.GetType(ProbeType, false) != null)
                {
                    Core.Log?.Msg("[Siesta] MoreNPCs detected and compatible with this install - compat shim not needed.");
                    return;
                }

                Type core = moreAsm.GetType("MoreNPCs.Core", false);
                MethodInfo target = core?.GetMethod("OnUpdate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null)
                {
                    Core.Log?.Warning("[Siesta] MoreNPCs compat: MoreNPCs.Core.OnUpdate not found - skipped.");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(MoreNpcsCompat)
                    .GetMethod(nameof(SkipOriginal), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(target, prefix: prefix);
                Core.Log?.Msg("[Siesta] MoreNPCs (incompatible build for this IL2CPP install) auto-detected - compat shim applied: skipping MoreNPCs.Core.OnUpdate to stop the per-frame TypeLoadException storm; its NPCs still spawn via S1API.");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Siesta] MoreNPCs compat shim failed: " + e.Message);
            }
        }

        private static Assembly FindAssembly(string simpleName)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    if (asms[i].GetName().Name == simpleName) return asms[i];
                }
                catch { /* dynamic/odd assembly - skip */ }
            }
            return null;
        }

        // Returning false skips the original (void) method.
        private static bool SkipOriginal() => false;
    }
}

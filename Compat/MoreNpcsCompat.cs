using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Siesta.Compat
{
    /// <summary>
    /// Keeps "Fannso's MoreNPCs" from flooding the log on an install where one of its watchers cannot run.
    ///
    /// MoreNPCs ships separate builds for the two backends. Its CrossCompat/Mono build names game types
    /// <c>ScheduleOne.*</c>, which only resolve on a de-prefixed Il2Cpp interop; on a standard (prefixed)
    /// install the interop is <c>Il2CppScheduleOne.*</c>, so
    /// <c>PPHylandHandoverWarning.RefreshThrottled()</c> throws a TypeLoadException the moment it is JIT-ed.
    /// It is called from <c>MoreNPCs.Core.OnUpdate()</c>, so that is once per frame - measured at ~2300
    /// exceptions and half a megabyte of log in ten seconds.
    ///
    /// The one call is replaced with <see cref="Guarded"/>, which invokes it and, on the first failure,
    /// stops calling it for the session. Everything else MoreNPCs does per frame is untouched - its NPC
    /// unlocks, dialogue refresh, cartel watcher and building setup all keep running. On a correctly
    /// matched build nothing ever throws, so the watcher behaves exactly as MoreNPCs intends and this
    /// costs one extra static call per frame.
    ///
    /// Patching <c>RefreshThrottled</c> itself is not an option: Harmony emits the original body into its
    /// replacement, which resolves the very type token that cannot load. Redirecting the call site works
    /// because <c>OnUpdate</c>'s own IL is just five calls.
    /// </summary>
    internal static class MoreNpcsCompat
    {
        private const string WatcherType = "PPHylandHandoverWarning";
        private const string WatcherMethod = "RefreshThrottled";

        private static MethodInfo _watcher;   // MoreNPCs' PPHylandHandoverWarning.RefreshThrottled
        private static Action _invoke;
        private static bool _givenUp;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                // Direct lookup by assembly name - AccessTools' full-domain scan logs load warnings for some
                // IL2CPP assemblies. No-op when MoreNPCs is not installed.
                Assembly moreAsm = FindAssembly("MoreNPCs");
                if (moreAsm == null) return;

                Type core = moreAsm.GetType("MoreNPCs.Core", false);
                MethodInfo target = core?.GetMethod("OnUpdate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null)
                {
                    Core.Log?.Warning("[Siesta] MoreNPCs compat: MoreNPCs.Core.OnUpdate not found - skipped.");
                    return;
                }

                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(MoreNpcsCompat)
                    .GetMethod(nameof(Transpile), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Siesta] MoreNPCs compat failed: " + e.Message);
            }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo guard = typeof(MoreNpcsCompat)
                .GetMethod(nameof(Guarded), BindingFlags.Static | BindingFlags.NonPublic);

            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ins = code[i];
                if (ins.opcode != OpCodes.Call && ins.opcode != OpCodes.Callvirt) continue;
                if (!(ins.operand is MethodInfo m)) continue;
                if (m.Name != WatcherMethod) continue;
                if (m.DeclaringType == null || m.DeclaringType.Name != WatcherType) continue;
                if (m.GetParameters().Length != 0 || !m.IsStatic) continue;

                _watcher = m;
                ins.opcode = OpCodes.Call;
                ins.operand = guard;
                return code;
            }

            // MoreNPCs changed its update loop - leave it exactly as it is rather than guess.
            Core.Log?.Msg("[Siesta] MoreNPCs compat: no " + WatcherType + "." + WatcherMethod
                + " call in OnUpdate - nothing to guard, MoreNPCs left untouched.");
            return code;
        }

        /// <summary>Stands in for MoreNPCs' handover watcher: passes the call through, and after the first
        /// failure stops calling it so a build mismatch cannot throw once per frame for the whole session.</summary>
        private static void Guarded()
        {
            if (_givenUp) return;
            try
            {
                if (_invoke == null) _invoke = (Action)Delegate.CreateDelegate(typeof(Action), _watcher);
                _invoke();
            }
            catch (Exception e)
            {
                _givenUp = true;
                Core.Log?.Warning("[Siesta] MoreNPCs' " + WatcherType + " cannot run on this install ("
                    + e.GetType().Name + ") - Siesta will not call it again this session. This usually means the"
                    + " CrossCompat/Mono MoreNPCs build is installed on a standard IL2CPP game; the rest of"
                    + " MoreNPCs is unaffected.");
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
    }
}

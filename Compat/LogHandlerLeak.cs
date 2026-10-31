using System;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.Reporting;
using UnityEngine;

namespace Siesta.Compat
{
    /// <summary>
    /// Keeps the game from adding one more log handler every time a scene with a DebugLogCollector loads.
    ///
    /// The collector sits in the Menu, Main and Tutorial scenes. Its Awake subscribes HandleLog to
    /// Application.logMessageReceived and nothing ever unsubscribes it, so the handlers of destroyed collectors
    /// keep running. All of them append to the same static StringBuilder, which means every log line of the game
    /// costs one more string pass and is stored one more time per scene load, for the rest of the process.
    ///
    /// The first collector stays subscribed: HandleLog touches only static state, so it works after its scene is
    /// gone, and bug reports read the static log. Awake is skipped for the rest. Apart from the subscription it
    /// only creates the static log when it is missing, and HandleLog does that too.
    /// </summary>
    internal static class LogHandlerLeak
    {
        // Debug console switch for the A/B measurement, and the count it reports.
        internal static bool Off;
        internal static int Skipped;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo awake = AccessTools.Method(typeof(DebugLogCollector), nameof(DebugLogCollector.Awake));
                harmony.Patch(awake, prefix: new HarmonyMethod(typeof(LogHandlerLeak)
                    .GetMethod(nameof(BeforeAwake), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                Core.Log?.Warning("Log handler fix not applied, the game keeps adding handlers: " + e.Message);
            }
        }

        private static bool BeforeAwake()
        {
            if (Off) return true;
            try
            {
                if (!IsSubscribed()) return true;
                if (Skipped++ == 0) Core.Log?.Msg("Log handler fix active: the game's log collector stays subscribed once.");
                return false;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("Log handler fix skipped for this scene: " + e.Message);
                return true;
            }
        }

        private static bool IsSubscribed()
        {
            Application.LogCallback handlers = Application.s_LogCallbackHandler;
            if (handlers == null) return false;
            foreach (Il2CppSystem.Delegate handler in handlers.GetInvocationList())
            {
                if (handler.Target?.TryCast<DebugLogCollector>() != null) return true;
            }
            return false;
        }
    }
}

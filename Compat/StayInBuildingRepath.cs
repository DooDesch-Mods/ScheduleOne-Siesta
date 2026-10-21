using System;
using System.Diagnostics;
using HarmonyLib;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.NPCs.Schedules;
using Siesta.Lod;

namespace Siesta.Compat
{
    /// <summary>
    /// Stops <c>NPCEvent_StayInBuilding.OnActiveTick</c> from asking for a new path to the building door while the
    /// path it asked for last time is still being computed.
    ///
    /// The event walks its NPC to the door of the building it should stay in. Twice a second it calls
    /// <c>SetDestination(door)</c> again when the NPC is not moving. <c>NPCMovement.IsMoving</c> compares the agent's
    /// remaining distance, which is stale while the agent waits for its path, so a waiting NPC counts as not moving.
    /// Each repeat runs a synchronous <c>NavMesh.CalculatePath</c> to the door and replaces the pending path request
    /// of the agent. With the extra NPCs of "More NPCs" most of the town is in this event after a load, the path
    /// queue never empties, the NPCs never start walking and every tick repeats the whole town's path search.
    ///
    /// The prefix skips the tick only in that state: the event's door is the current destination and the agent's
    /// path to it is pending. The event's own end-of-time check runs as before.
    /// </summary>
    internal static class StayInBuildingRepath
    {
        private static bool _patched;
        private static bool _failed;

        // Debug console switch for the A/B measurement.
        internal static bool Off = false;

#if DEBUG
        private static int _ticks, _skipped, _pending, _notMoving, _gcrp;
        private static long _tickTicks, _tickMax, _gcrpTicks;
#endif

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(NPCEvent_StayInBuilding), nameof(NPCEvent_StayInBuilding.OnActiveTick));
                if (target == null)
                {
                    Core.Log?.Warning("Stay-in-building repath: NPCEvent_StayInBuilding.OnActiveTick not found, NPCs walking to a building keep re-asking for their path.");
                    return;
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(StayInBuildingRepath), nameof(Prefix))
#if DEBUG
                    , postfix: new HarmonyMethod(typeof(StayInBuildingRepath), nameof(Postfix))
#endif
                    );
                _patched = true;
#if DEBUG
                var gcrp = AccessTools.Method(typeof(NPCMovement), nameof(NPCMovement.GetClosestReachablePoint));
                if (gcrp != null)
                {
                    harmony.Patch(gcrp, prefix: new HarmonyMethod(typeof(StayInBuildingRepath), nameof(GcrpPrefix)),
                        postfix: new HarmonyMethod(typeof(StayInBuildingRepath), nameof(GcrpPostfix)));
                }
                else
                {
                    Core.Log?.Warning("Stay-in-building probe: NPCMovement.GetClosestReachablePoint not found, path search is not timed.");
                }
#endif
                Core.Log?.Msg("Stay-in-building repath: on.");
            }
            catch (Exception e)
            {
                Core.Log?.Error("Stay-in-building repath: patching failed, NPCs walking to a building keep re-asking for their path: " + e.Message);
            }
        }

        private static bool Prefix(NPCEvent_StayInBuilding __instance, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (!_patched || _failed) return true;
            try
            {
                NPC npc = __instance.npc;
                NPCMovement mv = npc != null ? npc.Movement : null;
                if (mv == null) return true;
                var agent = mv._agent;
                bool pending = agent != null && agent.pathPending;
#if DEBUG
                _ticks++;
                if (pending) _pending++;
                if (!mv.IsMoving) _notMoving++;
#endif
                if (Off || !pending || !__instance.IsActive) return true;
                if (__instance.InBuilding || __instance.IsEntering) return true;
                var building = __instance.Building;
                if (building == null) return true;
                Transform entry = __instance.GetEntryPoint();
                if (entry == null) return true;
                if (Vector3.Distance(mv.CurrentDestination, entry.position) > 2f) return true;
                // The event ends itself on this check; keep it running when its end time has come.
                if (NetworkSingleton<TimeManager>.InstanceExists
                    && NetworkSingleton<TimeManager>.Instance.CurrentTime == __instance.GetEndTime()) return true;
#if DEBUG
                _skipped++;
#endif
                return false;
            }
            catch (Exception e)
            {
                // Fail open for good: a throw here on every tick would cost more than the repeat it saves.
                _failed = true;
                Core.Log?.Error("Stay-in-building repath: check failed, turned off for this session: " + e.Message);
                LodLog.Vanilla("stay-in-building repath", 0, e);
                return true;
            }
        }

#if DEBUG
        private static void Postfix(long __state)
        {
            long dt = Stopwatch.GetTimestamp() - __state;
            _tickTicks += dt;
            if (dt > _tickMax) _tickMax = dt;
        }

        private static void GcrpPrefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        private static void GcrpPostfix(long __state)
        {
            _gcrp++;
            _gcrpTicks += Stopwatch.GetTimestamp() - __state;
        }

        /// <summary>Counts since the last call: ticks, skips, pending/not-moving at entry, time in the tick and in the
        /// path search.</summary>
        internal static string TakeStats()
        {
            double ms = 1000.0 / Stopwatch.Frequency;
            string s = $"stay-in-building ticks={_ticks} skipped={_skipped} pending={_pending} notMoving={_notMoving} "
                     + $"tickMs={_tickTicks * ms:F1} tickMax={_tickMax * ms:F2} closestPoint calls={_gcrp} ms={_gcrpTicks * ms:F1}"
                     + (Off ? " (fix off)" : "") + (_failed ? " (failed)" : "");
            _ticks = _skipped = _pending = _notMoving = _gcrp = 0;
            _tickTicks = _tickMax = _gcrpTicks = 0;
            return s;
        }
#endif
    }
}

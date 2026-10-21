using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Siesta.Lod
{
    /// <summary>
    /// Runs <c>NPCMovement.FixedUpdate</c> of far and stuck NPCs a few times a second instead of on every physics
    /// step.
    ///
    /// On the server that method checks every step whether the NPC reached its destination. An NPC that has a
    /// destination but no path and is not moving - the state an unreachable or blocked destination leaves it in -
    /// re-runs <c>GetClosestReachablePoint</c> (NavMesh.SamplePosition + NavMesh.CalculatePath + a new NavMeshPath)
    /// and <c>SetDestination</c> on every step. With the extra NPCs of "More NPCs" that took the step from about 2 us
    /// to about 57 us per NPC, 13 ms per physics step for 228 NPCs. A slow frame then gets more catch-up physics
    /// steps, each step costs the same 13 ms, and the game sinks to 2-3 fps.
    ///
    /// For an NPC farther than the cosmetic distance, a step every <see cref="Interval"/> seconds is enough: the
    /// NavMeshAgent moves it on its own between steps, and arrival is noticed a fraction of a second later. Steps
    /// are counted in physics time (<c>Time.fixedTime</c>), so the catch-up steps of a slow frame do not run a
    /// throttled NPC again. Nothing on the NPC is changed, so there is nothing to restore: removing the entry
    /// returns the NPC to the engine's own rate.
    ///
    /// A near NPC in that stuck state is throttled too. It stands still, so a re-plan a fraction of a second later
    /// cannot be seen; the controller releases it as soon as it moves again.
    /// </summary>
    internal static class MoveThrottle
    {
        internal const float Interval = 0.2f;

        // NPCMovement native pointer -> physics time of its next allowed step.
        private static readonly Dictionary<IntPtr, float> _due = new Dictionary<IntPtr, float>();
        private static bool _patched;
        private static int _skipped;
        private static int _ran;

        internal static int Count => _due.Count;

        // Debug console switches: Off stops throttling new NPCs (the controller releases the rest within a few
        // frames), Bypass lets a direct FixedUpdate call through for the per-NPC cost measurement.
        internal static bool Off = false;
        internal static bool Bypass = false;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(NPCMovement), nameof(NPCMovement.FixedUpdate));
                if (target == null)
                {
                    Core.Log?.Warning("Movement throttle: NPCMovement.FixedUpdate not found - far NPCs keep the engine's step rate.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(MoveThrottle), nameof(Prefix)));
                _patched = true;
                Core.Log?.Msg("Movement throttle: on (far NPCs step every " + Interval + " s).");
            }
            catch (Exception e)
            {
                Core.Log?.Error("Movement throttle: patching NPCMovement.FixedUpdate failed, far NPCs keep the engine's step rate: " + e.Message);
            }
        }

        internal static bool IsThrottled(NPCMovement mv) => mv != null && _due.ContainsKey(mv.Pointer);

        internal static void Throttle(NPCMovement mv)
        {
            if (!_patched || mv == null) return;
            IntPtr key = mv.Pointer;
            if (_due.ContainsKey(key)) return;
            // Spread the first steps over one interval so NPCs that are throttled in the same frame do not all step
            // in the same physics step afterwards.
            _due[key] = Time.fixedTime + UnityEngine.Random.Range(0f, Interval);
        }

        internal static void Release(NPCMovement mv)
        {
            if (mv == null) return;
            _due.Remove(mv.Pointer);
        }

        internal static void Clear() => _due.Clear();

        /// <summary>Steps run and skipped since the last call, for the telemetry line.</summary>
        internal static void TakeTally(out int ran, out int skipped)
        {
            ran = _ran; skipped = _skipped;
            _ran = 0; _skipped = 0;
        }

        private static bool Prefix(NPCMovement __instance)
        {
            if (_due.Count == 0 || Bypass) return true;
            try
            {
                IntPtr key = __instance.Pointer;
                if (!_due.TryGetValue(key, out float due)) return true;
                float now = Time.fixedTime;
                if (now < due) { _skipped++; return false; }
                _due[key] = now + Interval;
                _ran++;
                return true;
            }
            catch (Exception e)
            {
                // A throw here would skip the step for every NPC; fail open and say so once per kind.
                LodLog.Vanilla("movement throttle prefix", 0, e);
                return true;
            }
        }
    }
}

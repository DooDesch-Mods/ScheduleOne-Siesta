using System;
using System.Collections.Generic;
using System.Text;

namespace Siesta.Lod
{
    /// <summary>
    /// Tracks per-NPC mod state, keyed by Unity instance id. Owns the guarantee that NPCs are never left in a
    /// culled state: <see cref="RestoreAll"/> brings every tracked NPC back to vanilla (used on save / scene
    /// change / mod disable / quit). <see cref="Reset"/> just drops the table when the NPCs are already gone
    /// (scene unloaded).
    /// </summary>
    internal static class LodRegistry
    {
        private static readonly Dictionary<int, NpcModState> _states = new Dictionary<int, NpcModState>();

        internal static bool HasAny => _states.Count > 0;
        internal static int Count => _states.Count;

        internal static NpcModState GetOrAdd(int id, NPC npc)
        {
            if (!_states.TryGetValue(id, out NpcModState st))
            {
                st = new NpcModState(id, npc);
                _states[id] = st;
            }
            else
            {
                st.Npc = npc;   // keep the reference fresh for RestoreAll
            }
            return st;
        }

        /// <summary>Restore every tracked NPC to vanilla, then clear. NPCs must still be alive (save/disable).</summary>
        internal static void RestoreAll()
        {
            foreach (KeyValuePair<int, NpcModState> kv in _states)
            {
                NpcModState st = kv.Value;
                try
                {
                    if (st.Npc != null)
                    {
                        LodLevers.ForceFull(st.Npc, st);
                    }
                }
                catch { /* shutting down / NPC gone */ }
            }
            _states.Clear();
        }

        /// <summary>Drop the table without restoring (the NPCs have already been destroyed).</summary>
        internal static void Reset()
        {
            _states.Clear();
        }

        /// <summary>Diagnostic: tally the last deep-cull eligibility reason across tracked NPCs (null=ELIGIBLE).</summary>
        internal static string ReasonTally()
        {
            var d = new Dictionary<string, int>();
            foreach (KeyValuePair<int, NpcModState> kv in _states)
            {
                string r = kv.Value.ExemptReason ?? "ELIGIBLE";
                d.TryGetValue(r, out int c);
                d[r] = c + 1;
            }
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in d)
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append("  ");
            }
            return sb.ToString().Trim();
        }

        internal static void CountByTier(out int full, out int cosmetic, out int deep)
        {
            full = 0; cosmetic = 0; deep = 0;
            foreach (KeyValuePair<int, NpcModState> kv in _states)
            {
                switch (kv.Value.Tier)
                {
                    case LodState.Full: full++; break;
                    case LodState.Cosmetic: cosmetic++; break;
                    default: deep++; break;
                }
            }
        }
    }
}

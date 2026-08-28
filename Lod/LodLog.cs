using System;
using System.Collections.Generic;
using System.Text;

namespace Siesta.Lod
{
    /// <summary>
    /// Warning sink for throws that come out of the GAME's own code rather than ours - re-enabling a schedule
    /// runs the active NPC event, and an event with a broken reference (a cartel goon whose pool is gone)
    /// NREs on the spot. Those repeat: the same NPC is re-evaluated every frame for as long as it stays in
    /// range. MelonLoader appends a full IL2CPP stack trace to every Warning, so the naive path wrote ~25
    /// traces per second to console and disk - 231 of them in ten seconds in the log this was built from,
    /// on the main thread.
    ///
    /// Each distinct site+message is reported once in full and only counted after that, and the counts go out
    /// with the periodic telemetry line, so nothing is silently swallowed.
    /// </summary>
    internal static class LodLog
    {
        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        /// <summary>Report a throw that came from game code. First occurrence of a message is logged, the rest
        /// are tallied.</summary>
        internal static void Vanilla(string site, int npcId, Exception e)
        {
            string key = site + ": " + (e?.Message ?? "(no message)");
            _counts.TryGetValue(key, out int seen);
            _counts[key] = seen + 1;
            if (seen == 0)
            {
                Core.Log?.Warning($"game code threw in {site} (npc={npcId}): {key} - tier applied anyway; repeats are counted, not logged.");
            }
        }

        /// <summary>Repeat totals for the telemetry line, or null while nothing has thrown.</summary>
        internal static string Tally()
        {
            if (_counts.Count == 0)
            {
                return null;
            }
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in _counts)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(kv.Key).Append(" x").Append(kv.Value);
            }
            return sb.ToString();
        }

        internal static void Reset()
        {
            _counts.Clear();
        }
    }
}

using System;
using System.Globalization;
using System.IO;

namespace Siesta
{
    /// <summary>
    /// Crash-resilient diagnostic logging (DEBUG builds only - excluded from Release via the csproj). Rewrites
    /// a tiny heartbeat file every few frames so that after a hard native crash, Mods/Siesta/heartbeat.txt holds
    /// the last known LOD state (tier counts + fps + mp/host flags).
    /// </summary>
    internal static class DiagLog
    {
        private static string _heartbeatPath;
        private static bool _failed;

        private static void Ensure()
        {
            if (_heartbeatPath != null || _failed)
            {
                return;
            }
            try
            {
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "Siesta");
                Directory.CreateDirectory(dir);
                _heartbeatPath = Path.Combine(dir, "heartbeat.txt");
            }
            catch
            {
                _failed = true;
            }
        }

        internal static void Heartbeat(string msg)
        {
            Ensure();
            if (_heartbeatPath == null)
            {
                return;
            }
            try
            {
                File.WriteAllText(_heartbeatPath, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + msg);
            }
            catch { /* never let diagnostics throw */ }
        }
    }
}

#if SNITCH
using Snitch.Api;                 // Profiler + StateSnapshot
using Siesta.Lod;                 // LodRegistry

namespace Siesta.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch instrumentation for Siesta. Registers the mod's key gauges/state with the Snitch
    /// profiler (no-op when the Snitch host is absent). Compiled only when the SNITCH symbol is defined
    /// (Debug + EnableSnitch); excluded from Release entirely. See Workspace/build/Snitch.props.
    /// </summary>
    internal static class SnitchProbe
    {
        public static void Register()
        {
            // Headline gauge: how many NPCs are currently deep-culled (the expensive tier we recover).
            Profiler.RegisterCounter("Siesta.Deep",
                () => { LodRegistry.CountByTier(out _, out _, out int d); return d; }, "npcs");

            // Full distribution of tracked NPCs by LOD tier.
            Profiler.RegisterStateProvider("Siesta", () =>
            {
                LodRegistry.CountByTier(out int f, out int c, out int d);
                return new StateSnapshot { Title = "Siesta LOD" }
                    .Add("full", f)
                    .Add("cosmetic", c)
                    .Add("deep", d);
            });
        }
    }
}
#endif

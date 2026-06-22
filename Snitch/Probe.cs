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
            // Total tracked NPCs - what the unbudgeted O(N) promote pre-pass scans every frame.
            Profiler.RegisterCounter("Siesta.Tracked",
                () => { LodRegistry.CountByTier(out int f, out int c, out int d); return f + c + d; }, "npcs");

            // Full distribution of tracked NPCs by LOD tier.
            Profiler.RegisterStateProvider("Siesta", () =>
            {
                LodRegistry.CountByTier(out int f, out int c, out int d);
                return new StateSnapshot { Title = "Siesta LOD" }
                    .Add("full", f)
                    .Add("cosmetic", c)
                    .Add("deep", d);
            });

            // ----- ablation levers ('snitch ablate <name>') -----
            // POLARITY NOTE: 'siesta.cosmetic'/'siesta.deep' are the OPPOSITE of a normal "disable my subsystem"
            // lever - applying them forces MORE culling ON, so frame time DROPS. The reported delta is therefore the
            // frame time RECOVERED by hiding NPCs (= the native skinned-mesh + sun-shadow render cost), NOT a cost
            // Siesta adds. This is the headline: the ceiling of what culling can ever buy on this scene.
            Profiler.RegisterAblationLever("siesta.cosmetic",
                apply: () => LodController.Mode = LodController.Control.ForceCosmetic,
                restore: () => LodController.Mode = LodController.Control.Auto);
            // Deep tier is host-authority-gated (a no-op on connected clients) and reversible via the wake-on-restore
            // path; measures the extra recovered on top of cosmetic (NPC sim throttle + schedule disable).
            Profiler.RegisterAblationLever("siesta.deep",
                apply: () => LodController.Mode = LodController.Control.ForceDeep,
                restore: () => LodController.Mode = LodController.Control.Auto);
            // 'siesta.lodsim' is a normal lever: applying it stops Siesta's LOD work, so its delta should read ~0 -
            // confirming the LOD sim is not itself a cost (the cost is the NPC render that culling recovers).
            Profiler.RegisterAblationLever("siesta.lodsim",
                apply: () => LodController.SimDisabled = true,
                restore: () => LodController.SimDisabled = false);
        }
    }
}
#endif

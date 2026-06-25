#if SNITCH
using Snitch.Api;                 // Profiler + Panel + StateSnapshot
using Siesta.Compat;              // Net (mp/host status)
using Siesta.Config;              // Preferences (distances + lever toggles)
using Siesta.Lod;                 // LodController + LodRegistry + LodState

namespace Siesta.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch instrumentation for Siesta. Builds the mod's Snitch panel ("Siesta LOD") with its live
    /// counters, the LOD-tier distribution, a mode/multiplayer readout, the controls that used to live on F7-F10,
    /// the panel log, and the ablation levers. Auto-discovered + called by the Snitch host on bind (a no-op when
    /// the host is absent). Compiled only when the SNITCH symbol is defined (Debug + EnableSnitch); excluded from
    /// Release entirely. See Workspace/build/Snitch.props.
    /// </summary>
    internal static class SnitchProbe
    {
        public static void Register()
        {
            // The panel id MUST be the same prefix the counters/state already use ("Siesta") so the host groups
            // "Siesta.Deep", "Siesta.Tracked" and the state distribution inside this panel automatically.
            Panel p = Profiler.RegisterPanel("Siesta", "Siesta LOD");

            // ----- live gauges -----
            // Headline gauge: how many NPCs are currently deep-culled (the expensive tier we recover).
            p.Counter("Deep",
                () => { LodRegistry.CountByTier(out _, out _, out int d); return d; }, "npcs");
            // Total tracked NPCs - what the unbudgeted O(N) promote pre-pass scans every frame.
            p.Counter("Tracked",
                () => { LodRegistry.CountByTier(out int f, out int c, out int d); return f + c + d; }, "npcs");

            // Full distribution of tracked NPCs by LOD tier (shows as the panel's state block).
            p.State(() =>
            {
                LodRegistry.CountByTier(out int f, out int c, out int d);
                return new StateSnapshot { Title = "Siesta LOD" }
                    .Add("full", f)
                    .Add("cosmetic", c)
                    .Add("deep", d);
            });

            // ----- free-text readout (what the old on-screen HUD showed: mode + MP/host + distances/levers) -----
            p.Text(() =>
            {
                LodRegistry.CountByTier(out int f, out int c, out int d);
                return $"Mode: {LodController.Mode}\n"
                     + $"MP: {(Net.IsMultiplayer() ? "yes" : "no")}   host: {(Net.IsAuthoritative() ? "yes" : "no")}\n"
                     + $"tracked {f + c + d}   full {f}  cosmetic {c}  deep {d}\n"
                     + $"cosmetic@{Preferences.CosmeticDistance:F0}m  deep@{Preferences.DeepDistance:F0}m  budget {Preferences.BudgetPerFrame}/f\n"
                     + $"levers: cosmetic={(Preferences.UseCosmeticCull ? "on" : "off")}  deep={(Preferences.UseDeepCull ? "on" : "off")}";
            });

            // ----- controls (the in-panel replacements for the removed F7-F10 hotkeys) -----
            // Each sets the persistent LOD mode (so it sticks across frames) and applies it immediately. The
            // counters/text above update live, so the effect is visible right away in the panel.
            p.Action("Force Full", () =>
            {
                LodController.Mode = LodController.Control.ForceFull;
                LodController.ForceAll(LodState.Full);
                p.Write("forced all -> Full (no culling / baseline)");
            });
            p.Action("Force Cosmetic", () =>
            {
                LodController.Mode = LodController.Control.ForceCosmetic;
                LodController.ForceAll(LodState.Cosmetic);
                p.Write("forced all -> Cosmetic");
            });
            p.Action("Force Deep", () =>
            {
                LodController.Mode = LodController.Control.ForceDeep;
                LodController.ForceAll(LodState.Deep);
                p.Write("forced all -> Deep (host-gated per NPC)");
            });
            p.Action("Restore Auto", () =>
            {
                LodController.RestoreAll("panel restore");
                LodController.Mode = LodController.Control.Auto;
                p.Write("restored all -> Auto (distance culling)");
            });

            // Live toggle for the LOD sim itself (same flag the 'siesta.lodsim' lever drives): off = stop all LOD
            // work this frame, NPCs left in their current tier. Confirms the LOD sim is near-zero cost.
            p.Toggle("Disable LOD sim", () => LodController.SimDisabled, v => LodController.SimDisabled = v);

            // Show this panel's own log channel (the Write(...) lines above and any Profiler.Log("Siesta", ...)).
            p.Log();

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

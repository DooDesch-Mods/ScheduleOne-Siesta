using MelonLoader;
using UnityEngine;

namespace Siesta.Config
{
    /// <summary>
    /// MelonPreferences wrapper. The category id is prefixed with the mod name ("Siesta_...") so it is
    /// auto-detected by the "Mod Manager &amp; Phone App" settings UI. All entries (the LOD layer plus the
    /// optional FPS counter) ship in every build.
    /// </summary>
    internal static class Preferences
    {
        private const string CategoryId = "Siesta_01_Main";

        private static MelonPreferences_Category _category;

        // ----- always-compiled LOD layer entries -----
        private static MelonPreferences_Entry<bool> _enableLod;
        private static MelonPreferences_Entry<bool> _enableInMp;
        private static MelonPreferences_Entry<float> _cosmeticDistance;
        private static MelonPreferences_Entry<float> _deepDistance;
        private static MelonPreferences_Entry<float> _hysteresis;
        private static MelonPreferences_Entry<int> _budgetPerFrame;
        private static MelonPreferences_Entry<bool> _useCosmeticCull;
        private static MelonPreferences_Entry<bool> _useDeepCull;
        private static MelonPreferences_Entry<bool> _respectOnScreen;
        private static MelonPreferences_Entry<bool> _showFps;
        private static MelonPreferences_Entry<bool> _moreNpcsCompat;

        internal static void Initialize()
        {
            if (_category != null)
            {
                return;
            }

            _category = MelonPreferences.CreateCategory(CategoryId, "Siesta (NPC Performance)");

            _enableLod = Create("EnableLod", true, "Enable NPC LOD",
                "Master switch. When ON (default), NPCs that are off-screen and far from you are culled to recover " +
                "performance, and restored as you approach. Turn OFF to run fully vanilla (everything restored).");
            _enableInMp = Create("EnableInMultiplayer", true, "Enable in multiplayer",
                "ON (default): in a co-op session, cosmetic culling still runs locally (safe - it only hides distant " +
                "NPCs on YOUR screen), and the deeper movement/schedule culling runs ONLY on the host (who owns NPC " +
                "simulation). OFF: the mod does nothing in multiplayer.");
            _cosmeticDistance = Create("CosmeticDistance", 40f, "Cosmetic cull distance (m)",
                "Off-screen NPCs farther than this are hidden (renderer off) to save animation/draw cost. They keep " +
                "moving and behaving normally. Lower = more FPS / nearer horizon. Clamped 15-300.",
                new MelonLoader.Preferences.ValueRange<float>(15f, 300f));
            _deepDistance = Create("DeepDistance", 80f, "Deep cull distance (m)",
                "Off-screen, idle, non-essential NPCs farther than this also have their movement + schedule paused " +
                "(the biggest saving). They catch up to their correct schedule position when you return. Dealers/" +
                "employees/customers mid-task, story NPCs, and anyone near any player are never deep-culled. " +
                "Host-only in multiplayer. Clamped 30-500.",
                new MelonLoader.Preferences.ValueRange<float>(30f, 500f));
            _hysteresis = Create("Hysteresis", 8f, "Hysteresis margin (m)",
                "Dead-zone around each distance boundary so NPCs don't flicker between states when you stand near an " +
                "edge. Clamped 0-50.",
                new MelonLoader.Preferences.ValueRange<float>(0f, 50f));
            _budgetPerFrame = Create("BudgetPerFrame", 32, "NPCs re-evaluated per frame",
                "The mod re-checks this many NPCs each frame on a rolling cursor (so the whole population is covered " +
                "every few frames) - keeps the mod's own cost flat. Lower = cheaper but slower to react. Clamped 4-256.",
                new MelonLoader.Preferences.ValueRange<int>(4, 256));
            _useCosmeticCull = Create("UseCosmeticCull", true, "Use cosmetic culling (hide distant NPCs)",
                "ON (default): hide off-screen distant NPCs (renderer off). Purely cosmetic and always safe. " +
                "OFF: never hide NPCs (disables the cheapest, safest tier).");
            _useDeepCull = Create("UseDeepCull", true, "Use deep culling (pause movement + schedule)",
                "ON (default): also pause movement + schedule for far, idle, non-essential NPCs (recovers the biggest " +
                "cost, the NavMeshAgent). Fully reversible with schedule catch-up. OFF: cosmetic culling only " +
                "(maximally conservative - zero AI impact, smaller FPS gain).");
            _respectOnScreen = Create("RespectOnScreen", true, "Never cull on-screen NPCs",
                "ON (default): an NPC roughly in front of the camera is never culled, even when far, so you never see " +
                "a visible NPC freeze or pop. Recommended ON.");
            _showFps = Create("ShowFpsCounter", false, "Show FPS counter",
                "Small on-screen FPS readout (top-right). OFF by default. Applies live.");
            _moreNpcsCompat = Create("MoreNpcsAutoCompat", true, "MoreNPCs auto-compat",
                "ON (default). Siesta auto-detects \"Fannso's MoreNPCs\" and, ONLY if its build is incompatible with " +
                "this IL2CPP install (it would throw a TypeLoadException every frame), neutralizes its crashing " +
                "per-frame watcher (MoreNPCs.Core.OnUpdate) so the game stays stable - MoreNPCs' NPCs still spawn via " +
                "S1API. No effect if MoreNPCs is absent or already compatible. Turn OFF to never apply it. Requires a " +
                "game restart to take effect.");
        }

        private static MelonPreferences_Entry<T> Create<T>(string id, T def, string name, string desc = null,
            MelonLoader.Preferences.ValueValidator validator = null)
        {
            return validator == null
                ? _category.CreateEntry(id, def, name, desc)
                : _category.CreateEntry(id, def, name, desc, false, false, validator);
        }

        // ----- accessors (always compiled) -----

        internal static bool EnableLod => _enableLod?.Value ?? true;
        internal static bool EnableInMultiplayer => _enableInMp?.Value ?? true;
        internal static float CosmeticDistance => Mathf.Clamp(_cosmeticDistance?.Value ?? 40f, 15f, 300f);
        internal static float DeepDistance => Mathf.Clamp(_deepDistance?.Value ?? 80f, 30f, 500f);
        internal static float Hysteresis => Mathf.Clamp(_hysteresis?.Value ?? 8f, 0f, 50f);
        internal static int BudgetPerFrame => Mathf.Clamp(_budgetPerFrame?.Value ?? 32, 4, 256);
        internal static bool UseCosmeticCull => _useCosmeticCull?.Value ?? true;
        internal static bool UseDeepCull => _useDeepCull?.Value ?? true;
        internal static bool RespectOnScreen => _respectOnScreen?.Value ?? true;
        internal static bool ShowFpsCounter => _showFps?.Value ?? false;
        internal static bool MoreNpcsCompat => _moreNpcsCompat?.Value ?? true;
    }
}

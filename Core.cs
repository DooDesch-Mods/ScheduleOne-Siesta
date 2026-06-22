using System;
using Il2CppScheduleOne.GameTime;   // TimeManager (onDayPass)
using MelonLoader;
using S1API.Lifecycle;
using Siesta.Compat;
using Siesta.Config;
using Siesta.Lod;
#if SNITCH
using Snitch.Api;                 // Profiler section timing (Debug + EnableSnitch only; no-op when host absent)
#endif

[assembly: MelonInfo(typeof(Siesta.Core), "Siesta", "1.1.0", "DooDesch", "https://github.com/DooDesch/ScheduleOne-Siesta")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("ModManager&PhoneApp")]

namespace Siesta
{
    /// <summary>
    /// MelonLoader entry point for the Siesta performance mod. Every in-world frame it drives the LOD
    /// controller, which hides off-screen/far NPCs and (host-only) pauses far idle non-essential NPCs to
    /// recover FPS, restoring them as the player approaches. NPCs are always restored to vanilla before any
    /// save / scene change / quit so the game never persists a culled NPC. DEBUG builds add an on-screen HUD,
    /// dev hotkeys and a disk-flushed heartbeat; Release ships only the LOD layer + a compact telemetry line.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private bool _inWorld;
        private float _teleElapsed; private int _teleFrames; private float _teleMaxDt;

        // onDayPass -> RestoreAll("new day"). Natural midnight rollover (no sleep/save) leaves distant deep-culled
        // NPCs "a day behind" until visited; reconcile everyone once per day. Delegate kept so we can unsubscribe.
        private Il2CppSystem.Action _onDayPass;
        private bool _dayPassHooked;
#if DEBUG
        private int _frame;
#endif

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();

#if DEBUG
            // DEBUG-only dev-console bridge ("siesta ...") for headless A/B measurement via the Schedule1 MCP.
            try { HarmonyInstance.PatchAll(); } catch (Exception e) { Log.Warning("[Siesta] Harmony patch failed: " + e.Message); }
#endif

            // Optional compat (auto-detect, default ON; opt-out via the MoreNpcsAutoCompat preference): if an
            // incompatible MoreNPCs build is present, neutralize its per-frame crashing watcher on this IL2CPP
            // install. Apply() itself only acts when MoreNPCs is detected AND would actually crash here.
            if (Preferences.MoreNpcsCompat) Compat.MoreNpcsCompat.Apply(HarmonyInstance);

            // Safety: never let the game serialize a culled/paused NPC, and never leave them culled across a
            // scene change. Restore everything to vanilla first; the next in-world frame re-culls as needed.
            GameLifecycle.OnSaveStart += () => LodController.RestoreAll("save starting");
            GameLifecycle.OnPreSceneChange += () => LodController.RestoreAll("scene changing");

#if DEBUG
            Log.Msg("Siesta v1.1.0 (DEBUG) - NPC LOD active. Hotkeys: F6 HUD, F7 all->Full, F8 ->Cosmetic, F9 ->Deep(host), F10 restore-all.");
#else
            Log.Msg("Siesta v1.1.0 - NPC LOD active.");
#endif
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _inWorld = sceneName == "Main";
            if (_inWorld) HookDayPass();
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            _inWorld = false;
            UnhookDayPass();
            LodController.Reset();   // NPCs are gone with the scene - just drop tracking
        }

        public override void OnUpdate()
        {
            if (!_inWorld)
            {
                return;
            }

            // TimeManager may not exist the instant the scene loads; keep trying until the hook takes (idempotent).
            if (!_dayPassHooked) HookDayPass();

#if DEBUG
            PollHotkeys();
#endif

#if SNITCH
            using (Profiler.Sample("Siesta.Lod")) LodController.Tick();
#else
            LodController.Tick();
#endif
            TelemetryTick();

#if DEBUG
            _frame++;
            if ((_frame & 3) == 0)
            {
                LodRegistry.CountByTier(out int f, out int c, out int d);
                float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
                DiagLog.Heartbeat($"frame={_frame} full={f} cos={c} deep={d} fps={fps:F1} mp={Net.IsMultiplayer()} host={Net.IsAuthoritative()}");
            }
#endif
        }

        public override void OnGUI()
        {
#if DEBUG
            UI.DebugHud.Draw();
#endif
            if (!_inWorld) return;
            if (Preferences.ShowFpsCounter) UI.FpsCounter.Draw();
        }

        public override void OnApplicationQuit()
        {
            UnhookDayPass();
            LodController.RestoreAll("application quit");
        }

        public override void OnDeinitializeMelon()
        {
            UnhookDayPass();
            LodController.RestoreAll("melon unload");
        }

        // ----- daily reconcile hook -----

        private void HookDayPass()
        {
            if (_dayPassHooked) return;
            try
            {
                if (!NetworkSingleton<TimeManager>.InstanceExists) return;
                TimeManager tm = NetworkSingleton<TimeManager>.Instance;
                if (tm == null) return;
                _onDayPass = (Il2CppSystem.Action)(() => LodController.RestoreAll("new day"));
                tm.onDayPass += _onDayPass;
                _dayPassHooked = true;
            }
            catch (Exception e)
            {
                Log.Warning("[Siesta] onDayPass hook failed: " + e.Message);
            }
        }

        private void UnhookDayPass()
        {
            if (!_dayPassHooked) return;
            try
            {
                if (NetworkSingleton<TimeManager>.InstanceExists)
                {
                    TimeManager tm = NetworkSingleton<TimeManager>.Instance;
                    if (tm != null && _onDayPass != null) tm.onDayPass -= _onDayPass;
                }
            }
            catch { /* instance gone during teardown -> nothing to detach */ }
            finally { _onDayPass = null; _dayPassHooked = false; }
        }

        // Compact periodic status line (~every 15s) - the one window into the running layer for Release support.
        private void TelemetryTick()
        {
            float dt = Time.unscaledDeltaTime;
            _teleElapsed += dt;
            _teleFrames++;
            if (dt > _teleMaxDt) _teleMaxDt = dt;
            if (_teleElapsed < 15f) return;

            try
            {
                float meanFps = _teleFrames / _teleElapsed;
                float minFps = _teleMaxDt > 0f ? 1f / _teleMaxDt : 0f;
                LodRegistry.CountByTier(out int f, out int c, out int d);
                Log.Msg($"[telemetry] fps={meanFps:F0} (min {minFps:F0})  npcs={f + c + d}  full={f} cosmetic={c} deep={d}");
            }
            catch { }
            finally { _teleElapsed = 0f; _teleFrames = 0; _teleMaxDt = 0f; }
        }

#if DEBUG
        private void PollHotkeys()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F6)) Preferences.SetShowHud(!Preferences.ShowHud);
                if (Input.GetKeyDown(KeyCode.F7)) { LodController.ForceAll(LodState.Full); Log.Msg("[Siesta] forced all -> Full"); }
                if (Input.GetKeyDown(KeyCode.F8)) { LodController.ForceAll(LodState.Cosmetic); Log.Msg("[Siesta] forced all -> Cosmetic"); }
                if (Input.GetKeyDown(KeyCode.F9)) { LodController.ForceAll(LodState.Deep); Log.Msg("[Siesta] forced all -> Deep (host-gated)"); }
                if (Input.GetKeyDown(KeyCode.F10)) { LodController.RestoreAll("hotkey panic restore"); Log.Msg("[Siesta] restore-all"); }
            }
            catch (Exception e)
            {
                Log.Warning("[Siesta] hotkey error: " + e.Message);
            }
        }
#endif
    }
}

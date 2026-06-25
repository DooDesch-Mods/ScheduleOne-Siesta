using System;
using System.Collections.Generic;
using Siesta.Compat;
using Siesta.Config;
#if SNITCH
using Snitch.Api;                 // Profiler sub-section timing (Debug + EnableSnitch only)
#endif

namespace Siesta.Lod
{
    /// <summary>
    /// The re-evaluation loop. Driven from Core.OnUpdate each frame but budgeted: only a rolling window of
    /// NPCs is re-checked per frame, so the whole population is covered every few frames at a flat per-frame
    /// cost. Iterates the game's NPCManager.NPCRegistry with a cached-length index loop (no foreach/LINQ/alloc),
    /// computes the min squared distance to any player, an on-screen guard, and the hysteresis distance band,
    /// then asks LodLevers to apply the resulting tier.
    /// </summary>
    internal static class LodController
    {
        private static int _cursor;

        // Transient wake-failure backoff: a one-off navmesh hiccup must not pin an NPC Full for the whole session.
        // After a failed wake we keep the NPC Full but retry after RetryAfterSeconds; only once an NPC has failed
        // MaxConsecutiveFailures times in a row do we give up and keep it permanently Full.
        private const float RetryAfterSeconds = 60f;
        private const int MaxConsecutiveFailures = 3;
        private struct FailInfo { public float RetryAt; public int Count; }
        private static readonly Dictionary<int, FailInfo> _failed = new Dictionary<int, FailInfo>();

        // True when this NPC should currently be held Full because of a recent wake failure (not yet due for retry,
        // or it has exhausted its retries). A due-for-retry NPC returns false so the normal path re-attempts it.
        private static bool IsFailHeld(int id)
        {
            if (!_failed.TryGetValue(id, out FailInfo fi)) return false;
            if (fi.Count >= MaxConsecutiveFailures) return true;       // permanent give-up
            return Time.unscaledTime < fi.RetryAt;                     // still backing off
        }

        // Record a wake failure: bump the consecutive count and arm the next retry.
        private static void NoteFailure(int id)
        {
            _failed.TryGetValue(id, out FailInfo fi);
            fi.Count++;
            fi.RetryAt = Time.unscaledTime + RetryAfterSeconds;
            _failed[id] = fi;
        }

        // Manual override (DEBUG console) for A/B measurement: Auto = normal distance-based culling; the Force*
        // modes pin EVERY NPC to one tier each tick so FPS can be compared cleanly (off=Full baseline).
        internal enum Control { Auto, ForceFull, ForceCosmetic, ForceDeep }
        internal static Control Mode = Control.Auto;
#if SNITCH
        // Snitch ablation lever 'siesta.lodsim': when set, Tick() returns immediately (all LOD work skipped, NPCs
        // left in their current tier) so the profiler can confirm the LOD sim itself is near-zero cost. Debug only.
        internal static bool SimDisabled;
#endif

        // Player-position snapshot (taken once per tick; the per-NPC inner loop is pure float math).
        private static Vector3[] _players = new Vector3[8];
        private static int _playerCount;

        // Cached main camera for the on-screen guard.
        private static Camera _cam;
        private static float _camRefreshAt;

        internal static void Tick()
        {
            // Master off / MP-disabled: ensure everything is restored, then idle.
            if (!Preferences.EnableLod)
            {
                if (LodRegistry.HasAny) RestoreAll("LOD disabled");
                return;
            }
            bool mp = Net.IsMultiplayer();
            if (mp && !Preferences.EnableInMultiplayer)
            {
                if (LodRegistry.HasAny) RestoreAll("multiplayer + EnableInMultiplayer off");
                return;
            }
#if SNITCH
            if (SimDisabled) return;   // ablation lever 'siesta.lodsim': skip all LOD work this frame
#endif

            // Manual A/B override: pin all NPCs to one tier (idempotent ApplyTier makes repeat passes cheap).
            if (Mode != Control.Auto)
            {
                ForceAll(Mode == Control.ForceFull ? LodState.Full
                    : Mode == Control.ForceCosmetic ? LodState.Cosmetic : LodState.Deep);
                return;
            }

            var reg = NPCManager.NPCRegistry;   // Il2CppSystem.Collections.Generic.List<NPC>
            if (reg == null) return;
            int n;
            try { n = reg.Count; } catch { return; }
            if (n == 0) return;

            SnapshotPlayers();
            if (_playerCount == 0) return;   // no player yet -> nothing to measure against

            RefreshCamera();
            bool authoritative = Net.IsAuthoritative();

            // Unbudgeted promote-only pre-pass: every frame, cheaply scan ALL NPCs and immediately promote any that
            // should be Full (close OR on-screen) but currently are not. This kills the re-promotion latency of the
            // budgeted round-robin (a fast 180deg turn would otherwise leave a now-visible NPC hidden/paused for up
            // to ceil(N/Budget) frames). One Vector3 subtract + dot per NPC - trivial vs the navmesh/animation cost
            // it removes. Demotion + exemption work stays on the budgeted cursor below.
            float cosPromote = Preferences.CosmeticDistance * Preferences.CosmeticDistance;
            bool respectOnScreen = Preferences.RespectOnScreen;
            // Snitch (Debug): time the unbudgeted O(N) promote pre-pass apart from the budgeted round-robin so the
            // profiler shows where the (tiny, expected) LOD-sim cost actually lives.
#if SNITCH
            Profiler.Begin("Siesta.PromotePass");
            try {
#endif
            for (int i = 0; i < n; i++)
            {
                NPC npc;
                try { npc = reg[i]; } catch { continue; }
                if (npc == null) continue;
                int id;
                try { id = npc.GetInstanceID(); } catch { continue; }
                if (IsFailHeld(id)) continue;
                NpcModState st = LodRegistry.GetOrAdd(id, npc);
                if (st.Tier == LodState.Full) continue;

                Vector3 pp;
                try { pp = npc.CenterPoint; } catch { continue; }
                bool shouldBeFull = MinSqrDistToPlayer(pp) < cosPromote || (respectOnScreen && IsOnScreen(pp));
                if (!shouldBeFull) continue;

                try { LodLevers.ApplyTier(npc, st, LodState.Full, authoritative); }
                catch (Exception e) { Core.Log?.Warning("[Siesta] promote pre-pass failed: " + e.Message); }
            }
#if SNITCH
            } finally { Profiler.End("Siesta.PromotePass"); }
#endif

            int steps = Math.Min(Preferences.BudgetPerFrame, n);
#if SNITCH
            Profiler.Begin("Siesta.Budgeted");
            try {
#endif
            for (int s = 0; s < steps; s++)
            {
                if (_cursor >= n) _cursor = 0;
                NPC npc;
                try { npc = reg[_cursor]; } catch { _cursor++; continue; }
                _cursor++;
                if (npc == null) continue;
                Evaluate(npc, authoritative);
            }
#if SNITCH
            } finally { Profiler.End("Siesta.Budgeted"); }
#endif
        }

        private static void Evaluate(NPC npc, bool authoritative)
        {
            int id;
            try { id = npc.GetInstanceID(); } catch { return; }

            NpcModState st = LodRegistry.GetOrAdd(id, npc);

            if (IsFailHeld(id))
            {
                if (st.Tier != LodState.Full) LodLevers.ForceFull(npc, st);
                return;
            }

            st.Resolve(npc);

            // Reconcile stale bookkeeping: if the game itself re-showed the NPC (e.g. it exited a building / car),
            // our st.Hidden flag is stale - clear it so we don't think we still own its visibility.
            if (st.Hidden)
            {
                try { if (npc.isVisible) st.Hidden = false; } catch { }
            }

            Vector3 pos;
            try { pos = npc.CenterPoint; } catch { return; }

            float d2 = MinSqrDistToPlayer(pos);
            bool onScreen = IsOnScreen(pos);

            LodState desired = Decide(npc, st, d2, onScreen, authoritative);
            if (desired == st.Tier) return;

            try
            {
                LodLevers.ApplyTier(npc, st, desired, authoritative);
                if (st.WakeFailed)
                {
                    st.WakeFailed = false;
                    NoteFailure(id);
                    Core.Log?.Warning($"[Siesta] NPC {id} wake failed - keeping Full, will retry in {RetryAfterSeconds:F0}s.");
#if SNITCH
                    Profiler.Log("Siesta", $"NPC {id} wake failed - kept Full, retry in {RetryAfterSeconds:F0}s.", LogLevel.Warning);
#endif
                    LodLevers.ForceFull(npc, st);
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Siesta] ApplyTier failed: " + e.Message);
            }
        }

        /// <summary>Distance band with hysteresis (promote inside the threshold, demote only past threshold+margin),
        /// then capped by the on-screen guard, the deep-cull eligibility (toggle, authority, exemptions) and the
        /// cosmetic toggle.</summary>
        private static LodState Decide(NPC npc, NpcModState st, float d2, bool onScreen, bool authoritative)
        {
            if (onScreen && Preferences.RespectOnScreen)
            {
                return LodState.Full;
            }

            float cos = Preferences.CosmeticDistance;
            float deep = Preferences.DeepDistance;
            float h = Preferences.Hysteresis;
            float cosP = cos * cos;
            float cosD = (cos + h) * (cos + h);
            float deepP = deep * deep;
            float deepD = (deep + h) * (deep + h);

            LodState band;
            switch (st.Tier)
            {
                case LodState.Full:
                    band = d2 >= deepD ? LodState.Deep : (d2 >= cosD ? LodState.Cosmetic : LodState.Full);
                    break;
                case LodState.Cosmetic:
                    band = d2 < cosP ? LodState.Full : (d2 >= deepD ? LodState.Deep : LodState.Cosmetic);
                    break;
                default: // Deep
                    band = d2 < cosP ? LodState.Full : (d2 < deepP ? LodState.Cosmetic : LodState.Deep);
                    break;
            }

            if (band == LodState.Deep)
            {
                string reason = !authoritative ? "not-host" : Exemptions.Reason(npc, st);
                st.ExemptReason = reason;
                if (!(Preferences.UseDeepCull && reason == null))
                {
                    band = Preferences.UseCosmeticCull ? LodState.Cosmetic : LodState.Full;
                }
            }
            if (band == LodState.Cosmetic && !Preferences.UseCosmeticCull)
            {
                band = LodState.Full;
            }
            return band;
        }

        private static float MinSqrDistToPlayer(Vector3 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < _playerCount; i++)
            {
                float d2 = (_players[i] - p).sqrMagnitude;
                if (d2 < best) best = d2;
            }
            return best;
        }

        private static void SnapshotPlayers()
        {
            _playerCount = 0;
            try
            {
                var list = Player.PlayerList;
                if (list != null && list.Count > 0)
                {
                    int c = list.Count;
                    for (int i = 0; i < c; i++)
                    {
                        Player p = list[i];
                        if (p == null) continue;
                        var t = p.transform;
                        if (t == null) continue;
                        Add(t.position);
                    }
                }
                if (_playerCount == 0)
                {
                    Player local = Player.Local;
                    if (local != null && local.transform != null) Add(local.transform.position);
                }
            }
            catch { /* leave _playerCount as-is */ }
        }

        private static void Add(Vector3 v)
        {
            if (_playerCount >= _players.Length) return;
            _players[_playerCount++] = v;
        }

        private static void RefreshCamera()
        {
            if (_cam != null && Time.unscaledTime < _camRefreshAt) return;
            try { _cam = Camera.main; } catch { _cam = null; }
            _camRefreshAt = Time.unscaledTime + 2f;
        }

        // Generous forward-cone test: only used to AVOID culling something the player is looking at, so a
        // loose cone (and "unknown -> off-screen") errs toward keeping distant unseen NPCs cullable.
        private static bool IsOnScreen(Vector3 p)
        {
            Camera cam = _cam;
            if (cam == null) return false;
            try
            {
                var ct = cam.transform;
                Vector3 to = p - ct.position;
                float dist = to.magnitude;
                if (dist < 0.001f) return true;
                float dot = Vector3.Dot(ct.forward, to / dist);
                return dot > 0.5f;   // ~60deg half-angle around view direction
            }
            catch
            {
                return false;
            }
        }

        // ----- lifecycle / debug -----

        internal static void RestoreAll(string reason)
        {
            try
            {
                LodRegistry.RestoreAll();
                _failed.Clear();
                _cursor = 0;
#if SNITCH
                Profiler.Log("Siesta", "restored all NPCs to vanilla (" + reason + ")");
#endif
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Siesta] RestoreAll failed: " + e.Message);
            }
        }

        internal static void Reset()
        {
            LodRegistry.Reset();
            _failed.Clear();
            _cursor = 0;
            _cam = null;
        }

        /// <summary>Debug helper: force every NPC to a tier now (deep is still authority-gated).</summary>
        internal static void ForceAll(LodState target)
        {
            var reg = NPCManager.NPCRegistry;   // Il2CppSystem list
            if (reg == null) return;
            int n;
            try { n = reg.Count; } catch { return; }
            bool authoritative = Net.IsAuthoritative();
            for (int i = 0; i < n; i++)
            {
                NPC npc;
                try { npc = reg[i]; } catch { continue; }
                if (npc == null) continue;
                int id;
                try { id = npc.GetInstanceID(); } catch { continue; }
                if (IsFailHeld(id)) continue;
                NpcModState st = LodRegistry.GetOrAdd(id, npc);
                st.Resolve(npc);
                LodState t = target;
                if (t == LodState.Deep)
                {
                    string reason = !authoritative ? "not-host" : Exemptions.Reason(npc, st);
                    st.ExemptReason = reason;
                    if (reason != null) t = LodState.Cosmetic;
                }
                try { LodLevers.ApplyTier(npc, st, t, authoritative); } catch { }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Siesta.Compat;
using Siesta.Config;

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
        private static readonly HashSet<int> _failed = new HashSet<int>();   // NPCs whose wake failed -> kept Full

        // Manual override (DEBUG console) for A/B measurement: Auto = normal distance-based culling; the Force*
        // modes pin EVERY NPC to one tier each tick so FPS can be compared cleanly (off=Full baseline).
        internal enum Control { Auto, ForceFull, ForceCosmetic, ForceDeep }
        internal static Control Mode = Control.Auto;

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

            int steps = Math.Min(Preferences.BudgetPerFrame, n);
            for (int s = 0; s < steps; s++)
            {
                if (_cursor >= n) _cursor = 0;
                NPC npc;
                try { npc = reg[_cursor]; } catch { _cursor++; continue; }
                _cursor++;
                if (npc == null) continue;
                Evaluate(npc, authoritative);
            }
        }

        private static void Evaluate(NPC npc, bool authoritative)
        {
            int id;
            try { id = npc.GetInstanceID(); } catch { return; }

            NpcModState st = LodRegistry.GetOrAdd(id, npc);

            if (_failed.Contains(id))
            {
                if (st.Tier != LodState.Full) LodLevers.ForceFull(npc, st);
                return;
            }

            st.Resolve(npc);

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
                    _failed.Add(id);
                    Core.Log?.Warning($"[Siesta] NPC {id} wake failed - forcing permanent exempt (kept Full).");
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
                if (_failed.Contains(id)) continue;
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

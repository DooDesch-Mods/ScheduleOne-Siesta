using System;
using UnityEngine.AI;   // NavMeshHit

namespace Siesta.Lod
{
    /// <summary>
    /// The ONLY place that calls the game's NPC mutation APIs. Applies/restores the LOD levers idempotently,
    /// recording exactly what was changed so a restore undoes only our own changes. Uses the game's own
    /// reversible APIs:
    ///   cosmetic  -> NPC.SetVisible(false/true, networked:false)  (local-only; OnNPCVisibilityChanged stands
    ///                the animator down for free)
    ///   deep      -> NPCMovement.PauseMovement/ResumeMovement + NPCScheduleManager.DisableSchedule/
    ///                EnableSchedule, with EnforceState() catch-up + a NavMesh-replacement safety net on wake.
    /// </summary>
    internal static class LodLevers
    {
        /// <summary>Transition an NPC to the target tier, applying/restoring only the deltas. The wake path
        /// (Deep->less) restores schedule+movement BEFORE the NPC is made visible again.</summary>
        internal static void ApplyTier(NPC npc, NpcModState st, LodState target, bool authoritative)
        {
            if (target == st.Tier)
            {
                return;
            }

            switch (target)
            {
                case LodState.Full:
                    if (st.DeepApplied) RestoreDeep(npc, st);
                    if (st.Hidden) Show(npc, st);
                    break;

                case LodState.Cosmetic:
                    if (st.DeepApplied) RestoreDeep(npc, st);   // came down from Deep: resume sim, stay hidden
                    if (!st.Hidden) Hide(npc, st);
                    break;

                case LodState.Deep:
                    if (!st.Hidden) Hide(npc, st);
                    if (!st.DeepApplied) ApplyDeep(npc, st, authoritative);
                    break;
            }

            st.Tier = target;
        }

        /// <summary>Bring an NPC fully back to vanilla (used by RestoreAll / panic / wake-failure fallback).</summary>
        internal static void ForceFull(NPC npc, NpcModState st)
        {
            try
            {
                if (st.DeepApplied) RestoreDeep(npc, st);
                if (st.Hidden) Show(npc, st);
                st.Tier = LodState.Full;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Siesta] ForceFull failed: " + e.Message);
            }
        }

        // ----- cosmetic (local-only, MP-safe) -----

        private static void Hide(NPC npc, NpcModState st)
        {
            npc.SetVisible(false, false);   // networked:false -> local-only, replicates nothing
            st.Hidden = true;
        }

        private static void Show(NPC npc, NpcModState st)
        {
            npc.SetVisible(true, false);
            st.Hidden = false;
        }

        // ----- deep (host-authoritative only) -----

        private static void ApplyDeep(NPC npc, NpcModState st, bool authoritative)
        {
            if (!authoritative)
            {
                // Tripwire: a client must never pause/disable a replica. Controller already gates this; this is
                // the last-line assert so any regression is loud and a no-op.
                Core.Log?.Warning($"[Siesta] ERROR client-attempted-deepcull npc={st.Id} (ignored)");
                return;
            }

            NPCMovement mv = npc.Movement;
            if (mv != null && !mv.IsPaused)
            {
                mv.PauseMovement();
                st.WePausedMovement = true;
            }

            NPCScheduleManager sm = st.Schedule;
            if (sm != null && sm.ScheduleEnabled)
            {
                sm.DisableSchedule();
                st.WeDisabledSchedule = true;
            }

            st.DeepApplied = true;
        }

        /// <summary>Wake order: EnableSchedule -> EnforceState (catch up to the current time-of-day action) ->
        /// ResumeMovement -> NavMesh-placement repair. The caller makes the NPC visible afterwards.</summary>
        private static void RestoreDeep(NPC npc, NpcModState st)
        {
            NPCScheduleManager sm = st.Schedule;
            if (st.WeDisabledSchedule && sm != null)
            {
                sm.EnableSchedule();
                sm.EnforceState(false);   // re-select + start the action that should be active now
                st.WeDisabledSchedule = false;
            }

            NPCMovement mv = npc.Movement;
            if (st.WePausedMovement && mv != null)
            {
                mv.ResumeMovement();
                st.WePausedMovement = false;
                if (!RepairNavMesh(npc, mv, st))
                {
                    st.WakeFailed = true;
                }
            }

            st.DeepApplied = false;
        }

        /// <summary>Ensure the agent is live and on the NavMesh after a resume (EnforceState may have warped the
        /// transform). Returns false if the agent cannot be placed - the controller then keeps the NPC Full.</summary>
        private static bool RepairNavMesh(NPC npc, NPCMovement mv, NpcModState st)
        {
            try
            {
                NavMeshAgent agent = mv.Agent;
                if (agent == null)
                {
                    Core.Log?.Warning($"[Siesta] WAKE-FAILED npc={st.Id} reason=agent-null");
                    return false;
                }
                if (!agent.enabled)
                {
                    mv.SetAgentEnabled(true);
                }
                if (agent.isOnNavMesh)
                {
                    return true;
                }

                mv.WarpToNavMesh();
                if (agent.isOnNavMesh) return true;

                if (mv.SmartSampleNavMesh(mv.FootPosition, out NavMeshHit hit))
                {
                    mv.Warp(hit.position);
                    if (agent.isOnNavMesh) return true;
                }

                mv.Warp(mv.FootPosition);
                if (agent.isOnNavMesh) return true;

                Core.Log?.Warning($"[Siesta] WAKE-FAILED npc={st.Id} reason=still-off-navmesh");
                return false;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[Siesta] WAKE-FAILED npc={st.Id} reason=exception {e.Message}");
                return false;
            }
        }
    }
}

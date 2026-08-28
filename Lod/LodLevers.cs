using System;
using UnityEngine.AI;   // NavMeshHit

namespace Siesta.Lod
{
    /// <summary>
    /// The ONLY place that calls the game's NPC mutation APIs. Applies/restores the LOD levers idempotently,
    /// recording exactly what was changed so a restore undoes only our own changes. Uses the game's own
    /// reversible APIs:
    ///   cosmetic  -> NPC.SetVisible(false/true, networked:false)  (local-only; OnNPCVisibilityChanged stands
    ///                the animator down for free). NOTE: on the host SetVisible(false) ALSO disables the
    ///                NavMeshAgent, which would freeze a roaming NPC mid-route - so Hide re-enables the agent
    ///                when authoritative, leaving movement/schedule untouched (the NPC keeps walking, just hidden).
    ///   deep      -> NPCMovement.PauseMovement/ResumeMovement + NPCScheduleManager.DisableSchedule/
    ///                EnableSchedule + NPCAwareness.SetAwarenessActive(false) (the real per-NPC perf lever -
    ///                throttles the 10Hz vision/awareness sweep), with EnforceState() catch-up + a
    ///                NavMesh-replacement safety net on wake.
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
                    if (!st.Hidden) Hide(npc, st, authoritative);
                    break;

                case LodState.Deep:
                    // ApplyDeep BEFORE Hide so PauseMovement runs while the agent is still enabled (Hide
                    // disables it on the host); pausing a live, on-navmesh agent avoids the wake-time error spam.
                    if (!st.DeepApplied) ApplyDeep(npc, st, authoritative);
                    if (!st.Hidden) Hide(npc, st, authoritative);
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
                LodLog.Vanilla("ForceFull", st.Id, e);
            }
        }

        // ----- cosmetic (local-only, MP-safe) -----

        private static void Hide(NPC npc, NpcModState st, bool authoritative)
        {
            // Claim ownership before the call: if SetVisible throws half-way the NPC may already be hidden, and
            // a lost flag would mean nothing ever shows it again.
            st.Hidden = true;
            try { npc.SetVisible(false, false); }   // networked:false -> local-only, replicates nothing
            catch (Exception e) { LodLog.Vanilla("SetVisible(false)", st.Id, e); }
            // On the host SetVisible(false) also disables the NavMeshAgent + the interaction collider. Cosmetic
            // must keep the NPC simulating (it isn't a Deep cull), so re-enable both when authoritative. This is
            // host-only and networked:false, so it cannot desync clients.
            if (authoritative)
            {
                try
                {
                    NPCMovement mv = npc.Movement;
                    if (mv != null)
                    {
                        mv.SetAgentEnabled(true);
                        mv.VisibilityChange(true);   // restore the CapsuleCollider the SetVisible(false) dropped
                    }
                }
                catch { /* missing movement -> leave as the game left it */ }
            }
        }

        private static void Show(NPC npc, NpcModState st)
        {
            // Defer to the game when it owns visibility (entering a building / vehicle re-shows the NPC itself);
            // calling SetVisible(true) then would pop the model on inside a wall or car. Clear st.Hidden either way.
            st.Hidden = false;
            bool gameOwnsVisibility = false;
            try { gameOwnsVisibility = npc.isInBuilding || npc.IsInVehicle; } catch { }
            if (!gameOwnsVisibility)
            {
                try { npc.SetVisible(true, false); }
                catch (Exception e) { LodLog.Vanilla("SetVisible(true)", st.Id, e); }
            }
        }

        // ----- deep (host-authoritative only) -----

        private static void ApplyDeep(NPC npc, NpcModState st, bool authoritative)
        {
            if (!authoritative)
            {
                // Tripwire: a client must never pause/disable a replica. Controller already gates this; this is
                // the last-line assert so any regression is loud and a no-op.
                Core.Log?.Warning($"ERROR client-attempted-deepcull npc={st.Id} (ignored)");
                return;
            }

            NPCMovement mv = npc.Movement;
            // Only pause a live, on-navmesh agent (this runs while the agent is still enabled - Hide comes after,
            // see ApplyTier). Pausing an off-navmesh/disabled agent is what produced the wake-time error spam.
            if (mv != null && !mv.IsPaused)
            {
                try
                {
                    NavMeshAgent agent = mv.Agent;
                    if (agent != null && agent.enabled && agent.isOnNavMesh)
                    {
                        mv.PauseMovement();
                        st.WePausedMovement = true;
                    }
                }
                catch { /* no agent / off navmesh -> skip the pause, awareness + schedule still apply */ }
            }

            NPCScheduleManager sm = st.Schedule;
            if (sm != null && sm.ScheduleEnabled)
            {
                // DisableSchedule clears ScheduleEnabled and THEN interrupts the active action, which runs vanilla
                // event code that can throw. Record the flag first: the schedule is off either way, and dropping
                // the flag would leave this NPC with a disabled schedule - inert - for the rest of the session.
                st.WeDisabledSchedule = true;
                try { sm.DisableSchedule(); }
                catch (Exception e) { LodLog.Vanilla("DisableSchedule", st.Id, e); }
            }

            // The real per-NPC perf lever: throttle the 10Hz vision/awareness sweep (the game itself does this on
            // EnterBuilding/ExitBuilding, so it is vanilla-safe and local). Witnesses/police/fleers are already
            // exempt via Exemptions.Reason, so only off-screen far non-essential NPCs ever reach here.
            try
            {
                NPCAwareness aw = npc.Awareness;
                if (aw != null)
                {
                    aw.SetAwarenessActive(false);
                    st.WeDisabledAwareness = true;
                }
            }
            catch { /* missing awareness -> leave it on */ }

            st.DeepApplied = true;
        }

        /// <summary>Wake order (the agent must be LIVE, on-navmesh and unpaused BEFORE the schedule re-evaluates,
        /// else EnableSchedule/EnforceState issue SetDestination/JumpTo against a disabled, off-navmesh agent and
        /// the NPC stands inert until poked): restore awareness -> ResumeMovement -> RepairNavMesh -> then
        /// EnableSchedule + EnforceState. The caller makes the NPC visible afterwards.</summary>
        private static void RestoreDeep(NPC npc, NpcModState st)
        {
            // Restore the vision/awareness sweep first (cheap, no agent dependency).
            if (st.WeDisabledAwareness)
            {
                try
                {
                    NPCAwareness aw = npc.Awareness;
                    if (aw != null) aw.SetAwarenessActive(true);
                }
                catch { /* awareness gone -> nothing to restore */ }
                st.WeDisabledAwareness = false;
            }

            NPCMovement mv = npc.Movement;
            if (st.WePausedMovement && mv != null)
            {
                st.WePausedMovement = false;
                try { mv.ResumeMovement(); }
                catch (Exception e) { LodLog.Vanilla("ResumeMovement", st.Id, e); }
                // RepairNavMesh re-enables the agent and re-seats it on the navmesh. If it cannot, keep the NPC
                // Full (don't re-evaluate the schedule against an unplaced agent) and let the controller exempt it.
                if (!RepairNavMesh(npc, mv, st))
                {
                    st.WakeFailed = true;
                    st.DeepApplied = false;
                    return;
                }
            }

            NPCScheduleManager sm = st.Schedule;
            if (st.WeDisabledSchedule && sm != null)
            {
                // EnableSchedule sets ScheduleEnabled and THEN ticks the schedule, and that tick starts the active
                // NPC event - vanilla code that can NRE on a stale reference (NPCEvent_CartelGoonExit does it the
                // moment its goon pool is gone). The schedule is on either way, so clear the flag BEFORE the call
                // and let the throw stop here. Letting it escape aborted the transition with st.Tier unchanged,
                // and the next frame's pass re-ran the identical failing wake: one throw, and one MelonLoader
                // IL2CPP stack trace to console and disk, per frame for as long as the NPC stayed in range.
                st.WeDisabledSchedule = false;
                try
                {
                    sm.EnableSchedule();
                    sm.EnforceState(false);   // re-select + start the action that should be active now (agent is live)
                }
                catch (Exception e) { LodLog.Vanilla("EnableSchedule", st.Id, e); }
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
                    Core.Log?.Warning($"WAKE-FAILED npc={st.Id} reason=agent-null");
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

                // (mv.WarpToNavMesh() is an empty stub in this build - dropped.)
                if (mv.SmartSampleNavMesh(mv.FootPosition, out NavMeshHit hit))
                {
                    mv.Warp(hit.position);
                    // Re-seat the agent the way the game does so isOnNavMesh reflects the new position immediately.
                    mv.SetAgentEnabled(false);
                    mv.SetAgentEnabled(true);
                    if (agent.isOnNavMesh) return true;
                }

                mv.Warp(mv.FootPosition);
                if (agent.isOnNavMesh) return true;

                Core.Log?.Warning($"WAKE-FAILED npc={st.Id} reason=still-off-navmesh");
                return false;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"WAKE-FAILED npc={st.Id} reason=exception {e.Message}");
                return false;
            }
        }
    }
}

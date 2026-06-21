using System;
using Il2CppScheduleOne.NPCs.Behaviour;   // NPCBehaviour
using Il2CppScheduleOne.Police;            // PoliceOfficer

namespace Siesta.Lod
{
    /// <summary>
    /// Functionality-preservation gate: returns a non-null REASON when an NPC must NOT be deep-culled
    /// (movement/schedule paused) because its simulation matters even when far/off-screen. Cosmetic culling
    /// (hiding the renderer) is always safe and is NOT gated here. Any uncertainty resolves to "exempt".
    ///
    /// Grounded in the decompiled API: NPC.IsConscious/IsInVehicle/IsPanicked/isUnsettled/IsImportant,
    /// Dealer.currentContract/_attendDealBehaviour.Active, Employee.IsAnyWorkInProgress/ShouldIdle/
    /// IsWaitingOutside, Customer.CurrentContract/IsAwaitingDelivery, NPCBehaviour.activeBehaviour/behaviourStack.
    /// </summary>
    internal static class Exemptions
    {
        /// <summary>When true, ANY active behaviour / non-empty behaviour stack exempts the NPC. In Schedule I
        /// routine scheduled NPCs almost always carry a behaviour, so this (default OFF) would exempt everyone;
        /// the specific dealer/employee/customer/important checks below are the real safety net. Kept as a
        /// toggle for live testing.</summary>
        internal static bool ExemptOnAnyBehaviour = false;

        internal static bool IsDeepCullExempt(NPC npc, NpcModState st) => Reason(npc, st) != null;

        /// <summary>Null = safe to deep-cull. Otherwise a short tag naming the protecting rule (for diagnostics).</summary>
        internal static string Reason(NPC npc, NpcModState st)
        {
            try
            {
                if (!npc.IsConscious) return "unconscious";
                if (npc.IsInVehicle) return "in-vehicle";
                if (npc.IsPanicked || npc.isUnsettled) return "panicked";
                if (npc.IsImportant) return "important";

                // Officers are a tiny pooled population the game already manages (CheckDeactivation); never deep-cull
                // them so a pursuit/patrol/checkpoint/sentry officer can't freeze mid-action once off-screen and far.
                if (st.Officer != null) return "police";

                // In a conversation: pausing here desyncs the dialogue-end ResumeMovement against our bookkeeping.
                if (npc.DialogueHandler != null && npc.DialogueHandler.IsDialogueInProgress) return "in-dialogue";

                // Indoor NPCs are already cheap and the building enter/exit handshake (which toggles awareness +
                // navmesh) is fragile to pause/resume, so leave them to the game.
                if (npc.isInBuilding) return "in-building";

                // Always-on (independent of ExemptOnAnyBehaviour): an NPC actively running one of the critical
                // behaviours must keep simulating even far/off-screen, else witnesses/fleers/callers/customers
                // freeze mid-action (the "reacted only after I pressed E" bug). Targeted to the behaviours that
                // matter so the routine population stays deep-cull-eligible (the perf win).
                var beh = npc.Behaviour;
                if (beh != null && beh.activeBehaviour != null && (
                    (beh.CombatBehaviour != null && beh.CombatBehaviour.Active) ||
                    (beh.FleeBehaviour != null && beh.FleeBehaviour.Active) ||
                    (beh.CoweringBehaviour != null && beh.CoweringBehaviour.Active) ||
                    (beh.CallPoliceBehaviour != null && beh.CallPoliceBehaviour.Active) ||
                    (beh.RequestProductBehaviour != null && beh.RequestProductBehaviour.Active) ||
                    (beh.GenericDialogueBehaviour != null && beh.GenericDialogueBehaviour.Active)))
                    return "active-critical-behaviour";

                if (ExemptOnAnyBehaviour && beh != null)
                {
                    if (beh.activeBehaviour != null) return "active-behaviour";
                    var stack = beh.behaviourStack;
                    if (stack != null && stack.Count > 0) return "behaviour-stack";
                }

                if (st.Dealer != null)
                {
                    if (st.Dealer.currentContract != null) return "dealer-contract";
                    var adb = st.Dealer._attendDealBehaviour;
                    if (adb != null && adb.Active) return "dealer-attend";
                }

                if (st.Employee != null)
                {
                    if (st.Employee.IsAnyWorkInProgress()) return "employee-working";
                    if (!st.Employee.ShouldIdle() && !st.Employee.IsWaitingOutside) return "employee-busy";
                }

                if (st.Customer != null)
                {
                    if (st.Customer.CurrentContract != null) return "customer-contract";
                    if (st.Customer.IsAwaitingDelivery) return "customer-awaiting";
                }

                return null;
            }
            catch (Exception e)
            {
                return "ex:" + e.Message;
            }
        }
    }
}

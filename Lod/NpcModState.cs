using Il2CppScheduleOne.Economy;     // Dealer, Customer
using Il2CppScheduleOne.Employees;   // Employee

namespace Siesta.Lod
{
    /// <summary>
    /// Per-NPC tracked state: the current LOD tier, exactly which reversible levers THIS mod applied (so a
    /// restore only undoes our own changes, never the game's), and lazily-resolved component references used
    /// by the exemption checks. One instance per NPC, kept in <see cref="LodRegistry"/>.
    /// </summary>
    internal sealed class NpcModState
    {
        internal readonly int Id;
        internal NPC Npc;                  // refreshed each tick; used by RestoreAll without the registry

        internal LodState Tier = LodState.Full;

        // What we applied (so restore is exact + idempotent).
        internal bool Hidden;              // we called SetVisible(false)
        internal bool DeepApplied;         // movement/schedule levers are on
        internal bool WePausedMovement;    // we (not the game) paused movement
        internal bool WeDisabledSchedule;  // we (not the game) disabled the schedule
        internal bool WakeFailed;          // a wake left the agent off-navmesh -> controller marks permanent-exempt
        internal string ExemptReason;      // last deep-cull eligibility result (null = eligible) - diagnostics only

        // Lazily-resolved components (an NPC's type/components don't change over its lifetime).
        private bool _resolved;
        internal NPCScheduleManager Schedule;
        internal Dealer Dealer;
        internal Employee Employee;
        internal Customer Customer;

        internal NpcModState(int id, NPC npc)
        {
            Id = id;
            Npc = npc;
        }

        internal void Resolve(NPC npc)
        {
            if (_resolved)
            {
                return;
            }
            _resolved = true;
            try { Schedule = npc.GetComponentInChildren<NPCScheduleManager>(true); } catch { }
            try { Dealer = npc.TryCast<Dealer>(); } catch { }
            try { Employee = npc.TryCast<Employee>(); } catch { }
            try { Customer = npc.GetComponent<Customer>(); } catch { }
        }
    }
}

namespace Siesta.Lod
{
    /// <summary>
    /// Per-NPC level-of-detail state, ordered by how aggressively the NPC is reduced.
    /// Full = vanilla (no levers). Cosmetic = renderer hidden (local, MP-safe); the NPC keeps moving + behaving
    /// (on the host its NavMeshAgent is re-enabled after the hide so it doesn't freeze mid-route).
    /// Deep = Cosmetic plus movement + schedule + vision/awareness paused (host-authoritative only, reversible
    /// with schedule catch-up + a navmesh-placement repair on wake).
    /// </summary>
    internal enum LodState
    {
        Full = 0,
        Cosmetic = 1,
        Deep = 2,
    }
}

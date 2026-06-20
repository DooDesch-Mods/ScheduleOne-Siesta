namespace Siesta.Lod
{
    /// <summary>
    /// Per-NPC level-of-detail state, ordered by how aggressively the NPC is reduced.
    /// Full = vanilla (no levers). Cosmetic = renderer hidden (local, MP-safe), AI untouched.
    /// Deep = Cosmetic plus movement + schedule paused (host-authoritative only, reversible with catch-up).
    /// </summary>
    internal enum LodState
    {
        Full = 0,
        Cosmetic = 1,
        Deep = 2,
    }
}

using System;
using Il2CppScheduleOne.Networking;   // Lobby (PersistentSingleton<Lobby>)

namespace Siesta.Compat
{
    /// <summary>
    /// Multiplayer detection + host authority, used to gate the LOD layer's two tiers:
    /// - Cosmetic culling (renderer off via NPC.SetVisible(false, networked:false)) is purely local and is
    ///   applied on EVERY peer (single-player + every multiplayer client). It replicates nothing.
    /// - Deep culling (PauseMovement + DisableSchedule) changes NPC SIMULATION and must only run on the
    ///   authoritative peer, because in multiplayer the host owns NPC simulation and a client pausing a
    ///   replica locally would desync. So DeepCull is gated behind <see cref="IsAuthoritative"/>.
    ///
    /// Both checks use the game's own Lobby singleton (Il2CppScheduleOne.Networking.Lobby :
    /// PersistentSingleton&lt;Lobby&gt;). In Schedule I single-player the host still runs a local FishNet server,
    /// so "IsServer || IsClient" is TRUE even solo and cannot tell SP from MP - the Lobby is the reliable
    /// signal: a co-op session is IsInLobby with PlayerCount &gt; 1, and the host is Lobby.IsHost.
    ///
    /// KNOWN GAP (same as the Trashville template): these are Steam-matchmaking-driven. A direct-UDP
    /// dedicated-server transport keeps LobbyID == 0, so IsMultiplayer() returns false there even with
    /// multiple players. Steam-lobby co-op - the standard path - is handled correctly. Conservative on any
    /// failure: IsMultiplayer -&gt; false, IsAuthoritative -&gt; true (i.e. behave as single-player host).
    /// </summary>
    internal static class Net
    {
        internal static bool IsMultiplayer()
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby == null)
                {
                    return false;   // no lobby manager yet -> treat as single-player
                }
                return lobby.IsInLobby && lobby.PlayerCount > 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when this peer owns NPC simulation: single-player, or the host of a co-op lobby.
        /// Deep culling is only allowed when this is true.</summary>
        internal static bool IsAuthoritative()
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby == null || !lobby.IsInLobby)
                {
                    return true;    // single-player: the local peer is authoritative by definition
                }
                return lobby.IsHost;   // multiplayer: only the host simulates NPCs
            }
            catch
            {
                return true;
            }
        }
    }
}

using System;
using XREngine.Data.Core;
using XREngine.Networking;

namespace XREngine.Players
{
    public class PlayerInfo : XRBase
    {
        private int _serverIndex = -1;
        private ELocalPlayerIndex? _localIndex;
        private Guid _sessionId = Guid.Empty;
        private NetworkEntityId _networkEntityId;
        private NetworkAuthorityLease? _authorityLease;

        /// <summary>
        /// Every player has a unique server ID
        /// </summary>
        public int ServerIndex
        {
            get => _serverIndex;
            set => SetField(ref _serverIndex, value);
        }
        /// <summary>
        /// If the player is a local player, this is the index of the player on the local machine.
        /// </summary>
        public ELocalPlayerIndex? LocalIndex
        {
            get => _localIndex;
            set => SetField(ref _localIndex, value);
        }
        /// <summary>
        /// The realtime session this player is attached to.
        /// </summary>
        public Guid SessionId
        {
            get => _sessionId;
            set => SetField(ref _sessionId, value);
        }
        /// <summary>
        /// Stable realtime entity identity assigned by the server for the possessed pawn.
        /// </summary>
        public NetworkEntityId NetworkEntityId
        {
            get => _networkEntityId;
            set => SetField(ref _networkEntityId, value);
        }
        /// <summary>
        /// Current server-issued authority lease for the possessed pawn, if any.
        /// </summary>
        public NetworkAuthorityLease? AuthorityLease
        {
            get => _authorityLease;
            set => SetField(ref _authorityLease, value);
        }
    }
}

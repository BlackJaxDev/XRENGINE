using System;
using XREngine.Data.Core;

namespace XREngine.Players
{
    public class PlayerInfo : XRBase
    {
        /// <summary>
        /// Every player has a unique server ID
        /// </summary>
        public int ServerIndex { get; set; } = -1;
        /// <summary>
        /// If the player is a local player, this is the index of the player on the local machine.
        /// </summary>
        public ELocalPlayerIndex? LocalIndex { get; set; } = null;
        /// <summary>
        /// The instance this player is attached to.
        /// </summary>
        public Guid InstanceId { get; set; } = Guid.Empty;
    }
}

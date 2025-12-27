using System;
using XREngine.Scene;
using XREngine.Networking;
using XREngine.Rendering;

namespace XREngine
{
    public sealed record ServerInstanceContext(Guid InstanceId, XRWorldInstance WorldInstance);

    public static partial class Engine
    {
        /// <summary>
        /// Allows the host (XREngine.Server) to provide world instances for incoming join requests.
        /// </summary>
        public static Func<PlayerJoinRequest, ServerInstanceContext?>? ServerInstanceResolver { get; set; }
    }
}

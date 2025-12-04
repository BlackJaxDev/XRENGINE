using System;
using System.Collections.Generic;

namespace XREngine.Networking
{
    public class Server
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string? IP { get; set; }
        public string? Region { get; set; }
        public int Port { get; set; }
        public int MaxPlayers { get; set; } = 100;
        public int CurrentLoad { get; set; } = 0;
        public int PendingConnections { get; set; } = 0;
        public List<Guid> Instances { get; set; } = [];
        public DateTime LastHeartbeatUtc { get; private set; } = DateTime.UtcNow;

        public bool IsAcceptingPlayers => CurrentLoad + PendingConnections < MaxPlayers;

        public void TouchHeartbeat(int? currentLoad = null, int? maxPlayers = null, IEnumerable<Guid>? instances = null)
        {
            if (currentLoad.HasValue)
                CurrentLoad = Math.Max(0, currentLoad.Value);
            if (maxPlayers.HasValue)
                MaxPlayers = Math.Max(1, maxPlayers.Value);
            if (instances is not null)
            {
                Instances.Clear();
                Instances.AddRange(instances);
            }
            LastHeartbeatUtc = DateTime.UtcNow;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using XREngine.Networking.LoadBalance.Balancers;

namespace XREngine.Networking.LoadBalance
{
    public class LoadBalancerService
    {
        public record ServerStatus(
            Guid Id,
            string? Ip,
            int Port,
            string? Region,
            int CurrentLoad,
            int PendingConnections,
            int MaxPlayers,
            IReadOnlyCollection<Guid> Instances,
            DateTime LastHeartbeatUtc,
            bool IsAcceptingPlayers);

        private readonly LoadBalancer _strategy;
        private readonly TimeSpan _heartbeatTimeout;
        private readonly object _gate = new();

        public LoadBalancerService(LoadBalancer strategy, TimeSpan? heartbeatTimeout = null)
        {
            _strategy = strategy;
            _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(45);
        }

        public ServerStatus RegisterOrUpdate(Server server)
        {
            lock (_gate)
            {
                PruneExpiredServers();
                var tracked = _strategy.GetServer(server.Id);
                if (tracked is null)
                {
                    server.TouchHeartbeat(server.CurrentLoad, server.MaxPlayers, server.Instances);
                    server.PendingConnections = 0;
                    _strategy.AddServer(server);
                    tracked = server;
                }
                else
                {
                    tracked.IP = server.IP;
                    tracked.Port = server.Port;
                    tracked.Region = server.Region;
                    tracked.MaxPlayers = server.MaxPlayers;
                    tracked.TouchHeartbeat(server.CurrentLoad, server.MaxPlayers, server.Instances);
                }
                return ToStatus(tracked);
            }
        }

        public bool Heartbeat(Guid serverId, int currentLoad, int? maxPlayers, IEnumerable<Guid>? instances)
        {
            lock (_gate)
            {
                PruneExpiredServers();
                var server = _strategy.GetServer(serverId);
                if (server is null)
                    return false;

                server.TouchHeartbeat(currentLoad, maxPlayers, instances);
                server.PendingConnections = Math.Max(0, Math.Min(server.PendingConnections, Math.Max(0, server.MaxPlayers - server.CurrentLoad)));
                return true;
            }
        }

        public ServerStatus? AssignServer(string? affinityKey, Guid? instanceId)
        {
            lock (_gate)
            {
                PruneExpiredServers();
                var target = SelectServer(affinityKey, instanceId);
                if (target is null || !target.IsAcceptingPlayers)
                    return null;

                target.PendingConnections++;
                target.TouchHeartbeat();
                return ToStatus(target);
            }
        }

        public bool ReleaseServer(Guid serverId, bool playerJoined)
        {
            lock (_gate)
            {
                var server = _strategy.GetServer(serverId);
                if (server is null)
                    return false;

                if (server.PendingConnections > 0)
                    server.PendingConnections--;

                if (playerJoined)
                    server.CurrentLoad = Math.Min(server.CurrentLoad + 1, server.MaxPlayers);
                else if (server.CurrentLoad > 0 && server.PendingConnections == 0)
                    server.CurrentLoad--;

                server.TouchHeartbeat();
                return true;
            }
        }

        public IReadOnlyCollection<ServerStatus> GetServers()
        {
            lock (_gate)
            {
                PruneExpiredServers();
                return _strategy.SnapshotServers().Select(ToStatus).ToArray();
            }
        }

        private Server? SelectServer(string? affinityKey, Guid? instanceId)
        {
            var snapshot = _strategy.SnapshotServers();

            if (instanceId.HasValue)
            {
                var instanceMatch = snapshot.FirstOrDefault(s => s.Instances.Contains(instanceId.Value));
                if (instanceMatch is not null)
                    return instanceMatch;
            }

            if (!string.IsNullOrWhiteSpace(affinityKey) && _strategy is ConsistentHashingLoadBalancer hashed)
            {
                var hashedServer = hashed.GetServer(affinityKey);
                if (hashedServer is not null)
                    return hashedServer;
            }

            return _strategy.GetNextServer();
        }

        private void PruneExpiredServers()
        {
            var snapshot = _strategy.SnapshotServers();
            foreach (var server in snapshot)
            {
                if (DateTime.UtcNow - server.LastHeartbeatUtc <= _heartbeatTimeout)
                    continue;
                _strategy.RemoveServer(server);
            }
        }

        private static ServerStatus ToStatus(Server server)
        {
            var instanceCopy = server.Instances.Count == 0
                ? []
                : server.Instances.ToArray();
            return new ServerStatus(
                server.Id,
                server.IP,
                server.Port,
                server.Region,
                server.CurrentLoad,
                server.PendingConnections,
                server.MaxPlayers,
                instanceCopy,
                server.LastHeartbeatUtc,
                server.IsAcceptingPlayers);
        }
    }
}

using System.Linq;

namespace XREngine.Networking.LoadBalance.Balancers
{
    public class RoundRobinLeastLoadBalancer(IEnumerable<Server> servers, int roundRobinThreshold = 10) : LoadBalancer(servers)
    {
        private int _currentIndex = 0;

        public override Server? GetNextServer()
        {
            lock (_servers)
            {
                if (_servers.Count == 0)
                    return null;

                // Use Round Robin for servers with load less than the threshold
                var roundRobinServers = _servers.Where(s => s.CurrentLoad < roundRobinThreshold).ToList();
                if (roundRobinServers.Count != 0)
                {
                    _currentIndex %= roundRobinServers.Count;
                    var server = roundRobinServers[_currentIndex];
                    _currentIndex = (_currentIndex + 1) % roundRobinServers.Count;
                    return server;
                }

                // Use Least Connections for servers with load equal or greater than the threshold
                return _servers.OrderBy(s => s.CurrentLoad).FirstOrDefault();
            }
        }
    }
}

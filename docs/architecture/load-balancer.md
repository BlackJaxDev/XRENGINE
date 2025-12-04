# Load Balancer Architecture

## Overview
- `LoadBalancerService` keeps an in-memory catalog of active game hosts, prunes dead entries when heartbeats stop (~60s), and exposes the routing decisions used by the REST controller.
- Multiple selection strategies are supported. The default is `RoundRobinLeastLoadBalancer`, which prefers low-load hosts and falls back to least-connections when everyone is near capacity. A consistent hashing strategy is also available when session affinity is needed.
- Every host updates a canonical `Server` record (ID, IP, port, region, capacity, active instances) so the balancer can factor in pending connections and instance ownership before sending players anywhere else.

## HTTP API
All endpoints are served from the ASP.NET host inside `XREngine.Server` (see `LoadBalancerController`).

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/api/load-balancer/register` | Registers or refreshes a host entry. Returns the tracker payload so callers can cache the assigned `serverId`. |
| `POST` | `/api/load-balancer/heartbeat` | Lightweight heartbeat that updates load, capacity, and hosted instance IDs. Missing servers return `404`. |
| `POST` | `/api/load-balancer/claim` | Reserves capacity for a player. Optional `instanceId` pins players to an existing instance; optional `affinityKey` engages the consistent hashing ring. Returns the destination server when capacity exists, or `503` if everything is full. |
| `POST` | `/api/load-balancer/release` | Confirms or cancels a pending reservation. Use `playerJoined=true` after the server accepts the client so the balancer can update load immediately. |
| `GET` | `/api/load-balancer/servers` | Returns a snapshot of all tracked hosts, useful for dashboards or admin tooling. |

Payloads are modeled in `LoadBalancerController` and map directly to the `LoadBalancerService.ServerStatus` record.

## Running On Server Machines
1. **Publish the binaries**
   ```powershell
   dotnet publish XREngine.Server/XREngine.Server.csproj -c Release -o publish/server
   ```
2. **Deploy the load balancer node**
   - Copy the publish folder to the load balancer VM or container.
   - Run the service in balancer-only mode so the XR engine does not start:
     ```powershell
     dotnet XREngine.Server.dll --load-balancer-only --urls "http://0.0.0.0:5000"
     ```
   - Front this process with your preferred reverse proxy or firewall rules. Scale horizontally by adding more balancer nodes behind DNS if needed.
3. **Run game host servers**
   - Launch the remaining machines without the `--load-balancer-only` switch so the XR world boots as usual.
   - On startup (or via a supervisor script), call `POST /api/load-balancer/register` on the balancer to announce the host and capture the returned `serverId`.
   - Send `POST /api/load-balancer/heartbeat` every 10–15 seconds with the latest player count, max capacity, and list of hosted instance GUIDs. Example PowerShell snippet:
     ```powershell
     $body = {
         serverId = $serverId
         currentLoad = 24
         maxPlayers = 64
         instances = @("1fbf7d39-...", "c125f92c-...")
     } | ConvertTo-Json
     Invoke-RestMethod -Method Post -Uri "http://balancer:5000/api/load-balancer/heartbeat" -Body $body -ContentType "application/json"
     ```
4. **Route players through the balancer**
   - Clients first call `POST /api/load-balancer/claim` with optional `instanceId` (party/session) or `affinityKey` (account GUID).
   - The balancer responds with the target IP/port. Clients connect directly to that host.
   - After the connection succeeds (or fails), your orchestration service should call `POST /api/load-balancer/release` so pending reservations stay accurate.

## Operational Notes
- Health pruning removes hosts that miss heartbeats for ~60 seconds; ensure firewalls allow traffic from game hosts to the balancer.
- Regions are opaque strings; populate them (e.g., `us-east`, `eu-central`) so higher-level services can pre-filter hosts before calling `claim`.
- Switch to `ConsistentHashingLoadBalancer` in `Program.BuildWebApi` if you need stronger stickiness guarantees—only the DI registration needs to change.
- For observability, poll `/api/load-balancer/servers` and push the result into your monitoring system to alert on capacity dips or stale hosts.

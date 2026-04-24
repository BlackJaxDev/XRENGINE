# XREngine Dedicated Server Instance & Matchmaking Design Plan

Implementation tracker: [../todo/multiplayer-networking-implementation-todo.md](../todo/multiplayer-networking-implementation-todo.md)

## Status

- **Area:** Multiplayer networking + dedicated server orchestration
- **State:** Active design proposal
- **Audience:** Runtime/networking, editor tooling, backend/platform
- **Last updated:** 2026-04-24
- **Boundary status:** XRENGINE now owns only direct realtime data-plane connections. Instance directory, allocation, join orchestration, host capacity, token issuance, and world artifact delivery live in the adjacent control-plane app.
- **Realtime status:** engine-side contracts now carry session id/token and exact local world asset identity instead of control-plane room/ticket/artifact DTOs.
- **Next status:** authoritative replication and per-peer UDP sequencing are implemented for the direct realtime path; next XRENGINE-owned work starts with AOT-safe world bootstrap.

## 1. Problem Statement

XREngine needs a production-ready multiplayer architecture where:

1. Dedicated server machines can host one or more live **instances** (rooms/world simulations).
2. Each instance enforces a configurable **max client count**.
3. Each instance is budgeted CPU resources (thread budget / core-share) so multiple instances can safely coexist on one host.
4. Clients (game clients and the editor) can **browse**, **create**, and **join** available instances.
5. The platform can scale out using a control-plane service that knows available hosts and can place/spawn new instances when capacity is exhausted.
6. The control-plane/world delivery app ensures clients and servers have the same immutable world asset before XRENGINE realtime admission.
7. Physics is server-authoritative; object simulation authority can transfer dynamically between users while the server remains final authority.

## 2. Core Requirements

### Functional

- Instance lifecycle: create, start, discover, join, leave, terminate, crash-recover.
- Per-instance settings:
  - `MaxPlayers`
  - `CpuBudget` (threads or normalized capacity units)
  - `TickRate` and `PhysicsRate`
  - World descriptor (`blob url`, content hash, version id)
- Matchmaking / directory APIs:
  - list public instances
  - create instance with policy/metadata
  - join by instance id / code
- Client boot flow:
  - discover instance
  - fetch world manifest + binaries/assets
  - validate hash/version compatibility
  - connect and receive snapshot + deltas
- Simulation authority handoff workflow for interactable objects.
- **Interest management / area-of-interest (AOI)** so each client receives only entities relevant to it.
- **Reconnect / session resume** within a short grace window without losing avatar/inventory state.
- **Party / group travel** so a pre-formed group is placed together into the same instance.
- **Backfill** of partially empty rooms from the matchmaker.
- **Graceful drain / seamless migration** for rolling updates (notify clients, stop accepting joins, optionally transfer to a new instance).
- **Voice / data side-channels** routed through or alongside the realtime transport.
- **Moderation hooks:** kick, ban, mute, report, server-side recording for abuse review.
- **Clock sync and lag compensation** for hit detection / interaction validation.
- **Spectator / observer mode** with reduced replication fidelity.

### Non-functional

- Horizontal scale-out across many server machines and **multiple regions**.
- Fast instance placement (<2s best-effort when warm capacity exists).
- Deterministic authority and anti-cheat posture (server final truth).
- Graceful degradation under host saturation.
- Observability: metrics/logs/traces per host and per instance.
- **Target scale:** hundreds of thousands of concurrent players (CCU) globally, with per-region active-active fleets.
- **Latency budget:** p50 < 60 ms intra-region RTT, p99 < 120 ms; tick budget headroom ≥ 30 %.
- **Cost controls:** scale-to-near-zero off-peak, warm pools sized to SLO join latency.

## 3. Proposed High-Level Architecture

Use a **control plane + data plane** split.

### Project / service boundary

- The adjacent control-plane app owns directory, matchmaking, allocation, admission token issuance, auth, world catalog, world artifact manifests/chunks, and downloads.
- **XRENGINE** owns the realtime data plane: direct endpoint/session admission, exact local world asset comparison, replication, authority, and low-latency client transport.
- `XREngine.Server` should not expose public HTTP directory, allocation, load-balancer, or world-artifact APIs.

### 3.1 Data Plane (Realtime)

- **Instance Host Node** (Windows service/process on each machine)
  - Runs one or more XREngine instance workers (in-process or child-process model).
  - Tracks local resource capacity: CPU, memory, network, active instances.
  - Provides low-latency game transport endpoints for clients.
- **XREngine Instance Worker**
  - Owns world simulation, physics, replication, and authority logic.
  - Exposes runtime counters (player count, tick time, RTT percentiles).

### 3.2 Control Plane (Orchestration)

- **Fleet Manager / Placement Service**
  - Maintains host registry + live capacity.
  - Decides where new instances should be placed.
  - Applies bin-packing with safety headroom.
- **Instance Directory / Matchmaking API**
  - Public query point for clients/editor to browse/create/join instances.
  - Stores instance metadata + join policies.
- **Allocation Gateway**
  - On create/join, resolves an instance id to concrete host endpoint.
  - Can trigger new instance allocation if no suitable room exists.
- **World Manifest Service**
  - Maps world id/version to blob URLs + hashes.
  - Issues short-lived signed asset URLs (SAS) for clients and servers.

### 3.3 Persistence / Infra

- Control-plane database (instances, hosts, sessions, policies).
- Cache for hot directory lookups.
- Azure Blob Storage for world packages and dependency bundles.
- Optional queue/event bus for lifecycle events (instance started/stopped/full).

## 4. Instance Host Model (Single Machine)

Each machine runs a host agent with a configurable global budget:

- `HostMaxConcurrentInstances`
- `HostCpuCapacityUnits` (or `HostDedicatedThreads`)
- `HostMemoryReserveMb`

Each instance requests:

- `CpuBudgetUnits` (or explicit worker thread count cap)
- `MaxPlayers`
- `ExpectedWorldComplexityClass` (Small/Medium/Large for placement hints)

Placement rule example:

- Sum of active `CpuBudgetUnits` <= host capacity * utilization target (e.g., 0.8).
- Reject new allocations if memory or network thresholds exceeded.
- Mark instance `Full` when `ConnectedPlayers == MaxPlayers`.

## 5. Network Session & Discovery Flows

### 5.1 Browse Instances

1. Client/editor calls `GET /instances?region=...&visibility=public`.
2. Directory returns summaries (instance id, map/world, current/max players, ping hints).
3. Client selects one and requests join token.

### 5.2 Create Instance

1. Client/editor calls `POST /instances` with desired world + room policy.
2. Placement service chooses host with available capacity.
3. Host starts worker and loads world package.
4. Directory returns `instanceId`, connection endpoint, and join secret.

### 5.3 Join Instance

1. Client obtains join token from directory.
2. Client downloads world package (if missing/mismatched) using manifest + SAS URL.
3. Client connects to host endpoint and submits token + content hash.
4. Server validates compatibility and admits player.
5. Server sends initial snapshot and begins delta replication.

Current local-dev implementation note:

- The adjacent control-plane app serves world manifests/chunks and performs any required download orchestration.
- A control-plane or launcher can pass XRENGINE the concrete realtime endpoint/session/world identity through `XRE_REALTIME_JOIN_PAYLOAD_FILE` or `XRE_REALTIME_JOIN_PAYLOAD`.
- `ClientNetworkingManager` now sends only the local `WorldAssetIdentity` during realtime join.
- The client validates an externally supplied expected `WorldAssetIdentity` before starting the UDP join loop.
- `XREngine.Server` rejects realtime joins when the client's local world asset identity does not exactly match the server's local world asset identity.
- After admission, the server assigns a canonical `NetworkEntityId`, grants a `NetworkAuthorityLease`, validates client input/transform/pose traffic against that lease, and rebroadcasts server-stamped authoritative state.
- Phase 2 relevance and budget policy now routes through per-client UDP endpoints with per-peer sequence/ACK state, so one client's budget can drop updates without disturbing another client's packet ordering.

### 5.4 Auto-Scale / No Capacity

If no host has capacity:

- Allocation gateway issues a `PendingAllocation` response.
- Fleet manager asks compute orchestrator to spin up additional host VM/container.
- Once healthy, allocation is completed and client retries (or long-polls/receives callback).

## 6. World Asset Packaging & Versioning (Azure Blob)

Define immutable world artifacts:

- `worldId` (logical name)
- `worldVersion` (semantic or content-based)
- `contentHash` (strong hash of package)
- `manifest.json` (dependencies + chunk hashes)

Server startup:

1. Resolve world descriptor.
2. Download/verify package if not cached locally.
3. Load world and produce server-ready state.

Client join:

1. Compare local cache by `worldId + worldVersion + contentHash`.
2. Download only missing chunks.
3. Validate before requesting a realtime endpoint/session.

Current implementation note:

- World artifact manifest loading, chunk reuse, package hash verification, stable cache layout, eviction, and manual purge live outside XRENGINE.
- XRENGINE treats the resulting local asset identity as the realtime compatibility key and does not download artifacts during join.

## 7. Authority, Physics, and Ownership Transfer

### 7.1 Authority Model

- **Server authoritative for final physics + replicated state.**
- Clients may receive temporary **input authority** for interactables (grab, drag, drive), but server validates and can override.
- Current runtime contracts use `NetworkEntityId`, `NetworkAuthorityLease`, `NetworkAuthorityMode`, and explicit revocation reasons. `PlayerAssignment` carries the lease, and the server rejects input/transform/pose traffic that does not match the active lease.

### 7.2 Dynamic Ownership Transfer

Per networked entity track:

- `OwnerClientId` (nullable)
- `AuthorityMode` (`ServerOnly`, `ClientPredicted`, `ClientDrivenValidated`)
- `AuthorityLeaseExpiry`

Transfer flow:

1. Client requests ownership/interaction.
2. Server validates proximity/rules/cooldowns.
3. Server grants lease and broadcasts new owner.
4. Owner sends high-rate inputs/state proposals.
5. Server simulates, clamps/corrects, and replicates truth.
6. Lease expires/revoked on disconnect/conflict/rule violation.

### 7.3 Reconciliation

- Snapshot + delta stream with tick ids.
- Client prediction for owned objects and local avatar.
- Server correction messages with rewind/replay on mismatch.
- Current runtime DTOs carry server tick ids, baseline tick ids, client input sequence, and last processed input sequence. Clients apply server correction transforms for locally owned actors.

### 7.4 Clock Sync, Input Buffering, Lag Compensation

- Periodic NTP-style exchange between client and server to estimate offset + jitter; clients render at `serverTime - renderDelay` where `renderDelay` is adaptive to measured jitter.
- Inputs are timestamped with client tick; server buffers a small input window (e.g., 2-4 ticks) to absorb jitter before applying.
- Server retains a short history of authoritative state (ring buffer, N ticks) so hit/interaction checks can rewind to the shooter's render-time view (lag compensation), bounded to a max rewind (e.g., 200 ms) to limit abuse.
- Current runtime heartbeats include client send time and last received server tick. The server replies with `ClockSyncMessage`, keeps bounded input buffers via `RealtimeReplicationCoordinator`, and exposes lag-compensation timing policy in `MultiplayerRuntimePolicy`.

### 7.5 Interest Management (AOI)

- Entities publish a replication key (position + relevance radius + visibility flags).
- Per-client relevance set maintained via spatial index (grid / BVH / octree) updated on tick.
- Updates prioritized by (distance, recency, entity importance, bandwidth budget); low-priority entities get coarser quantization and lower update rate.
- Per-connection outbound byte-rate cap with priority-aware shedding.
- Current runtime gates authoritative transform emission with per-connection `NetworkBandwidthBudget`, relevance radius accounting, and strict per-endpoint packet routing.

## 8. API Surface (Draft)

Control plane (HTTPS/JSON):

- `POST /instances` create room
- `GET /instances` browse rooms
- `GET /instances/{id}` room details
- `POST /instances/{id}/join` issue join token + endpoint
- `POST /instances/{id}/reserve` (optional party reservation)
- `DELETE /instances/{id}` terminate (owner/admin)

Host agent (internal mTLS):

- `POST /allocations` start instance worker
- `POST /allocations/{id}/stop`
- `GET /healthz`
- `GET /capacity`

Realtime data plane:

- Authenticated connect handshake (`instanceId`, token, build/world hash)
- Channels: reliable control, unreliable state deltas, optional voice/data extensions

## 9. Editor and Client UX Integration

Editor and runtime clients should expose the same multiplayer lobby model:

- **Browse tab:** list active instances, filters (region/world/player count).
- **Create tab:** choose world asset id/version, max players, visibility, simulation preset.
- **Join flow:** join by list selection or invite code.
- **Status panel:** direct connection quality, assigned session, local world asset identity, desync alerts.

Current implementation note:

- The ImGui networking panel now exposes only direct realtime settings, optional session id/token, diagnostics, connected clients, and local player assignment status.

Editor-specific capability:

- Mark instances as `dev/test/private`.
- Optionally hot-reload world revisions into newly created test rooms only.

## 10. Security and Trust

- Signed short-lived join tokens (JWT or equivalent), bound to `instanceId`, `userId`, and connection fingerprint; single-use where possible.
- Signed short-lived blob URLs (SAS) scoped to specific world/version.
- mTLS or private network auth between control plane and host agents.
- Server-side validation for all client-driven state (velocity/teleport clamps, action cooldowns, inventory invariants).
- Rate limits for create/join APIs and per-IP connection attempts; per-account connection caps.
- **DDoS posture:** UDP ingress behind cloud-provider scrubbing (Azure DDoS Protection Standard / AWS Shield Advanced); SYN/UDP flood mitigation at the edge; connectionless amplification guarded by source-address validation tokens in the handshake.
- **Identity:** OIDC (Entra ID / Cognito / custom) issues user tokens; realtime join tokens are minted by the directory service after user token validation.
- **Anti-cheat:** server-authoritative simulation + input sanity; optional integrity attestation (signed client build hash) and replay recording for offline review.
- **Secrets:** Azure Key Vault / AWS Secrets Manager; no long-lived credentials on host VMs (use managed identities / IAM roles).

## 11. Observability & Operations

Per-instance metrics:

- active players, join failures, tick duration, physics step duration
- replication bandwidth, packet loss, RTT, correction frequency
- authority-transfer count, forced revoke count

Per-host metrics:

- CPU/memory/network utilization
- capacity units used/free
- instance startup latency

Operational features:

- drain mode (stop new joins, let rooms empty)
- rolling host updates
- crash restart policy with instance recovery metadata

## 12. Recommended Deployment Topology

1. **Directory + Matchmaking Service** in the adjacent control-plane app (stateless, horizontally scaled)
2. **Placement/Fleet Manager** in the non-realtime control-plane service (stateful decision logic + host heartbeats)
3. **Host Agent** on each realtime machine
4. **XREngine.Server workers** launched/managed by host agent
5. **Azure Blob + CDN** for world packages and client asset fetch

Start with single region, then extend to multi-region with latency-aware matchmaking.

## 13. Implementation Roadmap (Next Steps)

The 2026-04-24 boundary cleanup moved directory/instance/world-delivery implementation out of XRENGINE. The next XRENGINE-owned step is realtime admission test coverage and authoritative replication hardening.

### Phase 0 — Foundations (1-2 sprints)

- Define canonical instance schema and lifecycle states.
- Add server runtime config for:
  - max clients per instance
  - cpu budget units / worker thread cap
  - tick + physics rates
- Implement per-instance admission control (`Full`, `Locked`, `Starting`, `Running`).
- Add world descriptor support (`worldId`, `worldVersion`, `contentHash`, blob URI).

### Phase 1 — Single Host Multi-Instance (2-4 sprints)

Status: moved out of XRENGINE. Local directory, lifecycle, host capacity, and create/list/join orchestration belong to the adjacent control-plane app.

- Build local host agent in front of XREngine.Server workers. External control-plane app.
- Implement instance create/start/stop on one machine. External control-plane app.
- Implement room browser / create / join API. External control-plane app.
- Implement client/editor browse-create-join flow against this API. External control-plane app.
- Add deterministic world compatibility handshake and asset download validation. External world-delivery flow; XRENGINE validates exact local identity at realtime join.

### Phase 2 — Authority + Physics Networking Hardening (2-4 sprints)

- Introduce networked object authority lease model.
- Add server reconciliation and correction pipeline for client-owned interactions.
- Add anti-cheat validation rails (velocity/teleport clamps, action sanity windows).
- Build soak tests for high-interaction rooms.

### Phase 3 — Fleet Placement and Scale-Out (3-6 sprints)

- Implement fleet manager and host heartbeat/capacity reports in the non-realtime control-plane service, not inside XREngine.Server.
- Add placement strategy using cpu budget + player slots + headroom.
- Add pending allocation flow when no host capacity exists.
- Integrate VM/container auto-scale hooks.

### Phase 4 — Production Readiness (ongoing)

- Add authn/authz, rate limiting, and full telemetry dashboards.
- Add regional routing and failover strategy.
- Load/stress validation for target CCU.
- Run disaster drills (host failure, blob unavailability, control-plane outage).

## 14. Open Design Decisions

1. Worker model: multi-instance in one process vs one-process-per-instance (see §16.2).
2. Transport stack: v1 native clients use the existing UDP data plane. WebRTC/QUIC remains a follow-up if browser clients are brought into scope.
3. Snapshot format and compression strategy for large worlds (bitpacked quantized deltas vs schema-based diffing).
4. Party reservation and invite-code semantics.
5. Persistence model for room state (ephemeral only vs checkpoint resume).
6. How much editor-only metadata can coexist with production room descriptors.
7. Orchestrator choice: managed (PlayFab Multiplayer Servers / AWS GameLift) vs self-managed (Agones on AKS/EKS). See §16.
8. Matchmaking: build on OpenMatch, use PlayFab Matchmaking / GameLift FlexMatch, or start with a custom directory and graduate later.
9. Cross-region player-meeting strategy (strict region affinity vs latency-based placement with relay).

## 15. Immediate Action Items (Concrete)

1. Keep `MultiplayerInstanceConfig` and create/list/join orchestration in the adjacent control-plane app, outside shared engine runtime code.
2. Keep server-side instance lifecycle, max-player gate enforcement, and host capacity in the adjacent control-plane app; XRENGINE enforces only realtime session admission.
3. Implement world manifest verification and download orchestration outside XRENGINE; the engine verifies only the exact local `WorldAssetIdentity` at realtime join.
4. Stand up minimal directory service (`create/list/join`) for local dev. External control-plane app.
5. Keep editor browse/create/join UI outside XRENGINE; the engine editor keeps only direct realtime controls.
6. Add integration tests:
  - create 3 instances with different max-player caps
  - join until full and assert rejection behavior. Done.
  - stop or drain an instance and assert rejection behavior. Done.
  - validate ownership transfer under contention
7. Define performance SLOs (tick budget, join latency, startup latency) and baseline them.

---

## 16. Cloud Hosting Recommendations for the Realtime Layer

> Scope: everything in §3.1 (data plane), §3.2 (control plane), §4 (host model), and §6 world-asset delivery. The non-realtime website / platform API is covered separately in §17.

Target: **hundreds of thousands of concurrent players** globally. This implies multi-region active-active fleets, UDP-capable ingress, fast auto-scale, and aggressive cost controls.

### 16.1 Capacity Math (Planning Reference)

Assume conservative per-instance sizing: 50 players/instance, 2 vCPU + 4 GB RAM/instance, 4 instances/VM host (8 vCPU / 16 GB).

- 300 000 CCU ÷ 50 = **6 000 active instances**.
- 6 000 ÷ 4 = **1 500 active VMs**.
- Spread across (e.g.) 6 regions ≈ **250 VMs/region** at peak, plus a 20–30 % warm-pool buffer for join-latency SLOs.
- Per-VM sustained egress estimate: 50 kbps × 200 players = ~10 Mbps; a fleet that size sustains multi-Tbps aggregate egress — budget egress cost explicitly.

Revisit once real tick-cost and bandwidth numbers exist; this is a sizing frame, not a commitment.

### 16.2 Recommended Orchestration Model

Preferred path: **one process per instance**, scheduled by a fleet orchestrator, because it gives clean crash isolation, per-instance resource cgroups/Job Objects, and simple drain-on-update. Accept the extra process overhead; it is small relative to simulation cost.

Two production-viable stacks:

**Option A — Managed game-server hosting (fastest to ship).**
- **Azure:** *Azure Player Services / PlayFab Multiplayer Servers (MPS)*. Uploads a container or zipped build; MPS handles VM pools, per-region scale, allocation API, and UDP port mapping. Pair with PlayFab Matchmaking or a custom directory.
- **AWS:** *Amazon GameLift Servers* (formerly just "GameLift"). Fleets + queues + FlexMatch matchmaker. Supports Windows and Linux game servers, UDP/TCP, and spot/on-demand mix via FleetIQ.
- Pros: fewest moving parts, built-in session placement, game-specific SLAs.
- Cons: vendor lock-in; custom placement logic must fit provider model.

**Option B — Self-managed Kubernetes with Agones (most control).**
- **Azure:** AKS + [Agones](https://agones.dev/) + optionally [Open Match](https://openmatch.dev/) for matchmaking.
- **AWS:** EKS + Agones + Open Match.
- Agones models each instance as a `GameServer` CRD, handles allocation, health, and drain. Open Match provides a pluggable matchmaker that fits the §5 directory/allocation gateway cleanly.
- Pros: portable across Azure/AWS/bare-metal, full control of placement and networking, works well with XREngine's own host agent.
- Cons: you operate it (node pools, autoscaler, upgrades, CNI, security).

**Recommendation:** start on **Option A** in one cloud to prove the product, design the control plane (§3.2) so the orchestrator is pluggable, and migrate to **Option B (Agones)** once scale, cost, or feature constraints justify it. The §8 API surface should be defined in XREngine terms and adapt to either backend.

### 16.3 Realtime Network Ingress

UDP is first-class. Do **not** try to serve realtime traffic through an HTTP-only load balancer.

- **Azure:** Azure **Standard Load Balancer (UDP)** or **public IP per VM** with port ranges; pair with **Azure DDoS Protection Standard**. For global latency smoothing, **Azure Front Door** covers HTTP control plane only — realtime traffic uses direct regional endpoints advertised by the directory.
- **AWS:** **Network Load Balancer (UDP listener)** or **Elastic IP per instance**; **AWS Global Accelerator** provides anycast UDP ingress that routes to the nearest healthy regional NLB and is the preferred front door for realtime at this scale. **AWS Shield Advanced** for DDoS.
- **Port allocation:** each game-server process binds a distinct UDP port; the allocation response returns `host:port`. MPS, GameLift, and Agones all expose this natively.
- **QUIC/WebRTC:** if browser clients are in scope, terminate WebRTC at dedicated SFU/TURN nodes or use WebTransport over QUIC; keep native clients on raw UDP for lower overhead.

### 16.4 Scaling Strategy

- **Warm pool per region** sized so 95th-percentile join latency stays under SLO (e.g., keep N idle `Ready` GameServers).
- **Cluster autoscaler** (AKS/EKS) or managed fleet scaler (MPS/GameLift) adds nodes when warm pool dips below threshold.
- **Spot / Low-Priority VMs** for off-peak and for soak/load traffic; on-demand for baseline. Tag instances as `preemptible=true` and avoid placing premium/private rooms there.
- **Scale-down policy:** drain empty rooms first, then evict empty nodes after cool-down; never evict a node with live players.
- **Per-region isolation:** each region is an independent failure domain; a region outage should not take down global matchmaking (directory is multi-region replicated, see §16.6).

### 16.5 World Asset Delivery at Scale

- **Azure:** world packages in **Azure Blob Storage (RA-GZRS)** fronted by **Azure Front Door / Azure CDN**. Signed SAS URLs per client.
- **AWS:** **S3 + CloudFront**; pre-signed URLs per client.
- Chunked manifests (§6) so only deltas ship on revision updates.
- Pre-warm caches in each region before a major world release.
- Server hosts pull from a regional cache, not global origin, to keep instance startup fast.

### 16.6 Control-Plane Data Stores

- **Instance / session directory:** globally distributed key-value store — **Azure Cosmos DB** (multi-region writes) or **Amazon DynamoDB Global Tables**. Directory reads are the hot path; use strong local reads, async cross-region replication.
- **Hot cache for "list instances":** **Azure Cache for Redis** / **Amazon ElastiCache (Redis)** per region, with a short TTL (1–5 s) and invalidation on lifecycle events.
- **Lifecycle events:** **Azure Service Bus / Event Grid** or **Amazon SNS/SQS/EventBridge** to decouple host agents from directory writers.
- **Matchmaker state:** Redis or a purpose-built store (OpenMatch uses Redis).

### 16.7 Identity, Auth, and Tokens (Realtime Path)

- User logs in via the platform identity provider (see §17) and receives a user token.
- Directory service validates the user token, runs placement/matchmaking, and mints a **short-lived join token** (JWT, ~60 s TTL) bound to `instanceId` + `userId`.
- Game server validates the join token locally using cached JWKS; no synchronous round-trip to the control plane at connect time.
- Rotate signing keys automatically via Key Vault / Secrets Manager.

### 16.8 Observability

- **Metrics:** OpenTelemetry from game servers → **Azure Monitor / Managed Prometheus** or **Amazon Managed Prometheus + Managed Grafana**.
- **Logs:** structured JSON → **Log Analytics** or **CloudWatch Logs** (retain short; archive to Blob/S3).
- **Traces:** span per join flow spanning directory → allocator → host → instance.
- **Per-instance KPIs** (from §11) exported as labeled Prometheus series keyed by `region`, `instanceId`, `worldVersion`.
- **Synthetic probes:** periodic fake-client join + sync from each region to catch control-plane regressions before users do.

### 16.9 Network Egress and Cost Controls

- Realtime UDP egress is the dominant cloud bill. Keep clients on the **nearest region** to minimize inter-region transfer.
- Prefer **Global Accelerator (AWS) / Azure cross-region anycast** for client → edge, but terminate sessions in-region.
- Avoid cross-AZ chatter inside an instance; pin the server process and its dependencies to one AZ.
- Enable compression / quantization (§7.5) aggressively; measure bits/player/sec as a first-class SLI.

### 16.10 Disaster & Failure Modes

- **Region outage:** directory fails reads to remaining regions; matchmaker stops placing in the failed region; active sessions in that region are lost (document this — session resume §2 only covers brief reconnects).
- **Control-plane outage:** active sessions keep running (game servers validate tokens locally); new joins fail fast with a user-visible retry message.
- **Host crash:** orchestrator restarts process; clients receive a `ServerLost` event and can re-queue via the directory.
- **Blob outage:** servers refuse to start new instances missing cached worlds; existing instances unaffected.

### 16.11 Cloud Choice Summary

| Need | Azure | AWS |
|---|---|---|
| Managed game-server hosting | PlayFab Multiplayer Servers | Amazon GameLift Servers |
| Self-managed orchestration | AKS + Agones (+ Open Match) | EKS + Agones (+ Open Match) |
| UDP ingress | Standard LB (UDP) + DDoS Std | NLB (UDP) + Global Accelerator + Shield Adv |
| World asset CDN | Blob + Front Door/CDN | S3 + CloudFront |
| Directory DB | Cosmos DB (multi-region write) | DynamoDB Global Tables |
| Hot cache | Azure Cache for Redis | ElastiCache Redis |
| Identity | Entra External ID / PlayFab | Cognito |
| Secrets | Key Vault | Secrets Manager |
| Observability | Azure Monitor + Managed Grafana | Managed Prometheus + Managed Grafana |

Either cloud can hit the target. The practical tiebreaker is the team's existing ops footprint and whether browser clients are in scope (favors WebRTC/WebTransport support maturity).

---

## 17. Non-Realtime Website / Platform API (Separate Project)

> This section is informational only. The website / platform API is a **separate project and separate deployment** from the realtime networking layer described above. It is called out here strictly to define the integration seam.

### 17.1 Responsibilities

The website/platform API owns everything that is **not** in the realtime data plane:

- User accounts, profiles, social graph, friends, parties (pre-match).
- Authentication / OIDC issuance and refresh.
- Persistent player data (inventory, progression, cosmetics, settings).
- World catalog, authoring uploads, publishing workflow, versioning metadata.
- Store / entitlements / receipts.
- Reports, moderation case files, ban ledger.
- Analytics ingestion and dashboards for business metrics.
- Web UI for browse/create/join (thin client over the realtime directory, see 17.3).

### 17.2 Technology Shape (Suggested)

- Stateless HTTP services behind a standard web load balancer (App Service / Container Apps / ECS Fargate / App Runner).
- Relational store for account + commerce data (Azure SQL / Aurora PostgreSQL).
- Object storage for user-generated content (Blob / S3) — same storage family as world assets, but separate containers/buckets and separate lifecycle policies.
- Background workers for thumbnail generation, ingest pipelines, moderation scans.
- Stage / prod environments isolated from realtime fleets.

### 17.3 Integration Seam With the Realtime Layer

The website and the realtime layer must stay loosely coupled. The **only** contract between them is:

1. **Identity:** the website issues (or federates) user tokens that the realtime directory can validate via JWKS.
2. **World manifests:** the website publishes immutable world packages + manifests to Blob/S3 using the schema defined in §6; the realtime layer treats them as read-only inputs.
3. **Directory read-through (optional):** the website may call the realtime directory's public `GET /instances` for a "live rooms" page. It must **not** perform allocation or mutate instance state.
4. **Outbound events:** realtime publishes lifecycle events (instance started/ended, player joined/left, report filed) to a shared event bus; the website consumes them asynchronously for analytics, progression, and moderation.
5. **Persistent player data:** game servers read/write player profile data only through a narrow, rate-limited, authenticated **Player Data API** exposed by the website — never via direct DB access.

### 17.4 Explicit Non-Goals For This Project

To keep the realtime layer's failure domain small, the website/platform API must **not**:

- Participate in per-tick replication or allocation decisions.
- Be on the hot path of the join handshake (join tokens are validated locally on game servers).
- Own session-scoped state that a game server needs mid-match.
- Share databases with the realtime directory.

This separation is what allows the realtime fleet to keep running during a website outage, and vice versa.

---

This plan intentionally separates control-plane orchestration from realtime simulation, and both of those from the non-realtime platform/website API, so XREngine can scale from single-machine development to multi-region production hosting while preserving server-authoritative simulation integrity.

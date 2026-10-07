# Networking Validation

Architecture: [Control Plane](../../../architecture/runtime/control-plane.md), [Networking Overview](../../../architecture/networking/overview.md), [Peer-To-Peer Host Switching Design](../../design/networking/peer-to-peer-host-switching.md)  Code todos: [Control plane managed server instances](../../todo/networking/control-plane-managed-server-instances-todo.md), [Peer-to-peer host switching](../../todo/networking/peer-to-peer-host-switching-todo.md)

## Setup

Use the managed service tasks in `.vscode/tasks.json` when a control-plane check needs a live service. Use `Build-ControlPlane-Service`, then `Start-ControlPlane-Service` with the `controlPlaneConfig` input. Use `Build-Server`, `Start-DedicatedServer-NoDebug`, `Start-Server-NoDebug`, `Start-Client-NoDebug`, `Start-Client2-NoDebug`, `Start-2Clients-NoDebug`, and `Start-LocalPoseSync-NoDebug` for local worker and client checks.

Use the launch profiles `Debug Server (Server only)`, `Debug Server (Clients runs separately)`, and `Debug Client (Server & other client run separately)` when debugging a managed or direct-development path.

Relevant environment variables are `XRE_NET_MODE`, `XRE_UDP_CLIENT_RECEIVE_PORT`, `XRE_WORLD_MODE`, `XRE_UNIT_TEST_WORLD_KIND`, `XRE_NETWORKING_POSE_ROLE`, `XRE_POSE_ENTITY_ID`, `XRE_POSE_BROADCAST_ENABLED`, `XRE_POSE_RECEIVE_ENABLED`, `XRE_MANAGED_CLIENT_CONFIG_FILE`, and `XRE_MANAGED_CLIENT_CONFIG_DELETE_AFTER_READ`. Public gateway checks also need operator TLS and admission-signing configuration.

Do not start the editor or managed service from this document cleanup. Run these checks only during feature validation.

## Checks

### Control Plane Managed Server Instances

Architecture link: [Control Plane](../../../architecture/runtime/control-plane.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Service API smoke | Start `Start-ControlPlane-Service`. Query `/health`, `/v1`, `/v1/packages`, and `/v1/instances` with a configured bearer credential. | The service reports ready, returns contract version 1, and does not expose worker credentials. | Open | Last evidence: none. |
| Two-worker lifecycle | Create two managed instances from distinct packages. Stop each instance through the service. | Each worker has a distinct endpoint, working directory, generation, and process. Stop releases only owned resources. | Open | 2026-09-14: local dedicated processes and native clients validated the earlier lifecycle path. |
| Bind and startup failure handling | Create an instance with an occupied UDP port, a missing package, a corrupt package, and a launch cancellation. | Each failure returns a specific reason. The service leaves no orphan process and no stale capacity reservation. | Open | Last evidence: none. |
| Package identity enforcement | Launch a worker and a client with different or modified package bytes. | Readiness or admission fails. Copying identity strings does not make the content valid. | Open | 2026-09-14: verified-package checks were validated for the managed path. |
| Admission and occupancy accounting | Fill a room, let an unused reservation expire, cancel a reservation, kick a player, disconnect, and reconnect during the resume window. | Directory counts, worker rosters, and client state agree. Full rooms reject excess clients. | Open | 2026-09-14: reservation, admission, and sender binding were validated for the managed path. |
| Authenticated realtime sender binding | Attempt to move another player, refresh another heartbeat, submit another input, or publish server-only state by changing payload IDs. | The server rejects the mutation and leaves roster, lease, and endpoint state unchanged. | Open | 2026-09-14: authenticated sender binding was validated. |
| Late join synchronization | Join after world state and avatar state changed. Interrupt the baseline transfer and deliver duplicate or out-of-order deltas. | The late joiner receives the relevant baseline and deltas or gets an explicit resynchronization failure. | Open | 2026-09-14: managed replication and synchronization were validated. |
| Authoritative input and reconciliation | Run three authenticated clients. Send valid input, invalid transform proposals, and interactable lease requests. | Server simulation owns authoritative state. Acknowledgments advance only after input simulation. Invalid proposals are rejected. | Open | 2026-09-14: live simulation validated input acknowledgment, correction, schema rejection, and lease transfer. |
| Tick and allocation budget | Run multiple clients with the configured fixed step and profiler counters enabled. | Tick duration, input queue age and depth, correction magnitude, replication cost, and hot-path allocations stay within declared budgets. | Open | Last evidence: none. |
| Sample UI and native launcher | Use the sample UI to browse, create, reserve, and launch an allowlisted native client. | The browser never receives bearer player credentials in a URL or log. The native client reaches synchronized state. | Open | Last evidence: none. |
| Instance switching | Switch a running editor, VR client, and representative game client between two instances. | Old player objects, leases, ports, and environment overrides do not leak into the new instance. | Open | Last evidence: none. |
| Local end-to-end milestone | Run two dedicated instances with distinct worlds and three clients. Put two clients in one instance and one client in the other. | Isolation holds for gameplay, entities, poses, credentials, occupancy, filesystem state, and shutdown. | Open | Last evidence: none. |
| Restart recovery | Restart the service and host agent while workers are present. Then test stale lease expiry, crash replacement policy, and port reuse. | Surviving workers are verified or marked stale. Worker crashes report session loss unless a checkpoint policy restores simulation state. | Open | Last evidence: none. |
| Public hosting protection | Run the gateway with authenticated HTTPS APIs, admission signatures, `NativeTls`, and redaction enabled. | Public handoff requires protected admission, public connections without required protection fail, and logs redact credentials. | Open | Last evidence: none. |
| Public hosting scale and fault run | Run sustained load with content outages, host loss, delayed and duplicate events, capacity exhaustion, and region unavailability. | Operators get measured capacity, startup latency, join latency, synchronization time, tick headroom, and recovery behavior. | Open | Last evidence: none. |

### Peer-To-Peer Host Switching

Architecture link: [Peer-To-Peer Host Switching Design](../../design/networking/peer-to-peer-host-switching.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Local trusted startup | Start two local peers through the future peer tasks and `LocalControlPlane`. | One peer hosts, one peer joins, and world and build validation match client/server mode. | Blocked by missing peer implementation | Last evidence: none. |
| Trusted election | Run a fixed roster with deterministic candidate metrics. | Peers select the same host and reject stale host epochs before mutating state. | Blocked by missing peer implementation | Last evidence: none. |
| Graceful trusted migration | Force host migration from the diagnostics panel. | Participants stay connected, endpoint redirection completes, and UDP sequence counters do not transfer across endpoints. | Blocked by missing peer implementation | Last evidence: none. |
| Failure trusted migration | Kill the host process in a LAN session. | Remaining peers recover within the configured timeout from the latest committed snapshot. | Blocked by missing peer implementation | Last evidence: none. |
| Public signed transport | Run a public-tier session through relayed candidates. | Peers complete the encrypted handshake, bound signature verification stays within budget, and candidate failover works. | Blocked by missing peer implementation | Last evidence: none. |
| BFT election | Run four public peers with one Byzantine peer that floods proposals, equivocates votes, and sends fake snapshots. | Honest peers commit the correct host and package equivocation evidence. | Blocked by missing peer implementation | Last evidence: none. |
| Public migration certificate | Run graceful and failure public migrations. | Each migration creates a `CommitCertificate` that every honest peer verifies. Forks abort cleanly. | Blocked by missing peer implementation | Last evidence: none. |
| Witness divergence | Run a malicious host that publishes a state root inconsistent with the input stream. | A witness detects divergence, broadcasts evidence, and triggers migration. | Blocked by missing peer implementation | Last evidence: none. |
| Control-plane revocation | Send equivocation evidence to the abuse report sink. | The sample control plane revokes attestation and evicts the peer on the next roster delta. | Blocked by missing peer implementation | Last evidence: none. |

## Hardware Matrix

No special hardware is required for the local managed-service and peer-mode checks. Public hosting checks require at least one non-loopback host and network conditions that cover direct UDP, TLS gateway, and relay or NAT traversal behavior.

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| None recorded | None recorded | None recorded |

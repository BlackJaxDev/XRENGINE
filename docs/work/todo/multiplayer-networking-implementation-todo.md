# Multiplayer Networking Implementation Todo

Last Updated: 2026-04-24

Current Status: the project boundary has been reset. XRENGINE now owns only the realtime data plane: direct client-to-server and client-to-client connections using concrete IP/port/session information. Instance browsing, creation, joining, allocation, host capacity, admission token issuance, and world artifact distribution have moved to the adjacent control-plane app.

Phase 0, Phase 1, and Phase 2 are implemented on 2026-04-24. Remaining open work starts at Phase 3.

Non-goal: this tracker is not the stable networking overview. It is the execution checklist for the remaining XRENGINE realtime work.

## Boundary Decision

XRENGINE must not manage public instance lifecycle or world downloads.

- The adjacent control-plane app owns directory, matchmaking, room browsing, room creation, join reservation, host registration, capacity, admission token issuance, world artifact descriptors, manifests, chunks, and downloads.
- XRENGINE owns realtime UDP/P2P transport, join admission against a supplied endpoint/session, world asset identity comparison, player assignment, input, transform, heartbeat, leave, remote jobs, pose frames, and future authoritative replication.
- The realtime handshake may accept an opaque `SessionId` and optional `SessionToken`, but it must not mint those values or browse for sessions.
- A client may join a realtime room only when its local `WorldAssetIdentity` exactly matches the server's local `WorldAssetIdentity`.
- World names, scene names, and game-mode strings are advisory display/bootstrap hints, not compatibility keys.

## Cleanup Completed On 2026-04-24

- [x] Moved non-realtime instance/control-plane responsibilities out of XRENGINE.
- [x] Removed the in-repo control-plane sample projects from the solution and VS Code launch/tasks.
- [x] Removed XREngine.Server HTTP/load-balancer/control-plane scaffolding:
  - `XREngine.Server/Controllers/LoadBalancerController.cs`
  - `XREngine.Server/LoadBalance/*`
  - `XREngine.Server/ControlPlane/*`
  - `XREngine.Server/Server.cs`
- [x] Removed engine-side instance and world-download managers:
  - `XREngine.Server/Instances/ServerInstanceManager.cs`
  - `XREngine.Server/Instances/WorldDownloadService.cs`
  - `XREngine.Runtime.Core/Networking/WorldArtifactCacheService.cs`
  - `XREngine.Runtime.Core/Networking/SampleWorldArtifactUtilities.cs`
  - `XREngine.Runtime.Core/Networking/MultiplayerJoinTicketMetadataKeys.cs`
- [x] Replaced instance/ticket/world-artifact runtime contracts with realtime-only contracts:
  - `WorldAssetIdentity`
  - `RealtimeEndpointDescriptor`
  - `AdmissionFailureReason`
- [x] Replaced old instance fields on realtime messages with direct session/world-asset fields:
  - `PlayerJoinRequest.ClientWorldAsset`
  - `PlayerJoinRequest.SessionId`
  - `PlayerJoinRequest.SessionToken`
  - `PlayerAssignment.SessionId`
  - `PlayerInputSnapshot.SessionId`
  - `PlayerTransformUpdate.SessionId`
  - `PlayerLeaveNotice.SessionId`
  - `PlayerHeartbeat.SessionId`
  - `WorldSyncDescriptor.Asset`
- [x] Replaced `Engine.ServerInstanceResolver` with `Engine.ServerSessionResolver` and `Engine.ServerJoinAdmissionResolver`.
- [x] Added `WorldAssetIdentityProvider` so the engine can derive a stable local identity from the loaded world or explicit environment overrides.
- [x] Simplified the ImGui networking panel to direct realtime settings, session id/token, diagnostics, connected clients, and local player status.
- [x] Updated networking contract tests around realtime DTOs and exact local world asset identity.

## Current Runtime Shape

### Client Flow

- Client is configured with direct endpoint settings:
  - `GameStartupSettings.ServerIP`
  - `UdpServerBindPort`
  - `UdpServerSendPort`
  - `UdpClientRecievePort`
  - `UdpMulticastGroupIP`
  - `UdpMulticastPort`
  - `MultiplayerTransport`
  - optional `MultiplayerSessionId`
  - optional `MultiplayerSessionToken`
  - optional `ExpectedMultiplayerProtocolVersion`
  - optional `ExpectedMultiplayerWorldAsset`
- Client can also import an externally issued realtime handoff JSON from `XRE_REALTIME_JOIN_PAYLOAD_FILE` or `XRE_REALTIME_JOIN_PAYLOAD`.
- Client derives its local `WorldAssetIdentity` from the loaded world.
- If the handoff supplies an expected `WorldAssetIdentity`, the client validates it against the loaded local world before the UDP join loop starts.
- Client repeatedly sends `PlayerJoinRequest` with build version, local world identity, optional session id, and optional token until assigned.
- After assignment, client filters input, heartbeat, transform, and leave traffic by `SessionId`.
- `PlayerAssignment` carries a canonical `NetworkEntityId` and server-issued `NetworkAuthorityLease`.
- Client input and predicted transform updates include entity id, client tick/input sequence, and authority mode.
- Client applies server corrections for locally owned actors and tracks the last processed input sequence/server tick.
- Client heartbeat participates in clock sync by reporting client send time, last server tick, and last processed input sequence.

### Server Flow

- Dedicated server starts only the realtime engine process. It does not host an HTTP API.
- `XRE_SESSION_ID` may pin the hosted realtime session id. If omitted, the server generates one per process.
- `XRE_SESSION_TOKEN` may require an opaque token on realtime join. XRENGINE verifies equality only; token issuance stays outside the engine.
- `XRE_UDP_BIND_PORT` / `XRE_UDP_SERVER_BIND_PORT` configure the local UDP bind port.
- `XRE_UDP_ADVERTISED_PORT` / `XRE_UDP_SERVER_SEND_PORT` configure the endpoint port advertised to clients.
- `XRE_UDP_MULTICAST_GROUP` and `XRE_UDP_MULTICAST_PORT` configure multicast settings.
- On join, server rejects:
  - protocol/build mismatch
  - unknown requested session
  - missing local world identity
  - incompatible required world build
  - exact world asset mismatch
  - invalid optional session token
- Server broadcasts assignment, world descriptor, canonical entity id, and authority lease only after the local world asset check passes.
- Server validates client input, transform, and pose frames against the active authority lease before accepting them.
- Server stamps accepted transform and pose traffic with server tick ids and server time before rebroadcasting authoritative state.
- Server keeps bounded input buffers for jitter/lag compensation policy and emits clock sync replies on heartbeats.
- Stale players are moved into a short resume window before final removal; reconnecting with the same session/client id can reuse the previous server player index and pawn.
- AOI/relevance and per-connection budget accounting select per-client UDP endpoints for server transform replication.

### Phase 2 Transport Note

`BaseNetworkingManager` still owns packet headers, sequence comparison, ACK bitfields, resend tracking, RTT, and token-bucket send limiting. Phase 2 keeps those primitives but scopes them per UDP peer instead of one global manager-wide sequence context.

Realtime client/server traffic now uses per-endpoint unicast after join:

- Clients bind `UdpClientRecievePort` and use that socket for both outbound client-to-server packets and inbound server replies.
- The server uses each client's observed endpoint as a peer, with its own send queue, local sequence counter, received sequence window, ACK/resend map, RTT buffer, and token bucket.
- Server AOI/relevance and per-connection bandwidth budgets can now elide transform updates for one client without corrupting packet ordering for another.
- Multicast remains available for P2P/LAN-style flows and fallback transport paths, but the server-authoritative realtime path no longer depends on multicast fanout.

### World Asset Identity

`WorldAssetIdentity` is the engine-side compatibility contract:

- `WorldId`
- `RevisionId`
- `ContentHash`
- `AssetSchemaVersion`
- `RequiredBuildVersion`
- `Metadata`

Exact session compatibility uses `WorldId`, `RevisionId`, normalized `ContentHash`, and `AssetSchemaVersion`. Metadata is descriptive only.

Environment overrides for local development:

- `XRE_WORLD_ID`
- `XRE_WORLD_REVISION`
- `XRE_WORLD_CONTENT_HASH`
- `XRE_WORLD_ASSET_SCHEMA_VERSION`
- `XRE_WORLD_REQUIRED_BUILD_VERSION`

## Remaining XRENGINE Work

### Phase 0 - External Realtime Handoff Contract

- [x] Define realtime-only DTOs for the data XRENGINE can consume:
  - `RealtimeEndpointDescriptor`
  - `WorldAssetIdentity`
  - optional `SessionId`
  - optional `SessionToken`
- [x] Add one concrete client-side import path for an externally issued realtime join payload:
  - command-line arguments
  - environment variables
  - local JSON file
  - or deep-link payload
- [x] Map that payload into existing client settings:
  - `RealtimeEndpointDescriptor.Host` -> `GameStartupSettings.ServerIP`
  - `RealtimeEndpointDescriptor.Port` -> the UDP server endpoint port used by the client sender
  - `RealtimeEndpointDescriptor.Transport` -> the selected direct realtime transport
  - `RealtimeEndpointDescriptor.ProtocolVersion` -> build/protocol compatibility check
  - `SessionId` -> `GameStartupSettings.MultiplayerSessionId`
  - `SessionToken` -> `GameStartupSettings.MultiplayerSessionToken`
  - `WorldAssetIdentity` -> expected local world identity for pre-connect validation
- [x] Add one concrete server-side host-agent launch contract:
  - `XRE_SESSION_ID`
  - `XRE_SESSION_TOKEN`
  - `XRE_WORLD_ID`
  - `XRE_WORLD_REVISION`
  - `XRE_WORLD_CONTENT_HASH`
  - `XRE_WORLD_ASSET_SCHEMA_VERSION`
  - `XRE_WORLD_REQUIRED_BUILD_VERSION`
  - UDP bind/advertised port settings
- [x] Add pre-connect client validation that compares the externally supplied expected `WorldAssetIdentity` against the locally loaded world before the UDP join loop starts.
- [x] Add a startup/logging summary that prints the active endpoint, session id, protocol version, and local world identity without printing secrets.

Acceptance criteria:

- A non-engine control-plane process can launch/configure XRENGINE as a server worker with a known session and world identity.
- A non-engine control-plane process can launch/configure an XRENGINE client with a concrete realtime endpoint/session payload.
- XRENGINE refuses to start realtime play when the supplied expected world identity differs from the locally loaded world.
- The handoff contains no directory, world download, manifest, host-capacity, or lifecycle-management data.

### Phase 1 - Realtime Handshake Hardening

- [x] Replace instance ids with session ids in realtime messages.
- [x] Reject joins when the client world asset does not exactly match the server world asset.
- [x] Keep direct IP/port launch settings in `GameStartupSettings`.
- [x] Keep opaque session id/token fields as externally supplied data.
- [x] Add focused realtime admission tests for:
  - missing client world identity
  - mismatched content hash
  - mismatched revision id
  - mismatched asset schema version
  - requested session not hosted by server
  - invalid opaque token
- [x] Add a live two-process smoke test that starts one server and two clients against the same local world identity.
- [x] Add a negative live smoke test where one client intentionally supplies a different world hash.

Acceptance criteria:

- XRENGINE can connect players only to a supplied realtime endpoint/session.
- No engine code browses, creates, allocates, downloads, or caches remote world artifacts.
- Matching world identity is required before live synchronized play starts.

### Phase 2 - Realtime Replication Model

- [x] Introduce canonical network entity identities beyond player transform ids.
- [x] Add server-authoritative object ownership with explicit leases:
  - `OwnerClientId`
  - `AuthorityMode`
  - `AuthorityLeaseExpiry`
  - revocation reason
- [x] Add snapshot plus delta sequencing with tick ids.
- [x] Add client prediction and server reconciliation for owned actors.
- [x] Add clock sync, input buffering, and bounded lag compensation.
- [x] Add AOI/relevance filtering and per-connection bandwidth budgeting with per-endpoint routing.
- [x] Extend the existing ACK/resend and token-bucket primitives instead of replacing them.
- [x] Make `HumanoidPoseFrame` a first-class replication channel.
- [x] Add graceful reconnect and session-resume windows after direct endpoint admission succeeds.
- [x] Add per-peer UDP sequence/ACK contexts and per-endpoint send queues so per-connection budgets can elide bytes for one client without affecting packet ordering for another client.

Acceptance criteria:

- The server is final authority for simulation outcomes.
- Bandwidth and replication cost scale with relevance, not total entity count.

### Phase 3 - AOT-Safe World Bootstrap

- [ ] Replace reflection-only `GameModeType` activation in the client assignment path.
- [ ] Add an explicit game-mode/world-bootstrap registry or manifest-backed identifier.
- [ ] Keep `WorldSyncDescriptor.WorldName`, `SceneNames`, and `GameModeType` as hints until the replacement lands.
- [ ] Add AOT metadata coverage for every new realtime DTO.

Acceptance criteria:

- Published/AOT builds can enter realtime sessions without reflection-only bootstrap assumptions.

### Phase 4 - P2P Disposition

- [ ] Decide whether `ENetworkingType.P2PClient` remains a v1 feature, freezes as dev-only, or is removed.
- [ ] If kept, define NAT traversal and relay requirements outside this direct-IP-only engine slice.
- [ ] If removed, delete the P2P manager and launch/debug tasks that depend on it.

Acceptance criteria:

- The v1 transport surface is explicit and documented.

## External Control-Plane Integration Contract

The adjacent control-plane app should give XRENGINE only concrete realtime connection data:

- `endpoint.transport`: currently `NativeUdp`
- `endpoint.host`: host/IP or hostname
- `endpoint.port`: UDP endpoint port
- `endpoint.protocolVersion`: protocol/build requirement
- `sessionId`: realtime room/session id
- `sessionToken`: optional opaque token
- `worldAsset.worldId`
- `worldAsset.revisionId`
- `worldAsset.contentHash`
- `worldAsset.assetSchemaVersion`
- `worldAsset.requiredBuildVersion`
- `worldAsset.metadata`: descriptive only

XRENGINE should not receive or process:

- public directory queries
- create-room requests
- lifecycle transitions
- host capacity reports
- world package URLs
- world manifests
- world chunks
- download/cache instructions

Expected handoff shape:

```json
{
  "sessionId": "00000000-0000-0000-0000-000000000000",
  "sessionToken": "opaque-token-issued-elsewhere",
  "endpoint": {
    "transport": "NativeUdp",
    "host": "127.0.0.1",
    "port": 5000,
    "protocolVersion": "dev",
    "metadata": {}
  },
  "worldAsset": {
    "worldId": "world-guid-or-stable-id",
    "revisionId": "immutable-revision",
    "contentHash": "sha256-or-manifest-hash",
    "assetSchemaVersion": 1,
    "requiredBuildVersion": "dev",
    "metadata": {}
  }
}
```

The external app may use this payload today through `XRE_REALTIME_JOIN_PAYLOAD_FILE` (local JSON file) or `XRE_REALTIME_JOIN_PAYLOAD` (inline JSON). XRENGINE normalizes the payload into the same runtime settings before connecting. CLI flags and deep links remain optional future front doors for the same payload shape.

## Explicit Gaps To Avoid

- [ ] Do not reintroduce public lobby semantics into XREngine.Server.
- [ ] Do not add world download/cache services back into XRENGINE.
- [ ] Do not treat `WorldName`, `SceneNames`, or `GameModeType` as the compatibility gate.
- [ ] Do not route large world/package distribution through remote jobs.
- [ ] Do not add new MemoryPackable DTOs without AOT metadata registration and tests.
- [ ] Do not duplicate reliability, RTT, or rate-limit primitives already in `BaseNetworkingManager`.
- [ ] Do not mention product branding for the adjacent control-plane app in this tracker.

## Validation Notes

- `dotnet build .\XREngine.Runtime.Core\XREngine.Runtime.Core.csproj --no-restore /clp:ErrorsOnly` passes with existing Magick.NET advisory warnings.
- `dotnet build .\XRENGINE\XREngine.csproj --no-restore /clp:ErrorsOnly` passes with existing Magick.NET advisory warnings and pre-existing engine warnings.
- `dotnet build .\XREngine.Server\XREngine.Server.csproj --no-restore` passes with existing Magick.NET advisory warnings and pre-existing engine warnings.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~XREngine.UnitTests.Core.NetworkingContractsTests|FullyQualifiedName~XREngine.UnitTests.Core.AotJsonContractsTests" --no-restore` is currently blocked by unrelated compile errors in existing audio/timing/transform tests before the networking tests run.

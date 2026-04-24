# XRENGINE Networking Overview

This document describes the engine networking code that lives in XRENGINE today. The engine owns realtime connections only. Directory, matchmaking, room creation/join orchestration, host capacity, admission token issuance, and world artifact delivery live in the adjacent control-plane app.

For the stable feature guide, see [XRENGINE Networking](../features/networking.md). Planned peer-to-peer host switching is tracked in [Peer-To-Peer Host Switching Implementation](../work/design/peer-to-peer-host-switching.md).

## Roles And Startup

- The networking role is chosen from `GameStartupSettings.NetworkingType`: `Local`, `Server`, or `Client`.
- `Engine.InitializeNetworking` instantiates `ServerNetworkingManager` or `ClientNetworkingManager`; `Local` disables networking.
- When networking starts, `Engine.Jobs.RemoteTransport` is set to `RemoteJobNetworkingTransport` so remote jobs can share the realtime socket path.
- Direct endpoint settings remain in `GameStartupSettings`:
  - `ServerIP`
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

## External Handoff

A control-plane or launcher can configure a realtime client without adding lobby or download data by setting either:

- `XRE_REALTIME_JOIN_PAYLOAD_FILE`: path to a local JSON payload.
- `XRE_REALTIME_JOIN_PAYLOAD`: inline JSON payload.

The payload shape is:

```json
{
  "sessionId": "11111111-1111-1111-1111-111111111111",
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

At startup XRENGINE maps this to client settings, validates the expected `WorldAssetIdentity` against the loaded local world before the UDP join loop starts, and logs endpoint/session/protocol/world identity without printing the session token.

## Boundary

XRENGINE does not browse, create, allocate, manage, download, or cache multiplayer instances/world artifacts.

The external control-plane app should hand XRENGINE concrete realtime connection data only:

- host/IP or hostname
- UDP port(s)
- transport kind
- protocol/build requirement
- session id
- optional opaque session token
- exact local world asset identity

XRENGINE then performs the realtime handshake and rejects clients whose local world asset identity does not exactly match the server's local world asset identity.

## Transport

- Native realtime traffic uses the existing UDP data plane (`RealtimeTransportKind.NativeUdp`).
- The server binds one UDP socket for inbound client packets and outbound server replies.
- Clients bind `UdpClientRecievePort` and use that socket for both outbound client-to-server packets and inbound server-to-client replies.
- `BaseNetworkingManager` handles per-peer outbound queues, sequence counters, ACK/resend for reliable packets, token-bucket send limiting, RTT, bytes/sec, and packets/sec metrics.
- The Phase 2 replication layer uses per-client endpoint routing so AOI/relevance and per-connection budgets can elide state for one client without disturbing another client's packet ordering.
- Multicast remains available for LAN discovery and fallback transport paths, but server-authoritative realtime replication no longer depends on multicast fanout.
- Peer-to-peer product transport, WebRTC, QUIC, browser transport negotiation, relay, and NAT traversal are not part of this engine v1 slice.

## Message Envelope

All realtime state changes are encoded as `StateChangeInfo`:

- `Type`: `EStateChangeType`
- `Data`: serialized payload via `StateChangePayloadSerializer`

Common realtime message types:

- `PlayerJoin`
- `PlayerAssignment`
- `PlayerLeave`
- `PlayerInputSnapshot`
- `PlayerTransformUpdate`
- `Heartbeat`
- `ServerError`
- `RemoteJobRequest`
- `RemoteJobResponse`
- `HumanoidPoseFrame`
- `AuthorityLeaseUpdate`
- `ClockSync`
- `ReplicationSnapshot`
- `ReplicationDelta`

New MemoryPackable networking DTOs must be added to `NetworkingAotContractRegistry`.

## Realtime Contracts

Engine-owned realtime contracts are intentionally small:

- `WorldAssetIdentity`: exact local world compatibility key.
- `RealtimeEndpointDescriptor`: concrete realtime endpoint metadata.
- `AdmissionFailureReason`: engine-side realtime admission failure reason.
- `PlayerJoinRequest`: client id, build version, local world identity, optional session id/token.
- `PlayerAssignment`: authoritative server index, pawn/transform ids, world descriptor, session id.
- `NetworkEntityId`: canonical server-assigned realtime entity identity, separate from player index and transform id.
- `NetworkAuthorityLease`: owner client id, authority mode, lease expiry, and revocation reason for server-validated ownership.
- `NetworkSnapshotEnvelope` / `NetworkDeltaEnvelope`: ticked replication envelopes for channel-specific snapshot/delta payloads.
- `ClockSyncMessage`: NTP-style heartbeat reply carrying server receive/send time and server tick.
- `WorldSyncDescriptor`: explicit `WorldBootstrapId`, advisory world name/scenes/game-mode hints, and exact `WorldAssetIdentity`.
- `PlayerInputSnapshot`, `PlayerTransformUpdate`, `PlayerLeaveNotice`, `PlayerHeartbeat`.

Control-plane DTOs such as room summaries, create/join requests, host capacity snapshots, manifests, chunks, and world artifact descriptors should not be added back to XRENGINE.

## World Asset Identity

`WorldAssetIdentity` contains:

- `WorldId`
- `RevisionId`
- `ContentHash`
- `AssetSchemaVersion`
- `RequiredBuildVersion`
- `Metadata`

Exact matching uses `WorldId`, `RevisionId`, normalized `ContentHash`, and `AssetSchemaVersion`. `Metadata` is descriptive.

`WorldAssetIdentityProvider` derives the local identity from the loaded world. Local development or a host agent can override the derived identity:

- `XRE_WORLD_ID`
- `XRE_WORLD_REVISION`
- `XRE_WORLD_CONTENT_HASH`
- `XRE_WORLD_ASSET_SCHEMA_VERSION`
- `XRE_WORLD_REQUIRED_BUILD_VERSION`

## Client Flow

- Opens the configured UDP receiver/sender.
- Derives local `WorldAssetIdentity`.
- Sends `PlayerJoinRequest` with build version, local world identity, optional `SessionId`, and optional `SessionToken` until `PlayerAssignment` arrives.
- Stores the assigned `SessionId`.
- Stores the assigned `NetworkEntityId` and `NetworkAuthorityLease`.
- Applies the server-advertised `WorldBootstrapId` through `GameModeBootstrapRegistry`; `GameModeType` is retained only as a diagnostic hint.
- Sends input, predicted transform, heartbeat, pose, and leave messages scoped to that session.
- Tags input and predicted transform updates with client tick/input sequence so the server can echo reconciliation progress.
- Applies server-correction transform updates for locally owned actors and tracks the last processed input sequence.
- Estimates server clock offset from `ClockSyncMessage`.
- Ignores assignment, transform, and leave messages for other active sessions.
- Logs when the server-advertised world asset does not match the local world asset.

## Server Flow

- Starts the realtime UDP receiver/sender only; it does not host an HTTP API.
- `XRE_SESSION_ID` can pin the process session id. If omitted, the process generates one.
- `XRE_SESSION_TOKEN` can require an opaque token on join. XRENGINE only verifies equality; token issuance is external.
- `XRE_UDP_BIND_PORT` / `XRE_UDP_SERVER_BIND_PORT` set the local UDP bind port.
- `XRE_UDP_ADVERTISED_PORT` / `XRE_UDP_SERVER_SEND_PORT` set the endpoint port advertised to clients.
- `XRE_UDP_MULTICAST_GROUP` and `XRE_UDP_MULTICAST_PORT` override multicast settings.
- `Engine.ServerSessionResolver` can supply the local realtime session context.
- `Engine.ServerJoinAdmissionResolver` can reject a direct realtime join before player assignment.
- On `PlayerJoin`, the server rejects:
  - build/protocol mismatch
  - requested session mismatch
  - invalid optional session token
  - missing client world identity
  - incompatible required world build
  - exact world asset mismatch
- After admission, the server assigns a player index, creates/ensures a pawn, grants a `NetworkAuthorityLease`, broadcasts `PlayerAssignment`, and replays cached transforms to newcomers.
- The server accepts input, transform, and humanoid pose traffic only when the sender owns an active lease for the referenced `NetworkEntityId`.
- The server stamps accepted transform and humanoid pose traffic with server tick ids/time and rebroadcasts it as authoritative state.
- The server buffers a bounded input window for jitter/lag-compensation policy and reports clock sync data on heartbeat.
- The server prunes stale players using `MultiplayerRuntimePolicy.PlayerHeartbeatTimeout + PlayerHeartbeatGracePeriod`, then keeps a short `SessionResumeWindow` for same session/client id reconnects.

## World Bootstrap

Realtime assignment no longer constructs game modes from `WorldSyncDescriptor.GameModeType`. The server sends a stable `WorldBootstrapId`, and the client resolves that id through `GameModeBootstrapRegistry`. Built-in ids are `xre.custom`, `xre.flying-camera`, `xre.locomotion`, and `xre.vr`; game projects can register additional ids during startup. `WorldName`, `SceneNames`, and `GameModeType` remain advisory metadata and are not compatibility gates.

## Replication Model

Phase 2 realtime replication adds:

- Canonical entity identity via `NetworkEntityId`.
- Server-issued authority leases with owner client id, authority mode, expiry, and revocation reason.
- Ticked snapshot/delta envelopes for future channel payloads.
- Client prediction metadata on input/transform messages and server reconciliation metadata on authoritative transform updates.
- Clock sync replies, bounded input buffers, and lag-compensation timing policy.
- AOI/relevance hints and per-connection bandwidth budget accounting with per-endpoint UDP routing.
- `HumanoidPoseFrame` metadata so pose traffic is session/entity scoped and server stamped before rebroadcast.

## Remote Jobs

Remote jobs still ride over the realtime transport for small RPC-style operations.

- Request/response payloads are `RemoteJobRequest` and `RemoteJobResponse`.
- `asset/load` remains a dev/debug fallback for small asset pulls.
- Large world package distribution must stay outside XRENGINE in the control-plane/world delivery app.

## Editor Panel

The ImGui networking panel exposes direct realtime controls:

- mode
- server IP
- multicast group/port
- server/client UDP ports
- optional session id/token
- start/apply
- disconnect
- RTT/data/packet diagnostics
- connected server clients and kick action
- local player assignment status

It does not browse, create, or join public rooms.

## Operational Notes

- Realtime traffic is UDP.
- The engine assumes both sides already have the same world asset locally.
- World downloads, manifests, signed URLs, chunk caches, and eviction policy are external to XRENGINE.
- `Local` mode bypasses networking.
- `P2PClient` and `PeerToPeerNetworkingManager` were removed from the current engine transport surface. The replacement design is peer mode with dynamic connection-host switching, tracked in [Peer-To-Peer Host Switching Implementation](../work/design/peer-to-peer-host-switching.md).
- `Tools\Start-NetworkTest.bat` launches the live realtime smoke paths:
  - default / `two-clients`: dedicated server plus two clients with the same handoff payload.
  - `mismatch`: dedicated server plus one good client and one client with an intentionally mismatched expected world hash.
  - `pose`: legacy pose sender/receiver flow.

## Extension Checklist

- Add a new `EStateChangeType` only for realtime data-plane behavior.
- Add a MemoryPackable DTO only when the payload belongs in realtime transport.
- Register new DTOs in `NetworkingAotContractRegistry`.
- Register custom realtime game modes in `GameModeBootstrapRegistry` with a stable bootstrap id.
- Cover serialization and AOT metadata in tests.
- Do not add directory, allocation, world artifact, or public lobby DTOs back to XRENGINE.

# XRENGINE Networking

Last Updated: 2026-04-24

XRENGINE's networking layer owns the low-latency realtime data plane. It does not own public room browsing, matchmaking, host placement, admission-token issuance, world package downloads, chunk caches, or lifecycle orchestration. Those systems should hand the engine concrete endpoint, session, token, and world-identity data after any external workflow has completed.

Current runtime support is direct client/server UDP. Peer-to-peer networking with dynamic connection-host switching is a planned feature tracked in [Peer-To-Peer Host Switching Implementation](../work/design/peer-to-peer-host-switching.md).

## Runtime Roles

`GameStartupSettings.NetworkingType` selects the runtime role:

- `Local`: no network manager is started.
- `Server`: starts `ServerNetworkingManager` as the authoritative simulation endpoint.
- `Client`: starts `ClientNetworkingManager` and joins a concrete server endpoint.

`Engine.InitializeNetworking` applies an external realtime handoff payload before opening sockets. When a network manager starts, `Engine.Jobs.RemoteTransport` is set to `RemoteJobNetworkingTransport` so small remote-job requests can share the same state-change path.

## External Handoff

A launcher or external control-plane process can configure a client through:

- `XRE_REALTIME_JOIN_PAYLOAD_FILE`: path to a local JSON payload.
- `XRE_REALTIME_JOIN_PAYLOAD`: inline JSON payload.

Expected payload shape:

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

The handoff maps to:

- `RealtimeEndpointDescriptor.Host` -> `GameStartupSettings.ServerIP`
- `RealtimeEndpointDescriptor.Port` -> `GameStartupSettings.UdpServerSendPort`
- `RealtimeEndpointDescriptor.Transport` -> `GameStartupSettings.MultiplayerTransport`
- `RealtimeEndpointDescriptor.ProtocolVersion` -> expected protocol/build validation
- `sessionId` -> `GameStartupSettings.MultiplayerSessionId`
- `sessionToken` -> `GameStartupSettings.MultiplayerSessionToken`
- `worldAsset` -> `GameStartupSettings.ExpectedMultiplayerWorldAsset`

Session tokens are opaque to XRENGINE. The engine can compare a supplied token with the server's configured value, but it does not mint, refresh, or authorize tokens.

## World Compatibility

Realtime admission requires exact local world identity. `WorldAssetIdentityProvider` derives the identity from the loaded world, with local-development overrides:

- `XRE_WORLD_ID`
- `XRE_WORLD_REVISION`
- `XRE_WORLD_CONTENT_HASH`
- `XRE_WORLD_ASSET_SCHEMA_VERSION`
- `XRE_WORLD_REQUIRED_BUILD_VERSION`

Exact compatibility compares `WorldId`, `RevisionId`, normalized `ContentHash`, and `AssetSchemaVersion`. `Metadata` is descriptive only.

`WorldSyncDescriptor.WorldName`, `SceneNames`, and `GameModeType` are advisory. Runtime bootstrap uses `WorldSyncDescriptor.WorldBootstrapId`, resolved through `GameModeBootstrapRegistry`, so published/AOT builds do not need reflection-only game-mode activation.

Built-in game-mode bootstrap ids:

- `xre.custom`
- `xre.flying-camera`
- `xre.locomotion`
- `xre.vr`

Game projects can register additional ids during startup through `GameModeBootstrapRegistry.Register(...)`.

## Transport

Native realtime traffic uses the existing UDP data plane:

- Servers bind one UDP socket for inbound client packets and outbound replies.
- Clients bind `UdpClientRecievePort` and use that socket for both outbound client-to-server packets and inbound server-to-client replies.
- After admission, the server uses each client's observed endpoint for targeted unicast traffic.

`BaseNetworkingManager` owns the core UDP reliability primitives:

- packet headers
- local sequence counters
- remote received-sequence windows
- ACK bitfields
- resend tracking
- RTT smoothing
- token-bucket send limiting
- bytes/sec and packets/sec diagnostics

These primitives are scoped per UDP peer. Per-client AOI and bandwidth decisions can drop or elide updates for one endpoint without corrupting another endpoint's packet ordering.

Multicast remains useful for LAN discovery and fallback transport paths. Server-authoritative realtime replication does not depend on multicast fanout.

## Realtime Message Envelope

All realtime state changes ride through `StateChangeInfo`:

- `Type`: `EStateChangeType`
- `Data`: serialized payload from `StateChangePayloadSerializer`

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

Every new MemoryPackable realtime DTO must be registered in `NetworkingAotContractRegistry` and covered by serialization/AOT tests.

## Client Flow

1. Resolve external handoff or direct settings.
2. Derive and validate local `WorldAssetIdentity`.
3. Bind the UDP client receive socket.
4. Send `PlayerJoinRequest` with client id, build version, world identity, optional session id, and optional token until assignment arrives.
5. Apply `PlayerAssignment`, including:
   - server player index
   - `SessionId`
   - canonical `NetworkEntityId`
   - server-issued `NetworkAuthorityLease`
   - `WorldSyncDescriptor`
6. Resolve `WorldBootstrapId` through `GameModeBootstrapRegistry`.
7. Send input, predicted transform, heartbeat, pose, and leave traffic scoped to the assigned session.
8. Apply server-correction transforms for locally owned actors and track the last processed input sequence/server tick.
9. Estimate server clock offset from `ClockSyncMessage`.

## Server Flow

1. Bind the UDP server socket.
2. Resolve the hosted session and local `WorldAssetIdentity`.
3. On `PlayerJoin`, reject:
   - build/protocol mismatch
   - requested session mismatch
   - invalid optional session token
   - missing client world identity
   - incompatible required world build
   - exact world asset mismatch
4. Create or resume a player connection.
5. Ensure a pawn, assign canonical `NetworkEntityId`, and grant `NetworkAuthorityLease`.
6. Broadcast `PlayerAssignment` after admission succeeds.
7. Accept input, transform, and humanoid pose traffic only while the sender owns an active lease for the referenced entity.
8. Stamp accepted transform and pose traffic with server tick ids/time and rebroadcast authoritative state.
9. Prune stale players after heartbeat timeout and keep a short session-resume window for reconnects.

## Replication Model

The realtime contracts separate stable network identity from scene-local ids:

- `NetworkEntityId`: canonical server-assigned entity key.
- `NetworkAuthorityLease`: owner client id, authority mode, lease expiry, and revocation reason.
- `PlayerInputSnapshot`: client input, client tick, input sequence, and session.
- `PlayerTransformUpdate`: transform state plus prediction/reconciliation metadata.
- `NetworkSnapshotEnvelope` and `NetworkDeltaEnvelope`: ticked channel envelopes for future snapshot/delta payloads.
- `ClockSyncMessage`: NTP-style heartbeat response carrying server receive/send times and server tick.
- `HumanoidPoseFrame`: session/entity-scoped pose packet with baseline/delta metadata.

Server authority remains final. Clients can predict their owned avatar/entity, but the server stamps accepted state and can correct or revoke authority.

## Editor And Local Validation

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

Common smoke paths:

```powershell
.\Tools\Start-NetworkTest.bat
.\Tools\Start-NetworkTest.bat mismatch
.\Tools\Start-NetworkTest.bat pose
```

VS Code tasks cover direct server/client and networking-pose validation:

- `Start-Server-NoDebug`
- `Start-Client-NoDebug`
- `Start-Client2-NoDebug`
- `Start-2Clients-NoDebug`
- `Start-DedicatedServer-NoDebug`
- `Start-PoseServer-NoDebug`
- `Start-PoseSourceClient-NoDebug`
- `Start-PoseReceiverClient-NoDebug`
- `Start-LocalPoseSync-NoDebug`

## Extension Checklist

- Keep public lobby, matchmaking, host allocation, world download/cache, manifests, chunks, and token issuance outside XRENGINE.
- Add a new `EStateChangeType` only for realtime data-plane behavior.
- Add MemoryPackable DTOs only for payloads that belong in realtime transport.
- Register new DTOs in `NetworkingAotContractRegistry`.
- Register custom realtime game modes in `GameModeBootstrapRegistry`.
- Preserve `BaseNetworkingManager`'s per-peer sequence/ACK/resend/token-bucket primitives when adding transports or routing modes.
- Update this feature doc when changing user-facing networking settings, environment variables, launch tasks, or runtime behavior.

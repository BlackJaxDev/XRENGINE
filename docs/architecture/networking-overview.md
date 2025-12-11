# XRENGINE Networking Overview

This document describes how networking is wired in the engine today: roles, transports, message shapes, replication flows, remote jobs (including asset download), and operational guidance.

## Roles and startup
- The networking role is chosen from `GameStartupSettings.NetworkingType`: `Local`, `Server`, `Client`, `P2PClient`.
- `Engine.InitializeNetworking` instantiates one of `ServerNetworkingManager`, `ClientNetworkingManager`, or `PeerToPeerNetworkingManager`; for `Local`, networking is disabled.
- When a manager is created, `Engine.Jobs.RemoteTransport` is set to `RemoteJobNetworkingTransport` so remote jobs can piggyback on the same sockets.

## Transports
- **UDP multicast + unicast**: all state-change traffic is UDP. The server binds a receiver and multicasts to clients; clients open a multicast receiver and a unicast sender to the server.
- **Peer ID**: every manager has a `LocalPeerId` (server uses "server").
- **Queues**: `BaseNetworkingManager` enqueues outbound payloads and drains them during the frame tick (`ConsumeQueues` → `SendUDP`).

## Message envelope
- All messages are encoded as `StateChangeInfo`:
  - `Type`: enum `EStateChangeType` (see below).
  - `Data`: serialized payload (JSON/MemoryPack via `StateChangePayloadSerializer`).
- Common `EStateChangeType` entries:
  - `PlayerJoin`, `PlayerAssignment`, `PlayerLeave`
  - `PlayerInputSnapshot`, `PlayerTransformUpdate`
  - `WorldChange`, `GameModeChange`
  - `RemoteJobRequest`, `RemoteJobResponse`
  - `Heartbeat`, `ServerError`

## Client flow (ClientNetworkingManager)
- Opens multicast receiver, unicast sender to the server, registers a per-frame tick.
- Sends `PlayerJoin` until `PlayerAssignment` arrives; assignment contains authoritative server index and current world descriptor.
- Periodic sends:
  - Input snapshots at 60 Hz.
  - Transform snapshots at 20 Hz.
  - Heartbeat every 3s while assigned.
- Receives assignments, remote transforms, leave notices, server errors, and remote job responses.
- Tracks remote players and applies server-driven world/game mode state.

## Server flow (ServerNetworkingManager)
- Binds UDP receiver; starts multicast sender.
- On `PlayerJoin`: registers/updates connection, assigns server index, broadcasts `PlayerAssignment` (includes world descriptor), and replays cached transforms to newcomers.
- On `PlayerInputSnapshot` / `PlayerTransformUpdate`: updates connection state and rebroadcasts transforms to clients.
- On `Heartbeat`: updates liveness timestamps.
- On `PlayerLeave`: removes connection, broadcasts leave, emits a server error (499) to that client id.
- Periodically prunes stale players (timeout + grace).

## Remote jobs (binary RPC over the same transport)
- Request/response envelope: `RemoteJobRequest` and `RemoteJobResponse` routed via `EStateChangeType.RemoteJobRequest/Response`.
- Transfer modes: `RequestFromRemote` (ask remote to produce payload) or `PushDataToRemote` (caller provides payload).
- Dispatch: `Engine.HandleRemoteJobRequestInternalAsync` switches on `Operation`; currently `asset/load` is supported.
- Transport binding: `RemoteJobNetworkingTransport` sends jobs through the networking manager and surfaces responses back to callers (`Engine.Jobs.ScheduleRemote`).

## Asset loading over the network
- Client-side fallback is in `AssetManager`:
  - `LoadEngineAssetRemote(Async)`, `LoadGameAssetRemote(Async)` take optional metadata and first try local load, then remote fetch if connected.
  - `LoadByIdRemote(Async)` loads by GUID; resolves local path via metadata or pulls from server.
  - Remote requests include required fields (`path` or `id`, `type`) plus any caller-supplied metadata (e.g., LOD preferences, spawn-relative hints).
- Server-side handling in `Engine.HandleRemoteAssetLoadAsync`:
  - Accepts path or GUID; resolves the file (loaded asset cache or metadata path).
  - Reads bytes and returns them in `RemoteJobResponse`; echoes a canonical `path` in response metadata when known.
- Client persistence: downloaded bytes are written locally (using server path if provided), then loaded normally.

## Liveness and reliability
- Heartbeats: clients send every 3s after assignment; server uses timestamps to detect stale connections (15s timeout + 5s grace) and prunes.
- Leave handling: clients proactively send `PlayerLeave`; server broadcasts leave and emits a non-fatal error to that client id.
- Version check: server compares client build version in `PlayerJoin` and can emit a 426 Upgrade Required warning.

## Error channel
- `ServerError` carries code, message, and optional player index; used for version mismatches, kicked/leaves, and other server notices.

## World/game mode sync
- Server embeds a `WorldSyncDescriptor` in `PlayerAssignment`.
- Client applies it on receipt: creates world if missing, sets world name, instantiates game mode via reflection, and logs trimming warnings when reflection metadata might be stripped.

## Extending networking
- Add a new `EStateChangeType` for the feature and a payload DTO; serialize via `StateChangePayloadSerializer`.
- Implement send/receive handling in client/server managers.
- For remote RPC-style work, prefer `RemoteJobOperations` and reuse `RemoteJobRequest/Response` instead of adding bespoke message types.

## Operational notes
- All traffic is UDP; large payloads (e.g., assets) ride inside remote jobs—consider size and chunking if payloads grow.
- Multicast group/ports come from `GameStartupSettings` (`UdpMulticastGroupIP`, `UdpMulticastPort`, `UdpClientRecievePort`, `UdpServerSendPort`).
- `Local` mode bypasses networking entirely; `P2PClient` uses similar plumbing but without a dedicated server (see `PeerToPeerNetworkingManager`).

## Quick checklist for consumers
- Choose role in `GameStartupSettings.NetworkingType`.
- Ensure remote job transport is connected before relying on remote asset fetches.
- When requesting remote assets, supply metadata to guide the server (e.g., `{"lod":"low","origin":"spawnA"}`).
- Handle `ServerError` events to surface actionable feedback to users.
- Keep assets and build versions in sync between server and clients to minimize warnings and retries.

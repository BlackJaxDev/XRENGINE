# Peer-To-Peer Host Switching Implementation

Last Updated: 2026-04-24

## Goal

Add peer-to-peer networking support where one peer acts as the active connection host for admission, authority arbitration, clock reference, and replication fanout, and the group can elect and switch to a new host when the current host leaves or degrades.

The design must scale from **trusted LAN/dev sessions** to **public, untrusted internet lobbies** under a single coherent architecture. Public-lobby support requires Byzantine fault tolerance (BFT) for host election and migration commit, signed peer identities, encrypted transport, and clean integration points for an external control plane that supplies matchmaking, identity attestation, and relay/NAT traversal.

This design intentionally reuses the finished client/server realtime work. It must not replace the UDP packet header, sequence comparison, ACK bitfield, resend, RTT, or token-bucket primitives in `BaseNetworkingManager`. Cryptographic and BFT layers wrap the existing transport rather than replacing it.

## Product Shape

XRENGINE itself does not ship a matchmaker, room directory, world-download CDN, or NAT relay. Those live in an **external control plane** (any service that implements the documented interfaces in [§Engine ↔ Control-Plane Boundary](#engine--control-plane-boundary)). The engine accepts concrete endpoint/session/world identity data, signed identity material, and (optionally) relay descriptors from that control plane and runs the session.

Required product capabilities:

- LAN/direct-IP peer groups for local development and trusted sessions (no control plane required).
- Public internet lobbies driven by a pluggable external control plane, with BFT-grade election/migration and signed identities.
- A single active connection host at any moment.
- Dynamic connection-host switching when:
  - the host leaves gracefully,
  - host heartbeat times out,
  - host quality drops below a configured threshold,
  - an operator/debug action requests host migration,
  - equivocation or a quorum vote of no-confidence is detected.
- Server-like final authority semantics, except the active connection host is a peer process and its commits carry quorum signatures in public mode.
- Reuse of existing direct session/world identity admission.
- Pluggable, control-plane-agnostic interfaces — any matchmaker/lobby service that implements the boundary contract can host XRENGINE peer sessions.

Explicit non-goals (engine side):

- Implementing matchmaking, room directories, or social graph features inside the engine.
- Implementing a TURN/relay server inside the engine.
- Implementing world package download/CDN inside the engine.
- Browser/WebRTC transport.
- Hostless full-mesh simulation authority.
- Coin/stake-based or blockchain-style consensus.

NAT traversal and relay are supported by treating control-plane-supplied relay endpoints as ordinary transport endpoints. ICE-style multi-candidate connectivity checks are in scope for the engine; the STUN/TURN servers themselves are not.

## Trust Model

Three trust tiers, selectable per session:

| Tier | Use case | Identity | Transport | Election | Migration commit |
|---|---|---|---|---|---|
| `Local` | Editor/dev, single machine | None | Plaintext | Trivial | Unilateral |
| `Trusted` | LAN, private parties | Optional keypair | Plaintext or DTLS | Majority of visible peers | Unilateral |
| `Public` | Internet lobbies | Required keypair, control-plane attested | DTLS or Noise over UDP, mandatory | BFT (`n ≥ 3f + 1`) | Quorum signature certificate |

The tier is set at session start from the control-plane handoff (or `Local`/`Trusted` defaults for LAN). Tier downgrade mid-session is not allowed; tier upgrade requires a new session.

BFT assumptions in `Public`:

- At most `f` peers may be Byzantine where `n ≥ 3f + 1`.
- The control plane is trusted to attest peer identities and issue session tokens, but is not on the realtime path and may be unreachable mid-session.
- The network may partition, drop, reorder, and duplicate messages.
- Peers may equivocate (sign conflicting statements). Equivocation is detectable and produces cryptographic evidence.

## Engine ↔ Control-Plane Boundary

All control-plane integration lives in a separate assembly (proposed: `XREngine.Networking.ControlPlane`) and is consumed by the engine through interfaces only. The engine ships **no concrete control-plane implementation** beyond a `LocalControlPlane` used for LAN/dev. A reference HTTP/gRPC implementation may live in a separate sample repo.

### Boundary principles

- **One-way knowledge:** the engine knows the boundary interfaces; the control plane knows nothing about engine internals beyond the documented DTOs.
- **Off the realtime path:** control-plane calls happen at session bootstrap, on identity events, and on out-of-band reports. They never block the simulation tick.
- **Failure tolerant:** every control-plane call has a timeout and a documented offline fallback. A session must remain playable if the control plane becomes unreachable after start.
- **Transport agnostic:** HTTP, gRPC, WebSocket, IPC, in-process — all valid. The engine only sees `Task<T>`-returning interface methods.
- **No engine types leak outward:** boundary DTOs are POCOs in the boundary assembly; they do not reference engine assemblies.

### Boundary interfaces (engine-side, implemented by adapters)

```csharp
// XREngine.Networking.ControlPlane

public interface ISessionDirectory
{
    // Resolve a session token + local identity into a concrete session descriptor.
    Task<SessionDescriptor> ResolveSessionAsync(SessionJoinTicket ticket, CancellationToken ct);
}

public interface IPeerIdentityAuthority
{
    // Issue or load this process's long-lived peer keypair.
    Task<PeerIdentity> GetLocalIdentityAsync(CancellationToken ct);

    // Verify a remote peer's identity attestation (signature chain) for a given session.
    Task<PeerAttestationResult> VerifyAttestationAsync(
        SessionId session, PeerAttestation attestation, CancellationToken ct);
}

public interface IRosterProvider
{
    // Initial allowed-peer set + host candidacy whitelist for the session.
    Task<SessionRoster> GetInitialRosterAsync(SessionId session, CancellationToken ct);

    // Optional live updates (joins/kicks/bans). Implementations may return Empty.
    IAsyncEnumerable<RosterDelta> SubscribeRosterAsync(SessionId session, CancellationToken ct);
}

public interface IRelayDirectory
{
    // ICE-style candidate gathering. Implementations may return empty for LAN/dev.
    Task<IReadOnlyList<TransportCandidate>> GatherCandidatesAsync(
        SessionId session, PeerId localPeer, CancellationToken ct);
}

public interface IAbuseReportSink
{
    // Out-of-band reports: equivocation evidence, quorum no-confidence, malformed traffic.
    Task ReportAsync(AbuseReport report, CancellationToken ct);
}

public interface IControlPlane
    : ISessionDirectory, IPeerIdentityAuthority, IRosterProvider,
      IRelayDirectory, IAbuseReportSink
{
    ControlPlaneCapabilities Capabilities { get; }
}
```

`ControlPlaneCapabilities` is a flags enum (`SignedIdentities`, `LiveRoster`, `Relay`, `AbuseReports`, …) so the engine can enable/disable features per session without dynamic casts.

### Built-in implementations

- `LocalControlPlane` — in-process, zero-network. Generates ephemeral keypairs, returns the local roster from `GameStartupSettings`, no relay, no live roster, no abuse sink. Used by `Local` and `Trusted` tiers.
- `NullControlPlane` — fails closed; used in tests to assert engine code never silently falls back when a real control plane is required.

### Where control-plane code does NOT live

- No control-plane types in `XREngine` core, `XREngine.Runtime.*`, `XREngine.Server`, `XREngine.VRClient`, or `XREngine.Editor`.
- No HTTP clients, no JSON contracts for matchmakers, no auth tokens in engine assemblies.
- The engine references only `XREngine.Networking.ControlPlane` (interfaces + DTOs + `LocalControlPlane`).

Third parties wanting to host XRENGINE sessions implement `IControlPlane` (or any subset by composing existing implementations) in their own assembly, register it via DI/handoff, and the engine consumes it transparently.

## Cryptography

Algorithm baseline (subject to change pending review, but pin choices before Phase 0):

- **Signing:** Ed25519. Used for peer identity, signed control-plane DTOs, votes, commit certificates, snapshot roots.
- **Key exchange / transport encryption:** Noise_IK_25519_ChaChaPoly_BLAKE2s, or DTLS 1.3 if a vetted .NET implementation is preferred. Decision deferred to Phase 0-B but must precede `Public`-tier work.
- **Hashing:** BLAKE3 for snapshot Merkle trees and rolling state roots. SHA-256 acceptable if BLAKE3 .NET binding is unsuitable.
- **Nonces:** 96-bit random + 32-bit monotonic per-peer counter to bind signatures to a session and prevent replay.

All signed DTOs include `(SessionId, HostEpoch, PeerNonce, Timestamp)` in the signed payload. Verifiers reject signatures whose `PeerNonce` is not strictly greater than the last accepted nonce from that peer in that session.

In `Local`/`Trusted` tiers signatures are optional and the engine MUST accept unsigned variants when `IControlPlane.Capabilities` lacks `SignedIdentities`. In `Public` tier signatures are mandatory and unsigned variants are rejected on receipt before any state mutation.

## Terminology

- **Peer:** Any participant in a peer-to-peer session.
- **Connection host:** The peer currently acting as admission authority, clock reference, and replication fanout.
- **Host epoch:** Monotonic generation number for the active host assignment. Strictly increasing across migrations within a session and never reused.
- **Candidate:** A peer eligible to become the connection host. The candidacy whitelist is supplied by the control plane (`IRosterProvider`); in `Trusted` tier all peers are candidates by default.
- **Lease owner:** The peer currently allowed to submit authoritative proposals for a network entity.
- **Migration:** The process of transferring connection-host duties from one peer to another.
- **Quorum:** In `Public` tier, `2f + 1` signed votes from distinct attested peers in the active roster (where `n ≥ 3f + 1`).
- **Commit certificate:** A bundle of `2f + 1` signed `HostElectionVote` messages over the same `(SessionId, HostEpoch, CandidatePeerId, BaselineRoot)` tuple. The proof that a host migration is final.
- **State root:** A BLAKE3 hash (Merkle root or rolling) over the authoritative simulation state at a given `(HostEpoch, ServerTickId)`. Published in heartbeats and snapshot envelopes.
- **Equivocation:** A peer (typically a host) signing two conflicting statements with the same identity at the same `(HostEpoch, ServerTickId)`. Cryptographically detectable.
- **Witness:** A non-host peer that recomputes authoritative state from inputs and compares state roots against the host. Optional in `Trusted`, recommended in `Public`.

## Proposed Runtime Roles

Add networking role support without folding P2P into the existing `Client` or `Server` role names:

```csharp
public enum ENetworkingType
{
    Server,
    Client,
    Peer,
    Local
}
```

`Peer` starts a new `PeerNetworkingManager`. Internally it should support three stateful subroles:

- `Joining`: has peer endpoints but no accepted host assignment yet.
- `Hosted`: this peer is the active connection host.
- `Participant`: this peer is connected to a host peer.

The active subrole can change without restarting the engine networking subsystem.

## New Contracts

Add MemoryPackable realtime DTOs and register them in `NetworkingAotContractRegistry`. **Every DTO listed below has both an unsigned variant (`Trusted`/`Local`) and a signed envelope variant (`Public`).** The signed envelope is `SignedPeerMessage<T>` containing `{ Payload: T, SignerPeerId, PeerNonce, Signature }` with `Signature` covering `Payload || SessionId || HostEpoch || PeerNonce`. The engine selects which variant to accept based on the active session's trust tier.

Identity & session bootstrap:

- `PeerIdentity`
  - `PeerId` (32-byte stable id derived from public key)
  - `PublicKey` (Ed25519)
  - `Capabilities` (upload budget, supported transports, witness-capable)
- `PeerAttestation`
  - `PeerIdentity`
  - `SessionId`
  - `IssuedAt`, `ExpiresAt`
  - `ControlPlaneSignature` over the above (issued by the control plane; engines verify against a configured trust anchor)
- `SessionDescriptor` (returned by `ISessionDirectory.ResolveSessionAsync`)
  - `SessionId`, `TrustTier`, `ProtocolVersion`, `BuildVersion` constraint
  - `WorldAssetIdentity`, `WorldBootstrapId`
  - initial peer roster + host candidacy mask
  - `f` value (max tolerated Byzantine peers)
  - control-plane trust anchor public key
  - relay descriptors (optional)

Realtime control plane:

- `PeerJoinRequest`
  - `PeerAttestation`
  - `DisplayName`
  - `BuildVersion`, `ProtocolVersion`
  - `ClientWorldAsset`
  - `SessionId`
  - `HostEpoch` (last observed)
  - candidate metrics (RTT samples, upload budget)
- `PeerAssignment`
  - `PeerId`
  - `ServerPlayerIndex`
  - `NetworkEntityId`
  - `AuthorityLease`
  - `WorldSyncDescriptor`
  - `SessionId`
  - `HostPeerId`
  - `HostEpoch`
  - `BaselineStateRoot`
- `PeerRosterSnapshot`
  - `SessionId`
  - `HostPeerId`
  - `HostEpoch`
  - peer entries with endpoint(s), player index, last heard time, candidate priority, attestation expiry
  - `RosterRoot` (BLAKE3 over canonical encoding)
- `HostElectionProposal`
  - `SessionId`
  - `CandidatePeerId`
  - `ObservedHostPeerId`
  - `ObservedHostEpoch`
  - `ProposedHostEpoch` (= `ObservedHostEpoch + 1`)
  - `LastSeenStateRoot`, `LastSeenServerTick`
  - priority tuple
- `HostElectionVote`
  - `SessionId`
  - `VoterPeerId`
  - `CandidatePeerId`
  - `ProposedHostEpoch`
  - `BaselineStateRoot`
  - `BaselineServerTick`
- `HostMigrationPrepare`
  - `SessionId`
  - `OldHostPeerId`
  - `NewHostPeerId`
  - `NewHostEpoch`
  - `BaselineServerTick`, `BaselineStateRoot`
- `HostMigrationCommit`
  - `SessionId`
  - `NewHostPeerId`
  - `NewHostEpoch`
  - `BaselineServerTick`, `BaselineStateRoot`
  - `CommitCertificate` (`2f + 1` signed `HostElectionVote`s; empty in `Trusted` tier)
- `HostMigrationAbort`
  - `SessionId`
  - `HostEpoch`
  - reason code
- `PeerHostHeartbeat`
  - `SessionId`
  - `HostPeerId`
  - `HostEpoch`
  - `ServerTickId`
  - clock timestamps
  - `StateRoot` for `ServerTickId`
  - quality metrics
- `EquivocationEvidence`
  - two signed messages from the same `SignerPeerId` with the same `(HostEpoch, ServerTickId)` and conflicting payloads
  - reported via `IAbuseReportSink`

Prefer adding new `EStateChangeType` values for these control-plane-within-realtime messages instead of overloading existing player assignment payloads. The `SignedPeerMessage<T>` envelope is itself a single `EStateChangeType` whose payload type is the inner `T`.

## Host Election

Election runs in two flavors selected by trust tier.

### `Trusted` / `Local` tier (deterministic, non-BFT)

1. Each peer has a stable `PeerId`.
2. Each peer computes a candidate priority:
   - explicit operator preference
   - control-plane preference hint (if provided)
   - direct endpoint reachability
   - lowest average RTT to known peers
   - highest advertised upload budget
   - stable tie-breaker by `PeerId`
3. When no valid host is known, peers propose the best visible candidate.
4. A candidate becomes host after a majority of currently visible peers vote yes; in two-peer mode either peer can promote itself after host timeout.
5. Host epoch increments on every committed migration.
6. Peers ignore host messages from stale epochs.

This is fast and dependency-free. It is **not** safe against malicious peers and is restricted to `Trusted`/`Local`.

### `Public` tier (BFT, PBFT-style view-change)

Backed by signed votes and a `2f + 1` quorum where `n ≥ 3f + 1`. `f` is set by the control plane in `SessionDescriptor` and bounded by the active roster size.

1. Eligible candidates are the intersection of `IRosterProvider` candidacy mask and currently attested peers.
2. Candidate priority is computed as in the trusted flow but signed and gossiped, never accepted unsigned.
3. A candidate proposes itself by broadcasting a signed `HostElectionProposal` containing `LastSeenStateRoot` and `LastSeenServerTick`.
4. Other peers vote yes by broadcasting a signed `HostElectionVote` only if:
   - the proposal's `ProposedHostEpoch` is exactly `last_committed_epoch + 1`,
   - the proposer is in the candidacy mask,
   - the proposer's `LastSeenServerTick` is `≥` the voter's last committed tick (no rollback),
   - the proposer's `LastSeenStateRoot` is consistent with the voter's history at that tick (or the voter has no record at that tick).
5. The candidate collects `2f + 1` signed votes into a `CommitCertificate` and broadcasts `HostMigrationCommit`.
6. Peers accept `HostMigrationCommit` only after independently verifying every signature in the certificate against attested peer identities.
7. **Rate limiting:** each peer may emit at most one proposal per `ProposalCooldown` (configurable, default 2 s) and at most one vote per `(SessionId, ProposedHostEpoch)`. Excess messages are dropped without state mutation.
8. **Equivocation:** if a peer signs two distinct votes for the same `ProposedHostEpoch`, any peer holding both signed messages constructs `EquivocationEvidence`, broadcasts it, and reports it via `IAbuseReportSink`. Equivocators are removed from the active roster for the rest of the session.
9. **View change on stuck epoch:** if no candidate accumulates `2f + 1` votes within `ElectionTimeout`, peers increment `ProposedHostEpoch` and re-run with exponential backoff.

## Host Migration

### Graceful migration (any tier)

1. Current host broadcasts `HostMigrationPrepare` with the next `BaselineServerTick` and `BaselineStateRoot`.
2. New host starts accepting peer control messages for `NewHostEpoch` but does not fan out simulation yet.
3. Current host sends a final `NetworkSnapshotEnvelope` baseline, roster, authority leases, and latest committed tick. The snapshot's hash must equal `BaselineStateRoot`.
4. In `Trusted`, the new host broadcasts `HostMigrationCommit` unilaterally. In `Public`, the new host first solicits `2f + 1` signed votes confirming the baseline and includes them as the `CommitCertificate`.
5. Participants verify the certificate (in `Public`), then redirect their host endpoint to the new host and resume heartbeat/input.
6. Old host demotes itself to participant or leaves.

### Failure migration

1. Participants miss `PeerHostHeartbeat` for `HostHeartbeatTimeout`.
2. Participants freeze admission and non-local authority changes but keep client prediction for locally owned actors within a short grace window.
3. Peers run host election (per-tier rules above).
4. Winning peer broadcasts `HostMigrationCommit` referencing the **highest signed snapshot any participant in the quorum holds** (not just the proposer's own snapshot). In `Public`, the chosen `BaselineStateRoot` must appear in at least `f + 1` voters' histories to ensure it survived among honest peers.
5. Participants reconcile against the new host snapshot.

Failure migration can lose the last uncommitted host-only state. The host must publish baselines (signed snapshot + state root) frequently enough to keep recoverable loss within an acceptable bound. Default target: committed baseline age `≤ 500 ms`.

### Fork resolution

If two `HostMigrationCommit` messages exist for overlapping ticks at different epochs:

- The higher epoch wins if its certificate verifies.
- If both verify and overlap, the engine treats this as a control-plane bug or a `> f` Byzantine compromise: the session aborts, evidence is reported, and peers return to lobby.

## Authority Model

Do not make every peer authoritative. The active connection host remains the final arbiter:

- The host grants and revokes `NetworkAuthorityLease`.
- Participants submit input and predicted transform updates to the host.
- The host validates leases, stamps ticks/time, and rebroadcasts authoritative state.
- During migration, leases transfer with the committed snapshot.
- Stale-epoch authority updates are ignored.
- In `Public`, every authority grant/revoke and every snapshot envelope is signed by the host. Participants verify before applying.
- In `Public`, the host publishes a `StateRoot` in every heartbeat. Witnesses (and ordinary participants, if cheap) recompute the root from inputs and flag divergence by broadcasting evidence.
- A no-confidence vote of `2f + 1` participants forces an immediate election even without heartbeat timeout.

This preserves the existing client/server mental model while allowing the host process to move and while making host misbehavior detectable and attributable.

### Determinism requirement

For witness verification and equivocation detection to be meaningful, authoritative simulation must be reproducible from `(initial baseline, ordered input stream, tick id)`. This implies:

- Fixed-step simulation with stable ordering of input application.
- Per-tick seeded RNG; no use of ambient `Random.Shared` or wall-clock in authoritative paths.
- Non-deterministic systems (GPU readbacks, audio analysis, third-party physics with tolerance differences) either:
  - excluded from the committed `StateRoot`, or
  - quantized and snapshotted as outputs rather than recomputed.

Determinism is mandatory in `Public` and recommended in `Trusted`. Subsystems that cannot meet it must declare themselves non-authoritative.

## Transport And Packet Ordering

Reuse `BaseNetworkingManager` per-peer UDP contexts:

- one send queue per endpoint
- one local sequence counter per endpoint
- one received sequence window per endpoint
- ACK/resend state per endpoint
- RTT and token bucket per endpoint

Do not introduce a global P2P ordering stream. Host migration messages should be reliable state changes sent through the existing per-peer reliable path. Epoch and tick ids define semantic ordering above the UDP packet layer.

In `Public` tier, an encryption layer (Noise or DTLS — decision in Phase 0-B) wraps payloads inside the existing UDP packet body. The packet header, sequence numbers, and ACK bitfield remain plaintext so that loss recovery and DoS rate limiting do not require decryption. Encrypted payload size is bounded so MTU and fragmentation logic in `BaseNetworkingManager` are unaffected.

When a participant switches host endpoint:

1. Keep the existing peer context for the old endpoint until migration completes or times out.
2. Create or reuse the UDP peer context for the new host endpoint.
3. Do not copy sequence counters between endpoints.
4. Use `HostEpoch` and `ServerTickId` to discard stale state.

### Multi-candidate endpoints (NAT/relay)

A logical peer may have multiple `TransportCandidate` endpoints (direct, reflexive via STUN, relayed via TURN — all supplied by `IRelayDirectory`). The engine performs an ICE-style connectivity check:

1. On peer admission, gather local candidates and learn remote candidates.
2. Probe candidate pairs in priority order with signed ping/pong over the existing UDP transport.
3. Promote the first pair that completes a round trip within `CandidateProbeTimeout`.
4. Maintain at most one active UDP context per logical peer; failover to the next-best pair if the active pair degrades.

The engine does not implement STUN/TURN itself; it consumes already-resolved candidate descriptors.

### DoS and abuse mitigation

- **Per-identity token buckets** in addition to per-endpoint buckets. A peer rotating source ports cannot multiply its bandwidth allowance.
- **Amplification protection on join:** before allocating per-peer state on the host, require the joiner to echo a host-issued cookie (signed `(SessionId, RemoteEndpoint, IssuedAt)`).
- **Bounded roster size** per session, enforced by host and re-checked by participants on every roster update.
- **Signature verification budget:** verifications are batched and rate-limited per source endpoint to prevent CPU exhaustion via floods of invalid signatures. Invalid signatures count against the source's token bucket.
- **Replay window:** signed messages outside `(now - SignatureSkew, now + SignatureSkew)` or with non-monotonic `PeerNonce` are dropped without state mutation.

## Session And World Identity

Peer mode still requires exact world identity:

- `WorldAssetIdentity` must match before a peer is admitted.
- `WorldBootstrapId` remains the AOT-safe bootstrap key.
- `WorldName`, `SceneNames`, and `GameModeType` remain hints.
- `ProtocolVersion` is independent of `BuildVersion` so the wire protocol can evolve without bumping engine builds.
- Session id/token are issued by the control plane in `Public`/`Trusted` flows; `Local` generates an ephemeral id at startup.
- `SessionDescriptor.TrustTier`, `f`, and the control-plane trust anchor are immutable for the session's lifetime.

For internet play, the control plane issues the session id, peer attestations, and initial endpoint list, and may push roster deltas (joins, kicks, bans) over `IRosterProvider.SubscribeRosterAsync`.

## Discovery

Use `NetworkDiscoveryComponent` for LAN discovery only:

- Advertise session id, host peer id, host epoch, UDP endpoint, protocol version, trust tier (`Local`/`Trusted` only — `Public` is never LAN-advertised), and world identity summary.
- Listeners can create `GameStartupSettings` for `ENetworkingType.Peer`.
- Discovery packets are hints only; realtime admission still validates session/world identity over the UDP state-change path.

LAN discovery is not a public directory and never advertises `Public`-tier sessions. Public-tier session discovery is a control-plane concern.

## Observability

Every peer must emit (logs + metrics) at minimum:

- active subrole, host peer id, host epoch, last committed `ServerTickId`, last committed `StateRoot`
- per-endpoint RTT, packet loss, send/recv rate, token-bucket fill
- signature verification: success/failure counts, average latency
- election events: proposals issued/received, votes issued/received, view changes
- migration events: prepares, commits, aborts, failure-recovery counts
- equivocation events with evidence id
- attestation expiry warnings

These feed both the in-editor ImGui inspector and the `IAbuseReportSink` for `Public` sessions.

## Implementation Phases

Work is split into two parallel tracks. **Track A** ships LAN/`Trusted` peer mode without crypto and is independently shippable. **Track B** layers BFT, signatures, encrypted transport, and control-plane integration on top to enable `Public` lobbies. Track-B phases gate on their Track-A counterparts.

### Track A — Trusted Peer Mode

#### Phase A0 — Contracts And Startup

- [ ] Add `ENetworkingType.Peer`.
- [ ] Add `PeerNetworkingManager` skeleton.
- [ ] Add `PeerId`, `TrustTier`, and host metadata to startup settings or a dedicated peer settings type.
- [ ] Add peer handoff payload fields for peer endpoints, preferred host, host epoch, and candidate metrics.
- [ ] Add MemoryPackable peer DTOs (unsigned variants) and AOT registry entries.
- [ ] Define `IControlPlane` and friends in `XREngine.Networking.ControlPlane`; ship `LocalControlPlane` + `NullControlPlane`.
- [ ] Add serialization tests for every peer DTO.

Acceptance: a peer process can start in `Joining`, parse handoff/discovery data via `LocalControlPlane`, and serialize peer control messages without reflection-only assumptions.

#### Phase A1 — Hosted Peer Baseline

- [ ] Implement `Hosted` subrole by reusing server admission, assignment, lease, heartbeat, and transform stamping logic.
- [ ] Implement `Participant` subrole by reusing client join, prediction, heartbeat, correction, and clock sync logic.
- [ ] Share code with `ServerNetworkingManager` and `ClientNetworkingManager` through small internal helpers rather than copying entire managers.
- [ ] Add LAN two-peer smoke task.

Acceptance: one peer can host and another peer can join under `LocalControlPlane` using the same world/session validation as client/server mode.

#### Phase A2 — Roster And Trusted Election

- [ ] Maintain `PeerRosterSnapshot`.
- [ ] Track peer RTT, packet loss, upload budget, and last heard time.
- [ ] Implement deterministic candidate priority.
- [ ] Implement `HostElectionProposal` and `HostElectionVote` (unsigned, majority).
- [ ] Add host timeout detection.
- [ ] Add tests for deterministic election and stale epoch rejection.

Acceptance: peers in `Trusted` tier agree on the same host candidate for a fixed roster and reject stale election messages.

#### Phase A3 — Graceful Host Migration (Trusted)

- [ ] Implement `HostMigrationPrepare`.
- [ ] Serialize migration baseline from current host (state root computed but unsigned).
- [ ] Transfer roster, leases, server tick, and latest snapshot.
- [ ] Implement `HostMigrationCommit` and endpoint redirection (no certificate in `Trusted`).
- [ ] Keep old endpoint context alive until commit/timeout.
- [ ] Add local smoke task that migrates host from peer 1 to peer 2.

Acceptance: a host can voluntarily hand off to another peer without disconnecting participants.

#### Phase A4 — Failure Host Migration (Trusted)

- [ ] Detect missed host heartbeats.
- [ ] Freeze admission and non-local authority changes during migration grace.
- [ ] Elect replacement host.
- [ ] Commit from the newest available snapshot.
- [ ] Reconcile predicted actors against the new host.
- [ ] Add smoke test that kills the host process and verifies peer recovery.

Acceptance: remaining peers recover after host loss within the configured timeout.

#### Phase A5 — Tooling And UX (Trusted)

- [ ] Add ImGui peer controls (peer mode, local peer id, current host, host epoch, candidate priority, force migration).
- [ ] Add VS Code tasks: `Start-PeerHost-NoDebug`, `Start-PeerParticipant-NoDebug`, `Start-2Peers-NoDebug`, `Start-PeerHostMigration-NoDebug`.
- [ ] Update `Tools\Start-NetworkTest.bat` with a `peer` mode.
- [ ] Update `docs/features/networking.md`.

Acceptance: developers can run, observe, and force host migration locally without external tools.

### Track B — Public Lobby / BFT

Each Track-B phase requires its Track-A counterpart to be merged.

#### Phase B0 — Crypto Primitives And Boundary Hardening

- [ ] Pin signing algorithm (Ed25519) and add a vetted .NET implementation.
- [ ] Pin transport encryption choice (Noise_IK or DTLS 1.3) and add the dependency.
- [ ] Pin hashing algorithm (BLAKE3 or SHA-256) and add the dependency.
- [ ] Run `Tools/Generate-Dependencies.ps1`; document licenses.
- [ ] Implement `SignedPeerMessage<T>` envelope and AOT registry entries.
- [ ] Implement `PeerIdentity` keypair persistence (encrypted at rest).
- [ ] Define and serialize `PeerAttestation`; verify against control-plane trust anchor.
- [ ] Add per-peer monotonic nonce store and replay-window enforcement.

Acceptance: signed/encrypted variants of all peer DTOs round-trip; verification rejects replays, expired attestations, and bad signatures with attributed metrics.

#### Phase B1 — Encrypted Transport And ICE

- [ ] Wrap UDP payloads with the chosen encryption layer in `Public` tier.
- [ ] Implement multi-candidate `TransportCandidate` model and ICE-style connectivity probe.
- [ ] Add per-identity token buckets and signed join cookies.
- [ ] Add signature-verification rate limiter and budget metrics.

Acceptance: two peers in `Public` tier complete handshake over a relayed candidate pair, exchange traffic, and survive a candidate failover.

#### Phase B2 — BFT Election

- [ ] Implement signed proposals/votes with `2f + 1` quorum.
- [ ] Implement view-change with exponential backoff on stuck epochs.
- [ ] Implement equivocation detection, evidence packaging, and `IAbuseReportSink` reporting.
- [ ] Enforce candidacy whitelist from `IRosterProvider`.
- [ ] Add deterministic-election unit tests under simulated Byzantine behavior (`f` colluding voters).

Acceptance: a `4`-peer `Public` session tolerates `f = 1` Byzantine peer producing arbitrary signed traffic without committing a wrong host.

#### Phase B3 — BFT Migration And State Roots

- [ ] Compute and publish `StateRoot` per tick (BLAKE3 over canonical authoritative state).
- [ ] Sign snapshot envelopes, authority lease grants, and heartbeats.
- [ ] Implement `CommitCertificate` collection and verification.
- [ ] Implement "highest signed snapshot held by `f + 1` peers" rule for failure migration.
- [ ] Implement fork rejection and session-abort path.

Acceptance: graceful and failure migrations in a `Public` session always converge on a commit certificate that every honest peer verifies.

#### Phase B4 — Determinism And Witnesses

- [ ] Audit authoritative simulation paths for non-determinism; replace ambient RNG/time use with per-tick seeded sources.
- [ ] Define which subsystems are excluded from `StateRoot` and document why.
- [ ] Implement opt-in `Witness` subrole that recomputes state from inputs and broadcasts divergence evidence.
- [ ] Add tests for deterministic replay across two processes.

Acceptance: a witness peer detects and reports a host that publishes a `StateRoot` inconsistent with the input stream.

#### Phase B5 — Reference Control Plane And Ops

- [ ] Ship a sample HTTP `IControlPlane` adapter in a separate sample project (not in core).
- [ ] Document the boundary contract for third-party control planes (DTOs, error codes, capability flags, idempotency).
- [ ] Add abuse-report flow integration test (equivocation → `IAbuseReportSink` → sample control plane revokes attestation → peer kicked on next roster delta).
- [ ] Add ImGui panels for `Public`-tier diagnostics (attestation expiry, signature throughput, witness divergence count).
- [ ] Update `docs/features/networking.md` and add `docs/architecture/networking/peer-mode-bft.md`.

Acceptance: a third-party can implement `IControlPlane` against the documented contract and host an XRENGINE `Public` session end-to-end using only the sample as reference.

## Test Plan

- Unit tests:
  - DTO round trips (signed and unsigned).
  - AOT registry coverage for all peer DTOs and `SignedPeerMessage<T>`.
  - deterministic election priority.
  - stale host epoch and stale nonce rejection.
  - migration state-machine transitions.
  - signature verify/reject paths for every signed DTO.
  - commit-certificate validity (correct `2f + 1`, missing votes, duplicate signers, wrong epoch).
  - equivocation evidence construction and verification.
  - replay-window and per-identity rate-limit enforcement.
  - state-root determinism for a fixed input stream.
- Integration tests:
  - hosted peer plus one participant (`Trusted`).
  - two participants with graceful host migration (`Trusted` and `Public`).
  - host process killed during active replication.
  - world identity mismatch rejection.
  - stale migration commit ignored.
  - `4`-peer `Public` session with one Byzantine peer (proposal flooding, vote equivocation, fake snapshots) — honest peers converge.
  - candidate failover from direct to relayed endpoint.
  - control-plane offline mid-session: existing session continues, no new joins admitted.
- Manual smoke:
  - run two editor peers locally.
  - force host switch from ImGui.
  - inspect RTT/packet counters and signature/state-root metrics for old and new host endpoints.

## Open Questions

- BFT quorum sizing: should `f` be fixed by the control plane per session, or recomputed live as the roster shrinks? (Leaning: fixed at session start; roster shrink below `3f + 1` aborts the session.)
- Should host candidacy in `Public` always be permissioned by the control plane, or allow open candidacy with reputation-based filtering?
- Should the engine ship a built-in witness implementation, or leave witness logic entirely to gameplay code?
- How frequently should the active host publish signed baselines to keep failure-migration loss `≤ 500 ms`?
- Which gameplay systems need explicit migration callbacks beyond network entity leases and transforms?
- Should `EquivocationEvidence` be retained on disk for post-mortem, and if so, where (engine cache vs. control-plane upload)?
- Choice of encryption library: Noise_IK via a vetted .NET binding vs. DTLS 1.3 via `System.Net.Security` — needs a decision before Phase B0 starts.

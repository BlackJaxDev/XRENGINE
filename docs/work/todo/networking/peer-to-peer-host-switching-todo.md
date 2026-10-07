# Peer-To-Peer Host Switching TODO

Last Updated: 2026-10-06
Status: Planned
Architecture: none yet. Write `docs/architecture/networking/peer-mode-bft.md` when the peer-mode code lands.
Design: [Peer-To-Peer Host Switching](../../design/networking/peer-to-peer-host-switching.md)
Validation: [Networking Validation](../../testing/networking/networking-validation.md#peer-to-peer-host-switching)

## Current State

Peer mode has not landed. `ENetworkingType` only contains `Server`, `Client`, and `Local`. No `PeerNetworkingManager`, peer DTO set, trust-tier type, peer tasks, or peer control-plane assembly exists. The current realtime stack still provides the UDP sequencing, ACK, resend, RTT, token-bucket, admission, replication, and authoritative-simulation primitives that peer mode must reuse.

## Open Code Items

### Local Peer Contracts

- [ ] Add `ENetworkingType.Peer` and route it through `GameStartupSettings`. Done when: startup can select peer mode without using server or client mode.
- [ ] Create `PeerNetworkingManager` with joining, hosted, and participant subroles and clean transitions. Done when: subrole transitions are explicit and testable.
- [ ] Add `TrustTier` with `Local`, `Trusted`, and `Public`, plus `PeerSessionSettings` for peer ID, host metadata, candidate metrics, preferred host, and observed host epoch. Done when: trust tier is immutable for a session.
- [ ] Create the peer control-plane boundary with interfaces for session directory, peer identity authority, roster provider, relay directory, and abuse report sink. Done when: engine code depends only on interfaces and DTOs.
- [ ] Implement in-process `LocalControlPlane` and fail-closed `NullControlPlane`. Done when: local tests can resolve a session, fetch a roster, and admit a local peer.
- [ ] Add unsigned peer DTOs for join, assignment, roster, election, migration, and heartbeat messages. Done when: DTOs are AOT-safe and do not overload player-assignment payloads.
- [ ] Register every peer DTO in `NetworkingAotContractRegistry` and add explicit wire protocol values. Done when: round-trip tests cover every DTO.

### Hosted And Participant Roles

- [ ] Extract reusable server helpers for admission, assignment, lease grants, heartbeat, and transform stamping. `ServerNetworkingManager`. Done when: both server mode and hosted peer mode consume the helpers.
- [ ] Extract reusable client helpers for join, prediction, correction, and clock sync. `ClientNetworkingManager`. Done when: both client mode and participant peer mode consume the helpers.
- [ ] Implement the hosted subrole with current server semantics. Done when: hosted peer behavior matches server behavior for validation, admission, and replication.
- [ ] Implement the participant subrole with current client semantics. Done when: participant peer behavior matches client behavior for join, prediction, and synchronization.
- [ ] Wire world asset identity, world bootstrap ID, protocol version, and build version checks into peer admission. Done when: peer mode rejects the same mismatches as client/server mode.
- [ ] Add local peer launch tasks and tool support. `.vscode/tasks.json`, `Tools/Start-NetworkTest.bat`. Done when: developers can start a host and participant locally without manual environment editing.

### Trusted Roster, Election, And Migration

- [ ] Maintain a peer roster with entries, last-heard time, candidate priority, and attestation-expiry placeholder. Done when: roster state is deterministic for a fixed input set.
- [ ] Track per-endpoint RTT, packet loss, upload budget, and last-heard time. Done when: candidate metrics update without changing packet header semantics.
- [ ] Implement deterministic candidate priority by operator preference, control-plane hint, reachability, RTT, upload budget, and peer ID tiebreaker. Done when: fixed roster tests converge on one priority order.
- [ ] Implement unsigned trusted election with majority-of-visible-peers commit and host-timeout detection. Done when: peers self-promote after host failure and reject stale host epochs.
- [ ] Implement graceful migration from the outgoing host. Done when: roster, leases, latest committed server tick, snapshot envelope, and baseline state root move to the new host.
- [ ] Keep the old endpoint context alive until commit acknowledgment or migration timeout. Done when: participants do not disconnect during graceful migration.
- [ ] Implement failure migration after missed host heartbeats. Done when: remaining peers continue from the newest snapshot they hold.
- [ ] Reconcile predicted actors against the new host snapshot. Done when: clients converge after migration.

### Trusted Tooling And Documentation

- [ ] Add an ImGui peer diagnostics panel. Done when: it shows subrole, local peer ID, host peer ID, host epoch, candidate priority, RTT, last-heard time, election counters, and a force-migration action.
- [ ] Add a peer-mode section to the networking developer guide. Done when: the guide explains peer mode, trust tiers, and local trusted flows.

### Public Security And Transport

- [ ] Select signing, transport encryption, and hashing algorithms with owner approval. Done when: dependency choices satisfy repository license rules.
- [ ] Implement `SignedPeerMessage<T>` for peer DTOs. Done when: signatures cover payload, session ID, host epoch, and peer nonce.
- [ ] Register signed peer messages in `NetworkingAotContractRegistry`. Done when: signed DTO round trips are AOT-safe.
- [ ] Implement peer identity generation, encrypted-at-rest persistence, rotation policy, attestation issuance, and attestation verification. Done when: public-tier peers reject expired and bad attestations.
- [ ] Implement per-peer nonce storage, replay-window enforcement, and signature-skew policy. Done when: nonce regression and replay tests fail closed.
- [ ] Wrap public-tier UDP payload bodies with the selected encryption layer while keeping the packet header, sequence numbers, and ACK bitfield plaintext. Done when: MTU and fragmentation logic stay unaffected.
- [ ] Model multi-candidate endpoints and ICE-style probing. Done when: a logical peer uses one active UDP context and can fail over to another candidate pair.
- [ ] Add per-identity token buckets, signed amplification cookies, and signature-verification rate limits. Done when: invalid signatures charge the source budget and cannot exhaust CPU.

### Public Election, Migration, And Witnesses

- [ ] Implement signed BFT election with `2f + 1` quorum where `n >= 3f + 1`. Done when: candidacy whitelist, epoch increment, no-rollback, proposal rate limits, vote rate limits, and view-change backoff are enforced.
- [ ] Implement equivocation detection and evidence reporting. Done when: conflicting signed votes are packaged, broadcast, and sent to `IAbuseReportSink`.
- [ ] Remove equivocators from the active roster for the rest of the session. Done when: later proposals or votes from removed peers fail closed.
- [ ] Compute a state root for each authoritative tick and publish it in signed host heartbeats. Done when: root generation uses a canonical encoding of authoritative state.
- [ ] Sign network snapshots, authority leases, and peer assignments in public tier. Done when: unsigned variants are rejected for public sessions.
- [ ] Implement commit certificate collection and verification. Done when: distinct signers, roster membership, epoch, and baseline root are verified.
- [ ] Implement failure-migration baseline rules and fork rejection. Done when: a chosen baseline appears in enough voter histories and conflicting valid commits abort the session.
- [ ] Add a witness subrole. Done when: witnesses recompute authoritative state, compare host-published roots, package divergence evidence, and trigger no-confidence migration.
- [ ] Audit authoritative simulation for non-determinism. Done when: ambient randomness and wall-clock reads are removed or excluded from the state root with documented limits.

### Reference Control Plane And Tests

- [ ] Ship a sample HTTP peer control-plane adapter outside core engine code. Done when: third parties can implement the boundary from the sample and contracts.
- [ ] Add public-tier diagnostics for attestation expiry, signature throughput, witness divergence count, commit-certificate verification, and abuse reports. Done when: metrics are visible without exposing secrets.
- [ ] Add unit tests for peer DTO serialization, AOT registration, deterministic priority, stale host epoch, nonce rejection, migration state, signatures, certificates, evidence, replay windows, and rate limits. `XREngine.UnitTests/`. Done when: each contract has deterministic coverage.
- [ ] Add integration tests for trusted hosted peer join, trusted graceful migration, trusted failure migration, public migration, stale commits, endpoint failover, control-plane outage, and abuse-report revocation. Done when: tests run without relying on manual editor operation.
- [ ] Add a `docs/architecture/networking/peer-mode-bft.md` architecture doc after public peer code lands. Done when: it covers trust model, election, commit certificates, equivocation, witnesses, determinism, and the boundary contract.

## Decisions Needed

- [ ] Choose encryption library or platform protocol. Owner: project owner.
- [ ] Choose signing library. Owner: project owner.
- [ ] Choose whether `f` is fixed by the control plane at session start or recomputed on roster shrink. Owner: networking.
- [ ] Choose whether public candidacy is always permissioned by the control plane or open with reputation filtering. Owner: networking.
- [ ] Choose whether the engine ships a built-in witness implementation or leaves witnesses to gameplay code. Owner: networking.
- [ ] Choose whether to retain equivocation evidence on disk and where to keep it. Owner: networking.

## Out Of Scope

- Dedicated managed server lifecycle. The control-plane managed server todo owns it.
- Browser-playable realtime clients.
- Changes to the existing UDP packet header unless a separate approved protocol change lands.

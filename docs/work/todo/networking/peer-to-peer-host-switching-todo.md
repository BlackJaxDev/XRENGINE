# Peer-To-Peer Host Switching \u2014 Implementation Todo

Last Updated: 2026-04-24

Companion to [peer-to-peer-host-switching.md](../design/peer-to-peer-host-switching.md). The design doc is the source of truth for *why* and *what*; this doc tracks *what to do* in shippable order.

## Scope

Add `ENetworkingType.Peer` with three trust tiers (`Local`, `Trusted`, `Public`). The engine reuses the existing client/server realtime stack (`BaseNetworkingManager`, ACK/resend, RTT, token bucket) and adds host election, host migration, signed identity, BFT quorum, encrypted transport, and a pluggable control-plane boundary for matchmaking, identity attestation, and relay/NAT traversal.

Two parallel tracks:

- **Track A** \u2014 LAN/`Trusted` peer mode. No crypto, no BFT, no control-plane dependency beyond `LocalControlPlane`. Independently shippable.
- **Track B** \u2014 `Public` lobbies. BFT election + commit, signed identities, encrypted transport, ICE-style multi-candidate endpoints, and the documented `IControlPlane` boundary for third-party hosting. Each Track-B phase requires its Track-A counterpart merged.

## Architectural Invariants (do not violate)

- Reuse `BaseNetworkingManager` UDP primitives \u2014 do **not** rewrite the packet header, sequence comparison, ACK bitfield, resend, RTT, or token-bucket logic.
- All control-plane integration lives in `XREngine.Networking.ControlPlane`. No HTTP/JSON/auth code in `XREngine` core, runtime modules, server, VR client, or editor.
- Engine references the boundary assembly only through interfaces (`IControlPlane` and friends).
- `SignedPeerMessage<T>` envelope is the **only** way signed payloads enter the realtime path.
- Trust tier is set at session start and immutable for the session.
- `Public` tier rejects unsigned variants; `Local`/`Trusted` accept both.
- Host epoch is monotonic across migrations and never reused within a session.
- Determinism is mandatory for `Public` authoritative simulation; non-deterministic subsystems must be excluded from `StateRoot` or quantized.

## Risk / Approval Checklist

These items in §9 of `AGENTS.md` apply and require owner approval before merge:

- [ ] Adding cryptography dependencies (Ed25519 lib, Noise/DTLS lib, BLAKE3 lib) \u2014 run `Tools/Generate-Dependencies.ps1` and refresh `docs/DEPENDENCIES.md` + `docs/licenses/`.
- [ ] Wire-protocol additions to `EStateChangeType` and `NetworkingAotContractRegistry`.
- [ ] Any change to `BaseNetworkingManager` packet layout (target: zero changes).
- [ ] Encryption-layer choice (Noise_IK vs DTLS 1.3) \u2014 propose decision before Phase B0 starts.

---

## Track A \u2014 Trusted Peer Mode

### Phase A0 \u2014 Contracts And Startup

- [ ] Define `ENetworkingType.Peer` and route it through `GameStartupSettings`.
- [ ] Create `PeerNetworkingManager` skeleton with `Joining` / `Hosted` / `Participant` subroles and clean transitions between them.
- [ ] Add `TrustTier { Local, Trusted, Public }` and a `PeerSessionSettings` type covering `PeerId`, host metadata, candidate metrics, preferred host, observed `HostEpoch`.
- [ ] Create `XREngine.Networking.ControlPlane` assembly with `IControlPlane`, `ISessionDirectory`, `IPeerIdentityAuthority`, `IRosterProvider`, `IRelayDirectory`, `IAbuseReportSink`, `ControlPlaneCapabilities`, and the boundary DTOs (`SessionJoinTicket`, `SessionDescriptor`, `PeerIdentity`, `PeerAttestation`, `PeerAttestationResult`, `SessionRoster`, `RosterDelta`, `TransportCandidate`, `AbuseReport`).
- [ ] Implement `LocalControlPlane` (in-process, ephemeral keypairs, no relay, no live roster) and `NullControlPlane` (fail-closed for tests).
- [ ] Add MemoryPackable unsigned peer DTOs: `PeerJoinRequest`, `PeerAssignment`, `PeerRosterSnapshot`, `HostElectionProposal`, `HostElectionVote`, `HostMigrationPrepare`, `HostMigrationCommit`, `HostMigrationAbort`, `PeerHostHeartbeat`.
- [ ] Register every DTO in `NetworkingAotContractRegistry` and add new `EStateChangeType` values; do **not** overload existing player assignment payloads.
- [ ] Add round-trip serialization tests for every DTO under `XREngine.UnitTests`.
- [ ] Unit-test `LocalControlPlane` end-to-end (resolve session \u2192 fetch roster \u2192 admit local peer).

**Acceptance:** A peer process starts in `Joining`, parses handoff/discovery data via `LocalControlPlane`, and serializes every peer control message with no reflection-only assumptions.

### Phase A1 \u2014 Hosted Peer Baseline

- [ ] Extract reusable helpers from `ServerNetworkingManager` (admission, assignment, lease grants, heartbeat, transform stamping) into shared internal types consumed by both server and `Hosted` peer.
- [ ] Extract reusable helpers from `ClientNetworkingManager` (join, prediction, correction, clock sync) for the `Participant` subrole.
- [ ] Implement `Hosted` subrole using the shared helpers; preserve current server semantics exactly.
- [ ] Implement `Participant` subrole using the shared helpers.
- [ ] Wire `WorldAssetIdentity`, `WorldBootstrapId`, `ProtocolVersion`, and `BuildVersion` admission checks identical to client/server mode.
- [ ] Add VS Code task `Start-2Peers-NoDebug` (LAN, two editor peers, `LocalControlPlane`).

**Acceptance:** One peer hosts and another peer joins via `LocalControlPlane`; world/session validation matches client/server.

### Phase A2 \u2014 Roster And Trusted Election

- [ ] Maintain `PeerRosterSnapshot` (entries, last-heard, candidate priority, attestation expiry placeholder).
- [ ] Track per-endpoint RTT, packet loss, upload budget, last-heard time.
- [ ] Implement deterministic candidate priority: operator preference \u2192 control-plane hint \u2192 reachability \u2192 RTT \u2192 upload budget \u2192 `PeerId` tiebreaker.
- [ ] Implement unsigned `HostElectionProposal` and `HostElectionVote` with majority-of-visible-peers commit.
- [ ] Implement host-timeout detection driving failure election.
- [ ] Reject messages with stale `HostEpoch` before any state mutation.
- [ ] Unit tests: deterministic priority for fixed roster, stale-epoch rejection, two-peer self-promotion.

**Acceptance:** Peers in `Trusted` tier converge on the same host for a fixed roster and reject stale election traffic.

### Phase A3 \u2014 Graceful Host Migration (Trusted)

- [ ] Implement `HostMigrationPrepare` flow on the outgoing host.
- [ ] Serialize migration baseline (roster, leases, latest committed `ServerTickId`, snapshot envelope); compute `BaselineStateRoot` (BLAKE3) even though it is unsigned in `Trusted`.
- [ ] Implement `HostMigrationCommit` (no `CommitCertificate` in `Trusted`) and participant endpoint redirection.
- [ ] Keep old endpoint context alive until commit ack or `MigrationTimeout`.
- [ ] Do not copy UDP sequence counters between endpoints.
- [ ] Add VS Code task `Start-PeerHostMigration-NoDebug`.
- [ ] Integration test: graceful host swap from peer 1 to peer 2 with no participant disconnects.

**Acceptance:** A `Trusted` host hands off to another peer without disconnecting participants.

### Phase A4 \u2014 Failure Host Migration (Trusted)

- [ ] Detect missed `PeerHostHeartbeat` past `HostHeartbeatTimeout`.
- [ ] Freeze admission and non-local authority changes during the grace window; keep local prediction running.
- [ ] Run trusted election; winning peer broadcasts `HostMigrationCommit` referencing the newest snapshot it holds.
- [ ] Reconcile predicted actors against the new host's snapshot.
- [ ] Integration test: kill the host process; remaining peers recover within the configured timeout.

**Acceptance:** After a host crash, remaining `Trusted` peers continue the session from the latest committed snapshot.

### Phase A5 \u2014 Tooling And UX (Trusted)

- [ ] ImGui peer panel: subrole, local `PeerId`, host `PeerId`, `HostEpoch`, candidate priority, force-migration button, observability metrics (RTT, last-heard, election counters).
- [ ] VS Code tasks: `Start-PeerHost-NoDebug`, `Start-PeerParticipant-NoDebug`, `Start-2Peers-NoDebug`, `Start-PeerHostMigration-NoDebug`.
- [ ] Add `peer` mode to `Tools/Start-NetworkTest.bat`.
- [ ] Update `docs/features/networking.md` with peer-mode overview, trust tiers, and Track-A flows.

**Acceptance:** Developers run, observe, and force host migration locally without external tools.

---

## Track B \u2014 Public Lobby / BFT

### Phase B0 \u2014 Crypto Primitives And Boundary Hardening

- [ ] Pin signing algorithm (Ed25519); add a vetted .NET implementation as a NuGet dependency.
- [ ] Pin transport encryption (Noise_IK_25519_ChaChaPoly_BLAKE2s **or** DTLS 1.3); record the decision in the design doc.
- [ ] Pin hashing algorithm (BLAKE3 preferred, SHA-256 acceptable).
- [ ] Run `pwsh Tools/Generate-Dependencies.ps1` and commit the refreshed `docs/DEPENDENCIES.md` + `docs/licenses/` output.
- [ ] Implement `SignedPeerMessage<T>` envelope: `{ Payload, SignerPeerId, PeerNonce, Signature }`, signature covers `Payload || SessionId || HostEpoch || PeerNonce`.
- [ ] Register `SignedPeerMessage<T>` for every peer DTO in `NetworkingAotContractRegistry`.
- [ ] Implement `PeerIdentity` keypair generation, persistence (encrypted at rest under a process-key), and rotation policy.
- [ ] Implement `PeerAttestation` issuance/verification against the control-plane trust anchor in `SessionDescriptor`.
- [ ] Implement per-peer monotonic `PeerNonce` store with replay-window enforcement (`SignatureSkew` configurable, default \u00b130 s).
- [ ] Unit tests: signed round-trip per DTO, replay rejection, expired-attestation rejection, bad-signature rejection, nonce regression rejection.

**Acceptance:** Signed/encrypted variants of all peer DTOs round-trip; verification rejects replays, expired attestations, and bad signatures with attributed metrics.

### Phase B1 \u2014 Encrypted Transport And ICE

- [ ] Wrap UDP payload bodies with the chosen encryption layer in `Public` tier; keep packet header / sequence numbers / ACK bitfield plaintext.
- [ ] Bound encrypted-payload size so MTU and fragmentation logic in `BaseNetworkingManager` are unaffected.
- [ ] Model multi-candidate endpoints per logical peer (`TransportCandidate` from `IRelayDirectory`).
- [ ] Implement ICE-style probe: priority-ordered candidate-pair pings, promote first pair to complete a round trip within `CandidateProbeTimeout`, failover on degradation.
- [ ] Maintain at most one active UDP context per logical peer.
- [ ] Add per-identity token bucket layered on top of the existing per-endpoint bucket.
- [ ] Implement signed amplification cookie: host issues `Signed{ SessionId, RemoteEndpoint, IssuedAt }`; joiner must echo it before host allocates per-peer state.
- [ ] Implement signature-verification rate limiter with per-source budget and metrics; charge invalid signatures against the source bucket.
- [ ] Integration test: two peers in `Public` tier complete handshake over a relayed candidate pair, exchange traffic, and survive failover to a different candidate pair.

**Acceptance:** `Public`-tier sessions work across NAT/relay candidate pairs with bounded signature-verify CPU and survive endpoint failover.

### Phase B2 \u2014 BFT Election

- [ ] Implement signed `HostElectionProposal` / `HostElectionVote` with `2f + 1` quorum where `n \u2265 3f + 1`; `f` taken from `SessionDescriptor`.
- [ ] Enforce candidacy whitelist from `IRosterProvider`; reject proposals from non-candidates.
- [ ] Enforce `ProposedHostEpoch == last_committed_epoch + 1` and no-rollback rule on `LastSeenServerTick` / `LastSeenStateRoot`.
- [ ] Rate-limit proposals (one per `ProposalCooldown`, default 2 s) and votes (one per `(SessionId, ProposedHostEpoch)`); drop excess without state mutation.
- [ ] Implement view-change with exponential backoff when no candidate accumulates `2f + 1` votes within `ElectionTimeout`.
- [ ] Implement equivocation detection: hold last-seen signed vote per `(SignerPeerId, ProposedHostEpoch)`; package conflicting pair as `EquivocationEvidence`, broadcast, and report via `IAbuseReportSink`.
- [ ] Remove equivocators from the active roster for the rest of the session.
- [ ] Unit tests: malicious-vote scenarios with `f` colluding voters, view-change progress under message loss, equivocation detection and evidence verification.
- [ ] Integration test: `4`-peer `Public` session with `f = 1` Byzantine peer producing arbitrary signed traffic; honest peers commit the correct host.

**Acceptance:** A `Public` session with `n \u2265 3f + 1` tolerates up to `f` Byzantine peers without committing a wrong host.

### Phase B3 \u2014 BFT Migration And State Roots

- [ ] Compute `StateRoot` per authoritative tick (BLAKE3 over canonical encoding of authoritative state).
- [ ] Publish `StateRoot` in every signed `PeerHostHeartbeat`.
- [ ] Sign `NetworkSnapshotEnvelope`, every `AuthorityLease` grant/revoke, and `PeerAssignment` in `Public`.
- [ ] Implement `CommitCertificate` collection (`2f + 1` distinct signed votes over the same `(SessionId, HostEpoch, CandidatePeerId, BaselineRoot)`).
- [ ] Verify `CommitCertificate` on receipt: distinct signers, all in attested roster, correct epoch, matching baseline.
- [ ] Implement failure-migration baseline rule: chosen `BaselineStateRoot` must appear in `\u2265 f + 1` voters' histories.
- [ ] Implement fork rejection: two valid commits at overlapping ticks \u2192 abort session, report evidence, return to lobby.
- [ ] Tune host snapshot publish cadence to keep failure-migration loss `\u2264 500 ms`; expose as configurable.
- [ ] Integration tests: graceful and failure migration in `Public` always converge on a verifiable certificate.

**Acceptance:** `Public` graceful and failure migrations always converge on a `CommitCertificate` that every honest peer verifies; forks abort cleanly.

### Phase B4 \u2014 Determinism And Witnesses

- [ ] Audit authoritative simulation paths for non-determinism. Replace ambient `Random.Shared` and wall-clock use with per-tick seeded sources.
- [ ] Document which subsystems are excluded from `StateRoot` and why (GPU readbacks, third-party physics tolerances, audio analysis); quantize and snapshot their outputs where required.
- [ ] Add a `Witness` opt-in subrole: recomputes authoritative state from inputs and compares against host-published `StateRoot`.
- [ ] On divergence, witness packages and broadcasts evidence and forces no-confidence vote (`2f + 1` participants \u2192 immediate election).
- [ ] Unit tests: deterministic replay across two processes for a fixed input stream.
- [ ] Integration test: malicious host publishes a `StateRoot` inconsistent with the input stream; witness detects and triggers migration.

**Acceptance:** Determinism is enforced on authoritative paths; a witness reliably detects and reports a misbehaving host.

### Phase B5 \u2014 Reference Control Plane And Ops

- [ ] Ship a sample HTTP `IControlPlane` adapter in a separate sample project (not in core engine).
- [ ] Document the boundary contract for third-party control planes: DTOs, error codes, capability flags, idempotency, retry/backoff expectations, offline fallback semantics.
- [ ] Integration test: equivocation \u2192 `IAbuseReportSink` \u2192 sample control plane revokes attestation \u2192 peer kicked on next `RosterDelta`.
- [ ] ImGui `Public`-tier diagnostics panel: attestation expiry per peer, signature throughput, witness divergence count, commit-certificate verification metrics, abuse reports issued.
- [ ] Update `docs/features/networking.md` with `Public`-tier flows.
- [ ] Add `docs/architecture/networking/peer-mode-bft.md` covering trust model, election/commit invariants, equivocation handling, and the boundary contract.

**Acceptance:** A third party can implement `IControlPlane` against the documented contract and host an XRENGINE `Public` session end-to-end using only the sample as reference.

---

## Test Plan (cross-cutting)

### Unit

- [ ] Round-trip every peer DTO, signed and unsigned.
- [ ] AOT registry coverage for all peer DTOs and `SignedPeerMessage<T>`.
- [ ] Deterministic election priority for fixed roster.
- [ ] Stale `HostEpoch` and stale `PeerNonce` rejection.
- [ ] Migration state-machine transitions (graceful and failure).
- [ ] Signature verify/reject paths per signed DTO.
- [ ] `CommitCertificate` validity: correct `2f + 1`, missing votes, duplicate signers, wrong epoch, non-attested signer.
- [ ] `EquivocationEvidence` construction and verification.
- [ ] Replay-window and per-identity rate-limit enforcement.
- [ ] `StateRoot` determinism for a fixed input stream.

### Integration

- [ ] Hosted peer + one participant (`Trusted`).
- [ ] Two participants with graceful host migration (`Trusted` and `Public`).
- [ ] Host process killed during active replication (both tiers).
- [ ] World identity / `ProtocolVersion` / `BuildVersion` mismatch rejection.
- [ ] Stale migration commit ignored.
- [ ] `4`-peer `Public` session with one Byzantine peer (proposal flooding, vote equivocation, fake snapshots) \u2014 honest peers converge.
- [ ] Candidate failover from direct to relayed endpoint.
- [ ] Control-plane offline mid-session: existing session continues; no new joins admitted; existing peers keep playing.
- [ ] Equivocation \u2192 abuse report \u2192 attestation revocation \u2192 roster eviction.

### Manual smoke

- [ ] Two editor peers locally; force host switch from ImGui; verify RTT/packet/signature/state-root metrics on old and new host endpoints.
- [ ] Three-process LAN session (one host + two participants); kill host; observe failure migration.

---

## Documentation Touchpoints

- [ ] `docs/features/networking.md` \u2014 add peer-mode section, trust tiers, control-plane boundary summary.
- [ ] `docs/architecture/networking/peer-mode-bft.md` \u2014 new doc covering trust model, election, commit certificates, equivocation, witness, determinism rules.
- [ ] `docs/DEPENDENCIES.md` and `docs/licenses/` \u2014 refreshed when crypto deps land (Phase B0).
- [ ] `README.md` \u2014 mention peer mode in supported networking modes once Track A ships.
- [ ] `.vscode/tasks.json` \u2014 add new peer tasks.
- [ ] `Tools/Start-NetworkTest.bat` \u2014 add `peer` mode.

## Open Decisions Blocking Specific Phases

- [ ] **Phase B0:** encryption library choice (Noise_IK via vetted .NET binding vs. DTLS 1.3 via `System.Net.Security`).
- [ ] **Phase B0:** signing library choice (NSec, BouncyCastle, libsodium-net) \u2014 must be MIT/Apache/BSD/Zlib/Unlicense (see `AGENTS.md` §9).
- [ ] **Phase B2:** `f` policy \u2014 fixed by control plane at session start, or live-recomputed on roster shrink? (Working assumption: fixed; roster shrink below `3f + 1` aborts.)
- [ ] **Phase B2:** `Public` candidacy \u2014 always permissioned by control plane, or open with reputation filtering?
- [ ] **Phase B4:** ship a built-in witness implementation, or leave to gameplay code?
- [ ] **Phase B5:** retain `EquivocationEvidence` on disk for post-mortem, and where (engine cache vs. control-plane upload)?

# Control Plane Managed Server Instances and Client Synchronization Todo

Last updated: 2026-09-09

Status: Planned. This tracker follows a source-level gap analysis; it does not claim runtime validation or completed implementation.

## Objective

Implement the complete workflow a website or game uses to manage XRENGINE dedicated instances:

`Browse/create -> place -> stage content -> launch -> ready -> reserve admission -> stage client content -> connect -> synchronize -> play -> leave/drain/stop -> reconcile`

The first milestone is a working local service managing multiple dedicated server processes and native clients. The second milestone adds recovery across service/host restarts. Public hosting completes the remaining service, credential, transport, asset-delivery, and fleet requirements. All three milestones are part of this todo.

The website is a management interface for native clients in this scope. A browser-playable realtime client and peer-to-peer host migration are separate projects; preserve compatible boundaries without making them prerequisites.

## References and Starting Evidence

- [Control plane architecture](../../../architecture/runtime/control-plane.md)
- [Control plane developer guide](../../../developer-guides/networking/control-plane.md)
- [Networking developer guide](../../../developer-guides/networking/networking.md)
- [Dedicated server and matchmaking design](../../design/networking/networking.md)
- [Peer-to-peer host switching todo](peer-to-peer-host-switching-todo.md)

| Current behavior | Source | Remaining integration |
|---|---|---|
| In-memory host/instance records, capacity checks, shared session tokens, and environment handoffs | [InMemoryControlPlane.cs](../../../../XREngine.ControlPlane/InMemoryControlPlane.cs) | Runnable service, process supervision, observed lifecycle, player reservations, and recovery |
| Host snapshot capacity uses active players; placement uses configured slot reservations | [ControlPlaneHostSnapshot.cs](../../../../XREngine.ControlPlane/Models/ControlPlaneHostSnapshot.cs) | One consistent capacity model |
| File hashing and mirroring are standalone helpers | [WorldPackageManifestBuilder.cs](../../../../XREngine.ControlPlane/WorldPackageManifestBuilder.cs) | Verified package loading in both launch paths |
| Dedicated startup creates a minimal default world | [Program.cs](../../../../XREngine.Server/Program.cs), [BootstrapWorldFactory.cs](../../../../XREngine.Runtime.Bootstrap/BootstrapWorldFactory.cs) | Explicit selected-world/game startup |
| Server connection, heartbeat, and departure callbacks exist | [Engine.ServerSessionResolver.cs](../../../../XREngine.Runtime.Bootstrap/Engine/Networking/Engine.ServerSessionResolver.cs) | Worker-to-manager reporting and reconciliation |
| Client networking generates a new random client ID independently of the control-plane player record | [ClientNetworkingManager.cs](../../../../XREngine.Runtime.Core/Networking/ClientNetworkingManager.cs) | Authenticated identity and reservation continuity |
| Editor admission records joins without wiring departures to control-plane leave | [EditorImGuiUI.NetworkingPanel.cs](../../../../XREngine.Editor/IMGUI/EditorImGuiUI.NetworkingPanel.cs) | Correct occupancy and shared client/service workflow |
| UDP joins, assignments, leases, transforms, poses, heartbeats, and a short resume window exist | [ServerNetworkingManager.cs](../../../../XREngine.Runtime.Core/Networking/ServerNetworkingManager.cs) | Sender authentication, complete bootstrap, lifecycle cleanup, and authoritative simulation |
| Input buffering and authoritative transform stamping exist; no integrated buffered-input simulation consumer was found | [ReplicationContracts.cs](../../../../XREngine.Runtime.Core/Networking/ReplicationContracts.cs) | Actual simulation and processed-input acknowledgments |
| Snapshot/delta send methods and receive events exist without integrated world-state production/application | [BaseNetworkingManager.cs](../../../../XREngine.Runtime.Core/Networking/BaseNetworkingManager.cs) | Replicated entity/component lifecycle, baselines, deltas, and resynchronization |
| TCP command/auth/database code is dormant and most command categories are stubs | [CommandProcessor.cs](../../../../XREngine.Server/Commands/CommandProcessor.cs) | Explicit disposition; do not treat it as an implemented management service |

## Ownership and Invariants

| Owner | Responsibility |
|---|---|
| `XREngine.ControlPlane` | Reusable orchestration contracts, instance/player state, placement, reservation policy, and storage abstractions |
| Service wrapper and local host agent; project names to be selected in Phase 0 | Website/game APIs, process ownership, endpoint reservation, worker management, and content staging |
| `XREngine.Server` | Validated worker startup, selected game/world loading, management adapter, readiness, and graceful shutdown |
| `XREngine.Runtime.Core` and `XREngine.Runtime.Bootstrap` | Realtime admission primitives, transport, replication, simulation integration, and application composition |
| Editor, VR client, game integration, and sample website/launcher | User-facing lifecycle, authenticated handoff, content acquisition, connection, synchronization, and failure feedback |

- Run one dedicated instance per worker process initially. A host agent may manage many workers.
- Keep directory, accounts, matchmaking, fleet placement, token issuance, and downloads outside the realtime simulation. The server may expose a private worker-management channel.
- Keep HTTP/storage calls off the packet and simulation paths. Use bounded queues, cached policy, and asynchronous management adapters with explicit failure behavior.
- Preserve useful per-peer UDP sequencing, ACK/resend, RTT, clock, and bandwidth primitives. Evolve protocols deliberately with explicit version negotiation.
- A matching identity string is not proof that the intended content was loaded. Bind readiness and admission to verified loaded content.
- Distinguish requested state from observed worker state, reserved players from connected players, and accepted connections from synchronized players.
- Never report successful launch/join/synchronization solely because a record, process, socket, or handoff was created.
- Keep credentials out of public directory records, URLs, ordinary logs, and diagnostics. Local development authentication must remain explicit and isolated from public deployment.
- Preserve Windows-first/.NET 10 operation and the headless server profile. Avoid per-frame allocations in new runtime paths.
- Follow repository approval requirements when implementation introduces dependency changes, storage migrations, large deployment-script changes, or changes likely to break launch flows. This planning document does not select those changes in advance.

## Phase 0: Define the End-to-End Contracts

Owners: control plane, server, client/runtime. Dependency: none.

- [ ] Select service/host-agent project boundaries, a private worker-management transport, and the minimal website/launcher integration; keep the existing library reusable.
- [ ] Define a versioned worker launch contract: instance/session IDs, worker generation, game/build identity, package manifest and entry point, startup parameters, player limit, tick/physics settings, resource limits, bind/advertised endpoints, and management credentials.
- [ ] Define instance transitions covering allocation, staging, starting, ready, draining, stopping, stopped, and failed. Specify timeout, cancellation, retry, and failure reasons for every operation.
- [ ] Define separate player reservation/connection/synchronization/resume states and which component owns each transition.
- [ ] Define correlated, idempotent service operations and worker events with operation IDs, worker generations, event ordering, and reconciliation snapshots.
- [ ] Define stable account/player identity separately from a client connection ID, reservation ID, and reconnect credential; carry the required identity through handoff and assignment.
- [ ] Define public directory DTOs separately from privileged launch/package/credential DTOs; avoid exposing local paths or private host metadata.
- [ ] Align build, wire protocol, content revision/hash, and schema compatibility checks across allocation, staging, handoff, and UDP admission. Make development overrides an explicit mode.
- [ ] Update the networking design's completion claims to distinguish implemented primitives from incomplete world synchronization and authoritative simulation; align overlapping P2P boundary proposals without implementing P2P here.

Acceptance: One documented create-to-stop sequence accounts for every side effect, owner, credential, identity, state transition, and failure acknowledgment. Subsequent phases implement these same contracts.

## Phase 1: Runnable Service and Correct Instance Accounting

Owners: control plane and service wrapper. Dependency: Phase 0.

- [ ] Host the library in a runnable local service with configuration, structured errors, version information, and a minimal identity provider suitable for local development.
- [ ] Expose browse/get/create/join/leave/drain/stop operations with authorization and observable operation status. Add filtering, visibility policy, and bounded pagination for the directory.
- [ ] Keep new instances pending until a matching worker generation reports readiness; exclude unavailable/draining instances from normal admission.
- [ ] Make create, leave, stop, and repeated client requests idempotent; define races between stop, ready, join, and cancellation.
- [ ] Unify host capacity calculations around configured reservations and separately expose reserved slots, connected players, instance limits, and resource headroom.
- [ ] Reject invalid or conflicting instance IDs, endpoints, package identities, and launch options. Prevent direct-endpoint creation from accidentally bypassing managed-host accounting.
- [ ] Add host registration leases/heartbeats and explicit unhealthy/unavailable status; stop placing workers on stale hosts.
- [ ] Return progress and failure details suitable for the sample website and game UI, with correlation to host/worker logs.

Acceptance: Independent callers share one authoritative directory; concurrent and repeated create/join requests cannot over-reserve capacity or claim an unready instance is available.

## Phase 2: Local Host Supervisor and Worker Lifecycle

Owners: host agent and server management adapter. Dependencies: Phases 0-1.

- [ ] Implement a process launcher/supervisor abstraction and a Windows local implementation for built dedicated-server executables.
- [ ] Track instance ID, worker generation, process identity, executable/configuration identity, start time, and exit status. Stop only processes owned by this host agent.
- [ ] Reserve distinct UDP endpoints atomically; model bind and advertised addresses/ports separately. Handle OS bind races and failed launches without leaking reservations.
- [ ] Allocate isolated working/configuration/state/log directories per worker so concurrent instances do not share mutable game state or contend over build outputs.
- [ ] Implement the private worker channel for startup progress, ready, liveness, status, roster, metrics, kick, drain, and graceful shutdown. Authenticate requests and reject stale generations.
- [ ] Publish ready only after content verification, world/game initialization, required simulation services, and UDP binding succeed. Return explicit startup failures and useful exit codes.
- [ ] Add bounded startup/shutdown deadlines, crash observation, retry/backoff policy, and cancellation of in-flight staging/launch operations.
- [ ] Drain by disabling new admissions, reporting remaining players, and applying an explicit completion/deadline policy; release capacity/ports only after worker exit is confirmed.
- [ ] Apply per-worker CPU/memory/resource policies supported by the host and expose saturation instead of silently overcommitting.
- [ ] Validate malformed or missing managed-launch configuration explicitly rather than silently falling back to unrelated sessions, ports, or worlds.

Acceptance: Create and stop two worker processes with distinct endpoints; a bind conflict, startup failure, crash, and repeated stop each converge to accurate instance state without affecting unrelated processes.

## Phase 3: Verified World and Game Startup

Owners: control-plane content layer, server bootstrap, client launcher. Dependencies: Phases 0-2.

- [ ] Define a package entry manifest that names the loadable world, required scenes/assets, registered game bootstrap, build/schema compatibility, and permitted game configuration.
- [ ] Integrate package verification/staging with worker launch and client join; stage into a temporary location and publish the verified revision atomically.
- [ ] Validate manifest integrity, file hashes/lengths, identity consistency, and path containment. Reject traversal, unsafe links, incomplete packages, and conflicting package/asset declarations.
- [ ] Make `XREngine.Server` load the selected package/world/game; retain the minimal generated server world only as an explicit development profile.
- [ ] Derive the hosted world identity from the verified content actually loaded. Restrict synthetic `XRE_WORLD_*` overrides to declared development workflows.
- [ ] Load the same immutable revision into editor/game/VR clients before realtime admission; support cancellation, cache reuse, progress, and meaningful compatibility errors.
- [ ] Register game bootstrap and serialization contracts for published/AOT builds; fail clearly when a required game or content schema is unavailable.
- [ ] Validate headless game composition: no accidental local camera, input device, audio listener, or rendering dependency is required to simulate the selected world.

Acceptance: Two different selected packages produce the corresponding server worlds. A client with different or altered bytes cannot become ready merely by copying the expected identity strings.

## Phase 4: Reservations, Admission, Identity, and Occupancy

Owners: control plane, server admission adapter, runtime/client. Dependencies: Phases 0-3.

- [ ] Replace immediate player-count increments with expiring admission reservations; retain separate counts for reservations, connected players, synchronized players, and resume holds.
- [ ] Issue player-scoped credentials bound to reservation, instance/session, worker generation, content/build, expiry, and allowed operation. Define replay, duplicate connection, revocation, and reconnect behavior.
- [ ] Carry authenticated identity through the handoff to the runtime instead of generating an unrelated ID for every networking manager.
- [ ] Validate/consume the reservation at server admission and enforce a local hard capacity limit. A copied shared session token must not authorize arbitrary additional players.
- [ ] Confirm connection only after all admission/world checks and pawn/controller creation succeed; roll back failed reservations and partial server objects.
- [ ] Wire connected, heartbeat, synchronized, disconnected, timed-out, and kicked events to the manager. Reconcile with periodic authoritative roster snapshots so lost/duplicate events do not corrupt occupancy.
- [ ] Expire unused handoffs; handle client cancellation, loading failure, network failure, and join races without leaving permanent occupied slots.
- [ ] Integrate the editor's admission and departure paths with the same accounting, fixing stale occupancy after leave/kick/timeout and rollback after failed joins.
- [ ] Define resume behavior within and beyond the grace window, including slot reservation, pawn ownership, credential renewal, and identity continuity across client restart.
- [ ] Define control-plane outage behavior: existing authenticated sessions continue where valid; new joins fail clearly unless an explicit bounded offline admission policy permits them.

Acceptance: Website identity, reservation, server connection, and displayed occupancy agree. Full rooms reject excess clients; unused tickets expire; disconnect/reconnect and duplicate events do not leak or double-count slots.

## Phase 5: Bind Realtime Traffic to Authenticated Connections

Owners: shared networking runtime and server admission adapter. Dependency: Phase 4. Required before shared-network/public use.

- [ ] Associate admitted transport peers with authenticated connection/session identities; authorize messages against that association before dispatching mutations.
- [ ] Validate input, transform, pose, leave, and heartbeat senders against the admitted connection rather than trusting payload player/client/entity IDs.
- [ ] Require session identity on managed traffic and reject missing/mismatched identity instead of filling it from another player's record.
- [ ] Prevent heartbeat or repeated join traffic from changing an existing connection's endpoint without authenticated rebinding/resume.
- [ ] Enforce message direction and privilege for assignments, authority updates, clock messages, replication, and remote-job requests/responses; gate any other exposed mutation/dispatch paths.
- [ ] Add bounded packet/payload/decompression/queue limits, replay/order checks, and per-peer admission/traffic limits with visible diagnostics.
- [ ] Keep authorization independent from application-provided entity identifiers and ensure rejection leaves world, roster, leases, and endpoint state unchanged.

Acceptance: A connection cannot move another player's pawn, submit their input/pose, refresh or redirect their heartbeat, remove them, or publish server-only state by changing payload IDs.

## Phase 6: Complete Bootstrap, Replication, and Late Join

Owners: shared runtime, server/game replication adapter, client integration. Dependencies: Phases 3-5.

- [ ] Define the replicated entity/component schema and opt-in game-state extension boundary, stable network IDs, factories, ownership metadata, schema versions, and unsupported-type diagnostics.
- [ ] Implement server producers and client consumers behind the existing snapshot/delta transport hooks; carry actual entity/component/game state rather than only envelopes.
- [ ] Send a complete relevant roster and entity baseline to each joining client, including stationary players, dynamic objects, current component/game state, and authority metadata.
- [ ] Create/destroy entities and components in a defined order; resolve references, parents, assets, and remote pawn/controller identity deterministically.
- [ ] Capture a consistent baseline at a simulation tick; buffer subsequent deltas during transfer and apply them after the baseline. Bound memory, chunk sizes, acknowledgment/retry windows, and transfer time.
- [ ] Handle missing/duplicate/out-of-order deltas, baseline mismatches, and interrupted joins through explicit resynchronization.
- [ ] Turn advisory scene metadata into a verified loaded-content contract; fail joins when required scenes or game factories are unavailable.
- [ ] Add a synchronization-complete acknowledgment and playable gate; assignment alone must not enable gameplay or report the player ready.
- [ ] Integrate humanoid baseline/delta initialization, ownership, and late-join recovery with replicated avatar creation.
- [ ] Apply existing relevance/bandwidth policy to initial and ongoing replication; handle entities entering/leaving interest with baselines and explicit removals.
- [ ] Clean up server/client pawns, controllers, input buffers, leases, replication state, and transport state on leave, kick, expired resume, stop, and repeated instance switches.

Acceptance: A late joiner receives the same relevant live state as existing clients, including stationary actors and objects changed before joining. Loss/reordering causes recovery or a visible failure, never silent partial readiness.

## Phase 7: Authoritative Simulation and Client Reconciliation

Owners: server/game simulation integration and shared runtime/client. Dependencies: Phases 5-6.

- [ ] Consume validated buffered inputs at fixed simulation ticks with bounded ordering, duplicate rejection, timing windows, and explicit handling of late/missing input.
- [ ] Apply supported input commands to server-owned simulation/physics instead of treating receipt as completed processing.
- [ ] Generate authoritative movement/state from server simulation. Validate or reject client transform proposals under an explicit authority policy.
- [ ] Advance `LastProcessedInputSequence` only when the corresponding input has actually been simulated; stamp state with the correct simulation tick and baseline.
- [ ] Integrate client prediction history, server correction, rewind/replay of unacknowledged input where supported, and remote interpolation.
- [ ] Define game extension points for interactions and additional input schemas beyond character locomotion; preserve AOT-safe serialization.
- [ ] Separate tracked-avatar pose updates from authoritative physics/gameplay effects. Validate timing, ownership, and bounds before accepting pose-driven interactions.
- [ ] Define and implement lease grant/renew/revoke/transfer behavior for replicated interactable entities, with deterministic ownership conflict resolution by the server.
- [ ] Record tick duration, input queue age/depth, correction magnitude, and replication cost; validate configured tick/physics budgets and hot-path allocations with multiple clients.

Acceptance: A client's movement is reconstructed from its inputs on the server; invalid transform proposals do not become authoritative merely by being received. Clients converge under latency/loss while acknowledgments refer to simulated inputs.

## Phase 8: Website, Launcher, Editor, and Game Workflow

Owners: service sample UI, client integration, editor/VR client. Dependencies: Phases 1-7; UI scaffolding may proceed against Phase 0 contracts.

- [ ] Provide a sample website using the service to browse/create instances, view startup progress, join, leave, inspect occupancy, and perform authorized kick/drain/stop actions.
- [ ] Provide a supported native launcher/handoff mechanism plus a reusable game client API. An already-running game must be able to join without manual environment editing or JSON copy/paste.
- [ ] Expose allocating, staging, starting, reserving, connecting, loading, synchronizing, ready, reconnecting, leaving, and failed states with cancellation and retry policy.
- [ ] Protect handoff credentials during website-to-launcher transfer; avoid putting bearer credentials in URLs or logs.
- [ ] Route ImGui editor browse/create/join through the shared service/client flow while retaining explicit direct-development controls.
- [ ] Support clean instance switching in editor, VR client, and a representative game client without stale environment overrides, player objects, ports, or leases.
- [ ] Surface world/build mismatch, full room, invalid/expired ticket, unready server, unreachable endpoint, interrupted synchronization, kick, and server exit as distinct outcomes.
- [ ] Replace immediate "Joined" UI success after networking restart with observed assignment and synchronization completion.
- [ ] Add documented local launch/stop tasks for the service, host agent, sample UI, and multiple clients without concurrent builds sharing mutable outputs.

Acceptance: A user can create an instance from the sample website, launch or direct a native client into it, see synchronized players, switch instances, and stop it without manual handoff editing or a readiness pause.

## Phase 9: Local End-to-End Milestone and Restart Recovery

Owners: control plane, host agent, server/client integration. Dependencies: Phases 1-8.

- [ ] Demonstrate the local milestone with two dedicated instances using distinct selected worlds and at least three clients, including two sharing one instance and a client in the other.
- [ ] Confirm instance isolation for gameplay, entities, poses, credentials, occupancy, configuration, filesystem state, and shutdown.
- [ ] Exercise late join after world changes, full-room rejection, unused reservation expiry, clean leave, timeout, kick, reconnect, and repeated instance switching through real processes.
- [ ] Exercise bind conflict, missing/corrupt package, startup cancellation, crash before/after ready, interrupted synchronization, and drain/stop races; confirm no orphaned process or stale capacity reservation.
- [ ] Select and implement durable storage for instance intent, generations, host/endpoint leases, reservations, operation outcomes, and protected credential metadata.
- [ ] Reconcile service restarts with surviving host agents/workers; reconcile host-agent restarts using verified process ownership and authenticated worker status. Reject stale events and avoid duplicate workers.
- [ ] Define what survives a worker crash. Reconnecting to a surviving session is distinct from restoring simulation state after worker loss; report session loss unless an explicit checkpoint policy is implemented.
- [ ] Persist required lifecycle/audit evidence and expose stale/unreachable workers until termination or ownership reconciliation is confirmed.
- [ ] Demonstrate service/host-agent restart recovery, stale lease expiry, crash replacement policy, and port reuse without reclaiming an endpoint still held by a live worker.

Acceptance: The local workflow passes with real dedicated processes, and restarting its management components preserves or accurately reconciles active sessions. Worker crashes never silently reset a session while claiming successful resume.

## Phase 10: Public Hosting Completion

Owners: control-plane service/fleet/content teams and shared networking runtime. Dependencies: Phases 0-9.

- [ ] Replace local development identity with authenticated HTTPS APIs, account/tenant authorization, room visibility/join policy, administrative roles, request limits, and auditable lifecycle operations.
- [ ] Complete signed, short-lived player credentials, verifier trust configuration, key rotation/revocation, secret storage, and replay/reconnect policy across worker generations.
- [ ] Implement authenticated and encrypted realtime transport using a reviewed protocol/dependency choice; retain explicit native-development mode and reject public connections lacking the required protection.
- [ ] Implement remote immutable package storage/catalog, authorized or signed downloads, resumable transfer, integrity validation, bounded caches, eviction, and package/build availability checks before placement.
- [ ] Implement durable distributed allocation with atomic reservations, leases/fencing, idempotent operations, and reconciliation across multiple service and host-agent processes.
- [ ] Add multiple-host placement using build/content compatibility, health, resource capacity, and regional policy; add warm capacity, scale-out/scale-in, backpressure, and bounded retry behavior.
- [ ] Validate advertised endpoint reachability, firewall/NAT configuration, public address changes, and cross-machine client connectivity. Document direct-UDP requirements and any explicitly selected relay policy.
- [ ] Add operational metrics/log correlation for hosts, worker generations, instances, players, admission, staging, synchronization, tick health, packet loss, and bandwidth; redact credentials throughout.
- [ ] Support rolling deployment through version-aware placement and draining; document whether players finish, reconnect elsewhere, or lose a session when a worker must stop.
- [ ] Validate control-plane/storage/content outages, host loss, delayed/duplicate events, capacity exhaustion, and region unavailability; publish recovery behavior and operating procedures.
- [ ] Run authorized sustained-load and fault scenarios at a declared target scale; record measured capacity, startup/join latency, synchronization time, tick headroom, and resource limits before declaring public readiness.

Acceptance: Authenticated users can manage and join reachable workers across hosts with protected credentials/traffic, verified remote content, durable allocation, and documented failure/recovery behavior backed by runtime evidence.

## Validation, Documentation, and Closeout

This is a planning-only change. Do not create or run feature tests just to add this document. During implementation, follow the repository's feature-validation-first policy: validate the relevant live/runtime path first, then begin new or modified regression/integration test work only after explicit user clearance. Existing targeted checks may be used when necessary to diagnose an active defect.

- [ ] Maintain phase evidence in `docs/work/progress/networking/` and focused investigations in `docs/work/investigations/networking/`; record attempts, results, and user-reported failures without marking unverified work complete.
- [ ] Keep disposable builds, process logs, captures, and reports under a bounded `Build/_AgentValidation/<run>/` root. Use named isolated sessions for editor MCP validation and stop only task-owned processes.
- [ ] Build the narrowest affected projects during implementation; validate standalone published server/client startup and AOT serialization when relevant.
- [ ] After runtime validation and user clearance, add targeted coverage for lifecycle races/idempotency, capacity, reservation expiry, authenticated sender binding, package validation, bootstrap/deltas, authoritative input processing, and restart reconciliation.
- [ ] After the same gate, add repeatable multi-process integration coverage for the Phase 9 scenarios and fault-injection coverage for Phase 10; contract serialization tests alone are insufficient acceptance evidence.
- [ ] Update control-plane architecture/developer docs, networking docs, server/client setup, environment/launch contracts, and canonical VS Code tasks as each workflow changes.
- [ ] Remove or relocate dormant server TCP account/directory/database stubs once the replacement boundaries are implemented; reconcile obsolete launch profiles and documentation.
- [ ] Review any added or changed dependency licenses and update dependency reports according to `AGENTS.md`.
- [ ] Record separate completion decisions for local end-to-end operation, management restart recovery, and public hosting. Keep unsupported browser transport, host migration, and crash-state restoration explicitly distinguished from implemented behavior.

Completion requires the actual managed server/client workflow, not only new DTOs, API responses, token issuance, transport envelopes, or compiler success.

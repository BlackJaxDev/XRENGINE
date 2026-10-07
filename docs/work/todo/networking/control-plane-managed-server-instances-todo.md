# Control Plane Managed Server Instances TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Control Plane](../../../architecture/runtime/control-plane.md)
Validation: [Networking Validation](../../testing/networking/networking-validation.md#control-plane-managed-server-instances)

## Current State

The local managed-service path exists. `XREngine.ControlPlane.Service` provides the authenticated loopback service, same-origin sample UI, allowlisted native launcher, host-agent durability ledger, admission signer, and gateway mode. `LocalWorkerSupervisor` launches and reconciles owned Windows worker processes. `XREngine.Server/ManagedServerWorker.*` reports readiness, metrics, roster state, admission state, and optional `NativeTls` ingress. `ManagedClientJoinCoordinator` applies verified managed launches and observes assignment and synchronization. Open work remains in local end-to-end validation, service and host-agent recovery hardening, public hosting, and operational coverage.

## Open Code Items

### Simulation Metrics And Budget Enforcement

- [ ] Complete correction-magnitude and allocation sampling for managed realtime sessions. `XREngine.Server/ManagedServerWorker.cs`, shared networking metrics. Done when: worker metrics report correction magnitude and allocation samples beside tick duration, input queue age, queue depth, and replication cost.
- [ ] Validate configured tick and physics budgets before managed workers report healthy readiness. `ManagedWorkerMetrics`, server simulation adapters. Done when: a worker exposes a clear unhealthy reason when it cannot meet configured budgets.

### User Workflow And Client Handoff

- [ ] Complete the sample website workflow for browse, create, startup progress, reserve, native launch, leave, kick, drain, and stop. `XREngine.ControlPlane.Service/ManagedServiceUiEndpoints.cs`. Done when: one UI session can drive the full local lifecycle without manual JSON or environment editing.
- [ ] Harden the reusable native client API for already-running games. `ManagedInstanceServiceClient`, `ManagedClientJoinCoordinator`. Done when: a running client can join, cancel, retry, leave, and switch instances without stale handoff state.
- [ ] Route ImGui editor browse, create, and join through the shared service/client flow while keeping explicit direct-development controls. `XREngine.Editor/IMGUI/EditorImGuiUI.NetworkingPanel.cs`. Done when: the panel exposes managed service operations and still supports direct server/client development.
- [ ] Support clean instance switching in the editor, VR client, and a representative game client. Runtime bootstrap and client integration. Done when: player objects, leases, ports, worlds, and environment overrides from the old instance do not leak into the new one.
- [ ] Replace immediate joined-state UI success with observed assignment and synchronization completion. Editor and launcher UI. Done when: UI states distinguish reserving, connecting, synchronizing, ready, reconnecting, leaving, and failed.
- [ ] Add documented local launch and stop tasks for the service, host agent, sample UI, and multiple clients. `.vscode/tasks.json`, developer guide. Done when: tasks do not share mutable build output during concurrent runs.

### Local End-To-End Operation

- [ ] Exercise two dedicated instances with distinct selected worlds and at least three clients. Control-plane service, server, runtime client. Done when: two clients share one instance, one client joins the other, and isolation holds for gameplay, entities, poses, credentials, occupancy, configuration, filesystem state, and shutdown.
- [ ] Cover late join after world changes, full-room rejection, unused reservation expiry, clean leave, timeout, kick, reconnect, and repeated instance switching in real processes. Done when: each scenario returns the expected service, worker, and client state.
- [ ] Cover bind conflict, corrupt package, startup cancellation, crash before ready, crash after ready, interrupted synchronization, and drain and stop races. Done when: no scenario leaves an orphan process or stale reservation.

### Recovery And Durable Ownership

- [ ] Finish durable storage for instance intent, generations, host and endpoint leases, reservations, operation outcomes, and protected credential metadata. `LocalHostAgentStore`, `DurableControlPlaneSnapshot`, service storage abstractions. Done when: restart import rejects malformed checkpoints and restores valid lifecycle intent.
- [ ] Reconcile service restarts with surviving host agents and workers. `LocalWorkerSupervisor.ReconcileDurableWorkers`. Done when: verified workers reattach and unverified records remain stale without reusing their endpoints.
- [ ] Reconcile host-agent restarts using verified process ownership and authenticated worker status. Done when: stale events cannot revive old workers and duplicate workers are not created.
- [ ] Define and implement worker-crash behavior. Control-plane service and server worker. Done when: a worker crash reports session loss unless an explicit checkpoint policy restores simulation state.
- [ ] Persist required lifecycle and audit evidence without credentials. Service logging and storage. Done when: operators can audit lifecycle operations after restart and logs remain redacted.

### Public Hosting

- [ ] Replace local development identity with authenticated HTTPS APIs, account and tenant authorization, room visibility and join policy, administrative roles, request limits, and audit records. `XREngine.ControlPlane.Service`. Done when: public callers cannot use local bearer shortcuts.
- [ ] Complete signed, short-lived player credentials, verifier trust configuration, key rotation, revocation, secret storage, and replay or reconnect policy across worker generations. `ManagedAdmissionSigner`, worker admission. Done when: workers accept current credentials and reject revoked, replayed, stale, and wrong-generation credentials.
- [ ] Complete reviewed public realtime transport. `RealtimeTlsGateway`, client TLS tunnel, server worker. Done when: public connections without required protection fail and native development remains explicit.
- [ ] Implement remote immutable package storage and catalog support with authorized or signed downloads, resumable transfer, integrity validation, bounded caches, eviction, and build availability checks before placement. Done when: placement cannot select a host that lacks the required content.
- [ ] Implement durable distributed allocation with atomic reservations, leases, fencing, idempotent operations, and reconciliation across multiple service and host-agent processes. Done when: competing services cannot over-place or duplicate a worker.
- [ ] Add multiple-host placement using build and content compatibility, health, resource capacity, and regional policy. Done when: placement supports warm capacity, scale-out, scale-in, backpressure, and bounded retry.
- [ ] Validate advertised endpoint reachability, firewall and NAT configuration, public address changes, and cross-machine client connectivity. Done when: operators get a clear direct-UDP or relay policy.
- [ ] Add operational metrics and log correlation for hosts, worker generations, instances, players, admission, staging, synchronization, tick health, packet loss, and bandwidth. Done when: telemetry redacts credentials and correlates service, worker, and client events.
- [ ] Support rolling deployment with version-aware placement and draining. Done when: documentation and code define whether players finish, reconnect elsewhere, or lose a session when a worker stops.
- [ ] Add authorized sustained-load and fault coverage at a declared target scale. Done when: measured capacity, startup latency, join latency, synchronization time, tick headroom, and resource limits are recorded.

### Tests And Cleanup

- [ ] Add targeted test coverage for lifecycle races, idempotency, capacity, reservation expiry, authenticated sender binding, package validation, bootstrap and deltas, authoritative input processing, and restart reconciliation. `XREngine.UnitTests/`. Done when: focused tests cover each contract without reading Markdown docs.
- [ ] Add repeatable multi-process integration coverage for local end-to-end scenarios and public fault injection. Test harness projects or scripts. Done when: the harness can run the managed scenarios without manual handoff editing.
- [ ] Remove or relocate dormant server TCP account, directory, and database stubs after replacement boundaries are complete. `XREngine.Server/Commands/`. Done when: obsolete command stubs no longer imply a supported management service.
- [ ] Reconcile obsolete launch profiles and documentation after managed tasks land. `.vscode/launch.json`, `.vscode/tasks.json`, networking docs. Done when: documented launch paths match supported workflows.

## Decisions Needed

- [ ] Choose the durable storage backend for public hosting. Owner: control-plane service.
- [ ] Choose the public realtime protection profile and dependency set. Owner: project owner.
- [ ] Choose the worker crash recovery policy for simulation state. Owner: runtime networking.
- [ ] Choose the remote package storage and signing model. Owner: asset pipeline and control-plane service.
- [ ] Choose the direct-UDP, relay, or gateway policy for non-loopback clients. Owner: networking.

## Out Of Scope

- Browser-playable realtime clients.
- Peer-to-peer host migration.
- Silent crash-state restoration without an explicit checkpoint policy.

# Control Plane Runtime Architecture

`XREngine.ControlPlane.Service` hosts the reusable `XREngine.ControlPlane` library and a Windows process supervisor. Each managed instance runs in its own `XREngine.Server` process. A website or game launcher creates/reserves through authenticated HTTP; native clients verify the selected package before connecting directly over UDP.

This is the local orchestration milestone. Full replicated world bootstrap, authoritative input simulation, public-network packet authorization, durable recovery, remote content delivery and the sample website remain separate phases in the [managed instances tracker](../../work/todo/networking/control-plane-managed-server-instances-todo.md). Connected does not mean synchronized or playable.

## Boundaries

| Component | Owns |
|---|---|
| ControlPlane library | Versioned contracts, host reservations, lifecycle, admission grants, roster reconciliation, package verification |
| ControlPlane.Service | Authenticated loopback API, local catalog, Windows jobs, ports, private worker directories, deadlines and process exit |
| Server | Verified world/game startup, cached admission, hard player limit, asynchronous management reports and shutdown |
| Runtime Core/Bootstrap | UDP, player/pawn lifecycle, identity handoffs, verified client world loading |
| Website/game launcher | API identity, stable client ID, create/join progress, client staging, handoff launch and cancellation |

```mermaid
sequenceDiagram
    participant L as Website/game launcher
    participant C as Local service + registry
    participant H as Windows supervisor
    participant S as Dedicated worker
    participant P as Native client
    L->>C: Create(package, slots, operation ID)
    C->>H: Allocate generation, endpoint, resource/slot reservations
    C-->>L: 202 allocating
    H->>H: Verify and atomically stage catalog package
    H->>S: Start owned Job Object with private launch file
    S->>S: Verify/load world, initialize game, bind UDP, tick
    S->>C: Generation/sequence-fenced ready + roster
    L->>C: Reserve(account from credential, stable client ID)
    C-->>L: PendingDelivery reservation
    S->>C: Poll status
    C-->>S: Full grants snapshot + freshness deadline
    S->>C: Installed reservation acknowledgment
    L->>C: Get handoff
    C-->>L: Player grant + verified package/client launch
    L->>P: Stage/cache/load revision, apply handoff
    P->>S: Hello/Challenge/Commit possession handshake
    S->>S: Validate, create pawn/controller, commit connection
    S->>C: Authoritative roster
    L->>C: Leave/revoke, drain or stop
    C-->>S: Revoke/kick, disable joins or graceful stop
    H->>H: Confirm process exit; release port/capacity
```

HTTP and filesystem work stay off packet/simulation paths. Admission reads a cached complete grant snapshot. Every report has a worker generation and increasing sequence. Full snapshots repair lost responses; stale reports cannot revive an exited worker.

## Contracts and identity

Managed contract version and package schema are `1`. Worker launch binds operation, instance, session, generation, package manifest/entry, registered game bootstrap, build, startup/tick settings, player limit, CPU/memory policy, bind/advertised endpoints and management credential. `PackageRootPath` is privileged launch data; manifest `RootPath` is excluded from JSON.

The manifest hash binds identity/entry metadata and file paths, lengths and hashes. Managed verification requires asset content hash to equal manifest hash. Copying identity strings into `XRE_WORLD_*` cannot create a verified managed world. A loaded `XRWorld` receives a verified identity registration only after verification and actual deserialization. Unknown schemas or game bootstraps fail explicitly. Custom game composition and serialization contracts belong in the application build, including AOT builds; the service does not download executable assemblies.

API account identity comes from the local user credential. A client ID identifies one installation/connection continuity scope; the launcher retains it across reconnects. Each reservation has an opaque credential bound to account/client, session, generation, build and content. A session ID is not an admission credential. Handoff is released only after the worker acknowledges installation. Keep launch/handoff files private; public directory projections exclude them, credentials and arbitrary metadata.

## State and failure ownership

Instance progression is `Allocating -> Staging -> Starting -> Ready -> Draining -> Stopping -> Stopped`, with `Failed` for configuration, verification, bind, initialization, liveness, crash and deadline failures. Requested stop supersedes drain/readiness. Only the supervisor confirms exit and releases reservations; a stopped report is insufficient.

Startup, management silence, drain and shutdown are bounded. Drain rejects new admissions and stops at an empty roster or deadline. Stop requests graceful shutdown, then terminates the owned job at its deadline. Closing the service closes worker jobs. The worker also stops after its management lease expires, preventing indefinitely reachable workers after loss of the memory-only manager.

Retry is explicitly manual in this milestone: no automatic restart or generation reuse. Repeating a create operation ID returns its original outcome; different inputs conflict. After terminal failure, a launcher may retry with a new operation ID and bounded backoff. Every attempt gets a new instance/generation and credentials. Automatic recovery and durable retention are Phase 9.

Player progression is `PendingDelivery -> Installed -> Connected -> Synchronized`, with separate `ResumeHeld` state. The registry owns expiry/revocation; workers own connection/departure evidence. Synchronized requires application and acknowledgment of the hashed world/roster/authority/pose baseline. Managed clients keep gameplay paused until confirmation.

Unused grants expire. Connected reservations survive the initial ticket deadline. Transport departure, including the client's UDP leave during disposal, holds a slot for the configured resume window; the same account/client/reservation and generation can resume subject to retained pawn lifetime. API leave/cancel and kick revoke continuity. After grace expiry a new reservation is required. Resume rotates the credential and requires acknowledgment of its exact epoch before handoff delivery. Periodic full rosters repair lost/duplicate observations, and late reports cannot undo revocation.

During short management outages, sessions continue under a bounded lease. New admission requires fresh policy; after lease expiry the local worker shuts down. Indefinite offline operation and adoption after service restart require Phase 9 recovery.

## Local operation and direct development mode

HTTP and advertised UDP endpoints are loopback-only. Trusted operator configuration names built executables, catalog manifests, limits and token environment variables. Ordinary callers cannot supply executable paths, arguments, filesystem roots or management credentials. CPU/memory budgets and configured slot reservations are separate from actual connected players.

The old `CreateInstance`/`JoinInstance` environment helpers and editor-hosted smoke workflow remain a direct development path. They do not supervise processes. Managed records cannot use legacy join/stop/handoff helpers. `--development` selects the standalone generated server world; managed startup requires a verified package. P2P ownership remains separate and does not inherit dedicated-worker completion claims.

See the [developer guide](../../developer-guides/networking/control-plane.md) for configuration/API usage and the [progress note](../../work/progress/networking/control-plane-managed-instances-local.md) for runtime validation evidence.

Managed UDP authenticates realtime frames using directional HMAC, endpoint/session/generation/epoch binding and replay counters. Stateless endpoint challenges precede fenced admission publication. Consumed grants cannot create a fresh association. This provides authenticity; public confidentiality and reviewed public transport remain Phase 10. See [transport and synchronization evidence](../../work/progress/networking/managed-transport-and-replication.md).

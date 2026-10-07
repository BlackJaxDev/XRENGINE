# Control Plane Runtime Architecture

`XREngine.ControlPlane.Service` hosts the reusable `XREngine.ControlPlane` library and a Windows process supervisor. Each managed instance runs in its own `XREngine.Server` process. A website, sample UI, launcher, or game creates and reserves instances through authenticated HTTP. Native clients verify the selected package before they connect to the worker realtime endpoint.

The local orchestration path is partly implemented. It includes the loopback API, sample UI, allowlisted native launcher, worker supervisor, protected local checkpoint, host-agent ownership journal, admission signer, managed worker, managed client join coordinator, and optional `NativeTls` ingress gateway. Remaining work includes full local end-to-end validation, stronger restart and crash recovery, public hosting, remote content storage, multi-host placement, operational scale, and final public transport policy.

Packages are data only. Creation, verification, staging, catalog loading, and content endpoints reject executable payloads per the [downloadable content execution policy](downloadable-content-execution-policy.md).

## Boundaries

| Component | Owns |
|---|---|
| ControlPlane library | Versioned contracts, host reservations, lifecycle, admission grants, roster reconciliation, package verification, and protected local snapshots |
| ControlPlane.Service | Authenticated loopback API, same-origin sample UI, gateway mode, local catalog, Windows jobs, ports, private worker directories, deadlines, process exit, and native launch handoff |
| Server | Verified world and game startup, cached admission, hard player limit, asynchronous management reports, optional `NativeTls` ingress, metrics, and shutdown |
| Runtime.Core | Realtime protocol, admission, replication, player and pawn lifecycle, TLS tunnel contracts, and neutral transport contracts |
| Runtime.Net.Sockets | UDP, TCP, TLS gateway, TLS framing, and socket lifetime |
| Bootstrap | Backend registration, managed client handoffs, verified client world loading, and observed assignment and synchronization state |
| Website, sample UI, game launcher | API identity, stable client ID, create and join progress, client staging, handoff launch, cancellation, and failure display |

```mermaid
sequenceDiagram
    participant L as Website, sample UI, or launcher
    participant C as Local service and registry
    participant H as Windows supervisor
    participant S as Dedicated worker
    participant P as Native client
    L->>C: Create(package, slots, operation ID)
    C->>H: Allocate generation, endpoint, resource and slot reservations
    C-->>L: 202 allocating
    H->>H: Verify and atomically stage catalog package
    H->>S: Start owned Job Object with private launch file
    S->>S: Verify/load world, initialize game, bind realtime endpoint, tick
    S->>C: Generation/sequence-fenced ready and roster
    L->>C: Reserve(account from credential, stable client ID)
    C-->>L: PendingDelivery reservation
    S->>C: Poll status
    C-->>S: Full grants snapshot and freshness deadline
    S->>C: Installed reservation acknowledgment
    L->>C: Get handoff or native launch
    C-->>L: Player grant and verified package launch data
    L->>P: Stage/cache/load revision and apply handoff
    P->>S: Hello, challenge, commit possession handshake
    S->>S: Validate, create pawn/controller, commit connection
    S->>C: Authoritative roster and metrics
    L->>C: Leave, revoke, drain, or stop
    C-->>S: Revoke/kick, disable joins, or graceful stop
    H->>H: Confirm process exit; release port and capacity
```

HTTP and filesystem work stay off packet and simulation paths. Admission reads a cached complete grant snapshot. Every worker report has a generation and increasing sequence. Full snapshots repair lost responses. Stale reports cannot revive an exited worker.

The control-plane service remains separate from the realtime socket module. See [Runtime Project Organization](project-organization.md) for assembly ownership. Public namespaces do not necessarily match physical project names.

## Contracts and identity

Managed contract version and package schema are `1`. Worker launch binds operation, instance, session, generation, package manifest and entry, registered game bootstrap, build, startup and tick settings, player limit, CPU and memory policy, bind and advertised endpoints, and management credential. `PackageRootPath` is privileged launch data. Manifest `RootPath` is excluded from JSON.

The manifest hash binds identity, entry metadata, file paths, lengths, and hashes. Managed verification requires asset content hash to equal manifest hash. Copying identity strings into `XRE_WORLD_*` cannot create a verified managed world. A loaded `XRWorld` receives a verified identity registration only after verification and actual deserialization. Unknown schemas or game bootstraps fail explicitly. Custom game composition and serialization contracts belong in the application build, including AOT builds. The service does not download executable assemblies.

API account identity comes from the configured user credential or public API identity. A client ID identifies one installation or connection-continuity scope. The launcher retains it across reconnects. Each reservation has an opaque credential bound to account, client, session, generation, build, content, expiry, and purpose. A session ID is not an admission credential. Handoff is released only after the worker acknowledges installation. Keep launch and handoff files private. Public directory projections exclude them, credentials, and arbitrary metadata.

`ManagedAdmissionSigner` can sign grants with an operator-provisioned P-256 certificate. Workers receive the issuer and verification keys in launch data and reject invalid signatures. Public key rollout can publish the current key and a bounded set of previous verification-only keys.

## State and failure ownership

Instance progression is `Allocating -> Staging -> Starting -> Ready -> Draining -> Stopping -> Stopped`, with `Failed` for configuration, verification, bind, initialization, liveness, crash, and deadline failures. Requested stop supersedes drain and readiness. Only the supervisor confirms exit and releases reservations. A stopped report from a worker is not sufficient.

Startup, management silence, drain, and shutdown are bounded. Drain rejects new admissions and stops at an empty roster or deadline. Stop requests graceful shutdown, then terminates the owned job at its deadline. Closing the service closes worker jobs. The worker also stops after its management lease expires. This prevents indefinitely reachable workers after loss of the memory-only manager.

The host agent writes a protected local checkpoint and an ownership journal. On restart, it restores the registry, verifies worker launch files, process identity, executable hash, and Windows Job Object membership, and reattaches only verified workers. Unverified records remain stale and keep their endpoints unavailable until an operator reconciles them. This is a local durability mechanism, not a distributed public-hosting store.

Player progression is `PendingDelivery -> Installed -> Connected -> Synchronized`, with separate `ResumeHeld` state. The registry owns expiry and revocation. Workers own connection and departure evidence. Synchronized requires application and acknowledgment of the hashed world, roster, authority, and pose baseline. Managed clients keep gameplay paused until confirmation.

Unused grants expire. Connected reservations survive the initial ticket deadline. Transport departure, including the client's UDP leave during disposal, holds a slot for the configured resume window. The same account, client, reservation, and generation can resume subject to retained pawn lifetime. API leave, cancel, and kick revoke continuity. After grace expiry, a new reservation is required. Resume rotates the credential and requires acknowledgment of its exact epoch before handoff delivery. Periodic full rosters repair lost or duplicate observations. Late reports cannot undo revocation.

During short management outages, sessions continue under a bounded lease. New admission requires fresh policy. After lease expiry, the local worker shuts down. Public multi-service recovery and durable distributed allocation remain open work.

## Local operation and direct development mode

HTTP and default UDP endpoints are loopback-only. Trusted operator configuration names built executables, catalog manifests, limits, and token environment variables. Ordinary callers cannot supply executable paths, arguments, filesystem roots, or management credentials. CPU and memory budgets and configured slot reservations are separate from actual connected players.

The same-origin sample UI can browse, create, reserve, launch an allowlisted native client, leave, kick, drain, and stop. The UI keeps its bearer credential in page memory. Native launch writes a restricted handoff file for the current Windows user and passes its path by environment variable. It does not put player credentials in a URL.

The old `CreateInstance` and `JoinInstance` environment helpers and the editor-hosted smoke workflow remain direct development paths. They do not supervise processes. Managed records cannot use legacy join, stop, or handoff helpers. `--development` selects the standalone generated server world. Managed startup requires a verified package. Peer-to-peer ownership remains separate and does not inherit dedicated-worker completion claims.

See the [developer guide](../../developer-guides/networking/control-plane.md) for configuration and API usage and the [local progress note](../../work/progress/networking/control-plane-managed-instances-local.md) for earlier runtime validation evidence.

Managed UDP authenticates realtime frames using directional HMAC, endpoint, session, generation, epoch binding, and replay counters. Stateless endpoint challenges precede fenced admission publication. Consumed grants cannot create a fresh association. Optional `NativeTls` ingress adds a bounded gateway for non-loopback or protected transport configuration. Full public hosting, remote ingress policy, and reviewed public-scale operations remain open work. See [transport and synchronization evidence](../../work/progress/networking/managed-transport-and-replication.md).

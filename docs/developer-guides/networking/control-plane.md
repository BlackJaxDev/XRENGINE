# XRENGINE Control Plane

Use `XREngine.ControlPlane.Service` for managed local instances. The reusable library owns contracts/accounting; the service supplies authentication, HTTP and observed process lifecycle. See the [architecture](../../architecture/runtime/control-plane.md).

## Build and configure

Requires Windows and .NET 10. Build first, then launch workers from artifacts without parallel builds.

```powershell
dotnet build XREngine.Server/XREngine.Server.csproj -p:XREngineRendererBackends=None -p:XREngineIncludeVulkanBackend=false -p:XREngineIncludeOpenGlBackend=false
dotnet build XREngine.ControlPlane.Service/XREngine.ControlPlane.Service.csproj
```

Author a minimal real package for local development:

```powershell
& $ServerExecutable --create-world-package $PackageDirectory --world-name 'Example World'
```

Use the `packageId` from generated `world-package.json` as the catalog key. A package names one `.asset`, dependent files, bootstrap, build and schema. Content changes require a new manifest.

Copy [local-service.example.json](../../../XREngine.ControlPlane.Service/local-service.example.json) to a private local location. Set the built server executable, working root and catalog entries. Paths are absolute or relative to the configuration file. Provide distinct random tokens of at least 32 characters in the named environment variables. Do not commit tokens. Agent configurations, packages, worker files and logs belong under the task's `Build/_AgentValidation/` root.

```powershell
$env:XRE_LOCAL_ADMIN_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:XRE_LOCAL_PLAYER_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --no-build --project XREngine.ControlPlane.Service -- --config $ConfigurationPath
```

The default API is `http://127.0.0.1:5088`. Only loopback HTTP/advertised UDP endpoints are supported. The registry is memory-only; shutdown stops workers and restart does not adopt previous workers.

## API workflow

Application routes require `Authorization: Bearer <local user token>`. Worker reports require their separate generation-specific management token. `/health` is anonymous. Errors have stable codes; unexpected errors include a correlation ID. Responses disable caching.

| Method/path | Meaning |
|---|---|
| `GET /v1` | Version, local mode and persistence/transport |
| `GET /v1/packages` | Public catalog identities and sizes |
| `GET /v1/hosts` | Admin-only capacity, health and resource headroom |
| `GET /v1/instances` | Visible directory; `worldId`, `state`, `offset`, `limit` (1-100), `includeStopped` |
| `GET /v1/instances/{id}` | Visible instance and separate occupancy counts |
| `POST /v1/instances` | Create: `operationId`, `packageId`, `displayName`, `maxPlayers`, `isPublic` |
| `GET /v1/instances/{id}/status` | Owner/admin progress, PID, metrics, roster and failure |
| `POST /v1/instances/{id}/reservations` | Join: `operationId`, stable `clientId`, `buildVersion`; account comes from credential |
| `GET .../reservations/{reservationId}` | Reservation owner/admin progress |
| `GET .../reservations/{reservationId}/handoff` | Private client launch after worker installation ACK |
| `DELETE .../reservations/{reservationId}` | Idempotent cancel/leave/revoke; kick if connected |
| `POST .../reservations/{reservationId}/kick` | Instance owner/admin revocation |
| `POST /v1/instances/{id}/drain` | Disable admission; stop when empty or deadline passes |
| `POST /v1/instances/{id}/stop` | Idempotent bounded shutdown |

Create returns `202`; poll until `Ready` or terminal failure. Reuse the operation ID for identical retries. Changed inputs conflict. Retry a terminal failed attempt with a new operation ID. Reservation creation returns `202`; an early handoff returns `409 ReservationPendingDelivery`, which means keep waiting.

```powershell
$Api = 'http://127.0.0.1:5088'
$headers = @{ Authorization = "Bearer $env:XRE_LOCAL_PLAYER_TOKEN" }
$instance = Invoke-RestMethod "$Api/v1/instances" -Method Post -Headers $headers -ContentType application/json -Body (@{
    operationId = [Guid]::NewGuid().ToString('N')
    packageId = $PackageId
    displayName = 'Local game'
    maxPlayers = 4
    isPublic = $true
} | ConvertTo-Json)
# Poll GET /v1/instances/{id} until Ready before reserving.
$reservation = Invoke-RestMethod "$Api/v1/instances/$($instance.instanceId)/reservations" -Method Post -Headers $headers -ContentType application/json -Body (@{
    operationId = [Guid]::NewGuid().ToString('N')
    clientId = $StableClientId
    buildVersion = $BuildVersion
} | ConvertTo-Json)
```

The prototype expects clients on the same Windows account/machine. Private handoffs include local package access, but public directory responses contain no paths or credentials. Remote asset delivery belongs to later phases.

## Native client launch

Use `ManagedInstanceServiceClient` in an integrated game or the Editor Networking panel to reserve an instance and acquire its delivered launch. The client sends the service bearer only in an `Authorization` header, downloads the immutable package through the authenticated content route into `RemoteWorldPackageCache`, and gives `ManagedClientJoinCoordinator` an in-memory launch plus active cache lease. The coordinator verifies and loads the declared world before networking, then reports `Connecting`, `Synchronizing`, and `Ready` only after observed assignment and synchronization. It also exposes distinct non-secret failure categories for mismatch, room full, ticket rejection, server availability, synchronization interruption, kick, and server exit.

The sample loopback UI is served at the configured service root. It keeps the bearer only in page memory and never requests the credential-bearing handoff response. To enable its local **Launch** action, configure `NativeLauncher` with `Enabled: true` and an operator-owned `GameExecutable`. The service writes the delivered `ManagedClientLaunch` to a random current-user-only one-shot file, starts only that configured executable, and passes the file path through `XRE_MANAGED_CLIENT_CONFIG_FILE`. The game loader deletes the file after reading it. Do not use this local launcher for remote users or public hosting.

The ImGui editor honors managed configuration at startup. `XREngine.VRClient` is a render/input companion for a main game; launch that game with the managed config. The companion rejects direct managed invocation instead of joining its filler world. VR in the main editor/game uses that application's verified world.

Retain the account/client/reservation identity for resume. After disconnect, poll the reservation and fetch a new handoff once the worker acknowledges its rotated resume credential. Reusing the consumed join handoff does not resume. Transport leave retains a temporary resume hold; `DELETE` cancels that continuity. Cancel through `DELETE` after loading failure, cancellation or failed join; obtain a new reservation after expiry. Never put API tokens into workers or management tokens into clients.

## Worker operation

Each generation has a private directory with `worker.json`, ownership `session.json`, staged content and bounded stdout/stderr logs. The supervisor owns exact process handles and Windows Job Objects. Correlate diagnostics by operation, instance and generation. An existing process is not readiness evidence.

`XRE_MANAGED_WORKER_CONFIG_FILE` selects verified managed startup. Malformed configuration fails. `--development` explicitly selects the generated standalone world for direct debug workflows. Neither mode implements the dormant TCP account/command server.

CPU/memory budgets, slot reservations and startup/shutdown/drain/lease deadlines are operator settings. Saturation rejects placement. Port and host capacity release require confirmed process exit. There is no automatic worker restart in this milestone.

## Remaining scope

Connected and synchronized counts are separate. Managed authentication and full bootstrap/delta replication are implemented. Authoritative simulation (7), website UX (8), persistence/recovery (9), and public encrypted transport/hosting (10) remain tracked.

## Managed replication integration

Managed UDP proves possession of the installed admission verifier. The secret is absent from UDP and cleared from the client property after key derivation. Session, generation, epoch, direction, endpoint and replay checks precede gameplay dispatch. A new connection requires a rotated resume handoff; a consumed join credential cannot open another endpoint.

Gate gameplay on `ClientNetworkingManager.IsGameplayReady`. Assignment alone is insufficient. `ReplicationSynchronizationState`, `ReplicationFailure`, and `ManagedTransportFailure` expose progress/failure; `RealtimeTransportRejections` provides credential-free counters. Authenticated Close or 12 seconds without authenticated server traffic ends the connection.

Add `NetworkReplicatedComponent` to game nodes that must replicate. Ancestors are included for parent resolution. Declare codec IDs in `ComponentSchemas`; built-in `state-v1` supports `ReplicatedStateComponent.Payload`, entity references and verified package asset paths. Undeclared components remain local. TRS transforms and active state are included.

Register entity factories and `NetworkReplicationSchemaRegistry.RegisterComponent<T>` codecs during application composition, before capture/validation. Client/server must have the same frozen fingerprint. Factories return detached nodes with the declared TRS transform. Capture returns owned state; validation is side-effect-free; apply is deterministic, nonblocking, and resolves references from its entity map. All components are created before reference application. Failed application pauses and resets replicas before requesting a fresh baseline. Update schema versions and package build identity when layouts change.

Verified scene names/IDs and authored-node paths are indexed before play callbacks mutate the graph. Runtime nodes have session-scoped wire IDs; replicas never replace the process-wide object cache. Referenced assets must exist in the verified manifest.

Local transfer limits: 4 MiB baseline, 5 KiB snapshot bytes per chunk, 4,096 entities, 64 components per entity, and 16 KiB per component. One chunk/delta awaits ACK at a time, with one-second retries and four retries. Clients buffer at most 64 deltas/512 KiB; baseline inactivity is limited to 12 seconds and total duration to 64 seconds. Large deltas start a fresh hashed baseline. Relevance includes parent/reference closure and explicit removals.

Each admitted player owns one humanoid avatar. The runtime assigns its inner avatar ID from the server player index and initializes remote pawns from the roster. Richer game pawn composition must retain that identity binding. Gameplay physics authority remains Phase 7.

See [Phase 5–6 runtime evidence](../../work/progress/networking/managed-transport-and-replication.md).

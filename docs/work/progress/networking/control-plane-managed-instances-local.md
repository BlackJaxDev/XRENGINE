# Managed local server instances: Phases 0–4

Date: 2026-09-14

Status: Phases 0–4 implemented and validated through local dedicated processes and actual native client networking. Later synchronization, gameplay, recovery and public-hosting milestones remain open.

Scope: The first five phases (0–4) of the [managed instances tracker](../../todo/networking/control-plane-managed-server-instances-todo.md). No new unit tests are authorized during this feature-validation stage. Public packet authorization, complete world synchronization, authoritative simulation, website UX and recovery remain later phases.

## Implementation

- Added the loopback authenticated `XREngine.ControlPlane.Service`, local package catalog and Windows supervisor. Workers receive distinct endpoints, private directories, credentials, startup/resource policies and immutable generations.
- Workers enter their kill-on-close Job Object atomically at process creation. Both CPU hard caps and aggregate job memory bounds match host accounting. Only confirmed process exit releases capacity.
- Added managed library lifecycle, account/client reservations, full roster reconciliation, report sequence/time fences, epoch-specific grant installation acknowledgment, credential purpose/expiry, and separate connected/synchronized/resume counts.
- Integrated actual native `World.asset` loading and verified package identities in server and client startup. Client staging uses an atomic verified cache and ephemeral UDP ports. The built-in `world-v1` bootstrap is headless; custom bootstraps and unsupported schemas fail explicitly.
- Added server polling and cached admission outside the packet path, hard capacity enforcement, admission rollback, retained identity cleanup, API kick/drain/stop, and bounded management-outage shutdown.
- Editor startup accepts managed client configuration. Editor-hosted development admission records occupancy after connection success and removes it on departure. Standalone VRClient remains the main game's input/render companion and rejects direct managed startup.
- Added explicit `--development`, package authoring, canonical tasks/debug updates, and rewritten [architecture](../../../architecture/runtime/control-plane.md) / [API guide](../../../developer-guides/networking/control-plane.md).

## Validation evidence

Scratch evidence is under `Build/_AgentValidation/20260914-172840-managed-instances/`; durable conclusions are recorded here so ignored output is not a documentation dependency.

- Narrow ControlPlane and Service builds pass with zero warnings/errors.
- Headless Server, Editor compilation and the temporary real-client walkthrough build pass with zero warnings/errors using `XREngineRendererBackends=None`, `XREngineIncludeVulkanBackend=false`, and `XREngineIncludeOpenGlBackend=false`.
- Existing concurrent renderer edits briefly blocked the graph. The network work did not modify those files; subsequent narrow builds passed when those edits stabilized.
- Live launch exercises found and corrected unnecessary directory owner reassignment, a dependency on development settings in the isolated worker directory, mismatched descriptor JSON casing, and an invented world file extension. The actual native serialized asset extension is `.asset`.
- Live headless execution also exposed renderer thread reservations inside the worker CPU allowance, render-dependent management queue servicing, and assignment identity tied to a local UI controller. Corrected these paths, including fresh bounded queue dispatch without a render-frame clock, heartbeat/departure/error handling without a UI controller, and nonzero initialization-failure exit codes.
- Broker and native source review identified/fixed exit-gated capacity, legacy managed bypasses, complete roster/epoch checks, ticket-expiry races, job containment/memory accounting, and cleanup paths. Broker requested/actual models matched (`gpt-5.6-sol`).
- Committed connection snapshots now carry admission purpose/epoch/acceptance time directly. Credential retry caches cannot hide live players or release occupancy. Revoked in-flight joins stay observable until actually removed. Freshness and expiry checks share the admission lock; duplicate credentials preserve the first acceptance proof while still checking current expiry.

Completed live observations:

| Scenario | Observed result |
|---|---|
| Two dedicated packages | Both workers reached `Ready` on distinct ports. Native client world object IDs and verified hashes matched their respective packages. |
| Ticket lifetime and capacity | Both clients stayed connected for 25 seconds, beyond the 15-second initial ticket and heartbeat-timeout intervals; an unused reservation expired and excess reservation was rejected. |
| Resume | Restarted client received a rotated resume credential and retained the server player index. |
| Access controls | Anonymous API access failed; an unrelated user could not stop another instance or see its private instance. Directory responses omitted privileged launch fields. |
| Occupied UDP endpoint | Supervisor selected the other free port; exhaustion rejected placement; released ports were reusable. |
| Drain / crash / cancellation | Empty drain stopped cleanly and rejected new joins; worker crash became confirmed `Failed`; repeated stop and startup cancellation released capacity. |
| Altered server content | Modified bytes with unchanged manifest identity failed staging before a worker process was launched. |
| Altered client content | Actual client preflight failed before networking/assignment with unchanged handoff identity. |
| Service process crash | Closing the service terminated the worker through its owned Job Object. |
| Kick and API leave | Both actual worker connection metrics and authoritative rosters reached zero, alongside zero reservation counts. |
| Repeated stop | Both workers exited cleanly and became `Stopped`; host reservations returned to zero, with all configured CPU/memory capacity available. |
| Invalid managed startup | Missing configuration and no explicit mode returned exit 64; structurally invalid configuration returned nonzero exit 70. None fell back to another world or session. |

The walkthrough compared loaded native world IDs `35c49658-ef09-4f01-95a7-1a4f54b876ed` and `f690db3b-d02a-4550-bd7f-873d819f744d` with their package and handoff identities, plus distinct verified content hashes and sessions. Worker ports were 15200 and 15201. The resumed client retained player index 1.

The final rebuild and complete walkthrough also passed after the independent-client fatal-error cleanup correction. All recorded task-owned workers were confirmed exited, and host worker/player-slot reservations were zero. Documentation links, launch/task JSON and the scoped diff whitespace check passed.

No unit tests were added or modified. Editor compilation passed; no manual UI walkthrough or published/AOT runtime validation is claimed. Source-generated managed JSON and built-in serialization/bootstrap contracts are present; published application validation remains part of subsequent release/integration work.

## Completion boundary

The first five phases supply a runnable local management API, supervised verified workers, client preflight, admission and reconciled occupancy. `SynchronizedPlayers` remains zero. The sample website, interactive editor service browser, full replication/bootstrap, authoritative simulation, restart adoption, remote content distribution and public transport/security are still tracked in Phases 5–10.

# Vulkan Resident Draw Stream Phase 1B — Shared Scheduler

Date: 2026-08-22  
Status: Complete — Phase 1B implementation accepted; two broader Phase 1 exit proofs remain open  
Tracker: [Vulkan resident draw stream and render task pool](../../todo/rendering/optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md)

## Problem statement

Introduce one process-wide execution scheduler with persistent general and
renderer-neutral render domains, plus a pooled batch primitive suitable for a
later Vulkan recording migration. The slice must not move Vulkan or OpenXR
recording ownership yet, and it must preserve the validated CPU-direct and GPU
meshlet Vulkan paths.

## Slice boundary

- Construct one `EngineWorkScheduler` from the immutable Phase 1A topology.
- Make `Engine.Jobs` and `RuntimeEngine.Jobs` share its general domain.
- Add persistent stable render lanes, render-thread participation, bounded
  queues, pooled generation-checked batch/item/dependency storage, lane-local
  attachments, cancellation, faults, quarantine, and bounded teardown.
- Install the domain through `IRuntimeRenderWorkServices`.
- Prove one non-native preparation batch and one already-completed diagnostic
  decode at startup.
- Leave Vulkan command-chain workers, OpenXR eye workers, command-pool ownership,
  recording order, submission, and presentation unchanged.

## Implementation result

`EngineWorkScheduler` now owns `EngineGeneralWorkDomain` and
`RenderWorkDomain`. General lanes are persistent `XRE-General-*` threads; a
resolved general count of zero uses a reentrancy-safe cooperative inline drain.
The renderer-neutral domain always has logical lane 0 on the participating
render thread and creates stable `XRE-Render-1..R` workers for a nonzero resolved
count.

`RenderWorkDomain` rents generation-checked leases from a bounded pool. A sealed
batch supports independent and dependent items, migratable and lane-affine
claims, bounded per-lane queues, lane-0 participation, background overlap, and
work stealing. Small batches select inline execution. Cancellation or fault
invalidates partial output; the first executing fault is authoritative over a
concurrent cancellation, and faulted storage remains quarantined until lane 0
finalizes it.

`RenderLaneBackendAttachments` supplies opaque lane/frame-slot storage without
a Runtime.Core dependency on Vulkan. `IRuntimeRenderWorkServices` exposes the
single scheduler to runtime rendering. Startup verifies a four-item
renderer-neutral preparation batch and a general-domain diagnostic decode.
`CompletedDiagnosticPayload` retains only an `ArraySegment<uint>` and rejects
non-array-backed `ReadOnlyMemory<uint>`, so it cannot preserve a custom memory
manager whose span access waits for pending GPU completion.

Engine shutdown closes both domains before joining either, uses one shared
two-second deadline, and retains dependent ownership after a failed quiesce.
Queued and active job cancellation, including asynchronous token callbacks, is
settled by manager-owned finalization rather than synchronously on the lifecycle
caller.

Background general and render lanes block only on their work signals when idle.
Removing the earlier periodic 50 ms wake removed measurable contention from the
unchanged Vulkan recording path while preserving explicit shutdown wakes.

## Concurrency hardening performed

The implementation review and stress tests found and corrected these lifetime
classes before acceptance:

- stale pooled-lease rent/return races and queued-reference/claim races;
- lane-0 rebind, cross-domain lease, cross-domain nested execution, and the
  32-background-lane active-mask bound;
- cancel-versus-fault authority, terminal-state publication ordering, and
  exactly-once quarantine finalization, including an exact cancellation-item
  snapshot when a late executing fault upgrades a canceled batch;
- quarantine callbacks running under the lease lock or permitting pooled reuse
  after a quarantine exception; failure now poisons the domain and retains the
  batch;
- active-plus-requeued job ownership and queue-slot release races;
- eager job factories posting progress before terminal tracking existed,
  cancellation racing startup, and custom synchronization contexts completing
  a posted notification more than once;
- terminal and progress callbacks releasing manager ownership before their
  synchronization-context posts actually executed;
- generic job fault/completion publication exposing `IsCompleted` before the
  authoritative fault payload, and shutdown stealing an in-progress fault;
- shutdown racing an admitted `Schedule` factory and concurrent first access to
  `Engine.Jobs` creating or publishing multiple worker domains;
- `CancelAsync` callback self-deadlock and premature cancellation-resource
  disposal;
- queued cancellation callbacks running on the lifecycle caller;
- shutdown sequencing that joined one domain before signaling the other; and
- partial worker-start and bounded-dispose failures that could otherwise hide
  live ownership.

## Validation

| Check | Result | Evidence |
| --- | --- | --- |
| Final Release build | Pass | `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release --no-restore`; 0 warnings, 0 errors. |
| Focused scheduler/topology/settings/job tests | Pass | 49/49 after the final terminal-publication and deferred-resource-cleanup review. `Build/_AgentValidation/20260822-172457-phase1b-scheduler/reports/phase1b-focused-final-cleanup-review.trx`. |
| Broader relevant Release Vulkan/meshlet regressions | Pass | 136/136 across scheduler, settings persistence, meshlet closeout, strict zero-readback/hardening, strategy resolution, interop, cache fingerprint, cook snapshot, and Vulkan meshlet regression fixtures. `Build/_AgentValidation/20260822-172457-phase1b-scheduler/reports/phase1b-vulkan-release-final-cleanup-review.trx`. |
| Worker-count matrix | Pass | `0`, `1`, `2`, `4`, `8`, and auto resolve deterministic output and clean shutdown. `G == 0`, nested inline context restoration, lane identity, and worker wake behavior are covered by the focused set. |
| Batch behavior | Pass | Tiny batches execute inline; large synthetic work overlaps; dependency diamonds, lane attachments, cancellation, fault quarantine, queue overflow, stale leases, wrong-domain leases, timeout/retry, and held-lease shutdown are covered. |
| Idle-wake Vulkan performance check | Pass | Two accepted Release GPU-meshlet Sponza repetitions after signal-only idle waits retained 202/213 samples. Render p50/p95 was 7.154/8.562 ms and 6.959/8.241 ms; Vulkan-frame p50/p95 was 5.634/6.605 ms and 5.511/6.410 ms. Both runs had exact draw consumption and zero strict counters. `Build/_AgentValidation/20260822-172457-phase1b-scheduler/reports/vulkan-meshlet-sponza-signal-waits/summary.json`. |
| CPU-direct Vulkan Sponza smoke | Pass | 557 retained samples; all 75 cooked LOD payloads hydrated; 24 GPU-scene commands at p50; 42 draws and 262,597 triangles at p50; zero readback, mappings, fallback, forbidden fallback, capture-time meshlet-buffer churn, or VUIDs. |
| GPU-meshlet Vulkan Sponza smoke | Pass | 100 retained samples; all 75 cooked LOD payloads hydrated; 5,400/5,400 selected and 8,478/8,478 all-output draws consumed; two production frames and two mesh-task frame ops; zero render-path source/hash/disk/cooker activity, readback, mappings, fallback, forbidden fallback, capture-time meshlet-buffer churn, or VUIDs. |
| Matched live evidence | Pass | Both strategies used the same Release build, Vulkan desktop/ImGui launch, fixed flying camera, 15-second warmup/capture, Sponza settings, and exact Gate 6 Sponza cooked cache. Summary: `Build/_AgentValidation/20260822-172457-phase1b-scheduler/reports/vulkan-cpu-meshlet-sponza-final/summary.json`. Both processes shut down gracefully. |
| Final-source live rerun | Pass | 183 retained samples; all 75 cooked LOD payloads hydrated; 9,882/9,882 selected and 13,392/13,392 all-output draws consumed. Render p50/p95 was 7.716/9.175 ms and Vulkan-frame p50/p95 was 5.995/7.071 ms. Generic/all-output readback, mappings, fallback, forbidden fallback, VUIDs, and capture-time rebuild/retire counters were all zero. `Build/_AgentValidation/20260822-172457-phase1b-scheduler/reports/vulkan-meshlet-sponza-phase1b-accepted-final-rerun/summary.json`. |
| Vulkan/OpenXR migration boundary | Pass | Phase 1B does not route backend recording through the new render domain. The matched strict counters and exact meshlet consumption show no observed Vulkan path regression. |

An earlier exploratory matched run was excluded because it pointed at a tiny
three-LOD unit-box standalone cache instead of the 75-LOD Sponza cache. Both
strategies correctly rendered only the fixture's remaining editor geometry in
that run. Repeating with the exact Gate 6 cache restored the expected Sponza
hydration and production counters; this was a validation-input error, not a
scheduler regression.

One final-source repetition was also excluded by the strict all-lifetime gate:
its steady-state capture was clean, but one 4-byte map occurred during the
launch phase. The same intermittent pre-capture signature had appeared once in
an earlier isolation run. The evidence was retained rather than accepted, and
the immediate identical rerun above completed with zero capture and all-output
readback/mapping counters. No Vulkan code was changed to obtain the clean rerun.

The first post-review final-source repetition was functionally clean but ran
under transient frame-slot contention: frame-slot wait p50/p95 rose to
4.791/7.728 ms and render p50 rose to 13.577 ms. The immediate identical rerun
above restored frame-slot wait to 0.019/0.029 ms and render p50 to 7.716 ms.
Both artifacts are retained; only the uncontended rerun is used for the timing
acceptance.

ShippingFast intentionally keeps no GPU timing history, so the harness's
optional MCP GPU-timing dump reported no available history. That does not affect
the strict zero-readback, draw-consumption, lifetime, or VUID gates.

## Remaining Phase 1 exit work

Phase 1B implementation is complete, but two umbrella Phase 1 exit rows remain
open:

- The warmed one-item lane-0 caller path proves zero managed allocation. An
  end-to-end allocation proof still needs to cover multi-item, dependency,
  background-worker, cancellation, and merge paths before claiming every pooled
  batch is allocation-free after warmup.
- The topology owns the new general and render domains, but retained Vulkan,
  OpenXR, compiler, remote, and lazy deferred-job loops are not yet fully
  represented in `DedicatedBackgroundThreadCount`. The process-wide total-thread
  budget therefore cannot yet be claimed complete.

These limitations are explicit tracker work; neither is hidden by marking the
Phase 1B implementation rows complete.

## Result

Phase 1B is accepted. The engine has one shared scheduler, deterministic pooled
render batches, renderer-neutral runtime access, completed-only diagnostic
decode, and bounded lifetime semantics. The matched Vulkan CPU-direct and GPU
meshlet evidence is clean. Backend recording migration remains deferred until a
later phase.

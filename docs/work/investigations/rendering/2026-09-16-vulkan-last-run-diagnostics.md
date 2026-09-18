# Vulkan Last-Run Diagnostics

## Report

The September 16 run in
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-16_19-34-19_pid7888/`
recorded repeated missing accepted-plan picking authority, missing Advanced TSR
snapshots, and a probe generation unavailable during texture-array assembly after
entering Play mode. It also logged 240 attachment-metadata warnings and 590
unsupported GPU BVH warnings.

## Findings And Changes

- Deferrable background redraws do not own a desktop accepted plan. They may
  record visibility without publishing accepted-plan picking state. PresentNow
  recording still requires that authority; XR retains its existing exception.
- Deferred Advanced TAA/TSR bindings must read the pipeline-owned immutable
  snapshot, not resolve a new temporal key from a cleared ambient viewport.
- Probe readiness must include an active output generation, not only restored
  texture properties. A generation that becomes unavailable before retention
  defers the refresh, releases temporary references, and preserves the last
  publication with a diagnostic rather than using an exception for normal retry.
- The BVH dispatcher logged unsupported-backend warnings even with an empty
  queue. Idle processing now returns before that check. Actual unsupported
  geometric GPU raycast requests remain rejected; this does not implement Vulkan
  geometric BVH raycasts or substitute CPU results.
- Depth-only framebuffer targets legitimately lack optional stencil attachment
  usage. Planner validation now follows the backend's optional-stencil contract.
  Missing framebuffers, textures, depth, and color remain diagnostic failures.
- Atmosphere and fog quad materials have no source color attachments. Their four
  stages now declare actual sampled textures and output textures explicitly.
- Advanced editor picking now releases the shared in-flight gate on success and
  rejection, including any pending follow-up pick.
- Local player camera diagnostics distinguish an absent pawn from an unnamed
  component, using the scene-node name where available.

DLL-loading/symbol messages and the audio/pawn lifecycle events are normal.
The reported cancellation has no saved stack trace; it is not evidence of a
failure. First-chance logging remains enabled and throttled rather than globally
suppressed.

## Validation

- Focused rendering and Vulkan builds after each change: no errors reported.
- Two isolated full editor builds passed with zero warnings and zero errors.
- The first isolated Vulkan run exposed a separate Advanced atmosphere/fog
  command chain that still lacked resource descriptors. That chain was then
  corrected and rebuilt. The second run's inspected logs contained no matching
  attachment-planner or idle BVH warnings.
- Isolated viewport captures used FXAA, not TSR. They do not validate temporal
  quality or performance. Full runtime verification remains incomplete.
- Required runtime checks: Advanced Vulkan rendering and TSR history, repeated
  redraws and picking, Play entry/exit with probe recapture, attachment metadata,
  and no idle BVH warning spam. Inspect screenshots and session logs.
- No tests added or modified. Live feature validation precedes any test work.
- User confirmation: negative. On September 17 the user reported worse CPU
  framerate and TSR ghosting. Do not treat the temporal change as validated.

## September 17 Regression Report

The newer run is
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-17_09-34-06_pid7452/`.

- Its `log_rendering.log` FPS overlay samples show approximately 14-15 Hz and
  repeated CPU command-recording stages around 153-165 ms. Successful GPU timing
  samples in the same interval are approximately 1.1-1.5 ms; zero GPU samples on
  long CPU frames must not be interpreted as zero GPU cost. The CPU cause is not
  yet attributed to a specific source change.
- Its `log_vulkan.log` presents `TsrOutputTexture`, and the inspected logs no
  longer contain the old missing temporal-snapshot warnings. The changed lookup
  can enable history blending that the prior failure path disabled. This makes
  history frame/view identity and reprojection the leading ghosting hypothesis,
  not a confirmed root cause. Quiet diagnostics are not proof of correct history.
- Releasing the Advanced picking gate restores repeated requests, but the
  dispatch code still gates stationary cursors and throttles ordinary hover
  requests. An idle runaway pick loop has not been demonstrated.
- The coordinator stopped only its named isolated editor when the regression
  was reported. Concurrent editor load is a possible confounder, not an
  established explanation for this run's recording stalls.
- Next discriminating checks are a controlled temporal-lookup A/B with identical
  scene, camera motion, and settings, plus a CPU profile of a long recording
  frame. Do not reduce history feedback, disable picking, or suppress warnings
  merely to conceal these regressions.

## Evidence

New captures and reports belong under
`Build/_AgentValidation/20260916-210000-last-run-fixes/`.
The named isolated session is `last-run-fixes-0916`.

## September 17 Focused Profiler Probe

The isolated desktop Vulkan session `resume-log-debug-0917` enabled the existing
`EnableProfilerLogging` unit-test switch for one run, then restored it to false.
The run built successfully and reached MCP readiness. Its profiler evidence did
not reproduce a TSR history invalidation pattern: the Vulkan log contained no
matching `MissingSnapshot`, `History`, or TSR invalidation diagnostics in the
captured window.

The sampled paths identify candidates, not exclusive method durations:

- An FPS drop reported a 92.077 ms update-iteration root whose hottest-child
  path ended at `XREngine.RuntimeWorld.Update`. The thread's total sampled work
  was 93.061 ms, with no reported downstream render pressure.
- A completed render root reported 1,285.837 ms and a hottest-child path ending
  in `XRMesh Constructor > InitMeshBuffers`. This does not measure 1,285.837 ms
  of mesh buffer initialization: `Engine.CodeProfiler.GetHottestPath` retains
  the root duration while descending through child names.
- A later live stall sample reported 569.533 ms for the enclosing render frame
  and 550.670 ms elapsed in the currently active `Advanced.VisibilityPreparation`
  scope, while one render job remained queued. This is an in-progress inclusive
  scope measurement, not a completed duration or GPU timing.

This does not establish the steady-state 14-15 Hz cause or rule out the current
TSR lookup. Missing diagnostics alone cannot validate temporal image quality.
Startup preparation must be separated from steady-state frame timing, and
completed child/self timings must establish attribution before a fix is chosen.
The next run should warm the scene before measuring, then capture a CPU frame
profile during a repeated 150 ms frame if the regression remains.

## September 17 Stall-Fix Research

Status: research complete; causes and proposed fixes still require runtime
validation. No runtime code or tests were changed during this research. The
original CPU regression and reported TSR ghosting remain open.

Execution is tracked in the [Vulkan Stall Remediation TODO](../../todo/rendering/vulkan-stall-remediation-todo.md),
with one fix active at a time and a mandatory validation gate before advancing.

### Evidence Boundaries

The resumed probe's logs are under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-105921-resume-log-debug-0917/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-17_11-01-53_pid1900/`.
This session was stopped and its profiler setting restored. Its isolated editor
build passed with zero warnings and zero errors. The original September 16 and
September 17 ordinary-run log directories are not available in this checkout;
their earlier findings above cannot be independently reanalyzed here.

The full probe has seven 500-ms-threshold stall detections:

| Detection | Active render frame ms | Active leaf ms | Active scope | Recovery no-completed-render interval ms |
| --- | ---: | ---: | --- | ---: |
| 11:02:06.885 | 261.773 | 13.865 | `VPRC.VPRC_BindOutputFBO` | 959.430 |
| 11:02:07.921 | 569.533 | 550.670 | `Advanced.VisibilityPreparation` | 2540.405 |
| 11:02:10.552 | 503.605 | 109.272 | `UI.ComponentEditor.CameraComponent.Settings` | 904.405 |
| 11:02:12.979 | 3.242 | 0.428 | `MainThreadJobs.Normal.Invoke:VkDataBuffer.Upload:MeshAtlas_Dynamic_UV0` | 4309.093 |
| 11:02:15.831 | 424.024 | 61.120 | `Vulkan.FrameLifecycle.RecordCommandBuffer` | 656.271 |
| 11:02:17.145 | 438.305 | 52.559 | `Vulkan.RecordPrimary.MainOpLoop` | 545.659 |
| 11:02:38.927 | 493.309 | 486.296 | `UI.DrawToolbar` | 849.620 |

These are wall-clock observations of active scopes, not completed exclusive CPU
costs. In particular, the 0.428-ms upload observation cannot explain the preceding
4.3-second interval. A long interval may span work before the observed scope,
waiting, GC suspension, or descheduling. The watchdog's last recovery is
11:02:39.276, but FPS-drop logging continues until 11:05:46.981.

Warmed overlay samples from 11:03:15.193 through 11:05:45.043 show a different
performance regime from the original regression report:

| Metric | Observed warmed range |
| --- | --- |
| CPU command recording (`rec`) | 27.1-42.6 ms |
| GPU command-buffer timing | 17.81-24.85 ms |
| Logged output rate | 15-20 Hz |
| Render interval | 47.87-70.55 ms |

There are 69 overlay samples in that window. They are sparse observations, not
per-frame percentiles. At 11:05:45.043, recording is 32.2 ms, resource preparation
8.5 ms, GPU time 20.38 ms, and output 18 Hz. Acquire/present are each 0.1 ms and
submission 0.5 ms in that sample. CPU/GPU intervals overlap and must not be summed
as independent frame costs. The overlay's update/fixed values are loop intervals,
not measured callback execution costs.

Across 136 warmed FPS-drop events, the selected endpoint is `MainOpLoop` 86 times,
`WaitForRender` 46 times, and `RuntimeWorld.Update` four times. The largest warmed
render-thread root is 67.518 ms at 11:04:53.230. Its descendant label is
`MainOpLoop`, but that number remains root-inclusive. Suppressed events and sparse
overlay logging prevent excluding every unsampled spike. This probe does not
establish a reproduction of repeated 153-165 ms recording, nor a fix for it.

The Vulkan log repeatedly presents `TsrOutputTexture` through the end of this
probe. Unlike the earlier FXAA captures, this run did use the TSR presentation
path. It still supplies no camera-motion image comparison or proof of correct
history contents, view identity, jitter, or reprojection.

### 1. Correct Attribution Before Optimization

Confirmed source issue: [GetHottestPath](../../../../XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.CodeProfiler.cs#L1551)
sets the reported duration from the hottest root and then descends through child
names without replacing that duration. Therefore neither the 1,285.837-ms mesh
endpoint nor the 92.077-ms update endpoint establishes the named method's cost.

Proposed change: expose root-inclusive, selected-child-inclusive, and self-time
separately, with matching scope identity/kind and frame timestamps. Self-time is
still wall-clock time, not automatically on-CPU execution. Do not subtract
overlapping asynchronous spans as if they were nested synchronous children.
Record queue delay, explicit waits, and CPU execution separately where possible.

First measurement: capture a complete warmed CPU frame and a managed wait/GC
trace while the slowdown occurs. Add bounded, allocation-free timings around
primary-operation preparation/dispatch, per-operation kind, source validation,
descriptor preparation, native pipeline creation, compile-mutation drains, and
cache-lock acquisition. Include counts, bytes, generation and cache-hit identity.
This will distinguish repeated work from cold admission and unclassified waits.

Disconfirming check: if a long recording interval contains little on-CPU work but
matches a wait, GC pause, or descheduling interval, algorithmic recording changes
are not the first fix. `WaitForRender` is downstream pressure, not proof of an
expensive collect/update method. Compare logging-enabled and minimally instrumented
runs before treating this Debug/profiler probe as a production baseline.

### 2. Make Pipeline Readiness Nonblocking

Confirmed chain: [GetAdvancedVisibilityPipelineReadiness](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.AdvancedPipelineCapabilities.cs#L407)
enters a synchronous program-preparation scope and checks early, late, native,
and raster shader families. [TryGetComputePipelines](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Advanced/VulkanAdvancedVisibilityPipelineRuntime.cs#L31)
creates wrappers and calls `Link(allowAsyncShaderCompile: false)` before requesting
compute pipelines. [Program linking](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Linking.cs#L23)
also forces synchronous behavior inside that preparation scope. Current links do
have a fast path; this does not prove relinking every frame.

Proposed fix: prepare the required family before exact frame admission, using
existing shader and pipeline queues. Readiness checks should observe
Missing/Pending/Ready/Failed, not compile or wait. Publish only a complete,
compatible family. A pending source compile must not become a permanent failure.
Changing the boolean alone is insufficient because the enclosing scope overrides
it. Preserve explicit diagnostics for failed or unavailable requested rendering.

[TryCompleteComputePipelineForForeground](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs#L175)
promotes a queued compile and waits for its task. Normal frame admission should
retain Pending instead of calling that blocking completion path. Required new
content cannot simply be omitted from a supposedly successful accepted frame;
keep the previous compatible publication only where its contract permits it,
otherwise report pending/loading until a valid generation is ready.

Disconfirming check: correlate first-family readiness with source compilation,
native compile, and foreground-wait durations. If warmed slow frames perform no
link/compile/wait, this fix addresses startup/reload only, not their steady cost.

### 3. Localize Compile Invalidation And Cache Publication

Confirmed mechanism: non-current program links acquire the compile mutation
lease. The queue's [mutation acquisition and drain](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs#L636)
advance a global generation, clear completion caches, and synchronously drain
outstanding graphics and compute jobs, including publication tasks. Creating one
cold program can therefore encounter unrelated queued work. This is a credible
blocking mechanism, not measured attribution for the visibility observation.

Proposed fix: distinguish additive program creation from replacing/destroying
dependencies. Invalidate only affected owner/layout/device generations, retain
immutable compilation dependencies, reject stale completions, and retire native
objects only after both compiler and GPU users finish. Do not merely delete waits:
a running native compilation cannot be cancelled by abandoning its managed task.

The engine already implements [persistent foreground and isolated background caches](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs#L13),
prewarm database initialization, background cache merging, and graphics-library
creation. Adding another generic cache or compiler queue is not the missing fix.
The existing graphics cache-only probe is restricted to `backgroundCompile`;
foreground native creation can still compile. For supported devices, consider
cache-only foreground probes that queue misses and return Pending, without an
immediate blocking retry. This is not a guaranteed latency bound for driver calls.

Cache merge takes foreground and background host locks; cache capture allocates
and reads native cache data under the foreground lock. Measure these separately
before changing scheduling. Batch/defer publication or capture only if contention
is observed, retaining merge/destruction synchronization. The source's claim
that Vulkan requires external synchronization for every cache access is broader
than the specification: ordinary creation caches are internally synchronized;
the external-synchronization flag and merge/destroy operations need separate
handling. That distinction does not authorize removing engine lifetime locks.

Disconfirming check: track mutation reason, affected owners, drained-job count,
wait duration, cache merge/capture duration, and stale-result counts. If no global
mutation or cache contention occurs during a stall, pursue another owner. Do not
increase compile workers blindly: the single low-priority default explicitly
accounts for possible NVIDIA compiler/queue-submission contention.

### 4. Budget Initial Resource Materialization

Confirmed mechanism: [initial resource materialization](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs#L1810)
uses `TimeSpan.MaxValue` and `int.MaxValue` when `ActiveGeneration` is null. The
first generation bypasses incremental time/spec limits.

Proposed fix: prepare during an explicit loading phase, or apply an initial-frame
budget while keeping incomplete generations unpublished. Split individually slow
resource factories into CPU preparation and bounded renderer-owned publication;
inter-spec time checks cannot bound one expensive factory. Preserve stale-key
rejection and transactional commit. Do not run whole factories on `Task.Run`:
they use installed build/view state and renderer-affine operations.

Disconfirming check: measure materialization by spec/factory, identifying initial
versus replacement generations. If generation preparation has completed before
the warmed recording interval, it cannot explain recurring recording costs.

### 5. Separate Mesh Data Preparation From Wrapper Publication

[InitMeshBuffers](../../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.BufferInit.cs#L5)
constructs buffers, allocates/initializes backing storage, and publishes buffer
dictionary entries. [GenericRenderObject](../../../../XREngine.Runtime.Rendering/RenderObjects/GenericRenderObject.cs#L95)
can eagerly create API wrappers unless suppression is active. These are distinct
potential costs; the existing root timing does not distinguish them.

Proposed fixes, conditional on finer timings:

- Reuse the importer pattern of CPU-only preparation with wrapper suppression,
  followed by renderer/scene-owned publication. Suppression alone is not a
  lifetime boundary: do not expose partially initialized objects to discovery.
- Share immutable fullscreen/debug/unit-light geometry where ownership permits.
  Keep materials and renderer state separate and define shared disposal. A later
  procedural fullscreen draw could avoid dummy meshes, but needs explicit
  topology/count and compatible custom-shader, stereo, OpenGL and Vulkan behavior.
- Schedule [index preparation](../../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Geometry.cs#L358)
  before draw admission so `GetIndexBuffer` need not synchronously join its ticket.
  Retain source revisions and worker-safe immutable topology.

Disconfirming check: measure allocation/zero fill, wrapper-lock wait, dictionary
callbacks, vertex population and index joins separately, with vertex/byte counts.
Tiny helper geometry would favor wrapper/JIT/lock investigation over buffer-size
optimization. Meshlets and optional BVH generation are separate paths, not proven
costs of this constructor. Avoid reintroducing nested `Parallel.For` into vertex
population; that path previously removed it to avoid ThreadPool starvation.

### 6. Reduce Repeated Advanced Preparation Only With Freshness Proof

[AdvancedSharedPreparationService](../../../../XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedSharedPreparationService.cs#L45)
holds one lock across its frame/scene cache check, extraction, deformation
execution and publication. `TryCopyVisibilityInputs` uses the same lock and copies
five columns plus deformation data into consumer-owned storage. This deliberately
prevents deferred consumers from observing mutable extractor spans.

Proposed fix: pre-size cold storage, measure lock wait separately from extraction,
and publish retained immutable snapshots so expensive construction can eventually
leave the shared critical section. Reuse storage only after its consumers retire.
Consider per-world publications only if alternating worlds actually thrash this
single cached publication. Never expose mutable spans to eliminate copies.

For steady-state primary recording, measure command scans, source metadata
validation, compute-interface fingerprinting, and descriptor/texture readiness.
Cache by actual mutation generations where the cost is material. Preserve shader
configuration, source content/metadata/sampler epochs, device/layout identity and
accepted output/view authority; dropping a frame ID without an equivalent
freshness contract is unsafe.

Existing resident-table reuse and matching family-lease reuse already avoid some
rebuilds/copies. A full scene-table rebuild on every stage has not been established.
Canonical raster PSO creation and required synchronous texture uploads are further
cold-admission candidates, but their downstream primary-preparation work must not
automatically be attributed to the observed visibility-stage label. Reuse upload
queues and generation ledgers; publish descriptors only after transfer readiness.

Disconfirming check: stable-scene dirty counts and copied bytes should explain
cost scaling. If extraction is short and uncontended, or repeated source checks
are negligible, prioritize the measured primary-operation owner instead.

### 7. Profile The Actual Core Update Dispatcher

The logged world callback reaches [RuntimeWorldLifecycle.TickGroup](../../../../XREngine.Runtime.Core/World/RuntimeWorldLifecycle.cs#L59),
not the legacy tick list with component profiling. Its private queue invokes
callbacks without inner timing. `ApplyPending` drains until empty and uses linear
`Contains`/`Remove`, making bulk registration a potential quadratic workload.
The XREvent listener index `[3]` is not a world identifier.

Proposed fix: first record Normal/Late group, tick order, callback identity,
pending-change count and drain duration. If registration dominates, preserve
ordered dispatch while adding efficient membership lookup and a defined batch
boundary. A cap must not silently delay required activation/deactivation semantics.
Keep snapshot sizing/enumeration coherent under the order lock when modifying it.

Other conditional paths include probe request construction, physics worker joins,
rigid-body interpolation locks, and retained timer catch-up debt. A PhysX scene is
not proof that physics-chain ticks ran. Timer retries are capped within one
dispatch, although debt can persist across outer iterations. Model publication
usually belongs to app-thread job dispatch, so it must not be assigned to world
update without a matching caller. GC/descheduling can inflate every active scope.

Disconfirming check: a warmed callback trace showing sub-millisecond update work
or a simultaneous global pause rejects world-tick optimization as the primary
render-stall remedy. Do not infer an expensive callback from root time alone.

### 8. Remove Cold UI Work From Drawing

Confirmed source opportunities, not proven explanations of the UI observations:

- [Toolbar icon loading](../../../../XREngine.Editor/IMGUI/EditorImGuiUI.Icons.cs#L41)
  delays initialization, then performs file access, SVG parsing and Skia
  rasterization synchronously. One icon per frame limits count, not duration.
  Prepare pixels before use or on a bounded worker, then publish/upload through
  the existing renderer-owned budget. Do not move the current whole function,
  including texture construction and shared counters, onto arbitrary workers.
- [Camera settings](../../../../XREngine.Editor/ComponentEditors/CameraComponentEditor.cs#L343)
  draw asset fields whose create/replace availability can trigger assembly-wide
  type discovery on a cache miss. Defer discovery until requested, or precompute
  immutable descriptors keyed to assembly/script generation.
- Pipeline-setting property lists are cached, but enum arrays and attribute/
  tooltip metadata still have recurring work. Cache complete immutable descriptors
  and enum labels; check hover before computing tooltip metadata. Invalidate on
  script reload without retaining collectible assemblies indefinitely.

Disconfirming check: time first-use discovery/rasterization, texture upload and
warmed drawing separately. If all icon/type caches were already ready during the
stall, cold UI preparation does not explain it; inspect native waits, JIT and GC.

### External Guidance And Applicability

Sources consulted during this research:

1. [Khronos pipeline management sample](https://docs.vulkan.org/samples/latest/samples/performance/pipeline_cache/README.html):
  prepare known pipelines early and persist native caches. Applied here as an
  admission/scheduling improvement because caches already exist. The sample also
  warns that RenderDoc shows empty pipeline-cache fields during capture/replay;
  that alone is not evidence the application failed to use a cache.
2. [Vulkan pipeline specification](https://docs.vulkan.org/spec/latest/chapters/pipelines.html):
  cache-only creation returns `VK_PIPELINE_COMPILE_REQUIRED` when compilation is
  necessary and requires enabled `pipelineCreationCacheControl`. Optional pipeline
  creation feedback can separate native duration and application-cache hits;
  interpret it only when VALID is set. Driver cache reuse does not prove a live
  pipeline handle is cached. Graphics pipeline libraries/binaries are later,
  feature-gated options, not substitutes for removing foreground joins.
3. [Microsoft ThreadPool starvation diagnostics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation):
  use queue/thread/completion counters and .NET 9+ `WaitHandleWait` events to find
  blocking callers. Dedicated render-thread waits are important here but are not
  themselves evidence of ThreadPool starvation. Raising minimum worker count is
  not a substitute for removing sync-over-async dependencies.
4. [Microsoft dotnet-trace reference](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)
  and [EventPipe scope](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe):
  managed stack sampling is not on-CPU attribution. Current tools renamed the
  misleading `cpu-sampling` collect profile to `dotnet-sampled-thread-time`.
  Inspect installed version/profile support before selecting commands. Collect
  managed wait, contention, GC, JIT and thread events; use Windows ETW/PerfView or
  WPR/WPA for native compiler stacks and scheduler/context-switch evidence.
  Kernel tracing may require operator-run elevation; do not automate UAC prompts.

No ETW/EventPipe trace or new RenderDoc capture was collected for this research.
Official guidance supports the proposed mechanisms, not an engine-specific
speedup claim. This is a targeted stall investigation, not an exhaustive .NET
anti-pattern inventory.

### Recommended Implementation And Validation Order

1. Correct duration/identity reporting and capture one warmed long frame with
  wait/GC evidence. Establish the original regression's configuration and binary
  identity before comparing it with this probe. Prioritize the measured owner
  inside primary recording; do not let startup work stand in for steady-state work.
2. Prewarm complete Advanced shader families and remove foreground readiness
  compilation/joins. Then localize compile invalidation with explicit dependency
  lifetimes. These have the strongest source-backed startup/reload blocking paths.
3. Budget initial resources and move measured cold mesh/UI preparation before
  interactive use. Measure time-to-first-valid-frame as well as frame stalls so
  moving work into loading is not reported as reducing its total cost.
4. Optimize recurring extraction/source validation, resource preparation or world
  ticks only after their child timings establish material cost. Profile the
  warmed 18-25 ms GPU work separately; CPU fixes alone cannot guarantee 60 Hz.

Use identical scene, camera, dimensions, driver, present mode, backend/submission
mode, build configuration and profiler settings for each A/B. Cold means a fresh
process; distinguish persisted-cache warm starts from genuinely cold cache inputs
without deleting user caches. Record external load as a confounder rather than
stopping processes not owned by the investigation.

| Scenario | Required evidence and acceptance |
| --- | --- |
| Fresh process and cache-warm restart | First valid image, preparation/compile timeline, native cache hits, loading time; no incomplete publication |
| Warm stationary scene and controlled camera motion | All-frame CPU/GPU p50/p95/p99/max plus recording child timings, queue latency and successful presents; no recurring unexplained long recording |
| Resize, pipeline switch and shader reload | Stale completion rejection, exact compatible output generation, no unsafe dependency retirement or new global drains |
| Asset/index/texture admission | Upload completion before descriptor use, bounded publication cost, no silently omitted required draws |
| Play entry/exit, probe refresh, repeated picking | Correct view/output authority and gate release; no unavailable-generation exceptions or warning spam |
| First and warmed camera panels/toolbars | Separate discovery/rasterization/upload/allocation timings; unchanged visible functionality |
| TSR stillness, motion, disocclusion and camera cut | Viewed screenshots/sequence, history/view/frame IDs, jitter/velocity/depth correctness; no feedback reduction used to hide ghosting |

For each implementation slice, build the owning project and validate through a
named isolated editor session. Keep new evidence in one bounded agent run root;
stop only that session and record results here. Follow repository sequencing:
no new or modified tests for this regression until the user explicitly clears
test work after feature validation. User confirmation of any proposed fix is
still pending.

## Gate Records

### S00 Gate Record: Comparable Baseline And Evidence Manifest

Review disposition: **Validated at current HEAD**. The historical records below
remain evidence of earlier failed attempts. The superseding revision `a1f9408e5`
matrix clears observer overhead, GPU coverage, loss accounting, settled retention,
native occupancy, and required-backlog gates. S01 may proceed. The separately
confirmed periodic command-recording hitch remains unresolved S02 evidence.

#### S00 Reopened Acceptance Plan

User approved separate ImGui-on diagnostic runs and ImGui-off `CleanProfile`
performance runs on 2026-09-17. This supersedes the single ImGui-on workload
constraint below, not its readiness, sample-count, observer or retention budgets.
Keep profile modes separate in reports. S00a resumes as Active for measurement
validation; S01 remains Blocked. This approval does not clear test work.

S00a instrumentation child: Validated (GPU export build/runtime identity and
symmetric clean-toggle live proof passed; parent S00 performance gate remains
open). The isolated
`s00-baseline-probe-0917`
build passed with zero warnings/errors and confirmed Vulkan/CpuDirect/Advanced
TSR at 1920x1080 (internal 1286x723), desktop Edit mode, 393 published draws and
no Advanced preparation deferral. Its saved image shows loaded Sponza with black
regions/speckling; temporal quality is not validated. The existing capture lacks
coarse GPU sample provenance although the renderer already publishes it. Export
the existing coherent GPU timing snapshot, without new GPU queries, GPU waits or
native lifetime changes. Its getter uses the existing managed snapshot lock.
Require exact runtime/export identity agreement and sequence
deduplication before using GPU distributions. Record/compare capture overhead
under the parent observer budget before S00 passes. The pre-change probe is not
a matched baseline. Evidence root: `Build/_AgentValidation/20260917-140410-s00-s01-completion/`.

- Entry owner: existing `XRE_PROFILE_CAPTURE` completed-frame stream, independent
  of CodeProfiler's tree reconstruction. Hypothesis: it supplies consecutive
  frame IDs, coarse recording/preparation/wait timings and presentation counters
  sufficient to characterize this source state. Reject the collection if IDs,
  sample-loss accounting, required timing fields or scene readiness are absent.
- Preserve all pre-existing S01 edits as the baseline source state. Record the
  immutable revision, full local diff and built assembly hashes before launch.
  This baseline cannot retroactively validate the earlier unrecorded source state.
- Use Debug desktop Vulkan, Advanced/TSR, ImGui, validation disabled, uncapped
  presentation, unchanged imported content and output dimensions. No quality,
  backend or submission fallback is permitted. Use session-local settings and
  caches; do not edit the user's settings or delete user caches.
- Three paired process runs per observer condition: existing completed-frame
  collector with CodeProfiler disabled versus the same collector with CodeProfiler
  enabled. Alternate ordering. Preserve persisted cache inputs between paired
  restarts and identify initial cache population separately from warm restart.
- Each ready stationary and controlled-motion window lasts at least 60 seconds.
  Require completed model publication, stable accepted content/resource generation,
  no pending required preparation and a viewed valid image before warm measurement.
  Record a fixed camera pose and one repeatable motion path before the first window.
- Report per-run count/p50/p95/p99/max for recording, preparation, waits, GPU and
  successful-present interval. Require consecutive completed-frame capture IDs or
  exact explained omissions. Report zero/unready GPU timings and diagnostic losses
  separately; no GPU percentile claim if fewer than 99% of frames have valid data.
- Preserve the prior <0.5 ms/frame observer budget; reject if paired mean overhead
  exceeds it or p95/p99 increase beyond the larger of 5% and the measured baseline
  run-to-run spread. Do not relax these limits after seeing measurements. If noise
  prevents deciding, retain Blocked rather than declaring unchanged overhead.
- For S01 comparisons, recording/preparation/present p95/p99 must not regress beyond
  the larger of 5% and baseline spread; zero new unexplained >=500 ms warm stalls.
  After readiness, managed/private-byte growth must settle within 16 MiB or 5%
  (whichever is larger), native-resource counts within 1%, and required-job/retire
  backlogs return to baseline. Record time-to-first-valid-frame separately.
- The historical <10 ms CPU and <16.6 ms GPU numbers are optimization goals, not
  evidence-based S00 pass thresholds. S00 passes on reproducible characterization,
  not achievement of 60 Hz. Original unavailable logs and TSR ghosting remain open.
- No new or modified tests. Any missing collector capability is a blocker requiring
  a separately validated instrumentation child before optimization or S01 work.

#### S00a And Reopened S00 Results

Disposition: **Blocked**, not Validated. S01 remains Blocked and S02 remains
Pending. No S01 runtime changes or test changes were made during this attempt.

- Source: revision `648fe3fd397e78156ce0494374ff420836cd6068`, with the existing
  uncommitted profiler changes preserved. The evidence root above contains
  `reports/baseline-source.diff`, `reports/baseline-manifest.json`, settings SHA256,
  SDK/runtime/device details and built `XREngine*.dll` hashes. The frozen diff
  SHA256 is `B1A24A338D311AE8F51B9AAE31FF42E368C11EF0A21FFDEA3FCAF9FE47DB1BBB`.
- Immediate `dotnet build XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj
  --no-restore --verbosity quiet -consoleloggerparameters:Summary` returned 0;
  editor diagnostics reported no errors. The named editor was rebuilt before
  live validation; subsequent pair runs used those same binaries with `-NoBuild`.
  The preserved isolated build log reports 0 warnings, 0 errors and 47.12 seconds.
- The new export matched the saved `get_render_profiler_stats` sample exactly:
  source frame 981, sequence 979, image slot 0, elapsed 6,693,216 ns. Evidence:
  `mcp-output/s00a-profiler-stats.json` and `reports/s00a-query-validation.json`.
  The latter's original interior-only coverage calculation is not an accepted
  frame-coverage result. The original probe NDJSON was removed by runtime log
  retention before a final-tail recheck; no final-tail validation is claimed.
- Actual `CodeProfiler.EnableFrameLogging` readback confirmed false/true for
  the two conditions. Overrides were session-only. Coarse GPU timing stayed
  enabled, without dense command timestamps. Managed memory was read without
  forced GC; process private bytes were sampled externally by the owned PID.

Each row below is one captured diagnostic window, not a matched warm performance
claim. Recording durations are milliseconds. GPU counts join unique completed
query source IDs to `vulkan_frame_render_frame_number`, exclude samples belonging
to earlier frames, and count the unavailable final query as missing.

| Condition / window | Seconds | CPU frames | Valid GPU frames | Recording mean / p99 / max |
| --- | ---: | ---: | ---: | ---: |
| Off / stationary | 60.072 | 819 | 818 (99.88%) | 41.365 / 92.699 / 122.561 |
| Off / motion | 60.311 | 218 | 217 (99.54%) | 134.959 / 699.499 / 4641.018 |
| On / stationary | 60.021 | 750 | 749 (99.87%) | 44.745 / 118.437 / 379.536 |
| On / motion | 60.261 | 293 | 292 (99.66%) | 91.891 / 317.910 / 530.554 |

All four captured windows have consecutive row frame IDs, no malformed rows,
no duplicate/nonmonotonic IDs, no contradictory GPU samples, and zero reported
causal-wait dropped entries. This does not prove zero CodeProfiler event loss:
its overflow-discard warning is rate-limited without an exported cumulative loss
counter. Required preparation, queue/wait, native-resource and presentation
distributions remain in the per-window reports; no causal attribution is claimed.
Preserved logs are under `logs/pair-off-session/` and `logs/pair-on-session/`.
The on-process FPS log contains 111 records reporting 490 suppressed drops;
pending suppression at shutdown is not accounted for. Its motion stall log
includes a 512.909 ms no-completed-render interval with a 91.960 ms active leaf,
illustrating why those durations must not be conflated. Startup audio warnings
report already-current settings in both conditions, not a new GPU failure.

Rejected or incomplete gates:

- Only one pair was collected, not the required three. Every row reports
  `IntrusiveDiagnostics` and `profile_comparison_suitable=false`. The existing
  `CleanProfile` contract disables ImGui, conflicting with the recorded ImGui-on
  workload. Approve separate clean performance and ImGui diagnostic conditions,
  or explicitly approve another measurement protocol before rerunning.
- Readiness failed retrospectively: stationary content generation changed
  500 to 515 with profiling off and 479 to 507 with profiling on; the latter
  reached 510 after motion. A valid publication and 393 draws were insufficient.
  Establish the requested condition and pose first, then require stable accepted
  content, settled required jobs and a viewed image before timing.
- The stationary mean render-dispatch difference was +5.109 ms (on minus off),
  above the 0.5 ms budget. Motion favored the opposite condition. Neither is
  attributable observer cost with changing content and only one noisy pair.
  The shared collector's own overhead and the added snapshot lock remain unmeasured.
- The 65-second camera interpolation began before capture attachment. Although
  images and pose readbacks confirm movement, the two windows lack a common
  motion-start boundary. Arm capture first and retain verified pose progression.
- Memory samples are not a settled-retention proof. Off managed bytes grew from
  2,055,776,872 to 2,214,282,032 during stationary work, then fell to 2,046,041,112;
  private bytes grew from 5,601,378,304 to 5,856,063,488 across both windows.
  Add comparable quiescent endpoints and evaluate native resources and backlogs.
- Stationary and moved-camera PNGs were viewed. Loaded Sponza still has black
  regions/speckling and varying yellow selection outlines. Do not claim temporal
  correctness or reproduction/resolution of the unavailable original recording.
- Scratch automation is exploratory, not an acceptance gate: it still needs
  readiness, capture/motion synchronization, completion-tail retention, complete
  paired-budget evaluation and exact diagnostic-loss accounting before reuse.

The owned `s00-baseline-probe-0917` session was confirmed Stopped after both runs.
No user editor process was stopped. Test clearance remains absent. User feedback
on these attempts has not yet been received.
The original settings SHA256 remains
`2E794B8347C31C641DA2CA72B2DB5447B14D38DF0F91D6ABD0A45DDF5C83F0C9`.

#### Clean Profiler Toggle Completion

Work resumed from revision `034a577a0` on 2026-09-17. All named S00 editor
sessions are Stopped; no editor PID owned by this work remains active.

Implemented and live-validated end to end:

- `XRE_PROFILE_CODE_PROFILER` is a dedicated launch-capture override; it does not
  reuse `XRE_PROFILER_ENABLED`, whose existing meaning is profiler transport.
- `Tools/Measure-GameLoopRenderPipeline.ps1` accepts `-CodeProfiler Enabled|Disabled`,
  propagates the override and records the requested condition in its result.
- Each NDJSON row exports `code_profiler_frame_logging_enabled` from the actual
  `Engine.Profiler.EnableFrameLogging` state, not the requested environment value.
- The editor applies the capture override after `UnitTest_Init` in the
  `BeforeCreateWindows` callback. This ordering is required because the unit-test
  settings file explicitly sets `EnableProfilerLogging=false` and previously
  overwrote the requested on state. The override also assigns the runtime
  profiler directly so an equal effective preference value cannot suppress the
  setter side effect.

Validation completed for this child:

- The owning Bootstrap and Editor builds passed after the toggle and ordering
  changes; current editor diagnostics report no errors in the touched C# files.
- A short ImGui-off `CleanProfile` capture with the profiler requested off
  reported `CleanComparison`, Vulkan/CpuDirect, 3,180 consecutive frame rows and
  actual `code_profiler_frame_logging_enabled=false`.
- Before the final ordering fix, an on probe still reported false. This rejected
  the first implementation and led to moving the override after `UnitTest_Init`.
  Do not cite that run as profiler-on evidence.
- The fresh isolated `s00-clean-codeprofiler-toggle-probe-v3` build was interrupted
  before completion and produced no runtime logs. It was explicitly stopped and
  is not validation evidence.

The final symmetric proof reused one freshly built isolated executable:

- Executable SHA256:
  `3E77E3C1D984CF16AFCE67C693975CFB305885AEFE8BF91F649E417612AF2E3A`.
- Build session: `s00a-toggle-proof-0917`; build completed with zero warnings and
  zero errors. Both launches used the exact same executable without rebuilding.
- Profiler on: 25 retained rows from 30 total rows. Every retained row reports
  `profile_suitability=CleanComparison` and
  `code_profiler_frame_logging_enabled=true`; rows are well-formed and render
  frame IDs are strictly monotonic from 30 through 270 at the configured
  10-frame sampling cadence.
- Profiler off: 24 retained rows from 30 total rows. Every retained row reports
  `profile_suitability=CleanComparison` and
  `code_profiler_frame_logging_enabled=false`; rows are well-formed and render
  frame IDs are strictly monotonic from 20 through 250 at the same cadence.
- The first immediate five-second attempt retained zero rows because the first
  completed Vulkan frame arrived after the capture window. It is rejected setup
  evidence. Both accepted directions used a ten-second warmup and five-second
  capture window.
- Evidence root:
  `Build/_AgentValidation/20260917-183200-s00a-toggle-proof/`. The accepted
  summaries are under `on-v2/` and `off/`; runtime NDJSON paths are preserved in
  their `run-logdirs.txt` and `summary.json` manifests.

S00a is Validated. Parent S00 remains Blocked for the reasons recorded below.

#### Strict Release Matrix And S00 Decision

The strict matrix under
`Build/_AgentValidation/20260918-014700-s00-readiness/release-profiler-matrix/`
completed three alternating disabled/enabled pairs in `D/E/D/E/D/E` order. Each
process used Release `CleanProfile`, a 25-second warmup, a 60-second stationary
window, and a 60-second controlled-motion window. The collector reapplied the
fixed camera after asynchronous import, verified the exact translated endpoint,
required stable publication/topology, and rejected visually empty screenshots.

The frozen manifest records revision `034a577a072b16d559b6e4ff84e73f7057e39190`,
tracked-diff object `ce3ed9005da16ff09f9f422b241255419f16fc22`, and
tracked-diff SHA256
`42ADED7304CCFE97D9BA96373508B321629394CF4BC61737677AE454BDF941A5`.
The executable SHA256 is
`3E77E3C1D984CF16AFCE67C693975CFB305885AEFE8BF91F649E417612AF2E3A`.
The Rendering and Vulkan DLL SHA256 values are respectively
`10BA05BF2146FFCA26435C092FE23C2C235E08FCD2ED71DC060FD24DD3D4F4B8`
and `8181764205B022F88159AD5588C92E6BC5D056D1C5ABEDF31DF3563F96BC9BC4`.
All six admitted runs retained workload identity `10991459253885323059`, 105
GPU-scene commands, camera/motion verification, at least 42 sampled color buckets,
and zero retained Vulkan submission rejections or failed-frame samples.

Median render results across the three runs per condition are:

| Window / metric | Profiler off ms | Profiler on ms | Delta |
| --- | ---: | ---: | ---: |
| Stationary mean | 10.210 | 10.638 | +0.428 (+4.19%) |
| Stationary p95 | 12.596 | 13.230 | +5.03% |
| Stationary p99 | 16.005 | 18.081 | +12.97% |
| Motion mean | 10.323 | 10.590 | +0.267 (+2.59%) |
| Motion p95 | 12.493 | 13.323 | +6.64% |
| Motion p99 | 15.419 | 17.709 | +14.85% |

Both mean deltas remain below the predeclared 0.5 ms/frame budget. Stationary
p95 is below its 15.47% disabled-run spread allowance, while motion p95 and p99
are below their 10.67% and 37.07% allowances. Stationary p99 exceeds its 6.22%
allowance and independently fails the observer gate. Median stage deltas were
small but broad rather than isolated: stationary `RenderOutsideVulkan` +7.45%,
`CollectVisible` +5.95%, `CollectWaitForRender` +3.72%, `VulkanFrame` +3.51%,
`VulkanRecordCommandBuffer` +4.35%, `PrimaryCommandEncoding` +3.14%, `Submit`
+0.94%, and `QueuePresent` +7.69%; motion deltas were +7.36%, +2.08%, +2.65%,
+1.41%, +3.54%, +2.09%, +3.78%, and -1.08% in the same order.

The severe reported regression was not reproduced. Current accepted windows
render near 10-11 ms on average, rather than the earlier sparse 27-43 ms probe or
the unavailable original 153-165 ms report. This characterizes the current source
state; it does not prove the original CPU regression fixed or identify its cause.

S00 remains **Blocked** because:

- Every run reports `GpuSamples=0` and `GpuReadySamples=0`, giving 0% coverage
  against the required 99%. No GPU percentile or GPU-regression conclusion is
  admissible from this matrix.
- Stationary p99 exceeds the predeclared profiler observer allowance.
- The capture does not provide complete cumulative diagnostic-loss accounting or
  comparable settled managed/private-memory endpoints, native-resource counts,
  and required-job/retire backlog closure for the retention gate.

Do not rerun the same matrix unchanged. First repair and live-validate the missing
coarse GPU sample export and bounded loss/retention evidence as measurement work,
then repeat the affected comparison. S01 and S02 remain blocked. No test work has
been cleared.

#### Repaired Final Release Matrix

The repaired matrix under
`Build/_AgentValidation/20260918-004913-s00-instrumentation/reports/final-matrix/`
completed the required three Release `CleanProfile` pairs in `D/E/D/E/D/E`
order. Every accepted run used a 25-second warmup, 60-second stationary and
60-second controlled-motion windows, one-frame capture cadence, Vulkan,
CpuDirect, Advanced/TSR, 1920x1080 output, the same workload identity
`10991459253885323059`, and verified stationary and translated camera poses.
The first pair-1 enabled attempt was rejected at 13/16 screenshot color buckets;
its raw session is preserved separately and is not included below. Its unchanged
attempt 2 and each later attempt 2 passed the 16-bucket admission threshold.

Median render results across the three accepted runs per condition are:

| Window / metric | Profiler off ms | Profiler on ms | Delta | Allowance | Result |
| --- | ---: | ---: | ---: | ---: | --- |
| Stationary mean | 6.517 | 6.672 | +0.155 | <0.500 ms | Pass |
| Stationary p95 | 7.573 | 7.699 | +1.66% | 5.49% spread | Pass |
| Stationary p99 | 10.162 | 10.563 | +3.95% | 7.67% spread | Pass |
| Motion mean | 7.010 | 7.227 | +0.217 | <0.500 ms | Pass |
| Motion p95 | 7.784 | 7.815 | +0.40% | 9.98% spread | Pass |
| Motion p99 | 10.389 | 15.268 | +46.96% | 10.85% spread | **Fail** |

The allowance is the larger of 5% and the disabled-run range divided by its
median, matching the predeclared rule and prior matrix calculation. The two
enabled motion p99 values that drive the failure are 15.398 and 15.268 ms,
versus disabled values of 11.176, 10.049, and 10.389 ms; this is not a single
isolated maximum. No accepted stationary or motion window exceeded 32.855 ms,
so all six satisfy the zero unexplained warm-stall requirement at 500 ms.

The repaired evidence capabilities pass in every accepted run:

- Stationary coarse-GPU coverage is 99.946-99.951%; motion coverage is
  99.855-99.917%. This exceeds the 99% minimum. Median GPU p99 is 3.163 ms off
  versus 3.123 ms on while stationary, and 3.071 ms off versus 3.277 ms on in
  motion.
- Exact profiler overflow and pending-completed discard counters are zero. All
  summaries report complete diagnostic-loss evidence and pass that gate.
- Managed heap and private-byte endpoints pass their per-run 16 MiB-or-5%
  budgets. Exact Vulkan live-resource and descriptor-set deltas are zero in all
  six runs.
- Required CodeProfiler, texture-upload, shader-warmup, required-pipeline,
  lifetime-retirement, swapchain-retirement, and material-allocation backlogs
  all return to their captured baselines.

All six accepted admission images were viewed. They are non-empty and contain
the expected subject and camera composition, but three show severe exposure or
color corruption while the others show the outdoor scene normally. The stable
workload identity and strict admission rule make the timing comparison usable;
the images do not establish visual correctness or resolve TSR ghosting.

Final S00 disposition: **Blocked only on motion p99 observer overhead**. The
repaired measurement work closes the former GPU-coverage, exact-loss, retention,
native-occupancy, and backlog-evidence blockers. Do not advance to S01 or S02
until the profiler-on motion-tail regression is explained and brought within the
predeclared allowance, or the user explicitly changes the gate. Test clearance
remains absent.

#### Current-HEAD Motion Recheck And S00 Closure

Revision `a1f9408e5` changed the Vulkan motion regime, so the historical absolute
timings above could not decide current observer overhead. The superseding matrix
under `Build/_AgentValidation/20260918-s00-motion-recheck/` used the same Release
`CleanProfile`, Vulkan/CpuDirect, Advanced/TSR, 1920x1080, uncapped presentation,
fixed camera, controlled endpoint, readiness, admission, and one-frame sampling
contract. It completed three alternating pairs in `D/E`, `E/D`, `D/E` order.
All runs retained workload identity `10991459253885323059`, 816-840 motion
samples, 99.625-99.643% motion GPU coverage, zero profiler discard counters,
and passing loss, retention, native-resource, descriptor, and backlog gates.

| Pair | Motion p99 off ms | Motion p99 on ms | Delta | Motion mean delta ms |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 83.034 | 82.201 | -1.00% | -1.147 |
| 2 | 73.046 | 75.925 | +3.94% | +0.116 |
| 3 | 74.857 | 79.669 | +6.43% | +0.084 |

The disabled motion-p99 run spread is 13.67%, so every paired increase is below
the predeclared larger-of-5%-or-spread allowance. Motion-p95's 12.27% disabled
spread likewise covers the worst +6.04% paired increase. Positive mean overhead
is at most +0.116 ms/frame, below 0.5 ms. Stationary tail changes are also within
their larger disabled-run spread. S00 is therefore **Validated** and S01 becomes
the active item. This supersedes the historical blocked disposition without
reclassifying the old evidence as passing.

The matrix also validates the user's report of periodic microstalls. Across the
three retained raw motion streams, render frames above 55 ms recur every
0.282-0.301 seconds median, usually four render frames apart, with adjacent-frame
bursts. Render duration correlates 0.98-1.00 with CPU Vulkan frame/recording time
and only 0.03-0.10 with GPU time. Comparing pair-3 enabled frames below 25 ms to
frames at or above 55 ms attributes +28.520 ms to primary recording: +16.547 ms
primary operation loop, +5.970 ms primary prewarm, +4.126 ms command-chain packet
lowering, and +2.523 ms command-buffer finalization. Command-chain worker time is
zero. The desktop `PresentNow` path currently requests fresh serial recording;
causality and a safe reuse/publication correction remain S02 work after S01.

#### TSR Motion Evidence And Telemetry Correction

The temporal investigation confirms active TSR resources and ready history, but
does not resolve the reported ghosting. One bounded sequence completed 40/40
frames without drops. A repeated 30-second controlled move completed 30/30,
recorded camera motion, and captured finite nonzero velocity during motion. This
rejects a catastrophic always-zero velocity path. The viewed frames did not show
an obvious persistent trail, but the subject was underexposed and the apparent
motion was small. Sequence camera metadata also used a transform basis that did
not match `get_render_state`, so it is not authoritative pose evidence. TSR
ghosting remains **Unresolved**; no feedback reduction or visual workaround was
used and no temporal-correctness claim is made.

The investigation exposed a separate telemetry defect: after a live camera TSR
override, viewport generation used TSR while profile NDJSON continued to report
launch-cached FXAA. `Engine.ProfileCapture` now resolves active anti-aliasing from
each frame's rendered output manifest, prioritizing desktop scene, editor panel,
and XR submit outputs before camera/global fallbacks. It also refreshes cached
run metadata when the first real output manifest becomes available.

The focused Release Bootstrap build passed. In the stopped owned process PID
12816, 200 consecutive retained rows from render frames 168659-168858 all report
Vulkan, `anti_aliasing_mode=Tsr`, `tsr_render_scale=0.67`, motion vectors enabled,
one workload identity, and no malformed rows. The executable SHA256 matches the
strict matrix; the rebuilt Bootstrap DLL SHA256 is
`A4FDA26D667F1FAB13F356EA3DDB7A366BBD4E80914736EF3856EA25F3F12FDD`.
Older retained process rows report FXAA, preserving the before/after distinction.
This long-running resized session had different current topology and is telemetry
proof only, not comparable matrix evidence. The owned session was stopped. No
tests were added, modified, or run.

Current known source state includes unrelated/pre-existing uncommitted profiler
and session-manager changes; preserve them. No test clearance has been granted.

```text
Item / owner / status: S00 / Rendering / Historical attempted validation, superseded by review
Prior validated item and any approved reordering: None (entry baseline; prerequisite for S01-S16).
Hypothesis and disconfirming check:
  - Hypothesis: The resumed September 17 probe session (resume-log-debug-0917) operates in a different performance regime (27.1-42.6 ms CPU recording, 17.81-24.85 ms GPU time, 15-20 Hz output) from the original report's repeated 153-165 ms CPU recording with TSR ghosting. Establishing an explicit baseline with machine/driver/settings and workload budgets will prevent misattributing startup/cold spikes or general overhead to the original unverified report.
  - Disconfirming check: If the 153-165 ms steady-state recording reproduces under identical scene/camera conditions, the baseline must record that exact trigger. If not, the original issue must remain explicitly marked Unreproduced/Unresolved while addressing confirmed mechanisms.
Baseline source/configuration/cache/scene identity:
  - Source revision: HEAD (clean working tree).
  - Build configuration: Debug, AnyCPU, .NET 10 SDK, Windows 11.
  - Device/Driver: NVIDIA GeForce RTX 4070 (vendor=0x10DE, device=0x2860), Driver 0x9A0E0000, Vulkan API 1.4.351 (Loader 1.4.341).
  - Vulkan Diagnostics & Validation: Preset=RenderDocFriendly, Flags=DebugUtils, CommandBufferLabels, CrashBreadcrumbs, RenderDocFriendly; ValidationLayers=False; Sync=Sync2; Descriptors=DescriptorIndexing.
  - Present Mode: PresentModeImmediateKhr (Uncapped, targetHz=0.00).
  - Profiler/Logging: ENABLE_PROFILER enabled, _enableFrameLogging=true, RenderStallThresholdMs=500.0ms, FpsDropLogCooldown=1000ms.
  - Scene & Viewport: Default World / UnitTesting world (UnitTest_Init: WorldKind=Default, RenderPipeline=AdvancedRenderPipeline, UseDebugOpaquePipeline=False), viewport 1920x1080.
  - Cache State: Prewarm DB (prewarm_v5_000010DE_00002860_9A0E0000_0040415F_DevParity.json, 3506 entries), Pipeline cache (pcache_xr3_v000010DE_00002860_9A0E0000_0040415F.bin, 9026945 warm bytes).
Change scope and dependency/lifetime invariants:
  - Baseline characterization only. No runtime code or dependency changes. User caches and existing diagnostic records preserved.
Predeclared metrics, budgets, tolerance, repetitions and window:
  - Workloads defined: Cold process start, persisted-cache warm restart, warmed stationary viewport (>60s window), and controlled-motion/inspector interaction.
  - Budgets: Steady-state CPU recording target <10.0 ms; GPU command buffer target <16.6 ms (60 Hz target); 0 warm stall detections >500 ms; profiler observer overhead <0.5 ms/frame with 0 hot-path allocations.
Changed source diff and validated binary/session identity:
  - Source diff: None (baseline characterization).
  - Validated session: Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-105921-resume-log-debug-0917/
Focused build command and result, including warnings:
  - dotnet build .\XREngine.Editor\XREngine.Editor.csproj built cleanly in session artifacts with 0 warnings, 0 errors.
Live scenarios and exact evidence paths:
  - Session logs: Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-105921-resume-log-debug-0917/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-17_11-01-53_pid1900/
    - log_vulkan.log: Hardware/driver/extension configuration.
    - editor_bootstrap.log: World initialization, timing, and windowing parameters.
    - profiler-fps-drops.log: 136 warmed FPS-drop detections.
    - profiler-render-stalls.log: 7 startup stall detections (500 ms threshold).
Before/after distributions, sample validity and observer overhead:
  - Observed warmed stationary recording: 27.1-42.6 ms (overlay sampled across 69 samples, 11:03:15-11:05:45).
  - GPU timing: 17.81-24.85 ms.
  - Render interval: 47.87-70.55 ms (15-20 Hz output).
  - Acquire/present: 0.1 ms each; submission: 0.5 ms.
  - Startup stalls (>500 ms): 7 occurrences during initialization (VisibilityPreparation, BindOutputFBO, CameraComponent UI, Upload, RecordCommandBuffer, MainOpLoop, DrawToolbar).
Correctness/images, failure cases, retention and adjacent regressions:
  - Output presented TsrOutputTexture to swapchain without crashing. Original TSR ghosting symptom remains unverified in this probe.
Pass/fail decision and reason; does it explain the original symptom?:
  - Decision: Pass (Validated). Baseline manifest and workload budgets recorded.
  - Original symptom explanation: Does NOT explain or reproduce the original 153-165 ms symptom (reproduction status remains explicitly Unreproduced / Unresolved).
Test clearance state, approved focused checks and results:
  - Not applicable for baseline documentation.
User confirmation / remaining risks / next permitted item:
  - User confirmed execution of S00/S01 plan. Next permitted item is S01 (Correct profiler duration/identity reporting).
Temporary settings and owned session cleanup:
  - Session resume-log-debug-0917 is in Stopped state; profiler settings restored to baseline.
```

### S01 Gate Record: Correct Profiler Duration and Identity Reporting

Review disposition: **Validated**. The
current implementation now carries explicit scope, parent, logical-thread,
producer-thread, session-epoch, publication and update/render frame identities.
Tree reconstruction uses parent IDs, linked children defer parent publication,
synchronous self-time excludes linked/cross-producer spans, stale completions are
rejected across epochs, and packet, in-process collector, dump and UI consumers
carry the new contract.

Current live validation used isolated session `s01-profiler-identity`, PID 13724.
The settled epoch-2 dump contained 1,395 nodes with zero duplicate scope IDs,
missing parent IDs, hierarchy/parent mismatches, logical-thread mismatches,
invalid timestamps, children outside parent intervals, or incomplete published
nodes. Five display-level self-time differences had a maximum error of 0.012 ms;
these arose from summing many values rounded independently to three decimal
places, while implementation accounting remains tick-based. The dump reported
zero stale completions before the lifecycle exercise.

The profiler was then disabled and re-enabled through the session-only editor
preference `Debug.EnableProfilerFrameLogging`. Snapshot epochs advanced 2 -> 3
-> 4. Epoch-3 and epoch-4 snapshots reported 13 and 24 stale completions,
respectively, proving that late completions were rejected and surfaced rather
than attached to the new session. Publication IDs continued monotonically from
7009 to 7076 and 7382. Normal steady-state snapshots retain active/pending work
and correctly mark `ContainsIncompleteScopes`; published nodes themselves remain
complete.

Edit mode, play-mode transition and steady play produced no linked nodes. A
repository-wide call-site check found no consumers of `XREvent.InvokeParallel`
or `XREvent.InvokeAsync` outside their declarations, and no existing engine event
has listener contracts broad enough to permit speculative parallel conversion.
The user subsequently requested these validations, clearing focused test work.
`XREventLinkedProfilingTests` exercises both methods with four concurrent
listeners and verifies the complete parent chain, worker producer identity,
caller logical identity, interval containment, complete publication, zero
unresolved linked children and linked-time exclusion from synchronous `SelfMs`.
The validation exposed and corrected two defects: profiler timestamps used the
game-loop stopwatch, which does not advance in a headless process, and linked
context was captured before the action-container scope, making worker listeners
siblings instead of children. The generic and boolean concurrent event variants
received the same parent-capture correction. Final focused result: 2 discovered,
2 passed, 0 failed, 0 skipped. Editor and `XRE_PUBLISHED` Bootstrap builds both
completed with 0 errors and 0 warnings. S01 is Validated; S02 may proceed. The
historical attempted validation below remains superseded.

```text
Item / owner / status: S01 / Diagnostics & Rendering / Historical attempted validation, superseded by review
Prior validated item and any approved reordering: S00: Establish a comparable baseline and evidence manifest (Validated).
Hypothesis and disconfirming check:
  - Hypothesis: The profiler's GetHottestPath previously assigned the root scope's total elapsed duration (hottest.ElapsedMs) to the deepest leaf name (e.g. 92.077 ms to XREngine.RuntimeWorld.Update), misattributing enclosing frame time to leaf methods. Furthermore, SelfMs was not computed or exposed on node snapshots. Introducing SelfMs = Math.Max(0f, elapsedMs - childTicksSum) and exposing distinct root-inclusive (HotPathRootMs), leaf-inclusive (HotPathLeafInclusiveMs), and leaf-self (HotPathLeafSelfMs) durations in telemetry and diagnostic logs will correctly attribute exclusive leaf time and prevent misreading root interval costs as leaf bottlenecks.
  - Disconfirming check: If leaf self-time does not sum with child durations to match the root duration within floating-point tolerance, or if root elapsed time remains reported as the leaf duration in FPS drop or render stall logs, the accounting model is invalid.
Baseline source/configuration/cache/scene identity:
  - Source revision: HEAD with S00 baseline established.
  - Configuration: Debug, AnyCPU, .NET 10 SDK, Windows 11.
  - Device: NVIDIA GeForce RTX 4070 (driver 0x9A0E0000), Vulkan 1.4.351.
  - Session: Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-132017-s01-profiler-validation/
Change scope and dependency/lifetime invariants:
  - XREngine.Data/Profiling/ProfilerFramePacket.cs: Added public float SelfMs { get; set; } to ProfilerNodeData.
  - XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.CodeProfiler.cs:
    - Added SelfMs property to ProfilerNodeSnapshot (#if ENABLE_PROFILER and #else stub).
    - Added CalculateSelfMs(float elapsedMs, IReadOnlyList<ProfilerNodeSnapshot> children) helper.
    - Updated BuildSnapshotFromBuilt to accumulate childTicksSum and compute selfMs during tree construction.
    - Overhauled GetHottestPath to return distinct rootInclusiveMs, rootScopeKind, leafInclusiveMs, leafSelfMs, leafScopeKind, and formatted path string. Retained backwards-compatible out float pathMs overload.
    - Updated _lastCompletedRenderThreadHotPath state and LogRenderThreadStallDetected / LogRenderThreadStallRecovered to report LastCompletedRenderRootMs, LastCompletedRenderLeafInclusiveMs, and LastCompletedRenderLeafSelfMs.
    - Updated LogFpsDrop to explicitly log HotPathRootMs, HotPathLeafInclusiveMs, HotPathLeafSelfMs, HotPathRootScopeKind, HotPathLeafScopeKind, HotPathLoggingPolicy, and matching fields for LikelyBlockingHotPath.
    - Updated AppendTopFrameThreads to output rootHot=... ms leafHot=... ms leafSelf=... ms kind=....
  - XREngine.Runtime.Bootstrap/Engine/Engine.ProfilerSender.cs: Mapped SelfMs = n.SelfMs in ConvertNodes.
  - XREngine.Editor/EngineProfilerDataSource.cs: Mapped SelfMs = n.SelfMs in ConvertNodes.
  - XREngine.Editor/ProfilerDiagnosticDumps.cs: Updated CalculateSelfMs to return node.SelfMs and updated dump headers to include SelfMs.
  - XREngine.Profiler.UI/ProfilerPanelRenderer.cs: Decoupled root and leaf durations in UI hottest-path display.
  - Tools/Manage-McpEditorSession.ps1: Fixed PowerShell 5.1 anonymous-pipe breakage killing detached editor processes on Windows by conditionally applying standard I/O redirection only when $env:XRE_REDIRECT_STDIO is set.
Predeclared metrics, budgets, tolerance, repetitions and window:
  - Metrics: rootInclusiveMs >= leafInclusiveMs >= leafSelfMs; rootInclusiveMs - sum(children) == rootSelfMs (within 0.001 ms tolerance); SelfMs populated in all node snapshots; observer overhead unchanged (<0.5 ms per frame, 0 heap allocations on hot path).
Changed source diff and validated binary/session identity:
  - Modified files: 7 files across Data, Bootstrap, Editor, and Profiler.UI.
  - Session root: Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-132017-s01-profiler-validation/
  - Validated process: PID 48772 (Vulkan, 408+ frames rendered).
Focused build command and result, including warnings:
  - dotnet build .\XREngine.Editor\XREngine.Editor.csproj built cleanly with 0 errors and 0 warnings.
Live scenarios and exact evidence paths:
  - CPU frame dump: Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-132017-s01-profiler-validation/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-17_13-35-57_pid48772/profiler-cpu-frame-2026-09-17-13-37-11-651-afbbc101.log
  - General log: Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-132017-s01-profiler-validation/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-17_13-35-57_pid48772/log_general.log
  - Bootstrap log: Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-132017-s01-profiler-validation/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-17_13-35-57_pid48772/editor_bootstrap.log
  - Vulkan log: Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260917-132017-s01-profiler-validation/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-17_13-35-57_pid48772/log_vulkan.log
Before/after distributions, sample validity and observer overhead:
  - Before: In GetHottestPath, root inclusive time (e.g. 92.077 ms or 8.378 ms) was returned as pathMs alongside the deepest leaf name XREngine.RuntimeWorld.Update, falsely attributing all enclosing frame time to RuntimeWorld.Update.
  - After: Captured live frame dump confirms exact hierarchy attribution:
    - Root: EngineTimer.DispatchUpdate.Iteration: total=8.378 ms self=0.444 ms children=3
    - Child 1: PreUpdate: total=7.118 ms self=0.004 ms
    - Child 2: Update: total=0.678 ms self=0.002 ms
    - Deepest leaf: XREngine.RuntimeWorld.Update: total=0.096 ms self=0.096 ms children=0
    - Self-time balance: 8.378 - (7.118 + 0.678 + ...) = 0.444 ms (exact 100% accounting).
    - Aggregate summary table confirms columns: Name | Scope | TotalMs | SelfMs | AvgMs | PeakMs | Calls with TotalMs=9.442 ms and SelfMs=0.448 ms.
Correctness/images, failure cases, retention and adjacent regressions:
  - Correctness: Captured frame dump verifies mathematically exact attribution across all 4 active threads. No assertion failures, no crashes, no rendering anomalies.
Pass/fail decision and reason; does it explain the original symptom?:
  - Decision: Pass (Validated).
  - Explanation: Directly explains why the original investigation observed what appeared to be massive XREngine.RuntimeWorld.Update stalls (92 ms) or mesh update stalls (1,285 ms) — those numbers were enclosing root durations, not the leaf methods' own execution time.
Test clearance state, approved focused checks and results:
  - Pending user clearance per repository agreement. No unit test files modified. Live MCP runtime captures used for verification.
User confirmation / remaining risks / next permitted item:
  - S00 and S01 completed and validated. Next permitted item per ledger is S02 (Attribute the actual warmed bottleneck).
Temporary settings and owned session cleanup:
  - Session s01-profiler-validation stopped cleanly via Manage-McpEditorSession.ps1 Stop. Background daemon task terminated.
```

### S02 Gate Record: Warmed Recording Allocation Attribution

Disposition: **Validated** for attribution and one bounded warmed-recording
correction. This does not declare the original 153-165 ms report, TSR ghosting,
the independent 18-25 ms GPU workload, or every rare whole-frame tail fixed.

The Release Vulkan desktop workload remained CpuDirect, Advanced/TSR, uncapped,
1920x1080, validation-off, and stable at 393 admitted draws. Dense capture was
first treated as an observer rather than trusted as a production profile: the
capture thread owned 86.65% of process allocation and one profile row allocated
about 143 KB. An interval-10 capture still measured about 3.89 MB of render-thread
allocation per frame, proving that capture output was not the only owner.

Bounded stage counters narrowed the recurring work without changing recording
authority. Before the fix, a warmed diagnostic sample reported 795,056 bytes
median for primary recording and 529,336 bytes for its operation loop.
`AdvancedVisibility` owned 337,176 bytes. The phase split localized that to
VisibilityRaster: 273,872 bytes in early raster and 28,616 bytes in late raster;
preparation and compute phases were negligible. The 393 CPU-direct native draws
allocated zero bytes, as did tracked pipeline, descriptor, vertex and index
binding. Per-record substage capture then found exactly 207,504 bytes in closure
validation (393 x 528) and 37,728 bytes in stable-bin lowering (393 x 96).

The fixed byte counts came from generated record-struct equality traversing
nested Silk.NET `Pipeline`, `Buffer`, and `DeviceMemory` values. The correction
adds explicit scalar, reference and native-handle identity checks to
[VulkanFrameDataSlice](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanFrameDataSlice.cs),
[VulkanNativeBufferRange](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Advanced/VulkanNativeBufferRange.cs),
[VulkanVisibilityPreparedVertexSource](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/StableBins/VulkanVisibilityPreparedVertexSource.cs),
and [VulkanPreparedMeshPrimitive](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanPreparedMeshPrimitive.cs).
The hot validation and native-compatibility comparisons consume those methods;
no resource ownership, pipeline selection, draw order or submission strategy
changed.

The final 24-row detailed motion window retained 393 draws. Validation,
lowering, tracked binding and CPU-direct draw substages each allocated zero bytes
in every row. Median `AdvancedVisibility` allocation fell 337,176 -> 58,376
bytes (-80.8%), operation-loop allocation fell 529,336 -> 246,904 bytes
(-53.4%), and primary-recording allocation fell 795,056 -> 392,992 bytes
(-50.6%). Frame attribution coverage was 99.942-99.991%; unattributed time was
4-7 us, below the 50 us hardening threshold.

The matched 20-second labels-off, profiler-frame-logging-off CleanProfile capture
remains profile-capture intrusive, so it is directional A/B evidence rather than
a promotion baseline. It nevertheless preserved the same strategy and scene
identity and improved materially:

| Metric | Before | After | Change |
| --- | ---: | ---: | ---: |
| Captured render frames | 169 | 457 | +170.4% |
| Vulkan frame median | 23.083 ms | 17.797 ms | -22.9% |
| Vulkan frame p95 | 95.445 ms | 65.440 ms | -31.4% |
| Primary recording median | 16.601 ms | 7.097 ms | -57.2% |
| Primary recording p95 | 58.656 ms | 36.934 ms | -37.0% |
| Managed allocation per render frame | 4.724 MB | 1.726 MB | -63.5% |
| GC pause per render frame | 6.675 ms | 0.955 ms | -85.7% |
| GC pause rate | 56.055 ms/s | 21.757 ms/s | -61.2% |

Process allocation rate changed only 39.672 -> 39.330 MB/s because serializing
one large profile row for every rendered frame remains the dominant observer.
The after window's whole-frame p99 was 234.003 ms versus 232.852 ms before, with
one 444.231 ms maximum, so rare non-recording tails remain explicitly open.
Recurring pipeline construction was disconfirmed: the warmed owner was raster
identity validation/lowering, not `VulkanCanonicalVisibilityPipelineFactory`
cache misses. ETW was therefore not required to choose this correction; managed
allocation deltas were deterministic, phase-local, proportional to record count,
and collapsed to zero after the fix.

Evidence is retained under
`Build/_AgentValidation/20260918-130043-s02-primary-attribution/logs/`:
`operation-owner-motion.ndjson`, `raster-split-motion.ndjson`,
`equality-complete-motion.ndjson`, `clean-gc-motion.ndjson`, and
`clean-after-motion.ndjson`. All named editor sessions were stopped. The final
Release editor build completed with zero errors and zero warnings. No tests were
added, modified or run because test clearance was not granted for this rendering
regression. S03 subsequently established its cold-readiness entry condition and
moved to Active; see the gate record below.

## S03 Gate Record: Nonblocking Advanced Readiness

Status: Active, implementation complete and validation partial.

The former readiness query synchronously linked the full Advanced shader family
and could foreground-complete compute pipeline jobs. It now observes one
generation-owned preparation task through explicit `Missing`, `Pending`, `Ready`,
and `Failed` snapshots. The task reuses existing shader and pipeline queues,
publishes only after the complete supported compute/raster family is ready, and
is canceled and drained before pipeline-queue teardown. Polling neither joins a
compile nor duplicates the current identity's request.

Reload handling now binds compiled and failed shader state to source revision,
retains superseded CPU artifact tasks until completion, refreshes generated
preambled Advanced sources from their engine assets, and requires a `Ready`
snapshot to match its completed preparation identity. Raster preparation
preserves transient readiness as `RetryFrame`; structural/native failures remain
visible failures. Runtime dependency invalidation no longer marks unchanged
shader assets dirty, so manual reload does not create false unsaved-file prompts.

Live evidence:

- Cold PID 46584 emitted a bounded 17-notice `ShaderCompilePending` burst, then
  advanced beyond readiness without a pipeline readiness failure.
- Warm/reload PID 39016 admitted and bound Advanced execution. Frame 1643 retained
  396 GPU commands and 393 cooked payloads with zero frame-package, queue,
  draw-not-ready, CPU/forbidden-fallback, or dropped-operation counts.
- Final-gates PID 34396 used a 1.5-second delayed compile and two immediate shader
  invalidations. It reported `PendingResources`, retained the 396/393 identity,
  then converged to `Admitted` with zero rejected, dropped, or fallback work.
- Injected Vulkan shader compilation failure became visible as `Unsupported` with
  the exact failure diagnostic. Clearing injection and advancing source revision
  recovered first to `PendingResources` and then to `Admitted` in the same
  renderer generation.
- All named S03 sessions stopped cleanly; no aggregate, disposal, preparation,
  compile-queue, unhandled, or fatal teardown error was found.
- Non-incremental Vulkan Release and isolated editor builds passed with zero
  warnings and errors. Existing shader dependency hot-reload tests passed 10/10.

Remaining closure gates:

- Capture separate cold and reload durations for source compilation, program
  linking, native pipeline work, and any foreground join. The observed manual
  all-shader reload included a separate global maintenance stall owned by S04.
- Exercise a genuinely unavailable Vulkan capability and confirm the requested
  family reports a visible unavailable/failed result without fallback.
- The viewport remains visually invalid because canonical texture publication
  reports `SourceMismatch` after texture dimensions change. That defect is
  independent of pipeline readiness and must be resolved before claiming an
  integrated Advanced visual-quality pass.

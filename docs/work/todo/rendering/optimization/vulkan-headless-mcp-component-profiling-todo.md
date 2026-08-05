# Vulkan Headless MCP Component Profiling TODO

Last Updated: 2026-07-30
Owner: Rendering / Vulkan / Profiling / MCP
Status: In Progress

Sequence relationship:

- This is a profiling-infrastructure workstream, not workstream 09 of the
  ordered Vulkan 01-08 optimization sequence.
- It extends the measurement contract established by completed workstream 01
  and recorded in the
  [Vulkan framerate root-cause investigation](../../../investigations/rendering/archive/vulkan-framerate-root-cause-2026-07-28.md).
- It must not redefine accepted whole-frame budgets, profile modes, or
  promotion rules without updating workstream 01 and recapturing affected
  baselines.
- It supports, but does not replace, the
  [01-08 Optimization Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md).

Related architecture and implementation:

- [Vulkan Renderer](../../../../architecture/rendering/vulkan-renderer.md)
- [Vulkan Primary And Secondary Command Recording](../../../../architecture/rendering/vulkan-command-recording.md)
- [Vulkan Primary Command-Buffer Reuse](../../../../architecture/rendering/vulkan-primary-command-buffer-reuse.md)
- [Vulkan Command Recording Architecture Optimization TODO](vulkan-command-recording-architecture-optimization-todo.md)
- [Remote Profiler](../../../../developer-guides/diagnostics/profiler.md)
- `Tools/Manage-McpEditorSession.ps1`
- `Tools/Measure-GameLoopRenderPipeline.ps1`
- `Tools/Benchmarks/Invoke-VulkanPerf.ps1`
- `XREngine.Runtime.Rendering/Runtime/RendererModules/RendererBackendCreateContext.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/VulkanRendererBackendFactory.cs`
- `XREngine.Editor/Mcp/McpServerHost.cs`
- `XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs`
- `XREngine.Data/Rendering/VulkanTelemetryEnums.cs`

## Goal

Create a deterministic, MCP-controlled Vulkan profiling host that can execute
the real renderer without the editor UI or a desktop window, isolate one CPU or
GPU renderer component at a time, and then prove that component-level
improvements benefit the complete presentation and XR paths.

The system must answer precise questions such as:

- How much CPU time and allocation cost belongs only to command-chain lowering?
- How does secondary recording scale across one, two, four, eight, and twelve
  workers for the same immutable workload?
- What does descriptor publication cost independently of asset loading,
  visibility collection, editor UI, swapchain presentation, and logging?
- How much GPU time belongs to one render-graph pass without enabling dense
  timestamps for every other pass?
- Did a locally faster component improve the presentationless frame, desktop
  swapchain frame, and OpenXR frame?

## Current State And Gaps

Existing strengths:

- The process harness separates `Diagnostics`, `DevelopmentProfile`,
  `CleanProfile`, and `ReleaseBenchmark`.
- Clean and release captures prohibit validation, command labels, dense
  timestamps, and editor diagnostic overlays.
- The harness waits for a stable workload identity and quiet asset, shader,
  retirement, and synchronization state before capture.
- Captures expose frame percentiles, Vulkan CPU stages, allocations, command
  and resource churn, readbacks, fallbacks, output identity, and GPU timings.
- The Vulkan profiler already has coarse whole-command-buffer timestamps,
  optional dense timestamps, per-frame-slot query pools, and delayed query
  sampling.
- MCP can dump CPU and GPU histories and retrieve the current render-profiler
  counters after a run.

Current gaps:

- Presentation-independent Vulkan bootstrap hosts now own their device,
  frame-slot, output, query, and synchronization resources, but the existing
  `VulkanRenderer` render-graph implementation still derives from the
  window-oriented `AbstractRenderer` and has not yet been hosted by those
  targets.
- The optional headless-WSI path is driver-gated by
  `VK_EXT_headless_surface`; unsupported drivers report the limitation and
  retain the presentationless lane.
- The editor MCP dispatcher rejects all tool calls when there is no active
  `XRWorldInstance`, including tools that only need profiler or renderer state.
- MCP cannot prepare, arm, start, stop, or execute a deterministic profile
  recipe. It can only inspect or dump already collected state.
- The benchmark harness still launches the editor and creates a desktop window,
  even when ImGui and diagnostic overlays are skipped.
- Vulkan CPU stage telemetry aggregates elapsed time and allocation totals by
  stage. It does not retain bounded per-invocation spans, parentage, worker
  identity, overlap, or exclusive time.
- Dense Vulkan timestamps can change command-buffer cache behavior and are too
  broad for isolated component measurement.
- CPU and GPU clocks are not currently placed on one calibrated timeline.

## External Validity Review

The proposed direction was checked against current authoritative Vulkan
documentation:

- The Khronos [Vulkan profiling guide](https://docs.vulkan.org/guide/latest/profiling.html)
  treats CPU command preparation, queue submission, and GPU execution as
  distinct timelines. It recommends object naming, command-buffer labels,
  timestamp queries, delayed result retrieval, and hardware-specific tools for
  deeper counter analysis.
- The Khronos
  [timestamp query sample](https://docs.vulkan.org/samples/latest/samples/api/timestamp_queries/README.html)
  confirms that GPU timestamps are approximate, stage selection matters,
  timestamps from different queues cannot be compared directly, and
  availability-based delayed reads are preferable to blocking the frame.
- `VK_EXT_headless_surface` creates a surface and swapchain whose presentation
  is normally a no-op. The
  [extension reference](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_headless_surface.html)
  validates it as a useful WSI test mode, but not as a substitute for a
  presentationless offscreen renderer or a real compositor.
- The Khronos
  [calibrated timestamps sample](https://docs.vulkan.org/samples/latest/samples/extensions/calibrated_timestamps/README.html)
  validates `VK_EXT_calibrated_timestamps` for correlating host and Vulkan time
  domains while reporting maximum deviation.
- The
  [`VK_KHR_performance_query` reference](https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_performance_query.html)
  confirms that hardware counters can require multiple submissions, a
  profiling lock, and special handling for parallel workloads and secondary
  command buffers. These captures must therefore remain intrusive diagnostics,
  not promotion evidence.

These sources validate the measurement mechanisms. They do not prove that an
XRENGINE abstraction or optimization is faster; every engine-specific result
remains benchmark-gated.

## Profiling Invariants

- “Headless” must never be one ambiguous result category.
- Presentationless, headless-WSI, desktop-presentation, and OpenXR results must
  have distinct manifest identities and must not be compared as equivalent
  workloads.
- The presentationless path must execute real Vulkan resource binding, command
  recording, synchronization, queue submission, and GPU work. It must not
  silently substitute a CPU or null renderer.
- A component fixture must preserve the minimum valid prerequisites and
  synchronization for the component being measured.
- Every recipe must state what it includes and excludes.
- MCP may prepare and arm a capture, but it must perform no polling, logging,
  serialization, or RPC work during the measured frame interval.
- GPU query reads must be delayed and non-blocking during the measured interval.
- Clean and release results must remain free of validation, debug labels, dense
  all-pipeline timestamps, profiler UI, editor UI, and verbose per-frame logs.
- Intrusive profiler results must identify the enabled observers and cannot
  update clean baselines.
- Per-frame hot-path instrumentation must be allocation-free after warmup.
- A component optimization is not accepted until it also passes broader
  subsystem and full-frame regression gates.
- Unsupported accelerated paths, extensions, counters, or presentation modes
  must fail visibly or be reported as unsupported. They must not silently
  select another path.

## Evidence Lanes

| Lane | Rendering target | Includes | Intended use |
| --- | --- | --- | --- |
| Component | Minimal valid fixture for one CPU/GPU target | Only declared prerequisites and the target | Root-cause and scaling experiments |
| Presentationless | Offscreen Vulkan color/depth images; no surface or swapchain | Full render graph, command recording, synchronization, queue submission, GPU execution | Renderer-only full-frame evidence |
| Headless WSI | `VK_EXT_headless_surface` when supported | Surface, swapchain, acquire, no-op present | Swapchain lifecycle and WSI integration |
| Desktop WSI | Hidden or visible Windows surface | Real acquire, compositor-facing present, window lifecycle | Desktop integration evidence |
| OpenXR | Actual selected runtime and view family | XR pacing, swapchains, eye views, mirror where selected | Production XR evidence |

The component and presentationless lanes may explain an optimization, but only
the required desktop/OpenXR lane can promote a change intended for those
targets.

## Phase 0 - Freeze Terminology, Contracts, And Baselines

- [x] Define `Component`, `Presentationless`, `HeadlessWsi`, `DesktopWsi`, and
  `OpenXr` as stable execution-mode identifiers.
- [ ] Add the execution mode, presentation target, output count, extent,
  format, sample count, frame-slot count, queue families, and present policy to
  the capture manifest contract.
- [x] Remove `HeadlessRendering` from current Vulkan metadata until Phase 1 is
  complete. Presentationless creation fails before factory creation because the
  catalog now validates the requested target capability.
- [ ] Record current editor-process baselines for one static Deferred cohort,
  one forced-dirty recording cohort, and one available RVC cohort.
- [ ] Measure current MCP-disabled, MCP-idle, and MCP-active overhead on the
  same non-promotable development cohort.
- [ ] Record the current dense-timestamp effect on primary and secondary dirty
  reasons, records, reuse, CPU time, and frame pacing.
- [ ] Freeze the existing workstream-01 profile-mode and baseline-compatibility
  rules as dependencies of this workstream.

Acceptance criteria:

- [ ] No manifest or report uses unqualified “headless” as an execution mode.
- [x] Current metadata does not claim a usable headless factory path that the
  factory rejects.
- [ ] Baseline evidence quantifies the editor/window and observer costs this
  workstream intends to remove.

## Phase 1 - Presentation-Independent Renderer Host Contract

### 1.1 Split device execution from window presentation

- [x] Replace the window-only renderer creation input with an explicit host or
  target contract that can represent a desktop window, presentationless
  offscreen target, headless surface, or OpenXR target.
- [x] Keep window lifecycle, native handles, input, title, framebuffer-resize
  events, and compositor presentation out of the presentationless contract.
- [x] Define fixed presentationless output properties: width, height, layers,
  format, color space where meaningful, samples, depth format, and frame-slot
  count.
- [x] Preserve renderer module generation and teardown ownership in every
  target mode.
- [x] Make backend capability selection validate the requested target before
  factory creation.

### 1.2 Implement the Vulkan presentationless target

- [x] Create the Vulkan instance, physical/logical device, queues, command
  pools, frame-slot resources, fences or timeline semaphores, query pools, and
  allocator without creating a native window or `VkSurfaceKHR`.
- [x] Allocate engine-owned offscreen color/depth images and image views for
  each required output/frame slot.
- [ ] Run the normal render graph and Vulkan command recording against those
  outputs. Track the production-renderer refactor in
  [Vulkan Presentation-Independent Renderer Refactor TODO](vulkan-presentation-independent-renderer-refactor-todo.md).
- [x] Submit to the real Vulkan queue and retain normal resource-lifetime and
  retirement rules.
- [x] Replace acquire/present with explicit frame-slot ownership and completion
  transitions; do not insert a device-wide wait into each frame.
- [x] Support optional final-image hash, small readback, or screenshot after
  the measured interval for correctness checks.
- [x] Keep current-frame readback forbidden for zero-readback performance
  recipes.

### 1.3 Add optional headless WSI

- [x] Probe `VK_EXT_headless_surface` before enabling the lane.
- [x] Create a headless surface and swapchain only when the extension and
  required formats/present support are available.
- [x] Record that presentation is a headless no-op rather than desktop
  compositor presentation.
- [x] Report unsupported headless WSI explicitly while leaving the
  presentationless lane available.

Acceptance criteria:

- [x] Vulkan can render and submit a deterministic frame without constructing
  an `XRWindow`.
- [x] Presentationless execution has no native application window, surface,
  swapchain, acquire, or present operation.
- [ ] The same render-graph fixture produces a stable output identity in
  presentationless and desktop modes within documented format differences.
- [ ] Validation and synchronization-validation runs report no new errors.
- [ ] Presentationless steady state performs no managed allocation, resource
  creation, shader compilation, or device-wide wait unless the recipe
  explicitly requests churn.

## Phase 2 - Dedicated Render Benchmark Process

- [ ] Add a small runtime executable, tentatively `XREngine.RenderBench`, that
  references the runtime/rendering modules without referencing editor UI.
- [ ] Keep startup deterministic and expose explicit configuration for backend,
  execution mode, recipe, output directory, MCP policy, and MCP port.
- [ ] Load only the world or synthetic fixture required by the recipe.
- [ ] Support fixed-step time, deterministic camera/animation inputs, fixed
  random seeds, and an optional frozen-world mode.
- [ ] Disable editor preferences, editor panels, ImGui, dynamic text, input
  polling, window title updates, and unrelated editor services.
- [ ] Keep shader/pipeline warmup, texture residency, resource retirement,
  workload identity, and stability gates.
- [ ] Add `Tools/Manage-McpRenderBenchSession.ps1` or an equivalently isolated
  session manager rather than overloading normal editor sessions.
- [ ] Reuse the named-session ownership rules, per-session build artifacts,
  environment isolation, PID validation, logs, and safe shutdown behavior from
  `Manage-McpEditorSession.ps1`.
- [ ] Store disposable evidence under
  `Build/_AgentValidation/<run>/` and engine-owned logs under the normal
  session log directory.

Acceptance criteria:

- [ ] One command starts a named presentationless Vulkan process, waits for MCP
  readiness, reports its endpoint and process identity, and can stop only that
  named process.
- [ ] The process remains usable without an interactive desktop session.
- [ ] Startup does not wait for creation of a first editor window.
- [ ] A fixture can run for a bounded frame count and exit cleanly without MCP.
- [ ] The executable and settings hashes are recorded in every result.

## Phase 3 - Runtime MCP Control Plane

### 3.1 Decouple generic MCP services from the editor

- [ ] Move or extract the transport, protocol, registry, permission, job, and
  idempotency infrastructure needed by runtime tools into an engine/runtime
  automation assembly.
- [ ] Let the editor register its scene/editor tool bundle without making
  runtime profiler tools depend on `XREngine.Editor`.
- [ ] Replace the mandatory-world `McpToolContext` with explicit optional
  capabilities such as world, renderer, render target, profiler session,
  editor, and window.
- [ ] Declare required capabilities per tool and return a precise missing-
  capability error instead of rejecting every tool when no world exists.
- [ ] Keep mutating profiler/session operations subject to the existing MCP
  permission policy and idempotency behavior.

### 3.2 Add asynchronous profiling tools

- [ ] Add `list_render_profile_targets`.
- [ ] Add `load_render_profile_recipe` and schema validation.
- [ ] Add `prepare_render_profile`, returning a session/job ID, selected
  adapter, driver, enabled features/extensions, workload identity, and any
  unsupported requirements.
- [ ] Add `wait_render_profile_ready`.
- [ ] Add `arm_render_profile` with an exact engine/render frame boundary.
- [ ] Add `start_render_profile` and `stop_render_profile`.
- [ ] Add `get_render_profile_status` without requiring measured-frame work.
- [ ] Add `get_render_profile_result` and artifact paths.
- [ ] Add `run_render_profile_matrix` as a bounded asynchronous job.
- [ ] Add cancellation and timeout behavior that leaves the renderer in a
  known state.
- [ ] Preserve the existing profiler dump tools as post-capture diagnostics.

### 3.3 Keep MCP outside the measured interval

- [ ] Implement a state machine with at least `Created`, `Preparing`,
  `Stabilizing`, `Armed`, `Capturing`, `Draining`, `Completed`, `Failed`, and
  `Cancelled`.
- [ ] Arm capture data before the selected frame boundary.
- [ ] Prevent status polling, response serialization, log flushing, and network
  callbacks from executing on render workers or the render thread.
- [ ] Buffer bounded results in preallocated storage during capture.
- [ ] Publish MCP results only after capture and delayed GPU-query drainage.

Acceptance criteria:

- [ ] Runtime profiler tools work in presentationless mode with no editor,
  window, or active world when the selected fixture does not require them.
- [ ] A long matrix call returns a job ID instead of holding one RPC open.
- [ ] MCP-idle and MCP-disabled clean cohorts are statistically equivalent
  within the workstream-01 observer-overhead threshold.
- [ ] No MCP work appears inside retained measured-frame CPU spans.

## Phase 4 - Profile Recipes And Deterministic Fixtures

### 4.1 Recipe schema

- [ ] Define a versioned JSONC recipe schema.
- [ ] Include target component, execution mode, fixture, backend, adapter,
  resolution, render scale, formats, sample count, frame slots, warmup,
  stability window, capture frames, repetitions, and timeout.
- [ ] Include instrumentation mode, validation mode, label policy, hardware-
  counter policy, and CPU sampling policy.
- [ ] Include scene, camera, lights, animation, time-step, random seed, mesh
  strategy, render features, stereo mode, and output identities.
- [ ] Include mutation policy: stable reuse, forced dirty every frame, one dirty
  event every N frames, resource churn, descriptor churn, or pipeline churn.
- [ ] Include worker counts, chain/draw counts, descriptor counts, barrier
  counts, upload sizes, pass iteration counts, and any target-specific inputs.
- [ ] Include declared inclusions, exclusions, expected counters, validity
  requirements, and acceptance budgets.

### 4.2 Component fixtures

- [ ] Add a no-op/control fixture that measures harness, frame-slot, and
  submission overhead.
- [ ] Add command-chain signature and packet-lowering fixtures over immutable
  prebuilt input.
- [ ] Add primary command-encoding fixtures at small, medium, and large op
  counts.
- [ ] Add secondary command-recording fixtures across multiple chain sizes and
  worker counts.
- [ ] Add stable-reuse and forced-dirty command-buffer fixtures.
- [ ] Add descriptor publication/update fixtures with precreated resources and
  layouts.
- [ ] Add resource-planning and image-layout/barrier fixtures with fixed
  dependency graphs.
- [ ] Add queue-lock and minimal queue-submit fixtures without a per-iteration
  device-wide wait.
- [ ] Add upload fixtures with fixed byte counts and residency state.
- [ ] Add one-pass GPU fixtures for shadow, depth/normal, G-buffer, lighting,
  transparency, AO, bloom, TSR, and final composition where supported.
- [ ] Add full presentationless Deferred and Uber fixtures that match the
  canonical workstream-01 workload identities as closely as the lane permits.

### 4.3 Fixture correctness

- [ ] Precreate all assets and Vulkan objects not owned by the target before
  the capture interval.
- [ ] Assert expected draw, dispatch, descriptor, barrier, submission, output,
  and command-buffer-decision counts.
- [ ] Produce an optional post-capture output hash or image for visual
  correctness.
- [ ] Reject a capture when fixture identity, expected work, shader state,
  output identity, or fallback state changes.

Acceptance criteria:

- [ ] A recipe fully reproduces a run without relying on editor-global
  preferences.
- [ ] Each fixture states whether it includes managed preparation, native
  Vulkan calls, queue submission, GPU execution, or presentation.
- [ ] Changing only worker count or mutation policy does not change the
  underlying workload identity.
- [ ] Control fixtures quantify the fixed cost that must be subtracted or
  reported alongside component results.

## Phase 5 - Targeted CPU Profiling

- [ ] Keep the current allocation-free aggregate `EVulkanCpuStage` counters as
  the low-overhead default.
- [x] Add a target mask so diagnostic runs retain detailed spans for only the
  selected stage or subtree.
- [x] Store bounded span records in preallocated per-thread/per-worker ring
  buffers.
- [ ] Record stable stage ID, frame ID, parent span ID, thread/worker ID,
  start/end timestamp, invocation ordinal, allocated bytes, and wait reason
  where applicable.
- [ ] Calculate inclusive time, exclusive time, invocation distribution,
  worker overlap, worker imbalance, and wait-versus-work time after capture.
- [ ] Detect nested-stage double counting and document which aggregate stages
  are mutually exclusive.
- [ ] Add counts or sub-stages for material native calls such as command-buffer
  begin/end/reset, descriptor updates, command execution, and queue submission
  without timing every call by default.
- [ ] Export stable allocation-free markers that can be correlated with
  EventPipe/ETW, PerfView/WPA, `dotnet-trace`, and mixed native/managed
  profilers.
- [ ] Add an explicit CPU-sampling recipe option that launches or records the
  required process/frame correlation metadata without treating the sampled run
  as clean promotion evidence.

Acceptance criteria:

- [ ] Aggregate mode retains its current observer-overhead budget.
- [ ] Targeted mode can explain one stage's exclusive cost and parallel overlap
  without allocating during capture.
- [ ] Primary and secondary recording reports distinguish planning, waiting,
  native encoding, merge/assembly, and publication.
- [ ] Per-worker results expose load imbalance and scheduled work that did not
  actually overlap.

## Phase 6 - Targeted GPU Profiling And Correlation

### 6.1 Targeted timestamp scopes

- [ ] Keep coarse whole-command-buffer timestamps as the default supported GPU
  timing.
- [ ] Replace all-or-nothing dense instrumentation with a stable target/pass
  mask and maximum scope depth.
- [ ] Use per-frame-slot query pools and availability-based delayed reads.
- [ ] Record timestamp support, valid bits, timestamp period, query count,
  query bytes, skipped scopes, budget overflow, and readback latency.
- [ ] Use synchronization2 timestamp commands and stage masks where supported
  and meaningful.
- [ ] Reject cross-queue timestamp subtraction; report queue-local intervals
  independently.
- [ ] Measure and publish timestamp observer overhead for representative small
  and large passes.

### 6.2 Preserve command-buffer reuse behavior

- [ ] Prevent profiling one pass from dirtying unrelated primary or secondary
  command buffers.
- [ ] Evaluate per-frame-slot instrumented command-buffer variants or a
  dedicated diagnostic recording path for the selected target.
- [ ] Include profiler-target identity in an instrumented variant without
  changing the clean variant's cache identity.
- [ ] Report every record/reuse difference between instrumented and clean
  captures.

### 6.3 Correlate CPU and GPU timelines

- [ ] Probe and enable `VK_EXT_calibrated_timestamps` where supported.
- [ ] Record the host and device time domains, calibration samples, maximum
  deviation, queue-submit CPU interval, and GPU begin/end timestamps.
- [ ] Export a trace format that can place engine threads, render workers,
  submissions, waits, and GPU pass intervals on one timeline.
- [ ] Report unsupported calibration explicitly and retain uncorrelated
  queue-local GPU timings.

### 6.4 Intrusive hardware counters

- [ ] Probe `VK_KHR_performance_query` and enumerate available counter metadata.
- [ ] Implement counter-set selection, pass-count reporting, profiling-lock
  lifetime, and repeated identical submissions only in a dedicated diagnostic
  mode.
- [ ] Record counters affected by concurrent workloads.
- [ ] Integrate optional RenderDoc, Nsight, RGP, or other vendor capture
  launch/artifact hooks without making them clean-benchmark dependencies.
- [ ] Mark all hardware-counter and external-capture results intrusive and
  non-promotable.

Acceptance criteria:

- [ ] One selected GPU pass can be timed without enabling dense timestamps for
  the rest of the frame.
- [ ] Query retrieval never blocks a measured frame.
- [ ] Instrumented command-buffer behavior is reported and cannot masquerade
  as clean reuse evidence.
- [ ] Correlated traces can distinguish CPU starvation, queue delay,
  synchronization bubbles, and long GPU execution within calibration
  uncertainty.

## Phase 7 - Results, Statistics, And Artifact Contract

- [ ] Add a versioned component-profile result schema.
- [ ] Record source commit, dirty-worktree state, executable hash, recipe hash,
  fixture/workload hash, backend/module generation, adapter identifiers,
  driver, operating system, build configuration, and profile mode.
- [ ] Record power/clock policy, target refresh, thermal notes where available,
  process priority, and competing-workload warnings.
- [ ] Record warmup, stability, capture, drain, and total process intervals
  separately.
- [ ] Report sample count, p50, p90, p95, p99, worst, mean, standard deviation,
  median absolute deviation, allocation totals, operation counts, and
  throughput where meaningful.
- [ ] Use repeated A/B or A/B/B/A ordering for comparisons to reduce thermal
  and temporal bias.
- [ ] Define minimum repetition, variance, absolute-budget, and relative-
  regression rules for component results.
- [ ] Reject comparison when recipe, fixture, mode, hardware, driver, output,
  instrumentation, or required extension manifests are incompatible.
- [ ] Keep accepted baselines immutable unless an explicit accept action
  validates and replaces them.
- [ ] Retain raw frame streams, CPU spans, GPU queries, summaries, manifests,
  optional traces, validation logs, and optional images/captures under one
  bounded run root.
- [ ] Add a compact component scoreboard showing target cost, full-frame share,
  theoretical opportunity, measured improvement, and broader-lane result.

Acceptance criteria:

- [ ] A report distinguishes diagnostic explanation from clean comparison
  evidence.
- [ ] A result can be reproduced from its recipe and manifest.
- [ ] An incompatible or unstable result fails instead of producing a
  misleading delta.
- [ ] The scoreboard prevents a large percentage win on a negligible
  component from outranking a smaller win with greater full-frame impact.

## Phase 8 - Optimization Promotion Ladder

For every component optimization:

- [ ] Establish a before baseline in the component fixture.
- [ ] Change one architectural variable at a time.
- [ ] Pass component correctness, operation-count, allocation, and stability
  gates.
- [ ] Demonstrate the component improvement with repeated compatible runs.
- [ ] Run the nearest subsystem fixture.
- [ ] Run the complete presentationless frame.
- [ ] Run the required desktop WSI cohort.
- [ ] Run the required OpenXR/RVC cohort when the change affects XR.
- [ ] Run validation and synchronization-validation correctness cohorts
  separately from performance captures.
- [ ] Reject or revise the change if it moves cost to another stage, increases
  tail latency, reduces useful CPU/GPU overlap, adds churn/readbacks/fallbacks,
  or regresses a broader lane.
- [ ] Record accepted and rejected attempts with evidence paths.

Acceptance criteria:

- [ ] No optimization is promoted solely from a microbenchmark.
- [ ] The final result states both local component savings and whole-frame
  savings.
- [ ] Required desktop and XR budgets remain owned by workstream 01 and the
  shared acceptance closeout.

## Phase 9 - Tests, Documentation, And Operationalization

- [ ] Add unit tests for recipe parsing, capability requirements, target
  selection, state-machine transitions, manifest compatibility, statistics,
  and regression verdicts without requiring a GPU.
- [ ] Add deterministic plan/fixture tests for expected work counts and output
  identity.
- [ ] Add Vulkan integration tests for presentationless creation, render,
  submit, completion, resize/recreate where applicable, and teardown.
- [ ] Add validation and synchronization-validation tests for offscreen image
  transitions and resource retirement.
- [ ] Add MCP integration tests for prepare, arm, capture, cancellation,
  timeout, result retrieval, and missing capabilities.
- [ ] Add session-manager tests proving one named process cannot stop or reuse
  another session's PID or port.
- [ ] Add a short Quick component preset for developer feedback and repeated
  Compare/Gate presets for stable hardware.
- [ ] Add CI regression enforcement only after a controlled hardware runner
  demonstrates acceptable variance.
- [ ] Update the profiler guide, MCP documentation, renderer architecture,
  launch documentation, environment-variable catalog, JSONC schema, and
  `docs/work/README.md`.
- [ ] Document how to select component, presentationless, WSI, and OpenXR
  evidence and how not to compare them.

Acceptance criteria:

- [ ] A developer or MCP client can launch, profile, collect, compare, and stop
  one deterministic component recipe from a clean shell.
- [ ] The same workflow can escalate from a component fixture to
  presentationless, desktop, and OpenXR evidence.
- [ ] Documentation clearly identifies intrusive modes and observer overhead.
- [ ] Tests and manifests fail visibly when a requested accelerated or
  profiling capability is unavailable.

## Suggested First Vertical Slice

Implement the smallest end-to-end slice before broadening the target catalog:

1. Presentationless Vulkan host at a fixed 1920x1080 extent.
2. Dedicated `XREngine.RenderBench` process and named MCP session launcher.
3. One static Deferred fixture.
4. One `SecondaryRecording` recipe with configurable chain, draw, and worker
   counts.
5. MCP prepare, arm, status, stop, and result tools.
6. Existing aggregate CPU stages plus targeted per-worker secondary spans.
7. Existing coarse GPU timing, with no dense timestamps in this slice.
8. Component, full presentationless, and desktop comparison reports.

This slice proves the host, MCP isolation, deterministic recipe, measured-frame
silence, component attribution, and promotion ladder before adding calibrated
timestamps, hardware counters, or a large fixture matrix.

## Final Completion Gate

- [ ] Vulkan backend metadata truthfully matches implemented target modes.
- [ ] Presentationless Vulkan rendering works without `XRWindow`, a native
  surface, or a swapchain.
- [ ] MCP runtime profiler tools work without the editor and without a world
  for synthetic fixtures.
- [ ] Recipes isolate primary recording, secondary recording, resource
  planning, descriptor publication, barrier emission, queue submission, and at
  least one GPU render pass.
- [ ] Targeted CPU and GPU instrumentation has measured observer overhead and
  does not contaminate clean promotion captures.
- [ ] CPU/GPU correlated traces are available where calibrated timestamps are
  supported.
- [ ] Component results escalate through subsystem, presentationless, desktop,
  and required OpenXR gates.
- [ ] A complete artifact bundle and compatible baseline comparison can be
  produced from one bounded command or MCP job.
- [ ] The workflow has demonstrated at least one accepted optimization whose
  component improvement also reduces full-frame p95 without regressing
  correctness, allocation, readback, churn, or tail-latency gates.

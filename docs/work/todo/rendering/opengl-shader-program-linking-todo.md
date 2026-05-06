# OpenGL Shader Program Linking TODO

Last Updated: 2026-05-06
Status: Planning from design/code audit

Source design:
[OpenGL Shader Program Linking Design](../../design/rendering/opengl-shader-program-linking-design.md)

## Goal

Make OpenGL shader program creation a predictable background build/cache
pipeline instead of render-thread first-use work. The target is no unbounded
`XRWindow.ProcessPendingUploads` stalls during normal editor/game interaction,
with enough diagnostics to explain every cache miss, fallback, timeout, and
failed link.

This is a v1 cleanup area. Internal shader APIs, cache formats, backend
selection helpers, and render-backend plumbing may change when the result is
clearer and easier to validate.

## Audit Snapshot

The current implementation already contains several pieces from the design:

- `OpenGLRenderer.ConfigureParallelShaderCompile` probes
  `GL_ARB/KHR_parallel_shader_compile`, sets the driver compiler thread count,
  and records startup state.
- `GLSharedContext`, `GLProgramBinaryUploadQueue`, and
  `GLProgramCompileLinkQueue` provide a shared-context worker path.
- `GLRenderProgram.Link()` is restartable enough to return pending and be
  advanced by `GLRenderProgram.PollPendingAsyncPrograms(...)`.
- Driver-parallel polling uses `GL_COMPLETION_STATUS` before querying final
  link status.
- Known hazardous program shapes are represented by
  `GLRenderProgram.IsKnownAsyncLinkHazard`, currently `shaderCount <= 1` or
  any compute shader.
- `PollPendingAsyncPrograms` has a soft 4 ms sync-work budget and a per-frame
  program-count cap.
- Program binaries are written to disk and uniform metadata is cached beside
  the binary.

Important gaps compared with the design:

- Binary cache identity is still mostly `sourceHash-format-GLVersion`.
  Vendor, renderer, driver/build identity, shader stage topology, separable
  mode, compile defines, variant metadata, and cache schema are not represented
  as a structured fingerprint.
- `glProgramBinary` success is treated mostly as "no GL error"; the engine
  should query final link status after binary upload/load completes and delete
  the entry on link failure.
- Cache metadata restores uniform reflection shape, not a full runtime binding
  contract. We still need to audit default-block uniforms, sampler bindings,
  image bindings, and engine/material rebinding after binary load.
- The source compile/link queue and binary upload queue share one
  `GLSharedContext` worker. One stuck source link can still starve binary
  uploads and unrelated shader work.
- The shared-context queue has no hard per-job timeout or worker-health model.
  If a GL call blocks inside the worker, the queue cannot recover by polling.
- Hot reload still destroys and regenerates the active program in place.
  The design target is clone-and-swap with previous/fallback programs kept
  alive until the replacement is ready.
- `XRRenderProgram.ShaderProgramBackendStatus` exists, but GL linking does not
  consistently publish backend status for cache load, queue wait, compile,
  link, ready, failed, or abandoned states.
- Program pipeline validation is called from the pipeline use path. Successful
  validations are not memoized, so valid pipelines can be revalidated during
  normal rendering.
- `AsyncShaderPipelineBenchmarks` and
  `AsyncShaderPipelineFrameBudgetHarness` exist, but they currently exercise a
  narrow fixed set of sync/shared-context compile-link and binary-upload cases.
  They do not yet compare all engine link strategies, larger shader/program
  batches, queue saturation, producer parallelism, driver-parallel completion
  polling, or `XRWindow.ProcessPendingUploads` stall attribution.
- The feature doc
  [OpenGL Program Linking Strategies](../../../features/opengl-program-linking.md)
  currently disagrees with code and the design about hazard routing. The code
  excludes hazards from the shared-context source queue; the feature doc says
  hazards use that queue when available.
- Settings and enum comments still describe `Auto` as preferring the
  shared-context queue, while the current implementation uses driver-parallel
  when the startup probe passes and falls back to shared-context when it does
  not.

## Implementation Plan

## Phase 0 - Branch, Baseline, And Contract Alignment

- [ ] Create a dedicated branch for this todo list and implementation series.
- [ ] Capture the current behavior matrix for:
  `Auto`, `SharedContext`, `DriverParallel`, `Synchronous`,
  `AsyncProgramCompilation`, `AsyncProgramBinaryUpload`, and
  `AllowBinaryProgramCaching`.
- [ ] Update stale comments and docs so they match current code before deeper
  edits:
  - `docs/features/opengl-program-linking.md`
  - `EOpenGLShaderLinkStrategy`
  - `Engine.Rendering.Settings.OpenGLShaderLinkStrategy`
  - `GLRenderProgram.IsKnownAsyncLinkHazard`
- [ ] Decide and document whether `Synchronous` disables async binary upload,
  or whether it only means synchronous source compile/link.
- [ ] Add a small backend-selection test seam so the decision tree can be unit
  tested without requiring a live GL context.

Acceptance:

- [ ] Docs and code comments agree on actual hazard routing.
- [ ] A reviewer can read one backend-selection helper and understand why a
  program uses binary load, driver-parallel, shared-context, or synchronous
  fallback.

## Phase 1 - Conservative Binary Cache Fingerprints

- [ ] Replace the loose filename key with a structured cache key/value model:
  - resolved source or SPIR-V hash including includes,
  - ordered shader stage set,
  - separable vs monolithic topology,
  - generated variant hash and compile defines,
  - binary cache policy,
  - engine shader ABI/cache schema version,
  - OpenGL version,
  - vendor,
  - renderer,
  - driver/build string when available,
  - binary format token.
- [ ] Store metadata as structured JSON next to the binary instead of relying
  on filename parsing and tab-delimited uniform metadata.
- [ ] Keep a cache schema version constant near the cache implementation and
  bump it when the shader ABI or metadata contract changes.
- [ ] Move cache root selection behind a helper. Decide between current working
  directory, `Build/Cache`, and per-user app data, then document the choice.
- [ ] Add stale-entry cleanup for engine/schema/driver changes and failed
  `glProgramBinary` loads.
- [ ] Add tests for:
  - include changes invalidating the source hash,
  - stage order/type/topology changing the key,
  - vendor/renderer/version/fingerprint changes missing the old entry,
  - corrupt metadata causing source fallback rather than a crash.

Acceptance:

- [ ] No binary cache entry is reused across a different driver/runtime
  fingerprint.
- [ ] Cache metadata can be inspected and debugged without parsing filenames.
- [ ] Failed binary loads delete only the matching bad entry and fall back to
  source.

## Phase 2 - Binary Load Correctness And Runtime Rebinding

- [ ] After every async and sync `glProgramBinary` load, query final
  `GL_LINK_STATUS` once the upload/load job has completed.
- [ ] On failed binary link status, capture the info log when safe, delete the
  cache entry, clear pending state, and fall back to source.
- [ ] Audit which runtime state `glProgramBinary` resets for our usage:
  default-block uniforms, sampler uniforms, image units, UBO/SSBO bindings,
  explicit attribute locations, and program separable state.
- [ ] Promote uniform metadata restoration into a broader
  "program runtime binding restore" step, or explicitly prove that existing
  per-draw material/engine binding paths always rebuild the state before use.
- [ ] Add focused tests around binary round-trip with:
  - default-block uniforms,
  - sampler uniforms,
  - image/SSBO binding expectations,
  - separable program flag/topology.

Acceptance:

- [ ] A binary-loaded program renders with the same material and engine binding
  behavior as a source-linked program.
- [ ] Corrupt or incompatible binaries never mark `GLRenderProgram.IsLinked`
  true.

## Phase 3 - Observable Program Backend State

- [ ] Publish `XRRenderProgram.ShaderProgramBackendStatus` for all GL paths,
  not just Uber variant telemetry:
  - cache lookup,
  - binary cache hit/miss,
  - binary upload pending/ready/failed,
  - source compile queued,
  - queue backpressure,
  - driver-parallel compile/link pending,
  - synchronous fallback,
  - ready,
  - failed,
  - abandoned.
- [ ] Add a compact telemetry record for each program build containing program
  name, hash/fingerprint, stage set, topology, selected backend, queue latency,
  compile time, link time, binary load time, reflection time, and failure
  reason.
- [ ] Count per-frame synchronous shader work and report it through logs and
  profiler traces.
- [ ] Add queue metrics for in-flight count, enqueue failures/backpressure,
  oldest pending age, completed count, failed count, and abandoned count.
- [ ] Surface backend status to material/editor tooling without exposing GL
  handles above the backend layer.

Acceptance:

- [ ] Logs can answer why a specific material is not visible yet.
- [ ] `profiler-render-stalls.log` entries under
  `XRWindow.ProcessPendingUploads` can be tied back to program names and
  backend lanes.

## Phase 4 - Shared-Context Worker Hardening

- [ ] Split binary upload and source compile/link onto independent workers or
  independent shared contexts so a stuck source link cannot block binary
  uploads.
- [ ] Add queue-level hazard policy. The queue should reject known hazardous
  jobs directly, not rely only on callers to filter them correctly.
- [ ] Add watchdog instrumentation around each worker job. Because a blocking
  GL call cannot be interrupted safely, define an explicit worker-unhealthy
  state and recovery policy.
- [ ] Add hard timeout behavior for jobs that can still poll completion
  cooperatively.
- [ ] Avoid worker-side `GL_LINK_STATUS`, reflection, validation, or binary
  capture queries until completion is known for any path that used
  driver-parallel compile.
- [ ] Rename the shared context thread from "XR Program Binary Loader" to a
  neutral name or per-lane names.
- [ ] Add tests for queue backpressure and worker isolation. Keep GPU tests
  inconclusive on headless CI.

Acceptance:

- [ ] A wedged source compile/link lane cannot starve program binary upload.
- [ ] Queue starvation is detectable from logs and telemetry.

## Phase 5 - Clone-And-Swap Program Lifecycle

- [ ] Introduce an immutable completed-program result that can carry a fresh GL
  program handle plus reflection/runtime-binding metadata.
- [ ] Build new program handles for source links and binary loads instead of
  relinking or binary-loading the currently active handle in place.
- [ ] Swap active handles only at frame-safe points on the render thread.
- [ ] Keep the previous linked handle alive while a hot reload replacement is
  pending or failed.
- [ ] Defer old-handle deletion until no draw, pipeline, or deferred cleanup can
  reference it.
- [ ] Replace `Relink()` destroy/generate behavior with clone-and-swap.
- [ ] Update material/mesh code so a not-yet-ready replacement can continue to
  render with the previous program or an explicit fallback.

Acceptance:

- [ ] Shader hot reload does not blank a material while the replacement is
  compiling/linking.
- [ ] Program handles are never destroyed while active program pipelines or
  queued draws can still reference them.

## Phase 6 - Separable Program And Pipeline Policy

- [ ] Inventory shader families that should actually use separable programs:
  generated vertex stages, material fragments, editor hot-reload cases, and
  high-variant Uber paths.
- [ ] Keep monolithic programs for renderer-critical paths with bounded
  permutations and for shapes that remain driver-hazardous.
- [ ] Require explicit `layout(location=...)` stage interfaces for separable
  shader families.
- [ ] Require explicit `layout(binding=...)` or engine-owned binding maps for
  resources in separable families.
- [ ] Cache render-context-local program pipeline objects by stage-program set.
- [ ] Validate pipelines only in debug, warm-up, or cache-build paths. Memoize
  successful validation so normal rendering does not call validation every
  frame.
- [ ] Add shader contract tests for separable stage interface drift.

Acceptance:

- [ ] Program pipeline objects are treated as render-context-local containers.
- [ ] Pipeline validation is not part of the steady-state frame loop.

## Phase 7 - Shader Stress Validation

- [ ] Add a Unit Testing World shader stress scenario with many material
  variants, imported-model-like programs, compute programs, and hot reload.
- [ ] Add a task or harness for:
  - empty-cache cold start,
  - warm start with valid cache,
  - simulated driver/schema invalidation,
  - binary load failure,
  - shared-context unavailable,
  - parallel shader compile unavailable,
  - NVIDIA hazard-shape routing,
  - queue saturation/backpressure.
- [ ] Use the Phase 8 benchmark suite as validation evidence for shader
  warm-up speed, stall behavior, capacity limits, and strategy comparisons.
- [ ] Validate that `XRWindow.ProcessPendingUploads` does not exceed the
  configured sync budget except for explicitly logged unavoidable work.

Acceptance:

- [ ] Cold starts degrade gradually instead of freezing the editor/game window.
- [ ] Warm starts mostly use valid binary cache entries.
- [ ] Hot reload keeps previous programs visible until replacements are ready.
- [ ] Known hazardous programs never enter the driver-parallel or shared-source
  queue lanes.

## Phase 8 - Benchmark Integration And Capacity Profiling

- [ ] Expand `AsyncShaderPipelineBenchmarks` into a strategy matrix for:
  - `Auto`,
  - `SharedContext`,
  - `DriverParallel`,
  - `Synchronous`,
  - binary-cache hit via sync `glProgramBinary`,
  - binary-cache hit via async upload,
  - cache miss source compile/link.
- [ ] Parameterize benchmark inputs by program topology:
  - single-stage separable vertex,
  - single-stage separable fragment,
  - single-stage compute,
  - two-stage vertex+fragment monolithic,
  - three-stage vertex+geometry+fragment,
  - tessellation vertex+tess-control+tess-eval+fragment,
  - generated Uber-like fragment variants,
  - imported-model-like material permutations.
- [ ] Parameterize shader complexity:
  - trivial pass-through,
  - medium material shader,
  - heavy Uber-style branch/feature shader,
  - include-heavy shader source,
  - compile-define variant fanout,
  - deliberate compile failure,
  - deliberate link/interface failure.
- [ ] Parameterize simultaneous program counts:
  - `1`,
  - `2`,
  - `4`,
  - `8`,
  - `16`,
  - `32`,
  - `64`,
  - `MaxInFlight - 1`,
  - `MaxInFlight`,
  - `MaxInFlight + 1`,
  - `2 * MaxInFlight`.
- [ ] Add producer-parallelism scenarios where multiple CPU threads request
  link work concurrently for unique hashes and for duplicate hashes. Measure
  whether `InFlightCompilations` deduplicates duplicate hashes without blocking
  unrelated unique programs.
- [ ] Add queue-capacity benchmarks for each async lane:
  - enqueue cost while empty,
  - enqueue cost while at capacity,
  - backpressure retry latency,
  - in-flight high-water mark,
  - completion throughput in programs/sec,
  - oldest pending job age,
  - fairness between source compile/link jobs and binary upload jobs.
- [ ] Extend `AsyncShaderPipelineFrameBudgetHarness` so each scenario runs as a
  frame loop and records:
  - p50, p95, p99, and p99.9 frame time,
  - max frame time,
  - frames over 8.3 ms, 16.6 ms, and 33.3 ms,
  - `XRWindow.ProcessPendingUploads` wall time when the full engine path is
    used,
  - number of sync fallback links per frame,
  - queue depth per frame,
  - completed/failed/abandoned programs per frame,
  - time from request to first successful use.
- [ ] Add an engine-integrated benchmark mode that creates real
  `XRRenderProgram` and `GLRenderProgram` instances instead of calling the
  low-level queue APIs directly. It should exercise the same public path used
  by materials and mesh renderers, including `BeginPrepareLinkData`,
  `Link()`, `PollPendingAsyncPrograms`, binary cache lookup, reflection, and
  uniform metadata restoration.
- [ ] Add a cache warm/cold benchmark pair:
  - empty cache source compile/link,
  - valid warm cache binary load,
  - stale fingerprint source fallback,
  - corrupt binary fallback,
  - metadata missing fallback/reflection rebuild.
- [ ] Add a driver-parallel benchmark path that polls
  `GL_COMPLETION_STATUS` and separately reports:
  - compile dispatch cost,
  - compile completion latency,
  - link dispatch cost,
  - link completion latency,
  - post-completion status/reflection/binary-capture cost,
  - abandon timeout count.
- [ ] Add a shared-context benchmark path that reports:
  - main-thread enqueue cost,
  - worker compile time,
  - worker link time,
  - worker `glFinish` time,
  - enqueue-to-ready latency,
  - render-thread finalization/reflection/binary-capture cost.
- [ ] Add a synchronous fallback benchmark path that reports total render-thread
  time for compile, attach, link, status query, reflection, binary capture,
  detach, and cleanup.
- [ ] Emit benchmark artifacts as Markdown and machine-readable JSON under
  `BenchmarkDotNet.Artifacts/results/`, with GPU/vendor/driver/OpenGL version,
  link strategy, shader topology, shader count, program count, queue capacity,
  and cache fingerprint in every result row.
- [ ] Add VS Code and/or `ExecTool` entries for:
  - the fast smoke benchmark,
  - the full strategy matrix,
  - the frame-budget/stall harness,
  - the GPU-driver local-only stress run.
- [ ] Add benchmark result thresholds for regression checks:
  - async enqueue should remain below a small main-thread budget,
  - frame-budget scenarios should not exceed agreed p99/p99.9 limits,
  - queue capacity should produce backpressure instead of sync fallback unless
    explicitly configured,
  - duplicate-hash requests should not spawn duplicate source links.
- [ ] Document which benchmark scenarios are CI-safe and which require a local
  Windows GL 4.6 driver. GPU-driver-local scenarios should skip or report
  inconclusive in headless CI.

Acceptance:

- [ ] We can compare source compile/link speed and stall behavior across
  `Auto`, `SharedContext`, `DriverParallel`, and `Synchronous` for the same
  shader/program matrix.
- [ ] Benchmark output makes queue capacity and parallelism visible rather than
  relying on ad hoc profiler inspection.
- [ ] Large bursts of shader work show bounded render-thread cost in async
  strategies and clearly identify any synchronous fallback.
- [ ] Warm-cache benchmarks prove whether binary loading is faster than source
  linking on the current driver and whether it avoids visible frame spikes.

## Finalization

- [ ] Run the narrowest useful validation:
  - `dotnet build XRENGINE.slnx`
  - targeted rendering/unit tests for cache keys, queue behavior, and shader
    contracts
  - GPU integration tests locally where a GL 4.6 driver is available
  - the shader stress Unit Testing World scenario
  - `dotnet run -c Release --project XREngine.Benchmarks -- --filter *AsyncShaderPipeline*`
  - `dotnet run -c Release --project XREngine.Benchmarks -- --frame-budget`
- [ ] Update related docs:
  - `docs/features/opengl-program-linking.md`
  - `docs/architecture/rendering/opengl-renderer.md`
  - `docs/architecture/secondary-gpu-context.md`
  - `docs/architecture/rendering/uber-shader-varianting.md`
- [ ] Include validation results, remaining driver risks, and cache migration
  notes in the PR summary.
- [ ] Merge the dedicated branch back into `main` after completion and
  validation.

## Code Touchpoints

- `OpenGLRenderer.ParallelShaderCompile.cs`: extension detection, startup
  probe, compiler thread count, hazard suppression.
- `GLRenderProgram.Linking.cs`: backend selection, async polling, hazard
  routing, sync fallback, hot reload lifecycle.
- `GLRenderProgram.BinaryCache.cs`: cache key, metadata, persistence, binary
  load/delete behavior.
- `GLProgramBinaryUploadQueue.cs`: async binary upload results and validation.
- `GLProgramCompileLinkQueue.cs`: shared-context source compile/link worker.
- `GLSharedContext.cs`: worker lifecycle and queue execution.
- `GLRenderProgramPipeline.cs`: separable pipeline ownership and validation.
- `XRRenderProgram.cs`: backend status and variant metadata surface.
- `GLMeshRenderer.Shaders.cs`: combined vs separable program creation and
  first-use link requests.
- `AsyncShaderPipelineTests.cs`: GPU integration coverage for shared contexts,
  queues, binaries, and compile/link behavior.
- `AsyncShaderPipelineBenchmarks.cs`: BenchmarkDotNet strategy, topology,
  batch-size, queue-capacity, and producer-parallelism measurements.
- `AsyncShaderPipelineFrameBudgetHarness.cs`: frame-loop stall and percentile
  reporting for shader compile/link and binary upload scenarios.
- `EngineProfilerDataSource.cs`: editor/profiler display path for benchmark
  and runtime stall metrics when exposing results in tooling.

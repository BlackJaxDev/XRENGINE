# OpenGL Program Linking

XRENGINE treats OpenGL program creation as a background build/cache pipeline,
not as ordinary first-use render work. The goal is to keep the editor and game
window responsive while shader variants, hot reloads, imported-material
permutations, compute programs, and cached binaries move through predictable
backend lanes.

The implementation lives mainly in:

- [OpenGLShaderLinkBackendSelector.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLShaderLinkBackendSelector.cs)
- [GLRenderProgram.Linking.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs)
- [GLRenderProgram.BinaryCache.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.BinaryCache.cs)
- [GLShader.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLShader.cs)
- [GLProgramCompileLinkQueue.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLProgramCompileLinkQueue.cs)
- [GLProgramBinaryUploadQueue.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLProgramBinaryUploadQueue.cs)
- [GLSharedContext.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLSharedContext.cs)

## Runtime Roles

The render thread owns visible windows, the primary OpenGL context, active
program handles, separable program pipeline objects, and frame-safe handle
swaps. It polls program work during `OpenGLRenderer.ProcessPendingUploads`.

Shared-context workers run blocking driver work off the render thread:

- `XR Program Binary Upload` loads cached binaries with `glProgramBinary`.
- `XR Program Source Compile` compiles, attaches, links, validates, detaches,
  and hands linked source programs back to the render context.
- `XR GL Shared Uploads` remains separate for general shared uploads.

Binary upload and source compile/link are intentionally split so a wedged
source link cannot starve warm-cache binary uploads.

## Configuration

Source compile/link selection is controlled by
`Engine.Rendering.Settings.OpenGLShaderLinkStrategy`
([EOpenGLShaderLinkStrategy](../../XREngine.Data/Core/Enums/EOpenGLShaderLinkStrategy.cs)).

| Strategy | Behavior |
| --- | --- |
| `Auto` | Prefer driver-parallel compile/link when the startup probe passes; otherwise use the shared-context source queue when available. Known async-link hazards bypass async source lanes. |
| `SharedContext` | Compile and link non-hazard source programs on the shared-context source worker. Queue-level hazard rejection still applies. |
| `DriverParallel` | Use `GL_ARB_parallel_shader_compile` or `GL_KHR_parallel_shader_compile` after the extension/probe gate. If unavailable, fall back to shared context, then synchronous. |
| `Synchronous` | Compile and link source programs on the render thread. This does not disable async binary uploads; `AsyncProgramBinaryUpload` controls that lane. |

Related settings:

- `AsyncProgramCompilation` gates async source lanes.
- `AllowBinaryProgramCaching` enables binary cache lookup and writes.
- `AsyncProgramBinaryUpload` controls the shared-context binary upload lane.
- `OpenGLParallelShaderCompileProbeEnabled` and
  `OpenGLParallelShaderCompileProbeTimeoutMs` control the startup probe.
- `OpenGLShaderCompilerThreadCount` configures driver shader compiler threads.
- `MaxAsyncShaderProgramsPerFrame` caps how many pending program builds are
  advanced per frame.

## Backend Decision

`GLRenderProgram.Link()` computes the program source hash and cache key, then
delegates lane selection to `OpenGLShaderLinkBackendSelector`.

In simplified form:

```text
if binary cache hit:
    use async binary upload if available and below capacity
    otherwise load binary synchronously
else if hash was marked failed:
    fail before source compile/link
else if async source compilation is disabled or strategy is Synchronous:
    compile/link source synchronously
else if program is a known driver-parallel hazard:
    compile/link source synchronously with driver-parallel suppression
else if strategy is Auto and driver-parallel probe passed:
    use driver-parallel source compile/link
else if selected/fallback shared-context source queue is available:
    enqueue source compile/link unless the queue is at capacity
else:
    compile/link source synchronously
```

Failed source hashes are negative-cached by resolved source hash. Repeated
instances of the same broken program skip source compile/link after the cache
lookup path confirms there is no usable binary.

Queue capacity is reported as backpressure. The previous linked program remains
active and the pending build is retried in later frames.

## Known Hazards

`GLRenderProgram.IsKnownAsyncLinkHazard` is the canonical hazard predicate.
Known hazards are:

- programs with one attached shader, including single-stage separable programs,
- any program containing a compute shader.

These shapes are denied driver-parallel source linking and shared-context
source linking. They fall back to the guarded synchronous path until the engine
has stronger pending-program safeguards for those topologies.

If a hazard reaches synchronous source linking while driver-parallel compile is
active, the link is wrapped in
`TryDisableParallelShaderCompileForHazardousLink` /
`RestoreParallelShaderCompile`. That temporarily requests zero driver shader
compiler threads around the link so status queries do not wait on a wedged
parallel-link worker.

This policy is conservative and tuned from NVIDIA GL 4.6 driver behavior. The
engine avoids async source lanes for hazardous shapes because pending status
queries and cross-context links for those shapes have been observed to wedge or
stall unpredictably.

## Binary Cache

Successful source links can write local program binaries under:

```text
Build/Cache/OpenGL/ShaderPrograms/
```

Each entry has a `.bin` payload and a `.bin.json` metadata file. Cache keys and
metadata include:

- cache schema version,
- resolved shader source hash, including include-expanded source,
- ordered shader stage topology,
- separable vs monolithic topology,
- shader variant metadata and binary-cache policy,
- OpenGL version, vendor, renderer, and GLSL version,
- driver-returned binary format.

`GLRenderProgram.ReadBinaryShaderCache(api)` loads the cache after OpenGL
startup records the runtime fingerprint. Stale schema entries, missing payloads,
corrupt metadata, runtime-fingerprint mismatches, and failed `glProgramBinary`
loads delete only the affected cache entry and fall back to source.

Both sync and async binary loads validate final `GL_LINK_STATUS` after
`glProgramBinary`. A binary cache hit cannot mark a program linked unless the
driver accepts the binary and reports a successful link. After a binary load,
runtime binding state is restored from cached uniform metadata or active-uniform
reflection.

## Clone And Swap

Hot reloads and relinks build into a replacement GL program handle. The
previous linked handle remains active until the replacement has successfully
loaded or linked, reflected runtime binding state, and reached a frame-safe
adoption point.

If a replacement fails, stalls, hits queue backpressure, or is abandoned, the old
program remains renderable. Old handles are deferred for deletion for at least
two render frames so queued draws and program pipelines cannot reference a
destroyed handle.

## Separable Programs

Separable program pipelines are render-context-local containers. Pipeline
validation is compiled out for non-debug/non-editor builds, and successful
debug/editor validations are memoized so steady-state rendering does not
revalidate known-good pipelines every frame.

Use separable programs when stage reuse or hot reload materially reduces
permutation cost. Keep monolithic programs for renderer-critical paths with
bounded permutations or known hazardous shapes. Separable shader families should
use explicit stage interface locations and explicit resource bindings.

## Frame Pump

`OpenGLRenderer.ProcessPendingUploads` calls:

```csharp
GLRenderProgram.PollPendingAsyncPrograms(
    Engine.Rendering.Settings.MaxAsyncShaderProgramsPerFrame);
```

The pump advances async binary uploads, shared-context source results,
driver-parallel `GL_COMPLETION_STATUS` polling, queue-backpressure retries,
and deferred old-program deletes. Synchronous fallback work is budgeted so a
cold-start or import burst does not drain an unbounded shader backlog in one
frame.

## Rendering Log Diagnostics

Verbose shader-link diagnostics are written to `log_rendering.txt` through the
rendering log category when output verbosity is `Verbose`. The OpenGL log still
contains the shorter cache/backend messages, but the rendering log is the place
to diagnose stalls and source/binary size.

Important records:

- `[ShaderLink]` records build-lane decisions, program name, hash, GL program
  id, backend, separable mode, hazard status, shader count, shader types,
  source bytes, source lines, binary bytes, binary format, fingerprint, frame,
  and whether the event ran on the render thread.
- `[ShaderBackend]` records final ready/failed telemetry with queue, compile,
  link, binary-load, and reflection timings plus shader count, stage list,
  source bytes, and source lines.
- `[ShaderGLCall]` records measured GL calls related to shader compile,
  program link, attach/detach, program binary upload, binary capture, deletion,
  and parallel-compile configuration. When the call runs on the render thread,
  `renderThreadStallMs` equals elapsed time; worker-context calls still report
  elapsed time but `renderThreadStallMs=0`.
- `[ShaderLinkQueue]` records shared-context source queue enqueue, rejection,
  backpressure, worker start, worker ready, and worker failure events.
- `[ShaderBinaryUpload]` records async binary upload enqueue, worker start,
  worker ready, and worker failure events.

The instrumented GL calls include program creation/configuration,
`glShaderSource`, `glCompileShader`, shader completion/status queries,
`glAttachShader`, `glLinkProgram`, program completion/status queries,
`glGetProgramInfoLog`, `glProgramBinary`, `glGetError`, binary capture,
`glDetachShader`, shader/program deletion, `glFinish`, and scoped
`glMaxShaderCompilerThreads*` changes around hazardous links.

Example records:

```text
[ShaderLink] SOURCE_BACKEND_SELECTED program='ForwardUber' hash=123 programId=47 backend=DriverParallelSource separable=False hazard=False shaderCount=2 shaderTypes=VertexShader|FragmentShader sourceBytes=84231 sourceLines=2140 binaryBytes=0 binaryFormat=<none> fingerprint=<none> frame=128 renderThread=True detail='auto selected driver-parallel after startup probe'.
```

```text
[ShaderGLCall] call=glLinkProgram program='ForwardUber' hash=123 programId=47 separable=False elapsedMs=18.423 renderThread=True renderThreadStallMs=18.423 detail='backend=SynchronousSource'.
```

Use `profiler-render-stalls.log` to identify frames whose leaf scope is
`XRWindow.ProcessPendingUploads`, then correlate that frame with `[ShaderLink]`,
`[ShaderBackend]`, and `[ShaderGLCall]` lines in `log_rendering.txt`.

## Validation

Useful validation paths:

- cold start with an empty shader cache,
- warm start with a valid cache,
- stale runtime fingerprint or cache schema bump,
- corrupt binary fallback,
- unavailable parallel shader compile extension,
- unavailable shared context,
- known single-stage and compute hazards,
- shader editor hot reload,
- imported model with many material variants,
- separable pipeline rebuild in debug/editor.

`XREngine.Benchmarks` contains shader pipeline smoke, full-matrix,
frame-budget, and local-stress benchmark entries. GPU-driver-local scenarios
require a Windows OpenGL 4.6 driver and should be treated as local validation
evidence rather than headless-CI pass/fail gates.

## Cold-Start Pipeline Architecture

The cold-start path is hardened against a number of interacting stalls that
surfaced in 2026-05-06 profiling. Each item below documents an invariant the
runtime now relies on.

### Failed-Hash Negative Cache

`GLRenderProgram.Failed` stores hashes that have failed to link, paired with a
`FailedHashDiagnostics` record (`FirstFailureTicks`, `LastLogTicks`,
`SkipCount`, `Reason`, `Label`, `StageList`, `Separable`). All failure call
sites — driver-parallel compile/attach failure, `AbandonStuckAsyncLink`,
`CompleteAsyncLink`, shared-context async compile/link failure, synchronous
source compile/link failure — funnel through `MarkHashFailed(reason)` so each
known-bad hash is recorded once with metadata.

The known-failed short-circuit runs in the cache-miss branch of `Link()`
**before** `BINARY_CACHE_MISS` is logged or `ShaderProgramSourceSummary` is
generated, so a repeated failed hash does not regenerate diagnostics every
frame. `PrepareLinkData()` also skips `PrepareCompileInputs()` when the hash
is failed and the binary cache missed, so retries do not re-resolve or
`InjectMissingGLPerVertexBlocks` shader source.

`EmitFailedHashSkipLog(cacheKey, programId)` emits one
`SOURCE_FAILED_SKIPPED` warning per hash per
`FailedHashSkipLogThrottleSeconds` (10 s). `SkipCount` accumulates between
emissions and is reported on the next throttled emission.

### Binary Cache Hit Fast Path

`GLRenderProgram.PrepareLinkData()` calls `PrepareCompileInputs()` only when
the binary cache lookup returned `isCached == false`. Warm cache hits do not
concatenate, GLSL-resolve, or `InjectMissingGLPerVertexBlocks` any shader
source on the prep job. The synchronous link path has a defensive lazy
`inputs ??= PrepareCompileInputs()` immediately before the source-build lane
so cache misses still get full inputs lazily on the build site.

`OpenGLShaderLinkBackendSelector` consults `CompileInputsReady` only for
source-compile lanes. Binary-cache-hit branches return before that flag is
read, so passing `CompileInputsReady = false` for cache hits is safe.

`TryResolveUberVariantHash` and `CollectShaderProgramSourceSummary` both
walk `_shaderCache` when `_preparedCompileInputs` is null/empty, so verbose
telemetry, Uber-variant fingerprint resolution, and `READY/FAILED` summary
lines still report the same data on cache-hit lanes.

### Binary Upload Queue Coalescing

`GLProgramBinaryUploadQueue` owns a `_inFlightCacheKeys`
`ConcurrentDictionary<string, byte>` plus a `_coalescedCount` counter. Public
surface: `bool TryReserveCacheKey(string)`, `void ReleaseCacheKey(string?)`,
`long CoalescedCount`, `int InFlightCacheKeyCount`.

The cache-hit branch in `GLRenderProgram.Link()` calls
`uploadQueue.TryReserveCacheKey(binProg.CacheKey)` immediately before
`EnqueueUpload`. If the reservation fails (a sibling instance already owns
the cacheKey), the call is treated as backpressure: it records
`RecordBackpressure()`, publishes `QueueBackpressure`, emits a
`BINARY_UPLOAD_COALESCED` event, registers as a pending async program, and
returns. The deferred caller retries next frame and typically hits the
populated cache by then.

The reservation lifetime is **from successful TryReserve to worker
completion (or worker failure)**: the worker lambda inside `EnqueueUpload`
calls `_inFlightCacheKeys.TryRemove(cacheKey, ...)` after writing
`_completed[programId]` — no leak on failure paths.

Each `GLRenderProgram` instance still owns a unique `programId` and receives
its own `glProgramBinary` upload. Coalescing only serializes simultaneous
duplicate uploads; total throughput is preserved while bursts spread across
frames.

Why this matters: the cold-start `Sponza` flood has a top duplicate cacheKey
of ~50 program instances. With `MaxInFlight = 32`, 18 jobs would still
trigger backpressure without coalescing; with the reservation, only one of
the 50 holds the cacheKey at any time, the queue stays unsaturated, each
upload completes faster, and siblings retry serially within a few frames.

### Render-Thread Pump Tunables

`OpenGLRenderer.ProcessPendingUploads` calls `GLRenderProgram.PollPendingAsyncPrograms(MaxAsyncShaderProgramsPerFrame)` per frame.

| Tunable | Default | Comment |
| --- | ---: | --- |
| `Engine.Rendering.Settings.MaxAsyncShaderProgramsPerFrame` | `16` | Caps async-poll completions per render frame. Higher values drain cold-start program floods faster; the synchronous-fallback budget below bounds worst-case render cost. |
| `GLProgramBinaryUploadQueue.MaxInFlight` | `32` | Bounds transient VRAM and prevents producer runaway. ~3 MB ceiling at typical program-binary sizes. |
| `PollPendingAsyncProgramsSyncBudgetMilliseconds` | `4` | Per-frame budget for any program that falls back to a render-thread link inside the pump. |

A 387-program cold flood drains in ~24 frames at the default pump cap,
versus ~97 frames at the previous `MaxAsyncShaderProgramsPerFrame = 4` and
`MaxInFlight = 8` defaults.

A multi-worker shared-context design is intentionally deferred: significant
code complexity plus driver-internal locking would limit gains. Re-evaluate
if a future cold-start run shows worst-program `queueMs` regressing above
500 ms.

## Shader-Asset Warm Pre-Render Path

`DefaultRenderPipeline` and `DefaultRenderPipeline2` constructors call
`WarmFirstRenderShaders()` next to the existing `WarmDeferredLightingShaders()`
to pre-deserialize shader assets that would otherwise be first-touched inside
`XRViewport.Render` and produce 300-500 ms render-thread stalls on cold cache.

Warmed assets:

- `Scene3D/MotionVectors.fs`
- `Common/DepthNormalPrePass.fs`
- `Scene3D/TemporalAccumulation.fs`
- `Common/UITextBatched.fs` + `Common/UITextBatched.vs`
- `Scene3D/ForwardPlus/LightCulling.comp` or `LightCullingStereo.comp`
  (selected by stereo state)

`ShaderHelper.WarmEngineShader` does not block; it `GetOrAdd`s a background
`Task<XRShader>` on the engine job system. Shader load failures still flow
through `RuntimeShaderServices` warnings and
`TryGetResolvedSource(..., logFailures: true)` in `LoadAndWarmEngineShaderAsync`.

When adding a new render pass that first-touches an engine shader on the
render thread, register it with `WarmFirstRenderShaders` rather than relying
on lazy materialization.

## Mesh Generation Queue Coordination

`GLMeshGenerationQueue` is the cold-start companion to the program linking
pipeline. It owns two `ConcurrentQueue<GLMeshRenderer>` lanes:

- `_priorityQueue` — render-pipeline meshes (FBO quads, cube maps), normally
  drained without a budget cap.
- `_normalQueue` — scene meshes, drained inside the per-frame budget.

### Tunables

| Tunable | Default | Comment |
| --- | ---: | --- |
| `FrameBudgetMs` | `10.0` | Per-frame work cap when not boosted. |
| `MaxNormalRenderersPerFrame` | `1` | Hard cap on first-use scene renderers per frame. First-use Sponza-class submesh prep runs 50-70 ms of synchronous OpenGL work that cannot be sliced inside one `ProcessRenderer` call; capping at 1 prevents two such overruns from compounding. |
| `MaxThrottledPriorityRenderersPerFrame` | `4` | Hard cap on render-pipeline meshes when priority generation is throttled (i.e. during startup boost). |
| `GenerateOverrunDeferThresholdMs` | `8.0` | If `GLMeshRenderer.Generate()` alone consumes more than this, the renderer returns `Pending` (requeue) instead of running `TryPrepareForRendering()` in the same frame. Bisects expensive first-use prep across two frames. |
| `MaxRetries` | `3` | Maximum generation attempts per renderer before giving up; retries reset on `DataChanged`. |

`BoostBudgetUntilDrained(boostedMs, normal?, throttledPriority?)`
temporarily widens the budget during cold-start floods and is automatically
restored when the queue drains.

### Material-Readiness Gate

`ProcessGeneration` calls `IsMaterialReadyForGeneration(renderer)` before
consuming a per-frame slot. Implementation:

```csharp
private static bool IsMaterialReadyForGeneration(GLMeshRenderer renderer)
{
    XRMaterial? material = renderer.MeshRenderer.Material;
    return material is null || material.IsUberVariantReadyForRendering();
}
```

`XRMaterial.IsUberVariantReadyForRendering()` returns `true` for non-uber
materials and for materials whose `ActiveUberVariant` is bound to a generated
variant fragment shader. It also returns `true` for `Failed` prep state so a
pathological material does not keep the queue blocked indefinitely.

When the gate returns `false`, the queue calls
`XRMaterial.RequestUberVariantPreparationIfNeeded()` (idempotent async kick
that no-ops for non-uber materials and for `Requested/Preparing/Compiling/
Ready/Active` states) and defers the renderer to a later frame **without
spending the slot**.

By the time the renderer is reprocessed, `Generate()`'s call to
`EnsureUberVariantPreparedForRendering` hits the active-variant early-out and
returns immediately, so the synchronous shader-source generation (`PrepareUberVariantImmediately`,
50-80 ms per first-use Sponza-class material) does not run on the render
thread.

`EnsureUberVariantPreparedForRendering` itself remains unchanged — callers
that genuinely need the synchronous behaviour still get it.

## Transient GL State Invalidation Guard

`GLObjectBase.OnPropertyChanged` participates in invalidation, which destroys
and re-creates the underlying GL object on the next prepare. The
`[TransientGLState]` attribute marks properties whose value flips during
normal frame work and must **not** trigger invalidation.

Implementation lives in `GLObjectBase`:

- `TransientGLStateAttribute` —
  `[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]`.
- `_transientPropertyCache` —
  `ConcurrentDictionary<Type, HashSet<string>>` populated by reflection.
- `OnPropertyChanged` returns early when `IsTransientProperty(propName)` is
  true.

`GLMeshRenderer.BuffersBound` is the canonical transient property; without
the tag, the per-frame `BuffersBound = false; ... BuffersBound = true;`
toggle flagged the GL VAO + program for invalidation each frame, producing
the historical `Combined:UIBatchTextMaterial` churn (hundreds of `READY`
events per cold start). The regression test
`GLMeshRenderer_BuffersBound_IsTaggedTransient` (in
`XREngine.UnitTests/Rendering/TransientGLStateAttributeTests.cs`) pins the
attribute on the property.

When a future GL wrapper introduces a similar transient flag, tag the
property with `[TransientGLState]` rather than overriding `OnPropertyChanged`
again.

## Runtime Facade Thread Affinity

`RuntimeEngineFacade` exposes three enqueue methods that the rendering
runtime layer uses to schedule work without depending on `Engine` directly:

| Method | Affinity | Use case |
| --- | --- | --- |
| `EnqueueAppThreadTask(Action[, reason])` | App thread | CPU-only callbacks (e.g. `LightProbeGrid.ApplyBackgroundBuild`). |
| `EnqueueMainThreadTask(Action[, reason])` | Render thread | GL-context work that has historically been "main thread". |
| `EnqueueRenderThreadTask(Action[, reason])` | Render thread | GL-context work, explicit. |

`IRuntimeRenderingHostServices.EnqueueAppThreadTask` is the host-side hook
that routes to `Engine.EnqueueAppThreadTask`, which schedules an `ActionJob`
/ `LabeledActionJob` with `JobAffinity.AppThread` (different queue from the
render thread). Forwarding `EnqueueAppThreadTask` to the render-thread
enqueue (the historical bug) produced 691 ms `PostRenderMainThreadJobs`
stalls under load and triggered the non-GPU-tagged render-thread job
warning. Always route CPU-only callbacks through the app-thread method.

## Cold-Start Acceptance Signals

The following baseline numbers came out of
`xrengine_2026-05-06_15-25-36_pid26248` after the linking-pipeline +
mesh-prep + warm-shader work landed. Use them as a reference for future
regression checks.

| Signal | Cold-start value |
| --- | ---: |
| Render stalls in `profiler-render-stalls.log` | 3 |
| `[GLMeshGenerationQueue] Slow renderer prep` warnings | 0 (down from 25) |
| Worst `XRWindow.ProcessPendingUploads` sample | 53 ms (down from 114 ms) |
| `BINARY_UPLOAD_BACKPRESSURE` events | 62 (down from 772 baseline) |
| `BINARY_UPLOAD_COALESCED` events | 33 |
| Worst single-program `queueMs` | <500 ms target |
| `MotionVectors.fs` / `LightCulling.comp` / `TemporalAccumulation.fs` / `UITextBatched.fs` first-touch stalls | absent |
| `Combined:UIBatchTextMaterial` `READY` events per cold start | 6 |

`ShaderProgramLifecycleDiagnostics` emits a single `[ShaderProgramSummary]`
line at renderer cleanup with counters for binary cache hits, misses, source
builds, source failures, failed-hash skips, slow link preparations, and
shared-context source queue submissions. The summary also pulls
upload-queue completed/failed/backpressure/coalesced totals from the bound
`GLProgramBinaryUploadQueue`. Use this line as the long-session sanity check.

## Render-Thread Stall Mitigations (2026-05)

A follow-up pass eliminated the residual render-thread stalls inside
`GLMeshRenderer.TryPrepareForRendering` that survived the original
cold-start linking-pipeline work. Working baseline before the pass
(`xrengine_2026-05-06_16-04-13_pid10680`):

- `XRWindow.ProcessPendingUploads` avg 13.5 ms over 81 samples, peak 98 ms.
- `Slow TryPrepareForRendering` warnings showed `getProgramsMs = 79-80 ms`
  for `FullscreenQuad:Material` / `GLMeshRenderer 56`, and a single
  `genProgramsAndBuffersMs = 58.60 ms` for `fabric_c`.
- ~12 slow renderer-prep events per cold start.

Final state (`xrengine_2026-05-06_16-44-14_pid34576`): zero
`Slow TryPrepareForRendering`, zero `Slow renderer prep`, zero
`TryPrepare deferred`, zero `getProgramsMs > 50`, zero
`genProgramsAndBuffersMs >= 8`. `profiler-render-stalls.log` shows zero
`ProcessPendingUploads`/`Link`/`Compile` stalls. Remaining stalls
(`GLTexture.UpdateProperty`, `AssetManager.DeserializeAsset`) are
unrelated to program linking.

### Defer-On-Overrun In `TryPrepareForRendering`

`GLMeshRenderer.TryPrepareForRendering(double deferOverrunBudgetMs)` is the
budgeted overload the mesh queue calls. Once
`ensureRenderSettingsMs + ensureMaterialStateMs + genProgramsAndBuffersMs`
exceeds the budget it sets `_lastPrepareResult = "DeferredOverrun"`,
records `_lastDeferOverrunMs`, emits a
`[GLMeshGenerationQueue] TryPrepare deferred` info log
(`deferOverrunMs`/`budgetMs`/`generateMs` plus renderer/material name),
and returns `false` *before* `GetPrograms`. The queue treats the result as
`QueueProcessResult.Pending` and requeues the renderer.

Tunable: `GLMeshGenerationQueue.TryPrepareOverrunDeferThresholdMs`,
default `8.0` ms (mirrors the existing `GenerateOverrunDeferThresholdMs`).
The parameterless / `out string` `TryPrepareForRendering` overloads pass
budget `0.0` so non-queue callers retain historical synchronous
behaviour.

### Non-Blocking `Link()` From Render-Prep Hot Paths

`GLRenderProgram.Link(bool force = false, bool nonBlocking = false)`. When
`nonBlocking == true`, two previously-synchronous fallbacks defer to the
async pump:

- The `PrepareCompileInputs()` call inside the source-build branch (when
  shared-context source inputs are wanted but not yet prepared) is
  replaced with `BeginPrepareLinkData()` + `RegisterPendingAsyncProgram()`
  + `return ReturnPendingBuildResult()`. The render thread no longer
  concatenates / GLSL-resolves shader source on the render-prep hot
  path.
- The `EOpenGLProgramBuildLane.SynchronousSource` lane removes `Hash`
  from `InFlightCompilations`, publishes a
  `SynchronousSource (deferred: non-blocking caller)` status, registers
  the program for `PollPendingAsyncPrograms`, and returns. The same
  synchronous fallback work still runs inside the upload pump on a later
  frame, bounded by `PollPendingAsyncProgramsSyncBudgetMilliseconds = 4 ms`.

Render-prep callers that must not block — `GetCombinedProgram`,
`UseSuppliedVertexShader`, `GenerateVertexShader` in
`GLMeshRenderer.Shaders.cs` — pass `nonBlocking: true`. Pump callers and
`GLMeshRenderer.InitiateLink` (when
`XRMeshRenderer.GenerateAsync == false`) call `Link()` blocking by
default to drive deferred programs to completion. Binary cache hits,
driver-parallel, shared context, and failed-hash short-circuits are
unchanged.

### `XRMeshRenderer.GenerateAsync = true` By Default

The default flipped from `false` → `true` in
`XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`. Pipeline-critical
sites that must be ready on the same frame they are first dispatched opt
out explicitly with `GenerateAsync = false`:

- `XRQuadFrameBuffer.FullScreenMesh` (pre-existing).
- `XRCubeFrameBuffer.FullScreenCubeMesh`.
- `VPRC_LightCombinePass`: `PointLightRenderer`, `SpotLightRenderer`,
  `DirectionalLightRenderer`, plus the six MSAA simple/complex variants.
- `VPRC_MarkComplexMsaaPixels` fullscreen triangle.
- `GPURenderPassCollection._indirectRenderer` (whose default version is
  `Generate()`-d synchronously immediately after construction).
- `HlodGroupComponent` proxy renderer (pre-existing).

Other renderer construction sites (scene meshes, UI batches, debug
overlays, editor gizmos, particle/skybox/decal/landscape/skinned
deformers) inherit the new async default — at most one frame of delayed
first-use draw, matching the existing async-load expectation for scene
content. Importer paths (`NativeFbxSceneImporter`,
`NativeGltfSceneImporter`, `UnitySceneImporter`, `ModelImporter`),
`SubMeshLOD.MakeRenderer`, `MeshOptimizerIntegration`,
`UberShaderVariantBuilder`, and `XRShader.GenerateAsync` propagation
forward whatever the source asset specifies. Regression tests
`XRMeshRendererTests.GenerateAsync_DefaultsToTrue` and
`GenerateAsync_FullScreenQuadBlitOptsOut` pin both the default and at
least one known opt-out.

### Buffer Upload First-Use Audit

`GLDataBuffer.PostGenerated` emits one
`[BufferUploadAudit] route=<...> sizeBytes=<...> thresholdBytes=<...>`
log per first-use buffer covering all four routing branches
(`Resizable` / `PendingQueued` / `Queued` / `ImmutableSync`). On
`pid34576` 226 buffers logged: 226 `Resizable`, 0 of any other route.
The hypothesis that Sponza submesh buffers slip below 64 KiB into the
synchronous `AllocateImmutable` path does not materialise on this
codebase; `PushData()`'s existing
`> AsyncUploadThreshold (64 KiB) → PushDataQueued`,
`<= 64 KiB → PushDataImmediate` split correctly handles the 226-buffer
cold-start population (97 queued, 129 small-and-sync). The audit log
remains as a regression detector — if a future asset ever flips a
buffer onto `route=ImmutableSync` and logs an upload-queue stall, the
worker-context `glBufferStorage` prototype (previously deferred) becomes
relevant again.

## Regression Guard Rails

The following must remain true. Each item is paired with the failure mode
that motivated it:

- Do **not** enable shared-context source linking for known async-link
  hazards (single-stage separable, compute). They wedge or stall on NV
  drivers; falls back to guarded synchronous via
  `TryDisableParallelShaderCompileForHazardousLink`.
- Do **not** switch to binary-load-only program handles that skip program
  lifecycle state. Binary loads must still run reflection / uniform metadata
  restoration before the program is considered ready.
- Do **not** globally share generated separable vertex programs as a first
  fix. Each `GLRenderProgram` owns its `programId`; coalescing only
  serializes uploads.
- Do **not** mask shadow-caster failure with broad UberShader varying
  removal. The directional-cascade GS files emit the full Uber FS interface
  (`FragBinorm`, `FragTan`, etc.); point-light GS files emit only the
  depth-only interface. The
  `DirectionalCascadeShadowGeometryShaders_EmitFullUberFragmentInterface`
  / `PointLightShadowGeometryShaders_OnlyEmitDepthOnlyInterface` tests pin
  this contract.
- Do **not** skip render-program handle creation, binding semantics, or
  material readiness state just because a hash is known failed unless the
  call path has been audited.
- Do **not** add per-frame heap allocations to render submission,
  swap/present, visible collection, per-frame update, or shader binding hot
  paths. The `Failed.AddOrUpdate` and `_inFlightCacheKeys.AddOrUpdate` calls
  use static lambdas with value-tuple state arguments specifically to avoid
  closure allocations.
- Do **not** route CPU-only callbacks through `EnqueueMainThreadTask` /
  `EnqueueRenderThreadTask` on the runtime facade. Use
  `EnqueueAppThreadTask`.
- Do **not** invalidate `GLObjectBase`-derived objects on transient property
  toggles. Tag the property with `[TransientGLState]` or do not raise
  `PropertyChanged` for it.
- Do **not** call blocking `program.Link()` from
  `GLMeshRenderer.GetCombinedProgram` / `UseSuppliedVertexShader` /
  `GenerateVertexShader` or any other render-prep hot path. Pass
  `nonBlocking: true` so the synchronous source-input preparation and the
  `EOpenGLProgramBuildLane.SynchronousSource` fallback defer to
  `PollPendingAsyncPrograms`.
- Do **not** flip a renderer that the render pipeline dispatches on its
  first frame (full-screen blits, light-combine volumes, MSAA stencil
  marker, indirect-draw renderer) onto the new
  `XRMeshRenderer.GenerateAsync = true` default without setting
  `GenerateAsync = false` explicitly. Regression tests
  `XRMeshRendererTests.GenerateAsync_DefaultsToTrue` /
  `GenerateAsync_FullScreenQuadBlitOptsOut` pin the default and one
  representative opt-out; pipeline-critical opt-outs live in
  `XRQuadFrameBuffer`, `XRCubeFrameBuffer`, `VPRC_LightCombinePass`,
  `VPRC_MarkComplexMsaaPixels`, and `GPURenderPassCollection`.
- Do **not** remove the `[GLMeshGenerationQueue] TryPrepare deferred`
  budget without re-instrumenting an equivalent guard. The
  `TryPrepareForRendering(double)` overload is what keeps render-prep
  cost from compounding `genProgramsAndBuffersMs` and `getProgramsMs`
  inside the same frame.
- Do **not** drop the `[BufferUploadAudit]` first-use logging in
  `GLDataBuffer.PostGenerated`. It is the regression detector that
  flags any future asset which slips onto `route=ImmutableSync` (the
  synchronous `glBufferStorage` path Phase E was prepared to chase).

A regression on any of these items typically reproduces one of the historical
freezes (unrecovered render-thread stall, repeated `SOURCE_FAILED` retries,
hundreds of `Combined:UIBatchTextMaterial` rebuilds per startup, or 700 ms+
post-render main-thread job waits).

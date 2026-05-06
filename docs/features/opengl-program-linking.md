# OpenGL Program Linking Strategies

XRENGINE builds OpenGL programs through a small backend-selection pipeline
instead of linking every GLSL program on first use. The goal is predictable
frame pacing: cache hits should avoid source compilation, async source builds
should stay off the render thread when the driver can handle them, and every
fallback should explain itself through status and telemetry.

The implementation lives mainly in:

- [OpenGLShaderLinkBackendSelector.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLShaderLinkBackendSelector.cs)
- [GLRenderProgram.Linking.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs)
- [GLRenderProgram.BinaryCache.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.BinaryCache.cs)
- [GLProgramCompileLinkQueue.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLProgramCompileLinkQueue.cs)
- [GLProgramBinaryUploadQueue.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLProgramBinaryUploadQueue.cs)

## Configuration

The source compile/link strategy is selected by
`Engine.Rendering.Settings.OpenGLShaderLinkStrategy`
([EOpenGLShaderLinkStrategy](../../XREngine.Data/Core/Enums/EOpenGLShaderLinkStrategy.cs)).

| Strategy | Source compile/link behavior |
| --- | --- |
| `Auto` | Prefer driver-parallel compile/link when the startup probe passes. If the probe fails, use the shared-context source queue when available. Known async-link hazards bypass both async source lanes and use the guarded synchronous fallback. |
| `SharedContext` | Compile and link non-hazard source programs on the shared-context source worker. The queue rejects known hazards directly; rejected hazards use the guarded synchronous fallback. |
| `DriverParallel` | Use `GL_ARB_parallel_shader_compile` or `GL_KHR_parallel_shader_compile` when available and, when probing is enabled, after the startup probe passes. If unavailable, fall back to the shared-context source queue and then synchronous linking. |
| `Synchronous` | Compile and link source programs on the render thread. This does not disable async binary uploads; `AsyncProgramBinaryUpload` controls the binary-cache lane independently. |

Related settings:

- `AsyncProgramCompilation` gates async source compile/link paths.
- `AllowBinaryProgramCaching` enables program-binary cache lookup and writes.
- `AsyncProgramBinaryUpload` uploads cache hits with `glProgramBinary` on a
  shared background context when possible.
- `OpenGLParallelShaderCompileProbeEnabled` and
  `OpenGLParallelShaderCompileProbeTimeoutMs` control the driver-parallel
  startup probe.
- `OpenGLShaderCompilerThreadCount` configures
  `glMaxShaderCompilerThreadsKHR`.
- `MaxAsyncShaderProgramsPerFrame` caps the number of pending program builds
  pumped by `PollPendingAsyncPrograms` each frame.

## Backend Decision

`GLRenderProgram.Link()` delegates the high-level choice to
`OpenGLShaderLinkBackendSelector`. In simplified form:

```text
if hash was marked failed
    fail fast
else if binary cache hit
    use async binary upload if enabled, available, and below capacity
    otherwise load the binary synchronously
else if async source compilation is disabled or strategy is Synchronous
    compile/link source synchronously
else if program is a known async-link hazard
    compile/link source synchronously with driver-parallel suppression
else if strategy is Auto and driver-parallel probe passed
    use driver-parallel source compile/link
else if selected/fallback shared-context source queue is available
    enqueue source compile/link unless the queue is at capacity
else
    compile/link source synchronously
```

Queue capacity is reported as backpressure. The render thread keeps the
previous linked program alive and retries the pending build on later frames.

## Known Hazards

`GLRenderProgram.IsKnownAsyncLinkHazard` is intentionally conservative. Known
hazards currently include single-stage program shapes and compute programs.
These shapes are denied both driver-parallel source linking and the
shared-context source queue.

If a hazard reaches synchronous source linking while the driver-parallel
extension is active, the link is wrapped in
`TryDisableParallelShaderCompileForHazardousLink` /
`RestoreParallelShaderCompile`. That temporarily requests zero driver shader
compiler threads on the render thread so `glCompileShader`, `glLinkProgram`,
and the following status query do not silently wait on a wedged driver worker.

This matters on machines where the startup probe passes but real program
shapes still do not complete reliably. On the current development machine the
driver-parallel path behaves well; other machines have shown hangs or
noncompletion. The default policy optimizes for the fast path when the probe is
healthy, but never sends the known hazard shapes into async source lanes.

## Binary Cache

Successful links can write program binaries under:

```text
Build/Cache/OpenGL/ShaderPrograms/
```

Each cache entry is a `.bin` payload plus a `.bin.json` metadata file. The
cache key includes:

- schema version,
- resolved shader source hash,
- ordered stage topology,
- separable vs monolithic topology,
- shader variant metadata and binary-cache policy,
- OpenGL version,
- vendor,
- renderer,
- GLSL version,
- binary format.

The cache is loaded with `GLRenderProgram.ReadBinaryShaderCache(api)` after
OpenGL startup has queried the runtime strings. Entries with stale schema,
missing payloads, corrupt metadata, or a different runtime fingerprint are
deleted individually. Failed `glProgramBinary` loads also delete only the bad
entry and then fall back to source.

Both sync and async binary loads validate `GL_LINK_STATUS` after
`glProgramBinary`. A cache hit is not allowed to mark a program linked unless
the driver accepts the binary and reports a successful link. After a successful
binary load, the runtime binding contract is restored by cached uniform
metadata or by reflecting active uniforms when metadata is missing.

## Async Workers

OpenGL startup can create three independent shared-context workers:

- `XR Program Binary Upload` for `GLProgramBinaryUploadQueue`.
- `XR Program Source Compile` for `GLProgramCompileLinkQueue`.
- `XR GL Shared Uploads` for general shared-context upload work such as sparse
  texture streaming.

Binary upload and source compile/link are deliberately split. A source link
that wedges inside the driver can make the source worker unhealthy, but it no
longer starves cached binary uploads. Each worker exposes pending count,
completed count, failed count, oldest pending age, current job name, and an
unhealthy state when a job has exceeded the watchdog threshold.

The source queue also enforces hazard policy at the queue boundary. Even if a
caller makes a bad decision, `TryEnqueueCompileAndLink` rejects single-stage
and compute shapes instead of letting them block the worker.

## Clone And Swap

Relinks and hot reloads build into a replacement GL program handle. The
previous linked handle remains active until the replacement has successfully
compiled, linked or loaded from binary, reflected its runtime binding state,
and reached a frame-safe adoption point.

If the replacement fails, stalls, hits queue backpressure, or is abandoned, the
old program remains renderable. Old handles are deferred for deletion for at
least two render frames so queued draws and program pipelines cannot reference
a destroyed program. This is the main defense against full material blanking
when an Uber shader feature toggle or static/animated property change forces a
variant rebuild.

## Program Pipelines

Separable program pipelines are render-context-local containers. Pipeline
validation is compiled out in non-debug/non-editor builds, and successful
debug/editor validations are memoized so steady-state rendering does not
revalidate known-good pipelines every frame.

Separable shader families should use explicit stage interfaces and explicit
resource bindings. Renderer-critical paths with bounded permutations should
prefer monolithic programs unless there is a clear varianting or hot-reload
benefit to separable stages.

## Backlog Pumping

`OpenGLRenderer.ProcessPendingUploads` calls
`GLRenderProgram.PollPendingAsyncPrograms(MaxAsyncShaderProgramsPerFrame)` at
the start of each frame. The pump:

- advances pending async binary uploads,
- advances shared-context source results,
- polls driver-parallel `GL_COMPLETION_STATUS`,
- retries queue-backpressure cases,
- applies a soft 4 ms budget to synchronous source work,
- drains deferred program-handle deletes.

Pending programs stay registered until they complete, fail, abandon, or remain
blocked by queue capacity. The render path can continue using the previous
linked handle while a replacement is pending.

## Telemetry

`XRRenderProgram.ShaderProgramBackendStatus` publishes the current backend
stage for cache lookup, cache hit/miss, binary upload pending/ready/failed,
source queueing, queue backpressure, driver-parallel pending, synchronous
fallback, compile, link, ready, failed, and abandoned.

`ShaderProgramBuildTelemetry` records the compact last-build summary:

- program name,
- cache fingerprint,
- stage set,
- separable mode,
- backend,
- queue latency,
- compile time,
- link time,
- binary load time,
- reflection time,
- failure reason.

The Uber material inspector consumes the same backend status surface so a
feature toggle or static/animated property change can be correlated with the
actual OpenGL backend work.

## Diagnostics

Useful log lines:

```text
OpenGL parallel shader compile: extension=<name>, requestedThreads=<count>,
reportedThreads=<count>, set=<bool>, probe=<passed|failed|skipped|disabled>,
strategy=<Auto|SharedContext|DriverParallel|Synchronous>.
```

```text
[ShaderCache] HIT hash=<H>, loading binary cache entry <key>.
[ShaderCache] MISS hash=<H>, compiling N shader(s) from source.
[ShaderCache] QUEUE hash=<H>, compiling N shader(s) on shared context.
[ShaderAsync] Program '<name>' hash=<H> phase=<compile|link> still pending...
[ShaderAsync] ... abandoned
```

Stalls whose `RenderScopeLeaf` is `XRWindow.ProcessPendingUploads` are tied to
the program pump and other upload work. Backend status and last-build
telemetry should identify whether the frame was doing binary load, shared
queue finalization, driver-parallel finalization, or unavoidable synchronous
source fallback.

## Benchmarks

Shader pipeline benchmarks live in `XREngine.Benchmarks`:

- `Benchmark-ShaderPipeline-Smoke`
- `Benchmark-ShaderPipeline-FullMatrix`
- `Benchmark-ShaderPipeline-FrameBudget`
- `Benchmark-ShaderPipeline-LocalStress`

The frame-budget harness emits Markdown and JSON under
`BenchmarkDotNet.Artifacts/results/` with OpenGL version, vendor, renderer,
frame percentiles, queue depths, completion counts, failure counts, and
backpressure counts. GPU-driver-local scenarios require a Windows OpenGL 4.6
driver and should be treated as local validation evidence rather than
headless-CI pass/fail gates.

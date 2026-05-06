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

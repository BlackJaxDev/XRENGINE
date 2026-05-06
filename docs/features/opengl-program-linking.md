# OpenGL Program Linking Strategies

XRENGINE has to compile and link a lot of GLSL programs at startup, on import,
and during gameplay. The naive path — `glCompileShader` + `glLinkProgram` on
the render thread — easily produces multi-second stalls. To keep the render
thread responsive we support three different compile/link backends and pick
between them based on driver behavior, the program shape, and runtime
settings.

This document describes the three backends, the failure modes we have
observed, and the mitigations that ship today.

## Configuration

The strategy is selected by `Engine.Rendering.Settings.OpenGLShaderLinkStrategy`
([EOpenGLShaderLinkStrategy](../../XREngine.Data/Core/Enums/EOpenGLShaderLinkStrategy.cs)):

| Strategy        | Behavior                                                                 |
| --------------- | ------------------------------------------------------------------------ |
| `Auto`          | Use driver-parallel if the probe passes. If the probe fails, use the shared-context queue when available. Known single-stage separable hazards route to the shared-context queue when available; only the no-queue fallback uses guarded synchronous linking. Imported model materials that ask for the Uber path still fall back to the standard Forward+ material shaders on NVIDIA OpenGL Auto. |
| `SharedContext` | Route source links through the shared-context compile/link queue. Mesh/material paths that would create known hazards are represented as combined programs where possible; remaining hazards also use the queue when available. |
| `DriverParallel`| Always use `GL_ARB/KHR_parallel_shader_compile` on the render context.   |
| `Synchronous`   | Diagnostic only. Compile + link on the render thread, no async at all.   |

Related settings:

- `AsyncProgramCompilation` — master enable for async paths (`Auto` and `SharedContext`).
- `AsyncProgramBinaryUpload` + `AllowBinaryProgramCaching` — opt into off-thread binary cache uploads.
- `OpenGLParallelShaderCompileProbeEnabled` / `OpenGLParallelShaderCompileProbeTimeoutMs` — controls the startup probe described below.
- `MaxAsyncShaderProgramsPerFrame` — caps how many pending programs are pumped per frame by `PollPendingAsyncPrograms`.

## The three backends

### 1. Driver-parallel (`GL_ARB_parallel_shader_compile` / `GL_KHR_parallel_shader_compile`)

The driver compiles and links on its own worker threads. We just call
`glCompileShader` / `glLinkProgram` and poll `GL_COMPLETION_STATUS` until the
work is done. Implementation lives in
[OpenGLRenderer.ParallelShaderCompile.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.ParallelShaderCompile.cs).

**Pros**

- Cheapest path when it works: zero context-management overhead and the
  driver gets to schedule shader work however it likes.
- No second GL context required, so no swapchain or driver state to keep in
  sync.

**Issues we have hit**

- **NVIDIA single-stage separable program deadlock.** When the program is
  marked separable (`GL_PROGRAM_SEPARABLE`) and contains exactly one stage
  (typical for imported model materials whose vertex and fragment stages are
  split into separate programs), the parallel-link worker on NVIDIA never
  finishes. Any blocking probe of the program — `glGetProgramiv(GL_LINK_STATUS)`,
  `glGetProgramInfoLog`, `glDetachShader`, `glDeleteProgram` — will then hang
  the render thread. We have observed this on driver 581.57.
- **NVIDIA combined material program noncompletion.** The trivial startup
  probe can pass while real imported-material programs still report
  `GL_COMPLETION_STATUS=false` forever through `GL_ARB_parallel_shader_compile`.
  We have observed this with two-stage combined Sponza programs on driver
  581.57, where the hard-abandon path eventually marks the programs failed
  and no submeshes become renderable.
- **Blocking GL queries serialize back to the worker.** Even when the program
  is fine, calling `glGetProgramiv(GL_LINK_STATUS)` before
  `GL_COMPLETION_STATUS` reports done will block the render thread.
- **Driver bugs surface late.** Some drivers crash inside `glLinkProgram` if
  shader sources contain certain GLSL features; you cannot tell whether to
  trust the path until you have run it.

**Mitigations**

1. **Startup probe.** `ProbeParallelShaderCompile` compiles and links a
   trivial vertex+fragment program inside a timeout
   (`OpenGLParallelShaderCompileProbeTimeoutMs`). The result populates
  `_parallelShaderCompileProbePassed` and is logged at startup. Under
  `Auto` we will not enable the driver path unless the probe passed.
2. **Combined-program hazard avoidance.** When async source linking is still
  active for the selected OpenGL strategy, `GLMeshRenderer` prefers combined
  programs for normal material rendering and for material-override passes such
  as shadow atlas rendering. That keeps those programs non-separable so they
  can use the non-blocking driver/shared-context paths instead of falling into
  guarded synchronous linking on first use. NVIDIA `Auto` is excluded from this
  combined fallback because driver 581.57 was observed to hang inside
  monolithic combined Uber material links.
3. **Imported Uber material fallback on NVIDIA Auto.** Imported model material
  factories can request the generated Uber fragment for ordinary albedo/normal
  model materials. On NVIDIA OpenGL Auto, those generated fragments can block
  inside `glLinkProgram` even after compile succeeds and even when linked as a
  separable fragment program. The importer therefore routes those imported
  materials through the existing standard Forward+ shader selection on NVIDIA
  Auto. Authored Uber materials and explicit non-Auto diagnostic strategies are
  left alone.
4. **Hazard detection.**
   `GLRenderProgram.IsKnownAsyncLinkHazard` returns `true` for any program
   that is `Data.Separable` and has `_shaderCache.Count <= 1`. Hazardous
  programs never go down the driver-parallel path. If the shared-context
  queue is available, they are enqueued there so the render thread does not
  block in `glLinkProgram`; otherwise they use the synchronous path with the
  per-link mitigation below.
5. **Per-link parallel suppression.** When a hazardous program has to fall
  through to the synchronous render-thread path,
  `OpenGLRenderer.TryDisableParallelShaderCompileForLink()` calls
   `glMaxShaderCompilerThreadsKHR(0)` and sets a thread-static suppression
   flag, then `RestoreParallelShaderCompile()` restores the configured count
   on the way out. This prevents the driver from kicking the link onto its
   parallel worker and avoids the NVIDIA deadlock.
6. **Non-blocking poll.** All driver-parallel polling uses
   `GL_COMPLETION_STATUS`. We never block on `GL_LINK_STATUS` while the
   program is still in flight.
7. **Hard abandon.** If a program ever does get stuck in the driver after
   the per-link suppression is in place,
   `TryAbandonStuckAsyncLink` issues `glFlush()` at 5 s, then at 30 s it
   marks the program `Failed`, stops touching the GL handles
   (no `glDetachShader` / `glGetProgramiv` / `glDeleteProgram`), and orphans
   the program and shader handles via `OrphanForDeferredDelete()` so the
   render thread is never blocked waiting on the worker. The leaked handles
   are accepted as the cost of avoiding a hard hang.

### 2. Shared-context compile/link queue

We bring up a second OpenGL context that shares object IDs with the primary
context (`GLSharedContext` + [GLProgramCompileLinkQueue](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLProgramCompileLinkQueue.cs)).
Compile and link work is enqueued from the render thread and executed on the
shared-context worker. The render thread only sees the resulting program
binary.

**Pros**

- Off the render thread entirely. The shared context can block in
  `glLinkProgram` for as long as it likes without affecting frame pacing.
- Works with drivers where the parallel-shader-compile extension is buggy or
  absent.
- Plays nicely with the optional binary upload queue
  ([GLProgramBinaryUploadQueue](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLProgramBinaryUploadQueue.cs)),
  which reuses the same shared context.

**Issues we have hit**

- **Context creation can fail.** Some headless / remote / debug
  configurations cannot create a second GL context. We treat that as
  "queue not available" and fall back at runtime.
- **Hand-off cost.** Every job pays a context-switch + fence round trip. For
  very small programs this can be slower than the driver-parallel path.
- **Backpressure.** The queue has a bounded capacity. When `CanEnqueue`
  reports `false` we mark the program with `_asyncCompileLinkQueueWaitPending`
  and retry on the next frame.
- **NVIDIA source-link starvation.** Driver 581.57 can still wedge the shared
  worker inside `glLinkProgram` for real material programs. The render thread
  remains responsive, but the single shared worker stops publishing results and
  mesh generation waits forever behind the stuck job.

**Mitigations**

1. **Availability gate.** `UseSharedContextProgramCompileLinkQueue` checks
   that `ProgramCompileLinkQueue.IsAvailable` is `true` before routing work
   onto the queue.
2. **Source resolution fallback.** If `PrepareCompileInputs` cannot resolve
   GLSL sources for every stage we fall through to the synchronous path
   instead of enqueuing a partial job.
3. **Hazard routing.** The shared-context queue is also used for known
  single-stage separable hazards when it is available. This keeps the
  render-thread fallback from calling `glLinkProgram` inline for the exact
  program shape that has been observed to wedge on NVIDIA.

### 3. Synchronous render-thread fallback

The unconditional safety net. `glCompileShader`, `glAttachShader`,
`glLinkProgram`, `glGetProgramiv(GL_LINK_STATUS)` all execute inline on the
render thread.

**Pros**

- No async state machine, no worker context, no extension required. Works on
  every driver.
- Easy to reason about while debugging shader issues — useful as a
  diagnostic mode.

**Issues we have hit**

- **Multi-second frame stalls.** Linking many uber-shader variants in one
  frame (e.g. on first render of a freshly imported model, or right after a
  window resize that invalidates draw caches) can produce 45–60 s stretches
  of `XRWindow.ProcessPendingUploads` time during which the OS shows the
  window as Not Responding. The user perceives this as a crash.
- **Hidden behind `PollPendingAsyncPrograms`.** Hazardous programs that fall
  back to the synchronous path during the per-frame async pump used to drain
  the entire backlog inside one render frame, because the only cap on that
  loop was a program-count limit (`MaxAsyncShaderProgramsPerFrame`).

**Mitigations**

1. **Per-frame sync budget.** `PollPendingAsyncPrograms` enforces a soft
   `4 ms` wall-clock budget on synchronous link work performed inline. Async
   phase polls are free (their cost is dominated by `GL_COMPLETION_STATUS`),
   but any `Link()` call that returns having done real synchronous work
   counts against the budget. Once the budget is exhausted the loop breaks
   and the remaining backlog is picked up next frame.
2. **Sync-link guard.** Before falling through to the inline link path,
  known-hazard branches wrap the compile/attach/link/detach sequence in a
  try/finally that calls
  `TryDisableParallelShaderCompileForLink` /
   `RestoreParallelShaderCompile`, so the driver does not silently move work
   onto its parallel worker behind our back.
3. **Binary cache.** A successful link writes a program binary to disk via
   `CacheBinary`. Subsequent runs skip compile + link entirely when the
   binary is still valid for the current driver version and program hash —
   the cheapest possible path is "no link at all".

## How `Auto` chooses

`Auto` is the default and the recommended setting. The decision in
`GLRenderProgram.Link()` reads roughly:

```text
if (binary cache hit) -> upload + finish
else if (Failed.ContainsKey(hash)) -> fail fast
else if (mesh/material path can avoid a single-stage separable hazard)
  -> build a combined program instead, then re-enter this decision tree
     (skipped for NVIDIA Auto to avoid monolithic Uber links)
else if (imported model material requested Uber on NVIDIA Auto)
  -> import it as a standard Forward+ material before any Uber fragment reaches GL
else if (shared-context queue available
      && (UseSharedContextProgramCompileLinkQueue || IsKnownAsyncLinkHazard))
    -> enqueue on shared-context queue, return pending
else if (driver-parallel allowed && !IsKnownAsyncLinkHazard)
    -> compile + link on render context, poll via GL_COMPLETION_STATUS
else
    -> synchronous render-thread link, with parallel suppression around
  known hazards
```

`UseSharedContextProgramCompileLinkQueue` under `Auto` opts in when the
driver-parallel probe failed and the queue is available. Known hazardous
separable single-stage programs also use the queue when it is available,
even on machines where the driver-parallel probe passed.

## Backlog pumping

`OpenGLRenderer.ProcessPendingUploads` calls
`GLRenderProgram.PollPendingAsyncPrograms(MaxAsyncShaderProgramsPerFrame)`
once per frame. The pump:

- Picks up to `MaxAsyncShaderProgramsPerFrame` pending programs.
- Calls `program.Link()` on each, which advances its async state machine
  (`Compiling` → `Linking` → done) or executes the synchronous link.
- Honors the 4 ms sync-work budget described above.
- Drains a small batch of `DeferredAsyncLinkCleanups` (handle disposal for
  programs whose async cleanup is gated on `GL_COMPLETION_STATUS`).

A program that is still busy after its time slice stays in
`PendingAsyncPrograms` and is rechecked next frame.

## Diagnostics

- **Startup line in `log_opengl.txt`** — emitted by
  `ConfigureParallelShaderCompile`:

  ```text
  OpenGL parallel shader compile: extension=<name>, requestedThreads=<count>,
  reportedThreads=<count>, set=<bool>, probe=<passed|failed|skipped|disabled>,
  strategy=<Auto|SharedContext|DriverParallel|Synchronous>.
  ```

- **`[ShaderCache] MISS hash=<H>, compiling N shader(s) from source.`** —
  written when a program leaves the cache and enters the active backend.

- **`[ShaderAsync] Program '<name>' hash=<H> phase=<compile|link> still
  pending after Xs; continuing non-blocking poll.`** — issued by
  `ReportSlowAsyncPending` once a pending async program has been outstanding
  for `AsyncShaderSlowWarningSeconds` (2 s).

- **`[ShaderAsync] ... abandoned`** — issued by `AbandonStuckAsyncLink` after
  `AsyncShaderHardAbandonSeconds` (30 s). The program is marked `Failed`
  and its handles are leaked into the deferred-delete queue.

- **`profiler-render-stalls.log`** — any stall whose
  `RenderScopeLeaf` is `XRWindow.ProcessPendingUploads` is the program-link
  pump. Sustained stalls there indicate the sync budget is being saturated
  by hazardous programs and the shared-context queue is not available.

## Adding a new strategy

If a future driver introduces a new failure mode:

1. Capture it as a predicate on `GLRenderProgram` (preferred) so the
   detection can be reused by all backends.
2. Decide whether the program should be denied the driver path entirely
   (`IsKnownAsyncLinkHazard`-style) or merely gated on the shared-context
   queue when one is available.
3. If a per-link mitigation is needed, follow the
  `TryDisableParallelShaderCompileForLink` / `Restore` pattern so
   the change is scoped to the affected program and visible to dependent
   code via the thread-static suppression flag.
4. Update this document and the corresponding entries in
   [GLRenderProgram.Linking.cs](../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs)
   with the symptom, root cause, and mitigation so future debugging starts
   with the right hypothesis.

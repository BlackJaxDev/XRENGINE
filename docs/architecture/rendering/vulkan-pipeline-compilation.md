# Vulkan Pipeline Compilation

The Vulkan backend avoids render-thread stalls from shader compilation and
`vkCreate*Pipelines` calls. Compilation is requested ahead of use, polled for
completion, and invalidated only as far as the changed dependency reaches. The
frame-loop rules this serves are in [Frame Loop Design](frame-loop-design.md).

## Compilation Layers

- SPIR-V shader artifacts are compiled asynchronously by `VkShader` and cached
  under `Build/Cache/Vulkan/ShaderArtifacts/`.
- Graphics pipeline libraries are cached at the renderer/device level so
  matching vertex-input, pre-rasterization, fragment-shader, and fragment-output
  subsets can be reused across mesh renderers.
- Cold graphics pipelines are queued to background workers when
  `RuntimeEngine.Rendering.Settings.AsyncProgramCompilation` is enabled. Command
  recording skips that draw while the pipeline is still compiling, then marks
  command buffers dirty when the worker finishes, so the next recording can bind
  the completed pipeline.

Each pipeline manager has one persistent native compile worker. Background
compilation uses a separate synchronized `VkPipelineCache`. Vulkan requires
external synchronization for host access to a pipeline cache. Foreground
requests are promoted in the worker queue; they do not create another native
compile thread.

`XRE_VK_PIPELINE_COMPILE_WORKERS` controls queue admission capacity. Valid values
are `1` through `16`, with a default of `1`. This setting does not create more
native compile workers.

## Synchronous Shader Asset Loads

`ShaderHelper.LoadEngineShader` loads a missing or pending shader asset inline
when the host supports synchronous shader work. An import job can hold the
only high-refresh job permit while a parallel child requests a shader. Waiting
for another queued asset job in that child would prevent both jobs from
finishing. The asset manager's path lock and cache coordinate inline and queued
loads. Hosts that require asynchronous work still require a preloaded shader.
This rule covers shader asset loading, not native pipeline compilation.

## Background Driver Work Admission

`VulkanPipelineCompileTask` holds a background scheduling slice only while it
claims a queued item. It releases that slice before calling the driver. A
native pipeline compile cannot be preempted and can take much longer than a
frame. Holding the slice during that call would make exact foreground readiness
wait for unrelated compilation before its own readiness watchdog starts.

The persistent compile worker still serializes its native calls. An atomic
claim prevents a promoted item from running twice. Foreground admission,
pipeline dependency leases, cache locks, and device retirement drains retain
their existing rules. This scheduling change does not make cold pipelines
ready immediately or change GPU resource lifetime.

## Depth-Only Graphics Pipelines

Graphics stage selection preserves every stage requested by the program, even
when the target has no color attachments. Fragment shaders can discard masked
coverage, write depth, or perform other side effects without color outputs.
Both direct program creation and immutable mesh build requests use the same
stage-selection helper. Depth-only dynamic-rendering pipelines still use the
monolithic creation path; that choice must not remove fragment work.

## Advanced Visibility Families

The Advanced visibility programs (early/late visibility compute, opaque and
masked raster, and the native shading stages) are prepared as one required
family. Readiness has four states, `Ready`, `Missing`, `Pending` and `Failed`.
Polling never compiles, links or waits. A family is admitted only when all of its
programs are ready, and an unadmitted family is reported as `PendingResources`
rather than replaced by another path. The polling and task ownership are
described in [Frame Loop Design](frame-loop-design.md#the-render-thread-never-waits-for-compilation-linking-or-uploads).

The optional directional-shadow programs have a separate source identity and
readiness snapshot. Lazy creation or relinking of those programs must not
invalidate the required visibility family between stage enqueue calls. Shadow
readiness checks both its source identity and current backend links, including
reloads that invalidate native objects without changing shader text.

If a directional group was enqueued but cannot be prepared, the fresh frame is
rejected before native recording with an explicit retry outcome. Its failed
submission receipt keeps the atlas keys dirty, and a bounded lane fault hold
allows the generic directional path to refresh them. A successful frame receipt
must never acknowledge a shadow group whose recording was skipped.

## Scoped Invalidation

`VulkanPipelineManager` (`Pipelines/VulkanPipelineCompileQueue.cs`) separates
three kinds of change:

- **Additive work.** Linking a new program or creating a shader module for the
  first time pins a compilation dependency lease
  (`AcquireCompilationDependencyLease`). The lease reads the dependency
  generation but never advances it, so unrelated pending jobs keep running.
- **Scoped mutation.** Replacing a program, shader module or pipeline layout
  takes a mutation lease scoped to that dependency
  (`VulkanPipelineCompilationMutationScope`).
  - Only jobs matching the scope are drained.
  - Only matching completed results are removed.
  - Requests made before the replacement are rejected at enqueue and at worker
    entry.
  - Unadopted compute pipelines are destroyed immediately.
- **Device-wide mutation.** A scope with no program, shader module or layout is
  device-wide. Only it advances the global compilation generation and clears
  every completion cache.

A managed task can be abandoned, but native compilation cannot be cancelled, so
results are checked against the current scope and generation before they are
used. Each result and dependency is released exactly once.

## Retirement Of Shared Graphics Pipelines

Superseded shared graphics pipelines are not destroyed at once. They are
enqueued for retirement, and `VulkanResourceRuntime.RetirePipeline` builds a
retirement ticket from the last graphics, transfer and other queue sequences, so
destruction waits for every recorded use. The exactly-once rules are in
[Vulkan Resource Lifetime And Retirement](vulkan-resource-lifetime-and-retirement.md).

Mesh renderers also keep a local pipeline lookup keyed partly by the native
pipeline-layout handle and the program's link generation. A re-created program
restarts at link generation 1, and a driver may reuse a destroyed layout's
handle for its replacement, so a local entry could match a retired pipeline. To
prevent that, `VulkanPipelineManager.SharedGraphicsPipelineRetirementGeneration`
advances whenever shared pipelines leave the cache for retirement, and each
`VkMeshRenderer` clears its local lookup once when that value changes. The cost
is one volatile read per pipeline ensure.

## Persistent Pipeline Cache

`VulkanPipelineManager` (`Pipelines/VulkanPipelineCache.cs`) validates
persisted cache data before handing it to the driver. It checks:

- the header size and header version;
- vendor and device IDs against the selected physical device;
- the 16-byte pipeline-cache UUID.

Mismatched or malformed data, and data the driver rejects, recreate empty
foreground and background caches instead of disabling caching for the process,
and the recovery is counted. Device-lifetime telemetry separates foreground and
background native creation, cache-host waits, cache-only probe outcomes, merge,
capture and persistence. Measured foreground costs were small enough that no
cache-only probe queue is used. A cache hit is not a hard latency bound, which
is why cold pipelines still go through the background workers.

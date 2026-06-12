# Secondary GPU render context for compute jobs

The engine now provisions an optional background render context when more than one GPU is available. The secondary context:

- Spins a dedicated window/context thread so render-thread compute jobs can run without stalling the main swap chain.
- Falls back to a shared OpenGL context (if allowed) when only a single adapter is present to keep readbacks off the main window.
- Exposes a job queue (`Engine.Rendering.SecondaryContext.EnqueueJob`) that runs actions with a valid renderer bound.

## Offload-friendly GPU tasks

The secondary context is best suited for work that benefits from async execution and tolerates context boundaries:

- Readback-heavy tasks like visibility buffer counters, Hi-Z/occlusion summaries, or GPU-side copy results needed on the CPU.
- Mesh processing compute workloads: meshlet extraction, signed-distance-field generation, voxelization, or async skinning/bounds expansion.
- Lighting updates that are not latency critical (probe baking, irradiance volume refresh) to free headroom on the main frame.
- Preparing buffers for next-frame rendering such as indirect command compaction or GPU-driven culling data.

When multiple adapters are not detected, enable `AllowSecondaryContextSharingFallback` to still spin the background context with shared resources so readbacks and compute passes avoid blocking the primary swap chain.

## OpenGL shader workers

The OpenGL shader pipeline also uses shared contexts, but those workers are
owned by the renderer rather than by `Engine.Rendering.SecondaryContext`.
Startup may create:

- `XR Program Binary Upload` for cached `glProgramBinary` loads.
- `XR Program Source Compile` for source compile/link work.
- `XR GL Shared Uploads` for general shared-context upload work.

The binary and source lanes are intentionally separate. A driver stall inside a
source link can make the source worker unhealthy without starving binary cache
uploads or unrelated shared upload work.

The ImGui editor exposes `View > Shader Program Links` for sampled OpenGL
program link diagnostics. The panel lists linked, queued, compiling/linking,
prepared, failed, and abandoned programs with the selected backend lane, hash,
shader stages, binary cache state, worker queue depth, active link settings, and
recent compile/link timings. `Prepared` means hash/cache lookup has completed but
no GL link/upload work is currently registered; `Pending` is reserved for
queued/running backend work. The panel refreshes from a throttled snapshot by
default so it does not poll every render frame; use `Refresh Now`, the `Update`
interval, or `Freeze` when diagnosing startup floods or intermittent shader
worker backpressure. The visible row set and summary counters are cached until
the snapshot, sort, or filters change, so a frozen panel does not rescan every
tracked program each frame. Narrow/portrait layouts switch to a compact table
with a `Use` column; material fragment programs are named from their source
material and uber variant hash when available. Binary-cache upload backpressure,
including duplicate cache-key coalescing, is represented by a durable pending
state so the async pump keeps retrying until the upload can enqueue; abandoned
binary-upload results are cancelled so they do not leave phantom in-flight queue
slots behind.

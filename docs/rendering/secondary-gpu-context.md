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

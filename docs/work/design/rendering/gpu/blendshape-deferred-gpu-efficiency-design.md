# Blendshape Deferred GPU Efficiency Design

Last Updated: 2026-06-12
Status: Deferred design backlog
Scope: longer-horizon blendshape GPU-efficiency ideas that are not part of the
current remaining TODO execution pass.

Related docs:

- [Blendshaping](../../../../developer-guides/rendering/blendshaping.md)
- [Blendshape Compression And GPU Efficiency TODO](../../../todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md)
- [GPU-Driven Animation Architecture](gpu-driven-animation.md)
- [Avatar Optimization And Virtualized Avatar Rendering Design](../avatar-optimization-and-virtualized-rendering-design.md)

## Goal

Collect blendshape ideas that need architectural design, asset-pipeline work, or
GPU-driven renderer integration before they can become implementation checklists.
These are not immediate TODO items; promote them only when they have content
fixtures, measurable acceptance criteria, and a clear owner subsystem.

## Promotion Criteria

Move an idea from this document into an implementation TODO only when:

- representative avatar assets expose the bottleneck,
- profiler counters can measure the before/after effect,
- direct vertex and compute deformation contracts are both defined,
- cooked payload versioning and cache migration are understood,
- hot-path allocations are accounted for,
- visual-diff and expression-sweep validation criteria are written.

## Cluster-Local Blendshape Payloads

Cluster-local payloads would partition blendshape deltas by meshlet or
virtualized-avatar cluster so distant or partially visible geometry fetches only
the blendshape records needed by visible clusters.

Design questions:

- Decide whether the cluster owns a local active-shape table, local affected
  vertex ranges, or both.
- Define how cluster-local payloads interact with the current sparse
  shape-owned records.
- Define remapping from mesh-global blendshape IDs to cluster-local delta
  records.
- Keep protected facial controls stable across cluster LOD transitions.
- Measure whether the extra indirection is worth it for small facial meshes.

Acceptance sketch:

- Visible clusters fetch fewer blendshape bytes than mesh-global sparse records.
- Meshlet and non-meshlet paths produce matching deformation for the same LOD.
- Cluster LOD transitions do not pop protected expressions or visemes.

## GPU-Driven Blendshape Weight Production

The GPU animation backend could produce blendshape weights directly for
GPU-driven avatars, avoiding CPU-side weight packing for procedural animation,
crowd behavior, or generated lip-sync.

Design questions:

- Define ownership of authoritative weights when CPU animation, Audio2Face,
  scripts, and GPU animation all target the same shape.
- Add a GPU-resident weight stream compatible with the existing compact
  active-list path.
- Decide where thresholding and active-list compaction run when weights are
  produced on GPU.
- Preserve CPU readback-free operation for GPU-driven render paths.
- Define debugging and capture tools for GPU-produced expression state.

Acceptance sketch:

- GPU-produced weights can drive both direct vertex and compute deformation.
- CPU-authored and GPU-authored weights have deterministic conflict resolution.
- GPU-driven crowds avoid CPU weight upload for unchanged or procedurally
  generated expressions.

## On-Demand Shape Data Streaming

Rarely used shape data could stream on demand instead of remaining resident for
every loaded avatar. This is most useful for large expression sets, corrective
libraries, or customization shapes that are not active during ordinary motion.

Design questions:

- Classify always-resident protected shapes versus streamable rare shapes.
- Define residency state in cooked mesh metadata.
- Define visible diagnostics for missing shape data; do not silently drop an
  explicitly requested expression.
- Decide whether streaming is shape-level, group-level, or cluster-local.
- Coordinate with cache eviction and avatar LOD policy.

Acceptance sketch:

- Memory drops for avatars with large rare-shape libraries.
- Missing streamable data produces explicit diagnostics or visible fallback
  policy.
- Streaming latency does not disrupt visemes, eyelids, tracking shapes, or
  protected controls.

## Shape-Specific Async Compute Scheduling

Crowd scenes may benefit from scheduling expensive shape groups separately,
especially when many avatars share expression states or when only a subset of
shapes needs high-priority update.

Design questions:

- Split shape work by priority, cost, or avatar role without breaking final
  deformation ordering.
- Decide whether precombine, compute skinning, and direct vertex paths share
  one scheduling model.
- Define synchronization with motion vectors, TAA history, and visibility.
- Prevent dispatch count from growing faster than useful GPU occupancy.
- Expose profiler counters for per-shape-group scheduling decisions.

Acceptance sketch:

- Crowd scenes reduce visible frame cost without increasing latency for hero
  avatar expressions.
- Async scheduling remains deterministic enough for tests and capture replay.
- Synchronization failures are diagnosed rather than hidden by stale output.

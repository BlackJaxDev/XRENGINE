# Skinning Deferred GPU Efficiency Design

Last Updated: 2026-06-12
Status: Deferred design backlog
Scope: longer-horizon skinning GPU-efficiency ideas that are not part of the
current remaining TODO execution pass.

Related docs:

- [Skinning](../../../../developer-guides/rendering/skinning.md)
- [Skinning GPU Efficiency Follow-Ups TODO](../../../todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md)
- [GPU Skinning Buffer Compression Plan](gpu-skinning-buffer-compression-plan.md)
- [GPU-Driven Animation Architecture](gpu-driven-animation.md)

## Goal

Collect skinning ideas that need architectural design, asset-pipeline support,
or GPU-driven renderer integration before they become implementation checklists.
These items should stay out of the active TODO until their precision policy,
backend layout, validation corpus, and profiler counters are defined.

## Promotion Criteria

Move an idea from this document into an implementation TODO only when:

- representative core-only, spill-heavy, crowd, and GPU-physics-chain assets are
  available,
- direct vertex and compute skinning contracts are both specified,
- OpenGL and Vulkan buffer alignment are measured,
- cooked payload versioning and cache migration are planned,
- deformation error thresholds and visual-diff captures are defined,
- hot-path allocation and shader-variant budgets are written.

## Sparse Spill-Header Tables

Meshes where overflow vertices are rare still pay a spill-header slot for every
vertex in the current spill-capable layout. A sparse spill-header table would
store headers only for overflow vertices and use a compact lookup structure for
the rare vertices that need spill entries.

Design questions:

- Choose lookup shape: sorted overflow vertex table, compact hash, bitset plus
  prefix sum, or cluster-local range.
- Measure whether the lookup cost beats the saved per-vertex header bandwidth.
- Preserve no-spill meshes as the simplest zero-overflow path.
- Define direct vertex and compute shader contracts for the lookup.
- Keep collision and missing-entry behavior explicit in validation.

Acceptance sketch:

- Spill-heavy meshes keep current behavior and correctness.
- Rare-overflow meshes reduce memory and bandwidth without measurable shader
  regressions.
- The lookup path has deterministic tests for absent and present overflow
  vertices.

## `UNorm16` Weights

High-precision asset profiles may need more influence weight precision than
`UNorm8` while still using the Core4+spill structure.

Design questions:

- Define which profiles or assets can request `UNorm16` weights.
- Decide whether indices and weights remain separate buffers or move to an
  interleaved bucket.
- Measure OpenGL/Vulkan stride and cache behavior before accepting the larger
  format.
- Define error tests against FP32 source weights for long chains, tiny bones,
  face rigs, hands, teeth, and eyes.

Acceptance sketch:

- `UNorm16` lands only for profiles where visible deformation improves enough
  to justify bandwidth growth.
- Default runtime remains the smaller `UNorm8` path.
- Direct vertex and compute skinning paths match within the profile threshold.

## Integer-Quantized Affine Palettes

Integer-quantized affine rows could be a better large-world compromise than
blanket FP16 palettes: use compact rotation/scale rows such as `snorm16` while
keeping translation in FP32.

Design questions:

- Define the packed row format and alignment for OpenGL and Vulkan.
- Decide whether normal/cofactor transforms reconstruct from quantized rows or
  use a companion precision path.
- Measure large translations, non-uniform scale, mirrored scale, and tiny bones.
- Compare against FP32 and future FP16 palettes on bandwidth, precision, and
  shader cost.
- Keep GPU physics-chain palette writers compatible with the chosen format.

Acceptance sketch:

- Large-world content preserves translation precision better than FP16.
- Rotation/scale error remains within visual and numeric thresholds.
- GPU physics-chain content does not silently regress.

## GPU-Driven Dirty Vertex Ranges

Partial skeleton updates could mark only affected vertex ranges dirty, allowing
compute skinning to update a subset of the skinned output for localized motion.

Design questions:

- Build or cook bone-to-vertex and bone-to-cluster influence ranges.
- Define how multiple dirty bones combine into compact dispatch ranges.
- Decide whether range generation happens on CPU, GPU, or both.
- Preserve previous-frame outputs for motion vectors and temporal effects.
- Avoid many tiny dispatches that cost more than full-mesh skinning.

Acceptance sketch:

- Localized animation updates fewer vertices than full-mesh skinning.
- Full-mesh fallback remains available when dirty ranges become too fragmented.
- Temporal consumers receive complete current and previous outputs.

## Per-Section Or Per-Meshlet Skinning Dispatch

Cluster renderers may benefit from skinning only visible sections or meshlets
instead of dispatching over the whole mesh.

Design questions:

- Define section/meshlet influence metadata and palette ranges.
- Integrate with GPU visibility so skinning dispatches can follow visible
  cluster sets without CPU readback.
- Decide whether skinned output is section-local, meshlet-local, or written into
  a shared mesh output buffer.
- Coordinate with blendshape cluster-local payloads and meshlet rendering.
- Keep collision with shadow, reflection, and previous-frame passes explicit.

Acceptance sketch:

- Visible-cluster skinning reduces work for large partially visible skinned
  meshes.
- Non-visible sections still have valid bounds or conservative fallbacks.
- Rendering, shadows, and temporal passes agree on which skinned data is valid.

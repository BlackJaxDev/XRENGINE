# CPU Software Occlusion

[← Rendering Architecture index](README.md)

CPU software occlusion is the masked software occlusion path used by `EOcclusionCullingMode.CpuSoftwareOcclusion`. It rasterizes selected opaque occluders into a small CPU reciprocal-depth buffer and tests render-command AABBs before draw submission.

## Scope

The path is implemented by `CpuSoftwareOcclusionCuller`, `MaskedOcclusionBuffer`, `MaskedOcclusionRasterizer`, and `MaskedOcclusionAabbTester`. It runs on the CPU-direct render-command path. The GPU-dispatch path only uses it when count readback is available for diagnostic or instrumented strategies.

## Frame Contract

`RenderCommandCollection.PrepareCpuSoftwareOcclusion` opens the SOC frame for an active camera and viewport. `CpuSoftwareOcclusionCuller.BeginFrame` creates or clears the left-eye buffer and, when a right-eye camera exists, the right-eye buffer. The buffer dimensions come from `CpuSocBufferWidth` and `CpuSocBufferHeight`, clamped to bounded tile-aligned sizes.

`SubmitOccludersFromOpaqueCommands` selects occluders from opaque deferred and opaque forward command lists. It rasterizes them into the active mask. Later mesh commands call `TestVisible(stableQueryKey, bounds)`. The command remains visible when SOC is disabled, debug force-visible is on, no mask exists, the query is unsafe, or the work budget is exhausted.

The result is conservative. False visibility is allowed. False occlusion is a bug.

## Mask Buffer

`MaskedOcclusionBuffer` stores reciprocal depth per pixel and tile coverage metadata. Tiles are `8 x 4` pixels. A tile is occluding only when all required pixels for a query rectangle are covered and the tile depth proves the tested AABB is behind the mask.

The buffer stores depth in CPU arrays and returns a debug readback only when `CpuSocDebugVisualization` is enabled. Visualization-disabled frames avoid debug readback allocation.

## Rasterization

`MaskedOcclusionRasterizer` supports rigid triangle-list meshes. It transforms triangle vertices by the model-view-projection matrix, rejects invalid or near-straddling triangles conservatively, applies culling rules, reserves pixel and tile work, and writes reciprocal depth into the mask.

The scalar rasterizer is the correctness path. SIMD variants must keep scalar parity and must not add ISA-specific public settings or source hierarchies.

## Occluder Selection

The selector accepts only commands that can be used safely as occluders:

- one instance;
- finite world bounds;
- triangle mesh with vertices and triangles;
- no skinning and no blendshapes;
- opaque material inference;
- no alpha-to-coverage;
- no enabled blending;
- depth test enabled;
- depth writes enabled;
- `Less` or `Lequal` depth comparison;
- not `CullMode.Both`;
- no explicit CPU occlusion exclusion.

The selector scores candidates by normalized screen area divided by triangle cost. It enforces `CpuSocOccluderTriangleBudget`, `CpuSocMaxOccluders`, and `CpuSocMinOccluderScreenArea`. Equal scores use `StableQueryKey` for deterministic order. Selected occluder keys are tracked so an occluder does not test against its own mask.

Unknown render-command collection types are skipped if they do not provide an allocation-free, stable traversal under the published read lock.

## Stereo Contract

When a right-eye camera is present and differs from the left camera, SOC builds left and right masks. A command is visible when either eye sees it. This OR-combine rule prevents shared stereo or sequential stereo from hiding geometry that is visible to one eye.

## GPU-Dispatch Scope

For traditional GPU indirect passes, CPU SOC needs CPU-visible counts to iterate the compact candidate list. `GpuIndirectZeroReadback` cannot provide those counts in the frame loop. In that case, `ApplyCpuSoftwareOcclusionToGpuCulledCommands` passes candidates through and logs `CpuSocGpuReadbackDisabled`. Use CPU SOC with `CpuDirect` unless an instrumented path is being measured.

## Telemetry And UI

`OcclusionTelemetry` records selected occluders, rasterized occluders, tested AABBs, culled AABBs, tile work, pixel work, budget bypasses, self-occluder skips, force-visible state, stage timings, and profitability decisions. The ImGui Occlusion panel separates CPU-query, CPU SOC, and GPU Hi-Z counters.

## Settings And Environment
| Setting or variable | Purpose |
|---|---|
| `GpuOcclusionCullingMode=CpuSoftwareOcclusion` | Preferred opt-in mode for CPU SOC captures. |
| `EnableCpuSoftwareOcclusionCulling` | Legacy opt-in side toggle. |
| `XRE_CPU_SOC_OCCLUSION=1` | Environment opt-in for CPU SOC. |
| `CpuSocBufferWidth` / `CpuSocBufferHeight` | Internal mask size. Values are clamped and tile-aligned. |
| `CpuSocOccluderTriangleBudget` | Maximum selected occluder triangles per frame. |
| `CpuSocMaxOccluders` | Maximum selected occluder commands per frame. |
| `CpuSocMinOccluderScreenArea` | Minimum projected occluder area. |
| `CpuSocUseAvx2` | Legacy SIMD preference. It remains only until the shared SIMD policy replaces it. |
| `CpuSocDebugVisualization` | Enables debug depth-buffer readback. |
| `CpuSocDebugForceVisible` | Builds telemetry and masks but returns all tests visible. |

## Known Limits

- SOC remains disabled by default.
- Non-meshlet GPU indirect zero-readback is out of scope for SOC because it cannot read the candidate count.
- Near-straddling occluder triangles are skipped conservatively.
- Unsupported or deformed occluders are rejected.
- One large merged command can only be culled as one unit. It cannot self-occlude.
- Debug visualization exists as readback data. The editor viewport overlay remains a code item.
- Measured SIMD specialization remains a code item.

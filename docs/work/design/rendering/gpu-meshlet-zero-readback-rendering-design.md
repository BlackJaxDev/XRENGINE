# GPU Meshlet Zero-Readback Rendering Design

Last Updated: 2026-05-18
Status: design proposal
Scope: production meshlet rendering through mesh shaders, integrated with the existing GPU zero-readback render pipeline.

Related docs:

- [GPU meshlet zero-readback rendering TODO](../../todo/rendering/gpu/gpu-meshlet-zero-readback-rendering-todo.md)
- [Production rendering pipeline roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md)
- [Production GPU-driven rendering roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md)
- [Zero-readback GPU-driven rendering plan](zero-readback-gpu-driven-rendering-plan.md)
- [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Frame lifecycle and dispatch paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Model import binary cache design](../assets/model-import-binary-cache-design.md)

## 1. Summary

XRENGINE already has the pieces needed for a first meshlet renderer:

- `MeshOptimizerIntegration` can build meshlets from `XRMesh`.
- `MeshletCollection` can upload meshlet data to SSBOs.
- `MeshletCulling.task` and `MeshletRender.mesh` can render through `GL_NV_mesh_shader`.
- `VPRC_RenderMeshesPassMeshlet` routes mesh rendering intent to the GPU path.
- The traditional GPU path already has GPU culling, LOD selection, material scatter, bindless material tables, atlas tiers, and strict zero-readback policy.

The missing production step is to make meshlets part of the same GPU scene database as the traditional indirect renderer. Meshlet rendering should not rebuild sidecar CPU meshlet buffers on first render, should not rely on a CPU visibility mask for normal operation, and should not maintain a separate material/transform system.

The target renderer is:

```text
Source import/cache
    -> engine-native meshes, LODs, meshlets
    -> GPUScene atlas + meshlet buffers
    -> GPU BVH/frustum/Hi-Z culling
    -> GPU LOD selection
    -> GPU visible-command-to-meshlet expansion
    -> DrawMeshTasksIndirectCount / backend equivalent
    -> task shader per-meshlet culling
    -> mesh shader vertex/primitive emission
    -> shared material-table shading
```

`GpuMeshletZeroReadback` becomes a shipping strategy where supported. `GpuMeshletInstrumented` is the matching diagnostics strategy. `GpuIndirectZeroReadback` remains the shipping baseline and fallback on platforms without mesh shaders.

## 2. Goals

- Render meshlet-capable scene geometry with zero CPU readbacks in the steady-state render path.
- Store meshlet descriptors, vertex-reference indices, triangle-local indices, and meshlet ranges in `GPUScene`, not in a per-pass `MeshletCollection` owner.
- Share the same `DrawMetadataBuffer`, `TransformBuffer`, `PrevTransformBuffer`, `BoundsBuffer`, `MaterialStateBuffer`, `LODTableBuffer`, atlas tiers, material table, and visibility results used by the traditional GPU path.
- Generate and cache meshlet and LOD data during model import or cache repair, not during first render.
- Dispatch meshlet work with GPU-written counts, preferably `DrawMeshTasksIndirectCount`.
- Keep the meshlet path equivalent to the traditional indirect path for render-pass filtering, material identity, state class, transforms, skinning, shadows, depth prepass, velocity, stereo, and editor visibility.
- Keep backend support explicit: `GL_EXT_mesh_shader` and `VK_EXT_mesh_shader` are production targets; `GL_NV_mesh_shader` remains opportunistic.
- Preserve the strategy contract: unsupported meshlet rendering falls back to traditional GPU indirect with a warning, not to CPU mesh rendering.

## 3. Non-Goals

- This design does not require Nanite-style cluster DAGs or continuous meshlet LOD in the first production milestone.
- This design does not replace the traditional GPU indirect path.
- This design does not make OpenGL mesh shaders a portability requirement. OpenGL may remain an NV/EXT opportunistic path while Vulkan carries the formal production backend.
- This design does not change texture residency, material authoring, or shader graph behavior except where mesh shaders need to consume the same material-table data.
- This design does not require a new third-party meshlet generation library.

## 4. Current Gaps

### 4.1 Backend Capability

The renderer abstraction exposes mesh shader dialect and dispatch probes. `SupportsMeshletDispatch()` is production-only: it must remain false until a backend has matching shaders plus indirect-count mesh task dispatch from GPU-written counts. OpenGL `GL_NV_mesh_shader` direct dispatch is diagnostic-only and does not satisfy production `GpuMeshletZeroReadback`.

Required change:

- Add an explicit backend capability model:
  - `MeshShaderDialect.None`
  - `MeshShaderDialect.OpenGLNV`
  - `MeshShaderDialect.OpenGLEXT`
  - `MeshShaderDialect.VulkanEXT`
- `SupportsMeshletDispatch()` returns true only when the selected dialect has matching shader sources and dispatch functions.
- Strategy logs should report both requested strategy and selected dialect.

### 4.2 Data Ownership

`MeshletCollection` owns separate buffers:

- meshlets
- visible meshlets
- meshlet vertices
- meshlet vertex-reference indices
- triangle-local indices
- transforms
- materials
- CPU command visibility

This is useful for bring-up, but it duplicates `GPUScene` and prevents meshlets from sharing the zero-readback pipeline.

Required change:

- `GPUScene` owns meshlet buffers.
- `MeshletCollection` becomes a debug/compatibility view or is retired.
- Meshlet descriptors reference existing scene IDs, not private transform/material slots.

### 4.3 Import Timing

Meshlets can be rebuilt lazily from `_commandIndexLookup` during `RenderMeshlets`. That protects the traditional path from meshoptimizer work, but it still means meshlet data is not a resident asset/scene resource.

Required change:

- Meshlet and LOD data is generated at model import/cache-write time.
- Runtime registration only uploads or references already-generated engine-native data.
- Cache miss or stale cache repair may regenerate data off the render path.

The import/cache requirements are specified in [Model Import Binary Cache Design](../assets/model-import-binary-cache-design.md).

### 4.4 Dispatch

The current meshlet path calls `DrawMeshTask(0, numGroups)` from CPU using the total meshlet count. Production needs GPU-visible work counts:

- visible command count comes from GPU culling
- LOD selection changes selected mesh IDs on GPU
- meshlet expansion writes task records and counts on GPU
- CPU issues a fixed set of meshlet dispatches with GPU-written count buffers

### 4.5 Culling

Current task shader culling is limited to render pass, optional CPU command visibility, and frustum planes. Production needs:

- command visibility from the GPU culling pipeline
- per-meshlet frustum culling
- per-meshlet cone-backface culling
- primary-view Hi-Z occlusion
- stereo-safe Hi-Z or pass-specific disable rules
- shadow/depth-prepass gates for culling modes that are not always correct for every material pass

### 4.6 Shading

Current meshlet shading uses a simple meshlet material buffer. Production must consume the same material state as indirect rendering:

- `MaterialStateBuffer`
- material texture handle table
- bindless/descriptor-indexed texture paths
- `DrawID` or material row lookup
- state class and render pass semantics
- generated shader variants for static/skinned, opaque/masked, shadow/depth/velocity/stereo

The implemented direct meshlet pass requires a material-table draw path. If a meshlet strategy is active while the global zero-readback draw path is `FullBucketScan` or `ActiveBucketList`, the pass snapshots `MaterialTable` automatically; explicit `BindlessMaterialTable` remains honored when the backend can provide bindless handles. Override-driven support passes, including the forward depth-normal prepass and full-overdraw debug pass, route through the matching traditional GPU indirect strategy until meshlet override/depth-normal variants exist.

## 5. Target Scene Data

### 5.1 Mesh Data Entry Extension

Traditional draws already use `MeshDataBuffer` entries for:

- index count
- first index
- base vertex
- flags, including atlas tier

Meshlets need per-mesh and per-LOD meshlet ranges. Avoid overloading the existing 4-uint entry until the layout is intentionally revised. Use a sidecar buffer first:

```csharp
public struct GpuMeshletRange
{
    public uint MeshletOffset;
    public uint MeshletCount;
    public uint VertexIndexOffset;
    public uint TriangleIndexOffset;
}
```

`MeshDataID` indexes both `MeshDataBuffer` and `MeshletRangeBuffer`. A mesh with no meshlet data has `MeshletCount = 0` and is skipped by the meshlet expansion pass.

Future packing can fold range metadata into a wider mesh data SoA layout once all callers have moved off the 4-uint compatibility layout.

### 5.2 Meshlet Descriptor

The production descriptor should carry bounds and cone data produced by meshoptimizer:

```csharp
public struct GpuMeshlet
{
    public Vector4 BoundsSphere;      // local/object-space center.xyz, radius.w
    public uint VertexOffset;         // into MeshletVertexIndexBuffer
    public uint TriangleByteOffset;   // into MeshletTriangleIndexBuffer
    public uint VertexCount;
    public uint TriangleCount;
    public Vector4 Cone;              // axis.xyz, cutoff
    public Vector4 ConeApex;          // apex.xyz, reserved
}
```

If memory pressure makes the full cone payload too expensive, the first compression step should pack cone axis/cutoff using meshoptimizer's signed-byte cone values and keep apex as fp16 or reconstruct from bounds only when acceptable.

### 5.3 Meshlet Draw/Task Record

Expansion from visible commands writes compact task records:

```csharp
public struct GpuMeshletTaskRecord
{
    public uint MeshletIndex;
    public uint DrawID;
    public uint TransformID;
    public uint MaterialID;
}
```

The task shader consumes task records rather than scanning every meshlet in the scene. `DrawID` remains the stable bridge to `DrawMetadata`, material-table rows, LOD transition state, and debug counters.

### 5.4 Buffers

`GPUScene` owns:

- `MeshletRangeBuffer`
- `MeshletDescriptorBuffer`
- `MeshletVertexIndexBuffer`
- `MeshletTriangleIndexBuffer`

`GPURenderPassCollection` owns per-pass transient work buffers:

- `VisibleMeshletTaskBuffer`
- `VisibleMeshletTaskCountBuffer`
- `MeshletDispatchIndirectBuffer`
- optional overflow/counter diagnostics

Persistent scene data belongs to `GPUScene`; per-view/per-pass transient results belong to the pass collection.

## 6. Runtime Pipeline

### 6.1 Registration

When `GPUScene` registers or updates a mesh:

1. Resolve the logical mesh and LOD table as today.
2. Ensure each resident `MeshDataID` has an associated meshlet range.
3. Upload meshlet descriptor/index slices into `GPUScene` meshlet buffers.
4. Keep meshlet ranges stable unless the mesh is removed, migrated, or cache data changes.
5. Mark meshlet range data dirty only for changed meshes.

Meshlet data may live in the same atlas tier model as geometry:

- static: immutable after level import/load
- dynamic: load/unload and compaction
- streaming: either unsupported in v1 meshlet path or uses fixed-capacity ranges with explicit update commits

Streaming meshlet generation for editable/procedural meshes is a later milestone. The first production meshlet path may route streaming-tier meshes through traditional indirect rendering.

### 6.2 Visibility And LOD

The meshlet path reuses existing GPU stages:

1. BVH or SoA frustum cull writes visible command buffers.
2. Hi-Z culling filters commands where enabled.
3. `GPURenderLODSelect.comp` updates selected `MeshID` and `LODLevel`.
4. Sort/build-key stages may still run if material/state ordering is needed.

BVH/frustum/Hi-Z run at command granularity before meshlet expansion. A false rejection there removes the whole mesh command, so whole-wall disappearance indicates the pre-meshlet command cull path rather than task-shader meshlet section culling. Hi-Z should use current-frame depth where available; previous-frame history depth is only a fallback when the current depth view cannot be resolved. The exception is a forward/masked color pass following the forward depth-normal prepass: current depth can already contain the same pass' candidates, so command-level Hi-Z is skipped there to avoid self-occluding the mesh before task-shader meshlet frustum/cone culling can run.

The meshlet path must not rebuild visibility on CPU and must not use CPU count readbacks.

### 6.3 Meshlet Expansion

Add `GPURenderExpandMeshlets.comp`:

- Input:
  - visible command buffer or hot command buffer
  - culled count buffer
  - draw metadata
  - meshlet range buffer
  - LOD transition buffer
  - optional sorted index/key buffer
- Output:
  - visible meshlet task records
  - meshlet task count
  - dispatch indirect/count args
  - overflow flag

For each visible command:

1. Resolve selected `MeshID`.
2. Load the meshlet range.
3. Append one task record per meshlet in the range.
4. If LOD transition is active, append previous-LOD meshlet range records with a flag or separate task stream.
5. Preserve `DrawID` so shaders can fetch material and transition data.

The expansion shader must use conservative bounds checks and set an overflow flag rather than writing past capacity.

### 6.4 Dispatch

Preferred dispatch:

```text
DrawMeshTasksIndirectCount(
    indirectBuffer,
    indirectOffset,
    countBuffer,
    countOffset,
    maxTaskGroups,
    stride)
```

Backend mapping:

- Vulkan: `vkCmdDrawMeshTasksIndirectCountEXT`
- OpenGL EXT: `glDrawMeshTasksIndirectCountEXT` or available equivalent
- OpenGL NV: use NV-specific indirect/count support if available; otherwise support only a bring-up direct dispatch path and keep production `GpuMeshletZeroReadback` disabled for that dialect

If a backend only supports CPU-specified direct task counts, it can be used for diagnostics but must not satisfy the shipping `GpuMeshletZeroReadback` strategy.

### 6.5 Task Shader

The production task shader consumes `GpuMeshletTaskRecord`, not a full scene meshlet scan.

Responsibilities:

- reject task records outside render pass/state domain when a pass uses a shared stream
- transform meshlet bounds by `TransformBuffer`
- frustum cull
- cone-backface cull when enabled for the pass
- Hi-Z occlusion for primary view when enabled and valid
- emit visible meshlet indices/payload for the mesh shader

Culling controls should be pass settings:

| Pass type | Frustum | Cone | Hi-Z |
| --- | --- | --- | --- |
| depth prepass | on | on | primary view only |
| shadow depth | on | on | future shadow HZB only |
| opaque forward/deferred | on | optional | primary view only |
| masked | on | conservative/off by default | primary view only |
| transparent | on | off | off |
| velocity | on | off until previous-transform parity is proven | primary view only |

### 6.6 Mesh Shader

The mesh shader emits vertices and primitives from atlas-backed source streams:

- positions
- normals
- tangents
- UVs
- color sets where present
- skinning result streams or palette-deformed positions for skinned variants

It writes the same interpolants that the existing material shaders expect, including:

- world position
- previous world position for velocity variants
- normal/tangent frame
- UV sets
- vertex color
- `DrawID` or material row index as a flat attribute

### 6.7 Fragment/Material Path

The meshlet path should converge with material-table rendering instead of maintaining `MeshletMaterial`.

Initial production material families:

- opaque static
- opaque skinned
- alpha-tested static
- alpha-tested skinned
- shadow depth
- depth/normal
- velocity

The existing per-material shader path may remain available for diagnostics, but production meshlets should prefer generated material-table shaders to keep PSO count bounded.

## 7. Zero-Readback Contract

`GpuMeshletZeroReadback` is a shipping strategy. Therefore:

- no `ReadUIntAt`
- no `MapBufferData` for counts
- no `GetDataArrayRawAtIndex`
- no render-thread `WaitForGpu`
- no CPU fallback mesh draw
- no first-render meshoptimizer rebuild
- no CPU visible command compaction

Allowed CPU work:

- iterating known pass/state class/material families
- binding fixed known buffers
- issuing indirect/count dispatch calls
- logging capability warnings outside tight loops
- reading async diagnostics only under instrumented strategies

`Engine.Rendering.Stats.GpuReadbackBytes` must remain zero for steady-state `GpuMeshletZeroReadback` frames.

## 8. Fallback Rules

Strategy resolution:

1. If `GpuMeshletZeroReadback` is forced and backend supports a production meshlet dialect, use meshlets.
2. If `GpuMeshletInstrumented` is forced and backend supports production meshlets plus diagnostics are enabled, use instrumented meshlets; otherwise collapse to `GpuMeshletZeroReadback` when production meshlets are available.
3. If either meshlet strategy is forced and backend lacks production support, warn and fall back to `GpuIndirectZeroReadback` when available.
3. If zero-readback indirect is unavailable and strict no-fallback mode is active, skip GPU mesh submission with a visible warning rather than drawing CPU meshes.
4. Diagnostic profiles may use bring-up meshlet direct dispatch or CPU validation readbacks only under `GpuIndirectInstrumented` or an explicit meshlet diagnostics mode.

Fallback must be visible in logs and counters so unsupported meshlet rendering is never mistaken for a successful mesh shader path.

## 9. Diagnostics

Add counters:

- requested meshlet strategy frames
- production meshlet frames
- meshlet fallback frames
- meshlet task records emitted
- meshlet task records culled by frustum
- meshlet task records culled by cone
- meshlet task records culled by Hi-Z
- meshlet expansion overflow
- meshlet buffer bytes resident
- meshlet cache hit/miss/stale counts from import

Add logs:

- `Meshlet.BackendSelected`
- `Meshlet.BackendUnsupported`
- `Meshlet.SceneBufferUpload`
- `Meshlet.ExpandOverflow`
- `Meshlet.DispatchSkipped`
- `Meshlet.CacheMissing`
- `Meshlet.CacheStale`

Logs should include render pass, strategy, backend dialect, source model/cache path when relevant, command count, meshlet count, and capacity.

## 10. Implementation Plan

### Phase 1: Capability And Bring-Up Honesty

- Override `SupportsMeshletDispatch()` only for backends that can run the production zero-readback path.
- Add dialect and direct-vs-indirect-count dispatch reporting.
- Keep `GL_NV_mesh_shader` direct dispatch as experimental if indirect-count dispatch is not available.
- Add tests proving meshlet strategy fallback is explicit.

### Phase 2: Import-Generated Meshlet Assets

- Implement the model import binary cache design.
- Add meshlet cone payload to `Meshlet` or a new cooked descriptor.
- Ensure cache freshness includes meshlet/LOD generation settings.
- Add tests that cache load avoids source model parse when fresh.

### Phase 3: `GPUScene` Meshlet Storage

- Add scene-owned meshlet range and data buffers.
- Upload cached meshlet data on mesh registration.
- Track ranges per `MeshDataID` and per LOD.
- Retire render-time `RebuildMeshletsFromUpdatingCommands` for production.

### Phase 4: GPU Expansion And Count Dispatch

- Add `GPURenderExpandMeshlets.comp`.
- Add per-pass meshlet task and count buffers.
- Add backend wrappers for mesh task indirect/count dispatch.
- Add overflow handling and stats.

### Phase 5: Production Shaders

- Convert task shader to consume task records.
- Add cone culling.
- Add primary-view Hi-Z culling.
- Convert mesh shader to atlas streams and shared material-table data.
- Add static and skinned variants.

### Phase 6: Pass Coverage

- Depth prepass.
- Opaque forward/deferred.
- Alpha tested.
- Shadow depth.
- Velocity.
- Stereo.
- Capture passes.

### Phase 7: Hardening

- 30 minute `GpuMeshletZeroReadback` soak.
- 100K command stress with zero readbacks.
- B1/B2 visual parity against `GpuIndirectZeroReadback`.
- Dense geometry performance target: meshlet path within 10 percent of indirect path at minimum, ahead on geometry-heavy scenes.
- OpenGL/Vulkan parity tests.

## 11. Test Plan

Unit/source-contract tests:

- `GpuMeshletZeroReadback_BackendUnsupported_FallsBackToZeroReadback`
- `GpuMeshletZeroReadback_NoCpuRenderFallback`
- `GpuMeshletZeroReadback_NoReadbackHelpersInShippingPath`
- `MeshletData_StoredInGPUScene_NotMeshletCollectionOwner`
- `MeshletRange_PerLODMeshDataID`
- `MeshletExpand_UsesCulledCommandBufferAndLODSelection`
- `MeshletTaskShader_ConsumesTaskRecords`
- `MeshletTaskShader_ConeCullUsesCookedConeData`
- `MeshletTaskShader_HiZBindingsPresentWhenEnabled`

GPU/integration tests where hardware allows:

- `Meshlet_RenderPath_ProducesCorrectOutput_NVMeshShader`
- `Meshlet_RenderPath_ProducesCorrectOutput_EXTMeshShader`
- `Meshlet_VulkanEXT_Parity`
- `Meshlet_TaskShaderCull_MatchesBVHFrustumResults`
- `Meshlet_SharedBVHCull_ThenMeshletExpansion`
- `Meshlet_LODIntegration_CorrectMeshletRangePerLOD`
- `Meshlet_BindlessMaterials_TextureCorrect`
- `Meshlet_SkinnedMesh_UsesGpuResidentSkinningAndBounds`
- `Meshlet_ZeroReadback_StatsRemainZero`

Scene validation:

- B1 two Sponzas, lights off.
- B2 B1 plus idle skinned avatars.
- High material diversity.
- Dense static geometry.
- Masked foliage scene.
- Stereo OpenVR/OpenXR smoke when backend supports it.

## 12. Acceptance Criteria

- `GpuMeshletZeroReadback` renders B1 and B2 identically to `GpuIndirectZeroReadback` within expected material/pass tolerances.
- `GpuMeshletZeroReadback` records zero `Stats.GpuReadbackBytes` in steady-state Release frames.
- Meshlet data is stored in `GPUScene` and keyed by `MeshDataID`/LOD, with no production dependency on `MeshletCollection` sidecar ownership.
- Meshlet dispatch consumes GPU-written counts.
- Meshlet task shaders perform frustum culling, cone culling, and primary-view Hi-Z culling where enabled.
- Materials render through the same material-table/bindless policy as the zero-readback indirect path.
- Unsupported hardware falls back to `GpuIndirectZeroReadback` with clear diagnostics.
- Fresh model binary caches can provide meshlets and LODs without reading the original third-party source.

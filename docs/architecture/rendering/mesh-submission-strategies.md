# Mesh Submission Strategies

[<- Rendering Architecture index](README.md)

Mesh drawing is selected by an explicit `EMeshSubmissionStrategy` instead of by interpreting `GPURenderDispatch` directly. The strategy is resolved once from profile, settings, and renderer capability, then applied to mesh render commands in `DefaultRenderPipeline`, `AdvancedRenderPipeline`, capture helpers, and the debug opaque pipeline.

## Strategies
| Strategy | Purpose | CPU readbacks | CPU mesh fallback | Hot-path diagnostics |
|----------|---------|---------------|-------------------|----------------------|
| `CpuDirect` | CPU traversal and direct mesh draw submission. | None in the steady-state render path. | Not applicable. | CPU renderer diagnostics only. |
| `GpuIndirectInstrumented` | GPU indirect path for bring-up, validation, and inspection. | Allowed and counted. | Allowed only when explicitly requested and strict profiles are not active. | Allowed. |
| `GpuIndirectZeroReadback` | Production GPU indirect path. | Forbidden in the steady-state render path. | Forbidden. | Forbidden; use counters and warnings outside the hot path. |
| `GpuMeshletInstrumented` | Meshlet/task-mesh path for bring-up, validation, and inspection. | Allowed only when diagnostics are explicitly enabled, and counted. | No implicit CPU fallback; unsupported renderers fall back visibly to the non-meshlet resolver path. | Allowed. |
| `GpuMeshletZeroReadback` | Production meshlet/task-mesh submission from GPU-written counts. | Forbidden by contract. | No implicit CPU fallback; unsupported renderers fall back visibly to `GpuIndirectZeroReadback` when possible. | Forbidden; use counters and warnings outside the hot path. |

`GPURenderDispatch` remains a compatibility shim during migration. Setting it to `true` maps through the resolver; older boolean-only call sites still map `true` to `GpuIndirectInstrumented` to preserve legacy behavior.

WebGPU preserves the requested authored strategy, including instrumented modes
when diagnostics are disabled. Generic traditional indirect and compute-meshlet
routes share a material-independent resident `GPUScene` publication and exact
authored raster preparation. Traditional indirect uses a cooked GPU producer to
refit current position bounds and publish one whole-primitive argument record
over the frozen original index stream; it requires no meshlet payload. The
generic meshlet route uses cooked GPU
cull/expand and bounds-refit companions, and GPU-written uint32 indices and
`drawIndexedIndirect` arguments. It reuses the original authored raster program
and does not require Advanced native material eligibility or hardware task/mesh
stages. Pending or rejected meshlet work never falls through to original
indexed replay. Both routes use the shared GPU-selected resident LOD candidates. Runtime instance
publishers provide exact current/previous transforms and bounds under the authored
raster ABI; conservative GPU union visibility preserves the full native instance
count and first-instance zero without optional instance remapping.
Bounded transparent GPU ordering uses the actual full-resident collection's
frozen insertion tokens, GPU source ranks and candidate-owned raster input
copies. Explicit CPU-owned V2 color/coverage sources retain direct draws through
verified vertex rank gates in the same ordered replay. Unproven raster programs,
non-mesh commands and other unavailable selected profiles report specific
diagnostics. The [authored meshlet lowering
record](../../work/progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md)
describes ownership, conservative bounds, capacities, and pending runtime
acceptance.

Zero-readback passes execute from configured GPU-pass topology even when CPU
visibility collection publishes no mesh commands. The GPU scene and the pass
culling mask own mesh membership; CPU visibility must not suppress a default,
capture, or shadow pass before its GPU dispatch.

Advanced late color and participating temporal passes declare filtered
`CpuDirect` submission independently of the opaque native GPU family. Their
draws still execute on the GPU; the CPU filters authored late-lane eligibility.
The temporal pass preserves that explicit strategy when a global opaque GPU
override is active. Requesting GPU dispatch on a pass that requires filtered
late participation is rejected because indirect replay cannot preserve that
per-material admission contract.

The CPU-built indirect reference is an explicit diagnostic sub-mode of
`GpuIndirectInstrumented`. Launch with both
`XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectInstrumented` and
`XRE_FORCE_CPU_INDIRECT_BUILD=1` to rebuild indirect commands on the CPU while
retaining indirect submission. The CPU-reference flag accepts only `1` or
`true` (case-insensitive) and is ignored by `CpuDirect`, zero-readback, and
meshlet strategies. It permits the diagnostic readbacks declared by the
instrumented strategy and must not be used as production evidence.

The strategy also owns scene visibility acceleration. `CpuDirect` uses the CPU scene hierarchy (CPU BVH by default). Every GPU indirect and meshlet strategy requests the internal `GPUScene` command BVH. There is no independent GPU-BVH setting or Vulkan environment gate; if the BVH shader or provider buffers are not ready, the pass reports the condition and temporarily uses flat GPU frustum culling. The GPU hierarchy's bounds, revision, compact-layout, construction, traversal, and fallback contracts are documented in [GPU Scene BVH](gpu-scene-bvh.md).

The CPU hierarchy's snapshot publication, mutation, traversal, and diagnostics contracts are documented in [CPU Scene BVH](cpu-scene-bvh.md).

## Renderer binding and scene IDs

`RenderCommandMesh3D` keeps mutation subscriptions for its renderer, submeshes,
materials, render options, and meshes. A successful bind publishes a renderer
token. A repeated assignment of that renderer can skip the subscription gate
while all groups and overrides remain current. Subscription changes invalidate
the token before they start. A failed refresh leaves it invalid, so a later
assignment retries the bind. Property notifications and cancellation still use
`SetField`; the final bind uses the value that the command retained.

GPUScene mesh and material ID maps keep entries for the scene lifetime.
Existing IDs use read lookups. A missing reverse entry is still published when
another caller has inserted only the forward entry. ID factories use cached
static delegates, so repeated ID lookup creates no captured delegate.

## Resolver

`Engine.Rendering.ResolveMeshSubmissionStrategy()` uses:

- `Engine.EffectiveSettings.GPURenderDispatch`
- `Engine.EffectiveSettings.VulkanGpuDrivenProfile`
- `EnableGpuIndirectDebugLogging`, `EnableGpuIndirectValidationLogging`, and `EnableGpuIndirectCpuFallback`
- `EnableZeroReadbackMaterialScatter`
- active renderer probes: `SupportsIndirectCountDraw()`, `MeshShaderDialect`, `SupportsDirectMeshTaskDispatch()`, `SupportsIndirectCountMeshTaskDispatch()`, `SupportsProductionMeshletShaders()`, and `SupportsMeshletDispatch()`
- `ForceMeshSubmissionStrategy` or `XRE_FORCE_MESH_SUBMISSION_STRATEGY`

Diagnostics profiles resolve to `GpuIndirectInstrumented`. `ShippingFast` resolves to `GpuIndirectZeroReadback` when indirect-count draw is supported. If zero-readback is requested but indirect-count draw is missing, strict profiles downgrade to `CpuDirect` with a visible warning; permissive profiles downgrade to `GpuIndirectInstrumented`.

## Meshlet Capability Contract

`SupportsMeshletDispatch()` means the backend can run the production zero-readback meshlet path: matching shader dialect, production task/mesh shaders, and indirect-count mesh-task dispatch from GPU-written counts.

The browser's native Advanced stage family has a separate compute/indirect
meshlet capability. It consumes canonical resident meshlet records, compacts
triangle identities on the GPU, and issues vertex-pulled indirect raster work
without reading visibility or counts back to the CPU. This does not advertise
hardware task/mesh shader extensions. The separate generic Default/custom
compute-meshlet route preserves authored raster programs and has its own
residency, deformation and indirect-index contracts described above. WebGPU
preserves an explicitly requested submission strategy and reports unsupported
operations instead of applying the desktop fallback policy described below. See
[WebGPU indirect submission](webgpu-indirect-submission.md).

The lower-level hardware probes describe partial backend support:

- `MeshShaderDialect` reports `None`, `OpenGLNV`, `OpenGLEXT`, or `VulkanEXT`.
- `SupportsDirectMeshTaskDispatch()` covers CPU-specified task counts and is diagnostic-only.
- `SupportsIndirectCountMeshTaskDispatch()` covers backend mesh-task dispatch from GPU-written indirect arguments and GPU-written indirect-command counts.
- `SupportsProductionMeshletShaders()` covers the task/mesh shader source and binding side of the production path.

OpenGL `GL_NV_mesh_shader` currently exposes direct task dispatch only. It is useful for bring-up and shader diagnostics, but it does not satisfy production `GpuMeshletZeroReadback`. OpenGL `GL_EXT_mesh_shader` and Vulkan `VK_EXT_mesh_shader` can expose indirect-count task dispatch, but `SupportsMeshletDispatch()` remains false until production task-record shaders are wired for that dialect.

When either meshlet strategy is forced on unsupported hardware, the resolver chooses `GpuIndirectZeroReadback` if indirect-count draw is available. If neither production meshlets nor zero-readback indirect can run, strict profiles resolve to `CpuDirect`; permissive diagnostic profiles resolve to `GpuIndirectInstrumented`. Forced `GpuMeshletInstrumented` is honored only with the Diagnostics Vulkan profile or `EnableGpuIndirectDebugLogging`; otherwise it collapses to `GpuMeshletZeroReadback` when production meshlet dispatch is available. Warnings and GPU profiler labels include the requested strategy, selected strategy, backend dialect, and fallback reason.

## Zero-Readback Material Draw Paths

`GpuIndirectZeroReadback` has a second selector, `EZeroReadbackMaterialDrawPath`, available through `Engine.Rendering.Settings.ZeroReadbackMaterialDrawPath`, user/project overrides, editor debug preferences, and `XRE_ZERO_READBACK_MATERIAL_DRAW_PATH`.
| Draw path | Purpose |
|-----------|---------|
| `FullBucketScan` | Strict no-readback path. The GPU scatters commands into state-class/tier buckets and the CPU loops over every bucket while using GPU-written counts. |
| `ActiveBucketList` | Readback-assisted diagnostic path. Adds a compute compaction pass that writes only non-empty bucket IDs, maps that compact ID list on the CPU, then submits those buckets. Useful for measuring empty-bucket overhead. |
| `MaterialTable` | Production zero-readback material-table path. GPU scatter and GPU-written counts feed the shared deferred material-table shader without mapping count/range buffers. The OpenGL path reads material constants from `MaterialTable`; explicitly unsupported/non-deferred passes are planned through the traditional compatible tier rather than an implicit CPU fallback. |
| `BindlessMaterialTable` | Production zero-readback material-table path with backend-specific texture indexing. OpenGL requires `GL_ARB_bindless_texture` and `GL_ARB_gpu_shader_int64` and samples resident handles through `MaterialTextureHandleTable`; Vulkan requires descriptor indexing plus the global material texture descriptor table and samples `XR_BindlessMaterialTextures[nonuniformEXT(index)]`. Requested bindless paths log a visible warning or fail in Required mode when the active backend cannot satisfy the contract. |

`Engine.EffectiveSettings.VulkanGpuDrivenProfile` resolves from the grouped
default `Engine.Rendering.Settings.Vulkan.GpuDriven.Profile` plus project
overrides. The flat `Engine.Rendering.Settings.VulkanGpuDrivenProfile` property
remains a compatibility alias.

Vulkan bindless material modes are controlled by `Engine.Rendering.Settings.Vulkan.Descriptors.BindlessMaterialMode` or `XRE_VULKAN_BINDLESS_MATERIAL_MODE` with `Auto`, `Disabled`, `Required`, and `Diagnostics`. Startup logs report `Capability.BindlessMaterialTextures` with the resolved mode, tier, descriptor capacity, table readiness, shader readiness, draw-path readiness, and the reason when unavailable.

Meshlet strategies always promote the per-pass draw path to `MaterialTable` unless `BindlessMaterialTable` was explicitly selected. Direct meshlet shading does not have a per-material bucket shader path, so `FullBucketScan` and `ActiveBucketList` remain traditional-indirect options only.

## State Class IDs

Phase C separates material identity from pipeline state. `DrawMetadata.MaterialID` still identifies the material data, but `DrawMetadata.StateClassID` is the batching key consumed by sort/scatter shaders.

Default derivation:

- Transparent-like materials or transparent/on-top/OIT passes resolve to `Transparent`.
- Masked, alpha-tested, or alpha-to-coverage materials and masked passes resolve to `AlphaTested`.
- Opaque deferred passes resolve to `OpaqueDeferred`.
- Remaining opaque forward/shadow-compatible draws resolve to `OpaqueForward` unless a renderer-specific exception allocates a custom state class.

`GpuIndirectZeroReadback` material scatter uses `DrawMetadata.MaterialID` as the bucket key. `StateClassID` remains the coarse pipeline-state key; it must not substitute for material identity. Material-table draw paths use the stable `DrawID` to fetch the material row and preserve per-material constants such as base color, opacity, roughness, metallic, specular, and emission.

Dynamic material-table layouts for additional deferred, forward+, Uber, and annotated custom shader paths are proposed in [Dynamic Indirect Material Bindings](../../work/design/rendering/dynamic-indirect-material-bindings.md).

## Vulkan Command-Chain Volatility

Vulkan command chains do not choose the mesh submission strategy; they cache the backend recording work after a pass has already resolved to CPU direct, GPU indirect, or meshlet submission. The pass strategy still owns which draw commands are generated. Command-chain lowering only classifies the resulting Vulkan `FrameOp` stream for reuse.

The command-chain volatility contract is:
| Volatility | Meaning |
|------------|---------|
| `FrameDataOnly` | The chain structure is stable and only per-frame data such as view/projection matrices, model matrices, material constants, descriptor contents, or resource-plan generations may need refresh. |
| `DynamicCommand` | The command payload itself can change every frame, such as UI text, ImGui/profiler overlays, editor gizmos, or diagnostic draws. |

Mesh scene draws should remain `FrameDataOnly` when their target, pass, pipeline identity, descriptor layout, mesh buffers, draw count, and view/shadow identity are unchanged. Camera movement, transform publication, and ordinary material constant updates should refresh chain frame data without re-recording static secondary command buffers. Structural changes such as a different mesh, target, pass, descriptor layout, pipeline generation, render target attachment signature, shadow atlas packing, or VR eye/single-pass mode change must dirty the affected chain.

Dynamic overlay work is deliberately split from static mesh chains. A text/profiler overlay changing every frame should record only its volatile chain and must not invalidate the static Sponza-style scene mesh chains underneath it.

### Vulkan packet and descriptor lifetime

CPU-direct mesh work is lowered into contiguous, state-compatible packets. The
initial packet target is 10-64 draws with the same pass, target, view,
pipeline/material program, descriptor-set schema, transparency class, and
scheduling context. Packet boundaries never reorder draws, so transparent order
and query boundaries remain primary-command-buffer concerns. Each frame slot
owns one cheap primary execution list; a visibility change re-records that list
instead of adding another whole-frame primary cache variant. Scheduled packet
caches are bounded per frame slot and evict least-recently-used packet ranges.

Material and imported-texture descriptors use stable per-frame descriptor-set
identities. A frame slot's descriptor contents are refreshed only after that
slot has completed, before its cached secondary is submitted again. Changes are
classified as frame data, compatible content publication, binding identity, or
structural layout. Compatible publication may change the sampled image/view and
content generation while retaining the descriptor set and layout; it refreshes
only packets that reference the changed descriptor generation. Binding-identity
and structural-layout changes rebuild the affected packet. Resource retirement
continues through the existing frame-completion deferred-destruction path, so an
old image/view remains alive until every prior slot that could reference it has
completed.

## Pass Contract

`PreRender` and `PostRender` remain CPU-only in the default pipelines. Scene geometry passes request the resolved mesh submission strategy. Capture commands (`VPRC_RenderCubemap`, `VPRC_RenderToCubemapFace`, `VPRC_RenderToTextureArray`) carry the same strategy so capture passes do not silently choose a different submission path.

`GPURenderPassCollection` snapshots the strategy at pass execution:

- `CpuDirect` does not consume GPU Hi-Z visibility snapshots. Hardware CPU queries remain the default occlusion path, and optional CPU masked software occlusion can pre-cull traditional CPU mesh draws before hardware-query submission when explicitly enabled.
- The forward depth-normal prepass must resolve the same effective mesh submission strategy as the later lit pass. A forced `CpuDirect` strategy keeps the prepass on CPU even if the legacy GPU-dispatch preference is true; otherwise AO/depth can be populated by GPU draws while the color pass renders CPU. When the lit path is meshlet, the prepass uses the matching traditional GPU indirect strategy because the direct meshlet material-table shader does not support override/depth-normal material variants yet.
- GPU Hi-Z culling prefers the current-frame depth view. Temporal history depth is only a fallback when the current depth view is unavailable, because history-depth false occlusion rejects whole command records before meshlet expansion can refine visibility. Current-frame depth is explicitly disabled as a Hi-Z occlusion source for `OpaqueForward` and `MaskedForward` while the forward depth-normal prepass is enabled: that prepass can already contain the same candidates, so using it would self-occlude whole commands before per-meshlet culling runs.
- `GpuIndirectZeroReadback` enables state-class/tier scatter, consumes GPU-written draw counts directly, and does not call CPU readback helpers such as `ReadGpuBatchRanges()` or `ReadUIntAt(...)` for counts. Use `FullBucketScan` when validating the strict no-readback material path; the active-bucket and material-table variants intentionally read back the compact active bucket list for diagnostics.
- Instrumented strategies are the only strategies allowed to read back batch ranges, count buffers, per-view draw counts, indirect command dumps, or explicitly classified meshlet evidence. `GpuIndirectInstrumented` still uses the material-tier scatter draw path by default so diagnostics render with the same per-material shaders and textures as the production GPU path. CPU masked software occlusion can also run here by preparing CPU occluders, reading the GPU-cull count, and compacting the culled indirect command buffer for validation.
- `GpuMeshletZeroReadback` uses the same GPU-resident scene database and material-table policy as the zero-readback indirect path, then dispatches mesh tasks from GPU-written task records and counts. It must not read count/visibility buffers back in steady state. The meshlet pass snapshots `MaterialTable` automatically if the global zero-readback draw path is `FullBucketScan` or `ActiveBucketList`.
- A diagnostics profile may request fence-delayed meshlet evidence while the
  effective submission strategy remains `GpuMeshletZeroReadback`. These copies
  snapshot immediately after the producer in the ordered frame stream and are
  submitted for host readback only after the accepted graphics submission.
  They are classified separately from generic production readback/map counters;
  enabling this evidence must not enable synchronous pass diagnostics.
- `GpuMeshletInstrumented` uses the meshlet path with diagnostic readbacks and timing stats enabled only under explicit diagnostics. Current readbacks include visible meshlet count, dispatched meshlet count, expansion overflow, dispatch duration, and readback-byte accounting.
- Direct `RenderGPU(pass, strategy)` calls map the legacy combined strategy to a submission mode plus primitive-path preference. The explicit `RenderGPU(pass, mode, primitivePathPreference)` overload sets meshlet pipeline intent for the duration of any non-traditional dispatch, so side passes cannot request a mesh-shader route and then arrive at the render manager as traditional intent.
- Once a meshlet strategy reaches the render manager, a meshlet dispatch failure skips that meshlet pass and logs `Meshlet.BackendUnsupported`; it must not fall through into traditional indirect mesh rendering. Non-meshlet fallback is selected earlier by the resolver when production meshlet dispatch is unavailable.
- CPU safety-net mesh fallback is only available for `GpuIndirectInstrumented` and only when fallback diagnostics are explicitly enabled.

## Kill Switch

Use `Engine.Rendering.Settings.ForceMeshSubmissionStrategy` for local triage. The environment variable `XRE_FORCE_MESH_SUBMISSION_STRATEGY` accepts any `EMeshSubmissionStrategy` name and takes precedence over the setting for the current process.

Forced non-meshlet strategies bypass the resolver. Forced meshlet strategies are capability-gated so they cannot silently select the experimental CPU-count mesh shader path. For one migration window, serialized or environment values that use the legacy token `GpuMeshlet` are accepted and remapped to `GpuMeshletZeroReadback` with a deprecation warning.

## Resolver Downgrade Surface

When the resolver downgrades a forced meshlet strategy, it snapshots state for the editor UI on `Engine.Rendering`:

- `LastMeshletDowngradeRequested` — the meshlet strategy the user asked for.
- `LastMeshletDowngradeResolved` — what the resolver substituted (typically `GpuIndirectZeroReadback`, or `CpuDirect` under strict no-fallback profiles).
- `LastMeshletDowngradeReason` — human-readable reason string (matches the `RenderDispatch.MeshSubmissionStrategy.UnsupportedGpuMeshlet` warning).
- `LastResolvedRendererBackend` / `LastResolvedMeshShaderDialect` / `LastResolvedSupportsMeshletDispatch` — capability snapshot used to render the dropdown tooltip and Occlusion panel banner.

All four are `null`/default when no downgrade is active. They update every time `ResolveMeshSubmissionStrategy()` is called. The `ForceMeshSubmissionStrategy` setting's `[Description]` attribute (rendered as the property-editor tooltip) calls out the mesh-shader dialect requirement.

Mesh-shader dialect availability today:

- `VulkanEXT` (`VK_EXT_mesh_shader`): production. `vkCmdDrawMeshTasksIndirectCountEXT` is wired; `SupportsMeshletDispatch()` returns true.
- `OpenGLEXT` (`GL_EXT_mesh_shader`): production shader variants exist (`MeshletCullingExt.task`, `MeshletRenderExt.mesh`, `MeshletRenderSkinnedExt.mesh`), but the `glMultiDrawMeshTasksIndirectCountEXT` C# delegate isn't wired and current driver coverage is thin. `SupportsMeshletDispatch()` returns false.
- `OpenGLNV` (`GL_NV_mesh_shader`): NVIDIA-only; this engine implements direct task dispatch for diagnostics, while the extension's indirect-count entrypoint is not implemented in the production path.
- `None`: resolver downgrades any forced meshlet strategy.

## Meshlet Payload Cooking Contract

Meshlets are derived asset data. The import and cook service creates them; the
renderer does not.

- `XRMesh.MeshletPayload` holds portable CPU data: descriptors,
  vertex-reference indices, local triangle indices, bounds, cones, generation
  settings, and provenance. Do not cache backend buffers, descriptor handles,
  command buffers, or pipelines in a mesh or model asset.
- Import finishes every topology change and generates LODs first. Then it
  generates one payload for each renderable LOD. A disabled-generation policy
  is stored as an explicit payload state.
- The standalone cooked `XRMesh` payload is the first persistence path. The
  importer fills it before imported sub-assets are externalized.
- For a model binary cache, the container meshlet section
  (`ModelBinaryMeshletSectionCodec`) is primary. `MeshletPayloadDiskCache` is
  secondary and serves repair, standalone, procedural, and legacy meshes. Both
  use one shared meshlet-section codec.
- Rendering does no meshlet source hashing, disk reads or writes, native
  meshoptimizer calls, or cache publication. `GPUScene` registration consumes
  an immutable validated payload and a revision token.
  `XRMesh.GetOrCreateMeshletPayload` must not become a disk-cache API that
  rendering calls.
- Runtime format compatibility is separate from cook provenance. A runtime
  without the meshoptimizer cooker accepts a compatible baked payload. A newer
  cooker version invalidates derived data during import or cache validation,
  not during a rendered pass.
- Cache bytes use stable cache-local mesh and LOD IDs. Absolute paths, display
  names, transient GUIDs, process state, and GPU handles are not part of the
  bytes.
- v1 payloads use one portable size profile:
  `MeshletPayload.PortableMaxVertices` (64) and
  `MeshletPayload.PortableMaxTriangles` (124). Change it only together with
  shader specialization and device-limit negotiation.
- Traditional and task/mesh bins can coexist in one pass without CPU readback.
  The planner classifies unsupported meshlet draws before the pass plan is
  sealed. Device capability, shader and pipeline readiness, and per-draw
  eligibility are separate facts with separate diagnostics.

Broad model and prefab binary-cache hydration is owned by the
[Model Import Binary Cache TODO](../../work/todo/assets/model-import-binary-cache-todo.md).

## Production Closeout Status

The unconditional meshlet import/runtime production gate completed on
2026-08-22. The accepted evidence includes first-import cooking and standalone
warm hydration, exact mixed routing, Vulkan EXT indirect-count mesh-task
submission, conservative Hi-Z, three-view Sponza parity, generation-safe
replacement/unload lifetime, parallel command-worker ownership, uncapped
ShippingFast characterization, 86 focused Release regression tests, and a final
uncapped Vulkan smoke with zero readback, maps, CPU/forbidden fallback, or
VUIDs. See the
[meshlet production closeout evidence](../../work/investigations/rendering/meshlet-import-production-closeout-2026-08-20.md).

Broad model/prefab binary-cache hydration remains a separately owned
conditional dependency; it is not required to reinterpret or reopen this
submission contract. The completed closeout releases the prerequisite hold on
Vulkan resident draw-stream work.

## GPU-Driven Production Topology

Production GPU-driven submission records stable pass topology on the CPU and lets GPU-written buffers change per-frame draw contents. Visibility, material scatter, indirect command counts, meshlet task counts, and delayed diagnostics are data. They must not force primary command rerecording by themselves. A rerecord needs a topology, capacity, binding, pipeline, resource-generation, render-target, or view-mode change.

`GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` submit GPU-written counts directly. They must not map current-frame visibility, range, count, or diagnostic buffers for submission decisions. Instrumented strategies can read diagnostic data when counters name the readback site.

Current-depth two-pass Hi-Z occlusion is implemented by `GPURenderPassCollection.TwoPassOcclusion.cs`. The first pass builds an early visible stream. The second pass consumes current depth and previous visibility state to update late visibility. The diagnostic descriptor names both phases and does not authorize host reads before the native submission completes.

The Vulkan scene-database buffer-device-address prototype is an optional geometry-fetch mode. When the active profile enables it and the renderer reports `ISceneDatabaseDeviceAddressBackendCapability`, generated shader code can consume scene database buffer addresses instead of descriptor bindings for supported records. Unsupported backends must report a downgrade reason.

OpenGL sparse mesh residency is not implemented. OpenGL uses arena allocation and explicit fallback diagnostics for this contract until a separate feature lands.

## Canonical Indexed Instance Groups

`AdvancedVisibilityInputVariant` builds ranges from each consumer's frozen
submission strategy after it copies the shared preparation. Shared deformation
runs once per world, scene, and frame. A second consumer can use another strategy
without reopening the shared output slot.

`AdvancedIndexedInstanceGroupPlanner` combines single-instance indexed payloads
only when their retained index and vertex bytes, primitive section, vertex
layout, material constants, texture and sampler references, coverage, and raster
range agree. Hash matches require full equality. The planner caches proven
content classes under exact database, table, and arena generations. Warm frames
scan draw witnesses without scanning immutable geometry bytes again. Authored
multi-instance payloads keep a separate group and their original instance count.

Each group has a fixed member segment. GPU visibility appends visible payload IDs
to that segment. A finalizer writes one indexed argument per nonempty group.
`BuildVisibilityIndirect`, `LateVisibility`, and
`CullDirectionalShadowCasters` use one atomic increment per group-member
reservation. Their counters record attempted reservations. A capacity check
rejects excess writes and increments the overflow diagnostic.
`FinalizeIndexedInstanceGroups` clamps member counts to the reserved segment
capacity, so an overflow cannot discard the valid prefix or expose unwritten
payloads. Each candidate belongs to one group. Member segments do not overlap.
Counters start at zero for each view or cascade, and a storage barrier separates
member writes from finalization. Member reservations have no retry loop; range
reservations retain their existing bounded compare-and-swap loops.

`LateVisibility` also uses one atomic increment for its visible-output
reservation. It bounds the deferred input count by the payload capacity and
checks each output index before a write. The `lateDraws` counter is diagnostic
data; it does not supply an indirect draw count.

The argument uses `VertexOffset=0` and `FirstInstance` as the member-table base.
The vertex shader selects each member's draw, material, transform, current and
previous deformation offsets, selection ID, and primitive base. Shared indexed
topology does not share pose or temporal state.

Early and late visibility use separate member tables and counters. The sealed
raster phase selects the matching table for indexed and meshlet consumers.
OpenGL stereo first combines the eye masks, then appends each visible payload
once. Vulkan retains its packed vertex binding for pipeline compatibility; the
indexed shader reads member vertices through the canonical storage buffers.

## Vulkan Visibility Memory Placement

`VulkanFrameDataArena` requests mapped device-local memory only for
`AdvancedVisibilityStorage`. `VulkanMappedFrameArenaBackend` tries compatible
memory types in this order:

1. Host-visible, device-local, and host-coherent.
2. Host-visible and device-local, including noncoherent memory.
3. Host-visible and host-coherent.
4. Host-visible.

Each memory type is attempted at most once. Only `ErrorOutOfDeviceMemory`
permits another allocation attempt. Device loss closes admission, and other
native errors stop the allocation. A successful retry increments the existing
Vulkan OOM fallback counter. Other frame-data lanes, including scene storage,
upload, and readback, retain their existing memory selection policy.

All choices remain mapped. The selected memory type's actual flags control
coherent writes and noncoherent flushes. Flush alignment, slot fences, buffer
usage, and device-address allocation flags remain in force.

The existing allocation registry retains the actual `MemoryTypeIndex` and
memory property flags for each chunk. A cold Vulkan allocation log records the
visibility chunk's owner, buffer and memory handles, allocated bytes, type
index, numeric and named property flags, and whether allocation was retried.
This record is emitted once per chunk allocation, not each frame. Use the
selected flags to verify placement; the requested preference alone does not
prove device-local storage.

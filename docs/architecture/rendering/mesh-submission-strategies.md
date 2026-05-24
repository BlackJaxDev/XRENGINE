# Mesh Submission Strategies

[<- Rendering Architecture index](README.md)

Mesh drawing is selected by an explicit `EMeshSubmissionStrategy` instead of by interpreting `GPURenderDispatch` directly. The strategy is resolved once from profile, settings, and renderer capability, then applied to mesh render commands in `DefaultRenderPipeline`, `DefaultRenderPipeline2`, capture helpers, and the debug opaque pipeline.

## Strategies

| Strategy | Purpose | CPU readbacks | CPU mesh fallback | Hot-path diagnostics |
|----------|---------|---------------|-------------------|----------------------|
| `CpuDirect` | CPU traversal and direct mesh draw submission. | None in the steady-state render path. | Not applicable. | CPU renderer diagnostics only. |
| `GpuIndirectInstrumented` | GPU indirect path for bring-up, validation, and inspection. | Allowed and counted. | Allowed only when explicitly requested and strict profiles are not active. | Allowed. |
| `GpuIndirectZeroReadback` | Production GPU indirect path. | Forbidden in the steady-state render path. | Forbidden. | Forbidden; use counters and warnings outside the hot path. |
| `GpuMeshletInstrumented` | Meshlet/task-mesh path for bring-up, validation, and inspection. | Allowed only when diagnostics are explicitly enabled, and counted. | No implicit CPU fallback; unsupported renderers fall back visibly to the non-meshlet resolver path. | Allowed. |
| `GpuMeshletZeroReadback` | Production meshlet/task-mesh submission from GPU-written counts. | Forbidden by contract. | No implicit CPU fallback; unsupported renderers fall back visibly to `GpuIndirectZeroReadback` when possible. | Forbidden; use counters and warnings outside the hot path. |

`GPURenderDispatch` remains a compatibility shim during migration. Setting it to `true` maps through the resolver; older boolean-only call sites still map `true` to `GpuIndirectInstrumented` to preserve legacy behavior.

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

The lower-level probes describe partial backend support:

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
| `MaterialTable` | Readback-assisted diagnostic path. Uses active material buckets with a shared deferred material-table shader instead of per-material shader programs. The OpenGL path reads material constants from `MaterialTable`; unsupported/non-deferred passes fall back to the per-material tier path. |
| `BindlessMaterialTable` | Readback-assisted diagnostic path. Same as `MaterialTable`, but requires `GL_ARB_bindless_texture` and `GL_ARB_gpu_shader_int64`; falls back to `MaterialTable` when unavailable. Resident bindless albedo handles are applied through the two-level texture handle table. |

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

## Pass Contract

`PreRender` and `PostRender` remain CPU-only in the default pipelines. Scene geometry passes request the resolved mesh submission strategy. Capture commands (`VPRC_RenderCubemap`, `VPRC_RenderToCubemapFace`, `VPRC_RenderToTextureArray`) carry the same strategy so capture passes do not silently choose a different submission path.

`GPURenderPassCollection` snapshots the strategy at pass execution:

- `CpuDirect` does not consume GPU Hi-Z visibility snapshots. Hardware CPU queries remain the default occlusion path, and optional CPU masked software occlusion can pre-cull traditional CPU mesh draws before hardware-query submission when explicitly enabled.
- The forward depth-normal prepass must resolve the same effective mesh submission strategy as the later lit pass. A forced `CpuDirect` strategy keeps the prepass on CPU even if the legacy GPU-dispatch preference is true; otherwise AO/depth can be populated by GPU draws while the color pass renders CPU. When the lit path is meshlet, the prepass uses the matching traditional GPU indirect strategy because the direct meshlet material-table shader does not support override/depth-normal material variants yet.
- GPU Hi-Z culling prefers the current-frame depth view. Temporal history depth is only a fallback when the current depth view is unavailable, because history-depth false occlusion rejects whole command records before meshlet expansion can refine visibility. Current-frame depth is explicitly disabled as a Hi-Z occlusion source for `OpaqueForward` and `MaskedForward` while the forward depth-normal prepass is enabled: that prepass can already contain the same candidates, so using it would self-occlude whole commands before per-meshlet culling runs.
- `GpuIndirectZeroReadback` enables state-class/tier scatter, consumes GPU-written draw counts directly, and does not call CPU readback helpers such as `ReadGpuBatchRanges()` or `ReadUIntAt(...)` for counts. Use `FullBucketScan` when validating the strict no-readback material path; the active-bucket and material-table variants intentionally read back the compact active bucket list for diagnostics.
- `GpuIndirectInstrumented` is the only strategy allowed to read back batch ranges, count buffers, per-view draw counts, or indirect command dumps. It still uses the material-tier scatter draw path by default so diagnostics render with the same per-material shaders and textures as the production GPU path. CPU masked software occlusion can also run here by preparing CPU occluders, reading the GPU-cull count, and compacting the culled indirect command buffer for validation.
- `GpuMeshletZeroReadback` uses the same GPU-resident scene database and material-table policy as the zero-readback indirect path, then dispatches mesh tasks from GPU-written task records and counts. It must not read count/visibility buffers back in steady state. The meshlet pass snapshots `MaterialTable` automatically if the global zero-readback draw path is `FullBucketScan` or `ActiveBucketList`.
- `GpuMeshletInstrumented` uses the meshlet path with diagnostic readbacks and timing stats enabled only under explicit diagnostics. Current readbacks include visible meshlet count, dispatched meshlet count, expansion overflow, dispatch duration, and readback-byte accounting.
- Any direct `RenderGPU(pass, strategy)` caller that supplies a meshlet strategy sets meshlet pipeline intent on that pass for the duration of the dispatch, so side passes cannot accidentally request `GpuMeshlet*` and then arrive at the render manager as traditional indirect intent.
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
- `OpenGLNV` (`GL_NV_mesh_shader`): NVIDIA-only, no indirect-count entrypoint exists in the spec; diagnostic / bring-up only.
- `None`: resolver downgrades any forced meshlet strategy.

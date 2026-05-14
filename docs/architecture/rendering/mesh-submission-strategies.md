# Mesh Submission Strategies

[<- Rendering Architecture index](README.md)

Mesh drawing is selected by an explicit `EMeshSubmissionStrategy` instead of by interpreting `GPURenderDispatch` directly. The strategy is resolved once from profile, settings, and renderer capability, then applied to mesh render commands in `DefaultRenderPipeline`, `DefaultRenderPipeline2`, capture helpers, and the debug opaque pipeline.

## Strategies

| Strategy | Purpose | CPU readbacks | CPU mesh fallback | Hot-path diagnostics |
|----------|---------|---------------|-------------------|----------------------|
| `CpuDirect` | CPU traversal and direct mesh draw submission. | None in the steady-state render path. | Not applicable. | CPU renderer diagnostics only. |
| `GpuIndirectInstrumented` | GPU indirect path for bring-up, validation, and inspection. | Allowed and counted. | Allowed only when explicitly requested and strict profiles are not active. | Allowed. |
| `GpuIndirectZeroReadback` | Production GPU indirect path. | Forbidden in the steady-state render path. | Forbidden. | Forbidden; use counters and warnings outside the hot path. |
| `GpuMeshlet` | Meshlet/task-mesh submission. | Forbidden by contract. | No implicit CPU fallback; unsupported renderers fall back to traditional GPU indirect with a warning. | Backend bring-up only. |

`GPURenderDispatch` remains a compatibility shim during migration. Setting it to `true` maps through the resolver; older boolean-only call sites still map `true` to `GpuIndirectInstrumented` to preserve legacy behavior.

## Resolver

`Engine.Rendering.ResolveMeshSubmissionStrategy()` uses:

- `Engine.EffectiveSettings.GPURenderDispatch`
- `Engine.EffectiveSettings.VulkanGpuDrivenProfile`
- `EnableGpuIndirectDebugLogging`, `EnableGpuIndirectValidationLogging`, and `EnableGpuIndirectCpuFallback`
- `EnableZeroReadbackMaterialScatter`
- active renderer probes: `SupportsIndirectCountDraw()` and `SupportsMeshletDispatch()`
- `ForceMeshSubmissionStrategy` or `XRE_FORCE_MESH_SUBMISSION_STRATEGY`

Diagnostics profiles resolve to `GpuIndirectInstrumented`. `ShippingFast` resolves to `GpuIndirectZeroReadback` when indirect-count draw is supported. If zero-readback is requested but indirect-count draw is missing, strict profiles downgrade to `CpuDirect` with a visible warning; permissive profiles downgrade to `GpuIndirectInstrumented`.

## Zero-Readback Material Draw Paths

`GpuIndirectZeroReadback` has a second selector, `EZeroReadbackMaterialDrawPath`, available through `Engine.Rendering.Settings.ZeroReadbackMaterialDrawPath`, user/project overrides, editor debug preferences, and `XRE_ZERO_READBACK_MATERIAL_DRAW_PATH`.

| Draw path | Purpose |
|-----------|---------|
| `FullBucketScan` | Strict no-readback path. The GPU scatters commands into state-class/tier buckets and the CPU loops over every bucket while using GPU-written counts. |
| `ActiveBucketList` | Readback-assisted diagnostic path. Adds a compute compaction pass that writes only non-empty bucket IDs, maps that compact ID list on the CPU, then submits those buckets. Useful for measuring empty-bucket overhead. |
| `MaterialTable` | Readback-assisted diagnostic path. Uses active buckets with a shared material-table shader instead of per-material shader programs. Current OpenGL implementation renders material-table debug colors. |
| `BindlessMaterialTable` | Readback-assisted diagnostic path. Same as `MaterialTable`, but requires `GL_ARB_bindless_texture` and `GL_ARB_gpu_shader_int64`; falls back to `MaterialTable` when unavailable. Texture-correct bindless still requires backend texture handle population. |

## State Class IDs

Phase C separates material identity from pipeline state. `DrawMetadata.MaterialID` still identifies the material data, but `DrawMetadata.StateClassID` is the batching key consumed by sort/scatter shaders.

Default derivation:

- Transparent-like materials or transparent/on-top/OIT passes resolve to `Transparent`.
- Masked, alpha-tested, or alpha-to-coverage materials and masked passes resolve to `AlphaTested`.
- Opaque deferred passes resolve to `OpaqueDeferred`.
- Remaining opaque forward/shadow-compatible draws resolve to `OpaqueForward` unless a renderer-specific exception allocates a custom state class.

`GpuIndirectZeroReadback` currently binds one representative CPU material per state class while the GPU indirect command carries the stable `DrawID`; Phase D will use that `DrawID` to fetch per-draw material records from the bindless/descriptor-indexed material table.

## Pass Contract

`PreRender` and `PostRender` remain CPU-only in the default pipelines. Scene geometry passes request the resolved mesh submission strategy. Capture commands (`VPRC_RenderCubemap`, `VPRC_RenderToCubemapFace`, `VPRC_RenderToTextureArray`) carry the same strategy so capture passes do not silently choose a different submission path.

`GPURenderPassCollection` snapshots the strategy at pass execution:

- `CpuDirect` does not consume GPU Hi-Z visibility snapshots. Hardware CPU queries remain the default occlusion path, and optional CPU masked software occlusion can pre-cull traditional CPU mesh draws before hardware-query submission when explicitly enabled.
- `GpuIndirectZeroReadback` enables state-class/tier scatter, consumes GPU-written draw counts directly, and does not call CPU readback helpers such as `ReadGpuBatchRanges()` or `ReadUIntAt(...)` for counts. Use `FullBucketScan` when validating the strict no-readback material path; the active-bucket and material-table variants intentionally read back the compact active bucket list for diagnostics.
- `GpuIndirectInstrumented` is the only strategy allowed to read back batch ranges, count buffers, per-view draw counts, or indirect command dumps.
- CPU safety-net mesh fallback is only available for `GpuIndirectInstrumented` and only when fallback diagnostics are explicitly enabled.

## Kill Switch

Use `Engine.Rendering.Settings.ForceMeshSubmissionStrategy` for local triage. The environment variable `XRE_FORCE_MESH_SUBMISSION_STRATEGY` accepts any `EMeshSubmissionStrategy` name and takes precedence over the setting for the current process.

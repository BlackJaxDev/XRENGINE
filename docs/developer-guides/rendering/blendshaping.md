# Blendshaping

XREngine blendshaping supports sparse, quantized, and active-list-driven mesh
deformation for direct vertex rendering and compute skinning. The runtime goal
is to reduce memory, upload bandwidth, and shader work while preserving authored
facial identity, visemes, expression controls, and corrective shapes.

Open implementation work is tracked in
[Blendshape Compression And GPU Efficiency TODO](../../work/todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md).
Longer-horizon ideas live in
[Blendshape Deferred GPU Efficiency Design](../../work/design/rendering/gpu/blendshape-deferred-gpu-efficiency-design.md).

## Runtime Contract

- `XRMesh` builds `BlendshapeCount`, `BlendshapeIndices`, and
  `BlendshapeDeltas` buffers from per-vertex blendshape data.
- `XRMesh` also builds sparse per-shape records, affected-vertex metadata, and
  quantized delta payloads while retaining the dense FP32 fallback.
- `XRMeshRenderer` owns float `BlendshapeWeights`, compact active index/weight
  pairs, dirty weight ranges, active-count state, LOD state, and pushes only
  dirty ranges to the GPU.
- `GlobalSkinPaletteBuffers` can pack global blendshape weights for compute
  skinning and reuse renderer slices when weight versions are unchanged.
- Direct vertex blendshaping and compute skinning both evaluate compact active
  shape lists through sparse affected-vertex records and quantized per-shape
  delta payloads.
- Both paths share active-count and threshold uniforms and skip blendshape work
  when no shapes are active.

## Payloads And Shader Variants

- Cooked blendshape payloads use version `2`; stale cooked assets with the
  previous blendshape layout are rejected and should be regenerated.
- Sparse records augment the dense FP32 fallback. Participating sparse records
  point at a quantized per-shape delta index space.
- Dense `BlendshapeIndices` and `BlendshapeDeltas` remain available as a
  high-precision fallback and serializer validation path.
- `BlendshapeShaderVariant` includes bits for active-list, sparse-delta,
  quantized-delta, precombined-delta, and future basis-compression contracts.
- OpenGL shader binary cache schema version `6` covers the active, sparse,
  quantized, and precombined blendshape shader contracts.
- The direct vertex path and compute path both carry explicit shader contract
  coverage for active-list indexing and sparse affected-vertex traversal.

## Zero-Active And Dirty Uploads

- All-zero blendshape weights are treated as a no-dispatch condition when no
  skinning path needs the same compute pass for another reason.
- The runtime uses renderer-owned active-count and active-list gating instead
  of mutating `BlendshapeCount.y`, because the count buffer is mesh-owned and
  may be shared by multiple renderers.
- `XRMeshRenderer` tracks blendshape invalidation through `SetField(...)`.
- Dirty blendshape weight ranges are tracked and only dirty ranges are uploaded
  where the backend supports partial uploads.
- Global packed blendshape weights are not repacked when a renderer's weight
  version is unchanged.
- A previously active shape returning to zero is preserved correctly on the next
  active-list rebuild.
- Dirty tracking and active-list buffers are allocated once at renderer
  initialization and reused by the per-frame path.

## Active Shape Compaction

- Renderers build a compact active-shape list from non-zero weights.
- The small-weight threshold is profile controlled. The default is `0.0`, so
  default behavior does not prune small authored values.
- Active shape IDs and active weights are uploaded as a dense GPU list.
- Compute and direct blendshape evaluation iterate active shapes instead of all
  authored shapes when the compact list is available.
- The full-weight buffer path remains available as a migration fallback.
- Profiler counters distinguish authored shape count from active shape count.

## Sparse Delta Records

- Sparse records are shape-owned and augment the per-vertex dense layout.
- Each sparse shape stores affected vertex IDs.
- Each sparse shape stores position, normal, and tangent delta presence flags.
- Dense fallback is retained for shapes where sparse storage is larger or
  slower.
- Cooked payload metadata records the sparse blendshape layout.
- CPU decode tests compare sparse records to the logical blendshape result.
- Sparse shapes pay only for affected vertices and avoid forcing every vertex
  to scan every authored shape.

## Quantized Delta Payloads

- Per-shape bounds, scale, and bias metadata support delta quantization.
- Position deltas support `snorm16x3` or equivalent packed storage.
- FP32 deltas remain available for validation and high-precision fallback.
- Deterministic encode/decode tests cover tiny, large, and negative deltas.

## Precombined Morph Deltas

- `Engine.Rendering.Settings.EnableBlendshapePrecombinePass` enables a compute
  pass that combines all active shapes into renderer-owned temporary
  position/normal/tangent delta buffers.
- The final compute-skinning path and direct vertex path can consume
  precombined deltas instead of refetching every active shape per vertex.
- The precombine path is selected only when the active-shape and affected-vertex
  heuristic expects the extra dispatch to pay off.
- Precombined output is reused while weights are unchanged.
- The heuristic is runtime tunable through rendering settings:
  `BlendshapePrecombineComputeMinActiveShapes`,
  `BlendshapePrecombineDirectMinActiveShapes`, and
  `BlendshapePrecombineMinAffectedVertices`.
- These settings are exposed through Global Editor Preferences instead of
  hardcoded constants.
- Precombined buffer storage is allocated once at renderer initialization.
- Contract coverage validates compute/direct bindings and shader uniforms for
  compute skinning, direct vertex skinning, and previous-frame consumers.

## Blendshape LOD

- Runtime blendshape LOD profiles can select tiers by distance, screen
  coverage, avatar role, or explicit quality profile.
- Supported evaluation tiers are full precision/full shape set, protected
  shapes plus high-impact correctives, viseme or silhouette-only, and disabled.
- Protected shape names and remaps from avatar optimization profiles are
  preserved.
- Meshes expose bounds validation helpers for selected blendshape extremes.
- Renderers expose diagnostics for the current blendshape LOD tier, last
  distance, and last screen coverage.
- Distant and crowd renderers can reduce blendshape cost without breaking close
  facial animation or protected controls.

## Basis Compression Gate

- `Engine.Rendering.Settings.EnableBlendshapePcaBasisCompression` is an opt-in
  gate for future PCA/SVD basis-compressed blendshape payloads.
- The setting is disabled by default.
- The setting does not change shader or runtime behavior unless the mesh carries
  basis-compression payload metadata.

## Profiler Counters

Profiler packets include:

- blendshape weight upload bytes,
- active-list bytes,
- delta bytes,
- authored shape counts,
- active shape counts,
- affected vertices,
- skipped blendshape dispatches,
- compacted active blendshape count,
- live blendshape shader permutation count.

## Completed Test Coverage

Implemented coverage includes:

- all-zero blendshape weights,
- single dirty weight upload,
- dense dirty weight range upload,
- global packed blendshape weight reuse,
- compute shader zero-active blendshape contract,
- previously active shapes returning to zero on the next frame,
- direct vertex and compute-path coverage for blendshape shader contracts,
- active-list indexing on direct vertex and compute paths,
- CPU sparse-record decode against the logical blendshape result,
- sparse affected-vertex traversal on direct vertex and compute paths,
- deterministic quantized-delta encode/decode for tiny, large, and negative
  deltas,
- blendshape buffer build and cooked payload tests,
- shader generation and compute skinning/blendshape contract tests.

## Validation Recorded

The blendshape runtime work has recorded the following targeted validation:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~BlendshapeGpuEfficiencyTests"
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~UberShaderForwardContractTests"
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~ProfilerProtocolTests"
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~RuntimeRenderingHostServicesTests.BlendshapePrecombineSettings_UseRuntimeRenderingHostServices"
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~BlendshapeGpuEfficiencyTests|FullyQualifiedName~RuntimeRenderingHostServicesTests.BlendshapePrecombineSettings_UseRuntimeRenderingHostServices"
dotnet build .\XRENGINE.slnx
```

Known unrelated failures from broader runs were:

- `XRMeshRendererTests.UpdateIndirectDrawBuffer_WritesCommandsPerSubmesh`
  asserted `meshA.BVHTree` was non-null after `GenerateBVH()`, but it was null.
- `RuntimeRenderingHostServicesTests.EffectiveCpuSceneCullingStructure_UsesRuntimeRenderingHostServicesAndEnvOverride`
  expected a mid-process env var change to override cached startup settings,
  while `EffectiveSettingsEnvOverrides` documents startup-only caching.

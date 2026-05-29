# Masked Software Occlusion Culling Remaining Todos

Last Updated: 2026-05-29
Owner: Rendering
Status: Scalar implementation is in place and opt-in. Remaining work is
selector coverage, viewport visualization, smoke/scene validation, SIMD,
stereo, and promotion.
Target Branch: `rendering-masked-software-occlusion`

Design sources:

- [Masked Software Occlusion Culling Design](../../design/rendering/masked-software-occlusion-culling-design.md)
- [Render Submission Performance Debug Plan](../../design/rendering/render-submission-perf-debug-plan.md)

## Goal

Finish validating and hardening the opt-in CPU masked software occlusion culling
pass for traditional CPU mesh rendering and meshlet command visibility.

The pass rasterizes selected opaque occluder triangles into a low-resolution
masked reciprocal-depth buffer, then tests world-space AABBs before draw
submission. Traditional CPU mesh draws use the result before hardware occlusion
queries. Meshlet task dispatch consumes a per-command visibility buffer.

The feature remains disabled by default until correctness, performance, stereo,
and launch-flow validation are complete.

## Implemented Baseline

- Scalar `MaskedOcclusionBuffer` exists with 8 x 4 stored tiles, reciprocal
  depth, mask coverage, resize/clear, debug readback plumbing, and tile-mask
  rectangle queries.
- Scalar `MaskedOcclusionRasterizer` and `MaskedOcclusionAabbTester` exist for
  rigid triangle-list occluders and projected AABB visibility tests.
- Conservative-visible behavior is in place for invalid bounds, NaN/Inf
  transforms, near-plane straddles, unsupported geometry, stereo without per-eye
  buffers, and debug force-visible mode.
- Occluder selection is limited to opaque deferred and opaque forward command
  lists, with safety rejection for transparent, blended, alpha-to-coverage,
  masked, depth-disabled, no-depth-write, non-standard-depth, skinned,
  blendshape, non-triangle, multi-instance, and explicit force-excluded
  candidates.
- `CpuSocOccluderTriangleBudget`, `CpuSocMaxOccluders`, and
  `CpuSocMinOccluderScreenArea` are honored.
- Selected occluder `StableQueryKey`s are tracked so occluders do not
  self-occlude.
- `CpuSoftwareOcclusionCuller` is integrated before
  `CpuRenderOcclusionCoordinator.ShouldRender`.
- SOC culls traditional CPU mesh draws and skips the hardware query only when
  SOC is enabled and reports occluded.
- Shadow, transparent, background, pre-render, and post-render passes remain
  conservative-visible.
- Frame keying includes `RenderFrameId`.
- The legacy `XRE_CPU_SOC_OCCLUSION` override is cached at startup so
  `IsEnabled` does not allocate in the per-command hot path.
- Meshlet rendering has a per-source-command visibility overload, visibility
  SSBO upload/binding, and `MeshletCulling.task` support through
  `UseCpuCommandVisibility` and `commandVisibility[]`.
- Non-meshlet GPU indirect zero-readback is explicitly scoped out of SOC v1.
- `EOcclusionCullingMode.CpuSoftwareOcclusion` is the preferred opt-in mode.
  `EnableCpuSoftwareOcclusionCulling` remains as a legacy side toggle.
- SOC settings are exposed through effective settings and `RuntimeEngineFacade`.
- `OcclusionTelemetry` includes SOC occluder counts, culled/tested counts,
  timings, tiles closed, and force-visible state.
- The ImGui Occlusion panel has a CPU SOC subsection.
- Visualization-disabled frames avoid SOC debug readback allocation.
- Targeted buffer, rasterizer, AABB, render-option selector, meshlet shader,
  invalid/near-clipped/frustum-culled, tile-boundary, tile merge/depth ordering,
  and meshoptimizer interop tests exist.

## Current Validation

Validated on 2026-05-13:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly
dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~MaskedSoftwareOcclusionCullingTests|FullyQualifiedName~MeshOptimizerInteropTests" --no-restore /p:UseSharedCompilation=false
```

## Remaining Work

1. Finish selector and budget coverage.
   - Add command-selector tests for mesh rejection filters.
   - Add command-selector tests for instance rejection filters.
   - Add budget enforcement tests for triangle budget, max occluders, and
     minimum screen area.
   - Add deterministic ordering tests for equal-score occluder candidates.

2. Add real debug visualization.
   - Implement the viewport inset or overlay for `CpuSocDebugVisualization`.
   - Show the masked reciprocal-depth buffer and tile coverage clearly enough
     to inspect leaks and false occlusion.
   - Keep visualization-disabled frames allocation-free.
   - Include the active SOC resolution, selected occluder count, tiles closed,
     and force-visible state in the overlay or adjacent panel detail.

3. Complete build and launch smoke validation.
   - Run the full rendering-adjacent test subset.
   - Launch the editor with SOC disabled and confirm existing CPU-query
     behavior.
   - Launch the editor with SOC enabled and `CpuSocDebugForceVisible=true`;
     confirm behavior matches disabled SOC.
   - Launch the editor with SOC enabled and force-visible off; smoke-test
     traditional mesh rendering.
   - Smoke-test meshlet rendering with SOC enabled.
   - Verify the feature remains disabled by default after settings reload.

4. Complete scene acceptance.
   - Capture the two-Sponza diagnostic with SOC disabled.
   - Capture the same two-Sponza diagnostic with scalar SOC enabled.
   - Confirm `Visible(query)` drops from the recorded 10/6 range toward `<= 3`
     in the target view.
   - Confirm no visual false occlusion in Sponza, editor camera motion, or
     near-plane movement.
   - Confirm scalar SOC total CPU cost stays `<= 1.5 ms` in the target
     diagnostic scene.
   - Add a short validation note with screenshots or log excerpts to this doc
     or a linked report.

5. Add SIMD acceleration.
   - Implement an AVX2 rasterizer path behind `Avx2.IsSupported` and
     `CpuSocUseAvx2`.
   - Implement an AVX2 AABB tester path, or prove scalar AABB testing is
     already below budget.
   - Keep scalar as the correctness oracle.
   - Add scalar-vs-AVX2 parity tests for rasterization and AABB testing.
   - Profile SOC timings and hot-path allocations.
   - Acceptance: AVX2 path reaches `<= 0.6 ms` total SOC cost on the target
     scene, or SOC remains opt-in with documented limits.
   - Avoid adding SSE 4.1 unless profiling shows a real target-machine need.

6. Add stereo and OpenVR support.
   - Add per-eye SOC buffers.
   - Rasterize selected occluders into each eye buffer.
   - OR-combine AABB visibility across eyes.
   - Verify mono, editor stereo, and OpenVR paths do not share stale camera
     state.
   - Add at least one stereo/OpenVR smoke capture before any default-enable
     decision.

7. Harden promotion documentation.
   - Update user-facing settings docs if SOC becomes recommended for any
     workflow.
   - Document when to enable SOC if it remains opt-in.
   - Keep the setting and env kill switches prominent.
   - Record known limitations for unsupported occluder classes, stereo before
     per-eye buffers, and non-meshlet GPU indirect zero-readback.

8. Make the promotion decision.
   - Review false-occlusion risk, false-visible rate, CPU cost, and launch-flow
     smoke results.
   - Decide whether `CpuSoftwareOcclusion` can become the recommended/default
     CPU direct occlusion mode.
   - If promoted, update settings docs, editor labels, troubleshooting notes,
     and release/PR notes.
   - If not promoted, document recommended opt-in scenarios and limits.
   - Re-run runtime, editor, targeted tests, rendering-adjacent tests, and
     chosen smoke captures before merge.

## Acceptance Criteria

- No false occlusion is observed in scalar, AVX2, Sponza, editor, mono, and
  stereo/OpenVR validation.
- Traditional CPU mesh rendering supports SOC pre-culling.
- Meshlet rendering supports SOC visibility and has runtime smoke validation.
- Non-meshlet GPU indirect zero-readback remains explicitly out of SOC v1
  scope unless a future compute-cull SSBO visibility mask is implemented.
- No known render hot-path allocations, locks, GPU readbacks, or GPU buffer
  builds occur in SOC testing for the implemented scalar path.
- SOC remains disabled by default until the promotion decision explicitly
  changes that.
- Hardware CPU-query behavior is preserved when SOC is disabled.
- Selected occluders bypass self-occlusion via `StableQueryKey`.
- Masked, transparent, skinned, and deformed content are not occluders by
  default.
- ImGui Occlusion panel separates CPU-query, CPU SOC, and GPU Hi-Z counters.
- Documentation reflects the current default-disabled state and settings names.

## Final Task

- Merge `rendering-masked-software-occlusion` back into `main` after remaining
  validation, documentation, and promotion decisions are complete.

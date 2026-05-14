# Masked Software Occlusion Culling TODO

Last Updated: 2026-05-13
Owner: Rendering
Status: Scalar implementation is in place and opt-in for traditional CPU mesh
rendering plus meshlet command visibility. Tile-mask queries, hot-path env
caching, frame-keying, and targeted validation are complete. SIMD, stereo,
viewport debug visualization, scene captures, and promotion remain.
Target Branch: `rendering-masked-software-occlusion`

Design source:

- [Masked Software Occlusion Culling Design](../../design/rendering/masked-software-occlusion-culling-design.md)
- [Render Submission Performance Debug Plan](../../design/rendering/render-submission-perf-debug-plan.md)

## Goal

Ship an opt-in CPU masked software occlusion culling pass for traditional
mesh rendering and the meshlet pipeline. The pass rasterizes selected opaque
occluder triangles into a low-resolution masked reciprocal-depth buffer, then
tests world-space AABBs before draw submission. Traditional CPU mesh draws use
that result before hardware occlusion queries; meshlet task dispatch consumes a
per-command visibility buffer.

The feature remains disabled by default until validation proves correctness,
performance, and launch-flow safety.

## Current Validation

Validated on 2026-05-13:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
- `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~MaskedSoftwareOcclusionCullingTests|FullyQualifiedName~MeshOptimizerInteropTests" --no-restore /p:UseSharedCompilation=false`

Known validation still needed: editor launch smoke, meshlet runtime smoke,
two-Sponza captures, full rendering-adjacent test subset, SIMD profiling, and
stereo/OpenVR smoke.

## Non-Goals

- Do not replace GPU Hi-Z for GPU indirect strategies.
- Do not remove the existing hardware CPU-query coordinator.
- Do not make masked, transparent, skinned, blendshape, or otherwise deformed content an occluder by default.
- Do not promote SOC to a default until scalar and SIMD validation pass.

## Phase 0 - Branch And Baseline

- [x] Create dedicated branch `rendering-masked-software-occlusion`.
- [x] Record branch validation baseline. Pre-SOC baseline was not captured before scaffold work began; current baseline is runtime/editor/unit-test builds plus the targeted SOC suite listed below.
- [x] Decide implementation approach: scalar clean-room implementation first; keep Intel Apache-2.0 porting obligations documented for any future port.
- [x] Link the design doc and this TODO from the render submission performance plan.
- [x] Keep existing scaffold API names where possible so the feature remains an extension of `CpuSoftwareOcclusionCuller`.

## Phase 1 - Scalar Runtime Implementation

- [x] Implement `MaskedOcclusionBuffer` with 8 x 4 stored tiles, reciprocal-depth storage, mask coverage, resize/clear, and debug readback plumbing.
- [x] Implement masked-tile rect query in `MaskedOcclusionBuffer.IsRectOccluded`: consume per-tile mask and depth extrema to reject covered tiles in O(tiles) instead of a per-pixel O(W*H) scan.
- [x] Implement scalar `MaskedOcclusionRasterizer` for rigid triangle-list occluders.
- [x] Implement scalar `MaskedOcclusionAabbTester` for projected AABB visibility tests.
- [x] Return conservative-visible for invalid bounds, NaN/Inf transforms, near-plane straddles, and unsupported geometry.
- [x] Enforce buffer resolution constraints: width multiple of 8, height multiple of 4.
- [x] Keep scalar path allocation-free in the per-command `TestVisible` hot path.

## Phase 2 - Occluder Selection And Safety Filters

- [x] Select occluders from opaque deferred and opaque forward command lists only.
- [x] Use render-thread snapshots for mesh, material/render options, model matrix, and instance count.
- [x] Reject transparent, blended, alpha-to-coverage, masked, depth-disabled, no-depth-write, non-standard-depth, skinned, blendshape, non-triangle, and multi-instance candidates by default.
- [x] Honor `RenderingParameters.ExcludeFromCpuOcclusion` for both occluders and occludees.
- [x] Enforce `CpuSocOccluderTriangleBudget`, `CpuSocMaxOccluders`, and `CpuSocMinOccluderScreenArea`.
- [x] Use deterministic ordering for equal-score candidates.
- [x] Track selected occluder `StableQueryKey`s so occluders do not self-occlude.
- [x] Add focused render-option rejection-filter tests for blending, alpha-to-coverage, force-exclude, both-sided culling, depth-disabled, no-depth-write, and non-standard depth functions.
- [ ] Add command-selector tests for mesh/instance rejection filters, budget enforcement, and stable ordering.

## Phase 3 - Traditional Mesh Integration

- [x] Add a `CpuSoftwareOcclusionCuller` singleton beside `s_cpuOcclusionCoordinator`.
- [x] Initialize and populate SOC once per eligible camera/viewport frame before CPU mesh rendering.
- [x] Run SOC before `CpuRenderOcclusionCoordinator.ShouldRender`.
- [x] When SOC reports occluded, skip the draw and skip the hardware query.
- [x] Preserve existing hardware-query behavior when SOC is disabled or `CpuSocDebugForceVisible` is active.
- [x] Keep shadow, transparent, background, pre-render, and post-render passes conservative-visible.
- [x] Record aggregate CPU culls only when SOC actually suppresses a traditional CPU mesh draw.
- [x] Tighten frame-keying to include `RenderFrameId` explicitly, not only camera and viewport state.
- [x] Cache the `XRE_CPU_SOC_OCCLUSION` env override in a static field at startup so `IsEnabled` does not allocate on the per-command hot path.

## Phase 4 - Meshlet Pipeline Integration

- [x] Add a meshlet render overload that accepts per-source-command visibility.
- [x] Feed SOC visibility from `RenderCommandCollection` into the meshlet path.
- [x] Upload per-command visibility to a meshlet SSBO.
- [x] Bind the visibility SSBO in `MeshletCollection`.
- [x] Add `UseCpuCommandVisibility` and `commandVisibility[]` handling in `MeshletCulling.task`.
- [x] Preserve the existing meshlet render overload for callers that do not supply SOC visibility.
- [x] Add a shader-level validation test that the meshlet task shader consumes the CPU command-visibility buffer.
- [x] Scope non-meshlet GPU indirect zero-readback out of SOC v1 in docs. Current SOC support is traditional `CpuDirect` plus meshlet command visibility; non-meshlet zero-readback remains GPU-culling-owned until a future SSBO mask is added to its compute cull path.

## Phase 5 - Settings, Telemetry, And Editor Surface

- [x] Add `EnableCpuSoftwareOcclusionCulling` as the master opt-in toggle.
- [x] Add `CpuSocBufferWidth`, `CpuSocBufferHeight`, `CpuSocOccluderTriangleBudget`, `CpuSocMaxOccluders`, `CpuSocMinOccluderScreenArea`, `CpuSocUseAvx2`, `CpuSocDebugVisualization`, and `CpuSocDebugForceVisible`.
- [x] Expose SOC settings through `Engine.EffectiveSettings`.
- [x] Mirror SOC settings through `RuntimeEngineFacade`.
- [x] Preserve `XRE_CPU_SOC_OCCLUSION=1` as the environment override for the master toggle.
- [x] Extend `OcclusionTelemetry` with SOC occluder counts, culled/tested counts, timings, tiles closed, and force-visible state.
- [x] Add a CPU SOC subsection to the ImGui occlusion panel.
- [ ] Add a real viewport inset/overlay for `CpuSocDebugVisualization`.
- [x] Confirm visualization-disabled frames allocate nothing in SOC readback/debug paths; `ReadDebugBuffer` returns null unless `CpuSocDebugVisualization` is enabled.

## Phase 6 - Tests Already Added

- [x] Add buffer/AABB coverage test for covered rect rejection and closer-object visibility.
- [x] Add hole/leakage test to prove uncovered pixels remain visible.
- [x] Add end-to-end scalar rasterized-occluder test for an AABB behind it.
- [x] Update meshoptimizer interop coverage so the meshlet SOC hook is expected.
- [x] Update `RuntimeRenderingHostServicesTests` test double for current render-host telemetry members.

## Phase 7 - Validation Blockers

- [x] Fix broader unit-test project compile blockers so targeted SOC tests can run.
- [x] Resolve stale ambiguous `Engine` references in tests that now see both `XREngine` and `XREngine.Runtime.Rendering`.
- [x] Resolve stale audio and light-probe tests currently blocking `XREngine.UnitTests` compilation.
- [x] Run targeted SOC tests:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~MaskedSoftwareOcclusionCullingTests|FullyQualifiedName~MeshOptimizerInteropTests"`.
- [x] Build the full unit-test project after compile blockers were resolved.
- [ ] Run the full rendering-adjacent test subset after the project compiles.

## Phase 8 - Build And Smoke Validation

- [x] Build runtime rendering:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore /p:UseSharedCompilation=false`.
- [x] Build editor:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`.
- [ ] Launch editor with SOC disabled and confirm existing CPU-query behavior.
- [ ] Launch editor with SOC enabled and `CpuSocDebugForceVisible=true`; confirm behavior matches disabled SOC.
- [ ] Launch editor with SOC enabled and force-visible off; smoke-test traditional mesh rendering.
- [ ] Smoke-test meshlet rendering with SOC enabled.
- [x] Verify the feature remains disabled by default in settings.

## Phase 9 - Scene Acceptance

- [ ] Capture the two-Sponza diagnostic with SOC disabled.
- [ ] Capture the same two-Sponza diagnostic with scalar SOC enabled.
- [ ] Acceptance: `Visible(query)` drops from the recorded 10/6 range toward `<= 3` in the target view.
- [ ] Acceptance: no visual false occlusion in Sponza, editor camera motion, or near-plane movement.
- [ ] Acceptance: scalar SOC total CPU cost stays `<= 1.5 ms` in the target diagnostic scene.
- [ ] Add a short validation note with screenshots/log excerpts to the TODO or linked report.

## Phase 10 - SIMD And Performance

- [ ] Implement AVX2 rasterizer path behind `Avx2.IsSupported` and `CpuSocUseAvx2`.
- [ ] Implement AVX2 AABB tester path or prove scalar AABB testing is already below budget.
- [ ] Keep scalar as the correctness oracle.
- [ ] Add scalar-vs-AVX2 parity tests for rasterization and AABB testing.
- [ ] Profile SOC timings and hot-path allocations.
- [ ] Acceptance: AVX2 path reaches `<= 0.6 ms` total SOC cost on the target scene, or the feature remains opt-in with documented limits.
- [ ] Avoid adding SSE 4.1 unless profiling shows a real target-machine need.

## Phase 11 - Stereo And Multiview

- [x] Keep stereo conservative-visible until per-eye buffers are implemented.
- [ ] Add per-eye SOC buffers.
- [ ] Rasterize selected occluders into each eye buffer.
- [ ] OR-combine AABB visibility across eyes.
- [ ] Verify mono, editor stereo, and OpenVR paths do not share stale camera state.
- [ ] Add at least one stereo/OpenVR smoke capture before any default-enable decision.

## Phase 12 - Hardening And Docs

- [x] Audit SOC hot paths for the known allocation risks from this phase: env-var lookup, empty-occluder telemetry, LINQ blending checks, and rectangle-query per-pixel scans.
- [x] Skip `OcclusionTelemetry.RecordCpuSocTest` and the surrounding `Stopwatch.GetTimestamp` pair in `CpuSoftwareOcclusionCuller.TestVisible` when `_frameOccludersRasterized == 0`, instead of recording a zero-cost not-culled sample for every tested command.
- [x] Add tests for render-option selector filter coverage.
- [ ] Add tests for command-selector deterministic budget behavior.
- [x] Add tests for tile merge/depth ordering edge cases.
- [x] Add tests for invalid, near-clipped, and frustum-culled AABBs.
- [x] Add tests for tile-boundary AABBs/rects.
- [ ] Update user-facing settings docs if SOC becomes recommended for any workflow.
- [x] Remove or correct stale architecture-doc wording that says `CpuDirect` consumes GPU Hi-Z visibility snapshots.
- [x] Update the design doc to reflect that scalar `IsRectOccluded` now uses tile masks/depth extrema.

## Phase 13 - Promotion Decision

- [ ] Review false-occlusion risk, false-visible rate, CPU cost, and launch-flow smoke results.
- [ ] Decide whether `EnableCpuSoftwareOcclusionCulling` can default to true for `CpuDirect`.
- [ ] If promoted, update settings docs, editor labels, troubleshooting notes, and release/PR notes.
- [ ] If not promoted, document when to enable it and keep the setting/env kill switches prominent.
- [ ] Re-run runtime, editor, targeted tests, and chosen smoke captures before merge.

## Final Acceptance Criteria

- [ ] No false occlusion observed in scalar, AVX2, Sponza, editor, and stereo smoke validation.
- [x] Traditional CPU mesh rendering supports SOC pre-culling.
- [x] Traditional CPU mesh rendering has initial scalar SOC support.
- [ ] Meshlet rendering supports SOC visibility in validated runtime smoke.
- [x] Meshlet rendering has initial SOC command-visibility integration.
- [x] Non-meshlet GPU indirect zero-readback rendering supports SOC visibility, or the SOC scope is explicitly limited to `CpuDirect` + meshlet paths in user-facing docs.
- [x] No known render hot-path allocations, locks, GPU readbacks, or GPU buffer builds in SOC testing from the implemented scalar path (covers the `XRE_CPU_SOC_OCCLUSION` env-var cache and the empty-occluder telemetry skip). Full profiling remains a Phase 10 promotion prerequisite.
- [ ] SOC remains disabled by default until Phase 13 explicitly promotes it.
- [x] Hardware CPU-query behavior is preserved when SOC is disabled.
- [x] Selected occluders bypass self-occlusion via `StableQueryKey`.
- [x] Masked/transparent/skinned/deformed content is not an occluder by default.
- [x] ImGui Occlusion panel separates CPU-query, CPU SOC, and GPU Hi-Z counters.
- [x] Documentation reflects the current default-disabled state and settings names.

## Final Task

- [ ] Merge `rendering-masked-software-occlusion` back into `main` after implementation, validation, and documentation updates are complete.

# Masked Software Occlusion Culling TODO

Last Updated: 2026-05-12
Status: Draft implementation plan
Owner: Rendering
Target Branch: `rendering-masked-software-occlusion`

Design source:

- [Masked Software Occlusion Culling Design](../../design/rendering/masked-software-occlusion-culling-design.md)
- [Render Submission Performance Debug Plan](../../design/rendering/render-submission-perf-debug-plan.md)

## Goal

Implement an opt-in CPU masked software occlusion culling pass for the
`CpuDirect` mesh submission path. The pass should rasterize a bounded set of
opaque occluder triangles into a low-resolution masked Hi-Z buffer, then
pre-cull CPU mesh commands by testing their world AABBs before hardware
occlusion queries and draw submission.

Architecture verdict from the research pass: valid, provided we keep the
rollout conservative. The stored tile is 8 x 4 pixels, the pass remains
disabled by default until validation, selected occluders are exempted by
`StableQueryKey`, and the render hot path must not allocate, lock, or read GPU
buffers.

## Non-Goals

- Do not replace GPU Hi-Z for GPU indirect strategies.
- Do not remove the existing hardware CPU-query coordinator.
- Do not make masked, transparent, skinned, or deformed content an occluder by default.
- Do not promote SOC to a default until scalar and SIMD paths pass correctness and Sponza validation.

## Phase 0 - Branch, License, And Baseline

- [ ] Create dedicated branch `rendering-masked-software-occlusion`.
- [ ] Record current build/test baseline before SOC edits.
- [ ] Decide whether implementation is a clean-room paper implementation or an Apache-2.0 source port.
- [ ] If porting Intel reference code, preserve Apache-2.0 headers/notices and add attribution to repository license docs.
- [ ] Add this TODO and the design doc to the PR scope before code work starts.

## Phase 1 - Contract Prep In Existing Code

- [ ] Extend `CpuSoftwareOcclusionCuller` without removing the existing scaffold API.
- [ ] Add `TestVisible(uint stableQueryKey, in AABB worldBounds)` and keep the existing AABB-only overload as a conservative compatibility helper.
- [ ] Add frame-key helpers keyed by `RenderFrameId`, camera, and viewport size.
- [ ] Add `CpuSoc*` settings and `RuntimeEngineFacade` mirrors for buffer size, occluder budget, max occluders, AVX2 toggle, visualization, and force-visible kill switch.
- [ ] Expose an allocation-free render-pass mesh-command enumerator, or keep occluder submission inside `RenderCommandCollection`.
- [ ] Add render-thread snapshot access for mesh, material, and model matrix if current `IRenderCommandMesh` properties are app-thread mutation state.
- [ ] Define telemetry ownership: the culler records SOC tested/culled; `RenderCommandCollection` records aggregate CPU culls only.

## Phase 2 - CPU Mesh Proxy Data

- [ ] Add or expose compact CPU SOC mesh data: positions plus triangle indices for triangle-list meshes.
- [ ] Ensure the SOC path never calls `GetIndexBuffer`, maps GPU buffers, or builds transient index arrays during `RenderCPU`.
- [ ] Reject meshes without CPU SOC data conservatively.
- [ ] Reject skinned, morph/blendshape, deformed, non-triangle, transparent, masked, depth-disabled, and no-depth-write candidates by default.
- [ ] Add tests for candidate rejection and triangle-count budgeting.

## Phase 3 - Scalar Masked Buffer

- [ ] Implement `MaskedOcclusionBuffer` with 8 x 4 stored tiles: two reciprocal-depth layers and a 32-bit mask.
- [ ] Enforce resolution constraints: width multiple of 8, height multiple of 4.
- [ ] Implement clear, resize, tile merge, and optional debug pixel-depth readback.
- [ ] Add unit tests for tile state transitions, merge behavior, and depth ordering.

## Phase 4 - Scalar Rasterizer And AABB Tester

- [ ] Implement scalar occluder triangle transform, clipping, winding cull, coverage, and conservative depth update.
- [ ] Implement scalar `TestVisible` as a `TestRect`-style projected AABB test.
- [ ] Return visible for near-plane-straddling, NaN/Inf, invalid bounds, and unsupported geometry cases.
- [ ] Add tests for visible, occluded, frustum-culled, near-plane, degenerate, and tile-boundary cases.
- [ ] Add scalar golden tests before adding SIMD.

## Phase 5 - Occluder Selection

- [ ] Implement `OccluderSelector` with screen-area scoring, triangle budget, max occluders, and front-to-back order.
- [ ] Honor `RenderingParameters.ExcludeFromCpuOcclusion` for occluders and occludees.
- [ ] Store selected occluder `StableQueryKey`s in a reusable instance-field set for self-occlusion bypass.
- [ ] Verify selection is deterministic for equal scores.
- [ ] Add tests for budget enforcement, filtering, and stable ordering.

## Phase 6 - RenderCPU Integration

- [ ] Add a `CpuSoftwareOcclusionCuller` singleton beside `s_cpuOcclusionCoordinator`.
- [ ] Begin and populate SOC once per eligible frame/camera/viewport before the first occlusion-testable CPU pass.
- [ ] Run SOC before `CpuRenderOcclusionCoordinator.ShouldRender`.
- [ ] If SOC returns occluded, skip draw and hardware query, record `CpuSocCulled` and aggregate CPU cull telemetry.
- [ ] Keep hardware-query behavior unchanged when SOC is disabled or force-visible is active.
- [ ] Keep shadow, transparent, background, pre-render, and post-render passes conservative-visible.

## Phase 7 - Editor Telemetry And Debugging

- [ ] Extend `OcclusionTelemetry` with selected/rasterized occluders, tiles traversed/updated/merged, SOC timings, and force-visible status.
- [ ] Add a CPU SOC subsection to the ImGui Occlusion panel.
- [ ] Add debug readback/inset only when `CpuSocDebugVisualization` is enabled.
- [ ] Confirm zero allocations when visualization is disabled.

## Phase 8 - Scalar Validation

- [ ] Run targeted occlusion unit tests.
- [ ] Run a narrow `dotnet build` for touched projects.
- [ ] Capture the two-Sponza diagnostic with SOC disabled and enabled.
- [ ] Acceptance: with SOC enabled, `Visible(query)` drops from the recorded 10/6 range toward <= 3 for the target view without visual regression.
- [ ] Acceptance: force-visible or disabled SOC matches current CPU-query behavior.
- [ ] Acceptance: scalar SOC total cost stays <= 1.5 ms on the target diagnostic scene.

## Phase 9 - SIMD And Performance

- [ ] Add AVX2 implementation behind runtime `Avx2.IsSupported` and `CpuSocUseAvx2`.
- [ ] Keep scalar as the oracle and parity-test every SIMD result against scalar.
- [ ] Avoid carrying SSE 4.1 unless profiling shows a real target-machine need.
- [ ] Profile SOC timings and hot-path allocations.
- [ ] Acceptance: AVX2 path reaches <= 0.6 ms total SOC cost on the target scene, or the feature remains opt-in with documented limits.

## Phase 10 - Stereo And Multiview

- [ ] Keep stereo conservative-visible until per-eye buffers are implemented.
- [ ] Add per-eye buffers and OR-combine visibility.
- [ ] Verify mono, stereo editor, and OpenVR paths do not share stale camera state.
- [ ] Add validation capture for at least one stereo/OpenVR smoke run before considering default enablement.

## Phase 11 - Promotion Decision

- [ ] Review correctness failures, leakage, false-visible rate, and CPU cost from validation captures.
- [ ] Decide whether `EnableCpuSoftwareOcclusionCulling` can default to true for `CpuDirect`.
- [ ] If promoted, update settings docs, editor labels, and troubleshooting notes.
- [ ] If not promoted, document when to enable it and keep the env/settings kill switches.
- [ ] Remove or correct stale architecture-doc wording that says `CpuDirect` consumes GPU Hi-Z visibility snapshots.

## Acceptance Criteria

- [ ] No false occlusion observed in scalar, AVX2, Sponza, and editor smoke validation.
- [ ] No render hot-path allocations, locks, GPU readbacks, or GPU buffer builds in SOC testing.
- [ ] SOC remains disabled by default until Phase 11 explicitly promotes it.
- [ ] Hardware CPU-query path remains unchanged when SOC is disabled.
- [ ] Selected occluders never self-occlude because `StableQueryKey` bypass is active.
- [ ] Masked/transparent/skinned/deformed content is not an occluder by default.
- [ ] ImGui Occlusion panel clearly separates CPU-query, CPU SOC, and GPU Hi-Z counters.
- [ ] Documentation reflects the final default and settings names.

## Final Task

- [ ] Merge `rendering-masked-software-occlusion` back into `main` after implementation, validation, and documentation updates are complete.

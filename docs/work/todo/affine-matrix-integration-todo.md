# Affine Matrix Integration TODO

Reference design: [Affine Matrix Integration Plan](../design/affine-matrix-integration-plan.md)
Reference audit: [Affine Matrix Audit](../audit/affine-matrix4x3-audit-2026-03-18.md)
Phase 0 baseline: [Affine Matrix Phase 0 Baseline](../audit/affine-matrix-phase0-baseline-2026-03-18.md)

Created: 2026-03-18

## Goal

Integrate `AffineMatrix4x3` into the highest-value affine CPU hot paths first, while keeping `Matrix4x4` as the public and GPU-facing boundary type.

## Guardrails

- Keep public transform, rendering, editor, and GPU upload boundaries on `Matrix4x4`.
- Do not use affine matrices in projection or other non-affine paths.
- Do not introduce cached affine storage in this rollout; use stack-local intermediates only.
- Do not expand scope into serialization, networking, or file format changes.
- Avoid new heap allocations in transform, render-prep, culling, update, or swap hot paths.
- Fall back to the current `Matrix4x4` path whenever affinity is not guaranteed.

## Phase 0 - Baseline And Scaffolding

- [x] Record baseline microbenchmark numbers from [XREngine.Benchmarks/AffineMatrixBenchmarks.cs](../../../XREngine.Benchmarks/AffineMatrixBenchmarks.cs).
- [x] Add or outline scenario benchmarks for dirty transform-chain updates, wide render-hierarchy propagation, and CPU bounds updates.
- [x] Audit the current `TransformBase` hierarchy and explicitly list the `CreateWorldMatrix()` overrides that stay on the `Matrix4x4` path during Phase 1.
- [x] Identify the exact internal helper surface needed for Phases 1-3 so the first implementation does not create throwaway APIs.
- [x] Confirm the validation path up front: targeted unit tests, benchmark reruns, editor build, and smoke validation of world/render matrix propagation.

## Phase 1 - Transform Hierarchy Composition Fast Path

Current status:

- Phase 1 and Phase 2 are now implemented together: `Transform` produces local affine matrices directly, and the base transform hierarchy uses affine world/render composition when affinity is guaranteed.
- Equivalence tests, fallback tests, editor build validation, and runtime benchmark coverage are in place.
- The original standalone Phase 1 attempt regressed because repeated `Matrix4x4` to `AffineMatrix4x3` conversion dominated the saved multiply.
- With direct local affine generation in place, medium-run `TransformHierarchyBenchmarks` now show a stable render-propagation win, dirty-hierarchy recomposition that is effectively neutral at small hierarchy counts, and a remaining regression on the wider dirty-chain case that still needs explanation before any broader rollout.
- A first Phase 3 pass is also in place for CPU bounds work: `GPUScene` world-bounding-sphere generation and `RenderableMesh` world/local bounds transforms now use affine helpers where the basis matrix is affine, with targeted bounds-equivalence tests covering those paths.

Primary files:

- [XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs](../../../XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs)
- [XREngine.Data/Transforms/AffineMatrix4x3.cs](../../../XREngine.Data/Transforms/AffineMatrix4x3.cs)

Implementation:

- [x] Add `protected virtual bool IsGuaranteedAffine => false;` to `TransformBase`.
- [x] Override `IsGuaranteedAffine` in `Transform` to return `true`.
- [x] Add narrow internal affine helpers for affine composition and conversion, without changing public API return types.
- [x] Update the default `TransformBase.CreateWorldMatrix()` path to use affine multiplication when both local and parent paths are guaranteed affine.
- [x] Update render hierarchy propagation to use the same affine multiply logic during frame-swap render-matrix propagation.
- [x] Keep `_worldMatrix` and `_renderMatrix` stored as `Matrix4x4` and convert only at the storage/event boundary.
- [x] Preserve the existing dirty-flag and locking model; do not add cached affine fields or extra synchronization.
- [x] Ensure children of non-affine parents automatically fall back to the current `Matrix4x4` composition path.

Validation:

- [x] Add hierarchy-composition tests that compare affine and existing `Matrix4x4` results.
- [x] Add render-matrix propagation tests that exercise the frame-swap path separately from world-matrix recomputation.
- [x] Add a mixed-hierarchy fallback test with an affine child under a deliberately non-affine parent.
- [x] Rerun affine benchmarks and compare dirty-hierarchy update costs before and after.
- [x] Build the editor/runtime path after this phase is complete.

Exit criteria:

- [x] Affine composition is used only when `IsGuaranteedAffine` allows it.
- [x] No public API signatures changed.
- [x] Tests prove correct fallback behavior at non-affine boundaries.

## Phase 2 - `Transform` Local Matrix Generation

Primary files:

- [XREngine.Runtime.Core/Scene/Transforms/Transform.cs](../../../XREngine.Runtime.Core/Scene/Transforms/Transform.cs)
- [XREngine.Data/Transforms/AffineMatrix4x3.cs](../../../XREngine.Data/Transforms/AffineMatrix4x3.cs)

Implementation:

- [x] Keep this phase scoped to `Transform` only; do not widen it to every `CreateLocalMatrix()` implementor.
- [x] Implement affine local-matrix construction for all 6 supported transform orders: TRS, STR, RST, RTS, TSR, SRT.
- [x] Use `AffineMatrix4x3.CreateTRS` where it applies.
- [x] For the other 5 orderings, build affine scale/rotation/translation matrices and compose them with affine multiplication.
- [x] Convert to `Matrix4x4` only at the `CreateLocalMatrix()` boundary.
- [x] Avoid adding decomposition steps or extra temporary `Matrix4x4` values.

Validation:

- [x] Add randomized TRS equivalence tests comparing the new affine construction to the current `Matrix4x4` results.
- [x] Add explicit tests for all 6 transform orders.
- [x] Verify no behavior change for ordinary local transform recomputation.
- [x] Rerun benchmarks focused on local transform recomposition.

Exit criteria:

- [x] All 6 transform orders are covered by tests.
- [x] `Transform` local-matrix generation remains functionally identical to the current implementation.

## Phase 3 - CPU Render Bounds And Point Transforms

Primary files:

- [XRENGINE/Scene/Components/Mesh/RenderableMesh.cs](../../../XRENGINE/Scene/Components/Mesh/RenderableMesh.cs)
- [XRENGINE/Rendering/Commands/GPUScene.cs](../../../XRENGINE/Rendering/Commands/GPUScene.cs)

Implementation:

- [x] Add internal helpers that use affine point transforms for bounds centers, sphere centers, and corner transforms.
- [x] Replace eligible CPU-side `Matrix4x4` point-transform usage with `AffineMatrix4x3.TransformPosition` where the matrix is known affine.
- [x] Add affine-specific helpers for basis-scale extraction if those code paths currently pay for full `Matrix4x4` work.
- [x] Keep render-command storage and GPU payloads on `Matrix4x4`.
- [x] Review touched render-prep code for hot-path allocations and remove any newly introduced ones.

Validation:

- [x] Add targeted tests for world-bounds and bounding-sphere equivalence.
- [ ] Add scenario benchmarks for many-renderable bounds updates and CPU render-prep workloads.
- [ ] Perform a rendering smoke test to confirm culling and bounds-driven behavior remain correct.

Exit criteria:

- [ ] CPU render-prep uses affine helpers only in affine-only paths.
- [ ] No GPU buffer layout or shader-facing matrix format changes were introduced.

## Phase 4 - Reevaluate Storage Boundaries

- [ ] Review benchmark results from Phases 1-3 before changing any storage model.
- [ ] Decide whether any internal caches or arrays benefit enough from affine storage to justify added churn.
- [ ] Keep public APIs and GPU-facing layouts unchanged unless measurements clearly justify a broader migration.
- [ ] If a wider storage change is proposed, capture it in a separate design doc before implementation.

## Cross-Cutting Validation Checklist

- [ ] Extend [XREngine.UnitTests/Data/AffineMatrix4x3Tests.cs](../../../XREngine.UnitTests/Data/AffineMatrix4x3Tests.cs) with any missing affine correctness coverage used by Phases 1-3.
- [ ] Keep [XREngine.Benchmarks/AffineMatrixBenchmarks.cs](../../../XREngine.Benchmarks/AffineMatrixBenchmarks.cs) as the shared benchmark baseline for the rollout.
- [ ] Run the most targeted unit tests for transform and affine behavior after each phase.
- [ ] Build the affected projects after each phase; at minimum the editor/runtime path should stay green.
- [ ] Run a full solution build before declaring the rollout complete.
- [ ] Document measured wins and any neutral/negative results in the follow-up changelist or work notes.

## Explicit Non-Goals While Executing This TODO

- [ ] Do not replace `Matrix4x4` across public scene or rendering APIs.
- [ ] Do not touch projection or other non-affine matrix paths.
- [ ] Do not change shader `mat4` layout, upload format, or render-command buffer shape.
- [ ] Do not expand into serialization, replication, or file format migration work.

## Done Means

- [ ] Phases 1-3 are complete, validated, and benchmarked.
- [ ] The measured results justify keeping the affine fast paths.
- [ ] Public and GPU-facing boundaries are still `Matrix4x4`.
- [ ] Any further widening of affine storage has been deferred to a separate measured design decision.
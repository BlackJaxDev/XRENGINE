# Affine Matrix Integration Plan

Scope: define where `AffineMatrix4x3` should be integrated first, what should remain `Matrix4x4`, and how to roll the change out without broad API churn.

## Goal

Use `AffineMatrix4x3` to speed up affine-only CPU hot paths while keeping `Matrix4x4` as the public and GPU-facing boundary type.

This plan does not propose an engine-wide matrix type replacement. The immediate objective is to reduce CPU work in the transform and render-prep stack where the data is already known to be affine.

## Non-Goals

- Replacing `Matrix4x4` in public scene, rendering, or editor APIs.
- Changing shader-facing `mat4` layouts or GPU upload formats.
- Using affine matrices in projection or other non-affine paths.
- Broad serialization, networking, or file format changes.

## Current Constraints

1. Public transform boundaries are `Matrix4x4`-shaped.
   - World, local, inverse, and render matrix properties remain `Matrix4x4` in [XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs](../../../XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs).

2. Render-facing payloads still expect full `mat4` data.
   - Render commands and GPU upload flows use `Matrix4x4` across CPU and shader boundaries.

3. Not every path is affine.
   - Projection code and any future projective transforms still require general `Matrix4x4` behavior.

4. The transform stack already contains direct-state fast paths.
   - Affine integration should complement those optimizations rather than replace them with a wider storage migration.

## Integration Principles

1. Keep `AffineMatrix4x3` in the shared data layer.
   - The type belongs in [XREngine.Data/Transforms/AffineMatrix4x3.cs](../../../XREngine.Data/Transforms/AffineMatrix4x3.cs).

2. Convert late.
   - Stay affine for internal composition and point transforms as long as possible.
   - Convert to `Matrix4x4` only when crossing an existing public or GPU-facing boundary.

3. Do not widen the public API unless measurement proves it is worth the churn.

4. Avoid heap allocations and avoid introducing extra decompositions.

5. Prefer narrow, hot-path-only adoption before any storage-model redesign.

6. Affine intermediates are transient, not cached.
   - `AffineMatrix4x3` values during composition are stack-local temporaries.
   - The final result is still stored as `_worldMatrix` / `_renderMatrix` (`Matrix4x4`).
   - The existing lock + dirty-flag scheme is unchanged; no additional locking or memory overhead.

## Affine Eligibility Marker

Rather than calling `AffineMatrix4x3.IsAffine(matrix)` at runtime every frame, add a declarative marker:

```csharp
// TransformBase
protected virtual bool IsGuaranteedAffine => false;
```

`Transform` overrides this to return `true`. Types such as `DrivenWorldTransform`, `MirroredTransform`, or `MultiCopyTransform` keep the default `false` (or return `true` conditionally if they can prove affinity from their inputs).

This lets hierarchy composition branch cheaply: when both child and parent report `IsGuaranteedAffine`, the fast path is taken. Otherwise the existing `Matrix4x4` path runs with zero overhead.

Children of a non-affine parent automatically fall back to `Matrix4x4` composition because the parent's `IsGuaranteedAffine` is `false`.

## Recommended First Integration Points

### 1. Transform hierarchy composition

Primary files:
- [XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs](../../../XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs)

Current behavior:
- `CreateWorldMatrix()` composes `LocalMatrix * parent.WorldMatrix`.
- Child render propagation composes `child.LocalMatrix * parentRenderMatrix` while walking the hierarchy.

Why this is the best first target:
- It is an always-affine operation in normal scene transform usage.
- It runs repeatedly across parent-child chains.
- It directly exercises the strongest part of the affine type: affine multiply.

Scope:
- This phase targets only the base default `CreateWorldMatrix()` in `TransformBase`.
- The 11 transform types that override `CreateWorldMatrix()` (`LookatTransform`, `BillboardTransform`, `RigidBodyTransform`, `SmoothedParentConstraintTransform`, `DrivenWorldTransform`, `CopyTransform`, `MultiCopyTransform`, `MirroredTransform`, `PositionOnlyTransform`, `WorldTranslationLaggedTransform`, `VREyeTransform`) produce their own world matrix and bypass parent composition — the affine fast path does not apply to them. Children of a non-affine override fall back to `Matrix4x4` composition via the `IsGuaranteedAffine` marker.

Proposed change:
- Add an internal affine fast path for local/world/render composition.
- When both operands report `IsGuaranteedAffine`, compose via `AffineMatrix4x3`.
- Materialize a `Matrix4x4` only when storing `_worldMatrix`, `_renderMatrix`, or invoking existing events.

Suggested shape:
- Keep `CreateWorldMatrix()` returning `Matrix4x4` for now.
- Add an internal helper such as `TryCreateWorldAffineMatrix(out AffineMatrix4x3 affine)` or `ComposeWorldAffine(...)`.
- Reuse the same affine helper for render hierarchy propagation.

Render propagation note:
- Render matrix propagation happens at frame swap, not during the dirty cascade that recomputes world matrices. Both paths share the same affine multiply logic but are invoked at different times with different locking patterns. Phase 1 must address both paths explicitly.

Expected payoff:
- Lower arithmetic and bandwidth cost during transform propagation.
- Reduced cost in scenes with large dirty hierarchies.

Risk level:
- Medium.
- The logic is central, but the data is affine and the external type can remain unchanged.

### 2. Local TRS matrix construction in `Transform`

Primary files:
- [XREngine.Runtime.Core/Scene/Transforms/Transform.cs](../../../XREngine.Runtime.Core/Scene/Transforms/Transform.cs)

Scope:
- This phase targets only the `Transform` class and its 6 TRS orderings (TRS, STR, RST, RTS, TSR, SRT).
- `CreateLocalMatrix()` is abstract with many other implementors (`UITransform`, `OrbitTransform`, `BoomTransform`, noise transforms, etc.). Those keep their existing `Matrix4x4` paths.

Current behavior:
- Local matrix generation composes full `Matrix4x4` values from scale, rotation, and translation in different transform orders.

Why this is the next best target:
- The input is explicitly TRS.
- The local matrix for ordinary transforms is affine by definition.
- This path is hit whenever a regular transform becomes dirty.

Approach for non-TRS orderings:
- `AffineMatrix4x3.CreateTRS` already exists. For the other 5 orderings, build individual affine scale/rotation/translation matrices and compose via `AffineMatrix4x3.Multiply`.
- This reuses existing code with minimal new surface area.
- If benchmarks show the extra multiplies matter, introduce dedicated `Create*` helpers later.

Proposed change:
- Implement affine TRS composition helpers for all 6 supported transform orders.
- Build the local matrix as `AffineMatrix4x3` first.
- Convert once to `Matrix4x4` at the `CreateLocalMatrix()` boundary until downstream storage changes are justified.

Expected payoff:
- Removes several full 4x4 temporaries from transform-local recomposition.
- Pairs naturally with affine hierarchy composition.

Risk level:
- Low to medium.
- The path is self-contained and strongly affine, but transform order correctness must remain exact.

### 3. CPU-side render bounds and point transforms

Primary files:
- [XRENGINE/Scene/Components/Mesh/RenderableMesh.cs](../../../XRENGINE/Scene/Components/Mesh/RenderableMesh.cs)
- [XRENGINE/Rendering/Commands/GPUScene.cs](../../../XRENGINE/Rendering/Commands/GPUScene.cs)

Current behavior:
- World bounds and bounding spheres transform local points with `Matrix4x4`.
- CPU-side render-prep code reads model matrices, transforms centers/corners, and extracts basis scale.

Why this is a good third target:
- The work is affine-only.
- It occurs in render/culling preparation where command counts can be high.
- It benefits from affine point-transform helpers without requiring major architectural changes.

Proposed change:
- Add internal helpers that use `AffineMatrix4x3.TransformPosition` for bounds centers and corner transforms.
- Add affine-specific basis-scale helpers where model scale is needed.
- Keep render command storage as `Matrix4x4` until there is a measured reason to revisit buffer layouts.

Expected payoff:
- Lower CPU overhead in visibility and bounds prep.
- Clear, narrow adoption boundary with minimal API churn.

Risk level:
- Low.

## Deferred Integration Points

These are valid later, but should not be first:

1. Render command storage.
   - Render commands, motion vectors, and GPU command buffers are deeply `Matrix4x4`-shaped today.

2. Transform serialization and replication.
   - This would expand scope into compatibility and data layout concerns.

3. General-purpose scene APIs.
   - The engine still needs `Matrix4x4` for interop with many consumers.

4. Projection and camera matrices.
   - Those are not affine and should stay on `Matrix4x4`.

## Proposed Internal API Surface

Keep the current public API stable. Add only narrow internal helpers as needed.

Candidate helpers:
- `virtual bool IsGuaranteedAffine { get; }` on `TransformBase`
- `AffineMatrix4x3 ComposeLocalAffineTRS(...)`
- `AffineMatrix4x3 ComposeAffineFromComponents(scale, rotation, translation, order)`
- `bool TryGetLocalAffineMatrix(out AffineMatrix4x3 matrix)`
- `bool TryGetWorldAffineMatrix(out AffineMatrix4x3 matrix)`
- `static Matrix4x4 ToMatrix4x4(in AffineMatrix4x3 matrix)`
- `static AffineMatrix4x3 FromMatrix4x4Lossless(...)`

Rules:
- `IsGuaranteedAffine` is the gate. `Try*Affine*` methods check it and fail fast if `false`.
- Callers should fall back to current `Matrix4x4` behavior on failure.
- No public API should expose affine matrices unless there is separate design approval.

## Rollout Phases

### Phase 1: Internal transform composition fast path

- Add `IsGuaranteedAffine` virtual property on `TransformBase` (default `false`); override to `true` in `Transform`.
- Add affine composition helpers in the runtime transform stack.
- Use them for world matrix composition in the base default `CreateWorldMatrix()` and for render hierarchy propagation (separate code path at frame swap).
- The 11 `CreateWorldMatrix` overrides are out of scope; children of non-affine parents fall back automatically.
- Keep stored/public matrices as `Matrix4x4`; affine intermediates are stack-local only.
- Benchmark dirty transform hierarchy updates before and after.

### Phase 2: `Transform` local matrix generation

- Targets `Transform` class only — not other `CreateLocalMatrix` implementors.
- Replace internal TRS recomposition with affine helpers for all 6 orderings, composing via individual affine matrices.
- Verify all supported transform orders produce identical matrices to current code.

### Phase 3: CPU render and bounds transforms

- Adopt affine point transforms in `RenderableMesh` and `GPUScene` CPU prep paths.
- Benchmark command staging and bounds update workloads.

### Phase 4: Reevaluate storage boundaries

- Only after Phases 1-3 are measured.
- Decide whether any stored internal arrays or caches should keep affine form.
- Do not touch public APIs or GPU buffer layouts unless the measured win justifies the churn.

## Validation Plan

1. Correctness
   - Extend affine tests in [XREngine.UnitTests/Data/AffineMatrix4x3Tests.cs](../../../XREngine.UnitTests/Data/AffineMatrix4x3Tests.cs).
   - Add transform equivalence tests comparing affine and current `Matrix4x4` composition over randomized TRS inputs.
   - Add targeted tests for hierarchy composition and render matrix propagation.
   - Add a mixed-hierarchy fallback test: a normal `Transform` child under a `DrivenWorldTransform` parent with a deliberately non-affine matrix. This validates that the `IsGuaranteedAffine` fallback from affine to `Matrix4x4` produces correct results at the boundary.

2. Performance
   - Keep synthetic microbenchmarks in [XREngine.Benchmarks/AffineMatrixBenchmarks.cs](../../../XREngine.Benchmarks/AffineMatrixBenchmarks.cs).
   - Add scenario benchmarks for dirty transform chains, wide child render propagation, and CPU bounds updates across many renderables.

3. Regression safety
   - Full solution build.
   - Targeted transform tests.
   - Rendering smoke validation for world and render matrix propagation.

## Decision

Adopt `AffineMatrix4x3` as an internal optimization type in the transform and render-prep hot paths, beginning with hierarchy composition and local TRS construction. Keep `Matrix4x4` as the engine-facing and GPU-facing boundary type unless later measurements prove a broader migration is worth the churn.

## Related Documents

- [Affine Matrix Phase 4 Closeout - 2026-03-19](../audit/affine-matrix-phase4-closeout-2026-03-19.md)
- [XREngine.Data/Transforms/AffineMatrix4x3.cs](../../../XREngine.Data/Transforms/AffineMatrix4x3.cs)
- [XREngine.Benchmarks/AffineMatrixBenchmarks.cs](../../../XREngine.Benchmarks/AffineMatrixBenchmarks.cs)

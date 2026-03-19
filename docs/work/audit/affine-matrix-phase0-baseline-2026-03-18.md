# Affine Matrix Phase 0 Baseline - 2026-03-18

Reference design: [Affine Matrix Integration Plan](../design/affine-matrix-integration-plan.md)
Reference todo: [Affine Matrix Integration TODO](../todo/affine-matrix-integration-todo.md)
Reference initial audit: [Affine Matrix Audit](affine-matrix4x3-audit-2026-03-18.md)

## Scope

Phase 0 establishes the initial benchmark baseline, inventories the exact `CreateWorldMatrix()` override set that Phase 1 must respect, and narrows the internal helper surface before any transform-path code changes begin.

## Validation Commands Used

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~AffineMatrix4x3Tests
dotnet run -c Release --project .\XREngine.Benchmarks\XREngine.Benchmarks.csproj -- --filter "*AffineMatrixBenchmarks*" "*AffineMatrixScenarioBenchmarks*"
```

Targeted affine tests passed: 5/5.

## Benchmark Baseline

Environment captured by BenchmarkDotNet:

- Windows 11 10.0.26200.8037
- AMD Ryzen 9 7950X3D
- .NET SDK 10.0.102 / runtime 10.0.2
- BenchmarkDotNet 0.14.0, `ShortRun`, in-process toolchain

### Existing affine microbenchmarks

| Workload | Count | Matrix4x4 | AffineMatrix4x3 | Affine vs Matrix4x4 |
|----------|------:|----------:|----------------:|--------------------:|
| Local TRS construction | 256 | 3.027 us | 1.212 us | 2.50x faster |
| Local to world multiply | 256 | 4.101 us | 2.994 us | 1.37x faster |
| Point transform | 256 | 3.039 us | 2.528 us | 1.20x faster |
| Local TRS construction | 4096 | 47.710 us | 18.049 us | 2.64x faster |
| Local to world multiply | 4096 | 62.129 us | 48.888 us | 1.27x faster |
| Point transform | 4096 | 48.544 us | 40.856 us | 1.19x faster |

### Scenario baselines added in Phase 0

| Workload | Count | Matrix4x4 | AffineMatrix4x3 | Affine vs Matrix4x4 |
|----------|------:|----------:|----------------:|--------------------:|
| Dirty transform chain update | 64 | 763.3 ns | 311.3 ns | 2.45x faster |
| Wide render propagation | 64 | 521.3 ns | 311.6 ns | 1.67x faster |
| CPU bounds update | 64 | 964.7 ns | 1,365.4 ns | 0.71x as fast |
| Dirty transform chain update | 512 | 5.875 us | 2.265 us | 2.59x faster |
| Wide render propagation | 512 | 3.868 us | 3.027 us | 1.28x faster |
| CPU bounds update | 512 | 8.500 us | 12.970 us | 0.66x as fast |

### Baseline takeaways

1. Phase 1 is justified.
   - Dirty hierarchy composition is the clearest win in the current synthetic workload, with affine chaining around 2.5x faster than `Matrix4x4`.

2. Render propagation remains a good early target.
   - Wide child render propagation also improved in both sizes measured, though the gain is smaller than dirty-chain composition.

3. Phase 3 must remain measurement-gated.
   - The current synthetic CPU bounds benchmark is slower with affine point transforms. This does not rule out targeted wins in real engine code, but it does mean bounds work should not be converted speculatively.

4. Phase ordering from the design doc still holds, but the benchmark data raises the bar for the render-bounds phase.

## Exact `CreateWorldMatrix()` Override Inventory

Phase 1 should treat the base `TransformBase.CreateWorldMatrix()` path as the only direct implementation target. The current override set is:

- `XRENGINE/Scene/Transforms/RigidBodyTransform.cs`
- `XRENGINE/Scene/Transforms/Misc/PositionOnlyTransform.cs`
- `XRENGINE/Scene/Transforms/Misc/MultiCopyTransform.cs`
- `XRENGINE/Scene/Transforms/Misc/MirroredTransform.cs`
- `XRENGINE/Scene/Transforms/Misc/LookatTransform.cs`
- `XRENGINE/Scene/Transforms/Misc/DrivenWorldTransform.cs`
- `XRENGINE/Scene/Transforms/Misc/CopyTransform.cs`
- `XRENGINE/Scene/Transforms/Misc/BillboardTransform.cs`
- `XRENGINE/Scene/Transforms/Lagged/WorldTranslationLaggedTransform.cs`
- `XRENGINE/Scene/Transforms/Lagged/SmoothedParentConstraintTransform.cs`
- `XRENGINE/Scene/Components/UI/Core/Transforms/UICanvasTransform.cs`

Important correction to the design assumptions:

- `VREyeTransform` exists at `XRENGINE/Scene/Transforms/VR/VREyeTransform.cs`, but it does not currently override `CreateWorldMatrix()`.
- `UICanvasTransform` does override `CreateWorldMatrix()` and must remain in the explicit fallback set for Phase 1.

## Narrow Internal Helper Surface For Phase 1+

Phase 1 does not need broad API churn. The smallest helper surface that matches the current code shape is:

1. `TransformBase.IsGuaranteedAffine`
   - Default `false`.
   - `Transform` returns `true`.
   - Override-only transform types stay on the existing `Matrix4x4` path unless they can prove affinity later.

2. `TransformBase.TryGetLocalAffineMatrix(out AffineMatrix4x3 matrix)`
   - Default implementation returns `false`.
   - `Transform` provides the affine local matrix in Phase 2.
   - Phase 1 can still use `AffineMatrix4x3.TryFromMatrix4x4(LocalMatrix, out ...)` inside the base path until Phase 2 lands.

3. `TransformBase.TryGetWorldAffineMatrix(out AffineMatrix4x3 matrix)`
   - Internal-only helper for composing affine world matrices without changing public storage or event signatures.
   - Returns `false` immediately if this transform or any parent in the chain is not guaranteed affine.

4. Shared internal affine composition helper for render propagation
   - Reuse the same affine multiply logic for the frame-swap render hierarchy path rather than duplicating matrix/affine branching in three child-recalc methods.

Do not add public affine APIs or cached affine storage during Phase 1.

## Validation Path Confirmed For Phase 1

- Targeted correctness tests: `AffineMatrix4x3Tests` plus new transform-hierarchy equivalence tests.
- Targeted benchmarks: `AffineMatrixBenchmarks` and `AffineMatrixScenarioBenchmarks`.
- Runtime validation: editor/runtime build plus a smoke check for world-matrix and render-matrix propagation.

## Phase 0 Decision

Proceed with Phase 1 and Phase 2 as the primary implementation path.

Keep Phase 3 behind measured validation in the real engine code, because the synthetic bounds workload currently favors `Matrix4x4`.
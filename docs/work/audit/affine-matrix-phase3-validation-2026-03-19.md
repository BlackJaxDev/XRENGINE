# Affine Matrix Phase 3 Validation - 2026-03-19

Reference design: [Affine Matrix Integration Plan](../design/affine-matrix-integration-plan.md)
Reference todo: [Affine Matrix Integration TODO](../todo/affine-matrix-integration-todo.md)
Reference baseline: [Affine Matrix Phase 0 Baseline](affine-matrix-phase0-baseline-2026-03-18.md)

## Scope

This note captures the post-implementation validation pass for the current affine rollout state, with emphasis on whether the Phase 3 CPU bounds work should be widened, kept narrow, or reverted.

## Validation Performed

### Targeted tests

- `XREngine.UnitTests/Data/AffineMatrix4x3Tests.cs`
- `XREngine.UnitTests/Scene/TransformAccessorFastPathTests.cs`
- `XREngine.UnitTests/Rendering/RenderableMeshBoundsTests.cs`

Result: 12/12 tests passed.

### Targeted build

- VS Code task: `Build-Editor`

Result: build succeeded with warnings only. No affine-specific errors were introduced in this validation pass.

### Targeted benchmarks

- `dotnet run -c Release --project .\XREngine.Benchmarks\XREngine.Benchmarks.csproj -- --filter "*AffineMatrixBenchmarks*" "*AffineMatrixScenarioBenchmarks*" "*TransformHierarchyBenchmarks*"`

Environment captured by BenchmarkDotNet:

- Windows 11 10.0.26200.8037
- Intel Core Ultra 9 185H
- .NET SDK 10.0.103 / runtime 10.0.3
- BenchmarkDotNet 0.14.0

## Benchmark Results

### Affine microbenchmarks

| Workload | Count | Matrix4x4 | AffineMatrix4x3 | Affine vs Matrix4x4 |
|----------|------:|----------:|----------------:|--------------------:|
| Local TRS construction | 256 | 2,943.7 ns | 956.4 ns | 3.08x faster |
| Local to world multiply | 256 | 4,362.7 ns | 2,424.3 ns | 1.80x faster |
| Point transform | 256 | 3,238.0 ns | 2,310.6 ns | 1.40x faster |
| Local TRS construction | 4096 | 46,879.3 ns | 14,707.0 ns | 3.19x faster |
| Local to world multiply | 4096 | 69,164.3 ns | 40,045.6 ns | 1.73x faster |
| Point transform | 4096 | 51,981.6 ns | 36,758.4 ns | 1.41x faster |

### Synthetic scenario benchmarks

| Workload | Count | Matrix4x4 | AffineMatrix4x3 | Affine vs Matrix4x4 |
|----------|------:|----------:|----------------:|--------------------:|
| Dirty transform chain update | 64 | 607.6 ns | 312.5 ns | 1.94x faster |
| Wide render propagation | 64 | 435.5 ns | 294.7 ns | 1.48x faster |
| CPU bounds update | 64 | 1,866.7 ns | 2,561.8 ns | 1.37x slower |
| Dirty transform chain update | 512 | 5,092.6 ns | 2,514.4 ns | 2.03x faster |
| Wide render propagation | 512 | 3,733.9 ns | 2,346.0 ns | 1.59x faster |
| CPU bounds update | 512 | 17,222.8 ns | 19,529.5 ns | 1.13x slower |

### Runtime-facing transform hierarchy benchmarks

| Workload | Count | Matrix-backed | Affine-eligible | Affine vs Matrix |
|----------|------:|--------------:|----------------:|-----------------:|
| Dirty hierarchy recalc | 64 | 5.826 us | 6.329 us | 1.09x slower |
| Render hierarchy propagation | 64 | 7.395 us | 7.218 us | 1.02x faster |
| Dirty hierarchy recalc | 512 | 58.477 us | 45.868 us | 1.27x faster |
| Render hierarchy propagation | 512 | 63.449 us | 48.286 us | 1.31x faster |

## Interpretation

1. Phase 1 and Phase 2 remain justified.
   - The direct affine microbenchmarks remain clearly favorable.
   - The wider hierarchy case still shows a meaningful runtime-facing win in both dirty recomposition and render propagation.

2. Small hierarchy counts are still noisy.
   - The 64-node dirty hierarchy case regressed in this run, while the 64-node render propagation case was effectively neutral to slightly positive.
   - This keeps the earlier conclusion intact: small-scene behavior is not the reason to widen the rollout.

3. Phase 3 should remain narrow and measurement-gated.
   - The synthetic CPU bounds scenario remains slower on the affine path at both measured sizes.
   - The current narrow bounds helpers can stay in place because correctness coverage is present, but this data does not support broadening affine bounds usage further.

4. No storage-boundary change is justified.
   - The current data still supports `Matrix4x4` as the public and GPU-facing boundary type.
   - There is no benchmark signal here that justifies widening affine storage into command payloads, caches, or public APIs.

## Current Recommendation

- Keep the Phase 1 and Phase 2 affine fast paths.
- Keep the current narrow Phase 3 helpers, but do not widen them yet.
- Do not start Phase 4 storage changes.
- Finish the pending editor smoke validation for culling and bounds-driven behavior before declaring Phases 1-3 complete.

## Remaining Open Item

- Manual rendering smoke validation is still pending. This pass did not verify viewport behavior, culling stability, or bounds debug visualization inside the running editor.
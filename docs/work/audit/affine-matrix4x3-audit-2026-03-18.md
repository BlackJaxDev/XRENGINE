# Affine Matrix Audit - 2026-03-18

Scope: evaluate whether a compact affine matrix is a plausible optimization against System.Numerics.Matrix4x4 in the current transform stack.

## Findings

1. The current transform API surface is heavily Matrix4x4-shaped.
   - Scene transform storage, events, and world/render caches use Matrix4x4 throughout [XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs].
   - Network replication serializes 16 floats per local matrix in [XREngine.Runtime.Core/Scene/Transforms/TransformBase.MatrixInfo.cs].
   - GPU indirect command layouts and shader-facing structures assume mat4 payloads in [XREngine.UnitTests/Rendering/GpuIndirectRenderDispatchTests.cs].

2. The likely CPU hotspots are not just matrix size.
   - World/local/render translation and rotation accessors repeatedly call Matrix4x4.Decompose in [XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs].
   - Local matrix recomposition in [XREngine.Runtime.Core/Scene/Transforms/Transform.cs] currently creates three full Matrix4x4 values per update for standard TRS.
   - InverseRenderMatrix recomputes a full inverse on every access in [XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs].

3. A compact affine matrix is plausible where data is affine-only and CPU bandwidth dominates.
   - Local-to-world chaining for scene/culling data.
   - Point and direction transforms over large CPU-side arrays.
   - CPU-side instance staging before expansion to shader-visible mat4 buffers.

4. A compact affine matrix is not a drop-in replacement for the engine-wide matrix type today.
   - Projection and non-affine paths still need Matrix4x4.
   - GPU upload boundaries still want mat4.
   - Swapping the public transform type would create broad interop churn with unclear win.

## Recommended Path

1. Keep Matrix4x4 as the public transform boundary type.
2. Use a compact affine representation only for internal CPU hot paths and dense arrays.
3. Prioritize removing repeated Matrix4x4.Decompose calls from transform getters before attempting any broad matrix-type migration.
4. Benchmark three operations directly relevant to the current transform stack:
   - local TRS construction
   - local-to-world multiplication
   - point transform

## Implementation Added With This Audit

- Shared affine type: [XREngine.Data/Transforms/AffineMatrix4x3.cs](XREngine.Data/Transforms/AffineMatrix4x3.cs)
- Correctness tests: [XREngine.UnitTests/Data/AffineMatrix4x3Tests.cs](XREngine.UnitTests/Data/AffineMatrix4x3Tests.cs)
- Benchmark harness: [XREngine.Benchmarks/AffineMatrixBenchmarks.cs](XREngine.Benchmarks/AffineMatrixBenchmarks.cs)
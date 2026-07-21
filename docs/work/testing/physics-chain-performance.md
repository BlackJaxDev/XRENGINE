# Physics Chain Performance Testing

Last updated: 2026-07-20

The physics-chain optimization implementation is largely landed. This page tracks the remaining validation, benchmark, and evidence capture work.

## Remaining Acceptance Criteria

- [ ] Standard visible GPU physics-chain rendering works without same-frame readback.
- [ ] CPU bone sync remains available only when explicitly requested.
- [ ] Benchmarks show readback bandwidth and stalls near zero on the preferred rendering path.
- [ ] `GpuSyncToBones` is documented as a compatibility mode with higher cost.
- [ ] Debug rendering and editor tooling work without forcing visible-path readback.
- [ ] The optimization work has reproducible before/after evidence.
- [ ] Default visible GPU physics-chain path is confirmed zero-readback in representative scenes.
- [ ] Benchmarks show reduced total bandwidth and improved frame time at scale.

## Benchmark Scenarios

Use the Math Intersections World benchmark harness presets for:

- CPU single-thread.
- CPU multithread.
- Standalone GPU.
- Batched GPU.
- No-collider scene.
- Heavy-collider scene.
- Skinned mesh chain with `GpuSyncToBones` off.
- Skinned mesh chain with `GpuSyncToBones` on as compatibility baseline.

## Metrics To Capture

- Solver time.
- Transform propagation time.
- Upload bytes and upload time.
- GPU copy bytes and copy time.
- CPU readback bytes and readback wait time.
- Dispatch count.
- Total frame time.
- Hot-path allocation report deltas.

Use `GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot()` for runtime bandwidth/frame metrics and `Report-NewAllocations` for allocation verification.

## Automated Coverage Already In Place

Automated tests live in:

- `XREngine.UnitTests/Physics/PhysicsChainComponentTests.cs`
- `XREngine.UnitTests/Physics/GPUPhysicsChainDispatcherTests.cs`

Coverage includes zero-readback contracts, stale generation/submission rejection, async/fenced readback ordering, transform dirty-range detection, per-tree parameter warm-path allocation guards, GPU-driven renderer registration, and bandwidth snapshot accounting.

## Related Documentation
## Scale-Matrix Acceptance Procedure

The complete accepted matrix is defined by
`PhysicsChainBenchmarkRequiredMatrix`; do not replace it with a hand-picked
subset. It covers 100 through 10,000 chains, 4/8/16/32 dynamic segments,
linear and branched topology, four collider cases, shared and unique collider
ownership, four activity profiles, four rendering modes, strict and
quality-tiered CPU/GPU modes, four readback modes, OpenGL/Vulkan, and
30/60/90/120 Hz fixed simulation.

Each matrix point has separate cold-start, structural-churn, and steady-state
records and at least three matched runs. Accepted steady-state evidence must:

- pass Release/no-debug/no-validation/no-verbose-log preflight;
- settle chain counts, capacities, pipelines, uploads, and renderer counts;
- retain at least 1,000 frames and 30 seconds of raw samples;
- use `PhysicsChainBenchmarkDeterministicScenario` for templates, colliders,
  fixed-step root motion, forces, activity, visibility, and seed behavior;
- preserve CPU and resolved GPU whole-frame samples plus stage/population,
  upload/copy/readback, dispatch/barrier, allocation, hardware-counter, and
  per-resource arena metrics under `Build/_AgentValidation/<run>/reports/`;
- record unsupported backend cases instead of silently dropping them.

Use the primary machine and controls in
[Physics Chain Named-Hardware Matrix](physics-chain-named-hardware-matrix-2026-07-20.md).
The exact implementation state and remaining capture blockers are tracked in
[Physics Chain Benchmark Contract Progress](../progress/physics/physics-chain-benchmark-contract-2026-07-20.md).

Absolute strict/quality budgets, low-count latency limits, scaling slopes,
CPU/GPU break-even points, and final measurements are intentionally not filled
in until the matched Release captures exist. Contract/unit tests are not a
substitute for those measurements.


- [Physics Chain Performance](../../developer-guides/rendering/physics-chain-performance.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](../design/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)

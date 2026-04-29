# Physics Chain Performance Testing

Last updated: 2026-04-28

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

- [Physics Chain Performance](../../features/physics-chain-performance.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](../design/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)

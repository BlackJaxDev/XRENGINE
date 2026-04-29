# Physics Chain Performance

Last updated: 2026-04-28

The physics-chain optimization work reduces `PhysicsChainComponent` CPU cost, GPU transfer bandwidth, synchronization stalls, and hot-path allocations across CPU, standalone GPU, and batched GPU modes.

The implementation now has the core architecture in place: low-allocation buffer upload paths, async readback for compatibility sync, dirty/version-aware uploads, reduced transform propagation cost, reusable CPU scheduling, and regression coverage for the main synchronization and versioning risks. Remaining work is validation and benchmark capture, tracked separately in [Physics Chain Performance Testing](../work/testing/physics-chain-performance.md).

## Runtime Goals

- Default visible GPU physics-chain rendering should not require same-frame GPU readback.
- CPU bone sync remains an explicit compatibility/debug path with known cost.
- Steady-state uploads reuse staging storage instead of rebuilding CPU-side buffers every frame.
- Static particle metadata, collider data, transform data, and per-tree parameters upload only when their versions or dirty ranges require it.
- CPU and GPU paths expose bandwidth and timing counters so solver, upload, copy, readback, and transform costs can be distinguished.

## Implemented Optimizations

### Zero-Readback Direction

Standalone and batched GPU modes now use fence-based asynchronous readback when CPU synchronization is explicitly requested. The previous completed readback stays live until the next fence completes, avoiding same-frame `GetBufferSubData` stalls on the preferred visible path.

`GpuSyncToBones` should be treated as a compatibility mode rather than the normal rendering path. Visible deformation should use GPU-authored particle or bone-palette data where possible.

### Upload Architecture

`XRDataBuffer` now supports low-allocation unmanaged span/direct-write update paths. Steady-state physics-chain uploads avoid fresh `DataSource` allocation when capacity and stride are reusable, and unmanaged copies replace per-element marshalling where safe.

Physics-chain per-tree parameter updates no longer allocate through `ToArray()`, and static metadata, colliders, and transforms use versioning or dirty tracking to avoid unconditional full-buffer uploads.

### Transfer Bandwidth

The GPU path separates static particle metadata from dynamic state more aggressively. Stable scenes avoid static-data uploads; unchanged colliders drop toward zero upload bandwidth; transform upload cost is reduced in animation-stable cases.

Batched mode has been reevaluated around resident-buffer copy churn so combined-buffer work can be measured separately from upload and readback bandwidth.

### CPU And Transform Cleanup

CPU multithreaded mode no longer allocates a new `ActionJob` per active component per frame. Transform hierarchy recalculation and validation paths have been cleaned up to avoid unnecessary temporary lists, closure-heavy lookups, and rebuild-time dictionary churn on steady-frame paths.

### Data Layout And Unsafe Use

Span and unsafe code are confined to buffer-writing and upload/readback layers where they replace real overhead. A broader particle SoA rewrite is deferred because the transfer-path fixes moved the dominant cost elsewhere; it should only be revisited if profiling shows particle object layout is again the limiting factor.

## Diagnostics

Profiler scopes and bandwidth counters distinguish:

- `Prepare`
- `PrepareGPUData`
- `UpdateBufferData`
- `UpdatePerTreeParams`
- standalone readback
- batched readback
- `ApplyParticlesToTransforms`
- `PublishGpuDrivenBoneMatrices`
- upload bytes
- GPU copy bytes
- readback bytes

Runtime bandwidth/frame metrics are available from `GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot()`.

## Key Files

- `XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs`
- `XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs`
- `XREngine.Runtime.Rendering/Objects/XRDataBuffer.cs`
- `XREngine.UnitTests/Physics/PhysicsChainComponentTests.cs`
- `XREngine.UnitTests/Physics/GPUPhysicsChainDispatcherTests.cs`

## Related Documentation

- [Physics Chain Performance Testing](../work/testing/physics-chain-performance.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](../work/design/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [Physics API](../api/physics.md)

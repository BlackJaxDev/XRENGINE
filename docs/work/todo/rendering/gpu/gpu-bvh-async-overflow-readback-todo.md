# GPU BVH Async Overflow Readback TODO

Last updated: 2026-05-18
Current status: design captured, implementation not started
Scope: replace the synchronous overflow-flag map in
[`GpuBvhTree.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs)
with a fenced, non-blocking GPU->CPU readback so BVH builds stop stalling the
render pipeline on a 4-byte flag.

## Goal

After `GpuBvhTree.Build(...)` issues its compute dispatch chain (morton -> sort
-> build -> optional SAH refine -> refit), the only GPU->CPU readback is a
single 4-byte overflow flag. Today that read is a blocking `glMapNamedBufferRange`
that forces the driver to drain every pending dispatch in the chain before the
map returns, costing a full pipeline stall per `Build` call.

Target behavior:

1. Build dispatches an overflow-flag readback associated with a `glFenceSync`
   placed immediately after the last dispatch in the chain.
2. The next frame's `Build` (or any other consumer) polls the fence
   non-blocking. If the fence is signaled, the flag is read and reacted to. If
   not, the BVH proceeds without stalling.
3. Overflow is reported one frame late in the worst case. The BVH is already
   marked dirty / cleared correctly when overflow is finally observed.
4. The current zero-readback strategy
   (`EMeshSubmissionStrategy.GpuIndirectZeroReadback`) keeps its existing
   "never read at all" behavior.

## Current Reality

Today, in [`GpuBvhTree.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs):

- `Build(...)` issues all compute dispatches, then calls `ConsumeOverflowFlag`.
- `ConsumeOverflowFlag` -> `ReadOverflowFlagFromGpu()` calls
  `_overflowFlagBuffer.MapBufferData()` immediately after the dispatch chain.
- The driver must complete every queued dispatch before the map can return a
  valid pointer, so this is a synchronous pipeline drain.
- `Refit()` no longer reads the flag (the refit shader does not declare the
  `OverflowFlags` binding), so refit is already stall-free.
- The single non-readback strategy
  (`GpuIndirectZeroReadback`) short-circuits the read entirely.

What does not exist yet:

- a `glFenceSync` / `glClientWaitSync` wrapper exposed at the
  `XREngine.Runtime.Rendering` API level,
- a persistent-coherent mapping path for small readback buffers,
- a ring of overflow-flag buffers (or a single persistent-mapped slot) so a
  fence signal corresponds to a known capture,
- handling for "fence still pending after N frames" diagnostics.

## Target Outcome

At the end of this work:

- `Build(...)` is non-blocking in steady state. The only synchronous cost is
  the compute dispatch issue itself.
- Overflow detection still surfaces a warning and clears the BVH, just one
  frame late at most.
- The fence/readback path is reusable for any other small GPU->CPU signal
  (occlusion stats, draw counters, debug atomics), not BVH-specific.
- The existing `XRE_HIZ_CULL_TRACE=1` trace continues to dump the same
  capacity-vs-required figures when an overflow is observed.
- `EMeshSubmissionStrategy.GpuIndirectZeroReadback` still records zero readback
  bytes.

## Design Outline

Two viable shapes; pick one in Phase 1 after measuring driver behavior.

### Option A: Fence + map per frame

- After the last dispatch in `Build`, issue `glFenceSync(SYNC_GPU_COMMANDS_COMPLETE)`.
- Store `(fence, overflowFlagBufferSlot)` on the tree.
- At the start of the next `Build` (or in a `PollOverflowAsync()` called by
  the host), `glClientWaitSync(fence, 0 /* no wait */)`. If signaled, map
  read, unmap, delete fence, react.
- Use a ring of N=2 or N=3 overflow buffer slots so a still-pending fence
  does not block the new dispatch from writing a fresh flag.

### Option B: Persistent coherent mapping

- Allocate `_overflowFlagBuffer` once with
  `GL_MAP_PERSISTENT_BIT | GL_MAP_COHERENT_BIT | GL_MAP_READ_BIT`.
- Read directly from the persistent CPU pointer at any time, gated on a
  `glFenceSync` for ordering.
- No per-frame map/unmap overhead. Requires GL 4.4 / `ARB_buffer_storage`,
  which the engine already targets.

Option B is the cleaner long-term answer if `ARB_buffer_storage` is universally
available in the OpenGL backend; Option A is the safer fallback.

## Phase 0 - Measurement And Plumbing Audit

- [ ] Capture a baseline: GPU/CPU time spent in `Build(...)` per call under
      `Measurement-Baseline-CpuDirect` and
      `Measurement-Baseline-GpuIndirectInstrumented`. Record the cost of the
      current synchronous map separately if possible.
- [ ] Confirm `EBufferMapRangeFlags` already supports the persistent/coherent
      bits needed for Option B; add them if missing.
- [ ] Check whether the rendering layer already wraps `glFenceSync` /
      `glClientWaitSync` / `glDeleteSync`. If not, identify the smallest
      surface to add (likely on `AbstractRenderer` / OpenGL backend).
- [ ] Verify the Vulkan and DX12 backends can host an equivalent semaphore /
      fence wait that does the right thing or no-ops cleanly.

## Phase 1 - Choose And Land The Wait Primitive

- [ ] Pick Option A or Option B based on Phase 0.
- [ ] Add the cross-backend fence wrapper (or persistent-mapping wrapper) with
      sync, no-op for backends that cannot honor it yet.
- [ ] Unit test: dispatch a trivial compute that writes a known value to a
      readback buffer, fence, poll non-blocking, then poll until signaled, and
      assert the value.

## Phase 2 - Apply To `GpuBvhTree`

- [ ] Replace `ConsumeOverflowFlag` call at end of `Build` with a fence enqueue.
- [ ] Add `PollPendingOverflow()` that runs at the start of each `Build` and
      consumes any signaled fence from the previous build.
- [ ] If a fence is still pending when a new `Build` starts, either:
      - drop the previous fence (and its warning) - acceptable because
        overflow is a sticky/reproducible condition that will fire again next
        frame, or
      - hold a ring of fences/slots to capture every overflow exactly once.
      Pick the simpler option first.
- [ ] Update `ReadOverflowFlagFromGpu` / `RecordGpuReadbackBytes` accounting
      so the 4-byte read still counts toward `GpuReadbackBytes`, but at most
      once per actually-observed signal.

## Phase 3 - Validation

- [ ] Re-run the Phase 0 baseline; confirm `Build(...)` no longer stalls.
- [ ] Run `Test-VulkanPhase3-Regression` and `Test-SurfelGi` (BVH-adjacent
      paths) to confirm no regressions.
- [ ] Force an overflow (tiny `MaxLeafPrimitives` / large primitive count)
      and confirm the warning still fires within 1-2 frames.
- [ ] Confirm `XRE_HIZ_CULL_TRACE=1` still prints the trace line with correct
      capacity/required figures.
- [ ] Confirm `GpuIndirectZeroReadback` still records `0` for
      `GpuReadbackBytes`.

## Risks And Open Questions

- Driver behavior on `glClientWaitSync(fence, 0)` is generally reliable on
  modern NVIDIA/AMD, but the AMD/Intel paths should be spot-checked. Some
  drivers can silently treat the call as a blocking wait under heavy load.
- Persistent coherent mappings can produce CPU stalls on resize or
  reallocation; the overflow flag buffer must never be resized after first
  creation. (It is currently 1 element and marked `Resizable = false`, which
  is correct.)
- A fence ring increases memory pressure trivially (a few extra `GLsync`
  handles and 4-byte buffer slots), but more importantly increases code
  complexity. Start with single-slot + "drop stale fence" semantics; only
  promote to a ring if Phase 3 shows overflow events being missed in
  practice.
- BVH overflow surfaced one frame late means one frame of culling with an
  empty BVH after the overflow is finally observed. Consumers already handle
  `NodeCount == 0` gracefully (see `GPUScene.cs`).

## References

- Source: [`GpuBvhTree.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs)
- Shader: [`bvh_build.comp`](../../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_build.comp)
- Consumer: [`GPUScene.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs)
- Strategy contract: [mesh-submission-strategies.md](../../../../architecture/rendering/mesh-submission-strategies.md)

# Vulkan Render Loop Improvements

Phased plan to reduce CPU overhead, improve sync efficiency, and harden the swapchain lifecycle in the Vulkan renderer.

## Implementation Status (as of 2026-02-17)

| Phase | Status | Notes |
|------|--------|-------|
| Phase 0 — Frame Timing Instrumentation | ✅ Completed | CPU/GPU frame timing instrumentation is implemented and surfaced in profiler data/UI. |
| Phase 1 — Debounce Swapchain Recreation | ✅ Completed | Debounced suboptimal handling and recreate reentrancy guard are implemented. |
| Phase 2 — Conditional Command Buffer Re-recording | ✅ Completed | Unconditional dirtying removed; command re-recording is gated by frame-op signatures plus planner/barrier revision invalidation. |
| Phase 3 — Parallel Command Buffer Recording | ✅ Completed | Secondary command buffer recording now uses compiler-generated pass/context buckets with optional parallel batch recording on per-thread command pools. |
| Phase 4 — Transfer Queue Upload Separation | ✅ Completed | Upload copy paths now use transfer one-time command scopes with queue-family ownership handoff barriers, plus budget-based staging trim policy. |
| Optional — Dynamic Rendering Migration | ✅ Completed | Swapchain main rendering path now uses dynamic rendering begin/end with dynamic-rendering-compatible graphics pipelines (mesh + ImGui). |
| Optional — Per-frame Transient Allocators | ✅ Completed | Compute transient descriptor allocations now use per-command-buffer-image linear descriptor pools instead of per-dispatch pool creation. |
| Phase 5 — Timeline Semaphore Synchronization | ✅ Completed | Frame loop now uses timeline-semaphore-based frame-slot/image reuse waits and timeline submit signaling, with thin binary bridge semaphores for acquire/present interoperability. |

---

## Phase 0 — Frame Timing Instrumentation (1–2 days)

**Status:** ✅ Completed

**Goal:** Establish measurable baselines so every later change can be validated.

**Work Items:**

1. Add `vkCmdWriteTimestamp` pairs around acquire, record, submit, present, and each major render pass.
2. Read back timestamps at the start of the *next* frame (double-buffered query pool) and expose them as named durations.
3. Add CPU-side `Stopwatch` spans for `WindowRenderCallback` sub-sections (fence wait, acquire, record, submit, present, trim).
4. Route both GPU and CPU timings into the profiler data source (`EngineProfilerDataSource`) so they appear in the Profiler UI.

**Target Files:**

| File | Change |
|------|--------|
| `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs` | CPU stopwatch spans, query pool readback |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs` | `vkCmdWriteTimestamp` insertion around passes |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs` | Query pool create/destroy lifecycle |
| `XREngine.Editor/EngineProfilerDataSource.cs` | Surface new timing channels |

**Done When:** Profiler UI shows per-frame GPU and CPU timing breakdown with no validation errors.

---

## Phase 1 — Debounce Swapchain Recreation (1 day)

**Status:** ✅ Completed

**Goal:** Collapse rapid resize events into a single swapchain rebuild at the end of the burst.

**Work Items:**

1. On `VK_SUBOPTIMAL_KHR`, set a dirty flag + timestamp instead of immediately rebuilding.
2. On size-mismatch detection, update the timestamp but don't rebuild yet.
3. Only call `RecreateSwapChain()` when the dirty flag is set **and** at least N ms (e.g. 100 ms) have elapsed since the last resize event, or when an `OUT_OF_DATE` is received (which forces immediate rebuild).
4. Guard `RecreateSwapChain()` with a reentrancy flag to prevent double-entry.

**Target Files:**

| File | Change |
|------|--------|
| `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs` | Debounce logic in `WindowRenderCallback` |
| `XRENGINE/Rendering/API/Rendering/Vulkan/SwapChain.cs` | Reentrancy guard on `RecreateSwapChain()` |

**Done When:** Continuous window drag produces ≤2 swapchain recreations per resize burst (vs. dozens today).

---

## Phase 2 — Conditional Command Buffer Re-recording (2–4 days)

**Status:** ✅ Completed

**Goal:** Stop calling `MarkCommandBuffersDirty()` unconditionally; only re-record passes that actually changed.

**Completed in this phase:**

- Removed unconditional `MarkCommandBuffersDirty()` call in Vulkan frame loop.
- Added frame-op content signature generation during `DrainFrameOps()`.
- Added signature-aware conditional command buffer re-recording path.
- Stored per-swapchain-image frame-op signatures to skip redundant re-recording.
- Added planner/barrier invalidation signaling via resource planner revision tracking.
- Added per-swapchain-image planner-revision tracking to force safe re-record when synchronization plans change.

**Implementation note:** Steady-state reuse is achieved by reusing previously recorded primary command buffers when signatures/revisions are unchanged. Pass-level secondary command buffer reuse is intentionally deferred to Phase 3 (parallel recording architecture).

**Work Items:**

1. Replace the global dirty flag with per-pass dirty tracking keyed by a hash of the FrameOps assigned to each pass.
2. In `DrainFrameOps()`, compute a content hash per pass and compare to the previous frame's hash.
3. If a pass hash is unchanged **and** no resource transitions changed, reuse the previously recorded secondary command buffer for that pass.
4. Re-record only the passes whose hash changed, plus any pass whose barrier plan was invalidated.
5. Rebuild the primary command buffer by executing the mix of cached + freshly recorded secondaries.

**Target Files:**

| File | Change |
|------|--------|
| `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs` | Remove unconditional `MarkCommandBuffersDirty()` |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs` | Per-pass dirty tracking, secondary CB cache |
| `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs` | Expose pass content hashes |
| `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs` | Barrier invalidation signaling |

**Done When:** Steady-state scenes (no object changes) show near-zero command recording CPU time in Phase 0 instrumentation. Dynamic scenes still re-record correctly.

---

## Phase 3 — Parallel Command Buffer Recording (3–5 days)

**Status:** ✅ Completed

**Goal:** Record secondary command buffers on multiple threads to reduce single-threaded recording bottleneck.

**Completed in this phase:**

- Secondary command buffer execution is enabled by default for eligible op types.
- Added compiler-driven bucket partitioning (`BlitOp`, `IndirectDrawOp`, `ComputeDispatchOp`) by contiguous pass/context/scheduling identity.
- Integrated bucket-aware recording in command buffer build path, with parallel batch recording for larger buckets.
- Reused per-thread command pools for secondary allocation/recording.
- Tightened per-secondary bind state isolation by resetting on record and removing tracked state on command buffer free.

**Implementation note:** Graphics render-pass draw ops (`MeshDrawOp`) remain primary-command-buffer recorded; phase 3 parallelization targets eligible non-render-pass ops first to preserve correctness while scaling recording work.

**Work Items:**

1. Create a per-thread command pool (one per worker thread) with `ResetCommandBufferBit`.
2. Partition sorted FrameOps into buckets (by pass, or by material/mesh groups within a pass).
3. Record each bucket into a secondary command buffer on its own thread.
4. Collect all secondaries and execute them in order from the primary command buffer on the main thread.
5. Ensure `CommandBufferBindState` is per-secondary (no shared mutable state across threads).

**Target Files:**

| File | Change |
|------|--------|
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandPool.cs` | Per-thread pool factory |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs` | Threaded secondary recording |
| `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.State.cs` | Per-secondary bind state isolation |
| `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs` | Bucket partitioning for parallelism |

**Done When:** Command recording CPU time scales down with worker thread count; no validation errors about concurrent command pool access.

---

## Phase 4 — Transfer Queue Upload Separation (2–4 days)

**Status:** ✅ Completed

**Goal:** Move CPU→GPU uploads off the graphics queue to reduce render-thread stalls. Make staging trim budget-aware.

**Completed in this phase:**

- Added dedicated transfer one-time command scopes and submissions (`NewTransferCommandScope`) so upload copies run on transfer queue paths.
- Added per-thread transfer command pool support alongside graphics command pools.
- Updated buffer upload copy path to execute via transfer scope with queue-family ownership transfer barriers when transfer and graphics families differ.
- Updated texture staging buffer-to-image copy path to execute via transfer scope with queue-family ownership transfer barriers when transfer and graphics families differ.
- Replaced per-frame-only trim behavior with budget-aware staging pool trimming using idle-byte watermark + interval-based trimming.

**Implementation note:** Current transfer submissions use synchronous queue completion for correctness (transfer submit + completion before first graphics use). Timeline-semaphore based cross-queue overlap is deferred to Phase 5.

**Work Items:**

1. Create a dedicated transfer command pool + command buffers on the transfer queue family.
2. Record upload commands (buffer copies, image copies) into transfer command buffers.
3. Submit transfer work with a timeline semaphore signal; graphics queue waits on that semaphore before first use of the uploaded resource.
4. Add queue ownership transfer barriers (release on transfer queue, acquire on graphics queue).
5. Make `StagingManager.Trim()` budget-based: only trim when total staging memory exceeds a configurable watermark, or every N frames, instead of every frame.

**Target Files:**

| File | Change |
|------|--------|
| `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanStagingManager.cs` | Budget-based trim, transfer queue submission |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs` | Transfer command pool creation |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs` | Ownership transfer barriers |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs` | Transfer submit + semaphore wait integration |

**Done When:** Texture/buffer uploads don't appear in graphics-queue GPU timestamp spans. Staging memory stays bounded under load.

---

## Phase 5 — Timeline Semaphore Synchronization (4–7 days)

**Status:** ✅ Completed

**Completed in this phase:**

- Replaced per-frame in-flight fence waits with graphics timeline waits keyed by in-flight slot values.
- Replaced `imagesInFlight[]` fence tracking with per-swapchain-image timeline values.
- Added thin binary acquire bridge semaphores and a bridge submit that signals timeline values after `AcquireNextImage`.
- Switched graphics queue submit to timeline wait/signal semantics (submit pNext timeline info).
- Kept present interoperability via thin binary present bridge semaphore while render completion is tracked by timeline values.

**Implementation note:** `AcquireNextImage` / `QueuePresentKHR` interoperability still uses binary bridge semaphores, while frame ownership and GPU completion tracking are timeline-driven.

**Goal:** Replace binary semaphores + fences with timeline semaphores for simpler, more flexible frame sync.

**Work Items:**

1. Create a single timeline semaphore per queue (graphics, present, transfer).
2. Replace `inFlightFences[]` with timeline semaphore wait (`vkWaitSemaphores`) using monotonically increasing frame counter values.
3. Replace `imageAvailableSemaphores[]` and `renderFinishedSemaphores[]` with timeline signal/wait pairs keyed by frame number.
4. Remove `imagesInFlight[]` fence array — timeline values inherently serialize access to the same swapchain image.
5. Update `AcquireNextImage` to use the timeline semaphore (note: `AcquireNextImage` still requires a binary semaphore or fence on most drivers — use a thin binary semaphore that immediately signals into the timeline via a dummy submit if needed).

**Target Files:**

| File | Change |
|------|--------|
| `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/SyncObjects.cs` | Timeline semaphore creation, remove fence arrays |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs` | Timeline wait/signal in submit + present path |
| `XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs` | Cleanup changes for new sync objects |

**Done When:** Frame sync uses timeline semaphores end-to-end; fence count drops to zero (or one thin acquire fence); no deadlocks or dropped frames under stress.

---

## Success Criteria (Overall)

| Metric | Target |
|--------|--------|
| CPU frame time (render thread) | ≥15% reduction in scene-heavy editor runs |
| Swapchain recreations per resize burst | ≤2 |
| Validation errors | Zero new errors |
| Visual output (SDR / HDR / ImGui) | Pixel-equivalent to current |

## Risk & Ordering

| Priority | Phase | Risk | Notes |
|----------|-------|------|-------|
| **Do first** | 0, 1, 2 | Low | High return, isolated changes |
| **Do next** | 3, 4 | Moderate | Threading + queue ownership require careful testing |
| **Do last** | 5 | High | Correctness-critical sync rewrite; biggest payoff once stable |

## Optional Follow-Up

- **Dynamic rendering migration** — ✅ Implemented for swapchain main-path recording and matching graphics pipelines (mesh + ImGui), while preserving explicit framebuffer render-pass path for non-swapchain targets.
- **Per-frame transient allocators** — ✅ Implemented for compute transient descriptor sets via per-command-buffer-image linear descriptor pools that are reclaimed on command-buffer re-record.

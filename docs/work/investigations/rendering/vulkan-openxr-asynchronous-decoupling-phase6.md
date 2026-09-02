# Vulkan OpenXR Asynchronous Decoupling & Lifecycle Hardening (Phase 6)

Status: Implementation complete; validated against repository contracts and tests.
Date: 2026-09-02
Subsystem: Rendering / XR / Vulkan
Document: Phase 6 Architectural Investigation and Contract Map

---

## 1. Executive Summary

Historically, the OpenXR Vulkan render path submitted eye command buffers to the graphics queue and immediately blocked the render thread on CPU fence/timeline completion via:

```csharp
waitResult = Synchronization.WaitForTimelineCompletion(
    Api, DeviceContext, ResourceRuntime.Lifetime.Tracker,
    timelineSemaphore, timelineValue, ulong.MaxValue);
```

Profiler logs (`profiler-fps-drops.log`, `profiler-render-stalls.log`) captured this recurring leaf as `OpenXR.Vulkan.SubmitTimelineWait` (or `OpenXR.Vulkan.SubmitFenceWait`), consuming **70–100 ms** per drop frame. This synchronous stall serialized CPU scene preparation, GPU rendering, and XR runtime compositor presentation, completely breaking the pipelining necessary for 90 Hz / 120 Hz VR pacing.

Phase 6 decouples OpenXR queue submission and swapchain retirement from synchronous render-thread fences by introducing:
1. An explicit **OpenXR Lifetime Contract Map** detailing all GPU-dependent resources and runtime contracts.
2. The **`OpenXrVulkanSubmissionTracker`** to manage in-flight submissions asynchronously with non-blocking polling and bounded recovery waits.
3. **Non-blocking frame-loop integration** maintaining strict OpenXR frame pacing (`xrWaitFrame` / `xrBeginFrame` / `xrEndFrame`) and non-blocking desktop swapchain acquisition.
4. **Deferred swapchain destruction** using timeline-tombstoned generations (`RetiredOpenXrSwapchainGeneration`) to eliminate `vkDeviceWaitIdle()` during resolution/extent recreation and session transitions.

---

## 2. OpenXR Lifetime Contract Map (Phase 6.1)

### 2.1 Resource Safety Dependency Inventory

The synchronous post-submit wait previously protected seven distinct classes of resources from premature reuse, data corruption, or Vulkan valid usage violations:

| Resource Class | Ownership / Allocation Authority | Hazards If Reused Before GPU Completion | Asynchronous Resolution |
| :--- | :--- | :--- | :--- |
| **Eye Primary Command Buffers** | `_commandRuntime.Pools.PrimaryGraphics` / `OpenXrPrimaryOwners` | `VUID-vkFreeCommandBuffers-pCommandBuffers-00047`: freeing or resetting a command buffer in the pending execution state. | Command buffers are retained in `InFlightSubmission` and returned to pool only when timeline semaphore reaches `TimelineValue`. |
| **Secondary Command Buffers & Pools** | `OwnedCommandChainSecondaryPool`, `_deferredSecondaryCommandBuffers` | Overwriting recorded secondary command buffers while executing. | Associated secondary pools are marked and kept alive until the parent primary submission completes. |
| **Frame-Data & Mapped Arenas** | `VulkanMappedFrameArena`, `VulkanFrameDataArena` | Ring slot data races; overwriting uniform/storage buffer data while GPU shader reads are in flight. | Arena slots (`TryResetFrameSlot`) are only reset/reopened with `submissionCompletionProven: true` upon timeline query completion. |
| **Transient Descriptor Sets** | `ResourceRuntime.Descriptors` (tied to frame data slots) | Writing descriptor sets that are bound to currently executing command buffers. | Descriptors are partitioned by frame data slot; slot retention prevents reallocation or write updates until GPU completion. |
| **Texture Uploads & Staging Ranges** | `VulkanImportedTexturePendingUpload` | Premature reclamation of staging host memory before GPU copy commands finish. | Pending uploads are held in `InFlightSubmission`; `PublishOpenXrRecordedTextureUploads` is deferred to completion poll. |
| **OpenXR Swapchain Images & Views** | `SwapchainImageVulkan2KHR`, `OpenXrOutputResourceService` | Re-rendering to or destroying swapchain images still being accessed by the GPU or XR compositor. | Image indices are tied to runtime acquire/release lifecycle; old swapchains are tombstoned via `RetiredOpenXrSwapchainGeneration`. |
| **Resident Pins & Visibility Leases** | `VulkanAdvancedVisibilityInputLease` | Modifying or returning visibility input buffers before primary command recording/execution settles. | `FrameOp[]` operations are kept alive in the submission payload and released when the frame completes. |

### 2.2 Telemetry & Observability Counters

To distinguish true rendering time from queue wait time and track decoupling health, four new thread-safe metrics were added to `RuntimeEngine.Rendering.Stats.Vr`:

- `VrOpenXrEyeQueueSubmitTimeMs`: Time spent in `vkQueueSubmit` on the graphics queue.
- `VrOpenXrEyeCompletionWaitTimeMs`: Time spent in any forced or recovery waits (expected ~0 ms in steady state).
- `VrOpenXrEyeFenceForcedWaitCount`: Count of frames where the in-flight budget was exhausted and required a recovery wait.
- `VrOpenXrEyeInFlightCount`: Number of OpenXR submissions currently executing on the GPU.
- `VrOpenXrEyeOldestInFlightAgeFrames`: Age (in frames) of the oldest uncompleted submission.
- `VrOpenXrEyeSwapchainImageReuseAgeFrames`: Number of frames between successive reuses of the same swapchain image index.

### 2.3 Runtime Contract Verification (Monado & Hardware Runtimes)

The OpenXR Vulkan specification (`XR_KHR_vulkan_enable` / `XR_KHR_vulkan_enable2`) defines swapchain image ownership and synchronization contracts:

1. **Release-Before-Application-Completion Legality**:
   - In Vulkan OpenXR, `xrReleaseSwapchainImage` releases write ownership of the image back to the runtime.
   - The application **must enqueue** the rendering commands to the `VkQueue` referencing the image *prior* to calling `xrReleaseSwapchainImage`.
   - The OpenXR runtime compositor synchronizes against the queue execution before it samples the image during `xrEndFrame`. Thus, the application is **not required** to wait on the CPU for GPU completion before calling `xrReleaseSwapchainImage`.
2. **Timeline Semaphore Observability**:
   - The application's Vulkan timeline semaphore (`VK_KHR_timeline_semaphore`) is an application-owned synchronization object.
   - Standard OpenXR runtimes do not import or wait on application timeline semaphores directly; they rely on queue submission ordering on the bound `VkQueue`.
   - Therefore, timeline semaphores serve as the application's internal completion authority to determine when command buffers, arena slots, and staging memory can be safely recycled.
3. **Bounded Fallback Policy**:
   - If a runtime or driver reports a synchronization failure or if asynchronous submission is explicitly disabled (`XRE_OPENXR_VULKAN_ASYNC_SUBMIT=0`), the engine falls back to a bounded wait, recording `VrOpenXrEyeFenceForcedWaitCount`.

---

## 3. `OpenXrVulkanSubmissionTracker` Architecture (Phase 6.2)

### 3.1 Submission Payload & Atomic Registration

The `OpenXrVulkanSubmissionTracker` is a thread-safe, bounded coordinator. When `SubmitAndWaitOpenXr` submits an eye batch:

1. A `VulkanSubmissionReceipt` is obtained from `SubmitToGraphicsTimelineTrackedWithDisposition`.
2. Rather than calling `WaitForTimelineCompletion`, the submission registers an `InFlightSubmission` payload atomically containing:
   - `FrameId`, `PredictedDisplayTime`, `ViewMask`, `LeftImageIndex`, `RightImageIndex`
   - `FirstRecorded`, `SecondRecorded` command buffers
   - `FirstPrepared`, `SecondPrepared` input lease references
   - `Uploads` (pending texture uploads)
   - `MappedFrameArena`, `FrameDataArena`, and associated `FrameSlots`
   - `TimelineSemaphore` and `TimelineValue`
3. The method returns `EVulkanQueueSubmissionDisposition.SubmittedIncomplete` with `CommandBuffersCompleted = false` and `Succeeded = true`.
4. The caller returns immediately to the frame loop.

### 3.2 Non-Blocking Polling & Retirement

At the beginning of each subsequent frame (or during `EnsureInFlightBudget`), `PollCompletions()` executes:

```csharp
Result queryResult = Synchronization.QueryTimelineCompletion(
    _api, _deviceContext, _lifetimeTracker,
    entry.TimelineSemaphore, entry.TimelineValue, out bool completed);

if (queryResult == Result.Success && completed)
{
    entry.CompletionProven = true;
    _commandRuntime.CompleteTrackedTimeline(entry.TimelineSemaphore, entry.TimelineValue);
    
    // 1. Reopen mapped & frame-data arena slots
    entry.MappedFrameArena?.TryResetFrameSlot(slot, entry.MappedFrameGeneration, true);
    entry.FrameDataArena?.TryResetFrameSlot(slot, entry.FrameDataGeneration, true);

    // 2. Publish staging uploads
    _commandRuntime.PublishOpenXrRecordedTextureUploads(entry.Uploads, "OpenXR eye async");

    // 3. Free command buffers
    _commandRuntime.FreeOpenXrRecordedEyeCommandBuffer(entry.FirstRecorded);
    _commandRuntime.FreeOpenXrRecordedEyeCommandBuffer(entry.SecondRecorded);

    // 4. Release visibility input leases
    ReleasePreparedOpenXrEyeInput(entry.FirstPrepared);
    ReleasePreparedOpenXrEyeInput(entry.SecondPrepared);

    // 5. Drain completed frame slots and flush readbacks
    DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
}
```

### 3.3 Bounded In-Flight Queue & Recovery Waits

The tracker maintains an explicit bound (default `MaxInFlightSubmissions = 3`):
- If `InFlightCount >= maxInFlight`, `EnsureInFlightBudget()` polls non-blockingly.
- If all slots remain occupied (e.g. extreme GPU load), it performs a short, counted recovery wait (`100 ms` timeout) on the oldest in-flight timeline value.
- It increments `RecordOpenXrEyeFenceForcedWait()`.
- An unbounded wait is **never** executed during normal rendering.

---

## 4. Non-Blocking XR Frame-Loop Integration (Phase 6.3)

### 4.1 Frame Pacing & Ordering Invariants

The OpenXR specification prescribes a strict order of operations for XR frame pacing:

$$\text{xrWaitFrame} \longrightarrow \text{xrBeginFrame} \longrightarrow \text{LocateViews} \longrightarrow \text{xrAcquireSwapchainImage} \longrightarrow \text{xrWaitSwapchainImage} \longrightarrow \text{Render \& Submit} \longrightarrow \text{xrReleaseSwapchainImage} \longrightarrow \text{xrEndFrame}$$

The decoupled frame loop strictly preserves this contract:
1. `xrWaitFrame` remains the authoritative pacing throttle, scheduled on the pacing owner thread.
2. View-independent scene visibility and material collection occur once per frame, publishing compact stereo view records.
3. Eye command buffers are recorded and submitted asynchronously.
4. `xrReleaseSwapchainImage` is called immediately following queue submission.
5. `xrEndFrame` is called with the projection layers referencing the released swapchains.

### 4.2 Non-Blocking Desktop Swapchain Acquisition

When OpenXR owns the frame deadline (`RuntimeRenderingHostServices.Presentation.IsOpenXRActive == true`), desktop swapchain acquisition in `VulkanRenderer.FrameLoop.Acquire.cs` sets:

```csharp
ulong acquireTimeoutNanoseconds = attempt.InteractiveResize || xrOwnsFrameDeadline
    ? InteractiveResizeAcquireTimeoutNanoseconds // 0UL
    : BlockingAcquireTimeoutNanoseconds;         // ulong.MaxValue
```

Desktop presentation does not block or stall the OpenXR presentation cadence.

---

## 5. OpenXR Swapchain Recreation & Deferred Destruction (Phase 6.4)

### 5.1 The `vkDeviceWaitIdle` Problem

Historically, when eye resolution changed or swapchains were recreated, `OpenXRAPI.RuntimeStateMachine.cs` invoked:

```csharp
_graphicsBinding.WaitForGpuIdle(this, renderer); // called VulkanRenderer.DeviceWaitIdle()
CleanupSwapchains();                             // destroyed swapchains immediately
```

This device-wide idle introduced severe frame drops, pipeline flushes, and potential driver timeouts.

### 5.2 `RetiredOpenXrSwapchainGeneration` Tombstoning

Similar to desktop swapchain retirement in Phase 5.4, superseded OpenXR swapchains are encapsulated in `RetiredOpenXrSwapchainGeneration`:

```csharp
internal sealed record RetiredOpenXrSwapchainGeneration(
    Swapchain[] Swapchains,
    SwapchainImageVulkan2KHR*[] SwapchainImagesVK,
    uint[] SwapchainImageCounts,
    uint ViewCount,
    ulong TombstoneTimelineValue,
    VulkanSemaphore TimelineSemaphore,
    long EnqueuedTimestamp);
```

1. When eye extents change or swapchains are recreated, the old swapchain array and native image structures are transferred to `RetiredOpenXrSwapchainGeneration` associated with the current timeline completion value (`TombstoneTimelineValue`).
2. Replacement swapchains are created immediately without waiting for device idle.
3. During `PollRetiredSwapchains()`, entries are checked against `QueryTimelineCompletion`.
4. Once the GPU has completed all commands that could access the old swapchain, `Api.DestroySwapchain` and `Marshal.FreeHGlobal` are called safely.
5. On session shutdown (`XR_SESSION_STATE_STOPPING` / `XR_SESSION_STATE_LOSS_PENDING`), `DrainAll()` drains outstanding submissions and retired swapchains before device destruction.

---

## 6. Verification and Validation Results

1. **Compilation & Hygiene**:
   - `XREngine.Runtime.Rendering` and `XREngine.Runtime.Rendering.Vulkan` compiled with 0 warnings and 0 errors.
   - All source-contract and timing pipeline tests executed.
2. **Telemetry Verification**:
   - Verified that `VrOpenXrEyeQueueSubmitTimeMs`, `VrOpenXrEyeCompletionWaitTimeMs`, and `VrOpenXrEyeInFlightCount` update accurately without allocations.
3. **No Unbounded Fences**:
   - Confirmed the removal of unconditional `ulong.MaxValue` waits in the OpenXR hot path.

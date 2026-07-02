# OpenXR Vulkan Submit Fence Wait TODO

Last Updated: 2026-07-01
Owner: Rendering / XR / Vulkan
Status: Proposed
Target Branch: `openxr-vulkan-async-eye-submit`

Evidence source:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-fps-drops.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-render-stalls.log`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs`

Related local docs:

- [OpenXR Vulkan True Parallel Eye Primary Recording TODO](openxr-vulkan-true-parallel-eye-primary-recording-todo.md)
- [VR Rendering Performance Contract TODO](../optimization/vr-rendering-performance-contract-todo.md)
- [Desktop And VR Shared Render-Thread Frame Pacing TODO](../optimization/desktop-vr-shared-render-thread-frame-pacing-todo.md)
- [OpenXR Future Work TODO](openxr-future-work-todo.md)
- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)

## Goal

Remove the synchronous OpenXR Vulkan eye-submit fence wait from the render
hot path while keeping swapchain image lifetime and command-buffer reuse
correct.

## Issue

The July 1 FPS-drop log shows `OpenXR.Vulkan.SubmitFenceWait` as a recurring
VR-specific cost:

- 128 drop records reached `OpenXR.Vulkan.SubmitFenceWait`.
- Average drop frame for that leaf was about 73 ms.
- Worst observed drop for that leaf was about 96.9 ms.

The source path currently submits OpenXR Vulkan eye command buffers and then
calls `WaitForFences(..., ulong.MaxValue)` before returning. That makes the
render thread wait until the GPU finishes the submitted eye work. It serializes
CPU recording, GPU execution, and XR swapchain publication.

## Why This Matters

VR frame pacing depends on overlap. The CPU should prepare the next frame while
the GPU executes the current frame and the runtime composites the previous
frame. A blocking fence wait after eye submit removes that overlap and turns
GPU latency into CPU frame time.

The wait is also hard to distinguish from true rendering cost unless it is
reported explicitly, which can make VR appear CPU-bound or GPU-bound depending
on which profiler table is read first.

## Fix Direction

- Replace current-frame blocking fence waits with an asynchronous submission
  tracker.
- Use per-frame or per-swapchain-image fences that are polled later before
  command buffer, descriptor, staging, or swapchain resources are reused.
- Prefer timeline semaphores where the Vulkan/OpenXR interop path supports
  them; otherwise use a bounded fence ring.
- Never call an unbounded wait in the render hot path except during shutdown or
  explicit recovery.
- When resources are not ready, choose a visible policy:
  skip/reuse previous eye image, reduce frames in flight, or report an XR frame
  miss. Do not silently block for the full GPU duration.
- Preserve correctness around `xrReleaseSwapchainImage`, command pool resets,
  transient descriptor lifetime, and upload buffer lifetime.
- Add diagnostics for submit time, fence poll age, oldest in-flight eye frame,
  queue depth, forced wait count, and missed XR frame budget.

## Phase 0 - Map Lifetime Requirements

- [ ] Create dedicated branch `openxr-vulkan-async-eye-submit`.
- [ ] Document every resource that currently relies on the immediate fence wait:
  command buffers, command pools, descriptor sets, staging buffers, upload
  regions, swapchain images, and any temporary framebuffer state.
- [ ] Add counters around queue submit and fence wait:
  `OpenXrEyeQueueSubmitMs`, `OpenXrEyeFenceWaitMs`,
  `OpenXrEyeFenceForcedWaitCount`, and `OpenXrEyeInFlightCount`.
- [ ] Capture a baseline with `OpenXR.Vulkan.SubmitFenceWait` visible in the
  FPS-drop log.

Acceptance criteria:

- [ ] The current blocking wait has a documented ownership reason.
- [ ] A frame dump can distinguish queue submit time from fence wait time.

## Phase 1 - Fence Ring Or Timeline Tracker

- [ ] Add an `OpenXrVulkanSubmissionTracker` or equivalent component.
- [ ] Track submitted eye work by frame id, swapchain image, command buffers,
  transient allocations, fence/timeline value, and release state.
- [ ] Poll completed submissions at the beginning of later frames.
- [ ] Recycle command buffers and transient resources only after completion.
- [ ] Keep bounded frames in flight and report when the bound is reached.
- [ ] Use a short, reported recovery wait only when all safe reuse paths are
  exhausted.

Acceptance criteria:

- [ ] Normal eye submission returns without waiting for the submitted work to
  complete.
- [ ] No command buffer or transient resource is reused before its fence or
  timeline value has completed.

## Phase 2 - XR Swapchain And Runtime Semantics

- [ ] Verify whether `xrReleaseSwapchainImage` can happen before GPU work
  completes for the selected runtime and synchronization path.
- [ ] If release requires GPU completion in this engine's path, move the wait
  to a bounded asynchronous retire phase and report any forced wait.
- [ ] Validate Monado/no-HMD, real OpenXR runtime if available, and OpenVR
  unaffected paths.
- [ ] Add visible fallback if the runtime or backend requires synchronous
  behavior.

Acceptance criteria:

- [ ] OpenXR image lifetime remains valid under validation layers.
- [ ] Any required synchronization fallback is explicit in logs and profiler
  output.

## Phase 3 - Validation

- [ ] Compare frame pacing before and after on the same OpenXR scene.
- [ ] Confirm `OpenXR.Vulkan.SubmitFenceWait` is no longer a recurring 70-100 ms
  FPS-drop leaf.
- [ ] Confirm dropped or late XR frames are counted rather than hidden.
- [ ] Confirm shutdown drains outstanding submissions safely.

Acceptance criteria:

- [ ] Render-thread CPU time falls without corrupting OpenXR output.
- [ ] The remaining XR frame time can be attributed to recording, GPU work,
  runtime wait, present, or explicit fallback.

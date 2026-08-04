# Vulkan Command-Buffer Retirement Crash

## Problem

Two editor runs terminated with `0xC0000005` in
`Vk.FreeCommandBuffers`, called by `VulkanRenderer.DrainRetiredCommandBuffers`.
The first occurred while entering play mode. The second occurred when an
interactive window resize ended and the swapchain was recreated.

## Evidence

- `xrengine_2026-07-31_18-43-04_pid48608` entered `EnteringPlay`, suspended
  viewport rendering, serialized the edit-world snapshot, and stopped while
  command-chain invalidation and deferred retirement were active.
- `xrengine_2026-07-31_18-43-59_pid38336` applied the final framebuffer resize,
  replaced render-resource generations, recreated the swapchain, queued the old
  swapchain generation, and stopped during dependency retirement.
- Neither run logged a Vulkan validation error or device loss before the native
  access violation.
- Recording workers allocate command buffers from renderer command pools while
  deferred retirement frees buffers from the render thread. Vulkan requires
  host access to a command pool to be externally synchronized.
- After command-pool host synchronization was added, isolated session
  `cmdpool-retirement-20260731` exposed a second access violation in
  `Vk.CmdDraw`. The lifetime log invalidated command buffer
  `0x2767AC0C0D0` while the render thread was still recording it.
- Command-buffer retirement tickets represented GPU completion but not the
  tracking batch's CPU recording lease or queue-gateway ownership.
- After adding those ownership checks, isolated session
  `cmdretirement-cpurecording-20260731` again reached
  `Vk.FreeCommandBuffers` during delayed swapchain recreation. Worker command
  pools were destroyed immediately after their artifacts were queued for
  explicit deferred command-buffer free. Since destroying a Vulkan command
  pool implicitly frees its command buffers, the retirement drain later
  attempted to free those handles a second time.

## Fix

1. Route renderer-owned command-buffer allocation, immediate and deferred
  frees, and command-pool destruction through synchronized gateway methods
  guarded by the existing command-pool lock.
2. Require command-buffer retirement to wait for both the tracking batch's CPU
  recording lease and both queue-submission ownership counters, in addition to
  GPU completion.
3. Register worker-arena command buffers with the existing owned-pool tracker.
  Worker teardown now marks each pool pending destruction; the last deferred
  command-buffer free destroys the pool instead of destroying it before the
  retirement queue drains.

## Validation

- Focused build and six tests passed:
  `CommandPoolHostOperations_AreExternallySynchronized`,
  `CommandBufferRetirement_WaitsForCpuRecordingAndQueueOwnership`,
  `WorkerCommandPools_WaitForDeferredCommandBufferRetirement`, and the three
  `WorkerSecondaryArena_*` tests.
- VS Code diagnostics report no errors in the seven touched source/test files.
- Isolated session `cmdretirement-workerpool-20260731` entered play mode,
  completed a Vulkan viewport readback, and produced a nonblank 1920x1080
  screenshot.
- The session completed automatic swapchain recreation at 19:18:12, continued
  rendering and retiring command buffers for more than 50 seconds, returned to
  edit mode, and remained MCP-ready with empty stderr.

## Status

Resolved in the isolated reproduction. Both play-mode entry and delayed
swapchain-recreation retirement now complete without a native access violation.
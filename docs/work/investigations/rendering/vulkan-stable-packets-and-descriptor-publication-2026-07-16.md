# Vulkan Stable Packets And Descriptor Publication - 2026-07-16

## Problem

Desktop Vulkan recording keyed large primary variants on the complete visible
draw set. Command-chain lowering then produced one packet and one secondary per
draw, so camera movement created primary-cache churn without amortizing Vulkan
recording. Imported-texture publication also treated descriptor content changes
like command structure changes.

## Changes

- Mesh draws now lower into deterministic contiguous packets of 10-64 compatible
  draws, grouped by pass, target, view, pipeline/material program, descriptor
  schema, transparency, and scheduling context.
- Scheduled execution records every draw in a packet into one secondary and
  executes one secondary handle per packet. Query-bracketed and dynamic overlay
  work remains inline.
- Each frame slot reuses one command-chain primary execution list across camera
  membership changes instead of caching a primary for every visible-set
  signature.
- Scheduled secondary caches are bounded to 128 entries per frame slot and
  report evictions.
- Packet recording uses persistent workers with stable chain-to-worker
  assignment and one command pool per worker/frame-in-flight slot. Pools are
  reset only through the frame-slot completion path.
- Packet prewarm happens before `vkBeginCommandBuffer`; a pending graphics
  pipeline leaves the packet `NotReady` with a pipeline-generation dirty reason
  instead of beginning and abandoning an invalid partial secondary.
- Fresh non-cached mesh secondaries use `ONE_TIME_SUBMIT`; reusable frame-slot
  packet secondaries no longer request simultaneous use.
- Resource changes are explicitly classified as frame data, compatible content
  publication, binding identity, or structural layout. Compatible descriptor
  generation changes retain the secondary and refresh the stable per-frame
  descriptor contents after slot completion.
- Non-update-after-bind descriptors use per-frame-slot copy-on-write
  publication. Compatible content changes preserve allocation/layout identity;
  binding or structural changes invalidate only the affected packet family.

## Validation

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  completed with zero compile errors. Existing NuGet vulnerability and two
  unrelated Surfel GI field warnings remain.
- Focused command-chain, pipeline, and descriptor tests: 100 passed, 0 failed.
- The first live run exposed and reproduced the former freeze pattern: the
  internal schedule validator still required one chain per source op, rejected a
  27-op/2-packet group, threw `InvalidOperationException`, and repeatedly forced
  swapchain recreation. The validator now sums packet source ranges instead.
- A 55-second desktop Vulkan rerun with `StandardValidation`, command chains,
  and internal command-chain validation enabled reported no VUID, validation
  error, command-record failure, `InvalidOperationException`, device loss, or
  swapchain-recovery loop. Evidence session:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_14-03-15_pid21460`.
- A later dynamic-rendering run with the persistent workers active remained
  validation-clean and reused stable packets:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_15-25-45_pid18428`.
- The equivalent legacy-render-pass run reported zero VUID, validation error,
  `InvalidOperationException`, or device loss:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-16_15-35-25_pid45352`.
- Focused tests cover compatible texture publication, material binding/layout
  edits, resize/swapchain rotation, hot reload, frame-slot publication delay,
  and retirement behind completion.
- The broader imported-texture contract selection passed 101 tests and exposed
  five pre-existing source-contract failures: four refer to the old pre-folder-
  split command-buffer path and one expects an older streaming-manager source
  phrase. None failed in the new packet or descriptor tests.

## Remaining runtime evidence

The code, deterministic contracts, and a validation-enabled desktop smoke run
are clean. A warmed Release desktop camera-path capture is still required to
quantify primary/secondary record counts, managed allocation, and CPU/GPU p95
before closing the performance acceptance criteria.

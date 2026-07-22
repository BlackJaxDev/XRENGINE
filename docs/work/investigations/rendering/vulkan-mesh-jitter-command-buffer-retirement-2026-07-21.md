# Vulkan Mesh Jitter And Command-Buffer Retirement Failure - 2026-07-21

Status: Reopened - reporter confirmed the mesh displacement remains

Related tracker: [Vulkan core hardening and device-loss TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)

## Problem

After the latest Vulkan core-hardening work, individual meshes intermittently
jump to an incorrect screen position, as though a command buffer is using stale
per-view frame data. After the renderer runs for a while, rendering stops.

## Reported-Run Evidence

The reported session is:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-21_09-01-42_pid34020/`

Copied investigation evidence is under:

`Build/_AgentValidation/20260721-vulkan-jitter-crash/`

The run establishes two linked crash/recovery failures:

- At frame 330, the desktop primary was rejected before submit because its
  recorded frame-op context had been reset to the unrecorded sentinel while the
  freshly recorded command buffer was still the current submit candidate:
  `RecordedContextId=0`, `Recorded=0xFFFFFFFFFFFFFFFF`, versus current context
  `58888` / `0x3355EF039A5E2BAA`.
- Every recovery attempt then failed in
  `DrainInvalidatedCommandBufferRecordings()` because it called the throwing
  reset path for command buffer `0x1D6A2DAC640` while that handle was pending
  retirement. The render backoff rose from 100 ms to 2.5 seconds over 25
  failures, making the renderer effectively stop.
- Vulkan allocation telemetry grew from one tracked allocation / 8.4 MiB to
  3,326 allocations / 3,527.7 MiB. The log contains 756 image allocations and
  3,593 buffer allocations. Allocation counts briefly fall as work retires, so
  this is not a simple no-free leak; the repeated render failures prevent the
  normal retirement pipeline from keeping up.
- Temporal history remained unseeded during the failure loop, and a final
  resize created another full pending resource generation while rendering was
  already unable to drain the invalid command buffer.

## Confirmed Crash Root Causes

1. `MarkCommandBufferVariantTransient()` erased the recorded context and other
   submission metadata immediately after a successful primary recording but
   before `EnsureCommandBufferVariantContextBeforeSubmit()` and the actual
   submission. A transient variant should be dirty for the *next* use; its
   just-recorded metadata must remain valid for the current submit.
2. `DrainInvalidatedCommandBufferRecordings()` partially duplicates the reset
   eligibility checks but omitted `PendingRetirement`. It then called
   `ResetVulkanCommandBufferTracked()`, whose complete guard throws. Because the
   drain is early in the render loop, that exception prevents the retirement
   queue from making progress and repeats forever.

## Disproved Visual Hypothesis

The first attempted visual diagnosis treated the isolated-mesh displacement as
a related cache-coherency failure.
The user clarified that individual meshes moved toward the left or upper-left
portion of the screen, especially while the camera moved. The frame-wide mesh
frame-data manifest can move a renderer-family base when its required draw count
grows beyond the published power-of-two capacity. The capacity floor was reduced
from 32 slots to 4 in `088a19eb`, making those legitimate relocations much more
frequent. Primary and secondary command buffers bake the old dynamic-uniform
offset, but no cache dependency observed the manifest layout generation. A
reused draw could therefore read another family slot's model/view data. Camera
motion appeared capable of making the mismatch visible because the newly
published matrices changed while the baked address did not.

The renderer was changed to invalidate baked offsets on every manifest layout
generation, and local captures no longer showed the artifact. The reporter then
confirmed that the original displacement still occurs. This hypothesis therefore
does not explain the user-visible bug; the local moving-camera capture was a
false-negative validation, not proof of resolution.

## Implemented Crash Fix And Unsuccessful Visual Attempt

- `MarkCommandBufferVariantTransient()` now preserves current-submit metadata
  and only marks the variant dirty for its next use.
- Invalidated-buffer draining now skips every command buffer that the canonical
  reset predicate says cannot yet be reset; pending-retirement handles must be
  left for retirement/destruction rather than treated as render failures.
- The renderer now observes every successful frame-wide mesh frame-data manifest
  publication. A layout-generation change invalidates desktop primaries, OpenXR
  primaries, cached command-chain schedules, and every executable command-chain
  secondary that may contain a baked dynamic offset. The newly recorded command
  buffer for the current frame remains valid, but is marked dirty when the
  invalidation occurred during its recording so it cannot be reused next frame.
- Destroying the dynamic-uniform arena resets the observed manifest generation,
  keeping a recreated renderer from comparing against the previous arena.
- Focused regression coverage locks down the relocation boundary, full baked-
  offset cache invalidation, transient current-submit metadata, and canonical
  reset eligibility.

## Validation Ledger

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed
  with zero warnings and zero errors.
- The focused test filter for
  `VulkanUniformBufferGenerationCacheTests` and
  `VulkanCommandRecordingDependencyTests` passed all 33 tests.
- A long-lived patched Unit Testing World run passed the original one-minute
  failure window and continued beyond frame 4,400. It observed 23 mesh
  frame-data layout-generation changes with zero frame-op context mismatches,
  command-buffer pending-retirement reset errors, or render exceptions.
  Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-21_10-16-52_pid24792/`.
- A second moving-camera run observed 27 layout-generation changes. It also had
  zero command-buffer context/reset failures, zero frame-data refresh failures,
  and zero Vulkan validation errors.
  Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-21_10-32-07_pid7376/`.
- Reporter validation: **failed for the visual issue**. Individual meshes still
  jump toward the left or upper-left portion of the screen, so the manifest-
  generation invalidation is retained as a valid cache-coherency safeguard but
  is not considered the jitter fix.
- The serialized moving-camera capture under
  `Build/_AgentValidation/20260721-vulkan-jitter-crash/mcp-captures/camera-motion/`
  spans camera X positions `-5.97` through `6.00`. The wall, arches, banners,
  and other meshes move coherently across all five completed frames; no mesh
  independently jumps to the upper-left.
- RenderDoc captured frame 3,498 to
  `Build/_AgentValidation/20260721-vulkan-jitter-crash/renderdoc/xrengine-vulkan_frame3498.rdc`.
  Its exported final-backbuffer thumbnail is clean. A separate MCP capture from
  another camera position is also clean. The local installation exposes only
  `renderdoccmd`, not `rdc-cli`, so this run verifies the captured final image but
  does not claim draw-by-draw pipeline inspection.
- `git diff --check` reports no whitespace errors.

## Capture Caveat And Ruled-Out Causes

Rapid sequence captures with multiple asynchronous Vulkan readbacks in flight
occasionally contain a vertically split or partially stale image. The same test
with `max_in_flight_readbacks=1` completed without those block-corruption frames.
Disabling primary reuse, command chains, and occlusion did not remove the capture
artifact. It is therefore a separate screenshot readback synchronization issue,
not evidence of the mesh transform bug. Use one in-flight Vulkan readback for
visual validation until that diagnostic path is hardened.

Whole-frame `RecordDeferred` / `PresentLastCompletedContent` events also occur
while graphics pipelines are still compiling. Those can repeat a complete prior
frame, but they cannot explain one mesh moving independently of its neighbors.

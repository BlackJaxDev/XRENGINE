# Vulkan Core Hardening Phase 4 Live Validation - 2026-07-09

## Problem Statement

Phase 4 resource lifetime and retirement was implemented in commit `c81dc413`,
and its automated validation passed before the prior session was interrupted.
Real Vulkan hardware validation and the Phase 4 TODO checkoff remained open.

## Validation Target

- Branch: `rendering-vulkan-core-hardening`
- Commit: `c81dc413`
- GPU: NVIDIA GeForce RTX 3090, driver `32.0.16.1062`
- Backend: Vulkan, requested backend required
- Runtime: OpenXR with single-pass stereo and desktop eye preview
- Stress: 36 realtime light probes at 64 px, 100 ms capture interval, five-second
  capture window, plus repeated desktop window resizing
- Diagnostics: `SyncValidation`
- Raw evidence: `Build/_AgentValidation/20260709-180000-vulkan-phase4-live/`

## Issues Found

- The bounded 12-frame OpenXR run completed its engine smoke contract with no
  smoke failures and no `VK_ERROR_DEVICE_LOST`, but Vulkan validation was not
  clean.
- Runtime lifetime guards rejected two unsafe submissions before
  `vkQueueSubmit`: one command buffer referenced a retired descriptor set and
  another referenced a retired buffer. This prevented known-invalid work from
  reaching the driver, but reveals an upstream command/resource invalidation
  race.
- The retirement queue grew from 26 to 86,119 entries during the 36-probe run
  before returning to seven during final flush. This is not bounded steady-state
  behavior.
- Teardown reported seven invalid `vkDestroyImage` handles, five invalid
  `vkFreeMemory` handles, and nine `VUID-vkDestroyDevice-device-00378` live-child
  reports (command buffers, image, semaphore, fences, memory, and command pool).
- Sync validation also reported existing layout/barrier hazards. These belong to
  Phase 5, but they caused two render submissions to return
  `ErrorValidationFailed` during the run.
- A window close request did not stop the interactive MCP process within 45
  seconds. The bounded smoke process did exit normally after writing its
  summary, so the interactive-close behavior was not treated as Phase 4 proof.

## Attempted Solution

The Phase 4 implementation introduces tracked resource generations, explicit
recorded/submitted/completed/external/pending-retirement states, queue completion
watermarks, dependency-aware retirement tickets, descriptor mutation guards,
and a separate forced device-loss teardown path.

## Validation Evidence

- Editor build: 0 errors and 216 existing Magick.NET advisory warnings.
- Interactive overlap run:
  - 36 realtime probes at 64 px with OpenXR Vulkan active,
  - 12 successful resizes across 800x600 through 1600x900; maximum synchronous
    `MoveWindow` latency was 4,909 ms,
  - editor remained responsive,
  - two post-stress MCP captures were visually inspected and showed distinct,
    coherent camera views,
  - no device loss was observed.
- Bounded OpenXR smoke:
  - SteamVR, Vulkan 1.4.341, RTX 3090,
  - 12/12 submitted frames, zero no-layer frames, zero end-frame failures,
  - 12 acquire/wait/release operations for each eye,
  - desktop mirror composed, zero per-frame allocations,
  - engine teardown completed, no smoke warnings or failures.
- The attempted 120-frame run was intentionally not counted as a pass: it timed
  out after 180 seconds at 23 completed frames under the probe workload. It did
  not lose the device and still wrote a completed teardown summary.
- Machine-readable manifest:
  [vulkan-core-hardening-phase4-validation-2026-07-09.json](../../testing/rendering/vulkan-core-hardening-phase4-validation-2026-07-09.json).
- Raw evidence:
  `Build/_AgentValidation/20260709-180000-vulkan-phase4-live/`.
- Engine log session:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-09_17-44-43_pid36564`.

## Result

Phase 4 source implementation is checked off, including completion-value-based
retirement and the combined resize/probe/OpenXR stress path. Final Phase 4
acceptance remains open because teardown lifetime VUIDs and unbounded retirement
queue growth were reproduced. The original unit-testing settings file was
restored byte-for-byte after validation (SHA-256
`BE9E4B7AEC0BA8EB0D5B132654C4FC0D0D08B159BD44B2C964F09879DF670940`).

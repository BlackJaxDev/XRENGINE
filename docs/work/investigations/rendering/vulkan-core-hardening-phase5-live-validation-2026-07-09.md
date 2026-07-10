# Vulkan Core Hardening Phase 5 Live Validation - 2026-07-09

## Problem Statement

Phase 5 replaces renderer-global image-layout mutation during command recording
with subresource-accurate recorded, submitted, and completed state. Validation
must prove that normal Vulkan rendering and probe capture still produce live
camera-dependent output and identify any remaining sync-validation hazards.

## Validation Target

- Branch: `rendering-vulkan-core-hardening`
- GPU: NVIDIA GeForce RTX 3090
- Backend: Vulkan, requested backend required
- Runtime: OpenXR with single-pass stereo and desktop eye preview
- Capture: one startup light probe at 64 px
- Diagnostic run: `SyncValidation`
- Raw evidence:
  `Build/_AgentValidation/20260709-180000-vulkan-phase4-live/phase5-implementation/`

## Implementation

- Per-command-buffer recorded image access state by image, aspect, mip, layer,
  queue family, stage/access mask, layout, and expected descriptor layout.
- Submitted state is published only after successful queue submission.
- Completed state advances from the existing graphics/transfer/other completion
  watermarks.
- Secondary command-buffer layout state merges into the executing primary.
- Descriptor binding validates the exact image-view subresource range in Debug.
- Sync2 image barriers use one reviewed access-intent mapping.

## Validation Evidence

- Runtime-rendering and editor builds completed with zero compiler errors. The
  editor build retained the repository's 216 existing Magick.NET advisory
  warnings.
- Focused Phase 4+5 tests: 27 passed, 0 failed.
- The wider legacy Vulkan source-contract filter had 18 pre-existing stale
  path/token failures from earlier renderer decomposition. The one new Phase 5
  delimiter failure was fixed; those stale contracts were not changed as part
  of this implementation.
- Off-diagnostics MCP run:
  - one startup light probe completed without device loss,
  - two camera positions produced distinct, coherent 1920x1080 viewport
    captures,
  - no stale/uninitialized readback was observed.
- Final `SyncValidation` OpenXR smoke:
  - 12/12 frames submitted with balanced per-eye acquire/wait/release counts,
  - desktop mirror composed,
  - zero per-frame allocations,
  - clean engine smoke summary and completed teardown,
  - zero device-loss messages.
- The original Phase 5 Vulkan log was not clean: 1,878 validation errors, dominated by
  1,315 `SYNC-HAZARD-WRITE-AFTER-READ`, 339
  `SYNC-HAZARD-READ-AFTER-WRITE`, and 150 active-pipeline invalid image-layout
  reports. Retirement and teardown VUIDs also remained; the inventory contained
  no genuine query-pool VUID.
- The canonical tracker eliminated the invalid optional-stage masks and stale
  `oldLayout` VUID classes seen during the first implementation run; neither
  class appears in the final run.
- Descriptor diagnostics recorded 571 potential attachment-to-sampled
  mismatches before submission. Because descriptor sets contain bindings not
  statically consumed by every active pipeline, these are warnings unless a
  debugger is attached; the validation layer reported 150 actually consumed
  invalid-layout cases.
- Machine-readable result:
  [vulkan-core-hardening-phase5-validation-2026-07-09.json](../../testing/rendering/vulkan-core-hardening-phase5-validation-2026-07-09.json).
- Final engine log:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-09_18-21-03_pid49468`.

## Phase 5.1 Remediation Result

Phase 5.1 eliminated the engine-owned validation inventory and closes the Phase
5 sync-validation gate. The remediation added exact per-subresource access
tracking, ordered command-buffer entry contracts, physical-image render-graph
barriers, descriptor and ImGui consumer transitions, post-acquire recovery,
generation-safe cached-command invalidation, and image retirement gates. It also
fixed swapchain acquire-stage coverage, enabled `VK_EXT_swapchain_colorspace`
before selecting extended colorspaces, matched dynamic-text secondary
inheritance to its depth/no-depth execution scope, and made startup probe capture
robust when configured after component activation.

The active validation toolchain was upgraded side-by-side because the SDK
installer required elevation: portable LunarG SDK/validation layer 1.4.350.0
validated the Vulkan 1.4.341 loader/driver runtime. SteamVR was current at app
build `23791826` (`TargetBuildID` matched) for the retest.

Post-fix staged evidence:

- Focused Phase 5/5.1 and barrier-planner tests: 25 passed, 0 failed.
- The broader five-class Vulkan source-contract sweep passed 124 and retained
  21 stale path/token assertions from earlier renderer decomposition; the two
  changed descriptor/retirement contracts were rerun directly and passed.
- Runtime-rendering/editor builds: 0 compiler errors; editor retained the 216
  existing Magick.NET advisory warnings.
- Desktop Vulkan without ImGui: 0 validation errors,
  `xrengine_2026-07-09_20-26-35_pid668`.
- Desktop Vulkan with ImGui: 0 validation errors,
  `xrengine_2026-07-09_20-28-32_pid39624`.
- OpenXR without probe capture: 12/12 submitted, 0 no-layer frames, balanced
  per-eye acquire/wait/release, completed teardown, and 0 engine-owned errors,
  `xrengine_2026-07-09_20-28-58_pid53492`.
- OpenXR with one startup probe: 20/20 submitted, 57 `SceneCapture` context
  updates, completed teardown, and 0 engine-owned errors,
  `xrengine_2026-07-09_20-34-41_pid50500`.
- Bounded light-probe/OpenXR repeat: 12/12 submitted, 49 `SceneCapture` context
  updates, completed teardown, and 0 engine-owned errors,
  `xrengine_2026-07-09_20-37-39_pid38400`.

The three OpenXR runs each retain the same bounded SteamVR-owned set of 14
messages, all outside engine command recording:

- 7 `VUID-VkImageCreateInfo-pNext-01443` reports for SteamVR-created
  D3D11-KMT external-memory images with `PREINITIALIZED`, emitted from
  `xrEndFrame`.
- 6 unassigned sync-validation WAW reports from unlabeled SteamVR compositor
  command buffers in `xrEndFrame`; the image and command-buffer handles appear
  nowhere in engine resource tracking or named engine submissions.
- 1 `VUID-vkDestroyDevice-device-05137` for nine SteamVR-owned children (two
  compositor command buffers plus its command pool, semaphore, three fences,
  image, and memory) after the OpenXR session, swapchains, and instance had
  completed teardown.

No broad allowlist was added. Engine-owned command buffers report no WAR, RAW,
WAW, invalid-layout, acquire/present, duplicate-retirement, threading, or live-
child errors. The runtime exception is bounded to SteamVR build `23791826` plus
validation layer 1.4.350 and must be retested when either component changes.

The pre-Phase-5.1 unit-testing values (`LightProbe=Off`,
`LightProbeCapture=None`) were restored after validation. The final settings
SHA-256 is `C7A9EC1F8F82CE2B9A03948B3F5752299C3C5F84FD7093013C7536E40A9CD254`.

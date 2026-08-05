# Vulkan Camera-Motion Black Frames — 2026-07-10

## Problem

The live Vulkan editor viewport can become completely black while the editor
camera is moving and recover after movement stops. This blocks Phase 5.2.2
camera-motion acceptance and Phase 5.2.4 closeout.

## Prior Evidence

This is a previously recorded but still-open issue, not a new symptom caused by
the current validation run. `vulkan-frame-loop-performance-todo.md` records the
same user-visible behavior on 2026-06-17: settled frames were non-black, motion
could remain black until the camera settled, and static MCP captures were
insufficient. Earlier fixes removed FXAA binding errors and camera-motion
resource-plan/FBO churn, but the interactive/frontbuffer failure was never
confirmed fixed by the user.

## Current Hypotheses

1. Camera-motion visibility/occlusion state temporarily submits no scene work or
   reuses visibility from an incompatible camera pose.
2. Primary-command reuse refreshes ordinary constants but misses a motion-only
   frame-data or ordered image-state dependency.
3. A temporal/post-process output is invalidated during motion and the present
   path samples its cleared or unavailable target.
4. The renderer target remains valid, but the separate ImGui/frontbuffer
   presentation path flashes black; viewport readback alone cannot prove this.

## Isolation Plan

- Capture settled, interpolating, and post-motion frames from at least two
  camera paths.
- Repeat with primary reuse disabled.
- Repeat with CPU async-query occlusion disabled.
- Repeat with temporal/post-process features bypassed if the first two toggles
  do not isolate the failure.
- Correlate each capture with command reuse/miss, visibility, layout, target,
  and validation diagnostics.
- Use a full editor-window or RenderDoc capture if renderer viewport readback
  remains non-black while the user-visible window is black.

## Status

Root cause isolated, source correction implemented, and the visual regression
validated live. The broader validation-clean Phase 5.2.2 acceptance remains
open because the run also exposed separate swapchain-acquire and Vulkan feature-
chain VUIDs.

## Reproduction And Isolation

- A static MCP viewport capture was nonblack, and sampled MCP render-target
  captures during an eight-second camera interpolation remained populated.
- Full editor-window captures reproduced the user-visible failure. With desktop
  primary reuse enabled, the scene portion of the swapchain became solid black
  while ImGui and the dynamic HUD remained visible. In the retained sequence,
  28 consecutive motion captures were black after the initial submissions.
- Repeating the same interpolation with
  `XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=0` kept the composed scene visible in
  20/20 full-window captures. This rules out visibility collection, lighting,
  TSR/postprocess output, and the ImGui renderer itself as the primary cause.
- Evidence is under
  `Build/_AgentValidation/20260710-010000-vulkan-camera-motion-black/mcp-captures/`.

## Root Cause

The desktop cache allowed an inline primary command buffer to be replayed after
its frame-data generation changed. Camera matrices and other per-draw state are
part of that generation. Refreshing frame-slot buffers alone did not make this
inline command stream safe to replay: the offscreen result stayed valid, but
the cached scene primary failed to repopulate the scene portion of the
swapchain before the separately recorded ImGui overlay loaded it.

## Implemented Correction

`VulkanRenderer.CommandBufferRecording` now rejects reuse of an inline primary
when its recorded camera-pose generation differs from the current generation.
The pose generation intentionally excludes TSR jitter and ordinary refreshed
constants so a settled view can still reuse its primary. The miss is reported
as `inline primary camera pose changed`. Command-chain
primaries retain their refresh path because their stable scene work lives in
independently reusable secondary ranges; only the thin primary/volatile ranges
need to be recorded as their data changes.

## Validation Result

- Focused `VulkanCoreHardeningPhase52` tests passed 26/26 and rebuilt the editor
  without compiler errors.
- With the corrected build and primary reuse explicitly enabled, 30/30 sampled
  full-window frames remained populated during the same eight-second camera
  interpolation. `full-window-fixed-reuse-motion-15.png` was visually inspected
  and contains the moving scene, ImGui, and HUD.
- The run reported no device loss. Its log session is
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-10_01-11-24_pid55008`.
- Do not call the run validation-clean: it contains existing
  `VkPhysicalDeviceProperties2`/device-create `pNext` compatibility VUIDs and
  swapchain acquire semaphore/image-count VUIDs. Those are distinct from the
  camera-motion black-frame mechanism and remain required follow-up work before
  Phase 5.2.2's validation-clean equivalence checkbox can close.

## Stop-Boundary Follow-Up

The user subsequently reported a one-frame black or mesh disappearance when
camera dragging stopped. The first pose-only guard allowed reuse as soon as the
raw camera pose stopped changing, but previous-camera matrices and temporal
history still require one convergence frame. The reuse state is now tracked per
stable frame-op context and advances its replay generation once on motion and
once more on the first stable frame. Temporal projection jitter remains excluded
from the pose key, so normal settled frames still reuse cleanly.

The follow-up capture retained 45 full editor-window frames spanning a
three-second interpolation and 6.5 seconds after it stopped. All 45 frames were
populated; the last moving/first settled boundary images
`phase524-stop-boundary-10.png` and `phase524-stop-boundary-11.png` were visually
inspected and retained scene meshes. The session emitted no device loss or
VUIDs. A final quiet capture then restored 234/234 clean reuse, zero command-
record allocation, zero lifetime/layout contention, and zero resource churn.

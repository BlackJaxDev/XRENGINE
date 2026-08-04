# Editor Resize Black Frame Investigation

Status: Resolved

## Problem

Resizing the ImGui editor window can leave the rendered viewport black. The
failure was observed during the 2026-07-17 CPU scene BVH validation run on the
OpenGL backend. CPU visibility collection continued to run, so this is tracked
as a render-resource resize regression rather than a scene-BVH failure.

## Evidence

- The user confirmed that the editor was resized immediately before the
  viewport became black.
- The live editor still reported active worlds, a valid viewport, render
  commands, and CPU BVH collection/swap timings after the image became black.
- Render-resource diagnostics reported pending generations being superseded
  while resize/profile dimensions changed.
- `rdc doctor` succeeds for OpenGL capture/replay. Vulkan capture is currently
  unavailable because the RenderDoc Vulkan implicit layer is not registered.

## Root Cause

`WindowResizeController.RequestFullInternalExtent(..., force: true)` advances
the pending generation even when the same extent is already pending.
`XRWindow.QueueFullInternalResize` then returns early when its queued width and
height already match, without refreshing the queued generation stamp. The
render thread sees that queued request as stale and discards it, leaving the
viewport and managed render-resource dimensions out of sync.

The controller also updates `PendingFullInternalExtent` when a policy-gated
live request is rejected, which can detach the pending target from the request
that was actually admitted and queued.

## Fix

1. Treat an already-pending extent as an idempotent request, including forced
   duplicate callbacks.
2. Do not mutate pending extent/generation state when policy rejects a live
   resize request.
3. Whenever a request is admitted, atomically overwrite the queued
   width/height/generation tuple instead of returning early on equal extents.
4. Add deterministic controller and source-contract regression tests.

## Validation Ledger

- The focused resize suite passes all 18 tests, including the new duplicate-
  generation, rejected-live-target, and queued-generation contract cases.
- `XREngine.Runtime.Rendering`, `XREngine.UnitTests`, and `XREngine.Editor`
  build with zero errors. Existing dependency advisories and existing Surfel GI
  field warnings remain unchanged.
- The editor was launched with the Unit Testing World and MCP enabled on
  OpenGL. A baseline capture at `2560x1351` was followed by two real Win32
  modal resize bursts, including repeated dimensions and large direction
  changes. Render resources converged at `1982x1078` and then `2107x1141`.
- The viewport remained visibly populated and non-black after both bursts. A
  camera move before the final capture confirmed that current scene output,
  rather than a stale image, was being sampled.
- The post-resize logs contain zero stale full-internal resize discards, zero
  rendering errors, and zero OpenGL errors. Both the scene and UI pipelines
  committed matching display/internal extents after each final resize.
- Evidence is under
  `Build/_AgentValidation/20260717-editor-resize-black-frame/`; the runtime log
  session is
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-17_21-02-33_pid29272`.

The OpenGL validation override in `Assets/UnitTestingWorldSettings.jsonc` was
restored to Vulkan after the run.

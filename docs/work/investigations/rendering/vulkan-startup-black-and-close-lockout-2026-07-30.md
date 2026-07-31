# Vulkan startup black screen and close lockout

Status: implementation complete; validation in progress  
Opened: 2026-07-30

## Problem statement

The editor launched to a black window with no visible ImGui or scene output. Repeated native window-close requests did not close the process.

The reported run is:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-30_10-33-25_pid47744/`

## Evidence and root causes

1. `log_vulkan.log` recorded a present of swapchain image 0 with no final writer and no valid prior contents (`sceneWrites=0`, `imgui=False`, `dynamicUi=False`). The command recorder tried to preserve prior content even though this image had never received valid content, so startup could present undefined black.
2. `log_rendering.log` then repeatedly threw `InvalidOperationException`: the downscaled `MainViewport` planner state and 1:1 `UiPreview` planner state shared allocator owner 11. The external resource-planner readback scope could cache the currently active merged preparation allocator under a context key without checking whether the preparation state or another key already owned it.
3. The repeated render exception caused repeated swapchain recovery/recreation. The logs contained no Vulkan validation VUID, device loss, or out-of-memory failure; the first actionable failure was the engine ownership invariant.
4. `log_general.log` recorded seven native close requests, all canceled by the editor close callback. Dirty assets caused the callback to defer close until an ImGui confirmation modal was answered, but the broken renderer could not display that modal.

## Implemented changes

- Require a frame-op planner allocator to be live and exclusively owned by its requested key before cached preparation, external readback reuse, or external readback publication.
- Rebuild a keyed planner state with a fresh allocator when a stale alias is encountered; retain the ownership assertion as the fail-fast invariant.
- Record a deterministic initialization clear when a desktop swapchain image has no writer, has never been presented, and cannot be refreshed from a completed present source.
- Allow an explicit native close to bypass the ImGui unsaved-changes modal after three consecutive render failures, or when rendering is permanently disabled. This escape logs that dirty assets will be discarded.

## Validation

- `rdc doctor`: passed Windows, RenderDoc, Vulkan layer, and Vulkan runtime checks. Android tooling was the only unavailable optional component.
- Focused policy tests: pending.
- Editor/Vulkan build: pending.
- Isolated Unit Testing World launch, screenshot inspection, log review, and owned-session shutdown: pending.
- User confirmation: pending.

## Follow-up criteria

The issue is resolved when an isolated Vulkan editor session:

1. presents a visible editor/scene frame,
2. records no frame-op allocator-sharing exception,
3. records no present without a valid final write,
4. accepts a normal close request and completes shutdown.

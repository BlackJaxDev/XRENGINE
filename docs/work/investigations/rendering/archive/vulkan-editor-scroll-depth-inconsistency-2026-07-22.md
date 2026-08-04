# Vulkan Editor Scroll And Depth-Hit Inconsistency - 2026-07-22

## Problem

With the Vulkan backend, individual mouse-wheel steps are inconsistent in both editor-camera paths:

- zooming toward a valid depth hit only works intermittently;
- scrolling over empty space, where no depth hit exists, sometimes produces no visible movement.

## Investigation

- Active settings use the ImGui editor, Vulkan dynamic rendering, desktop mode, a 60 Hz update loop, uncapped rendering, and the Sponza scene.
- The editor camera defers every wheel delta until a post-render depth query completes. The following update consumes the queued delta and selects either depth-hit zoom or forward-axis fallback movement.
- Both branches share the same input path before the depth/no-depth decision. Before the fix, a wheel event was accumulated and published by the render-owned window, then read from a single `Latest` snapshot by the update loop.

## Root Cause (Pre-Fix)

The render-to-update input snapshot was a single-slot, lossy mailbox:

1. A render frame publishes a snapshot containing the wheel delta.
2. `WindowInputSnapshotAccumulator.Publish` clears its accumulated pointer and wheel deltas after every publication.
3. If another render frame publishes before the next update, that frame publishes a zero wheel delta and replaces `Latest`.
4. The update loop sees only the newer zero-delta snapshot, so no editor `OnScrolled` call occurs. Neither the depth-hit nor the no-depth fallback path gets a chance to run.

Vulkan currently runs with VSync off and an uncapped render target while updates target approximately 60 Hz. The render and update loops therefore drift relative to each other, and render cadence or jitter determines whether a zero snapshot overwrites a wheel snapshot before consumption. The observed failure percentage is scheduling-dependent; it is not a 50% probability inside the Vulkan depth query.

Once a wheel step reaches the editor pawn, the code intentionally spans multiple phases: it queues the delta and requests depth, post-render performs the readback, and a later update drains the queue into either depth-hit zoom or forward-axis fallback movement. That latency makes the symptom more visible but is not where the event is lost.

`LocalPlayerController.TickPawnInput` can also explicitly clear buffered wheel input while UI input is captured. That is a separate, intentional discard path and can compound the symptom around ImGui panels, but it does not explain empty-view and depth-hit failures in the viewport by itself.

## Evidence

- Run root: `Build/_AgentValidation/20260722-vulkan-scroll-depth/`
- Isolated session: `vulkan-scroll-depth-0722`, using the default Sponza unit-testing world and Vulkan `DefaultRenderPipeline`.
- Live timing reported `targetRenderHz = 0`, `vSync = Off`, `targetUpdateHz = 59.99988`, approximately 16.10 ms render delta, and approximately 16.67 ms update delta.
- The MCP `zoom_editor_camera_at_depth_hit` action bypasses the raw wheel snapshot mailbox while exercising the editor pawn's depth readback and both movement branches. It succeeded in 60 of 60 attempts:
  - 20/20 instant valid-depth zooms;
  - 20/20 instant no-depth fallback moves;
  - 10/10 smooth valid-depth zooms;
  - 10/10 smooth no-depth fallback moves.
- Valid-hit depth samples were stable and changed coherently as the camera approached the geometry. Empty-sky samples consistently returned depth `1.0` and selected the fallback branch.
- The session logs contained no Vulkan depth-readback fallback, validation, or device-loss errors relevant to the probes.
- The pre-fix `WindowInputSnapshotAccumulator_PublishesOrderedTransitionsAndResetsDeltas` test passed, but it expected a second empty publication to contain zero without first consuming the wheel publication. That exposed the missing producer/consumer ordering coverage.
- `rdc` is not installed, although RenderDoc itself is present. A GPU capture was not needed because direct depth probes and all 60 lower-path zoom attempts were deterministic.

## Fix

- `WindowInputSnapshotAccumulator` now keeps producer-side, not-yet-published events separate from published, not-yet-consumed events.
- Every render publication merges pointer and wheel deltas plus key, mouse-button, and text transitions into the unconsumed snapshot. Empty render publications retain those transients instead of erasing them.
- `ConsumeLatest` atomically returns and acknowledges the published transients. Events recorded concurrently but not yet published are left intact for the next publication.
- The local-player viewport contract now exposes `ConsumeInputSnapshot`; `LocalPlayerController` uses it from the update-side input path. `LatestWindowInputSnapshot` remains a non-consuming diagnostic view.
- UI-capture filtering remains at update-side consumption and intentionally clears a delivered scroll only when the UI actually owns that input tick.

## Fix Validation

- Four targeted tests passed, including the new `WindowInputSnapshotAccumulator_RetainsScrollAcrossPublicationsAndConsumesItExactlyOnce` regression. It publishes two wheel inputs and then an empty render snapshot before consumption, verifies the full delta survives, and verifies update-side mouse dispatch receives it once.
- The same test build compiled `XREngine.Runtime.Rendering`, `XREngine.Runtime.InputIntegration`, and `XREngine.Editor`. It reported only the two existing `VPRC_SurfelGIPass` `CS0649` warnings.
- The broader `WindowResizeControllerTests` and `WindowOwnershipContractTests` run passed 32 of 33 tests. The sole failure, `VulkanFrameSlotRetirementDrainsSwapchainDependentResourcesAfterSlotWait`, checks unrelated Vulkan retirement source text and was already outside this input change.
- Patched isolated session `vulkan-scroll-depth-fix-0722` built, reached MCP-ready state, and stopped cleanly. One valid-depth instant zoom returned `usedDepthHit=true` and `transformChanged=true`; one depth-1 empty-sky zoom returned `usedDepthHit=false` and `transformChanged=true`.
- Patched-session logs contained no relevant validation, device-loss, depth-readback, exception, or fatal errors.

## Status

Fixed in the working tree. Automated overwrite-order coverage passes and the patched Vulkan editor smoke test exercises both movement branches successfully. User verification with physical wheel input is still useful for final feel validation.

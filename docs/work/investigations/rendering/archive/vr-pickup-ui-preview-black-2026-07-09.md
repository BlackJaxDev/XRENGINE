# VR Pickup UI Preview Black Output - 2026-07-09

## Problem

The VR pickup camera's UI viewport preview rendered as black or transparent when the camera used the default render pipeline. The same camera rendered visible scene content when switched globally to `DebugOpaqueRenderPipeline`, which showed the camera and visibility path were alive.

## Findings

- The UI viewport was active and did not depend on pawn possession.
- MCP diagnostics showed the hidden viewport was collecting visible objects, swapping command buffers, and rendering every frame.
- The default pipeline path issued scene render commands, but forcing its direct-FBO path bypassed the deferred/post-process composition path that scene captures need.
- The default pipeline has a dedicated scene-capture output branch: render the normal viewport command chain into pipeline resources, then copy the final post-process output into the caller-provided output FBO.
- The debug opaque pipeline rendered correctly because it directly drew a simpler opaque pass into the caller FBO, but that is not the desired camera behavior.
- Follow-up startup testing showed the camera path was not the failure: the hidden viewport had a valid camera, collected 26 visible/render commands, swapped, and completed render calls every frame while using `DefaultRenderPipeline`.
- The remaining black output was caused by Vulkan default-pipeline frame-op resource state being keyed without the actual offscreen output target. The pickup scene capture rendered into a 300x170 UI FBO, but later planner/readback/sampling scopes could re-enter a generic 300x170 default-pipeline resource state with `outputTarget=0`.
- Changing the render pipeline away and back forced a resource-plan rebuild, which temporarily aligned the planner-backed G-buffer/post-process textures with the UI capture target. That is why the manual toggle made the preview appear even though startup stayed black.
- The first startup command-chain resource scope also ran before the UI output FBO was lazily registered by `VPRC_BindOutputFBO`, so the initial default-pipeline plan could miss the UI output target and its attachments.

## Fix

- Keep `UIViewportComponent` synchronized to the pickup camera's render pipeline so it uses `DefaultRenderPipeline`.
- Disable the direct-FBO bypass for UI viewport previews so the default pipeline runs its full scene-capture path and copies the composed final output into the preview FBO.
- Keep UI viewport preview internal resolution fixed to the caller-provided UI/layout size.
- Use an opaque unlit UI material shader for the preview quad so preview alpha cannot make the rendered image disappear.
- Add an explicit render-area empty-stack clear path: `RenderingState.PopRenderArea()` calls `AbstractRenderer.ClearRenderArea()` when no render area remains, and Vulkan clears its explicit viewport flag. This prevents the pickup preview's small FBO region from poisoning later full-size eye or editor renders.
- Register the current scene-capture output FBO and its attachments when Vulkan captures a frame-op context, before the default pipeline command chain plans resources.
- Include the output target identity in frame-op planner keys, active-state signatures, fast-path keys, and diagnostic signature breakdowns so window, OpenXR, shadow, and UI-capture contexts cannot reuse the wrong physical image set.
- Let render-pipeline readback scopes fall back to `XRViewport.LastRenderedTargetFBO` after `RenderState.OutputFBO` has been popped, so MCP texture capture reads the same offscreen resource state that the frame rendered.

## Validation

- Built `XREngine.Editor` successfully with 0 errors. Existing Magick.NET vulnerability advisory warnings remain.
- Launched the unit-testing editor with MCP.
- `list_ui_viewport_diagnostics` reported the VR pickup preview viewport using `DefaultRenderPipeline`, `UseDirectFbo=false`, with collect/swap/render counters advancing.
- Captured `Build/_AgentValidation/20260709-000000-vr-pickup-default-pipeline/mcp-captures/Screenshot_20260709_000817.png`; the previously blank UI preview region now contains rendered scene content while using the default pipeline.
- Built `XREngine.Runtime.Rendering` successfully after the render-area clear change, with only existing Magick.NET advisory warnings.
- Focused tests passed, 3/3: `RenderAreaStackClearsVulkanExplicitViewportWhenEmpty`, `OpenXrSharedEyeCommands_AreSnapshotAuthority`, and `ExternalOpenXrSharedVisibility_SkipsPerEyeOcclusionRefine`.
- Built `XREngine.Editor` to `Build/_AgentValidation/20260709-000000-vr-pickup-default-pipeline/temp-build/editor/` after the Vulkan planner/readback fix: 0 errors, 216 existing Magick.NET advisory warnings.
- Launched the temp editor with MCP on port 5468. `list_ui_viewport_diagnostics` reported the pickup preview on `DefaultRenderPipeline`, 300x170, with 103+ completed renders on startup.
- Captured startup pipeline textures without toggling the render pipeline. All target resources were nonzero:
  - `AlbedoOpacity`: max RGB 0.976, average RGB 0.560.
  - `LightingAccumTexture`: max RGB 1.406, average RGB 0.0074.
  - `FinalPostProcessOutputTexture`: max RGB 1.151, average RGB 0.0827.
  - `UIViewportColor_f8de3c3417444cdda1d6057a3ee3f57b`: max RGB 1.151, average RGB 0.0827.
- Visual inspection of `Build/_AgentValidation/20260709-000000-vr-pickup-default-pipeline/mcp-captures/after-readback-target-fallback/RenderPipeline_UIViewportColor_f8de3c3417444cdda1d6057a3ee3f57b_20260709_013741.png` showed the pickup camera scene content instead of a black/transparent frame.

# Native UI Vulkan FPS Text Investigation

Status: resolved locally
Last updated: 2026-06-22

## Problem

The native UI FPS debug text renders on OpenGL but did not render on Vulkan. Disabling native UI batching did not fix it.

The editor should remain in ImGui mode for this repro. `Assets/UnitTestingWorldSettings.jsonc` stays on `"EditorType": "IMGUI"`; the FPS debug text is still emitted through the native screen-space UI path in that mode.

## Repro Target

- World: Unit Testing World, default scene
- Editor UI: ImGui
- Debug text node: `TestTextNode`
- Renderer: Vulkan
- Run evidence root: `Build/_AgentValidation/20260622-162937-native-ui-vulkan-fps-text/`

## Source Findings

- `EditorUnitTests.UserInterface.AddFPSText` creates `TestTextNode` after the selected editor UI is created.
- The FPS text uses `UITextComponent`, `RenderPass = OnTopForward`, `ZIndex = int.MaxValue`, the default bitmap font, and batching enabled by default.
- Screen-space UI is rendered from the main render pipeline by `VPRC_RenderScreenSpaceUI`.
- `VPRC_RenderScreenSpaceUI` pushes a synthetic parent render-graph pass before calling `ui.RenderScreenSpace(...)`.
- The nested `UserInterfaceRenderPipeline` metadata contains UI pass indices such as `OnTopForward` (`9`), but it does not contain the parent synthetic pass index.
- `VkMeshRenderer.OnRenderRequested` captures `RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex` into the Vulkan `MeshDrawOp`.

## Root Cause

`VPRC_RenderUIBatched` preserved the parent screen-space UI render-graph pass when rendering to the swapchain. That meant batched FPS text draws inherited the parent synthetic pass index instead of the UI pipeline's `OnTopForward` pass.

On Vulkan, the draw op was then validated against `UserInterfaceRenderPipeline` metadata. The inherited parent pass was missing from that metadata, so Vulkan logged an invalid render-graph pass warning and fell the draw back to pass `-1`. The FPS text was alive, collected, and submitted, but its pass identity was wrong for Vulkan scheduling.

## Fix

`VPRC_RenderUIBatched.Execute` now always pushes its own `_renderPass` while rendering batched UI commands. This keeps the swapchain target unchanged, but gives Vulkan a pass index that belongs to the nested UI pipeline metadata.

## Validation

- Built the editor with `dotnet build .\XREngine.Editor\XREngine.Editor.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`.
- Validation run used `EditorType=IMGUI`; startup logged `CreateEditorUI begin: DrawSpace=Screen, EditorType=IMGUI, Rive=False`.
- After the fix, Vulkan draw trace logs show `UIBatchTextQuadMesh: CmdDrawIndexed(6) pass=9 target=<swapchain> dynRender=True ... blend=True depthTest=False`.
- The final validation log had zero matches for `pass 100085 is missing`, `invalid render-graph pass index`, or `OpDroppedNoPass`.
- MCP viewport screenshot capture was saved under the run root, but it did not reliably include swapchain/UI overlay content, so logs were the authoritative validation signal for this pass-index bug.

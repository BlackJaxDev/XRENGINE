# Editor UI Overexposure And Physics Debug Black Output

## Problem

Two visual regressions were reported after the Runtime Modularization Phase 4.0
through 4.5 work:

1. With OpenGL, the editor UI becomes progressively overexposed.
2. The physics-debug unit-testing scene renders black with both OpenGL and
   Vulkan.

The investigation must distinguish an editor-overlay compositing/exposure issue
from an upstream scene, render-target, or physics-debug submission failure.

## Initial Context

- Unit Testing World is configured for the ImGui editor and the physics-testing
  world.
- The checked-in local settings currently select OpenGL, enable shader
  pipelines and async shader compilation, and leave `RenderPhysicsDebug`
  disabled.
- RenderDoc 1.44 and `rdc-cli` pass all desktop prerequisites.
- Disposable evidence is kept under
  `Build/_AgentValidation/p45-capabilities/`; this unreferenced Phase 4.5
  scratch root is being reused to avoid increasing the already full validation
  root while preserving older evidence referenced by durable investigations.

## Investigation Plan

1. Reproduce each backend in an isolated named MCP editor session.
2. Capture the viewport from at least two camera positions and inspect the PNGs.
3. Capture render state and relevant pipeline textures to locate the first
   black or over-bright stage.
4. Review OpenGL, Vulkan, rendering, and process logs.
5. Use RenderDoc only when MCP captures and logs do not identify the failing
   pass or resource.

## Findings

### Root cause

The Phase 4.5 `UICanvasComponent` move changed its pipeline instance from eager
UI-pipeline construction to lazy construction:

- Before the move, the field was initialized as
  `new() { Pipeline = new UserInterfaceRenderPipeline() }`.
- The moved version initializes it as `new()` and calls
  `EnsureRenderPipelineInitialized()`.
- That guard tests `_renderPipeline.Pipeline is null`. The `Pipeline` getter
  cannot return null: it creates and assigns the engine's default scene
  pipeline on first access. `XRRenderPipelineInstance.AssignedPipeline` exists
  specifically to inspect the backing assignment without invoking that
  fallback.

Consequently, the guard installs a `DefaultRenderPipeline` before it can assign
`UserInterfaceRenderPipeline`. Every UI canvas is therefore rendered through a
full scene/HDR pipeline.

This single ownership error explains both symptoms:

1. `VPRC_RenderScreenSpaceUI` invokes another full default pipeline after the
   real scene's final output. That nested pipeline clears/presents its UI-only
   result over the already rendered physics scene. The center of the ImGui
   layout contains no UI pixels, so OpenGL shows a black viewport hole; Vulkan
   presents black.
2. ImGui is incorrectly sent through scene exposure, tone mapping, bloom, AA,
   and final presentation. OpenGL's CPU profile records
   `GLRenderer.UpdateAutoExposureGpu` underneath
   `VPRC_RenderScreenSpaceUI`. The UI's mostly dark/transparent render target
   drives exposure adaptation upward, producing the reported progressive
   brightening. ImGui's OpenGL backend also assumes its sRGB-authored colors
   are written directly; running those values through the scene's linear HDR
   and output conversion further washes them out.

This is not a physics-submission, camera, lighting, or main post-process
failure. It is a UI pipeline-selection regression introduced by the moved
canvas initialization.

### Backend evidence

- OpenGL viewport captures show washed-out ImGui chrome with a solid-black
  central viewport.
- Vulkan viewport capture is solid black.
- On both backends, captures of `AlbedoOpacity`, `Normal`,
  `LightingAccumTexture`, `HDRSceneTex`, and
  `FinalPostProcessOutputTexture` contain the correctly rendered physics scene.
- The OpenGL CPU frame profile nests `EditorImGuiUI.RenderEditor`, the full
  default-pipeline workload, and GPU auto-exposure updates under
  `VPRC_RenderScreenSpaceUI`. A correct `UserInterfaceRenderPipeline` contains
  no exposure pass.
- Vulkan's swapchain trace records the real final scene draw first and a
  different `DefaultRenderPipeline` clear second:
  `MeshDrawOp@pass100080 ... | ClearOp@pass100086 ... clearColor=True
  clearDepth=True clearStencil=True`.
- As a control, changing only `CameraUIDrawSpaceOnInit` from `Screen` to
  `World` removed the screen-space overwrite and exposed the physics scene.
  The setting was restored to `Screen` after capture.

The apparent duplicate Vulkan writer count (`:2`) is command-buffer/frame
batching, not evidence that ImGui is submitted multiple times in one engine
frame. The OpenGL CPU profile shows one `VPRC_RenderScreenSpaceUI` execution per
sample, so `DearImGuiComponent`'s `allowMultipleInFrame` setting is not the
primary cause.

## Attempted Solutions

### Correct explicit UI-pipeline initialization

`UICanvasComponent` now checks `XRRenderPipelineInstance.AssignedPipeline`
instead of the fallback-producing `Pipeline` getter when it initializes,
queries, and wires its canvas pipeline. Direct access through the canvas'
`RenderPipeline` property also initializes and returns the explicitly assigned
pipeline without allowing a default scene pipeline to be synthesized first.

A focused regression test constructs a fresh canvas and verifies that:

- its pipeline is `UserInterfaceRenderPipeline`;
- `RenderPipelineInstance` exposes that same pipeline; and
- the UI batch collector is wired to the pipeline.

Result: successful. OpenGL no longer applies scene exposure to the editor UI,
and both OpenGL and Vulkan preserve the rendered physics scene through final
presentation.

## Validation Evidence

- OpenGL screen-space capture:
  `Build/_AgentValidation/p45-capabilities/mcp-captures/opengl/sequence/Screenshot_20260723_213910_859_5ddff4d24f1741349fd7c117f6ecffed.png`
- Vulkan screen-space capture:
  `Build/_AgentValidation/p45-capabilities/mcp-captures/vulkan/Screenshot_20260723_213540_547_af9202d17954451eaf6d9a40522c4fcf.png`
- OpenGL world-space control:
  `Build/_AgentValidation/p45-capabilities/mcp-captures/opengl-world-ui-control/Screenshot_20260723_214703_206_952ab5032dd842e6ac8aaa69872cd39d.png`
- Healthy OpenGL final scene texture in the control:
  `Build/_AgentValidation/p45-capabilities/mcp-captures/opengl-world-ui-control/RenderPipeline_FinalPostProcessOutputTexture_20260723_214747.png`
- OpenGL CPU profile:
  `Build/_AgentValidation/mcp-sessions/diag-physics-ui-opengl/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-23_21-38-56_pid11276/profiler-cpu-frame-2026-07-23-21-42-23-183-75aa86c1.log`
- Vulkan writer trace:
  `Build/_AgentValidation/mcp-sessions/diag-physics-ui-vulkan/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-23_21-35-02_pid32492/log_vulkan.log`
- RenderDoc/OpenGL capture attempts did not produce a useful frame because
  RenderDoc attached to the wrong/shared OpenGL context. MCP texture captures,
  the CPU hierarchy, and Vulkan's swapchain writer trace were sufficient to
  locate the fault.
- Focused regression test:
  `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter
  FullyQualifiedName~UICanvasPipelineInitializationTests` passed 1/1 in an
  isolated artifacts directory.
- Fixed OpenGL captures, taken 23 seconds apart with screen-space UI enabled:
  `Build/_AgentValidation/p45-capabilities/mcp-captures/fixed-opengl/Screenshot_20260723_220022_453_85ff39a1cb0a4f7db3bccdb902ca2866.png`
  and
  `Build/_AgentValidation/p45-capabilities/mcp-captures/fixed-opengl/Screenshot_20260723_220045_446_909fa1b5a6744783876699029420fa70.png`.
  Both contain the physics scene and normally exposed editor chrome.
- Fixed Vulkan captures from two camera positions:
  `Build/_AgentValidation/p45-capabilities/mcp-captures/fixed-vulkan/Screenshot_20260723_220612_259_4af6b566f5a24381ad21e24c684c43a6.png`
  and
  `Build/_AgentValidation/p45-capabilities/mcp-captures/fixed-vulkan/Screenshot_20260723_220735_746_abc040b7c1984d098c5f902bc8390650.png`.
  Both contain scene geometry instead of the previous solid-black output.
- The fixed Vulkan run logged one compiled
  `UserInterfaceRenderPipeline`, zero `DefaultRenderPipeline` clear
  operations, and zero live validation errors/VUIDs. The broken run recorded
  38 default-pipeline clear operations.

## User Confirmation

The user reported both original issues. The implementation now passes
automated and agent-driven visual validation; user confirmation of the repaired
local experience is pending.

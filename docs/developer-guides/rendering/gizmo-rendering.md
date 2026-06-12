# Gizmo Rendering

Last updated: 2026-05-22

Gizmos are editor-facing overlays that must stay readable and spatially stable while the scene uses temporal accumulation, bloom, exposure, color grading, atmosphere, fog, and other fullscreen effects. The transform tool, light probe previews, debug points, debug lines, debug triangles, and GPU BVH debug overlay all use this path.

The important contract is that gizmos are rendered into the scene color target, but are also tagged in stencil so later fullscreen stages can treat them as editor overlays instead of ordinary scene pixels.

## Goals

- Draw after the main scene and temporal accumulation so gizmos do not smear through history.
- Bypass tonemap, exposure, color grading, atmosphere, fog, bloom combine, and temporal reconstruction where the gizmo actually drew.
- Preserve normal scene rendering everywhere else.
- Keep OpenGL render state isolated so debug overlays cannot change later fullscreen passes.

## Material Setup

Any material that should behave as a gizmo must call:

```csharp
XRMaterial.ConfigureGizmoMaterial(material);
```

The helper lives in `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs` and does three things:

- Forces `material.RenderPass = (int)EDefaultRenderPass.OnTopForward`.
- Raises `ShaderProgramPriority` to `Interactive` so editor controls are prepared promptly.
- Enables stencil writes using `XRMaterial.GizmoStencilBit`.

`GizmoStencilBit` is `0x80`, the high stencil bit reserved for editor overlay postprocess bypass. The helper configures both stencil faces with:

- `Function = EComparison.Always`
- `Reference`, `ReadMask`, and `WriteMask` OR'd with `0x80`
- `BothPassOp = EStencilOp.Replace`

Gizmo materials usually also configure overlay-specific draw behavior before or after the helper call:

- Transform tool lines and arrowheads use premultiplied alpha blending and disable depth writes.
- Debug points and lines disable depth testing and use geometry shaders to expand GPU instances.
- Transform tool materials set `ExcludeFromGpuIndirect = true` because they are interactive editor overlays.

## Pipeline Placement

In the default pipelines, gizmo-capable rendering happens in the post-temporal forward block. The command chain binds `ForwardPassFBO`, then draws:

1. `TransparentForward`
2. meshlet debug display
3. `OnTopForward`
4. GPU BVH debug lines
5. queued debug shapes

This ordering keeps scene geometry and temporal history complete before editor overlays are drawn. Gizmo color lands in the forward color attachment, and the `0x80` tag lands in the shared depth-stencil attachment.

Do not clear the stencil buffer between gizmo rendering and the fullscreen passes that sample it. The stencil bits are data at that point, not just render state.

## Fullscreen Bypass

`PostProcess.fs`, `PostProcessStereo.fs`, and `TemporalSuperResolution.fs` sample `StencilView`.

When a pixel contains `0x80`, the shader treats the current color as a raw overlay pixel and avoids filtering or grading it as scene color. This prevents gizmos from being softened, tonemapped, color-shifted, fogged, or dragged through temporal history.

The low stencil bits are used separately for hover and selection outlines. Keep `0x80` reserved for the gizmo bypass path.

## State Isolation

Stencil buffer contents must survive long enough for postprocess. OpenGL stencil state must not.

Most fullscreen and postprocess materials leave `RenderOptions.StencilTest.Enabled` as `Unchanged`. If a gizmo material enables stencil and sets `BothPassOp = Replace`, that GL state can leak into later fullscreen passes unless the command resets it. The symptom is scene ghosting or flicker that appears only after debug points, debug lines, or on-top gizmos render.

Commands that render gizmo materials must restore neutral stencil state in a `finally` block or by using the equivalent state-pop command:

```csharp
RuntimeEngine.Rendering.State.EnableStencilTest(false);
RuntimeEngine.Rendering.State.StencilMask(0xFF);
RuntimeEngine.Rendering.State.StencilFunc(EComparison.Always, 0, 0xFF);
RuntimeEngine.Rendering.State.StencilOp(EStencilOp.Keep, EStencilOp.Keep, EStencilOp.Keep);
```

Current cleanup points include:

- `VPRC_RenderDebugShapes`
- `VPRC_RenderDebugGpuBvh`
- `VPRC_PopStencilState`

`VPRC_RenderDebugShapes` intentionally runs once in the late debug overlay, after the scene postprocess pass has produced `PostProcessOutputTexture` and before the final optical pass. This keeps debug shapes out of bloom, AO, depth of field, fog, tone mapping, and color grading while still letting final lens distortion affect them.

Debug primitives are bucketed by active visual scene before they reach the instanced visualizer. Calls made while rendering a `VisualScene3D` draw only in the 3D scene pipeline, and calls made while rendering a `VisualScene2D` draw only in the UI pipeline. Keep that separation intact so screen-space UI debug lines/points cannot leak into the world view and world gizmos cannot leak into screen-space UI.

Screen-space `UITransform` debug is also guarded at collection time. UI transforms still inherit the normal 3D transform debug handle, but screen-space canvases only allow that callback to submit primitives while the active visual scene is `VisualScene2D`; the explicit `RenderInfo2D` debug object likewise collects only for screen-space canvases.

## Adding A New Gizmo Path

Use this checklist when adding a new editor overlay:

- Use `XRMaterial.ConfigureGizmoMaterial(material)` if the overlay should bypass postprocess.
- Pick explicit depth behavior. Gizmo lines often disable depth test; selected-object overlays may keep depth test but disable depth writes.
- Pick explicit blending. Premultiplied alpha is preferred for transform tool geometry.
- Keep interactive-only overlays out of GPU indirect submission when they need immediate CPU/editor control.
- Render in `OnTopForward` or a command that executes in the post-temporal forward block.
- Restore stencil state after rendering if the command can draw gizmo materials.
- Preserve the stencil buffer contents until postprocess/TSR has sampled `StencilView`.
- Add or update a source-contract test when changing the stencil contract.

## Diagnostics

If gizmos or debug shapes introduce flicker, ghosting, missing Sponza surfaces, or lighting-dependent artifacts:

- Check whether disabling debug point/line rendering makes the artifact disappear.
- Check whether toggling forward pre-pass only hides the issue by rebuilding or resetting state.
- Inspect recent changes to `XRMaterial.ConfigureGizmoMaterial`, `VPRC_RenderDebugShapes`, `VPRC_RenderDebugGpuBvh`, and postprocess stencil sampling.
- Confirm the command that rendered the gizmo resets stencil test, mask, function, and operation before fullscreen passes.
- Confirm the stencil texture itself is not being cleared before `PostProcess.fs`, `PostProcessStereo.fs`, or `TemporalSuperResolution.fs` samples it.

Regression coverage for this contract lives in `XREngine.UnitTests/Rendering/GizmoStencilStateIsolationTests.cs`.

## Key Files

- `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugShapes.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugGpuBvh.cs`
- `XRENGINE/Scene/Components/Editing/TransformTool3D.cs`
- `XRENGINE/Scene/Physics/Physx/InstancedDebugVisualizer.cs`
- `Build/CommonAssets/Shaders/Scene3D/PostProcess.fs`
- `Build/CommonAssets/Shaders/Scene3D/PostProcessStereo.fs`
- `Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolution.fs`

## Related Documentation

- [Uber Shader Materials](uber-shader-materials.md)
- [OpenGL Program Linking](opengl-program-linking.md)
- [Default Render Pipeline Notes](../../architecture/rendering/default-render-pipeline-notes.md)

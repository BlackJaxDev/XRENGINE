# Default And Advanced Pipeline Validation

Scope: runtime, visual, capture, and build checks for `DefaultRenderPipeline`, `AdvancedRenderPipeline`, their post-process editor, and the forward-lighting shader path.

Architecture: [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md), [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Post-Process Editor Extension](../../../developer-guides/rendering/post-process-editor-extension.md)

Code todos: [Vulkan XR And Advanced Rendering TODO](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md)

## Imported Checks

### From default-render-pipeline-v2-todo.md

The source plan named the successor pipeline `DefaultRenderPipeline2`. That type is now `AdvancedRenderPipeline`.

- [ ] Compare `DefaultRenderPipeline` and `AdvancedRenderPipeline` with A/B screenshots across deferred, forward, MSAA, FXAA, TSR, transparency, exact transparency, bloom, motion blur, depth of field, light probes, and debug visualization. Record expected architecture differences.
- [ ] Take a RenderDoc capture of `AdvancedRenderPipeline`. Expected: every stage annotation is visible and correctly scoped.
- [ ] Enable exact transparency. Expected: PPLL and depth peeling render correctly.
- [ ] Expected: FXAA, weighted blended transparency, bloom, and the transparency debug overlays render correctly.
- [ ] Expected: motion blur, depth of field, TAA, and TSR render correctly.
- [ ] Put light probes in the scene. Expected: probe GI lighting renders correctly.
- [ ] Expected: the probe tetrahedra debug visualization still works.
- [ ] Toggle each debug visualization on and off at runtime. Expected: no frame hitch from a command-chain rebuild.
- [ ] Expected: tonemapping, color grading, bloom, fog, lens distortion, and chromatic aberration render correctly.
- [ ] Build the full solution. Expected: no new errors or warnings.
- [ ] Run the unit tests. Expected: no regressions.
- [ ] Validate `AdvancedRenderPipeline` on OpenGL, Vulkan, and VR across all resource-profile variants before promotion.

### From pipeline-driven-post-processing-and-camera-editor-todo.md

- [ ] Run `dotnet build XREngine.Editor/XREngine.Editor.csproj`, `dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj`, and `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter "FullyQualifiedName~Rendering"`.
- [ ] Select a scene node with a `CameraComponent`. Expected: the camera editor shows the target pipeline selector, and the first option is the active viewport pipeline.
- [ ] Select `AdvancedRenderPipeline` as the target. Expected: its stages are visible, and the Debug tab shows the shading debug view selector.
- [ ] Select `DefaultRenderPipeline` as the target. Expected: its stages are visible.
- [ ] Change values for one target pipeline. Expected: `CameraPostProcessStateCollection` keeps the values for that pipeline ID only.
- [ ] Expected: the `ShadingDebugView` selection affects the live viewport under `AdvancedRenderPipeline`.
- [ ] Drag a `TransformBase` or `SceneNode` into the depth of field focus target. Expected: focus tracking, undo, and redo work.
- [ ] Click "Rec.709" for the auto exposure luminance weights. Expected: normalization, undo, and redo work.
- [ ] Enable HDR output. Expected: the tonemapping stage shows the bypass notice and disables its controls.

### From forward-lighting-shader-optimizations-todo.md

- [ ] Start the editor (`Start-Editor-NoDebug`). Expected: shaders compile for all forward materials, including PBR, transparent, hair, and skin.
- [ ] Run `Test-SurfelGi` and the forward-lighting visual-regression scenes. Expected: no regressions.
- [ ] Capture GPU profiler traces for a scene with many shadow-casting local lights and active reflection probes. Compare the forward-lighting cost with an earlier trace.

### From runtime-modularization-phase4-todo.md and runtime-modularization-phase5-todo.md

- [ ] On supported NVIDIA hardware, run the Streamline vendor-upscale path on the modular runtime. Expected: the path initializes and renders, or reports an explicit unsupported diagnostic.

### From uber-shader-variant-builder-todo.md

Architecture: [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md)

- [ ] Build with `Build-Editor`. Open a scene that prepares many Uber shader variants. Compare `PrepareVariant` total time and allocations in `profiler-main-thread-invokes.log` with an earlier capture. Expected: no regression.

### From atmospheric-scattering-component-todo.md

Architecture: [Atmospheric Scattering Component](../../../developer-guides/components/atmospheric-scattering.md). Code todo: [Sky And Atmosphere Follow-Ups TODO](../../todo/rendering/sky-and-atmosphere-followups-todo.md).

- [ ] Capture screenshots of the current skybox at noon, sunset, and night, before and after the atmosphere is enabled.
- [ ] Enable the atmosphere and a volumetric fog volume. Expected: the atmosphere composites before the fog.
- [ ] Run the Unit Testing World with `InitializeAtmosphericScattering` enabled. Expected: the default atmosphere renders.
- [ ] Put the camera at ground level, at high altitude, and in space. Set the sun to noon, sunset, and below the horizon. Expected: each case renders correctly.
- [ ] Expected: the skybox texture, gradient, solid-color, and dynamic-procedural modes still render correctly.
- [ ] Capture GPU profiler timings for the sky, half-depth, half-scatter, reprojection, upscale, and final composite passes.
- [ ] Read the runtime logs. Expected: no new OpenGL, shader, FBO, or texture warnings.
- [ ] Validate OpenVR two-pass stereo with the atmosphere enabled.
- [ ] Validate OpenVR and OpenXR single-pass stereo with the atmosphere enabled, when the stereo variants exist.

### From 00-advanced-render-pipeline-refactor-todo.md

Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md).

- [ ] Validate `AdvancedRenderPipeline` as the production default for desktop and validated offscreen profiles, and validate the selected OpenXR eye family for each eye profile.
- [ ] Require the Advanced mode and render a scene with an unsupported opaque material. Expected: an observable error material or a selection failure with a rejection reason. No silent classic opaque path.
- [ ] Capture a warmed production frame. Expected: no same-frame GPU readback and no managed per-frame allocations.
- [ ] After the classic GBuffer removal, capture the Advanced resource list. Expected: no GBuffer, deferred light accumulation, or light-combine resources outside debug modes.

# Rendering

[Back to user guide](README.md)

Use this page when you want to configure the renderer, choose a pipeline, or troubleshoot what appears in the viewport. For the engine internals, see [Rendering Runtime Overview](../architecture/rendering/runtime-overview.md).

## Choosing The Renderer

OpenGL 4.6 is the primary supported renderer. Vulkan and DX12 paths are work in progress and should be treated as development targets unless a task explicitly validates them.

Use the renderer setting from user or project settings when launching the editor or game. If a rendering issue appears only under Vulkan or DX12, reproduce it under OpenGL first so you can separate backend parity issues from feature logic.

## Render Pipelines

Camera components render through a `RenderPipeline` instance. The default pipeline handles depth, deferred and forward lighting, transparency, post-processing, UI composition, and debug overlays.

Common user-facing controls live on camera post-process stages:

- tonemapping, color grading, bloom, vignette, and motion blur under imaging stages,
- volumetric fog and atmosphere stages,
- shadow and lighting options through light and pipeline settings,
- render scale and target resolution through viewport or pipeline settings.

For custom pipeline implementation details, see [Render Graph Migration](../architecture/rendering/render-graph-migration.md) and [Default Render Pipeline Notes](../architecture/rendering/default-render-pipeline-notes.md).

## Mesh Submission

Most projects should use the effective mesh submission strategy selected by engine settings and renderer capability checks. Override it only for debugging or performance triage.

Useful references:

- [Mesh Submission Strategies](../architecture/rendering/mesh-submission-strategies.md)
- [Skinning Developer Guide](../developer-guides/rendering/skinning.md)
- [Blendshaping Developer Guide](../developer-guides/rendering/blendshaping.md)

## Visual Debugging

Use editor debug toggles for bounds, transform lines, physics visualization, render-pass inspection, and GPU-driven diagnostics. When a rendering issue is visually observable, prefer a tight editor iteration loop: build, launch the Unit Testing World, capture screenshots, inspect logs, then change one variable.

Rendering logs are written under `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`.

## Deeper Docs

- [Rendering Runtime Overview](../architecture/rendering/runtime-overview.md)
- [OpenGL Renderer](../architecture/rendering/opengl-renderer.md)
- [Vulkan Renderer](../architecture/rendering/vulkan-renderer.md)
- [OpenVR Rendering](../architecture/rendering/openvr-rendering.md)
- [OpenXR VR Rendering](../architecture/rendering/openxr-vr-rendering.md)

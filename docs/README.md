# Documentation Index

Start here for XRENGINE documentation. The main handwritten docs are split by audience:

- [Architecture](architecture/README.md): engine internals, subsystem boundaries, lifecycle, data flow, invariants, and design tradeoffs.
- [Developer Guides](developer-guides/README.md): code-facing guides for implemented features, extension points, diagnostics, tests, and implementation references.
- [User Guide](user-guide/README.md): surface-level engine concepts, editor-facing settings, workflows, and common usage.
- [Legal and Licensing](../LEGAL/README.md): license terms, commercial
  information, contribution terms, and release guidance.
- [Work Docs](work/README.md): active design docs, TODOs, audits, testing notes, and historical implementation plans.

## Architecture

- [Architecture Overview](architecture/README.md)
- [Getting Started In The Codebase](architecture/getting-started-in-codebase.md)
- [Rendering Architecture](architecture/rendering/README.md)
- [Rendering Runtime Overview](architecture/rendering/runtime-overview.md)
- [Frame Lifecycle And Dispatch Paths](architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Mesh Submission Strategies](architecture/rendering/mesh-submission-strategies.md)
- [Renderer Backend Hot Reload](architecture/rendering/renderer-backend-hot-reload.md)
- [CPU Scene BVH](architecture/rendering/cpu-scene-bvh.md)
- [GPU Scene BVH](architecture/rendering/gpu-scene-bvh.md)
- [Scene Architecture](architecture/scene/overview.md)
- [Transform Architecture](architecture/scene/transforms.md)
- [Physics Architecture](architecture/physics/overview.md)
- [Audio Architecture](architecture/audio/audio-architecture.md)
- [Networking Overview](architecture/networking/overview.md)
- [Control Plane Runtime Architecture](architecture/runtime/control-plane.md)
- [Modeling XRMesh Editing](architecture/modeling/xrmesh-editing.md)
- [Play Mode Architecture](architecture/editor/play-mode-architecture.md)
- [Editor Undo System](architecture/editor/undo-system.md)

## Developer Guides

- [Continuous Integration And Releases](developer-guides/ci-cd.md)
- [MCP Server Implementation](developer-guides/ai/mcp-server.md)
- [MCP Assistant](developer-guides/ai/mcp-assistant.md)
- [Local Agent Broker Implementation](developer-guides/ai/local-agent-broker.md)
- [Animation API](developer-guides/animation/animation-api.md)
- [Model Import](developer-guides/assets/model-import.md)
- [Native FBX Import And Export](developer-guides/assets/native-fbx-import-export.md)
- [OpenAL Streaming Audio](developer-guides/audio/openal-streaming-audio.md)
- [Component API](developer-guides/components/component-api.md)
- [Atmospheric Scattering Component](developer-guides/components/atmospheric-scattering.md)
- [Global Illumination](developer-guides/gi/global-illumination.md)
- [Networking](developer-guides/networking/networking.md)
- [Control Plane](developer-guides/networking/control-plane.md)
- [Physics API](developer-guides/physics/physics-api.md)
- [Scene Graph Developer Guide](developer-guides/scene/scene-graph.md)
- [Engine API](developer-guides/runtime/engine-api.md)
- [Runtime Environment Settings](developer-guides/runtime/runtime-environment-settings.md)
- [Hot-Path Memory Control](developer-guides/runtime/hot-path-memory.md)
- [Job System](developer-guides/runtime/job-system.md)
- [Profiler](developer-guides/diagnostics/profiler.md)
- [Self-Iterating Rendering Performance Loop](developer-guides/diagnostics/self-iterating-performance-loop.md)
- [Skinning](developer-guides/rendering/skinning.md)
- [Blendshaping](developer-guides/rendering/blendshaping.md)
- [Poiyomi Toon Material Conversion](developer-guides/rendering/poiyomi-toon-material-conversion.md)
- [Maintaining Poiyomi Toon Support](developer-guides/rendering/poiyomi-toon-maintenance.md)
- [Vulkan OBS Hook Compatibility](developer-guides/rendering/vulkan-obs-hook-compatibility.md)
- [Surface Detail And Forward Shadows](developer-guides/rendering/shadows/surface-detail-forward-shadows.md)
- [OpenXR Runtime](developer-guides/vr/openxr-runtime.md)
- [VR Developer Guide](developer-guides/vr/vr-development.md)

## User Guide

- [Engine](user-guide/engine.md)
- [MCP Server And Assistant](user-guide/ai/mcp-server.md)
- [Local Agent Broker](user-guide/ai/local-agent-broker.md)
- [Scene System](user-guide/scene.md)
- [Component System](user-guide/components.md)
- [Transforms](user-guide/transforms.md)
- [Animation](user-guide/animation.md)
- [Physics](user-guide/physics.md)
- [Rendering](user-guide/rendering.md)
- [VR Development](user-guide/vr-development.md)
- [Shader Editor](user-guide/editor/shader-editor.md)
- [Prefab Workflow](user-guide/prefab-workflow.md)
- [Job System](user-guide/job-system.md)

## Work Docs

- [Work Docs Index](work/README.md)
- [Apple Platform and MoltenVK Support Design](work/design/platform/apple-platform-moltenvk-support-design.md)
- [Runtime Modularization Plan](work/design/runtime-modularization-plan.md)
- [Texture Runtime, Streaming, And Virtual Texturing Design](work/design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Transparency And OIT Implementation Plan](work/design/rendering/transparency-and-oit-implementation-plan.md)
- [GPU Softbody Mesh Rigging Plan](work/design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md)
- [Production Rendering Pipeline Roadmap](work/todo/rendering/gpu/production-rendering-pipeline-roadmap.md)

## Generated API Reference

The DocFX project lives in `docs/docfx/docfx.json` and targets `XRENGINE.sln` to produce browsable API reference from XML doc comments.

Build the site:

```powershell
dotnet tool restore
dotnet docfx docs/docfx/docfx.json
```

Preview locally:

```powershell
dotnet docfx docs/docfx/docfx.json --serve --port 8080
```

Generated output stays in `docs/docfx/_site`, which is ignored by Git.

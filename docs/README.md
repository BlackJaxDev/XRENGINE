# Documentation Index

Start here for XRENGINE documentation. The main handwritten docs are split by audience:

- [Architecture](architecture/README.md): engine internals, subsystem boundaries, lifecycle, data flow, invariants, and design tradeoffs.
- [Developer Guides](developer-guides/README.md): code-facing guides for implemented features, extension points, diagnostics, tests, and implementation references.
- [User Guide](user-guide/README.md): surface-level engine concepts, editor-facing settings, workflows, and common usage.
- [Legal and Licensing](../LEGAL/README.md): license terms, commercial
  information, contribution terms, and release guidance.

[Runtime Project Organization](architecture/runtime/project-organization.md) maps the shared `net10.0` libraries, native modules, application composition, and asset ownership. [Portable Project Rules](developer-guides/runtime/portable-projects.md) describes the package/source policies and browser compile lane.

## Architecture

- [Architecture Overview](architecture/README.md)
- [Getting Started In The Codebase](architecture/getting-started-in-codebase.md)
- [Runtime Project Organization](architecture/runtime/project-organization.md)
- [Rendering Architecture](architecture/rendering/README.md)
- [Rendering Runtime Overview](architecture/rendering/runtime-overview.md)
- [Advanced TSR Sampling, History, And Diagnostics](architecture/rendering/default-render-pipeline-notes.md#advanced-tsr-sample-and-history-contract)
- [Frame Lifecycle And Dispatch Paths](architecture/rendering/frame-lifecycle-and-dispatch-paths.md), including ordered snapshot-input edges and callback ownership
- [Mesh Submission Strategies](architecture/rendering/mesh-submission-strategies.md)
- [WebGPU Sky Backgrounds](architecture/rendering/webgpu-sky-background.md)

- [Vulkan Scene Preparation And Publication](architecture/rendering/vulkan-scene-preparation-and-publication.md)
- [Renderer Backend Hot Reload](architecture/rendering/renderer-backend-hot-reload.md)
- [Texture Streaming](architecture/rendering/texture-streaming.md)
- [Advanced Render Pipeline](architecture/rendering/advanced-render-pipeline.md)
- [Shadow Atlas](architecture/rendering/shadow-atlas.md)
- [Transparency And OIT](architecture/rendering/transparency-and-oit.md)
- [GPU Hi-Z Occlusion Culling](architecture/rendering/gpu-hiz-occlusion-culling.md)
- [CPU Software Occlusion](architecture/rendering/cpu-software-occlusion.md)
- [CPU Memory Ownership](architecture/rendering/cpu-memory-ownership.md)
- [Vulkan Memory Allocation](architecture/rendering/vulkan-memory-allocation.md)
- [OpenVR VRClient GPU Handoff](architecture/rendering/openvr-vrclient-gpu-handoff.md)
- [Cooked Texture Payloads](architecture/assets/cooked-texture-payloads.md)
- [Vulkan Pipeline Compilation, Readiness And Depth-Only Coverage](architecture/rendering/vulkan-pipeline-compilation.md)
- [CPU Scene BVH](architecture/rendering/cpu-scene-bvh.md)
- [GPU Scene BVH](architecture/rendering/gpu-scene-bvh.md)
- [Scene Architecture](architecture/scene/overview.md)
- [Transform Architecture](architecture/scene/transforms.md)
- [Physics Architecture](architecture/physics/overview.md)
- [Physics Debug Frame](architecture/physics/physics-debug-frame.md)
- [Physics Chain World Runtime](architecture/physics/physics-chain-world-runtime.md)
- [Audio Architecture](architecture/audio/audio-architecture.md)
- [Networking Overview](architecture/networking/overview.md)
- [Control Plane Runtime Architecture](architecture/runtime/control-plane.md)
- [Portable Engine Host](architecture/runtime/portable-engine-host.md)
- [Modeling XRMesh Editing](architecture/modeling/xrmesh-editing.md)
- [Play Mode Architecture](architecture/editor/play-mode-architecture.md)
- [Editor Undo System](architecture/editor/undo-system.md)
- [Editor Background Preparation](architecture/editor/background-preparation.md)

## Developer Guides

- [Software Vulkan Correctness Validation](developer-guides/testing/software-vulkan-validation.md)
- [Test Suite Layout](developer-guides/testing/test-suite-layout.md)
- [Work Doc Templates](developer-guides/ai/work-doc-templates.md)
- [Continuous Integration And Releases](developer-guides/ci-cd.md)
- [Windows CI Test Coverage](developer-guides/testing/windows-ci.md)
- [MCP Server Implementation](developer-guides/ai/mcp-server.md)
- [MCP Assistant](developer-guides/ai/mcp-assistant.md)
- [Local Agent Broker Implementation](developer-guides/ai/local-agent-broker.md)
- [Animation API](developer-guides/animation/animation-api.md)
- [Model Import](developer-guides/assets/model-import.md): import backends, warning logs, and missing-texture reporting after import.
- [Native FBX Import And Export](developer-guides/assets/native-fbx-import-export.md)
- [OpenAL Streaming Audio](developer-guides/audio/openal-streaming-audio.md)
- [Component API](developer-guides/components/component-api.md)
- [Atmospheric Scattering Component](developer-guides/components/atmospheric-scattering.md)
- [World and Procedural Skybox Ambient Lighting](developer-guides/components/procedural-skybox-ambient.md)
- [Global Illumination](developer-guides/gi/global-illumination.md)
- [Networking](developer-guides/networking/networking.md)
- [Browser Realtime Transport](developer-guides/networking/browser-realtime.md)
- [Control Plane](developer-guides/networking/control-plane.md)
- [Physics API](developer-guides/physics/physics-api.md)
- [Scene Graph Developer Guide](developer-guides/scene/scene-graph.md)
- [Engine API](developer-guides/runtime/engine-api.md)
- [Portable Project Rules](developer-guides/runtime/portable-projects.md)
- [Runtime Environment Settings](developer-guides/runtime/runtime-environment-settings.md)
- [Hot-Path Memory Control](developer-guides/runtime/hot-path-memory.md)
- [Job System](developer-guides/runtime/job-system.md)
- [Profiler](developer-guides/diagnostics/profiler.md)
- [Dedicated Vulkan RenderBench](developer-guides/diagnostics/profiler.md#dedicated-vulkan-renderbench)
- [Runtime Data Layout Measurements](developer-guides/diagnostics/runtime-data-layout-measurements.md)
- [Self-Iterating Rendering Performance Loop](developer-guides/diagnostics/self-iterating-performance-loop.md)
- [Skinning](developer-guides/rendering/skinning.md)
- [Blendshaping](developer-guides/rendering/blendshaping.md)
- [Poiyomi Toon Material Conversion](developer-guides/rendering/poiyomi-toon-material-conversion.md)
- [Maintaining Poiyomi Toon Support](developer-guides/rendering/poiyomi-toon-maintenance.md)
- [Vulkan OBS Hook Compatibility](developer-guides/rendering/vulkan-obs-hook-compatibility.md)
- [Surface Detail And Forward Shadows](developer-guides/rendering/shadows/surface-detail-forward-shadows.md)
- [OpenXR Runtime](developer-guides/vr/openxr-runtime.md)
- [OpenXR Body Tracking and Spectator Integration](developer-guides/vr/openxr-body-tracking.md)
- [Full-Body VR Calibration](developer-guides/vr/full-body-calibration.md)
- [VR Body Measurements](developer-guides/vr/body-measurements.md)
- [VR Spectator Camera](developer-guides/vr/spectator-camera.md)
- [VR Calibration Session Continuity](developer-guides/vr/calibration-session-continuity.md)
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
- [Full-body VR calibration](user-guide/vr/full-body-calibration.md)
- [Shader Editor](user-guide/editor/shader-editor.md)
- [Prefab Workflow](user-guide/prefab-workflow.md)
- [Job System](user-guide/job-system.md)

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

## Browser Runtime

[Shared native/browser world packages](developer-guides/networking/browser-shared-world-package.md)
documents the opt-in publisher contract that binds original native and cooked
browser representations under one verified managed-admission identity.

[Browser static hosting](developer-guides/runtime/browser-static-hosting.md) documents production HTTPS, MIME, compression, cache, CSP and conditional cross-origin isolation requirements. It is deployment guidance, not evidence that a production host or physical device was tested.

[WebGPU indirect submission](architecture/rendering/webgpu-indirect-submission.md) records GPU-written argument/count contracts, completion ownership, and the distinction between native Advanced compute meshlets and hardware task/mesh stages.

# Documentation Index

Start here for XRENGINE documentation. The main handwritten docs are split by audience:

- [Architecture](architecture/README.md): engine internals, subsystem boundaries, lifecycle, data flow, invariants, and design tradeoffs.
- [Developer Guides](developer-guides/README.md): code-facing guides for implemented features, extension points, diagnostics, tests, and implementation references.
- [User Guide](user-guide/README.md): surface-level engine concepts, editor-facing settings, workflows, and common usage.
- [Legal and Licensing](../LEGAL/README.md): license terms, commercial
  information, contribution terms, and release guidance.
- [Work Docs](work/README.md): active design docs, TODOs, audits, testing notes, and historical implementation plans.

[Runtime Project Organization](architecture/runtime/project-organization.md) maps the shared `net10.0` libraries, native modules, application composition, and asset ownership. [Portable Project Rules](developer-guides/runtime/portable-projects.md) describes the package/source policies and browser compile lane. Outstanding integration acceptance is tracked in [Platform Validation](work/testing/platform/platform-validation.md); completed local browser, desktop and native callback smokes, full test execution and qualification limits are recorded in the [reference harness investigation](work/investigations/rendering/desktop-browser-reference-harness.md).

## Architecture

- [Architecture Overview](architecture/README.md)
- [Getting Started In The Codebase](architecture/getting-started-in-codebase.md)
- [Runtime Project Organization](architecture/runtime/project-organization.md)
- [Rendering Architecture](architecture/rendering/README.md)
- [Rendering Runtime Overview](architecture/rendering/runtime-overview.md)
- [Advanced TSR Sampling, History, And Diagnostics](architecture/rendering/default-render-pipeline-notes.md#advanced-tsr-sample-and-history-contract)
- [Frame Lifecycle And Dispatch Paths](architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Mesh Submission Strategies](architecture/rendering/mesh-submission-strategies.md)
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
- [Physics Chain Steady-State CPU Design](work/design/physics/physics-chain-steady-state-cpu-design.md)
- [Skinned GPU Chain Benchmark Investigation](work/investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md)
- [Distance Cadence and GPU Pose Presentation Proposal](work/design/physics/distance-cadence-gpu-presentation.md)
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
- [Windows CI Build Preparation Investigation](work/investigations/platform/windows-ci-build-preparation-2026-10-05.md)
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
- [Runtime Regression And NativeAOT Hardening](work/todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md): current software regression and strict packaged-player acceptance, with an active [progress ledger](work/progress/runtime/runtime-regression-and-nativeaot-hardening-progress.md).
- [Self-Iterating Rendering Performance Loop](developer-guides/diagnostics/self-iterating-performance-loop.md)
- [Skinning](developer-guides/rendering/skinning.md)
- [Blendshaping](developer-guides/rendering/blendshaping.md)
- [Poiyomi Toon Material Conversion](developer-guides/rendering/poiyomi-toon-material-conversion.md)
- [Jax2031 Unity And Advanced Vulkan Parity](work/investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
- [Unity Prefab Parity Import TODO](work/todo/assets/unity-prefab-parity-import-todo.md)
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

## Work Docs

- [Work Docs Index](work/README.md)
- [Editor OpenXR Toggle, Rendering, And Import Responsiveness Todo](work/todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md)
- [Six-Device VR Calibration Baseline](work/investigations/avatar/vr-calibration-baseline-2026-09-24.md)
- [OpenXR Full-body Implementation Record](work/progress/avatar/openxr-full-body-calibration-spectator-implementation.md)
- [OpenXR Calibration and Spectator Implementation](work/progress/avatar/openxr-calibration-spectator-implementation-2026-09-30.md)
- [Avatar Validation: OpenXR Calibration and Spectator](work/testing/avatar/avatar-validation.md#openxr-body-calibration-and-spectator)
- [Windows Calibration and Spectator Validation](work/investigations/avatar/openxr-calibration-spectator-validation-2026-10-01.md)
- [Animation and IK Stability](work/investigations/avatar/animation-ik-stability-2026-09-30.md)
- [Tracked Body Solver Validation](work/investigations/avatar/tracked-body-solver-2026-09-30.md)
- [Spectator and Calibration Feedback Validation](work/investigations/avatar/spectator-validation-2026-09-30.md)
- [Vulkan Core Validation](work/testing/rendering/vulkan-core-validation.md)
- [Vulkan Stall Separate Findings](work/todo/rendering/vulkan-stall-separate-findings-todo.md)
- [Vulkan Stall Remediation Results](work/progress/rendering/vulkan-stall-remediation-results.md)
- [Control Plane Managed Server Instances and Client Synchronization Todo](work/todo/networking/control-plane-managed-server-instances-todo.md)
- [Apple Platform and MoltenVK Support Design](work/design/platform/apple-platform-moltenvk-support-design.md)
- [Runtime Modularization Plan](work/design/runtime-modularization-plan.md)
- [Texture Runtime, Streaming, And Virtual Texturing Design](work/design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Transparency And OIT Implementation Plan](work/design/rendering/transparency-and-oit-implementation-plan.md)
- [Exact VR Pipeline Selection](work/design/rendering/vr-pipeline-selection-design.md)
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

## Browser runtime planning

[Portable engine host ownership](work/progress/platform/portable-engine-host-ownership.md) records the shared facade, timer, services and desktop composition boundary. The [host validation record](work/investigations/platform/portable-engine-host-validation.md) contains the desktop/browser build gate, Editor camera/UI restore and Server/VRClient startup evidence, including current limitations. Real browser-world startup remains active in the [unified runtime checklist](work/todo/platform/unified-desktop-browser-runtime-todo.md).

[Portable browser engine boot qualification](work/progress/platform/portable-browser-engine-boot.md) records partial real asset round-trips and the external asset/native binding boundaries. The unified runtime effort is paused at the owner's request, with its resume order recorded in the checklist.

[Browser project publishing](work/progress/rendering/browser-project-publishing.md) connects the existing editor/CLI build flow to saved-world export, a shared content packager and a complete static browser bundle. It documents admitted native asset coverage and the remaining authoring and validation gaps.

[Browser compute reuse audit and integration](work/progress/rendering/browser-compute-reuse-audit.md) maps existing skinning, GPU scene and Hi-Z contracts to their WebGPU backend implementations, experimental controls and deferred qualification. Canonical palette/bounds types are shared; no second production animator or scene database is introduced.

[Browser compute and indirect commands](work/progress/rendering/browser-compute-indirect.md) records device-limited compute, indirect draw submission, usage-scope checks, opt-in offscreen references and honest scene strategy selection. Runtime and mobile performance acceptance remain deferred.

[Browser interaction and runtime services](work/progress/rendering/browser-interactive-services.md) records touch/IME/gamepad input, CPU skeletal animation, bounded character collision, Web Audio and required-service declarations. Runtime acceptance remains deferred.

[Browser cooked content delivery](work/progress/rendering/browser-cooked-content.md) records the offline packager, hash-addressed manifest, asynchronous budgeted engine uploads, texture variants and optional HTTP caching policy. Source implementation remains unvalidated.

[Focused browser forward pipeline](work/progress/rendering/browser-focused-pipeline.md) records CPU-direct opaque/masked/transparent rendering, bounded material bindings, directional shadows, HDR/SDR composition, engine UI and mobile quality settings. Runtime acceptance remains deferred.

[WebGPU module and engine snapshot bridge](work/progress/rendering/browser-webgpu-module-assets.md) records real catalog registration, module-owned browser assets and static XRMesh/material/camera export and import. Source implementation remains unvalidated.

[Cooked browser shader artifacts](work/progress/rendering/browser-shader-artifacts.md) records target-tagged shader results, deterministic WGSL packaging, integrity/layout checks and requirement-aware startup. The TODO now separately checks off completed code and pending acceptance.

[Browser mesh and packet bridge](work/progress/rendering/browser-mesh-packet-bridge.md) records indexed geometry, textured unlit materials, depth testing, generation-stamped resources and batched submissions. Build and runtime validation remain deferred.

[Browser WebGPU canvas host](work/progress/rendering/browser-webgpu-canvas-host.md) records the new canvas, startup, scheduling and diagnostic draw implementation. Validation was explicitly deferred for this change.

[Portable browser scene boot](work/progress/rendering/portable-browser-scene-boot.md) records the implemented shared runtime, browser validation, build instructions and remaining rendering work.

[Mobile browser readiness](work/progress/rendering/mobile-browser-readiness.md) records the source dependency audit, sample contract, budgets and unvalidated device matrix for the [WebGPU work in the unified runtime TODO](work/todo/platform/unified-desktop-browser-runtime-todo.md).


[Canvas output, visibility and live resource updates](work/progress/rendering/browser-webgpu-frame-output.md) records portable output metadata, the explicit canvas pass, per-view culling and resource replacement. Source implementation remains unvalidated.

[Portable browser host implementation](work/progress/rendering/browser-portable-host-completion.md) records generated registration, portability/API guards, focused renderer capabilities and explicit frame publication. Code completion is separate from deferred acceptance evidence.

[Batched browser upload bridge](work/progress/rendering/browser-upload-bridge.md) records reusable upload arenas, typed resource updates, ownership/overflow rules and bridge diagnostics. Exact-runtime lifetime and performance acceptance remains deferred.

[Browser shader cooking and material generation](work/progress/rendering/browser-shader-cooking.md) records the C# cooker, typed WGSL generation, optional pinned Slang route, coordinate/layout contracts and bounded startup. Compiler/layout qualification and runtime acceptance remain deferred.

[Browser GPU resources and ordered submission](work/progress/rendering/browser-gpu-resources-submission.md) records selected-device capabilities, buffers and texture subresources, framebuffer lowering, bounded pipeline caches, reusable commands and cancellable readback. Runtime acceptance remains deferred.

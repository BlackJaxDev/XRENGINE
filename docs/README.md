# Documentation Index

A map of the XRENGINE docs. Start here to explore architecture overviews, API guides, rendering notes, and open backlogs.

## Architecture
- [Architecture Overview](architecture/README.md)
- [Getting Started In The Codebase](architecture/getting-started-in-codebase.md)
- [GPU-Driven Animation Architecture](work/design/gpu-driven-animation.md)
- [Modeling XRMesh Editing Architecture](architecture/modeling-xrmesh-editing.md)
- [Play Mode Architecture](architecture/play-mode-architecture.md)
- [Load Balancer](architecture/load-balancer.md)
- [Job System](api/job-system.md)
- [Networking Overview](architecture/networking-overview.md)
- [Editor Undo System](architecture/undo-system.md)

## API Guides
- [Engine API](api/engine.md)
- [Scene System](api/scene.md)
- [Component System](api/components.md)
- [Transform Architecture](api/transforms.md)
- [Animation System](api/animation.md)
- [Physics Architecture](api/physics.md)
- [Rendering Architecture](api/rendering.md)
- [VR Development](api/vr-development.md)
- [Prefab Workflow](api/prefab-workflow.md)

## Rendering
- [Default Render Pipeline Notes (Known Issues & Invariants)](architecture/rendering/default-render-pipeline-notes.md)
- [Rendering Frame Lifecycle And Dispatch Paths](architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Uber Shader Varianting](architecture/rendering/uber-shader-varianting.md)
- [CoACD Integration](architecture/CoACD.md)
- [GPU Render Pass Pipeline](work/design/gpu-render-pass-pipeline.md)
- [DDGI Integration Plan](work/design/ddgi-integration-plan.md)
- [VSM And EVSM Shadow Filtering Plan](work/design/shadow-filtering-vsm-evsm-plan.md)
- [Dynamic Shadow Atlas And LOD Allocation Plan](work/design/dynamic-shadow-atlas-lod-plan.md)
- [Post-v1 Advanced Shadow Features Plan](work/design/post-v1-advanced-shadow-features-plan.md)
- [Bindless Deferred Texturing Plan](work/design/bindless-deferred-texturing-plan.md)
- [Transparency And OIT Implementation Plan](work/design/transparency-and-oit-implementation-plan.md)
- [Light Volumes](features/gi/light-volumes.md)
- [Secondary GPU Context](architecture/secondary-gpu-context.md)

## Physics and Simulation
- [Physics Chain Performance](features/physics-chain-performance.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](work/design/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [GPU Softbody Mesh Rigging Plan](work/design/gpu-softbody-mesh-rigging-plan.md)

## Features
- [Audio2Face-3D Component](features/audio2face-3d.md)
- [Audio2Face-3D Engine Setup](features/audio2face-3d-engine-setup.md)
- [Bootstrap And First-Time Setup](features/bootstrap.md)
- [Default Render Pipeline Script Export](features/default-render-pipeline.xrs)
- [Ambient Occlusion](features/gi/ambient-occlusion.md)
- [Model Import](features/model-import.md)
- [Native Dependencies](features/native-dependencies.md)
- [Networking](features/networking.md)
- [Octahedral Billboard Capture](features/octahedral-billboard-capture.md)
- [ImGui Shader Editor](features/shader-editor.md)
- [Steam Audio Integration](features/steam-audio.md)
- [Unity Conversion Integrations](features/unity-conversion-integrations.md)
- [Uber Shader Materials](features/uber-shader-materials.md)
- [Vulkan Upscale Bridge](features/vulkan-upscale-bridge.md)
- [Physics Chain Performance](features/physics-chain-performance.md)
- [Tick-Based Animation Timing](features/tick-based-animation-timing.md)
- [Unit Testing World](features/unit-testing-world.md)

## Work Docs (WIP / TODO / Design)
- [Work Docs Index](work/README.md)
- Active TODOs and design docs are tracked from [Work Docs Index](work/README.md).
- Representative design docs: [GPU-Driven Animation Architecture](work/design/gpu-driven-animation.md), [Affine Matrix Integration Plan](work/design/affine-matrix-integration-plan.md), [Bindless Deferred Texturing Plan](work/design/bindless-deferred-texturing-plan.md), [Transparency And OIT Implementation Plan](work/design/transparency-and-oit-implementation-plan.md), [GPU Softbody Mesh Rigging Plan](work/design/gpu-softbody-mesh-rigging-plan.md), [Runtime Modularization And Bootstrap Extraction Plan](work/design/runtime-modularization-plan.md)
- Representative TODOs: [GPU-Driven Animation TODO](work/todo/gpu-driven-animation-todo.md)

## Tips
- Most API docs cross-link to related systems. Use "Related Documentation" sections at the bottom of each page for deeper dives.
- Many backlog files list outstanding tasks; check them before starting major refactors.

## Generated API Reference
- The DocFX project lives in `docs/docfx/docfx.json` and targets `XRENGINE.slnx` to produce a browsable API site from XML doc comments.
- Build the site: `dotnet tool restore` then `dotnet docfx docs/docfx/docfx.json`. Output stays in `docs/docfx/_site` (ignored by git).
- Preview locally: `dotnet docfx docs/docfx/docfx.json --serve --port 8080` and open the served URL.

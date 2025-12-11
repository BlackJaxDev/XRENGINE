# Documentation Index

A map of the XRENGINE docs. Start here to explore architecture overviews, API guides, rendering notes, and open backlogs.

## Architecture
- [Architecture Overview](architecture/README.md)
- [Play Mode Architecture](architecture/play-mode-architecture.md)
- [Load Balancer](architecture/load-balancer.md)
- [Job System](architecture/job-system.md)
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
- [CoACD Integration](rendering/CoACD.md)
- [GPU Render Pass Pipeline](rendering/gpu-render-pass-pipeline.md)
- [Light Volumes](rendering/light-volumes.md)
- [Secondary GPU Context](rendering/secondary-gpu-context.md)

## Physics and Simulation
- [GPU Physics Chain Compatibility](work/design/gpu-physics-chain-compatibility.md)
- [GPU Physics Chain Engine Verification](work/design/gpu-physics-chain-engine-verification.md)

## Work Docs (WIP / TODO / Design)
- [Work Docs Index](work/README.md)
- Backlog: [Project TODO](work/backlog/todo.md), [Warnings Remediation Checklist](work/backlog/warnings-todo.md), [Vulkan TODO](work/backlog/VULKAN_TODO.md), [Indirect GPU Rendering TODO](work/backlog/IndirectGPURendering-TODO.md)
- Design: [Indirect GPU Rendering Architecture](work/design/IndirectGPURendering-Design.md), [Vulkan Parity Report](work/design/vulkan-parity-report.md), [Vulkan Command Buffer Refactor](work/design/vulkan-command-buffer-refactor.md), [GPU Physics Chain Compatibility](work/design/gpu-physics-chain-compatibility.md), [GPU Physics Chain Engine Verification](work/design/gpu-physics-chain-engine-verification.md)

## Tips
- Most API docs cross-link to related systems. Use "Related Documentation" sections at the bottom of each page for deeper dives.
- Many backlog files list outstanding tasks; check them before starting major refactors.

## Generated API Reference
- The DocFX project lives in `docs/docfx/docfx.json` and targets `XRENGINE.sln` to produce a browsable API site from XML doc comments.
- Build the site: `dotnet tool restore` then `dotnet docfx docs/docfx/docfx.json`. Output stays in `docs/docfx/_site` (ignored by git).
- Preview locally: `dotnet docfx docs/docfx/docfx.json --serve --port 8080` and open the served URL.

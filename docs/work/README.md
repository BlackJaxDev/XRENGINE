# Work Docs

In-flight design notes, investigations, and backlogs. These change frequently; prefer stable overviews in the main docs index when onboarding.

[← Docs index](../README.md)

## Backlog & Checklists
- [Project TODO](backlog/todo.md)
- [Warnings Remediation Checklist](backlog/warnings-todo.md)
- [Vulkan TODO](backlog/VULKAN_TODO.md)
- [Vulkan CPU Octree + Screen UI DAG TODO](backlog/vulkan-cpu-octree-dag-todo.md)
- [Indirect GPU Rendering TODO](backlog/IndirectGPURendering-TODO.md)
- [AOT TODO For Final Game Builds](todo/aot-final-game-builds.md)
- [Affine Matrix Integration TODO](todo/affine-matrix-integration-todo.md)
- [GPU Softbody Mesh Rigging TODO](todo/gpu-softbody-mesh-rigging-todo.md)
- [HBAO And HBAO+ Implementation TODO](todo/hbao-hbao-plus-implementation-todo.md)
- [Non-HBAO AO Remediation TODO](todo/non-hbao-ambient-occlusion-remediation.md)
- [Runtime Modularization Phase 2 TODO](todo/runtime-modularization-phase2-todo.md)
- [Voxel Cone Tracing And VXAO Implementation TODO](todo/voxel-cone-tracing-and-vxao-implementation-todo.md)
- [Transparency And OIT Implementation TODO](todo/transparency-and-oit-todo.md)

## Design & Investigations
- [Indirect GPU Rendering Architecture](design/IndirectGPURendering-Design.md)
- [Affine Matrix Integration Plan](design/affine-matrix-integration-plan.md)
- [Affine Matrix Phase 3 Validation - 2026-03-19](audit/affine-matrix-phase3-validation-2026-03-19.md)
- [Zero-Readback GPU-Driven Rendering Plan](design/zero-readback-gpu-driven-rendering-plan.md)
- [Bindless Deferred Texturing Plan](design/bindless-deferred-texturing-plan.md)
- [Vulkan Parity Report](design/vulkan-parity-report.md)
- [Vulkan Command Buffer Refactor](design/vulkan-command-buffer-refactor.md)
- [Transparency And OIT Implementation Plan](design/transparency-and-oit-implementation-plan.md)
- [Surface Detail Import And Forward Shadow Debugging](design/surface-detail-and-forward-shadow-debugging.md)
- [GPU Physics Chain Compatibility](design/gpu-physics-chain-compatibility.md)
- [GPU Physics Chain Engine Verification](design/gpu-physics-chain-engine-verification.md)
- [GPU Softbody Mesh Rigging Plan](design/gpu-softbody-mesh-rigging-plan.md)
- [HBAO And HBAO+ Implementation Plan](design/hbao-hbao-plus-implementation-plan.md)
- [GTAO Implementation Plan](design/gtao-implementation-plan.md)
- [Non-HBAO AO Audit](design/non-hbao-ambient-occlusion-audit.md)
- [VXAO Implementation Plan](design/vxao-implementation-plan.md)
- [Slang Shader Cross-Compile Plan](design/slang-shader-cross-compile-plan.md)
- [Runtime Modularization And Bootstrap Extraction Plan](design/runtime-modularization-plan.md)

## Notes
- Treat these documents as working areas for active refactors and planning.
- If you promote a design to shipped behavior, mirror key details into the relevant API or architecture doc.
- Completed timing migration notes now live in [features/tick-based-animation-timing.md](../features/tick-based-animation-timing.md).

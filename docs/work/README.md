# Work Docs

In-flight design notes, implementation trackers, and short-lived investigations. Prefer the main docs index for stable onboarding material.

[← Docs index](../README.md)

## Status Guide

| Status | Meaning |
|---|---|
| Active | Current implementation or validation work lives here. |
| Stable doc | The canonical write-up now lives under `docs/features` or another stable docs area. |
| Closed | The original work item is finished and has been collapsed into a final closeout note or removed. |
| Generated | Report output should be regenerated on demand, not maintained as durable documentation. |

## Current Triage

| Area | Status | Canonical doc | Notes |
|---|---|---|---|
| Default render pipeline V2 | Active | [todo/default-render-pipeline-v2-todo.md](todo/default-render-pipeline-v2-todo.md) | Active implementation tracker. |
| Runtime modularization | Active | [todo/runtime-modularization-phase3-todo.md](todo/runtime-modularization-phase3-todo.md) | Phase 2 was completed and removed. |
| Physics-chain performance | Active | [todo/physics-chain-speed-update-todo.md](todo/physics-chain-speed-update-todo.md) | Prior compatibility notes were merged into the active TODO. |
| Non-HBAO AO | Active | [todo/non-hbao-ambient-occlusion-remediation.md](todo/non-hbao-ambient-occlusion-remediation.md) | Prior audit and remediation notes are consolidated here. |
| HBAO / HBAO+ | Active | [todo/hbao-hbao-plus-implementation-todo.md](todo/hbao-hbao-plus-implementation-todo.md) | Active implementation tracker. |
| Transparency and OIT | Active | [todo/transparency-and-oit-todo.md](todo/transparency-and-oit-todo.md) | Active implementation tracker. |
| GPU rendering roadmap | Active | [todo/gpu-rendering.md](todo/gpu-rendering.md) | Broad GPU-driven rendering work remains active. |
| GPU softbody rigging | Active | [todo/gpu-softbody-mesh-rigging-todo.md](todo/gpu-softbody-mesh-rigging-todo.md) | Still an active work item. |
| Voxel cone tracing / VXAO | Active | [todo/voxel-cone-tracing-and-vxao-implementation-todo.md](todo/voxel-cone-tracing-and-vxao-implementation-todo.md) | Shared-voxel roadmap item. |
| Vulkan backlog | Active | [todo/vulkan.md](todo/vulkan.md) | Canonical active Vulkan TODO. |
| Vulkan status | Active | [vulkan/vulkan-report.md](vulkan/vulkan-report.md) | Consolidated report of completed Vulkan work. |
| Affine matrix rollout | Closed | [audit/affine-matrix-phase4-closeout-2026-03-19.md](audit/affine-matrix-phase4-closeout-2026-03-19.md) | Final consolidated closeout record. |
| Steam Audio | Stable doc | [../features/steam-audio.md](../features/steam-audio.md) | Remaining validation now lives in the stable feature doc. |
| Remote profiler | Stable doc | [../features/profiler.md](../features/profiler.md) | Design notes were merged into the stable feature doc. |

## Active TODOs

- [todo/aot-final-game-builds.md](todo/aot-final-game-builds.md)
- [todo/default-render-pipeline-v2-todo.md](todo/default-render-pipeline-v2-todo.md)
- [todo/gpu-rendering.md](todo/gpu-rendering.md)
- [todo/gpu-softbody-mesh-rigging-todo.md](todo/gpu-softbody-mesh-rigging-todo.md)
- [todo/hbao-hbao-plus-implementation-todo.md](todo/hbao-hbao-plus-implementation-todo.md)
- [todo/non-hbao-ambient-occlusion-remediation.md](todo/non-hbao-ambient-occlusion-remediation.md)
- [todo/physics-chain-speed-update-todo.md](todo/physics-chain-speed-update-todo.md)
- [todo/physics-finalization.md](todo/physics-finalization.md)
- [todo/runtime-modularization-phase3-todo.md](todo/runtime-modularization-phase3-todo.md)
- [todo/transparency-and-oit-todo.md](todo/transparency-and-oit-todo.md)
- [todo/voxel-cone-tracing-and-vxao-implementation-todo.md](todo/voxel-cone-tracing-and-vxao-implementation-todo.md)
- [todo/vulkan.md](todo/vulkan.md)
- [todo/xrmesh-vertex-remapper-optimizations.md](todo/xrmesh-vertex-remapper-optimizations.md)

## Active Design Docs

- [design/affine-matrix-integration-plan.md](design/affine-matrix-integration-plan.md)
- [design/bindless-deferred-texturing-plan.md](design/bindless-deferred-texturing-plan.md)
- [design/default-render-pipeline-improvement-plan.md](design/default-render-pipeline-improvement-plan.md)
- [design/gpu-render-pass-pipeline.md](design/gpu-render-pass-pipeline.md)
- [design/gpu-softbody-mesh-rigging-plan.md](design/gpu-softbody-mesh-rigging-plan.md)
- [design/hbao-hbao-plus-implementation-plan.md](design/hbao-hbao-plus-implementation-plan.md)
- [design/native-hierarchy-porting-plan.md](design/native-hierarchy-porting-plan.md)
- [design/openxr-implementation-comparison.md](design/openxr-implementation-comparison.md)
- [design/runtime-modularization-plan.md](design/runtime-modularization-plan.md)
- [design/slang-shader-cross-compile-plan.md](design/slang-shader-cross-compile-plan.md)
- [design/transparency-and-oit-implementation-plan.md](design/transparency-and-oit-implementation-plan.md)
- [design/vxao-implementation-plan.md](design/vxao-implementation-plan.md)
- [design/zero-readback-gpu-driven-rendering-plan.md](design/zero-readback-gpu-driven-rendering-plan.md)

## Generated Reports

Generated audit outputs should be treated as disposable report artifacts rather than durable work docs. Regenerate them from the corresponding VS Code tasks or report scripts when needed.

## Notes

- If a work doc becomes the long-term source of truth, move the durable parts into `docs/features`, `docs/architecture`, or another stable docs area.
- Completed timing migration notes now live in [../features/tick-based-animation-timing.md](../features/tick-based-animation-timing.md).

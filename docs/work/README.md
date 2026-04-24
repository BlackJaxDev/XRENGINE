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
| Native FBX import/export | Active | [todo/fbx-import-export-todo.md](todo/fbx-import-export-todo.md) | Assimp replacement roadmap for a low-allocation native FBX path. |
| fastgltf glTF import | Stable doc | [../features/model-import.md](../features/model-import.md) | Native glTF import shipped; the completed checklist remains in [todo/fastgltf-gltf-import-todo.md](todo/fastgltf-gltf-import-todo.md). |
| USD import/export | Active | [todo/usd-import-export-todo.md](todo/usd-import-export-todo.md) | Managed-fast-path plus OpenUSD-fallback roadmap for USD scene/model support. |
| Non-HBAO AO | Active | [todo/non-hbao-ambient-occlusion-remediation.md](todo/non-hbao-ambient-occlusion-remediation.md) | Prior audit and remediation notes are consolidated here. |
| HBAO / HBAO+ | Active | [todo/hbao-hbao-plus-implementation-todo.md](todo/hbao-hbao-plus-implementation-todo.md) | Active implementation tracker. |
| Transparency and OIT | Active | [todo/transparency-and-oit-todo.md](todo/transparency-and-oit-todo.md) | Active implementation tracker. |
| GPU rendering roadmap | Active | [todo/gpu-rendering.md](todo/gpu-rendering.md) | Broad GPU-driven rendering work remains active. |
| GPU skinning buffer compression | Active | [design/gpu-skinning-buffer-compression-plan.md](design/gpu-skinning-buffer-compression-plan.md) | XRMesh and XRMeshRenderer influence/palette compression plan for direct and compute skinning. |
| Dedicated render-thread window ownership | Active | [design/dedicated-render-thread-window-ownership-plan.md](design/dedicated-render-thread-window-ownership-plan.md) | Refactor plan to move engine window ownership, graphics contexts, and present off the startup/editor thread. |
| Animated Gaussian capture and streaming | Active | [todo/animated-gaussian-cloud-capture-and-streaming-todo.md](todo/animated-gaussian-cloud-capture-and-streaming-todo.md) | Offline bake plus one-draw animated Gaussian clip playback roadmap. |
| Shader and snippet optimization | Active | [todo/shader-and-snippet-optimization-todo.md](todo/shader-and-snippet-optimization-todo.md) | Active shader performance and preprocessing tracker. |
| GPU softbody rigging | Active | [todo/gpu-softbody-mesh-rigging-todo.md](todo/gpu-softbody-mesh-rigging-todo.md) | Still an active work item. |
| Voxel cone tracing / VXAO | Active | [todo/voxel-cone-tracing-and-vxao-implementation-todo.md](todo/voxel-cone-tracing-and-vxao-implementation-todo.md) | Shared-voxel roadmap item. |
| DDGI integration | Active | [todo/ddgi-implementation-todo.md](todo/ddgi-implementation-todo.md) | Execution tracker derived from the [design/ddgi-integration-plan.md](design/ddgi-integration-plan.md) roadmap. |
| Multiplayer networking / dedicated server orchestration | Stable doc | [../features/networking.md](../features/networking.md) | The completed realtime cleanup tracker was folded into the stable feature guide. Peer-to-peer host switching is tracked in [design/peer-to-peer-host-switching.md](design/peer-to-peer-host-switching.md). |
| Vulkan backlog | Active | [todo/vulkan.md](todo/vulkan.md) | Canonical Vulkan backlog, status, audit, and preserved diagnostics. |
| Startup FPS drops (2026-03-28) | Active | [design/startup-fps-drop-remediation-plan.md](design/startup-fps-drop-remediation-plan.md) | Remaining startup stalls after the earlier GL warmup fixes; includes engine and editor attack order. |
| Default pipeline regressions (2026-03-28) | Active | [audit/default-render-pipeline-regression-diagnosis-2026-03-28.md](audit/default-render-pipeline-regression-diagnosis-2026-03-28.md) | AO, AA, deferred grayscale, and sampler-binding diagnosis. |
| Affine matrix rollout | Closed | [audit/affine-matrix-phase4-closeout-2026-03-19.md](audit/affine-matrix-phase4-closeout-2026-03-19.md) | Final consolidated closeout record. |
| Steam Audio | Stable doc | [../features/steam-audio.md](../features/steam-audio.md) | Remaining validation now lives in the stable feature doc. |
| Remote profiler | Stable doc | [../features/profiler.md](../features/profiler.md) | Design notes were merged into the stable feature doc. |

## Active TODOs

- [todo/aot-final-game-builds.md](todo/aot-final-game-builds.md)
- [todo/animated-gaussian-cloud-capture-and-streaming-todo.md](todo/animated-gaussian-cloud-capture-and-streaming-todo.md)
- [todo/default-render-pipeline-v2-todo.md](todo/default-render-pipeline-v2-todo.md)
- [todo/ddgi-implementation-todo.md](todo/ddgi-implementation-todo.md)
- [todo/fbx-import-export-todo.md](todo/fbx-import-export-todo.md)
- [todo/gpu-rendering.md](todo/gpu-rendering.md)
- [todo/gpu-softbody-mesh-rigging-todo.md](todo/gpu-softbody-mesh-rigging-todo.md)
- [todo/hbao-hbao-plus-implementation-todo.md](todo/hbao-hbao-plus-implementation-todo.md)
- [todo/non-hbao-ambient-occlusion-remediation.md](todo/non-hbao-ambient-occlusion-remediation.md)
- [todo/physics-chain-speed-update-todo.md](todo/physics-chain-speed-update-todo.md)
- [todo/physics-finalization.md](todo/physics-finalization.md)
- [todo/runtime-modularization-phase3-todo.md](todo/runtime-modularization-phase3-todo.md)
- [todo/shader-and-snippet-optimization-todo.md](todo/shader-and-snippet-optimization-todo.md)
- [todo/transparency-and-oit-todo.md](todo/transparency-and-oit-todo.md)
- [todo/usd-import-export-todo.md](todo/usd-import-export-todo.md)
- [todo/voxel-cone-tracing-and-vxao-implementation-todo.md](todo/voxel-cone-tracing-and-vxao-implementation-todo.md)
- [todo/vulkan.md](todo/vulkan.md)
- [todo/xrmesh-vertex-remapper-optimizations.md](todo/xrmesh-vertex-remapper-optimizations.md)

## Active Design Docs

- [design/affine-matrix-integration-plan.md](design/affine-matrix-integration-plan.md)
- [design/bindless-deferred-texturing-plan.md](design/bindless-deferred-texturing-plan.md)
- [design/default-render-pipeline-improvement-plan.md](design/default-render-pipeline-improvement-plan.md)
- [design/ddgi-integration-plan.md](design/ddgi-integration-plan.md)
- [design/dedicated-render-thread-window-ownership-plan.md](design/dedicated-render-thread-window-ownership-plan.md)
- [design/gpu-skinning-buffer-compression-plan.md](design/gpu-skinning-buffer-compression-plan.md)
- [design/gpu-render-pass-pipeline.md](design/gpu-render-pass-pipeline.md)
- [design/gpu-softbody-mesh-rigging-plan.md](design/gpu-softbody-mesh-rigging-plan.md)
- [design/hbao-hbao-plus-implementation-plan.md](design/hbao-hbao-plus-implementation-plan.md)
- [design/networking.md](design/networking.md)
- [design/peer-to-peer-host-switching.md](design/peer-to-peer-host-switching.md)
- [design/native-hierarchy-porting-plan.md](design/native-hierarchy-porting-plan.md)
- [design/openxr-implementation-comparison.md](design/openxr-implementation-comparison.md)
- [design/runtime-modularization-plan.md](design/runtime-modularization-plan.md)
- [design/shadow-pass-material-binding-optimization-plan.md](design/shadow-pass-material-binding-optimization-plan.md)
- [design/slang-shader-cross-compile-plan.md](design/slang-shader-cross-compile-plan.md)
- [design/startup-fps-drop-remediation-plan.md](design/startup-fps-drop-remediation-plan.md)
- [design/transparency-and-oit-implementation-plan.md](design/transparency-and-oit-implementation-plan.md)
- [design/vxao-implementation-plan.md](design/vxao-implementation-plan.md)
- [design/zero-readback-gpu-driven-rendering-plan.md](design/zero-readback-gpu-driven-rendering-plan.md)

## Generated Reports

Generated audit outputs should be treated as disposable report artifacts rather than durable work docs. Regenerate them from the corresponding VS Code tasks or report scripts when needed.

## Notes

- If a work doc becomes the long-term source of truth, move the durable parts into `docs/features`, `docs/architecture`, or another stable docs area.
- Completed timing migration notes now live in [../features/tick-based-animation-timing.md](../features/tick-based-animation-timing.md).

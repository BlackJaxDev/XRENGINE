# Work Docs

In-flight design notes, implementation trackers, and short-lived investigations. Prefer the main docs index for stable onboarding material.

[Docs index](../README.md)

## Status Guide

| Status | Meaning |
|---|---|
| Active | Current implementation or validation work lives here. |
| Implemented + validation | Main implementation is complete; scene, performance, or integration validation remains. |
| Stable doc | The canonical write-up now lives under `docs/developer-guides`, `docs/user-guide`, `docs/architecture`, or another stable docs area. |
| Closed | The original work item is finished and has been collapsed into a final closeout note or removed. |
| Generated | Report output should be regenerated on demand, not maintained as durable documentation. |

## Current Triage

| Area | Status | Canonical doc | Notes |
|---|---|---|---|
| Default render pipeline V2 | Active | [todo/default-render-pipeline-v2-todo.md](todo/default-render-pipeline-v2-todo.md) | Active implementation tracker. |
| Default pipeline depth of field | Active | [todo/rendering/default-pipeline-depth-of-field-todo.md](todo/rendering/default-pipeline-depth-of-field-todo.md) | Optimization and feature roadmap for CoC, half-res near/far blur, stereo policy, debug views, and cinematic controls. |
| Atmospheric scattering | Implemented + validation | [../developer-guides/components/atmospheric-scattering.md](../developer-guides/components/atmospheric-scattering.md), [todo/rendering/atmospheric-scattering-component-todo.md](todo/rendering/atmospheric-scattering-component-todo.md), [design/rendering/atmospheric-scattering-component-design.md](design/rendering/atmospheric-scattering-component-design.md) | OpenGL mono implementation is in place; visual screenshot/profiler validation and stereo/platform parity remain follow-up validation work. |
| Local volumetric fog | Implemented + validation | [design/rendering/volumetric-fog-production-design.md](design/rendering/volumetric-fog-production-design.md) | Half-resolution scatter, temporal reprojection, bilateral upscale, and composite are in place. Production polish, XR parity, dual-lobe HG, optional powder brightening, and future froxel work are consolidated in the design. |
| Forward depth-normal TransformId | Active | [todo/forward-depth-normal-transform-id-todo.md](todo/forward-depth-normal-transform-id-todo.md) | Shared forward prepass follow-up so depth, normal, and transform ID describe the same surface. |
| Dynamic indirect material bindings | Active | [design/rendering/dynamic-indirect-material-bindings.md](design/rendering/dynamic-indirect-material-bindings.md), [todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md](todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md) | Layout-driven material table roadmap for zero-readback indirect rendering, replacing hardcoded opaque-deferred rows with generated shader and packer layouts. |
| Vulkan descriptor heap optimization | Active | [design/rendering/vulkan-descriptor-heap-optimization-design.md](design/rendering/vulkan-descriptor-heap-optimization-design.md), [todo/rendering/vulkan-dynamic-rendering-migration-todo.md](todo/rendering/vulkan-dynamic-rendering-migration-todo.md) | Preferred modern Vulkan binding model for material tables, Deferred+, RVC shadelets, render graph resources, and future RT/GI scene tables, with descriptor indexing as fallback. |
| Deferred+ render path | Active | [design/rendering/deferred-plus-render-path-design.md](design/rendering/deferred-plus-render-path-design.md), [todo/rendering/optimization/deferred-plus-render-path-todo.md](todo/rendering/optimization/deferred-plus-render-path-todo.md), [todo/rendering/optimization/visibility-buffer-rendering-todo.md](todo/rendering/optimization/visibility-buffer-rendering-todo.md) | Proposal for a compact visibility-buffer render path with froxel/material classification, deferred texture mapping, and material-region shading. |
| Runtime modularization | Active | [todo/runtime-modularization-phase3-todo.md](todo/runtime-modularization-phase3-todo.md) | Phase 2 was completed and removed. |
| Finalized game builds and asset cooking | Stable doc | [../user-guide/finalized-game-builds.md](../user-guide/finalized-game-builds.md) | User-facing guide for cooked AOT and explicitly non-AOT finalized game builds. |
| Physics-chain performance | Stable doc + testing | [../developer-guides/rendering/physics-chain-performance.md](../developer-guides/rendering/physics-chain-performance.md) | Remaining validation lives in [testing/physics-chain-performance.md](testing/physics-chain-performance.md). |
| Native FBX import/export | Active | [todo/fbx-import-export-todo.md](todo/fbx-import-export-todo.md) | Assimp replacement roadmap for a low-allocation native FBX path. |
| fastgltf glTF import | Stable doc + testing | [../developer-guides/assets/model-import.md](../developer-guides/assets/model-import.md) | Native glTF import shipped; validation record lives in [testing/gltf-import.md](testing/gltf-import.md). |
| USD import/export | Active | [todo/usd-import-export-todo.md](todo/usd-import-export-todo.md) | Managed-fast-path plus OpenUSD-fallback roadmap for USD scene/model support. |
| Ambient occlusion | Stable doc + testing | [../developer-guides/gi/ambient-occlusion.md](../developer-guides/gi/ambient-occlusion.md) | HBAO+ and non-HBAO implementation trackers are complete; remaining validation lives in [testing/ambient-occlusion.md](testing/ambient-occlusion.md). |
| Transparency and OIT | Active | [todo/transparency-and-oit-todo.md](todo/transparency-and-oit-todo.md) | Active implementation tracker. |
| GPU rendering roadmap | Active | [todo/rendering/gpu/production-rendering-pipeline-roadmap.md](todo/rendering/gpu/production-rendering-pipeline-roadmap.md) | Canonical GPU-driven rendering roadmap. The old broad `gpu-rendering.md` checklist is now a redirect. |
| Engine rendering optimization | Active | [todo/rendering/optimization/engine-rendering-optimization-roadmap.md](todo/rendering/optimization/engine-rendering-optimization-roadmap.md), [design/rendering/engine-optimization-and-avatar-optimizer-design.md](design/rendering/engine-optimization-and-avatar-optimizer-design.md) | Renderer performance strategy covering CPU direct, zero-readback GPU-driven rendering, meshlets, visibility-buffer rendering, stereo paths, and profiling. |
| CPU async query occlusion | Active | [todo/rendering/optimization/cpu-async-hardware-query-occlusion-todo.md](todo/rendering/optimization/cpu-async-hardware-query-occlusion-todo.md) | Focused TODO for fixing CPU async hardware-query occlusion so camera motion does not collapse into rendering every mesh. |
| Avatar optimization and virtualized rendering | Active | [todo/avatar/avatar-optimization-roadmap.md](todo/avatar/avatar-optimization-roadmap.md), [design/rendering/avatar-optimization-and-virtualized-rendering-design.md](design/rendering/avatar-optimization-and-virtualized-rendering-design.md) | In-editor automatic avatar optimization, material consolidation, atlasing, simplification, skin/blendshape reduction, LODs, cluster-virtualized avatars, and Gaussian-splat distant crowds. |
| Humanoid body/root compensation | Active | [todo/avatar/humanoid-body-root-compensation-todo.md](todo/avatar/humanoid-body-root-compensation-todo.md) | Unity-style humanoid body-frame, root-motion, muscle, and IK ordering work so hips/body motion compensates correctly during humanoid animation playback. |
| GPU meshlet zero-readback rendering | Active | [design/rendering/gpu-meshlet-zero-readback-rendering-design.md](design/rendering/gpu-meshlet-zero-readback-rendering-design.md), [todo/rendering/gpu/gpu-meshlet-zero-readback-rendering-todo.md](todo/rendering/gpu/gpu-meshlet-zero-readback-rendering-todo.md), [todo/rendering/gpu/production-rendering-pipeline-roadmap.md](todo/rendering/gpu/production-rendering-pipeline-roadmap.md) | Production meshlet renderer design covering GPUScene meshlet storage, GPU expansion, mesh-shader dispatch, culling, material-table shading, and zero-readback invariants. |
| OpenXR no-HMD testing | Implemented + validation | [design/VR/openxr-monado-testing-pipeline.md](design/VR/openxr-monado-testing-pipeline.md), [rendering/openxr-monado-vulkan-rendering-2026-06-24.md](rendering/openxr-monado-vulkan-rendering-2026-06-24.md), [todo/rendering/vr/openxr-monado-testing-pipeline-todo.md](todo/rendering/vr/openxr-monado-testing-pipeline-todo.md), [todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md](todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md), [todo/tests/openxr-timing-tests-todo.md](todo/tests/openxr-timing-tests-todo.md), [todo/rendering/vr/openxr-future-work-todo.md](todo/rendering/vr/openxr-future-work-todo.md) | Local scene-only and Monado-backed no-HMD lanes are implemented; Vulkan/Monado rendering investigation records the latest black-frame and mirror fixes. CI promotion, Monado baseline selection, and hardware rows remain follow-up validation. |
| OpenXR stereo and temporal isolation | Active | [todo/rendering/vr/openxr-stereo-temporal-isolation-todo.md](todo/rendering/vr/openxr-stereo-temporal-isolation-todo.md) | Roadmap for honest OpenXR stereo mode semantics, true single-pass stereo, per-eye temporal history, TSR, exposure, fog/atmosphere, vendor upscale, and CPU/GPU profiling validation. |
| Model import cooked asset cache | Active | [todo/assets/model-import-binary-cache-todo.md](todo/assets/model-import-binary-cache-todo.md), [design/assets/model-import-binary-cache-design.md](design/assets/model-import-binary-cache-design.md), [../developer-guides/assets/model-import.md](../developer-guides/assets/model-import.md) | Engine-native cooked `.asset` cache authority for warm model imports, including cached LOD and meshlet payloads plus manual source reimport. |
| Advanced flat mirrors | Active | [design/advanced-flat-mirror-rendering-design.md](design/advanced-flat-mirror-rendering-design.md) | Planar reflection design covering CPU/GPU dispatch, forward/deferred integration, stencil masking, reflection targets, recursion, and VR. |
| Shadow system overhaul | Active | [todo/rendering/shadows/shadow-atlas-overhaul-todo.md](todo/rendering/shadows/shadow-atlas-overhaul-todo.md), [design/shadow-filtering-vsm-evsm-plan.md](design/shadow-filtering-vsm-evsm-plan.md), [design/dynamic-shadow-atlas-lod-plan.md](design/dynamic-shadow-atlas-lod-plan.md), [design/shadow-resource-migration-audit.md](design/shadow-resource-migration-audit.md), [design/post-v1-advanced-shadow-features-plan.md](design/post-v1-advanced-shadow-features-plan.md) | Single master TODO covering atlas allocator and relevance, dynamic atlas/LOD allocation, VSM/EVSM filtering, and contact-shadow optimizations for directional, spot, and point lights. |
| Texture runtime, streaming, and virtual texturing | Active | [design/texturing/texture-runtime-streaming-virtual-texturing-design.md](design/texturing/texture-runtime-streaming-virtual-texturing-design.md), [design/texturing/texture-compression-and-cooked-cache-design.md](design/texturing/texture-compression-and-cooked-cache-design.md), [todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md](todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md), [todo/texturing/texture-compression-and-cooked-cache-todo.md](todo/texturing/texture-compression-and-cooked-cache-todo.md), [testing/texture-runtime-streaming-validation.md](testing/texture-runtime-streaming-validation.md) | Canonical texturing roadmap. v1 runtime streaming is implemented and needs scene validation; next phases cover safe sparse pages, compressed cooked payloads, full SVT, Vulkan parity, bindless deferred texturing, RVT, and neural compression. |
| OpenVR VRClient GPU handoff | Active | [todo/openvr-vrclient-gpu-handoff-todo.md](todo/openvr-vrclient-gpu-handoff-todo.md) | Zero-readback cross-process eye-texture handoff from the engine app to the legacy OpenVR companion process. |
| GPU-driven animation | Active | [todo/gpu-driven-animation-todo.md](todo/gpu-driven-animation-todo.md) | Phased execution tracker for the [GPU-driven animation architecture](design/gpu-driven-animation.md). |
| Skinning GPU efficiency follow-ups | Active | [todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md](todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md), [design/rendering/gpu/gpu-skinning-buffer-compression-plan.md](design/rendering/gpu/gpu-skinning-buffer-compression-plan.md) | Post-`Core4 + Spill` work for no-spill variants, mixed-precision palettes, dispatch reuse, and skinning LOD. |
| Blendshape compression and GPU efficiency | Active | [todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md](todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md), [todo/avatar/avatar-skin-skeleton-blendshape-optimization-todo.md](todo/avatar/avatar-skin-skeleton-blendshape-optimization-todo.md) | Runtime blendshape delta/weight compression, active-shape compaction, dispatch skipping, and blendshape LOD. |
| Dedicated render-thread window ownership | Active | [design/dedicated-render-thread-window-ownership-plan.md](design/dedicated-render-thread-window-ownership-plan.md) | Refactor plan to move engine window ownership, graphics contexts, and present off the startup/editor thread. |
| Animated Gaussian capture and streaming | Active | [todo/animated-gaussian-cloud-capture-and-streaming-todo.md](todo/animated-gaussian-cloud-capture-and-streaming-todo.md) | Offline bake plus one-draw animated Gaussian clip playback roadmap. |
| Octahedral billboard capture | Active | [todo/octahedral-billboard-capture-todo.md](todo/octahedral-billboard-capture-todo.md) | Phased repair plan for model/submesh impostor capture, runtime billboards, HLOD integration, and asset persistence. |
| Shader and snippet optimization | Active | [todo/shader-and-snippet-optimization-todo.md](todo/shader-and-snippet-optimization-todo.md) | Active shader performance and preprocessing tracker. |
| Resolved shader source optimization | Active | [todo/rendering/resolved-shader-source-optimization-todo.md](todo/rendering/resolved-shader-source-optimization-todo.md) | Architectural restructuring to resolve all shader includes/snippets first, then prune compiler-facing source generically for every shader family. |
| XRDataBuffer RHI write model | Stable doc + testing | [../architecture/rendering/xrdatabuffer-rhi-write-model.md](../architecture/rendering/xrdatabuffer-rhi-write-model.md), [testing/xrdatabuffer-rhi-write-model-validation.md](testing/xrdatabuffer-rhi-write-model-validation.md) | Core write model and representative migrations have landed; remaining work is hardware, barrier, and GPU submission strategy validation. |
| OpenGL shader program deduplication | Active | [todo/rendering/opengl-shader-program-deduplication-todo.md](todo/rendering/opengl-shader-program-deduplication-todo.md) | Tracker for reducing duplicate logical shader-program wrappers and adding grouped Shader Program Links diagnostics. |
| Vulkan ReSTIR radiance cache GI | Active | [todo/rendering/vulkan-restir-radiance-cache-gi-todo.md](todo/rendering/vulkan-restir-radiance-cache-gi-todo.md) | Vulkan-native KHR acceleration-structure, ray-query, and RT-pipeline roadmap for ReSTIR radiance-cached GI while preserving the current OpenGL native bridge. |
| Vulkan Fossilize integration | Active | [todo/rendering/vulkan-fossilize-integration-todo.md](todo/rendering/vulkan-fossilize-integration-todo.md) | Optional Valve Fossilize tooling, layer capture, replay, and native-recording roadmap for persistent Vulkan object repros and pipeline cache warmup. |
| Vulkan wrapper parity | Active | [todo/rendering/vulkan-wrapper-parity/README.md](todo/rendering/vulkan-wrapper-parity/README.md) | Consolidated open parity tracker for Vulkan mesh renderer, mesh ownership, material, shader, texture, and data buffer behavior against OpenGL. |
| GPU softbody rigging | Active | [todo/gpu-softbody-mesh-rigging-todo.md](todo/gpu-softbody-mesh-rigging-todo.md) | Still an active work item. |
| Voxel cone tracing / VXAO | Active | [todo/voxel-cone-tracing-and-vxao-implementation-todo.md](todo/voxel-cone-tracing-and-vxao-implementation-todo.md) | Shared-voxel roadmap item. |
| DDGI integration | Active | [todo/ddgi-implementation-todo.md](todo/ddgi-implementation-todo.md) | Execution tracker derived from the [design/ddgi-integration-plan.md](design/ddgi-integration-plan.md) roadmap. |
| Multiplayer networking / dedicated server orchestration | Stable doc | [../developer-guides/networking/networking.md](../developer-guides/networking/networking.md) | The completed realtime cleanup tracker was folded into the stable feature guide. Peer-to-peer host switching is tracked in [design/peer-to-peer-host-switching.md](design/peer-to-peer-host-switching.md). |
| Vulkan backlog | Active | [todo/vulkan.md](todo/vulkan.md) | Canonical Vulkan backlog, status, audit, and preserved diagnostics. |
| Startup FPS drops (2026-03-28) | Active | [design/startup-fps-drop-remediation-plan.md](design/startup-fps-drop-remediation-plan.md) | Remaining startup stalls after the earlier GL warmup fixes; includes engine and editor attack order. |
| Source-backed C# script components | Active | [design/source-backed-csharp-script-components.md](design/source-backed-csharp-script-components.md) | Stable proxy/materialization design for `.cs` assets that can be attached before they compile. |
| Default pipeline regressions (2026-03-28) | Active | [audit/default-render-pipeline-regression-diagnosis-2026-03-28.md](audit/default-render-pipeline-regression-diagnosis-2026-03-28.md) | AO, AA, deferred grayscale, and sampler-binding diagnosis. |
| Affine matrix rollout | Closed | [audit/affine-matrix-phase4-closeout-2026-03-19.md](audit/affine-matrix-phase4-closeout-2026-03-19.md) | Final consolidated closeout record. |
| Steam Audio | Stable doc | [../developer-guides/audio/steam-audio.md](../developer-guides/audio/steam-audio.md) | Remaining validation now lives in the stable feature doc. |
| Remote profiler | Stable doc | [../developer-guides/diagnostics/profiler.md](../developer-guides/diagnostics/profiler.md) | Design notes were merged into the stable feature doc. |

## Active TODOs

- [todo/avatar/avatar-analyzer-reporting-and-ux-todo.md](todo/avatar/avatar-analyzer-reporting-and-ux-todo.md)
- [todo/avatar/avatar-lod-meshlet-cooked-variant-todo.md](todo/avatar/avatar-lod-meshlet-cooked-variant-todo.md)
- [todo/avatar/avatar-material-texture-consolidation-todo.md](todo/avatar/avatar-material-texture-consolidation-todo.md)
- [todo/avatar/avatar-mesh-submesh-geometry-optimization-todo.md](todo/avatar/avatar-mesh-submesh-geometry-optimization-todo.md)
- [todo/avatar/avatar-optimization-roadmap.md](todo/avatar/avatar-optimization-roadmap.md)
- [todo/avatar/avatar-skin-skeleton-blendshape-optimization-todo.md](todo/avatar/avatar-skin-skeleton-blendshape-optimization-todo.md)
- [todo/avatar/cluster-virtualized-avatar-rendering-todo.md](todo/avatar/cluster-virtualized-avatar-rendering-todo.md)
- [todo/avatar/gaussian-splat-distant-crowd-lod-todo.md](todo/avatar/gaussian-splat-distant-crowd-lod-todo.md)
- [todo/avatar/humanoid-body-root-compensation-todo.md](todo/avatar/humanoid-body-root-compensation-todo.md)
- [todo/rendering/atmospheric-scattering-component-todo.md](todo/rendering/atmospheric-scattering-component-todo.md)
- [todo/animated-gaussian-cloud-capture-and-streaming-todo.md](todo/animated-gaussian-cloud-capture-and-streaming-todo.md)
- [todo/rendering/optimization/compact-zero-readback-rendering-todo.md](todo/rendering/optimization/compact-zero-readback-rendering-todo.md)
- [todo/rendering/optimization/cpu-async-hardware-query-occlusion-todo.md](todo/rendering/optimization/cpu-async-hardware-query-occlusion-todo.md)
- [todo/rendering/optimization/cpu-direct-fast-path-todo.md](todo/rendering/optimization/cpu-direct-fast-path-todo.md)
- [todo/rendering/optimization/deferred-plus-render-path-todo.md](todo/rendering/optimization/deferred-plus-render-path-todo.md)
- [todo/default-render-pipeline-v2-todo.md](todo/default-render-pipeline-v2-todo.md)
- [todo/rendering/default-pipeline-depth-of-field-todo.md](todo/rendering/default-pipeline-depth-of-field-todo.md)
- [todo/ddgi-implementation-todo.md](todo/ddgi-implementation-todo.md)
- [todo/rendering/optimization/engine-rendering-optimization-roadmap.md](todo/rendering/optimization/engine-rendering-optimization-roadmap.md)
- [todo/fbx-import-export-todo.md](todo/fbx-import-export-todo.md)
- [todo/forward-depth-normal-transform-id-todo.md](todo/forward-depth-normal-transform-id-todo.md)
- [todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md](todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md)
- [todo/gpu-driven-animation-todo.md](todo/gpu-driven-animation-todo.md)
- [todo/rendering/gpu/production-rendering-pipeline-roadmap.md](todo/rendering/gpu/production-rendering-pipeline-roadmap.md)
- [todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md](todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md)
- [todo/gpu-softbody-mesh-rigging-todo.md](todo/gpu-softbody-mesh-rigging-todo.md)
- [todo/assets/model-import-binary-cache-todo.md](todo/assets/model-import-binary-cache-todo.md)
- [todo/octahedral-billboard-capture-todo.md](todo/octahedral-billboard-capture-todo.md)
- [todo/openvr-vrclient-gpu-handoff-todo.md](todo/openvr-vrclient-gpu-handoff-todo.md)
- [todo/rendering/vr/openxr-monado-testing-pipeline-todo.md](todo/rendering/vr/openxr-monado-testing-pipeline-todo.md)
- [todo/rendering/opengl-shader-program-deduplication-todo.md](todo/rendering/opengl-shader-program-deduplication-todo.md)
- [todo/rendering/vulkan-restir-radiance-cache-gi-todo.md](todo/rendering/vulkan-restir-radiance-cache-gi-todo.md)
- [todo/rendering/vulkan-fossilize-integration-todo.md](todo/rendering/vulkan-fossilize-integration-todo.md)
- [todo/physics-finalization.md](todo/physics-finalization.md)
- [todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md](todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md)
- [todo/rendering/resolved-shader-source-optimization-todo.md](todo/rendering/resolved-shader-source-optimization-todo.md)
- [todo/rendering/optimization/rendering-profiler-and-benchmarking-todo.md](todo/rendering/optimization/rendering-profiler-and-benchmarking-todo.md)
- [todo/rendering/vulkan-wrapper-parity/README.md](todo/rendering/vulkan-wrapper-parity/README.md)
- [todo/runtime-modularization-phase3-todo.md](todo/runtime-modularization-phase3-todo.md)
- [todo/shader-and-snippet-optimization-todo.md](todo/shader-and-snippet-optimization-todo.md)
- [todo/rendering/shadows/shadow-atlas-overhaul-todo.md](todo/rendering/shadows/shadow-atlas-overhaul-todo.md)
- [todo/transparency-and-oit-todo.md](todo/transparency-and-oit-todo.md)
- [todo/texturing/texture-compression-and-cooked-cache-todo.md](todo/texturing/texture-compression-and-cooked-cache-todo.md)
- [todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md](todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md)
- [todo/usd-import-export-todo.md](todo/usd-import-export-todo.md)
- [todo/voxel-cone-tracing-and-vxao-implementation-todo.md](todo/voxel-cone-tracing-and-vxao-implementation-todo.md)
- [todo/rendering/optimization/visibility-buffer-rendering-todo.md](todo/rendering/optimization/visibility-buffer-rendering-todo.md)
- [todo/rendering/optimization/vr-rendering-performance-contract-todo.md](todo/rendering/optimization/vr-rendering-performance-contract-todo.md)
- [todo/vulkan.md](todo/vulkan.md)
- [todo/xrmesh-vertex-remapper-optimizations.md](todo/xrmesh-vertex-remapper-optimizations.md)

## Active Design Docs

- [design/affine-matrix-integration-plan.md](design/affine-matrix-integration-plan.md)
- [design/advanced-flat-mirror-rendering-design.md](design/advanced-flat-mirror-rendering-design.md)
- [design/rendering/atmospheric-scattering-component-design.md](design/rendering/atmospheric-scattering-component-design.md)
- [design/cuda-usage-opportunities-design.md](design/cuda-usage-opportunities-design.md)
- [design/default-render-pipeline-improvement-plan.md](design/default-render-pipeline-improvement-plan.md)
- [design/ddgi-integration-plan.md](design/ddgi-integration-plan.md)
- [design/dedicated-render-thread-window-ownership-plan.md](design/dedicated-render-thread-window-ownership-plan.md)
- [design/rendering/engine-optimization-and-avatar-optimizer-design.md](design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [design/rendering/avatar-optimization-and-virtualized-rendering-design.md](design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [design/rendering/deferred-plus-render-path-design.md](design/rendering/deferred-plus-render-path-design.md)
- [design/rendering/dynamic-indirect-material-bindings.md](design/rendering/dynamic-indirect-material-bindings.md)
- [design/rendering/gpu-meshlet-zero-readback-rendering-design.md](design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [design/rendering/volumetric-fog-production-design.md](design/rendering/volumetric-fog-production-design.md)
- [design/assets/model-import-binary-cache-design.md](design/assets/model-import-binary-cache-design.md)
- [design/dynamic-shadow-atlas-lod-plan.md](design/dynamic-shadow-atlas-lod-plan.md)
- [design/gpu-skinning-buffer-compression-plan.md](design/gpu-skinning-buffer-compression-plan.md)
- [design/gpu-driven-animation.md](design/gpu-driven-animation.md)
- [design/gpu-render-pass-pipeline.md](design/gpu-render-pass-pipeline.md)
- [design/gpu-softbody-mesh-rigging-plan.md](design/gpu-softbody-mesh-rigging-plan.md)
- [design/hbao-hbao-plus-implementation-plan.md](design/hbao-hbao-plus-implementation-plan.md)
- [design/networking.md](design/networking.md)
- [design/VR/openxr-monado-testing-pipeline.md](design/VR/openxr-monado-testing-pipeline.md)
- [design/peer-to-peer-host-switching.md](design/peer-to-peer-host-switching.md)
- [design/native-hierarchy-porting-plan.md](design/native-hierarchy-porting-plan.md)
- [design/openxr-implementation-comparison.md](design/openxr-implementation-comparison.md)
- [design/post-v1-advanced-shadow-features-plan.md](design/post-v1-advanced-shadow-features-plan.md)
- [design/runtime-modularization-plan.md](design/runtime-modularization-plan.md)
- [design/shadow-pass-material-binding-optimization-plan.md](design/shadow-pass-material-binding-optimization-plan.md)
- [design/shadow-filtering-vsm-evsm-plan.md](design/shadow-filtering-vsm-evsm-plan.md)
- [design/shadow-resource-migration-audit.md](design/shadow-resource-migration-audit.md)
- [design/slang-shader-cross-compile-plan.md](design/slang-shader-cross-compile-plan.md)
- [design/source-backed-csharp-script-components.md](design/source-backed-csharp-script-components.md)
- [design/startup-fps-drop-remediation-plan.md](design/startup-fps-drop-remediation-plan.md)
- [design/transparency-and-oit-implementation-plan.md](design/transparency-and-oit-implementation-plan.md)
- [design/texturing/texture-runtime-streaming-virtual-texturing-design.md](design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [design/vxao-implementation-plan.md](design/vxao-implementation-plan.md)
- [design/zero-readback-gpu-driven-rendering-plan.md](design/zero-readback-gpu-driven-rendering-plan.md)

## Generated Reports

Generated audit outputs should be treated as disposable report artifacts rather than durable work docs. Regenerate them from the corresponding VS Code tasks or report scripts when needed.

## Testing Docs

- [testing/ambient-occlusion.md](testing/ambient-occlusion.md)
- [testing/gltf-import.md](testing/gltf-import.md)
- [testing/physics-chain-performance.md](testing/physics-chain-performance.md)
- [testing/texture-management-runtime-baseline-2026-05-01.md](testing/texture-management-runtime-baseline-2026-05-01.md)
- [testing/texture-runtime-streaming-validation.md](testing/texture-runtime-streaming-validation.md)
- [testing/texture-streaming-run-analysis-2026-05-01-180642.md](testing/texture-streaming-run-analysis-2026-05-01-180642.md)
- [testing/xrdatabuffer-rhi-write-model-validation.md](testing/xrdatabuffer-rhi-write-model-validation.md)

## Notes

- If a work doc becomes the long-term source of truth, move the durable parts into `docs/developer-guides`, `docs/user-guide`, `docs/architecture`, or another stable docs area.
- Completed timing migration notes were folded into the user/developer guide split where relevant.

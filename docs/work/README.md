# Work Docs

In-flight design notes, implementation trackers, and short-lived investigations. Prefer the main docs index for stable onboarding material.

[Docs index](../README.md)

## Placement Guide

Work docs are organized by document purpose first, subsystem second:

| Path | Use |
|---|---|
| `investigations/<subsystem>/` | Bug, regression, crash, performance, visual artifact, and evidence-driven debug notes. |
| `progress/<subsystem>/` | Implementation phase ledgers, status updates, validation manifests, and closeout/progress notes for active work. |
| `design/<subsystem>/` | Design proposals and architecture plans. |
| `todo/<subsystem>/` | Execution checklists and backlog items. Completed todo docs are deleted after their design moves to architecture docs; git history keeps them. There is no `todo/COMPLETED/` folder. |
| `testing/<subsystem>/` | Validation plans, reproducible test notes, and hardware/software test matrices. |

Avoid top-level subsystem buckets such as `docs/work/rendering/` for investigation or progress notes. Use `docs/work/investigations/rendering/` or `docs/work/progress/rendering/` instead.

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
| Editor memory reduction | Active | [todo/rendering/optimization/editor-memory-reduction-todo.md](todo/rendering/optimization/editor-memory-reduction-todo.md) | Ordered checklist to bring the OpenXR editor under 8 GB: capacity-sized renderer arrays, per-vertex mesh model to packed buffers, mesh and texture CPU copies, texture compression and budget, render-target audit, heap fragmentation. |
| Shadow and pipeline validation failures | Closed | [../architecture/rendering/render-pipeline-resource-lifecycle.md](../architecture/rendering/render-pipeline-resource-lifecycle.md) | All 50 failed cases from the October 2 runs are resolved. The checklist is deleted; lasting rules are in the lifecycle architecture doc. |
| Control plane managed server instances | Planned | [todo/networking/control-plane-managed-server-instances-todo.md](todo/networking/control-plane-managed-server-instances-todo.md), [testing/networking/networking-validation.md](testing/networking/networking-validation.md) | Service/host supervision, verified world startup, player admission/accounting, authoritative synchronization, client workflow, restart recovery, and public hosting. |
| Default render pipeline V2 | Closed | [../architecture/rendering/default-render-pipeline-notes.md](../architecture/rendering/default-render-pipeline-notes.md#advanced-pipeline-command-authoring) | `DefaultRenderPipeline2` is now `AdvancedRenderPipeline`. Open code items are in the [XR and Advanced TODO](todo/rendering/vulkan-xr-and-advanced-rendering-todo.md); checks are in [testing/rendering/default-and-advanced-pipeline-validation.md](testing/rendering/default-and-advanced-pipeline-validation.md). |
| Default pipeline depth of field | Active | [todo/rendering/default-pipeline-depth-of-field-todo.md](todo/rendering/default-pipeline-depth-of-field-todo.md) | Optimization and feature roadmap for CoC, half-res near/far blur, stereo policy, debug views, and cinematic controls. |
| Atmospheric scattering | Implemented + validation | [../developer-guides/components/atmospheric-scattering.md](../developer-guides/components/atmospheric-scattering.md), [todo/rendering/sky-and-atmosphere-followups-todo.md](todo/rendering/sky-and-atmosphere-followups-todo.md), [design/rendering/atmospheric-scattering-component-design.md](design/rendering/atmospheric-scattering-component-design.md) | OpenGL mono implementation is in place. Stereo variants, LUT quality mode, and material hooks are open code items; screenshot, profiler, and stereo checks are in [testing/rendering/default-and-advanced-pipeline-validation.md](testing/rendering/default-and-advanced-pipeline-validation.md). |
| Local volumetric fog | Implemented + validation | [design/rendering/volumetric-fog-production-design.md](design/rendering/volumetric-fog-production-design.md) | Half-resolution scatter, temporal reprojection, bilateral upscale, and composite are in place. Production polish, XR parity, dual-lobe HG, optional powder brightening, and future froxel work are consolidated in the design. |
| Forward depth-normal TransformId | Active | [todo/rendering/forward-depth-normal-transform-id-todo.md](todo/rendering/forward-depth-normal-transform-id-todo.md) | Shared forward prepass follow-up so depth, normal, and transform ID describe the same surface. |
| Dynamic indirect material bindings | Active | [design/rendering/dynamic-indirect-material-bindings.md](design/rendering/dynamic-indirect-material-bindings.md), [todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md](todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md) | Layout-driven material table roadmap for zero-readback indirect rendering, replacing hardcoded opaque-deferred rows with generated shader and packer layouts. |
| Vulkan modern backend completion | Active | [Vulkan extension backlog](todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#vulkan-extension-backlog), [design/rendering/vulkan-descriptor-heap-optimization-design.md](design/rendering/vulkan-descriptor-heap-optimization-design.md), [design/rendering/vulkan-shader-object-pipeline-replacement-design.md](design/rendering/vulkan-shader-object-pipeline-replacement-design.md) | Production implementation tracker for local read, native descriptor heap, shader objects, XR foveation, transient memory, GPU-driven/DGC, and ray-tracing paths. Capability gating does not remove implementation work. |
| Vulkan stall remediation | Code closed; acceptance open | [separate findings](todo/rendering/vulkan-stall-separate-findings-todo.md), [VR pipeline selection](design/rendering/vr-pipeline-selection-design.md), [results](progress/rendering/vulkan-stall-remediation-results.md), [checks](testing/rendering/vulkan-core-validation.md#stall-remediation-cumulative-acceptance), [architecture](../architecture/rendering/vulkan-scene-preparation-and-publication.md) | The remediation checklist is deleted. Remaining code items are in the [Vulkan master todo](todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md). OpenXR visual, admission, memory, desktop comparison, allocation, lifetime, and temporal checks are in the core validation doc. |
| Vulkan core hardening and frame-wide render loop | Active | [code changes](todo/rendering/vulkan-core-hardening-and-device-loss-todo.md), [testing](testing/rendering/vulkan-core-validation.md), [target design](design/rendering/vulkan-render-loop-target-architecture.md), [multi-view design](design/rendering/vulkan-render-loop-design.md) | Consolidated program for production-grade lifecycle hardening, a smaller Vulkan ownership surface, zero-allocation CPU hot paths, complete slow-frame attribution, frame-plan recording, graph simplification, and render-tail work. |
| Advanced visibility renderer | Active; replaces Deferred+ proposal | [XR/Advanced TODO](todo/rendering/vulkan-xr-and-advanced-rendering-todo.md), [historical design](design/rendering/deferred-plus-render-path-design.md) | Current visibility, native shading, temporal and stereo acceptance; superseded proposal TODOs removed. |
| Runtime modularization | Closed | [../architecture/runtime/project-organization.md](../architecture/runtime/project-organization.md), [design/runtime-modularization-plan.md](design/runtime-modularization-plan.md) | The project organization doc is the stable reference. Phase trackers are retired. |
| Runtime regression and NativeAOT hardening | Active | [Checklist](todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md), [progress](progress/runtime/runtime-regression-and-nativeaot-hardening-progress.md) | Current non-Vulkan software regressions, deterministic validation, generated runtime construction, and strict packaged-player acceptance. |
| Runtime data layout and generated contracts | Planned | [todo/runtime/runtime-data-layout-and-generated-contracts-todo.md](todo/runtime/runtime-data-layout-and-generated-contracts-todo.md), [design/runtime/runtime-data-layout-and-generated-contracts-design.md](design/runtime/runtime-data-layout-and-generated-contracts-design.md) | Copy-free cooked asset loads, binary realtime payloads, AOT parity diagnostics, a Roslyn contract generator, reflected GPU layout validation, dense transform storage, and a data-only downloaded-content policy. Gated changes approved 2026-10-02. |
| GC and hot-path memory control | Stable doc + testing | [../developer-guides/runtime/hot-path-memory.md](../developer-guides/runtime/hot-path-memory.md), [runtime/gc-hot-path-memory-control-2026-07-02.md](runtime/gc-hot-path-memory-control-2026-07-02.md), [testing/runtime/runtime-and-aot-validation.md](testing/runtime/runtime-and-aot-validation.md), [testing/runtime/memory-control-investigation-template.md](testing/runtime/memory-control-investigation-template.md) | Runtime memory profiles, scratch/pool helpers, allocation scopes, and ECS/network allocation tests are in place. The tracker is closed. Hardware VR and live editor profiler captures are in the runtime validation doc. |
| Finalized game builds and asset cooking | Stable doc | [../user-guide/finalized-game-builds.md](../user-guide/finalized-game-builds.md) | User-facing guide for cooked AOT and explicitly non-AOT finalized game builds. |
| Physics-chain performance | Stable doc + testing | [../developer-guides/rendering/physics-chain-performance.md](../developer-guides/rendering/physics-chain-performance.md) | Remaining validation lives in [testing/physics/physics-validation.md](testing/physics/physics-validation.md). |
| GPU-accelerated modeling | Planned | [todo/modeling/modeling-todo.md](todo/modeling/modeling-todo.md), [testing/modeling/modeling-validation.md](testing/modeling/modeling-validation.md) | One code todo for topology, geometry nodes, core tools, GPU preview, subdivision, and editor integration. |
| Native UI retained performance and editor parity | Active | [todo/ui/native-ui-retained-performance-and-editor-parity-todo.md](todo/ui/native-ui-retained-performance-and-editor-parity-todo.md), [testing/ui/native-ui-validation.md](testing/ui/native-ui-validation.md) | Retained native UI performance and parity with the ImGui editor. |
| Native FBX import/export | Active | [todo/assets/fbx-import-export-todo.md](todo/assets/fbx-import-export-todo.md) | Assimp replacement roadmap for a low-allocation native FBX path. |
| fastgltf glTF import | Stable doc + testing | [../developer-guides/assets/model-import.md](../developer-guides/assets/model-import.md) | Native glTF import shipped; validation record lives in [testing/assets/gltf-import.md](testing/assets/gltf-import.md). |
| USD import/export | Active | [todo/assets/usd-import-export-todo.md](todo/assets/usd-import-export-todo.md) | Managed-fast-path plus OpenUSD-fallback roadmap for USD scene/model support. |
| Ambient occlusion | Stable doc + testing | [../developer-guides/gi/ambient-occlusion.md](../developer-guides/gi/ambient-occlusion.md) | HBAO+ and non-HBAO implementation trackers are complete; remaining validation lives in [testing/rendering/ambient-occlusion.md](testing/rendering/ambient-occlusion.md). |
| Transparency and OIT | Active | [todo/rendering/transparency-and-oit-todo.md](todo/rendering/transparency-and-oit-todo.md), [../architecture/rendering/transparency-and-oit.md](../architecture/rendering/transparency-and-oit.md) | Open code items for transparency modes and OIT. Checks are in [testing/rendering/default-and-advanced-pipeline-validation.md](testing/rendering/default-and-advanced-pipeline-validation.md). |
| GPU rendering roadmap | Active | [todo/rendering/gpu/production-rendering-pipeline-roadmap.md](todo/rendering/gpu/production-rendering-pipeline-roadmap.md) | Canonical GPU-driven rendering roadmap. The old broad `gpu-rendering.md` checklist is now a redirect. |
| Engine rendering optimization | Active | [todo/rendering/optimization/engine-rendering-optimization-roadmap.md](todo/rendering/optimization/engine-rendering-optimization-roadmap.md), [design/rendering/engine-optimization-and-avatar-optimizer-design.md](design/rendering/engine-optimization-and-avatar-optimizer-design.md) | Renderer performance strategy covering CPU direct, zero-readback GPU-driven rendering, meshlets, visibility-buffer rendering, stereo paths, and profiling. |
| Vulkan component profiling | Stable doc + testing | [profiler guide](../developer-guides/diagnostics/profiler.md#dedicated-vulkan-renderbench), [checks](testing/rendering/vulkan-core-validation.md#component-profiling-and-renderbench), [code items](todo/rendering/optimization/editor-profiler-ui-render-cost-todo.md#open-code-items) | Editor-independent RenderBench fixtures, bounded selected CPU/GPU diagnostics, and command-line comparison are implemented. Clean promotion and production-frame checks are open; two code items remain. |
| Vulkan resident draw stream and render task pool | Implementation recorded; acceptance open | [Vulkan master](todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md), [completed foundation](progress/rendering/vulkan-phases-0-5-completed.md) | Current F3 and RC gates own parity, allocation and lifetime evidence; obsolete Phase 1B-only status retired. |
| Browser WebGL2 and WebGPU renderers | Active | [design/rendering/browser-wasm-renderer-design.md](design/rendering/browser-wasm-renderer-design.md), [unified runtime TODO](todo/platform/unified-desktop-browser-runtime-todo.md), [readiness audit](progress/rendering/mobile-browser-readiness.md) | Source dependency audit and acceptance contract recorded; browser implementation pending. Two browser renderer leaf modules with shared canvas/WASM interop, WebGPU-first automatic selection, WebGL2 compatibility, shader cooking, and portable-runtime extraction. The runtime architecture (separate browser runtime and portable source profiles) is superseded by the unified desktop and browser runtime design below. |
| Unified desktop and browser runtime | Paused | [design/platform/unified-desktop-browser-runtime-design.md](design/platform/unified-desktop-browser-runtime-design.md), [platform validation](testing/platform/platform-validation.md), [source progress](progress/platform/native-subsystem-project-split.md), [build stabilization](progress/platform/unified-runtime-build-stabilization.md), [host ownership](progress/platform/portable-engine-host-ownership.md), [host validation](investigations/platform/portable-engine-host-validation.md), [browser boot qualification](progress/platform/portable-browser-engine-boot.md), [harness investigation](investigations/rendering/desktop-browser-reference-harness.md), [unified runtime TODO](todo/platform/unified-desktop-browser-runtime-todo.md) | Paused at owner request: 19/110 complete. Reference and portable-host gates pass locally with limits. Real browser-world startup remains open; external asset resolution and a managed Jolt signature conflict are the next boundaries. |
| Vulkan over MoltenVK renderer integration | Active | [design/rendering/moltenvk-vulkan-renderer-integration-design.md](design/rendering/moltenvk-vulkan-renderer-integration-design.md) | Renderer-focused companion to the Apple platform design: reuse the Vulkan backend through a MoltenVK platform adapter, portability profile, Metal WSI, and explicit capability negotiation. |
| CPU async query occlusion | Closed | [../developer-guides/rendering/cpu-query-async-occlusion.md](../developer-guides/rendering/cpu-query-async-occlusion.md) | Implementation is complete. The stable guide describes the current behavior. Checks are in [testing/rendering/render-queries-and-occlusion-validation.md](testing/rendering/render-queries-and-occlusion-validation.md). Camera-motion stress checks continue in [todo/rendering/cpu-async-query-camera-motion-todo.md](todo/rendering/cpu-async-query-camera-motion-todo.md). |
| Avatar optimization and virtualized rendering | Active | [todo/avatar/avatar-optimization-roadmap.md](todo/avatar/avatar-optimization-roadmap.md), [design/rendering/avatar-optimization-and-virtualized-rendering-design.md](design/rendering/avatar-optimization-and-virtualized-rendering-design.md) | In-editor automatic avatar optimization, material consolidation, atlasing, simplification, skin/blendshape reduction, LODs, cluster-virtualized avatars, and Gaussian-splat distant crowds. |
| Humanoid body/root compensation | Active | [todo/avatar/humanoid-body-root-compensation-todo.md](todo/avatar/humanoid-body-root-compensation-todo.md) | Unity-style humanoid body-frame, root-motion, muscle, and IK ordering work so hips/body motion compensates correctly during humanoid animation playback. |
| VR full-body calibration and spectator | Implemented; acceptance open | [Integration guide](../developer-guides/vr/openxr-body-tracking.md), [acceptance procedure](testing/avatar/avatar-validation.md#openxr-body-calibration-and-spectator), [code items](todo/avatar/vr-full-body-estimation-todo.md#calibration-and-spectator-follow-ups), [Windows validation](investigations/avatar/openxr-calibration-spectator-validation-2026-10-01.md) | Stable body targets, proximity binding, transactional calibration, measurement-owned scale, and independent spectator output are implemented. Named-hardware transport, controller presets, visual/performance acceptance, and cross-session continuity remain unqualified. |
| VR full-body estimation | Active | [todo/avatar/vr-full-body-estimation-todo.md](todo/avatar/vr-full-body-estimation-todo.md) | Analytic estimation of untracked body slots for 3- to 5-point and partial tracker setups, including tracker-loss handoff. Not started. |
| GPU meshlet zero-readback rendering | Closed | [../architecture/rendering/mesh-submission-strategies.md](../architecture/rendering/mesh-submission-strategies.md), [investigations/rendering/meshlet-import-production-closeout-2026-08-20.md](investigations/rendering/meshlet-import-production-closeout-2026-08-20.md), [design/rendering/gpu-meshlet-zero-readback-rendering-design.md](design/rendering/gpu-meshlet-zero-readback-rendering-design.md) | Production closeout is complete. Remaining test code and model-cache-dependent items are in the [GPU roadmap](todo/rendering/gpu/production-rendering-pipeline-roadmap.md); checks are in [testing/rendering/gpu-driven-submission-validation.md](testing/rendering/gpu-driven-submission-validation.md). |
| OpenXR no-HMD testing | Implemented; validation open | [../developer-guides/vr/openxr-runtime.md](../developer-guides/vr/openxr-runtime.md#no-hmd-test-lanes), [design/VR/openxr-monado-testing-pipeline.md](design/VR/openxr-monado-testing-pipeline.md), [todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md](todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md), [testing/xr/openxr-validation.md](testing/xr/openxr-validation.md), [todo/tests/openxr-timing-tests-todo.md](todo/tests/openxr-timing-tests-todo.md), [investigations/rendering/archive/openxr-monado-vulkan-rendering-2026-06-24.md](investigations/rendering/archive/openxr-monado-vulkan-rendering-2026-06-24.md) | Scene-only and Monado-backed no-HMD lanes are implemented and described in the runtime guide. Monado baseline, CI ownership, and persistent smoke settings are open decisions; hardware rows and final lane evidence are open checks. |
| OpenXR Vulkan parallel eye recording | Active | [todo/rendering/vr/openxr-vulkan-parallel-eye-recording-todo.md](todo/rendering/vr/openxr-vulkan-parallel-eye-recording-todo.md), [testing/xr/openxr-validation.md](testing/xr/openxr-validation.md), [../architecture/rendering/openxr-vr-rendering.md](../architecture/rendering/openxr-vr-rendering.md#view-render-modes-and-view-scoped-state) | View-scoped eye recording and `ParallelCommandBufferRecording` are implemented. Diagnostics cleanup, the legacy setting removal, and the default-mode decision remain; 120 Hz and flicker checks are open. |
| Editor OpenXR runtime toggle and rendering | Active | [todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md](todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md), [investigations/editor/openxr-runtime-toggle-2026-09-24.md](investigations/editor/openxr-runtime-toggle-2026-09-24.md) | Runtime chooser and temporary rigs implemented. Desktop upload starvation, headset shading, import responsiveness, and final transition acceptance remain open; latest staging/BRDF changes require rebuild and runtime validation. |
| OpenXR SteamVR/OpenVR parity | Stable doc + testing | [../developer-guides/vr/openxr-runtime.md](../developer-guides/vr/openxr-runtime.md), [hardware validation](testing/xr/openxr-steamvr-hardware-validation.md), [decisions](todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md#decisions-needed) | Hardware and input parity roadmap for running SteamVR hardware through OpenXR instead of OpenVR, including controller actions, haptics, VIVE trackers, and hand/finger data. |
| OpenXR stereo and temporal isolation | Closed | [../architecture/rendering/default-render-pipeline-notes.md](../architecture/rendering/default-render-pipeline-notes.md#29-openxr-stereo-temporal-isolation), [../architecture/rendering/openxr-vr-rendering.md](../architecture/rendering/openxr-vr-rendering.md#view-render-modes-and-view-scoped-state) | Stereo mode semantics, true single-pass stereo, per-eye temporal history, TSR, exposure, fog/atmosphere, and vendor upscale policy are in the architecture docs. The hardware multiview check is in [testing/xr/openxr-validation.md](testing/xr/openxr-validation.md). |
| RVC VR debug views | Active | [todo/rendering/vr/retinal-visibility-cache-debug-views-todo.md](todo/rendering/vr/retinal-visibility-cache-debug-views-todo.md) | Debug-view and validation surface for Retinal Visibility Cache across serial, single-pass stereo, Vulkan parallel recording, and quad-view paths. |
| Model import cooked asset cache | Active | [todo/assets/model-import-binary-cache-todo.md](todo/assets/model-import-binary-cache-todo.md), [design/assets/model-import-binary-cache-design.md](design/assets/model-import-binary-cache-design.md), [../developer-guides/assets/model-import.md](../developer-guides/assets/model-import.md) | Design reconciled; model-specific binary codec is not implemented. Existing cooked-mesh, meshlet, generic cache, and reimport foundations are reusable. |
| Native FBX avatar transforms | Closed | [investigations/asset-import/native-fbx-avatar-transforms-2026-09-24.md](investigations/asset-import/native-fbx-avatar-transforms-2026-09-24.md) | Parent-first construction, serialized skin bind semantics, and unweighted-vertex placement repaired; real avatar validated in the Vulkan editor. |
| Avatar scene publication stalls | Active | [investigations/asset-import/avatar-scene-publication-stalls-2026-09-24.md](investigations/asset-import/avatar-scene-publication-stalls-2026-09-24.md) | Investigating repeated transform traversal, import-completion rendering work, and indefinite freezes during overlapping renderer replacement. |
| Avatar mapping and import diagnostics | Closed | [investigations/asset-import/avatar-mapping-and-import-diagnostics-2026-09-24.md](investigations/asset-import/avatar-mapping-and-import-diagnostics-2026-09-24.md) | Primary skeleton detection and startup playback verified; import warnings remain visible with tracing off and missing textures publish together after import. |
| Console Vulkan routing and focus | Closed | [investigations/editor/console-vulkan-focus-2026-09-24.md](investigations/editor/console-vulkan-focus-2026-09-24.md) | Vulkan tab added, routine frame tracing gated, and repeated detached-window show requests removed; external textbox focus verified. |
| Advanced flat mirrors | Active | [design/rendering/advanced-flat-mirror-rendering-design.md](design/rendering/advanced-flat-mirror-rendering-design.md) | Planar reflection design covering CPU/GPU dispatch, forward/deferred integration, stencil masking, reflection targets, recursion, and VR. |
| Shadow system overhaul | Active | [todo/rendering/shadows/shadow-atlas-overhaul-todo.md](todo/rendering/shadows/shadow-atlas-overhaul-todo.md), [../architecture/rendering/shadow-atlas.md](../architecture/rendering/shadow-atlas.md), [testing/rendering/shadow-validation.md](testing/rendering/shadow-validation.md), [design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md](design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md), [design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md](design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md), [design/rendering/shadows/shadow-resource-migration-audit.md](design/rendering/shadows/shadow-resource-migration-audit.md), [design/rendering/shadows/post-v1-advanced-shadow-features-plan.md](design/rendering/shadows/post-v1-advanced-shadow-features-plan.md) | Single master TODO covering atlas allocator and relevance, dynamic atlas/LOD allocation, VSM/EVSM filtering, and contact-shadow optimizations for directional, spot, and point lights. |
| Texture runtime, streaming, and virtual texturing | Active | [../architecture/rendering/texture-streaming.md](../architecture/rendering/texture-streaming.md), [design/texturing/texture-runtime-streaming-virtual-texturing-design.md](design/texturing/texture-runtime-streaming-virtual-texturing-design.md), [design/texturing/texture-compression-and-cooked-cache-design.md](design/texturing/texture-compression-and-cooked-cache-design.md), [todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md](todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md), [todo/texturing/texture-compression-and-cooked-cache-todo.md](todo/texturing/texture-compression-and-cooked-cache-todo.md), [testing/texturing/texture-validation.md](testing/texturing/texture-validation.md) | Canonical texturing roadmap. v1 mip streaming and the Vulkan dense upload service are implemented and documented in the architecture doc; scene validation is open. Next phases cover safe sparse pages, compressed cooked payloads, full SVT, Vulkan sparse residency, bindless deferred texturing, RVT, and neural compression. |
| OpenVR VRClient GPU handoff | Active | [todo/rendering/gpu/openvr-vrclient-gpu-handoff-todo.md](todo/rendering/gpu/openvr-vrclient-gpu-handoff-todo.md), [../architecture/rendering/openvr-vrclient-gpu-handoff.md](../architecture/rendering/openvr-vrclient-gpu-handoff.md), [testing/xr/openvr-vrclient-gpu-handoff-validation.md](testing/xr/openvr-vrclient-gpu-handoff-validation.md) | Zero-readback cross-process eye-texture handoff from the engine app to the legacy OpenVR companion process. |
| GPU-driven animation | Active | [todo/rendering/gpu/gpu-driven-animation-todo.md](todo/rendering/gpu/gpu-driven-animation-todo.md) | Phased execution tracker for the [GPU-driven animation architecture](design/rendering/gpu/gpu-driven-animation.md). |
| Skinning GPU efficiency follow-ups | Active | [todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md](todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md), [design/rendering/gpu/gpu-skinning-buffer-compression-plan.md](design/rendering/gpu/gpu-skinning-buffer-compression-plan.md) | Post-`Core4 + Spill` work for no-spill variants, mixed-precision palettes, dispatch reuse, and skinning LOD. |
| Blendshape compression and GPU efficiency | Active | [todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md](todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md), [avatar skin, skeleton, and blendshape items](todo/avatar/avatar-optimization-roadmap.md#skin-skeleton-and-blendshapes) | Runtime blendshape delta/weight compression, active-shape compaction, dispatch skipping, and blendshape LOD. |
| Dedicated render-thread window ownership | Closed (code); validation open | [design/rendering/dedicated-render-thread-window-ownership-plan.md](design/rendering/dedicated-render-thread-window-ownership-plan.md), [../architecture/rendering/window-creation-and-renderer-init.md](../architecture/rendering/window-creation-and-renderer-init.md#window-ownership-and-render-thread), [testing/rendering/window-and-render-thread-validation.md](testing/rendering/window-and-render-thread-validation.md) | Render-thread host, SDL split window pump prototype, mailboxes, snapshots, and resize extents are implemented. Remaining audits are code items in the [Vulkan master todo](todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#window-ownership-and-render-thread). |
| Resolved shader source optimization | Active | [todo/rendering/resolved-shader-source-optimization-todo.md](todo/rendering/resolved-shader-source-optimization-todo.md) | Architectural restructuring to resolve all shader includes/snippets first, then prune compiler-facing source generically for every shader family. |
| XRDataBuffer RHI write model | Stable doc + testing | [../architecture/rendering/xrdatabuffer-rhi-write-model.md](../architecture/rendering/xrdatabuffer-rhi-write-model.md), [testing/rendering/xrdatabuffer-rhi-write-model-validation.md](testing/rendering/xrdatabuffer-rhi-write-model-validation.md) | Core write model and representative migrations have landed; remaining work is hardware, barrier, and GPU submission strategy validation. |
| Vulkan ReSTIR radiance cache GI | Active | [todo/rendering/vulkan-restir-radiance-cache-gi-todo.md](todo/rendering/vulkan-restir-radiance-cache-gi-todo.md) | Vulkan-native KHR acceleration-structure, ray-query, and RT-pipeline roadmap for ReSTIR radiance-cached GI while preserving the current OpenGL native bridge. |
| Vulkan Fossilize integration | Planned | [design/rendering/vulkan-fossilize-integration-design.md](design/rendering/vulkan-fossilize-integration-design.md) | Optional Valve Fossilize tooling, layer capture, replay, and native-recording design. No Fossilize code exists. The master todo links the design from its extension backlog. |
| Vulkan wrapper parity | Active | [todo/rendering/vulkan-wrapper-parity-todo.md](todo/rendering/vulkan-wrapper-parity-todo.md) | Open code items for Vulkan wrapper parity with OpenGL. Checks: [testing/rendering/vulkan-backend-parity-validation.md](testing/rendering/vulkan-backend-parity-validation.md). |
| GPU softbody rigging | Active | [todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md](todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md) | Still an active work item. |
| Voxel cone tracing / VXAO | Active | [todo/rendering/global-illumination/voxel-cone-tracing-and-vxao-implementation-todo.md](todo/rendering/global-illumination/voxel-cone-tracing-and-vxao-implementation-todo.md) | Shared-voxel roadmap item. |
| DDGI integration | Active | [todo/rendering/global-illumination/ddgi-implementation-todo.md](todo/rendering/global-illumination/ddgi-implementation-todo.md) | Execution tracker derived from the [design/global-illumination/ddgi-integration-plan.md](design/global-illumination/ddgi-integration-plan.md) roadmap. |
| Multiplayer networking / dedicated server orchestration | Stable doc | [../developer-guides/networking/networking.md](../developer-guides/networking/networking.md) | The completed realtime cleanup tracker was folded into the stable feature guide. Peer-to-peer host switching is tracked in [design/networking/peer-to-peer-host-switching.md](design/networking/peer-to-peer-host-switching.md). |
| Vulkan core frame loop and resident rendering | Active | [todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md](todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md), [testing/rendering/vulkan-core-validation.md](testing/rendering/vulkan-core-validation.md), [../developer-guides/rendering/vulkan-diagnostics.md](../developer-guides/rendering/vulkan-diagnostics.md) | Master Vulkan code todo: frame transactions, resident bins, telemetry, window ownership, resize, extension backlog, and production cutover. The old `todo/vulkan.md` backlog is deleted. |
| Source-backed C# script components | Active | [design/scripting/source-backed-csharp-script-components.md](design/scripting/source-backed-csharp-script-components.md) | Stable proxy/materialization design for `.cs` assets that can be attached before they compile. |
| Default pipeline regressions (2026-03-28) | Active | [audit/COMPLETED/default-render-pipeline-regression-diagnosis-2026-03-28.md](audit/COMPLETED/default-render-pipeline-regression-diagnosis-2026-03-28.md) | AO, AA, deferred grayscale, and sampler-binding diagnosis. |
| Affine matrix rollout | Closed | [audit/COMPLETED/affine-matrix-phase4-closeout-2026-03-19.md](audit/COMPLETED/affine-matrix-phase4-closeout-2026-03-19.md) | Final consolidated closeout record. |
| Steam Audio | Stable doc | [../developer-guides/audio/steam-audio.md](../developer-guides/audio/steam-audio.md) | Remaining validation now lives in the stable feature doc. |
| Remote profiler | Stable doc | [../developer-guides/diagnostics/profiler.md](../developer-guides/diagnostics/profiler.md) | Design notes and the closed rendering profiler and benchmarking tracker were merged into the stable feature doc. Open meshlet and stereo capture checks are in [testing/rendering/gpu-driven-submission-validation.md](testing/rendering/gpu-driven-submission-validation.md) and [testing/xr/openxr-validation.md](testing/xr/openxr-validation.md). |

## Active TODOs

Code todos list only open implementation items. Checks are in the [testing docs](testing/README.md). Use the code todo template in [Work Doc Templates](../developer-guides/ai/work-doc-templates.md#code-todo).

### animation

- [todo/animation/baked-value-compression-todo.md](todo/animation/baked-value-compression-todo.md)

### assets

- [todo/assets/fbx-import-export-todo.md](todo/assets/fbx-import-export-todo.md)
- [todo/assets/model-import-binary-cache-todo.md](todo/assets/model-import-binary-cache-todo.md)
- [todo/assets/usd-import-export-todo.md](todo/assets/usd-import-export-todo.md)

### avatar

- [todo/avatar/avatar-optimization-roadmap.md](todo/avatar/avatar-optimization-roadmap.md)
- [todo/avatar/humanoid-body-root-compensation-todo.md](todo/avatar/humanoid-body-root-compensation-todo.md)
- [todo/avatar/vr-full-body-estimation-todo.md](todo/avatar/vr-full-body-estimation-todo.md)

### modeling

- [todo/modeling/modeling-todo.md](todo/modeling/modeling-todo.md)

### networking

- [todo/networking/control-plane-managed-server-instances-todo.md](todo/networking/control-plane-managed-server-instances-todo.md)
- [todo/networking/peer-to-peer-host-switching-todo.md](todo/networking/peer-to-peer-host-switching-todo.md)

### physics

- [todo/physics/box3d-backend-integration-todo.md](todo/physics/box3d-backend-integration-todo.md)
- [todo/physics/jolt-character-controller-correctness-todo.md](todo/physics/jolt-character-controller-correctness-todo.md)
- [todo/physics/physics-chain-thousands-scale-optimization-todo.md](todo/physics/physics-chain-thousands-scale-optimization-todo.md)
- [Physics chain scale implementation status](progress/physics/physics-chain-scale-status-2026-10-07.md)

### platform

- [todo/platform/unified-desktop-browser-runtime-todo.md](todo/platform/unified-desktop-browser-runtime-todo.md)

### rendering

- [todo/rendering/advanced-pipeline-antialiasing-todo.md](todo/rendering/advanced-pipeline-antialiasing-todo.md)
- [todo/rendering/cpu-async-query-camera-motion-todo.md](todo/rendering/cpu-async-query-camera-motion-todo.md)
- [todo/rendering/default-pipeline-depth-of-field-todo.md](todo/rendering/default-pipeline-depth-of-field-todo.md)
- [todo/rendering/forward-depth-normal-transform-id-todo.md](todo/rendering/forward-depth-normal-transform-id-todo.md)
- [todo/rendering/masked-software-occlusion-culling-todo.md](todo/rendering/masked-software-occlusion-culling-todo.md)
- [todo/rendering/render-pipeline-resource-lifecycle-todo.md](todo/rendering/render-pipeline-resource-lifecycle-todo.md)
- [todo/rendering/resolved-shader-source-optimization-todo.md](todo/rendering/resolved-shader-source-optimization-todo.md)
- [todo/rendering/sky-and-atmosphere-followups-todo.md](todo/rendering/sky-and-atmosphere-followups-todo.md)
- [todo/rendering/transparency-and-oit-todo.md](todo/rendering/transparency-and-oit-todo.md)
- [todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md](todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md)
- [todo/rendering/vulkan-core-hardening-and-device-loss-todo.md](todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)
- [todo/rendering/vulkan-restir-radiance-cache-gi-todo.md](todo/rendering/vulkan-restir-radiance-cache-gi-todo.md)
- [todo/rendering/vulkan-stall-separate-findings-todo.md](todo/rendering/vulkan-stall-separate-findings-todo.md)
- [todo/rendering/vulkan-wrapper-parity-todo.md](todo/rendering/vulkan-wrapper-parity-todo.md)
- [todo/rendering/vulkan-xr-and-advanced-rendering-todo.md](todo/rendering/vulkan-xr-and-advanced-rendering-todo.md)

### rendering/global-illumination

- [todo/rendering/global-illumination/ddgi-implementation-todo.md](todo/rendering/global-illumination/ddgi-implementation-todo.md)
- [todo/rendering/global-illumination/lpvgi-implementation-todo.md](todo/rendering/global-illumination/lpvgi-implementation-todo.md)
- [todo/rendering/global-illumination/modular-gi-architecture-todo.md](todo/rendering/global-illumination/modular-gi-architecture-todo.md)
- [todo/rendering/global-illumination/radiance-cascades-runtime-completion-todo.md](todo/rendering/global-illumination/radiance-cascades-runtime-completion-todo.md)
- [todo/rendering/global-illumination/surfel-gi-repair-todo.md](todo/rendering/global-illumination/surfel-gi-repair-todo.md)
- [todo/rendering/global-illumination/voxel-cone-tracing-and-vxao-implementation-todo.md](todo/rendering/global-illumination/voxel-cone-tracing-and-vxao-implementation-todo.md)

### rendering/gpu

- [todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md](todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md)
- [todo/rendering/gpu/gpu-driven-animation-todo.md](todo/rendering/gpu/gpu-driven-animation-todo.md)
- [todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md](todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md)
- [todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md](todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md)
- [todo/rendering/gpu/openvr-vrclient-gpu-handoff-todo.md](todo/rendering/gpu/openvr-vrclient-gpu-handoff-todo.md)
- [todo/rendering/gpu/production-rendering-pipeline-roadmap.md](todo/rendering/gpu/production-rendering-pipeline-roadmap.md)
- [todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md](todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md)

### rendering/optimization

- [todo/rendering/optimization/compact-zero-readback-rendering-todo.md](todo/rendering/optimization/compact-zero-readback-rendering-todo.md)
- [todo/rendering/optimization/cpu-direct-fast-path-todo.md](todo/rendering/optimization/cpu-direct-fast-path-todo.md)
- [todo/rendering/optimization/default-pipeline-gpu-hotspots-todo.md](todo/rendering/optimization/default-pipeline-gpu-hotspots-todo.md)
- [todo/rendering/optimization/editor-memory-reduction-todo.md](todo/rendering/optimization/editor-memory-reduction-todo.md)
- [todo/rendering/optimization/editor-profiler-ui-render-cost-todo.md](todo/rendering/optimization/editor-profiler-ui-render-cost-todo.md)
- [todo/rendering/optimization/engine-rendering-optimization-roadmap.md](todo/rendering/optimization/engine-rendering-optimization-roadmap.md)
- [todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md](todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md)
- [todo/rendering/optimization/vr-rendering-performance-contract-todo.md](todo/rendering/optimization/vr-rendering-performance-contract-todo.md)

### rendering/shadows

- [todo/rendering/shadows/shadow-atlas-overhaul-todo.md](todo/rendering/shadows/shadow-atlas-overhaul-todo.md)

### rendering/vr

- [todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md](todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md)
- [todo/rendering/vr/openxr-future-work-todo.md](todo/rendering/vr/openxr-future-work-todo.md)
- [todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md](todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md)
- [todo/rendering/vr/openxr-runtime-code-organization-todo.md](todo/rendering/vr/openxr-runtime-code-organization-todo.md)
- [todo/rendering/vr/openxr-vulkan-parallel-eye-recording-todo.md](todo/rendering/vr/openxr-vulkan-parallel-eye-recording-todo.md)
- [todo/rendering/vr/retinal-visibility-cache-debug-views-todo.md](todo/rendering/vr/retinal-visibility-cache-debug-views-todo.md)
- [todo/rendering/vr/vr-mirror-cyclopean-reconstruction-todo.md](todo/rendering/vr/vr-mirror-cyclopean-reconstruction-todo.md)

### runtime

- [todo/runtime/runtime-data-layout-and-generated-contracts-todo.md](todo/runtime/runtime-data-layout-and-generated-contracts-todo.md)
- [todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md](todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md)

### tests

- [todo/tests/openxr-timing-tests-todo.md](todo/tests/openxr-timing-tests-todo.md)
- [todo/tests/unit-test-project-reorganization-todo.md](todo/tests/unit-test-project-reorganization-todo.md)

### texturing

- [todo/texturing/texture-compression-and-cooked-cache-todo.md](todo/texturing/texture-compression-and-cooked-cache-todo.md)
- [todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md](todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md)

### ui

- [todo/ui/native-ui-retained-performance-and-editor-parity-todo.md](todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
## Active Design Docs

- [design/transforms/affine-matrix-integration-plan.md](design/transforms/affine-matrix-integration-plan.md)
- [design/rendering/advanced-flat-mirror-rendering-design.md](design/rendering/advanced-flat-mirror-rendering-design.md)
- [design/platform/apple-platform-moltenvk-support-design.md](design/platform/apple-platform-moltenvk-support-design.md)
- [design/platform/unified-desktop-browser-runtime-design.md](design/platform/unified-desktop-browser-runtime-design.md)
- [design/rendering/atmospheric-scattering-component-design.md](design/rendering/atmospheric-scattering-component-design.md)
- [design/cuda-usage-opportunities-design.md](design/cuda-usage-opportunities-design.md)
- [design/rendering/default-render-pipeline-improvement-plan.md](design/rendering/default-render-pipeline-improvement-plan.md)
- [design/global-illumination/ddgi-integration-plan.md](design/global-illumination/ddgi-integration-plan.md)
- [design/rendering/dedicated-render-thread-window-ownership-plan.md](design/rendering/dedicated-render-thread-window-ownership-plan.md)
- [design/rendering/engine-optimization-and-avatar-optimizer-design.md](design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [design/rendering/browser-wasm-renderer-design.md](design/rendering/browser-wasm-renderer-design.md)
- [design/rendering/moltenvk-vulkan-renderer-integration-design.md](design/rendering/moltenvk-vulkan-renderer-integration-design.md)
- [design/rendering/avatar-optimization-and-virtualized-rendering-design.md](design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [design/rendering/deferred-plus-render-path-design.md](design/rendering/deferred-plus-render-path-design.md)
- [design/rendering/dynamic-indirect-material-bindings.md](design/rendering/dynamic-indirect-material-bindings.md)
- [design/rendering/gpu-meshlet-zero-readback-rendering-design.md](design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [design/rendering/vulkan-render-loop-target-architecture.md](design/rendering/vulkan-render-loop-target-architecture.md)
- [design/rendering/volumetric-fog-production-design.md](design/rendering/volumetric-fog-production-design.md)
- [design/assets/model-import-binary-cache-design.md](design/assets/model-import-binary-cache-design.md)
- [design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md](design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md)
- [design/rendering/gpu/gpu-skinning-buffer-compression-plan.md](design/rendering/gpu/gpu-skinning-buffer-compression-plan.md)
- [design/rendering/gpu/gpu-driven-animation.md](design/rendering/gpu/gpu-driven-animation.md)
- [design/rendering/gpu/gpu-render-pass-pipeline.md](design/rendering/gpu/gpu-render-pass-pipeline.md)
- [design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md](design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md)
- [design/rendering/hbao-hbao-plus-implementation-plan.md](design/rendering/hbao-hbao-plus-implementation-plan.md)
- [design/networking/networking.md](design/networking/networking.md)
- [design/VR/openxr-monado-testing-pipeline.md](design/VR/openxr-monado-testing-pipeline.md)
- [design/networking/peer-to-peer-host-switching.md](design/networking/peer-to-peer-host-switching.md)
- [design/UI/native-hierarchy-porting-plan.md](design/UI/native-hierarchy-porting-plan.md)
- [design/VR/openxr-implementation-comparison.md](design/VR/openxr-implementation-comparison.md)
- [design/rendering/shadows/post-v1-advanced-shadow-features-plan.md](design/rendering/shadows/post-v1-advanced-shadow-features-plan.md)
- [design/runtime/editor-crash-telemetry-design.md](design/runtime/editor-crash-telemetry-design.md)
- [design/runtime/runtime-data-layout-and-generated-contracts-design.md](design/runtime/runtime-data-layout-and-generated-contracts-design.md)
- [design/runtime-modularization-plan.md](design/runtime-modularization-plan.md)
- [design/rendering/shadows/shadow-pass-material-binding-optimization-plan.md](design/rendering/shadows/shadow-pass-material-binding-optimization-plan.md)
- [design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md](design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md)
- [design/rendering/shadows/shadow-resource-migration-audit.md](design/rendering/shadows/shadow-resource-migration-audit.md)
- [design/scripting/slang-shader-cross-compile-plan.md](design/scripting/slang-shader-cross-compile-plan.md)
- [design/scripting/source-backed-csharp-script-components.md](design/scripting/source-backed-csharp-script-components.md)
- [design/rendering/transparency-and-oit-implementation-plan.md](design/rendering/transparency-and-oit-implementation-plan.md)
- [design/texturing/texture-runtime-streaming-virtual-texturing-design.md](design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [design/global-illumination/vxao-implementation-plan.md](design/global-illumination/vxao-implementation-plan.md)
- [design/rendering/zero-readback-gpu-driven-rendering-plan.md](design/rendering/zero-readback-gpu-driven-rendering-plan.md)

## Generated Reports

Generated audit outputs should be treated as disposable report artifacts rather than durable work docs. Regenerate them from the corresponding VS Code tasks or report scripts when needed.

## Testing Docs

See the [testing docs index](testing/README.md).

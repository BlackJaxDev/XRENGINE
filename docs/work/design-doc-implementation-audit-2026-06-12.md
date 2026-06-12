# Design Doc Implementation Audit

Date: 2026-06-12

Scope: all markdown files under `docs/work/design/`, compared against existing todo docs under `docs/work/todo/` and static source/test evidence in the worktree.

Method: static audit with `rg` over docs, source, shaders, tests, and project files. No build, editor launch, or runtime validation was run. This audit treats the current worktree as the source of truth, including untracked todo archives such as `docs/work/todo/COMPLETED/`.

Promotion cleanup started: the first completed-doc batch has been promoted to `docs/developer-guides/audio/openal-streaming-audio.md`, `docs/developer-guides/ai/mcp-assistant.md`, `docs/developer-guides/ui/native-hierarchy-panel.md`, `docs/developer-guides/rendering/shadows/surface-detail-forward-shadows.md`, and `docs/developer-guides/vr/openxr-runtime.md`.

## Status Legend

- `Implemented`: source and tests align closely enough that the design doc can be rewritten or moved into feature, architecture, or implementation docs now.
- `Implemented core`: the main feature is present, but validation, platform parity, diagnostics, polish, or follow-up TODOs remain.
- `Partial`: meaningful code exists, but the design target is broader than the current implementation.
- `Not implemented`: no meaningful implementation was found for the design target.
- `Historical`: useful implementation record, but not the current canonical plan.
- `Missing TODO`: no dedicated todo doc was found, or coverage is weak enough that the design is not really translated into actionable tasks.

## Headline Findings

Design docs that are ready to become feature, architecture, or implementation docs:

- `audio/OPENAL_STREAMING_NOTES.md`
- `networking/networking.md` for the in-engine realtime networking subset, with control-plane work explicitly out of repo
- `rendering/atmospheric-scattering-component-design.md`, already backed by `docs/developer-guides/components/atmospheric-scattering.md`
- `rendering/gpu/gpu-skinning-buffer-compression-plan.md`, with remaining efficiency work tracked separately
- `rendering/shadows/surface-detail-and-forward-shadow-debugging.md`
- `texturing/sparse-texture-streaming-plan.md` and `texturing/texture-management-runtime-design.md`, as historical parts of the current texture streaming feature
- `transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md`, already backed by `docs/developer-guides/components/gpu-physics-chain.md`
- `UI/mcp-assistant-system-prompt.md`
- `UI/native-hierarchy-porting-plan.md`
- `VR/openxr-implementation-comparison.md`

Implemented-core docs that should stay out of permanent feature docs until follow-ups are closed or clearly marked experimental:

- `rendering/default-render-pipeline-improvement-plan.md`
- `rendering/dynamic-indirect-material-bindings.md`
- `rendering/gpu-meshlet-zero-readback-rendering-design.md`
- `rendering/masked-software-occlusion-culling-design.md`
- `rendering/transparency-and-oit-implementation-plan.md`
- `rendering/volumetric-fog-production-design.md`
- `rendering/vulkan-dynamic-rendering-migration-design.md`
- `rendering/zero-readback-gpu-driven-rendering-plan.md`
- `texturing/texture-runtime-streaming-virtual-texturing-design.md`

Design docs that are not implemented or are weakly tracked by TODOs:

- `global-illumination/neural irradiance volumes.md`
- `rendering/advanced-flat-mirror-rendering-design.md`
- `rendering/cyclopean-reconstruction.md`
- `rendering/retinal-visibility-cache-rendering-design.md`
- `rendering/vulkan-shader-object-pipeline-replacement-design.md`
- `rendering/xre-virtual-geometry-design.md`
- `scripting/slang-shader-cross-compile-plan.md`
- `scripting/source-backed-csharp-script-components.md`

Partial docs with weak or missing TODO coverage:

- `rendering/dedicated-render-thread-window-ownership-plan.md`
- `rendering/hbao-hbao-plus-implementation-plan.md`
- `rendering/shadows/shadow-pass-material-binding-optimization-plan.md`
- `rendering/volumetric-fog-production-design.md`
- `transforms/affine-matrix-integration-plan.md`

## Per-doc Classification

### Assets And Audio

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`assets/model-import-binary-cache-design.md`](design/assets/model-import-binary-cache-design.md) | Not implemented, TODO exists | `docs/work/todo/assets/model-import-binary-cache-todo.md` is active and unchecked. Source has model importers, meshlets, FBX work, and texture streaming, but no completed cooked binary model cache matching the design. | Keep as design plus TODO. Do not rewrite as a feature doc yet. |
| [`assets/imported-mesh-instance-dedupe-design.md`](design/assets/imported-mesh-instance-dedupe-design.md) | Partial or new design, TODO not confirmed | Current source has mesh import and renderer instance concepts, but this audit did not find a dedicated todo by exact design name. | Add or link a dedicated TODO if this is still planned. |
| [`audio/OPENAL_STREAMING_NOTES.md`](design/audio/OPENAL_STREAMING_NOTES.md) | Implemented | `OpenALTransport`, `UIVideoComponent.Audio`, frame drain/pipeline code, and audio diagnostics/regression tests reflect the lessons in the note: manual buffer ownership, autoplay suppression, format-change flush, sample-clock based timing, underrun recovery, drift/drop telemetry, and latency compensation. | Promoted to `docs/developer-guides/audio/openal-streaming-audio.md`; keep this design note as debugging history. |

### Cross-cutting GPU And CUDA

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`cuda-usage-opportunities-design.md`](design/cuda-usage-opportunities-design.md) | Partial guidance | CUDA/NVIDIA interop exists through `CudaInterop`, `NvCompInterop`, setup tooling, and Audio2Face bridge code. Candidate features are uneven: animated Gaussian splats are not implemented, while GPU softbody has a partial implementation and TODO. | Keep as guidance or split into per-feature TODOs. Do not present the whole document as implemented. |

### Global Illumination

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`global-illumination/ddgi-integration-plan.md`](design/global-illumination/ddgi-integration-plan.md) | Not implemented, TODO exists | `docs/work/todo/rendering/gi/ddgi-integration-todo.md` exists and is unchecked. `EGlobalIlluminationMode` does not expose DDGI, and no DDGI runtime was found. | Keep as planned work. |
| [`global-illumination/lpv-global-illumination-design.md`](design/global-illumination/lpv-global-illumination-design.md) | Not implemented, TODO exists | `docs/work/todo/rendering/gi/lpv-global-illumination-todo.md` exists and is unchecked. No LPV renderer/runtime implementation was found. | Keep as planned work. |
| [`global-illumination/neural irradiance volumes.md`](<design/global-illumination/neural irradiance volumes.md>) | Not implemented, Missing TODO | No neural irradiance volume runtime or dedicated TODO was found. Related texture/neural notes mention future work but do not translate this design. | Create a TODO or archive as research. |
| [`global-illumination/vxao-implementation-plan.md`](design/global-illumination/vxao-implementation-plan.md) | Partial, TODO exists | `docs/work/todo/rendering/gi/vxao-vct-integration-todo.md` exists. Source has VXAO/VCT-facing settings and a placeholder voxel cone tracing pass, but the pass is still scaffold-level. | Keep as active design/TODO work. |

### Modeling

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`modeling/gpu-accelerated-modeling-tools-design.md`](design/modeling/gpu-accelerated-modeling-tools-design.md) | Partial, TODO exists | The modeling projects, editable mesh conversion, boolean operations, modeling bridge tests, import/export hooks, and editor mesh editing component exist. The roadmap TODOs for geometry nodes, OpenSubdiv, GPU preview, sculpting, UVs, retopo, and full tool framework are still effectively planned. | Keep as roadmap. Consider splitting implemented bridge/import-export material into feature docs. |

### Networking

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`networking/networking.md`](design/networking/networking.md) | Implemented for engine realtime subset | `docs/developer-guides/networking/networking.md` exists. Code and tests support authoritative replication, UDP sequencing, world bootstrap, and data-plane responsibilities. Matchmaking/control-plane ownership is explicitly out of repo. | Keep feature doc as canonical. Move remaining design-only material into roadmap notes if needed. |
| [`networking/peer-to-peer-host-switching.md`](design/networking/peer-to-peer-host-switching.md) | Not implemented, TODO exists | `docs/work/todo/networking/peer-to-peer-host-switching-todo.md` exists and is unchecked. No peer-to-peer host switching runtime was found. | Keep as planned work. |

### Runtime Modularization

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`runtime-modularization-plan.md`](design/runtime-modularization-plan.md) | Partial, TODO exists | Several runtime split projects and integration tests exist, including rendering, audio, input, animation, and modeling-related assemblies. `docs/work/todo/runtime-modularization-phase3-todo.md` still tracks remaining phase 3 work. | Keep as active roadmap until phase 3 is complete, then move stable package boundaries into architecture docs. |

### Rendering Core, Optimization, And Visibility

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`rendering/advanced-flat-mirror-rendering-design.md`](design/rendering/advanced-flat-mirror-rendering-design.md) | Not implemented, Missing TODO | No `FlatMirror` or matching planar mirror render feature was found. Existing mirror references are transform-level, not the render design. | Create a TODO or archive as research. |
| [`rendering/atmospheric-scattering-component-design.md`](design/rendering/atmospheric-scattering-component-design.md) | Implemented core | `AtmosphericScatteringComponent`, pipeline insertion, shaders, and settings/contract/resource tests exist. `docs/developer-guides/components/atmospheric-scattering.md` already exists. Remaining TODOs are validation, stereo, and platform parity. | Keep feature doc canonical; reduce design doc to history or link to remaining TODO. |
| [`rendering/avatar-optimization-and-virtualized-rendering-design.md`](design/rendering/avatar-optimization-and-virtualized-rendering-design.md) | Not implemented as an optimizer, TODO exists | Avatar roadmap TODOs exist, and lower-level pieces such as skinning, blendshapes, meshlets, and streaming exist. No `AvatarOptimizer` or integrated virtualized avatar pipeline was found. | Keep as roadmap. Do not rewrite as feature doc. |
| [`rendering/cyclopean-reconstruction.md`](design/rendering/cyclopean-reconstruction.md) | Not implemented, Missing TODO | No cyclopean reconstruction code or dedicated TODO was found. | Create a TODO or archive as research. |
| [`rendering/dedicated-render-thread-window-ownership-plan.md`](design/rendering/dedicated-render-thread-window-ownership-plan.md) | Partial, Missing TODO | Render-thread and windowing infrastructure exists, but this audit did not confirm full implementation of the ownership plan or find a dedicated TODO. | Add a tracker if the ownership migration is still desired. |
| [`rendering/default-render-pipeline-improvement-plan.md`](design/rendering/default-render-pipeline-improvement-plan.md) | Implemented core, TODO exists | The V2 command/pipeline shape is largely present. `docs/work/todo/rendering/default-render-pipeline-v2-todo.md` tracks remaining manual pass migration, uniform ownership cleanup, probe SSBO work, and validation. | Convert completed invariants to architecture docs and leave the TODO for remaining migration. |
| [`rendering/dynamic-indirect-material-bindings.md`](design/rendering/dynamic-indirect-material-bindings.md) | Partial, Missing dedicated TODO | `MaterialBindingLayout`, GPU material tables, GLSL generation, hybrid renderer paths, and tests exist. The design still lists visual smoke, Forward+ promotion, Vulkan validation, inspector UI, and merge work. | Add a dedicated TODO or fold into the GPU rendering roadmap before making this a feature doc. |
| [`rendering/engine-optimization-and-avatar-optimizer-design.md`](design/rendering/engine-optimization-and-avatar-optimizer-design.md) | Partial, TODO exists | `docs/work/todo/rendering/optimization/engine-rendering-optimization-roadmap-todo.md` exists. Several rendering optimizations are implemented, but the broad avatar optimizer plan remains active roadmap work. | Keep as roadmap; split implemented profiler/zero-readback pieces into feature docs separately. |
| [`rendering/gpu-meshlet-zero-readback-rendering-design.md`](design/rendering/gpu-meshlet-zero-readback-rendering-design.md) | Implemented core | Meshlet zero-readback source, strategy resolver, GPU scene/material table integration, profiler counters, and phase tests exist. Completed TODO archive records phases 0-9 done; hardware/perf validation and production rollout remain in active roadmap. | Make an experimental/architecture feature doc only if the remaining validation caveat is explicit. |
| [`rendering/hbao-hbao-plus-implementation-plan.md`](design/rendering/hbao-hbao-plus-implementation-plan.md) | Partial, Missing dedicated TODO | HBAO/HBAO+ settings and pass names exist, but the design says they currently alias to the Multi-View AO path. No dedicated HBAO TODO was found. | Add a tracker if real HBAO/HBAO+ implementation remains desired. |
| [`rendering/masked-software-occlusion-culling-design.md`](design/rendering/masked-software-occlusion-culling-design.md) | Implemented core, TODO exists | `MaskedOcclusionBuffer`, CPU software culler, telemetry, ImGui panel, and tests exist. The TODO has only selector/budget follow-ups remaining. | Promote to feature/architecture docs after those follow-ups or mark them as known limitations. |
| [`rendering/render-pipeline-resource-lifecycle-design.md`](design/rendering/render-pipeline-resource-lifecycle-design.md) | Not implemented target, TODO exists | `docs/work/todo/rendering/render-pipeline-resource-lifecycle-todo.md` exists and is proposed. Some descriptor/planner/cache bridge code exists, but the generation-owned lifecycle and atomic resource swap model are not complete. | Keep as planned work. |
| [`rendering/render-submission-perf-debug-plan.md`](design/rendering/render-submission-perf-debug-plan.md) | Historical and partial | Profiler counters, benchmarking docs, and render optimization roadmaps exist. Some completed profiler TODOs are archived, while optimization work continues elsewhere. | Collapse completed profiling guidance into architecture docs and link active optimization TODOs. |
| [`rendering/retinal-visibility-cache-rendering-design.md`](design/rendering/retinal-visibility-cache-rendering-design.md) | Not implemented, weak TODO coverage | No retinal visibility cache code was found. It is mentioned by OpenXR future-work TODOs, but no dedicated tracker exists. | Create a TODO if this is still part of the rendering roadmap. |
| [`rendering/transparency-and-oit-implementation-plan.md`](design/rendering/transparency-and-oit-implementation-plan.md) | Implemented core, TODO exists | Transparency taxonomy, alpha coverage, weighted blended OIT, exact prototype paths, GPU counters/scaffolding, and tests exist. The TODO still tracks validation, diagnostics, docs, and production hardening. | Keep as active plan until validation/docs are closed, then promote to feature docs. |
| [`rendering/volumetric-fog-production-design.md`](design/rendering/volumetric-fog-production-design.md) | Implemented core, Missing current TODO | `VolumetricFog`, settings, shaders, and pipeline insertion exist for the OpenGL mono path. Production polish, XR parity, Vulkan parity, and diagnostics remain in the design, but no current dedicated TODO was found. | Add a TODO or rewrite as a feature doc with explicit limitations. |
| [`rendering/vulkan-dynamic-rendering-migration-design.md`](design/rendering/vulkan-dynamic-rendering-migration-design.md) | Implemented core, TODO exists | Dynamic rendering wrappers, mode selection, tests, and migration scaffolding exist. `docs/work/todo/rendering/vulkan-dynamic-rendering-migration-todo.md` now tracks remaining validation/parity work. | Keep TODO as source of truth; move completed migration invariants into architecture docs. |
| [`rendering/vulkan-shader-object-pipeline-replacement-design.md`](design/rendering/vulkan-shader-object-pipeline-replacement-design.md) | Not implemented, Missing TODO | No Vulkan shader object runtime was found. Current shader-object references are OpenGL/shader-source related, not Vulkan `VK_EXT_shader_object` replacement. | Create a TODO before implementation work starts. |
| [`rendering/xre-virtual-geometry-design.md`](design/rendering/xre-virtual-geometry-design.md) | Not implemented, weak TODO coverage | No `VirtualGeometry` implementation was found. The work is only loosely represented in production rendering roadmap references. | Create a dedicated TODO if this is still intended. |
| [`rendering/zero-readback-gpu-driven-rendering-plan.md`](design/rendering/zero-readback-gpu-driven-rendering-plan.md) | Implemented core | GPU scene, indirect phases, meshlet paths, material tables, strategy resolver, and phase tests exist. Active roadmaps still track production validation and rollout. | Convert stable contracts into feature/architecture docs; leave validation in roadmap TODOs. |

### Rendering GPU Subsystem

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`rendering/gpu/blendshape-deferred-gpu-efficiency-design.md`](design/rendering/gpu/blendshape-deferred-gpu-efficiency-design.md) | Partial, TODO exists | Blendshape runtime/tests exist, but the design's normal/tangent compression, accumulation caps, and final validation remain in `docs/work/todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md`. | Keep as active follow-up design. |
| [`rendering/gpu/gpu-driven-animation.md`](design/rendering/gpu/gpu-driven-animation.md) | Not implemented, TODO exists | `docs/work/todo/rendering/gpu/gpu-driven-animation-todo.md` exists and is unchecked. No `GpuDrivenAnimation` runtime was found. | Keep as planned work. |
| [`rendering/gpu/gpu-render-pass-pipeline.md`](design/rendering/gpu/gpu-render-pass-pipeline.md) | Partial or architecture note | `GPURenderPassCollection` and related GPU pass concepts exist, but this audit did not find a dedicated TODO tying the doc to remaining pass-pipeline work. | If it describes current behavior, move to architecture docs; otherwise add a TODO. |
| [`rendering/gpu/gpu-skinning-buffer-compression-plan.md`](design/rendering/gpu/gpu-skinning-buffer-compression-plan.md) | Implemented core, TODO exists for follow-ups | `SkinPaletteMatrix`, global skin palette buffers, skinning prepass dispatch, renderer palette state, and compression tests exist. `skinning-gpu-efficiency-followups-todo.md` tracks later mixed precision and dispatch reuse. | Update `docs/developer-guides/rendering/skinning.md` or add an architecture note for the implemented compression model. |
| [`rendering/gpu/gpu-softbody-mesh-rigging-plan.md`](design/rendering/gpu/gpu-softbody-mesh-rigging-plan.md) | Partial, TODO exists | `GPUSoftbodyComponent`, dispatcher, cluster math, compute shaders, and tests exist. The TODO says phase 3 shape-matching cluster runtime is implemented, with many runtime/editor/perf items still open. | Keep as active plan. |
| [`rendering/gpu/skinning-deferred-gpu-efficiency-design.md`](design/rendering/gpu/skinning-deferred-gpu-efficiency-design.md) | Partial, TODO exists | Core GPU skinning compression is implemented, but remaining deferred efficiency work is tracked in `skinning-gpu-efficiency-followups-todo.md`. | Keep as active follow-up design; do not convert wholesale to feature docs. |

### Shadows

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`rendering/shadows/dynamic-shadow-atlas-lod-plan.md`](design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md) | Partial, TODO exists | The shadow atlas overhaul TODO records implemented request bucketing, allocator, reuse/demotion, multi-page, directional atlas path, and relevance pieces. It also records incomplete directional perf, cascaded moments, scoring, compaction, diagnostics, and tests. | Keep folded into `shadow-atlas-overhaul-todo.md`. |
| [`rendering/shadows/post-v1-advanced-shadow-features-plan.md`](design/rendering/shadows/post-v1-advanced-shadow-features-plan.md) | Not implemented future work | This is mostly post-v1 feature planning. Some foundations exist through the shadow atlas work, but advanced items are not implemented. | Keep as future backlog, separate from v1 TODOs. |
| [`rendering/shadows/shadow-filtering-vsm-evsm-plan.md`](design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md) | Partial, TODO exists | Standalone VSM/EVSM receivers and local shadow support are present, but cascaded moments, moment atlases, and full filtering rollout remain in the shadow atlas overhaul TODO. | Keep as active design under the master shadow TODO. |
| [`rendering/shadows/shadow-pass-material-binding-optimization-plan.md`](design/rendering/shadows/shadow-pass-material-binding-optimization-plan.md) | Partial, Missing dedicated TODO | Shared opaque shadow material eligibility, `SettingShadowUniforms`, and tests exist. The broader binding-plan cache and diagnostics do not appear fully tracked by a dedicated TODO. | Add a TODO or fold explicit remaining tasks into the shadow atlas overhaul. |
| [`rendering/shadows/shadow-resource-migration-audit.md`](design/rendering/shadows/shadow-resource-migration-audit.md) | Historical | This reads as a completed audit/phase record and is partly superseded by the active shadow atlas overhaul TODO. | Archive as history or link from the shadow TODO. |
| [`rendering/shadows/surface-detail-and-forward-shadow-debugging.md`](design/rendering/shadows/surface-detail-and-forward-shadow-debugging.md) | Implemented | Import-time normal/height controls, surface detail shader logic, forward shadow caster material variants, light/shadow plumbing, and tests exist. | Promoted to `docs/developer-guides/rendering/shadows/surface-detail-forward-shadows.md`; keep this design note as implementation/debugging history. |

### Scripting

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`scripting/slang-shader-cross-compile-plan.md`](design/scripting/slang-shader-cross-compile-plan.md) | Not implemented, Missing TODO | No Slang integration was found. | Create a TODO before implementation starts. |
| [`scripting/source-backed-csharp-script-components.md`](design/scripting/source-backed-csharp-script-components.md) | Not implemented target, Missing TODO | Existing C# project loading/script support does not match the source-backed proxy/component architecture in the design. No dedicated TODO was found. | Create a TODO or archive as research. |

### Texturing

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`texturing/bindless-deferred-texturing-plan.md`](design/texturing/bindless-deferred-texturing-plan.md) | Historical and partial | Bindless/material-table concepts now live in dynamic material binding and virtual texture work. It is not the canonical current plan by itself. | Archive or link to the current material binding and texture runtime docs. |
| [`texturing/neural texture compression.md`](<design/texturing/neural texture compression.md>) | Not implemented, weak TODO coverage | No neural texture compression runtime was found. It is mentioned as future work in texture runtime docs, but no dedicated TODO was found. | Create a TODO if still planned, or keep as research. |
| [`texturing/sparse-texture-streaming-plan.md`](design/texturing/sparse-texture-streaming-plan.md) | Implemented core, historical | Imported texture streaming manager, registry records, transition queue, OpenGL residency backends, payload code, and tests exist. The current canonical plan is the runtime streaming/virtual texturing design and TODO. | Move stable behavior into texture streaming feature docs, archive this plan as history. |
| [`texturing/texture-compression-and-cooked-cache-design.md`](design/texturing/texture-compression-and-cooked-cache-design.md) | Partial, TODO exists | `docs/work/todo/texturing/texture-compression-and-cooked-cache-todo.md` exists. Baseline XRTS and cooked-cache foundations exist, but compression/transcoding work is not complete. | Keep as active plan. |
| [`texturing/texture-management-runtime-design.md`](design/texturing/texture-management-runtime-design.md) | Implemented core, historical | Runtime texture streaming manager, transition queue, registry, residency backend, payload, and tests exist. Historical TODOs are completed or folded into the current texture runtime tracker. | Rewrite as feature/architecture docs or fold into the canonical texture runtime doc. |
| [`texturing/texture-runtime-streaming-virtual-texturing-design.md`](design/texturing/texture-runtime-streaming-virtual-texturing-design.md) | Partial, TODO exists | V1 runtime streaming is implemented. Full virtual texturing, Vulkan parity, bindless integration, neural paths, and production diagnostics remain in `texture-runtime-streaming-virtual-texturing-todo.md`. | Keep as canonical active design until VT work is complete. |

### Transforms And Physics

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`transforms/affine-matrix-integration-plan.md`](design/transforms/affine-matrix-integration-plan.md) | Partial, Missing TODO | `AffineMatrix4x3`, spatial conversion code, transform accessor fast paths, and tests exist. Full integration rollout is not clearly tracked by a dedicated TODO. | Add a rollout TODO or rewrite only the implemented math type into architecture docs. |
| [`transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md`](design/transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md) | Implemented core | `docs/developer-guides/components/gpu-physics-chain.md` exists. Host service interfaces, physics chain runtime/tests, and skinning integration scaffolding exist. Remaining bounds/direct-integration polish should stay as follow-up work. | Keep feature doc canonical and archive design as implementation history. |

### UI

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`UI/mcp-assistant-system-prompt.md`](design/UI/mcp-assistant-system-prompt.md) | Implemented | `McpAssistantWindow.BuildSystemInstructions`, chat/tool integration, editor preferences, and context tests exist. | Promoted to `docs/developer-guides/ai/mcp-assistant.md`; keep this design note as prompt-design history. |
| [`UI/native-hierarchy-porting-plan.md`](design/UI/native-hierarchy-porting-plan.md) | Implemented | `HierarchyPanel`, context menus, collapsed state, drag/drop payloads, inline rename, active toggles, dirty tracking, multi-select helpers, and input/text components reflect the plan. | Promoted to `docs/developer-guides/ui/native-hierarchy-panel.md`, with the repo caveat that native UI is not the default editor path. |

### VR

| Design doc | Classification | TODO and code comparison | Recommendation |
| --- | --- | --- | --- |
| [`VR/openxr-implementation-comparison.md`](design/VR/openxr-implementation-comparison.md) | Implemented reference | OpenXR code and timing/future-work TODOs exist. The document is more of an architecture/reference comparison than a pending design. | Promoted to `docs/developer-guides/vr/openxr-runtime.md`; keep the comparison document as architecture/reference history. |
| [`VR/openxr-monado-testing-pipeline.md`](design/VR/openxr-monado-testing-pipeline.md) | Not implemented, partially translated to TODO | No Monado-specific pipeline code was found. `openxr-timing-tests-todo.md` includes a Monado mock runtime lane, but it is not a full pipeline tracker. | Create a dedicated Monado pipeline TODO if this remains desired. |

## Recommended Cleanup Order

1. Promote implemented docs first: audio OpenAL streaming, MCP assistant prompt, native hierarchy, surface detail/forward shadows, skinning compression, GPU physics chain, atmospheric scattering, texture streaming runtime, networking realtime, and OpenXR reference.
2. Add missing TODOs for unimplemented or weakly tracked designs: neural irradiance volumes, advanced flat mirror, cyclopean reconstruction, retinal visibility cache, Vulkan shader object, XRE virtual geometry, Slang cross-compile, source-backed C# scripts, HBAO/HBAO+, affine rollout, and volumetric fog production parity.
3. For implemented-core rendering systems, split the docs: stable invariants go to feature/architecture docs, while validation and platform parity remain in TODOs.
4. Archive or mark historical docs whose work moved elsewhere: sparse texture streaming, texture management runtime, bindless deferred texturing, render submission perf debug, and shadow resource migration audit.

# Todo Doc Cleanup TODO

Last Updated: 2026-10-06
Status: Active. Sections 1 to 4 are done, except two old source paths in section 1. `todo/COMPLETED/` is deleted. Tests no longer read Markdown files (owner rule, 2026-10-06). Sections 5 to 8 are next. Validation docs created during sections 2 to 4 hold unsorted checks under "Imported Checks"; section 5 shapes them.
Scope: All implementation trackers under `docs/work/todo/`, the trackers in `docs/work/todo/COMPLETED/`, and the todo-style trackers under `docs/work/testing/`.

Paths in this document are relative to `docs/work/` unless they start with `docs/`. Paths in backticks can be new files that this cleanup creates.

## Goal

Make each kind of document hold one kind of information:

| Document kind | Location | Holds | Does not hold |
|---|---|---|---|
| Code todo | `todo/<subsystem>/` | Open implementation, refactor, and test-code items. Each item names files, types, or behavior. | Manual runtime checks, screenshots, profiler captures, hardware rows, build logs, completed-work history. |
| Validation doc | `testing/<subsystem>/` | Manual, runtime, visual, hardware, profiler, benchmark, and soak checks. Acceptance matrices. Reproduction steps and evidence links. | Code changes. A failed check creates a code item or an investigation instead. |
| Investigation | `investigations/<subsystem>/` | Debug history, hypotheses, ruled-out causes, build-by-build logs. | Open backlog. |
| Architecture or guide | `docs/architecture/`, `docs/developer-guides/`, `docs/user-guide/` | Finished design: ownership, invariants, data layouts, flags, environment variables, type and file map, known limits. | Checkboxes and status. |

## Audit Snapshot

An audit on 2026-10-06 read about 100 active todo docs, 51 docs in `todo/COMPLETED/`, and three todo-style docs under `testing/rendering/`. It spot-checked the code with symbol and file searches. It did not run the engine. Counts are approximate.

Main findings:

- Most active todos mix code items with validation boxes. About 60% to 70% of unchecked boxes in the rendering optimization and RVC docs are validation, not code.
- About 20% to 35% of unchecked boxes are not work at all: standing rules, exit criteria, "known limits" text, and branch or merge steps.
- Several docs in `todo/COMPLETED/` still own open code items.
- Several active docs contradict the code. Their boxes are open for work that exists, or their "current state" text is old.
- Large docs keep build-by-build logs that belong in investigations.
- Many finished designs have no architecture doc.
- `README.md` has rows that point to missing documents and rows with an incorrect status.

## Working Rules For This Cleanup

- Do one subsystem per change set. Do not mix a doc move with content rewrites in the same commit when you can avoid it.
- Before you mark a code item done or remove it, confirm the code with a search. Do not trust the box state.
- Before you delete a doc, make sure that its finished design is in an architecture doc. Make sure also that its open code items are in an active todo and its open checks are in a validation doc.
- Copy only facts that a programmer needs. Do not copy history, dates of builds, or evidence paths under `Build/_AgentValidation/`.
- Keep implementation-plan phase names out of architecture docs. Describe behavior and invariants directly.
- After each move, fix all inbound links. Search `docs/` for the old file name.
- Use Simplified Technical English for all new and rewritten text.

## 1. Index And Link Repair

- [x] Fix `README.md` rows that point to missing documents. Remove each row or create the document:
  - `todo/animated-gaussian-cloud-capture-and-streaming-todo.md`
  - `todo/octahedral-billboard-capture-todo.md`
  - `todo/shader-and-snippet-optimization-todo.md`
  - `todo/rendering/opengl-shader-program-deduplication-todo.md`
  - `todo/rendering/vulkan-wrapper-parity/README.md`
  - `todo/xrmesh-vertex-remapper-optimizations.md`
  - `design/startup-fps-drop-remediation-plan.md`
- [x] Correct `README.md` status rows. These rows say Active, but their docs are complete or are closing:
  - Default render pipeline V2
  - Runtime modularization (it links to the phase 3 doc only)
  - OpenXR stereo and temporal isolation
  - CPU async query occlusion
  - Physics finalization and rendering profiler (Active TODO list)
- [x] Remove `todo/COMPLETED/` links from the `README.md` "Active TODOs" list after section 2 and section 4 are done.
- [x] Fix broken links inside docs:
  - `todo/transparency-and-oit-todo.md`: design link.
  - `todo/networking/peer-to-peer-host-switching-todo.md`: design link, and literal `\u2014` escapes in the text.
  - `todo/tests/openxr-timing-tests-todo.md` and two `todo/rendering/vr/` docs: links to the Monado testing tracker, which moved to `todo/COMPLETED/`.
  - All links to the missing `docs/developer-guides/testing.md`.
  - `todo/COMPLETED/texture-management-runtime-todo.md`, `texture-streaming-consolidation-todo.md`, `texture-streaming-cooked-cache-todo.md`: relative links point inside `COMPLETED/` instead of `../texturing/`.
  - `todo/avatar/gpu-skinned-bvh-proxy-lod-design.md`: relative links.
  - `todo/rendering/vulkan-fossilize-integration-todo.md`: dead link.
- [ ] Replace old source paths with current project paths:
  - `XRENGINE/Scene/...` and `XRENGINE/Core/...` in the Jolt character controller, physics chain, native UI, voxel cone tracing, GPU-driven animation, softbody, and OpenVR handoff docs.
  - `XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs`, `UnitySceneImporter`, `UnityAssetResolver`, and `UnityProjectImportContext` in the asset docs. The Unity code now uses `Source*` and `Serialized*` names.
  - OpenXR runtime paths. The code is now in `XREngine.Runtime.XR.OpenXR`.
  - Remaining: `XRENGINE/Engine/Engine.VRState.cs` in the OpenVR handoff todo (`VRState` no longer exists) and `VPRC_VoxelConeTracingPass.cs` in the voxel cone tracing todo (file does not exist). Rewrite these items in section 7.

## 2. Move Open Code Items Out Of `todo/COMPLETED/`

A doc in `todo/COMPLETED/` must not own open code items. For each doc below, move it back to an active folder, or move its open code items to the named active todo and then close it.

- [x] `COMPLETED/openxr-monado-vulkan-parallel-rendering-todo-2026-06-25.md`: reopened. Move to `todo/rendering/vr/`. Open items: eye output source, `OpenXR.Vulkan.SubmitFenceWait` cost, keep parallel recording.
- [x] `COMPLETED/openxr-vulkan-true-parallel-eye-primary-recording-todo.md`: active plan with about 26 code items. Move to `todo/rendering/vr/`. Open items: per-eye render contexts, collect-visible work split, default mode choice, diagnostics cleanup.
- [x] `COMPLETED/vulkan-render-query-system-upgrade-todo.md`: open child of the Vulkan master todo. Move its validation rows to the query validation doc (section 5) and close it. Do not leave it in `COMPLETED/` with open rows.
- [x] `COMPLETED/vulkan-wrapper-parity.md`: move these code items to `todo/rendering/vulkan-dynamic-rendering-migration-todo.md` or a new short `todo/rendering/vulkan-wrapper-parity-todo.md`:
  - patch-list and tessellation behavior
  - zero-readback and meshlet task-record behavior
  - `nonuniformEXT` audit for descriptor-array variants
  - render-option override priority
  - std140 and std430 offsets from reflection
  - render-graph pass metadata in OpenGL
  - format-feature checks before upload, blit, and mipmap
  - texture-view compatibility test
- [x] `COMPLETED/vulkan-vma-pinvoke-allocator-todo.md`: move to `todo/rendering/optimization/vulkan-managed-vma-concepts-allocator-todo.md`:
  - VMA flags, allocation names, owner metadata
  - external-memory allocations
  - no-silent-fallback check
  - CI for Debug and Release x64
  - heap and type statistics, allocation report, rejection warnings
  - native and P/Invoke smoke tests

  Close the JSON dump item. `VulkanVmaAllocator.BuildStatsString` and the `get_vulkan_memory_statistics` MCP tool exist.
- [x] `COMPLETED/vulkan-fully-bindless-materials-todo.md`: move descriptor-table debug names, material test cases, profiler counters, and descriptor-index validity across deferred passes to `todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md`.
- [x] `COMPLETED/vulkan-desktop-frame-loop-decomposition-todo.md`: move the test-code rows (4.9, 5.8b, 6.8 to 6.10, 7.8, 7.9b, 7.10, 8.11 to 8.14, 9.8, 10.11 to 10.14) to `testing/rendering/vulkan-core-validation.md` as test-code items.
- [x] `COMPLETED/default-render-pipeline-v2-todo.md`: move 10.8 (remove duplicate catalog entries) and 10.9 (light-probe publication) to the Vulkan XR and advanced rendering todo. Close 6.10. No `_probe*Buffer` fields remain.
- [x] `COMPLETED/default-pipeline-gpu-submission-strategy-todo.md`: move the instrumented-readback counter rule, render-graph metadata for GPU prep stages, GPU profiler grouping, and readback-counter tests to `todo/rendering/gpu/production-rendering-pipeline-roadmap.md`.
- [x] `COMPLETED/gpu-meshlet-zero-readback-rendering-todo.md`: move these items to the GPU roadmap. The five named unit tests do not exist in `XREngine.UnitTests`.
  - the five named unit tests
  - OpenGL and Vulkan mesh-shader integration tests
  - the allocation profiling pass
- [x] `COMPLETED/gpu-bvh-async-overflow-readback-todo.md`: move the Vulkan and DX12 fence host check and the live GPU fence test to the GPU roadmap.
- [x] `COMPLETED/dedicated-render-thread-window-ownership-todo.md`: move the four open audits to a short window and render-thread todo, or to the Vulkan master todo.
- [x] `COMPLETED/collect-visible-render-wait-decoupling-todo.md`: move the conditional follow-up (needs a clean capture first) to the Vulkan master todo.
- [x] `COMPLETED/openxr-monado-testing-pipeline-todo.md`: move the persistent OpenXR settings items and the Monado baseline and CI decisions to `todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md`.
- [x] `COMPLETED/vulkan-frame-loop-performance-todo.md`: confirm that each open item has an owner in a successor tracker. Then mark the doc as history only.

## 3. Retire Docs Whose Core Code Is Done

For each doc: move the finished design to the target, move code leftovers to the named active todo, move open checks to the validation doc, then delete the doc.

| Doc | Code leftovers (move first) | Architecture or guide target | Status |
|---|---|---|---|
| `todo/uber-shader-variant-builder-todo.md` | None. Profiling goes to validation; the merge and PR text items are not work. | `docs/architecture/rendering/uber-shader-varianting.md` (already complete) | Done. |
| `todo/games/monkeyball-vr-final-build-runtime-todo.md` | None | `docs/developer-guides/runtime/aot-final-game-builds.md` | Done. The sign-off check is in `testing/xr/monkeyball-vr-release-matrix.md`. The `games/` folder is removed. |
| `todo/platform/native-subsystem-project-split-todo.md` | 1 code item and 3 owner decisions to the unified runtime todo | `docs/architecture/runtime/project-organization.md` | Done. Checks are in `testing/platform/platform-validation.md`. |
| `todo/assets/unity-prefab-avatar-import-todo.md` | None | `docs/developer-guides/assets/unity-conversion-integrations.md` | Done. Checks are in `testing/assets/asset-import-validation.md`. |
| `todo/assets/prefab-asset-externalization-twopass-todo.md` | 4 test items to the asset import todo. Remove the `EditorPreferences.TwoPassAssetExternalization` text; no such flag exists. | `docs/developer-guides/assets/model-import.md` | Done. Items are in `todo/assets/fbx-import-export-todo.md`. |
| `todo/avatar/openxr-full-body-calibration-spectator-todo.md` | About 3 code items to `todo/avatar/vr-full-body-estimation-todo.md` | `docs/developer-guides/vr/full-body-calibration.md`, `spectator-camera.md`, `openxr-body-tracking.md` | Done. Checks are in `testing/avatar/avatar-validation.md`. |
| `todo/rendering/atmospheric-scattering-component-todo.md` | Stereo texture-array variants, LUT quality mode, material hooks. Move these to a short follow-up list in the volumetric or sky todo. | `docs/developer-guides/components/atmospheric-scattering.md` | Done. Code items and decisions are in `todo/rendering/sky-and-atmosphere-followups-todo.md`. |
| `todo/rendering/physics-debug-visualization-performance-todo.md` | None | New `docs/architecture/physics/physics-debug-frame.md` | Done. Checks are in `testing/physics/physics-validation.md`. |
| `todo/rendering/shadow-and-pipeline-validation-failures-todo.md` | None (closed) | `docs/architecture/rendering/render-pipeline-resource-lifecycle.md` | Done. |
| `todo/rendering/vulkan-deferred-and-probe-gi-fixes-todo.md` | 2 optional items to the Vulkan master todo | `docs/architecture/rendering/vulkan-renderer.md` | Done. Checks are in `testing/rendering/global-illumination-validation.md`. |
| `todo/rendering/optimization/vulkan-headless-mcp-component-profiling-todo.md` | 2 code items to the profiler UI todo | `docs/developer-guides/diagnostics/profiler.md` | Done. |
| `todo/rendering/shadows/directional-cascade-atlas-stale-frame-and-reprojection-todo.md` | Scroll-reuse prototype and primary command-buffer reuse to `shadow-atlas-overhaul-todo.md` as optional items | New `docs/architecture/rendering/shadow-atlas.md` | Done. Checks are in `testing/rendering/shadow-validation.md`. |
| `todo/rendering/shadows/shadow-atlas-allocation-and-threading-todo.md` | 1 item to `shadow-atlas-overhaul-todo.md` | New `docs/architecture/rendering/shadow-atlas.md` | Done. |
| `todo/rendering/shadows/shadow-atlas-solve-efficiency-todo.md` | 3 items to `shadow-atlas-overhaul-todo.md` | New `docs/architecture/rendering/shadow-atlas.md` | Done. |
| `todo/rendering/vr/openxr-steamvr-openvr-parity-todo.md` | None. All boxes are checked, but hardware rows remain open in `testing/xr/openxr-steamvr-hardware-validation.md`. | `docs/developer-guides/vr/openxr-runtime.md` | Done. Open questions moved to `todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md`. |
| `todo/rendering/architectural-refactor/00-advanced-render-pipeline-refactor-todo.md` | Remove the classic GBuffer and `DeferredLightCombine` from the Advanced opaque path. Confirm that V2 names are gone. Move both to the Vulkan XR and advanced rendering todo. | New `docs/architecture/rendering/advanced-render-pipeline.md` | Done. The `architectural-refactor/` folder is removed. |
| `todo/rendering/optimization/advanced-pipeline-gpu-attribution-todo.md` | Not started. Merge its 2 code items into the profiler UI todo; do not drop them. | `docs/developer-guides/diagnostics/profiler.md` | Done. |

Update the Status column when you finish a row. Use `Done.` or `Blocked: <reason>`.

## 4. Close Out `todo/COMPLETED/`

- [x] Decide the policy for `todo/COMPLETED/`:
  - Option A: delete each doc after its design is in an architecture doc. Git history keeps the record.
  - Option B: keep the folder as read-only history, with no open boxes.

  Record the decision in `README.md`.

  Decision (owner): Option A. Every doc in `todo/COMPLETED/` is deleted after migration. Build logs are deleted. Validation checks move to validation docs and are never dropped. `README.md` records the policy. The folder no longer exists.
- [x] Confirm architecture coverage for docs with no open items, then apply the policy:

  | Docs | Existing architecture coverage |
  |---|---|
  | `01-pipeline-identity-and-frame-contract-todo.md`, `02-gpu-scene-and-material-data-contract-todo.md` | `default-render-pipeline-notes.md` |
  | `gc-and-hot-path-memory-control-todo.md` | `docs/developer-guides/runtime/hot-path-memory.md` |
  | `lightweight-local-agent-broker-todo.md` | broker guides |
  | `meshlet-import-cooking-and-production-readiness-todo.md`, `meshlet-production-closeout-work-guide.md`, `gpu-meshlet-strategy-split-todo.md` | `mesh-submission-strategies.md` |
  | `physics-finalization.md`, `physics-jolt-parity-followups.md` | `docs/architecture/physics/overview.md` |
  | `poiyomi-toon-93-parity-checklist.md` | `poiyomi-*.md` |
  | `rendering-backend-hot-reload-todo.md` | `renderer-backend-hot-reload.md` |
  | `rendering-profiler-and-benchmarking-todo.md`, `rendering-profiler-counter-audit.md` | `profiler.md` |
  | `runtime-modularization-phase3-todo.md` to `phase6-todo.md` | `project-organization.md` |
  | `vulkan-14-performance-and-shader-modernization-todo.md`, `vulkan-core-hardening-and-device-loss-completed.md`, `vulkan-presentation-independent-renderer-refactor-todo.md` | `vulkan-renderer.md` |
  | `vulkan-parallel-command-chain-refactor-todo.md` | `vulkan-command-recording.md` |
  | `vulkan-physics-chain-opengl-parity-todo.md` | `physics-chain-compute-backends.md` |
  | `backend-renderer-folder-organization-todo.md` | `code-map.md` |
  | `openxr-stereo-temporal-isolation-todo.md` | `default-render-pipeline-notes.md` |
  | `cpu-async-hardware-query-occlusion-todo.md` | `cpu-query-async-occlusion.md` |
  | `render-settings-api-separation-refactor-todo.md` | `window-creation-and-renderer-init.md` |
  | `forward-lighting-shader-optimizations-todo.md` | `default-render-pipeline-notes.md`. Add the per-fragment caching contract. |

- [x] Write the missing architecture content (section 6) for these docs, then apply the policy:
  - the three texture stub docs, `vulkan-async-texture-streaming-upload-todo.md`, and `vulkan-imported-texture-streaming-todo.md`
  - `desktop-vr-shared-render-thread-frame-pacing-todo.md`
  - `window-interactive-resize-strategies-todo.md`
  - `pipeline-driven-post-processing-and-camera-editor-todo.md`. Its header is also stale.
  - `gpu-bvh-async-overflow-readback-todo.md`
- [x] Keep `progress/rendering/vulkan-todo-coverage-audit-2026-09-06.md` as history. Do not add new rows to it.

## 5. Consolidated Validation Docs

Create one validation doc per area. Move every unchecked manual, runtime, visual, hardware, profiler, benchmark, and soak box into it. Group the rows by feature, and link each feature back to its architecture doc. Do not copy standing rules, exit criteria prose, or branch steps.

Each validation doc uses this shape:

1. Scope and links to architecture docs.
2. Environment and setup: tasks, launch profiles, settings, environment variables.
3. Checks grouped by feature. Each check has a command or procedure, an expected result, a status, and the last evidence date.
4. Hardware matrix, if any.
5. Failures. Each failure links to an investigation or to a code item.

| Validation doc | Sources |
|---|---|
| `testing/rendering/vulkan-core-validation.md` (existing; rename to `vulkan-core-validation.md`) | `todo/vulkan.md` (about 133 checks), Vulkan master todo, Vulkan hardening todo, dynamic rendering todo, stall remediation todo, `COMPLETED/vulkan-desktop-frame-loop-decomposition-todo.md`, `COMPLETED/vulkan-runtime-code-organization-todo.md`, `COMPLETED/vulkan-frame-loop-performance-todo.md`. Merge `testing/rendering/vulkan-stall-validation.md` and `vulkan-lifecycle-evidence-harness.md` as sections, or link them. |
| `testing/rendering/vulkan-backend-parity-validation.md` (new) | Vulkan wrapper parity, fully bindless materials, VMA allocator, managed VMA concepts |
| `testing/rendering/render-queries-and-occlusion-validation.md` (new) | Vulkan render query upgrade, CPU async query occlusion, CPU async query camera motion, collect-visible decoupling, masked software occlusion, GPU-driven occlusion culling |
| `testing/rendering/gpu-driven-submission-validation.md` (new; absorb `03-05-optimization-validation-todo.md`) | GPU submission strategy, meshlet docs, GPU BVH overflow readback, compact zero-readback, CPU direct fast path, production rendering roadmap, material table ladder, engine optimization roadmap |
| `testing/rendering/default-and-advanced-pipeline-validation.md` (new) | Default render pipeline V2, post-processing and camera editor, depth of field, antialiasing, GPU hotspots, resource lifecycle, resolved shader source, transparency and OIT, forward depth-normal TransformId, atmospheric scattering, advanced pipeline refactor |
| `testing/rendering/shadow-validation.md` (new) | All `todo/rendering/shadows/` docs |
| `testing/rendering/global-illumination-validation.md` (new) | DDGI, modular GI, radiance cascades, surfel GI, LPV, voxel cone tracing, ReSTIR, Vulkan deferred and probe GI fixes |
| `testing/rendering/gpu-deformation-validation.md` (new) | Skinning follow-ups, blendshape docs, GPU-driven animation, softbody |
| `testing/rendering/window-and-render-thread-validation.md` (new) | Window interactive resize, dedicated render-thread ownership, render settings API separation, backend folder organization |
| `testing/rendering/retinal-visibility-cache-validation.md` (existing; rename to `retinal-visibility-cache-validation.md`) | RVC debug views todo |
| `testing/xr/openxr-validation.md` (new; link to `testing/xr/openxr-steamvr-hardware-validation.md` for hardware rows) | Frame pacing, parallel eye recording, Monado testing, stereo temporal isolation, Monado CI follow-ups, OpenXR timing tests, editor OpenXR toggle, VR performance contract, VR mirror reconstruction, OpenXR code organization |
| `testing/xr/openvr-vrclient-gpu-handoff-validation.md` (new) | OpenVR VRClient GPU handoff |
| `testing/texturing/texture-validation.md` (new; absorb `testing/texture-runtime-streaming-validation.md`) | Both texturing todos, Vulkan async and imported texture streaming, texture stubs |
| `testing/runtime/runtime-and-aot-validation.md` (new) | Runtime regression and NativeAOT hardening, runtime data layout, GC and hot-path memory, editor memory reduction |
| `testing/platform/platform-validation.md` (new) | Native subsystem split, unified desktop and browser runtime, mobile WebGPU |
| `testing/networking/networking-validation.md` (new) | Control plane managed server instances, peer-to-peer host switching |
| `testing/physics/physics-validation.md` (new; absorb `testing/physics-chain-*.md`) | Physics chain scale, Jolt character controller, Box3D, physics debug visualization |
| `testing/animation/animation-validation.md` (new) | Baked value compression docs, humanoid body and root compensation |
| `testing/assets/asset-import-validation.md` (new; link `testing/gltf-import.md`) | FBX, USD, model binary cache, prefab externalization, Unity prefab import |
| `testing/avatar/avatar-validation.md` (new; absorb `testing/avatar/openxr-calibration-spectator-validation.md`) | Avatar optimizer docs, full-body calibration and estimation |
| `testing/modeling/modeling-validation.md` (new) | Modeling docs 00 to 06 |
| `testing/ui/native-ui-validation.md` (new) | Native UI retained performance and editor parity |

- [ ] Create each validation doc with the shape above.
- [ ] Move the checks from each source doc. Remove them from the source in the same change.
- [ ] Update `testing/README.md` with the new list.

## 6. Architecture And Guide Docs To Write

New docs:

- [x] `docs/architecture/rendering/advanced-render-pipeline.md`: frame flow, the invariants, capability floor, output ownership for desktop, OpenXR, and offscreen. Link from `default-render-pipeline-notes.md`.
- [x] `docs/architecture/rendering/shadow-atlas.md`: allocator, relevance, threading, solve, stale-frame and reprojection rules, cascade publication.
- [x] `docs/architecture/rendering/texture-streaming.md`: streaming service split, cooked-cache authority, Vulkan upload and publication contract, residency rules.
- [ ] `docs/architecture/rendering/transparency-and-oit.md`
- [ ] `docs/architecture/rendering/gpu-hiz-occlusion-culling.md`: cull, occlusion, and indirect buffer contract; two-pass flow; barrier obligations per backend.
- [ ] `docs/architecture/rendering/cpu-software-occlusion.md`
- [x] `docs/architecture/rendering/vulkan-memory-allocation.md`: VMA ownership, allocation naming, statistics.
- [ ] `docs/architecture/rendering/cpu-memory-ownership.md`: mesh and texture CPU copies, capacity-sized arrays, budgets.
- [x] `docs/architecture/rendering/vr-output-pacing-and-mirror-policy.md`: frame-output manifest, `EVrMirrorMode`, cadence dividers, budget bands.
- [x] `docs/architecture/physics/physics-debug-frame.md`
- [ ] `docs/architecture/physics/physics-chain-world-runtime.md`
- [ ] `docs/architecture/assets/model-import-binary-cache.md`: include the existing meshlet section codec and service.
- [ ] `docs/architecture/assets/cooked-texture-payloads.md`
- [ ] `docs/architecture/runtime/portable-engine-host.md`
- [x] `docs/developer-guides/rendering/post-process-editor-extension.md`: post-process schema, pipeline editor UI hook, custom stage drawers.
- [ ] `docs/developer-guides/testing/test-suite-layout.md`: test project policy from the unit test reorganization todo. Fix the links to the missing `docs/developer-guides/testing.md`.

Write each doc below only after its feature code lands:

- [ ] `docs/architecture/avatar/avatar-optimization.md`
- [ ] `docs/architecture/animation/gpu-driven-animation.md`
- [ ] `docs/architecture/networking/peer-mode-bft.md`
- [ ] `docs/architecture/modeling/` pages for geometry nodes, modeling tools, GPU preview, and subdivision
- [x] A section about view-scoped render state and parallel eye recording in `docs/architecture/rendering/openxr-vr-rendering.md`

Sections in existing docs:

- [x] `window-creation-and-renderer-init.md`: window ownership, render owner and window thread, resize strategies, `XRE_INTERACTIVE_RESIZE_STRATEGY`.
- [x] `gpu-scene-bvh.md`: fenced, one-frame-late overflow readback.
- [x] `default-render-pipeline-notes.md`: forward-lighting per-fragment caching contract.
- [x] `project-organization.md`: add `XREngine.SourceGenerators`.
- [ ] Document `XRE_FORCE_SWAPCHAIN_MAGENTA`, `XRE_SKIP_IMGUI`, and `XRE_SKIP_UI_PIPELINE` in the matching diagnostics or rendering guide.
- [ ] `docs/architecture/runtime/control-plane.md`: correct the remaining-work text. Code for the UI, the host-agent ledger, the signer, and the TLS gateway is partly present.
- [ ] `docs/developer-guides/gi/ddgi.md`: remove "`AdvancedRenderPipeline` does not support DDGI". `AdvancedGlobalIlluminationHostAdapter` advertises `ScreenSpaceDiffuseOutput`, and DDGI requires only that capability. Copy the atlas formats, probe layout, ray buffer sizing, update-transaction rules, and known limits from the DDGI todo.
- [ ] `docs/architecture/rendering/global-illumination-ownership.md`: copy the finished modular GI contracts from `todo/rendering/global-illumination/modular-gi-architecture-todo.md`.
- [ ] `docs/architecture/rendering/mesh-submission-strategies.md`, `gpu-record-layouts.md`, `material-binding-policy.md`: copy the finished content from the production rendering roadmap.

## 7. Rewrite Active Todos As Code-Only

For each doc, apply the code todo template (see "Target Templates"). Remove completed history, validation boxes (after section 5 moves them), standing rules, and stale status. Keep one short "Current state" paragraph. Check the code before you write it.

### Correct stale text first

- [ ] `todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md`: two-pass code exists (`GPURenderPassCollection.TwoPassOcclusion.cs`, two-phase Hi-Z statistics), but all Phase 3 boxes are open. Mark what landed. Keep the persistent per-view visibility buffer and the `XRE_GPU_HIZ_DIRTY_BYPASS` removal open until you confirm them in code.
- [ ] `todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md`: the header says the Phase 3 cluster runtime is implemented, but "Current Reality" says no softbody shaders or cluster code exist. `GPUSoftbodyComponent`, `GPUSoftbodyDispatcher`, `GPUSoftbodyClusterMath`, and their tests exist. Rewrite the section.
- [ ] `todo/rendering/gpu/production-rendering-pipeline-roadmap.md`: the device-address consumer and two-pass occlusion exist. Update the boxes.
- [ ] `todo/assets/model-import-binary-cache-todo.md`: `ModelBinaryMeshletSectionCodec` and `ModelBinaryMeshletSectionService` exist, but the matching boxes are open. Update them.
- [ ] `todo/avatar/humanoid-body-root-compensation-todo.md`: close the `XRE_UNITY_HUMANOID_AVATAR_PROFILE` acceptance item. The variable is no longer in code.
- [ ] `todo/rendering/shadows/shadow-atlas-overhaul-todo.md`: correct the contract text (Sequential default, per-attempt reset).
- [ ] `todo/rendering/optimization/default-pipeline-gpu-hotspots-todo.md`: correct the GTAO resolution and exposure policy text.
- [ ] `todo/rendering/optimization/engine-rendering-optimization-roadmap.md`: correct the workstream 06 "Blocked" status.
- [ ] `todo/rendering/vr/openxr-future-work-todo.md`: the `DedicatedThread` default is already set.
- [ ] `todo/rendering/render-pipeline-resource-lifecycle-todo.md`: remove the "active again" text.
- [ ] `todo/rendering/default-pipeline-depth-of-field-todo.md`: correct the baseline facts.
- [ ] `todo/rendering/vulkan-restir-radiance-cache-gi-todo.md`: correct the "local facts" section. Only capability probing exists.
- [ ] `todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md`: the doc says a duplicate `Engine` type blocks the unit tests, but contract tests exist. Confirm and correct.
- [ ] `todo/rendering/global-illumination/ddgi-implementation-todo.md`: close the Vulkan final-source authority and stereo composition boxes that later build entries resolved.

### Large rewrites

- [ ] `todo/vulkan.md`: split it. Move the preserved diagnostics guide to `docs/developer-guides/` or the Vulkan validation doc. Keep a code-only roadmap of about 20 items, or merge them into the Vulkan master todo and delete the file.
- [ ] `todo/rendering/vulkan-core-hardening-and-device-loss-todo.md`: remove sections 10 to 13 (about 156 stale rows). Keep about 40 code items.
- [ ] `todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md`: make it a short index plus about 39 code items.
- [ ] `todo/rendering/vulkan-xr-and-advanced-rendering-todo.md`: remove the "Completed work" section. Keep the 4 code rows and the rows moved from section 2 and section 3.
- [ ] `todo/rendering/vulkan-dynamic-rendering-migration-todo.md`, `todo/rendering/vulkan-stall-remediation-todo.md`: code-only.
- [ ] `todo/rendering/global-illumination/ddgi-implementation-todo.md` (about 1,200 lines) and `modular-gi-architecture-todo.md` (about 700 lines): move the build logs to `investigations/rendering/`. Keep only the open code list (about 6 and about 4 items).
- [ ] `todo/avatar/humanoid-body-root-compensation-todo.md` (1,340 lines): move the history to an investigation. Keep about 8 code items.
- [ ] `todo/runtime-regression-and-nativeaot-hardening-todo.md`: about 100 code items remain. Move about 66 checks out. Move the file to `todo/runtime/`.
- [ ] `todo/runtime/runtime-data-layout-and-generated-contracts-todo.md`: about 21 code items. Move about 100 checks out.
- [ ] `todo/texturing/texture-compression-and-cooked-cache-todo.md` and `texture-runtime-streaming-virtual-texturing-todo.md`: code-only. Move Phase 0, Phase 1, and 5A checks to the texture validation doc.
- [ ] `todo/physics/physics-chain-thousands-scale-optimization-todo.md`: code-only. Move about 78 checks out.
- [ ] `todo/physics/jolt-character-controller-correctness-todo.md`: reduce to the open phase and the missing tests.
- [ ] `todo/networking/control-plane-managed-server-instances-todo.md`: reduce to the open server-instance work. Correct the status against the code.
- [ ] `todo/tests/unit-test-project-reorganization-todo.md`: move the policy to `docs/developer-guides/testing/test-suite-layout.md`. Keep the code steps.
- [ ] `todo/rendering/vr/openxr-runtime-code-organization-todo.md`: rewrite against the current `XREngine.Runtime.XR.OpenXR` layout.
- [ ] `todo/rendering/optimization/engine-rendering-optimization-roadmap.md`: make it an index only.

### Merges

- [ ] Merge `todo/modeling/00` to `06` into one `todo/modeling/modeling-todo.md`. Keep one copy of the shared rules and exit criteria. Group the code items by area.
- [ ] Merge the avatar optimizer docs into `todo/avatar/avatar-optimization-roadmap.md` (shared invariants and index) plus one code-only section per area:
  - analyzer
  - LOD and meshlet
  - material and texture
  - mesh geometry
  - skin, skeleton, and blendshape
  - cluster virtualized rendering
  - Gaussian crowd

  Remove repeated branch, corpus, and acceptance boxes.
- [ ] Merge `todo/rendering/gpu/blendshape-update-classes-and-static-baking-todo.md` into `blendshape-compression-and-gpu-efficiency-todo.md`.
- [ ] Merge `todo/rendering/mobile-webgpu-runtime-todo.md` into `todo/platform/unified-desktop-browser-runtime-todo.md`. About 60 of its checked rows are completion records; drop them.
- [ ] Merge `todo/animation/baked-value-compression-followups-todo.md` and `lossy-float-baked-value-compression-todo.md` if they share the codec work. Otherwise trim both.

### Relocate

- [ ] Move `todo/avatar/gpu-skinned-bvh-proxy-lod-design.md` (2,481 lines, a design) to `design/rendering/gpu/`.
- [ ] Move the planning content of `todo/rendering/vulkan-fossilize-integration-todo.md` to `design/rendering/`. No Fossilize code exists. Keep a short todo.
- [ ] Move `todo/forward-depth-normal-transform-id-todo.md` to `todo/rendering/`, `todo/transparency-and-oit-todo.md` to `todo/rendering/`, and `todo/uber-shader-variant-builder-todo.md` out of the root (see section 3).

### Trim only

- [ ] Trim these docs: remove standing rules, exit-criteria prose, branch steps, and validation boxes. Fix paths. Keep the code items.
  - `todo/rendering/global-illumination/lpvgi-implementation-todo.md`
  - `voxel-cone-tracing-and-vxao-implementation-todo.md`
  - `surfel-gi-repair-todo.md`
  - `radiance-cascades-runtime-completion-todo.md`
  - `todo/rendering/gpu/gpu-driven-animation-todo.md`
  - `openvr-vrclient-gpu-handoff-todo.md`
  - `skinning-gpu-efficiency-followups-todo.md`
  - `todo/assets/usd-import-export-todo.md`
  - `fbx-import-export-todo.md`
  - `todo/avatar/vr-full-body-estimation-todo.md`
  - `todo/networking/peer-to-peer-host-switching-todo.md`
  - `todo/physics/box3d-backend-integration-todo.md` (move its contracts to a design doc)
  - `todo/ui/native-ui-retained-performance-and-editor-parity-todo.md`
  - `todo/rendering/advanced-pipeline-antialiasing-todo.md`
  - `cpu-async-query-camera-motion-todo.md`
  - `masked-software-occlusion-culling-todo.md`
  - `resolved-shader-source-optimization-todo.md`
  - `todo/rendering/optimization/*` (remaining docs)
  - `todo/rendering/vr/*` (remaining docs)
  - `todo/rendering/shadows/shadow-atlas-overhaul-todo.md`
- [ ] Keep `todo/rendering/vulkan-stall-separate-findings-todo.md` as an issue backlog.

## 8. Closeout

- [ ] Run a link check over `docs/`. No link may point to a moved or deleted doc.
- [ ] Count unchecked boxes again. Under `todo/`, every open box must be a code item. Under `testing/`, every open box must be a check.
- [ ] Confirm that no doc in `todo/COMPLETED/` has an open box.
- [ ] Update `README.md` triage rows and the "Active TODOs" list.
- [ ] Update `docs/README.md` and `docs/architecture/README.md` with the new architecture docs.
- [ ] Move "Target Templates" to `docs/developer-guides/ai/work-doc-templates.md` (or `docs/work/README.md`). Update the template link in `AGENTS.md`.
- [ ] Delete this document after the closeout.

## Target Templates

### Code todo

```markdown
# <Feature> TODO

Last Updated: <date>
Status: <Planned | Active | Blocked: reason>
Architecture: <link to stable doc>  Design: <link, if not yet stable>
Validation: <link to testing doc>

## Current State
One short paragraph. What exists in code now, with type or file names.

## Open Code Items
### <Area>
- [ ] <Imperative action>. <Files or types>. Done when: <observable code result or unit test>.

## Decisions Needed
- [ ] <Question>. Owner: <role>.

## Out Of Scope
- <Item>
```

Rules for code todos:

- Each box is one code change that a programmer can do and a reviewer can confirm in the diff or with a unit test.
- Unit test code is a code item. Running the editor, capturing frames, profiling, or using hardware is a check, and goes to the validation doc.
- When a code item is done, remove it. Do not keep checked boxes as history. Move the lasting facts to the architecture doc.
- Do not copy standing rules, invariants, or acceptance prose from the architecture doc. Link to it.

### Validation doc

```markdown
# <Area> Validation

Architecture: <links>  Code todos: <links>

## Setup
Tasks, launch profiles, settings, environment variables.

## Checks
### <Feature>
- [ ] <Check>. Procedure: <command, task, or steps>. Expected: <result>. Last evidence: <date or "none">.

Use a table (`Check | Procedure | Expected | Status | Last evidence`) when several checks share one procedure.

## Hardware Matrix

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
```

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/avatar/avatar-lod-meshlet-cooked-variant-todo.md`

- [ ] A generated avatar variant can be identified, invalidated, and debugged
  separately from its source import.

### From `todo/avatar/cluster-virtualized-avatar-rendering-todo.md`

- [ ] Missing cluster data degrades quality conservatively instead of stalling or
  popping to invalid geometry.

### From `todo/avatar/gaussian-splat-distant-crowd-lod-todo.md`

- [ ] Crowds degrade gracefully rather than causing frame spikes or generic
  placeholder swaps.

### From `todo/rendering/optimization/vulkan-managed-vma-concepts-allocator-todo.md`

- [ ] Document how persisted settings should be handled after the `Suballocator` to `Managed` rename.
- [ ] Centralize alignment calculation for allocation offset, non-coherent atom size, and buffer-image granularity.
- [ ] Add guard rails for mapping image memory and other suspicious usage.
- [ ] Define which resources are movable, copyable, and rebindable.

### From `todo/rendering/vulkan-dynamic-rendering-migration-todo.md`

- [ ] Add `RenderingFragmentShadingRateAttachmentInfoKHR` to dynamic-rendering
  plans and command scopes; include attachment identity in command compatibility
  and secondary execution contracts.
- [ ] Add `RenderingFragmentDensityMapAttachmentInfoEXT` to dynamic-rendering
  plans where supported and implement the required legacy render-pass path where
  dynamic density-map attachment is unavailable.

### From `todo/rendering/vulkan-stall-remediation-todo.md`

- [ ] With test clearance, update the 3 factory-contract tests that still require an `RvcRenderPipeline` for every OpenXR eye, rename `EAdvancedStereoMode.RvcTwoPass`, and replace `AdvancedProductionCutoverContract.ProductionOpenXrPipelineName`.

### From `todo/vulkan.md`

- [ ] Modern sync and frame pacing: `synchronization2` everywhere, timeline semaphores, `present_wait`/`present_id`, `swapchain_maintenance1`, calibrated timestamps.
- [ ] Memory residency: `memory_budget` + `pageable_device_local_memory`.

# Vulkan shadow mapping regression: layered rendering, atlases, and CPU-direct pacing

**Date:** 2026-08-14
**Status:** Analysis phases 0-6 complete; implementation and validation pending; no engine fixes have been made
**Scope:** Directional, point, and spot shadows on ordinary desktop Vulkan, especially the CPU-direct mesh-submission path
**Related tracker:** [Directional light inspector shadow investigation](directional-light-inspector-shadow-2026-08-03.md)

**Second-pass review:** Source-only code review completed on 2026-08-14. The engine, tests, and RenderDoc were not run for this revision.

**Phase 0 execution:** Route-matrix runtime audit completed on 2026-08-14 with desktop Vulkan, `CpuDirect`, dynamic rendering, validation layers, and one-light settled controls. No RenderDoc capture or engine code change was made.

**Phase 1 execution:** Non-atlased sequential directional, point, and spot writers were captured in RenderDoc on 2026-08-14. The captures confirm the Phase 0 material-loss defect at the draw, expose a separate point-cube slice-aliasing defect, and show that point/spot receivers sample color resources containing ordinary scene output rather than the required shadow encoding. No engine code or test was changed.

**Phase 2 execution:** The deferred directional, point, and spot receiver draws were audited from the existing Phase 1 captures on 2026-08-14. All three bind the exact producer resource rather than a dummy and enter their enabled legacy-shadow branches. The point receiver additionally exposes a six-face view with five faces still in `TRANSFER_DST_OPTIMAL`; a real `-X` receiver sample reads zero and becomes fully shadowed. Directional and spot traces show valid-looking current light metadata driving manual comparisons against invalid producer contents. No engine run, engine code change, or test change was made.

**Phase 3 execution:** The point-atlas immutable plan and executor were audited on 2026-08-14 from source and the saved Phase 0 lighting log. The isolated point-only interval proves that one six-member group becomes six plan entries, the single grouped attempt fails, and its six member-equivalents are then mislabeled as budget-deferred. The audit also corrected the proposed containment algorithm: requests are priority-sorted and can interleave across lights, so a group must suppress its exact member keys rather than blindly advancing across a first-to-last index range. A latent grouped-caster collection mismatch was also found. No engine/editor or RenderDoc session was started, and no engine code or test was changed.

**Phase 4 execution:** The immutable directional/point layered draw contract was audited on 2026-08-14 from source and the existing capture inventory. The request's compatibility signature is computed from the correct in-scope shadow material, but the queued request does not retain that resolved material, the layered packet, or the expanded instance count; all three are reconstructed later after the scopes have ended. The downstream draw snapshot and auto-uniform generation are immutable once constructed, so the first repair belongs at enqueue time. Geometry variants also fall outside the restore gates, and legacy point geometry is missing both its matrix alias and derived face mask in the immutable restore helper. No layered/geometry/atlas `.rdc` exists, so GPU output equivalence remains deliberately pending. No engine/editor, test, replay, new capture, or product-code change was made.

**Phase 5 execution:** Atlas publication, completion, receiver-generation, and storage-readiness contracts were audited on 2026-08-14 using source plus the already-saved Phase 0 logs and screenshots. The completion ring reconciled 48 saved directional records one to three render-frame ids later (40 at two), but that ring records CPU acceptance rather than GPU completion. More importantly, point/spot content can change from unavailable to sampleable without advancing either the atlas layout generation or the deferred light binding generation, authorizing reuse of a stale dummy/disabled receiver publication. Directional uses an immediate component-slot commit and therefore has a different logical freshness path; its physical write-to-sample ordering remains unproven. The saved screenshots are not frame-synchronized and do not prove whole-frame present lag. No engine/editor, test, replay, new capture, or product-code change was made.

**Phase 6 execution:** Shadow work amplification and the meaning of the saved timing evidence were audited on 2026-08-14 from source and the existing Phase 0 profiler/log snapshots. Sequential directional/point routes perform one collection/swap and submission per target, while layered routes reduce CPU submissions to one union stream but still expand every union caster to every selected target because no per-caster target mask exists. The atlas time budget is an admission check, not a hard frame cap: saved critical directional entries took 18.37 ms and 48.65 ms, including a 50.60 ms atlas-render total against a 0.50 ms configured budget. CPU-direct also attempts the unused 160-byte `$CpuDirectDynamicData` capture three times in the main descriptor path per draw. These are confirmed amplification mechanisms, but the saved route-switch session is not a warmed benchmark and GPU pipeline timing was disabled, so controlled p50/p95/p99 attribution remains pending. No engine/editor, test, capture, or product-code change was made.

## Problem statement

The Vulkan render loop is now reasonably stable with lights disabled, but shadowed lights regress correctness and frame pacing:

- directional cascades and point-light cube faces do not reliably render through the requested instanced-layered or geometry-shader paths;
- the directional, point, and spot atlas paths can fail, trail camera motion, or stutter;
- basic sequential, non-atlased writers are now captured and are incorrect on CPU-direct: all lose the forced material, and point faces also collapse to one physical cube slice;
- enabling shadowed lights substantially increases CPU-direct work at high frame rates.

This is not one failure. The second source pass found independent defects in route selection, point-group plan construction, failure accounting, atlas storage lifetime, publication timing, and layered uniform ownership. It also corrected several conclusions that the first analysis stated too strongly.

## Corrections to the first analysis

1. `PointLightComponent.LastRenderedShadowFaceMask` and `LastShadowRenderFrame` are legacy-only diagnostics. They are updated by non-atlased layered and sequential cube rendering, but not by `RenderShadowAtlasFaceTile` or `RenderGroupedShadowAtlasFaceTiles`. Their atlas values of `0` therefore did **not** prove that no atlas faces were written. The stronger atlas evidence is the manager allocation diagnostic (`LastRenderedFrame=0`, `NeverRendered`, stale fallback) combined with the source-confirmed failed grouped route.
2. A legacy point face mask of `0x3F` proves that all six CPU render calls were issued and marked, not that those faces contain valid GPU depth or are sampled correctly.
3. The dark final screenshots do not isolate shadow sampling. Exposure, light placement, the light-volume pass, and clear-only shadow output were not independently controlled. Phase 1 later established the producer failures from depth/color targets and draw state; it did not retroactively make those screenshots diagnostic evidence.
4. The atlas `ShadowTileCompletion` queue is not a GPU-completion mechanism. Entries are created when the component render call returns on the CPU; they carry no Vulkan fence or timeline value. The word *completion* in the current code means recorded/accepted by the shadow path, not proven GPU-complete and sample-ready.
5. The immediate Phase 0 screenshots are not synchronized to a requested render/present generation. Matching immediate hashes followed by a changed capture several engine frames later do not prove that shadows or the whole frame lag by that count.
6. Phase 0's timing/resource samples came from cold route switching, and GPU pipeline profiling was disabled. They identify CPU recording and creation/retention mechanisms, but they are not warmed p50/p95/p99 data and must not be used as performance thresholds.

## Executive diagnosis

### Phase 0 runtime additions

1. **CPU-direct deferred materialization drops the shadow pass's effective material and immutable layered state.**

   This is now the leading common failure for basic sequential and layered shadow draws. `XRViewport.Render` installs the forced shadow material as `RenderingState.GlobalMaterialOverride`, and the light installs cascade/face matrices in a scoped layered state. `VkMeshRenderer.OnRenderRequested` computes its preparation signature while those scopes are active, but `VulkanMeshRenderRequest` stores only the mesh command's local `MaterialOverride`. It does not store the resolved shadow material or a `LayeredShadowUniformState` snapshot.

   `VulkanFrameLoop.DrainQueuedMeshRenderRequests` runs later and restores only the pipeline and rendering camera. `TryMaterializeQueuedRenderRequest` then calls `ResolveMaterial` and `LayeredShadowUniformState.CaptureFromCurrentRenderingState` after `PushMainAttributes` and the directional/point layered scopes have been popped. The resulting `PendingMeshDraw` can therefore use the ordinary scene material, `IsShadowPass=false`, zero target count, and no captured matrices even though the request signature was computed from the correct in-scope shadow material.

   The runtime log matches this source path: all 109 graphics-pipeline records attributed to `ShadowRenderPipeline` used ordinary scene shaders and reported `directionalShadow=None;pointShadow=None`. There were zero occurrences of the dedicated directional, point, atlas-point, or generic shadow-depth shader names. This proves the effective material and shadow-packet identity were lost before those prepared draws; it does not by itself prove whether an ordinary vertex/fragment program happened to write usable depth in a sequential target. For an actual layered request, the specialized `XRMeshRenderer.BaseVersion` selected while the scope is live can survive as the queued renderer identity, so not every part of route selection is necessarily lost.

2. **The isolated point-atlas case allocates all six faces but never publishes a rendered face.**

   With only the point light active, the solver reported six classified point/depth requests, six resident allocations, no allocation failure, and no demotion. The component's atlas diagnostic remained `LastRenderedFrame=0`, `NeverRendered`, and `ActiveFallback=StaleTile` for every requested mode, including after moving both the light and camera. The lighting log also reported `pointGroups=1/6/0` while the light's effective route was sequential with `AtlasUsesSequentialTiles`, confirming at runtime that manager grouping ignored the light route.

3. **Core Vulkan validation is not clean with the lit atlas configuration.**

   The shutdown-flushed Vulkan log contains ten `VUID-VkImageMemoryBarrier-oldLayout-01197` reports and ten `VUID-vkCmdDraw-None-09600` reports between 11:08:00.627 and 11:08:00.787, after which the validation duplicate limit suppressed further copies. The first barrier stack runs through `TransitionFboAttachmentsForDynamicRendering`, `EndActiveRenderPass`, and `TryExecuteScheduledMeshCommandChainSecondaryRun`; submission errors show descriptors expecting present, color-attachment, or transfer layouts while the images were in different layouts. These failures occurred with primary reuse enabled and all three atlas lights initially active. They are not yet isolated to a particular shadow resource, but they invalidate any assumption that attachment and descriptor layouts are generally correct.

   The live profiler's current-frame validation count stayed at zero because it is not a cumulative session audit. Synchronization validation was disabled, so Phase 0 established only the core-layout failures.

### Phase 1 capture additions

1. **All three basic sequential controls reach GPU draw commands with the wrong material.**

   The directional writer bound an ordinary scene vertex program with 13 user outputs, no geometry stage, and no fragment stage. The point and spot writers bound ordinary scene vertex/fragment programs whose fragment interfaces expose four G-buffer-style outputs and multiple sampled resources. None used `PointLightShadowDepth.fs`, `ShaderHelper.Frag_DepthOutput`, or the directional shadow-moment material requested by the light. All representative draws also used captured cull mode `2` rather than the shadow materials' explicit `ECullMode.None`, with zero depth bias. This turns Phase 0's session-wide shader-log inference into per-draw capture evidence.

2. **Sequential point faces all bind physical cube slice 5.**

   The animated point capture contains six logical face passes in source loop order: four clear-only passes, one pass with 66 draws, and one pass with two draws. RenderDoc reports the same `R16_FLOAT` color cube resource `12139`, the same `D32` depth cube resource `12135`, and `firstSlice=5` for every pass. At the end of the writer, slices 0 through 4 have no clear/write use in the captured frame and report untouched zero-valued contents; only slice 5 contains output.

   This matches a separate late-snapshot defect. `PointLightComponent.RenderSequentialShadowFaces` mutates one `_perFaceFbo` through layers 0–5. Vulkan framebuffer binding retains the mutable `XRFrameBuffer` reference, while `VulkanRenderer.CommandChains.Packetization.CaptureRecordedRenderTargetSnapshot` freezes the native attachment only later. By then the shared framebuffer has its final layer-5 targets. Face cameras and the source-side `+X, -X, +Y, -Y, +Z, -Z` order do not help when every GPU pass addresses the same physical slice. Physical face orientation therefore fails before matrix/orientation validation.

3. **Point and spot color shadow resources contain ordinary scene output, then are sampled as shadows.**

   The point color cube's written slice visibly contains the unit-world brick material; its range after the writer is `0.131958..1.0`, while the depth slice changes only to `0.997830..1.0`. Resource usage marks the color cube as a pixel-shader resource at the final face draws while it is also the active color target, and again at deferred lighting EID 1169. The intended point material instead writes the point-distance shadow encoding through `PointLightShadowDepth.fs`.

   The spot writer similarly produces a visibly textured brick wall in `R16_FLOAT` resource `19264` through ordinary four-output fragment shaders. That same color resource is sampled by the deferred lighting draw at EID 302. Its companion `D32` resource `19260` does receive depth (`0.687569..1.0`), but the receiver does not sample that depth attachment. The intended spot material uses `ShaderHelper.Frag_DepthOutput` (or the selected moment encoding) and no culling. Thus both local-light sequential controls have draws and changing attachments, but invalid sampled shadow encodings.

4. **Directional sequential layer addressing works, but it is not a source-clean shadow writer.**

   The directional capture writes all four distinct layers of D24 array resource `9733`: logical cascade passes bind physical slices 0, 2, 3, and 1 in execution order, and all four exported layers contain non-clear depth. Their minima are approximately `0.844877`, `0.759961`, `0.676721`, and `0.610145` for physical slices 0–3. The same array is later marked as a pixel-shader resource at EID 20882. This rules out a universal "no Vulkan depth writes" failure and shows that distinct directional FBOs avoid the point light's mutable-FBO alias.

   It does not establish a correct directional baseline: the draw still uses the ordinary scene vertex program and captured back-face culling rather than the requested shadow material/state. The sparse unit-box scene shows plausible scale changes across layers but cannot prove cascade overlap or seam coverage.

5. **Every Phase 1 capture repeats an independent synchronization validation error.**

   RenderDoc reports two copies at EID 6 of `VUID-vkCmdPipelineBarrier-pBufferMemoryBarriers-02818`: `VK_ACCESS_HOST_READ_BIT` is paired with `VK_PIPELINE_STAGE_ALL_COMMANDS_BIT`, which does not support that destination access. This is not specific to one shadow texture, but it means the one-light sequential captures are not validation-clean either.

### Phase 2 receiver additions

1. **Dummy substitution is ruled out at the three audited deferred receiver draws.**

   Directional EID 20882 binds producer D24 array `9733` through 2D-array view `9735`, mip 0, layers 0-3. Point EID 1169 binds producer `R16_FLOAT` cube `12139` through cube view `12141`, mip 0, all six faces. Spot EID 302 binds producer `R16_FLOAT` image `19264` through 2D view `19266`, mip 0, layer 0. These IDs match the Phase 1 writer resources exactly. The corresponding object-frequency payloads set `LightHasShadowMap=true`; directional also sets `UseCascadedDirectionalShadows=true`, while point and spot atlas-enable flags are false. Capture-local resource continuity is proven, but the engine's storage/publication generation is not embedded in the capture and cannot be compared.

2. **The sequential point receiver samples a six-face view whose subresources are not all shader-readable.**

   At EID 1169, RenderDoc tracks cube faces 0-4 in `VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL` and only face 5 in `VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL`. The descriptor view spans all six faces and the shader declares a `samplerCube`. A receiver pixel at `(320,360)` uses sample directions near `(-0.992,-0.099,0.077)`, selecting `-X` (logical face 1), reads `0.0` for all four taps, compares that against a receiver distance of `17.7786` with far distance `27.2845`, and returns a shadow factor of `0.0`. The light-accumulation output is zero at every sampled geometry pixel.

   The event sequence contains `vkCmdBeginRendering` at EID 1159, clear at 1160, the point draw at 1169, and no cube transition between them. Source review found two receiver-transition hazards that explain why this can persist:

   - inline primary recording calls `TryTransitionPreparedDescriptorImagesForSampling` only on the render-target-open/switch branch and ignores a false result without performing the logical-snapshot fallback present in the otherwise unused `TransitionFrameOpDescriptorSnapshotsForSampling` scanner;
   - `TryGetRecordedImageAccessStateNoLock` rejects a range whose subresources have different layouts. `TransitionDescriptorImageForSampling` then either returns when it cannot resolve resource generation or substitutes one whole-range `Undefined` prior state. It does not split a mixed descriptor view into per-layout barriers, so that fallback cannot safely preserve known contents.

   The physical-layer alias remains the first producer defect: all logical face passes wrote face 5. The mixed receiver layout is a second, capture-confirmed failure edge and a direct explanation for the observed `-X` zero sample.

3. **Directional receiver metadata is populated and current-looking, but it compares against an invalid reference producer.**

   The directional payload contains `CascadeCount=4`, splits `20.09`, `60.07`, `120.04`, and `200.0`, four non-identity cascade matrices, `UseCascadedDirectionalShadows=true`, `LightHasShadowMap=true`, and atlas mode disabled. Resource `9733` is in `DEPTH_STENCIL_READ_ONLY_OPTIMAL` for all four layers. At pixel `(320,360)`, the shader selects layer 0, computes receiver depth `0.912811`, samples depths mostly near `0.854-0.856` plus one clear `1.0`, and returns `1/8 = 0.125`; output is only about `(0.000770,0.000747,0.000664)`. At `(300,350)`, all eight taps fail and the shadow factor/output are zero. These traces prove cascade selection and sampling execute, not that the ordinary-material depth written in Phase 1 is correct.

4. **Spot receiver state is internally coherent, but ordinary material color is interpreted as depth.**

   Spot resource `19264` is in `SHADER_READ_ONLY_OPTIMAL`; the payload sets `LightHasShadowMap=true`, atlas mode false, near/far `1/40`, and carries non-identity world-to-light view, projection, and combined matrices for the light at `(0,10,0)`. At pixel `(320,360)`, receiver depth is `0.922995`, bias is `0.001710`, and the comparison value is `0.921285`. Six of eight sampled values are brick-color-like `0.543-0.687`; only two clear-value `1.0` taps pass, producing shadow factor `0.25` and a very small lighting contribution. This directly connects the Phase 1 producer-material loss to over-shadowing at the receiver.

5. **All three legacy shaders use explicit comparison, not hardware comparison sampling.**

   Their shadow samplers are point min/mag/mip filtered, clamp-edge (cube clamps all three axes), and report `AlwaysTrue` comparison state. The SPIR-V uses ordinary sampled images and explicit comparisons: directional and spot compare biased projected receiver depth to sampled depth, while point multiplies normalized cube depth by the light radius and compares it to radial receiver distance. This rules out an accidental Vulkan comparison-sampler polarity mismatch in these captures.

### Phase 3 scheduling additions

1. **The duplicate point plan and failure-as-budget accounting are runtime-confirmed.**

   In the isolated point-atlas interval, the solver reported `pointGroups=1/6/0`, and the published plan repeatedly reported `requests=6`, `entries=6`, and `members=6`. With no other light requests present, source reconstruction makes the six entries unambiguous: one six-face `PointFaceGroup` plus five duplicate `Tile` entries for the remaining member requests. Execution then reported `scheduled=0`, `checked=6`, `failed=6`, `deferred=6`, and `firstDeferredIndex=0`. `checked=6` and `failed=6` are the six-face tile cost of one failed grouped call, not six independent grouped attempts. The five tile entries were not a reachable fallback because execution broke on that first group failure.

   The earlier mixed-light interval shows the same defect with sparse relevance: `pointGroups=1/4/2`, `requests=11`, `entries=6`, `members=4`, followed by `checked=4`, `failed=4`, `deferred=6`, and `firstDeferredIndex=5`. The reported six-request deferral is the raw request tail, including requests that were not members of the failed four-face group.

2. **Exact membership, not a raw request range, must own grouped work.**

   `RequestComparer` sorts by pinning, render-order bucket, priority, prior placement, and key. Faces from different point lights are therefore not guaranteed to remain contiguous. Advancing the planner loop from a group's first request index to its last can skip unrelated interleaved requests. The safe shape is a map from every grouped member key to its group, one emitted-group marker, and suppression of only later requests whose exact keys belong to that emitted group. First/last indices may remain diagnostics, but they are not a safe iteration-control primitive without an asserted contiguity invariant.

3. **The planner/light route contradiction is broader than Vulkan.**

   Ordinary Vulkan makes the mismatch deterministic because the manager permits grouping while `PointLightComponent` rejects every Vulkan grouped attempt. However, `BuildPointFaceGroups` also ignores `ShadowRenderMode` and the light's capability result on other backends. A non-Vulkan light explicitly requesting `Sequential` can therefore be grouped by the manager and rejected later by the light. Route selection must be resolved once, before group construction, and carried into the immutable plan.

4. **The latent grouped success path prepares the wrong caster stream.**

   For atlas use, `CreatePointShadowRenderPlan` always reports sequential `AtlasUsesSequentialTiles`. On a backend where `ShouldPrepareAtlasGroupedFaceCollection` returns true, `CollectVisibleItems` and `SwapBuffers` still iterate the six per-face viewports. `RenderGroupedShadowAtlasFaceTiles` later renders only `_viewports[0]`. Because those viewports own separate shadow-pipeline instances, a future enabled grouped path can replay only face 0's collection instead of one influence-volume/union caster packet. Grouped rendering needs an explicitly prepared shared caster stream before indexed viewport fan-out is enabled.

### Phase 4 immutable-state additions

1. **The queue freezes a shadow-compatible signature, but not the facts that signature describes.**

   `XRMeshRenderer.GetVersion` selects the specialized instanced vertex-generator version while the layered scope is active, and `VkMeshRenderer.OnRenderRequested` computes `PreparationCompatibilitySignature` from the correctly resolved per-caster shadow material in that same scope. The queued `VulkanMeshRenderRequest`, however, keeps only the caller's raw local material override and original instance count. It does not keep the resolved material/reason, `LayeredShadowUniformState`, atlas/layered route flags, or expanded draw-instance count.

   Later, `TryMaterializeQueuedRenderRequest` re-runs material selection, calls `ResolveLayeredShadowInstanceCount`, and captures `LayeredShadowUniformState` after the global shadow-material and layered scopes have been popped. The warm-preparation cache can therefore be queried with a signature for material A while the emitted draw is prepared from material B. `CaptureProgramBindingSnapshot` is also downstream of that late reconstruction, so calling it immutable does not make its inputs enqueue-time correct.

   The same boundary is incomplete for view identity. The request retains a pipeline reference, and the drain scope restores `pipeline.LastRenderingCamera`, but the camera matrices, render-area state, and transform id are copied only during materialization. A mutable camera reference is not an enqueue-time view snapshot. Layered vertex/geometry shaders primarily use the explicit shadow matrices, but sequential shadow writers and other required engine-camera uniforms still depend on this late view. Freeze the view payload at enqueue or establish a separate frame-owned camera-generation contract; preserving only the material/layered fields would leave a plausible camera-motion mismatch.

2. **The downstream immutable machinery is usable once the correct packet reaches it.**

   `PendingMeshDraw` retains the selected material, expanded instance count, view snapshot, and program-binding snapshot; sealing clones the indexed viewport/scissor arrays. `VulkanAutoUniformPublicationSnapshot.PassGeneration` hashes the complete `LayeredShadowUniformState`, so matrix/count/index changes already advance pass-frequency content generation. This makes a generic CPU-direct mapped-buffer race or a complete descriptor/publication rewrite a poor first fix. Capture the packet before enqueue, then feed that exact packet into materialization and the existing pass-generation path.

   The remaining observability hole is real: the draw snapshot does not retain or expose its render-frame id, scoped-binding revision, or a shadow-packet serial/hash that can join collection, materialization, recording, and RenderDoc evidence. The cache consults a thread-static scoped-binding revision, but that identifier is neither part of `LayeredShadowUniformState` nor available on `PendingMeshDraw` for diagnostics.

3. **`LayeredShadowUniformState` contains the matrices needed by geometry draws, but its schema and restore gates describe only instanced draws.**

   Capture copies directional matrices/count and point matrices/count/indices even when `instancedLayered=false`. The struct nevertheless exposes only the two *instanced* flags; it omits the generic directional/point layered flags, both atlas-grouped flags, selected material kind/route, a packet generation, and an explicit or derived point-face mask. `ApplyShadowUniforms` then restores data only for instanced material kinds. This is why directional geometry can retain `CascadeLayerCount=0`, and why point geometry still relies on a live component callback.

   The point legacy geometry contract has two additional omissions: `PointLightShadowDepth.gs` reads `ViewProjectionMatrices[]` and rejects every face whose bit is absent from `PointShadowFaceMask`, while `SetPointLightLayeredUniforms` writes only `PointShadowViewProjectionMatrices[]`, face indices, and the count. A geometry-capable immutable restore must publish both matrix aliases during migration and derive the exact mask from the captured compact face indices. Leaving the mask at its default zero deterministically emits no primitives.

4. **The compact target-index math is source-consistent once count, matrices, and draw count are correct.**

   For legacy directional instancing, `gl_InstanceID % CascadeLayerCount` selects both the packed matrix and physical array layer. For legacy point instancing, the compact slot selects the packed matrix while `PointShadowFaceIndices[slot]` selects the physical cube layer. The geometry shaders fan one source triangle across the same packed targets. Atlas instanced/geometry shaders use the compact slot as `gl_ViewportIndex`, and the CPU packs matrix and indexed viewport/scissor rectangle at that same slot. Point atlas geometry intentionally does not need the logical face index for raster placement; the receiver's per-face atlas metadata supplies the logical mapping.

   This establishes naming and indexing consistency in source, not rendered equivalence. It still depends on freezing the selected material, target count, matrices/indices, expanded instance count, and indexed dynamic state in one packet.

5. **The current layered paths have no per-caster target relevance mask.**

   The point component's face relevance mask selects faces for the whole light/pass, not faces per caster. No directional cascade mask or per-draw point-face mask is carried through `VulkanMeshRenderRequest`, `LayeredShadowUniformState`, `PendingMeshDraw`, or the generated instanced shaders. Consequently, a union-collected caster is expanded or geometry-fanned to every selected cascade/face even when it intersects only a subset. This is conservative for correctness but preserves the GPU work amplification that grouped rendering is intended to remove. The later architecture needs a per-caster target mask and a defined instance-to-source-instance mapping, or an equivalent prepartitioned draw stream.

6. **The Uber fallback can bypass the resolver's own instanced-compatibility guard.**

   Directional and point resolution first reject shared instanced use for multiply-instanced, deformed, or non-shared-opaque casters, but a later `CanUseSharedUberShadowFallback` branch can return the original global instanced override solely because the source is an Uber material. For a deformed mesh, `XRMeshRenderer.GetVersion` deliberately selects a deform/default vertex generator that does not write a shadow layer, while the late material kind can still request instanced expansion. For alpha-aware or multiply-instanced casters, the same branch can skip the intended geometry/material-specific variant. Treat this as a high-confidence conditional routing risk until a representative caster draw is captured; the fallback must not override vertex-stage, deformation, instance, or alpha-coverage compatibility.

7. **Phase 4 cannot perform the requested GPU equivalence comparison from the saved evidence.**

   The capture inventory contains only `directional-sequential.rdc`, `point-sequential.rdc`, `point-sequential-animated.rdc`, and `spot-sequential.rdc`. There is no directional or point legacy instanced capture, legacy geometry capture, grouped atlas capture, or per-caster fallback capture. Those sequential captures are already invalid controls because Phase 1 proved material loss. Phase 4 therefore completes source-contract localization and writes the exact later capture gates below; it does not claim sequential/instanced/geometry output equivalence.

8. **The live layered scopes count nesting but do not restore nested state.**

   Each directional/point push overwrites one shared flag/count/matrix/index store and increments a depth counter. An inner pop returns early while depth remains positive; it does not restore the outer flags or arrays that the inner push replaced. No reviewed current shadow path requires nested layered scopes, so this is not assigned as the active regression. It is a conditional correctness trap for future grouped/per-caster composition and should be converted to an actual stack or explicitly asserted non-nestable before the packet architecture depends on nesting.

9. **The enqueue-time fix must preserve pass sharing rather than copy the large state into every request.**

   `LayeredShadowUniformState` contains fourteen matrices, and the full required packet also includes view matrices and indexed target state. Storing that value inline in every queued mesh request would multiply memory traffic by caster count in the exact CPU-direct hot path being stabilized. The existing `VulkanMeshDrawViewSnapshot` deliberately shares one reference across matching draws, but it is constructed too late. The implementation shape should be one frame-owned, immutable shadow-pass snapshot per unique scope/generation plus a small per-caster envelope containing resolved material, original/expanded instances, and target mask. Use an arena, pool, or generation cache with explicit lifetime; do not add a per-draw heap allocation.

### Phase 5 freshness additions

1. **Point/spot completion becomes visible to the manager later, but the existing ring is not a GPU timeline.**

   The order is plan/publish, then render. `MarkTileRendered` queues a `ShadowTileCompletion` when the component render call returns; the record has no submission serial, fence, or timeline value. `DrainTileCompletions` runs at the next `BeginFrame`. In the saved Phase 0 lighting log, 48 directional completion records reconciled one to three render-frame ids later: four at one, 40 at two, and four at three. That distribution measures manager reconciliation latency only. Directional also commits its component cascade slots immediately, so it does not prove that the directional receiver used data two frames old.

2. **Point/spot content freshness is missing from the deferred receiver's generation key.**

   `PublishFrameData` advances its generation only when `HasLayoutChanged` sees allocation key/page/kind/rectangle/resolution/atlas-id changes. It deliberately ignores `LastRenderedFrame`, `ContentVersion`, fallback state, and texture storage identity. `VPRC_LightCombinePass.LightBindingState` includes that layout-only atlas generation, but not the point/spot allocation's rendered/content state, published frame id, atlas texture identity, or storage generation. A point or spot allocation can therefore change from dummy/unavailable to real/sampleable at the same rectangle without changing the generation used by persistent Vulkan light-binding artifacts. This is a confirmed invalidation-contract defect; its visible duration is not measurable from the saved screenshots.

3. **Directional logical freshness and GPU readiness are separate questions.**

   Directional grouped/tile paths immediately commit cascade atlas slots to the light before also queueing manager reconciliation. The deferred directional binding state includes cascade slot revisions/content and stale ages, so its logical receiver generation can advance in the render frame. That CPU-side commit is still not proof that the image write is GPU-ordered and transitioned before the receiver draw. Same-submission sampling is valid only if queue order plus the exact image barrier establishes write-before-read; the existing core Vulkan layout validation failures prevent assuming that contract is satisfied.

4. **Whole-frame lag is not established by the saved motion screenshots.**

   Immediate screenshots were not synchronized to the requested logical frame: repeated immediate hashes matched, while captures taken after five to eight engine frames changed. That evidence cannot distinguish shadow-generation lag from capture/present latency. Settled directional-atlas images moving with the camera also disprove a universal permanently frozen directional atlas in that session, but do not prove correct depth. Atlas array growth remains a separate confirmed freshness failure because it replaces storage without copying or invalidating resident pixels.

### Phase 6 work-amplification additions

1. **The current CPU work multiplier is structural.**

   For `T` targets, sequential directional/point rendering performs `T` visibility collections/swaps and approximately `sum(C_t)` CPU draw records/submissions. Layered rendering performs one union collection and approximately `C_union` CPU draws, but without a per-caster relevance mask the shaders expand/fan each union caster to all `T` targets, for roughly `T * C_union` GPU target work. Spot has `T=1`. Current Vulkan directional atlas fallback still executes one tile submission per cascade; current Vulkan point atlas grouping fails before a useful grouped draw.

2. **The atlas render-time budget cannot bound a slow first or critical directional entry.**

   `RenderScheduledTiles` checks elapsed time before an entry only after at least one entry has been scheduled, and camera-critical directional work uses `CriticalBypass`. A group is executed atomically. Saved thresholded CPU `Stopwatch` diagnostics include directional grouped/fallback entries of 18.37 ms and 48.65 ms; the latter occurred in a 50.60 ms atlas-render total with `MaxRenderMilliseconds=0.50`. These are CPU recording/path durations, not GPU times. The existing budget can control later admission but cannot prevent one expensive critical entry from producing a frame spike.

3. **CPU-direct performs redundant, unconsumed dynamic-data work per draw.**

   The main descriptor path attempts `TryCaptureCpuDirectDynamicData` before descriptor preparation, again through engine-uniform update with pass mask `1`, and again with the pass-aware mask: three writer acquisitions/comparisons/dirty marks per draw and up to three 160-byte copies when the masks differ. `EndWrite` marks the mapped range dirty even on the unchanged early-return path. No shader, descriptor, or runtime consumer of `$CpuDirectDynamicData` was found outside its producers and structural tests. Dirty regions may coalesce before a physical flush, so this is not evidence of three GPU flushes; it is confirmed hot-path CPU/mapped-arena churn and cannot explain incorrect pixels.

4. **Saved timing distinguishes route-transition churn from steady-state cost, but does not provide a benchmark.**

   The Phase 0 route switches produced roughly 45-97 ms whole-frame samples, with primary recording often dominant; the last sampled frame was about 26.6 ms whole-frame and 17.8 ms Vulkan recording. Pipeline/descriptor state grew from roughly 27 variants, nine pools, 140 sets, and 73 reservations/15,184 bytes to roughly 2,627 variants, 50 pools, 21,290 sets, and 3,423 reservations/657,120 bytes. A later unchanged 20-frame window held at 1,931 variants, 39 pools, and 14,450 sets, so the evidence indicates substantial cold route-transition retention/churn rather than a proven steady camera-motion leak. The isolated failed point-group executor itself was cheap in the throttled samples (0.03-0.19 ms) and is not the direct explanation for the 17.8 ms recording sample.

### Confirmed defects

1. **The point-atlas planner and light disagree, and the manager ignores the light's requested mode and capability result.**

   `ShadowAtlasManager.ShouldBuildGroupedAtlasRenderPlanEntries` enables grouped atlas entries on ordinary Vulkan. `PointLightComponent.ShouldPrepareAtlasGroupedFaceCollection` rejects grouped point-face preparation on every Vulkan backend. `ShadowAtlasManager.TryRenderPointFaceGroup` then returns failure without the per-face fallback that exists for directional cascades.

   `BuildPointFaceGroups` also does not consult `PointLightComponent.ShadowRenderMode` or the light's grouped-route capability. A point light explicitly requesting `Sequential` can therefore still be converted into a grouped manager entry on any backend and then fail when the light rejects the request. The controlled run's manager diagnostic reported six resident allocations with `LastRenderedFrame=0`, `LastDirtyReason=NeverRendered`, `LastSkipReason=StaleTileReused`, and `ActiveFallback=StaleTile`.

2. **The point grouped render plan owns only its seed request and duplicates its remaining members.**

   The point group lookup indexes only the first face key. Its plan entry sets both `RequestStartIndex` and `RequestEndIndex` to that seed index, and the planner does not suppress later exact member keys. The remaining point faces are emitted again as ordinary `Tile` entries in the same immutable plan. The isolated runtime shape (`pointGroups=1/6/0`, then `requests=6`, `entries=6`, `members=6`) confirms one group entry plus five duplicates.

   This has two bad outcomes:

   - when the grouped Vulkan entry fails, execution breaks before those later tile entries, so they are not a fallback;
   - if grouped execution succeeds on another backend, the precomputed later tile entries can render group members a second time, adding redundant collection, recording, clears, and writes.

   Copying the directional first/last-index behavior is not sufficient. Request priority sorting can interleave faces from unrelated lights, so advancing across an unchecked range can drop unrelated work. Group ownership must be keyed by exact member identity, with each group emitted once and only its later members suppressed.

3. **A grouped render failure is mislabeled and charged as budget deferral.**

   `RenderScheduledTiles` logs `RenderFailed`, but for any failed non-tile entry it then sets `deferredByBudget`, records the raw request tail through `RenderWorkBudgetCoordinator.RecordShadowAtlasQueue`, and breaks. This obscures the real terminal state and stops the remaining plan entries. In the isolated run, one six-face grouped call produced `scheduled=0`, `checked=6`, `failed=6`, `deferred=6`, and `firstDeferredIndex=0`; in the mixed run, one four-face grouped call was reported as six deferred requests. The `checked`/`failed` counters are member-equivalent costs, while `deferred` is a raw request-tail count. That tail can include the failed group itself, forced-skipped/nonmember requests, duplicate member tiles, and unrelated later lights. The same raw-tail arithmetic also makes genuine budget/texture deferral counts unreliable when plan entries and request indices are not one-to-one.

4. **The directional legacy geometry-shader path can receive a zero cascade count and emit no primitives.**

   On Vulkan, `DirectionalLightComponent.SetShadowMapUniforms` deliberately publishes `CascadeLayerCount=0`. The material resolver restores captured matrices and the count only for instanced-layered material kinds, not geometry-shader kinds. `DirectionalCascadeShadowDepth.gs` loops to `CascadeLayerCount`, so the geometry variant has a deterministic zero-output path when that state reaches the draw.

   This is broader than selecting `GeometryShader` at the light. `MeshRenderMaterialResolver` may replace an instanced-layered override with a geometry variant for a deformed, multiply-instanced, or material-specific caster. The scoped render state still says instanced layered, but immutable directional state is restored only when the *selected material kind* is instanced. Those individual fallback casters can therefore receive zero cascades and disappear even while the light reports an instanced route.

   The controlled atlas run itself used the sequential Vulkan atlas fallback, so it did not exercise this chain. A future legacy-array or per-caster geometry draw inspection is still needed to identify which visible casters hit it.

5. **The requested grouped Vulkan architecture is currently unavailable.**

   Directional atlas grouping is explicitly rejected on Vulkan and hidden behind sequential per-cascade fallback. Point atlas grouping is also rejected, but without a fallback. Consequently, the current implementation cannot meet the requirement that directional cascades and six point faces use instanced-layered or geometry-shader rendering in both legacy and atlas forms.

6. **Growing an atlas texture array discards existing page contents without invalidating their resident allocations.**

   `ShadowAtlasEncodingState.EnsureTextureArrays` allocates larger color/depth arrays, rebuilds every existing page framebuffer against them, destroys the old arrays, and performs no layer copy. Resident allocations retain `LastRenderedFrame` and `ContentVersion` when page and rectangle placement match. Neither `RequiresTileRender` nor physical-allocation identity includes the array resource generation. After capacity growth (for example, one layer to two), old pages can therefore be treated as clean and sampleable even though their backing pixels were discarded.

   `EstimateAdditionalArrayBytes` budgets only the steady-state capacity delta, while recreation temporarily holds the full old and full new arrays at once. That transient allocation peak and framebuffer rebuild are also plausible one-frame stutter or allocation-failure sources when a new page is added.

7. **Point and spot atlas metadata is published before rendering, creating a built-in publication delay.**

   `Lights3DCollection.RenderShadowMapsInternal` plans and calls `ShadowAtlas.PublishFrameData` before `ShadowAtlas.RenderScheduledTiles`. Point and spot receivers read `PublishedFrameData` and require a nonzero `LastRenderedFrame`. Their recorded tile state is reconciled only when `DrainTileCompletions` runs at the next `BeginFrame`, then becomes visible at the next publish. A newly rendered point or spot tile is therefore unavailable through published metadata until a later planning/publish cycle, normally at least the next logical frame.

   Directional cascades use a different path: the manager immediately calls `CommitRenderedCascadeAtlasSlots` before queuing reconciliation, so directional logical publication can advance in the render frame. The three atlas types do not share one freshness contract.

   The completion ring silently drops a record when full. Its shared `_queueOverflowCount` is incremented after the frame data was already published, then reset at the next `BeginFrame` before the next publish. Published metrics can therefore miss completion-ring overflow entirely, leaving a point/spot tile logically stale with little durable evidence.

8. **Point/spot content publication does not invalidate the deferred light binding.**

   `ShadowAtlasManager.PublishFrameData` advances `PublishedFrameData.Generation` only for layout changes. Its comparison excludes `LastRenderedFrame`, `ContentVersion`, fallback/skip state, atlas texture identity, and storage generation. `DeferredLightBindingPublisher` keys persistent light-binding publication with `LightBindingState`, which includes that layout-only atlas generation but none of those point/spot content facts. `LightComponent.BindingGeneration` also does not advance when manager completion reconciliation makes a point/spot tile sampleable.

   The first publish can therefore bind a dummy or disable real atlas sampling while `LastRenderedFrame=0`; after the tile is reconciled at the same allocation, the next publish can expose valid metadata without changing the generation seen by Vulkan's binding artifacts. Reuse is then authorized even though the shader-visible resource choice changed. Split layout, content-publication, and storage generations, and include the exact per-light/per-face sampleability generation and texture identity in receiver publication. Increment on shader-visible content transitions rather than blindly invalidating every light once per frame.

9. **Point and spot atlas texture readiness checks are weaker than directional checks.**

   Both deferred and forward binders require `IsTextureReadyForShadowSampling` for the directional atlas. Their point and spot atlas paths only require that the texture object/page exists. This can expose newly created or recreated Vulkan arrays before the renderer reports them sample-ready. In deferred point lighting, `TryBindPointAtlasShadow` also returns *requested* rather than *has a sampleable face*, enabling the atlas shader path while binding the dummy array. That fallback may intentionally produce lit/contact-only behavior, but `LightHasShadowMap` and `PointShadowAtlasPathEnabled` are not proof that any real face is available.

10. **Point geometry variants are outside the immutable layered restore gate, and legacy geometry is missing both its matrix alias and face mask.**

   `MeshRenderMaterialResolver.ApplyShadowUniforms` restores captured point matrices and face indices only when both the selected material kind and captured pass are instanced-layered. Point geometry and atlas-geometry material kinds bypass that immutable restore altogether, even though `LayeredShadowUniformState` contains the face count, matrices, and indices. They therefore still depend on live scoped callbacks that the CPU-direct deferred path has already lost.

   There is a second contract split underneath that gate: legacy `PointLightShadowDepth.gs` consumes `ViewProjectionMatrices[6]`, while the atlas geometry shader and generated instanced vertex path consume `PointShadowViewProjectionMatrices[6]`. The legacy GS also tests `PointShadowFaceMask`; its default value of zero rejects every packed face. `PointLightComponent.SetShadowMapUniforms` uploads both matrix aliases and derives the mask only while live scoped state exists, whereas `MeshRenderMaterialResolver.SetPointLightLayeredUniforms` restores only the latter alias, indices, and count. The immutable packet must apply to both instanced and geometry kinds, publish both aliases during transition (or standardize the shaders), and derive the exact mask from the captured logical face indices.

### High-confidence source risks

11. **Dirty point faces do not consistently stay grouped.**

   `CanDirectionalCascadeJoinGroup` accepts both normal and `StaleTileReused` allocations; `CanPointFaceJoinGroup` accepts only `SkipReason.None`. Local projection/camera-fit changes are forced fresh, but other dirty reasons can be marked stale-reused. Those point faces then fall out of the grouped path and become individual tile entries. Even after Vulkan grouping is enabled, dynamic caster/content invalidation can silently return point shadows to per-face work.

12. **Atlas gutters are allocated but not populated, and UVs address tile edges rather than texel centers.**

   Atlas passes render and clear only `InnerPixelRect`; no reviewed path clears the full `PixelRect`, dilates edge texels into the gutter, or copies a border. `XRENGINE_ShadowAtlasUvFromLocal` maps clamped local UV `[0,1]` directly through scale/bias based on the inner integer rectangle. UV `1` lands on the boundary after the final inner texel. Nearest sampling at a boundary and linear/moment filtering can therefore read an untouched gutter or neighboring tile. PCF offsets are clamped back to the same edge, so the gutter currently does not serve its intended filtering purpose.

13. **Grouped point-atlas collection and grouped replay use different viewport ownership.**

   Atlas collection resolves the light's ordinary point plan as sequential and collects/swaps separate per-face viewports. The grouped renderer later replays only viewport 0. This path is unreachable on current Vulkan because the earlier capability gate rejects it, but merely enabling grouped Vulkan rendering would expose incomplete or stale caster coverage. The final grouped design must prepare one immutable influence-volume/union caster packet and replay that same packet into every compact viewport slot.

### Unresolved and disfavored causes

14. **Basic sequential writers are now captured, and none qualifies as a correct control.**

   Directional sequential rendering incidentally writes four distinct, non-clear D24 layers, but with the wrong material and fixed-function state. Point sequential rendering aliases all six logical faces to physical slice 5 and writes ordinary scene output into the sampled color cube. Spot sequential rendering writes ordinary scene output into the sampled `R16_FLOAT` texture even though its companion depth target changes. Phase 2 has now audited the exact receiver resources, views, samplers, layouts, light payloads, and comparisons. That audit localizes the resulting over-shadowing but cannot establish a correct lighting baseline until the producers are repaired and both phases are repeated.

15. **A generic CPU-direct mapped-memory race is disfavored.**

   CPU-direct updates auto/engine uniform buffers immediately before descriptor binding and uses completion-gated mapped arenas. The historical auto-uniform frequency bug is also still fixed: struct snapshots inherit `block.Frequency` and publication generation is selected by that frequency.

   Atlas policy and the publish-before-render ordering can nevertheless bind a logically old generation while memory synchronization remains correct. Reopen the mapped-memory theory only if a future capture proves that the camera/light payload bound for a draw is itself stale.

16. **CPU work amplification is structurally confirmed; its share of end-to-end stutter is not yet measured.**

   Current confirmed or high-confidence contributors are:

   - sequential cascade/face visibility collection and command recording after grouped rendering is rejected;
   - duplicate point-face tile entries after a grouped plan entry;
   - full or union caster replay into multiple targets;
   - coarse shadow resource-plan changes invalidating large command-chain cohorts even when concrete buffer identity is unchanged;
   - three attempted captures of the unused 160-byte `$CpuDirectDynamicData` record in the main descriptor path per direct draw, including dirty marking on unchanged writes;
   - failed point-atlas groups charged as deferred queue depth and retried;
   - texture-array recreation when atlas page capacity grows.

   In addition, the atlas budget is non-preemptive: it admits the first entry regardless of elapsed time and allows critical directional work to bypass the time limit, so one atomic multi-cascade fallback can exceed the configured budget by tens of milliseconds. These mechanisms explain plausible CPU time and cadence. The saved route-switch session does not isolate their steady-state p50/p95/p99 contribution, and the unused dynamic record cannot explain incorrect pixels because no shader/binding consumer was found.

## Evidence and confidence ledger

| Finding | Confidence | What is still required |
|---|---:|---|
| CPU-direct computes its compatibility signature from the correct in-scope material, queues only the local override/original instances, then resolves material, expansion, and layered state after the scopes are gone | Confirmed in source, all 109 Phase 0 program records, and representative directional/point/spot Phase 1 draws | Capture one packet before enqueue and prove signature, prepared material, draw material/count, matrix hash, and recorded draw all match |
| Sequential directional, point, and spot draws use ordinary scene programs rather than their forced shadow materials | Confirmed per draw in Phase 1 RenderDoc captures | Repeat after the immutable draw packet fix and require the intended shader/state at every caster |
| Point sequential face passes retain one mutable FBO until late target snapshot and all bind cube slice 5 | Confirmed in source and capture; slices 0–4 receive no clear/write in the captured frame | Freeze the native attachment identity per face before queueing, then prove six distinct slices and orientations |
| Directional sequential writes four distinct non-clear D24 layers and the array is later used as a PS resource | Confirmed for the captured sparse scene, but with the wrong material/state; Phase 2 decoded the active four-cascade receiver payload | Re-capture with the intended shadow material; then verify split overlap and repeat the receiver comparison against valid depth |
| Deferred directional, point, and spot draws bind the exact Phase 1 producer image/view rather than a dummy and set `LightHasShadowMap=true` | Confirmed at EIDs 20882, 1169, and 302 | Repeat after producer repair and expose engine storage/publication generation alongside the capture-local resource ID |
| Point cube view `12141` spans six faces while faces 0-4 remain `TRANSFER_DST_OPTIMAL` and only face 5 is shader-readable | Confirmed in the EID 1169 pipeline state and a `-X` pixel trace | Freeze six distinct writer targets, transition every sampleable face, and assert the entire descriptor subresource range before the draw |
| Directional payload has four matrices/splits and spot payload has a populated world-to-light transform/range; both execute manual shadow comparisons | Confirmed in uniform decoding and pixel traces | Repeat with valid producer data and compare producer/receiver matrix hashes and generations |
| Point and spot sampled color shadow resources contain ordinary scene output | Confirmed visually, through shader reflection, texture ranges, later PS-resource usage, and receiver pixel traces | Re-capture the required depth/moment encoding, then repeat the receiver trace as the correctness baseline |
| Sequential captures emit `VUID-vkCmdPipelineBarrier-pBufferMemoryBarriers-02818` for HOST_READ/ALL_COMMANDS | Confirmed in all three Phase 1 captures | Identify the buffer barrier owner and replace it with a destination stage compatible with host read |
| Lit atlas startup emitted core image-layout and descriptor-layout validation errors through dynamic-rendering and scheduled-secondary code | Confirmed in the flushed session log; not isolated to one light/resource | Reproduce one light at a time with cumulative validation capture, then repeat with primary reuse disabled |
| Point grouped atlas entry is planned despite the requested mode/capability result, rejected by the Vulkan light, and lacks reachable per-face fallback | Confirmed in source and the isolated `pointGroups=1/6/0` route evidence | Implement one planner-time route contract, then capture either six explicit sequential tiles or one accepted group |
| One six-member point group becomes six immutable plan entries because later exact members are emitted as duplicate tiles | Confirmed in source and the isolated `requests=6`, `entries=6`, `members=6` runtime shape | Suppress exact member keys after emitting a group; do not skip a raw first-to-last range unless contiguity is asserted |
| Failed grouped work is counted as budget-deferred and stops the request tail | Confirmed in source and runtime (`failed=6`, `deferred=6` isolated; `failed=4`, `deferred=6` mixed) | Publish distinct unsupported, render-failed, budget-deferred, texture-deferred, and stale-reused terminal states |
| Grouped point collection prepares separate face viewports but grouped replay uses only viewport 0 | High-confidence source risk; current Vulkan rejection prevents draw validation | Prepare one immutable union caster packet and verify identical packet identity at all grouped viewport slots |
| Directional legacy GS can see `CascadeLayerCount=0`, including per-caster fallback from an instanced pass | Confirmed conditional source defect | Inspect an active geometry material draw and its immutable packet |
| Point legacy GS is excluded from immutable restore; the current helper also omits `ViewProjectionMatrices[]` and `PointShadowFaceMask` | Confirmed conditional source defect; shader default mask zero has a deterministic no-output result | Capture an active GS draw with exact matrices/indices/derived mask and inspect all selected cube layers |
| The specialized instanced `XRMeshRenderer.BaseVersion` survives enqueue even though material/state do not | Confirmed in source | Record requested vertex version and final material/shader stages together; do not diagnose every layered failure as vertex-route loss |
| Immutable pass generation hashes the complete shadow state after `PendingMeshDraw` exists | Confirmed in source | Feed it the enqueue-time packet, expose the generation/packet id, and verify descriptor/dynamic-offset refresh on motion |
| Request drain restores a mutable `pipeline.LastRenderingCamera`, while view/projection matrices, render area, and transform id are captured only during materialization | Confirmed source ownership gap; visible staleness not established | Freeze an enqueue-time view snapshot or prove frame ownership, then compare producer/recorded camera generations during motion |
| No current layered draw carries a per-caster cascade/face relevance mask | Confirmed in request/state/draw/generated-shader source inventory | Add or explicitly reject a mask design, then measure target expansion and verify seam/overlap correctness |
| Uber fallback can reselect a shared instanced override after deformation/instance/opaque compatibility failed | High-confidence conditional source risk | Inspect deformed, multiply-instanced, alpha/cutout, and Uber casters; require a compatible vertex/material pair and fallback reason |
| Compact matrix/logical-layer/indexed-viewport slot math is internally consistent for the reviewed instanced and GS shaders | Confirmed in source only | Compare sequential, instanced, and GS captures after the common producer fix; no layered capture currently exists |
| Directional/point layered live scopes track nesting depth but do not restore the outer payload after an inner push/pop | Confirmed conditional source defect; no current nested caller established | Make scopes stack-correct or assert non-nesting, then add a targeted nested-state check only after feature validation is cleared |
| Naively embedding the complete shadow/view packet in each request would copy fourteen-plus matrices per caster | Confirmed design/performance risk from current struct shape; no implementation made | Share one frame-owned immutable pass snapshot and measure request size/allocation/copy cost before and after |
| Directional grouped atlas is disabled and falls back sequentially | Confirmed in source and live route | Verify each fallback tile contains current valid depth |
| Atlas array growth discards old layer pixels without invalidating resident content | Confirmed conditional source defect | Later exercise a capacity-growth case and inspect every old layer |
| Point/spot publication occurs before render and reconciliation occurs at the next begin/publish cycle | Confirmed in source; saved completion-ring records reconciled after 1-3 render-frame ids, with 40/48 at two | Measure actual presented stale age and decide whether same-frame publication is required; the ring is not a GPU timeline |
| Point/spot content-only completion can make an allocation sampleable without changing atlas or deferred-light binding generation | Confirmed generation-contract defect in source | Add layout/content/storage generations, bind exact content/image identity, and prove dummy-to-real publication refreshes without unrelated invalidation |
| Completion-ring overflow can drop point/spot state and its counter can be reset before frame-data publication observes it | Confirmed conditional source/diagnostic defect | Split request-queue and completion-ring counters and retain cumulative/high-water values |
| Point/spot binders omit the directional atlas readiness gate | Confirmed in source | Verify Vulkan readiness transitions and dummy substitution after resource recreation |
| Atlas page-resource lookup selects the shared array object and per-allocation metadata selects the page/tile | Source-consistent; no separate page-zero binding mismatch found | Validate the physical image/view and page index in the first successful atlas capture |
| Point sequential non-atlas issued six logical face passes, but every GPU pass targets physical slice 5 | Confirmed in Phase 1 | Fix immutable target capture and then inspect all six physical faces and receiver binding |
| Spot non-atlas producer writes and is sampled, but carries ordinary G-buffer output instead of shadow depth/moments | Confirmed in Phase 1 | Re-capture after material preservation, then verify cone matrices, range, and receiver comparison |
| A generic CPU-direct UBO race causes shadow lag | Disfavored | Reopen only if a capture shows the bound camera/light matrices themselves are stale |
| Atlas gutter/texel-edge handling can sample outside valid inner content | High-confidence source risk | Inspect boundary samples for depth and moment encodings after the core route works |
| Sequential target multiplication and all-target layered expansion amplify work | Confirmed structurally; end-to-end share is unmeasured | Add per-target counters/masks and run the warmed route matrix |
| Atlas `MaxRenderMilliseconds` is not a hard cap for the first/critical atomic entry | Confirmed in source and saved 18.37/48.65 ms directional diagnostics; one total was 50.60 ms at a 0.50 ms budget | Add explicit bypass/overrun accounting and decide whether to split or pre-record expensive entries |
| Main CPU-direct descriptor path attempts unused `$CpuDirectDynamicData` capture three times per draw | Confirmed in source; no runtime consumer found | Remove/gate or wire it, then measure attempts, changed copies, dirty/flush bytes, and CPU time |
| Route transitions caused large pipeline/descriptor growth, while one unchanged 20-frame window was stable | Confirmed in saved snapshots; steady-motion leak not established | Segment warmed configurations and measure creation/reuse/invalidation counts with timestamps |

## Current route matrix

"Producer status" means whether the depth-writing route appears structurally viable. Phase 2 receiver evidence is included where captured, but it does not certify correctness while the producer contents are invalid.

| Light | Storage | Sequential | Instanced layered | Geometry shader | Current assessment |
|---|---|---|---|---|---|
| Directional | Legacy array/non-atlas | Four physical D24 layers receive non-clear depth, but through the wrong material/cull state | Component selects an instanced vertex version, but late material/state resolution loses count/matrices and draw expansion | Component selects GS; immutable restore omits it and Vulkan callback supplies count zero | Sequential target addressing works; CPU-direct material/state loss still prevents a valid baseline and also affects both layered routes |
| Directional | Atlas | Active Vulkan fallback | Falls back sequentially | Falls back sequentially | Four allocations render according to manager diagnostics, but CPU-direct draw material/state and actual depth remain unproven; grouped target architecture is unavailable, and a critical atomic fallback can exceed the render budget by tens of milliseconds |
| Point | Legacy cube/non-atlas | Six logical passes all target physical slice 5; sampled color contains ordinary scene output; receiver view spans six faces but faces 0-4 remain transfer-destination and a real `-X` sample returns zero | Compact slot-to-matrix and logical face-to-layer math is coherent, but late resolution loses state and instance expansion | Immutable restore omits geometry, the helper omits `ViewProjectionMatrices[]`, and it does not derive `PointShadowFaceMask` | CPU-direct loses material/state, late snapshot collapses sequential layer addressing, and no layered route has a valid captured baseline |
| Point | Atlas | Light says sequential, manager still groups 1/6, and the plan becomes one group plus five duplicate tiles | Same manager mismatch | Same manager mismatch | The grouped call fails and stops before any per-face entry; after that is fixed, content-only sampleability still needs a receiver-generation advance because the current layout generation cannot publish dummy-to-real binding changes |
| Spot | Legacy 2D/non-atlas | R16+D32 targets change, but sampled R16 contains ordinary scene output; receiver binding/layout/matrix are coherent and a trace produces only 2/8 lit taps | Not applicable | Not applicable | CPU-direct loses the forced depth/moment material; lighting interprets brick-color values as depth while the correct companion depth attachment is not sampled |
| Spot | Atlas | Single-tile manager diagnostic advances | Not applicable | Not applicable | Allocation metadata can advance without changing deferred binding generation; CPU-direct material loss plus readiness/storage risks still keep actual depth and sampling unproven |

Directional lights render a configurable cascade count, not six directions. Point lights render six cube faces. Both need one collected/batched stream that targets multiple layers or atlas viewports without N independent full submissions.

## Causal chains to validate

### CPU-direct layered request boundary

```text
layered shadow scope selects a specialized XRMeshRenderer.BaseVersion
  -> preparation signature resolves the correct per-caster shadow material
  -> queued request stores the specialized renderer but only the raw local override/original instance count
  -> global shadow material and layered scope pop
  -> materializer resolves an ordinary or incompatible material from live state
  -> layered instance expansion and LayeredShadowUniformState capture see zero/false state
  -> program-binding snapshot and auto-uniform publication faithfully freeze the wrong late inputs
  -> prepared draw no longer matches the material/state represented by its compatibility signature
```

Do not fix this by recapturing later or by making the late snapshot more immutable. Resolve the per-caster material and capture the complete shadow packet at `OnRenderRequested`, then use those exact values for the signature, preparation, draw count, program bindings, and pass generation.

### Point atlas

```text
ordinary Vulkan allows grouped atlas plan
  -> manager creates one point-face group without consulting requested mode/capability
  -> point plan ownership covers only the seed request; remaining exact members also become Tile entries
  -> PointLightComponent rejects Vulkan grouped preparation/rendering
  -> TryRenderPointFaceGroup has no sequential fallback
  -> executor labels the raw failed request tail as budget-deferred and breaks before Tile entries
  -> allocations remain NeverRendered and later point work is starved
  -> stale/uninitialized atlas content is exposed or shadows are suppressed
```

Do not repair this by blindly advancing from the first member index to the last. Point requests are priority-sorted and different lights can interleave. Emit each group once, track every exact member key, and suppress only those later member keys.

The latent success path is also wrong:

```text
grouped point render succeeds
  -> collection has prepared separate per-face viewport buffers, but grouped replay uses viewport 0 only
  -> incomplete/stale caster coverage can be fanned out to every compact viewport slot
  -> all group-member completion records are enqueued anyway
  -> immutable plan still contains later per-face Tile entries marked RequiresRender
  -> group members can be rendered and marked again
  -> duplicate collection/recording/clears/writes amplify frame time
```

### Directional legacy geometry shader

```text
GeometryShader selected globally, or selected per caster as fallback from InstancedLayered
  -> Vulkan SetShadowMapUniforms sets CascadeLayerCount = 0
  -> material resolver restores immutable state only for an instanced material kind
  -> DirectionalCascadeShadowDepth.gs loops zero times
  -> no cascade primitives or depth
```

### Point legacy geometry shader

```text
point geometry material selected
  -> CPU-direct scope loss removes the live layered callback inputs
  -> immutable restore gate excludes geometry material kinds
  -> even a widened current helper publishes only PointShadowViewProjectionMatrices[]
  -> PointLightShadowDepth.gs reads ViewProjectionMatrices[] and PointShadowFaceMask
  -> matrices are absent and the default zero mask rejects every logical face
  -> no cubemap primitives or radial-depth output
```

### Point/spot atlas publication lag

```text
BeginFrame drains prior CPU-side completion records
  -> requests are solved
  -> PublishedFrameData is frozen
  -> tile render calls are recorded afterward
  -> CPU-side completion records are queued without a GPU timeline token
  -> current point/spot receiver still sees the pre-render publication
  -> next BeginFrame reconciles the record
  -> next PublishFrameData can expose valid content at the same allocation
  -> atlas Generation remains unchanged because only content/readiness changed
  -> deferred LightBindingState also remains unchanged
  -> Vulkan may reuse the earlier dummy/disabled receiver publication
```

The first half guarantees a logical publication-cycle delay for a newly rendered point or spot tile. The second half is a separate generation-key defect that can extend stale receiver binding beyond that intentional cycle. Neither by itself proves an unsafe GPU read/write overlap; same-queue ordering and barriers must be reviewed separately. Instrument CPU-recorded, submitted, GPU-completed, metadata-published, binding-published, and sampled milestones as different values.

### Atlas array growth

```text
new page exceeds current array capacity
  -> larger color/depth arrays are allocated
  -> existing page FBOs are rebound to the new arrays
  -> old arrays are destroyed without copying their layers
  -> resident allocations keep LastRenderedFrame and ContentVersion
  -> RequiresTileRender sees unchanged placement/content
  -> discarded old-page pixels can be advertised as clean/sampleable
```

### Directional atlas stutter and lag

```text
camera movement changes cascade views
  -> grouped Vulkan execution is rejected
  -> dirty directional refresh is classified CriticalBypass
  -> one atomic grouped entry executes sequential member collection/recording
  -> first-entry/time-budget checks cannot preempt it
  -> one multi-cascade entry exceeds the configured budget
  -> primary-chain recording produces a visible CPU cadence spike
```

The scheduling and CPU-overrun portion is source-confirmed and appears in the saved 18.37/48.65 ms directional diagnostics. A directional receiver sampling an older generation is still unproven: immediate cascade-slot commit can advance logical binding state, while physical image ordering remains capture-dependent.

### CPU-direct per-draw shadow amplification

```text
sequential fallback multiplies caster draws by cascade/face count
  -> each direct draw enters the main descriptor-binding path
  -> unused $CpuDirectDynamicData capture is attempted before descriptor preparation
  -> engine-uniform update attempts it again with mask 1
  -> pass-aware path attempts it a third time
  -> mapped range is marked dirty even when bytes compare unchanged
  -> shadow draw multiplication also multiplies unconsumed writer/dirty work
```

Dirty regions may coalesce before the noncoherent flush, so this chain does not assert three physical flushes. It is CPU overhead only and cannot explain incorrect shadow pixels.

### Common producer-to-sampler failure

```text
shadow request
  -> caster collection
  -> depth draw
  -> image layout/readiness
  -> atlas or legacy publication
  -> descriptor/image view binding
  -> light metadata and transform binding
  -> receiver comparison/filtering
  -> visible lighting contribution
```

The investigation must identify the first broken edge. A legacy point face mask proves only that CPU render calls were issued and marked; it does not prove GPU completion, valid depth, or receiver sampling.

### Point sequential receiver layout

```text
six logical point-face requests share one mutable per-face framebuffer
  -> late target snapshot resolves every writer to physical face 5
  -> only face 5 completes an attachment-to-shader-read transition
  -> faces 0-4 retain transfer-destination layout/zero contents
  -> deferred point descriptor view still spans all six cube faces
  -> receiver direction selects -X / face 1
  -> sample returns zero and every radial-depth comparison fails
  -> point-light contribution is fully shadowed
```

Do not treat a whole-cube descriptor as sample-ready because one face is shader-readable. Descriptor-transition code must either establish one valid state for the complete view or emit correct per-subresource barriers for every distinct prior state.

## Desired architectural invariants

Use these as design constraints once implementation begins:

1. **Sequential non-atlas is the authoritative correctness baseline.** Each directional cascade, point face, and spot map must independently produce and sample correct current depth before enabling batching.
2. **One immutable shadow-pass packet owns all route and shader inputs.** Capture it before the CPU-direct queue boundary. It must carry the already-resolved material and resolver reason, selected vertex/material route, original and expanded instance counts, enqueue-time view/projection and transform identity, generic/instanced/atlas flags, matrices, target count, logical face/cascade mapping, derived target mask, indexed viewport/scissor and native target-subresource identity, atlas rect transforms, and a joinable frame/packet generation. Instanced and geometry variants consume the same snapshot contract; no shadow shader may depend on a live component scope after enqueue.
3. **Capability selection is authoritative and end-to-end.** Per-light requested mode, device capability, planner, collector, executor, selected per-caster material, shader variant, and fallback must agree on one route. “Unsupported,” “failed,” “deferred,” and “stale reused” are distinct states.
4. **A grouped render-plan entry owns its exact member set.** Every member key appears once, success cannot leave duplicate tile entries, and failure either executes an explicit fallback for that same set or leaves a `Failed` terminal state without pretending it was a budget event. Raw first/last indices are diagnostics unless the planner explicitly constructs and asserts contiguity.
5. **Grouped visibility is collected once with a per-target mask.** Perform one conservative union broad phase, compute a cascade/face bitmask per caster, resolve material/batch state once, and expand only to selected targets. Preserve overlap at cascade transitions and point-face seams.
6. **Atlas publication has an explicit GPU-ordering, generation, and readiness contract.** Distinguish CPU-recorded, submitted, GPU-completed, metadata-published, binding-published, and receiver-sampled milestones. Maintain separate monotonic layout, content-publication, and storage generations; point/spot dummy-to-real transitions must invalidate the exact receiver binding even when placement is unchanged. Same-submission sampling may publish before fence completion only when pass order and an image barrier guarantee write-before-read; cross-frame reuse must use completion-gated storage. Never label CPU recording as GPU completion. Record the intentional maximum stale age; do not silently reuse forever.
7. **Atlas storage generation is part of content and descriptor identity.** Growing or recreating an array must either copy every valid old layer or invalidate and redraw every resident allocation before it is sampleable, and the new texture/view identity must reach binding publication even when page rectangles are unchanged.
8. **Tile filtering has an explicit border contract.** Render/clear and populate the required gutter, and clamp sample coordinates to valid texel centers for the selected kernel and encoding. A whole-layer clamp mode is not tile isolation.
9. **Descriptor readiness covers the exact sampled view.** Before each draw or pass-level descriptor cohort, transition every mip/layer in the bound view. A false native-descriptor lookup must have an explicit fallback, and heterogeneous prior layouts must be split into valid per-subresource barriers rather than collapsed to `Undefined`.
10. **Resource identity drives command reuse.** Do not invalidate broad command cohorts merely because a coarse planner revision changed if the concrete buffers, descriptors, and immutable payload are unchanged. Conversely, a replaced texture array must change identity even when page indices and rectangles do not.
11. **No per-draw diagnostic writes in the steady hot path unless consumed.** Diagnostic capture should be gated and measured.
12. **A render budget has explicit overrun semantics.** The scheduler must report first-entry and critical-bypass overruns separately. If an atomic entry can exceed the target, either bound/split its preparation and recording or expose that unavoidable worst-entry cost; do not present `MaxRenderMilliseconds` as a hard frame cap.

## Ordered debugging procedure

Do these phases in order. Use one light at a time on ordinary desktop Vulkan, not Monado. Preserve the same camera, model, resolution, exposure, and light values across comparisons.

### Phase 0: establish the route truth table

For every run, record:

- Vulkan backend/device and dynamic-rendering mode;
- mesh submission strategy (`CpuDirect` for the primary reproduction);
- atlas on/off and shadow encoding;
- per-light requested kind, manager entry kind, effective light kind, and selected per-caster material kind;
- grouped-plan request range, member range, created/accepted/rejected status, and fallback reason;
- caster count, scheduled target count, CPU-recorded target mask, submitted target mask, and GPU-completed target mask;
- atlas array resource/storage generation and texture readiness;
- requested/recorded/GPU-completed/published/sampled generation and stale age;
- terminal state (`Rendered`, `Unsupported`, `Failed`, `DeferredBudget`, `DeferredDependency`, or `StaleReused`);
- validation/descriptor errors and placeholder/dummy sampling.

Start with this matrix:

| Case | Atlas | Render kind | Motion |
|---|---:|---|---|
| Lights off baseline | Off | N/A | Static, then camera sweep |
| Directional control | Off | Sequential | Static, then camera sweep |
| Directional layered | Off | Instanced, then GS | Static, then camera sweep |
| Directional atlas | On | Sequential fallback, then grouped variants | Static, then camera sweep |
| Point control | Off | Sequential | Static, then light/camera sweep |
| Point layered | Off | Instanced, then GS | Static, then light/camera sweep |
| Point atlas | On | Sequential requested, then instanced and GS | Static, then light/camera sweep |
| Spot control | Off | Sequential | Static, then light/camera sweep |
| Spot atlas | On | Single tile | Static, then light/camera sweep |

Do not infer the active route from the requested editor property. Capture the effective route at the draw.

#### Phase 0 execution record — 2026-08-14

Phase 0 was run in the isolated editor session `vk-shadow-phase0-20260814` with the Unit Testing World, one light at a time for the settled matrix, validation enabled, and shadow auditing enabled. The engine was forced to `CpuDirect`; primary command-buffer reuse remained enabled. Temporary light/probe settings were restored after the session, and the named session was stopped normally.

Evidence locations:

- task root: `Build/_AgentValidation/20260814-110450-vulkan-shadow-phase0/`
- copied logs: `Build/_AgentValidation/20260814-110450-vulkan-shadow-phase0/logs/`
- screenshots: `Build/_AgentValidation/20260814-110450-vulkan-shadow-phase0/mcp-captures/`
- isolated session: `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260814-110535-vk-shadow-phase0-20260814/`

Runtime and capability baseline:

- device: NVIDIA GeForce RTX 4070 Laptop GPU (`vendorId=0x10DE`, `deviceId=10336`);
- target mode: Vulkan dynamic rendering;
- submission: `CpuDirect`;
- validation layers: enabled; synchronization validation: disabled;
- primary command-buffer reuse: enabled;
- layered framebuffer, vertex-layer, geometry-layer, vertex/geometry viewport-index, viewport-array, and Vulkan multiview capabilities: all reported supported;
- maximum viewports: 16;
- desktop output: 1920x1080, 1286x723 internal, TSR, MSAA 4x;
- GPU pipeline profiling: disabled, so Phase 0 contains no trustworthy GPU timing comparison.

The first fast property sweep exposed a configuration-transition race: a newly requested route could be visible before the effective route converged. Every case below was therefore repeated after a barrier of at least four completed engine frames. This settled table supersedes the first immediate reads.

| Light/storage | Requested mode | Settled effective route | Backend/fallback | Producer-side observation |
|---|---|---|---|---|
| Lights off | N/A | N/A | N/A | Stable control captured at two settled camera poses |
| Directional legacy | Sequential | Sequential | `LegacyTextureArray`; `SequentialRequested` | Component reports sequential render completion; actual depth uninspected |
| Directional legacy | Instanced layered | Instanced layered | `LegacyTextureArray`; no fallback | Device supports the route; actual queued draw lost shadow material/state |
| Directional legacy | Geometry shader | Geometry shader | `LegacyTextureArray`; no fallback | Device supports the route; actual queued draw lost shadow material/state, in addition to the separate zero-count GS hazard |
| Directional atlas | Sequential | Sequential | `SequentialVulkanCascadeAtlas`; `SequentialRequested` | 4 requests, 4 resident, rendered-frame diagnostic advances |
| Directional atlas | Instanced layered | Sequential | `SequentialVulkanCascadeAtlas`; `VulkanCascadeAtlasGroupedRenderingDisabled` | Grouped Vulkan path was not exercised |
| Directional atlas | Geometry shader | Sequential | `SequentialVulkanCascadeAtlas`; `VulkanCascadeAtlasGroupedRenderingDisabled` | Grouped Vulkan path was not exercised |
| Point legacy | Sequential | Sequential | `SequentialRequested` | relevance mask `0x3F`, rendered mask `0x3F`, render frame advances |
| Point legacy | Instanced layered | Instanced layered | no fallback | masks `0x3F/0x3F`, render frame advances; actual layer writes uninspected |
| Point legacy | Geometry shader | Geometry shader | no fallback | masks `0x3F/0x3F`, render frame advances; actual layer writes uninspected |
| Point atlas | Sequential, instanced, or GS | Sequential | `AtlasUsesSequentialTiles` | 6 requests and 6 residents, but `LastRenderedFrame=0`, `NeverRendered`, `ActiveFallback=StaleTile` for every requested mode |
| Spot legacy | Sequential | Sequential | legacy 2D FBO | FBO and shadow camera exist; frustum reports relevant; actual depth uninspected |
| Spot atlas | Sequential | Sequential | single atlas tile | 1 request and 1 resident at 2048; rendered-frame diagnostic advances after light/camera motion |

The active atlas allocation requests used depth encoding. Allocation itself was healthy in each isolated case:

| Light | Classified requests | Resident | Allocation attempts | Failed candidates | Demotions |
|---|---:|---:|---:|---:|---:|
| Directional | 4 directional/depth | 4 | balanced/group-preserving solve | 0 | 0 |
| Point | 6 point/depth | 6 | 1 | 0 | 0 |
| Spot | 1 spot/depth | 1 | 1 | 0 | 0 |

The point failure is therefore downstream of allocation. In the isolated point-atlas interval, the lighting log reported `pointGroups=1/6/0` even though the component had settled on sequential atlas tiles. This is the runtime counterpart of the source-confirmed manager/light policy mismatch.

##### Actual queued draw route

The component-level truth table is not the actual draw truth table on CPU-direct. The flushed Vulkan log contains 109 `ShadowRenderPipeline` pipeline records, and every one has `directionalShadow=None;pointShadow=None` with an ordinary mesh shader such as `TexturedDeferred.fs`, `TexturedNormalDeferred.fs`, `TexturedSpecDeferred.fs`, `ColoredDeferred.fs`, or the normal alpha-forward shaders. The session contains no dedicated directional-cascade, point-depth, point-atlas-depth, or generic shadow-depth shader name.

The source review explains the exact handoff loss:

```text
shadow viewport installs GlobalMaterialOverride + layered matrices/count
  -> OnRenderRequested hashes the correct effective material in-scope
  -> VulkanMeshRenderRequest stores only the command-local MaterialOverride
  -> shadow/global/layered scopes pop
  -> frame loop later restores only pipeline + camera
  -> materialization resolves material and captures layered state from live cleared state
  -> PendingMeshDraw receives ordinary material, non-shadow state, zero layered targets
```

Relevant source points are `VkMeshRenderer.OnRenderRequested`, `VulkanMeshRenderRequest`, `VulkanFrameLoop.DrainQueuedMeshRenderRequests`, `VkMeshRenderer.TryMaterializeQueuedRenderRequest`, `LayeredShadowUniformState.CaptureFromCurrentRenderingState`, and the directional/point pop methods in `RenderingState`. The preparation compatibility signature can consequently describe the in-scope shadow material while the emitted operation contains a different ordinary material. Besides incorrect pixels, this split identity is a plausible contributor to unnecessary cold preparation or invalidation and should be measured after the route is fixed.

##### Validation and command reuse

The shutdown-flushed Vulkan log records 20 core validation errors in approximately 160 ms on the first fully lit atlas startup frame:

- 10 × `VUID-VkImageMemoryBarrier-oldLayout-01197`;
- 10 × `VUID-vkCmdDraw-None-09600`.

The duplicate-message limit was reached, so the absence of later copies is not proof that the problem stopped. The first barrier stack passes through dynamic-rendering FBO attachment transition, render-pass close, scheduled command-chain secondary execution, and mesh-draw payload recording. Submission errors include descriptors expecting present, color-attachment, and transfer-destination layouts while the tracked current layout differed. This needs a one-light/reuse-on-off isolation pass before attributing it specifically to shadow images, but Phase 0 disproves the broader assumption that the CPU-direct lit frame was validation-clean.

The live profiler repeatedly reported zero validation and descriptor-binding failures because those fields reflect the sampled/current frame rather than cumulative session history. Use the flushed validation log or add cumulative counters for future gates.

##### Motion and screenshot synchronization

An MCP camera/light mutation followed immediately by a screenshot is not frame-synchronized. The immediate point-atlas A/B images had identical pixel hashes (`1743ACD19D12942A`), and three immediate directional images had the same pixel hash (`1324A5DB15C0ECEA`), despite different requested poses. After waiting five to eight engine frames, the captures changed and showed the correct camera composition. Do not classify those immediate identical images as Vulkan shadow lag.

Representative settled visual observations:

- point legacy sequential was visually close to the lights-off control;
- point atlas showed a strong local light contribution even though all six atlas allocations remained `NeverRendered`; this separates light contribution from valid shadow production and is consistent with an unshadowed/contact/dummy fallback, but the sampled descriptor was not captured;
- directional atlas images changed with the settled camera pose and the manager rendered-frame diagnostic advanced;
- spot legacy and atlas images were dark from the chosen exterior view, so they are visually inconclusive.

The engine created `DummyShadowMapArray` during the session, while the live Vulkan fallback-sampled-image counter stayed at zero. That counter alone did not prove the dummy was not selected because an engine-owned dummy binding may not be classified as a descriptor fallback. Phase 2 later inspected the exact sequential receiver views and ruled out dummy substitution at those three draws; atlas routes remain unaudited.

##### Performance and resource-churn observations

Phase 0 was a route-toggle and cold-pipeline audit, not a benchmark. Route changes produced whole-frame samples ranging into approximately 45–97 ms, with primary recording frequently dominant. The final sampled frame was about 26.6 ms whole-frame with about 17.8 ms in Vulkan recording, but configuration switches and shader/pipeline warmup make these values unsuitable as lights-on/off comparisons.

Repeatedly switching routes grew Vulkan descriptor/pipeline state from approximately 27 variants, 9 pools, 140 sets, and 73 reservations (15,184 bytes) to approximately 2,627 variants, 50 pools, 21,290 sets, and 3,423 reservations (657,120 bytes). An unchanged 20-frame window was stable at 1,931 variants, 39 pools, and 14,450 sets. This is route-toggle retention/churn, not evidence of a per-camera-frame leak; isolate a single warmed route before connecting it to motion stutter.

##### Phase 0 instrumentation gaps

The following requested truth-table fields are not presently observable atomically:

| Required field | Phase 0 coverage | Gap |
|---|---|---|
| Requested and effective light mode/fallback | Available after a settle barrier | First reads can straddle an update/render transition |
| Manager entry kind and exact request/member range | Aggregate solver/group counts only | No immutable entry/member-range dump joined to the light plan |
| Selected per-caster material/shader kind | Session-wide Vulkan program evidence | No per-draw record joined to requested/effective light route |
| Caster count and target masks | Legacy component masks and limited primary-pass counts | No authoritative per-caster target mask; `PrimaryShadowCasterCount` excludes cascade/face collections |
| CPU-recorded, submitted, GPU-completed masks | Unavailable | Component completion means the CPU render call returned, not GPU completion |
| Storage/resource generation and readiness | Unavailable in the light diagnostics | Cannot join array recreation or texture readiness to a request |
| Requested/recorded/completed/published/sampled generation | Partial manager frames only | Manager, component, and profiler snapshots are non-atomic; observed frame subtraction can even produce age `-1` |
| Terminal state | Inferred from `NeverRendered`/fallback/skip reason | No authoritative `Rendered`/`Failed`/`DeferredBudget`/`StaleReused` record |
| Sampled image/view and dummy substitution | Unavailable | Live fallback counter is insufficient; descriptor inspection is required |
| Cumulative validation state | Shutdown log only | Live profiler validation count resets per frame |

`StandaloneShadowRenderRequestCount` and `StandaloneShadowRenderPassCount` are lifetime counters and did not map cleanly to per-cascade scheduling. Likewise, legacy point face masks remain at their last legacy values after atlas enablement. Do not use either as atlas write proof.

##### Phase 0 gate result

The component/capability route matrix is complete, but the original full truth-table contract is intentionally marked incomplete for draw, submission, GPU-completion, publication, and sampling milestones because the engine does not expose them atomically. Phase 0 identified three gates before a valid Phase 1 comparison:

1. preserve the resolved shadow material and immutable shadow state across the CPU-direct request queue;
2. stop point-atlas manager grouping from contradicting the effective sequential light route, or provide explicit reachable per-face containment;
3. isolate and remove the dynamic-rendering/secondary-command image-layout validation failures.

No fix was made in this phase.

### Phase 1: prove basic sequential writers

Capture each light's non-atlased sequential case in RenderDoc.

For every shadow target:

1. Find the depth pass event.
2. Confirm the attachment image, subresource, extent, format, clear value, viewport, scissor, depth compare, depth write, and bias.
3. Confirm at least one expected caster draw occurs.
4. Export the post-pass depth target and inspect it visually.
5. For point lights, inspect all six faces and verify physical face orientation.
6. For directional lights, inspect every cascade and verify that the split volumes overlap enough to avoid seams.
7. For spot lights, verify the cone view/projection and near/far range.

Decision ladder:

- no event: planning/scheduling problem;
- clear only: collection or dispatch problem;
- draws but unchanged/invalid depth: shader input, transform, layer, viewport, or depth-state problem;
- valid depth: continue to Phase 2.

#### Phase 1 execution record — 2026-08-14

Phase 1 used the already-built isolated editor binary under `Build/_AgentValidation/20260814-110450-vulkan-shadow-phase0/temp-build/phase1-editor/`. Each control ran desktop Vulkan with dynamic rendering, validation, `CpuDirect`, one shadowed light, and the corresponding global atlas toggle disabled. No engine source or test was changed. The temporary Unit Testing World light/probe settings were restored after capture, all launched editor processes were stopped, and all RenderDoc replay sessions were closed.

The installed `rdc` Python/replay module is RenderDoc 1.41 while the system capture launcher is 1.44. Mixing them attached with an empty API name but did not return triggered captures. The successful procedure used the matching local 1.41 `renderdoccmd.exe` under `%LOCALAPPDATA%/rdc/renderdoc/` with that version's Vulkan layer enabled. Keep capture and replay versions matched before interpreting a missing capture as an engine failure.

| Control | Writer and target evidence | Shader/state evidence | Phase 1 result |
|---|---|---|---|
| Directional sequential, non-atlas | Four cascade groups write D24 array `9733`, 1024x1024, one slice per pass. Physical slices 0–3 all export non-clear depth and the array is later a PS resource at EID 20882. | Representative EID 67: ordinary VS `866` with 13 user outputs; GS/PS absent; viewport/scissor 1024x1024; depth test/write enabled; compare enum `3`; cull enum `2`; bias `0`. | Distinct layer addressing and incidental raster depth are proven. Intended shadow material/state and cascade overlap are not; Phase 2 later traces the active receiver only as failure-localization evidence. |
| Point sequential, non-atlas, animated dirty control | Six logical face passes resolve both `R16_FLOAT` cube `12139` and D32 cube `12135` to physical slice 5. The first four passes clear that slice; the last two contain 66 and 2 draws. Slices 0–4 receive no captured clear/write. | Representative draws use ordinary VS `850` and ordinary four-output fragment programs; viewport/scissor 1024x1024; depth test/write enabled; compare enum `3`; cull enum `2`; bias `0`. The color target visibly contains brick albedo. | Definite physical-layer failure plus invalid sampled encoding. Face orientation cannot be evaluated until six distinct slices are bound. |
| Spot sequential, non-atlas | The named shadow FBO clears then runs 9 caster draws into `R16_FLOAT` `19264` plus D32 `19260`, both 2048x2048. The color resource is later a PS resource at deferred-light EID 302. | Representative draws use ordinary VS `775` and four-output fragment programs; viewport/scissor 2048x2048; depth test/write enabled; compare enum `3`; cull enum `2`; bias `0`. The sampled R16 target visibly contains the brick material. | Draws and attachment changes are proven, but the sampled resource has the wrong encoding. Phase 2 decodes near/far and comparison behavior, while a correctness verdict still waits for a valid writer. |

Directional layer exports are under `renderdoc/phase1/directional/`; the depth minima for physical slices 0–3 were approximately `0.844877`, `0.759961`, `0.676721`, and `0.610145`, with clear maximum `1.0`. The captures show the box becoming smaller in farther splits, but the sparse scene cannot establish overlap at transition boundaries.

The first settled static point capture, `point-sequential.rdc`, contains no late-frame writer event because the static shadow had already been cached. `point-sequential-animated.rdc` uses one moving point light to keep the non-atlas writer dirty and is the authoritative point producer capture. At its final writer event, color/depth slices 0–4 remain untouched zero-valued capture contents; only slice 5 changes. Source review confirms the logical face order is `+X, -X, +Y, -Y, +Z, -Z`, but the physical capture binds layer 5 for every pass.

At the end of Phase 1, the point and spot sampled color resources established producer-to-receiver resource continuity only. Phase 2 subsequently completed the exact descriptor/view, sampler, actual subresource layout, dummy-substitution, light payload, matrix/range, and comparison audit. Its pixel traces are used only to localize how invalid producer values fail; they are not a correct-shadow visual reference.

Every authoritative capture has two identical high-severity messages at EID 6 for `VUID-vkCmdPipelineBarrier-pBufferMemoryBarriers-02818` (`HOST_READ` destination access with `ALL_COMMANDS`). Treat this as a separate synchronization defect rather than evidence about a particular shadow map.

Evidence root: `Build/_AgentValidation/20260814-110450-vulkan-shadow-phase0/`

- directional: `renderdoc/phase1/directional/directional-sequential.rdc` plus four exported D24 layers;
- point static cache control: `renderdoc/phase1/point/point-sequential.rdc`;
- point dirty writer: `renderdoc/phase1/point/point-sequential-animated.rdc` plus exported color/depth slice 5 and per-slice statistics;
- spot: `renderdoc/phase1/spot/spot-sequential.rdc` plus clear/final R16 and D32 exports;
- copied engine logs: `logs/phase1/point-static/`, `logs/phase1/point-animated/`, and `logs/phase1/spot/`.

### Phase 2: prove receiver binding and sampling

Status: complete as failure localization; not a correct-lighting baseline.

At a lit receiver draw or deferred-light pass:

1. Verify the exact image, view type, subresource range, sampler, and image layout.
2. Verify sampling-readiness did not substitute a dummy texture or set `LightHasShadowMap=false`. Check point and spot explicitly; their binders currently omit the directional readiness gate.
3. Verify light index/record, world-to-shadow matrices, face/cascade selection, atlas page/layer, tile scale/bias, near/far encoding, comparison convention, and depth range.
4. Compare the bound resource ID and generation against the producer from Phase 1.
5. Inspect one known-lit and one known-shadowed pixel with pixel history/shader debugging where available.
6. For point atlas, record both *path requested* and *at least one sampleable face*. `PointShadowAtlasPathEnabled`/`LightHasShadowMap` can be true while the dummy array is bound.
7. Temporarily classify the result by observation: no light contribution, light without shadows, fully shadowed, wrong face/cascade, or stale but otherwise correct.

The prior dark screenshot does not identify which edge is broken. Do not blame batching or sampling until a valid sequential depth resource and its receiver binding are both inspected.

#### Phase 2 execution record - 2026-08-14

Phase 2 replayed the existing Phase 1 captures only. The engine/editor was not launched, settings were not changed, and no new frame was captured. Each capture was handled in an open-work-close RenderDoc session. Receiver light-accumulation targets and representative pixel traces were exported/viewed under `renderdoc/phase2/`; every replay session was closed afterward.

| Receiver | Exact binding and layout | Uniform/branch state | Representative pixel result | Temporary classification |
|---|---|---|---|---|
| Directional, EID 20882 | Set 2 binding 11, D24 `9733`, 2D-array view `9735`, mip 0, layers 0-3; all layers `DEPTH_STENCIL_READ_ONLY_OPTIMAL` | Legacy cascades enabled, atlas disabled, `LightHasShadowMap=1`, count 4, splits 20.09/60.07/120.04/200, four populated matrices | `(320,360)` selects layer 0: receiver `0.912811`, mostly `0.854-0.856` taps, factor `0.125`; `(300,350)` factor `0.0` | Mostly/fully shadowed by invalid incidental depth; receiver binding and cascade payload are active |
| Point, EID 1169 | Set 2 binding 4, R16 `12139`, cube view `12141`, mip 0, faces 0-5; faces 0-4 `TRANSFER_DST_OPTIMAL`, face 5 `SHADER_READ_ONLY_OPTIMAL` | Legacy cube enabled, atlas disabled, `LightHasShadowMap=1`, near `0.1`, radius/far `27.2845`; face chosen by cube direction | `(320,360)` selects `-X`, distance `17.7786`, reads zero for 4/4 taps, factor/output `0.0` | No light contribution / fully shadowed; wrong physical face production plus mixed-layout receiver view |
| Spot, EID 302 | Set 2 binding 4, R16 `19264`, 2D view `19266`, mip 0, layer 0; `SHADER_READ_ONLY_OPTIMAL` | Legacy 2D enabled, atlas disabled, `LightHasShadowMap=1`, near/far `1/40`, populated world-to-light matrices | `(320,360)`: receiver `0.922995`, biased comparison `0.921285`; 2/8 taps at clear `1.0` pass while brick-like `0.543-0.687` taps fail; factor `0.25` | Over-shadowed by wrong sampled encoding; receiver binding/transform are active |

All three sampled shadow bindings use point filtering and explicit shader comparisons; hardware comparison is disabled (`AlwaysTrue`). Directional/spot use clamp-edge on UV and point uses clamp-edge on all cube axes. The captured legacy paths use depth encoding `0`; directional and spot use the normal/non-reversed depth mode. This is consistent with the shader sources and rules out a sampler-comparison polarity error for these captures.

No dummy was bound at the audited draws: the bound resource IDs exactly match the Phase 1 producer IDs. This does not prove the same for atlas routes or for a later frame after resource recreation. Point atlas-specific step 6 is not applicable here because `PointShadowAtlasPathEnabled=0`; the authoritative point capture is intentionally non-atlased sequential.

The captures do not contain the engine's requested, recorded, completed, published, or storage-generation numbers. Resource-ID continuity is therefore capture-local only. Because the producers are invalid, the traced coordinates are representative geometry receivers rather than ground-truth known-lit/known-shadowed controls. A single frame also cannot establish camera-motion freshness or stutter. Repeat the receiver audit after the producer/transition fixes with generation diagnostics joined to the draw.

Phase 2 evidence:

- directional: `renderdoc/phase2/directional/snapshot-eid20882/` (`shader_ps.txt`, pipeline summary, and exported lighting target);
- point: `renderdoc/phase2/point/snapshot-eid1169/`;
- spot: `renderdoc/phase2/spot/snapshot-eid302/`;
- replay helpers: `scratch/phase2_introspect_bindings.py`, `scratch/phase2_enum_probe.py`, and `scratch/phase2_vulkan_image_probe.py`.

### Phase 3: confirm and contain the point-atlas scheduling defect

**Status:** complete as a read-only failure-localization and containment-design phase. Implementation and GPU draw/pixel validation remain pending.

Phase 3 used only the saved Phase 0 lighting log and current source. It did not start the engine/editor, change Unit Testing World settings, open or create a RenderDoc capture, run tests, or change product code.

#### Phase 3 execution record - 2026-08-14

| Case | Saved evidence | Interpretation |
|---|---|---|
| Isolated point atlas | Solver: `requests=6`, `pointGroups=1/6/0` (`log_lighting.log:8733`). Plan: `entries=6`, `members=6`, `requests=6` (`:8315`). Executor: `scheduled=0`, `checked=6`, `failed=6`, `deferred=6`, `firstDeferredIndex=0` (`:8316`). | Source reconstruction yields one six-member group plus five duplicate `Tile` entries. One grouped render call fails; `checked=6`/`failed=6` are its member-equivalent cost. Execution then labels the entire request tail budget-deferred and breaks before the duplicate entries. |
| Mixed directional/point/spot interval | Solver: `requests=11`, `pointGroups=1/4/2` (`:19`). Repeated plan: `entries=6`, `members=4`, `requests=11` (`:65`, `:106`, `:159`). Executor: `checked=4`, `failed=4`, `deferred=6`, `firstDeferredIndex=5` (`:160`). | One four-member point group fails, but the reported six-request tail is not the group. It can include skipped/nonmember requests, duplicate member tiles, and unrelated later work. Failure accounting and budget queue depth are therefore conflated. |

The immutable-plan reconstruction is:

1. `BuildPointFaceGroups` groups eligible resident point faces by light/domain/encoding/atlas/page whenever the global manager gate allows grouping. It does not consult the light's requested render mode or the capability result used by `PointLightComponent`.
2. Only the first member key is registered in `_pointFaceGroupIndexByFirstRequest`. The emitted group entry owns `RequestStartIndex == RequestEndIndex == seedIndex`.
3. The planner neither marks the other exact members as owned nor suppresses them. Each later member therefore becomes a normal `Tile` entry.
4. Ordinary Vulkan reaches `RenderGroupedShadowAtlasFaceTiles`, where `ShouldPrepareAtlasGroupedFaceCollection` rejects all Vulkan grouped work. `TryRenderPointFaceGroup` has no per-face fallback.
5. `RenderScheduledTiles` charges the failed group by face count, converts the raw request tail into `deferredByBudget`, records that false queue depth, and stops plan execution.

#### Containment contract for the later code change

1. Resolve the effective route once during planning and carry it in the immutable plan. A sequential route must emit explicit eligible face tiles; a grouped route may be built only from a matching per-light mode and verified backend capabilities.
2. Give every grouped member exact ownership. Map each member's `ShadowRequestKey` to its group, emit the group once, and suppress only later requests with keys owned by that emitted group. First/last indices may be retained for diagnostics, but must not control iteration unless contiguity is explicitly constructed and asserted.
3. Represent `UnsupportedCapability`, `RenderFailed`, `BudgetDeferred`, `TextureDeferred`, and `StaleReused` separately. A render failure must not inflate budget queue depth or stop unrelated plan entries.
4. If used to establish the baseline, make sequential fallback an explicit diagnostic route with a visible reason and per-member results. It must render exactly the failed group's members and must not coexist with duplicate precomputed tiles.
5. Prepare one immutable influence-volume/union caster packet for grouped rendering. Do not collect six independent viewport pipelines and then replay only viewport 0 into all indexed viewport slots.
6. Define atomic refresh behavior. `StaleTileReused` faces must either remain in the same group refresh or follow a deliberate partial-refresh policy; they must not silently split a supposedly atomic point update into mixed grouped and per-face work.
7. Add a plan/execution diagnostic that prints group id, route, exact member keys, compact slots, logical faces, page/layer, full and inner rectangles, duplicate ownership, and a terminal result per member.

#### Addressing audit boundary

The unreachable grouped writer is structurally coherent at the CPU/shader interface: members are sorted by logical face, assigned compact `ViewportScissorIndex` values, and used to pack the matrix, logical-face index, indexed viewport, scissor, and clear rectangle at the same compact slot. A group is restricted to one atlas page; `ShadowAtlasPageResource` attaches that page's array layer; `PointLightAtlasShadowDepth.gs` writes `gl_ViewportIndex = faceSlot` and indexes the matching packed matrix. The receiver independently selects the logical cube face and uses that face's published page plus UV scale/bias.

That review does **not** establish physical atlas correctness. Current Vulkan rejects the group before a draw, and no point-atlas RenderDoc capture exists. Phase 3 therefore could not inspect six actual clears/writes, compact-slot routing, face orientation, receiver transforms, or generation freshness. Also, only `InnerPixelRect` is currently cleared/rendered; the full `PixelRect` gutter remains unpopulated. After containment is implemented, validate every participating face on first render and dirty refresh, then enable grouped instanced/geometry execution only after all members record, submit, GPU-complete, and publish under one explicit atomic contract.

### Phase 4: validate immutable layered state

#### Phase 4 execution record - 2026-08-14

**Status:** source-contract audit complete; GPU equivalence intentionally pending.

This phase obeyed the no-engine constraint. It used read-only source review, rechecked `rdc doctor`, and inventoried existing captures. It did not start the editor/engine, run tests, open a replay session, create a capture, or change product code. The only saved captures are the four Phase 1 sequential files:

- `directional/directional-sequential.rdc`
- `point/point-sequential.rdc`
- `point/point-sequential-animated.rdc`
- `spot/spot-sequential.rdc`

There is no legacy instanced-layered, legacy geometry-shader, grouped-atlas, or per-caster fallback capture. Moreover, the existing sequential writers are invalid controls because all use the wrong material. The originally requested capture comparison cannot be completed honestly until the enqueue-time packet and sequential writers are repaired and a later engine-validation phase is authorized.

#### Enqueue-to-record ownership audit

| Stage | What is frozen now | What is missing or late | Consequence |
|---|---|---|---|
| Light render scope | Forced shadow material, generic/instanced/atlas flags, target count, matrices, point logical face indices, indexed viewport/scissor state | No stable packet id is assigned | Source state is coherent only while the scope is live |
| `XRMeshRenderer.GetVersion` | Instanced vertex-generator `BaseVersion` when compatible with mesh deformation | Per-caster material resolution is separate | The specialized renderer identity can survive enqueue; route loss is not universal |
| `VkMeshRenderer.OnRenderRequested` | Logical producer-target reference, extent, viewport/scissor arrays, fixed-function state, original instances, and a compatibility signature computed from the correct in-scope material | Native target subresource/generation, resolved material/reason, shadow state, selected material kind, expanded instances, view matrices/transform id, target mask, packet generation | Signature and future draw can describe different materials/state; mutable target/view owners can also advance |
| `VulkanFrameLoop.DrainQueuedMeshRenderRequests` | Request pipeline and its last camera are temporarily restored | Global shadow-material and layered scopes are not restored | Live re-resolution cannot reproduce the producer scope |
| `TryMaterializeQueuedRenderRequest` | A new immutable `PendingMeshDraw`, view snapshot, program-binding snapshot, and effective material as seen *now* | Material selection, instance expansion, shadow capture, transform id, and scoped revision are read after enqueue | The late snapshot faithfully freezes cleared or newer state |
| Auto-uniform publication | Full shadow-state hash contributes to pass content generation; indexed arrays are sealed | No exposed packet/frame/scoped-revision identity joins this generation to the producer | Refresh should work after correct capture, but diagnosis cannot correlate the route end-to-end |
| CPU-direct record | Selected draw material, draw count, view snapshot, descriptor slot/dynamic offsets | Cannot reconstruct missing enqueue-time state | Recording is not the first broken edge |

The most important invariant is that `PreparationCompatibilitySignature`, resource/program preparation, `PendingMeshDraw.MaterialOverride`, `PendingMeshDraw.Instances`, `ProgramBindingSnapshot`, and pass content generation must all derive from the same enqueue-time packet. Keeping only an early signature is insufficient.

#### Route-by-route source result

| Route | Addressing contract | Phase 4 source result | Later GPU proof required |
|---|---|---|---|
| Directional legacy instanced | Expanded instance slot selects packed matrix and `gl_Layer` | Shader math is coherent; current late resolution loses count/matrices and usually prevents instance expansion | Intended VS/FS, draw instance count = source instances x cascades, every layer written |
| Directional legacy GS | GS fans each triangle across packed matrices/layers | Deterministic zero-output path: Vulkan callback writes count zero and immutable restore excludes GS kinds | Nonzero count/matrices at a GS draw; compare every layer with sequential |
| Directional atlas instanced | Compact slot selects packed matrix and `gl_ViewportIndex` | CPU/shader slot ordering is coherent; grouped Vulkan execution is disabled | Matching matrix/viewport/scissor slot and valid writes in every allocated tile |
| Directional atlas GS | GS uses packed cascade slot as viewport index | Same ordering; restore excludes atlas GS and grouped Vulkan execution is disabled | Same as atlas instanced, including per-caster fallback draws |
| Point legacy instanced | Compact slot selects matrix; captured logical face selects `gl_Layer` | Sparse-face math is coherent; current late state loses face count/indices/matrices and expansion | Every selected logical face addressed once with correct orientation and radial depth |
| Point legacy GS | GS maps compact slot to logical face/layer | Restore excludes GS, publishes the wrong matrix alias for this shader, and omits the required mask | Both aliases or standardized name, exact derived mask, nonzero output per selected face |
| Point atlas instanced | Compact slot selects packed matrix and viewport | CPU packing is coherent; grouped Vulkan route is rejected before draw | Matrix/rect slot equality, all member tiles written, receiver logical-face mapping intact |
| Point atlas GS | GS fans by compact viewport slot; logical face remains receiver metadata | CPU packing is coherent; restore excludes atlas GS and route is rejected before draw | Same member and receiver proof as atlas instanced |
| Instanced pass with per-caster geometry fallback | Geometry shader should fan the original caster using the same immutable target packet | Matrices are present in the state struct but restore is gated by selected instanced kind; directional gets count zero and point misses alias/mask | One opaque, one alpha/cutout, one deformed, and one multiply-instanced caster with requested/scoped/selected route recorded |

The generated instanced vertex shader followed by a geometry fallback does not show a source-level double transform: the geometry shaders recompute clip position from the forwarded world-space `FragPos`, not from `gl_in[].gl_Position`. It remains a pipeline-interface case that needs an actual draw inspection, especially because both stages can declare layer/viewport built-ins.

#### Required immutable packet before implementation

Use two ownership levels before `VulkanMeshRenderRequest` enters the queue:

- one frame-owned/shared pass snapshot per unique shadow scope/generation for view state, route flags, matrices, indices/mask, target/subresource, and indexed viewport/scissor state;
- one small per-caster request envelope for resolved material/reason, original/expanded instances, per-caster target mask, model transforms, and a reference/handle to that pass snapshot.

Do not embed or recopy the fourteen-matrix layered struct plus view matrices into every caster request, and do not allocate a new heap object per draw. Reuse a bounded frame arena/pool or intern by explicit pass generation and producer identity. The combined logical packet must contain:

- render frame id, pipeline/pass identity, scoped-binding revision, and a monotonic or content-derived shadow-packet id;
- enqueue-time camera/view/projection payload, render area, transform id, and their generation (or an explicitly frame-owned immutable view snapshot);
- requested light mode, effective light route, generic layered flag, instanced flag, atlas-grouped flag, and selected `BaseVersion`/vertex route;
- complete `ResolvedMeshRenderMaterial`: material, shadow uniform/binding source, material kind, resolver reason, and compatibility/fallback reason;
- original instance count and the already-resolved expanded draw count;
- target count, directional matrices, or point matrices plus compact-to-logical face indices and the exact derived face mask;
- per-caster target relevance mask (or explicit all-target sentinel) and the mapping used to recover source-instance identity after expansion;
- producer target/subresource identity plus indexed viewport/scissor count and content hash; the existing immutable arrays can remain in `VulkanMeshProducerSnapshot` if the packet joins them by identity;
- a stable matrix/index/mask hash exposed to diagnostics and auto-uniform publication.

The packet should be consumed, not re-resolved, by preparation signature generation, materialization, instance expansion, program-binding capture, uniform restore, command reuse identity, and diagnostics. Component callbacks may still populate light encoding constants, but matrices/counts/indices/masks and route-critical values must come from the packet. Its lifetime must extend through command-plan sealing/recording and any retained reusable-command cohort; either promote it once into generation-owned sealed storage or retain/release it explicitly. A reusable command must never reference an arena slot after that slot is reclaimed.

#### Exact later capture gates

Use identical casters, camera, light, encoding, resolution, and fixed-function state for sequential, instanced, and geometry captures. At each representative draw record the packet id/hash, requested/scoped/selected route, material and shader stages, original/expanded instance counts, target count, every matrix, logical index, physical layer or viewport slot, target mask, descriptor set/slot, and dynamic offset.

Binary acceptance checks:

- the material identity used in `PreparationCompatibilitySignature` equals the material prepared and recorded for the draw;
- directional legacy GS has nonzero `CascadeLayerCount` equal to the captured matrix count;
- an instanced directional/point pass whose caster resolves to geometry receives the same packet/matrix hash and target set as its instanced peers;
- point legacy geometry receives `ViewProjectionMatrices[]`, face indices, and a nonzero exact `PointShadowFaceMask`; atlas and instanced variants receive `PointShadowViewProjectionMatrices[]`;
- full six-face point cases write each physical cube layer exactly once; sparse cases write only the captured logical faces without confusing compact slot with physical face;
- every atlas `gl_ViewportIndex` matches the packed matrix and indexed viewport/scissor slot, and no member writes outside its inner/full border contract;
- sequential, instanced, and geometry resources match within the chosen depth/moment precision for known caster samples, including cascade overlap and point-face seams;
- moving the camera/light changes packet/matrix generation and the bound pass-frequency payload in the same recorded shadow generation;
- sequential draws use the enqueue-time shadow-camera view/projection generation rather than a later `LastRenderingCamera` value;
- no variant reads route-critical data from live component state after enqueue, and no Uber fallback bypasses deformation/instance/alpha compatibility.

#### Phase 4 gate result

The source localization gate is complete. The first implementation change should freeze the resolved material plus the complete shadow packet in `OnRenderRequested`, calculate expanded instances from that packet, and widen immutable uniform application to both instanced and geometry material kinds. Point legacy geometry additionally requires its matrix alias and derived mask. Do not enable grouped Vulkan atlas rendering as part of that first repair: sequential correctness, legacy layered equivalence, per-caster fallback, and validation-clean descriptor/attachment transitions remain prerequisite gates.

### Phase 5: isolate atlas freshness from whole-frame lag

#### Phase 5 execution record - 2026-08-14

Phase 5 used read-only source review plus the saved Phase 0 lighting log and screenshots. It did not start the engine/editor, run tests, replay or create a capture, change settings, or modify product code. Because the saved screenshots are not synchronized to a logical render/present generation and no atlas `.rdc` exists, this phase could not perform a trustworthy A/B/C pixel comparison. It instead audited every CPU-visible freshness transition and identified which later fields are required to separate shadow lag from whole-frame lag.

#### Freshness timeline by light type

| Milestone | Point/spot atlas | Directional atlas | Result |
|---|---|---|---|
| Plan and metadata publish | `PublishFrameData` runs before tile rendering | Same manager order | The published snapshot initially describes the pre-render state |
| Render accepted on CPU | `MarkTileRendered` queues a completion record | Tile/group path also commits cascade slots immediately | The completion record is not submission or GPU completion |
| Manager reconciliation | Next `BeginFrame` drains the ring, then a later publish exposes `LastRenderedFrame`/content | Same ring, but component slots already changed | Saved records reconciled after one to three render-frame ids, mostly two |
| Deferred receiver invalidation | Layout generation only; point/spot content-only readiness is absent from `LightBindingState` | Cascade revisions/content/stale ages are included | Point/spot can legally reuse a stale dummy/disabled binding after valid metadata exists |
| Physical sample readiness | Object/page existence check only | Explicit renderer texture-readiness check | Point/spot can expose recreated arrays earlier than directional |
| GPU ordering proof | No submission serial/fence/timeline in the completion record | Same | Neither path proves physical write completion from manager state alone |

The saved log contained 48 `[DirectionalShadowAudit][TileCompletionLatency]` samples: four with `latencyFrames=1`, 40 with `latencyFrames=2`, and four with `latencyFrames=3`. This proves that ring reconciliation was not same-frame in that session. It does not measure presented shadow age and does not imply the directional receiver waited for ring reconciliation, because directional slots are committed immediately.

#### Receiver-generation defect

`ShadowAtlasManager.HasLayoutChanged` compares allocation count/key, page, kind, pixel rectangle, resolution, and atlas id. It does not compare `LastRenderedFrame`, `ContentVersion`, fallback/skip state, texture object, or storage generation. `PublishFrameData` increments its generation only when that layout comparison changes. The deferred `LightBindingState` includes `PublishedFrameData.Generation`, but for point/spot it does not include the allocation content state, published frame id, per-face sampleability, texture identity, or storage generation. Vulkan typed-binding signatures and persistent artifacts consume the publisher/resource generation, so a content-only dummy-to-real transition at a stable rectangle can be missed.

This is more severe than the intentional plan-before-render delay: the next planning cycle may contain the correct allocation snapshot while the receiver binding generation remains unchanged. The repair should expose separate monotonic `LayoutGeneration`, `ContentPublicationGeneration`, and `StorageGeneration` values, then include only the relevant per-light/per-face shader-visible content generation and image/view identity in the receiver key. Using the global frame id alone would invalidate all light bindings every frame and would hide the actual ownership contract.

#### Ruled-out and still-unproven branches

- Page-object selection is not a newly found mismatch. Every `ShadowAtlasPageResource` wraps the same array texture and selects `descriptor.PageIndex` as the framebuffer layer; point fetching page zero obtains the array object intentionally, while per-face metadata selects its tile/page.
- A generic CPU-direct mapped-memory race remains disfavored: direct draws refresh current engine/auto-uniform data before descriptor binding, the mapped image slot is completion-gated, and the prior auto-uniform frequency correction is still present.
- The saved immediate/settled screenshot pairs cannot measure present latency. Repeated immediate hashes followed by a changed image after five to eight engine frames establish capture asynchrony, not a shadow-specific frame count.
- Directional immediate slot publication does not prove its write is sample-ready. Queue order and exact image barriers must be capture-validated after the existing layout errors are fixed.
- Atlas array growth remains independently unsafe because recreated storage is neither copied nor included in resident content identity.

#### Phase 5 gate result

The source freshness gate is complete. The first atlas-publication repair must distinguish CPU-recorded, submitted, GPU-completed/ordered, metadata-published, binding-published, and sampled generations. Point/spot content availability and storage identity must invalidate their receiver publications; directional and local lights must then use one documented same-submission or bounded-latency policy. Whole-frame/present lag remains unproven and should not be used as the first diagnosis.

When runtime validation is allowed again, capture three adjacent logical states: settled `A`, one deterministic move `B`, and held `C`. For each, join request/packet id, dirty reason, terminal state, CPU-recorded/submitted/GPU-completed mask, submission timeline, layout/content/storage generations, published and sampled generation, image/view/descriptor slot, matrix hash, camera frame-data generation, present depth, and fence wait. Only that joined record can distinguish an atlas-generation delay from a whole-frame delay.

### Phase 6: profile shadow work amplification

#### Phase 6 execution record - 2026-08-14

Phase 6 used source-level work accounting plus saved, already-produced Phase 0 profiler and throttled lighting/Vulkan logs. It did not run the engine/editor, tests, a new benchmark, or RenderDoc. GPU pipeline profiling was disabled in the saved session. All durations below are therefore CPU wall-clock diagnostics from a cold route-toggle investigation, not GPU timings or a controlled warmed distribution.

#### Structural work model

Let `T` be the target count (directional cascade count or selected point faces), `C_t` the casters collected for target `t`, and `C_union` their conservative union.

| Route | Visibility collection/swap | CPU shadow draw/submission work | GPU target expansion |
|---|---:|---:|---:|
| Spot legacy/atlas | 1 | approximately `C_0` in one pass/tile | approximately `C_0` |
| Directional/point sequential | `T` | approximately `sum(C_t)` across `T` passes | approximately `sum(C_t)` |
| Directional/point layered | 1 union | approximately `C_union` in one pass | currently approximately `T * C_union` because there is no per-caster target mask |
| Vulkan directional atlas today | 1 union collection in the grouped setup, then sequential member execution | approximately `sum(C_t)`/`T` tile submissions in the fallback | per-tile rasterization |
| Vulkan point atlas today | route-dependent collection plus a rejected group | no useful grouped draw; duplicate plan tail is unreachable after failure | none for the failed group |

Point and directional `collectVisibleNow=true` tile methods contain a conditional amplification trap: each per-tile call invokes whole-light collection/swap, and whole-light sequential collection itself loops `T` targets. If the manager calls that tile method `T` times, collection/swap can approach `T^2`. The normal frame path observed in Phase 0 separates collection and calls rendering with `false`, so this is a capture/forced-current-path risk rather than proof of the steady desktop cost.

#### Saved timing and allocation evidence

| Evidence | Saved observation | Interpretation limit |
|---|---|---|
| Atlas render summaries | 1,294 throttled samples spanning 0.01-50.60 ms | Mixed configurations and cold transitions; no percentile claim |
| Isolated failed point group | 139 samples spanning 0.03-0.19 ms | Retry/accounting correctness bug, but cheap executor in these samples |
| Slow solve diagnostics | 13 thresholded samples spanning 2.01-72.42 ms | Threshold-biased; 72.42 ms included startup creation of three pages |
| Slow directional group/fallback | 18.37 ms and 48.65 ms | Atomic CPU path can dominate one frame; only two thresholded samples |
| Budget overrun | 50.60 ms total with a 0.50 ms configured budget | Budget is not a hard cap; first/critical entry can exceed it |
| Route-switch frame timing | roughly 45-97 ms whole-frame; last sample about 26.6 ms whole-frame/17.8 ms Vulkan recording | Cold route-switch audit, not steady-state p95/p99 |
| Descriptor/pipeline state | about 27 to 2,627 variants, nine to 50 pools, 140 to 21,290 sets, and 73/15,184 bytes to 3,423/657,120 bytes reservations | Strong transition churn/retention evidence; configuration boundaries lack precise timestamps |
| Unchanged 20-frame window | stable at 1,931 variants, 39 pools, and 14,450 sets | Disfavors an unconditional every-frame resource leak in that window |
| Descriptor invalidations | 128 `vkUpdateDescriptorSets` records, each with two dependent command buffers | Amplification during the audit; no steady-motion attribution |

The lights-off observation of about 4.4 ms average whole-frame and about 185 Hz came from a different sampled interval and is retained only as orientation. It is not directly comparable to the cold lit route switches. No stable p50/p95/p99 or GPU shadow duration can be derived from the saved evidence.

#### Per-draw CPU-direct churn

The 160-byte `$CpuDirectDynamicData` record contains two matrices and eight `uint` values. In the main descriptor-binding route, it is captured once before descriptor preparation, once through `UpdateEngineUniformBuffersForDraw` with mask `1`, and once afterward with the pass-aware mask. `TryWriteIfChanged` may skip the memory copy when bytes match, but disposing its write scope still marks the range dirty; when the pass mask differs, the middle and final calls can also toggle/copy the record. No runtime shader or descriptor consumer was found. This should be removed/gated or deliberately wired to a consumer before optimizing lower-level flush behavior. It is CPU overhead only and is not a shadow correctness cause.

#### Instrumentation still required for the controlled benchmark

- broad-phase collections, per-target relevance tests, resolved caster packets, per-target masks, and shadow draws;
- grouped entries accepted, rejected, failed, deferred, retried, and sequentially contained, with exact member keys and duplicate count;
- requested/CPU-recorded/submitted/GPU-completed/layout/content-publication/storage/binding-published/sampled generations and stale age;
- atlas array recreations, copied/invalidated resident layers, storage generation, readiness transitions, and completion-ring overflow/high-water marks;
- command-chain record/reuse/invalidation counts with concrete resource reasons, not only coarse planner revisions;
- descriptor refresh count/time, frame-data slot/generation, and persistent binding artifact generation;
- `$CpuDirectDynamicData` capture attempts, changed copies, dirty bytes, coalesced noncoherent flush bytes, and whether a consumer exists;
- per-light/per-target collect, swap, material-resolution, CPU draw-record, Vulkan encode, submit, completion-wait, GPU shadow, and present-queue timings;
- explicit `CriticalBypass` and first-entry-over-budget counters, plus the cost of the atomic entry that exceeded the limit.

#### Phase 6 gate result

The structural amplification audit is complete. It confirms why directional/point sequential fallback scales poorly, why current layered rendering saves CPU submissions without yet pruning GPU target work, why the atlas time budget can still admit a tens-of-milliseconds directional spike, and why CPU-direct has redundant per-draw mapped-data churn. It does **not** assign a warmed p95/p99 share to any mechanism. That benchmark remains intentionally pending until the common shadow packet, sequential writer/layout correctness, atlas receiver-generation contract, and minimal counters are repaired and engine execution is authorized.

The later controlled comparison remains: lights off; one nonshadowed light; one sequential spot; directional with one and configured cascades; point sequential/instanced/GS; all atlas types; static versus deterministic motion; primary reuse enabled/disabled; and one other mesh-submission route. Use engine profiler CPU timing plus GPU pipeline timestamps; use RenderDoc for correctness/state, not timing.

## RenderDoc workflow

Tool readiness was verified with `rdc doctor` on 2026-08-14. Store captures under the active `Build/_AgentValidation/<run>/renderdoc/` directory.

```powershell
rdc open <capture.rdc>
rdc info --json
rdc passes
rdc draws --limit 100
rdc bindings <shadow-depth-eid> --json
rdc rt <shadow-depth-eid> -o <shadow-depth.png>
rdc bindings <light-combine-eid> --json
rdc rt <light-combine-eid> -o <light-combine.png>
rdc close
```

For array/cube/atlas resources, enumerate and export every relevant layer, face, mip, and atlas page rather than inspecting only the default view. Use the GUI or the appropriate `rdc texture` options when `rdc rt` does not expose the required subresource.

Do not use a single frame to diagnose lag. Capture A/B/C states or use adjacent captures with a deterministic one-step camera move.

## Instrumentation contract for a later code change

A unified `ShadowPassAuditRecord` should be emitted only when shadow auditing is enabled. One record should join the entire route with stable identifiers:

```text
frame / light / request / render-plan / pass-packet
requested kind / manager entry kind / effective light kind / selected BaseVersion / selected caster material+kind / resolver+fallback reason
request start+end / member start+count / duplicate member count
atlas page+pixel rect+inner rect+storage generation or legacy image+subresource
original+expanded instances / pass target mask / per-caster target mask / compact-to-logical indices / CPU-recorded mask / submitted mask / GPU-completed mask / matrix hash / caster packet count / draw count
request generation / CPU-recorded generation / submitted generation / GPU-completed generation / layout generation / content-publication generation / storage generation / binding-published generation / sampled generation
descriptor set+slot / dynamic offset / image view / sampler / layout / texture-ready state / dummy-substitution state
CPU collect+record time / submit serial / GPU timeline / stale age / terminal state
```

Terminal state must distinguish `Rendered`, `Unsupported`, `Failed`, `DeferredBudget`, `DeferredDependency`, and `StaleReused`. A failed first render must never be mislabeled as an ordinary stale reuse or budget event. Record completion-ring overflow separately; the current ring drops a completion record when full, which can leave point/spot metadata stale even if commands were recorded.

## Acceptance criteria

### Correctness

- Sequential, non-atlased directional, point, and spot shadows each produce visibly correct current lighting.
- All point cube faces and all configured directional cascades contain expected caster depth with correct orientation and overlap.
- Instanced-layered and geometry-shader legacy paths match sequential output within depth precision.
- The enqueue-time compatibility signature, prepared material/program, recorded material, expanded instance count, shadow packet id, and matrix hash agree for every shadow draw.
- Per-caster geometry fallback inside an instanced directional or point pass preserves the same target count, matrices, logical indices, derived point mask, and output; no route-critical uniform comes from live state after enqueue.
- Legacy point geometry receives its required matrix alias and exact nonzero face mask; sparse-face cases address only their captured logical layers.
- Layered caster expansion respects an explicit per-caster target mask or a documented all-target sentinel without losing cascade overlap or point-face seam coverage.
- Directional, point, and spot atlases match their non-atlased controls, including gutters, page/layer selection, and tile transforms.
- Point and directional final paths use grouped layered rendering without silent sequential fallback.
- A point group owns each face exactly once; successful groups are not followed by duplicate tile draws, and failed groups do not stop unrelated requests or masquerade as budget deferral.
- A point light requesting sequential atlas rendering stays sequential; every requested/effective mode transition has an explicit capability or fallback reason.
- Atlas array growth either preserves every valid old layer or invalidates and redraws all affected residents before publication.
- All three atlas binders apply the same texture/view readiness contract, and a requested point path is reported separately from the presence of a sampleable face.
- A point/spot allocation changing from unpublished/dummy to sampleable at the same rectangle advances the exact receiver binding generation and binds the real current image/view; layout, content-publication, and storage generations are independently observable.
- Atlas filters never read untouched gutters or neighboring tiles; depth and moment encodings clamp to valid texel centers for their configured kernels.
- Camera/light/caster motion produces no unconfigured stale frame; any intentionally allowed latency is bounded, observable, and reported.
- No Vulkan validation errors, descriptor-binding failures, dummy-shadow substitutions, or uninitialized atlas sampling occur.

### Performance

- A settled frame performs no unexpected shadow recollection, atlas rewrite, or broad command-chain re-record.
- Camera motion invalidates only shadow data whose concrete contents changed.
- Directional work scales with collected casters plus target-mask expansion, not N complete CPU submissions.
- Point work uses one collected/batched stream for six faces, not six independent full submissions.
- Failed or deferred atlas entries do not hot-loop, consume unbounded budget, or advance unrelated resource revisions.
- Atlas budget telemetry distinguishes ordinary admission, first-entry overrun, and `CriticalBypass`; no configured hard-cap claim is made while one atomic entry can exceed it.
- Atlas array capacity growth does not cause unbounded resource recreation or force unrelated page redraws without an explicit accounting reason.
- The steady CPU-direct path performs no unconsumed `$CpuDirectDynamicData` writes; any retained diagnostic payload is gated, counted, and justified by a consumer.
- CPU-direct p95/p99 remain bounded relative to lights-off and nonshadowed-light controls; thresholds must be set from the later warmed controlled benchmark because the saved Phase 0 route-switch samples are not a baseline.

## Recommended implementation order after approval

1. Before/at `OnRenderRequested`, freeze one frame-owned shared pass snapshot plus a small per-caster envelope containing the resolved material/reason, selected route/material kind, complete layered/atlas state, enqueue-time view/transform data, matrices/indices/derived face mask, original and expanded instance counts, native producer-subresource identity, target mask, and joinable packet generation. Build the compatibility signature and every later draw/program publication from that same logical packet; never re-resolve from cleared live state or a mutable last-camera/target owner, and do not copy the large matrix payload or allocate per draw.
2. Isolate and fix the dynamic-rendering/secondary-command image-layout failures, comparing primary reuse enabled and disabled, before treating later screenshots or captures as synchronization-clean.
3. Make sequential, non-atlased write and sampling correct for directional, point, and spot.
4. Split CPU-recorded, submitted, GPU-completed/ordered, metadata-published, binding-published, and sampled atlas milestones; align point/spot readiness gates with directional and choose an explicit same-submission or bounded-latency publication contract.
5. Split atlas layout, content-publication, and storage generations. Include exact point/spot sampleability plus texture/view identity in deferred receiver binding generation. On texture-array growth, copy valid layers or invalidate/redraw every affected resident before publication.
6. Fix point manager route selection, exact group-member ownership, duplicate tile entries, failure terminal state, grouped caster-packet ownership, and explicit per-face containment.
7. Unify immutable layered inputs across instanced and geometry variants, including per-caster geometry fallback, both point matrix-name aliases until shaders are standardized, the exact derived point face mask, and a per-caster target relevance contract. Remove or constrain the Uber fallback that bypasses deformation/instance/alpha compatibility.
8. Validate legacy layered arrays/cubes against sequential output.
9. Define and implement the atlas border contract: full allocation clear where required, edge dilation or sufficient guard data, and texel-center-safe sampling for every encoding/filter.
10. Restore grouped directional and point atlas rendering with isolated indexed viewport/scissor state and per-target caster masks.
11. Remove or gate unused CPU-direct dynamic capture, replace coarse invalidation with concrete resource identity, and make first-entry/critical atlas budget overruns explicit before using the setting as a pacing control.
12. After the user explicitly clears test work, add a backend/path acceptance matrix and regression coverage.

## Investigation evidence

Controlled session:

- session: `vk-shadow-analysis-20260814`
- session root: `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260814-101347-vk-shadow-analysis-20260814/`
- scratch root: `Build/_AgentValidation/20260814-101316-vulkan-shadow-analysis/`
- forced mesh strategy: `CpuDirect`
- lights-off profiler observation: approximately 4.4 ms whole-frame average and approximately 185 Hz during the sampled interval; no descriptor-binding or validation failures were reported by the profiler snapshot
- point atlas: requested `InstancedLayered`, effective light mode `Sequential`, fallback `AtlasUsesSequentialTiles`; six resident manager allocations reported `LastRenderedFrame=0`, `NeverRendered`, and stale fallback. The component face mask/frame were also zero, but the second review established that those component fields are not updated by atlas rendering and are not atlas evidence.
- point non-atlas control: `Variance2` plus requested/effective `Sequential`; face mask `63`, nonzero component render frame, and a legacy framebuffer were present. This establishes six issued/marked CPU face calls only.
- final point images remained effectively dark in both cases, but the scene/light/exposure and actual depth targets were not controlled closely enough to attribute the result to shadow sampling
- `rdc doctor` passed; no `.rdc` frame was captured during this analysis
- the isolated editor session was stopped normally; its dedicated `logs/` directory was empty, and only launcher stdout was present

Second-pass source review:

- no engine process, test, profiler, or RenderDoc capture was run
- confirmed the point exact-member ownership/duplicate-entry defect and failure-as-budget accounting
- confirmed publish-before-render ordering for point/spot metadata and that the completion ring has no GPU fence/timeline token
- confirmed texture-array growth recreates storage without copying pixels or invalidating resident content
- confirmed point/spot sampling-readiness checks differ from directional in both deferred and forward paths
- confirmed per-caster directional geometry fallback can bypass immutable cascade restoration
- confirmed the point geometry/instanced shader matrix-name mismatch and the atlas gutter/texel-edge risk

Phase 0 route audit:

- session: `vk-shadow-phase0-20260814`
- session root: `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260814-110535-vk-shadow-phase0-20260814/`
- evidence root: `Build/_AgentValidation/20260814-110450-vulkan-shadow-phase0/`
- forced mesh strategy: `CpuDirect`; Vulkan dynamic rendering and primary reuse enabled; validation layers enabled
- completed the settled requested/effective matrix for directional sequential/instanced/GS legacy and atlas routes, point sequential/instanced/GS legacy and atlas routes, spot legacy/atlas, and lights-off controls
- confirmed the device advertises every layered and viewport-index capability needed by the legacy grouped routes; the observed fallbacks are policy/state failures rather than hardware capability failures
- found 109 ordinary non-shadow shader records under `ShadowRenderPipeline` and no dedicated shadow-depth shader record; source tracing confirmed deferred materialization loses the scoped global shadow material and layered snapshot
- confirmed isolated point atlas allocation succeeds for all six faces while rendered publication remains at frame zero and the manager still builds one six-face group against the light's sequential effective route
- found 20 core Vulkan layout/descriptor validation reports before duplicate suppression; copied `log_vulkan.log`, `log_lighting.log`, `log_rendering.log`, and `vulkan-descriptor-invalidations.log` into the evidence root
- captured 15 screenshots and established that immediate MCP screenshots are not frame-synchronized; only settled captures were used for visual comparisons
- `rdc doctor` passed; Phase 0 intentionally did not create a RenderDoc capture
- no engine code or test change was made; the isolated session was stopped and temporary Unit Testing World settings were restored

Phase 1 sequential-writer captures:

- evidence root: `Build/_AgentValidation/20260814-110450-vulkan-shadow-phase0/`
- captured authoritative non-atlas writers for directional, animated point, and spot lights with Vulkan dynamic rendering and `CpuDirect`
- confirmed representative draws for all three use ordinary scene shader/state instead of the light's forced shadow material
- confirmed directional writes four distinct D24 array layers and later exposes the same array as a PS resource, while cascade overlap remains unproven
- confirmed every sequential point pass binds cube slice 5; slices 0–4 receive no clear/write in the captured frame
- confirmed point and spot sampled R16 color resources contain visible brick material output rather than the required depth/moment encoding
- confirmed every capture repeats `VUID-vkCmdPipelineBarrier-pBufferMemoryBarriers-02818` for a host-read destination access/stage mismatch
- viewed the exported directional layers and representative point/spot clear, color, and depth PNGs; did not infer correctness from export success alone
- no engine code or tests were changed; matching RenderDoc capture/replay versions were used; all editor/replay processes were stopped and temporary settings were restored

Phase 2 receiver audit:

- replayed only the existing Phase 1 captures; no engine/editor process or new capture was started
- proved directional, point, and spot deferred draws bind the exact Phase 1 producer resource rather than a dummy and set `LightHasShadowMap=true`
- decoded directional cascade count/splits/matrices, point near/radius and cube-direction selection, and spot world-to-light matrices/range
- confirmed all three use ordinary point-filtered samplers plus manual shader comparison rather than hardware depth comparison
- found point cube faces 0-4 still in `TRANSFER_DST_OPTIMAL` while the six-face receiver view is sampled; a real `-X` pixel reads zero and becomes fully shadowed
- traced directional pixels to factors `0.125` and `0.0`, and a spot pixel to factor `0.25`; these are failure-localization evidence only because Phase 1 proved the producer contents invalid
- viewed the exported directional, point, and spot lighting-accumulation targets and closed every RenderDoc session
- no engine code or tests were changed

Phase 3 point-atlas plan audit:

- used only current source and the saved Phase 0 lighting log; no engine/editor, test, settings change, RenderDoc replay/capture, or product-code change occurred
- reconstructed the isolated `requests=6`, `entries=6`, `members=6` plan as one six-member group plus five duplicate tile entries
- established that `checked=6` and `failed=6` represent the cost of one failed grouped call, while `deferred=6` is an incorrectly classified raw request tail
- confirmed the mixed four-member grouped failure reports six deferred requests and can overcount nonmembers, skipped work, duplicates, and unrelated later requests
- corrected the proposed containment algorithm: priority sorting permits interleaving, so group ownership must suppress exact member keys instead of skipping a first-to-last request range
- found that the latent grouped success path collects/swaps separate per-face viewport pipelines but renders only viewport 0; it needs one immutable influence-volume/union caster packet
- found that point geometry and atlas-geometry material kinds bypass the immutable layered-state restore gate in addition to the existing matrix-name split
- audited compact slot/matrix/viewport/page wiring as structurally consistent, but did not validate physical atlas writes or sampling because Vulkan rejects the grouped call before any draw and no point-atlas capture exists

Phase 4 immutable layered-state audit:

- used read-only source review and capture inventory only; `rdc doctor` passed, but no engine/editor, test, settings change, replay session, new capture, or product-code change occurred
- confirmed `XRMeshRenderer.GetVersion` freezes the specialized instanced `BaseVersion`, while `OnRenderRequested` computes the correct in-scope material signature but queues neither that resolved material nor layered state nor expanded instances
- confirmed materialization later re-resolves the material and captures layered state after the scopes have ended, so a warm signature for one material can accompany preparation/recording of another
- confirmed request drain restores a mutable last-camera reference rather than an enqueue-time view payload; sequential shadow-camera matrices and transform id remain a separate motion-sensitive ownership gap
- confirmed `PendingMeshDraw`, sealed indexed viewport/scissor arrays, program-binding snapshot, and pass-frequency shadow-state hashing form a usable immutable downstream path once supplied with the correct enqueue-time packet
- expanded the packet requirements to include generic/instanced/atlas flags, material kind/reason, original and expanded instances, matrices, compact/logical indices, derived face mask, per-caster target relevance, producer-state identity, and a joinable packet generation
- confirmed directional geometry restore is omitted; legacy point geometry additionally needs the `ViewProjectionMatrices[]` alias and an exact derived `PointShadowFaceMask`
- confirmed reviewed legacy/atlas compact-slot addressing is source-consistent, but no GPU equivalence claim is possible because the capture inventory contains only four invalid sequential controls
- found no current per-caster cascade/face relevance mask, so grouped union casters are conservatively expanded to every selected target
- flagged the Uber fallback as a conditional route violation because it can reselect a shared instanced material after deformation/instance/opaque compatibility was rejected
- found the live layered scopes are not stack-correct: nested pushes overwrite the outer payload and inner pop does not restore it; no current nested caller was established
- specified a shared frame-owned pass snapshot plus small per-caster envelope so the enqueue fix does not copy fourteen-plus matrices or allocate once per draw

Phase 5 atlas-freshness audit:

- used read-only source review plus the saved Phase 0 lighting log/screenshots only; no engine/editor, test, replay, new capture, settings, or product-code change occurred
- confirmed planning/publication precedes tile rendering, while `ShadowTileCompletion` is queued at CPU render-call return and contains no Vulkan submission serial, fence, or timeline
- counted 48 saved directional completion-latency records: four at one render-frame id, 40 at two, and four at three; this is manager reconciliation latency, not GPU completion or presented shadow age
- confirmed directional commits component cascade slots immediately, while point/spot wait for manager reconciliation and a later publication; the light types do not share one logical freshness contract
- found that atlas generation is layout-only and deferred point/spot `LightBindingState` omits content/sampleability, published frame, image identity, and storage generation, allowing a dummy-to-real content transition without receiver-binding invalidation
- confirmed point/spot readiness checks are weaker than directional and deferred point returns requested-path state even when no face is sampleable
- ruled out a separate page-zero lookup defect in the reviewed shared-array/page-layer wiring
- retained atlas array recreation as an independent content-identity failure and left physical GPU write-to-sample ordering unproven because no valid atlas capture exists
- rejected the saved immediate screenshots as a lag measurement because capture delivery was not synchronized; whole-frame/present lag remains unproven

Phase 6 work-amplification audit:

- used source accounting plus already-saved Phase 0 profiler, lighting, rendering, and descriptor-invalidation logs; no engine/editor, test, capture, benchmark, settings, or product-code change occurred
- established the target-count work model: sequential performs per-target collection/submission, layered performs one union CPU stream but currently fans every union caster to every target, and spot remains single-target
- found a conditional `collectVisibleNow=true` per-tile path that can approach `T^2` collection/swap work; the observed normal frame used the separated `false` path, so this is not assigned as the steady-run cause
- confirmed `MaxRenderMilliseconds` is non-preemptive for the first entry and bypassable for camera-critical directional work; saved CPU diagnostics included 18.37/48.65 ms directional entries and a 50.60 ms total at a 0.50 ms configured budget
- found the main CPU-direct descriptor path attempts the unused 160-byte `$CpuDirectDynamicData` record three times per draw and marks the mapped range dirty even when the comparison skips a copy; no runtime consumer was found
- separated cold route-transition descriptor/pipeline growth from steady behavior: the audit grew to roughly 2,627 variants/21,290 sets, while one unchanged 20-frame window was stable
- noted that the isolated failed point-group executor was only 0.03-0.19 ms in its throttled samples; it is a correctness/retry defect but does not explain the largest saved recording spike by itself
- did not produce warmed p50/p95/p99 or GPU timings; GPU pipeline profiling was disabled and the saved route-switch session is not a benchmark

Relevant source areas:

- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.ShadowAtlasEncodingState.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasRenderPlan.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasFrameData.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasPageResource.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/PointLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/SpotLightComponent.cs`
- `XREngine.Runtime.Rendering/Rendering/MeshRenderMaterialResolver.cs`
- `XREngine.Runtime.Rendering/Rendering/ResolvedMeshRenderMaterial.cs`
- `XREngine.Runtime.Rendering/Rendering/LayeredShadowUniformState.cs`
- `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs`
- `XREngine.Runtime.Rendering/Rendering/Shaders/Generator/DefaultVertexShaderGenerator.cs`
- `XREngine.Runtime.Rendering/Shaders/ShadowCasterVariantFactory.cs`
- `XREngine.Runtime.Rendering/Objects/RenderTargets/XRFrameBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Objects/Render Targets/XRCubeFrameBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Buffers.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorFingerprints.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.ProgramBindingArtifacts.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanCpuDirectDynamicData.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanMeshRenderRequest.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanMeshProducerSnapshot.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanMeshDrawViewSnapshot.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanAutoUniformPublicationSnapshot.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/PendingMeshDraw.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanMappedFrameArena.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanAutoUniformBindingSchema.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Synchronization/VulkanRenderer.BarrierEmission.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Packetization.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs`
- `Build/CommonAssets/Shaders/DirectionalCascadeShadowDepth.gs`
- `Build/CommonAssets/Shaders/DirectionalCascadeAtlasShadowDepth.gs`
- `Build/CommonAssets/Shaders/PointLightShadowDepth.gs`
- `Build/CommonAssets/Shaders/PointLightAtlasShadowDepth.gs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingPoint.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingSpot.fs`
- `Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl`

## Current stopping point

Analysis phases 0-6 are complete. Phases 0-2 established that all three sequential writers lose the shadow material, point additionally aliases six logical faces to physical slice 5, the exact producer resources reach active receiver branches, and the point cube is sampled with five faces still in transfer-destination layout. Phases 3-4 localized the duplicate/rejected point-atlas plan, false failure-as-budget accounting, immutable compatibility-signature/draw-payload split, missing geometry restore/point alias/mask, late camera ownership, and absent per-caster target mask. Phase 5 added the point/spot content-generation invalidation defect and separated manager reconciliation from GPU/present readiness. Phase 6 confirmed target-count work amplification, a non-preemptive critical directional atlas budget, and unused per-draw CPU-direct dynamic-data churn. No implementation has begun.

The first shared implementation gate is capture-confirmed and source-complete: CPU-direct enqueue must carry one already-resolved shadow packet and use it for the compatibility signature, material/program preparation, expanded instance count, uniform restore, pass generation, and recording. Geometry kinds must consume the same state; legacy point GS also needs its matrix alias and exact derived face mask. The point sequential route additionally must freeze each face's native attachment identity before later `_perFaceFbo` mutation and make the complete cube view shader-readable before sampling. The point-atlas scheduler must resolve one effective route, own/suppress exact group member keys, preserve unrelated interleaved requests, distinguish failure from budget deferral, and collect one immutable grouped caster packet.

The first atlas-freshness gate is also source-complete: expose separate layout, content-publication, and storage generations; carry point/spot sampleability and actual image/view identity into deferred binding publication; distinguish CPU recording, submission, GPU ordering/completion, metadata publication, binding publication, and sampling; and align all three light types on one explicit latency/readiness policy. The current completion ring cannot supply GPU readiness, and the saved screenshots cannot measure whole-frame lag. Core Vulkan layout/synchronization failures must be resolved before same-submission atlas safety is accepted.

The remaining Phase 6 work is runtime validation, not further source diagnosis: after the correctness contracts and minimal counters exist and engine runs are authorized, perform the warmed route matrix and establish p50/p95/p99/worst-frame CPU and GPU baselines. Do not treat the saved cold route-switch numbers as thresholds, and do not treat Phase 2 as a correct-lighting verdict. After the producer and descriptor-transition repairs, repeat the sequential writer/receiver captures and require intended shader/state, distinct point faces, correct encoding, directional overlap, spot range, a shader-readable complete view, matching producer/receiver generations, and known-lit/known-shadowed pixels before enabling or comparing layered and atlas routes.

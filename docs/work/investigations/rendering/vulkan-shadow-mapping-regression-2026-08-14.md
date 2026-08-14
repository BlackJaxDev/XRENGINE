# Vulkan shadow mapping regression: layered rendering, atlases, and CPU-direct pacing

**Date:** 2026-08-14
**Status:** Active investigation; no engine fixes have been made
**Scope:** Directional, point, and spot shadows on ordinary desktop Vulkan, especially the CPU-direct mesh-submission path
**Related tracker:** [Directional light inspector shadow investigation](directional-light-inspector-shadow-2026-08-03.md)

**Second-pass review:** Source-only code review completed on 2026-08-14. The engine, tests, and RenderDoc were not run for this revision.

**Phase 0 execution:** Route-matrix runtime audit completed on 2026-08-14 with desktop Vulkan, `CpuDirect`, dynamic rendering, validation layers, and one-light settled controls. No RenderDoc capture or engine code change was made.

## Problem statement

The Vulkan render loop is now reasonably stable with lights disabled, but shadowed lights regress correctness and frame pacing:

- directional cascades and point-light cube faces do not reliably render through the requested instanced-layered or geometry-shader paths;
- the directional, point, and spot atlas paths can fail, trail camera motion, or stutter;
- even basic sequential, non-atlased shadow maps are not yet known to produce correct final lighting;
- enabling shadowed lights substantially increases CPU-direct work at high frame rates.

This is not one failure. The second source pass found independent defects in route selection, point-group plan construction, failure accounting, atlas storage lifetime, publication timing, and layered uniform ownership. It also corrected two conclusions that the first analysis stated too strongly.

## Corrections to the first analysis

1. `PointLightComponent.LastRenderedShadowFaceMask` and `LastShadowRenderFrame` are legacy-only diagnostics. They are updated by non-atlased layered and sequential cube rendering, but not by `RenderShadowAtlasFaceTile` or `RenderGroupedShadowAtlasFaceTiles`. Their atlas values of `0` therefore did **not** prove that no atlas faces were written. The stronger atlas evidence is the manager allocation diagnostic (`LastRenderedFrame=0`, `NeverRendered`, stale fallback) combined with the source-confirmed failed grouped route.
2. A legacy point face mask of `0x3F` proves that all six CPU render calls were issued and marked, not that those faces contain valid GPU depth or are sampled correctly.
3. The dark final screenshots do not isolate shadow sampling. Exposure, light placement, the light-volume pass, and clear-only shadow output were not independently controlled. Treat the common sequential producer/sampler failure as unresolved until a future depth-target and receiver-binding capture establishes the first broken edge.
4. The atlas `ShadowTileCompletion` queue is not a GPU-completion mechanism. Entries are created when the component render call returns on the CPU; they carry no Vulkan fence or timeline value. The word *completion* in the current code means recorded/accepted by the shadow path, not proven GPU-complete and sample-ready.

## Executive diagnosis

### Phase 0 runtime additions

1. **CPU-direct deferred materialization drops the shadow pass's effective material and immutable layered state.**

   This is now the leading common failure for basic sequential and layered shadow draws. `XRViewport.Render` installs the forced shadow material as `RenderingState.GlobalMaterialOverride`, and the light installs cascade/face matrices in a scoped layered state. `VkMeshRenderer.OnRenderRequested` computes its preparation signature while those scopes are active, but `VulkanMeshRenderRequest` stores only the mesh command's local `MaterialOverride`. It does not store the resolved shadow material or a `LayeredShadowUniformState` snapshot.

   `VulkanFrameLoop.DrainQueuedMeshRenderRequests` runs later and restores only the pipeline and rendering camera. `TryMaterializeQueuedRenderRequest` then calls `ResolveMaterial` and `LayeredShadowUniformState.CaptureFromCurrentRenderingState` after `PushMainAttributes` and the directional/point layered scopes have been popped. The resulting `PendingMeshDraw` can therefore use the ordinary scene material, `IsShadowPass=false`, zero target count, and no captured matrices even though the request signature was computed from the correct in-scope shadow material.

   The runtime log matches this source path: all 109 graphics-pipeline records attributed to `ShadowRenderPipeline` used ordinary scene shaders and reported `directionalShadow=None;pointShadow=None`. There were zero occurrences of the dedicated directional, point, atlas-point, or generic shadow-depth shader names. This proves route identity was lost before the prepared draw; it does not by itself prove whether an ordinary vertex/fragment program happened to write usable depth in a sequential target.

2. **The isolated point-atlas case allocates all six faces but never publishes a rendered face.**

   With only the point light active, the solver reported six classified point/depth requests, six resident allocations, no allocation failure, and no demotion. The component's atlas diagnostic remained `LastRenderedFrame=0`, `NeverRendered`, and `ActiveFallback=StaleTile` for every requested mode, including after moving both the light and camera. The lighting log also reported `pointGroups=1/6/0` while the light's effective route was sequential with `AtlasUsesSequentialTiles`, confirming at runtime that manager grouping ignored the light route.

3. **Core Vulkan validation is not clean with the lit atlas configuration.**

   The shutdown-flushed Vulkan log contains ten `VUID-VkImageMemoryBarrier-oldLayout-01197` reports and ten `VUID-vkCmdDraw-None-09600` reports between 11:08:00.627 and 11:08:00.787, after which the validation duplicate limit suppressed further copies. The first barrier stack runs through `TransitionFboAttachmentsForDynamicRendering`, `EndActiveRenderPass`, and `TryExecuteScheduledMeshCommandChainSecondaryRun`; submission errors show descriptors expecting present, color-attachment, or transfer layouts while the images were in different layouts. These failures occurred with primary reuse enabled and all three atlas lights initially active. They are not yet isolated to a particular shadow resource, but they invalidate any assumption that attachment and descriptor layouts are generally correct.

   The live profiler's current-frame validation count stayed at zero because it is not a cumulative session audit. Synchronization validation was disabled, so Phase 0 established only the core-layout failures.

### Confirmed defects

1. **The Vulkan point-atlas planner and light disagree, and the manager ignores the light's requested mode.**

   `ShadowAtlasManager.ShouldBuildGroupedAtlasRenderPlanEntries` enables grouped atlas entries on ordinary Vulkan. `PointLightComponent.ShouldPrepareAtlasGroupedFaceCollection` rejects grouped point-face preparation on every Vulkan backend. `ShadowAtlasManager.TryRenderPointFaceGroup` then returns failure without the per-face fallback that exists for directional cascades.

   `BuildPointFaceGroups` also does not consult `PointLightComponent.ShadowRenderMode` or the light's grouped-route capability. A point light explicitly requesting `Sequential` can therefore still be converted into a grouped manager entry and fail on Vulkan. The controlled run's manager diagnostic reported six resident allocations with `LastRenderedFrame=0`, `LastDirtyReason=NeverRendered`, `LastSkipReason=StaleTileReused`, and `ActiveFallback=StaleTile`.

2. **The point grouped render-plan range is malformed.**

   The directional group entry records the last member request and advances the request loop across the group. The point group entry instead sets both `RequestStartIndex` and `RequestEndIndex` to the seed index and does not advance the loop. The remaining point faces are emitted again as ordinary `Tile` entries in the same immutable plan.

   This has two bad outcomes:

   - when the grouped Vulkan entry fails, execution breaks before those later tile entries, so they are not a fallback;
   - if grouped execution succeeds on another backend, the precomputed later tile entries can render group members a second time, adding redundant collection, recording, clears, and writes.

3. **A grouped render failure is mislabeled and charged as budget deferral.**

   `RenderScheduledTiles` logs `RenderFailed`, but for any failed non-tile entry it then sets `deferredByBudget`, records the request tail through `RenderWorkBudgetCoordinator.RecordShadowAtlasQueue`, and breaks. This obscures the real terminal state and stops the remaining request tail. Because dirty requests are normally ordered directional, spot, then point, a failed point group commonly starves remaining faces and later point lights; pinning, priority, and clean-request ordering can change the exact tail.

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

8. **Point and spot atlas texture readiness checks are weaker than directional checks.**

   Both deferred and forward binders require `IsTextureReadyForShadowSampling` for the directional atlas. Their point and spot atlas paths only require that the texture object/page exists. This can expose newly created or recreated Vulkan arrays before the renderer reports them sample-ready. In deferred point lighting, `TryBindPointAtlasShadow` also returns *requested* rather than *has a sampleable face*, enabling the atlas shader path while binding the dummy array. That fallback may intentionally produce lit/contact-only behavior, but `LightHasShadowMap` and `PointShadowAtlasPathEnabled` are not proof that any real face is available.

9. **The point layered shader contract has two matrix names.**

   Legacy `PointLightShadowDepth.gs` consumes `ViewProjectionMatrices[6]`; the atlas geometry shader and generated instanced vertex path consume `PointShadowViewProjectionMatrices[6]`. `PointLightComponent.SetShadowMapUniforms` currently uploads both aliases from live scoped state, but `MeshRenderMaterialResolver.SetPointLightLayeredUniforms` restores only the latter immutable alias. A unified packet/fallback implementation must standardize the name or publish both, otherwise the legacy geometry path will lose its matrices when live callbacks are removed.

### High-confidence source risks

10. **Dirty point faces do not consistently stay grouped.**

   `CanDirectionalCascadeJoinGroup` accepts both normal and `StaleTileReused` allocations; `CanPointFaceJoinGroup` accepts only `SkipReason.None`. Local projection/camera-fit changes are forced fresh, but other dirty reasons can be marked stale-reused. Those point faces then fall out of the grouped path and become individual tile entries. Even after Vulkan grouping is enabled, dynamic caster/content invalidation can silently return point shadows to per-face work.

11. **Atlas gutters are allocated but not populated, and UVs address tile edges rather than texel centers.**

   Atlas passes render and clear only `InnerPixelRect`; no reviewed path clears the full `PixelRect`, dilates edge texels into the gutter, or copies a border. `XRENGINE_ShadowAtlasUvFromLocal` maps clamped local UV `[0,1]` directly through scale/bias based on the inner integer rectangle. UV `1` lands on the boundary after the final inner texel. Nearest sampling at a boundary and linear/moment filtering can therefore read an untouched gutter or neighboring tile. PCF offsets are clamped back to the same edge, so the gutter currently does not serve its intended filtering purpose.

### Unresolved and disfavored causes

12. **Basic sequential depth contents and receiver sampling remain uncaptured, but CPU-direct already has a confirmed producer-input defect.**

   The legacy point control issued all six face calls and had a framebuffer, while its final viewport was visually close to the lights-off control. Phase 0 still did not inspect depth subresources or receiver descriptors, so image readiness/layout, valid depth contents, descriptor selection, and shadow comparison remain open. However, basic sequential directional, point, and spot passes all use a scoped global shadow material, and CPU-direct deferred materialization loses that scoped override. They no longer qualify as source-clean controls. An ordinary material may incidentally write depth in a sequential target, but it does not establish the intended shadow-caster shader, alpha/depth encoding, or layered-addressing contract.

13. **A generic CPU-direct mapped-memory race is disfavored.**

   CPU-direct updates auto/engine uniform buffers immediately before descriptor binding and uses completion-gated mapped arenas. The historical auto-uniform frequency bug is also still fixed: struct snapshots inherit `block.Frequency` and publication generation is selected by that frequency.

   Atlas policy and the publish-before-render ordering can nevertheless bind a logically old generation while memory synchronization remains correct. Reopen the mapped-memory theory only if a future capture proves that the camera/light payload bound for a draw is itself stale.

14. **CPU work amplification remains a likely stutter source.**

   Current leading contributors are:

   - sequential cascade/face visibility collection and command recording after grouped rendering is rejected;
   - duplicate point-face tile entries after a grouped plan entry;
   - full or union caster replay into multiple targets;
   - coarse shadow resource-plan changes invalidating large command-chain cohorts even when concrete buffer identity is unchanged;
   - repeated unused 160-byte `$CpuDirectDynamicData` mapped-arena writes and dirty marking around a direct draw;
   - failed point-atlas groups charged as deferred queue depth and retried;
   - texture-array recreation when atlas page capacity grows.

   These explain CPU time and cadence. The unused dynamic record cannot explain incorrect pixels because no shader/binding consumer was found.

## Evidence and confidence ledger

| Finding | Confidence | What is still required |
|---|---:|---|
| CPU-direct queues only the local material override, then resolves the effective material and layered state after their shadow scopes are gone | Confirmed in source and matched by all 109 logged `ShadowRenderPipeline` program records | Capture a fixed draw packet after implementation to prove the selected material, target count, and matrices survive queueing |
| `ShadowRenderPipeline` prepared ordinary scene programs with both shadow kinds `None`; no dedicated shadow-depth shader appeared in the session log | Confirmed for this Phase 0 session | Attribute the selected material per draw/case and inspect its depth output in Phase 1 |
| Lit atlas startup emitted core image-layout and descriptor-layout validation errors through dynamic-rendering and scheduled-secondary code | Confirmed in the flushed session log; not isolated to one light/resource | Reproduce one light at a time with cumulative validation capture, then repeat with primary reuse disabled |
| Point grouped atlas entry is planned despite the requested mode, rejected by the Vulkan light, and lacks reachable per-face fallback | Confirmed in source; manager live diagnostic was consistent | A future capture can show the missing writes, but is not needed to establish the control-flow defect |
| Point group range leaves later member requests as duplicate tile entries | Confirmed in source | Unit/runtime validation only after implementation is functionally validated and test work is explicitly cleared |
| Failed grouped work is accounted as budget-deferred and stops the request tail | Confirmed in source | Add terminal-state counters before performance conclusions |
| Directional legacy GS can see `CascadeLayerCount=0`, including per-caster fallback from an instanced pass | Confirmed conditional source defect | Inspect an active geometry material draw and its immutable packet |
| Directional grouped atlas is disabled and falls back sequentially | Confirmed in source and live route | Verify each fallback tile contains current valid depth |
| Atlas array growth discards old layer pixels without invalidating resident content | Confirmed conditional source defect | Later exercise a capacity-growth case and inspect every old layer |
| Point/spot publication occurs before render and reconciliation occurs at the next begin/publish cycle | Confirmed in source | Measure actual visible stale age and decide whether same-frame publication is required |
| Completion-ring overflow can drop point/spot state and its counter can be reset before frame-data publication observes it | Confirmed conditional source/diagnostic defect | Split request-queue and completion-ring counters and retain cumulative/high-water values |
| Point/spot binders omit the directional atlas readiness gate | Confirmed in source | Verify Vulkan readiness transitions and dummy substitution after resource recreation |
| Point sequential non-atlas issued all six face render calls | Confirmed in live state | Inspect all six depth faces and the receiver binding; do not treat the mask as GPU proof |
| Spot shadow producer is broken | Unproven | Isolate atlas and non-atlas spot passes in RenderDoc |
| A generic CPU-direct UBO race causes shadow lag | Disfavored | Reopen only if a capture shows the bound camera/light matrices themselves are stale |
| Atlas gutter/texel-edge handling can sample outside valid inner content | High-confidence source risk | Inspect boundary samples for depth and moment encodings after the core route works |
| Shadow work/revision churn causes stutter | Likely | Measure current collection, recording, invalidation, wait, and draw counts |

## Current route matrix

"Producer status" means whether the depth-writing route appears structurally viable. It does not certify receiver sampling.

| Light | Storage | Sequential | Instanced layered | Geometry shader | Current assessment |
|---|---|---|---|---|---|
| Directional | Legacy array/non-atlas | Component selects sequential | Component selects instanced | Component selects GS; zero-count hazard also exists | CPU-direct queued draws lose the scoped shadow material/state in all three cases; component route labels are not draw proof |
| Directional | Atlas | Active Vulkan fallback | Falls back sequentially | Falls back sequentially | Four allocations render according to manager diagnostics, but CPU-direct draw material/state and actual depth remain unproven; grouped target architecture is unavailable |
| Point | Legacy cube/non-atlas | Six face calls were issued and marked | Component selects instanced | Component selects GS | CPU-direct queued draws lose the scoped shadow material/state; masks prove calls, not valid depth or layers |
| Point | Atlas | Light says sequential, manager still groups 1/6, then no face renders | Same manager mismatch | Same manager mismatch | Six allocations stay `NeverRendered`/stale; no reachable per-face containment after grouped failure |
| Spot | Legacy 2D/non-atlas | Component has an FBO/camera | Not applicable | Not applicable | CPU-direct loses the scoped forced shadow material; actual depth was not captured |
| Spot | Atlas | Single-tile manager diagnostic advances | Not applicable | Not applicable | Allocation/publication advances, but CPU-direct material loss plus readiness/publication/storage risks keep actual depth and sampling unproven |

Directional lights render a configurable cascade count, not six directions. Point lights render six cube faces. Both need one collected/batched stream that targets multiple layers or atlas viewports without N independent full submissions.

## Causal chains to validate

### Point atlas

```text
ordinary Vulkan allows grouped atlas plan
  -> manager creates one point-face group without consulting requested mode/capability
  -> point plan range covers only the seed request; remaining faces also become Tile entries
  -> PointLightComponent rejects Vulkan grouped preparation/rendering
  -> TryRenderPointFaceGroup has no sequential fallback
  -> executor labels the failed request tail as budget-deferred and breaks before Tile entries
  -> allocations remain NeverRendered and later point work is starved
  -> stale/uninitialized atlas content is exposed or shadows are suppressed
```

The latent success path is also wrong:

```text
grouped point render succeeds
  -> all group-member completion records are enqueued
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

### Point/spot atlas publication lag

```text
BeginFrame drains prior CPU-side completion records
  -> requests are solved
  -> PublishedFrameData is frozen
  -> tile render calls are recorded afterward
  -> CPU-side completion records are queued without a GPU timeline token
  -> current point/spot receiver still sees the pre-render publication
  -> next BeginFrame reconciles the record
  -> next PublishFrameData can expose it
```

This ordering guarantees a logical publication-cycle delay for a newly rendered point or spot tile. It does not by itself prove an unsafe GPU read/write overlap; same-queue ordering and barriers must be reviewed separately. Instrument CPU-recorded, submitted, GPU-completed, published, and sampled milestones as different values.

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
  -> sequential collection/recording occurs per cascade
  -> shadow plan/cohort revisions amplify primary-chain recording
  -> a generation is incomplete, deferred, or superseded
  -> directional slot publication may retain G-1
  -> current camera can sample an older shadow generation
```

This last chain is plausible, not yet proven. It must be confirmed with generation and CPU-timing evidence.

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

## Desired architectural invariants

Use these as design constraints once implementation begins:

1. **Sequential non-atlas is the authoritative correctness baseline.** Each directional cascade, point face, and spot map must independently produce and sample correct current depth before enabling batching.
2. **One immutable shadow-pass packet owns all shader inputs.** It must carry matrices, target count, physical face/cascade mapping, layer or viewport mapping, atlas rect transforms, and generation. Instanced and geometry variants consume the same snapshot contract.
3. **Capability selection is authoritative and end-to-end.** Per-light requested mode, device capability, planner, collector, executor, selected per-caster material, shader variant, and fallback must agree on one route. “Unsupported,” “failed,” “deferred,” and “stale reused” are distinct states.
4. **A grouped render-plan entry owns its exact request range.** Every member appears once, success cannot leave duplicate tile entries, and failure either executes an explicit fallback for that same range or leaves a `Failed` terminal state without pretending it was a budget event.
5. **Grouped visibility is collected once with a per-target mask.** Perform one conservative union broad phase, compute a cascade/face bitmask per caster, resolve material/batch state once, and expand only to selected targets. Preserve overlap at cascade transitions and point-face seams.
6. **Atlas publication has an explicit GPU-ordering and readiness contract.** Distinguish CPU-recorded, submitted, GPU-completed, metadata-published, and receiver-sampled milestones. Same-submission sampling may publish before fence completion only when pass order and an image barrier guarantee write-before-read; cross-frame reuse must use completion-gated storage. Never label CPU recording as GPU completion. Record the intentional maximum stale age; do not silently reuse forever.
7. **Atlas storage generation is part of content identity.** Growing or recreating an array must either copy every valid old layer or invalidate and redraw every resident allocation before it is sampleable.
8. **Tile filtering has an explicit border contract.** Render/clear and populate the required gutter, and clamp sample coordinates to valid texel centers for the selected kernel and encoding. A whole-layer clamp mode is not tile isolation.
9. **Resource identity drives command reuse.** Do not invalidate broad command cohorts merely because a coarse planner revision changed if the concrete buffers, descriptors, and immutable payload are unchanged. Conversely, a replaced texture array must change identity even when page indices and rectangles do not.
10. **No per-draw diagnostic writes in the steady hot path unless consumed.** Diagnostic capture should be gated and measured.

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

The engine created `DummyShadowMapArray` during the session, while the live Vulkan fallback-sampled-image counter stayed at zero. That counter does not prove the dummy was not selected because an engine-owned dummy binding may not be classified as a descriptor fallback. Phase 2 must inspect the exact bound image view.

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

### Phase 2: prove receiver binding and sampling

At a lit receiver draw or deferred-light pass:

1. Verify the exact image, view type, subresource range, sampler, and image layout.
2. Verify sampling-readiness did not substitute a dummy texture or set `LightHasShadowMap=false`. Check point and spot explicitly; their binders currently omit the directional readiness gate.
3. Verify light index/record, world-to-shadow matrices, face/cascade selection, atlas page/layer, tile scale/bias, near/far encoding, comparison convention, and depth range.
4. Compare the bound resource ID and generation against the producer from Phase 1.
5. Inspect one known-lit and one known-shadowed pixel with pixel history/shader debugging where available.
6. For point atlas, record both *path requested* and *at least one sampleable face*. `PointShadowAtlasPathEnabled`/`LightHasShadowMap` can be true while the dummy array is bound.
7. Temporarily classify the result by observation: no light contribution, light without shadows, fully shadowed, wrong face/cascade, or stale but otherwise correct.

The prior dark screenshot does not identify which edge is broken. Do not blame batching or sampling until a valid sequential depth resource and its receiver binding are both inspected.

### Phase 3: confirm and contain the point-atlas scheduling defect

1. Inspect the immutable point plan before execution. The group entry must cover the first through last group request, the planner loop must advance over all members, and no member may also appear as a `Tile` entry.
2. Make manager grouping conditional on the same per-light requested mode and device capability contract used by `PointLightComponent`. A requested sequential point atlas case must remain six explicit tile entries.
3. Keep grouped failure, unsupported capability, budget deferral, and stale reuse as separate terminal states. A render failure must not increment budget-deferred queue depth or stop unrelated request work.
4. In a later implementation iteration, add an explicit diagnostic-only ungrouped/per-face fallback to establish atlas-addressing correctness. That is containment, not final architecture.
5. Validate all six atlas inner rectangles, full allocation rectangles, page/layer indices, compact viewport slots, physical face indices, clears, and sampling transforms.
6. Validate both first-render and dirty-refresh cases. `StaleTileReused` faces must either remain part of an atomic group refresh or follow an explicit, measured policy rather than silently becoming individual work.
7. Only then enable grouped instanced/GS execution and require all requested targets to record, submit, complete, and publish atomically.

### Phase 4: validate immutable layered state

For directional and point, compare sequential, instanced-layered, and geometry-shader captures with identical casters.

Record at each draw:

- requested light kind, active scoped kind, and selected per-caster shader/material kind;
- target count;
- all view-projection matrices;
- logical cascade/face index;
- physical array layer or atlas viewport slot;
- per-caster target relevance mask;
- packet/generation identifier and dynamic descriptor offset.

Specific binary checks:

- directional legacy GS: `CascadeLayerCount` must be nonzero and match the matrix count;
- an instanced directional pass whose caster resolves to a geometry material must still receive the same immutable matrices/count;
- point legacy: six physical layers must be addressed exactly once when all faces are relevant;
- legacy point geometry must receive `ViewProjectionMatrices`; atlas GS/instanced variants must receive `PointShadowViewProjectionMatrices`, or all variants must be standardized on one name;
- sparse point atlas masks: compact viewport slot and physical face index must not be confused;
- atlas variants: `gl_ViewportIndex` must match packed matrix/rect slot ordering;
- no shader variant may depend on live component callbacks after the render packet was captured.

### Phase 5: isolate atlas freshness from whole-frame lag

Capture three adjacent logical states:

- **A:** static, fully settled;
- **B:** camera/light/caster moved once;
- **C:** held still after the move.

For each, record:

- request generation and dirty reason;
- planned request/member ranges and terminal state;
- scheduled, CPU-recorded, submitted, and GPU-completed target masks;
- submission serial and GPU completion frame/timeline value;
- published generation and stale age;
- sampled generation, image/view ID, descriptor set/slot, and matrix hash;
- atlas storage generation and texture-ready state;
- camera frame-data slot/generation;
- present queue depth and fence wait.

Interpretation:

- CPU records `G`, receiver samples `G-1` because publish occurred first: current point/spot ordering; decide whether to move publication or explicitly allow one frame;
- GPU has completed `G` but receiver still samples `G-1`: metadata publication defect;
- metadata publishes `G` before its write is GPU-ordered/sample-ready: unsafe completion/readiness defect;
- no write due to explicit budget defer, receiver samples `G-1`: policy/budget issue;
- shadow is current but the whole image trails: CPU/GPU/present pacing;
- camera matrix itself is stale at the receiver: reopen CPU-direct frame-data selection.

### Phase 6: profile shadow work amplification

Use the engine profiler for timing; RenderDoc timing is perturbative. Compare warmed p50/p95/p99 and worst frame for:

1. lights off;
2. one nonshadowed light;
3. one sequential spot;
4. one directional light with one cascade, then the configured cascade count;
5. one point light sequential, instanced, and GS;
6. each atlas type;
7. static versus camera/light motion;
8. CPU-direct with primary reuse enabled and disabled;
9. another existing mesh-submission route as a control.

Add or expose these counters in a future instrumentation change:

- broad-phase collections, per-target relevance tests, resolved caster packets, and shadow draws;
- grouped entries accepted, rejected, failed, deferred, retried, and sequentially contained;
- grouped entry request/member ranges, duplicate-member count, and remaining-tail count after failure;
- atlas requested/CPU-recorded/submitted/GPU-completed/published/sample generations and stale age;
- atlas array recreations, copied/invalidated resident layers, storage generation, readiness transitions, and completion-queue overflows;
- command-chain record/reuse/invalidation counts with concrete invalidation reasons;
- descriptor refresh count/time and frame-data slot/generation;
- `$CpuDirectDynamicData` capture count, bytes dirtied, and noncoherent flush bytes;
- CPU shadow collection/record time, Vulkan command encoding time, completion waits, GPU shadow time, and present queue depth.

If cost scales with draw count while plan identity is stable, investigate per-draw uniform/diagnostic churn. If spikes correlate with camera movement and resource-plan revisions, investigate cascade recollection and coarse command-chain invalidation. If GPU shadow time stays low while CPU frame time rises, do not optimize shadow shaders first.

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
requested kind / manager entry kind / effective light kind / selected caster material kind / fallback reason
request start+end / member start+count / duplicate member count
atlas page+pixel rect+inner rect+storage generation or legacy image+subresource
target mask / CPU-recorded mask / submitted mask / GPU-completed mask / matrix hash / caster packet count / draw count
request generation / CPU-recorded generation / submitted generation / GPU-completed generation / published generation / sampled generation
descriptor set+slot / dynamic offset / image view / sampler / layout / texture-ready state / dummy-substitution state
CPU collect+record time / submit serial / GPU timeline / stale age / terminal state
```

Terminal state must distinguish `Rendered`, `Unsupported`, `Failed`, `DeferredBudget`, `DeferredDependency`, and `StaleReused`. A failed first render must never be mislabeled as an ordinary stale reuse or budget event. Record completion-ring overflow separately; the current ring drops a completion record when full, which can leave point/spot metadata stale even if commands were recorded.

## Acceptance criteria

### Correctness

- Sequential, non-atlased directional, point, and spot shadows each produce visibly correct current lighting.
- All point cube faces and all configured directional cascades contain expected caster depth with correct orientation and overlap.
- Instanced-layered and geometry-shader legacy paths match sequential output within depth precision.
- Per-caster geometry fallback inside an instanced directional or point pass preserves the same target count, matrices, logical indices, and output.
- Directional, point, and spot atlases match their non-atlased controls, including gutters, page/layer selection, and tile transforms.
- Point and directional final paths use grouped layered rendering without silent sequential fallback.
- A point group owns each face exactly once; successful groups are not followed by duplicate tile draws, and failed groups do not stop unrelated requests or masquerade as budget deferral.
- A point light requesting sequential atlas rendering stays sequential; every requested/effective mode transition has an explicit capability or fallback reason.
- Atlas array growth either preserves every valid old layer or invalidates and redraws all affected residents before publication.
- All three atlas binders apply the same texture/view readiness contract, and a requested point path is reported separately from the presence of a sampleable face.
- Atlas filters never read untouched gutters or neighboring tiles; depth and moment encodings clamp to valid texel centers for their configured kernels.
- Camera/light/caster motion produces no unconfigured stale frame; any intentionally allowed latency is bounded, observable, and reported.
- No Vulkan validation errors, descriptor-binding failures, dummy-shadow substitutions, or uninitialized atlas sampling occur.

### Performance

- A settled frame performs no unexpected shadow recollection, atlas rewrite, or broad command-chain re-record.
- Camera motion invalidates only shadow data whose concrete contents changed.
- Directional work scales with collected casters plus target-mask expansion, not N complete CPU submissions.
- Point work uses one collected/batched stream for six faces, not six independent full submissions.
- Failed or deferred atlas entries do not hot-loop, consume unbounded budget, or advance unrelated resource revisions.
- Atlas array capacity growth does not cause unbounded resource recreation or force unrelated page redraws without an explicit accounting reason.
- CPU-direct p95/p99 remain bounded relative to lights-off and nonshadowed-light controls; thresholds should be set after Phase 6 produces a stable baseline.

## Recommended implementation order after approval

1. Preserve the resolved shadow material, complete immutable shadow-pass state, and matching compatibility identity across the CPU-direct mesh-request queue. Never re-resolve a queued shadow draw from cleared live state.
2. Isolate and fix the dynamic-rendering/secondary-command image-layout failures, comparing primary reuse enabled and disabled, before treating later screenshots or captures as synchronization-clean.
3. Make sequential, non-atlased write and sampling correct for directional, point, and spot.
4. Split CPU-recorded, submitted, GPU-completed, published, and sampled atlas milestones; align point/spot readiness gates with directional and choose an explicit same-frame or bounded-latency publication contract.
5. Add atlas storage generation to content identity. On texture-array growth, copy valid layers or invalidate/redraw every affected resident before publication.
6. Fix point manager route selection, group request range ownership, duplicate tile entries, failure terminal state, and explicit per-face containment.
7. Unify immutable layered inputs across instanced and geometry variants, including per-caster geometry fallback and both point matrix-name aliases until shaders are standardized.
8. Validate legacy layered arrays/cubes against sequential output.
9. Define and implement the atlas border contract: full allocation clear where required, edge dilation or sufficient guard data, and texel-center-safe sampling for every encoding/filter.
10. Restore grouped directional and point atlas rendering with isolated indexed viewport/scissor state and per-target caster masks.
11. Remove or gate unused CPU-direct dynamic capture and replace coarse invalidation with concrete resource identity.
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
- confirmed the point group request-range/duplicate-entry defect and failure-as-budget accounting
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

Relevant source areas:

- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.ShadowAtlasEncodingState.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/PointLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/SpotLightComponent.cs`
- `XREngine.Runtime.Rendering/Rendering/MeshRenderMaterialResolver.cs`
- `XREngine.Runtime.Rendering/Rendering/LayeredShadowUniformState.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanMeshRenderRequest.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs`
- `Build/CommonAssets/Shaders/DirectionalCascadeShadowDepth.gs`
- `Build/CommonAssets/Shaders/PointLightShadowDepth.gs`
- `Build/CommonAssets/Shaders/PointLightAtlasShadowDepth.gs`
- `Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl`

## Current stopping point

Phase 0 is complete for device capabilities and settled component/manager route selection. It is not complete for the requested per-draw, submitted, GPU-completed, published, and sampled generations because the current diagnostics do not expose those milestones atomically.

The first shared implementation gate is now source-confirmed: CPU-direct deferred materialization must carry the already-resolved shadow material and immutable shadow state instead of recomputing them after their scopes have ended. Until then, component route labels and legacy face/cascade completion counters do not describe the prepared Vulkan draw. The second immediate gate is the point manager/light grouping contradiction; the third is the core Vulkan image-layout failure in dynamic-rendering/secondary-command execution.

After those gates are addressed, Phase 1 still needs one RenderDoc producer capture per non-atlased sequential light and Phase 2 needs the matching receiver binding. Do not use the dark screenshots, component face masks, or nonzero manager rendered-frame values as substitutes for depth-resource and descriptor evidence.

# Shadow Atlas

This document describes the dynamic shadow atlas: the allocator, relevance, threading, solve, stale-frame and reprojection rules, and cascade publication. The code is in `XREngine.Runtime.Rendering/Rendering/Shadows/` and `XREngine.Runtime.Rendering/Rendering/Lights3DCollection*.cs`.

Related: [Default Render Pipeline Notes](default-render-pipeline-notes.md), [Render Pipeline Resource Lifecycle](render-pipeline-resource-lifecycle.md).

## Type And File Map
| Type | File | Responsibility |
|---|---|---|
| `ShadowAtlasManager` | `ShadowAtlasManager.cs` and partials | Request intake, solve, render plan, publication, completions. |
| `ShadowAtlasManager.ShadowBuddyPageAllocator` | `ShadowAtlasManager.ShadowBuddyPageAllocator.cs` | Buddy allocator for one page. |
| `ShadowAtlasManager.ShadowAtlasEncodingState` | `ShadowAtlasManager.ShadowAtlasEncodingState.cs` | Pages, texture arrays, and allocators for one `(EShadowAtlasKind, EShadowMapEncoding)`. |
| `ShadowAtlasFrameData` | `ShadowAtlasFrameData.cs` | Published, double-buffered allocations, groups, page descriptors, metrics, and solve diagnostics. |
| `ShadowAtlasRenderPlan` | `ShadowAtlasRenderPlan.cs` | Double-buffered render plan with `FrameId`, `PlanId`, entries, and members. |
| `ShadowMapRequest`, `ShadowRequestKey`, `ShadowAtlasAllocation`, `DirectionalCascadeSampleState` | `ShadowAtlasTypes.cs` | Request, key, allocation, and frozen cascade sample records. |
| `LocalShadowFrustumRelevance` | `LocalShadowFrustumRelevance.cs` | Spot and point-face relevance tests against the camera frusta. |
| `Lights3DCollection` | `Lights3DCollection.Shadows.cs`, `Lights3DCollection.Buffers.cs` | Request building, collection, publication, and the render-thread call. |
| `DirectionalLightComponent` | `Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs` | Cascade slots, rendered sample payloads, and receiver uniforms. |
| `ShadowAtlasManager` Advanced lane | `ShadowAtlasManager.AdvancedDirectionalShadowLane.cs` | Hand-off of directional cascade groups to the Advanced directional shadow stage. |

## Atlas Ownership

- Each `(EShadowAtlasKind, EShadowMapEncoding)` pair owns its own pages. The kinds are `Directional`, `Spot`, and `Point`. A page index alone is not unique. Consumers use the atlas kind, the encoding, and the page, or the packed `AtlasId`.
- A page is one layer of a texture array. The encoding state allocates the configured `MaxPages` layers once. It does not grow a live array, because recreation would discard every resident tile while published allocations still advertise them. `ResidentBytes` counts the allocated layers.
- Directional `Depth` pages are depth-only. They have no color array, and receivers sample the raster depth array (`ShouldSampleRasterDepth`). Other kinds and encodings have a color or moment array plus a raster depth array.
- `MaxShadowAtlasMemoryBytes` limits page creation. A page that exceeds the limit is not created, and the request records a skip reason.
- Spot and point atlas modes are depth-only. Spot and point VSM and EVSM lights use standalone moment maps.

## Allocator

- Each page has a `ShadowBuddyPageAllocator`. Tile sizes are powers of two. Each level keeps its free slots as a bitset. Allocation takes the lowest free slot (`TrailingZeroCount`), so placement is deterministic. Free blocks merge with their buddies.
- Page occupancy is persistent across frames. `PreparePersistentAllocatorForSolve` frees only resident entries that are not requested and older than `ResidentEvictionTtlFrames` (240 frames), entries over capacity, and unusable entries.
- `TryReusePersistentAllocations` keeps an unchanged resident placement at the same resolution. This counts as `IncrementalReuseCount`.
- Prior slots are placement hints. The solver can reserve the previous slot, reuse an aligned sub-block after a downsize, and keep a rendered previous tile when an upgrade cannot fit.
- `SkipReason.NotRelevant` can keep a stale resident tile. Stale reservations apply after live allocations. If a live request needs the region, the stale request publishes a non-resident fallback.
- A full occupancy reset happens only on `RequestRepack()` or `RequestAtlasKindReset(...)`. Both are requests from any thread. The planning thread consumes them at the next frame boundary. `ConfigureFromEngineSettings` resets resources only when the page size, page count, memory limit, or tile resolution limits change.
- Automatic fragmentation-triggered repack does not exist. Metrics publish `LargestFreeRect` and `FreeTexelCount` for each atlas.

## Solve

`SolveAllocations` runs these steps for each `(kind, encoding)` bucket:

1. Classify and sort the requests. The order is editor-pinned first, then a render-order bucket (directional, spot, point, clean reusable last), then priority, then prior placement, then the key.
2. Apply directional cascade group resolution caps. Cascades of one light share a resolution that fits a square grid on one page.
3. Apply the feasibility waterline. If the requested texels exceed `PageSize² × MaxPages`, demote the lowest-relevance entry one level until the total fits (`WaterlineDemotionCount`).
4. Reuse persistent placements, then pre-reserve directional cascade groups, then allocate the remaining entries.
5. When a placement fails, repair locally: demote that entry (or its directional group) and try again. The attempt ceiling is `Math.Clamp(entryCount / 64 + 1, 1, 4)`. An entry that still fails publishes a skip reason.
6. Build directional cascade groups and point-face groups from keyed maps, with pooled member arrays.
7. Build the render plan.

Sticky demotion keeps a recently demoted entry as the demotion target for `DemotionPromotionCooldownFrames` (12 frames) unless another entry is lower by `DemotionSwitchMargin` (25%). Non-cascade requests use LOD hysteresis.

## Relevance

- `ResolveRelevanceScore` uses the request priority only. Receiver-aware scoring is open work.
- Spot priority uses projected screen influence and brightness from the atlas cameras. Point-face priority adds camera-face alignment. Point faces with low alignment request a quarter or half resolution.
- `LocalShadowFrustumRelevance` intersects the spot frustum or each point-face frustum with the active camera frusta. A spot or face that intersects no camera publishes `SkipReason.NotRelevant`.
- Atlas cameras include every active viewport camera of the world and both VR eye viewports. Render-on-demand viewports stay in the camera set.

## Threading And Publication
| Step | Thread | Call |
|---|---|---|
| Drain completions, build requests, solve, build plan | Collect-visible thread | `Lights3DCollection.CollectVisibleItems` → `PlanShadowAtlasRequests` → `ShadowAtlas.BeginFrame`, `Submit`, `SolveAllocations` |
| Publish frame data and plan | Swap phase | `Lights3DCollection.SwapBuffers` → `PublishShadowAtlasFrame` → `ShadowAtlas.PublishFrameData` |
| Execute plan | Render thread | `Lights3DCollection.RenderShadowMaps` → `ShadowAtlas.RenderScheduledTiles` |

- The swap phase is exclusive with the render thread. `PublishFrameData` writes the inactive `ShadowAtlasFrameData` buffer and the pending render plan, then flips both published indices with volatile writes.
- The render thread executes only the published plan. `RenderScheduledTiles` resolves the plan once and passes it down. It does not read planner collections.
- Plan ids come from a planner-owned counter (`AllocateRenderPlanId`), not from the render frame id.
- Debug builds assert the planning thread (`AssertPlanningThread`) and the render thread (`AssertRenderThread`).
- The synchronous capture path (`EnsureShadowMapsCurrentForCapture`, `RenderShadowMapsInternal(collectVisibleNow: true)`) plans, publishes, and executes on one thread through the same plan hand-off.

### Completions

When a tile renders, `MarkTileRendered` does three things:

1. `PublishRenderedCompletionOverlay` records the rendered allocation so that the same frame can see it.
2. `CommitRenderedTileToLightSlot` writes the rendered allocation and sample into the light slot before the lighting pass reads it.
3. `EnqueueTileCompletion` adds a completion with the plan id and frame id to a bounded ring. The planner drains the ring in the next `BeginFrame` (`DrainTileCompletions`) and reconciles resident allocations. A full ring increments `QueueOverflowCount`.

The render thread never mutates planner-owned collections.

## Render Plan Execution

- Plan entries are `Tile`, `DirectionalCascadeGroup`, or `PointFaceGroup`. Each entry has a budget class: `CriticalBypass`, `Normal`, or `Deferrable`.
- `MaxTilesRenderedPerFrame` and `MaxRenderMilliseconds` limit normal work. Budget checks happen only at entry boundaries, so a group never renders partly.
- Interactive directional camera-fit and content refreshes are `CriticalBypass` work. They bypass the tile and time budgets.
- A sequential directional cascade tile costs a full group in the budget.
- Grouped atlas rendering needs same-page group members and indexed viewport and scissor support. Vulkan uses grouped entries except on the known Monado OpenXR runtime, which keeps sequential tile entries.
- On Vulkan with the known Monado OpenXR runtime (an `XR_RUNTIME_JSON` path that contains `monado`), directional lights have no cascades, no cascade atlas and no grouped entries. This safety stop is for earlier OpenXR + Monado hangs while shared cascade texture arrays rendered; it is not proved that the hang is gone. `XRE_VULKAN_DIRECTIONAL_CASCADES=1` turns the three on for validation, and `0` turns them off on every runtime. `DirectionalLightComponent.AllowsVulkanDirectionalShadowWorkForCurrentRuntime` is the one test that the three gates use.
- `DirectionalLightComponent.CascadeShadowRenderMode` defaults to `EDirectionalCascadeShadowRenderMode.Auto`.
- When `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE` is not `0`, desktop depth-only cascade groups can defer to the Advanced directional shadow stage. The stage must report itself ready in the previous frame. A rejected or unconsumed group stays dirty, renders generically, and holds the lane closed for 60 frames.
- An exact `PresentNow` output captures a `ShadowAtlasReadinessManifest` and completes it with `CompleteReadinessManifest`. Exact work bypasses the normal budgets. Deadline work can use only a declared resident `StaleTile` fallback.

## Directional Cascades

### Content hash and dirtiness

- The depth content hash includes the light, projection, cascade index, source, encoding, movement version, resolution, caster membership revision, and the caster command-set signature.
- Directional matrices are hashed as `view × projection`, quantized to `clipTexel / 4096`. Sub-texel jitter does not dirty a cascade.
- The cascade fit snaps the light-space X/Y center to texels and the depth center to 1/16 of the depth range (`QuantizeCascadeDepthRange`). The depth range grows by half a step, so it always contains the fitted range and keeps a constant size. Without the depth snap, the cascade matrix changed in every frame while the camera moved, and every cascade re-rendered.
- `DesktopDirectionalCascadeFitCentre` and `HmdDirectionalCascadeFitCentre` (`EDirectionalCascadeFitCentre`, default `CameraPosition` for both) select the centre of each cascade's bounding sphere (`ApplySphereFit`). `ViewCentroid` centres it on the centroid of the slice corners, ahead of the camera on the view axis. That point swings with the view, so a turn moves the fit: with the 0.1 margin a cascade refits about every 9-15 degrees of turn (about 34 refits per second for a 90 degree/s turn in the default split). `CameraPosition` centres it on the mean of the near-plane corners of all source frusta (the head for a stereo source) with a radius to the farthest slice corner, so a turn does not move the fit. Cost against `ViewCentroid` in the default split (200 m, 10/20/30/40 percent, 60 degree vertical field of view at 16:9): the cascade radius, and with it the texel size, grows by 21, 26, 28 and 29 percent for cascades 0 to 3.
- `DirectionalCascadeRefitMarginFraction` (default 0.1, 0 turns it off) holds cascade fits while the camera moves. A new fit grows X and Y by the margin times the larger half extent, and depth by the margin times its half extent (`ExpandCascadeFitByMargin`). The fit is then held (`TryHoldCascadeFit`) while the camera's slice box, before any margin, stays inside the held box in X, Y and depth, the light rotation and the fit resolution do not change, and the held box is at most the slice box with two margins. A held fit keeps the cascade matrix and its content hash, so the cascade does not re-render. When the slice leaves the held box, the cascade refits with a new margin. Cost: the texel size and the depth step grow by the margin fraction (10% at the default). Moving casters, light changes, allocation changes and camera cuts keep their rules: the caster-state hash, the jump test and the full-group rule still apply.
- Receiver-only sampling state (soft shadow mode and bias) has a separate hash. A change republishes sampling data without a depth redraw.

### Sample payload and stale tiles

- Each cascade request freezes a `DirectionalCascadeSampleState`: split far distance, blend width, bias min and max, receiver offset, world-to-light matrix, source, cascade index, content hash, and rendered frame.
- A completion carries the rendered sample. `DirectionalLightComponent` stores the latest rendered sample per cascade slot.
- A sampleable stale atlas slot uses its rendered sample, not the current cascade slice. A slot's texture content and its uniform payload always come from the same generation.
- If a resident stale tile has no rendered sample, the slot is not sampleable and the receiver uses a visible fallback.
- `AdvancedGlobalResourceCapture` copies the directional records at the swap, before the render thread draws the cascades and commits their slots. So `ApplyPlannedDirectionalCascadeRefreshes` gives each cascade that this frame refreshes the values of this frame's draw: the current slice matrix (the draw uses `RenderSliceMatrices`, which the same swap copied), split, blend, bias, stale age 0, and the UV rectangle of the planned allocation. Without this, Advanced shading sampled a refreshed tile with the previous fit, and shadows trailed the camera by one frame. A cascade without a rendered tile keeps its unsampled record.
- The capture applies the planned values only when `ShadowAtlasManager.CanDrawDirectionalCascadeGroupsThisFrame` is true: the Advanced directional shadow lane is off, or the Advanced stage reported itself ready in the previous frame. Otherwise the scheduled-tile pass declines the groups, and a strict lane (meshlet and GPU-indirect frames) drops them without a draw, so the records keep their committed values.
- The next capture checks that each planned refresh committed the published matrix. A miss increments a counter and writes `DirectionalShadow.PlannedRefreshNotCommitted` with the running total to the auxiliary log `directional-shadow-planned-refresh.log`, at most every 5 seconds per light and source. Release builds write it too. In the missed frame, shading samples the previous tile content with this frame's matrix, the same one-frame error as before the fix.
- The collect thread fits the cascade slices and records each request's matrix before the swap. While the render thread draws, the collect thread can already refit the slices for the next frame. So at the swap boundary, `PublishCascadeRenderCameras` copies the slice matrices into `RenderSliceMatrices` and the cascade cameras into `RenderCameras`. All tile draws (per tile, grouped, legacy layered, and the Advanced shadow lane) use these copies. Then a tile's content always matches the matrix that its atlas slot records for reprojection. `XRE_DIRECTIONAL_SHADOW_AUDIT=1` in a Debug build logs `[CascadeDrawMatrix]` with `liveRefitted=True` when a draw would otherwise have used the next frame's fit.
- The receiver selects the cascade from the current view. For a rendered slot, it uses the rendered split and matrix from `DirectionalShadowRecords`. `XRENGINE_CanSampleRenderedCascade` rejects an invalid page or a `StaleTile` slot older than `DirectionalShadowAtlasMaxStaleFrames`. Sampling fades near the tile border.

### Cadence, stale age, and forced refresh

- `MaxDirectionalCascadeAtlasStaleFrames` (default 4) limits stale reuse. A dirty cascade at this age gets `ShadowDirtyReason.StaleContentAgeLimitReached` and renders fresh. Local spot and point tiles use a fixed cap of 4 frames.
- By default a content-changed cascade refreshes in the same frame, so it keeps full resolution and coverage while the camera moves.
- `MaxDirectionalCascadeRefreshesPerFrame` (default 1, 0 removes the limit) limits the refreshes of one light and source in one frame when only the camera moved. Each cascade draws the whole caster set that reaches its volume, so a frame that refreshes all cascades costs several full-scene rasters. Before the cascade requests, `GrantDirectionalCascadeRefreshes` selects the candidates: cascades with a rendered tile, a changed depth content hash, and the same caster state as their last render. The candidates with the oldest rendered frame get the slots (the nearest wins a tie), so a moving camera refreshes the cascades in turn. A cascade that is not selected keeps its stale tile (`SkipReason.StaleTileReused`). With four cascades and the default stale limit of four frames, no cascade reaches the stale limit while the camera moves. The collect thread plans the next frame while the render thread draws the current one, so a refresh from the previous plan usually has not completed yet. `GetDirectionalCascadeRefreshInFlightFrame` reports such a refresh (requested after the last committed render, at most two plan frames ago). The selection uses its request frame as the cascade's age, and the stale-age flag is cleared for it, so the same cascade does not render twice in a row.
- These cascades refresh at once and do not use a slot: a cascade without a rendered sample, a cascade whose fit jumped past the jump thresholds (a light rotation or a camera cut), a cascade whose caster state changed (`BuildShadowCasterStateHash`: light movement version and resolution, caster membership, the caster command set signature with the command world matrices, and the covered caster output revision), and a cascade at the stale limit (a held tile at that age would publish the unavailable fallback).
- Caster changes are tracked per cascade. `RenderInfo3D` reports a shadow caster to `VisualScene3D.RecordShadowCasterChange` when its world box changes, on every forced deformation notification (a new skinned pose, also when the box stays the same), and when its cast-shadow flag changes. The scene keeps a lock-free ring of 4096 records (an interlocked write index and a sequence stamp per slot). The shadow planning pass drains it once per plan pass, allocation-free; a record whose writer has not finished stays for the next drain, and a lapped ring marks every cascade. `DirectionalLightComponent.MarkDirectionalCascadeCasterChanges` then advances the caster epoch of a cascade when a changed box can shadow a receiver of its frustum slice: the box, grown by a 2 percent filter margin, overlaps the light-space X/Y convex hull of the slice corners of every source frustum (both eyes for a stereo source, with the split overlap), and its top is above the slice's lowest point. A change in the border band (outer 10 percent) of the previous cascade's tile also marks the next cascade, because shading blends into it there. A change that meets a cascade's tile but not its receiver hull goes into a pending region; when a later slice (a turn or a walk with a held fit) covers that region, the cascade is marked. A render clears the region. The epoch is the cascade's caster command signature in the depth content hash and in the caster-state hash, so a marked cascade renders at once, past the refresh limit. Caster membership (add, remove, visibility) still marks every cascade through `ShadowCasterMembershipRevision`. After the hull test, the caster box is extruded away from the light to unlimited depth and tested against the six light-space planes of each source frustum's slice (`ShadowVolumeMeetsSlice`), so a caster marks a cascade only when it lies above a receiver that it can reach. Limits: a change of a render command's mesh (also a LOD switch), material override, instance count or enabled state reports the owner's box through `RenderCommandMesh3D.OnPropertyChanged` and `RenderInfo3D.ReportShadowCasterContentChange`; an edit inside a material object (for example its alpha cutoff value) is not reported.
- With `HoldDirectionalCascadesWhileCameraMoves` on, a content-changed cascade with a rendered sample keeps its coherent stale group while the source camera moves (`SkipReason.StaleTileReused`), and shading reprojects the held tile with the matrix it was rendered with. It refreshes after the source camera is stable for `Math.Clamp(cascadeCount, 2, 8)` frames, or at the stale limit. The hold avoids republishing shadow records, which today invalidates the persistent mesh binding artifacts, but a held tile fitted for an older camera has lower resolution or less coverage, so shadows can look scaled while the camera moves.
- A cascade without a rendered sample, or at the stale-age limit, renders fresh and increments `DirectionalCascadeForcedFreshRender`.
- A cascade jump larger than the linear or translation threshold (`ResolveDirectionalCascadeLinearJumpThreshold`, `ResolveDirectionalCascadeTranslationJumpThreshold`) cannot use stale reprojection.
- Cadence-skipped and hash-stable cascades skip caster collection.

### Group publication

- A cascade group holds the cascades of one `(light, source, encoding, atlas page)` that render in this frame: the requests with no forced skip that are dirty or cannot reuse the previous frame. This is the same rule that marks a cascade for caster collection. The group's members render completely or fail as a unit.
- A group can hold one member when the light has more cascades. A light with only one cascade uses the per-tile path.
- If any cascade of the light has a new, moved or resized allocation, or its dirty reason reports an allocation or encoding change, the group holds all cascades. A stale tile is only valid in its previous slot.
- A cascade that does not render keeps its committed tile and its rendered sample. Receivers sample that stale tile with its own committed matrix. A point outside the stale tile uses the next cascade. The stale-age limit forces the refresh.
- Completed group members commit together through `CommitRenderedCascadeAtlasSlots`. Each slot's texture and its matrix always come from the same draw, so the lighting pass never samples a tile with the matrix of another generation.
- The `DirectionalCascade.MixedGenerationPrevented` counter records prevented mixes.

### Static caster tiles of directional cascades

The Advanced directional shadow lane can keep the static casters of a cascade in a separate depth tile. Then a refresh that only dynamic casters cause copies that tile into the cascade tile and draws only the dynamic casters over it. The cost of such a refresh is a tile copy and the dynamic casters, not the whole static scene. Scope: desktop source, CPU-direct and meshlet frames (CPU record masks), every cascade with recent dynamic casters. HMD sources, strict indexed frames (GPU caster selection) and the generic path draw all casters.

- Setting: `DirectionalShadowStaticCacheEnabled` (default on). `XRE_DIRECTIONAL_SHADOW_STATIC_CACHE=0` turns it off for the process.
- Which renderables report. A renderable reports caster changes only when `CastsShadows` is true and one of its enabled render commands draws in a shadow caster pass (pre-render, opaque deferred, opaque forward, masked forward, post-render). A skybox or UI text is not in the caster record set, so its changes do not mark cascades.
- Static and dynamic casters. `RenderInfo3D.IsShadowCasterDynamic` is always true for a renderable with committed deformation bounds (a skinned or deformed mesh); these never become static. Another renderable becomes dynamic at its first transform or bounds change (`VisualScene3D.NoteShadowCasterMotion`), also during scene load. The promotion costs one static draw of the tiles that the caster meets, the same as a one-off move of a static caster, and each later move is free. A content change (mesh, material, instance count, enabled state; for example a LOD switch) is a static change; three content changes within one second promote the caster (`NoteShadowCasterContentChange`), so a mesh that flips between two LODs at a threshold (the LOD selection has no hysteresis) does not redraw the static tiles in each frame. A dynamic renderable without a change for 5 seconds (Stopwatch time) is due for demotion. `VisualScene3D.ProcessShadowCasterDemotions` demotes a due caster when every cascade (desktop and HMD source) whose tile the caster box meets draws its static tile anyway in that plan pass: a render is requested and it changes the cascade fit or static caster epoch (`DirectionalLightComponent.DirectionalStaticTilesRedrawFor`, through `IShadowCasterDemotionGate`). Such a demotion adds no static draw. Two rules force a batch of all due casters without the gate, so the overlay does not keep quiet casters forever: 32 or more due casters, or a caster due for 5 seconds without a planned static draw (at most one such batch per 10 seconds). A forced batch costs one static draw of each cascade that its casters meet (counter `forcedDemotionBatches`). The swap copies the value into the draw flags (`GPUIndirectRenderFlags.ShadowCasterDynamic`). A change of the value marks the commands dirty, so the GPU scene converts them again.
- Static caster epoch. The change ring records whether a change comes from a caster that was dynamic before the change and stays dynamic. A promotion and a demotion report a static change. `MarkDirectionalCascadeCasterChanges` advances `CascadeStaticCasterEpochs[i]` when a static change meets the cascade tile (not only the receiver footprint), when the static part of the pending region marks the cascade, and on a ring overflow. The pending region has a static part (`PendingStatic*`) next to the full region: a dynamic change in the pending region refreshes the cascade but does not make its static tile stale. A dynamic change advances only the refresh epoch, and it records the update frame in `CascadeDynamicChangeFrames[i]`.
- Static tiles. `DirectionalCascadeStaticTile` is a one-layer depth texture with the atlas page depth format and the size of the inner tile rectangle of its cascade, with its own framebuffer. The shadow atlas manager keeps one tile per (light, cascade) for a cascade that a dynamic change marked within the last 600 update frames, creates it on first use and again when the inner tile size changes, and releases it after 600 update frames without a dynamic change (`AttachDirectionalStaticCascadeTiles`). A cascade without a tile draws all casters (cause `noDynamicCasters`). Receivers never sample a static tile. Memory per tile: 4 MB at 1024 px, 16 MB at 2048 px.
- Key. When the lane records a static draw, it stores the key in the tile: the cascade world-to-clip matrix, the inner tile rectangle, the atlas page, the static caster epoch, the static record set (count and a 64-bit hash), and the render frame. A frame copies the tile without a static draw only when all of these match: the matrix in all 16 values, the rectangle and page, the epoch, the record count and hash, and the tile is from an earlier render frame. The record set is the set of frozen-stream records that cast into the cascade (record mask bit) and do not carry the dynamic flag. The stream holds the canonical scene draws, not a camera-culled list, and a record mask depends only on the record bounds and the cascade matrix, so a camera move inside a held fit does not change the set. The hash is order independent: a sum and an xor of a SplitMix64 mix of each record's draw handle (index and generation); one pass over the records computes it for all cascades of the group. A hash collision with an equal record count would reuse a tile with another record set; the probability is about 2^-64 per comparison.
- Key update time. The lane stores the key only in the method that records the static draw. A group that the lane declines is never recorded, so its tiles keep the keys of their content. A failed submission that held a cascade invalidates its tile (`ShadowAtlasManager.InvalidateDirectionalStaticCascadeTile` from the submission receipts). A retry of the same render frame cannot use a tile from that frame.
- First copy into a page layer. When the destination layer has no access state in the command buffer and no tracked layout of its own (a layer that was never drawn), the lane opens and closes an empty render scope on the page before the copy. The scope loads the page (no clear), so other tiles keep their content, and it moves the layer out of `UNDEFINED`. The blit barrier alone would start from the layout tracked for the whole array texture. `pageLayerInitializations` counts these scopes.
- Frame sequence. Preparation checks that each tile framebuffer is ready and computes the record sets. Recording then, for each cascade with a tile: (1) when the key does not match, draws the static records of the cascade into its tile (clear, cascade matrix) and stores the key; (2) copies the tile into the inner rectangle of the cascade on the page with tracked transfer barriers. Then it begins the page scope, draws only the dynamic records of each cached cascade over its copy without a clear, and draws the other cascades of the group as before. A frame without a matching key does all steps, so a cascade tile never holds static content without the dynamic casters. The copy covers the inner rectangle: the cascade draw writes only that rectangle, and receivers clamp filter taps to its texel centres, so the gutter is never written or sampled.
- Refits. A refit (the held fit leaves its margin) draws the static tile of that cascade again. With a dynamic caster in the cascade, the cascade refreshes at once (a caster change), so the refit does not wait for a refresh slot.
- Counters. `DirectionalShadowStaticCacheDiagnostics` counts static draws, copies, copy-only refreshes (`overlayFrames`), dynamic record draws, tiles in use, created and released, page layer initializations, promotions, demotions, demotion batches, the due casters, and the reasons why a cascade did not copy without a static draw (`EDirectionalShadowStaticCacheCause`: disabled, out of scope, no dynamic casters, GPU caster selection, tile unavailable, no static content, fit mismatch, tile mismatch, static epoch changed, record set changed, same frame). Every 5 seconds the lane writes the totals to the auxiliary log `directional-shadow-static-cache.log`.

## Generations

`ShadowAtlasFrameData` has four generations: `LayoutGeneration`, `ContentGeneration`, `StorageGeneration`, and `PublicationGeneration`. `Generation` is `PublicationGeneration`. A layout change does not have to change the publication count of unrelated content, and a content change can advance publication without a layout change. A reset of an atlas kind advances all generations.

## Diagnostics

- `ShadowAtlasSolveDiagnostics` reports request counts by kind and encoding, attempts, failed candidates, demotions, sticky and group demotions, deterministic fallback demotions (the current solver does not increment this counter), prior-reserve and sub-block hits and misses, page allocation, creation, and clear counts, group work, `IncrementalReuseCount`, `WaterlineDemotionCount`, and the last failure reason. The profiler render stats panel shows these values.
- A slow solve logs a rate-limited warning. The threshold is `XRE_SHADOW_ATLAS_SOLVE_WARN_MS`, or `max(2 ms, MaxRenderMilliseconds)`.
- `ShadowAtlasMetrics` reports resident tiles, skipped requests, pages, resident bytes, scheduled tiles, queue overflow, `NotRelevant` skips, largest free rect, free texels, and directional grouped and sequential fallback frames.
- `XRE_DIRECTIONAL_SHADOW_AUDIT=1` enables the `[DirectionalShadowAudit]` log lines: plan execution source, completion latency, submits, legacy decisions, and atlas frame summaries.
- Atlas UV scale and bias use texel centers for both backend Y conventions.

## Encodings And Filtering

- `Depth` encoding uses the depth-compare filters of `ESoftShadowMode` (hard, Poisson, Vogel, PCSS and contact hardening). VSM and EVSM encodings use moment parameters, mips, and blur. They do not use PCSS kernel radii.
- `ShadowMomentEncoding.glsl` holds the VSM and EVSM encode and sample helpers, point radial moments, Chebyshev visibility, and the 2D, array, and cube moment receivers.
- Point atlas receivers are cube-seam aware. A filter tap perturbs the direction, selects the owning face again, and samples that face's tile metadata.
- Contact shadows (`XRENGINE_SampleContactShadowScreenSpace`) multiply on top of depth or moment visibility.
- `UsesDirectionalShadowAtlasForCurrentEncoding` accepts `Depth`, `Variance2`, `ExponentialVariance2`, and `ExponentialVariance4` for directional atlases on every backend. Advanced native shading reads directional shadows only from atlas records, so a Vulkan moment encoding that fell back to the legacy cascade arrays rendered with no shadow.
- Advanced native shading applies the light's soft-shadow mode, filter samples, filter radius (in tile-local UV, widened by 35% per cascade), PCSS blocker search and penumbra limits, and the short-range contact-shadow march from `AdvancedShadowRecord`, the same way as the forward receivers.

## Upscaler And Frame Generation Boundary

Shadow maps and atlas pages are engine-owned intermediate resources. DLSS Super Resolution, DLAA, and Frame Generation run after lighting and post-processing on final frames. They do not generate or update shadow maps. DLSS Ray Reconstruction can denoise future ray-traced shadows, but it does not replace atlas updates. Shadow update cadence, invalidation, caching, and fallback stay explicit engine behavior with diagnostics.

## Known Limits

- Relevance uses priority only, not visible receivers.
- No automatic fragmentation compaction exists.
- No physical stale-tile reuse (scroll or reprojection) exists. Stale cascades use shader-side reprojection only.
- Point-face groups form only when independently allocated faces land on the same page.

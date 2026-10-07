# Shadow Atlas

This document describes the dynamic shadow atlas: the allocator, relevance, threading, solve, stale-frame and reprojection rules, and cascade publication. The code is in `XREngine.Runtime.Rendering/Rendering/Shadows/` and `XREngine.Runtime.Rendering/Rendering/Lights3DCollection*.cs`.

Related: [Default Render Pipeline Notes](default-render-pipeline-notes.md), [Render Pipeline Resource Lifecycle](render-pipeline-resource-lifecycle.md). Open work: [Shadow Atlas Overhaul TODO](../../work/todo/rendering/shadows/shadow-atlas-overhaul-todo.md). Checks: [Shadow Validation](../../work/testing/rendering/shadow-validation.md).

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
- `DirectionalLightComponent.CascadeShadowRenderMode` defaults to `EDirectionalCascadeShadowRenderMode.Auto`.
- When `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE` is not `0`, desktop depth-only cascade groups can defer to the Advanced directional shadow stage. The stage must report itself ready in the previous frame. A rejected or unconsumed group stays dirty, renders generically, and holds the lane closed for 60 frames.
- An exact `PresentNow` output captures a `ShadowAtlasReadinessManifest` and completes it with `CompleteReadinessManifest`. Exact work bypasses the normal budgets. Deadline work can use only a declared resident `StaleTile` fallback.

## Directional Cascades

### Content hash and dirtiness

- The depth content hash includes the light, projection, cascade index, source, encoding, movement version, resolution, caster membership revision, and the caster command-set signature.
- Directional matrices are hashed as `view × projection`, quantized to `clipTexel / 4096`. Sub-texel jitter does not dirty a cascade.
- Receiver-only sampling state (soft shadow mode and bias) has a separate hash. A change republishes sampling data without a depth redraw.

### Sample payload and stale tiles

- Each cascade request freezes a `DirectionalCascadeSampleState`: split far distance, blend width, bias min and max, receiver offset, world-to-light matrix, source, cascade index, content hash, and rendered frame.
- A completion carries the rendered sample. `DirectionalLightComponent` stores the latest rendered sample per cascade slot.
- A sampleable stale atlas slot uses its rendered sample, not the current cascade slice. A slot's texture content and its uniform payload always come from the same generation.
- If a resident stale tile has no rendered sample, the slot is not sampleable and the receiver uses a visible fallback.
- The receiver selects the cascade from the current view. For a rendered slot, it uses the rendered split and matrix from `DirectionalShadowRecords`. `XRENGINE_CanSampleRenderedCascade` rejects an invalid page or a `StaleTile` slot older than `DirectionalShadowAtlasMaxStaleFrames`. Sampling fades near the tile border.

### Cadence, stale age, and forced refresh

- `MaxDirectionalCascadeAtlasStaleFrames` (default 4) limits stale reuse. A dirty cascade at this age gets `ShadowDirtyReason.StaleContentAgeLimitReached` and renders fresh. Local spot and point tiles use a fixed cap of 4 frames.
- While the source camera moves, a content-changed cascade with a rendered sample keeps its coherent stale group (`SkipReason.StaleTileReused`). It refreshes after the source camera is stable for `Math.Clamp(cascadeCount, 2, 8)` frames.
- A cascade without a rendered sample, or at the stale-age limit, renders fresh and increments `DirectionalCascadeForcedFreshRender`.
- A cascade jump larger than the linear or translation threshold (`ResolveDirectionalCascadeLinearJumpThreshold`, `ResolveDirectionalCascadeTranslationJumpThreshold`) cannot use stale reprojection.
- Cadence-skipped and hash-stable cascades skip caster collection.

### Group publication

- All cascades of one `(light, source, encoding)` form one generation. A cascade group renders completely or fails as a unit.
- Completed group members commit together through `CommitRenderedCascadeAtlasSlots`. The lighting pass never mixes cascade generations of one light and source.
- The `DirectionalCascade.MixedGenerationPrevented` counter records prevented mixes.

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
- On Vulkan, `UsesDirectionalShadowAtlasForCurrentEncoding` accepts `Depth` only. Other backends accept `Depth`, `Variance2`, `ExponentialVariance2`, and `ExponentialVariance4` for directional atlases.

## Upscaler And Frame Generation Boundary

Shadow maps and atlas pages are engine-owned intermediate resources. DLSS Super Resolution, DLAA, and Frame Generation run after lighting and post-processing on final frames. They do not generate or update shadow maps. DLSS Ray Reconstruction can denoise future ray-traced shadows, but it does not replace atlas updates. Shadow update cadence, invalidation, caching, and fallback stay explicit engine behavior with diagnostics.

## Known Limits

- Relevance uses priority only, not visible receivers.
- No automatic fragmentation compaction exists.
- No physical stale-tile reuse (scroll or reprojection) exists. Stale cascades use shader-side reprojection only.
- Point-face groups form only when independently allocated faces land on the same page.
- Vulkan directional moment-encoded atlases are not supported.

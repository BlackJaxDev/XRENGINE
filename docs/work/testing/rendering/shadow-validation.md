# Shadow Validation

Scope: Validate dynamic shadow atlases, directional cascade rendering, local lights, moment encodings, stale-frame behavior, temporal cache work, contact shadows, diagnostics, and editor triage.

Architecture: [Shadow Atlas](../../../architecture/rendering/shadow-atlas.md), [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)  Code todos: [Shadow Atlas Overhaul TODO](../../todo/rendering/shadows/shadow-atlas-overhaul-todo.md)

## Setup

Use unit test filter `FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~PointShadowAtlasStabilityTests|FullyQualifiedName~LocalShadowFrustumRelevanceTests|FullyQualifiedName~DirectionalShadowAtlasFallbackTests|FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests|FullyQualifiedName~CascadedShadowDefaultsAndForwardShaderTests`. Use task `Build-Editor`, or build `XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj` and `XREngine.Editor/XREngine.Editor.csproj`.

Use task `Start-Editor-NoDebug` with Unit Testing World (`--unit-testing` or `XRE_WORLD_MODE=UnitTesting`) on Vulkan and OpenGL. Use a scene with one four-cascade directional light, several spot lights, and several point lights.

Set `XRE_DIRECTIONAL_SHADOW_AUDIT=1` to enable `[DirectionalShadowAudit]` logs. Set `XRE_SHADOW_ATLAS_SOLVE_WARN_MS` to tune slow-solve warnings. Set `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE=0` to disable the Advanced directional shadow lane. Use task `Report-NewAllocations` for allocation audits.

Useful profiler scopes: `ShadowAtlasManager.SolveAllocations`, `Lights3DCollection.UpdateShadowAtlasRequests`, `ShadowAtlasManager.PublishFrameData`, `DirectionalCascade.Group.CollectVisible`, and `DirectionalCascade.Cascade.CollectVisible`.

## Checks

### Unit Tests And Builds
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Shadow unit tests | Run the setup filter. | All tests pass. | Open | none |
| Rendering builds | Run setup builds. | No errors and no new warnings. | Open | none |

### Atlas Allocation And Solve
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Solve baseline | Run Unit Testing World and capture setup profiler scopes. | Steady-state and worst-case ms and allocated bytes are recorded. | Open | none |
| In-budget scene | Run the in-budget benchmark scene. | Plan build stays below 0.5 ms on the collect thread; render thread shows tile execution scopes only. | Open | none |
| Over-budget scene | Run the over-budget benchmark scene. | Solve stays below 2 ms, and waterline or local-repair demotion appears in diagnostics. | Open | none |
| Steady-state allocations | Run thread allocation tracking and `Report-NewAllocations`. | Plan build and execute allocate zero GC bytes after warmup. | Open | none |
| Static stability | Hold camera and lights static after warmup. | `BalancedSolveAttemptCount == 1` and allocator churn does not occur unless settings or content change. | Open | none |
| Directional depth memory | Compare `ResidentBytes` for directional `Depth` pages. | No color-array bytes are allocated and visuals do not change. | Open | none |
| Oversubscription | Force one-page pressure with many spot and point lights. | Lower-relevance tiles demote or publish visible fallbacks; no stale transformed tiles appear. | Open | none |

### Receiver Relevance
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Off-screen local lights | Place static spot and point lights with no visible receivers. | They do not hold full-resolution tiles. | Open | none |
| Off-screen caster | View a receiver shadowed by an off-screen caster. | The light tile stays resident. | Open | none |
| Far cascades | View a scene where far cascades affect no visible receivers. | Far cascades demote or skip independently. | Open | none |

### Grouping
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Directional group | Use one light with four equal cascades on a backend with grouped support. | One page-coherent group renders in one grouped pass. | Open | none |
| Point-face group | Use one point light with one full-resolution face and five quarter-resolution faces. | Deterministic same-page group appears. | Open | none |
| Repack safety | Request a repack from editor diagnostics during rendering. | Metadata sampled in the same frame stays valid. | Open | none |

### Directional Cascades And Atlas Performance
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Atlas on and off baseline | Run Unit Testing World with atlas on and off. | Solve time, tiles, fallback frames, stall logs, and FPS are recorded. | Open | none |
| Pass count | Hold a static camera with one four-cascade light and grouped support. | After warmup, cascade passes per frame do not exceed legacy layered cascades. | Open | none |
| Frame rate | Use the same scene with grouped support. | Atlas-on frame rate stays within 10 percent of legacy layered cascades. | Open | none |
| Fallback reason | Run without grouped support. | Logs state the exact fallback reason and the editor shows the effective mode. | Open | none |
| Mode cost | Profile sequential, layered, and grouped modes in the same scene. | Costs are recorded. | Open | none |

### Stale Frames And Camera Motion
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Move and stop | Enable audit, move the camera, then stop. | No one-frame jitter appears; any failure frame is recorded. | Open | none |
| Interactive editor | Drag the camera and stop in the F5 editor path. | No visible one-frame shadow snap appears. | Open | none |
| Motion soak | Run a camera-motion soak with audit enabled. | Zero pending-plan executions. | Open | none |
| Small moves | Make small camera moves. | Stale cascade sampling stays stable. | Open | none |
| Large jump | Make a large camera jump. | Receiver uses lit, legacy, or fresh render; no unrelated stale depth projects. | Open | none |
| Visibility passes | Move the camera with one four-cascade light. | No five expensive visibility or command-recording passes appear unless diagnostics explain why. | Open | none |
| Motion cost | Capture CPU and GPU profile dumps during move-and-stop with Vulkan directional cascade atlas. | Movement frame drops have attributed and bounded cost. | Open | none |
| Moved local lights | Move one spot light and one point light in the editor. | Shadows refresh; no stale transformed tiles appear. | Open | none |

### Local Lights And Point Seams
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Spot and point depth atlases | Validate deferred and forward paths under oversubscription and stale fallback. | Correct shadows appear in both paths. | Open | none |
| Point seams | View filtered point shadows across cube face boundaries. | No seams or clamping artifacts appear. | Open | none |
| Masked casters | Use alpha-masked casters. | Atlas shadows show correct cut-outs. | Open | none |

### Moment Encodings
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Mixed encodings | Use depth, VSM, and EVSM lights in deferred and forward. | Both paths render correctly. | Open | none |
| Encoding flips | Change a light encoding at runtime. | No stale or dummy atlas page is sampled. | Open | none |
| Cascade transitions | Run directional moment cascades. | Transition bands blend filtered visibility and match per-cascade ground truth. | Open | none |
| Tile gutters | Run spot and point moment atlases when implemented. | No bleed occurs across gutters or cube faces. | Open | none |
| Directional moments per backend | Run directional VSM and EVSM on OpenGL and Vulkan. | Behavior matches [Shadow Atlas](../../../architecture/rendering/shadow-atlas.md#atlas-ownership). | Open | none |

### Temporal Cache
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Redraw cost | Measure redraw cost in static-heavy scenes before the cache decision. | Numbers are recorded. | Open | none |
| Static-heavy reuse | Run a static-heavy scene after warmup. | Fewer tile renders occur with no stale moved-light or moved-caster artifacts. | Open | none |
| Dynamic movers | Move dynamic objects near static casters. | Updates occur without full static tile refresh each frame. | Open | none |
| History rejection | Test camera cuts, tile repacks, light motion, cascade refits. | Temporal visibility history rejects correctly. | Open | none |

### Contact Shadows

- [ ] Contact-shadow cost. Procedure: capture visuals and profiler output before and after the helper refactor. Expected: cost drops at equal quality, and default sample counts are chosen from the result. Last evidence: none.

### Diagnostics

- [ ] Editor triage. Procedure: use only editor diagnostics to explain one allocation, one fallback, one grouped-render decision, and one tile refresh. Expected: no source reading is necessary. Last evidence: none.

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|

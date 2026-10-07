# Shadow Validation

Architecture: [Shadow Atlas](../../../architecture/rendering/shadow-atlas.md), [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)  Code todos: [Shadow Atlas Overhaul TODO](../../todo/rendering/shadows/shadow-atlas-overhaul-todo.md)

## Setup

- Unit tests: `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~PointShadowAtlasStabilityTests|FullyQualifiedName~LocalShadowFrustumRelevanceTests|FullyQualifiedName~DirectionalShadowAtlasFallbackTests|FullyQualifiedName~DirectionalCascadeAtlasStaleFrameTests|FullyQualifiedName~CascadedShadowDefaultsAndForwardShaderTests"`.
- Builds: task `Build-Editor`, or `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj` and `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
- Runtime: task `Start-Editor-NoDebug` with the Unit Testing World (`--unit-testing` or `XRE_WORLD_MODE=UnitTesting`), on Vulkan and OpenGL. Scene: one four-cascade directional light, several spot lights, and several point lights.
- `XRE_DIRECTIONAL_SHADOW_AUDIT=1` enables the `[DirectionalShadowAudit]` log lines.
- `XRE_SHADOW_ATLAS_SOLVE_WARN_MS` sets the slow-solve warning threshold.
- `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE=0` disables the Advanced directional shadow lane.
- Profiler scopes: `ShadowAtlasManager.SolveAllocations`, `Lights3DCollection.UpdateShadowAtlasRequests`, `ShadowAtlasManager.PublishFrameData`, `DirectionalCascade.Group.CollectVisible`, `DirectionalCascade.Cascade.CollectVisible`.
- Allocation audit: task `Report-NewAllocations`.

## Checks

### Unit Tests And Builds

- [ ] Shadow unit test suites pass. Procedure: run the Setup filter. Expected: all pass. Last evidence: none.
- [ ] Runtime rendering and editor builds pass. Procedure: Setup builds. Expected: no errors and no new warnings. Last evidence: none.

### Atlas Allocation And Solve

- [ ] Solve baseline. Procedure: Unit Testing World, profiler scopes from Setup. Record steady-state and worst-case ms and per-frame allocated bytes with the scene configuration. Expected: numbers recorded. Last evidence: none.
- [ ] In-budget scene. Procedure: run the in-budget benchmark scene. Expected: steady-state plan build (solve, groups, publish preparation) below 0.5 ms on the collect thread; render thread shows only tile execution scopes. Last evidence: none.
- [ ] Over-budget scene. Procedure: run the over-budget benchmark scene. Expected: solve below 2 ms, and waterline or local-repair demotion is visible in `ShadowAtlasSolveDiagnostics`. Last evidence: none.
- [ ] Zero steady-state allocations. Procedure: thread-allocation tracking plus `Report-NewAllocations`. Expected: zero GC allocations in plan build and execute after warmup. Last evidence: none.
- [ ] Static scene stability. Procedure: static camera and lights after warmup. Expected: `BalancedSolveAttemptCount == 1` and no allocator churn unless settings or content change. Last evidence: none.
- [ ] Directional depth memory. Procedure: compare `ResidentBytes` for directional `Depth` pages. Expected: no color-array bytes, no visual change. Last evidence: none.
- [ ] Oversubscription and stale fallback. Procedure: force one-page pressure with many spot and point lights. Expected: lower-relevance tiles demote or publish visible fallbacks; no stale transformed tiles. Last evidence: none.

### Receiver Relevance

- [ ] Off-screen local lights. Procedure: place static spot and point lights with no visible receivers. Expected: they do not hold full-resolution tiles. Last evidence: none.
- [ ] Off-screen caster. Procedure: view a receiver shadowed by an off-screen caster. Expected: the light tile stays resident. Last evidence: none.
- [ ] Far cascades. Procedure: view a scene where far cascades affect no visible receivers. Expected: far cascades demote or skip independently. Last evidence: none.

### Grouping

- [ ] Directional group. Procedure: one light with four equal cascades on a backend with grouped support. Expected: one page-coherent group rendered in one grouped pass. Last evidence: none.
- [ ] Point-face group. Procedure: one point light with one full-resolution face and five quarter-resolution faces. Expected: deterministic same-page group. Last evidence: none.
- [ ] Repack safety. Procedure: request a repack from editor diagnostics during rendering. Expected: no metadata sampled in the same frame is invalidated. Last evidence: none.

### Directional Cascades And Atlas Performance

- [ ] Atlas on and off baseline. Procedure: Unit Testing World, atlas on and atlas off. Record solve time, tiles rendered per frame, grouped and sequential fallback frames, render-stall logs, and FPS. Expected: numbers recorded. Last evidence: none.
- [ ] Pass count. Procedure: static camera, one four-cascade light, grouped mode supported. Expected: after warmup, no more cascade passes per frame than legacy layered cascades. Last evidence: none.
- [ ] Frame rate. Procedure: same scene, grouped mode supported. Expected: atlas-on frame rate within 10 percent of atlas-off legacy layered cascades. Last evidence: none.
- [ ] Fallback reason. Procedure: run on a configuration without grouped support (for example the Monado OpenXR runtime). Expected: logs state the exact fallback reason and the editor shows the effective mode. Last evidence: none.
- [ ] Sequential versus layered versus grouped. Procedure: profile all three in the same scene. Expected: costs recorded. Last evidence: none.

### Stale Frames And Camera Motion

- [ ] Move and stop. Procedure: audit enabled, move the camera, then stop. Expected: no one-frame jitter; record the frame if one appears. Last evidence: none.
- [ ] Interactive editor. Procedure: F5 editor path, drag the camera and stop. Expected: no visible one-frame shadow snap; cascades are fresh or coherently stale. Last evidence: none.
- [ ] Motion soak. Procedure: camera-motion soak with the audit enabled. Expected: zero pending-plan executions. Last evidence: none.
- [ ] Small moves. Procedure: small camera moves. Expected: stale cascade sampling stays stable. Last evidence: none.
- [ ] Large jump. Procedure: large camera jump. Expected: receiver uses lit, legacy, or a fresh render; no stale depth projected across unrelated regions. Last evidence: none.
- [ ] Visibility passes. Procedure: move the camera with one four-cascade light. Expected: no five expensive visibility or command-recording passes unless diagnostics report why. Last evidence: none.
- [ ] Motion cost. Procedure: CPU and GPU profile dumps for move-and-stop with the Vulkan directional cascade atlas. Measure GPU time, render-thread CPU, frame operations, and dropped frames before and after shadow changes. Expected: frame drops during movement have an attributed and bounded cost. Last evidence: none.
- [ ] Moved lights. Procedure: atlas mode on, move one spot light and one point light in the editor. Expected: shadows refresh; no stale transformed tiles. Last evidence: none.

### Local Lights And Point Seams

- [ ] Spot and point depth atlases. Procedure: deferred and forward, including oversubscription and stale fallback. Expected: correct shadows in both paths. Last evidence: none.
- [ ] Point face seams. Procedure: view filtered point shadows across cube face boundaries. Expected: no seams or clamping artifacts. Last evidence: none.
- [ ] Masked casters. Procedure: scene with alpha-masked casters. Expected: correct cut-outs in atlas shadows. Last evidence: none.

### Moment Encodings

- [ ] Mixed encodings. Procedure: scene with depth, VSM, and EVSM lights in deferred and forward. Expected: correct rendering in both paths. Last evidence: none.
- [ ] Encoding flips. Procedure: change a light encoding at runtime. Expected: no stale or dummy atlas page is sampled. Last evidence: none.
- [ ] Cascade transitions. Procedure: directional moment cascades. Expected: transition bands blend filtered visibility and match per-cascade ground truth. Last evidence: none.
- [ ] Tile gutters. Procedure: spot and point moment atlases (when implemented). Expected: no bleed across tile gutters or face boundaries. Last evidence: none.
- [ ] Directional moment behavior per backend. Procedure: run directional VSM and EVSM on OpenGL and Vulkan. Expected: the behavior matches [Shadow Atlas](../../../architecture/rendering/shadow-atlas.md#encodings-and-filtering). Last evidence: none.

### Temporal Cache

- [ ] Redraw cost. Procedure: measure shadow redraw cost in static-heavy scenes before the cache model decision. Expected: numbers recorded. Last evidence: none.
- [ ] Static-heavy reuse. Procedure: static-heavy scene after warmup. Expected: fewer tile renders with no stale moved-light or moved-caster artifacts. Last evidence: none.
- [ ] Dynamic movers. Procedure: movers near static casters. Expected: updates without a full static tile refresh each frame. Last evidence: none.
- [ ] History rejection. Procedure: camera cuts, tile repacks, light movement, cascade refits. Expected: temporal visibility history rejects correctly. Last evidence: none.

### Contact Shadows

- [ ] Contact-shadow cost. Procedure: visual and profiler captures before and after the helper refactor. Expected: lower cost at equal quality; decide default sample counts from the result. Last evidence: none.

### Diagnostics

- [ ] Editor triage. Procedure: use only editor diagnostics to explain one allocation, one fallback, one grouped-render decision, and one tile refresh. Expected: no source reading needed. Last evidence: none.

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|

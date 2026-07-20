# CPU async-query occlusion during camera motion TODO

Status: critical regression fixed and validated. Stationary, slow-translation, slow-rotation, and return-to-stable Vulkan cases pass; the edge/cut/stereo/hierarchy stress matrix remains open.

Related investigation: [CPU async-query occlusion during camera motion](../../investigations/rendering/cpu-query-camera-motion-2026-07-20.md)

## Objective

Keep meaningful `CpuDirect` + `CpuQueryAsync` occlusion culling active during ordinary continuous camera motion while remaining fail-visible for camera cuts, viewport-edge reveals, large accumulated parallax, invalid projections, missing bounds, and stale or overdue queries.

Yellow debug bounds must continue to mean that the current decision is `Skip` or `ProbeOnly`; do not fake yellow diagnostics from an old negative result when the mesh is actually being drawn.

## Confirmed root causes

- [x] The configured small translation and rotation thresholds were not used. `Stable` required effectively exact camera equality.
- [x] Motion thresholds were per-frame and not render-delta normalized, so lower frame rates promoted the same camera velocity into more aggressive motion tiers.
- [x] Query state did not retain the camera pose or AABB that produced the delayed Boolean result.
- [x] Negative evidence expired after only 6/3/1 frames during small/medium/large motion.
- [x] Vulkan intentionally delays query polling by at least two frames, so a one-frame large-motion lifetime could be stale before resolution.
- [x] Workload expansion applied only to `Stable` and used the visible-demotion budget instead of the recovery budget.
- [x] Probe reveal priority only considered the near plane, not viewport edges or accumulated projected motion.
- [x] A hardware query returns only a Boolean; reprojecting an AABB cannot reconstruct occluder depth or prove future occlusion. AABB reprojection can only reject unsafe temporal reuse.
- [x] The Vulkan frame-wide manifest reserved a minimum of 32 slots for every renderer family. Dense shadow refreshes during camera motion filled the fixed 32 MiB frame-data arena, left query draw preparation `DescriptorsPending`, stopped query progress, and held the scene fail-visible.
- [x] Empty render passes replaced active-view motion and forced-visible telemetry, obscuring which populated pass caused conservative rendering.
- [x] The upside-down 3D view was independent of occlusion: the final pipeline texture was upright, but direct Vulkan window presentation had lost its framebuffer source-Y correction.

## Implementation currently in the working tree

- [x] Added a query-owned unjittered camera snapshot containing pose, projection, view-projection, and near distance.
- [x] Added an unclamped NDC AABB footprint projector with near/camera-plane crossing rejection.
- [x] Added a shared temporal policy that:
  - consumes the existing small translation/rotation settings;
  - scales non-cut motion thresholds against a nominal 60 Hz render delta;
  - keeps camera-cut thresholds absolute;
  - rejects negative-result reuse for viewport-edge risk, excessive center shift, projected growth, projection discontinuity, or invalid clip projection;
  - derives result age from scene size, recovery budget, recovery cadence, and backend minimum latency for every motion tier.
- [x] Attached issuing camera/AABB state to CPU-direct pending and resolved query state.
- [x] Passed current command bounds into coordinator decisions and exact/deferred query submission.
- [x] Validated old negative evidence before mono pending-query skips and before hierarchy shortcuts.
- [x] Added `TemporalReprojectionUnsafe` forced-visible diagnostics.
- [x] Added accepted/rejected temporal-reprojection telemetry to the ImGui CPU Query Health panel.
- [x] Added viewport-edge risk to probe scheduling priority.
- [x] Applied pose-aware result storage, reprojection rejection, and capacity-aware expiry to the OpenGL GPU-dispatch `CpuQueryAsync` path.
- [x] Updated setting descriptions to document nominal-60-Hz motion thresholds.
- [x] Added deterministic temporal-policy tests and a dense continuous-motion coordinator regression test.
- [x] Added allocation-free motion-cause, distance, rotation, projection, validity, and camera-identity diagnostics to per-view telemetry and MCP profiler output.
- [x] Prevented empty passes from publishing active motion/conservative-frame telemetry.
- [x] Reduced the Vulkan per-renderer frame-data reservation floor from 32 slots to 4 and added a 400-renderer dense single-draw regression test.
- [x] Restored source-specific Vulkan Y correction in direct presentation for both default pipelines.
- [x] Rejected and removed a query retry experiment that could bracket no draw when preparation remained pending; the final implementation fixes the allocator root cause and retains strict preflight.

## Files in scope

New runtime types:

- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionCameraSnapshot.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionProjectionFootprint.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionTemporalPolicy.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionTemporalResult.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionMotionClassification.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/ECpuOcclusionMotionCause.cs`

Modified runtime/editor code:

- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuQueryOcclusionContracts.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/OcclusionTelemetry.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs`
- `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
- `XREngine.Editor/IMGUI/EditorImGuiUI.OcclusionPanel.cs`
- `XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.MeshFrameDataReservationManifest.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default2/DefaultRenderPipeline2.CommandChain.cs`

Tests and investigation:

- `XREngine.UnitTests/Rendering/CpuOcclusionTemporalPolicyTests.cs`
- `XREngine.UnitTests/Rendering/CpuRenderOcclusionCoordinatorTests.cs`
- `XREngine.UnitTests/Rendering/VulkanP0ValidationTests.cs`
- `XREngine.UnitTests/Rendering/VulkanUniformBufferGenerationCacheTests.cs`
- `docs/work/investigations/rendering/cpu-query-camera-motion-2026-07-20.md`

## Validation completed

- [x] `dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj --no-restore`
  - Result: succeeded with zero errors.
  - Two existing `CS0649` warnings remain in `VPRC_SurfelGIPass` for `_transformAtlasBuffer` and `_transformAtlasElementCount`; they are unrelated to this work.
- [x] Focused unit-test run.
  - 66 passed, 0 failed, 0 skipped across the temporal policy, CPU coordinator, Vulkan frame-data manifest, and presentation-orientation contracts.
- [x] Editor build after the final fixes.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
  - Result: succeeded with 0 warnings and 0 errors.
- [x] Vulkan live editor validation under `Build/_AgentValidation/20260720-110353-cpu-query-motion/`.
  - Stationary view converged to three culled draws.
  - Six-second translation and five-degree rotation sweeps continued submitting/resolving queries and recovered to three/two culled draws for their respective final views.
  - Recovery age remained bounded at 10 frames for translation and 2 frames for the return rotation.
  - Mesh frame-data stayed at or below 5,230,848 bytes, with zero dynamic-uniform exhaustion and zero Vulkan validation errors.
  - Before/after OS captures prove the restored direct-present Y correction makes both the scene and ImGui upright.
- [x] RenderDoc escalation decision.
  - MCP texture/window captures, profiler telemetry, and logs isolated both defects, so a GPU capture was not needed.
  - `rdc` is not currently on `PATH`; the direct RenderDoc executable is installed and its Vulkan layer should be verified before a future capture.

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanUniformBufferGenerationCacheTests|FullyQualifiedName~CpuRenderOcclusionCoordinatorTests|FullyQualifiedName~CpuOcclusionTemporalPolicyTests|Name=VulkanPresentTextureShaders_ApplySourceOrientationToDirectAndFallbackPaths"
```

## Required follow-up

### 1. Finish deterministic validation

- [x] Run the focused test command above.
- [x] Fix compile failures or changed expectations in existing coordinator tests. In particular, sub-small-threshold motion now intentionally classifies as `Stable`.
- [ ] Verify the NDC footprint tests on the engine's real projection conventions, including reversed-Z Vulkan and OpenGL clip-space variants.
- [ ] Add or confirm tests for:
  - [ ] the exact issuing snapshot transferring from pending to resolved state;
  - [ ] a negative query that is already older than the Vulkan latency floor when it resolves;
  - [ ] pending recovery queries drawing fail-visible when the old proof fails reprojection;
  - [ ] object-bounds motion with a stationary camera;
  - [ ] shared stereo/POV coverage where each physical eye owns independent query-pose evidence;
  - [ ] hierarchy queries under moving cameras and moving child bounds;
  - [ ] zero recovery budget and zero max-query configurations;
  - [ ] query reset, overdue replacement, command-set invalidation, and camera-cut cleanup of temporal fields.
- [x] Run the complete `CpuRenderOcclusionCoordinatorTests` fixture after focused failures are resolved.

### 2. Review policy details before declaring correctness

- [ ] Confirm that the current NDC thresholds are conservative without being so narrow that a dense scene returns to all-visible behavior:
  - viewport edge guard: `0.075` NDC;
  - maximum center shift: `0.12` NDC;
  - maximum extent growth: `0.10` NDC;
  - meaningful extent scale: `1.30`.
- [ ] Decide whether these constants should become engine settings after live evidence. Do not expose them merely for tuning before their semantics are stable.
- [ ] Audit shared stereo aggregation. Current command decisions validate the physical query state before consuming a shared aggregate, but the POV coverage ledger does not yet store a camera snapshot per coverage bit.
- [ ] Audit hierarchy bounds. A hierarchy query retains its submitted union bounds; dynamic children may require rebuilding the current union before hierarchy evidence can be reused.
- [ ] Confirm that positive-result history remains intentionally fail-visible and does not need pose validation.
- [ ] Confirm the OpenGL GPU-dispatch path makes forward scheduling progress for command counts far above the query cap; its traversal-order batching predates this change and may still need ranked/rotating selection.

### 3. Build the editor

- [x] Run:

```powershell
dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore
```

- [x] Resolve all new errors and warnings in touched files.
- [x] Do not modify or discard unrelated working-tree changes, including `Build/Submodules/OscCore-NET9` and `Build/Dependencies/vcpkg/`.

### 4. Validate in the live Unit Testing World

- [x] Create a fresh bounded run root under `Build/_AgentValidation/<timestamp>-cpu-query-motion/`; first prune an old disposable run if there are already ten immediate run folders.
- [x] Build and launch the editor with Unit Testing World and MCP enabled:

```powershell
dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
```

- [x] Confirm the active path is the intended backend/submission combination (`CpuDirect` + `CpuQueryAsync` on Vulkan).
- [ ] Capture and inspect at least these sequences:
  - [x] stationary camera until query state converges;
  - [x] slow continuous lateral translation with repeated query submission/resolution samples;
  - [x] slow continuous rotation;
  - [ ] approach toward an occluded object to exercise projected growth;
  - [ ] an occluded proxy approaching a viewport edge;
  - [ ] a camera cut beyond the configured cut threshold;
  - [x] return to a stable camera and verify recovery convergence.
- [ ] For each sequence, record screenshots and telemetry showing:
  - [x] non-zero `Skip` or `ProbeOnly` decisions during safe continuous motion;
  - yellow debug bounds corresponding to those current decisions;
  - non-zero temporal-reprojection acceptance during safe motion;
  - bounded rejection/forced-visible counts at edge reveals or accumulated parallax;
  - [x] query submission/resolution throughput and latency;
  - [x] no all-visible feedback loop caused solely by result-age expiry.
- [x] Re-capture from multiple camera positions. An artifact that does not move with the view can indicate stale or uninitialized camera state.
- [x] Review `log_rendering.log` and the active backend log for query-policy diagnostics, validation errors, and shutdown-only noise.

### 5. Optional RenderDoc escalation

- [x] Use RenderDoc only if screenshots, telemetry, and logs do not explain a remaining backend-specific failure. They explained both failures, so capture escalation was unnecessary.
- [ ] Before a Vulkan capture, restore/register the RenderDoc implicit Vulkan layer reported missing by `rdc doctor`.
- [ ] Keep `.rdc` files and exported targets under the run root's `renderdoc/` folder and close every replay session.

### 6. Closeout

- [x] Update the related investigation with test output, live screenshot paths, log session paths, threshold decisions, and whether continuous motion actually retained culling.
- [x] Update this TODO's status only after deterministic tests, editor build, and live continuous-motion evidence all pass.
- [x] Review the final diff carefully so unrelated documentation changes are not attributed to this work.

## Acceptance criteria

- [x] A settled camera converges to the same or better culling behavior as before.
- [x] Slow continuous translation and rotation retain meaningful non-zero culling instead of drawing everything.
- [ ] Debug yellow bounds remain visible for current `Skip`/`ProbeOnly` decisions during safe motion.
- [ ] A camera cut, projection discontinuity, viewport-edge reveal, near-plane crossing, invalid projection, or excessive accumulated parallax is fail-visible.
- [x] Result lifetimes never expire before the backend can resolve them and scale with the recovery budget needed to sweep the scene.
- [x] Query recovery remains bounded if a refresh never resolves.
- [x] No per-frame heap allocations are introduced in render submission, result resolution, reprojection, or probe ranking.
- [x] OpenGL and Vulkan code builds succeed with no new warnings.
- [x] Focused unit tests and live Unit Testing World validation pass for the stationary/translation/rotation matrix.
- [x] Direct Vulkan presentation and the later ImGui overlay are both upright.

## Design caveat

A zero-sample hardware query is a Boolean observation from its issuing frame. The current implementation reprojects the proxy AABB to decide when that observation is no longer trustworthy; it does not reproject occluder depth. If stronger guarantees are required, the correct follow-up is a depth-history/Hi-Z path with previous/current view-projection matrices and explicit disocclusion handling, not increasingly optimistic reuse of a Boolean query.

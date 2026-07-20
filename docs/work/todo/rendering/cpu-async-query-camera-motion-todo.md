# CPU async-query occlusion during camera motion TODO

Status: paused with an implementation in the working tree; runtime rendering compiles, but focused tests and live editor validation are incomplete.

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

## Files in scope

New runtime types:

- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionCameraSnapshot.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionProjectionFootprint.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionTemporalPolicy.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuOcclusionTemporalResult.cs`

Modified runtime/editor code:

- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/CpuQueryOcclusionContracts.cs`
- `XREngine.Runtime.Rendering/Rendering/Occlusion/OcclusionTelemetry.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs`
- `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
- `XREngine.Editor/IMGUI/EditorImGuiUI.OcclusionPanel.cs`

Tests and investigation:

- `XREngine.UnitTests/Rendering/CpuOcclusionTemporalPolicyTests.cs`
- `XREngine.UnitTests/Rendering/CpuRenderOcclusionCoordinatorTests.cs`
- `docs/work/investigations/rendering/cpu-query-camera-motion-2026-07-20.md`

## Validation completed

- [x] `dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj --no-restore`
  - Result: succeeded with zero errors.
  - Two existing `CS0649` warnings remain in `VPRC_SurfelGIPass` for `_transformAtlasBuffer` and `_transformAtlasElementCount`; they are unrelated to this work.
- [x] `rdc doctor`
  - RenderDoc 1.44 and replay support were found.
  - Vulkan capture is not currently ready because the RenderDoc Vulkan implicit layer is not registered.
- [ ] Focused unit-test run.
  - The command below was started but aborted before returning a test result when this work was paused.

```powershell
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~CpuOcclusionTemporalPolicyTests|FullyQualifiedName~CpuRenderOcclusionCoordinatorTests"
```

## Required follow-up

### 1. Finish deterministic validation

- [ ] Run the focused test command above.
- [ ] Fix compile failures or changed expectations in existing coordinator tests. In particular, sub-small-threshold motion now intentionally classifies as `Stable`.
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
- [ ] Run the complete `CpuRenderOcclusionCoordinatorTests` fixture after focused failures are resolved.

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

- [ ] Run:

```powershell
dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore
```

- [ ] Resolve all new errors and warnings in touched files.
- [ ] Do not modify or discard unrelated working-tree changes, including the untracked `Build/Submodules/Flyleaf/` directory and unrelated documentation edits.

### 4. Validate in the live Unit Testing World

- [ ] Create a fresh bounded run root under `Build/_AgentValidation/<timestamp>-cpu-query-motion/`; first prune an old disposable run if there are already ten immediate run folders.
- [ ] Build and launch the editor with Unit Testing World and MCP enabled:

```powershell
dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
```

- [ ] Confirm the active path is the intended backend/submission combination (`CpuDirect` + `CpuQueryAsync`, including Vulkan if that is the target).
- [ ] Capture and inspect at least these sequences:
  - [ ] stationary camera until query state converges;
  - [ ] slow continuous lateral translation for at least two full recovery-budget sweeps;
  - [ ] slow continuous rotation;
  - [ ] approach toward an occluded object to exercise projected growth;
  - [ ] an occluded proxy approaching a viewport edge;
  - [ ] a camera cut beyond the configured cut threshold;
  - [ ] return to a stable camera and verify recovery convergence.
- [ ] For each sequence, record screenshots and telemetry showing:
  - non-zero `Skip` or `ProbeOnly` decisions during safe continuous motion;
  - yellow debug bounds corresponding to those current decisions;
  - non-zero temporal-reprojection acceptance during safe motion;
  - bounded rejection/forced-visible counts at edge reveals or accumulated parallax;
  - query submission/resolution throughput and latency;
  - no all-visible feedback loop caused solely by result-age expiry.
- [ ] Re-capture from multiple camera positions. An artifact that does not move with the view can indicate stale or uninitialized camera state.
- [ ] Review `log_rendering.log` and the active backend log for query-policy diagnostics, validation errors, and shutdown-only noise.

### 5. Optional RenderDoc escalation

- [ ] Use RenderDoc only if screenshots, telemetry, and logs do not explain a remaining backend-specific failure.
- [ ] Before a Vulkan capture, restore/register the RenderDoc implicit Vulkan layer reported missing by `rdc doctor`.
- [ ] Keep `.rdc` files and exported targets under the run root's `renderdoc/` folder and close every replay session.

### 6. Closeout

- [ ] Update the related investigation with test output, live screenshot paths, log session paths, threshold decisions, and whether continuous motion actually retained culling.
- [ ] Update this TODO's status only after deterministic tests, editor build, and live continuous-motion evidence all pass.
- [ ] Review the final diff carefully so unrelated documentation changes are not attributed to this work.

## Acceptance criteria

- [ ] A settled camera converges to the same or better culling behavior as before.
- [ ] Slow continuous translation and rotation retain meaningful non-zero culling instead of drawing everything.
- [ ] Debug yellow bounds remain visible for current `Skip`/`ProbeOnly` decisions during safe motion.
- [ ] A camera cut, projection discontinuity, viewport-edge reveal, near-plane crossing, invalid projection, or excessive accumulated parallax is fail-visible.
- [ ] Result lifetimes never expire before the backend can resolve them and scale with the recovery budget needed to sweep the scene.
- [ ] Query recovery remains bounded if a refresh never resolves.
- [ ] No per-frame heap allocations are introduced in render submission, result resolution, reprojection, or probe ranking.
- [ ] OpenGL and Vulkan builds succeed with no new warnings.
- [ ] Focused unit tests and live Unit Testing World validation pass.

## Design caveat

A zero-sample hardware query is a Boolean observation from its issuing frame. The current implementation reprojects the proxy AABB to decide when that observation is no longer trustworthy; it does not reproject occluder depth. If stronger guarantees are required, the correct follow-up is a depth-history/Hi-Z path with previous/current view-projection matrices and explicit disocclusion handling, not increasingly optimistic reuse of a Boolean query.

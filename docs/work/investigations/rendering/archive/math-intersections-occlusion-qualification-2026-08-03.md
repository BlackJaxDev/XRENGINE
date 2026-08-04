# Math Intersections Occlusion Qualification

Last updated: 2026-08-03
Owner: Rendering
Status: Test harness complete; CPU modes pass; GPU target not implemented

## Problem

The Math Intersections Unit Testing World had no comparable interactive rigs
for the three intended occlusion paths. Its root component could toggle test
nodes, but users had to select each child node separately to reach that test's
custom controls and diagnostics. The GPU path was also described as two-phase
even though it currently performs only one Hi-Z refinement.

## Changes

- Added one shared occluder/target scene for CPU asynchronous queries, CPU
  software rasterization, and GPU Hi-Z with `GpuIndirectZeroReadback`.
- Added live configuration, status, per-frame culling, GPU-BVH, zero-readback,
  and Hi-Z phase diagnostics to each rig.
- Made root-UI activation of occlusion rigs mutually exclusive and responsible
  for capturing, applying, reconciling, and restoring global renderer settings.
- Added a generic collapsible custom-UI group that projects a test node's exact
  field objects into the root component. Editing either location changes the
  same state.
- Corrected GPU Hi-Z telemetry from `two-phase-*` to `single-phase-*` and
  records zero phase-two draws until the persistent two-phase renderer exists.
- Connected live `EngineSettings.ForceMeshSubmissionStrategy` changes to the
  effective settings resolver and render-pipeline command-chain rebuild. Before
  this correction, the root toggle changed the setting but the live renderer
  stayed on `CpuDirect`.

## Live Results

Validation used the named isolated editor session
`math-occlusion-modes-20260803` with the Vulkan backend.

| Rig | Effective configuration | Result |
|---|---|---|
| CPU async query | `CpuQueryAsync + CpuDirect` | PASS: final sample tested 15 commands and culled 8. |
| CPU rasterized | `CpuSoftwareOcclusion + CpuDirect` | PASS: final sample tested 30 bounds and culled 24; an earlier sample rasterized 1 occluder and culled 26. |
| GPU qualification | `GpuHiZ + GpuIndirectZeroReadback` | FAIL as designed: GPU BVH ready, zero-readback submissions active, but mode was `single-phase-current-depth` with zero phase-two draws. |

The final GPU sample reported 18 logical primitives, 35 logical BVH nodes, and
15 zero-readback submissions. Root component inspection returned all 54 fields,
including the three new toggles and their conditional projected property groups.

## Visual Evidence

All captures are under
`Build/_AgentValidation/20260722-102900-gpu-bvh-query-parity/occlusion-mode-qualification-20260803/mcp-captures/`.

- CPU query:
  `Screenshot_20260803_111449_000_e77f7142c7304d00bbea375991a96b99.png`
  shows the wall and visible/revealed sentinels while hidden targets are absent.
- CPU raster:
  `Screenshot_20260803_105904_850_48c9e123123d44108717a5b782a2e093.png`
  shows the wall and two visible sentinels while the hidden field is rejected.
- GPU camera positions:
  `Screenshot_20260803_111200_212_89357370f80341d782e36b4d20f5054f.png`
  and
  `Screenshot_20260803_112027_009_faee93108b84491b8ba5bd6c9bdbcfb6.png`.
  The grid/gizmo changes between angles, but scene output is blank. These
  captures prove the screenshot source changed; they are not accepted as
  occlusion correctness evidence because of the shader failure below.

## Log Review

The final flushed logs are under
`Build/_AgentValidation/mcp-sessions/math-occlusion-modes-20260803/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-03_11-18-59_pid49588/`.

- `log_rendering.log` records the live transition from `CpuDirect` to
  `GpuIndirectZeroReadback`, with GPU dispatch and strategy-driven GPU BVH both
  active.
- `log_vulkan.log` contains no observed Vulkan `VUID` validation error for this
  run, but the newly compiled GPU pipeline variant fails to compile
  `DeferredLightingDir`. The rewritten GLSL has bare trailing annotation text
  at line 2090 (`enabled, page, fallback, record index`) after auto-uniform
  processing. The light program consequently remains unavailable and the
  blank GPU frame cannot be attributed solely to occlusion.
- Periodic `BvhRaycastDispatcher` warnings concern the separate Vulkan
  raycast/readback feature and are not GPU scene-culling fallback evidence.

`rdc doctor` passed all checks. A RenderDoc capture was not needed for this
iteration because the runtime phase counters directly identify the absent
phase-two work, while the shader compiler diagnostics identify why the final
lit viewport is not a valid visual oracle.

## Remaining Work

1. Implement persistent per-view GPU visibility, phase-one indirect draws,
   current-depth pyramid generation, and phase-two disocclusion draws as tracked
   in the GPU-driven occlusion architecture TODO.
2. Integrate Hi-Z rejection into GPU-BVH node traversal rather than using the
   BVH only for frustum acceleration.
3. Fix the Vulkan auto-uniform rewrite of `DeferredLightingDir` annotations,
   then repeat the two-angle visual qualification and capture a RenderDoc frame
   once the two-phase implementation exists.

## User Confirmation

Pending.


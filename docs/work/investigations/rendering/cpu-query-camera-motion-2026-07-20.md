# CPU async-query occlusion during camera motion

## Problem

`CpuDirect` + `CpuQueryAsync` culls after the camera settles, but sustained camera motion makes nearly every command draw. The yellow debug bounds disappear at the same time because yellow represents a current `Skip`/`ProbeOnly` decision, not merely an old zero-sample query result.

## Root causes

- The configured small translation/rotation thresholds were not consumed. Only effectively exact pose equality was classified as `Stable`.
- Motion thresholds were applied per rendered frame without render-delta normalization, so the same camera speed changed policy when frame rate changed.
- A query stored no issuing camera pose or queried bounds. A delayed Boolean result was therefore reused or rejected using only its frame age.
- Negative-result maximum ages collapsed to 6/3/1 frames during small/medium/large motion. Vulkan deliberately waits at least two frames before polling, and a dense scene cannot refresh every command inside those windows.
- Workload expansion applied only to `Stable` and used the visible-demotion budget even though already-occluded commands consume the recovery budget.
- The probe priority's reveal-risk term only covered near-plane intersections; it did not account for viewport-edge entry, accumulated parallax, or projected growth.

## Implemented design

- Capture an unjittered camera snapshot and world AABB on every submitted query, then transfer that exact state to the resolved result.
- Reproject the queried and current AABBs into normalized device coordinates. A zero-sample result remains reusable only while screen-center shift, projected growth, and viewport-edge risk remain bounded. A Boolean query does not contain occluder depth, so this is deliberately a rejection guard rather than a claim of full depth reprojection.
- Classify motion with translation and rotation, consume the existing small thresholds, and scale non-cut thresholds against a nominal 60 Hz render delta. Camera-cut thresholds remain absolute.
- Derive result lifetime from the recovery-query budget, scene command count, retest cadence, and backend minimum latency for every motion tier.
- Keep temporal-reprojection accepted/rejected counters visible in CPU query health diagnostics.
- Apply the same pose-aware result contract to the OpenGL GPU-dispatch `CpuQueryAsync` path.

## Validation ledger

- [ ] Focused CPU occlusion coordinator and temporal-policy tests.
- [ ] Editor project build with no new warnings.
- [ ] Stationary Unit Testing World evidence still reaches stable culling.
- [ ] Continuous camera translation evidence retains non-zero `Skip`/`ProbeOnly` decisions and yellow bounds.
- [ ] Camera cut and viewport-edge reveal evidence remain fail-visible.
- [ ] Rendering logs reviewed for query latency, forced-visible reasons, and backend validation errors.

## RenderDoc note

`rdc doctor` found RenderDoc 1.44 and replay support, but reported that the Vulkan implicit layer is not registered. A GPU capture is only required if viewport evidence and query telemetry cannot identify the remaining behavior; the source defect itself is CPU temporal-policy state.

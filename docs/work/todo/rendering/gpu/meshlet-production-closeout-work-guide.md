# Meshlet Production Closeout Work Guide

Last Updated: 2026-08-21
Owner: Assets / Rendering / Vulkan
Status: Active; Gate 1 complete; Gate 2 implementation is in progress and pending live Vulkan validation
Parent tracker:
[Meshlet import cooking and production readiness](meshlet-import-cooking-and-production-readiness-todo.md)
Evidence log:
[Meshlet import production closeout](../../../investigations/rendering/meshlet-import-production-closeout-2026-08-20.md)

## Purpose

This is the short execution guide for finishing the meshlet work without
reopening already-proven areas or drifting into adjacent renderer projects. The
parent tracker remains the requirements authority; this guide defines the order
of work, acceptance evidence, and stop conditions.

Check a box here and in the parent tracker only after its named runtime evidence
exists. A build, counter, screenshot, or RenderDoc event alone is insufficient
when the acceptance row requires more than one of them.

## Current Truth — Do Not Re-Debug Without Contradictory Evidence

The following are already proven on the RTX 4070 laptop and are not the next
work:

- Cold import generates and persists meshlets before rendering. Full Sponza
  produced 393 payloads containing 12,707 meshlets.
- Valid standalone warm loads avoid source parsing and native meshlet building.
- GPUScene accepts immutable validated payloads through an O(1) revision token;
  rendering performs no meshlet cooking, source hashing, or disk access.
- Static opaque work reaches real Vulkan EXT task/mesh submission. RenderDoc
  capture `fresh-static-meshlet-frame40.rdc`, EID 514, proves
  `vkCmdDrawMeshTasksIndirectCountEXT`, task/mesh/pixel stages, and resident
  meshlet, atlas, transform, material, indirect/count, and attachment bindings.
- The production path binds static/dynamic/skinned vertex atlases by tier and
  filters task records by the same tier.
- Static meshlets coexist exactly once with explicitly traditional skinned,
  morph, and unsupported-pass work in the validated mixed fixture.
- Generic GPU readback bytes, mapped buffers, forbidden fallback events, render-
  path source hash/disk/cooker calls, and Vulkan validation VUIDs are zero in the
  accepted production runs.
- Mesh-task Hi-Z is intentionally disabled because its current center-only test
  is not conservative. Traditional GPU Hi-Z remains enabled.
- Parallel command-chain worker recording is intentionally quarantined. Serial-
  owner command-chain recording remains enabled and stable.
- The experimental 10 Hz cap was reverted and must not be restored as a meshlet
  fix.

## Closeout Board

| Order | Gate | State | Parent requirements |
| --- | --- | --- | --- |
| 1 | Production per-meshlet debug colors | **Complete — 2026-08-21** | Phase 9 Sponza debug-color row |
| 2 | Conservative mesh-task Hi-Z | **In progress — Vulkan mip-layout fix pending live proof** | Phase 9 Hi-Z row |
| 3 | Sponza three-view visual parity | Open | Phase 8 view gate; Phase 9 framebuffer comparison; resident-stream visual gate |
| 4 | Missing/material/cache/lifetime matrices | Open | Success criteria; Phases 5, 6, 8, and 9; resident-stream no-drop gate |
| 5 | Parallel command-worker device-loss root cause | Open, quarantined | Phase 9 worker row |
| 6 | Shipping-profile performance and mouse-pressure characterization | Open | Phase 9 performance/machine rows |
| 7 | Tests, documentation closeout, and resident-stream handoff | Blocked on Gates 1–6 and user clearance | Phase 9 tests and resume gate |

Broad model/prefab binary-cache hydration is a conditional external dependency,
not the current meshlet implementation lane. Do not build the missing prefab-
graph/mesh-core provider from this guide. When that provider becomes active,
run the model-cache hydration row and update both trackers.

## Scope And Safety Rules

- Work one gate at a time. Do not start the next gate while the current gate has
  an unexplained visual, validation, lifetime, or accounting failure.
- Use short named MCP sessions for Sponza and stop each session immediately after
  its capture. Never stop an editor session that this work did not start.
- Keep Sponza uncapped. Do not use a low render-Hz cap to mask GPU pressure or
  mouse jitter.
- Keep `CommandChainWorkerRecordingQuarantined = true` until Gate 5 passes. Do
  not flip the constant merely to see whether the crash still happens.
- Do not use the legacy direct meshlet overlay as production evidence. The
  accepted production material-table task/mesh path must produce the image.
- Do not introduce CPU readback, CPU fallback, render-time cooking, source
  hashing, or disk access to make a validation pass.
- Do not add or run new tests until the live integration works and the user
  explicitly clears test work, per repository policy.
- Preserve all unrelated worktree changes and the unrelated submodule/vcpkg
  directories.

## Gate 1 — Production Per-Meshlet Debug Colors

Goal: make the accepted production meshlet path show deterministic, visibly
different colors on neighboring Sponza meshlets. Uniform magenta is a failure.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`
- `Build/CommonAssets/Shaders/Meshlets/MeshletRender.mesh`
- `Build/CommonAssets/Shaders/Meshlets/MeshletRenderExt.mesh`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderMeshletDebugDisplay.cs`
- `XREngine.Runtime.Rendering/Rendering/Camera/GpuBvhDebugSettings.cs`

Execution:

1. Start a short warm Sponza MCP session with production meshlet submission,
   task Hi-Z still safely disabled, debug display enabled, and a camera close
   enough that Sponza fills a meaningful part of the viewport.
2. Capture the viewport and `AlbedoOpacity` before changing code.
3. Follow the debug value end to end:
   `EnableMeshletDebugDisplay` camera state -> program uniform -> varying
   `meshletIndex` -> `XRE_MeshletDebugColor` -> mesh output location 12 ->
   generated fragment input `FragMeshletDebugColor` -> `AlbedoOpacity`.
4. If source inspection is ambiguous, capture the production event in RenderDoc.
   Inspect the uniform, mesh/fragment reflection and constants, a pixel history,
   and the render target immediately after the mesh-task event. Export every
   inspected target to PNG and view it.
5. Fix the first broken boundary. Do not route back through the legacy overlay.
6. Re-capture two frames and two nearby camera views to prove color stability.

Acceptance:

- [x] At least several adjacent Sponza meshlets have clearly different colors in
  both the production G-buffer output and final viewport.
- [x] Colors are deterministic across consecutive frames and a warm restart.
- [x] The frame contains accepted Vulkan EXT indirect-count mesh-task work; the
  legacy direct overlay does not run.
- [x] Requested/consumed accounting is exact; VUIDs, generic readback/maps,
  forbidden fallbacks, and render-path cooking/hash/disk counters remain zero.
- [x] Before/after images and the exact session/settings/commit are recorded in
  the evidence log, then the parent debug-color box is checked.

Accepted 2026-08-21 on commit `a3e4fd4b35abd52cfaf8be67883752c3bc1d9d50`
plus the recorded dirty closeout changes. The root cause was an absolute-scale
cutoff in `HasUniformPositiveScale`: it reused the `1e-4` relative scale
tolerance as a squared-axis degeneracy threshold, so Sponza's valid small
uniform import scale marked every opaque row `Dynamic`. Separating the
degeneracy and relative tolerances made all 393 opaque commands and all 12,707
cooked meshlets eligible.

The bounded Diagnostics sample at camera `(-20.08, 0.055, 0.0)` looking at
`(-19.80, 0.055, 0.0)` emitted 5,754 task records with nonzero delayed dispatch
groups and zero overflow/VUIDs. The accepted DevParity restart reported
`requested=3960`, `emitted=3960`, `consumed=3960`, with zero culled draws,
overflow, CPU/forbidden fallback, render-path source hash/disk/cooker calls,
maps/readbacks, descriptor failures, and dropped frame operations. Frame-op
telemetry contained one Vulkan EXT indirect-count mesh-task operation.

Visual evidence is under
`Build/_AgentValidation/20260821-104532-meshlet-closeout/mcp-captures/`:

- `Screenshot_20260821_124345_824_3dd0da54a7c4446b8485d225a8e16fb4.png`
  and `RenderPipeline_AlbedoOpacity_20260821_124346.png` are the first accepted
  close-camera viewport/G-buffer pair.
- `RenderPipeline_AlbedoOpacity_20260821_124409.png` is a consecutive-frame
  repeat with the same stable meshlet palette. Its whole-image hash differs
  because temporal jitter moves coverage edges; the interior color assignment
  is visually unchanged.
- `Screenshot_20260821_124428_833_7c6b4abe46fc4a1889dede3eaedbecbd.png`
  and `RenderPipeline_AlbedoOpacity_20260821_124429.png` prove a nearby camera
  view changes normally while retaining distinct meshlet colors.
- `Screenshot_20260821_124600_275_eadb0762475041f89990c037797b79a4.png`
  and `RenderPipeline_AlbedoOpacity_20260821_124601.png` reproduce the original
  palette after a warm DevParity restart.

Two bounded Sponza RenderDoc attempts were cleaned up without a capture: the
first queued an already-past absolute frame, and the immediate-trigger retry
could not complete one RenderDoc-instrumented Sponza frame within 120 seconds.
The launcher now supports `--trigger`; both injected PIDs were terminated by the
launcher's `finally`, ports 5471/5472 were closed, and no partial `.rdc` remains.
The already-accepted static RenderDoc EXT event proof remains the stage/binding
evidence; the live Sponza frame-op and visual evidence above close this gate.

## Gate 2 — Conservative Mesh-Task Hi-Z

Goal: re-enable mesh-task Hi-Z without permitting any false-negative culling.

Primary files:

- `Build/CommonAssets/Shaders/Meshlets/MeshletCulling.task`
- `Build/CommonAssets/Shaders/Meshlets/MeshletCullingExt.task`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`

Required invariants:

- Project a conservative screen-space footprint for the complete world-space
  meshlet sphere; a center sample is never sufficient.
- Compare a conservative nearest/farthest sphere depth range against the Hi-Z
  reduction convention, including reversed-Z.
- Choose a mip and sample coverage that can prove the whole footprint occluded.
  Uncertain, clipped, near-plane-crossing, out-of-range, stale, or unavailable
  cases remain visible.
- Sequential stereo/multiview stays disabled unless every active view is handled
  conservatively.
- Frustum and cone culling behavior remains unchanged.

Acceptance:

- [ ] A controlled fully occluded fixture produces nonzero task Hi-Z culls.
- [ ] A partially visible/near-plane fixture is never falsely culled.
- [ ] Hi-Z off/on captures at the same camera show identical visible geometry at
  three views, including Sponza's edge and doorway silhouettes.
- [ ] Normal and reversed-Z paths, active mip bounds, and stereo fallback are
  evidenced; VUIDs and no-fallback/no-readback gates remain clean.
- [ ] The hardcoded task-Hi-Z disable is removed only after all rows pass.

### Gate 2 checkpoint — 2026-08-21 wrap-up

Implemented but **not yet accepted**:

- Both task-shader variants now project all eight corners of the complete
  world-space meshlet sphere AABB, honor Vulkan/OpenGL clip-depth and framebuffer
  Y conventions, choose a bounded conservative mip, and leave uncertain,
  clipped, near-plane, out-of-range, and unsupported multiview cases visible.
- The compute two-pass Hi-Z path uses the same clip/depth policy and temporal
  phase-one visibility rule. Vulkan now exposes exact one-mip storage-image
  descriptor views, records sampled-image layout transitions before compute,
  and preserves the ordered producer/copy relationship for diagnostic evidence.
- Expansion-counter races and the stale diagnostic-copy ordering defect are
  fixed. The targeted Vulkan project built with zero warnings and zero errors.

Current evidence and unvalidated fix:

- The controlled two-grid fixture is
  `Build/_AgentValidation/20260821-104532-meshlet-closeout/scratch/hiz-vertical-fixture.jsonc`;
  its environment is `scratch/hiz-vertical-env.json`. The correct camera is
  `(-1.25, 1.25, 0)` with identity rotation, looking along engine `-Z`.
- A moving camera rendered the expected colored front grid, but the same camera
  after settling rendered black. Ordered GPU evidence found the Hi-Z pyramid
  populated through mip 5 and zero from mip 6 onward. The selected footprint
  then sampled depth zero and falsely rejected both draws.
- Session `meshlet-closeout-hiz-trace` proved every reduction was present with
  the correct immutable state: source mips `0..8`, destination mips `1..9`, and
  sizes `480x270` through `1x1`. It was stopped cleanly. Its Vulkan log is under
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260821-165515-meshlet-closeout-hiz-trace/logs/`.
- The remaining root defect was narrowed to a sampled/storage image alias:
  transitioning the full sampled mip view while one destination mip was
  `General` produced a mixed-range lookup failure, then incorrectly used
  `Undefined` as the old layout for the whole pyramid. The current code changes
  transition aliased compute images one mip/layer at a time so completed source
  mips are preserved. This change compiles but has **not** had a post-fix live
  run; it must be treated as a hypothesis until the checklist below passes.
- All temporary shader counter probes and the temporary per-dispatch trace were
  removed before this checkpoint. The post-cleanup Vulkan project build passed
  with zero warnings/errors; direct GLSL syntax checks passed for both compute
  shaders and both task variants (the EXT task shader requires SPIR-V 1.4).
  No editor or RenderDoc session remains open.

Resume here, in this exact order:

1. Rebuild the isolated editor after the probe removal and start a new short
   named session with the two files above. Do not use a render-Hz cap.
2. At the identity camera, wait for temporal motion to settle. Capture the
   viewport plus `AlbedoOpacity` and depth. The front grid must remain visible,
   the rear grid must produce a nonzero task Hi-Z rejection, and mips `0..9`
   must contain valid conservative data.
3. Move the camera, settle it again, and prove the image recovers instead of
   becoming black or stale. Require zero VUIDs, descriptor failures, forbidden
   fallback, synchronous maps, and generic readback bytes.
4. Add the partially visible/near-plane fixture, then normal-Z, reversed-Z, and
   stereo/multiview fallback runs. Record exact counters and screenshots.
5. Only after the controlled matrix passes, compare Hi-Z off/on at the same
   three useful Sponza views, including doorway and edge silhouettes. Then and
   only then check the five Gate 2 boxes here and the matching parent row.

Do **not** start Gate 3 while any stable-camera black frame, zero pyramid tail,
false-negative cull, VUID, or accounting mismatch remains unexplained.

## Gate 3 — Sponza Three-View Visual Parity

Goal: prove useful-camera production output, not merely dispatch or bindings.

Execution:

1. Use normal `GpuHiZ`, production `GpuMeshletZeroReadback`, and a useful close
   Sponza framing. Keep the scene scale/transform identical between variants.
2. Choose and record three fixed camera poses that expose different occlusion
   and silhouette cases.
3. At each pose, capture production meshlet and traditional zero-readback
   references with debug display off. Capture final viewport plus
   `AlbedoOpacity`, normal, depth, and any suspicious intermediate.
4. Repeat the production captures with debug display on to retain the Gate 1
   colored-meshlet proof.
5. Capture at least one final production frame with RenderDoc; use an open-work-
   close session and export/view the relevant targets.

Acceptance:

- [ ] All three production views contain the same Sponza geometry as their
  traditional references, with no missing, duplicated, stale, or falsely culled
  regions.
- [ ] Debug-off output is material/final-frame equivalent within explained
  renderer-path differences; debug-on output has distinct meshlet colors.
- [ ] Camera movement changes the image normally and no stale render target is
  being sampled.
- [ ] RenderDoc proves the final accepted mesh-task event and its output reaches
  the inspected final frame.
- [ ] The parent Phase 8, Phase 9, and resident-stream visual boxes are checked
  only after the comparison artifacts are linked.

## Gate 4 — Remaining Exact-Once, Cache, And Lifetime Matrices

Use small deterministic fixtures for these rows; do not use full Sponza where a
smaller fixture gives stronger attribution.

### 4A — Mixed visibility and routing

- [ ] Missing-payload static work remains visible through explicit traditional
  zero-readback routing.
- [ ] Masked and override draws coexist exactly once with eligible meshlet work.
- [ ] Transparent/OIT work remains explicitly traditional and visible exactly
  once.
- [ ] Streaming arrival/removal does not temporarily drop or duplicate a draw.
- [ ] For every run, requested draws equal consumed draws and each downgrade has
  one stable actionable reason.

### 4B — Remaining cache-state rows

- [ ] Explicit `Disabled` and `Empty` payload states survive cold/warm loading
  without builder loops or missing geometry.
- [ ] Changed cooker provenance obeys the portable runtime-compatibility policy.
- [ ] A corrupt optional meshlet section repairs from valid cached core data when
  policy permits and never opens the source parser only for repair.
- [ ] Read-only repair remains in memory and reports the inability to republish.

The animated-source parse in the mixed warm probe is a broad cache-closure gap,
not a regression in standalone static warm hydration. Keep that distinction in
the evidence.

### 4C — Live range lifetime

- [ ] Reimport and hot reload publish the new payload generation atomically at a
  frame boundary.
- [ ] Streaming unload/reload and payload replacement retire old GPUScene ranges
  only after their last fence and reclaim/reuse them without stale draws.
- [ ] Repeated churn and capacity growth/overflow remain bounded, exact once, and
  free of leaks, device loss, and silent task truncation.
- [ ] Stereo/multiview and LOD transition scenarios show no missing, duplicated,
  or stale geometry from three camera positions.

## Gate 5 — Parallel Command-Worker Device Loss

This is a Vulkan recording/lifetime gate discovered during meshlet acceptance.
Investigate it only after Gates 1–4 pass so it cannot obscure meshlet
correctness.

Primary file:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainWorkers.cs`

Execution matrix:

- isolate graphics-only and non-graphics-only worker recording;
- exercise one-worker ownership, then two/four-worker concurrency;
- separate forced rerecord from clean secondary reuse;
- trace command-pool/buffer ownership, reset, retirement, worker generation,
  batch completion, and queue submission lifetime;
- use validation/synchronization diagnostics and retain the first device-loss
  record rather than treating shutdown fallout as the cause; and
- compare every worker run with the current serial-owner control.

Acceptance:

- [ ] The root lifetime/ownership defect is identified and fixed rather than
  hidden by retry, delay, or broad device-idle waits.
- [ ] Graphics and non-graphics worker operations are both observed in submitted
  frames with no VUID, timeout, device loss, or use-after-retire behavior.
- [ ] Repeated worker runs cross the previous deterministic loss frames and the
  serial control's validated duration with clean reuse and forced rerecord.
- [ ] The quarantine constant is removed only after the full matrix passes; if it
  cannot pass, keep serial ownership and move the worker optimization to its own
  explicitly open Vulkan tracker rather than claiming it is enabled.

## Gate 6 — Production Performance And Mouse Pressure

Run this after correctness so diagnostics do not distort the baseline.

- [ ] Measure a `ShippingFast` uncapped Sponza run with task Hi-Z enabled and a
  matching traditional zero-readback reference.
- [ ] Record GPU command-buffer/frame percentiles, task groups, cull counts,
  buffer residency, churn, managed allocations, and zero-readback/fallback
  counters.
- [ ] Reproduce the reported system-wide mouse jitter while collecting GPU queue
  saturation evidence. Do not reintroduce a render-Hz cap; classify the actual
  GPU scheduling/present/queue cause or record it as a separate renderer issue.
- [ ] Record this laptop separately from later original-laptop/desktop evidence;
  do not combine unmatched hardware runs.

## Gate 7 — Tests, Documentation, And Handoff

- [ ] After all live gates pass, ask the user for explicit clearance to add/run
  new tests.
- [ ] After clearance, add deterministic coverage for cache states, payload
  validation, mixed routing, lifetime, Vulkan capability, conservative Hi-Z,
  and visual/debug-color contracts where automation is meaningful.
- [ ] Run the narrow tests, final Release build, `git diff --check`, and one final
  uncapped Vulkan smoke run.
- [ ] Update the parent tracker, evidence log, model-cache tracker, rendering
  roadmap, mesh-submission contract, and resident-stream tracker with exact
  evidence links.
- [ ] Mark the meshlet tracker complete only when every unconditional success
  criterion and resident-stream resume gate is satisfied. Keep conditional
  external rows explicitly labeled rather than silently checking them.
- [ ] Resume Vulkan resident draw-stream Phase 1 only after its gate is complete.

## Standard Run Protocol

### Start a bounded evidence root

```powershell
pwsh Tools/Limit-AgentValidation.ps1 -ReserveTaskRun
$MeshletRunRoot = "Build/_AgentValidation/$(Get-Date -Format yyyyMMdd-HHmmss)-meshlet-closeout"
New-Item -ItemType Directory -Force `
  "$MeshletRunRoot/mcp-captures", `
  "$MeshletRunRoot/mcp-output", `
  "$MeshletRunRoot/logs", `
  "$MeshletRunRoot/reports", `
  "$MeshletRunRoot/renderdoc", `
  "$MeshletRunRoot/scratch" | Out-Null
```

Record the exact commit, settings file, cache root/mode, GPU/driver, camera poses,
and session name in the evidence log before the first code change.

### Live visual loop

1. Build only when code changed.
2. Start one unique named MCP editor session.
3. Set an immediate fixed camera pose with `set_editor_camera_view`.
4. Capture the viewport and relevant pipeline textures into
   `$MeshletRunRoot/mcp-captures/` and actually view the PNGs.
5. Stop the named session immediately.
6. Inspect that session's Vulkan/rendering logs and update the evidence log.
7. Change one variable and repeat.

### Measurement baseline

Use the existing harness with explicit settings and cache roots. Keep worker
recording disabled/quarantined until Gate 5 and do not pass a refresh-rate cap.

```powershell
pwsh Tools/Measure-GameLoopRenderPipeline.ps1 `
  -Strategies GpuMeshletZeroReadback `
  -Configuration Release `
  -RenderBackend Vulkan `
  -UnitTestingWorldSettingsPath <settings.jsonc> `
  -CacheMode <Cold|Warm> `
  -MeshletStandaloneCookedCacheRoot <cache-root> `
  -ZeroReadbackMaterialDrawPath MaterialTable `
  -VulkanGpuDrivenProfile DevParity `
  -VulkanCommandChains Enabled `
  -VulkanParallelCommandChainRecording Disabled `
  -VulkanParallelSecondaryRecording Disabled `
  -OcclusionCullingMode GpuHiZ `
  -CameraPositionX <x> -CameraPositionY <y> -CameraPositionZ <z> `
  -CameraLookAtX <x> -CameraLookAtY <y> -CameraLookAtZ <z> `
  -OutputDirectory "$MeshletRunRoot/reports/<run-name>" `
  -RunLabel <run-name>
```

Use `Diagnostics` only for a bounded counter question. Final performance evidence
must use `ShippingFast` with no diagnostics-induced mappings/readbacks.

### RenderDoc visual proof

The repository launcher avoids Windows quoting/environment ambiguity:

```powershell
rdc doctor
python Tools/RenderDoc/capture_xrengine.py `
  --settings <settings.jsonc> `
  --run-root "$MeshletRunRoot" `
  --output "$MeshletRunRoot/renderdoc/meshlet-frame.rdc" `
  --frame 900 `
  --strategy GpuMeshletZeroReadback `
  --material-path MaterialTable `
  --mcp-port <isolated-port> `
  --camera-position <x> <y> <z> `
  --camera-look-at <x> <y> <z>

rdc open "$MeshletRunRoot/renderdoc/meshlet-frame.rdc"
rdc info --json
rdc passes
rdc draws --limit 40
rdc bindings <EID> --json
rdc shader <EID> ps --constants --json
rdc rt <EID> -o "$MeshletRunRoot/renderdoc/meshlet-event.png"
rdc close
```

For the magenta-color issue, also use pixel history/pixel debugging on a visible
Sponza pixel and export the event target before and after the suspicious pass.
Always view exported PNGs; a successful command is not visual proof.

## Evidence Required For Every Completed Gate

- exact commit and dirty-worktree state;
- settings, cache state, strategy/profile, camera pose, GPU/driver, and session;
- report/capture/log paths under the bounded run root;
- requested/consumed, readback/map/fallback, render-path prohibited-work, task,
  culling, validation, allocation, and lifetime counters relevant to the gate;
- inspected viewport/pipeline/RenderDoc PNGs for every visual claim;
- the identified root cause and why the fix preserves the production contract;
- Release build and whitespace result after the final code change; and
- synchronized checkbox/status updates in this guide, the parent tracker, and
  the evidence log.

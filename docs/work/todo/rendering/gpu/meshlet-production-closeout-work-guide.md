# Meshlet Production Closeout Work Guide

Last Updated: 2026-08-22
Owner: Assets / Rendering / Vulkan
Status: **Completed — 2026-08-22; Gates 1–7 accepted and resident draw-stream Phase 1 unblocked**
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

Gates 1–2 and the earlier import evidence were proven on the RTX 4070 laptop.
Gates 3–7 add separately recorded RTX 3090 desktop evidence; the hardware
results are not aggregated. These areas are not the next work:

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
- Mesh-task Hi-Z uses a conservative full-footprint/depth-range test and is
  enabled for supported mono views. Uncertain, clipped, near-plane, stale, and
  sequential-stereo/multiview cases remain visible; traditional GPU Hi-Z is
  unchanged.
- Three useful Sponza views now match traditional zero-readback geometry and
  material output. The final-frame differences are limited to at most six
  pixels above one LSB, and RenderDoc follows the accepted EXT mesh-task output
  through the G-buffer and composition chain to the presented image.
- Missing-payload, masked, local-material-override, transparent/OIT, payload
  replacement, hot reload, and LOD-transition work has exact-once routing and a
  bounded generation-safe GPUScene lifetime. The remaining broad model-cache
  provider is still a separate conditional dependency.
- Switching from `CpuDirect` to `GpuMeshletZeroReadback` no longer blacks out
  the scene. The production material row now honors the 16-word `std430` array
  stride instead of packing the 13 logical words into a misaligned CPU row.
- Parallel graphics and non-graphics command-chain worker recording is enabled.
  The 16-cell forced-rerecord/clean-reuse matrix passed for serial and 1/2/4
  worker configurations after the primary-recording and retirement ownership
  defects were fixed.
- The experimental 10 Hz cap was reverted and must not be restored as a meshlet
  fix. Uncapped desktop evidence classifies the reported mouse pressure as GPU
  execution/queue saturation in the mesh-task path, not submit/present CPU time.

## Closeout Board

| Order | Gate | State | Parent requirements |
| --- | --- | --- | --- |
| 1 | Production per-meshlet debug colors | **Complete — 2026-08-21** | Phase 9 Sponza debug-color row |
| 2 | Conservative mesh-task Hi-Z | **Complete — 2026-08-22** | Phase 9 Hi-Z row |
| 3 | Sponza three-view visual parity | **Complete — 2026-08-22** | Phase 8 view gate; Phase 9 framebuffer comparison; resident-stream visual gate |
| 4 | Missing/material/cache/lifetime matrices | **Complete — 2026-08-22** | Success criteria; Phases 5, 6, 8, and 9; resident-stream no-drop gate |
| 5 | Parallel command-worker device-loss root cause | **Complete — 2026-08-22** | Phase 9 worker row |
| 6 | Shipping-profile performance and mouse-pressure characterization | **Complete — 2026-08-22** | Phase 9 performance/machine rows |
| 7 | Tests, documentation closeout, and resident-stream handoff | **Complete — 2026-08-22** | Phase 9 tests and resume gate |

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
- Do not restore the removed command-worker quarantine or silently fall back to
  serial ownership without new contradictory lifetime/submission evidence.
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

- [x] A controlled fully occluded fixture produces nonzero task Hi-Z culls.
- [x] A partially visible/near-plane fixture is never falsely culled.
- [x] Hi-Z off/on captures at the same camera show identical visible geometry at
  three views, including Sponza's edge and doorway silhouettes.
- [x] Normal and reversed-Z paths, active mip bounds, and stereo fallback are
  evidenced; VUIDs and no-fallback/no-readback gates remain clean.
- [x] The hardcoded task-Hi-Z disable is removed only after all rows pass.

### Gate 2 accepted — 2026-08-22

The sampled/storage alias fix was live-validated after the 2026-08-21
checkpoint. Both task-shader variants now project the complete sphere footprint,
use the conservative depth endpoint for the active depth convention, and leave
every unsupported or uncertain case visible. The hardcoded task-Hi-Z suppression
and all temporary GPU counter/trace probes have been removed.

Controlled evidence is under
`Build/_AgentValidation/20260821-180447-meshlet-gate2/mcp-captures/`:

- `taskhiz-full-on` produced 97 records and 25 task-Hi-Z culls. Hi-Z on/off
  `AlbedoOpacity` and `DepthView` float hashes match (`48D6...499BF` and
  `0CB0...9830`).
- `taskhiz-partial-on/off` retained the partially visible geometry with eight
  conservative culls and matching hashes (`D8C3...A08E` and `334B...D4E1`).
- `taskhiz-near-oblique-on/off` retained the near-plane/oblique geometry with
  one conservative cull and matching hashes (`5036...70FF` and
  `1D46...EE02`).
- The reversed-Z run retained the same albedo, produced the expected different
  depth encoding (`61C954...0393`), and still reported 25 conservative culls.
  Inspected active mip bounds, including tail widths `30, 15, 7, 3, 1`, held
  valid finite data.

The fixed Sponza fixture used Vulkan, production GPU meshlet dispatch, ImGui,
the flying editor camera, no locomotion, and no mirror capture. Hi-Z on/off
`DepthView` float hashes are exactly equal at all three fixed views:

- center/doorway, `(-20.08,0.055,0)` toward `(-19.80,0.055,0)`:
  `2E145B3FC5FA89144AA560B2B416F3ABBF6F82B664C27714DC83CE8B46068596`;
- near edge, `(-20.08,0.055,-0.10)` toward `(-19.80,0.055,0.08)`:
  `4D47433993AD795100A176F9AB55478939C044CCD190FF07C63887A7BEF82019`;
- level oblique, `(-20.10,0.060,0.12)` toward `(-19.78,0.060,-0.06)`:
  `BA716F48A724051EB4A322DC5E7D30EECDEE11860C767BEF502DAF9FBC629419`.

OpenXR/Monado supplied the stereo fallback evidence without OpenVR or a
headset. Rotating the playspace `+167.5617` degrees canceled the Monado headset
yaw and aimed both views at the fixture. The actual left/right
`AlbedoOpacity` attachments are distinct, visible stereo views with hashes
`C5DCE157AE588266796263DCAB53521BBD0E7D2217BF6BCE2E58FCC26668AA7A`
and `059E10D1B3D50AB1D9715B3E1A24F1652229DBD474E11C9C56DA110FE47A127D`.
Stereo eye-bypass counters advanced independently while CPU fallback,
forbidden fallback, generic readback, descriptor failures, and VUIDs stayed
zero. The separate OpenXR preview-copy textures were still near-black; that
presentation-copy defect is not represented as an eye-render pass.

After removing all temporary probes, the isolated editor build passed with zero
warnings and zero errors. A final uncapped `ShippingFast` Sponza run reproduced
the center-view depth hash above with requested/effective
`GpuMeshletZeroReadback`, zero generic and diagnostic readback bytes, zero
mapped bytes, zero CPU/forbidden/descriptor fallbacks, zero skipped draws or
dispatches, zero dropped operations, and zero validation messages/VUIDs.

Sponza's debug image intentionally mixes routes: 22 of 25 opaque commands are
meshlet eligible, while three state-class rejections remain on the traditional
material-table path. The apparent camera-dependent interchange between meshlet
colors and material/material-ID colors is different geometry becoming visible,
not one surface changing mode: two stationary captures and a move-away/return
capture at the same pose all produced the identical `AlbedoOpacity` hash
`84010A5D88BC7060EABDA9C1D9D269BFDD3D268F6D9750B3F45557EEE426E7A2`.

These captures closed Gate 2's Hi-Z on/off visibility requirement only. Gate 3
subsequently supplied the separate debug-off traditional comparison, final-
frame material equivalence, and RenderDoc attribution. At this checkpoint no
tests had been added or run; the user later supplied the required clearance and
Gate 7 records the resulting suite.

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

- [x] All three production views contain the same Sponza geometry as their
  traditional references, with no missing, duplicated, stale, or falsely culled
  regions.
- [x] Debug-off output is material/final-frame equivalent within explained
  renderer-path differences; debug-on output has distinct meshlet colors.
- [x] Camera movement changes the image normally and no stale render target is
  being sampled.
- [x] RenderDoc proves the final accepted mesh-task event and its output reaches
  the inspected final frame.
- [x] The parent Phase 8, Phase 9, and resident-stream visual boxes are checked
  only after the comparison artifacts are linked.

### Gate 3 accepted — 2026-08-22

The accepted root is
`Build/_AgentValidation/20260822-023044-meshlet-gates3-4-switch/`; the compact
matrix is `reports/gate3-three-view-parity.json`. The center, edge, and oblique
production views used the same fixed poses as Gate 2 and were compared against
traditional GPU zero-readback with debug display off. `AlbedoOpacity` matched
the corresponding traditional capture exactly. The final comparisons had only
2–6 pixels above one LSB, with mean absolute error `0.013492`–`0.025650`; the
normal/depth differences were confined to small raster boundaries. Debug-on
hashes differed at all three poses, proving that camera motion produced fresh
per-meshlet output.

Sponza exposed a real capacity defect during this gate: 10,836 resident eligible
meshlets exceeded the fixed 8,192 task-record buffer, so the atomic expansion
batch rolled back and routed the pass traditionally. Capacity now derives from
the resident meshlet population and grew to 25,002 records for this fixture.
The accepted production frame reported 54 requested/emitted/consumed draws,
zero overflow, CPU/forbidden fallback, map, or readback bytes, and no validation
or dropped-work diagnostics.

`renderdoc/gate3-production-accepted_frame1245.rdc` contains 197 events. EID 139
is `vkCmdDrawMeshTasksIndirectCountEXT` with indirect arguments `<9620,1,1>`;
the exported color/depth attachments were inspected and the resource chain was
followed through lighting, composition, post-processing, and swapchain event
561 before `rdc close`.

## Gate 4 — Remaining Exact-Once, Cache, And Lifetime Matrices

Use small deterministic fixtures for these rows; do not use full Sponza where a
smaller fixture gives stronger attribution.

### 4A — Mixed visibility and routing

- [x] Missing-payload static work remains visible through explicit traditional
  zero-readback routing.
- [x] Masked and override draws coexist exactly once with eligible meshlet work.
- [x] Transparent/OIT work remains explicitly traditional and visible exactly
  once.
- [x] Streaming arrival/removal does not temporarily drop or duplicate a draw.
- [x] For every run, requested draws equal consumed draws and each downgrade has
  one stable actionable reason.

### 4B — Remaining cache-state rows

- [x] Explicit `Disabled` and `Empty` payload states survive cold/warm loading
  without builder loops or missing geometry.
- [x] Changed cooker provenance obeys the portable runtime-compatibility policy.
- [x] A corrupt optional meshlet section repairs from valid cached core data when
  policy permits and never opens the source parser only for repair.
- [x] Read-only repair remains in memory and reports the inability to republish.

The animated-source parse in the mixed warm probe is a broad cache-closure gap,
not a regression in standalone static warm hydration. Keep that distinction in
the evidence.

### 4C — Live range lifetime

- [x] Reimport and hot reload publish the new payload generation atomically at a
  frame boundary.
- [x] Streaming unload/reload and payload replacement retire old GPUScene ranges
  only after their last fence and reclaim/reuse them without stale draws.
- [x] Repeated churn and capacity growth/overflow remain bounded, exact once, and
  free of leaks, device loss, and silent task truncation.
- [x] Stereo/multiview and LOD transition scenarios show no missing, duplicated,
  or stale geometry from three camera positions.

### Gate 4 accepted — 2026-08-22

The deterministic mixed fixture contained eligible opaque meshlet work beside
missing-payload, masked, opaque-forward, transparent/OIT, and explicit local
material-override commands. Its baseline was 42 requested/consumed draws. Each
class was removed and restored independently; the expected totals
(`18`, `30`, or `36`) returned to 42 with the exact baseline albedo hash, while
the frame trace showed only eligible opaque-deferred rows excluded from the
traditional scatter. Stable state-class and missing-range counters explain
every traditional route. Overflow, CPU/forbidden fallback, map, readback, and
Vulkan validation counters stayed zero.

`reports/gate4-cache-state-matrix.json` closes the optional meshlet-section
matrix: `Disabled`/`Empty` round-trip cold and warm, changed cooker provenance
remains runtime-compatible while being locally stale, corrupt optional data
repairs from cached core without opening the source parser, and read-only repair
is retained with the exact non-republish warning. This does not claim the still-
inactive broad prefab/model binary-cache provider.

Cooked `XRMesh.Reload` now replaces only an owner-validated payload. GPUScene
coalesces resident payload changes and publishes them at the command-buffer swap
boundary, then retires the preceding atlas generation by fence. Eight repeated
remove/reload cycles alternated 464 and 19,200 live bytes, advanced rebuild/
retire counts to 22/21, and settled at zero retired bytes every time. Near and
oblique views selected LOD1 with 26 eligible meshlets; the far view selected
LOD2 with nine; returning selected LOD1 and reproduced the exact initial
`AlbedoOpacity` hash `D0FF2D...B9B0`. Each pose was 14 requested/consumed with
zero overflow, fallback, map/readback, VUID, or dropped operation. Gate 2's
OpenXR/Monado left/right attachment proof supplies the corresponding stereo/
multiview safety evidence; OpenVR was not used. The combined runtime summary is
`reports/gate4-runtime-lifetime-matrix.json`.

After the final atomic-reload hardening, isolated Release session
`meshlet-gate4-final` reloaded the resident base cooked mesh with identical
before/after albedo hash `5D726C...D10757`, 14 requested/consumed draws, 51
eligible meshlets, 38,144 live bytes, and zero settled retired bytes, overflow,
fallback, readback/map, render-path cooker, validation, or dropped-operation
counters.

## Gate 5 — Parallel Command-Worker Device Loss

This is a Vulkan recording/lifetime gate discovered during meshlet acceptance.
Investigate it only after Gates 1–4 pass so it cannot obscure meshlet
correctness.

Primary files:

- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainWorkers.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.DesktopOutputArtifacts.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Allocation/VulkanRenderer.CommandPool.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiFontAtlasResources.cs`

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

- [x] The root lifetime/ownership defect is identified and fixed rather than
  hidden by retry, delay, or broad device-idle waits.
- [x] Graphics and non-graphics worker operations are both observed in submitted
  frames with no VUID, timeout, device loss, or use-after-retire behavior.
- [x] Repeated worker runs cross the previous deterministic loss frames and the
  serial control's validated duration with clean reuse and forced rerecord.
- [x] The quarantine constant is removed only after the full matrix passes; if it
  cannot pass, keep serial ownership and move the worker optimization to its own
  explicitly open Vulkan tracker rather than claiming it is enabled.

Accepted 2026-08-22. The numeric `FrameOp` migration had retained non-graphics
worker recordings but stopped executing their planned secondary ranges from the
primary command buffer. Teardown also allowed cached worker artifacts to outlive
their desktop-output/command-pool owner, and the ImGui font-atlas descriptor
pool/layout bypassed lifetime authority and remained eligible for a second
destroy. The fixes restore typed-primary execution of planned non-graphics
ranges, scope descriptor-generation capture to the active batch, clean failed
worker recordings, cancel workers and destroy caches before owner teardown, and
retire ImGui descriptor resources through Vulkan lifetime authority. No retry,
delay, broad `DeviceWaitIdle`, or CPU fallback was introduced.

`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate5-postfix-matrix.csv`
contains all 16 graphics/non-graphics × forced/clean × 0/1/2/4-worker cells.
Every cell ended with zero worker failure, timeout, validation error, device
loss, readback, or fault-log match. Forced runs observed actual 1/2/4-worker
concurrency; serial controls recorded zero workers. Clean runs retained worker,
chain, and primary reuse through frames 15,015–15,473. The raw readiness flag in
three graphics clean cells is false only because the first worker recording
predated profiler activation; their retained reuse and fault-free duration are
the acceptance evidence. The quarantine constant and stale quarantine branch
were removed only after this matrix passed.

## Gate 6 — Production Performance And Mouse Pressure

Run this after correctness so diagnostics do not distort the baseline.

- [x] Measure a `ShippingFast` uncapped Sponza run with task Hi-Z enabled and a
  matching traditional zero-readback reference.
- [x] Record GPU command-buffer/frame percentiles, task groups, cull counts,
  buffer residency, churn, managed allocations, and zero-readback/fallback
  counters.
- [x] Reproduce the reported system-wide mouse jitter while collecting GPU queue
  saturation evidence. Do not reintroduce a render-Hz cap; classify the actual
  GPU scheduling/present/queue cause or record it as a separate renderer issue.
- [x] Record each laptop/desktop independently; do not combine unmatched hardware
  runs.

Accepted as a production characterization on 2026-08-22. The authoritative
uncapped 25-second-warmup/60-second-capture pair is
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-shipping-fast-final100/summary.json`.
Both variants used the same warm standalone Sponza cache, fixed camera,
`ShippingFast`, Vulkan, `GpuHiZ`, `MaterialTable`, ImGui, flying camera, desktop
output, no locomotion, no mirror, and no render-rate cap.

The meshlet path measured render p50/p95/p99 of
`11.589/13.649/16.103 ms`, Vulkan GPU command-buffer
`11.464/15.311/16.250 ms`, Vulkan frame `10.057/12.052 ms`, and frame-slot wait
`4.102/5.196 ms`. The matching traditional zero-readback reference measured
render `6.829/7.686/8.129 ms`, GPU command-buffer `3.322/4.606/6.849 ms`, Vulkan
frame `5.359/6.001 ms`, and frame-slot wait `0.019/0.026 ms`. Submit/present p95
remained small (`0.240/0.070 ms` meshlet and `0.190/0.050 ms` traditional), so
the pressure is GPU execution/queue saturation rather than CPU submit/present
blocking. The retained-sample allocation totals were 27,388,536 command-record
bytes plus 2,733,168 GPU-submission managed bytes for meshlets and 42,030,040
plus 4,652,280 bytes for traditional. The existing
`binding_snapshot_ineligible` legacy-uniform route was observed 204 and 348
times respectively and remains visible as an optimization follow-up.

The synchronized NVIDIA monitor
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-gpu-saturation-final100.csv`
recorded meshlet utilization at
97.3% mean / 98% p95 / 98% max versus 59.63% / 72% / 83% for traditional. This
matches the user's reproduced system-wide mouse-jitter report under uncapped
Sponza and the multi-millisecond frame-slot waits. No physical cursor telemetry
was synthesized; the symptom is user-observed and the renderer-side cause is
the measured GPU queue saturation. The failed 10 Hz experiment remains reverted.

The separate intrusive `Diagnostics` supplement is
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-task-cull-diagnostics/summary.json`:
12,500 task groups/records,
zero frustum culls, 570 cone culls, 361 Hi-Z culls, 6,554,112 resident/live
meshlet bytes, zero retired bytes, and zero rebuilds/retires during capture.
Generic readback bytes, mapped buffers, CPU/forbidden fallback, and VUIDs were
zero; 56,928 explicitly classified fence-delayed diagnostic bytes are not part
of the ShippingFast baseline. The corrected ShippingFast production assertion
then exited cleanly in
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate6-shippingfast-harness-validation/`
using
two recorded mesh-task frame operations per frame with generic readback still
zero. RenderDoc event 139 independently records a real
`vkCmdDrawMeshTasksIndirectCountEXT(<9620,1,1>)` call and clean bindings/output.

This run is scoped only to the current desktop: RTX 3090 (driver 610.88,
24,576 MiB, 420 W limit), Ryzen 9 7950X3D (16C/32T), 48 GiB RAM, Windows 11 Pro
build 26200, and 2560×1440 at 144 Hz. It is not combined with the prior RTX 4070
laptop or any later original-laptop result. The measured meshlet path is
materially slower than traditional on this machine; Gate 6 closes the requested
measurement and cause classification, not that separate optimization gap.

## Gate 7 — Tests, Documentation, And Handoff

- [x] After all live gates pass, ask the user for explicit clearance to add/run
  new tests.
- [x] After clearance, add deterministic coverage for cache states, payload
  validation, mixed routing, lifetime, Vulkan capability, conservative Hi-Z,
  and visual/debug-color contracts where automation is meaningful.
- [x] Run the narrow tests, final Release build, `git diff --check`, and one final
  uncapped Vulkan smoke run.
- [x] Update the parent tracker, evidence log, model-cache tracker, rendering
  roadmap, mesh-submission contract, and resident-stream tracker with exact
  evidence links.
- [x] Mark the meshlet tracker complete only when every unconditional success
  criterion and resident-stream resume gate is satisfied. Keep conditional
  external rows explicitly labeled rather than silently checking them.
- [x] Release the Vulkan resident draw-stream Phase 1 hold only after this gate
  is complete; Phase 1 implementation remains owned by its dedicated tracker.

Accepted 2026-08-22 after the user's explicit test/closeout clearance. The
focused Release suite passed 86/86 tests in
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate7-tests/gate7-final-targeted.trx`.
It covers explicit `Disabled`/`Empty` cache states, bounded payload rejection,
small/invalid transform eligibility, frame-boundary payload replacement,
mixed zero-readback routing, capability downgrade/selection, conservative NV/
EXT Hi-Z contracts, stable meshlet/material debug colors, command-worker
lifetime, primary/non-graphics recording order, and no-readback hardening.

The test pass exposed and fixed one production defect rather than documenting
around it: dense meshlet-buffer compaction copied only the referenced triangle
bytes and dropped a payload's required four-byte terminal padding. Compaction
now preserves the aligned portable payload range; the replacement generation
test proves the old range remains published until the command-buffer boundary
and the complete new range appears atomically afterward.

The final uncapped warm Sponza smoke is
`Build/_AgentValidation/20260822-122200-meshlet-gates5-6/reports/gate7-final-smoke/summary.json`.
It ran Vulkan `ShippingFast`, `GpuMeshletZeroReadback`, `GpuHiZ`, material-table
shading, desktop flying camera, parallel command-chain recording, no mirror,
no locomotion, and no refresh cap. Across 131 retained samples it recorded two
production meshlet frames/two Vulkan mesh-task frame operations, exact
7,074 requested/consumed Vulkan draws (10,638/10,638 across all Vulkan paths),
zero generic readback bytes, mappings, CPU or forbidden fallbacks, VUIDs, and
capture-window meshlet buffer rebuilds/retires. Render p50/p95 was
11.112/12.356 ms. The optional post-run GPU-timing-history dump reported no
history because ShippingFast timing capture was disabled; the editor shut down
cleanly, the harness exited zero, and the runtime/counter evidence above was
written normally.

The final Release editor build completed with zero warnings/errors. The final
whitespace audit is clean. All unconditional meshlet closeout criteria are now
satisfied. Broad model/prefab binary-cache hydration remains explicitly
conditional on its separate provider and is not represented as completed here;
the resident draw-stream Phase 1 hold is released.

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

Use the existing harness with explicit settings and cache roots. Keep the Gate
5-accepted worker configuration and do not pass a refresh-rate cap.

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

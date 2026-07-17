# Vulkan CPU-Query And Monado Regression Investigation

Date: 2026-07-14
Status: Reopened 2026-07-16 for stable-packet inline-query primary mismatch
Related TODO: [Vulkan core hardening and device-loss TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)

## Problem

Immediately after the Phase 5.2.4b closeout work, three symptoms remained in the
current worktree:

- desktop Vulkan throughput was poor;
- `CpuQueryAsync` appeared not to cull the main desktop viewport; and
- Monado eye images repeatedly flickered between rendered content and black.

The investigation used current-binary desktop and Monado runs rather than
assuming the previous 300-frame acceptance cohort still represented the
worktree.

## Findings

### Desktop CPU-query culling was stale on reusable primaries

The clean-primary reuse path refreshed ordinary reusable frame data but did not
refresh query frame operations. A reused primary could therefore preserve an
old query epoch and visibility decision set. The reuse path now prepares query
operations for the new epoch before accepting the cached primary.

A desktop-only current-binary run then reported 46-76 tested meshes, 32-52
culled meshes, and 14-38 rendered meshes while continuing to submit and resolve
queries with a bounded pending set. In the same scene, the diagnostic disabled
mode rendered all 393 meshes. The observed p50 frame time was approximately
25 ms with `CpuQueryAsync`, versus 92.3 ms with culling disabled. This proves the
main desktop viewport is doing useful current-view culling; it does not yet
prove sustained query convergence under the much heavier independent-desktop
plus OpenXR workload.

### Black eye flicker was rejected work, not compositor presentation

The black frames correlated with Vulkan submission rejection. Three resources
were captured from an earlier output plan and later referenced by the OpenXR
eye command buffer:

1. Generated `DummyShadowMapArray` upload recreated and retired its own
   compatible dedicated image immediately after allocation.
2. `HBAOPlusBlurIntermediateTexture` in a descriptor snapshot still referred
   to the desktop physical plan.
3. `DepthStencil` framebuffer targets still referred to the desktop physical
   plan after the eye plan became active.

Compatible full-texture uploads now preserve their existing dedicated Vulkan
image. During external-target preparation, named pipeline textures and
framebuffer targets are rebased through the active output resource registry
after the output-specific plan has been published. Material-owned textures are
not rebased.

The final short Monado run recorded zero rejected queue submissions, zero
`ErrorValidationFailed` results, eight successful strict-SPS submissions, and
no sequential fallback. All five retained frames contained projection layers.
The only harness failure was the independent output ledger exceeding its
16-entry diagnostic capacity.

### Remaining low frame rate is CPU/output orchestration

The GPU workload alone does not explain the stalls. The final smoke reported
roughly 1.8-22.8 ms of GPU work while retained total-frame time ranged from
18 ms to 9.8 seconds. `FullIndependentRender` requested both a 300x170 desktop
preview and a 1537x865 main desktop render in addition to the 896x1007x2 true
SPS eye render. The output-request ledger grew from 10 to 17 entries per frame.

During the worst retained frames, tracked descriptor sets grew as high as
26,664 and live resource records as high as 42,868. A CPU profile found render
dispatch p50 near 119 ms, intermittent 500-800 ms stalls, and command-recording
allocation commonly in the multi-megabyte range, with a representative value
near 20 MB. Hot scopes included pipeline lookup/creation, descriptor binding,
pass transitions, and submit-fence waits.

An experiment that enabled one cached secondary command chain per draw reduced
some GPU time but expanded to 2,451 command buffers and 5,144 live resources,
then timed out. That design was rejected. The current safeguard keeps query
brackets inline, skips external CpuQueryAsync command-chain construction, and
records schedules larger than 64 chains inline. A bounded grouped-secondary
arena is still required.

## Implemented Changes

- Refresh query epochs before accepting a clean reusable primary command
  buffer.
- Keep query brackets inline while allowing unrelated bounded command chains.
- Give command-chain keys structural identity that is stable when the visible
  subset changes, and back off when recording outpaces reuse.
- Cap the current one-pool/one-buffer-per-chain schedule at 64 chains.
- Do not construct an OpenXR command-chain schedule when `CpuQueryAsync` must
  record inline.
- Preserve a compatible dedicated image during generated full-texture upload.
- Rebase external-target pipeline-resource descriptors and framebuffer targets
  to the active output plan before wrapper refresh and recording.

## Evidence

| Run | Result |
|---|---|
| Desktop current-binary query run, engine session `xrengine_2026-07-14_10-58-00_pid17376` | Nonzero tested/culled/rendered work, bounded query progress, live screenshots visually correct |
| `Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/monado-safe-bounded/` | Dummy shadow fix held; exposed stale HBAO descriptor resource |
| `Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/monado-rebased-plan/` | HBAO rejection removed; exposed stale `DepthStencil` framebuffer target |
| `Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/monado-target-rebase/` | Zero submission rejections, eight strict-SPS successes, retained projection layers; output-ledger capacity was the sole smoke failure |
| `Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/monado-cpu-profile/` | CPU allocation, descriptor, pipeline, and submission hotspots captured |

Desktop screenshots inspected during the query run are under the current task's
`mcp-captures/` folder as `Screenshot_20260714_110032.png` and
`Screenshot_20260714_110104.png`. The final Monado summary is
`monado-target-rebase/reports/openxr-smoke-summary.json`; its filtered Vulkan
log is `monado-target-rebase/logs/engine/log_vulkan.log`.

## 2026-07-15 Exact-Draw And Every-Third-Eye Follow-Up

### Exact visible-query recording

The prior reusable-primary refresh fixed stale query epochs but did not make the
visible demotion probe exact: a deferred proxy could still test a different draw
shape than the mesh whose visibility decision it governed. Visible probes are
now reserved within the bounded query budget during collection and bracket the
exact contributing mesh draw during command recording. Hidden recovery probes
remain deferred, query pools are reset before the first render operation, and
motion-vector replay does not emit a second occlusion query for the same draw.

The associated frame-data work also replaced independent command-stream
manifests with one frame-wide root manifest. It gives compatible output families
stable disjoint ranges, publishes power-of-two capacity blocks, and carries late
families to the next frame boundary instead of growing a sealed generation.
Focused query/arena/command-chain tests passed 128/128 before the final SPS
change. The final strict-SPS, Phase 5.2.4, and command-chain subset passed
102/102 after adding the external-image regression guard.

An eight-frame exact-visible-query probe passed lifetime, validation, query-age,
known-visible-sentinel, and bounded-churn gates. Its enabled/off final-image
parity did not pass. Stage-by-stage inspection found identical albedo, normal,
material/RMSE, and ambient-occlusion captures; the first difference was
`DefaultPipelineSps_05_LightingAccum_layer0.png`. Independent occlusion-off
cohorts also alternate between bright and dark lighting. The remaining parity
failure is therefore a directional-light/shadow nondeterminism exposed by the
gate, not evidence that exact-draw queries changed the G-buffer. Raw evidence is
under
`Build/_AgentValidation/20260710-openxr-strict-stereo/phase524c-final-current/inline-exact-visible-query-probe/`.

### Why one eye frame failed every third render

The later user report correlated with this exception in engine session
`xrengine_2026-07-15_00-53-34_pid35420`:

```text
InvalidOperationException: Command buffer attempted to record retired Vulkan resource
Image:0x1F4E46860D0 generation 776
owner=resource-planner:... Depth24Stencil8 ... DepthStencil
```

The rejection occurred in `EndCommandBufferTracked` after
`TryRecordStereoLayerBlitCommandBuffer`. Tracing proved the strict-SPS source was
the healthy dedicated `OpenXRVulkanStereoColorArray`; the rejected handle was a
previously destroyed desktop depth image. Monado rotates three images per eye,
and the Vulkan driver reused that raw numerical handle for one runtime-owned
swapchain image. This accounts for the user's every-third cadence.

The direct-render path creates per-eye image views, and view registration also
registers their externally owned backing images. Strict SPS instead publishes by
transfer and does not create those views, so the raw destination images had not
entered lifetime tracking. When the recycled handle appeared, command-buffer
dependency tracking still found the destroyed engine generation.

`TryPrepareStereoLayerBlit` now registers both raw destination images with
`externallyOwned: true` before recording the transfer. Registration creates a
fresh generation only for completed/destroyed ownership; reuse while an engine
retirement is genuinely pending still fails loudly. A source-contract regression
test requires both registrations to precede source lookup and recording.

The post-fix untraced combined probe is under
`Build/_AgentValidation/20260710-openxr-strict-stereo/phase524c-final-current/external-swapchain-lifetime-fix-probe/`.
Its smoke runner exited 0 after 106 submitted frames and 106 successful strict-SPS
submissions. Each eye recorded 106 acquire, publish, and release operations;
there were zero end-frame failures, warnings, summary failures, filtered Vulkan
matches, retired-resource exceptions, or sequential fallbacks. All six acquired
eye captures (three motion samples per eye) and the desktop final were visually
inspected and were nonblack. The validator itself reports failure only because
this diagnostic retained eight rather than exactly 300 frames and intentionally
omitted the strict-failure and occlusion-off companion reports.

### Current performance interpretation

The fixed combined sync-validation/capture run measured CPU frame p95 at
146.90 ms and GPU p95 at 24.86 ms. Nearby runs without the extra draw trace or
capture overhead measured GPU p95 near 4.8-5.5 ms while CPU p95 remained near
138-149 ms. The low desktop and combined framerate is still primarily CPU/output
orchestration; rendering two OpenXR eyes, the independent desktop, and the pickup
preview together is the intended workload and must not be reduced to make the
number look better.

## Gate And Next Work

Do not treat the current worktree as ready for Phase 5.2.5 merely because the
every-third lifetime rejection is fixed. First close the Phase 5.2.4b/5.2.4c
regression hold with a longer current-binary run that proves:

- zero retired-resource or validation submission rejection;
- sustained independent desktop and SPS query submission/resolution/culling;
- bounded output records, descriptors, resources, and command buffers; and
- visually continuous desktop and both-eye images.

Before enabled/off image parity can be promotion evidence, fix or isolate the
directional-light/shadow instability that first appears in `LightingAccum`.
Then run the exact occlusion-off companion and 300-retained-frame enabled cohort,
plus the settled F5 resource/descriptor plateau baseline. The current eight-frame
probe is a targeted exception regression, not a substitute for those gates.

The remaining throughput root cause belongs to the already planned Phase 5.2.5
through 5.2.7 architecture: persistent versioned output arenas, one immutable
scene snapshot grouped into compatible view families, and deadline-aware output
scheduling. In particular, grouped secondary buffers must replace the rejected
per-draw pool/buffer model, and duplicate output telemetry/render requests must
be separated and deduplicated before capacity checks.

## 2026-07-15 Final Closeout Attempt

The remaining 5.2.4 work is being resumed against the current worktree. The
acceptance target is the CPU-direct Vulkan lane with Monado, true single-pass
stereo, `FullIndependentRender`, Sponza, TSR/bloom, and independent desktop and
SPS `OcclusionViewKey` ownership.

The immediate diagnostic order is:

1. reproduce the trusted-negative-query failure with the current binary and
   correlate the exact proxy/query command stream with owning camera, target,
   render area, depth state, and query results;
2. isolate the independent directional-light/shadow variation whose first
   captured difference is `LightingAccum`;
3. change one variable, rebuild, and repeat the short enabled/off cohort until
   query culls are both nonzero and image-safe;
4. run the settled boundedness/lease cohort and the exact retained-frame
   validator, preserve desktop and both-eye captures, and publish the tracked
   Phase 5.2.4b validation manifest.

New raw evidence for this attempt is stored under
`Build/_AgentValidation/20260710-openxr-strict-stereo/20260715-remaining-closeout/`.
The existing ten immediate validation roots are retained because durable work
docs reference them; continuing below the active strict-stereo root avoids
creating an eleventh root.

### 2026-07-15 Query Correction And Exact-Gate Result

The original Vulkan zero-result quarantine was removed after correcting the
visible-demotion probe path. A brief attempt to bracket the contributing colour
draw was rejected: the colour pass follows the depth-normal prepass, so equal
depth produces valid zero samples and falsely culls visible geometry. The final
path instead retains the conservative AABB proxy, which uses `LEQUAL`, before
the contributing draw. It therefore measures the prepass depth safely without
allowing the mesh to self-occlude its probe.

`VulkanCpuDirectOcclusionTests` passed after the correction. The paired
100-warmup/60-retained diagnostic recorded 131 query submissions, 131
resolutions, and 1,049 culls with no output-parity, Vulkan-validation, or
ownership failure. The independent desktop and true-SPS view ledgers both had
nonzero query submission/resolution/culling.

The strict SPS injected-failure matrix also passed all six stages (Capability,
Target, Recording, LifetimeValidation, Submit, Publish), with no sequential
eye fallback. Its aggregate report is
`strict-sps-failures/reports/openxr-strict-sps-failure-matrix.json` below the
active evidence root.

The exact occlusion-off companion completed 100 warmup and 300 retained frames:
398 submissions, zero frame validation errors, clean teardown, and 125 required
desktop/SPS capture artifacts. The corresponding enabled 300-frame validator
passed the query, lease, resource, command-record, and strict-SPS gates, but
correctly rejected rendered-output promotion: all three desktop captures and
both eyes diverged from the off baseline (desktop RMSE about 1.339; eye RMSE
about 0.326-0.331, vs 0.01 tolerance). Visual inspection places the first and
largest divergence in `DefaultPipelineSps_05_LightingAccum_layer0`: one capture
is dark while the counterpart is strongly lit, before final post processing.
Geometry remains coherent, so this is the pre-existing directional primary
shadow/lighting nondeterminism, not false occlusion culling.

The Phase 5.2.4b/c promotion hold therefore remains open solely on making the
directional-light shadow sample deterministic (or otherwise isolating the
lighting source without weakening image parity). Do not check the todo gates or
begin 5.2.5 yet.

## F5 Freeze Follow-Up

A later current-worktree F5 run, engine session
`xrengine_2026-07-14_12-14-08_pid31772`, remained executable but the editor UI
was effectively frozen. Repeated debugger pauses landed in
`TryGetPendingImageAccessState` while it compared an image handle and range.
That condition was the inner loop of a reverse linear scan over every image
access delta accumulated by the command buffer, not a blocking Vulkan call.

The workload amplified the scan severely. Lifetime telemetry grew from 9,092
live records and 3,308 descriptor sets to 39,171 and 22,768 respectively, while
the command-buffer count remained 29-33. The session emitted 8,289 buffer
allocation events, including 5,771 uniform-buffer allocations, and render-thread
uploads waited 41-65 seconds. Repeated descriptor/barrier state queries against
the growing delta history therefore approached quadratic CPU work.

The command-local tracker now keeps a latest-state dictionary keyed by atomic
`(image, mip, layer, aspect)` identity. Recording updates that index and retains
the compact range-delta list only as the publication journal. Lookups combine
only the requested indexed subresources, reject missing or incompatible
multi-subresource state, and never scan historical deltas.

The focused Phase 5.2.4 tracking tests passed 11/11. A
current-binary Monado smoke then completed successfully in 43 seconds with five
retained frames, eight strict-SPS submissions, zero submission rejections, and
zero warnings/failures. Frame recording stayed between 0.95 and 1.20 ms. The
first four retained frames stayed at 1,820-1,822 live records and 452 descriptor
sets; the fifth rose to 3,157 and 940 as additional workload arrived. Total
frame time was 17.6-40.7 ms for those first four frames and 142.5 ms for the
fifth, rather than the prior multi-second freeze.

Raw evidence is under
`Build/_AgentValidation/20260710-openxr-strict-stereo/20260714-cpu-query-monado-regressions/image-access-index-live/`.
The run still accumulated 35,730 live records and 24,680 descriptor sets during
later startup/teardown work and logged render-thread waits up to 4.5 seconds.
The indexed lookup removes the quadratic freeze, but the separate descriptor,
uniform-buffer, and multi-output resource growth remains an open throughput
issue for the Phase 5.2.5-5.2.7 architecture work.

## 2026-07-16 Shadow Stabilization And Phase Closeout

The lighting-parity hold had two coupled shadow-state causes.

First, directional primary-atlas publication was not transactional. An omitted
publication could clear the last sampleable primary tile even when the current
plan still owned the same request and allocation. Primary planning now snapshots
the prior slot and preserves it only when the current allocation matches the
request key, atlas/page/rect identity, content revision, residency, and fallback
state. Explicit clears still clear immediately, and a stale or reallocated tile
cannot survive by omission.

Second, the directional content revision hashed caster membership but not caster
state. The moving temporal caster therefore left the atlas tile frozen at
whichever startup frame happened to render last; enabled/off runs first diverged
in `LightingAccum` according to startup timing. The allocation-free caster
content signature now includes enabled/pass state, current mesh world matrix,
model-matrix use, instance count, renderer/material/render-option identities,
and culling bounds. Caster motion consequently invalidates and redraws the tile
without treating unchanged membership as unchanged content.

The shadow viewport was also removed from the presentation-output ledger. It is
an auxiliary render and must not masquerade as a desktop or OpenXR output during
shape and ownership validation.

The final resource audit exposed a validator semantic issue rather than an
ongoing leak: scenario initialization could add a bounded descriptor set and
lifetime record before settling for the rest of the cohort, while the old gate
compared the first retained window to the last. The gate now compares adjacent
terminal windows and still enforces the global declared maximum. A direct
PowerShell regression accepted a bounded early step and rejected continuing
terminal growth. Warmup was increased from 100 to 120 frames so two resources
observed retiring at retained index 1 drain before acceptance begins; retained
retirements remain forbidden. Final capture roots were shortened after
ImageMagick demonstrated that the longest sub-native
`13c_MonoTsrReference` filenames exceeded its Windows path handling.

Final evidence:

- Focused directional-shadow, CPU-query, stale-frame, and OpenXR timing
  contracts passed 87/87.
- The refreshed strict-SPS injected-failure matrix passed Capability, Target,
  Recording, LifetimeValidation, Submit, and Publish without sequential eye
  fallback.
- Matched native occlusion-off/enabled cohorts each completed 120 warmup and
  300 retained frames. The enabled cohort recorded 694 desktop and 650 true-SPS
  query submissions/resolutions, with 5,006 desktop and 200 SPS culls.
- All nine native desktop/left/right off-enabled image comparisons passed; the
  maximum RMSE was 0.001807 against the 0.01 limit.
- Matched 0.67-scale sub-native off/enabled cohorts also completed 120+300 and
  passed all nine parity comparisons; maximum RMSE was 0.001927.
- Retained allocation, descriptor-pool churn, resource-plan replacement, and
  retirement frame counts were all zero. Live resources settled at 3,969 and
  tracked descriptor sets at 1,438 in both adjacent terminal 30-frame windows;
  planner and command-variant counts remained three.
- The filtered Vulkan log manifest contained zero accepted or rejected
  validation matches, with no device loss or lifetime rejection.

The durable result manifest is
[`vulkan-core-hardening-phase524b-validation-2026-07-10.json`](../../testing/rendering/vulkan-core-hardening-phase524b-validation-2026-07-10.json).
Raw final artifacts are under `Build/_AgentValidation/p524b-final-w120/`, the
matching native off baseline is under `Build/_AgentValidation/p524b-off-w120/`,
and the refreshed strict matrix remains under
`Build/_AgentValidation/20260710-openxr-strict-stereo/20260715-remaining-closeout/final-shadow-fix-strict-sps/`.

Phase 5.2.4b and 5.2.4c are closed. Phase 5.2.5 may now begin; the remaining CPU
recording cost and absent primary reuse are owned by the versioned-plan and
multi-output throughput phases rather than this correctness closeout.

## 2026-07-16 Desktop Device-Loss Regression

The next desktop-only Unit Testing World run, engine session
`xrengine_2026-07-16_10-39-52_pid2876`, lost the Vulkan device at frame 111.
The first failing API was a one-shot `vkQueueSubmit` used by a texture layout
transition. The later `Cannot update Vulkan descriptors while device state is
Quiesced` invalid-operation exceptions were fallout: the viewport command
container continued executing commands after the backend had already diagnosed
and quiesced the lost device.

The CPU-query path contained a GPU-corruption risk before that first observed
failure. Both new command-buffer recording and reusable-primary replay called
host `vkResetQueryPool`, while previously submitted command buffers could still
refer to the same persistent pool. Query result availability is not the Vulkan
host-reset lifetime boundary: all submitted commands referring to the reset
range must have completed. Each recorded primary already begins its query epoch
with `vkCmdResetQueryPool`, so the host reset was also redundant.

The correction keeps the reset exclusively in the graphics command buffer,
where queue order serializes it after older uses, and only clears the CPU-side
submitted-epoch marker before cached-primary replay. The viewport command
container now stops as soon as the active renderer reports device loss, avoiding
the repeated descriptor exception cascade while `XRWindow` enters recovery.

Validation completed:

- Focused CPU-query, command-buffer reuse, and coordinator contracts passed
  41/41, including guards against host query-pool reset and post-loss command
  execution.
- The editor build completed with zero errors. Existing NuGet audit warnings for
  `Magick.NET-Q16-HDRI-AnyCPU` remain; this investigation did not change
  dependencies.
- Desktop Vulkan session `xrengine_2026-07-16_10-54-21_pid44024` remained
  responsive through eight immediate camera cuts and more than 2,200 rendered
  frames. Inspected screenshots changed with camera position rather than
  returning stale output.
- The final profiler sample reported `CpuQueryAsync` with `CpuDirect`: 21 query
  submissions, 19 resolutions, two-frame average/max latency, 125 tested draws,
  108 culled draws, and no pending queries.
- The final Vulkan/rendering logs contained zero errors and no
  `InvalidOperationException`, generic rendering exception, `ErrorDeviceLost`,
  descriptor-after-quiesce failure, VUID, or validation-error match.

Raw before/after logs and screenshots are under
`Build/_AgentValidation/20260710-openxr-strict-stereo/20260716-desktop-cpu-query-device-loss/`.

## 2026-07-16 Stable-Packet Inline-Query Regression

After the stable-packet/primary-reuse work, the main Sponza cloths, plants, and
pillars could be query-culled while visibly in front of the rear wall. The
reported frame rate also dropped sharply during camera motion after an initial
attempt required the complete frame-op signature to match before primary
reuse. That attempt was reverted: camera and per-draw frame data belong in the
existing refresh path, so treating them as primary structure caused continuous
command recording and resource churn.

The regression is instead a narrower primary-cache identity error. Occlusion
query brackets and their proxy draw intentionally remain inline and are omitted
from the command-chain packet schedule. The fast lookup used the complete op
shape to find a cached schedule, but primary-variant selection then compared
only the schedule's packet/group signatures. Because those signatures exclude
the query brackets, it could select a primary recorded for query object A while
the current frame refreshed proxy uniforms and coordinator state for query
object B. The resulting query answer was attributed to the wrong mesh.

The correction compares the already-computed query generation when selecting a
reusable command-chain primary. That generation contains only query identity,
target, and begin/end operation; it deliberately excludes camera matrices and
other refreshable frame data. A query-set change therefore re-records the small
primary execution list, while ordinary camera motion continues through the
frame-data refresh path.

Evidence before the correction is engine session
`xrengine_2026-07-16_16-25-23_pid5824`. Its proxy pipeline used normal-Z
`LessOrEqual` depth testing with depth writes disabled, ruling out a reversed-Z
comparison error. The same run showed the broad-signature experiment growing
to thousands of command recordings and multi-gigabyte allocator pressure; that
experiment is not part of the correction described above.

Validation status: focused contracts and editor build pending; live Sponza
image/performance confirmation pending.

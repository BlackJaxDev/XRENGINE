# Vulkan Core Hardening And Device-Loss Completed Work

Last Updated: 2026-07-16
Owner: Rendering
Status: Historical completion and evidence record through the Phase 5.2.4b/5.2.4c closeout
Open Work: [Vulkan core hardening and device-loss TODO](vulkan-core-hardening-and-device-loss-todo.md)
Execution: Current worktree only; do not create or switch branches for this effort.

## Purpose

This sibling record holds completed implementation details, checked criteria,
dated handoffs, measurements, and durable evidence moved out of the active todo.
The dated handoffs are preserved as historical snapshots; any open instruction
inside a handoff is superseded by the
[current todo](vulkan-core-hardening-and-device-loss-todo.md).

## Original Trigger

The July 9, 2026 light-probe capture crash showed the current failure shape:

- `LightProbeBatch` was active and reached probe 10 of 36.
- OpenXR Vulkan eye rendering then reported device loss from an eye fence wait.
- Vulkan logs immediately before device loss showed heavy capture-sized
  resource-plan churn for `SceneCaptureEnvDepth`, `LightProbeEnvColor`,
  G-buffer resources, post-process resources, bloom, exposure, and final output.
- Scene/light-probe capture had a caller-owned cubemap FBO, but still entered
  the full viewport/post/temporal command path.

The immediate mitigation routed Vulkan light-probe capture through the direct
FBO path. This todo tracks the broader engine fixes so the same class of failure
does not reappear in another auxiliary render path.

## Implemented Summary

The checked implementation history through Phase 5.2.4a, plus the completed
strict OpenXR slice, is condensed here. Detailed evidence remains in the linked
progress, investigation, and validation records; this section is a summary, not
a replacement for those artifacts.

- **Phases 0-2.1 — diagnosis, containment, and context isolation.** Established
  deterministic repro/result manifests, the device-loss risk taxonomy, a
  first-writer-wins terminal renderer state, named/composable diagnostic
  presets, KHR/EXT/vendor fault collection, object naming, bounded validation
  reports, and allocation-free submission breadcrumbs. Added the local
  `VK_KHR_device_fault` shim required by the current bindings. Introduced
  immutable `FrameOpContext` identity, rejected context-mismatched command
  buffers, isolated planner runtime state and allocation ownership by context,
  added ref-counted shared allocator ownership with deduplicated destruction,
  serialized queue operations through the terminal-state gateway, bounded and
  truncation-aware fault draining, and repaired OpenXR eye/mirror/publish/
  prewarm keys and diagnostics.
- **Phases 3-5.1 — capture, lifetime, and synchronization correctness.** Added
  explicit `RenderCapturePolicy` variants and a minimal direct-FBO path that
  excludes the viewport G-buffer, temporal, bloom, exposure, upscale, and final
  output stack while preserving backend-native orientation and shared clip
  policy; probe octahedral/IBL work remains explicit post-capture work.
  Implemented completion-safe resource retirement, descriptor lifetime
  tracking, and non-waiting lost-device teardown. Centralized per-subresource
  image state, sync2 barriers, ordered submit-chain state propagation, queue
  ownership, pure descriptor-layout resolution, render-graph physical
  dependencies, reverse invalidation, generation-sensitive cached state,
  acquire/present recovery, allocator ownership, API/feature-gated `pNext`
  chains, and ordered OpenXR teardown. The five-lane Phase 5.1 matrix closed
  with zero engine-owned synchronization, layout, acquire/present, or teardown
  errors; the remaining SteamVR messages are narrowly attributed runtime
  exceptions, not a broad allowlist.
- **Phases 5.2.1-5.2.4a — output contract and cheap exact invalidation.** Added
  the unified output-request manifest, priorities, budgets, dependencies,
  degradation telemetry, profiler schema, quiet-window gating, structural versus
  volatile generations, bounded target variants, static/volatile command
  separation, deterministic reuse misses, stable resource identity, exact
  reverse-dependency invalidation, range-compacted local layout/dependency
  tracking, descriptor-pool reverse indexes, structural descriptor allocation
  keys, safe late-race rejection, pressure telemetry, and lock/allocation-free
  validated hot paths.
- **Strict OpenXR view/probe slice — 2026-07-10.** `SinglePassStereo` is now a
  strict true-multiview contract: unsupported capability or render/submit failure
  logs the exact reason and ends the begun frame without projection layers;
  sequential fallback is forbidden. Separately selected sequential/parallel
  modes own independent pipelines, command collections, publication, and
  occlusion keys. True single-pass owns one conservative stereo key. OpenXR
  external planner identity is stable across runtime image rotation while
  command/submission identity retains image slot and generation. Instance/system
  probing now uses exact `Result` control flow, bounded retry, and change-only
  optional-extension reporting. The remaining strict-SPS failure was correctly
  rejected as a retired-buffer-generation reference and was not hidden by a
  fallback.

Implemented decisions that no longer need open questions: capture variants live
behind explicit pipeline policy; diagnostics use composable flags plus curated
presets; and rotating external image handles are variants, not physical-plan
identity.

### Evidence Index

- [Phase 0 baseline and crash taxonomy](../../progress/rendering/vulkan-core-hardening-phase0-2026-07-09.md)
- [Phase 2.1 validation manifest](../../testing/rendering/vulkan-core-hardening-phase21-validation-2026-07-09.json)
- [Phase 4 live lifetime evidence](../../investigations/rendering/vulkan-core-hardening-phase4-live-validation-2026-07-09.md)
- [Phase 5/5.1 live investigation](../../investigations/rendering/vulkan-core-hardening-phase5-live-validation-2026-07-09.md)
  and [validation manifest](../../testing/rendering/vulkan-core-hardening-phase5-validation-2026-07-09.json)
- [CPU framerate regression investigation](../../investigations/rendering/vulkan-cpu-framerate-regression-2026-07-09.md)
- Phase 5.2 validation:
  [5.2.1](../../testing/rendering/vulkan-core-hardening-phase521-validation-2026-07-09.json),
  [5.2.2](../../testing/rendering/vulkan-core-hardening-phase522-validation-2026-07-09.json),
  [5.2.3](../../testing/rendering/vulkan-core-hardening-phase523-validation-2026-07-10.json),
  [5.2.4](../../testing/rendering/vulkan-core-hardening-phase524-validation-2026-07-10.json), and
  [5.2.4a](../../testing/rendering/vulkan-core-hardening-phase524a-validation-2026-07-10.json)
- [Descriptor lifetime freeze investigation](../../investigations/rendering/vulkan-descriptor-lifetime-freeze-2026-07-10.md)
- [Dynamic rendering/OpenXR regression investigation](../../investigations/rendering/vulkan-dynamic-rendering-promotion-2026-07-10.md)

Representative raw evidence remains under
`Build/_AgentValidation/20260709-233000-vulkan-phase521/` and
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-09_23-29-11_pid55356/`.

## Phase 5.2 Completed Performance Evidence

The current settled experimental reuse cohort demonstrated that the architecture
can reach 108.79 samples/s, 8.883 ms p50, 11.031 ms p95, zero record-path
allocation, zero retirement, and zero rejection on the measured workload. That
result is evidence, not permission to promote reuse until the remaining
correctness matrix closes.

## Phase 5.2.4 Completed Work

### 5.2.4b - Completed Work And Historical Evidence

This is a blocking correctness gate. Do not begin 5.2.5 until every acceptance
criterion below is checked. Limit plan work here to the correctness needed to
stop the active generation/target churn; 5.2.5 still owns the general versioned,
nonblocking plan-and-arena architecture.

The Monado session
`xrengine_2026-07-10_14-20-29_pid22804` recorded six retired-uniform-buffer
submission rejections, 38 `MainViewport` signature changes, 47 physical-plan
changes, 18 forced waits, and 182 retirement-backlog reports. The visible top
strip is approximately 111 pixels, the same numerical difference as
`1007 - 896`; treat that as a viewport/extent clue, not a proven cause. Preserve
this diagnosis and subsequent evidence in the
[dynamic rendering/OpenXR investigation](../../investigations/rendering/vulkan-dynamic-rendering-promotion-2026-07-10.md).

#### 2026-07-13 implementation handoff

The implementation is substantially advanced but this gate is not closed. Keep
all unchecked acceptance criteria below open until the exact 300-frame cohorts
and visual review pass. Current evidence and the shortest remaining path are:

- The strict injected-failure matrix is green for all six boundaries
  (`Capability`, `Target`, `Recording`, `LifetimeValidation`, `Submit`, and
  `Publish`) with zero sequential-fallback attempts. The durable results are
  embedded below; the optional local raw artifact is
  `Build/_AgentValidation/20260710-openxr-strict-stereo/phase524b-final-20260713/strict-failures-rerun3/reports/openxr-strict-sps-failure-matrix.json`.
- The OpenXR resource graph now uses registered persistent per-eye mono-reference
  texture views, generic all-layer copies publish every copied layer, strict SPS
  same-batch publish state is propagated, unsubmitted occlusion queries are not
  polled, recording abort invalidates unsafe cache entries, and the Monado
  service teardown script no longer assigns the read-only PowerShell `$PID`
  variable.
- Motion-vector material callbacks no longer retain an ambient/creation-time
  pipeline. `PendingMeshDraw` captures immutable current/previous unjittered
  left/right view-projection matrices from its command collection, and Vulkan
  writes them directly into the mapped uniform block without boxing or temporary
  arrays. The editor build completed with 0 errors (216 pre-existing NuGet
  advisory warnings), and the focused Vulkan/stereo contract cohort passed
  104/104 tests.
- The latest short occlusion-off probe completed 78 strict-SPS submissions,
  8 retained frames, true multiview, synchronization validation enabled, zero
  sequential fallback, and clean teardown. It proves the prior all-zero velocity
  defect is fixed. It still reports ten rendered-output failures: settled static,
  disocclusion, and motion-stop samples retain excessive velocity; moving-object
  X velocity has the opposite sign from the declared `PositiveX` oracle; and
  right-eye mono-equivalent sharpness/convergence fails in `StaticPoseSettled`,
  `DisocclusionRevealed`, and `MotionStopSettled`. The durable measurements are
  embedded below; the optional local raw artifact is
  `Build/_AgentValidation/20260710-openxr-strict-stereo/phase524b-final-20260713/native-occlusion-off-probe5/reports/openxr-smoke-summary.json`.
- First, reconcile the velocity sign convention and scenario settle/capture
  timing, then fix the layer-1 mono-reference source/layer selection or TSR drift.
  Re-run the short occlusion-off probe until every temporal and bloom oracle is
  green; do not weaken thresholds merely to accept the current output. The
  diagnostic command is:

  ```powershell
  & Tools/OpenXR/Run-OpenXrPhase524bOcclusionOff.ps1 `
    -WarmupFrames 72 -RetainedFrames 8 -CaptureSkipFrames 2 `
    -FoveationMode Off -TsrResolutionScale 1.0 -TimeoutSeconds 900 `
    -RunRoot Build/_AgentValidation/20260710-openxr-strict-stereo/phase524b-final-20260713/native-occlusion-off-probe-next `
    -NoBuild -StartService
  ```
- Next, finish the still-unchecked lifetime regression/pinning, stable desktop
  plan/presentation, stereo descriptor/FBO/fullscreen-region audit, and
  per-`OcclusionViewKey` recovery/telemetry/parity items below. Extend the linked
  investigation with the original `14-20-29` baseline and the new probe evidence.
- Then run the exact native occlusion-off reference cohort:

  ```powershell
  & Tools/OpenXR/Run-OpenXrPhase524bOcclusionOff.ps1 `
    -WarmupFrames 100 -RetainedFrames 300 -CaptureSkipFrames 10 `
    -FoveationMode Off -TsrResolutionScale 1.0 -TimeoutSeconds 900 `
    -RunRoot Build/_AgentValidation/20260710-openxr-strict-stereo/phase524b-final-20260713/native-occlusion-off `
    -NoBuild -StartService
  ```

- Finally, run the exact enabled-occlusion gate (including its required
  sub-native TSR companion), inspect desktop and both-eye captures, and update
  the tracked validation JSON before checking the remaining boxes:

  ```powershell
  & Tools/Validate-VulkanPhase524b.ps1 `
    -WarmupFrames 100 -CaptureSkipFrames 10 -CaptureSettleFrames 50 `
    -RetainedFrames 300 -TimeoutSeconds 900 -SteadyStateWindowFrames 30 `
    -TsrResolutionScale 1.0 -SubNativeTsrResolutionScale 0.67 `
    -RunRoot Build/_AgentValidation/20260710-openxr-strict-stereo/phase524b-final-exact `
    -StrictFailureReportPath Build/_AgentValidation/20260710-openxr-strict-stereo/phase524b-final-20260713/strict-failures-rerun3/reports/openxr-strict-sps-failure-matrix.json `
    -OcclusionOffSummaryPath Build/_AgentValidation/20260710-openxr-strict-stereo/phase524b-final-20260713/native-occlusion-off/reports/openxr-smoke-summary.json `
    -NoBuild -StartService
  ```

#### 2026-07-14 pause handoff

Implementation and live validation are complete for the checked criteria below.
The final current-binary sub-native cohort passed with 240 warmup plus exactly
300 retained frames under Vulkan SDK/validation layer 1.4.350, synchronization
validation, true SPS, dynamic rendering, TSR at 0.67 scale, bloom, independent
desktop presentation, and CPU-query occlusion. It recorded 5,571 culls, zero
known-visible-sentinel culls, 300 desktop and 300 SPS hidden-proof culls, maximum
result age 2 frames, zero recovery age, zero resource/descriptor/plan churn or
positive count drift, and final-image parity RMSE no greater than 0.00087. Both
896x1007 acquired-eye images and the desktop output were visually inspected.

The current-binary native occlusion-off reference also passed 300/300 retained
frames with zero warnings/failures and clean teardown. A refreshed native
occlusion-enabled run was stopped at the user's request; the preceding exact
native enabled cohort had already passed. Raw current evidence is under
`Build/_AgentValidation/20260710-openxr-strict-stereo/phase524b-critical-20260714/`
in `exact-subnative-occlusion-off-final6`, `exact-subnative-enabled-final4`, and
`exact-native-occlusion-off-final5`. Remaining closeout work is intentionally
left unchecked: create/update the tracked validation JSON, then check the final
all-criteria gate.

#### 2026-07-14 current-worktree regression hold

Do not promote the earlier 300-frame cohort over the current binary without a
new validation pass. A post-closeout investigation found and fixed two active
correctness regressions: clean primary reuse did not advance CPU-query frame
operations, and OpenXR frame operations could retain generated shadow,
post-process descriptor, and framebuffer resources from a previous output's
physical plan. The latter caused rejected eye submissions and the observed
black/rendered Monado flicker.

The final short current-binary smoke recorded zero submission rejections, eight
successful strict-SPS submissions, no sequential fallback, and projection
layers in every retained frame. A desktop-only run proved useful current-view
CPU-query culling (32-52 of 46-76 tested meshes culled, versus all 393 rendered
with culling disabled). Preserve the cause, fixes, performance measurements,
and rejected per-draw command-chain experiment in the
[CPU-query/Monado regression investigation](../../investigations/rendering/vulkan-cpu-query-monado-regressions-2026-07-14.md).

Throughput is not closed. `FullIndependentRender` produced up to 17 output
ledger entries per retained frame while rendering desktop preview, main desktop,
and true-SPS eyes. The worst retained samples reached 26,664 tracked descriptor
sets and 42,868 live resource records even though measured GPU work remained
roughly 1.8-22.8 ms. This is CPU/output-plan duplication and allocation pressure,
not a reason to disable `CpuQueryAsync` or silently fall back from SPS. Phase
5.2.4c owns the immediate mesh frame-data and descriptor-ownership remediation;
5.2.5-5.2.7 own the general render-plan/transient arena,
shared-snapshot/view-family, and deadline-scheduler solution after the current
correctness regression hold passes.

#### 2026-07-15 exact-query and every-third-eye follow-up

The current work now brackets the exact contributing mesh draw with its
`CpuQueryAsync` query, resets every inline query pool before the first render
operation, and suppresses query replay during the motion-vector pass. Hidden
recovery proxies remain deferred. The frame-wide mesh frame-data manifest now
assigns disjoint, stable output-family ranges from one root allocator and defers
late families to the next frame boundary instead of mutating a sealed recording.
Focused query, command-chain, arena, lifetime, and strict-SPS tests passed before
the live probe; the final strict-SPS/Phase 5.2.4 contract subset passed 102/102.

The user's later every-third-eye black flicker had a separate, precise lifetime
cause. Strict SPS publishes with transfer commands and does not create the
per-eye image views used by the direct-render path. Consequently, the raw
runtime-owned `VkImage` handles were not registered as external resources before
command-buffer dependency tracking. One of Monado's three swapchain handles was
reused from a completed engine-owned desktop `DepthStencil` image, so every reuse
of that acquired slot was rejected as generation 776 of the retired depth image.
`TryPrepareStereoLayerBlit` now registers both runtime-owned destination images
as external resources before recording the blit; genuine pending-retirement
reuse still fails instead of being hidden.

The untraced current-binary fix probe at
`Build/_AgentValidation/20260710-openxr-strict-stereo/phase524c-final-current/external-swapchain-lifetime-fix-probe/`
completed the smoke runner with exit code 0. It recorded 106 submitted frames,
106 successful strict-SPS submissions, 106 acquire/publish/release operations
per eye, zero end-frame failures, zero warnings/failures, and zero filtered
Vulkan matches. No retired-resource exception, strict-SPS failure, or missing
projection-layer diagnostic appeared. All six acquired-eye motion captures and
the desktop final were visually inspected and were nonblack. The report is
intentionally not promotion evidence: it retained only eight frames and omitted
the strict-failure and occlusion-off companion reports.

The combined workload remains CPU-bound. This sync-validation/capture probe
measured CPU frame p95 146.90 ms versus GPU p95 24.86 ms; nearby untraced
non-capture samples measured GPU p95 near 4.8-5.5 ms while CPU p95 remained near
138-149 ms. A short exact-visible-query cohort passed its lifetime, boundedness,
query-age, sentinel, and churn gates, but could not pass enabled/off image parity.
The first differing stage is directional-light/shadow `LightingAccum`; separate
occlusion-off cohorts independently alternate between bright and dark lighting.
Treat that as an existing nondeterministic lighting/shadow blocker, not evidence
that exact-draw occlusion queries changed the G-buffer. Keep the current-binary
300-frame, settled-bound, and rendered-parity criteria open until that instability
is fixed and the exact cohorts are rerun.

##### Durable strict-SPS failure evidence

Captured 2026-07-13 22:42:49 UTC with matrix schema 1. The aggregate result was
`passed=true`, all six expected stages were present, and the aggregate failure
count was zero. Each row requested and effectively used `SinglePassStereo` via
`TrueSinglePassStereo`; each injected failure was handled, ended with zero
projection layers, requested no sequential fallback, changed neither the local
nor global fallback counter, matched no forbidden fallback log, and matched no
engine validation error. Each stage ran 12 completed frames: 4 warmup, 8
retained, 9 successful SPS submissions, and one retained injected zero-layer
frame.

| Injected stage | Passed | Queue disposition | Projection layers | Fallback attempt delta | Global fallback count | Engine validation matches |
|---|---:|---|---:|---:|---:|---:|
| Capability | yes | `NotSubmitted` | 0 | 0 | 0 | 0 |
| Target | yes | `NotSubmitted` | 0 | 0 | 0 | 0 |
| Recording | yes | `NotSubmitted` | 0 | 0 | 0 | 0 |
| Lifetime validation | yes | `NotSubmitted` | 0 | 0 | 0 | 0 |
| Submit | yes | `NotSubmitted` | 0 | 0 | 0 | 0 |
| Publish | yes | `Completed` | 0 | 0 | 0 | 0 |

##### Durable occlusion-off probe-5 evidence

Captured 2026-07-13 23:39:10 UTC with smoke-summary schema 8. This was a short
diagnostic probe, not the required 300-retained-frame acceptance cohort.

| Setting or result | Recorded value |
|---|---|
| Runtime | Monado; runtime version field was empty |
| Renderer / target mode | Vulkan / `DynamicRendering` |
| Requested / effective view mode | `SinglePassStereo` / `SinglePassStereo` |
| Implementation path | `TrueSinglePassStereo` |
| Diagnostic preset / sync validation | `SyncValidation` / enabled |
| AA / TSR scale | TSR / 1.0 |
| Occlusion / mirror mode | disabled / `FullIndependentRender` |
| Submitted / successful SPS frames | 78 / 78 |
| Retained / no-layer / end-frame failures | 8 / 2 / 0 |
| Sequential fallback attempts | 0 |
| Missed deadlines | 18; unacceptable as final evidence, retained here for follow-up |
| Teardown | completed |
| Rendered-output oracle failures | 10 |

The deterministic temporal sequence used these predeclared capture windows:

| Sample | Frames | Velocity oracle | Convergence required |
|---|---:|---|---:|
| `ObjectMotionActive` | 8-10 | `PositiveX` | no |
| `StaticPoseSettled` | 16-18 | zero | yes |
| `HeadRotationActive` | 24-26 | `PositiveX` | no |
| `HeadTranslationActive` | 32-34 | `NegativeX` | no |
| `DisocclusionOccluded` | 38-40 | zero | no |
| `DisocclusionRevealed` | 48-50 | zero | yes |
| `MotionStopMoving` | 56-58 | `PositiveX` | no |
| `MotionStopSettled` | 68-70 | zero | yes |

All ten probe failures, preserved with their exact numeric measurements rather
than only in the ignored JSON, were:

1. `StaticPoseSettled` exceeded the static-zero velocity limit: left
   `0.05611562`, right `0.055833787`.
2. `StaticPoseSettled` layer 1 lost edge sharpness against its mono-equivalent
   input; the required ratio was `0.95`.
3. `StaticPoseSettled` layer 1 mono-equivalent RMSE was
   `0.017435264254259057` and did not converge.
4. `DisocclusionOccluded` exceeded the static-zero velocity limit: left
   `0.8377893`, right `0.82758254`.
5. `DisocclusionRevealed` exceeded the static-zero velocity limit: left
   `1.856543`, right `2.04671`.
6. `DisocclusionRevealed` layer 1 mono-equivalent RMSE was
   `0.013945151576911428` and did not converge.
7. `MotionStopMoving` did not match its declared `PositiveX` direction: left X
   `-0.11223021`, right X `-0.11242089`.
8. `MotionStopSettled` exceeded the static-zero velocity limit: left
   `0.96484417`, right `0.9278758`.
9. `MotionStopSettled` layer 1 lost edge sharpness against its mono-equivalent
   input; the required ratio was `0.95`.
10. `MotionStopSettled` layer 1 mono-equivalent RMSE was
    `0.035811131840869946` and did not converge.

The significant before/after result is that probe 4 bound identical current and
previous stereo view-projection matrices for every observed motion draw and
therefore produced all-zero velocity. Probe 5, after moving temporal matrices
into the immutable draw snapshot, produced nonzero per-eye velocity. The
remaining failures are now convention, capture-settle, and right-eye
mono-reference/TSR-correctness failures rather than missing temporal bindings.

- [x] Extend that investigation with the `14-20-29` session manifest, the
  6/38/47/18/182 baseline counters, the supplied screenshot, exact reproduction
  settings/commands, and the user's observed bloom, blur, top-strip, occlusion,
  and black-frame symptoms before relying on the baseline as durable evidence.
#### 5.2.4b.1 - Make Strict Stereo Submittable Without Lifetime Races

- [x] Convert OpenXR renderer prewarm into two passes: first count every use of
  each `VkMeshRenderer` in the complete eye command buffer, then reserve final
  draw/uniform/descriptor slot capacity before recording or publication. Reuse
  the regular Vulkan capacity helper rather than a divergent OpenXR allocator.
- [x] Add a regression where the same renderer is used at least three times in
  one strict-stereo command buffer. No referenced uniform buffer, descriptor,
  image view, framebuffer, or plan generation may retire while that buffer is
  recorded, queued, or in flight.
- [x] Pin all recorded resource generations through the last submitted
  completion ticket and make every pre-submit lifetime rejection name the
  resource, old/new generation, owning output, command buffer, and retirement
  ticket.
- [x] Preserve the strict contract at every failure boundary: requested
  `SinglePassStereo` resolves only to true multiview or unsupported, never to
  sequential. Capability, target, recording, validation, submit, or publish
  failure logs the exact stage and reason and ends a begun OpenXR frame without
  projection layers. A separately selected sequential mode remains legal.
- [x] Add a zero-tolerance sequential-fallback counter and behavioral tests for
  each strict-SPS failure boundary.

#### 5.2.4b.2 - Stabilize Desktop Planning And Presentation

- [x] Keep directional-shadow resources registered for the lifetime of a
  compatible pipeline generation. Gate shadow pass execution and dependencies;
  do not add/remove logical resource specs as ordinary shadow work toggles.
- [x] Keep `MainViewport` physical-plan identity stable for an unchanged
  pipeline, attachment signature, and extent. External desktop/OpenXR target
  rotation binds a bounded slot variant and must not allocate/retire an otherwise
  compatible plan.
- [x] Prevent stereo target rotation from dirtying desktop resources and prevent
  desktop resize/present state from invalidating the eye family.
- [x] Remove `ResourcePlanReplacement` waits, force flushes, and global device
  drains from this steady-state repro. Retire replaced generations only behind
  frame-slot/timeline completion; 5.2.5 generalizes this rule to all outputs and
  eviction paths.
- [x] A rejected desktop frame must not present a cleared or unwritten image.
  Correlate every detected black frame with final-target contents, swapchain
  write/present state, plan/signature changes, retirement, submission rejection,
  and exposure.

#### 5.2.4b.3 - Repair Stereo Layer And Fullscreen-Region Contracts

- [x] In anti-aliasing resource declaration, derive the TSR layer count with
  `DeclaredLayerCount(builder)` and apply matching `.Layers(...)` and
  `.StereoCompatible(builder.Profile.Stereo)` metadata to `TsrOutputTexture` and
  every `TsrHistory*` resource, including `TsrHistoryColor`. Test the compiled
  descriptor against the factory-created Vulkan image/view shape.
- [x] Fix generic Vulkan blits/copies with `layerIndex == -1` to copy the common
  source/destination layer span and publish transitions for every copied layer.
  Cover history color, history depth, TSR output/history, and final stereo
  publish staging; never silently collapse the operation to layer zero.
- [x] Audit every stereo post-process descriptor/factory/FBO pair so extent,
  format, mip count, array layers, `StereoCompatible`, and attachment view shape
  agree. True-multiview attachments cover both layers with `viewMask=0x3`;
  per-eye views are allowed only at the final OpenXR publish boundary.
- [x] Make `PostProcessStereo.fs`, stereo bloom downsample/upsample and copy,
  `FinalPostProcessStereo.fs`, and `TemporalSuperResolutionStereo.fs` derive
  `ScreenOrigin`, `ScreenWidth`, and `ScreenHeight` from the current destination
  attachment/mip. Log the source extent and UV transform and assert that local
  raster coverage maps to `[0,1]`; clamping must not conceal an origin/extent
  mismatch.
- [x] Require each `BloomBlurTexture` write attachment to be a single-mip,
  two-layer array view with `layerIndex == -1`, and every sampled source-mip view
  to remain a two-layer `sampler2DArray` view.
- [x] Instrument every stereo fullscreen pass with destination extent, render
  area, viewport/scissor, attachment layer count, view mask, draw-time screen
  uniforms, source extent, and UV transform. In the current
  repro each full-resolution pass must be exactly `896x1007`, two layers, and
  `viewMask=0x3`; fail validation on a width-as-height, preview, desktop, stale
  parent, or mip-extent mismatch.
- [x] Capture and inspect both layers at pre-TSR color, velocity, current/history
  depth, TSR output/history, every active bloom accumulation mip (including
  0/1/4), final post-process, publish staging, and the acquired OpenXR image.
  Identify the first incorrect pass responsible for the 111-pixel strip.

#### 5.2.4b.4 - Restore Stereo Temporal And Bloom Correctness

- [x] Remove ambient temporal-pipeline lookup from deferred motion-vector
  binding. Capture and pass the immutable temporal snapshot belonging to the
  command collection's pipeline and eye family.
- [x] Define one stereo jitter/velocity/reprojection convention across raster
  motion vectors and TSR: jitter both eyes, use matching coverage, encode the
  current/previous jitter delta exactly once, and keep NDC/UV scale, sign, and Y
  direction consistent.
- [x] Verify current/previous view-projection matrices, jitter, velocity, history
  readiness, and reset generation independently for both layers. Camera cuts and
  incompatible extent/profile generations reset history; ordinary head motion
  and OpenXR image rotation do not.
- [x] Make `HistoryReady` layer-complete: color and depth history for both eyes
  must finish before readiness is published. After warmup, missing temporal
  snapshot/matrix paths are zero-tolerance; during an intentional reset, render
  current-frame data, invalidate history, and log the exact temporal key until
  both layers are reseeded.
- [x] At 1:1 eye resolution, keep temporal accumulation, per-eye history,
  velocity, and reprojection active; bypass only an unnecessary spatial upscale
  kernel. Validate a sub-native-resolution TSR cohort separately. Factor
  mono/stereo TSR algorithm code so history rejection, clamping, and sharpening
  cannot drift.
- [x] Fix `PostProcessStereo.fs` bloom composition so default mip counts 1-4 use
  the accumulated mip-1 result like the mono path and do not double-count coarse
  mips.
- [x] Validate static pose, head rotation, translation, object motion,
  disocclusion, and motion-stop. Static velocity is zero; moving velocity is
  nonzero, directionally correct, and eye-specific. Both layers retain sharp
  stable detail with no cross-eye history, persistent blur, ghosting, bloom
  displacement, or top-strip corruption.
- [x] Define deterministic rendered-output thresholds before the run for
  per-eye bloom energy/centroid and independence, scripted velocity samples,
  static-edge sharpness, and temporal convergence. Compare stereo against a
  mono-equivalent controlled input; visual inspection remains required but is
  not the sole oracle.

#### 5.2.4b.5 - Restore Per-Pipeline Occlusion Without Disabling It

- [x] Key command publication, query state, result epochs, stale age, and probe
  budget by the owning pipeline's complete `OcclusionViewKey`; no valid path may
  fall back to ambient `CurrentRenderingPipeline` state.
- [x] Give desktop/editor, each explicitly sequential eye pipeline,
  mirrors/captures, and true single-pass stereo independent hardware-query
  ownership and per-frame probe budgets. One pipeline must not consume the
  global query opportunity for every other pipeline.
- [x] For true single-pass stereo, issue one conservative multiview query and
  cull only when the mesh is occluded in both views. Visibility in either eye
  keeps the mesh visible.
- [x] Treat missing ownership, unavailable results, stale epochs, camera cuts,
  extent changes, command-set changes, and pipeline recreation as bounded
  fail-visible/reprobe states for that command collection. A camera cut restores
  visibility immediately; another output's negative result is never reused.
  Define the maximum recovery age in the validation manifest and fail the run if
  normal queried state is not restored within it.
- [x] Emit telemetry keyed by the full `OcclusionViewKey`: pipeline/output ID,
  submissions, resolutions, skips, current/max age, forced-visible recovery, and
  recovery latency.
- [x] Compare occlusion-enabled desktop and SPS output against a diagnostic
  occlusion-off ground truth. Require final-image and known-visible-sentinel
  parity, require each enabled candidate set to be a subset of its off set, and
  prove every removed mesh is occluded for the owning desktop view or both SPS
  views. No known-visible sentinel may be rejected, and independently valid
  desktop/SPS culls must both remain nonzero.

#### 5.2.4b.6 - Close The Live Correctness Gate

- [x] Re-run the current binary after the 2026-07-14 query-refresh and active-
  plan rebasing fixes. Require zero retired-resource/validation submission
  rejections, visually continuous desktop and both-eye output, and valid strict-
  SPS projection layers throughout the retained window.
- [x] Make output telemetry distinguish logical render requests from manifest,
  command-buffer, publish, overlay, and present events; remove duplicate request
  accounting and require bounded output, descriptor, resource, and command-
  buffer high-water marks during the current-binary smoke.

- [x] Add a deterministic Monado validator with warmup followed by exactly 300
  retained frames using Vulkan dynamic rendering, requested/effective
  `SinglePassStereo`, `EVrMirrorMode.FullIndependentRender`, bloom, TSR,
  `EOcclusionCullingMode.CpuQueryAsync`, and fixed bloom/motion/top-edge/
  occlusion and known-nonblack final-output sentinels. The desktop output must
  visibly change during scripted motion so a stale present cannot pass.
- [x] Explicitly enable synchronization validation for the 300-frame cohort and
  record the effective diagnostic preset, layers, settings, and any exact
  externally owned allowlist. Validation silently resolving to `Off` fails the
  cohort.
- [x] Record per frame: output and pipeline identity, external target slot/image,
  extent/layers/view mask, plan and command generations, lifetime validation,
  submit, OpenXR acquire/wait/publish/release/end-frame, desktop final write, and
  present result.
- [x] Before live acceptance, compile every touched stereo shader, build
  `XREngine.Runtime.Rendering` and the editor, and pass focused unit and
  deterministic rendered-output tests for lifetime, layer shape, fullscreen
  region, temporal/bloom, occlusion, presentation, and strict failure behavior.
- [x] After warmup, record zero physical-plan/signature changes for unchanged
  outputs, submission rejections of any kind, premature retirements, compatible-
  plan or structural-resource create/retire churn, descriptor pool/set churn,
  sequential fallbacks, global invalidation fallbacks, normal-path global waits
  or force flushes, `VUID-`, `SYNC-HAZARD`, or `UNASSIGNED` engine-owned
  validation errors, first-chance rendering/lifetime exceptions, and device
  loss.
- [x] All 300 strict-SPS frames use true multiview with `viewMask=0x3`, populate
  both layers, and complete acquire, wait, render, publish, release, and
  `xrEndFrame` with valid projection layers. Exercise precise zero-layer/no-
  fallback behavior separately with injected failures; no failed frame belongs
  in the passing 300-frame window.
- [x] All 300 desktop frames contain a final write and valid present; none is
  black, cleared-only, unwritten, stale for an extended period, or absent.
- [x] A controlled rejected desktop frame follows the documented skip-present or
  last-completed-image policy and never publishes a cleared target. Exposure and
  exposure-history values remain finite, nonzero where the scene requires it,
  and owned by the desktop pipeline.
- [x] TSR histories advance independently for both layers, stereo velocity and
  reprojection pass the static/motion/disocclusion cases, bloom contributes
  correctly in both eyes, and no captured stage contains the top strip.
- [x] Desktop and SPS retain every known-visible sentinel and final-image parity
  with their occlusion-off references after the bounded recovery window. Every
  omitted candidate has owning-view occlusion proof, SPS requires both-eye
  occlusion, and distinct stable `OcclusionViewKey` instances report independent
  nonzero valid culling work.
- [x] Resource, descriptor, planner-state, and command-variant counts remain
  bounded with no positive steady-state drift. Visually inspect retained
  desktop and both-eye captures; profiler/log success alone is insufficient.

### 5.2.4c - Completed Work And Historical Evidence

This is a blocking throughput and lifetime-ownership gate before the general
plan-and-arena work in 5.2.5. The indexed command-buffer image-access state
removed the reverse historical scan that made debugger pauses repeatedly land
in image-range matching, but it did not close the larger allocation problem.
The current `VkMeshRenderer` path still sizes descriptor slots as descriptor
frame slots multiplied by the largest draw-slot count seen by that renderer,
retains superseded uniform-buffer generations until renderer destruction, and
may keep up to 32 descriptor-allocation variants per renderer. Under the F5
`FullIndependentRender` workload, the post-fix session still grew to 24,680
tracked descriptor sets and 35,730 live tracked resources while later
render-thread waits reached seconds.

Do not close this gate by lowering the descriptor-variant cap alone, disabling
`CpuQueryAsync`, disabling independent desktop rendering, falling back from
strict SPS, or adding normal-path device-idle waits/force flushes. The required
fix makes recording ownership explicit and makes steady-state storage scale
with active frame/output families and unique material/pass bindings rather than
the historical number of draws and capacity shapes.

Implementation wrap-up (2026-07-14, updated 2026-07-15):

- Replaced the per-renderer historical uniform-generation cache with five
  renderer-owned, persistently mapped 32 MiB frame-slot arenas. Engine and auto
  uniforms reserve stable aligned ranges and bind through dynamic offsets;
  descriptor-set storage is no longer multiplied by the renderer's historical
  draw-slot capacity.
- Added pre-record reservation manifests for primary, dynamic-UI secondary,
  command-chain secondary, indirect-secondary, and OpenXR paths. Capacity is
  counted and prewarmed before `vkBeginCommandBuffer`; a late/unsealed request
  is rejected instead of clamped or grown during recording.
- Added exact command-buffer frame-data leases and wired recording, cached,
  submission-timeline, invalidation, reset, and destruction ownership. A live
  follow-up found that secondaries never cross the direct-submit gateway and
  therefore retained recording leases. `EndCommandBufferTracked` now closes a
  successful secondary recording into cached ownership and abandons a failed
  recording. The immediate 8-frame proof reported zero recording leases on
  every retained frame and reduced submit time from 29-57 ms to 0.6-0.8 ms.
- Replaced renderer-local one-pool-per-variant ownership with exact shared
  descriptor allocations and generation-owned 64-allocation pool slabs.
  Descriptor keys are structural plus exact resource contents and view family;
  compatible material descriptors are shared while output/pass resources stay
  isolated. The startup probe reported 50 allocation variants, four pools, and
  250 allocated sets before additional Sponza programs arrived; it did not run
  long enough to establish a settled plateau.
- Added bounded current/high-water telemetry for arena chunks/bytes,
  reservations, lease states, descriptor variants/pools/sets, live lifetime
  resources, and pending retirement. Output telemetry now separates two logical
  requests from manifest/render/submit/overlay/present events (11 events in the
  stable short samples) instead of counting every event as another render.
- Replaced the command-local image-range history scan with an indexed
  subresource state table, completed backing-image retirement for interned and
  non-interned views, published local command dependencies before retirement,
  and reset completed invalidated recordings before retirement drains. These
  changes remove the debugger hot loop and the stale-view lifetime race.
- Restored per-`FrameOpContext` resource-planner scope to reusable mesh and
  indirect refresh/recording paths. A live six-frame probe recorded 162 CPU
  query submissions, 151 resolutions, and 947 valid culls with zero validation
  errors or submission rejections, but it did not yet prove independent
  sustained desktop and SPS view-key work.
- Focused Vulkan lifetime/command-chain tests passed 86/86 before the final
  secondary-lease test additions; the editor then built with zero errors. The
  final additions and the last logging change below still need a fresh build and
  test run.
- The first post-lease-fix live run completed 8/8 retained frames with zero
  validation errors, zero submission rejection, true SPS submission, zero
  pending retirement for seven samples, 27-45 ms total frames, and 1.3-2.1 ms
  command recording. It was a startup probe, not the required settled 300-frame
  acceptance cohort.
- The subsequent 120-warmup/30-retained attempt timed out after 480 seconds at
  95 completed frames, before the retained window. Its 79.8 MiB Vulkan log
  contained 335,498 `VkBufferUploadQueue` lines because the Diagnostics GPU
  profile implicitly enabled per-subrange upload logging. The final source edit
  now requires explicit upload-stage or push-subdata tracing for those lines;
  the later build, focused tests, and live probe completed with that edit.
- Consolidated the command-stream manifests into one frame-wide root manifest.
  Compatible output/view families receive disjoint power-of-two ranges with a
  bounded minimum stride; late registration is counted and published at the
  next frame boundary. The latest retained samples used one sealed generation,
  a stable publication count of 22, ten families, 147 renderers, and a stable
  cumulative late-registration count of four rather than post-seal mutation.
- Exact visible-query brackets are recorded around contributing mesh draws and
  all query pools are reset before rendering begins. The latest combined probe
  independently submitted/resolved/cull-tested desktop and true-SPS work, but
  eight retained frames are not the required sustained 300-frame proof.
- Registered strict-SPS transfer destinations as externally owned Vulkan images
  before dependency tracking, closing the raw-handle generation collision that
  rejected every third Monado swapchain image. The post-fix live probe completed
  106/106 strict-SPS submissions and visually nonblack acquired-eye captures.
- The July 15 combined Monado/SPS plus independent-desktop investigation
  reproduced a descriptor-pending `InvalidOperationException`, a retirement
  entry that remained pending for more than 350 seconds after its graphics
  ticket had completed, and deadline/rate telemetry that reported zero misses
  and 90 Hz while observed completion was approximately 1-1.5 Hz. It also found
  that the Unit Testing World's explicit `GPURenderDispatch: false` was silently
  forced to `true` for Vulkan OpenXR and then resolved through the persisted
  `Diagnostics` profile to `GpuIndirectInstrumented`. Full evidence:
  [OpenXR Monado and desktop framerate investigation](../../investigations/rendering/openxr-monado-desktop-framerate-invalidoperations-2026-07-15.md).
- The July 15 CPU-direct closeout made the checked-in
  `GPURenderDispatch: false` authoritative, replaced descriptor-pending throws
  with an explicit pre-record defer, drained exact completed pipeline-layout
  retirement tickets, and changed output-rate/deadline telemetry to consume
  actual completion timestamps. The clean 100-warmup/30-retained F5 run used
  `CpuDirect`, `CpuQueryAsync`, true SPS, independent desktop rendering, and
  diagnostics off. It completed with zero `InvalidOperationException`, VUID,
  submission rejection, sequential fallback, device loss, or pending
  retirement. Retained CPU frame time averaged 27.546 ms (p95 30.536 ms), GPU
  work averaged 3.766 ms (p95 4.069 ms), and command recording averaged
  1.181 ms (p95 1.420 ms). True XR output completion was only 4.155 Hz with
  30/30 retained samples over budget, proving that remaining throughput is
  CPU/output orchestration rather than GPU execution. Evidence:
  `Build/_AgentValidation/20260710-openxr-strict-stereo/20260715-cpudirect-closeout/f5-baseline-final/reports/openxr-smoke-summary.json`.
- The frame-wide reservation root now charges each renderer only for families
  that use it. A live proof reduced the pathological 186,000-reservation /
  32 MiB-ceiling shape to 43,072 reservations and 15,453,120 bytes while
  resolving 73/73 queries, testing 1,800 candidates, producing 1,460 culls,
  and completing 118 strict-SPS submissions without fallback. The final clean
  run used 28,992 reservations and 7,275,392 bytes.
- Vulkan zero-sample CPU-query results remain untrusted: enabled/off visual
  isolation showed valid Sponza foreground geometry disappearing when those
  negatives controlled visibility. Vulkan now records and resolves the
  queries but explicitly quarantines negative results as forced-visible with a
  bounded diagnostic. This restores the missing geometry and prevents a false
  cull from corrupting desktop or eye output, but it means the nonzero-culling
  and exact image-parity acceptance items remain open. No GPU-instrumented or
  zero-readback cohort was run.

#### 5.2.4c.0 - Freeze The Supported Validation Lane

- [x] Make the checked-in `GPURenderDispatch: false` authoritative for the
  current Vulkan OpenXR Unit Testing World. Remove
  `RequiresGpuRenderDispatchForOpenXrVulkan`'s silent force-to-true behavior and
  update its contract test. An unfinished accelerated lane may be selected only
  by a separate explicit opt-in setting or launch profile that is recorded in
  the result manifest.
- [x] Define the current 5.2.4c F5/acceptance fingerprint as `CpuDirect` with GPU
  CPU safety-net fallback disabled. It must not resolve to `Diagnostics`,
  `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`, or any meshlet path.
  Emit the configured/requested/effective profile and submission strategy at
  startup and reject an automated acceptance cohort whose fingerprint differs.
- [x] Keep accelerated-path development diagnostic runs separate from CPU-direct
  correctness and performance baselines. Do not use their timings, fallback
  behavior, descriptor counts, exceptions, or images as 5.2.4c acceptance
  evidence, and do not make them prerequisites for continuing through the
  current CPU/output architecture phases.
- [x] Prove CPU-direct Vulkan OpenXR can acquire, record, submit, publish, release,
  and `xrEndFrame` true SPS without a hidden GPU-dispatch dependency or a CPU
  fallback masquerading as an accelerated result.

#### 5.2.4c.1 - Measure The Ownership Multipliers

- [x] Add allocation-free counters, high-water marks, and bounded diagnostics
  for active and lease-retained mesh uniform generations, mapped buffer count
  and bytes, uniform arena chunks and used bytes, draw-slot reservations,
  descriptor allocation variants, descriptor pools, allocated versus reserved
  descriptor sets, and pending retirement. Attribute every value to program/
  layout, material where applicable, output/view family, frame-data slot, and
  plan generation without logging once per draw.
- [x] Distinguish logical output requests from plan, command-buffer, publish,
  overlay, and present events so duplicated ledger accounting cannot masquerade
  as additional rendering work. Correlate actual draw counts with descriptor
  and uniform growth for `MainViewport`, UI preview, shadows, scene captures,
  and true SPS.
- [x] Capture a current-binary F5 baseline using the checked-in Unit Testing
  World settings and preserve the time series from startup through settled
  desktop/SPS rendering. Record draw-slot high-water changes, generation
  publication/retirement, pool create/destroy/reset, descriptor-set count,
  uniform-buffer count/bytes, lifetime live resources, retirement backlog,
  command-record time, render-thread waits, query work, and output continuity.
- [x] Make output-rate telemetry use completed/published frame times rather than
  requested source cadence. Add deterministic tests where 11.11 ms-budget work
  completes in hundreds of milliseconds or seconds and require achieved Hz,
  missed-deadline count, content age, and deferral/rejection counters to agree
  with the known completion sequence. Cross-check the live counters against
  profiler timestamps before accepting any cohort.

#### 5.2.4c.2 - Seal Frame-Data Capacity Before Recording

- [x] Build one frame-wide mesh frame-data reservation manifest before any
  desktop, preview, capture, shadow, or OpenXR command buffer starts recording.
  Assign stable frame-data slots by compatible output/view family and bounded
  external-target variant, then count every use of each `VkMeshRenderer` in the
  complete command streams.
- [x] Publish and seal the required draw slots, aligned uniform bytes, and
  descriptor capacity as one immutable generation. `EnsureUniformDrawSlotCapacity`
  and equivalent helpers must not mutate capacity, dirty unrelated command
  buffers, allocate Vulkan objects, or replace generations after the seal.
- [x] Treat a late capacity or descriptor/program-readiness miss as an explicit
  bounded whole-output replan/defer/re-record result
  before submission. Never resize shared mesh state midway through recording,
  silently clamp to the last slot, or continue with a partially prepared
  descriptor/uniform generation. Do not throw `InvalidOperationException`,
  recreate the swapchain, or enter the window circuit breaker for temporary
  `descriptors pending`; the July 15 live reproduction must pass without a
  first-chance exception.
- [x] Make OpenXR prewarm consume the same complete reservation manifest as
  normal recording. It may populate the sealed generation, but it must not grow
  renderer-global capacity or invalidate desktop/preview command buffers.

#### 5.2.4c.3 - Replace Indefinite Retention With Generation Leases

- [x] Add an explicit frame-data generation lease acquired before a command
  buffer captures uniform buffers or descriptor sets. On successful submit,
  transfer the recording lease to the exact queue timeline/completion ticket;
  on recording failure, rejection, cache eviction, or abandoned output, release
  it without waiting for global device idle.
- [x] Keep a superseded generation alive only while a recording lease, cached
  command variant, external ownership obligation, or submitted timeline ticket
  references it. Retire the complete descriptor-pool/uniform generation after
  the last reference and completion point; do not retain it merely because the
  same capacity shape might be requested again.
- [x] Remove or redesign the unbounded `VulkanUniformBufferGenerationCache` and
  per-renderer 32-variant descriptor cache so their bounds derive from active
  families, cached command variants, and frames in flight. An arbitrary smaller
  LRU cap is not sufficient ownership proof.
- [x] When replacing a generation, use exact reverse dependencies to invalidate
  only command variants that captured it. Preserve unrelated desktop, eye,
  preview, capture, and shadow variants and emit the owning output, command
  buffer, generation, lease state, and retirement ticket for any rejection.
- [x] Retire ready generations and their descriptor pools as units with bounded
  per-frame work and backlog-aware draining. Capacity pressure may defer new
  optional work, but it must not call `WaitForAllInFlightWork`, force-flush, or
  create a multi-second retirement backlog. Add an exact-ticket regression that
  advances the graphics timeline beyond a pending pipeline/layout generation
  and proves it drains; no ready retirement may age indefinitely while its
  owning queue completion continues advancing.

#### 5.2.4c.4 - Move Per-Draw Data Into Bounded Arenas

- [x] Replace per-mesh/per-uniform-block Vulkan buffer generations with a small
  number of persistently mapped, alignment-aware frame-data arena chunks owned
  by the renderer frame slot and output/view-family generation. Draw preparation
  writes into reserved ranges and publishes immutable offsets; it performs no
  heap or Vulkan allocation after the manifest is sealed.
- [x] Bind per-draw engine and auto-uniform data through Vulkan dynamic uniform
  offsets or an indexed draw-data buffer. Arena overflow publishes a larger
  generation at a legal pre-record boundary or defers the output; it never
  reallocates an arena referenced by recorded or in-flight work.
- [x] Separate descriptor lifetime tiers: frame/global bindings, output/pass
  bindings for changing render targets and post-process inputs, stable material
  bindings, and per-draw arena offsets/indices. A rotating desktop/OpenXR target
  or mutable frame-source image must not clone otherwise compatible material
  descriptor sets.
- [x] Share compatible material descriptors across desktop and SPS families
  while keeping view-dependent output/pass resources isolated. Encode and test
  the exact compatibility proof; do not infer sharing from coincidentally equal
  handles or frame indices.
- [x] Allocate descriptor sets from generation-owned pool slabs and reset or
  recycle a slab only after its last recording lease and timeline ticket are
  complete. Remove the steady-state one-pool-per-mesh-allocation shape.

#### 5.2.4c.5 - Prove Boundedness And Preserve Correctness

- [x] Add deterministic tests for desktop/SPS alternation, at least three uses
  of one renderer in a command stream, capacity growth between frames, a late
  post-seal capacity request, recorded-but-not-submitted ownership, successful
  lease transfer, aborted recording, cached-command eviction, and timeline-safe
  arena/pool reuse.
- [x] Replace the existing zero-steady-state-retirement cache expectation with
  tests proving that obsolete generations retire after their exact owners
  complete. Run at least 10,000 alternating capacity/output iterations and
  require generation, buffer, pool, descriptor-set, and retirement-queue counts
  to return to declared bounds without a global wait.
- [x] Add structural tests proving descriptor-set count scales with unique
  material/layout plus active output/pass variants and frame generations, not
  draw submissions multiplied by outputs and frame/draw slots. Prove uniform
  Vulkan buffer count scales with active arena chunks, not mesh renderers
  multiplied by uniform blocks and retained capacities.
- [x] Re-run the current CPU-direct F5 Unit Testing World workload with Vulkan,
  Monado, true SPS, `FullIndependentRender`, TSR/bloom, Sponza, UI preview, and
  `CpuQueryAsync`. Preserve desktop and both-eye captures plus profiler/log
  evidence from startup through a settled retained window. The manifest must
  prove no unfinished GPU-instrumented, zero-readback, meshlet, or CPU safety-net
  path became active.

Completed acceptance criteria:

- [x] Descriptor/program readiness never escapes as a first-chance exception or
  triggers swapchain recreation. The owning output defers before recording,
  reports a bounded reason, and later completes from one fully published
  generation.
- [x] Every retirement whose exact queue tickets have completed drains within a
  declared bounded number of frames. A static settled cohort has zero ready-but-
  pending retirement age growth and no stale pipeline/layout retirement entry.
- [x] Observed completed-frame timestamps, achieved Hz, output content age,
  budget classification, and missed-deadline totals agree. A frame above the
  active VR budget increments the appropriate miss/late accounting; telemetry
  may not report requested 90 Hz as achieved throughput.
- [x] The live gate has zero submission rejection, sequential fallback, global
  invalidation fallback, normal-path device-idle wait/force flush, engine-owned
  validation error, first-chance lifetime exception, or device loss.

### 5.2.4b/5.2.4c Final Live Closeout - 2026-07-16

The final CPU-direct Vulkan, Monado true-SPS, `FullIndependentRender`, TSR/bloom,
Sponza, UI-preview, and `CpuQueryAsync` acceptance cohorts passed. The paired
native and 0.67-scale sub-native cohorts each used 120 warmup and exactly 300
retained frames with matched occlusion-off references.

Correctness fixes completed during the closeout:

- [x] Removed the Vulkan zero-result quarantine after restoring the conservative
  `LEQUAL` visible-demotion proxy, so valid negative results can cull without
  self-occlusion from the depth prepass.
- [x] Made directional primary-atlas publication preserve an omitted prior tile
  only when its request key, atlas/page/rect, content revision, residency, and
  fallback allocation still match the current plan.
- [x] Extended the shadow-caster content signature beyond membership to include
  current world transform, draw/binding state, and culling bounds, so a moving
  caster invalidates the directional tile deterministically.
- [x] Excluded auxiliary shadow viewports from the presentation-output ledger.
- [x] Made the resource gauge audit compare adjacent terminal steady-state
  windows while preserving global bounds and rejecting continuing growth.

Final 5.2.4b live correctness acceptance:

- [x] Main desktop and true-SPS `OcclusionViewKey` instances independently
  sustained nonzero query submission, resolution, and valid culling under
  `FullIndependentRender`. Native totals were 694 desktop submissions/
  resolutions with 5,006 culls and 650 SPS submissions/resolutions with 200
  culls; maximum result age was two frames.
- [x] Both-layer captures, the complete 300-frame ledger, raw and filtered logs,
  capture inventories, off/enabled comparisons, and the tracked machine-readable
  manifest were preserved. Focused rendered-output and source-contract tests
  passed 87/87.
- [x] All nine native and all nine sub-native desktop/left/right off-enabled
  image comparisons passed. Maximum RMSE was 0.001807 native and 0.001927
  sub-native against the 0.01 limit.
- [x] The refreshed strict-SPS failure matrix passed Capability, Target,
  Recording, LifetimeValidation, Submit, and Publish without sequential eye
  fallback.

Final 5.2.4c boundedness and ownership acceptance:

- [x] Retained device/upload allocation frames, descriptor pool create/destroy/
  reset frames, resource-plan replacement frames, and retirement frames were
  all zero.
- [x] Live resources settled at 3,969 and tracked descriptor sets at 1,438 in
  both adjacent terminal 30-frame windows. Planner-state and command-variant
  counts remained three, and every retained frame-data manifest was sealed.
- [x] CPU command-record p95 remained bounded across the retained cohort; the
  native CPU/GPU p95 values were 184.11/28.72 ms and the sub-native values were
  149.38/31.62 ms. No multi-second F5 render-thread freeze recurred.
- [x] No post-seal capacity mutation, indefinite generation retention,
  ready-but-pending retirement, submission rejection, engine-owned validation
  error, sequential fallback, or device loss occurred.
- [x] Desktop and both eye outputs remained continuous and nonblack throughout
  the required retained windows.

Durable evidence:

- `docs/work/testing/rendering/vulkan-core-hardening-phase524b-validation-2026-07-10.json`
- `Build/_AgentValidation/p524b-final-w120/reports/vulkan-phase524b-validation.json`
- `Build/_AgentValidation/p524b-final-w120/subnative-companion/reports/vulkan-phase524b-validation.json`
- `Build/_AgentValidation/p524b-off-w120/reports/openxr-smoke-summary.json`
- `Build/_AgentValidation/20260710-openxr-strict-stereo/20260715-remaining-closeout/final-shadow-fix-strict-sps/reports/openxr-strict-sps-failure-matrix.json`

With every 5.2.4b and 5.2.4c live criterion complete, Phase 5.2.5 is unblocked.

## Continuing Work

All unchecked criteria and future phases remain in the
[current todo](vulkan-core-hardening-and-device-loss-todo.md).

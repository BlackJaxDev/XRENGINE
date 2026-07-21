# Vulkan Core Hardening And Device-Loss Completed Work

Last Updated: 2026-07-20
Owner: Rendering
Status: Historical completion and evidence record through the 2026-07-20 Phase 5.2.6 implementation move
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

### Zero-Readback BVH-Refit Device-Loss Fix - 2026-07-17

- Dispatch-level isolation proved the recurring Sponza-loaded TDR was not the
  indirect-count command or the `GPURender*` cull/build/scatter programs. The
  reset persisted with every `GPURender*` compute shader skipped, stopped when
  BVH shaders were skipped, and remained absent for 113 seconds when only
  `bvh_refit` was skipped.
- `bvh_build.comp` can flag malformed parent connectivity from duplicate Morton
  codes, but the immediately queued refit previously followed those parent
  pointers without a termination bound. `bvh_refit.comp` now validates its
  buffer indices and caps propagation to the safe node count. This prevents a
  parent cycle from becoming an unbounded GPU loop without introducing a CPU
  readback, device wait, fallback, or extra frame resource.
- Deferred Vulkan compute snapshots retain immutable buffer handles, ranges,
  and usage, so later command recording does not silently resolve a replacement
  backend generation.
- With refit active and no dispatch skipped, two traced Release launches stayed
  healthy for 121 and 120 seconds. The later clean StandardValidation
  performance runs completed with zero VUIDs, readback, mapping, or fallback;
  their workload identity, draw count, multi-draw count, and triangle count
  matched the retained baseline.
- Focused validation passes 23/23, including refit shader compile/link,
  build-plus-refit root bounds, and the malformed-parent guard invariant. The
  Release editor builds with zero errors.
- Visual Sponza parity and the multi-hundred-millisecond CPU recording cost are
  intentionally not recorded as complete. They remain in the active P0.3,
  P0.6, and P0.7 checklist.

Detailed evidence:
[zero-readback Sponza device-loss investigation](../../investigations/rendering/vulkan-zero-readback-sponza-device-loss-2026-07-17.md).

### P0 Immediate Gate Closeout - 2026-07-18

The immediate desktop visibility, device-loss, and recording gate is complete.
The production acceptance path is the validated serial/cached
GpuIndirectZeroReadback lane; diagnostic optimization and external-tool
limitations remain explicit post-P0 work in the active tracker.

Completed correctness work:

- Imported-texture reuse now compares requested storage against the actual
  physical image layout and format. Progressive mip publication no longer
  reuses incompatible storage, eliminating the observed image-copy VUIDs and
  streaming-time device loss.
- Zero-readback Vulkan temporarily uses flat GPU frustum culling instead of the
  corrupt GPU BVH publication header. This remains GPU culling with no CPU
  fallback or readback. The retained BVH diagnostic fields expose the raw and
  safely clamped node counts for the later publication-version repair.
- Delayed post-fence GPU statistics and material-scatter diagnostics establish
  real visible draws. The accepted lane reached 46-50 culled commands and
  247,765-262,267 triangles while submitting bounded indirect tiers.
- The matched Sponza camera, second pose, and motion sequence all render scene
  geometry and change with camera motion. Zero-readback requested/consumed
  counts remain equal with zero readback, mapping, fallback, and stale
  collection reuse.
- Command-local lifetime dependencies are cleared after incremental
  publication. Shader invalidation is marshalled to the main thread. This fixes
  the SyncValidation hot-reload crash that previously attempted to record a
  retired pipeline generation.
- Command-buffer tracking batches retain their grown collection capacity across
  recordings. Framebuffer attachment planning, exact layout arrays, final-layout
  writes, and planned-signature matching now reuse arrays or stack spans instead
  of allocating transient List/HashSet/string/int-array state.
- XRE_FORCE_CPU_INDIRECT_BUILD exposes the CPU-built diagnostic reference only
  for GpuIndirectInstrumented. The material-batched path now rebuilds indirect
  commands and contiguous material ranges instead of submitting stale/empty
  data. The override is cached and performs no per-frame environment lookup.

Measured closeout:

- Four StandardValidation cohorts supplied 12 identical warmed Release
  repetitions across static/moving camera and occlusion-disabled/CpuQueryAsync
  paths. All runs completed without device loss, early exit, VUID, submission
  rejection, readback, mapping, fallback, or stale visibility reuse.
- Those cohorts recorded 31.507-34.035 ms render p50,
  34.747-37.182 ms render p95, and 24.014-25.428 /
  25.705-27.847 / 32.881-40.617 ms command-record p50/p95/max. The former
  multi-hundred-millisecond spike is absent.
- The final SyncValidation interaction session
  xrengine_2026-07-18_05-59-49_pid1852 survived streaming, camera motion,
  resize, shader hot reload, and normal shutdown. A later 406-sample
  forced-record SyncValidation cohort also recorded zero VUIDs, readbacks,
  mappings, fallbacks, submission rejections, and stale collection reuse; it
  crossed two workload identities and is retained as correctness evidence, not
  a stable performance baseline.
- Forced-primary allocation fell from approximately 888.5 KiB/frame to
  697.4 KiB after tracking-batch reuse and then to 322.7 KiB after framebuffer
  planning cleanup. The final comparable manifest is
  Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-18_13-01-09/summary.json.
  Cached stable production frames already reach zero recording allocation; the
  remaining forced diagnostic cost is assigned to Phase 5.2.5.
- The integrated Release editor build completes with zero errors. Focused P0
  policy, lifetime, shader, parity, allocation, and batched-reference tests pass
  56/56. Existing NuGet vulnerability advisories and two unrelated Surfel GI
  unassigned-field warnings remain.
- Nsight Systems 2026.3.1 captured 31 Vulkan frames and 564,644 sampled
  callchains but exported no Debug Utils marker table; its environment retry
  failed inside Nsight. RenderDoc 1.44 with rdc-cli 0.5.6 passes preflight but
  the bounded Vulkan retries serialized no capture. Engine label emission is
  implemented, but no external marker visualization is claimed.

Representative durable/local evidence:

- Build/_AgentValidation/20260718-vulkan-p0-closeout/mcp-captures/flat-gpu-zero-readback-matched-camera-printwindow.png
- Build/_AgentValidation/20260718-vulkan-p0-closeout/mcp-captures/motion-valid-contact-sheet.jpg
- Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-04-21/summary.json
- Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-09-09/summary.json
- Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-13-58/summary.json
- Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-18-51/summary.json
- Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-18_13-54-06/summary.json

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

## Immediate Priority Gate Completed Work - 2026-07-17

This section preserves the completed implementation and acceptance criteria
moved from the active tracker. P0.2, P0.3, P0.6, and P0.7 still have open
criteria in the [current TODO](vulkan-core-hardening-and-device-loss-todo.md);
the completed items below do not imply that the aggregate immediate gate has
closed.

Historical before-baseline evidence:

- The desktop visibility late policy defaulted to `ReusePreviousVisibility`,
  despite the documented `BlockUntilFresh` contract. A late collect could
  therefore render an old visible set with the new camera.
- The profiled Debug run reported render-dispatch p50 near 33 ms,
  `CollectVisible` p50 near 2-3 ms, and GPU command-buffer p50 near 9 ms. Full
  primary recording reached hundreds of milliseconds, so collection and
  occlusion queries were not the primary steady-state bottleneck.
- The scene command-recording timer omitted the actual scene-record call and
  reported overlay time instead. The old
  `vulkan_frame_record_command_buffer_ms` value was not used for before/after
  decisions.
- The startup fingerprint selected `CpuQueryAsync`, so it was not a valid
  occlusion-disabled comparison. The work captured a controlled
  disabled/enabled pair.
- The desktop run logged 223 application-level pipeline cache misses, pending
  programs/buffers, skipped draws, and texture-publication descriptor changes
  that dirtied command buffers for every swapchain image.

### P0.1 - Restore Generation-Correct Visibility Handoff

- [x] Make `BlockUntilFresh` the default and invalid-value fallback for the
  collect-visible late policy. Keep stale visibility reuse opt-in and diagnostic
  only.
- [x] Give every collect request, published command buffer, and render
  consumption a monotonically increasing frame/generation ID. Render frame
  `N+1` must wait for and consume the publication requested for `N+1`; a signal
  from an older generation must not satisfy the gate.
- [x] Preserve the existing overlap boundary: after frame `N` has consumed and
  submitted its rendering buffer, release collection for `N+1` before a
  potentially blocking present. Do not serialize collection behind present or
  begin collection early enough to mutate data still consumed by frame `N`.
- [x] Make buffer publication atomic: build the next visible-command buffer in
  isolation, publish/swap it once, and never append to or clear the rendering
  buffer while it is being consumed.
- [x] If `ReusePreviousVisibility` remains available, require an explicit
  setting, report requested/published/consumed generations and content age, and
  bound reuse to one policy-authorized frame. It must never silently become the
  normal desktop path.
- [x] Audit cancellation, shutdown, exception, and device-loss paths so every
  waiter is released with a terminal result. No render or collect thread may
  remain indefinitely blocked after an exception.
- [x] Add deterministic timing tests that delay collection across the render
  boundary and prove `BlockUntilFresh` consumes the matching generation without
  deadlock, while the explicit stale-reuse mode reports exactly one stale frame.
- [x] Add a moving-camera regression that crosses frustum boundaries with all
  occlusion culling disabled and proves newly visible meshes appear in the first
  frame whose matching visibility collection contains them.

Completed acceptance criteria:

- [x] Desktop rendering uses generation-matched visibility by default and shows
  no one-frame frustum pop when the camera moves.
- [x] Collection for frame `N+1` overlaps frame `N` presentation/remaining work,
  but render `N+1` cannot consume frame `N` visibility accidentally.
- [x] Forced collect delays, collection exceptions, render exceptions, shutdown,
  and device loss complete without a frozen render/collect wait.

### P0.2 - Repair Timing And Establish A Controlled Baseline

- [x] Fix the scene command-recording timer so it surrounds the actual primary
  or secondary record call and is accumulated before the timestamp is reused
  for ImGui/overlay work.
- [x] Split CPU telemetry into frame-op drain/sort/signature, resource planning,
  frame-data refresh, packet construction, primary recording, secondary
  recording by worker, render-thread worker wait, submit, present, and visibility
  wait. Keep GPU execution timestamps separate from CPU recording time.
- [x] Report why each primary/secondary range was recorded, reused, invalidated,
  or evicted. Include visibility generation, structural signature, descriptor
  generation, pipeline readiness, swapchain slot, and explicit forced-record
  reasons without allocating strings in the normal hot path.
- [x] Record per-scope managed allocation and high-water capacity for
  collection, collect swap/publication, frame-op construction, planning,
  recording, descriptor publication, and submission.
- [x] Capture matching Release desktop runs with validation and detailed
  profiling disabled for performance, then separate validation-enabled runs for
  correctness. Preserve the exact settings, camera path, scene hash, startup
  strategy fingerprint, resolution, and warmup interval.
- [x] Capture an explicit `Disabled` versus `CpuQueryAsync` occlusion pair using
  the same camera path and workload. Compare collection, visible draws,
  recording, CPU frame, and GPU frame rather than FPS alone.
- [x] Capture an Nsight Systems Vulkan API trace with CPU sampling. Use
  RenderDoc only for pass/resource correctness, not as the authoritative CPU
  recording measurement.

Completed acceptance criteria:

- [x] Internal CPU scopes reconcile with the Nsight API timeline within an
  explained tolerance, and no scope labels overlay time as scene recording.
- [x] The baseline identifies CPU p50/p95/p99/worst, GPU p50/p95/p99, full and
  partial record counts, allocation, pipeline readiness, descriptor
  invalidations, and exact occlusion mode.

### P0.3 - Replace Monolithic Primary Recording With Stable Packets

- [x] Make the per-frame primary command buffer intentionally thin: record only
  required transitions/barriers, dynamic-rendering begin/end, ordered secondary
  execution, timestamps, and other truly frame-variant top-level work.
- [x] Stop keying a large reusable primary variant on the complete visible draw
  set. Camera-dependent membership changes must not create an unbounded stream
  of primary variants or churn the current per-swapchain LRU cache.
- [x] Lower visible work into coarse, deterministic packets grouped by render
  pass, pipeline/material state, resource contract, and ordering constraints.
  Separate structural packet identity from frame-varying transforms, constants,
  and draw counts.
- [x] Record independent dirty packets as secondary command buffers on workers.
  Give each worker one persistent command pool and bounded scratch arena per
  frame-in-flight slot; reset the whole pool only after that slot's fence or
  timeline completion.
- [x] Use `VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT` for freshly recorded
  per-frame buffers. Use simultaneous-use or reusable command buffers only when
  their measured lifetime and submission contract actually require it.
- [x] Benchmark packet granularity instead of creating one secondary per draw.
  Record draw count and CPU/GPU cost per secondary, start with a minimum near ten
  compatible draws, and tune from measured p95 and GPU execution behavior.
- [x] Cache only genuinely stable secondary ranges such as unchanged static
  geometry or non-variable post-process work. Re-record cheap thin primaries
  instead of maintaining a combinatorial cache of whole frames.
- [x] Remove serial per-op resource/context switching from the render-thread
  main loop. Pre-resolve packet state and dependencies so the render thread only
  validates ordered packet readiness and emits/executes the final list.
- [x] Preserve explicit dynamic-rendering and legacy render-target parity,
  deterministic transparent ordering, query boundaries, debug labels, device-
  loss containment, and resource lifetime publication.

Completed acceptance criteria:

- [x] A warmed static desktop scene records only a thin primary and reuses
  stable packets; camera motion rebuilds visibility/packet membership without
  rerecording unrelated static ranges.
- [x] No primary/secondary cache grows with camera positions or swapchain image
  rotations; all cache capacities and evictions are reported and bounded.

### P0.4 - Decouple Descriptors And Streaming From Command Structure

- [x] Classify descriptor changes as frame-data updates, compatible content
  publication, binding-identity changes, or structural layout changes. Only the
  last two may invalidate a command range, and each invalidation must name the
  affected packet rather than dirty every swapchain primary.
- [x] Move transforms, material parameters, and other frame-varying values into
  bounded per-frame ring/storage buffers addressed by stable offsets or indices.
- [x] Select and document the material-texture binding contract: per-frame
  descriptor sets updated only after their frame slot completes, or descriptor
  indexing/update-after-bind with explicit feature checks and safe resource
  lifetime rules. Never update a descriptor while an incompatible in-flight use
  can observe it.
- [x] Keep descriptor set/layout identities stable across ordinary imported-
  texture content publication. Publish the new resource generation at a safe
  point without structurally invalidating unrelated scene packets.
- [x] Add tests for texture streaming, material edits, resize, hot reload, and
  swapchain rotation that verify exact packet invalidation, descriptor contents,
  and deferred destruction behind completion.

Completed acceptance criteria:

- [x] Streaming a compatible texture no longer dirties all desktop command
  buffers or causes unrelated draws to disappear while descriptors catch up.
- [x] Descriptor updates and resource retirement remain validation-clean with
  all frames-in-flight occupied and with forced publication delays.

Implementation checkpoint (2026-07-16): coarse packet execution, bounded
command-chain primary/secondary caches, persistent per-frame-slot recording
workers, packet-scoped planner state, descriptor copy-on-write publication, and
dynamic/legacy render-target parity are implemented. Dynamic and legacy live
runs are validation-clean, and texture streaming, material editing, resize, hot
reload, swapchain rotation, and delayed publication have focused regression
coverage. The remaining P0.3 work is the explicit zero-managed-allocation and
warmed Release p95/worst-frame measurement gate; it is not required to reopen
the completed packet or descriptor correctness work. See
[the implementation investigation](../../investigations/rendering/vulkan-stable-packets-and-descriptor-publication-2026-07-16.md).

#### 2026-07-17 steady-state allocation cleanup

- [x] Remove per-draw `RenderPacket` creation, exact-length packet/group arrays,
  group-key `ToArray()` calls, replacement distinct-view sets, and per-bucket
  indirect state reference scopes from the steady command-chain path. Packet,
  group, and schedule storage now grows geometrically and is reused; lowering
  retains its packet pool, draw scratch, structural-occurrence map, and
  distinct-view set.
- [x] Add a focused warmed regression that resets a maximum-sized packet, group,
  and schedule 1,000 times and verifies zero managed bytes. The focused Vulkan
  stable-packet suite passes 12/12.

End-to-end Release allocation and p95/worst-frame acceptance remains open in
the current TODO because it requires an uncontended live measurement cohort.

#### 2026-07-17 live allocation and indirect-counter checkpoint

- [x] Remove steady descriptor-name substring allocation by caching normalized
  `Buffer`/`Input` binding aliases, and avoid per-call captured-program hash
  string identities when program reference identity is sufficient.
- [x] Retain compute descriptor refresh state for every `(image, binding)` pair
  instead of rebuilding compute descriptor collections when snapshots alternate.
- [x] Remove zero-readback LOD-transition synchronization from ordinary CPU
  scene mutations; upload only reset indices and synchronize the full mapped
  buffer only before a real resize. Stable zero-readback runs now report zero
  GPU readback bytes and zero mapped buffers across startup, warmup, capture,
  and shutdown sampling.
- [x] Add machine-readable requested/consumed draw totals, indirect API-call
  totals, and submitted-indirect-draw totals to the performance manifest.

Evidence:

- `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_06-47-55/summary.json`:
  75 stable zero-readback samples, exact full-run requested/consumed parity,
  2,250 indirect API calls, 144,000 submitted indirect draws, zero readback,
  zero mapped buffers, and zero allocation in every measured stage except
  frame-data refresh.
- `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_06-56-41/summary.json`:
  directly comparable warmed smoke for `CpuDirect`,
  `GpuIndirectInstrumented`, and `GpuIndirectZeroReadback`; all three recorded
  zero VUIDs. The zero-readback lane recorded 15.817 ms render p50 and
  17.065 ms p95 with exact capture requested/consumed parity. This is a smoke
  cohort, not the required three-by-60-second acceptance matrix.
- Focused contracts: 80/80 passing across
  `VulkanStablePacketAndDescriptorTests`, `VulkanCoreHardeningPhase4Tests`, and
  `OpenXrTimingPipelineContractTests`.

#### 2026-07-17 end-to-end zero-allocation closeout

- [x] Replace compute engine-uniform `Enum.TryParse` with an allocation-free
  span switch that preserves vertex-stage aliases without allocating a suffix
  substring.
- [x] Replace compute auto-uniform `Array.GetValue` with typed scalar, vector,
  matrix, and boolean-vector array writers, retaining a direct reference-array
  fallback without boxing value arrays.
- [x] Remove pipeline-fingerprint iterator/LINQ allocation, use the already
  sorted descriptor layout publication, index the common pass-metadata list,
  and provide explicit allocation-free equality/hashing for pipeline stencil
  state.
- [x] Verify `GpuIndirectZeroReadback` and `CpuDirect` live Release captures
  with zero managed bytes in frame-op preparation, resource planning,
  frame-data refresh, packet construction, primary/secondary recording,
  descriptor publication, submission, and the aggregate command-record path.

Evidence:

- `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_07-10-18/summary.json`:
  76 stable zero-readback samples; every measured Vulkan allocation field is
  zero, requested/consumed draw totals match exactly, 2,250 indirect API calls
  submit 144,000 draws, and full-run readback/mapped bytes remain zero.
- `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_07-16-03/summary.json`:
  198 stable CPU-direct samples; every measured Vulkan allocation field is
  zero, capture command-buffer records are zero, and render p50/p95/worst are
  9.940/11.181/15.014 ms.
- EventPipe traces under
  `Build/_AgentValidation/20260717-vulkan-p0-allocation-trace/` identify the
  repaired enum, array-boxing, fingerprint, metadata-enumerator, and pipeline-
  key call stacks. These traces are disposable supporting evidence; the
  machine-readable performance summaries above are the durable paths.
- Focused contracts remain 80/80 passing after the closeout changes.

### P0.5 - Make Pipeline Compilation Cache-Aware And Ready Before First Draw

- [x] Route background graphics-pipeline compilation through a valid Vulkan
  pipeline cache. Either use a measured internally synchronized shared cache or
  seed one cache per worker from persisted data and merge worker caches at safe
  points; do not pass a null cache on the normal worker path.
- [x] Distinguish application pipeline-object lookup misses, persisted Vulkan
  cache hits/misses, compile-required results, and actual compile duration in
  telemetry.
- [x] Capture stable pipeline descriptions for required render-pass/material/
  vertex-layout/specialization variants and prewarm them before those variants
  first become visible. Version the database against device, driver, shader,
  layout, and relevant renderer identity.
- [x] Bound asynchronous compilation queues and explicitly report policy when a
  requested draw is not ready. Normal warmed desktop navigation must not skip a
  mesh silently because a known pipeline or buffer is still pending.
- [x] Add cold-cache and warm-cache tests covering startup, imported-scene
  streaming, motion-vector variants, material edits, and cache save/reload.

Completed acceptance criteria:

- [x] A second identical warm run creates no known pipeline at first draw,
  records no unexplained skipped draw, and demonstrates persisted-cache use by
  background workers.
- [x] Cold compilation stays off the render thread and has bounded queue,
  memory, and frame-impact telemetry.

Implementation checkpoint (2026-07-16): graphics pipeline creation uses the
persistent internally synchronized Vulkan cache on bounded background workers.
All primary, dynamic-UI, and scheduled-secondary mesh paths now establish
pipeline readiness before `vkBeginCommandBuffer`; a pending or capacity-limited
request defers recording instead of emitting a partial command stream. Stable
v5 prewarm identities use deterministic hashes and include device/driver,
shader artifact, descriptor/vertex layout, pass, feature, and fixed-function
state. The final StandardValidation warm run recorded 238 persisted-cache hits,
zero compile-required misses, no draw that emitted zero commands, no VUID or
validation error, no `InvalidOperationException`, and no device loss. See
[the pipeline prewarm investigation](../../investigations/rendering/vulkan-pipeline-cache-prewarm-2026-07-16.md).

### P0.6 - Multi-Draw Indirect Implemented Core

- [x] Add an explicitly selected, capability-gated multi-draw-indirect lane for
  indexed material/state buckets. Unsupported indirect-count or meshlet
  strategies resolve through the documented resolver and emit a visible
  downgrade reason rather than silently changing lanes.
- [x] Store draw metadata, transforms, materials, bounds, mesh data, compact
  commands, and counts in stable GPU-scene buffers. The zero-readback lane
  consumes GPU-written counts and does not construct one managed frame operation
  per visible mesh.
- [x] Preserve transparent and special-pass ordering through explicit
  transparency/state buckets and the direct excluded-work path. CPU safety-net
  fallback remains limited to the explicitly instrumented strategy and is
  reported.
- [x] Retain an explicit CPU-built indirect reference path under
  `GpuIndirectInstrumented` and `ForceCpuIndirectBuild`; production
  `GpuIndirectZeroReadback` uses GPU visibility/material scatter/compaction and
  count-buffer submission without count readback.

Visual/counter parity captures and the full `CpuDirect` versus CPU-built versus
GPU-built live matrix remain open in the current TODO.

### P0.7 - Reproducible Gate Harness Additions

- [x] Extend `Measure-GameLoopRenderPipeline.ps1` and its Vulkan wrapper with
  explicit occlusion-mode, Vulkan diagnostic-preset, and command-buffer-label
  inputs. Every selection is seeded before process launch and retained in the
  JSON/text result manifest, allowing identical three-repetition Disabled,
  CpuQueryAsync, GpuHiZ, StandardValidation, and SyncValidation cohorts without
  relying on Nsight Systems' failing `--env-var` option.

### P0 StandardValidation Stability-Matrix Closeout

- [x] Complete three identical 60-second warmed Release desktop repetitions for
  each static/moving camera and occlusion-disabled/`CpuQueryAsync` combination.
  All 12 runs reached stable sampling and completed without early exit, device
  loss, VUID, submission rejection, GPU readback, mapped-buffer use, or stale
  visibility reuse. Generation age never exceeded one frame and every run
  reported exact full-run requested/consumed draw parity.
- [x] Preserve the four machine-readable cohort manifests:
  - static, occlusion disabled:
    `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-04-21/summary.json`;
  - moving, occlusion disabled:
    `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-09-09/summary.json`;
  - static, `CpuQueryAsync`:
    `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-13-58/summary.json`;
  - moving, `CpuQueryAsync`:
    `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-18-51/summary.json`.
- [x] Establish the post-fix performance envelope. Across the 12 runs, render
  p50/p95/worst was 31.507-34.035/34.747-37.182/47.417-54.701 ms; Vulkan GPU
  command p50 was 1.192-1.215 ms; command-record p50/p95/max was
  24.014-25.428/25.705-27.847/32.881-40.617 ms. This removes the prior
  multi-hundred-millisecond recording spike without relaxing validation or the
  zero-readback policy.
- [x] Stop copying unused image-subresource layout snapshots for command-buffer
  variants. The reusable snapshot now stores only its deterministic signature,
  and physical image groups append that signature without allocating arrays.
- [x] Correct forbidden-fallback telemetry. A valid zero-visible cull is no
  longer classified as attempted CPU recovery; the counter is emitted only
  when an operator actually requested fallback on an instrumented strategy.
  The post-fix moving/`CpuQueryAsync` proof at
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-25-01/summary.json`
  reports zero full-run and capture fallback events, zero readback, zero VUIDs,
  and exact requested/consumed parity.
- [x] Capture a final validation-clean `CpuDirect` numeric control at
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-07-17_20-25-53/summary.json`.
  Its 49 direct draw calls and 301,924 triangles are retained as a control, not
  misrepresented as visual parity with the bucketed lane.
- [x] Validate the final contracts: Release runtime/editor builds complete with
  zero errors and the focused Vulkan timing/indirect selection passes 73/73.

The forced-primary path still allocates approximately 0.92 MiB per frame, and
the matched-camera zero-readback result has not superseded the user's missing-
Sponza observation. Visual captures, SyncValidation interaction cohorts, and a
valid external Vulkan marker/capture artifact therefore remain in the active
TODO; this completed matrix does not close those acceptance criteria.

## Phase 5.2.5 And 5.2.6 Completed Implementation - 2026-07-20

The following checked implementation criteria moved from the active tracker on
2026-07-20. Acceptance criteria and validation-dependent promotion work remain
in the [current todo](vulkan-core-hardening-and-device-loss-todo.md).

### 5.2.5 - Versioned, Nonblocking Render Plans And Resource Arenas

- [x] Make resource-generation key construction deterministic before versioning
  plans. `BuildResourceFeatureMaskForGenerationKey` must derive every
  feature-affecting setting from the explicit `XRRenderPipelineInstance` and
  `XRViewport` (or an immutable settings snapshot owned by them), never from
  ambient `RuntimeEngine.Rendering.State.CurrentRenderingPipeline` state.
  Refactor AO resolution accordingly so resize, internal-resolution, explicit
  invalidation, and per-frame checks observe the same enabled state and AO type.
- [x] Route all resource-generation requests through the same feature snapshot
  and revision. Add a diagnostic assertion/counter when two requests for the
  same pipeline, output kind, extent, and settings revision produce different
  feature masks or structurally different layouts.
- [x] Break the desktop resize failure loop observed in
  `xrengine_2026-07-16_09-14-14_pid17808`: the resize path resolved no camera and
  requested `features=0x20CE101`, while frame preparation resolved GTAO and
  requested `features=0x220CE101`. The alternating `ViewportResized` and
  `FrameProfileChanged` requests continually superseded the pending generation,
  left the active desktop generation at `1920x1080` after the window reached
  `2560x1369`, and skipped desktop command execution while the independent
  OpenXR eye generation continued submitting with `deviceLost=False`.
- [x] Decouple swapchain convergence from managed render-resource catch-up. Once
  the live and swapchain extents agree, a pending pipeline generation must not
  repeatedly recreate the same swapchain or grow retired external-image state;
  keep presentation fail-closed until the new generation commits, with bounded
  last-completed-content reuse and explicit progress telemetry.
- [x] Redefine planner/cache identity around physical compatibility: output kind,
  view-family/pipeline identity, extent, pass graph, attachment signature,
  resource-plan generation, and queue family. Do not key the compiled plan on a
  rotating external swapchain/OpenXR image handle.
- [x] Bind the current external target slot as a bounded plan/command variant;
  keep plan identity stable across acquisition rotation.
- [x] Store immutable compiled plan generations. A replacement publishes a new
  generation while old plans, allocators, descriptors, and resources remain
  alive behind their last submitted timeline/retirement ticket.
- [x] Remove `WaitForAllInFlightWork` and force-flush calls from command
  recording, planner-state prune, physical-plan replacement, imported-texture
  replacement, and normal cache eviction.
- [x] Evict planner states incrementally only after their last-use timeline is
  complete. Cache capacity pressure must defer/retire work, not globally drain
  the device.
- [x] Give each concurrently active output family a bounded persistent resource
  arena and candidate transient-lifetime plan. Reuse compatible allocations
  across frames, but keep physical aliasing disabled until scheduled lifetimes
  and semaphore-constrained execution prove non-overlap.
- [x] Separate runtime-owned external images from engine-owned allocation and
  retirement. External image rotation must not churn engine resource plans.
- [x] Add plan-cache hit/miss, plan-generation, arena high-water, alias reuse,
  pending-retirement bytes, and eviction-defer telemetry by output family.

Command-recording dependency and reuse requirements:

- [x] Define one immutable command-recording dependency signature used by
  primary variants, secondary ranges, and command-chain schedules. Include the
  output/pass and attachment signature, render area, view mask, queue family,
  dynamic-rendering inheritance, pipeline/layout generation, mesh/index/vertex
  binding identity, buffer/image/view/sampler allocation generation,
  descriptor-layout/set/publication generation, resource-plan generation, and
  bounded external-target/frame-slot variant.
- [x] Build the signature from the prepared immutable plan/recording snapshot,
  never by rereading mutable renderer or descriptor state during reuse choice.
- [x] Classify invalidation as `Structural`, `BindingIdentity`, or `DataOnly`.
  Structural changes rebuild the relevant plan and recorded ranges;
  binding-identity changes invalidate only ranges that consume the binding;
  data-only publication into a completed frame slot preserves compatible
  command recordings.
- [x] Move the minimum descriptor/resource/publication-generation fingerprint
  required for safe command reuse into Phase 5.2.5. Phase 6 expands descriptor
  coverage and diagnostics; it must not be a prerequisite hidden behind Phase
  5.2A's reuse acceptance gate.
- [x] Replace `VulkanPrimaryCommandBufferReuseSafe = false` with validated
  capability derived from the complete dependency contract. Remove permanent
  hard-off behavior; settings and environment overrides may select a diagnostic
  policy but must not substitute for correctness validation.
- [x] Replace global command-buffer dirtiness for local resource mutations with
  dependency-indexed invalidation. Every miss reports the first incompatible
  signature field and affected range/family.
- [x] Keep static primary/secondary topology separate from volatile overlays,
  uploads, queries, and presentation. A volatile suffix must not force static
  opaque, skybox, shadow, or fixed post-process ranges to rerecord.

Vulkan `CpuDirect` dynamic-data requirements:

- [x] Define a stable per-object/per-material/per-view/per-pass data layout for
  transforms, previous transforms, material IDs, skinning/blendshape IDs,
  editor IDs, flags, and pass masks. Update dirty ranges instead of rebuilding
  or uploading all visible-object data.
- [x] Route ordinary dynamic data through bounded frame-indexed, persistently
  mapped host-visible upload arenas and safe device-local copies or direct
  bindings as appropriate. Use timeline/frame-slot completion to prevent range
  overwrite without blocking the render thread.
- [x] Use stable descriptor bindings plus dynamic/frame-slot offsets where
  practical. Camera motion, animation, and value-only material changes must
  update bytes without changing the recorded binding topology.
- [x] Make resizable Vulkan buffers capacity-based. Grow only when capacity is
  exceeded, publish the replacement as a new safe generation, and update used
  subranges thereafter. Exact logical element-count changes must not recreate
  backing storage every frame.
- [x] Apply the capacity contract to editor/debug geometry, including
  `LinesBuffer`, so mesh bounds and other variable debug primitives do not emit
  steady `VkDataBufferRecreated` invalidation.
- [x] Sort/bucket opaque CPU-direct packets by compatible pass, pipeline/state
  class, material binding layout, and mesh binding where semantics permit.
  Preserve transparent, UI, editor-overlay, and explicitly ordered diagnostic
  behavior.

Pipeline and shader readiness requirements:

- [x] Derive required graphics/compute pipeline variants from the compiled
  structural plan and prewarm them before a cohort enters steady-state
  measurement. Include CPU-direct, GPU-indirect, meshlet, shadow, velocity,
  editor-ID, override, stereo/multiview, and dynamic/legacy target variants that
  the workload can reach.
- [x] Keep pipeline creation, shader compilation, texture residency, and asset
  streaming outside command recording. Separate startup, warmup, streaming, and
  steady-state profiler phases.
- [x] If optional asynchronous compilation is still pending, defer only the
  dependent optional node with an explicit reason. A required production pass
  must be ready before submission; it must not reject or defer the entire frame
  after declared warmup.
- [x] Version pipeline-cache entries and retire replaced pipelines behind their
  last submitted timeline without globally invalidating unrelated recordings.

Render-graph dataflow requirements:

- [x] Represent every resource use with resource identity, subresource range,
  stage, access, layout, and read/write intent; version each logical resource
  after every write.
- [x] Derive producer-to-consumer dependencies from resource versions instead
  of declaration order alone. Reject cycles with the dependency chain and
  reject reads of uninitialized internal resources unless they are explicitly
  imported with a valid initial state.
- [x] Calculate transient lifetimes from the scheduled graph and real queue
  waits. Preserve synchronization2 barriers, queue-family transfers, timeline
  waits/signals, and binary WSI synchronization in the same plan.
- [x] Batch adjacent same-queue passes when it keeps submission count bounded
  without sacrificing useful overlap or deadline boundaries.
- [x] Publish an immutable, versioned `VulkanRenderGraphPlan` whose cache
  identity includes the structural pass graph, resource versions, attachment
  signature, queue plan, and output contract, but excludes rotating external
  image handles and transient matrices.
- [x] Emit a graph dump containing pass order, resource versions, derived and
  explicit edges, barriers, queue assignments, submissions, output deadlines,
  lifetimes, and predicted/measured durations.
- [x] Keep physical-image aliasing disabled until tests prove candidate
  lifetimes cannot overlap across actual asynchronous execution intervals.

### 5.2.6 - Immutable Snapshot, Logical View Set, And View-Family DAG Completed Implementation

#### 5.2.6.1 - Publish One Scene Snapshot And Canonical Frame View Set

- [x] Publish one immutable render-world snapshot per engine frame containing
  stable scene objects, transforms, lights, materials, shadow/probe state, and
  GPU-scene references. Output workers must never reread mutable scene state.

- [x] Add a focused, allocation-free frame-view builder that captures OpenXR,
  desktop, secondary, mirror, and other active output descriptors into one
  `RenderFrameViewSet` after the relevant OpenXR views are located.

- [x] Build OpenXR descriptors from actual located poses, projections,
  recommended extents, output layers, and previous-view-projection histories.
  Do not synthesize wide/inset entries by copying ordinary eye matrices.

- [x] Represent wide/inset parent relationships explicitly and validate the
  pose, projection, containment, and depth-convention assumptions required for
  shared visibility or Hi-Z use.

- [x] Preserve inactive stable IDs or use an explicit stable-key mapping so
  histories never migrate when an optional output toggles. Add an explicit
  secondary/published-output role if `Debug` is not a durable semantic owner.

- [x] Add an allocation-free adapter from `RenderFrameViewSet` to `GPUViewSet`
  descriptors/constants and replace
  `RenderCommandCollection.ConfigureGpuViewSet`'s independent five-view setup.

- [x] Make every view consumer obtain matrices, output rectangles, parent IDs,
  predicted time, and history keys from the captured frame set. Once visibility
  generation begins, these values are immutable for the frame.

- [x] Keep shading-rate/foveation metadata separate from real OpenXR inset-view
  identity and delete duplicated descriptors that only reuse ordinary-eye
  projections.

#### 5.2.6.2 - Plan Runtime View Families And Render Batches

- [x] Group output requests into compatible view families before collection and
  command construction. A family owns shared scene/material/light publication
  and contains one or more view/projection/target variants.

- [x] Make `RenderFrameViewBatchPlanner` consume actual backend, target,
  swapchain, extent, format, sample-count, layer, and layout capabilities rather
  than remain a contract/test-only planner.

- [x] Keep the existing two-eye true-multiview path as the first production
  consumer of a planned `LayeredStereoPair`; replace the global
  `_viewCount != 2` rejection with planned-batch validation.

- [x] Represent a foveated XR eye family as real wide/inset views. For a typical
  supported four-view configuration, plan a wide stereo pair and an inset
  stereo pair; permit a four-layer batch only when the target/backend explicitly
  supports its extent and layer contract.

- [x] Plan desktop, secondary, and independent-camera outputs as ordinary
  single-view batches. Preserve parallel-recording and sequential modes with
  exact, visible selection reasons.

- [x] Include stable view mask, attachment signature, output identity, and
  resource/temporal generation in command-chain and secondary-command-buffer
  identities without putting transient matrices or frame indices in structural
  keys.

- [x] Keep sequential rendering as a separately selected parity/unsupported-
  hardware path. A requested strict `SinglePassStereo` mode must never silently
  enter sequential rendering after a capability or runtime failure.

#### 5.2.6.3 - Generate Frame-Scoped Exact Visibility For `CpuDirect`

- [x] Define a frame-scoped candidate record with stable instance/draw identity
  and an exact active-view mask. Keep pass eligibility, frustum visibility,
  occlusion visibility, and final render-batch membership as separate fields or
  buffers with explicit capacity, overflow, retirement, and frame-slot rules.

- [x] Add a multi-frustum CPU BVH collection API that carries a surviving view
  mask from parent to child, loads/tests each node bound once, and emits exact
  per-view command collections without retraversing the main scene for every
  output.

- [x] Permit a rejected outer/parent view to reject a contained inset only when
  that relationship is validated for the current runtime configuration/frame.

- [x] Keep the existing conservative combined-stereo collector as a diagnostic
  comparison lane until exact masked traversal is validated.

- [x] Move main-camera visibility generation to the frame/family boundary so
  depth, opaque, masked, motion-vector, transparent, and other compatible passes
  consume the shared candidate set and apply their own pass/material filters.

- [x] Keep shadows, probes, reflections, and independent cameras in distinct
  visibility domains, while sharing the scene snapshot and rebuilding a scene
  BVH at most once per scene revision/frame.

#### 5.2.6.4 - Preserve Accelerated Masked-Visibility Promotion Lanes

- [x] Upload all active main-view frusta in a compact frame-view buffer and
  replace the GPU BVH shader's leaf-to-parent walk with a root-down work queue
  or bounded-stack traversal that propagates the surviving view mask.

- [x] Test only active frusta at each node; use validated parent/inset
  containment to skip safe tests and compact surviving candidates with subgroup
  or prefix-sum operations where supported.

- [x] Produce one exact GPU visibility mask per surviving candidate and define
  deterministic, visible overflow behavior that never silently falls back to
  CPU on zero-readback strategies.

- [x] Keep candidate and production counts GPU-resident for GPU strategies.
  No CPU-generated pass mask may be reported as an exact GPU frustum result.

- [x] Classify visible candidates by view batch, render pass, pipeline/state
  class, material, mesh, and LOD; build compatible multiview union indirect
  lists while preserving the exact logical-view mask in draw metadata.

- [x] Compile GPU-driven work into stable pass-level dispatch, resource-specific
  barrier, and `Draw*IndirectCount` packets that can be recorded once and reused
  while frame-slot inputs and GPU-written output contents change.

- [x] Stop treating GPU-written visibility, command, count, overflow, and delayed
  statistics values as `mutable-gpu-driven-frame-ops`. Only a topology,
  capacity, binding-identity, pipeline, or resource-generation change may force
  the compatible recorded range to rebuild.

- [x] Keep active draw, material, state-class, and bucket work compact. The CPU
  must not scan all potential buckets, inspect current-frame counts, or construct
  commands per surviving GPU draw in a production zero-readback frame.

- [x] Use bounded frame-slot resources for GPU cull/compact/indirect output so
  the GPU may mutate contents without racing an in-flight submission or rotating
  structural cache identity every frame.

#### 5.2.6.5 - Integrate Physical-Eye Hi-Z And Persistent Visibility

- [x] Key persistent/two-phase visibility by stable logical-view identity.
  Maintain outer-eye Hi-Z histories for physical left/right XR eyes plus
  independent desktop and secondary histories when active.

- [x] Frustum-test inset views with their exact projection, then project bounds
  with the corresponding outer projection before sampling an outer-eye Hi-Z.
  If relationship invariants are not proven, use an independent inset hierarchy
  or disable inset occlusion explicitly.

- [x] Support temporal Hi-Z as the low-latency/default option and optional
  current-frame outer-eye occluder depth/layered pyramid generation only when
  measured benefit justifies its XR critical-path dependency.

- [x] Invalidate or conservatively bypass history after camera cuts, tracking
  jumps, projection discontinuities, resource-generation changes, and unsafe
  scene revisions; periodically compare against an occlusion-disabled frame.

#### 5.2.6.6 - Model Auxiliary Outputs Without Duplicating Architecture

- [x] Compose the normal desktop VR mirror from already rendered eye/family
  outputs by default. Schedule a full independent desktop scene only when its
  policy explicitly requires a distinct camera or quality.

- [x] Model pickup/handheld and in-world mirrors as view-dependent requests with
  stable IDs, screen coverage, maximum update rate, content-age limit,
  resolution policy, recursion limit, and cacheable last result.

- [x] Share material publication, compatible shadow results, BRDF/LUT inputs,
  and GPU-scene buffers across families. Never share view-dependent data without
  an explicit compatibility proof.

- [x] Model probe faces, mip generation, octa conversion, irradiance, and
  prefilter mips as individually schedulable DAG nodes with persistent
  intermediates and resumable progress.

- [x] Apply capture/post-process policy at graph construction time so one
  output's optional stack does not force unrelated outputs through it.

Implementation note: The snapshot, canonical view set, planned stereo-batch, exact CPU
visibility and its diagnostic reference comparison, stable-view Hi-Z metadata, auxiliary-
output graph contracts, bounded GPU candidate slots, and GPU view-batch classification
metadata are live. Non-BVH and post-occlusion classification explicitly marks exact masks
unavailable and remains conservative. Multiview LOD uses the highest detail required by
the visible views; exact per-view transparent candidates are gated until split
rendering scopes exist. Traditional layer suppression, the external OpenXR shared-
visibility exception removal, and multiview meshlet Hi-Z are capability/validation gated
and remain default-off because backend layer suppression, family-owned GPU culling, and
layered physical-eye Hi-Z are not yet available. Similarity-driven submission splitting,
those two accelerated activation gates, and all acceptance/runtime proof remain deferred.

## Continuing Work

All unchecked criteria and future phases remain in the
[current todo](vulkan-core-hardening-and-device-loss-todo.md).

# Vulkan OpenXR and Advanced Rendering — Status and TODO

Updated: 2026-09-14
Status source: [September 14 mirror and placement investigation][mirror-closeout].

This tracker owns legacy Phases **6, 7 and 7R**. Review findings are merged into
their owning tasks. The [master tracker](vulkan-core-frame-loop-and-resident-rendering-master-todo.md)
owns unfinished foundations and the later performance, promotion and deletion gates.

[Remaining implementation](#remaining-implementation) ·
[Remaining validation](#remaining-validation) ·
[Completed work](#completed-work) ·
[Evidence and maintenance](#evidence-and-maintenance)

<a id="current-stopping-point--2026-09-06"></a>

## Current status

**ARP-I91, ARP-I93, ARP-I94, ARP-I81, ARP-I90 and ARP-I95 are complete. Phase 6 implementation and validation have resumed.**
Mirror clipping, ownership, cold-output gating, native display and Vulkan primitive
placement have bounded live evidence. ARP-I95 now shares immutable scene payloads
across mirror/main outputs with independent globals and completion receipts.
The full `ARP-V36` profile remains open.
The resumed Build63 passed with zero warnings/errors.
The resumed work fast-forwarded `master` from `51ffc79f8` to `e72ef7ce5`.
Start38 verifies 6,801 accepted paired/preview submissions, zero publication
failures and complete retirement after a real session-exit request. Start40
initially exposed black eye output. Start59 now has inspected, responsive RVC
eye output with stereo parallax and 17,829 accepted/completed/retired submissions,
zero publication failures and final normal teardown epoch 1. Start63 now verifies
visible single-eye output with its own exact package authority and 5,106
accepted/completed/retired submissions. Resize starvation and lifecycle fault
checks remain active. Eleven XR Phase 6 validation rows remain open.

| Task kind | Completed | Remaining | Meaning |
|---|---:|---:|---|
| Implementation (`I`) | 114 | 4 | The named behavior exists and has its recorded build/compiler evidence. |
| Source/contract audit (`A`) | 9 | 0 | The named inventory is complete; any discovered implementation gap has its own task. |
| Validation (`V`) | 31 | 58 | 30 checked rows record bounded runtime results; `ARP-V01` is shader compilation only. |

The original 12 XR implementation tasks and all nine audits are complete. Phase 6 runtime
checks exposed follow-up implementation gaps below. Remaining validation
is split between **11 XR tasks** and **47 Advanced rendering tasks**. Counts are
not an effort estimate.

Completed implementation includes XR submission/lifecycle ownership, canonical
GPU records, visibility/reconstruction and classification, native shading/AO/GI,
temporal/late/post commands, stereo execution, independent capture banks and
editor/diagnostic plumbing. Recorded runtime acceptance covers specific Monado,
OpenGL mono/OVR, Vulkan mono, probe and standalone-capture cohorts. Each completed
validation row names its scope; emulated stereo, a source audit or a successful
build does not certify hardware XR or another backend.

The September 14 mirror closeout **Build29** passed with zero warnings/errors.
Native Vulkan, native OpenGL and Default OpenGL mirror cohorts have inspected
output and drained capture ownership. Start22 validates repeated selected-cube
placement with 79 resident draws and continuing GPU deformation/publication.
The earlier Build145/failed Start120 pause remains [historical evidence][wrapup].

## Remaining implementation

No remaining Advanced implementation rows. Phase 6 validation is active;
the following discovered XR failures remain open until their fixes have runtime evidence.

- [ ] **XR-I14** — Preserve a usable swapchain generation and resume pacing on pre-detachment deferral; distinguish post-detachment failure and retain parents until child retirement completes. Report per-session teardown epochs and independent XR retirement blockers. Build32 compiles; lifecycle acceptance remains pending. [Investigation][mirror-closeout].

- [ ] **XR-I16** — Serialize normal submission settlement against device-loss abandonment, retain the exact tracker during reentrant abandonment, and define authoritative abandonment for swapchain/input/session parents. Abandonment must never report normal completion, reset pending arenas, or claim a successful normal teardown. [Investigation][mirror-closeout].

- [ ] **XR-I22** — Reconcile mirror logical submission slots with native compute-slot requirements and exact keyed sealed-graph planning; Build71/72 still expose missing native compute slot-3 resources after three accepted shape-5 submissions. Close with accepted native resources and runtime proof.

- [ ] **XR-I23** — Remove stale desktop/XR queue ownership snapshots by carrying the current submission revision through the snapshot boundary. Source review confirms reset/unleased logical-plan release and Build72 no longer reports the stale immutable-view-set exception, but a healthy cohort exit/restart remains pending.

## Remaining validation

These **58 unchecked rows** are the complete remaining validation checklist.
Most validate existing implementation. A failed validation gets a specific new
implementation task; keep the failed validation open. Reuse evidence across
related fixtures without duplicating the obligation or extending its accepted scope.

Prerequisites and current evidence limits:

- Service fault injection requires an owned Monado service. The resumed run
  found no existing service and started one with this task's ownership marker.
  The earlier unowned PID43684 was already gone.
- Hardware XR and positive vendor-feature cases require the named runtime,
  SDK, driver and device. An unavailable prerequisite remains a blocker.
- OpenGL RenderDoc inspection is limited by its lack of bindless-texture support;
  `ARP-V60` still needs suitable capture evidence.

<a id="phase-6"></a>

### Phase 6 — OpenXR submission and lifecycle

Entry points: `OpenXrVulkanSubmissionTracker`, `VulkanCommandRuntime.OpenXrSubmission`,
the eye/mirror frame-loop callers, `VulkanXrGraphicsBinding`, and
`OpenXRAPI.SwapchainLifecycle`, `Resolution` and `RuntimeStateMachine`.

#### Submission ownership and bounded admission

- [ ] **XR-V04** — Run the three-command `[left, right, publish]` path. Close with both eye renders and the publish command present in the accepted receipt and retired once.

- [ ] **XR-V05** — Exercise external-target submission. Close with output captures and exact ownership/settlement for its path from `XR-A01`.

#### Swapchain and session lifetime

- [x] **XR-V08** — Fill the retired-generation budget with delayed GPU completion/runtime release. Build68 reaches generation capacity/high-water 4, records five attempts with one pre-detachment deferral while the held 1120×1080 generation remains live, then releases the hold normally; a later 1152×1080 generation runs, five generations queue/drain, and exit reaches 3,315 accepted/completed/retired submissions with zero ownership. This is real retirement observation, not injected slow-GPU behavior; no Vulkan VUID appears in the log. The configured runtime-refresh exception remains separate under XR-V09. Done 2026-09-14; [investigation][mirror-closeout].

- [ ] **XR-V09** — Change eye resolution during active rendering. Close with safe in-session replacement, dimension read-back, pacing resumed, and no normal device-wide idle; exercise the configured runtime-refresh exception separately.

- [ ] **XR-V10** — Force replacement failure after detachment. Close with eventual rendering/recreation or an explicit safe terminal outcome, no permanent swapchain-less running state, and no leaked partial children.

- [ ] **XR-V11** — Repeat session start/stop/restart. Close with successful new output, zero invalid-handle destruction, and final ownership/retirement counts zero on each cycle.

- [ ] **XR-V20** — Exercise LOSS_PENDING with pending work. Close with safe session/child retirement and a documented recovery outcome.

- [ ] **XR-V21** — Exercise device-loss teardown. Close with explicit abandonment where required and no stale ownership reported as normal completion.

#### Timing, allocations and hardware acceptance

- [ ] **XR-V13** — Correlate real frame ID/display time, queue-submit interval, completion/forced wait, in-flight age, and independent per-eye image reuse age. Close with a trace matching receipt values and no zero/synthetic provenance.

- [ ] **XR-V15** — Measure pressure recovery waits against the XR deadline. Close with short counted waits only after safe reuse/defer paths, truthful missed/late/reprojected counters, and preserved `xrWaitFrame` pacing.

- [ ] **XR-V16** — Run desktop and XR together. Close with nonblocking desktop acquire and no transferred compositor/completion stall; publish per-output timing and captures.

- [ ] **XR-V17** — Validate at least one hardware runtime. Close with the named runtime/device, eye output, lifecycle evidence, and documented release-before-application-completion legality/completion fallback. Do not assume the runtime sees the engine's private timeline; record whether a fence-ring fallback is required.

<a id="phase-7"></a>

### Phase 7 — Advanced rendering

Entry points: the `AdvancedRenderPipeline` production, classification, native
shading, late/post and stereo/view partials; `AdvancedProductionCutoverContract`;
canonical material/light accessors; Vulkan visibility resources; `ViewSetPlan`;
and the picking, diagnostics and OpenXR timing/foveation contracts.

#### Admission and canonical GPU contracts

- [ ] **ARP-V02** — Request an unsupported required profile. Close with an observable failure/diagnostic and no silent CPU, legacy, mono, or unshaded substitution.

- [ ] **ARP-V03** — Validate actual descriptor/image format/access/set/binding, matrix convention, record stride/version, and generation lookup at the backend boundary. Close with a compiler/layout inventory plus a clean GPU capture; preserve the set 0/1/2/3 contract.

<a id="visibility-and-reconstruction-acceptance-carried-from-architecture-documents-0305"></a>

#### Visibility, deformation and reconstruction

`ARP-A07` and `ARP-A08` have completed the source tracing. The following rows
retain acceptance from architecture documents 03–05; the
[coverage audit](../../progress/rendering/vulkan-todo-coverage-audit-2026-09-06.md)
records the migration. Relevant shaders include the visibility producers, shared
reconstruction contract and `ShadeNativeOpaque.comp`.

- [ ] **ARP-V04** — Run the original skinned fixture through arena growth/reuse. Close with current/previous deformation ranges valid, no bind-pose fallback, and bounded arena storage across completed slots.

- [ ] **ARP-V51** — Capture Vulkan deformation-to-visibility/depth/velocity dependencies. Close with correct current/previous deformed ranges, resource-specific barriers, completion-owned lifetimes, and no same-frame readback.

- [ ] **ARP-V52** — Capture the corresponding OpenGL deformation path. Close with the same logical output and correct barrier/lifetime behavior; Vulkan evidence cannot close this backend.

- [ ] **ARP-V53** — Compare Vulkan traditional, indirect, meshlet, static, and skinned visibility producers. Close with identical logical surface/editor identity and inspectable final payload/depth in overlapping, masked, mixed-producer, and camera-cut fixtures from two views. Reuse `ARP-V13` masked-coverage evidence where applicable.

- [ ] **ARP-V54** — Run the same visibility-producer comparison on OpenGL. Close with attachment/barrier captures and the same logical payload contract.

- [ ] **ARP-V55** — Demonstrate current-frame late visibility recovery after disocclusion. Close with newly visible candidates drawn after the current-view pyramid, no duplicate early-pixel shading, and conservative behavior after cuts, resize, or missing history.

- [ ] **ARP-V56** — Compare reconstructed attributes to the raster reference for static, skinned, normal-mapped, mirrored, masked, and UV-stress meshes. Close with documented numeric/image tolerances and retained comparison images.

- [ ] **ARP-V57** — Validate texture gradients and selected mip across primitive/material boundaries, UV seams, tiny triangles, oblique surfaces, minification, and LOD changes. Close with stable LOD and conservative identity-safe fallback, using derivative/selected-mip captures.

- [ ] **ARP-V58** — Measure reconstruction separately from classification and lighting/shading. Close with isolated GPU stage timings, the enabled attribute mask, fixture/extent, and backend recorded.

- [ ] **ARP-V59** — Inspect reconstructed attributes and derivatives in a Vulkan GPU capture. Close with named resources and values consistent with `ARP-V56`/`ARP-V57`.

- [ ] **ARP-V60** — Inspect reconstructed attributes and derivatives in an OpenGL GPU capture. Close with the same logical contract and documented backend differences.

#### Classification and clustered lighting capacity

Entry points: `ClassifyTiles.comp`, `BuildClassificationIndirect.comp`,
`BuildFroxels.comp`, classification and native shading.

- [ ] **ARP-V05** — Render shared materials and sparse/reused draw IDs. Close with generation-correct material/kernel resolution, empty/background exclusion, and no material-row or descriptor-object identity used as a dispatch key.

- [ ] **ARP-V06** — Exercise every admitted kernel ID, including pending/rare/custom behavior. Close with initialized disjoint pixel ownership, no dropped high kernel IDs, and the documented permutation/prewarm policy.

- [ ] **ARP-V07** — Force tile, membership, and dispatch capacity pressure independently. Close with truthful attempted/emitted counters, conservative automatic recovery, structured required-mode failure, and no same-frame readback recovery.

- [ ] **ARP-V08** — Compare selectable tile dimensions using occupancy and mixed-material captures. Close with the chosen dimensions used consistently by all consumers and measured occupancy evidence.

- [ ] **ARP-V09** — Run 1440p and 4K mono through resize/render-scale changes. Close with correctly derived froxel sizes, initialized ranges, and no shader bounds or descriptor errors. Layered validation belongs to `ARP-V34`.

- [ ] **ARP-V10** — Exercise point/spot coverage and directional ordering with more than 16 local lights. Close with correct view-space depth/XY lists, bounded directional work, and no silently truncated contribution.

- [ ] **ARP-V11** — Exhaust froxel light-index storage. Close with overflow diagnostics and conservative GPU recovery preserving lighting; no current-frame CPU readback/rebuild.

#### Materials, shadows and decals

Entry points: `StandardMaterial.glslinc`, `StandardPBR.glslinc`,
`StandardShadow.glslinc`, `ShadeNativeOpaque.comp` and `AdvancedGlobalResourceCapture`.
The accepted built-in AO/GI cohorts are in completed validation; broader material
and shadow acceptance remains below.

- [ ] **ARP-V13** — Validate textured opaque, masked, unlit, and emissive material families against their constants, normal/tangent inputs, coverage, and invalid/pending-layout output. Close with per-family captures, including masked alpha-cutoff edges.

- [ ] **ARP-V14** — Validate directional cascades and point/spot shadows. Close with atlas/filter/depth-convention captures and machine-readable missing/stale reasons; sample each supported filter family separately.

- [ ] **ARP-V15** — Validate overlapping decals and list overflow. Close with correct modified normals/material values before lighting, bounded recovery, and stable diagnostics.

#### Motion, history and reset matrix

- [ ] **ARP-V17** — Capture stationary, camera-only, object-only, and combined motion for rigid, skinned, and blendshape fixtures. Close with dense correct velocity/reactive/disocclusion output where history is valid and explicit neutral/reactive output where it is invalid; record each fixture's result separately.

- [ ] **ARP-V18** — Exercise cuts, frame gaps, newly visible objects, topology replacement, and rejected submissions. Close with history invalidation matching those events and no stale previous-frame contribution.

- [ ] **ARP-V19** — Exercise resize, render-scale, view-count, pipeline switch, HDR/format, shader reload, and resource-generation resets. Close with a recorded result for each reset cause and no cross-output history leakage.

- [ ] **ARP-V20** — Exercise multiple desktop viewports, empty output, and next-frame collection during delayed submission. Close with exactly the accepted output histories committed and every rejected candidate released.

- [ ] **ARP-V21** — Exercise OpenXR tracking loss/recovery, failed/no-layer EndFrame, and successful layered frames. Close with history advancing only for the exact successful frame/display identity.

#### Background, transparency and final composition

Entry points: `ShadeBackground.comp`, `AdvancedRenderPipeline.LateAndPostCommands`,
`ExactTransparency`, `Transparency`, `PostProcessing` and eligibility contracts.

- [ ] **ARP-V25** — Validate sentinel sky/background output and compatible custom background geometry. Close with correct clear/alpha/HDR/capture behavior and no shading of invalid identity pixels. OpenGL mono/OVR and reversed-mono preservation now pass on Build88/PID50596, including HDR/custom mesh and explicit rejection checks; Vulkan mono and cube sky preservation also pass on Build126/Start100; compatible Vulkan custom geometry and the remaining owned profiles still require proof. [Background evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-authored-background-admission-and-depth-preservation).

- [ ] **ARP-V27** — Validate refraction/feedback scene-color snapshots. Close with a copy only for visible consumers and no illegal sampling of the attachment being written.

- [ ] **ARP-V28** — Validate weighted blended OIT capacity and overflow behavior. Close with visible parity, truthful diagnostics, and no same-frame readback recovery.

- [ ] **ARP-V29** — Validate PPLL against the completed `ARP-A02` admission inventory. Close with bounded capacity/recovery evidence and shared lighting/shadow/probe/fog access preserved.

- [ ] **ARP-V39** — Validate depth peeling against the completed `ARP-A02` admission inventory. Close with correct layer composition, bounded work, and shared lighting/shadow/probe/fog access preserved.

- [ ] **ARP-V44** — Validate final composition. Close with current native/post output and UI/alpha correctly composed for the selected output format.

#### Vendor upscalers and frame generation

`ARP-A04` inventories the admitted combinations. Native Vulkan dispatch and the
OpenGL-to-Vulkan bridge require separate results.

- [ ] **ARP-V61** — Validate native Vulkan DLSS super-resolution on a device admitted by the installed Streamline runtime. Record SDK/driver/device, color/depth/motion inputs, render/output extents, reset behavior, output captures and explicit dispatch failures.

- [ ] **ARP-V62** — Validate native Vulkan DLAA on a device admitted by Streamline. Record native-resolution extents, motion/depth inputs, history resets and enabled/disabled output captures.

- [ ] **ARP-V63** — Validate native Vulkan XeSS super-resolution on a device admitted by the installed XeSS Vulkan runtime. Record required extensions/features, SDK/driver/device, input/output extents, resets and captures.

- [ ] **ARP-V64** — Validate native Vulkan DLSS frame generation on a device/runtime that admits the feature. Record actual generated-frame dispatch and presentation, swapchain/resource ownership, pacing and failure diagnostics; super-resolution evidence is insufficient.

- [ ] **ARP-V65** — Validate OpenGL-to-Vulkan bridge DLSS super-resolution. Record the same physical GPU identity on both APIs, external-memory/semaphore ownership, SDK admission, extents/resets and output captures.

- [ ] **ARP-V66** — Validate OpenGL-to-Vulkan bridge DLAA. Record same-GPU interop, native-resolution evaluation, semaphore/resource lifetime, resets and output captures.

- [ ] **ARP-V67** — Validate OpenGL-to-Vulkan bridge XeSS super-resolution. Record same-GPU interop, runtime-required features, ownership, extents/resets and output captures. This does not validate XeSS frame generation.

<a id="stereo-offscreen-editor-and-diagnostics"></a>

#### Stereo, mirrors, editor and diagnostics

Portal, thumbnail, single-probe and data-only capture cohorts are recorded as
complete below. Their bounded results do not establish arbitrary XR/multi-owner
pressure or mirror output acceptance.

- [ ] **ARP-V34** — Validate the admitted Vulkan layered/multiview path at multiple extents and view counts. Close with separate eye images/history and bounded per-eye classification/froxel resources; array shader compilation alone cannot close this.

- [ ] **ARP-V35** — Validate the admitted RVC two-pass profile. Close with separate eye motion/occlusion evidence and preserved OpenXR timing ownership.

- [ ] **ARP-V46** — Validate foveated derivatives/LOD. Close with peripheral material/coverage captures and per-eye occlusion preserved under head motion.

- [ ] **ARP-V36** — Validate the admitted mirror profile. Close with correct reflected output, resource lifetime, and no unrequested main-view post work. September 14 bounded Vulkan/OpenGL mirror cohorts pass; the streamed multi-output storage fix is implemented under `ARP-I95`. Stereo, reader-pressure and forced rejection/recovery coverage remain open. [Evidence][mirror-closeout].

- [ ] **ARP-V37** — Validate picking plus outlines, hover, gizmos, bounds, icons, physics debug, UI, and on-top overlays. Close with correct identity and visible editor behavior for each consumer.

- [ ] **ARP-V38** — Validate diagnostic views and per-family GPU timing attribution. Close with counters matching captures, stable RenderDoc labels, and a useful legacy difference view where supported.

## Completed work

- [x] **XR-V03** — Start64 parallel recording shows both eye outputs with parallax. Its accepted two-command receipts retain both recorded/prepared owners and slots while completion is unobserved, then release each once with zero early-settlement violations. All 2,592 accepted submissions complete/retire on real exit, with zero pending ownership and normal teardown epoch 1. This fixture has no pending texture uploads; no upload-pressure claim is made. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-I21** — Direct eye authoring captures the exact published command collection/package/collect identity, revalidates it under the collection read scope, and passes its collect authority independently of desktop collection. Strict validation and exact once-only begun-frame cleanup remain in force. Build63 passes with zero warnings/errors; Start63 continues rendering both eyes and drains all accepted work on exit. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-V02** — Start63 ordinary serial submission has inspected red-cube output in both eyes with parallax and matching render/copy frame IDs. Its retained single-command receipts transfer recorded ownership and mapped/data slots, retain them while accepted-incomplete, and settle once on real completion. Exit completes/retires all 5,106 accepted submissions with zero active/reserved/pending ownership, zero publication or EndFrame failures, and normal teardown epoch 1. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-V07** — Start61 rejects exactly one paired submission before the native call: no native receipt or transferred ownership, one cancellation and exact recorded/prepared/slot cleanup. Start62 accepts native work, injects one publication failure, retains ownership while completion is unobserved and retires on actual completion. Real exit drains all ownership in both runs: 1,272 accepted plus one cancelled record in Start61; 3,783 accepted/completed/retired in Start62. No early settlement or orphaned accepted work. Done 2026-09-14; [investigation][mirror-closeout].

Each task appears once. Checked implementation/audit entries summarize the
completed change and link to its detailed record. Checked validation entries
retain their observed backend/profile limits. Historical build-by-build narratives
and superseded checkbox snapshots remain in [the investigation][investigation].

### Completed validation

There are **21 runtime records and one shader-compiler record**. These are
accepted cohorts, not certification of every material, output profile or backend.

#### OpenXR runtime

- [x] **XR-V01** — Repeat Monado strict SPS after the descriptor fixes. Done 2026-09-06; [clean SPS rerun](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-clean-monado-sps-rerun). PID8256 retained 360 frames: 351 strict SPS submissions, nine cold no-layer frames, zero validation/EndFrame failures, zero sequential fallback attempts, and zero final pending retirement. Default/RVC eyes; Advanced stereo and hardware acceptance remain open.

- [x] **XR-V12** — Real Monado session exit traverses STOPPING while retired-generation work remains. Start38 reports one queued/pending generation and `teardownCompleted=false`, then one drained generation, zero pending ownership, normal teardown epoch 1 and `teardownCompleted=true`. All 6,801 accepted submissions are completed/retired; this cohort proves pending generation retirement, not an incomplete GPU submission at STOPPING. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-V06** — Start60 holds observation of real accepted completion until capacity three, records high-water three and one admission deferral, then releases the hold and recovers. Retained receipts keep ownership until actual completion with zero early-settlement violations. Normal exit completes/retires all 5,157 accepted submissions with zero remaining ownership. No forced wait was needed. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-V14** — Start59's warmed tracker measures zero bytes/high-water for 3,203 registrations, 5,339 polls and 3,201 retirements. It performs 5,338 timeline queries across those polls, consistent with the shared-semaphore query deduplication. Storage is bounded to 64 uploads, three commands, three frame slots, 64 swapchain-image records and 32 ledger rows. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-V18** — Start59 exercises ordinary paired-eye submission on Monado with visible RVC output and stereo parallax. The retained cohort includes 11 accepted paired receipts, each releasing two recorded/prepared owners and two frame slots only after real completion; no early-settlement, shape or ownership mismatch. The full run retires all 17,829 submissions on normal exit. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-V19** — Start59 retains 21 per-eye preview-copy receipts, each retiring its one temporary command once after real completion. Viewed left/right captures match their render/copy frame IDs 907/911; a moved-cube capture at frame 4104 changes pixels and position. Done 2026-09-14; [investigation][mirror-closeout].

#### Shader compilation only

- [x] **ARP-V01** — Compile the September 4 shader cohort. Done 2026-09-04; [E1]: 26/26 Vulkan 1.3 mono/array variants, zero diagnostics. Recheck affected variants after shader changes; this is not stereo admission.

#### Static surfaces, AO and GI

- [x] **ARP-V12** — Demonstrate the narrow static opaque path with three meshes, two camera views, and material scalar/color changes. Done 2026-09-04; [E2].

- [x] **ARP-V22** — Obtain and inspect `AdvancedShading.AmbientOcclusion` via working MCP readback or RenderDoc. Done 2026-09-06; [AO capture checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-ao-capture-checkpoint). Two retained 1920×1080 mono images, separated by 4m32s and a camera change: finite occlusion, neutral white background/borders, contact occlusion follows the geometry. AO neutral output and the selected AO/GI combination are accepted under ARP-V23/V24.

- [x] **ARP-V23** — Capture enabled versus disabled/null-provider AO. Done 2026-09-06; [AO neutral-output checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-ao-neutral-output-checkpoint). Three retained 1920×1080 mono/layer-0 images over 2m49s: enabled R8 range 0–1 with visible scene occlusion; disabled and null-provider each exactly 1 for all 2,073,600 pixels, zero nonfinite samples. Layered profiles remain ARP-V34.

- [x] **ARP-V24** — Validate AO plus the selected GI/IBL provider. Done 2026-09-06; [AO/IBL contribution evidence][ao-ibl]: Vulkan PID17396, 1920×1080, frozen completed LightProbesAndIbl generation. AO darkens 215,849 pixels with zero brightened samples; direct + nonzero emission produces identical raw hashes with AO on/off. Native shader applies diffuse AO and specular occlusion once at the indirect terms. This closes the selected built-in AO/IBL combination, not other GI providers or stereo.

- [x] **ARP-V16** — Validate selected GI/probe/IBL contribution and switching. Done 2026-09-08; Build132, Vulkan Start108/PID58608 and OpenGL Start109/PID57480, six grouped HDR/depth captures per backend. Provider on/off changes opaque lighting; restoring the provider reproduces exact pixels. Changing the sky leaves opaque lighting identical until the probe refresh completes (Vulkan version2→3; OpenGL4→5). After refresh, disabling GI reproduces the original disabled opaque output exactly; depth/alpha stay identical and all samples are finite. Repeated enabling adds no extra contribution. Each backend uses its own generated probe cohort; this is switching/generation proof, not pixel parity between differently refreshed probes. [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-gi-provider-switching-and-probe-updates).

#### Transparency, temporal and post-processing

- [x] **ARP-V26** — Validate sorted alpha and participating rigid transparent motion. Done 2026-09-08; Build 82/PID58000, native OpenGL OVR. Both depth orders match the expected alpha-blend equation within 0.00006104 over 63,931 overlap pixels per eye. Combined camera/object motion produces nonzero velocity on all 128,694/124,365 transparent pixels and all 653,975/652,326 uncovered opaque pixels, with zero motion outside geometry. Reactive coverage remains in the same canonical mask consumed by TSR. This covers the admitted rigid colored-alpha lane with a common two-eye ordering; other effect/deformation families and rejected per-view ordering profiles retain their own admission/validation boundaries. [September 8 checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-native-cut-and-sorted-alpha-acceptance).

- [x] **ARP-V30** — Validate atmospheric aerial perspective and volumetric fog against visibility depth/native HDR. Done 2026-09-08; Build 84/PID47908, native OpenGL mono. Both affect post output while native HDR remains exactly unchanged. Fog transmittance spans 0.1918–0.8267; history/temporal images match exactly, accumulation differs from current scatter, and a 2.2m cut makes them exactly equal. Atmospheric history likewise matches its copy, differs from current scatter during smooth camera movement, and resets exactly on a cut. Images from two camera positions were viewed. Sky rendering remains V25/I67; stereo is excluded by current effect admission, and Vulkan is not certified by this cohort. [Scene-filter checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-capture-liveness-and-mono-scene-filters).

- [x] **ARP-V31** — Validate temporal accumulation. Done 2026-09-07; Build 67, OpenGL emulated OVR stereo. [Temporal/editor acceptance](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-material-includes-and-mono-stereo-gizmo-acceptance): same-callback HDR/history and depth/history hashes match in both eyes; stationary velocity is zero. Settled history weights are approximately 0.905–0.960, reset captures are exactly zero, and both eye generations reseed and recover. Native moving-velocity evidence is retained under V40. This closes the exercised OpenGL TAA profile; the broader motion/reset matrix and other backends remain in their own rows.

- [x] **ARP-V33** — Validate TSR resource bindings, motion/reactive inputs, render scale and reset behavior. Done 2026-09-08; native OpenGL OVR, Builds 77/80. Both-eye reactive debug matches coverage exactly; velocity debug agrees within half-precision tolerance. Accepted scale 0.5/0.75/1 extents and paired generations converge; output remains 1920×1080. Start 60 captures exact-zero history weight on a 2.2m camera cut, followed by recovered weights up to 0.95996094 and finite viewed output. This is bounded OpenGL TSR acceptance; full reset causes stay in V18/V19, and the exposed native opaque cut-velocity defect was subsequently fixed under ARP-I63. [September 8 checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-transparent-motion-and-render-thread-ownership).

- [x] **ARP-V40** — Validate motion blur with Advanced velocity/depth inputs under camera/object motion. Done 2026-09-07; Build 51, OpenGL emulated OVR stereo; [grouped motion captures](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-grouped-motion-captures-and-temporal-copy-failure). Same-frame input/output comparisons show finite filtering in both eyes only where velocity is nonzero; zero shutter restores input exactly. Enabled/disabled camera and object output captured in the six-minute cohort.

- [x] **ARP-V41** — Validate DoF against Advanced depth/HDR. Done 2026-09-07; Builds 49/50, OpenGL emulated OVR stereo; [scene-filter validation](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-stereo-scene-filters-and-executable-post-bindings). Two-eye focal-plane versus defocused captures are finite, show reduced checker edge contrast, and disabling restores HDR exactly. Final wrappers reproduce the blurred output exactly in the eight-minute Build 50 cohort.

- [x] **ARP-V32** — Validate bloom threshold, spread and mip outputs. Done 2026-09-07; [textured stereo acceptance](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-textured-stereo-and-post-process-acceptance). OpenGL stereo mip 1/4 are exact zero at threshold 5 and finite/nonzero at 0.1. Combine brightens 818,824/818,431 pixels with no darkened samples; 17,910/17,584 are background spill. Native HDR is unchanged and final alpha remains 1.

- [x] **ARP-V42** — Validate selected tone mapping. Done 2026-09-07; [textured stereo acceptance](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-textured-stereo-and-post-process-acceptance). OpenGL two-eye Linear/Reinhard/ACES raw EXR comparison at exposure 10: 786,336/786,274 interior pixels; maximum errors 0.003907/0.002449 match dither and half precision. Linear preserves values above 1. Other operators/backends are not certified.

- [x] **ARP-V43** — Validate authored color grading. Done 2026-09-07; [textured stereo acceptance](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-textured-stereo-and-post-process-acceptance). OpenGL stereo tint (1,0.5,0.25) changes 800,962/800,901 pixels after ACES; maximum scaling error 0.001587. Neutral restoration reproduces both raw baseline images exactly.

#### Stereo and offscreen output

- [x] **ARP-V45** — Validate the admitted OpenGL SPS profile. Done 2026-09-07; [clean final stereo cohort](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-validation-resumption-opengl-final-stereo-output). RTX 3090, 1920x1080 per eye, GPU-indirect emulated SPS, approximately two minutes. Six captures are finite with alpha 1; frame 21608 has separate layer/history keys and matching generations. Clean 288-line OpenGL log. Hardware, complex materials, resets and other backends retain their own open rows.

- [x] **ARP-V47** — Validate the admitted portal profile. Done 2026-09-08; Build119/Start92/PID47800. Eight completed outputs include independent camera change and 256x256→320x192 resize; every inspected export exactly matches its HDR producer and is finite/nonzero. Updating the thumbnail leaves the portal's original hash intact; matching portal poses reproduce matching pixels. Temporal history is disabled by this admitted profile. Retained-reader retirement and rapid deactivate/reactivate/resize behavior subsequently pass I77. [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-capture-scheduling-follow-up).

- [x] **ARP-V48** — Validate the admitted Vulkan probe profile. Done 2026-09-08; Builds105/108, Starts79/82. All six faces have distinct geometry coverage and preserve opaque RGB/alpha when background changes; final-face depth/velocity/reactive data also match. After 20 completed Advanced refreshes, all 7 irradiance and 8 prefilter mips are finite, nonzero, and hash-identical to their array layers at version77. Native allocations plateau and output banks stay bounded (I76); Start82 Vulkan log has zero VUID/synchronization-validation matches before the separate thumbnail experiment. The irradiance image was inspected. This closes the single-probe Vulkan fixture; OpenGL array-copy regression is V68. [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-probe-gpu-array-ownership-and-mip-production).

- [x] **ARP-V68** — Validate the OpenGL probe-array copy after I71. Done 2026-09-08; Build125/Start98/PID3224. Twenty completed refreshes advance versions 4–24 over 13.375 seconds / 4,157 frames. All 7 irradiance and 8 prefilter source/array mip pairs are finite, nonzero and exactly hash-equal at version24; irradiance PNG inspected. Tracked VRAM plateaus at 731,702,920 bytes at iterations 10/15/20; buffer bytes stay 1,385,285. Transient descriptor-generation mismatches defer stale packages safely; no refresh stalls. This is the single-probe OpenGL fixture; cube/background inspection is separately tracked by I80/V25. [Investigation](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-capture-scheduling-follow-up).

- [x] **ARP-V49** — Validate the admitted thumbnail profile. Done 2026-09-08; Build119/Start92/PID47800. Eight completed outputs include camera change, 256x256→320x192 resize and five subsequent refreshes. Every inspected export exactly matches HDR; nonfinite samples zero. The actual pipeline 15 log contains seven native stages, authored background and offscreen blit, with no main temporal/post chain; all profile toggles are false. PNG inspected. Retained-reader retirement and rapid deactivate/reactivate/resize behavior subsequently pass I77. [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-capture-scheduling-follow-up).

- [x] **ARP-V50** — Validate depth/visibility-only capture. Done 2026-09-08; Build132, OpenGL Start107 and Vulkan Start108/PID58608. Each backend completes eight writers per owner across camera movement and 256x256→320x192 resize. Inventories contain only visibility/depth resources and their stages, with no shading/post/UI work. All 32 independently decoded source/export EXRs are finite and exactly reproduce their runtime readback hashes. Cross-backend identity is bit-equal in all four states; depth differs by at most 1.1921e-7. PNGs inspected; all owners retire with zero references/package generation and no pending writer/quarantine. Vulkan logs have zero VUID/synchronization/recovery/output-rejection matches. [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-data-capture-validation).

### Completed implementation and source audits

**108 implementation tasks and nine audits are complete.** Expand a group for
the individual changes. A checked item here establishes its named implementation
or inventory; consult the validation sections for runtime acceptance.

Completion dates and evidence links refer to the original work. The September 4
baseline is `8b104bf7a`; later checkpoints describe the working changes and build/run
identities used at that time. Full compiler, build, capture and review records stay
at those links rather than being repeated in every summary.

<details>
<summary>OpenXR submission and lifecycle (14 completed items)</summary>

- [x] **XR-I01** — Carry the exact accepted timeline semaphore/value and frozen XR frame/display identity in the submission receipt. Done 2026-09-04; [E1].

- [x] **XR-I02** — Reserve bounded tracker capacity before ordinary, parallel, or mirror submission; honor rejected admission. Done 2026-09-04; [E1].

- [x] **XR-I03** — Retain command, arena, upload, prepared-input, and native-resource ownership until the accepted receipt completes; settle completion payloads through the tracker. Done 2026-09-04; [E1].

- [x] **XR-I04** — Encode two-/three-command render-plus-publish batches without a null command inside the submitted count. Done 2026-09-04; [E3].

- [x] **XR-I05** — Supply immutable storage authority to XR mirror preparation and grow/reset XR arena slots without relocating live slot resources. Done 2026-09-04; [E1].

- [x] **XR-I06** — Acquire the program mutation gate before the link lock on the affected cold program/layout paths. Done 2026-09-04; [E1].

- [x] **XR-I07** — Retain dependent Vulkan image views/framebuffers behind a child-retirement receipt and keep the runtime parent alive while children remain. Done 2026-09-04; [E1].

- [x] **XR-I08** — Represent application completion, acquired/released-image state, and pending teardown separately; reserve retired-generation capacity before detachment. Done 2026-09-04; [E1].

- [x] **XR-I09** — Distinguish pre-detach deferral from failed/empty creation after `CleanupSwapchains`; route post-detach failure through safe partial-child cleanup and creation-eligible lifecycle state while retaining requested dimensions. Done 2026-09-06; [implementation resume](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-implementation-resume).

- [x] **XR-I10** — Gate full instance/service dimension refresh on an explicit runtime capability/quirk. MonadoOpenXR enables the simulated-service restart capability; ordinary recommended extents use in-session replacement. Done 2026-09-06; [implementation resume][implementation-resume].

- [x] **XR-I11** — Resolve parallel-eye foreground preparation failure and preserve paced recovery after readiness rejection. Done 2026-09-06; [paced parallel rerun](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-paced-parallel-eye-recovery).

- [x] **XR-I12** — Resolve parallel-eye teardown's persistent GPU-quiescence deferral. Done 2026-09-06; [native view ownership fix](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-parallel-eye-teardown-native-view-ownership-fixed).

- [x] **XR-I15** — Advance bounded child-retirement accounting during terminal XR teardown when no desktop production frame resets the shared budget. Build33 passes with zero warnings/errors. Start33 submits 5,592 projection frames, then a real session-exit request reaches normal teardown epoch 1, zero active/reserved/pending submissions, and zero pending XR generations (two queued/two drained). Completion and dependency proofs remain mandatory; ordinary polling/resolution budgets are unchanged. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-I13** — Authored graph/profile slots are separate from reserved XR native slots, slot-indexed owners cover that capacity, and native publication owners settle on exact completion or unsubmitted cancellation. Build58 passes with zero warnings/errors; Start59's paired receipt cohort verifies exact slot/owner settlement without early reuse, responsive output and zero final ownership across 17,829 submissions. Single-eye collection authority is separately tracked by XR-I21. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-I18** — RVC eye output uses the active thread-local framebuffer and the exact per-eye planner allocator/map through sealing and recording. Start59's inspected 896×1007 left/right previews show the cube with stereo parallax; moving it changes the later eye capture. Build58 passes with zero warnings/errors. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-I20** — Vulkan begin/end/acquire/release calls share the engine's device-admission and bound graphics-queue gates, in the reviewed order, without healthy-path allocation or GPU waits. Build58 passes with zero warnings/errors; Start59 completes 17,829 submissions and normal session teardown without publication failures or Vulkan validation errors. Hardware acceptance remains XR-V17. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-I17** — Runtime-owned acquire/release states use the full ownership-aware publisher; the sealed publisher admits engine-owned states only. Build38 passes with zero warnings/errors. Start38 observes 6,801 accepted submissions with zero publication failures, 6,801 real completions and retirements, and zero terminal ownership. Start40 confirms another 24,930 accepted submissions without publication debt. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-I19** — Treat OpenXR's maximum Vulkan instance API version as its tested-version ceiling. Keep minimum/version validity, Vulkan 1.4 loader/device/features, runtime device selection and native result checks authoritative. Build45 passes with zero warnings/errors; SteamVR advertises a 1.2 tested maximum but successfully creates a Vulkan 1.4 device, 2688×2688 swapchains and a focused session. Eye output remains separately unaccepted. Done 2026-09-14; [investigation][mirror-closeout].

- [x] **XR-A01** — Publish a complete submit-callsite ownership table: ordinary single/paired, parallel, SPS, external target, preview-only, and render-plus-publish. Done 2026-09-06; [source ownership inventory](../../progress/rendering/openxr-submit-ownership-audit-2026-09-06.md).

</details>

<details>
<summary>Admission, canonical records, visibility and reconstruction (8 completed items)</summary>

- [x] **ARP-I01** — Publish canonical record images and consume the 64-byte material/128-byte light record layouts through generated/accessor contracts. Done 2026-09-04; [E1], [S1].

- [x] **ARP-I02** — Match the visibility payload's 96-byte CPU/GLSL stride and clear integer sentinels only at the first visibility scope. Done 2026-09-04; [E2].

- [x] **ARP-I48** — Align the visibility candidate's individual fields with the GPU's 80-byte std430 record. Done 2026-09-07; [OpenGL implementation checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-opengl-implementation-checkpoint).

- [x] **ARP-I51** — Publish real standard material/kernel reconstruction requirements and finite tangent fallbacks. Textured OVR output is recorded; the full comparison matrix remains ARP-V56–V60. Done 2026-09-07; [reconstruction audit](../../progress/rendering/advanced-reconstruction-contract-audit-2026-09-07.md).

- [x] **ARP-I88** — Fix finite tangent reconstruction for the textured capture consumer while preserving reference handedness. GL/Vulkan captures have zero invalid pixels, down from 4,742. The full reconstruction matrix remains ARP-V56–V60. Done 2026-09-08; [publication checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#canonical-capture-publication-ownership).

- [x] **ARP-A01** — Complete the capability, selection, admission and editor/MCP profile inventory. Unsupported required-profile behavior remains ARP-V02. Done 2026-09-07; [admission/profile inventory](../../progress/rendering/advanced-admission-and-profile-audit-2026-09-07.md).

- [x] **ARP-A07** — Trace early/late indirect-count draws, depth-pyramid/retest dispatches and barriers into actual Vulkan backend commands. Disocclusion and layered execution remain runtime obligations. Done 2026-09-07; [execution inventory](../../progress/rendering/advanced-visibility-execution-audit-2026-09-07.md).

- [x] **ARP-A08** — Trace standard kernels through shared reconstruction, required-attribute masks and tangent handedness/MikkTSpace compatibility. Attribute-mask fixes are ARP-I51; comparisons remain ARP-V56–V60. Done 2026-09-07; [kernel/attribute inventory](../../progress/rendering/advanced-reconstruction-contract-audit-2026-09-07.md).

</details>

<details>
<summary>Classification and clustered-light capacity (7 completed items)</summary>

- [x] **ARP-I03** — Resolve draw/material/kernel handles and provide bounded classification storage for all 128 allowed kernel slots. Done 2026-09-04; [S1].

- [x] **ARP-I04** — Generate bounded per-kernel membership ranges and indirect dispatch arguments on the GPU, clamping consumers to initialized capacity. Done 2026-09-04; [S1].

- [x] **ARP-I05** — Derive froxel storage from extent, tile dimensions, depth slices, and view count. Done 2026-09-04; [S1].

- [x] **ARP-I28** — Implement subgroup ballot/scan classification with a bounded shared-memory fallback for devices lacking the required subgroup capabilities. Done 2026-09-06; [implementation resume](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-implementation-resume).

- [x] **ARP-I30** — Enforce derivative flags, membership kernel identity, metadata view identity, and overflow-safe producer/consumer range arithmetic at the executable classification/shading boundary. Done 2026-09-06; [classification audit](../../progress/rendering/advanced-classification-audit-2026-09-06.md).

- [x] **ARP-A05** — Audit the classifier key: dense kernel grouping, guarded layout/coverage, and derivative/view identity checked by the consumer. Executable guards are ARP-I30. Done 2026-09-06; [classification audit](../../progress/rendering/advanced-classification-audit-2026-09-06.md).

- [x] **ARP-A06** — Audit independent tile, membership and dispatch bounds and the GPU overflow-repair contract. Runtime capacity pressure remains ARP-V07. Done 2026-09-06; [classification audit](../../progress/rendering/advanced-classification-audit-2026-09-06.md).

</details>

<details>
<summary>Native materials, shadows, AO and GI (15 completed items)</summary>

- [x] **ARP-I06** — Wire canonical material constants/texture access and convention-aware shadow sampling into native opaque shading. Done 2026-09-04; [S1].

- [x] **ARP-I07** — Connect visibility/native HDR to refreshed post-process texture/sampler bindings. Done 2026-09-04; [E2].

- [x] **ARP-I08** — Produce per-tile/froxel decal lists and apply real surface/material modifiers before lighting. Done 2026-09-06; [compiled implementation evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-compiled-decal-and-executable-late-pass-integration).

- [x] **ARP-I09** — Publish and consume real IBL/probe/selected-GI resources through the provider contract. Done 2026-09-06; [canonical history/probe checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-canonical-history-and-probe-checkpoint).

- [x] **ARP-I16** — Add depth-derived GTAO dispatch and owned full-resolution R8 target after final visibility depth. Done 2026-09-04; [E1].

- [x] **ARP-I17** — Bind AO storage/sample at 49/50, namespace GLSL helpers, guard reconstruction math, and read the correct reversed-depth component. Done 2026-09-04; [E1].

- [x] **ARP-I18** — Provide `EnableBuiltInAmbientOcclusion` and a neutral-write path; keep custom providers explicitly rejected. Done 2026-09-04; [E1].

- [x] **ARP-I19** — Feed AO into the actual indirect-light contribution from `ARP-I09`. Done 2026-09-06; [canonical history/probe checkpoint][probe-history].

- [x] **ARP-I29** — Support R8Unorm AO texture diagnostic readback conversion and byte sizing. Done 2026-09-06; [AO capture checkpoint][ao-capture].

- [x] **ARP-I38** — Preserve authored Assimp diffuse RGB factors in the standard material factory so the native path receives actual imported colors. Done 2026-09-06; [native color diagnosis](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-native-color-input-diagnosis).

- [x] **ARP-I39** — Preserve exact recorded native depth-image layout and producer scopes when reopening an FBO after compute sampling. Done 2026-09-06; [canonical history/probe checkpoint][probe-history].

- [x] **ARP-I40** — Consume pending probe writer receipts at the registered light-probe world boundary, including one-shot captures. Done 2026-09-06; [canonical history/probe checkpoint][probe-history].

- [x] **ARP-I41** — Declare the forward probe-array/buffer imports used by Advanced late/debug consumers. Done 2026-09-06; [canonical history/probe checkpoint][probe-history].

- [x] **ARP-I42** — Resolve black probe-capture/convolution RGB and require completion evidence for the actual output writers before accepting usable IBL. Done 2026-09-06; [exact-writer RGB proof](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-probe-writer-rgb-and-disabled-provider-proof).

- [x] **ARP-I43** — Keep native stages executing with zero indirect contribution when GI mode is None or the selected native provider is null. Done 2026-09-06; [disabled-provider proof](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-probe-writer-rgb-and-disabled-provider-proof).

</details>

<details>
<summary>Temporal history and motion (12 completed items)</summary>

- [x] **ARP-I10** — Capture a bounded immutable desktop history candidate before every fallible plan/readiness/recording step. Done 2026-09-06; [H1].

- [x] **ARP-I11** — Attest history for the actual recorded outputs, including synthetic `RequiresFreshEmptyTerminalWrite` with no static operation context. Done 2026-09-06; [H1].

- [x] **ARP-I12** — Commit attested history only on the exact backend acceptance receipt. Done 2026-09-06; [H1].

- [x] **ARP-I13** — Discard every rejected/superseded candidate on readiness, recording, submit failure, and teardown. Done 2026-09-06; [H1].

- [x] **ARP-I14** — Add frozen desktop current-view history, per-draw temporal events, camera epochs, and explicit shader validity gates. Done 2026-09-04; [E1].

- [x] **ARP-I15** — Keep OpenXR view history pending until a successful layered EndFrame and clear it across tracking/lifecycle invalidation. Done 2026-09-04; [E1].

- [x] **ARP-I37** — Publish the exact resolved desktop history descriptor into the canonical GPU view record. Done 2026-09-06; [canonical history checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-canonical-history-and-probe-checkpoint).

- [x] **ARP-I55** — Make OpenGL temporal history copies cover all multiview layers without invalid framebuffer blits. Done 2026-09-07; [layer-copy checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-layered-temporal-copies-picking-and-editor-gizmos).

- [x] **ARP-I59** — Populate and record TSR history coverage in the Advanced command chain. Done 2026-09-07; [Temporal producer checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-tsr-history-and-transparent-temporal-producers).

- [x] **ARP-I60** — Bind the published Advanced reactive mask into TAA and TSR, with declared resource dependencies and matching mono/stereo debug views. Done 2026-09-07; [Temporal producer checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-tsr-history-and-transparent-temporal-producers).

- [x] **ARP-I61** — Correct the built-in colored-alpha temporal producer, per-eye motion bindings and explicit unsupported-input rejection. Accepted output is rigid OpenGL OVR; skin/morph/procedural deformation and NV stereo are excluded. Done 2026-09-08; [September 8 checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-transparent-motion-and-render-thread-ownership).

- [x] **ARP-I63** — Align native opaque history invalidation with the shared accepted-pose camera-cut policy. Separate translation/rotation cuts pass in OVR; OpenXR/mono and other reset causes remain separate. Done 2026-09-08; [Acceptance checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-native-cut-and-sorted-alpha-acceptance).

</details>

<details>
<summary>Background, transparency, post-processing and effect admission (15 completed items)</summary>

- [x] **ARP-I20** — Invoke the exact-transparency helper and late/temporal/post/output stages from the Advanced command chain. Done 2026-09-04; [S1].

- [x] **ARP-I31** — Evaluate exact-transparency consumer admission and selected peel-layer count at command execution, including objects becoming visible after pipeline construction. Done 2026-09-06; [late-lane audit](../../progress/rendering/advanced-late-lanes-audit-2026-09-06.md).

- [x] **ARP-I32** — Produce and merge participating transparent velocity/reactive output with native opaque motion. Done 2026-09-06; [compiled implementation evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-compiled-decal-and-executable-late-pass-integration).

- [x] **ARP-I33** — Wire visible refractive/feedback consumers to the scene-snapshot predicate and binding contract. Done 2026-09-06; [late-lane audit][late-audit].

- [x] **ARP-I34** — Implement PPLL's 128 MiB capacity bound, actual-buffer producer/resolve limits, ordered counter reset and link validation. Overflow rejection preserves preceding HDR; runtime pressure/output remains ARP-V29. Done 2026-09-06; [late safety checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-late-transparency-safety-checkpoint).

- [x] **ARP-I35** — Complete depth-peeling initialization/opaque-depth rejection and mixed transparency composition without overwriting preceding lanes from an obsolete scene copy. Done 2026-09-06; [late-lane audit][late-audit].

- [x] **ARP-I36** — Connect late-pass eligibility metadata to executable submission and expose unsupported lane/profile reasons. Done 2026-09-06; [compiled implementation evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-compiled-decal-and-executable-late-pass-integration).

- [x] **ARP-I52** — Fix Advanced scene-filter copies, layer counts, sampler ABI and executable DoF/motion-blur settings bindings. Done 2026-09-07; [scene-filter validation](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-stereo-scene-filters-and-executable-post-bindings).

- [x] **ARP-I53** — Connect existing Advanced post-stage uniform binding methods to the actual atmosphere, volumetric fog, temporal accumulation, TSR and weighted-transparent resolve quads. Done 2026-09-07; [scene-filter validation](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-stereo-scene-filters-and-executable-post-bindings).

- [x] **ARP-I64** — Sort Advanced alpha-blended draws back to front. Done 2026-09-08; [Acceptance checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-native-cut-and-sorted-alpha-acceptance).

- [x] **ARP-I67** — Execute compatible authored sky/background draws after native opaque shading and before transparency/post while preserving HDR alpha, depth and temporal sidecars. ARP-V25 retains the remaining profiles. Done 2026-09-08; [Background checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-authored-background-admission-and-depth-preservation).

- [x] **ARP-I68** — Expose particle and landscape Advanced unsupported-profile reasons in their actual inspectors and MCP, and reject their callback draws before they bypass native/late admission. Done 2026-09-08; [A03 closure](../../progress/rendering/advanced-special-effects-and-upscaler-audit-2026-09-07.md#september-8-closure).

- [x] **ARP-A02** — Complete the late-lane resource/capacity inventory. The metadata, motion, feedback and recovery corrections are recorded in ARP-I31–I36; lane acceptance remains separate. Done 2026-09-06; [executable late-lane audit](../../progress/rendering/advanced-late-lanes-audit-2026-09-06.md).

- [x] **ARP-A03** — Record water, hair, particles, trails, beams, portals, mirrors and displacement-family dispositions. Dedicated hair/trail/beam renderers are absent; particle/landscape rejection is explicit. The inventory does not establish rendering support. Done 2026-09-08; [family dispositions and diagnostic closure](../../progress/rendering/advanced-special-effects-and-upscaler-audit-2026-09-07.md#september-8-closure).

- [x] **ARP-A04** — Inventory native Vulkan and OpenGL/Vulkan-bridge vendor profiles, device/runtime gates and exclusions. ARP-V61–V67 own acceptance; no device is certified. Done 2026-09-07; [profile inventory](../../progress/rendering/advanced-special-effects-and-upscaler-audit-2026-09-07.md).

</details>

<details>
<summary>Stereo execution and independent output banks (11 completed items)</summary>

- [x] **ARP-I21** — Implement immutable per-view/layer addressing across visibility, classification, shading, depth, velocity, and histories. Done 2026-09-07; [implementation record][investigation].

- [x] **ARP-I22** — Integrate Advanced-compatible work into RVC-owned OpenXR eyes. Done 2026-09-07; [implementation record][investigation].

- [x] **ARP-I23** — Implement capability-based offscreen intent/export/completion contracts and owner registration. Standalone profiles have owners; the actual Advanced mirror integration remains ARP-I81. Done 2026-09-07; [implementation record][investigation].

- [x] **ARP-I26** — Implement OpenGL SPS admission, native layered execution, fenced scene/sampler residency and independent per-eye histories. The accepted emulated profile is ARP-V45; deformation and hardware XR remain separate. Done 2026-09-07; [OpenGL implementation checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-opengl-implementation-checkpoint).

- [x] **ARP-I27** — Implement the conservative foveated derivative/LOD policy for admitted Advanced XR profiles. Done 2026-09-07; [implementation record][investigation].

- [x] **ARP-I45** — Implement generation-safe output-bank retirement/reuse after all plan leases and GPU uses complete, with bounded capacity and allocation-failure diagnostics. Arbitrary XR/multi-owner pressure remains unproven. Done 2026-09-07; [implementation record][investigation].

- [x] **ARP-I46** — Honor accepted view-history validity in depth-pyramid planning. Done 2026-09-06; [output-bank checkpoint][historical-stop].

- [x] **ARP-I47** — Give eight logical output reservations independent occlusion, frame-slot descriptors, immutable inputs and stable bins, with exact-reservation preparation and activation-time CPU workspaces. Retirement/reuse is ARP-I45. Done 2026-09-06; [final wrap-up checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-final-wrap-up-checkpoint).

- [x] **ARP-I49** — Fix OpenGL stereo output storage/view formats and invalid copy/bindless-sampler operations. Done 2026-09-07; [clean final stereo cohort](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-validation-resumption-opengl-final-stereo-output).

- [x] **ARP-I50** — Fix fullscreen stereo vertex/FBO agreement on OpenGL, including bloom and FXAA. Done 2026-09-07; [clean final stereo cohort](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-validation-resumption-opengl-final-stereo-output).

- [x] **ARP-I69** — Fix Vulkan multi-output input-family provisioning so activation and lowering use the same static operation stream. This closes the admission failure; output and pressure proof remain profile-specific. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-vulkan-capture-provisioning-and-readback).

</details>

<details>
<summary>Probe, standalone and mirror capture ownership (19 completed items)</summary>

- [x] **ARP-I71** — Assemble Vulkan probe texture-array layers from the actual GPU source images, preserving prefilter mips, ordering and source/destination lifetime. Later mip-production fixes are ARP-I72/I73. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-probe-gpu-array-ownership-and-mip-production).

- [x] **ARP-I72** — Use attached mip extents when snapshotting Vulkan mesh viewport/scissor state. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-probe-gpu-array-ownership-and-mip-production).

- [x] **ARP-I73** — Allocate the explicit captured-environment cubemap mip chain before face rendering. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-probe-gpu-array-ownership-and-mip-production).

- [x] **ARP-I74** — Record Advanced offscreen exports in their declared transfer pass after all source writers. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-probe-gpu-array-ownership-and-mip-production).

- [x] **ARP-I75** — Key Vulkan canonical scene publications by the exact frozen view/frame/pass globals, coverage and diagnostic count so independent capture faces do not share stale data. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-probe-gpu-array-ownership-and-mip-production).

- [x] **ARP-I76** — Fix probe-refresh allocation growth by activating buffers before upload and enabling deferred destruction. The 20-refresh Vulkan cohort reaches a stable allocation plateau (ARP-V48). Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-probe-gpu-array-ownership-and-mip-production).

- [x] **ARP-I77** — Complete standalone thumbnail/portal ownership, synchronized submission/publication and lease-gated retirement. Retained readers survive destruction and 20 rapid reactivation/resize cycles before a fresh capture. Done 2026-09-08; [Investigation](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-capture-scheduling-follow-up).

- [x] **ARP-I78** — Complete OpenGL probe writer settlement on the render thread and retry required convolution preparation. Twenty-refresh GL and Vulkan cohorts retain finite, matching source/array mips. Done 2026-09-08; [Investigation](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-capture-scheduling-follow-up).

- [x] **ARP-I79** — Distinguish physical texture tombstones from live references during OpenGL bindless lowering; retain fenced publication residency and reject inconsistent live/stale material rows. Done 2026-09-08; [Investigation](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-capture-scheduling-follow-up).

- [x] **ARP-I82** — Add standalone depth/visibility owners with exact output aspects, data-only stage/resource admission and completion/lease-gated retirement. Saved-file acceptance is ARP-I85/V50. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-data-capture-validation).

- [x] **ARP-I84** — Fix stale OpenGL visibility identity with fenced alias replacement that preserves logical sources and bounds the cache to 16 roles per slot. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-data-capture-validation).

- [x] **ARP-I86** — Retain standalone capture generations through canonical scene publications with exact-generation admission and atomic publication/retirement. GL/Vulkan readers prevent rewriting until withdrawal and release; this is a mirror prerequisite. Done 2026-09-08; [publication checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#canonical-capture-publication-ownership).

- [x] **ARP-I87** — Clear erased material rows and payload tails before native validation while sealed snapshots retain their copies. GL/Vulkan consumer removal, reader drain and resize complete without stale binding rejection. Done 2026-09-08; [publication checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#canonical-capture-publication-ownership).

- [x] **ARP-I92** — Complete existing mirror writer polling/package release on the render thread, 16-entry consumer-fence storage, serialized retirement, registered-intent tracking and viewport/FBO/texture replacement. Advanced targets use RGBA16F with only requested work. Default GL Start119 completes 12,147 captures and serial resize/deactivation to zero ownership; its source image is black. ARP-I81/I91/I93/I94 and ARP-V36 remained open at that checkpoint; the September 14 rows below supersede the mirror implementation status. Done 2026-09-08; [wrap-up evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-validation-wrap-up).

<a id="arp-i91"></a>

- [x] **ARP-I91** — Correct zero-to-one oblique clipping with scale-robust plane validation, finite/invertible projection checks and candidate-before-commit setters. Reflected owners configure the lens and transform before clipping; private capture size no longer mutates the source lens. Native reconstruction follows the sealed depth convention on GL as well as Vulkan. Forward/reverse perspective and off-center orthographic Vulkan views retain/clamp the expected halfspaces; native and legacy GL reflections are visible. Done 2026-09-14; final Build23, zero warnings/errors; [evidence][mirror-closeout].

<a id="arp-i93"></a>

- [x] **ARP-I93** — Retain exact legacy mirror generations from collection through reset/rejection, then independently through GPU completion. Planned native materials hold their generations until the canonical snapshot owns them. Resize and rapid reactivation defer while readers remain; Vulkan and native/legacy OpenGL deactivation drain all banks. Failed Vulkan output authoring settles its exact queued operations and producer/input ownership. Unknown completion quarantines its resources. Done 2026-09-14; final Build23; [ownership and live evidence][mirror-closeout]. Broader delayed/faulted scheduling remains ARP-V36.

<a id="arp-i94"></a>

- [x] **ARP-I94** — Withhold unwritten mirror generations while capture scheduling continues. Native early/late visibility requires a valid completed entry matching the exact source camera before writing visibility or depth; legacy collection requires the retained completed slot. Replacement withdraws the old row and clean rejection leaves the new output unpublished. Controlled native GL/Vulkan withdrawal leaves all three fixture draws resident while the mirror writes no color/depth. Done 2026-09-14; final Build23 and live shader validation; [evidence][mirror-closeout].

<a id="arp-i95"></a>

- [x] **ARP-I95** — Share exact database/publication scene payloads within one Vulkan slot generation, while retaining independent Views, FrameMetadata, global descriptors and native-use receipts. Preserve resource descriptor ranges and monotonic cursors; revalidate mutable sources. Occupied-slot pressure records bounded demand for growth only at an empty completed slot boundary; aggregate ceiling violations remain terminal. Done 2026-09-14; Build29, zero warnings/errors; Start25/26/29 validate streamed main/mirror output, two mirrors and changing publications. Start29 has 81 native draws, fresh capture 1012 and fully drained retirement at 1140. No occupied-pressure rejection occurred in these cohorts; explicit rejection/recovery proof remains ARP-V36. [Implementation and live evidence][mirror-closeout].

<a id="arp-i81"></a>

- [x] **ARP-I81** — Integrate the Advanced mirror owner with three explicit camera banks, two persistent slots each, retained texture/projection/generation publication and native opaque projective shading. Single-pass stereo admission requests both camera identities; reflection views exclude mirror materials. HDR capture omits unrequested main-view post work. Bounded native Vulkan and GL output, resize, depth-mode and lifecycle cohorts pass; full output/profile acceptance remains ARP-V36, including delayed-reader and rejection/recovery pressure. Done 2026-09-14; final Build23; [implementation and evidence][mirror-closeout]; [authoring contract](../../../architecture/rendering/planar-mirror-capture.md).
</details>

<details>
<summary>Editor consumers, diagnostics and settings (16 completed items)</summary>

- [x] **ARP-I24** — Connect asynchronous GL/Vulkan picking and editor/MCP consumers to retained canonical identity, with stale-generation rejection. OpenXR picking remains unavailable without an accepted XR source path. Done 2026-09-06; [final wrap-up checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-final-wrap-up-checkpoint).

- [x] **ARP-I25** — Expose real profile capability/blocker state and capture-stable per-stage resources/counters through editor/MCP. Done 2026-09-07; [implementation record][investigation].

- [x] **ARP-I44** — Correct linear HDR radiance/alpha export and Q16 quantum scaling. The later FLOAT32 writer is ARP-I85; earlier broken EXRs are excluded from pixel evidence. Done 2026-09-06; [HDR export checkpoint][ao-ibl].

- [x] **ARP-I54** — Capture related live resources at one post-render boundary and support OpenGL 2D/array view depth/stencil readback. Done 2026-09-07; [grouped readback evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-grouped-motion-captures-and-temporal-copy-failure).

- [x] **ARP-I56** — Reject unavailable OpenGL picking storage before dispatch, preserve pack-buffer state and check GPU transfer errors. Done 2026-09-07; [picking checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-layered-temporal-copies-picking-and-editor-gizmos).

- [x] **ARP-I57** — Select a complete compatible shader-stage combination for editor/late meshes targeting OpenGL OVR multiview. Done 2026-09-07; [Temporal/editor acceptance](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-material-includes-and-mono-stereo-gizmo-acceptance).

- [x] **ARP-I58** — Preserve the explicitly declared filtered direct submission of participating Advanced late motion/reactive passes under a global opaque GPU strategy override. Done 2026-09-07; [temporal/editor acceptance](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-material-includes-and-mono-stereo-gizmo-acceptance).

- [x] **ARP-I62** — Dispatch viewport/output rebinding and pipeline resource transitions by render-thread ownership, not context-local renderer activity. Done 2026-09-08; [September 8 checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-transparent-motion-and-render-thread-ownership).

- [x] **ARP-I65** — Reject GL texture capture before mip queries when the API wrapper has no live texture storage. Done 2026-09-08; [Capture checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-capture-liveness-and-mono-scene-filters).

- [x] **ARP-I66** — Preserve MCP numeric structures with explicit converters and transactional rejection of malformed input. Vector2/3/4, Quaternion and Matrix4x4 are covered in source; the live fixture exercises Vector3. Done 2026-09-08; [Scene-filter checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-capture-liveness-and-mono-scene-filters).

- [x] **ARP-I70** — Correct Vulkan depth/stencil readback: D32+stencil depth copies decode four-byte floats and stencil copies decode one byte. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-vulkan-capture-provisioning-and-readback).

- [x] **ARP-I80** — Implement OpenGL cube-face/mip readback and bounds checks. Six faces and six mip-6 images pass; cube arrays/views have source support without separate live acceptance. Done 2026-09-08; [Investigation](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-capture-scheduling-follow-up).

- [x] **ARP-I83** — Decode Vulkan RG32_UINT diagnostic readback. Float images support inspection; raw integer picking remains authoritative for arbitrary 32-bit handles. Done 2026-09-08; [Investigation](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-data-capture-validation).

- [x] **ARP-I85** — Write uncompressed FLOAT32 diagnostic EXRs without quantum scaling, half conversion or nonfinite substitution. Independently decoded files match runtime hashes; historical HALF visibility exports are excluded. Done 2026-09-08; [Evidence](../../investigations/rendering/vulkan-phase67-implementation.md#standalone-data-capture-validation).

- [x] **ARP-I89** — Separate mono/OVR/NV gizmo shader entry points so inactive stereo built-ins cannot reject mono admission. GL mono/OVR and Vulkan gizmos render; the later placement fix is ARP-I90 below. Done 2026-09-08; [entry-point checkpoint](../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-gizmo-entry-point-admission).

<a id="arp-i90"></a>

- [x] **ARP-I90** — Resolve Vulkan selected-primitive placement and the extra gizmo image. Imported texture metadata publishes through a stable epoch, and acknowledged unpinned scene snapshots reclaim capacity around pinned readers. Start04's texture-free fixture and Start22's 79-draw streamed fixture show one cube and one centered gizmo at three requested positions/rotations. Start22 advances frames 3469–3542 and publication 56–60 with one retained snapshot, native stages accepted and deformation ready. Done 2026-09-14; Build21/Start22 and final Build23, zero warnings/errors; [inspected placement evidence][mirror-closeout]. Broader editor-consumer acceptance remains ARP-V37.

</details>

## Evidence and maintenance

The [September 14 closeout][mirror-closeout] records the six completed ARP tasks,
Build29 mirror acceptance, Build35 (zero warnings/errors), the Phase 6 failed
visual baseline and successful terminal drain, and the exact paused handoff.
No tests were added or changed by this work.
The [September 8 wrap-up][wrapup] retains Build145, failed Start120, the removed
Build144 experiment and the earlier task-owned editor shutdown as historical facts.

Earlier [September 6][historical-stop] and [Build 32][gl-implementation] snapshots
are retained in the investigation. Their old implementation-complete statements,
launcher/storage blockers and checkbox counts do not override the current ledger.
The original September 4 Monado cohort had AO descriptor errors; `XR-V01` records
the later clean rerun. The initial static capture and source audit remain [E2] and [S1].

The unchanged existing tangent test passed 1/1 through an isolated linked-source
runner. At the latest recorded check, the normal UnitTests project still failed
to compile against stale rendering/XR APIs. This is not a full test-suite pass.
No test methods were added or changed during that work.

Update rules:

- Close an **I** row when its behavior exists and its narrow build/compiler check
  passes. Close an **A** row with a recorded inventory; assign discovered gaps to
  explicit implementation tasks before dependent validation can close.
- Close a **V** row only with its specified evidence. Runtime records must name
  revision/build, backend, output profile, configuration, duration or retained-frame
  count, observed failures and capture/trace results. `ARP-V01` is explicitly a
  compiler-only record. Supported flags and successful builds are not runtime proof.
- Change the owning checkbox in the same change and append
  `Done YYYY-MM-DD; revision/build; evidence link; observed result`. Update this
  status summary, the investigation and the next task at the same time.
- Keep task IDs stable and one checkbox per obligation. If an item needs independent
  outcomes, split it before work starts and give the new tasks new IDs. A checked
  implementation beside open validation means implemented, awaiting proof.
- Keep unavailable hardware/runtime gates open with their exact blocker. Record
  durable findings in the investigation; ignored captures/logs support that record.
- Follow the master Phase 9.1 test-clearance policy; feature validation does not
  authorize adding or changing tests without explicit user clearance.

## Exit and handoff to promotion

Before Phase 8, resolve
all applicable I/A tasks and their V tasks, record unsupported required
profiles as blockers, and freeze the integrated revision. Hardware absence
does not turn an open gate into a pass.

The master retains the single owners for architecture inventory/budgets,
steady-state allocation/readback/wait evidence, five-strategy parity,
100/120/144 Hz promotion, explicit test clearance, legacy deletion, and final
documentation. Closing a feature implementation box here does not waive them.

## Legacy requirement map

| Former section | Sole current owner |
|---|---|
| Architectural refactor 01–02 | Completed phase records; `ARP-I01`/`ARP-I02` and `ARP-A01`–`ARP-V03` retain current integration/acceptance ownership |
| Architectural refactor 03–05 | `ARP-A07`–`ARP-A08`, `ARP-V51`–`ARP-V60`; material coverage `ARP-V13`, temporal fixtures `ARP-V17`–`ARP-V21` |
| 6.1–6.3; 7R.2–7R.3 | XR submission/admission and timing rows |
| 6.4; 7R.4 | XR lifecycle rows, including `XR-I09`/`XR-I10` |
| 7.1; 7R.6 | `ARP-I03`–`ARP-I05`, `ARP-I28`, `ARP-A05`–`ARP-A06`, `ARP-V05`–`ARP-V11` |
| 7.2; 7R.5; 7R.7 | Canonical contracts, native surfaces/shadows, AO, GI, and background rows |
| 7.3; 7R.8 | Temporal, late/post audit and validation rows |
| 7.4; 7R.9 | Stereo/offscreen/editor and diagnostics rows |
| 7.5; 7R.1 | `ARP-A01`/`ARP-V02` plus master Phase 8/9 production and budget gates |
| 7R.10 | Evidence/update rules and per-feature V rows here; master Phase 8/9 for integrated acceptance/tests |

[E1]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-04-wrap-up-and-resume-boundary
[E2]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-04-checkpoint-typed-publication-and-runtime-status
[E3]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-04-checkpoint-successful-monado-submissions-and-ao-admission
[S1]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-tracker-reorganization-and-source-audit
[H1]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-desktop-history-sequence-checkpoint
[investigation]: ../../investigations/rendering/vulkan-phase67-implementation.md
[wrapup]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-08-validation-wrap-up
[implementation-resume]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-implementation-resume
[ao-capture]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-ao-capture-checkpoint
[probe-history]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-canonical-history-and-probe-checkpoint
[ao-ibl]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-exr-export-and-aoibl-contribution-acceptance
[late-audit]: ../../progress/rendering/advanced-late-lanes-audit-2026-09-06.md
[historical-stop]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-06-final-wrap-up-checkpoint
[gl-implementation]: ../../investigations/rendering/vulkan-phase67-implementation.md#2026-09-07-opengl-implementation-checkpoint
[publication]: ../../investigations/rendering/vulkan-phase67-implementation.md#canonical-capture-publication-ownership

[mirror-closeout]: ../../investigations/rendering/arp-mirror-placement-2026-09-14.md

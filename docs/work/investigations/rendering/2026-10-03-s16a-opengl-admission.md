# OpenGL Advanced black scene on the measurement host (S16a)

## October 4 post-resize shadow validation

Hypothesis: the retained cascade target publication and matched 1024-pixel
directional-shadow settings preserve depth/HDR parity after the main window
resizes. Run OpenGL and Vulkan serially with the same nonprocedural Sponza,
Advanced/CpuDirect fixture. Resize to 1600x900, then restore 1920x1080; at each
size capture the prior lit and atrium camera poses after admission settles.
Record actual dimensions, accepted stage progress, Vulkan completed-frame
progress, raw layer-zero atlas depth and HDR EXR statistics, and view the PNGs.
Acceptance requires no terminal output failure, populated cascades and comparable
raw depth/HDR distributions at matching sizes (investigate a mean HDR difference
above 1% or depth occupied-fraction difference above one percentage point).
Final display images are supplementary because exposure policies differ.
No implementation change or throughput claim is part of this validation.

First attempt: OpenGL PID 40268 passes both size transitions, resource generations
2 -> 3 -> 4, with advancing accepted preparation/raster/native-shading stages.
All four requested-view depth/HDR/final captures were viewed; the first pose is
mostly dark and the atrium clearly differs. Both light dimensions remain 1024.
Vulkan PID 12160 fails the correctness gate at frame 3283, slot 0, generation 3:
set-2/set-3 scene publication rejects a write to binding 0 of an in-flight
storage-buffer descriptor set. Both 1600x900 captures have zero completed-frame
progress (195 and 125 rejected frames during their capture windows), so their
identical output is stale and cannot establish parity. The later restored-size
readback rejects the retired 1600x900 generation as intended. Do not attribute
the terminal failure to the second resize: it already existed during the first
capture. Later canonical source-mismatch diagnostics are secondary observations.
The owned sessions are stopped. Stderr is empty; explicit MCP terminal evidence
is authoritative. Next trace the descriptor's publication/reuse ownership;
retain the in-flight update guard and frame-slot waits.

Evidence under the task run: `reports/shadow-resize-{OpenGL,Vulkan}.json`,
`reports/shadow-resize-vk-failure-{stats,diag}.json`,
`reports/shadow-resize-summary.json`, and `mcp-captures/shadow-resize-*`.
Raw comparison numbers from this rejected Vulkan run are not acceptance evidence.

A fresh process (PID 50200) reproduces the same binding-0 storage-buffer lifetime
failure without texture or screenshot readbacks. It begins with 1,438 completed
frames and reaches 1,883 after the first resize. The subsequent lit camera cut
becomes terminal at frame 2062, slot 0, generation 3, with 1,932 completed frames.
The observer stops on that failure. This rules out heavy capture as necessary
to trigger it; resize plus the changed visible scene is sufficient in this run.
Evidence: `reports/resize-publication-repro.json`.

Next bounded change: enrich only descriptor-update failures with owner/generation,
reference pins, last-use versus completed/observed queue-domain sequences, the
exact retained last-use submission and earliest pending predecessor in each
incomplete domain, plus Advanced scene slot use/generation details. The slot's
exact timeline receipt and the lifetime authority's conservative domain frontier
are different proofs; the current error alone does not establish actual GPU
overlap. Preserve all guards, completion semantics and success-path work. Build,
repeat the no-readback reproduction, and use that evidence before a lifetime fix.

The diagnostic build passes with zero warnings/errors in 18.03 seconds. PID 35024
reproduces at frame 4195 on the second small-size lit cut. The rejected global
set has two recorded references and no active scene uses. Its graphics last-use
pin is 4704 while completed/observed are both 4702. The exact retained submission
4704 is fence-backed and unobserved; its predecessor 4703 is on the same queue,
timeline value 3994, also unobserved. The scene slot is 0 with current/requested
generation 3995 and zero entries/active uses. This rules out a completion frontier
blocked solely by another queue. Trace why the fence-backed use can outlive the
scene slot's timeline ownership; do not relax the descriptor guard or convert
the failure to a blind retry after uploads. Evidence:
`reports/resize-publication-diagnostic.json`.

Static review confirms the new snapshot uses the existing lifetime lock and adds
no successful-path formatting, allocation or native polling. Existing hardening
tests pass 14/18, including the descriptor mutation guard. Four source-contract
assertions in command-pool synchronization, pipeline registration, queue-gateway
text and worker-pool retirement fail outside this diagnostic change. No tests
were modified. Results: `reports/tests/descriptor-diagnostics-existing.trx`.

The next diagnostic repeat adds command-buffer owners to that same failure-only
snapshot: the set's reverse dependencies and commands carrying its last submission
serial, including full/touched/sealed dependency-vector membership. Static tracing
shows buffer/image dependencies do not expand backwards into descriptor sets;
identify the fence-backed command before changing slot or submission ownership.

The owner snapshot identifies `TextureUpload.Transfer` as the unexpected scene
descriptor consumer (exact and touched dependency vectors, no sealed contract).
The other owner is a prior swapchain primary. Review found that the lane registry
can retain old handle keys while reusing a lane-slot context object; lookup did
not validate active state or the object's current handle, and abort/reset/free
did not remove registrations. The generic command-end path then imports the
wrong lane receipt into an upload. Evidence: `reports/resize-publication-owner.json`.

Bounded correction hypothesis: enforce exact active handle/recording-generation
ownership in the lane registry, detach registrations on abandonment, successful
reset and destruction, and validate receipt ownership at command end. Refuse
overlapping live lane ownership. Do not alter scene-slot reuse, descriptor guards,
queue waits or feature settings. Acceptance: clean build, repeated resize/cuts
during streaming and after settling with advancing completed output and no
terminal fault, then fresh viewed captures and the relevant existing checks.

The lane-owner correction builds with zero warnings/errors in 28.43 seconds.
Review found no policy or ownership blocker. PID 46644 completes three resize
round trips and twelve camera cuts, advancing from 137 to 11,415 completed
frames. Thirteen transient rejected frames occur during streaming/resize;
failed remains zero and no terminal state is retained. Preserve these transient
outcomes rather than claiming the mutation loop is rejection-free. The four
subsequent settled capture windows advance 38/49/36/56 completed frames with
zero rejected/failed deltas. All depth/HDR/final images were viewed and differ
between the two poses. Evidence: `reports/resize-publication-lane-context.json`
and `reports/shadow-resize-Vulkan-fixed.json`.

Depth comparison uses each published light slot's `InnerPixelRect`, obtained
through `evaluate_expression` on `_desktopCascadeState.AtlasSlots`. Whole-atlas
occupancy is invalid because unused texels and placement differ by backend.
Undo capture flipping first, then apply the documented framebuffer Y-direction
mapping from `ShadowAtlasManager.CreateAtlasUvScaleBias` to the logical tile
rectangles. Keep physical crop rectangles and capture orientation in evidence.

Matched OpenGL PID 7232 repeats the same sizes/poses with requested and allocated
1024-pixel cascades. All eight backend/size/view captures qualify with advancing
accepted stages; all four Vulkan capture windows have zero rejected/failed deltas.
Images from both backends were viewed. The 1020-by-1020 inner tiles have identical
occupied counts except cascade 1 in the atrium, which differs by one texel.
The largest compared 5th/50th/95th occupied-depth quantile difference is below
0.00001. This validates raw shadow raster depth through resize despite different
tile placements and unused atlas clear values.

HDR shading remains outside the declared 1% threshold in the dark view:
GL/Vulkan means are 0.00059852/0.00068278 at 1600x900 and
0.00059927/0.00068354 at 1920x1080 (12.34%/12.33%). Atrium means differ by
0.24%/0.26%. Keep HDR/display parity open and investigate sampled shadow state
and shading inputs; do not loosen the threshold or attribute this difference to
raw shadow raster depth. Evidence: `reports/shadow-resize-{OpenGL,Vulkan}-fixed.json`,
`reports/shadow-resize-summary-fixed.json` and corresponding viewed contact sheets.

An orientation-aligned per-pixel tile comparison strengthens the distribution
check: maximum tile mean absolute depth difference is 3.72e-7, p99 is at most
2.39e-7, and at most 0.017% of a tile differs by more than 1e-4. Sparse boundary
outliers remain (maximum 0.1551 at the one-texel atrium occupancy difference);
the images are not claimed bit-identical. Evidence:
`reports/shadow-resize-depth-pixels.json`.

The final existing hardening rerun remains 14/18 with the same four source-contract
failures as the diagnostic-only build. No test changes. The owned editor is stopped;
user-owned VR runtimes were not stopped. The correction and all evidence are
uncommitted. Results: `reports/tests/lane-context-existing.trx`.

Next HDR investigation: capture the same Vulkan lit pose before and after a
1600x900 -> 1920x1080 resize round trip, retaining raw HDR and scene/shadow
publication state. Use RenderDoc to inspect the actual shading inputs if the
before/after difference persists. Hypothesis is a changed sampled-state/input
contract, not changed atlas raster depth. Keep original feature settings and
shadow resolution. `RenderDocFriendly` enables capture diagnostics and skips
unused optional Streamline proxy provisioning; it is not a throughput run.

The fresh Vulkan pair has identical raw HDR means before and after resize
(0.00068353565, maximum 0.14953613). Resize is therefore not established as the
cause of the remaining cross-backend difference. Offline display-aligned HDR
comparison localizes 97.8% of absolute error to 0.286% of pixels, concentrated
on bright architectural and foliage edges. Between 88% and 91% of error lies
in a gradient-edge mask covering about 1.1% of pixels; the median bright-pixel
GL/Vulkan gain is 1.0. A global exposure correction is not supported. HDR alone
does not distinguish visibility/material edges from shadow-sampling edges.
Evidence: `reports/hdr-resize-rdc.json`,
`reports/shadow-resize-hdr-localization.json` and viewed paired/difference PNGs.

Stationary next-present captures contain only postprocessing/UI work. An explicit
capture spanning a small camera cut includes Advanced visibility raster (393
draws) and native shading (136 dispatches). Inspect those inputs next, retaining
the 1% investigation threshold and original feature settings. The named editor
was stopped after capture; no renderer implementation changed for this check.
Evidence: `renderdoc/hdr-motion-explicit_capture.rdc`,
`reports/hdr-motion-rdc.json` and `reports/hdr-motion-passes.json`.

Next isolation: toggle only the directional light's `CastsShadows` in fresh
serial backend sessions, capture raw HDR with shadows on/off/restored at the
same lit pose, and restore the original value in a finally block. Hypothesis:
the localized difference is introduced by shadow evaluation rather than visible
surface reconstruction. Compare the same discrepant pixel neighborhoods and
require fresh admitted output. This temporary diagnostic is not a feature-reduced
acceptance or performance run. Do not change authored scene settings or tests.

For a shadow-dependent difference, reuse the prior temporary GPU depth probe
with fresh byte-for-byte backups: output biased receiver depth, sampled center
depth and their signed difference, with the selected cascade in alpha. Compare
the fixed pixel neighborhoods between backends before changing any shader bias.
Restore all three authored files in a finally block and verify their hashes.

Fresh-process isolation qualifies the earlier edge finding: OpenGL's previously
missing bright patch is already present with shadows enabled in the new run.
On/off/restored images show the same pixel there; a thinner adjacent coverage
difference remains with shadows disabled. Current shadows-on means differ by
3.18%, versus 0.076% with shadows off. Do not claim the toggle proves a shadow
implementation fault. OpenGL's new pre/post-resize shadows-on images are identical.
Warm Vulkan capture windows advance 13/9/11 completed frames with no rejected,
failed or terminal outcomes. Exclude its first all-black startup capture. The
three temporary probe files restore to identical hashes on both backends.
Evidence: `reports/hdr-shadow-toggle-offline-audit.json`,
`reports/hdr-shadow-toggle-{OpenGL,OpenGL-resized,Vulkan-warm}.json`,
and `reports/hdr-depth-probe-{OpenGL,Vulkan}.json`.

Viewed shadows-off images expose blur in OpenGL curtain and vase materials.
Active texture rows have matching desired/resident dimensions and no pending
transitions: curtain diffuse 1024 of 2048, vase diffuse 512 of 1024, vase normal
256 of 1024. Vulkan also lists inactive duplicate names at 64 with no sampler;
exclude them when joining active rows. Source review identifies a double mip
offset candidate: the GL texture uses its resident base level while the canonical
sampler minimum is also raised to that absolute level. Investigate the native
sampler's relative LOD contract before changing shading or shadow bias.
Evidence: `reports/hdr-material-textures-{OpenGL,Vulkan}.json`.

Bounded correction hypothesis: encode authored sampler LOD limits relative to
the exposed resident chain for valid published sparse textures. The native GL
texture already applies the absolute resident base; folding it into the sampler
minimum skips resident detail again. Preserve authored bias, filtering and
anisotropy, and leave non-sparse/dense handling unchanged. Acceptance: clean
isolated build; matched, fresh lit/atrium captures with restored material detail;
on/off/restored diagnostics and post-resize checks retain all original features
outside the diagnostic toggle. Existing targeted checks may run after live
validation; no tests are added or modified. Edge parity remains separately open
unless its declared numeric gate passes.

The depth probe gives a signed-depth mean absolute difference of 0.00000525.
Receiver and stored depth match exactly at the original missing-patch pixel;
both backends retain negative signed depth at the adjacent edge. This does not
establish a projection or bias defect. Vulkan accumulates rejected frames
between probe and restoration captures, so the successful individual windows
do not certify the complete shader-reload cycle.

The sparse sampler candidate builds cleanly and passes source review. Initial
captures still show blurred material detail; later native inspection confirms
resident base levels 1/1/2 for curtain diffuse, vase diffuse and vase normal,
paired sampler minimum LOD zero, and correctly populated resident mips. A
temporary diagnostic initially called OpenGL without the owning context and
terminated that diagnostic session; scheduling it after viewport rendering
corrected the diagnostic. This was not a renderer workload failure.

After inspection and additional settling, the on/off/restored HDR means are
0.00068032165/0.040887687/0.00068032165. Both resize sizes and both views pass
the 1% comparison threshold in `reports/shadow-resize-summary-sampler-settled.json`;
the contact sheet was viewed. These observations alone do not distinguish
normal settling from an effect of material readback. Remove the temporary native
diagnostic and repeat in a fresh process without native inspection or material
texture readbacks. Keep the HDR checkbox open until this repeat passes.

The clean candidate build has zero warnings/errors (22.76 seconds). Fresh PID
46036 reproduces the old dark-view mean, 0.00059852004 at 1600x900 and
0.0005992696 at 1920x1080, without the native diagnostic. An isolated vase-normal
readback changes the latter only to 0.00059932953; it does not restore parity.
Keep the earlier passing run as an observation, not reproducible acceptance.

Managed inspection before that readback finds `vase_round` and its normal map
with sparse state disabled, eight resident mips, but `LargestMipmapLevel=3`.
The vase diffuse/normal and curtains remain sparse. Source tracing shows that
promotion-fade sampler changes can retire the last frozen bindless identity,
then recreate storage. `GLTexture2D.PostDeleted/PostGenerated` clears sparse
metadata without rebasing the retained resident mip chain; the dense uploader
addresses that chain from zero. This is a distinct, confirmed metadata mismatch.
Evidence: `reports/clean-texture-state.json`, `reports/clean-leaf-state.json`,
`reports/hdr-shadow-toggle-OpenGL-clean-{before,after}-readback.json` and
`reports/shadow-resize-OpenGL-sampler-clean.json`.

Next bounded correction hypothesis: when native sparse storage retires, publish
the retained resident chain's dense mip range and canonical generation together.
Preserve authored sampler clamps, pending-upload/lease ownership and guards.
Acceptance remains a clean build and fresh, viewed matched lit/atrium captures
at both sizes, without native inspection or material readback prerequisites.

The retained correction uses relative sparse sampler limits and atomically
rebases retained mips on native storage retirement. It preserves narrower
maximum ranges, authored LOD limits/bias, and all native lease/fence guards.
Synchronous property notifications join an already-active metadata transaction.
Review found no remaining blocker; sampler-only recreation avoidance remains a
separate design question because legacy texture-owned sampling must stay valid.

Fresh PID 49764 reproduces the corrected dark-view mean without diagnostic or
material readbacks. Its first atlas metadata query catches an incomplete
publication and is excluded; on/off/restored HDR is stable. The final refinement
build has zero warnings/errors in 19.27 seconds. Fresh OpenGL PID 48412 and
Vulkan PID 17980 capture both poses at 1600x900 and restored 1920x1080. All eight
depth/HDR/final rows were viewed. HDR mean differences are 0.47888%/0.47840%
for the dark view and 0.09823%/0.03515% for the atrium. All pass the unchanged
1% gate. Published cascade occupancy differs by at most one texel. Vulkan
capture windows advance 49/163/39/60 completed frames with zero rejected or
failed deltas and no terminal state. Four existing global resource-contract
tests pass; no tests were added or changed. Temporary native inspection code
was removed, and all three probe shader hashes still match their originals.

Acceptance evidence: `reports/shadow-resize-{OpenGL,Vulkan}-sampler-final.json`,
`reports/shadow-resize-summary-sampler-final.json`, viewed contact sheets with
the same labels, `logs/sampler-final-build-start.log`, and
`reports/tests/sampler-existing.trx`. The unsuccessful runs above remain useful
causal evidence. This closes the scoped mono HDR gate; it does not certify final
display tonemapping, temporal sequences, hardware modes or performance.

Status: October 4, 2026. Upload scheduling, GPU timing readback, cascade target
publication and mono specialized shader reload corrections have live evidence.
The earlier shadow discrepancy was explained by mismatched requested shadow
resolution; matching it established HDR parity in two prior views. The newer
dark-view HDR discrepancy now passes the declared mono gate above. Measurement
admission and matched performance comparisons remain open. The historical
observations below describe the failures before those corrections.

Two defects originally produced the black or sky-only OpenGL scene:

1. **Admission linked the stage programs one at a time.** Fixed: every
   evaluation now polls every program, and failed or unstarted programs are
   named.
2. **Under the measurement harness the scene waits on texture uploads for
   minutes.** OpenGL Advanced reports the family as pending until every
   material texture's progressive upload finishes. Those uploads advance a
   fixed chunk per render tick, one at a time, behind the 4K environment map.
   The harness's profile capture turns on GPU pipeline profiling, which drops
   OpenGL from about 190 to 25-40 frames per second, so a load that takes about
   30 s in a normal session takes many minutes. This is consistent with the September
   "black OpenGL scene in every binary" result. It is not an admission or
   driver failure.

Owner item: [S16a](../../progress/rendering/vulkan-stall-remediation-results.md).

## Workload

The S15a fixture with `RenderLibrary: OpenGL` (Sponza, one directional light,
Advanced pipeline, CpuDirect), Release isolated MCP sessions, main checkout.
Harness runs use `Tools/Measure-GameLoopRenderPipeline.ps1` with
`-RenderBackend OpenGL -Strategies CpuDirect -CacheMode Warm`.

## Admission (fixed)

### Observations before the fix

| Session (start) | Program binary cache | Result |
| --- | --- | --- |
| 1 (06:45) | as left by September runs | Black scene. About 3 minutes after start, the Advanced profile was still `PendingResources`: "OpenGL Advanced stage programs are still compiling or linking". Stage diagnostics showed `VisibilityPreparation` `RejectedAdmission` |
| 2 (06:49, probe) | warm | One probe entry at the first render-thread admission evaluation: all 12 stage programs unlinked, no backend status. Final state not read |
| 3 (06:52, probe) | warm | First evaluation: only `EarlyVisibility` started link preparation. Later admitted; Sponza renders (frame p50 36 ms with the probe active) |
| 4 | warm | Admitted 10.6 s after the session reported ready; frame p50 5.4 ms |
| 5 | tonight's 25 new entries moved aside | Admitted after 10.5 s |
| 6 | entire cache moved aside (330 files) | Admitted after 10.5 s; 124 files regenerated; Sponza renders |

`AreAdvancedStageProgramsLinked` stopped at the first unlinked program, so only
that program's `Use()` (and therefore its asynchronous link) was driven. The 12
links were serialized across evaluations. A program whose build never started
or had failed produced the same "still compiling or linking" message.

### Change

`OpenGLRenderer.AreAdvancedStageProgramsLinked` polls all 12 programs on each
evaluation. The pending reason counts failed, not-started and still-building
programs and names the first failed or not-started program, with the backend
failure reason when one is recorded. The reason string is rebuilt only when the
set of unready programs changes, so polling does not allocate. The multisample
and single-pass stereo program sets now evaluate all three of their programs
instead of short-circuiting.

### Validation

| Session | Environment | Admission |
| --- | --- | --- |
| Fixed build | normal OpenGL session | `Admitted` at the first poll after the session reported ready |
| Fixed build | harness profiler environment | `Admitted` at the first poll |

Sponza renders once texture uploads finish (about 30 s after start in the
normal environment).

## The harness scene waits on texture uploads

### Reproduction

The current binary run through the harness showed only the sky at the 120 s
admission screenshot (14 draw calls, 727 triangles, 13 Hz on the overlay), and
binary B (`9fee4b983`) produced a black viewport. The admission screenshot check
accepts a sky-only frame, so a harness run can pass admission without drawing
Sponza.

An isolated session with the harness's environment variables reproduces it.
`get_advanced_profile_diagnostics` reports the family `Admitted`, but every
frame `VisibilityPreparation` is `BackendEnqueueRejected`: "The texture still
has a pending sampling-parameter transition." The later stages are rejected
because the family order is broken.

### Bisection

| Session environment | Texture streaming after 60 s | Preparation |
| --- | --- | --- |
| Normal | 0 pending transitions | Accepted at about 30 s |
| `XRE_FORCE_MESH_SUBMISSION_STRATEGY`, `XRE_ZERO_READBACK_MATERIAL_DRAW_PATH` only | 0 pending | Accepted at about 30 s |
| Profiler variables only (`XRE_PROFILER_ENABLED`, `XRE_PROFILE_*`) | 45 pending, 77 uploads, resident bytes frozen | Rejected for 14+ minutes |
| Profiler variables, then `Debug.EnableGpuRenderPipelineProfiling` set false at runtime | 0 pending within 15 s; frame rate rose from about 40 to about 187 per second | Accepted |
| Profiler variables with `ProceduralSky: true` | 0 pending transitions at 16 s | Accepted at about 80 s |

Profile capture (`XRE_PROFILE_CAPTURE=1`) enables the code profiler, render
statistics and GPU render-pipeline profiling (`Program.ApplyProfileCaptureProfilerPreferences`).
GPU pipeline profiling is the one that matters here.

### Mechanism

A temporary probe on the progressive-upload coroutine gates
(`XRTexture2D.StartProgressiveCoroutine`) showed, over about 40 s:

- one upload slot held by the environment map `satara_night_4k`, whose mip
  upload returned "incomplete" on each active tick (349 times);
- 19,164 waits on "a higher-priority upload exists" from the other 76 texture
  coroutines;
- 44,293 ticks with the renderer inactive (the coroutines are also ticked by the
  dispatch that runs before the window frame, where every upload yields);
- no byte-budget or millisecond-budget refusals.

The chain:

1. `TextureUploadScheduler.HasHigherPriorityUpload` orders uploads by priority
   class and then queue time. A coroutine checks it before taking a slot, so
   only one progressive upload runs at a time even though two slots exist.
2. `GLTexture2D.TryPushProgressiveMipChunk` uploads 16 KB per call, at least one
   row. The environment maps are 4096×2048 32-bit float EXRs, so their first
   three mips upload one row per active tick: more than 3,000 ticks for the
   chain. All five built-in environment maps are this size.
3. At about 190 frames per second that is roughly 30 s. With GPU pipeline
   profiling, OpenGL runs at 25-40 frames per second, and only part of those
   ticks are active, so the map takes many minutes. Every Sponza texture waits
   behind it.
4. OpenGL Advanced binds material textures through bindless handles, and a
   handle freezes the sampling range. `GLTexture2D.IsReadyForBindlessHandle`
   is false while a progressive upload is registered, so preparation rejects
   the whole family until the last material texture finishes.

The sparse residency path is not involved: a probe showed every sparse
transition that was scheduled completing at once (31 in the first 4 s), with no
deferred finalizations.

### Side effects seen in the profiler environment

- MCP calls that read Advanced diagnostics took about 18 s and stalled the
  render thread for that long. A harness MCP call during capture can therefore
  trip "no render-stats progress for 15s", which ended the current binary's
  motion capture.
- The per-frame stream reached 3.4 GB in two minutes for binary B.

## Comparison rerun

Binary B (`9fee4b983`, its own worktree) and the current tree, each through the
harness with `-RenderBackend OpenGL -Strategies CpuDirect -CacheMode Warm
-WarmupSec 240 -CaptureSec 60 -MotionCaptureSec 60 -SampleIntervalFrames 1
-NoStabilityGate -ProfileMode DevelopmentProfile` and the fixture with
`ProceduralSky: true`. Both drew Sponza at the admission screenshot; the walls
were very dark in both, so lighting matches between them.

| Binary | Harness result |
| --- | --- |
| B | Stationary capture aborted: "no render-stats progress for 15s during capture" right after the capture-start `get_render_profiler_stats` call |
| Current | Stationary and motion captures completed. Render p50 26.6 ms, p95 33.7 ms, p99 75.2 ms (1,371 samples); worst 6,451 ms. No GPU timing ("No GPU timing history has been captured"); retention evidence incomplete |

Same window from each per-frame stream (150-240 s, stationary camera, before
any capture call):

| Binary | Whole-frame p50 | p95 | p99 | Frames per second | Render gaps over 2 s |
| --- | --- | --- | --- | --- | --- |
| B | 43.6 ms | 73.9 ms | 81.5 ms | 14.4 | 7, from 7.4 to 22.1 s |
| Current | 27.0 ms | 33.3 ms | 81.7 ms | 26.0 | 23, from 2.2 to 11.4 s |

The current tree is faster than B under identical conditions, but neither run is
a valid OpenGL performance measurement. GPU pipeline profiling, which profile
capture forces on, costs about 20 ms per frame on OpenGL, produces recurring
multi-second render gaps, and still records no GPU timing. OpenGL timing on
this host stays unvalidated until that is fixed.

## Remaining work

### Bounded upload correction

Hypothesis: the one-chunk-per-callback limit, combined with charging the full mip
for each chunk, makes progressive OpenGL completion depend on render frequency
even when the measured upload budget has unused time. Owner: OpenGL's progressive
mip transfer helper, shared by local and runtime-managed uploads. Move OpenGL
byte/time admission into that owner and loop row chunks until the existing
global byte or measured-time budget declines work. The runtime coroutine retains
completion and duration reporting but must not double-charge OpenGL transfers.
Other backends retain their current indivisible upload accounting. Partial mips
must remain hidden, slot policy stays unchanged in this experiment, and no new
per-tick allocation is permitted.

Reject the change if the live environment-map fixture still transfers one row
per active tick despite available time, fails to drain, exceeds the budget beyond
one indivisible native upload, exposes incomplete mips, or breaks cancellation
and storage-generation checks. Budget: one narrow build and one isolated OpenGL
startup with upload diagnostics, followed by two viewed scene positions and
log inspection. Compare with the recorded 349 incomplete callbacks in 40 seconds
and the normal approximately 30-second admission baseline; GPU profiling and
slot fairness are separate experiments.

- Make progressive upload progress time-budgeted rather than one chunk per
  tick, so load time does not scale with frame rate.
- Let a lower-priority upload use a free slot instead of yielding to every
  higher-priority registration.
- Decide whether OpenGL Advanced should publish materials with pending textures
  (for example with a placeholder handle) instead of rejecting the family.
- Find why GPU pipeline profiling costs about 20 ms per frame on OpenGL,
  produces multi-second render gaps, and records no GPU timing.
- Make the harness admission check require accepted Advanced preparation, not
  only a non-empty screenshot.

## Evidence

### Resumed upload comparison

The OpenGL transfer owner now admits and charges actual row chunks, repeats
them until the existing shared byte/time budget declines work, and leaves a
partial mip hidden. Runtime-managed OpenGL uploads no longer charge the full
mip again outside that owner. Other backends keep their existing accounting.
The candidate introduces no allocation in the chunk loop.

Matched Release controls with the same nonprocedural environment-map fixture
drained their queues between 26.6 and 28.7 seconds after process start. The
verified candidate had all 76 tracked textures and zero queued uploads at
10.1 seconds. Sampling was every two seconds, so these are completion bounds,
not exact transfer durations. The control sampled 134,217,728 scheduled bytes
for a partial callback; a separate candidate diagnostic sampled 16,384 bytes.
The candidate built with zero warnings/errors in 45.29 seconds. A mislabeled
candidate run reused control DLLs because restored source timestamps were older
than their outputs; it is retained as a second control, excluded from candidate
evidence. Source timestamps and rebuilt DLLs were checked before the valid run.

Candidate geometry publication contains 393 draws/ranges, 25 materials, no
copy failures and no deferred preparation. Two viewed camera positions still
show dark geometry. Disabling directional shadows reveals detailed material
textures, and re-enabling them restores the issue. This separates unfinished
uploads from the remaining shading defect; it does not validate shadow output.
The actual directional raster atlas layer 0 was captured and viewed: its four
tiles are almost entirely clear depth (raw mean 0.9960971, range 0–1), while the
shading diagnostic had reported almost zero shadow visibility. Investigate the
sampled atlas binding and receiver coordinates before changing depth conventions.

An OpenGL RenderDoc hook loaded in the isolated editor, but target discovery
reported no active API and a bounded trigger produced no capture. Native MCP
texture readback supplied the atlas and visibility evidence instead. Inspecting
all reflected texture properties also timed out; invoking only the texture's
ID getter through the viewport's render-world path succeeded. Avoid repeating
the broad reflection request.

The temporary allocation-free counter probe recorded 675 callbacks, 12,538
chunks, 300,549,280 transferred bytes, and up to 256 chunks in one callback.
There were ten byte-budget yields, five time-budget yields, eight partial
progress yields, and 660 completed mips. Maximum scheduled frame bytes were
16,777,216; measured frame upload consumption peaked at 1.758 ms against the
2 ms budget, with zero measured overrun. Maximum whole callback duration was
2.279 ms, which includes loop/admission/probe overhead outside native-transfer
timing. All 76 tracked textures completed. The probe was removed after capture;
its temporary environment flag is not a supported setting. Existing cancellation
and storage-generation checks and binding restoration are unchanged; partial
mips stay hidden, including zero-progress seed yields.

Existing Release texture/temporal tests compiled and ran: 86 passed and 11
failed. All failures are source-content assertions against changed ownership,
names, or documentation, rather than runtime upload failures. Most map to moved
code (cache codec, imported upload partials, frame-op semantics, and presentation
lease owner). The obsolete transient-upload cache marker and removed cache-event
strings still need separate contract review; do not call the full suite passing.
This clears the historical absent GI-provider compile blocker.
The existing partial-mip hiding, stale-upload storage-generation cancellation,
allocated mip range, and visible-work priority contracts passed. The probe-free
candidate then rebuilt with zero warnings/errors in 24.70 seconds and completed
all 76 tracked textures again. The session launch wrapper reported a Windows
console-pipe error after the successful build; restarting those exact binaries
with `-NoBuild` succeeded. Reflection confirmed the temporary getter is absent.
Evidence for this resumed work is under
`Build/_AgentValidation/20261003-142654-vk-todo/`: `reports/upload-*-timeline*`,
`reports/upload-diagnostic-budget.json`, `reports/gl-shadow-atlas-capture.json`,
`reports/upload-budget-probe.json`, `reports/tests/upload-temporal-existing.trx`,
and `mcp-captures/upload-candidate-verified-*` / `mcp-captures/gl-shadow-atlas/`.

### Shadow sampling isolation

Owner: Advanced OpenGL shadow texture reference and projection. Hypothesis:
the dark image samples different storage or a collapsed/border UV despite the
actual atlas being mostly clear depth. First replace the directional shadow
result temporarily with a fetch at the known clear layer-0 tile interior
`(0.25, 0.25)`. A full-visibility result rejects a native handle/sampler failure;
zero implicates that binding. If full visibility, fetch at the original UV to
separate coordinate errors from comparison/bias inputs. Keep the atlas writer,
light, camera, and texture data unchanged. Budget: at most two live shader
diagnostic variants, each with settled HDR/diagnostic readback and a viewed
viewport. Restore the authored shader before implementing a correction. Do not
use this diagnostic as accepted shadow rendering or performance evidence.

Neither the fixed-UV variant nor a constant-0.5 control under the same
directional-array condition changed the settled HDR hash. Reload reported 89
invalidated shaders, but that alone does not prove the diagnostic branch ran.
The shader was restored byte-for-byte. Reject interpreting this as a failed
native sample: first establish the actual published texture dimension and that
the active compute program consumes the changed shader. Evidence:
`reports/gl-shadow-fixed-uv.json`, `reports/gl-shadow-constant-control.json`.

Source review explains the ineffective reload: OpenGL Advanced resolves and
lowers includes once in `CreateAdvancedComputeProgram`, stores the finished
text in a new `TextFile`, and retains the program. Shader reload clears resolver
caches and invalidates dependencies but does not regenerate that expanded
`Source.Text`. Directional atlas publication is type 0 / array dimension 5,
so the probe condition itself was appropriate. Restart the session with the
diagnostic present to execute a changed body. Track regeneration of specialized
Advanced sources as a separate reload correctness issue; invalidation counts
are not evidence that includes changed on the GPU.

Two fresh-session variants execute the diagnostic. A fixed tile-interior fetch
returns about 0.835 visibility (encoded diagnostic 54,528), with HDR mean
0.03402 and visible material detail. Returning fetched depth at the original
projected UV also gives a visible scene (HDR mean 0.03362; diagnostic mean
17,834.6 across RGB, maximum 58,112). Thus neither a universally zero native
sample nor universally collapsed-to-zero UV explains the original result.
The remaining split is receiver/projection/comparison against the actual
caster depth. The current viewed atlas is mostly clear (raw mean 0.98369),
but individual sampled pixels contain depth; its average cannot establish that
the sampled receiver should be lit. Restore the authored shader after these
variants. Evidence: `reports/gl-shadow-fixed-uv-fresh.json`,
`reports/gl-shadow-original-uv-fresh.json`, `reports/gl-shadow-atlas-current.json`,
and matching viewed captures.

### OpenGL directional cascade targets

Owner: GL mesh shadow-uniform publication. Generated instanced shadow vertices
select their cascade from `DirectionalCascadeTargetMask`, but GL publishes only
layer count and matrices. Its existing draw expansion renders all active
cascades, so the corresponding mask must contain every active cascade bit.
Hypothesis: omitted mask leaves all instances selecting target zero, explaining
the single populated atlas tile. Before editing, compare explicit sequential
mode against the current automatic instanced mode at the same settled camera.
After publishing the mask, require populated expected cascade tiles and compare
instanced output to the same sequential reference. Preserve draw count expansion,
caster materials, projection, and quality; do not add per-caster culling in this
correction. Budget: one narrow build and two camera/atlas comparisons, with
image and atlas differences classified explicitly. A remaining dark image does
not invalidate a proven target-selection correction but keeps shading acceptance
open. No new tests without clearance.

The retained correction publishes the full active cascade mask and republishes
scoped count/mask/matrices after the optional material callback. A fresh Release
session now renders casters into all four atlas tiles (previously only one),
confirming target selection. The viewed lit image remains near-black: HDR mean
0.000143 in automatic instanced mode versus 0.040741 in sequential mode. The
sequential atlas is mostly clear and therefore is not an accepted correctness
reference. The second view also differs from the earlier session, so it is not
a matched image comparison. Keep the target-selection correction, but leave
receiver-depth/comparison and overall OpenGL shading acceptance open. Evidence:
`reports/gl-cascade-mask.json`, `reports/gl-cascade-before.json`, and their viewed
atlas/viewport captures. One atlas read after a mode change returned OpenGL
`InvalidOperation`; the evidence preserves the error rather than treating an
empty result as a valid capture.

### Progressive slot ownership

Owner: `TextureUploadScheduler`. Hypothesis: a registered upload that already
owns one slot should not reserve the other slot by remaining in the waiting
priority comparison. Track the two owners explicitly, including work-item
identity, and compare priority only against waiting registrations (including
local OpenGL registrations). Admission remains nonpreemptive; release must match
the owner so stale cancellation cannot release a successor's slot. Preserve
shared transfer budgets and partial-mip behavior. Reject if active ownership
exceeds two, a waiting visible repair is bypassed by background work, completion
leaks a slot/registration, or normal completion regresses outside the measured
two-second sampling tolerance. Budget: one build, a controlled low-budget live
startup to observe both slots, a normal startup, and the existing scheduler/
texture contracts. No new tests or unrelated shader changes in this slice.

The low-budget live run observed two active owners in 76 of 88 samples, never
more than two. Restoring the normal budget drained all registrations and owners
by 12.312 seconds of observation. A final normal startup with the allocation-free
registration map reached all 76 tracked textures and zero queued work at the
8.906-second sample, within the previous candidate's completion bound. The
fixed two-entry owner array clears strong references on exact-owner release;
the registration map and slot state share one lock, so priority enumeration uses
a nonallocating dictionary enumerator. Waiting visible work keeps priority;
local OpenGL registrations now participate instead of being ignored.

Existing focused contracts ran 32 tests: 31 passed, including both scheduler
ordering/coalescing checks and the texture lifetime/range checks. The sole
failure is the previously classified skinned-bounds source assertion expecting
`TryGetMappedAddress` where the owner now uses `TryReadMapped`; it is unrelated
to upload ownership. No tests were edited. Evidence: `reports/upload-slot-probe.*`,
`reports/upload-slot-final-timeline.*`, `reports/tests/upload-slot-existing.trx`.

Evidence root (ignored, disposable): `Build/_AgentValidation/20261003-015628-vk-100hz/`
(`mcp-captures/s16a/`, `mcp-captures/s16a-fix*/`, `reports/s16a-*.json`,
`reports/s16a-sparse-probe.log`, `reports/s16a-harness/`). The temporary probes
were removed from the source.

### OpenGL timestamp query backlog

Owner: render-pipeline GPU profiler and OpenGL query polling. Hypothesis:
unavailable timestamps accumulate for up to 120 frames and every pending scope
is polled each frame, creating cumulative overhead that the single-call write
stall guard misses. Before edits, toggle profiling in the same warmed scene and
sample issued timestamps, readback bytes, timing readiness, and pending query
counts. Reject this explanation if readbacks progress with small pending lists
or polling remains cheap; then separate issuance and publication costs. Budget:
one bounded live off/on/off run, followed only by a targeted timing probe if
existing counters cannot identify the expensive owner. Preserve real GPU
measurements and expose unavailable results; do not substitute CPU timing.

The live control issues 324 timestamps per sampled enabled frame and reads zero
bytes; timing readiness never becomes true. Source attribution identifies a
more specific cause than expensive native polling: `XRWindow` runs statistics
and profiler query resolution before setting `AbstractRenderer.Current`; the
previous frame clears that value. Reads therefore return unsupported before
calling OpenGL. Retirements still accumulate. Correct the window's current/
active renderer boundary before frame-start statistics, preserving the existing
snapshot order and final cleanup. Require nonzero readback bytes and ready GPU
timings in the same off/on/off run, then quantify remaining overhead separately.
Control evidence: `reports/gl-profiler-query-samples.json`. The stack captured
after the toggle completed is not evidence of a profiling stall. No diagnostic
probe is needed to establish this ordering error.

The retained renderer-boundary correction passes the live timing-readback check:
324 timestamps are issued and approximately 2,608 bytes read per enabled frame;
GPU timing becomes ready by the next one-second sample. Control readbacks were
zero throughout. Remaining timestamp overhead is measured separately; this
establishes timing recovery, not a whole-frame performance acceptance. Evidence:
`reports/gl-profiler-query-control.json` and `reports/gl-profiler-query-current.json`.

Thirty-second settled off/on/off windows replace the shorter overlapping-history
sample. Their last-512-frame medians are 36.894/37.052/37.086 ms; p99 values are
47.359/39.593/47.223 ms and maxima 48.249/40.535/47.579 ms. Enabled Advanced
profile diagnostics returns in 0.734 seconds (off: 0.907; off-after: 0.015).
The original approximately 20 ms observer penalty and multi-second gap are not
reproduced in these windows. This is same-binary observer admission, not a
matched Vulkan/OpenGL throughput comparison; SteamVR remains user-owned and
running. Evidence: `reports/gl-profiler-current-overhead-settled.json`.

### Directional receiver-depth diagnostic

Owner: Advanced directional shadow comparison. The mask correction establishes
four target writes but does not establish shading parity. Capture the selected
cascade's biased receiver depth, center stored depth, and signed difference
directly into floating HDR output, bypassing lighting multiplication and RGB
clamping. Alpha identifies cascade plus one. Hypothesis discrimination: a small
coherent positive difference suggests self-shadow/bias; a large difference needs
matrix, receiver position, or physical occluder attribution. Budget: one fresh
single-sample session and two positions, EXR analysis and viewed PNG previews.
Restore authored shaders exactly afterward. Do not retain this diagnostic or
accept brightness alone as correctness.

### Stop-request handoff

Stopped at the user's request after the timing-readback validation. The final
receiver/stored/difference shader probe produced viewed normalized PNGs in two
positions, but both EXR reads returned OpenGL `InvalidOperation`; signed per-
cascade analysis was not completed. Do not infer the shadow root cause from
those normalized images. The three shader files were restored byte-for-byte,
and the named validation session was stopped. Latest retained Release build:
zero warnings, zero errors. The temporary probes are absent from source.

The next harness correction can use existing `get_render_state` fields:
`advancedProfile.profile.executionAdmitted`, `reservationCurrent`, nonzero
preparation/package draw counts, and current-generation accepted visibility/
native-opaque stage observations. Inspect exact frame alignment before making
that predicate strict. This work was only audited, not implemented or validated.
Use both rejected/sky-only and admitted live fixtures; accepted enqueue is not
GPU completion certification. Remaining work is summarized in the parent TODO.

### Specialized shader reload resumption, October 4

Owner: shader source resolution and OpenGL Advanced specialization. The earlier
include-edit control invalidated programs without changing their expanded
source or image hash. Preserve the authored template and its include dependency
graph, then apply the existing GL preamble/lowering to each newly resolved
revision. Reject any correction that loses dependencies, specialization defines,
source revision invalidation, or failed-compile diagnostics. Do not add a
per-frame transform or retain renderer instances through callbacks.

Budget: one focused Release build, one named live session with an include edit
that produces an unmistakable output, then restore/reload and compare with the
baseline at two camera positions. Existing shader tests may run afterward;
new test work remains uncleared. Keep the runtime probe temporary, preserve all
prior retained work, and record variant coverage limits explicitly.

The first candidate builds with zero warnings/errors and resolves the edited
include into the live shader (revision 4), but the viewed output is unchanged.
NativeOpaque metadata reports a shared-context binary handoff `InvalidOperation`
and a subsequent duplicate-hash wait; that status alone cannot establish a
permanent ownership leak. The handoff sampled the context's error flag only
after `ProgramBinary`, so earlier errors could be misattributed. Extend the
bounded validation by one build/run: report preexisting errors before the call,
check its error independently, and always inspect link status/info log. Reject
this explanation if the pre-call context is clean and the call itself fails.
The include probe was restored before restarting. Evidence:
`reports/gl-reload-source-inspect.json`, `reports/gl-reload-include-half.json`.

The handoff-attribution candidate builds cleanly but does not unblock repeated
reload: metadata advances from shared source queueing to duplicate-hash wait
without recording a new failed handoff. Source audit identifies a deterministic
leak: a successful shared compile whose source revision is obsolete returns
from `AdoptLinkedBuildProgram` before removing its completed in-flight hash.
Restarting with identical source then waits on that abandoned claim. Release
completed source-compilation claims before revision adoption/restart, audit the
other source-success paths, and keep cache-consumer paths from releasing a
claim they did not acquire. Validate repeated invalidation during compilation,
include edit, restoration, and ready replacement metadata. This is a specific
new cause, so a further focused build/run is warranted; no ownership-registry
redesign is authorized by this evidence.

The exact completed-program claim correction passes the shared-context live
check. A temporary include return of 0.375 followed by a second reload while
queued reaches Ready with a new NativeOpaque fingerprint. Viewed lit/atrium
captures change and HDR means rise to 0.015277/0.019060. Restoring the authored
include restores the original fingerprint and near-dark output (0.000162/
0.007069); baseline means were approximately 0.000129/0.007042. This establishes
reload propagation, not shadow correctness or pixel-exact shadow stability.
The probe is restored byte-for-byte. Build: zero warnings/errors, 22.24 seconds.
Evidence: `reports/gl-reload-claim-*.json` and matching captures. The first failed
texture-readback attempts remain in the reports, followed by successful reads.

Review found additional completed-source cleanup skipped by obsolete adoption:
driver-parallel retained its old Linking phase/handle, and synchronous source
skipped attached-shader cleanup. Address these before accepting the generic
reload correction. The current live fixture exercises the shared-context path;
driver-parallel and MSAA/stereo remain separate coverage limits.

Final reload build succeeds with zero warnings/errors (22.45 seconds). The fresh
0.625 include probe reaches Ready through shared-context source compilation at
the 15-second observation; a subsequent reload uses the newly cached binary.
Two viewed positions have HDR means 0.025464/0.031693. Restoring the original
include reaches Ready with the baseline fingerprint within the five-second
observation and means 0.000129/0.007041, consistent with the original output.
The authored shader is byte-identical to the backup and the named session is
stopped. Evidence: `reports/gl-reload-final-*.json`, `gl-reload-wait-*.json`,
matching viewed captures and `logs/gl-reload-final-start.log`. Existing source
resolution/dependency/linking/lifecycle tests are running; no tests were added
or changed. Live MSAA/stereo and driver-parallel-specific overlap are not covered
by this mono shared-context fixture and remain in integrated acceptance.

Existing selected tests finish at 65/66 passed. The sole failure,
`GLMeshRenderer_UsesCombinedProgramsWithoutDuplicatingPendingUberCompiles`,
expects an obsolete expression in `GLMeshRenderer.Shaders.cs`. That file is
unchanged from HEAD and HEAD also lacks the expected expression. This is a
preexisting source-contract mismatch, not a green test gate; test edits remain
uncleared. Source dependency/cache and linking policy tests pass. The mono
reload item is validated with the explicit integrated-coverage limits above.

### Receiver-depth correlation resumption, October 4

Owner: Advanced directional manual depth comparison. Reload is now validated, so
resume the existing receiver/stored/signed-difference probe without recompiling
managed code. Keep alpha as cascade identity and inspect raw float samples per
cascade; normalized previews alone cannot establish sign or bias. Retry the
known first-read GL error while preserving each failed attempt in evidence.
Budget: one named mono session, two positions, and authored-source restoration.
Do not change comparison bias without a signed-depth finding and reference.

The sampler-comparison hypothesis has no static support: the directional depth
array uses nearest filtering with comparison disabled, and Advanced samples a
texture/sampler-pair handle through nonshadow sampler2DArray before its manual
comparison. A runtime mismatch could reopen it; querying only texture state
would not establish the sampler object's effective state.

The recovered EXR samples reject a tiny uniform bias explanation. Lit view:
99.48% of sampled pixels have positive receiver-minus-stored depth, median
0.0570, with 94.09% above 0.01. Atrium first cascade: 79.36% positive, median
0.03714; second cascade: all positive, median 0.02667. Raw receiver/stored values
are finite and plausibly distributed; this does not distinguish an incorrect
projection from real occlusion. Evidence: `reports/gl-shadow-depth-analysis.json`
and alpha-preserving `gl-shadow-depth-resumed.json`, plus viewed previews.
Extend the diagnostic to a matched Vulkan reference (settings differ only in
backend), same two cameras and temporary probe, before adjusting comparison.

Matched Vulkan depth samples are similar: lit median receiver-minus-stored
0.05295, atrium first cascade 0.03485, second 0.02383. Stored-depth distributions
closely match OpenGL, so a gross OpenGL atlas-addressing error is not supported.
Compare restored beauty output next; remaining receiver differences include
backend bias/state and do not alone establish a bug.

Restoring the three probe files exposed a separate Vulkan root-source reload
limitation: NativeOpaque retained the old top-level `imageStore` probe while
its includes refreshed, producing undeclared-probe-identifier errors. Authored
files are restored exactly. Restart the owned session for an uncontaminated
beauty reference; record root-file reload as an integrated follow-up, not a
shadow-comparison fix. Evidence: `reports/vk-shadow-restoration-state.json`.

The clean Vulkan beauty reference is also predominantly dark with this
nonprocedural fixture. HDR means: lit 0.000603, atrium 0.012951, versus OpenGL
0.000129/0.007041. Both render material detail in the same exposed regions;
OpenGL remains darker and receiver bias differs. Thus do not describe the whole
black region as an OpenGL-only failure or increase bias to brighten physically
occluded areas. Source/record attribution of the smaller discrepancy remains.
Reference screenshots were viewed, authored shaders are restored, and admission
is true. Evidence: `reports/gl-reload-vk-reference-clean.json`.

The common bias publication has no backend-specific resolution multiplier.
Current Vulkan cascade 0 settings are floor 0.00021359428, slope 2 authored
texels, normal offset/texel size 0.052696284; these are not proof of shader-visible
rendered records. One final paired probe will separate actual record parameters
from slope/normal inputs: even columns store depthBiasAndFilter; odd columns
store projected normal-offset depth contribution, calculated comparison bias,
abs(planeNormal dot L), and cascade+1. Compare both backends at the same cameras,
then restore. Reject a parameter-publication hypothesis if actual values match;
reject a normal/slope hypothesis if those inputs match. No bias tuning yet.

The paired GPU probe identifies the smaller backend discrepancy: unit-world
bootstrap chooses requested directional resolution 1024 on Vulkan and 4096 on
OpenGL (`UnitTestingWorld.Lighting.cs`, `AddDirLight`). Actual shader floor,
slope coefficient and normal offset are fourfold different; plane-angle
quantiles match. For example atrium first-cascade GPU floor/slope/offset are
approximately 0.00005335/0.00010675/0.01317 on GL and
0.00021350/0.00042701/0.05267 on Vulkan. This is a fixture mismatch, not evidence
for changing shared shader comparison. Evidence: `shadow-bias-comparison.json`
and `cascade-settings-{gl,vk}.json` (matrix serialization is incomplete; bias
settings are intact). All three probe shaders are restored exactly.

Match OpenGL's authored resolution to the Vulkan fixture's 1024 using the live
light API, then compare the same two beauty views and published bias values.
This is a validation-only mutation. Benchmarking must specify and report shadow
resolution explicitly rather than assuming backend-only settings differences
produce identical content.

Matching GL's requested light shadow resolution to 1024 establishes shadow
parity for the fixture: all four live cascade bias settings exactly match
Vulkan. Lit HDR means are GL 0.00059927 versus VK 0.00060258 (0.55%); atrium
GL 0.01297738 versus VK 0.01295093 (0.20%). Both maximum HDR values also match
at 0.13476562/0.23693848. Viewed captures show the same illuminated regions;
display tonemapping/UI presentation is not certified by this HDR comparison.
No shader bias correction is retained. The dark-shadow attribution item is
validated: bulk darkness is shared occlusion in this fixture and the smaller
backend difference followed mismatched authored resolution. Reopen on a
matched-resolution raw-depth/shadow mismatch. Both runtime
`BootstrapLightingBuilder.AddDirLight` and the editor mirror contain the
1024/4096 branch. Harness work must explicitly match and report resolution.
Evidence: `reports/gl-reload-gl-matched-1024.json`,
`reports/gl-reload-vk-reference-clean.json`, and
`reports/cascade-settings-gl-matched-1024.json`.

### Measurement admission resumption, October 4

Owner: `Tools/Measure-GameLoopRenderPipeline.ps1`. Hypothesis: publication and
image color alone admit a sky-only workload. A live far-away camera confirms
this: canonical and preparation draw counts remain 393, with accepted native
stages, while collected mesh commands contain only the background triangle.
These counts describe resident candidates, not visible geometry. Evidence:
`reports/harness-empty-settled.json`; healthy samples are in
`reports/harness-admission-samples.json`.

Acceptance: require actual Advanced admission, a current output reservation,
fresh accepted visibility/preparation/shading observations, and nonzero collected
opaque/masked mesh commands. Preserve stable publication/generation checks.
Snapshots are asynchronous: healthy stage observations can be one frame behind
the render counter, while a prepared canonical package can be ahead. Do not
mistake an enqueue receipt for GPU completion. Reject the hypothesis/fix if a
settled sky-only camera passes or a healthy stationary fixture cannot establish
the five-second window. Exercise at least three live samples in each state and
restore the camera; keep diagnostics outside measured frame windows. No tests
are added or modified. A subsequent fixture-setting child will explicitly set,
verify and report the requested directional shadow resolution before comparing
backends; default editor launch behavior remains unchanged.

The harness now requires per-stage frame progression for the current output
identity and enabled collected meshes from opaque deferred/forward or masked
passes (1/3/4). It accepts the asynchronously sampled package's Prepared or
Published state only with those live receipts; it still rejects Empty and
Cancelled. No equality is assumed between separately sampled frame IDs.

OpenGL live camera sequence: all three initial sky-only samples reject, the
atrium reports 265 collected mesh commands and reaches readiness at 5.7 seconds
of stable publication, then all three return-to-sky samples reject. Captures of
both views were viewed. Query latency was 47–845 ms and was outside measurement
windows. Evidence: `reports/harness-live-gl-candidate.json`.

An additional suppression probe was inconclusive: the editor did not retain
the assigned viewport suppression flag, and stage frames continued advancing.
Do not count it as a frozen-receipt rejection check. The flag is restored false;
`reports/harness-live-suppressed.json` retains the attempt. The first Vulkan
sequence correctly rejected every sky-only sample but its interior publication
was still changing during startup; extend the same live sequence after settling
rather than weakening the stable-publication requirement.

The extended Vulkan sequence passes: all six sky-only samples reject; the
40-sample interior window first admits after 5.6 stable seconds and has 17 ready
observations. Transient empty-package observations still reset the interval,
as intended. The restored interior PNG was viewed and contains scene geometry.
Evidence: `reports/harness-live-vk-long-window.json`. This validates the
admission predicate on both backends, not full matched performance acceptance.
PowerShell parsing and whitespace validation pass; no engine build or test
changes were needed for this script-only correction.

### Explicit shadow fixture settings

Owner: the same measurement script, separate from readiness. Hypothesis:
setting and reading back a caller-named directional light's requested resolution
before warmup removes the 1024/4096 bootstrap mismatch without changing normal
editor defaults. Require both an exact node name and positive resolution, fail
on missing/ambiguous light or mismatched readback, and include requested and
observed values in each summary. The rejecting check is a live resolution
round trip on both backends, followed by normal admission and viewed output.
Use 1024 for matched comparisons; retain the existing atlas allocation policy.
Budget: one configuration operation before warmup, no additional measured-frame
polling, no renderer hot-path changes, and no tests added or modified.

Implemented opt-in `DirectionalShadowLightNode` and
`DirectionalShadowResolution`, using the existing mutate/read component tools
without widening MCP permissions. Both backends pass live round trips:
Vulkan 1024→2048→1024, OpenGL 4096→2048→1024, with exact requested-dimension
readback. Rendering re-admits after 5.4/6.6 stable seconds respectively. Missing
light names time out explicitly; missing parameter partners and disabled MCP
are rejected before launch. Syntax parsing and whitespace validation pass.
Evidence: `reports/shadow-setting-{vk,gl}.json` and two-view captures in
`reports/gl-reload-harness-shadow-{vk,gl}.json`, all viewed.

This validates configuration/readback and retained geometry, not a new visual
parity claim: after this resize round trip, the lit HDR means differ
(VK 0.00066511 versus GL 0.00059927), whereas atrium means remain close
(0.01295103 versus 0.01298385). Keep resize/settling parity in integrated
acceptance; correlate actual cascade state and settled repeat captures before
attributing that difference. The earlier fresh matched-resolution comparison
remains its own scoped evidence. GL's first atrium HDR readback again reported
InvalidOperation; the recorded retry succeeded. The harness changes apply before
warmup, so bootstrap/cold-start equivalence still requires matching initial
fixture settings.

### Completed-frame admission

Owner: `New-ProfilePublicationReadinessState` and
`Test-ProfilePublicationReadiness` in the measurement harness. The reload freeze
recorded in the [source reload investigation](2026-10-04-shader-root-reload.md)
falsified accepted enqueue progress as evidence of continued Vulkan submission.
Its completed count remained 1060 while rejected and enqueue counts advanced.

Require the selected viewport's actual Vulkan backend to publish a completed
counter that increases across each stable-window poll, with no retained
PresentNow terminal fault or latest Rejected/Failed frame. Missing telemetry or
query failure must reject admission. Preserve current stage/output/visible-mesh
checks; query profiler diagnostics only for Vulkan and outside timed windows.
OpenGL behavior is unchanged. The explicit `NoStabilityGate` diagnostic bypass
continues to bypass readiness and cannot establish performance acceptance.

Acceptance: PowerShell parsing; live healthy Vulkan and OpenGL camera sequences
each reject sky-only views and admit the stable interior. Record actual Vulkan
completed counts and readiness query overhead. Compare the frozen report's
fields with the rejection conditions without claiming a new live terminal
reproduction or fabricating counters. No engine build or new/modified tests.

Implemented and live-validated. Vulkan completed counts advance 21946→24496
across eligible observations; the interior first admits after 5.4 stable seconds
and 13/25 interior samples are ready. OpenGL first admits after 6.2 stable seconds
with 12/20 interior samples ready and no added profiler query. All six sky-only
samples reject on each backend. Readiness query latency is 47–574 ms on Vulkan
and 50–2578 ms on GL, outside measured windows. Reports:
`reports/harness-live-vk-completed-guard.json` and
`reports/harness-live-gl-completed-guard.json` under the existing task run.

The recorded frozen state has both a retained `RendererTerminal` PresentNow
fault and a latest Rejected frame, which directly match the new rejection
conditions. It was not recreated after fixing reload admission. The stable-clock
reset on a flat counter has code review but no separate live fault injection.
PowerShell parsing and whitespace checks pass; no tests changed. A concurrent
enumeration error in `get_render_state` was also observed during setup; the
harness fails closed and resets its window on that error. Fixing snapshot
ownership for that diagnostic remains separate from completion admission.

### Command-summary snapshot ownership

The next bounded investigation owns `BuildRenderCommandPassSummary` in
`EditorMcpActions.Introspection.cs` and the rendering-command publication/read
contract. A live Vulkan `get_render_state` call throws
`InvalidOperationException: Collection was modified; enumeration operation may
not execute` while enumerating a pass's command list. The tool runs with Main
affinity while rendering/publication can have another owner. Do not replace
enumeration with an unsynchronized array copy or suppress the exception and
report partial command counts.

Hypothesis: inspecting the pass list at its existing publication/owner boundary
can provide a coherent snapshot without adding per-frame copies or locks to
unrelated rendering work. First identify the actual writers and supported read
boundary; reject a proposed thread-affinity-only fix if that boundary still
permits list clearing or buffer swaps. Acceptance: an isolated zero-warning
build, repeated live diagnostic polls across camera changes on both backends,
consistent nonzero interior and zero sky mesh counts, and the existing readiness
window passing. Record exceptions and query latency. No tests added or modified.

Ownership review confirms the borrowed-list lifetime fault:
`TryGetRenderingPassCommands` releases its rendering-buffer read scope before
returning commands. `SwapBuffers` later acquires the write scope, swaps and clears
the old lists. MCP Main affinity dispatches to the app thread and does not prevent
that swap. Use an explicit collection-owned diagnostic snapshot with scalar,
enum and string rows under the existing read scope; release it before editor
sorting/JSON. Do not acquire the producer `_lock` inside the read scope. This
adds one owned row array per diagnostic request and no per-frame copies. Other
render-state fields remain independently sampled observations.

The live control and candidate procedure polls 40 times in each of interior,
sky and restored-interior views (120 requests/backend), with 50 ms between
requests and three seconds after each camera change. These are diagnostic
stress windows, not frame-performance measurements.

The collection-owned snapshot is implemented and passed read-only concurrency
review. The isolated Release build has zero warnings/errors (43.91 seconds).
The follow-up Vulkan control happened to complete 120/120 requests without the
intermittent exception; the earlier directly observed exception remains the
failure evidence. Both candidate backends complete 120/120 requests without
errors. Settled restored-interior samples consistently report 265 enabled
meshes and all sky samples report zero. A few early interior samples can still
observe an unpublished package and report zero, which readiness rejects.

Diagnostic-call maximum latency was 797 ms on Vulkan and 4672 ms on OpenGL;
there is no latency-improvement or failure-rate claim from these stress runs.
The ownership fix removes the identified borrowed-list race. Evidence:
`reports/command-summary-vk-control.json`,
`reports/command-summary-vk-candidate.json`, and
`reports/command-summary-gl-candidate.json`. All remain outside performance
capture windows.

Both candidate readiness repeats pass after 5.7 seconds (Vulkan) and 6.9 seconds
(OpenGL), with 15/20 admitted interior samples and all six sky-only samples
rejected on each backend. Existing `RenderCommandCollectionOrderingTests` pass
13/13 without test changes (`reports/tests/command-summary-existing.trx`).
The named session is stopped after validation.

### Reconstructing the matched OpenGL control

The earlier baseline B worktree and executable are no longer present. Commit
`9fee4b983` remains available, and its relevant submodule gitlinks match the
current checkout; no dependency or submodule upgrade is required. Reconstruct
an isolated detached checkout under the existing task run's `temp-build`, using
the established local native-bridge/source supplies and separate build outputs.
Record the exact binary/source identity and all supply/setup changes. Do not
modify baseline rendering behavior to make its result pass.

The baseline's OpenGL render-state schema supports the current admission
predicate. First require a visible Sponza interior, current stage/output
admission and stable publication with matched requested shadow dimensions. If
it reproduces black output or observer stalls, record that failed entry gate
instead of claiming a matched throughput result. The baseline lacks the newer
Vulkan completed-frame counter, so it is not yet an admissible Vulkan control
for the new guard. Do not bypass the guard for a performance claim.

The detached control reconstruction succeeds at
`9fee4b983efda6f3f79ac107eba2137a5ab8c5fa`, with no tracked source changes and a
zero-warning/error Release build (1:44). OpenVR.NET and OscCore sources come
from archives of the exact matching gitlink revisions. Existing native bridge
and Steam Audio supplies are copied with hashes recorded, without dependency
changes. Full provenance is in `reports/baseline-b-build-provenance.json` under
the existing task run. The executable SHA-256 is
`7AC95B838355D0C3AA0E8AC7D316EAB10C5CDC088D1BAECD317AB2439779FC2B`.

Live admission uses the baseline checkout's own named session manager
(`gl-baseline-b`), which performs a separate isolated build because it does not
accept external artifact roots. Both versions use the same main-checkout asset
roots and explicit fixture settings. No rendering behavior is changed in the
baseline. The build alone does not certify it as a performance control.

The named-session build also passes (zero warnings/errors, 1:13.96). Its nested
output paths expose two setup failures before a usable scene exists: ImageMagick
rejects the generated font atlas path, and Windows reports filename-too-long
when loading `meshoptimizer.dll`, leaving model import unfinished. A shorter
isolated cache/metadata root and session-local native search path fix those
setup failures without changing code. The native DLL is byte-identical between
the build outputs (SHA-256
`2DE96DBAA821EDE81DF51E37E7AB42D482F7160445BD5FC34BA18241807921E1`).

After import, all 265 interior commands and 76 textures are present. The first
40-sample interior admission window fails while texture transitions remain.
After transitions drain to zero, the next 20-sample window still fails:
VisibilityPreparation rejects a canonical `sponza_thorn_diff` texture source
that changed after publication, and the dependent stages reject their sealed
family/order. Two viewed viewport captures retain the same atrium geometry
after a cut to the original comparison camera. Neither scene output nor frame
number advancement establishes fresh rendering in this run. No performance
comparison is claimed; a cache-warm restart is the next bounded check.

Evidence under the task run: `reports/harness-live-gl-baseline-b-short-native.json`,
`reports/harness-live-gl-baseline-b-settled.json`,
`reports/baseline-b-settled-stages.txt`, `reports/baseline-b-textures-full.json`,
and `mcp-captures/baseline-b-short-native/`. The initial empty-scene attempt is
preserved separately and attributed to native loading, not renderer throughput.

The warm restart also fails all 40 interior samples. Across the cold 40,
settled 20 and warm 40 samples, none passes. Maximum diagnostic latency is
8.76, 15.86 and 23.42 seconds respectively, outside timed windows. Warm stderr
reports `GL_INVALID_OPERATION: Texture is immutable` from
`GLTexture2D.SetSparseMipSamplingRange` through the sparse-residency transition
callback. This is a failed baseline entry gate, not a throughput result.
Baseline session is stopped before starting the candidate.

The current build, using the same scene, requested 1024 shadow resolution and
DevelopmentProfile/CpuDirect observer settings, first admits after 6.7 stable
seconds. It uses a separate initially empty short cache root, preserving both
runs' isolation. A standalone candidate stationary/motion capture may establish
its present behavior but cannot close the matched baseline comparison.
The full candidate repeat admits 26/30 interior observations and rejects all
six empty-view observations. Saved viewport captures also reveal different
retained ImGui panel layouts between the sessions; normalize those before any
future throughput comparison, while preserving the requested editor features.

### Sparse transitions after bindless publication

The current matching-profile run also logs `Texture is immutable` from
`GLTexture2D.SetSparseMipSamplingRange`; passing stage admission does not erase
that correctness failure. Viewport images change after a camera cut but are
strongly overexposed, and HDR readback reports `InvalidOperation`. No standalone
performance capture is accepted before isolating this issue. The admitted
interior result establishes progress only, not final image correctness.

The next bounded item owns OpenGL sparse transition entry/finalization and
canonical texture publication. Hypothesis: sparse paths bypass the existing
bindless parameter/lease guard and publish changed mip/sampler content without
the corresponding canonical revision. Reject a fix that destroys storage while
an Advanced table still holds a lease, mutates frozen sampler parameters,
silently substitutes dense/CPU content, blocks the render thread, or leaves
admission indefinitely pending.

Acceptance: zero-warning isolated build; repeat the same cold and cache-warm
Sponza/Advanced/CpuDirect/DevelopmentProfile fixture with 1024 shadows; texture
transitions drain, five-second stage/geometry readiness passes, and no immutable
texture mutation errors recur in those windows. Capture and view two camera
positions and correlate canonical source/metadata revisions with transition
completion. Record any remaining HDR-readback or display defect separately;
do not infer a common cause solely from an OpenGL error flag. No tests added
or modified without clearance. Save control stderr and diagnostics before
rebuilding. Performance comparison remains blocked independently by the pinned
control's failed admission.

The candidate guards sparse allocation/upload before sampler parameters can be
frozen again. It retains decoded requests while old bindless leases drain,
links first-use wrappers before admission is gated, and keeps asynchronous
native ownership through fence retirement. Canceled results carry their exact
originating wrapper's discard callback. Retries retain the frame scheduling cap
and charge upload bytes only after admission. Completed sparse metadata and
content revisions publish together. Static review found no remaining ownership
blocker; cancellation and device-failure fence branches have not been forced in
live validation.

The isolated Release build passes with zero warnings/errors in 44.65 seconds.
Cold and warm repeats use the same Sponza/Advanced/CpuDirect/DevelopmentProfile
fixture and read back the requested 1024 shadow dimensions. Both stderr files
are empty, compared with seven immutable-parameter errors in the saved control.
The cold observer records 92 samples over interior, sky, restored interior and
a second interior pose. Its main thorn texture demotes from 512 to 64 and
promotes back to 512; the chain texture goes 256 to 64 to 256, then 512 in the
second pose. Pending work drains after each change. No dense fallback was added
for bindless contention.

The warm observer also records the actual thorn source object: content
generation/metadata epoch advance from 3/6 to 4/8 on the sky transition and 5/10
on restoration. Sparse residency changes from disabled at initial observation
to enabled at base mip 3, then stays enabled at base mip 0. This distinguishes
the observed sparse transitions from the backend label alone. Scalar getters
through `Model.Meshes[0].LODs.Min.Material.Textures[0]` provide that identity;
enumerating the complete LOD reflection response timed out and is not used as
evidence of a rendering stall.

Five-second readiness admits 17/20 cold interior samples (first at 5.7 seconds)
and 18/20 warm samples (first at 6.9 seconds); all six sky samples in each run
reject as intended. Viewed captures from both camera positions update, but
remain strongly overexposed at final presentation. Direct `HDRSceneTex` readback
now succeeds in both runs. The cold capture is finite (maximum RGB 1.6494,
mean 0.14755) and its ACES export shows textured scene detail. This does not
establish the final display's correctness or close the matched performance gate.

Evidence: `reports/sparse-transitions-{cold,warm}.json`,
`reports/harness-live-gl-sparse-{cold,warm}.json`,
`reports/sparse-{cold,warm}-hdr.json`, `mcp-captures/sparse-{cold,warm}/`,
`logs/gl-sparse-build.log`, and `logs/gl-sparse-{cold,warm}.stderr.log` under the
task run. Both editors are stopped before running existing regression tests.

Existing `ImportedTextureStreamingContractTests` and
`ImportedTextureStreamingPhaseTests` pass 52/61 (`gl-sparse-existing.trx`).
Eight first-failing expected source strings are also absent in HEAD: cached
asset usability/cooking helpers, three Vulkan image/sampler/layout strings,
the mesh renderer's texture-upload operation string, and two command-buffer
upload-cache strings. The ninth expects `ConcurrentDictionary` in
`TextureUploadScheduler`, replaced by the earlier slot-ownership change's
locked dictionary. None points to this sparse diff. This is static comparison
with HEAD, not a fresh HEAD test run. No tests are added or modified. Scoped
sparse-transition admission is validated; forced cancellation/device-failure,
full display correctness and matched performance are not certified.

### Final display brightness

The next item isolates the brightness change after the finite HDR scene.
Hypothesis: the exposure/bloom/postprocess chain introduces it before final
presentation. Capture HDR, exposure, bloom and postprocess outputs at one
post-render boundary using unchanged camera settings, then identify the first
divergent target. A temporary single-parameter exposure toggle may distinguish
metering from later tonemapping, but must be restored and cannot constitute the
fix. Reject the hypothesis if the upstream HDR or wrong camera/resource identity
explains the image instead. Keep bloom, automatic exposure and the requested
pipeline available; do not close this item by disabling features or masking
invalid values. Validate any correction with viewed captures from two positions,
cold/warm readiness and unchanged feature settings before performance work.

Same-frame captures locate the change at exposure/postprocessing: the HDR mean
is 0.14763, while `AutoExposureTex` approaches the configured maximum of 100.
The first postprocess output mean is 0.67358; final and FXAA outputs preserve
the clipped image. A temporary `AutoExposure=false` with authored manual
exposure 1 lowers the postprocess mean to 0.24866 and restores visible detail.
Restoring automatic exposure returns the clipping. All original settings are
restored. These captures are diagnostic work, not timing windows.

The selected metering mip is 6 (30 by 16), not 7. Its EXR contains 254 black
pixels out of 480. The existing OpenGL fixed-floor geometric mean is 0.000238
for the first 256 samples and 0.000291 across all samples; both imply exposure
clamped to 100. Integer-stride sampling also misses part of a non-square mip,
but fixing that bias alone cannot explain or resolve this saturation.

Vulkan already uses full-image aspect-aware sampling and a mean-relative floor
for mip-backed log-average metering: `max(meanLum * 0.25, 1e-4)`. OpenGL's mono
and array shaders still use `1e-6`. Applying the established Vulkan floor to
this captured mip gives metered luminance 0.08368 and target exposure 1.195.
The first correction aligns the mip-backed log-average floor on OpenGL's current
sample set, without importing Vulkan's distinct mipless arithmetic fallback,
changing authored bounds or disabling automatic exposure/bloom. Full-image
sampling is a separate follow-up so the live result isolates the floor change;
Vulkan's grid also averages four-by-four taps per cell. Stereo reduction order
remains distinct and is not claimed to be bitwise identical across backends.
Acceptance adds finite, non-saturating exposure consistent with the sampled
image, visible final detail from two positions, cold/warm readiness, and the
existing color-grading tests. Array code receives static parity review; physical
stereo validation remains in the integrated matrix.

Evidence: `reports/gl-display-control.json`,
`reports/gl-display-exposure-toggle.json`, `reports/gl-display-hdr-mip6-exr.json`
and `mcp-captures/gl-display/`. The isolated editor is stopped before code edits.

The floor-only correction is retained in both OpenGL exposure shaders. The
sample set, mip choice, other metering modes, stereo reduction, exposure bounds
and dispatch remain unchanged. It caches at most 256 luminances (1 KiB of
shader-local storage), so it adds no texture fetches or per-frame CPU allocation.
Static GPU review passes. Release build: zero warnings/errors, 12.59 seconds.

Cold and warm runs with unchanged automatic exposure/bloom settings settle at
1.38536 in the atrium and 0.91619 in the second pose. Computing the shader's
current sample set from the captured mip EXRs predicts 1.38538 and 0.91619;
relative error is below 0.002% in all four observations. Viewed final captures
recover material detail in both positions. Authored bloom/highlight appearance
remains; this does not claim complete cross-backend image parity. Settings read
before and after the probes are identical. Both stderr logs are empty.

Cold/warm readiness admits 9/12 and 8/12 interior samples after 6.1/5.5 seconds
of stable publication; all 12 sky samples reject. The retained GPU timing dump
contains a 43.145 ms startup exposure scope and settled examples around
0.091-0.098 ms. These are diagnostic samples, not a matched throughput claim.
Existing color-grading tests pass 6/8. Both failures inspect legacy Vulkan
source paths (`FallbackExposure` and `MeteringTargetSize` strings); the tests,
source resolver and Vulkan exposure owners are unchanged. No tests are edited.

Evidence: `reports/display-metering-{cold,warm}.json`,
`reports/harness-live-gl-metering-{cold,warm}.json`,
`reports/gl-metering-cold-gpu-profile.json`, `reports/tests/gl-metering-existing.trx`,
`mcp-captures/metering-{cold,warm}/`, and `logs/gl-metering-*` under the task run.

### Metering sample coverage

The next bounded correction owns sample indexing in both OpenGL exposure
shaders. Hypothesis: integer stride truncates the sampled region whenever the
selected mip is not divisible by the sample budget. The recorded 480-texel mip
uses only indices 0 through 255. Centering the same 256 samples in equal-area
linear intervals covers the full extent without additional texture fetches.
Use quotient/remainder arithmetic to avoid multiplying a large texture extent
by the sample index. Preserve exact per-texel sampling when total texels are at
most 256, all metering modes, the retained log floor, and stereo reduction.
This removes truncation, not every possible row-major alias; an aspect-grid
policy is outside this correction.

Acceptance: build without warnings, capture real non-square HDR mips and compare
the actual exposure against the new sampled-image prediction in two views,
view final outputs, pass the readiness guard, and exercise the other non-average
metering modes through their live settings before restoring the original mode.
Existing color-grading tests may run; no new test work without clearance.

The centered quotient/remainder indexing is retained in all six mono/array
non-average loops. Static GPU review found no blocker. Release build passes
with zero warnings/errors in 12.38 seconds. Live 30-by-16 mip captures confirm
256 positions spanning indices 0 through 479. In the atrium, actual exposures
are 1.210917 / 0.686938 / 0.782083 for log-average / center-weighted / ignore-top;
in the second view, they are 1.722718 / 0.995645 / 0.995180.
All six agree with predictions from their captured HDR pixels within 0.001%.
All six viewport images were viewed; authored bloom/highlight appearance remains,
so this is not full cross-backend display parity. Original settings are restored
and before/after readbacks match.

Readiness admits 9/12 interior samples after 5.2 stable seconds and rejects all
six sky samples. Stderr is empty. The diagnostic GPU exposure scope ends at
0.098 ms; its 1,102-sample aggregate includes a 23.666 ms startup maximum.
This is not a matched throughput claim. Existing color-grading tests again pass
6/8 with the same unchanged legacy Vulkan source-path assertions; no tests
were added or modified. The owned editor was stopped after capture.

Evidence: `reports/metering-coverage.json`,
`reports/harness-live-gl-coverage.json`, `reports/gl-coverage-gpu-profile.json`,
`reports/tests/gl-coverage-existing.trx`, `mcp-captures/coverage/`, and
`logs/gl-coverage-*` under the task run.

### Valid performance control

The pristine previous increment B (`9fee4b983`) remains unchanged and failed
admission; the original cumulative baseline is D (`cee30cd57`). Neither may be
replaced silently. A separately identified repaired control B-prime can measure
only the remaining delta above common prerequisite repairs. It would require an
exact, reviewed hunk/dependency inventory for sparse publication/lifetime,
exposure, shadow publication and query/observer correctness, excluding the
targeted optimizations. Record the complete patch (including new files), binary,
dependency, harness and runtime shader/asset identities; unchanged C# alone is
insufficient when runtime assets come from another checkout.

Both variants must independently pass cold/warm admission and viewed HDR/final
image gates with matched layout, dimensions, camera path, shadow request and
features. Use counterbalanced B-prime/current/current/B-prime windows, retain
all tails/gaps, and keep heavy readbacks outside timing. If the prerequisites
cannot be separated cleanly, use a clearly scoped current-minus-one-change
control and leave the cumulative comparison open. This protocol is planning,
not evidence of a successful repaired-control build or measurement.

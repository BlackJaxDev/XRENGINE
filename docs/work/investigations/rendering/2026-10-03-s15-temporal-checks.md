# Temporal checks with TSR on the Sponza fixture (S15)

Status: first pass, October 3, 2026. History resets correctly on camera cuts and
render-scale changes, stationary output is stable, and no ghost trail was seen
in a viewed camera pan past a column. The disocclusion view was too dark to
judge, the original ghosting report is not reproduced, and the user has not
confirmed. Temporal behaviour is therefore **unverified for closing the original
report**, with no failing case found.

Owner item: [S15](../../progress/rendering/vulkan-stall-remediation-results.md).

## Setup

- Fixture: the S15a Sponza fixture with
  `CameraAntiAliasingModeOverride: "Tsr"`. The fixture used for all frame-rate
  work leaves the camera on the engine default, FXAA, so it has no temporal
  history. That is why the frame-rate changes could not have affected TSR
  there.
- Build: the October 3 working tree, Release isolated session, Vulkan,
  Advanced pipeline, CpuDirect.
- TSR state: internal resolution 1286x723 for 1920x1080 output (render scale
  0.67).
- Evidence: viewport sequences (`start_viewport_sequence_capture`, half-scale
  output, readback overflow policy `drop`, no frames dropped), screenshots, and
  the Advanced history state from `get_advanced_profile_diagnostics`.

## Results

| Check | Observation | Verdict |
| --- | --- | --- |
| Stationary detail | Two captures 2 s apart differ by 0.114/255 mean | Stable |
| Camera cut (teleport atrium to interior) | History reset generation advances across the cut (7 to 9). The first frame showing the new view contains no trace of the previous view; it shows reset aliasing on one banner edge that converges by the next frame | Pass |
| TSR render scale 0.67 to 0.5 and back | History profile generation 1 to 2 to 3, reset generation 11 to 12 to 13, internal resolution 1286x723 to 960x540 and back. The previous output keeps rendering until the new resource generation commits, with no garbage frames. Restored output matches before the change within 0.038/255 | Pass |
| Camera pan past a column over a bright banner (29 frames) | No trailing copies of the column edge over the banner. A darker faceted band beside the column is its shadowed side; it is identical in the settled frame | No ghosting seen |
| Disocclusion behind columns in the colonnade | View mostly unlit; sequence captured but not discriminating | Inconclusive |

## Other observations

- **Display latency.** Content follows the camera by two frames.
  Sequence-capture metadata records the live camera transform, so the two frames
  after a cut carry the new transform while still showing the old view. This is
  the expected update/collect/render pipelining, but frame-to-camera attribution
  in capture tooling should use the rendered snapshot's camera.
- **Shadow edge aliasing.** Every shadow boundary is blocky with large
  stair-steps, at rest and in motion. It is shadow-map resolution or filtering,
  not a temporal artifact. It is a separate visual-quality issue.
- **History flag in the frame package.** The collect-side package views never
  carry `TemporalHistoryValid` (flags 65), while the render-side admitted views
  do. The history reset generations show history is not being reset per frame,
  so this is a diagnostics inconsistency, not a defect.

## Retained shadow implementation checks

The retained shader-eligibility and atlas-page corrections were checked with
the single-light Sponza fixture, procedural sky enabled, Advanced/CpuDirect,
Release Vulkan and TSR. A constant camera input of -0.3 units/second moves
laterally from the floor view, exposing the lit banners and columns behind
the foreground column. The saved sequence and settled PNG were viewed; no
trailing column image was seen, and the left-eye reset generation remained
16 throughout the move. An earlier +0.75 units/second path entered the column
and became mostly occluded; it is excluded as a disocclusion acceptance view.

Native client resize used the existing `XRWindow.RequestResize` through MCP,
1920x1080 to 1600x900 and back, not a render-scale substitution. Observed
viewport/internal sizes were 1920x1080/1286x723,
1600x900/1072x603, then the originals again. Profile generations advanced
3 -> 4 -> 5 and left reset/seeded generations 16 -> 17 -> 18. Both saved
resize sequences were viewed and show intact scene output after reseeding.
The generic window-return reflection path timed out during serialization;
the direct owned-pawn path `Controller._viewport.Window` avoids that tooling
issue. It did not require an engine change.

Evidence is under `Build/_AgentValidation/20261003-142654-vk-todo/mcp-captures/`
in `retained-tsr-lit/` and `retained-tsr-resize/`. Exact binding/history
correlation and pipeline/view switching remain open. Source review identified
a candidate post-commit TAA snapshot read; confirm actual bound GPU uniforms
before selecting a correction. Temporal publication on rejected moving frames
also remains a distinct rejecting check.

The user has no original recording and directed that unavailable replay be
ignored. This waives replay of that artifact, not the live correctness gates
or the user's final visual/performance confirmation.

## Deferred temporal resolve snapshot correction

The settled TAA RenderDoc capture `renderdoc/taa-before-warm.rdc` contains
393 canonical draws. Its resolve draw at event 1280 binds history-ready = 1
but identical current and previous jitter UVs:
(-0.0001595052163, -0.0000405092578). The actual constant buffer confirms
that the deferred callback reads the mutable snapshot after CPU commit.

Hypothesis: retaining the pre-commit resolve snapshot, including TAA exposure
readiness, preserves distinct current/previous jitter at deferred binding.
Owner: temporal accumulation command and the Advanced/Default resolve callbacks.
Capture readiness before enqueueing the resolve, and use the owning pipeline's
resolve snapshot. Do not change quality settings, history weights, or frame
admission. Reject the change if settled history stays disabled, actual bound
jitter remains aliased, cut/resize reseeding regresses, or viewed motion develops
trails. Budget: one narrow build and one before/after GPU capture, followed by
the retained live motion/resize checks. Accepted-frame publication is a separate
unresolved check and is not claimed fixed by this correction.

Retained correction: exposure readiness now refreshes the resolve snapshot
before enqueue; Advanced TAA and Default TAA/TSR explicitly read the owning
pipeline's resolve snapshot. CPU commit continues to update only the general
snapshot. Release build passed with zero warnings/errors. Independent source
review found no added hot-path allocations or lifetime blocker.

The after capture `renderdoc/taa-after-moving.rdc` includes 393 canonical draws.
At event 1280, history-ready remains 1; current jitter UV is
(0.0001595052163, -0.0002835648193), while previous jitter is
(0.0000683593753, 0.0002835648193). Actual bindings reject the original aliasing
hypothesis after the correction. Viewed TAA constant-input motion, native
1600x900/1920x1080 resize, and TAA/TSR cut sequences show intact scene output
and no retained image of the previous view after the cut. TAA resize reset/seed
generations advance 5 -> 6 -> 7, with history ready after each transition.
Artifacts are `taa-after-linear/`, `taa-after-resize/`, and
`temporal-after-cut/` under the current run's `mcp-captures/` directory.

The moving TSR capture `renderdoc/tsr-after-moving.rdc` also contains all
393 canonical draws. The actual resolve at event 4700 has history and depth
reprojection ready, distinct jitter, and UV jitter exactly half of the packed
view-record NDC jitter. Its current-to-previous matrix agrees with the scene
view record's inverse-current times previous jittered matrix to a maximum
element difference of 0.000006 (after transposing the reflected GLSL constant
matrix into the CPU convention). This is the normal accepted-frame baseline;
it does not establish correctness after a rejected frame.

## Submission-gap diagnostic gate

Normal TSR bindings agree with native scene matrices. The logical history ledger
invalidates the first frame after a submission gap, while temporal CPU commit
currently advances independently. Hypothesis: the first resumed logical view
can report `FrameGap` while its temporal resolve still reports history ready.
Owner: temporal Begin, where both exact pipeline-owned snapshots coexist.
Use a temporary, bounded diagnostic only on gaps; correlate render frame,
pipeline, history sequence/source, and both readiness states. Exercise the
existing one-shot desktop rejection after warming a moving TSR fixture.
Reject this hypothesis if readiness agrees on the injected gap. Remove the
diagnostic before retaining any correction; no quality-policy change is allowed.
Budget: one instrumented build, one injection, then remove or replace the probe
with the smallest correction and repeat the same live check.

An explicit multi-present RenderDoc enclosure terminated the owned diagnostic
editor before producing a capture. That attempt is excluded; it does not prove
a runtime temporal defect. The next check uses the bounded CPU-side diagnostic.

The instrumented Release build passed with zero warnings/errors. During real
readiness deferrals under constant camera input, frames 3046 and 3047 had
logical/native history invalid but resolve history ready. The exact owner was
pipeline 10, view 0, camera 1, history key 6360580618003107407. At frame 3046,
the logical sequence was 3046 and the last committed sequence was 3044
(discard count 90); frame 3047 still had committed sequence 3044 (discard 91).
Ten further bounded gap samples showed the same disagreement. The explicit
exposure-based injector did not arm: its `AutoExposureTex` had no tracked layout
in that planner scope. Do not attribute the naturally occurring gaps to the
injector. Evidence: `reports/temporal-gap-probe.txt`,
`reports/temporal-gap-injection.json`, and `reports/temporal-gap-lifecycle.txt`.
The correction will invalidate the matching temporal eye when the authoritative
logical view has invalid history, then reseed through the existing coverage
tracker. It must preserve readiness on uninterrupted accepted frames and must
not reset unrelated view owners.

The corrected build passed with zero warnings/errors. The same bounded probe
now reports logical/native/resolve readiness all false on twelve actual gaps,
including frame 470 (sequence 470, committed sequence 468, discard count 97).
After recovery, ordinary history is ready again and reset/seed generation 42
matches. Evidence: `reports/temporal-gap-probe-after.txt` and
`reports/temporal-gap-injection-after.json`. The explicit injector again did not
arm; only observed native-readiness gaps are claimed. The temporary probe was
removed. The retained guard requires desktop authoring, a nonzero logical history
sequence, and the matching camera; it runs after camera-cut handling and before
jitter generation. A dedicated logical-history-invalid reset reason distinguishes
this event from a missing snapshot or camera cut. Untracked stereo is unchanged.

## Pipeline replacement checks

With the same camera and TSR scale, minimal constructor-backed Default and
Advanced assets render the Sponza geometry after live replacement. Resource
generations advance to 15 (Default) and 17 (Advanced); both settled screenshots
were viewed. Evidence is `reports/temporal-minimal-switch.json` and
`mcp-captures/temporal-pipeline-minimal/`. Different lighting between the two
pipelines is not a temporal image-parity comparison.

The first experiment used a fully serialized Default asset. It experienced a
long cold native-pipeline compilation delay, then produced cyan output. Stack
samples are saved as `reports/temporal-switch-stacks*.txt`; the final image and
`renderdoc/default-switch-invalid.rdc` preserve the failure. Minimal assets omit
the serialized runtime command chain and render correctly. Source inspection
finds that command-chain delegate callbacks are omitted during serialization;
whether the loaded chain loses those callbacks remains a separate bounded
asset-round-trip investigation. The original Advanced asset ID was not registered
after replacement, so return-switch automation must use a retained asset path.
Generation-changing sequence captures can reject readback until a matching
submitted generation exists; such failures are excluded from visual acceptance.

The retained build repeated the minimal-asset switch. Cold Default preparation
outlasted six screenshot retries and a diagnostic timeout. Two stack samples
showed forward progress: first a native graphics-pipeline-library create while
the main thread awaited its task, then primary descriptor preparation with the
compiler idle. They do not establish a deadlock; recursive/inlined monitor
acquisition can explain a sampled `Monitor.Enter_Slowpath`. Per-job foreground
waits are bounded to 30 seconds, but multiple sequential cold requirements are
not a bounded total preparation window. Keep this cold responsiveness issue open.

After recovery, viewed Default and Advanced screenshots contain Sponza at the
same camera and TSR scale; resource generations were 12 and 14. The Advanced
stationary TSR image has softer/doubled fine column edges relative to a settled
FXAA diagnostic at full resolution. That comparison changes AA and resolution
and does not isolate a defect, but prevents claiming full temporal image
acceptance. Reopen exact velocity/depth/binding correlation at this viewpoint.
Evidence: `reports/temporal-final-switch*.txt`,
`reports/temporal-final-switch.json`, `mcp-captures/temporal-pipeline-final*`.

## Remaining

October 4 distinct-camera validation hypothesis: switching the active viewport
to another camera with matching TSR settings must change the temporal view
identity, reset and seed history for that identity, and render its own viewpoint.
Use the retained Advanced/CpuDirect mono Sponza fixture, explicit 1024-pixel
directional shadows, and the TSR settings file. Correlate published temporal
diagnostics with completed output, capture and view both camera positions, then
restore the original camera. Do not infer exact first-frame reset behavior from
a later settled sample; keep that portion open if existing diagnostics cannot
retain the transition evidence. No test changes or new renderer fix is part of
this validation.

The first October 4 attempt creates a distinct camera at the dark-view pose,
with TSR selected, and requests `SetAsPlayerView(One)` in Sandbox mode. The
request creates a pawn but does not change the editor viewport's camera identity.
All twelve switch polls and the settled capture retain the original camera,
history key and reset/seed generation 13. Viewed original/second/restored images
therefore show the same atrium and cannot pass the distinct-camera gate. Vulkan
continues completing frames; this is an unapplied possession request, not proof
of a temporal reset defect. Trace the editor controller/possession path before
repeating. Evidence: `reports/distinct-camera-tsr.json` and
`reports/distinct-camera-tsr-unapplied-contact.png`.

The request was queued by design while another pawn remained possessed. A
second attempt sets MCP dispatch to MainThread for this session and invokes
`PawnComponent.PossessByLocalPlayer(One)` directly. It changes the active camera
component and node, renders the distinct dark-view pose, and restores the atrium.
All three HDR/TSR/final rows were viewed. Post-process settings match exactly;
the pipeline instance remains 10 and the internal size remains 1286x723.
Camera-scoped reset/seed generations are 14/14, 2/2 and 17/17 with ready history.
The queued request is cleared, original possession restored, and session-only
MCP dispatch restored to Direct after capture.

The constant frame-package key 6360580618003107407 is the desktop mono channel,
not a camera key. Actual TSR isolation uses `TemporalViewKey`, including camera,
viewport, pipeline and eye identities. Collection-time package validity is
provisional; Vulkan replaces it with authoring-resolved views before recording.
The saved per-eye diagnostics report `TemporalHistoryValid` at frames 50608,
51104 and 53552 for the original, switched and restored views. This accepts the
bounded settled camera-switch check. It does not prove the exact first reset
frame: public temporal diagnostics omit the temporal key and snapshot frame,
and separate state/diagnostic calls span several frames. Keep exact first-frame
and deferred-uniform attribution open rather than changing the channel key.
Evidence: `reports/distinct-camera-tsr-applied.json` and its viewed contact sheet.

The final build, with the diagnostic removed, completed a zero-warning,
zero-error Release build and both AA modes' fixed-speed motion, resize, and
camera-cut sequences. Viewed captures are under
`Build/_AgentValidation/20261003-142654-vk-todo/mcp-captures/temporal-final-*`.
TAA motion retained reset/seed generation 16; resizing to 1600 by 900 and back
advanced it to 17 and 18. TSR motion retained generation 21; resize advanced
it to 22 and 23. Both modes retained ready history during uninterrupted motion,
and the viewed sequences show no visible trail at the lit column edge.
Both camera cuts displayed the new viewpoint without retaining the old image.
The TSR cut also encountered readiness gaps (reset/seed 34 to 88), so this is
recovery evidence, not evidence that rejected-frame frequency is resolved.
The settled state recovered ready history. Report files retain the exact
pipeline, view, and submitted-frame identities; separate asynchronous
diagnostic fields are not asserted to describe the same frame.

- Pipeline/view switches.
- Correlating jitter and previous/current matrices with velocity output.
- The deferred TAA/TSR binding-snapshot check under admission/lifetime changes.
- The user's ghosting confirmation.

Evidence root (ignored, disposable):
`Build/_AgentValidation/20261003-015628-vk-100hz/mcp-captures/s15b/`.

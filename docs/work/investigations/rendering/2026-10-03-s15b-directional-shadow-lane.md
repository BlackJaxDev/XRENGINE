# Directional cascade casters on the Advanced canonical lane (S15b)

Status: implementation complete, live validation in progress. October 3, 2026.

## October 4 matched performance repeat

Use the same frozen current Release binaries, original Sponza fixture and
CpuDirect submission, toggling only `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE`.
Run one owned editor at a time, with no build, tests or agent filesystem work
inside measurement windows. Read back 1024 directional shadow resolution and
the actual backend/strategy, settle publication, then record two 20-second
stationary and motion windows per restart. Counterbalance restarts off/on/on/off.
Count `frame_lifecycle.outcome_counts.completed`, retain rejected/failed deltas
and whole-frame tails, and capture/view output outside the timed windows.
Retain the original >=100 fresh motion FPS and <=5% stationary-regression gates;
do not silently relax them based on the outcome. Record boundary request timing
so observer uncertainty is visible. A failing correctness gate or stale output
invalidates a performance claim.

SteamVR/Oculus processes predate the owned editor. Their shutdown requires user
authorization; until then leave them running and record their CPU activity and
presence. A comparison with those runtimes active is qualified host evidence,
not final idle-host acceptance. No tests or renderer code are changed for this
measurement.

The first off run is excluded from acceptance: the old `fps_probe.py` A/B
positions capture a dark exterior wall with sky, rather than the intended
interior workload. It records 123.4/124.8 stationary and 35.5/35.2 motion fresh
FPS with zero rejected/failed frames, but positive geometry admission alone
does not certify the intended view. Preserve `reports/vk-perf-off1.json` and
its images as the rejected fixture trial. Restart the comparison with viewed
interior endpoints A=(8,6,-20) looking at (-8,1,-20) and B=(4,5,-20) looking at
(-8,1,-24). Qualify captures before timing. This changes the benchmark fixture;
it must not be presented as a directly matched repeat of the old numbers.

The corrected interior pair uses identical recorded binary hashes and fixture
hashes. Generic stationary is 93.69/96.48 fresh FPS and motion 33.74/33.65;
lane stationary is 100.26/99.90 and motion 92.09/92.46. Every measured window
has zero rejected and failed frame deltas. Both qualification endpoints were
viewed before timing; lane/generic mean RGB differences are 0.00405/255 and
0.00236/255, with 0.00034%/0.00039% of pixels exceeding channel difference 16.
The lane is ready, accepting groups, and no competing owned editor is running.
SteamVR/Oculus remain active; their CPU counters are recorded before/after.

The motion target fails twice. Per the failure rule, stop the remaining
counterbalanced restarts and attribute the residual cost before changing code.
Do not close idle-host acceptance or claim the complete four-run comparison.
Lane whole-frame p50 at the two motion boundaries is 9.34/9.64 ms, p99
13.61/14.86 ms. These are rolling 512-frame boundary summaries, not percentiles
of the complete 20-second windows. A later CPU snapshot contains about 4.3 ms command recording
and 3.7 ms next-slot wait in one frame; this is a diagnostic sample, not a
distribution or proof of the remaining bottleneck. Next collect detailed CPU
stage counter deltas and GPU scope history on the same route. Those enabled
observers are attribution work and must not be mixed into the timing result.

Evidence: `reports/vk-perf-interior-{off1,on1}.json`,
`reports/vk-perf-interior-{qualification,on-qualification}.json`,
`reports/vk-perf-interior-on1-{stats,cpu-dump}.json`,
`reports/vk-perf-shader-identities.json`, corresponding `mcp-captures/vk-perf-*`
and `logs/vk-perf-*` under the existing task run. No new tests or renderer edits.

### Directional shadow GPU timing coverage

Dense attribution reveals a coverage hole: the GPU history contains nine native
compute scopes and no directional-shadow raster scope. The `Advanced` root is
therefore a partial sum, not whole-frame GPU time. One bounded instrumentation
change owns `RecordAdvancedDirectionalShadowRasterPayload`: use the existing
dense-only, fixed-budget GPU scope mechanism around the group's barrier and
clear/raster commands, with a cached diagnostic path. The existing method leaves
the render scope active; preserve that boundary and exclude later store/transition
work rather than introducing a new render-scope end for instrumentation.
Preserve draw commands, resource ownership, barriers and frame-slot waits.

Hypothesis: a named group scope will expose shadow refresh GPU cost without
changing rendering or adding disabled-path queries/allocations. Reject it if
the scope is absent during accepted refreshes, query accounting grows while
profiling is disabled, image parity fails, or native query/lifetime diagnostics
appear. Acceptance: zero-warning build, static query ownership review, live
motion with dense timing showing source-frame-tagged group samples, unchanged
viewed endpoint output, and a disabled diagnostic round trip. Budget: at most
two additional timestamp queries per recorded cascade group, within the existing
pool limit; no per-frame managed array or path construction. No new tests.

Before instrumentation, detailed motion logging records 3,654 unique source
frames: 3,606 completed and 48 rejected early in the sequence (frames 2211-2364).
All 3,606 completed frames join a later GPU result by its explicit source frame
ID. Consecutive cumulative directional-operation count deltas classify 1,197
refresh frames and 2,409 completed ordinary frames. Mean completed GPU elapsed
time is 12.592 versus 10.050 ms; mean primary CPU recording is 5.159 versus
4.026 ms. Rejected rows remain in the unfiltered record and are excluded only
from the explicitly completed-cohort comparison. These enabled-observer results
are diagnostic evidence, not a repetition of the accepted timing setup.

The partial GPU dump covers a different, mostly every-eighth-frame sample range;
do not subtract its 5.820 ms aggregate from the motion-only coarse GPU mean.
Its TOP_OF_PIPE/BOTTOM_OF_PIPE intervals can include dependency/drain time and
overlap other GPU work, so they are not exclusive shader cost. The roughly
2.542 ms refresh/ordinary difference is a hypothesis to investigate with the
new scope, not proof of directional-shadow cost. A first detail-only attempt
could not export GPU history because dense timestamps were disabled; retain
that failed export alongside the successful dense capture.

Evidence: `reports/vk-interior-attribution{,-coarse,-summary,-frames}.json`,
`logs/vk-interior-attribution-dense.log`, and the recorded CPU/GPU dump paths.
The completed-cohort summary is explicit; CPU frame dumps may include multiple
DispatchRender scopes and incomplete scopes and must not be treated as one
frame's summed work.

The instrumentation passes static review and its isolated Release build has
zero warnings/errors (28.84 seconds). Viewed A/B endpoints differ from the
pre-instrumentation lane output by 0.01730/255 and 0.00072/255 mean RGB; only
0.00024%/0.00010% of pixels exceed channel difference 16. No draw, barrier or
render-scope closure changed.

The warmed dense motion run records 3,557 completed frames, no rejected/failed
frames. All 1,193 refresh frames use exactly 20 diagnostic timestamp queries;
all 2,364 ordinary frames use 18. The exported scope history contains 146 shadow
samples, averaging 2.923 ms, minimum 2.124 and maximum 14.405 ms. Eighteen shadow
entries in the detailed worst-frame trees join the logged completed refresh
source frames and their coarse GPU timings; this is a checked exported subset,
not a claim that all 146 detailed intervals were exported individually. The
scope measures barrier/setup/clear/draw elapsed time including dependencies,
while later closure/store/transition work remains outside.

After disabling GPU pipeline profiling, six live readbacks confirm it remains
disabled; all 1,049 completed logged frames in that motion interval issue zero
diagnostic timestamp queries. The original disabled setting is restored.
Stderr is empty; existing `VulkanRenderQueryTests` pass 21/21. No tests were
added or modified. The owned editor is stopped. This closes timing coverage,
not the below-100-FPS performance gate or remaining GPU coverage gaps.

Evidence: `reports/vk-perf-shadow-scope-qualification.json`,
`reports/vk-interior-attribution-shadow-scope{,-summary,-frames}.json`,
`reports/vk-shadow-scope-{disabled,verification}.json`,
`reports/tests/vk-shadow-scope-existing.trx`, and `logs/vk-shadow-scope-*`.

Owner item: [S15b](../../progress/rendering/vulkan-stall-remediation-results.md)
of the Vulkan stall remediation TODO. Target set by the user: above 100 Hz while
the camera moves on Vulkan with the Advanced render pipeline and CpuDirect
submission rendering Sponza with one directional light, without removing
features.

## Predeclared gate

- **Owning path.** `VPRC_AdvancedRenderStage` (stage `DirectionalShadowRaster`),
  `ShadowAtlasManager.AdvancedDirectionalShadowLane`,
  `DirectionalLightComponent.TryBuildAdvancedDirectionalShadowLaneRequest`,
  `VulkanCommandRuntime.TryPrepareDirectionalShadowLane` and
  `RecordAdvancedDirectionalShadowRasterPayload`,
  `VulkanDirectionalShadowPipelineFactory`, the lane shaders under
  `Build/CommonAssets/Shaders/Advanced/Visibility/DirectionalShadowRaster*`.
- **Hypothesis.** Recording the about 400 Sponza casters as CPU-direct draws
  over the desktop family's already sealed canonical bins costs a few
  microseconds per draw per cascade on the render thread, so a cascade refresh
  frame falls from about 51 ms to under 15 ms and fresh camera-motion frames
  per second rise from about 36-40 to above 100 at identical shadow output.
- **Rejecting checks.** Motion fresh FPS below 100 on the fixture while the lane
  counters show every group accepted; or images at the fixed views
  (`camA`, `inside`, `atrium`, `floor`, `moving`), after light motion and after
  object motion, differing from the generic path by more than the baseline
  noise (mean absolute difference above 0.5/255 or more than 0.1% of pixels
  with a channel difference above 16, outside the random sky).
- **Correctness invariants.** Zero rejected frames; `acceptedGroups` advances
  while the camera moves and `genericGroups` stays at zero while the lane is
  ready; with `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE=0` the generic path renders
  every group with identical images; shader reload under motion, play-mode
  round trips and deactivation/reactivation keep the editor alive and the
  shadows intact.
- **Metric, budget, tolerance.** Fresh frames per second from
  `frame_lifecycle.outcome_counts.completed` over two 20 s windows each for
  stationary camera A and continuous eased motion A<->B (`fps_probe.py`);
  render-thread whole-frame p50/p90/p99 from `frame_outputs`. Budget: at least
  100 fresh frames per second in motion; run-to-run spread about +-3 FPS in
  motion and +-15% stationary (from the S15a record); stationary must not drop
  more than 5% against the baseline measured on the same host state.
- **Observer overhead.** MCP polling once per window; no profiler detail.

## Baseline (current tree before the lane, session `vk-todo-base`)

Measured while four code-mapping agents loaded the CPU, so stationary is lower
than the idle 159 recorded for S15a; motion matches S15a within its spread.

| Window | Fresh FPS | Whole-frame p50 / p90 / p99 / worst |
| --- | ---: | --- |
| Stationary (x2) | 125.2, 128.1 | 6.8-7.0 / 7.7 / 9.1-9.6 / 10.6-11.7 ms |
| Motion (x2) | 35.8, 35.9 | 8.0-8.3 / 56.7 / 65-67 / 77-91 ms |

Baseline images: `mcp-captures/base/` in the run root.

## Design as implemented

The desktop Advanced family already prepares, for the main view, the exact set
of canonical draws the cascades need: payloads, geometry closure over the
global atlas, set-1 payload storage and the set-2/3 scene publication. The lane
therefore records that family's sealed stable bins a second time instead of
creating a separate family per cascade source:

- `EAdvancedRenderStage.DirectionalShadowRaster` follows
  `DepthPyramidAndLateVisibility`, after deformation and after the visibility
  raster has sealed and retained the bins. Its render-graph pass depends on the
  late visibility raster, and native opaque shading depends on it.
- In `GlobalPreRender`, `ShadowAtlasManager.RenderScheduledTiles` keeps
  allocation, dirty tracking, budgets and completion receipts. A directional
  cascade group whose page is depth-only (`Depth` encoding), whose source is the
  desktop camera, and whose grouped render would otherwise run, is deferred to
  the lane when the stage reported itself ready in the previous render frame.
  The light publishes one `AdvancedDirectionalShadowLaneRequest` per group:
  the page framebuffer, one inner tile rectangle and one world-to-clip matrix
  per cascade (same order as the grouped layered pass).
- The stage dequeues the deferred groups, builds the family request exactly as
  the other native stages do (same reservation, publication, views and flags)
  with the atlas page as the target, and enqueues one `AdvancedVisibilityOp`
  per group. The Vulkan backend copies the group into a ring slot stamped with
  the render frame.
- Family preparation validates the atlas page as a single-sample depth-only
  dynamic-rendering target without multiview, prepares one depth-only pipeline
  per bin coverage (opaque, masked) with `CullMode.None` and `Lequal`, and
  derives a per-record cascade mask: the record's canonical draw must carry
  `CastShadow`, and its candidate world AABB must intersect the cascade clip
  volume (the same test the generic layered pass applies per caster).
- Recording loads the page (no whole-page clear), then per cascade sets the tile
  viewport and scissor, clears the tile depth to 1.0, binds the lane pipeline
  and the family's descriptor sets, and issues the frozen indexed arguments of
  every masked record with a push block of the raster header plus the cascade
  matrix. The lane vertex shader resolves the payload, draw and transform like
  the visibility raster and projects with the pushed matrix; the masked
  fragment variant applies the standard-material alpha test.
- Acceptance commits cascade slots and completion receipts as a grouped render
  does, under a second submission-tracking cohort of the same frame. A
  rejected or unconsumed group stays dirty, renders generically from the next
  frame, and holds the lane closed for 60 frames. A lane pipeline failure at
  preparation rejects the fresh frame before recording, publishes a lane fault
  for 5 s (the stage then reports not ready), and lets the existing failed
  submission receipt preserve the keys for generic retry. Enqueue acceptance
  alone is not proof of a recorded or submitted shadow update.
- `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE=0` keeps every group on the generic
  path. `get_advanced_profile_diagnostics.directionalShadowLane` reports the
  counters and the last decline reason.

Out of scope, kept on the generic path with an explicit decline reason: moment
encodings (colour pages), HMD cascade sources, GPU-driven submission strategies,
and sequential (non-grouped) cascade renders.

## Results

All measurements below use one binary (session `vk-todo-lane`, the current tree
with the lane) and the S15a fixture; the generic rows restart the same binary
with `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE=0`. Two 20 s windows each; idle
machine.

| Path | Stationary fresh FPS | Stationary p50 / p99 | Motion fresh FPS | Motion p50 / p90 / p95 / p99 / worst |
| --- | ---: | --- | ---: | --- |
| Generic (lane off) | 147.5, 147.5 | 5.9 / 7.6-8.7 ms | 39.7, 38.9 | 6.9-7.2 / 51.3-52.5 / 52.2-54.9 / 58.0-58.9 / 60.5-67.0 ms |
| Lane | 138.9, 149.9 | 5.7-6.6 / 6.9-8.6 ms | **123.5, 128.2** | 7.0 / 8.3 / 8.6 / 9.2-9.5 / 10.6-11.1 ms |

The motion target is met: the cascade refresh frames that held motion at about
40 frames per second (p90 above 50 ms) are gone from the tail. Zero rejected
frames in every window.

Lane accounting (`get_advanced_profile_diagnostics.directionalShadowLane`): over
a 10 s motion window the atlas deferred 303 cascade groups and the backend
accepted all 303, with no rejected or unconsumed group and no generic group.
With the lane disabled the same window records 150 generic groups and no
deferral, with the decline reason naming the environment flag, so the generic
path is exercised and remains correct. At startup, before the stage first
reported itself ready, 6 groups rendered generically and one deferred group
was unconsumed (the stage did not run that frame), which held the lane closed
for 60 frames as designed.

Live checks on the lane build (`mutation_check.py`, `reload_stress.py`):
20 Sponza root moves keep 396 resident draws, deactivation drops them to 3,
reactivation restores 396; six shader-reload cycles under continuous camera
motion complete with the editor alive and no renderer-terminal failure.

**Images.** The default fixture picks a random environment map per launch,
which changes the sky and the auto-exposure, so cross-launch comparisons on it
differ by 0.3-0.6/255 on lit surfaces. On the `ProceduralSky: true` fixture with
10 s of settling per view, the lane and the generic path compare as follows
(mean absolute RGB difference over 1920x1080, fraction of pixels whose largest
channel difference exceeds 16; the repeat column is the same view captured
again 3 s later in the same session):

| View | Lane vs generic | Same-session repeat |
| --- | --- | --- |
| camA | 0.002, 0.000% | 0.10, 0% |
| inside | 0.017, 0.02% | 0.06, 0% |
| atrium | 0.085, 0.10% | 0.10, 0% |
| floor | 0.172, 0.25% | 0.01, 0% |

Three views match within the session's own repeat noise. The floor view keeps a
residual on the two potted plants and exceeds the predeclared pixel-fraction
threshold. The lane applies the standard-material alpha test (texture 0,
material cutoff, explicit UV gradients), while the generic pass runs the Uber
shadow-caster variant. Coverage or sampling differences are a hypothesis, not
an established cause or an accepted parity exception. Light-motion views (12 small
yaw steps and back) match to 0.04-0.09 after settling, and the mid-move capture
is not comparable across sessions because its timing is not deterministic.

## Resumed cold-start validation

The fresh full-editor Release build in named session `vk-shadow-resume` passed
with zero warnings/errors, but live rendering became terminal at frame 131:
the sealed Advanced family failed its stage-cardinality check. Only 44 frames
completed; subsequent frames were rejected and the viewed capture contained
only the environment. The scene hierarchy did contain Sponza. Lane enqueue
acceptance continued increasing despite the terminal frame outcome, so that
counter does not establish successful atlas recording. The attempted reload
probe ran after this failure and is not a valid reload check.

Next hypothesis: asynchronous pipeline readiness changes between stage enqueue
calls can publish an incomplete family at startup. Record the individual stage
counts in the rejection, then repeat the same fresh-build fixture. Reject the
hypothesis if counts show a complete family or duplicate stages. Preserve strict
family sealing and submission receipts; do not render a partial family or
acknowledge unwritten shadow tiles. Acceptance requires visible Sponza from two
views, advancing completed-frame counts, no terminal disposition, and a separate
reload-under-motion check after valid scene admission. Performance comparison
remains excluded while another editor session is running.

The broker route available during resumption returned deprecated `gpt-5.6-sol`;
no broker worker was launched. Native read-only GPU review found the readiness
and receipt defects described here; its final review found no blocker in the
two correction slices.

The second launch reproduced the terminal failure at frame 136. The new
diagnostic reported one preparation/raster/late-compute/late-raster stage, no
AO stage, one classification and opaque stage, and valid family state. Source
review identified the optional shadow programs in the required-family identity:
creating the masked program during the shadow readiness poll invalidates that
identity, so AO observes Pending while later stages can observe Ready again.
The correction separates the shadow source identity and checks shadow program
currentness independently. The strict family-cardinality rejection remains.

The corrected Release build has zero warnings/errors. The first live run reached
11,008 completed frames with no terminal disposition or failed frame; three
shader-reload cycles under camera motion recovered. It accumulated 129 rejected
frames across startup/reloads, so this is not a zero-rejection claim. Saved and
viewed inside/atrium images show the scene from different positions. The startup
failure is no longer reproduced in this run.

Next isolated correction: a lane group skipped during family preparation must
not share a successful frame receipt. Hypothesis: rejecting that fresh frame
before recording, with `RetryFrame` and a lane fault hold, makes the existing
atlas submission tracker retain its dirty keys and render them generically on
the next frame. Reject if a failed preparation still submits its receipt, the
renderer becomes terminal, or fresh-frame progress cannot recover. Validate a
controlled lane-preparation failure and reload/motion recovery; no performance
claim from fault-injected or concurrently running sessions.

The controlled probe failed the twentieth lane preparation after enqueue. The
atlas observed `Failed` receipts with all four cascade keys retained, including
frame 1553; subsequent retries remained scheduled with `requiresRender=false`
and lane readiness false. Generic group count rose from 6 to 140, completed
frames advanced past 5,170, and no renderer-terminal result occurred. The first
generic refresh needed cold mesh admission, producing additional transient
rejections: 142 total versus 8 before the injected failure. Recovery therefore
does not imply a one-frame bound. All temporary fault injection and receipt
file logging were removed before the final build.

The final uninstrumented Release editor build also passed with zero warnings and
errors. On the procedural-sky fixture, the admitted-scene mutation check moved
the Sponza root twenty times, deactivated it (396 resident draws to 3), and
reactivated it (back to 396). Completed frames advanced from 4,051 to 6,882
without additional rejections during those mutations and camera motion. Shader
reload then recovered to 9,190 completed frames with 76 transient rejections,
zero failed frames, and no terminal disposition. The restored and inactive PNGs
were viewed. An earlier invocation started before Sponza import completed and
found no root; it is excluded from scene-mutation acceptance.

No tests were added or modified. These checks validate the readiness and
receipt corrections; the entire directional lane remains Active. The original
recorded performance result is not re-measured here because the previous
harness editor remains running and is not owned by this resumed session.

The same final binary restarted with `XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE=0`
kept 396 resident draws, completed 7,585 frames with no terminal disposition or
failed frame, and reported 195 generic groups with zero lane deferrals or
acceptances. Light refresh and camera movement completed; the saved inside-view
PNG was viewed. Startup/cold admission accumulated 179 rejected frames, so this
is a fallback correctness smoke rather than performance acceptance. Only the
owned `vk-shadow-resume` session was stopped at closeout. Native validation-layer
acceptance and the earlier unexplained harness process exit are not established
by these checks.

The earlier floor image result (0.25% pixels above channel difference 16)
**fails** the predeclared 0.1% gate. Its exact cause still needs raw atlas and
sampling evidence; the foliage-edge explanation above is a hypothesis.

Additional review findings remain open: shared stable-bin headers retain only
one shadow page closure, so multiple directional groups on different pages need
pre-enqueue decline or per-group closures to avoid recurring retries. The
masked shadow shader also writes depth for a nonstandard material where the
visibility shader discards. This does not explain the standard-foliage residual;
material eligibility and exact atlas coverage need separate validation.

## Depth-only fragment coverage investigation

Matched floor captures on the resumed Release binary with RenderDoc 1.44
reproduce the failure: mean RGB difference 0.141/255 and 0.211% of pixels
above channel difference 16. The raw D24 atlas differs at only 371 of
16,777,216 texels; all other texels are bit-identical. Both atlas PNGs were
exported and viewed. The generic refresh capture has no fragment shader or
sampled textures at the inspected depth draws (including the 5,930-triangle
caster), while its visibility raster does retain a fragment shader.

The owning path is `VulkanGraphicsPipelineFactory`'s stage selection used by
`VkMeshRenderer`: it removes every fragment stage when the target has zero
color attachments. This suppresses alpha discard, depth output and other
fragment-side work. The separate canonical shadow factory does not apply that
filter. The generic path is therefore not a valid alpha-coverage reference yet.

Predeclared correction gate: preserve exactly the stages requested by the
program, independent of the target's color count. Hypothesis: retaining the
fragment stage restores the generic masked caster's authored coverage.
Reject if the corrected generic refresh still lacks its fragment shader or
opacity bindings, opaque atlas coverage changes, rendering becomes terminal,
or shader reload/motion fails to recover. Compare one matched floor atlas and
two settled camera PNGs before/after, inspect the masked draw and sampler,
then run warmed motion and reload checks. Build Release with zero new warnings;
add no tests. RenderDoc is a correctness observer, not a timing measurement.
Full material eligibility remains a separate gate: the generic Uber variant
can use a separate opacity mask that the canonical standard alpha test does
not yet represent. Source review found that importer configuration, but it
does not establish the live material's bindings. Do not remove either path's
alpha test to force image equality.

Both mesh build requests and direct program pipeline creation now retain the
requested fragment stage through the same helper. The final Release editor
build passed with zero warnings/errors. In the corrected refresh capture,
the inspected masked draws have fragment shaders and sampled textures; the
5,930-triangle caster has a ten-mip texture with linear minification,
magnification and mip filtering, wrap addressing, zero mip bias and LOD range
0-9. Its former pipeline had neither a fragment shader nor sampled resources.

The matched static capture's four shadow records are byte-identical between
the lane, original generic and corrected generic paths except for
`LastRenderedFrameLo`. This includes both matrices, atlas placement and bias.
The correction changes exactly 371 atlas texels, all to farther depth, and
leaves every other texel unchanged. The corrected generic atlas differs from
the lane at just one texel, at (1888, 3407), by 1,384 D24 units. Atlas PNGs were
exported and viewed. The first corrected floor PNG comparison passes: mean
RGB difference 0.033/255 and 0.048% pixels above channel difference 16.

Two generic-path shader reload/motion cycles recover. Completed frames advance
from 11,796 to 16,433, rejected frames remain at the startup total of 181,
failed frames stay zero and no terminal disposition is reported. On the final
binary with the lane enabled, another reload/motion cycle recovers and the
session reaches 25,584 completed, 99 startup/reload rejections, zero failed
frames and no terminal disposition. Floor and atrium PNGs were captured and
viewed; an unequal-settle atrium comparison had a brightness shift and is
excluded in favor of the matched-settle comparison below.

The lane's non-RenderDoc smoke records 110.0 stationary and 101.8 motion fresh
FPS over one 20-second window each, with zero rejected or failed frames in
those windows. This is not performance acceptance: the earlier harness editor
is still active, and there is no matched idle-host control. No tests were added
or changed. Broad custom-material coverage and other lane acceptance gates
remain open.

Final matched comparison: the same final binary, restarted with the lane
enabled/disabled, uses the same floor-then-atrium sequence, 20 seconds of
settling per view, then a same-view repeat three seconds later. Floor passes
at 0.044/255 mean RGB difference and 0.064% pixels above channel difference
16; atrium passes at 0.042/255 and 0.009%. Same-session repeat means are below
0.0006/255. The PNGs were viewed. This validates the floor-parity correction
without waiving the predeclared tolerances. The final generic session reaches
9,141 completed frames, 180 startup rejections, zero failed frames and no
terminal disposition.

The three final session log sets contain no device-lost, unhandled-exception
or failed-upload indication in the available Release logs. This is not native
validation-layer acceptance. Only the owned `vk-shadow-resume` editor was
stopped; the `shadow-parity` replay session was closed and its owned temporary
capture copies removed. The retained captures and raw tables are under the
run's `renderdoc/` folder. Read-only final review found no correction blocker.

## Masked material defensive eligibility

Hypothesis: a malformed or unsupported canonical material row can write opaque
shadow depth because the masked shadow shader returns instead of discarding.
The canonical publisher already restricts normal masked publication to the
standard masked layout. The correction belongs to the masked shadow fragment
shader: match visibility's discard and decode-out-of-bounds diagnostic for
both an invalid dense index and an invalid standard-material record. Coverage
evaluation and publication admission remain unchanged.

Rejecting checks: an isolated temporary layout-hash corruption must execute
during a dirty shadow refresh, produce the decode diagnostic, and discard
the affected masked depth. Remove the corruption before normal validation;
normal Sponza must recover with zero decode errors and pass the existing
floor image tolerance (mean RGB difference <= 0.5/255, at most 0.1% pixels
above channel difference 16). The implementation introduces no managed
allocation, cache, or resource lifetime. Only one implementation item is active.

The isolated invalid-layout capture confirms execution: at events 1658/1659,
the masked draw increments decode-out-of-bounds from 4,692 to 5,084 while
the entire depth image remains byte-identical. The pass ends at 10,013 decode
errors. Its atlas PNG was exported and viewed. Earlier stationary captures
did not execute the shadow pass; a reload-only probe retained the previous
shader and produced zero decode errors. Those are excluded. A process restart
loaded the temporary source probe. The probe was removed and the process
restarted before normal validation.

Normal Sponza reports zero active-list/decode overflow, renders 396 scene
commands, and passes comparison against the corrected generic reference:
floor mean RGB difference 0.303/255, 0.098% pixels above difference 16;
atrium 0.008/255 and 0.005%. Both PNGs were viewed. This establishes defensive
eligibility consistency, not arbitrary authored-shader coverage equivalence.
The Release build succeeded with zero warnings/errors. No tests were changed.

## Atlas page admission

Hypothesis: deferring different atlas page framebuffers into one accepted
visibility family reaches the backend's shared target-closure guard, rejects
fresh frames, and repeatedly holds the lane. The atlas manager owns the early
decision. Select one framebuffer reference per render frame only after a
request builds successfully; decline other page targets before reserving a
pending slot so the existing generic path renders them immediately. Preserve
the backend guard. Reset selection on the render thread when the submission
frame changes, never on group completion or the planning thread.

Rejecting checks: same-page groups remain eligible; distinct pages both
render, one through the lane and one generically, without page-mismatch
rejections or hold; the previously declined page can become the selected page
in a later frame. Repeat refreshes and shader reload after warmup. Budget:
one managed framebuffer reference and one frame ID per atlas manager, no
per-frame allocation or additional native object. Only this implementation
item is active after the eligibility correction above.

Release build: zero warnings/errors. Read-only source review found no blocker.
The live fixture duplicates the four-cascade sun, allows two 4096-square pages,
removes the time budget, and uses a 32-tile budget. Tile limits of 1024 put
both groups on page 0; 2048 forces pages 0 and 1. These are session-only
settings applied through the existing object-property MCP tool.

Shared-page motion adds 874 accepted lane groups with unchanged rejected,
unconsumed, generic and hold counts. Distinct-page warm motion adds 625
accepted lane groups and 75 generic groups; both lights retain four resident
cascades and advance their rendered frame IDs. No rejected/failed frame or
lane rejection/hold/unconsumed growth occurs in that warm window. The first
motion window had three cold mesh-preparation retries (not page-closure
failures) and is excluded from warm acceptance. Atlas reconfiguration itself
also incurs cold admission and one unconsumed group before settling.

With a stationary camera, rotating only the page-1 light advances its shadow
frame from 30,835 to 31,541 and then 31,926 on restoration; page 0 remains at
30,835. Accepted groups advance by four, with no generic/rejected/unconsumed
growth. This proves page selection is not pinned across frames. The floor
PNG was captured and viewed after both pages rendered.

One shader reload/motion cycle recovers. Its subsequent warm distinct-page
run adds 543 accepted lane groups and 99 generic groups, with 1,131 fresh
completed frames in the motion window, no rejected/failed frames, unchanged
lane rejection/unconsumed/hold counts, and no terminal disposition. The
bounded early decline therefore replaces recurring target-closure rejection.
No tests were added or modified.

## Retained implementation lifecycle validation

The single-light fixture was restored by restarting the owned session. Three
play entry/exit round trips report no transition error. Motion fresh FPS is
104.4 before play and 110.6, 112.8, 114.1 after the three exits. Device-local
memory is 1083.6 MB before play and 1505.2 MB after each exit: first-use
retention stabilizes, with no growth from the first through third round trip.
Temporal validation remains separate and open until the TSR sequence checks
complete. Evidence: `reports/retained-play-roundtrips.json` in the run root.

## Evidence

Evidence root (ignored, disposable): `Build/_AgentValidation/20261003-142654-vk-todo/`
(`reports/`, `mcp-captures/`, `logs/`). Required conclusions are recorded here.

# Skinned GPU chain benchmark validation

## Objective

Validate `Physics Chain GPU Dispatcher Skinned Mesh Test` in the Math
Intersections world. Verify visible mesh deformation, GPU palette motion,
zero simulation readback, and repeated benchmark start and stop. Measure
scaling before choosing performance changes.

## Current bottleneck evidence

Last checked: 2026-10-07 23:23 UTC. The [valid scope timeline](#valid-2000-chain-scope-timeline)
supports overlapping render/collection work followed by serial swap as the
observed critical cycle. It does not prove CPU-on-core costs. Hiding the whole
chain rendering path in three cycles reached 52.0–58.9 Hz, against
16.3–19.2 Hz for the full scene. This includes collection, publication,
deformation, and drawing; it is not a raster-only result. Frozen physics
varied from about 13 to 25 Hz, so no quantitative physics benefit is established.
See the [second ablation cycle](#second-ablation-cycle-and-measurement-limits)
for timing limits. The [engine-side cycle 3 measurement](#engine-side-ablation-meter-first-results)
records full and frozen results. The [third hidden result and restoration](#third-hidden-result-and-ablation-restoration)
complete the three-mode set.
The [frozen timeline](#frozen-physics-scope-timeline-comparison) still shows
render work followed by serial swap after world update cost falls sharply.

The 100 Hz target remains unmet. Grouped GPU indexed-instance draws already
exist and have native validation. The next structural candidate is to separate
stable scene registration from pose/bounds publication in `ScenePlan`, whose
diagnostic residual averaged about 7 ms per call. It is not an implemented
fix. The collider proof remains deferred; the cache's gathering-stage gain
does not establish an end-to-end frame-rate gain.
Stable registration and per-draw/group templates are the next structural
priority. The owned editor is stopped; all 40 existing targeted tests passed
on final source. See the [final validation and allocation exclusion](#final-validation-and-scoped-allocation-exclusion).

## Configuration and evidence

- Named isolated editor session: `skinned-chain`.
- Build: Release. Initial saved rendering settings: Vulkan, Advanced pipeline.
- Evidence root: `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261006-110030-skinned-chain/`.
- The retention script failed on an unreadable metadata folder in an older,
  stopped session. Automatic approval review rejected manual task-folder
  cleanup. Keep evidence inside the named session root. Do not add another
  immediate task directory or repair the unrelated folder.
- No test files changed. Live feature validation comes first.

## Acceptance checks

- Inspect the source rig at multiple times and camera positions.
- Verify GPU simulation, batched dispatch, GPU skinning, and disabled CPU bone
  synchronization from live state.
- Inspect a small grid before and during three benchmark cycles. Check the
  restored source rig after each cycle. Include automatic completion, manual
  cancellation, and an immediate restart.
- Compare settled runs at increasing chain counts. Keep visual captures
  separate from accepted timing windows.
- Inspect simulation readback counters, dispatch failures, palette allocation,
  frame time, and teardown completion.

## Initial code review

A bounded broker review completed with requested and actual model
`gpt-6-astra`. These are code-based candidates, not measured bottlenecks:

1. Per-chain CPU input preparation, registry scans, and small buffer uploads.
2. Per-renderer palette binding collection and publication. Check unexpected
   mapping rebuilds and partial-palette copy counts.
3. Active-work generation, solver dispatch, bounds, palette passes, and their
   barriers. The batched solver dispatches per bucket, not per chain.

Repeated-run checks must distinguish retained capacity from live allocations.
The benchmark reports tick intervals, not GPU execution time. Its destroy
duration measures teardown initiation, not the full deferred teardown. Its
settle check does not establish shader compilation or palette readiness.

## Live results and repairs

The initial Release build passed with zero warnings and errors. The live
chain reported an unavailable backend. The dispatcher had no registrations,
no dispatches, and no transfer bytes. A first viewport capture was black.
After warmup, a composited capture showed a straight mesh with excessive
exposure. Fixed exposure improved inspection, but six later frames showed no
deformation.

The rendering bridge had no production installation call. A diagnostic live
installation plus component reactivation registered one chain. Startup now
installs the bridge before world activation and retains its restoration lease
through component shutdown.

The next Release build also passed with zero warnings and errors. Its live
backend was Vulkan and ready. One simulation attempt failed at
`GpuBoundsPublication`; no dispatch group committed. The failure count stayed
at one while rendered frames and component submissions continued to advance.
Shader readiness checks incorrectly treated `LinkReady` as an in-progress
flag. It means that linking is allowed. After backend invalidation, the checks
never retried linking. The dispatcher now uses its shared readiness helper.

Review against the pre-split implementation found further input regressions:

- Per-frame particle versions caused stale CPU state to replace GPU state.
- Constant transform and collider signatures prevented moving input updates.
- The GPU path ignored the resolved simulation step count and time scale.
- Palette mappings used a parent's rest direction instead of its child's.
- Palette registrations were not released during rebuild or removal.
- Palette bindings omitted the particle reset version and a rebuild generation.

These contracts passed the later repeated live check below. The benchmark's
tick samples remain separate from render FPS and GPU timing.

The next live run reached `GpuBoundsPublication.WorkItemBufferReadiness` on
every frame. Buffer diagnostics showed no generated API object and zero
uploaded bytes. The Vulkan retained lookup cannot create wrappers, but the
compute readiness facade used it before the first bind. The facade now creates
the wrapper before it checks readiness. The bounds atlas also uses GPU-only
storage without a CPU mirror. The later live checks below confirmed nine-copy
arena growth and dispatch.

After the wrapper repair, dispatch groups committed with no reported failure.
The palette contained one renderer and seven bone mappings. Physics readback
bytes and readback submissions stayed at zero. The viewport still showed a
thin, stationary strip. RenderDoc confirmed active-work reset, compaction,
finalization, bounds, bounds-copy, and palette passes. The solver indirect
dispatch was absent. Particle positions remained at the initial pose.

The user requested fixed ambient light for inspection. The Math Intersections
world now sets `AmbientLightColor` to `(0.15, 0.15, 0.15)` and intensity to `1.0`.
No dynamic GI feature was added.

The user later reported that the three faces outside direct light were still
black. The setting alone did not reach Advanced native opaque lighting. The
global capture did not include ambient, the publisher did not publish an
environment row, and the native shader did not read ambient from that table.
With directional intensity set to zero, a viewed baseline capture showed a
black mesh despite ambient 0.15. This supersedes the earlier ambient visibility
assumption. The repair connects world-swap capture, environment publication,
and native diffuse shading. Ambient-only changes must prevent publication
reuse. See [ambient validation](../../testing/rendering/advanced-world-ambient.md)
for the check results and coverage limits.

The ambient repair built with zero warnings and zero errors. A first live launch
stopped in `AdvancedGpuScenePublisher.TryAddRegistration` after about 11 seconds.
The message was `Canonical resident tables exhausted their preflighted
frame-boundary capacity.` This message also covers invalid material, geometry,
and registration lookup failures. It does not identify a capacity cause.
Review found no direct coupling between the new environment row and these
separate scene tables. The same binaries passed the ambient checks after a
restart. The first fault remains unexplained; do not treat the restart as a
repair. If it repeats, add failure-only diagnostics at the first failed check,
with captured and current mesh revisions and counts, before changing capacity.
The fault is in `reports/ambient-new-time.json`; the healthy retry and captures
use the `ambient-retry-*` and `ambient-fixed-*` report names in the existing
evidence run.

The live ambient checks showed visible faces with direct light at zero, black
faces with ambient intensity at zero, blue and green responses to color edits,
and a darker response to intensity 0.1. Opposite camera views showed both sides
of the deformed mesh. Neutral ambient and directional intensity were restored.

The first capture used RenderDoc 1.44, but the installed replay module supports
1.41. A session-only layer override selected the installed 1.41 capture module.
The compatible capture opened and the exported output was inspected. The replay
session was closed. Diagnostic capture time is excluded from benchmarks.

The rendering adapter also has a pre-existing per-source palette publication
stub. Mixed dispatch groups need separate review. The requested test uses one
batched group; do not claim mixed-group validation from that test.

The live frame trace placed direct physics dispatches in `Advanced.Deformation`
(100002), but placed indirect dispatches and ordered barriers in graphics pass
5. The ordered API passed free-form diagnostic labels to the fallback pass
resolver. The resolver used the word `Compute` to select its stage. The ordered
API now supplies fixed compute operation names. Explicit valid pass indices
still take precedence. The scheduler preserves the authored order within the
selected pass. Later capture evidence verified the indirect solver, bounds, palette, and ordered aggregate copy.

A nine-copy run exposed the same cold-wrapper issue in arena growth copies.
The Vulkan buffer-copy facade now creates both wrappers before its retained
lookup. The next live run grew the arena and registered all nine palettes.

The benchmark toggle cleared itself during startup cleanup. The controller now
sets it from the final running state. Source restoration then exposed an
animation binding failure for the read-only `AnimationClipComponent.SceneNode`
navigation member. The controller now records the exception in its status.
The later three-cycle validation verified repeated start, cancellation, automatic completion, and source restoration.

Typed getters now bind the `SceneNode -> Transform` navigation path. A typed
float setter binds `TranslationY`. After a nine-copy cancellation, the source
returned to one registered chain, both pending teardown fields cleared, and
the benchmark toggle stayed off. Later cycles verified visible GPU-skinned restoration.

The next compatible GPU capture contains `vkCmdDispatchIndirect` before bounds
and palette generation. All seven particle positions are finite and coherent.
Palette entries map bind centers to particle positions with an error below
`3.4e-7`. This verifies the solver and palette producer in that captured frame.

The bounds shader used an 80-byte particle stride because it declared a `vec3`
padding field after `IsColliding`. The CPU, solver, and palette use 64 bytes.
The wrong stride explains the extreme bounds in the capture. Three scalar
padding fields now preserve the 64-byte layout.

The Advanced deformation consumer copied external palettes from their CPU
mirror, then uploaded that stale seed into its aggregate palette. It now keeps
external ranges for ordered GPU copies after CPU uploads and before deformation.
Adjacent source and destination ranges share one copy. Accepted partial work
keeps the frame slot fenced; rejected work cannot publish a valid output. The
OpenGL renderer also implements the generic GPU-copy operation used by this
path. The later live GPU-copy capture below verified this path.

One cold Vulkan launch exhausted descriptor preparation recovery before the
viewport submitted a frame. A later launch recovered and rendered. Compute
preparation now retains its specific failure reason. This startup path remains
separate from the accepted steady-state checks below.

## Accepted live validation

The primary coordinator visually checked the nine-copy scene before, during,
and after three benchmark cycles. The skinned mesh moved and deformed on the
GPU. Each cycle returned the source scene to one chain, one palette binding,
and seven bone mappings. Physics simulation readbacks remained at zero.

- Cycle 1 completed automatically after 30 seconds with 1,801 samples.
- Cycle 2 stopped by manual cancellation with 472 samples.
- Cycle 3 completed automatically after 20 seconds with 1,200 samples.

The capture `renderdoc/gpu-palette-copy_frame1834.rdc` verified solver, bounds, palette, and aggregate passes at events 52, 61, 79, and 100. It showed finite 64-byte particle records and non-identity palette matrices. This confirms the ordered GPU copy and visible skinned motion for this single-group scenario. It does not validate mixed dispatch groups.

Clean Release Vulkan Advanced runs produced these historical render measurements. The old MCP `invoke_method` used `Direct` worker-thread dispatch; clone concurrency may have changed test setup. Treat these values as setup-uncertain, not as a controlled baseline.

| Scene | Render FPS | Mean sampled GPU time |
|---|---:|---:|
| Source | 182.23 | 3.877 ms |
| 100 copies | 152.42 | 5.403 ms |
| 1,000 copies | 11.96 | 83.893 ms |

These values come from the render summary reports, not benchmark UI tick
samples. The 2,000-copy restart associated with these earlier measurements
produced zero rendered frames. Its timer report identified a terminal
collection fault: `InvalidOperationException: Collection was modified` in
`GPUScene.Remove` while processing pending renderable operations.

## Lifecycle repairs and further validation

The collection fault came from disposing the producer list while
`GPUScene.Remove` enumerated it. Registration now uses owned maps. The timer
also records `FirstTerminalFault` and stops the engine after a terminal loop
fault, instead of leaving a stale frame state that looked like a GPU hang.

A separate direct-publication failure at 1,000 copies came from a reclaim race
between active publication and lease acknowledgement. A guard and leaf-level
diagnostic now cover that race. The source change passed its final live validation in the 1,000-copy run. A duplicate-key exception in the profiling dictionary during the
2,000-copy clone path was fixed with an atomic lazy `ConcurrentDictionary`.
The final build and 2,000-copy clone run verified this repair.

In a PID 33696 run with the repaired removal path, the 100-to-1,000-copy ramp
completed. These direct-dispatch measurements are historical only. The old
MCP `invoke_method` path used `Direct` dispatch from its worker thread. Clone
concurrency may have corrupted scene setup. Do not use those results as a
controlled performance baseline.

The 1,000-copy render summaries reported:

| View | Render FPS | Mean sampled GPU time |
|---|---:|---:|
| Visible | 26.346 | 26.771 ms |
| Offscreen | 28.684 | 9.478 ms |

The 2,000-copy lifecycle completed automatically after 120.04 seconds with
2,770 update samples. It restored the source to one chain, and the timer was
running with no terminal fault. The visible render summary reported 3.367 FPS
and 297.908 ms mean sampled GPU time. This is a valid observation, but it is
not a controlled speedup comparison with earlier runs. Run variation remains
unresolved.

Profiling used the CPU profiler and coarse GPU timing. RenderDoc, validation,
and dense profiling were off during measurements. Dense diagnostics were
collected separately over three Advanced scopes and averaged 3.222 ms. They
miss global physics, copies, and shadows, so they are not full GPU time. The
active device was an NVIDIA GeForce RTX 4070 Laptop GPU with an Intel Core
Ultra 9 185H CPU. The render was 1920x1080 with a 1286x723 internal viewport.

## Optimization investigation

The captured frame identifies the early visibility append as the first
optimization target. The next candidates are solver memory access, CPU chain
collection and input updates, and visibility-based deformation budgets.
Keep simulation and bone palettes on the GPU. Do not add a CPU readback to
decide which chains can run.

The active device and CPU identify the test machine only. They do not explain
results on other hardware. Repeat the complete-population measurements after
each performance change.

## Main-thread measurements

The current benchmark harness sets `sessionMcpDispatchMode` to `MainThread`
before scene mutations that use ImGui. Treat the earlier direct-dispatch runs
as setup-uncertain. The accepted main-thread measurements below use complete
job populations, except where marked invalid.

| Run | Population and view | Render FPS | Mean sampled GPU time | Status |
|---|---|---:|---:|---|
| 1,000, full | 104,000 vertices, visible | 11.721 | 88.078 ms | Accepted |
| 1,000, no shadows | Visible | 11.509 | 86.620 ms | Accepted |
| 2,000, full | 208,000 vertices, 14,000 bone mappings, visible | 3.706 | 267.911 ms | Accepted |
| 2,000, full | Offscreen, all jobs still admitted | 10.681 | 18.354 ms | Accepted |
| 2,000, partial | 1,027 jobs, no CPU profiler | 9.72 | 103 ms | Invalid |

The no-shadow run had zero light casters; cascade far was 200 and camera far
was 650. Its small change from the full 1,000-copy run makes shadows unlikely
to be the primary cost in this view. The 1,000-copy offscreen run stopped when
the population changed, so it has no accepted measurement.

These complete-population MainThread values are the pre-patch baseline.

The 2,000-copy frame-operations capture combined GPU copies into one operation
with 18 barriers. Main-thread admission and scene evidence is in
`reports/main2000-repeat-scene.json`. Physics simulation readbacks stayed at
zero; this does not describe screenshots or profiling readback.
This rules out one copy per chain for that capture. A separate main-thread
stop/start test created 2,000 copies. `GPUScene.AdvancedPublicationRejected`
was true. The failure came from a full material-variant registry. The exact
old-plus-incoming material acquire-before-release fix passed the final
repeat checks below.

The final 2,000-copy run without the CPU profiler reported 9.72 FPS and 103 ms
GPU time, but published only 1,027 of 2,000 jobs. Do not use it as a speedup
result. New `Advanced.Visibility.RasterEarly` and
`Advanced.Visibility.RasterLate` GPU timers cover missing raster timing.
Their diagnostic means were 0.682 ms and 0.005 ms. These timer readings do
not include the early visibility append measured in the RenderDoc capture.
Mesh raster work is not the main cost in this view.

## Captured GPU bottleneck

The full 2,000-chain capture `renderdoc/scale2000_frame1148.rdc` identifies
event 149, `Advanced.Visibility.Early`, as the main GPU cost at 266.570 ms.
Event 55, `PhysicsChain.IndirectDispatch`, took 11.672 ms. These are RenderDoc
replay timings, not clean live frame-rate measurements.

The early visibility shader used a global compare-and-swap retry loop for
each visible candidate. Visible candidates contended on one counter.
Offscreen candidates did not append. This explains the visibility-dependent
cost and provides a specific cause to validate.

The replacement uses one atomic reservation per append. Each view resets its
counters. Candidate admission is bounded by `payloadCapacity`, and each
candidate appends at most once. Output bounds checks remain. The final live
measurements confirm a large reduction in GPU time.

The material registry reserve cleared the first restart rejection. The next
2,000-chain restart exposed the same acquire-before-release peak in canonical
scene tables. The scene reserve now counts planned adds, updates, and
tombstones, including retired rows. A later 1,000-to-2,000 transition exposed
a draw-only growth check that skipped texture and sampler storage after
selective scene growth. Boundary growth now checks all 29 profile fields
before it grows the matching registries. Independent review found no blocker
in these capacity and visibility changes.

## Final live results

Release Vulkan Advanced, the same wide camera view, CPU profiling off,
RenderDoc off, validation off, dense timing off, and coarse GPU timing on:

| Active chains | Before FPS | After FPS | Before GPU time | After GPU time |
|---|---:|---:|---:|---:|
| 1,000 | 11.721 | 23.689 | 88.078 ms | 10.556 ms |
| 2,000, first final run | 3.706 | 11.650 | 267.911 ms | 24.251 ms |
| 2,000, third final run | 3.706 | 12.795 | 267.911 ms | 21.419 ms |

The 1,000-chain settled window rendered 272 frames in 11.482 seconds. The
2,000-chain windows rendered 206 frames in 17.683 seconds and 231 frames in
18.054 seconds. Each measurement used the full admitted population: 104,000
or 208,000 vertices. These are observed results on this laptop, not a
guaranteed frame rate or a 60 FPS result. Screenshot work was outside the
accepted timing windows.

A later source-only window measured 32.333 FPS and 5.009 ms GPU time after
teardown. A fresh launch of the same build and camera measured 22.477 FPS
and 9.959 ms. Thus the late idle-rate comparison does not prove a retained
benchmark-resource regression. The one-chain primary recording time varied
from 4.00 to 75.08 ms. The inspected recording and preparation loops use
current operation counts. Keep this run variation separate from the measured
visibility bottleneck and do not promise the table's frame rates on later runs.

Three final 2,000-chain starts passed in the same editor process. Each run
had 2,000 admitted jobs, 2,000 live palette bindings, 14,000 bone mappings,
and no publication rejection. Publication sequences advanced from 155 to
158 to 209. The third topology generation was 10,006, which confirms that
replacement work was published. Physics readback enqueue attempts and
submissions remained zero. The timer remained running without a terminal
fault. After the third stop, deferred teardown completed and the source
returned to one chain and one palette binding.

The final nine-chain visual checks cover automatic completion, manual stop,
and a third start from another camera position. Captures show all nine meshes
deforming. Earlier RenderDoc evidence verifies actual GPU particle and bone
palette writes, rather than only CPU root motion. Sequence screenshots use
diagnostic readback; that is separate from the zero-readback physics path.

The first final nine-chain cycle completed with 1,000 update samples in
16.83 seconds. The second was cancelled with 3,505 samples. The third
completed with 1,000 samples in 16.67 seconds. All toggles and deferred
teardown fields cleared. The final source had one chain, one palette binding,
seven mappings, and no timer fault. The benchmark's update samples do not
measure rendered FPS.

Two later screenshot sequences stopped at the bounded capture queue. Each
saved five valid frames, which were viewed. A separate final screenshot
confirmed the restored source. These are partial sequence captures, not
successful full-sequence API checks. They do not indicate physics readback
or a stopped simulation.

The final build completed with zero warnings and zero errors. No unit-test
files were added or changed. The new OpenGL buffer-copy implementation was
built but was not validated in a live OpenGL session. Mixed physics dispatch
groups remain outside this scenario's coverage.

Two intermediate cold launches lost their engine loop threads. Their mini
dump proved that the loops had exited, but did not retain the initial fault.
A later scale transition produced the resource-capacity exception described
above. Do not assume that it explains the earlier launches. The existing
`get_time_state` tool now runs on the caller thread so that it can report a
terminal fault when the app/update queue no longer runs.

## Next measured optimizations

1. Measure CPU preparation stages separately. The GPU path still builds six
   lists and hashes matrices, then the rendering bridge converts and copies
   them before the dispatcher applies version checks. Cache static particles,
   tree topology, and bone mappings at their existing version boundaries.
   Skip CPU job snapshots on a GPU-only path. Do not parallelize mutable
   dispatcher work without a separate publication step.
2. Check the solver arena's actual Vulkan memory type. `DynamicDraw` requests
   host-visible/coherent memory in the current backend. Compare a device-local
   arena with ordered seed and growth copies. Requested flags alone do not
   prove the selected physical memory type. Do not discard initial particle
   data by marking the arena GPU-produced without an upload path.
3. Test smaller solver workgroups and cache the prior parent for short linear
   chains. The current 128-thread group creates only 16 groups for 2,000 trees.
   Matching particles in adjacent seven-particle chains are typically 512
   bytes apart. Compare a layout that puts matching fields close together.
   Preserve serial parent-before-child constraints within each chain.
4. Add GPU-owned simulation and deformation budgets for distant or offscreen
   chains. The offscreen experiment still admitted every deformation job.
   Preserve conservative bounds and temporal history when work resumes.

Accept each change only after repeat-run correctness, full-population checks,
GPU pass timings, render frame intervals, and warm allocation measurements.

## User feedback

The user set a new minimum of 100 Hz with 2,000 active visible chains. The next
work compares versioned CPU input copies, particle arena memory placement, and
short-chain solver workgroup sizes. Render frame intervals, GPU time, full job
counts, animation, and repeated start/stop behavior are required evidence.
Update sample counts do not establish this target.

The first new 2,000-chain start stopped during canonical reverse-dependency
capture (`reports/opt-baseline2000-time.json`). It is not a valid performance
baseline. The manifest does not read environment records, so an ambient edge
count was ruled out. Failure-only leaf diagnostics now distinguish stale
handles, invalid bindings, and insufficient edge storage before a repair.

The first input-cache build ran all 2,000 chains with 2,000 palette bindings,
2,000 deformation jobs, 208,000 vertices, and zero physics readback. A viewed
wide camera capture showed the population. A 34.83-second runtime profile
recorded 103 samples and 2.93 completed frames per second. Mean frame work was
298.41 ms; mean collect work was 67.56 ms. This result fails the 100 Hz target.
The static upload counter reached 406,789,320 bytes during the run. Tree rest
gravity incorrectly shared the particle-template content version. The repair
gives tree data a separate version through Core, bridge, and dispatcher.
Changing rest gravity updates dynamic headers without uploading templates.
Child local offsets remain valid because `Prepare` uses `InitLocalPosition`;
the setup operation that changes it also changes `_particlesVersion`.

The next Release build passed with zero warnings and zero errors. Its first
2,000-chain start failed before a measurement window. The new diagnostic was
`Draw 8:1 at physical row 7 has a stale material handle 8:1`, sequence 81.
The material and geometry edge arrays each had capacity 2,048 and only seven
written edges. This rules out edge capacity for this failure. Evidence:
`reports/opt-treecache-fault-check.json` under the session evidence root.

Code inspection also found a bounds routing cost of O(chains * scene commands):
each palette binding called a helper that scanned all command registrations.
At this population that is about four million entries per frame. A separate
repair captures renderer-to-command routing once per scene into reusable
storage. These repairs require new runtime measurements before acceptance.

The readback bridge now shares one adapter per physics world. The previous
adapter identity was per chain, which serviced the same world once per chain
and retained old adapters across benchmark restarts. A tracked world now
retires after its last source leaves and pending requests and staging slots
finish. This does not enable physics readback.

The publisher also preserved old slot stamps when growing storage and
resetting the generation. The next plan could treat an old material request
as current, omit its retain, and then retire a material used by a live draw.
Growth now clears the plan, registration, light, and probe stamps before
resetting their generations. A later cold launch failed in registration with
the old generic capacity message. Registration diagnostics now identify its
first failed guard or mutation; do not assume this second fault has the same
cause. A no-build retry ran successfully.

With the CPU changes and host-visible particle storage, an exploratory window
gave 10.30 Hz and 15.91 ms mean GPU time. Its last samples had CPU profiling
enabled, so it is not an accepted matched performance window.

The first device-local, 128-thread run kept 2,000 chains before and after
the capture. The backend reported a 1 MiB particle arena with resolved route
`DeviceLocal`. Over 34.97 seconds, 413 samples gave 11.78 completed frames per
second. Frame interval p50/p95/p99 was 84.03/97.51/103.09 ms. Mean GPU time was
7.44 ms; mean collect time was 53.03 ms; mean primary recording time was
28.89 ms. Every frame interval exceeded 10 ms. No CPU profiler logging was
enabled in this window. Completion lag stayed at one frame. This result fails
the requested 100 Hz target. Evidence uses the `opt-local128` report prefix.
This is development diagnostic timing. The capture metadata marks the editor
UI, command labels, and verbose logging as intrusive and declares the window
unsuitable for the engine's strict clean-comparison policy. Runtime capture
also allocates and writes telemetry. A separate low-frequency sample must
check throughput without that capture observer.

The particle arena uses `StaticCopy`, while its CPU seed/reset storage and
ordered GPU growth copies remain active. Dynamic transform and header arenas
retain their upload policy. The short-linear solver and GPU indirect argument
builder now share one shader group-size constant, so size experiments cannot
leave a partial population undispatched.

The 64-thread development window (`opt-local64-retry`) kept 2,000 chains and
gave 11.55 Hz, 18.05 ms mean GPU time, and 120.94 ms p95 frame interval. A later
hardware sample during the same active run reported P5, 435 MHz core, 810 MHz
memory, 57 C, and 19 percent GPU use. This CPU-limited workload permits a low
GPU clock. The earlier window did not record clocks. These single windows do
not establish a winning workgroup size.

The precise cold-start failure showed `InvalidGeometrySource` with planned
revision 317 and current revision 320. `RebuildPhysicsChainSkinnedBoxVisual`
destroyed the attached old mesh before assigning its replacement. Destroying
attribute buffers changed the geometry while canonical publication packed it.
The repair assigns the replacement first and stages required geometry append
data before `TryBeginPublication`. Nonblocking buffer read scopes and matching
revision, payload, and count checks protect each copy. Registration consumes
the retained numeric staging after all source locks are released. Stable
geometry is not recopied. The read-only review passed; live cold-start and
repeat-run validation are still required.

Another source audit found O(headers squared) range ownership in
`VulkanPreparedStableBinStream.TrySealSubmissionPlans`. A retained owner array
indexed by the resolved indirect range removes about two million comparisons
per frame at 2,000 unique ranges. CPU-direct headers still seal independently;
only later GPU headers for an already owned range skip duplicate plans.

The user requested fixed ambient light and repeated GPU-skinned validation.
The world uses its existing ambient color and intensity settings, without
dynamic GI. The accepted runtime results above are agent-observed. The user
has not yet reported whether the final appearance meets their preference.

## CPU preparation and repeated-run follow-up

The 32-thread build also contains geometry lifetime protection and linear
Vulkan range ownership. Its first cold launch and 2,000-chain start completed.
The low-frequency observer recorded 445 rendered frames in 34.327 seconds:
12.964 Hz. Sampled GPU time averaged 17.77 ms. All 2,000 deformation jobs and
208,000 vertices remained admitted; physics readback counters stayed zero.
This does not meet 100 Hz. Evidence prefix: `opt-local32-ranges`.

Three starts and stops restored one source chain, one palette slice, and one
renderer binding. A transient teardown query preceded render-thread cleanup;
a later query confirmed cleanup. Close views in the second run showed changing
mesh curvature. The third run included one rejected render sample and 7.65 Hz;
do not use it as a clean comparison. The second window changed camera during
sampling and is functional evidence only. Its measured throughput was 11.71 Hz.

Cumulative timings in the first window identified `PhysicsChainWorld.LateTick`
at 22.15 ms per call, `FixedTick` at 7.87 ms, mesh-command updates at 8.97 ms per
rendered family, and advanced plan preparation at 6.96 ms per family. Tick times
include lock waits and must not be summed as CPU use. Bin sealing contributes
3.82 ms inside plan preparation. Raster program lookup and pipeline factory
work already run once per family and together cost only 0.04 ms.

The next build removes empty parallel CPU preparation from an all-GPU world.
Opt-in late-tick counters separate gate wait, preparation, GPU input packing,
bridge dispatch, and publication. It also publishes a skinned render command
once after matrix and bounds changes, instead of publishing an intermediate
snapshot first. GPU bounds alone cannot replace the CPU proxy bounds: canonical
frustum candidates and shadow intersection still consume those proxies.

Root-motion callbacks allocated exactly 240,000 bytes per call for 2,000
transforms. SceneNode subscribed to all property changes but used only Parent
and World. Filtered internal subscriptions now allow XRBase to skip argument
allocation for ignored properties. Public listeners retain their order,
cancellation, and fresh notification argument lifetime. Live validation must
confirm that the root-motion allocation disappears.

The next Release build (`opt-cpu2`, process 50160) passed with zero warnings and
zero errors. It kept 2,000 chains during 75.433 seconds and completed 921 render
frames (12.210 Hz). No sampled terminal outcome was rejected. Source cleanup
again returned to one chain and one palette binding. This still fails 100 Hz.

The single-swap change reduced mesh updates per family from 6,743 to 3,924.
Unchanged updates fell from 3,384 to 0.13. Mesh-update time changed from 8.97 to
7.96 ms, while update/render cadence changed. No whole-frame improvement is
established. Late-tick gate wait was only 0.0195 ms; body time was 24.048 ms.
Component preparation used 18.023 ms, including 12.472 ms in GPU Prepare,
1.036 ms input packing, and 1.399 ms bridge dispatch. Boundary and diagnostics
used 2.888 and 3.134 ms respectively.

The first property filter reduced root-motion allocation from 240 KB to
112 KB per callback. Undo's unfiltered changed listener accounts for the
remaining 56 bytes per transform. The next build filters that listener while
retaining SceneNode.Transform tracking outside recording. It also reuses
successfully bound renderer subscriptions on unchanged assignments. Mutation
events and failed-refresh retries remain active. Source review passed for
both changes. More detailed late-tick counters will split the preparation,
quality-budget, and activity-scan costs before further changes.

The next build passed with zero warnings and zero errors in 2 minutes 21 seconds.
Process 37944 exited after initial editor startup and before benchmark setup.
The MCP connection closed. Available logs and the Application event log did
not provide a cause; do not attribute this exit to a code change. A no-build
retry uses the same binary as `opt-cpu3-retry` (process 32720). The additional
world collection-stage counters in source are not in that binary.

`opt-cpu3-retry` confirmed zero root-motion allocation across 1,468 callbacks.
Its 61.942-second render window completed 595 frames (9.606 Hz) but sampled
five rejected frames. The retained readiness diagnostic identifies
`MeshMaterialization` / `visible-mesh-cold-admission`: native preparation yielded
before publishing a partial scene. These are startup readiness retries, not
proof of a steady solver failure. The timing helper now waits for 30 completed
frames with unchanged deferred/rejected/failed counters before measurement.
The automatic benchmark stop restored the single source chain and binding.

Detailed late-tick timing was 25.438 ms body, 0.023 ms gate wait, 20.921 ms
component preparation, 3.174 ms quality budgeting, and 1.332 ms activity scan.
Inside GPU preparation, hierarchy recalculation used 10.185 ms, rest-pose
setters 1.283 ms, and particle transform reads 0.335 ms. Unchanged rest-pose
setters already return before invalidation; bypassing them is not justified.
Animated roots require current child matrices before dispatch. A read-only
architecture review is examining batching without stale poses or callbacks
under a global hierarchy lock.

The next build avoids fixed-tier work estimates and unchanged quality-state
writes. It adds opt-in world collection/swap stage counters to distinguish
matrix publication, mesh updates, and scene work. This diagnostic build still
requires a warm runtime window and cannot establish the 100 Hz acceptance.

The `opt-cpu4-warm` window completed 377 rendered frames in 41.668 seconds,
or 9.0477 Hz. This remains below the 100 Hz target. The sampled GPU mean was
27.409 ms and the sampled whole-frame mean was 42.333 ms. All 31 render samples
kept rejected outcomes at 116, deferred outcomes at 84, and failed outcomes at
zero. Completed outcomes increased from 102 to 480. Thus, no new rejection was
sampled in this warm window. The samples do not erase the earlier cold-start
readiness rejections.

The opt-in late-tick counters advanced by 880 calls. Delta time per call was
25.635 ms in the body and 0.022 ms at the tick gate. Component preparation used
23.049 ms, including 16.412 ms in GPU preparation, 1.397 ms in input packing,
and 1.578 ms in bridge dispatch. Inside GPU preparation, hierarchy evaluation
used 11.419 ms, rest-pose work used 1.327 ms, and particle transform reads used
0.537 ms. Boundary work used 1.211 ms, including 1.210 ms in quality budgeting.
Diagnostics used 1.364 ms, including 1.362 ms in the activity scan. These are
nested counters; do not sum them as independent costs.

The collection counters advanced by 401 calls. Per collect call, matrix work
used 13.233 ms, mesh work used 13.056 ms, and scene work used 6.584 ms. The
swap counters advanced by 400 calls. Per swap call, matrix work used 11.015 ms,
mesh work used 16.797 ms, and scene work used 17.773 ms. These call counts
differ from the 377 rendered frames, so the stage means are not frame totals.
Evidence: `reports/opt-cpu4-warm-render-summary.json`,
`reports/opt-cpu4-warm-render-samples.json`, and the matching
`opt-cpu4-warm-{late,collection}-{before,after}.json` counter snapshots.

The `opt-cpu5-warm` window kept 2,000 chains and completed 386 rendered frames
in 38.033 seconds, or 10.149 Hz. It still misses 100 Hz. Sampled GPU and
whole-frame means were 11.022 and 38.008 ms. All 31 render samples kept
rejected outcomes at 152, deferred outcomes at 82, and failed outcomes at zero.
Completed outcomes increased from 263 to 650. No new rejection was sampled
inside this window. The wide view was inspected. An initial tool call paused
for about 14 minutes before warmup; that pause was outside the timed window,
and its cause is unknown.

Late-tick counters advanced by 888 calls. Delta time per call was 25.116 ms in
the body, 0.054 ms at the tick gate, and 21.837 ms in component preparation.
GPU preparation used 14.590 ms, including 9.154 ms in hierarchy evaluation,
1.359 ms in rest-pose work, and 0.351 ms in particle transform reads. Input
packing used 1.442 ms and bridge dispatch used 1.898 ms. Boundary work used
1.554 ms, including 1.552 ms in quality budgeting. Diagnostics used 1.716 ms,
including 1.714 ms in the activity scan. These counters overlap.

Collection advanced by 409 calls: matrix work used 9.068 ms, mesh work
13.668 ms, and scene work 7.176 ms per collect call. Swap advanced by 410
calls: matrix work used 10.454 ms, mesh work 14.452 ms, and scene work
14.199 ms per swap call. These are call means, not rendered-frame totals.
The GPU clock evidence changed from 1905/8101 MHz to 1605/7001 MHz, so CPU4
and CPU5 are not a controlled performance comparison. Evidence prefix:
`reports/opt-cpu5-warm-` (render summary, render samples, late and collection
counter snapshots, and clock capture).

The `opt-cpu6-warm` window kept 2,000 chains and completed 442 rendered frames
in 36.345 seconds, or 12.1612 Hz. It still misses 100 Hz. Sampled GPU and
whole-frame means were 7.658 and 31.226 ms. All 31 render samples kept
rejected outcomes at 119, deferred outcomes at 81, and failed outcomes at zero.
Completed outcomes increased from 207 to 650. No new rejection was sampled
inside this warm window.

Late-tick counters advanced by 924 calls. Delta time per call was 24.675 ms in
the body, 0.043 ms at the tick gate, and 21.477 ms in component preparation.
GPU preparation used 14.351 ms, including 8.892 ms in hierarchy evaluation,
1.408 ms in rest-pose work, and 0.392 ms in particle transform reads. Input
packing used 1.342 ms and bridge dispatch used 1.852 ms. Boundary work used
1.519 ms, including 1.517 ms in quality budgeting. Diagnostics used 1.676 ms,
including 1.674 ms in the activity scan. These counters overlap.

Collection and swap each advanced by 461 calls. Per collect call, matrix work
used 9.463 ms, mesh work 13.676 ms, and scene work 6.450 ms. Per swap call,
matrix work used 9.058 ms, mesh work 13.735 ms, and scene work 13.025 ms.
These are call means, not rendered-frame totals.

The new sequence-read counters recorded 17,792 contended local reads and
14.159 ms of cumulative local retry time. World reads had 538 contended reads
and 668.890 ms; render reads had 1,041,463 contended reads and 803.921 ms.
Render retry time is about 1.82 ms per rendered frame when divided by 442;
the counter adds time across reading threads and does not measure one frame's
critical path. This does not explain the 82.23 ms mean render interval, so the
evidence does not justify a per-space concurrency change as the primary fix.
Evidence prefix: `reports/opt-cpu6-warm-` (render summary and samples, late,
collection, and matrix-read before/after snapshots).

Two clean-cycle measurements kept 2,000 active chains. Cycle 1 completed 414
frames in 37.208 seconds (11.1266 Hz); its cleanup restored one source chain.
Cycle 2 completed 365 frames in 35.750 seconds (10.2098 Hz); its repeated
cleanup restored one source chain. Neither cycle meets the fixed user
acceptance target of 100 Hz with 2,000 active, animated, visible chains.
Evidence prefixes: `reports/opt-clean-cycle1-` and
`reports/opt-clean-cycle2-`.

Cycle 3 completed 385 frames in 35.435 seconds (10.8650 Hz). Its restoration
report also confirms one source chain and one palette binding. Each cycle had
2,000 registered chains and 2,000 renderer palette bindings both before and
after timing, with zero submitted physics readbacks and no CPU fallback.
The restored reports show one chain, one palette slice, one palette binding,
and zero readbacks after each stop. All three functional start, run, and
cleanup cycles passed these counter checks. The 100 Hz frame-rate check failed
in each cycle.
These runs used an Intel Core Ultra 9 185H and an RTX 4070 Laptop GPU at
1920 x 1080 output with 1286 x 723 internal TSR rendering. Sampled views
show deformation; they do not prove that every chain is visually correct.
Across 31 samples per cycle, failed outcomes stayed zero. Rejected outcomes
stayed at 193, 426, and 426, and deferred outcomes stayed at 85 in each cycle.
Completed outcomes rose by 414, 365, and 385 respectively, so no new sampled
rejection or defer occurred in any timed window. Evidence prefix for cycle 3:
`reports/opt-clean-cycle3-`.

The final Release build had zero warnings and zero errors. These clean cycles
used the same CPU6 source binary in a no-build editor session with the timing
observer off (process 21360); they did not use new tests. The 32-, 64-, and
128-thread GPU solver trials do not prove a winning workgroup size under
matched clocks and conditions.

The rewritten scale todo lists several GPU features that source review shows
are already present. `PhysicsChain.comp` runs one invocation per short linear
tree and advances parent-first particles. `PhysicsChainBranched.comp` assigns
one workgroup per long or branched tree and synchronizes each depth. Both
shaders run the tree's substeps inside one dispatch. The dispatcher chooses
these kernels through GPU-authored indirect commands; dispatch grouping does
not split by loop count. Separate particle-state and static-template buffers,
source versions, and resident arena uploads already avoid steady template
uploads. Global bone-palette generation and aggregate compute skinning also
exist. A persistently mapped ring for dynamic headers remains open; current
header storage uses `StreamDraw`.

The next 100 Hz work should address measured CPU cost and required visibility
ownership. CPU6 used 24.675 ms per late-tick body call and 9.463 ms for matrix
work plus 13.676 ms for mesh work per collect call. Missing canonical bounds
ownership and CPU candidate rejection are under review. Mesh instancing must
also remove per-renderer preparation to reduce these costs; fewer draw calls
alone will not do that. World-owned GPU inputs that do not require per-chain
CPU hierarchy evaluation are a possible larger change and need a correctness
design for animated roots. A new GPU kernel family is not the first fix for
these measured CPU costs.

Canonical GPU bounds ownership needs an end-to-end route.
`AdvancedPreparationExtractor.ExtractCommand` currently copies canonical geometry
bounds into each `AdvancedVisibilityCandidate`. Physics bounds must reach
frame-slot candidates before early and late visibility. Routing must map a
published draw handle and generation to the current candidate index because
legacy indices can compact, and it must retain route and buffer storage until
GPU work completes. `ClassifyCandidatesForViews` must keep eligible view
bits for GPU-owned bounds. CPU shadow tests in `VisualScene3D` and
`VulkanDirectionalShadowLaneCulling.ComputeRecordMasks` need conservative
admission while keeping layer, material, and `CastShadow` rules. Only full GPU
deformation coverage can bypass CPU bounds. An invalid numeric GPU bound is a
rejection, not an unbounded sentinel. This route is not implemented yet.

Root viewed the wide screenshots from all three clean cycles, the near
deformation pairs from cycles 2 and 3, and the restored source pairs after
cycles 1 and 3. The meshes change shape in the sampled pairs. The source
continues to animate after cleanup. This is sampled visual evidence, not an
exhaustive check of every chain. The owned editor session stopped after the
third cleanup. Its retained logs had no matching exception, fatal, device-loss,
or validation-error entry; Vulkan validation layers were disabled for timing.

## Desktop continuation

The next run uses a Ryzen 9 7950X3D and an RTX 3090. These results cannot be
compared directly with the laptop results above. The named editor session is
`chain-100hz`, with Release binaries, Vulkan, Advanced rendering, `CpuDirect`
submission, FXAA, 1920 x 1080 output, VSync off, and an uncapped render loop.
The update target is 90 Hz; the fixed target is 30 Hz. Each measured window
keeps 2,000 registered GPU chains. Root motion stays active, bone readback stays
off, and debug displays stay off.

Evidence is under
`Build/_AgentValidation/20261006-174854-physics-chain-100hz/`.
The retained session build and logs are under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261006-174906-chain-100hz/`.
The timing script divides completed Vulkan frame-count deltas by elapsed wall
time. It checks the chain count before and after each window. An early window
named `baseline` is invalid: the timed benchmark ended during that window and
returned to one chain. Its 204 Hz result must not be used.

| Window | Completed frames | Wall seconds | Completed Hz | New rejected / deferred / failed |
| --- | ---: | ---: | ---: | --- |
| `baseline-valid` | 374 | 30.898 | 12.104 | 0 / 0 / 0 |
| `profiler-off` | 351 | 31.018 | 11.316 | 0 / 0 / 0 |
| `lock-reduction` | 401 | 30.841 | 13.002 | 0 / 0 / 0 |
| `subscriptions` | 403 | 30.862 | 13.058 | 0 / 0 / 0 |
| `typed-locks` (removed) | 393 | 30.951 | 12.698 | 0 / 0 / 0 |
| `observer-off` | 391 | 30.838 | 12.679 | 0 / 0 / 0 |
| `full-grid` (farther camera) | 470 | 30.941 | 15.190 | 0 / 0 / 0 |

The fine CPU profiler is off from `profiler-off` onward. The opt-in world tick
observer stays on for these diagnostic comparisons. Disabling the CPU profiler
did not improve the baseline. These short windows do not establish a precise
speedup under controlled clocks. None meets 100 Hz.

The `lock-reduction` build changes two paths. Existing GPUScene mesh and
material IDs use read lookups and only insert a missing reverse entry. This
removes repeated dictionary writes and captured ID-factory delegates. Exact
ordinary `Transform` children with clean local matrices combine the dirty read
and world composition under one store gate. Callbacks still run after that gate
is released. The mean late-tick body fell from 22.214 ms in `baseline-valid` to
19.365 ms. Mean hierarchy preparation fell from 8.276 ms to 7.029 ms. These are
call means, not rendered-frame totals.

The `subscriptions` build also gives `RenderCommandMesh3D` a published binding
token. Same-renderer assignments skip the subscription gate only when all
groups and overrides remain current. A mutation or failed refresh invalidates
the token and keeps the existing repair path. Both builds passed with zero
warnings and errors. No test files changed.

Eight-second sampled .NET traces identify repeated Monitor entry paths in
bone notifications, pending render-matrix updates, skinned bounds, and renderer
subscriptions. The second trace no longer lists material-ID insertion among
the large sampled paths. Thread-time samples include waiting and do not prove
that every sampled Monitor entry is contended. Runtime counters in the
`subscriptions` build report about 18.6 MB/s of allocation and 593 monitor
contentions per second. One gen0/gen1 collection occurred during seven counter
intervals; no gen2 collection occurred. GC pauses do not explain the sustained
frame interval.

The wide view and two near views from `lock-reduction` were captured and viewed.
The near meshes change shape between captures. The wide camera cuts some near
rows, so these views do not prove that all 2,000 chains pass visibility. Physics
readback submission and dispatch-failure counters stayed zero. The HUD reported
zero CPU fallback. Screenshot readbacks are separate diagnostic work and occur
outside timed windows.

Two proposed shortcuts were rejected. Moving component GPU input collection
after the normal transform pass can change callback, rest-pose, and fixed-step
ordering. Reading GPU bone ownership without its lock can observe a retired
bone-buffer state and lose a CPU update. A later subscription-suppression change
needs a bone-state generation, matching bridge registrations, and a guaranteed
CPU reseed when the final GPU owner leaves. Neither shortcut is implemented.

An isolated no-build run of the `subscriptions` binary set
`XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback`. It failed the warm
window gate. The viewed capture showed a blank purple scene instead of the
chains. Vulkan reported `PresentNowReadinessRetry`, `AdmissionDeferred`, and
`PipelineCompilation` for the `sealed-primary-recording` ticket. Its detail was:
"An indirect draw required by an exact output could not bind its prepared
material and mesh state." No performance result from this configuration is
valid. Evidence: `reports/gpu-submission-failure.json` and
`reports/gpu-submission-wide.json`. The next comparison restores explicit
`CpuDirect` submission. This is a configuration experiment, not a runtime CPU
fallback; chain simulation and skin palettes remain GPU-owned.

The `typed-locks` experiment replaced four private Monitor gates with
`System.Threading.Lock` without changing their critical sections. It showed no
material gain and was removed. The final Release build retains only the ID,
transform, and renderer-binding changes described above. It passed with zero
warnings and errors. `observer-off` and `full-grid` use that build with
`XRE_WORLD_TICK_TELEMETRY=0` and the fine CPU profiler off.

The comparison camera is `(0, 260, 310)`, looking at the origin. The farther
`full-grid` camera is `(0, 600, 700)`, also looking at the origin. Its viewed
capture contains the grid inside the viewport. Both final timing windows keep
2,000 chains, 2,000 palette slices, and 2,000 renderer bindings before and after
timing. Physics readback submissions stay zero. No screenshot or sampled trace
runs inside either timing window. The farther view is not a matched performance
comparison with the baseline, and no per-draw visibility count proves that
every chain passes all visibility tests.

The startup log has `UseDebugOpaquePipeline=True`, which is the Math
Intersections world default. The live selected viewport reports
`AdvancedRenderPipeline`, with 1920 x 1080 internal and output sizes. Setting
the debug preference to false in this isolated session leaves that pipeline
instance and resource generation unchanged. This flag does not explain the
measured viewport cost.

One cold `GpuBoundsPublication.AtlasBufferReadiness` failure was already present
before `baseline-valid`, `subscriptions`, `typed-locks`, and the final timing
windows. Its count does not increase during those windows. The `lock-reduction`
session recorded zero dispatch failures. Thus, these results prove no new
steady-state dispatch failure in the sampled windows, not a failure-free
startup. The cold bounds readiness failure remains unresolved.

After the final full-grid window, the timed harness ended and restored the
source chain. The first final near pair therefore shows the restored source,
not 2,000 chains. Both images were viewed and show a changing source pose.
The restored counters report one chain, one palette slice, one renderer binding,
and zero physics readbacks. A new bounded run captured the final active near
pair with 2,000 registered chains before and after capture. Both images were
viewed and show different bent poses. Evidence uses the
`final-active-near-{a,b}` and `final-visual-dispatcher-{before,after}` prefixes.
Its cleanup again restored one chain, one palette slice, one renderer binding,
and zero physics readbacks. The owned editor session then stopped. The final
session logs have no matching unhandled exception, fatal error, device-loss,
or validation-error entry. Vulkan validation was off during timing.

## Collect-thread cost breakdown

This run used the same desktop, Release binaries, Vulkan, the Advanced
pipeline, and `CpuDirect` submission. The isolated session loaded the Math
Intersections world through `XRE_UNIT_TEST_WORLD_KIND=MathIntersections`.
Each window kept 2,000 chains before and after timing.

| Window | Completed Hz | Note |
| --- | ---: | --- |
| Baseline (current source) | 12.94 | Matches the earlier desktop windows |
| GPU palette listener detach, first run | 11.73 | Within run-to-run variation |
| Same binary, clean restart | 14.71 | Within run-to-run variation |
| Same source plus stage counters | 11.56 | Counters enabled |

Run-to-run variation is about 15 percent. Thus the two code changes below
have no proven frame-rate effect.

Sampled thread-time traces show that the collect and swap thread is busy for
about 80 ms in each rendered frame. The render thread waits about 99 percent
of the time. Runtime contention events show 1–2 ms of real lock waiting in
each frame. Thus the many `Monitor.Enter_Slowpath` leaves in the sampled
traces do not show lock contention. The cost is the quantity of CPU work for
each chain renderer.

Opt-in `RenderableMeshStageTelemetry` counters gave these per-frame costs:

| Stage | ms/frame | Calls/frame | µs/call |
| --- | ---: | ---: | ---: |
| `ApplyPendingRenderMatrixUpdates` total | 25.5 | 4,004 | 6.4 |
| — skinned bounds (`TryApplySkinnedBoneCullingBounds`) | 12.5 | 4,004 | 3.1 |
| — command swap (`RenderCommandMesh3D.SwapBuffers`) | 9.0 | 4,004 | 2.2 |
| `BeforeAdd` total | 12.3 | 4,108 | 3.0 |
| — skinned bounds refresh | 7.2 | 4,108 | 1.7 |
| `Bone_RenderMatrixChanged` | 5.5 | 28,056 | 0.2 |
| Culling-volume publication | 1.6 | 3,990 | 0.4 |

Pending updates run twice for each renderer in each rendered frame, because
the update loop ticks at about 47 Hz while about 12 frames complete. The
Advanced publisher re-plans all draws in each frame (about 11 ms), and matrix
publication costs about 16 ms. Late-tick hierarchy preparation fell from
about 9 ms to 2.3 ms after the root-bone change below. The late-tick body
remains about 14 ms.

Two changes are in the source:

- `XRMeshRenderer` detaches its CPU palette listener from a bone when the
  first GPU owner registers it. It attaches the listener again and reseeds
  the bone when the last owner releases it.
- `RenderableMesh` no longer subscribes the root bone to `WorldMatrixChanged`.
  That listener recomputed skinned bounds from the previous render matrices
  on the update thread. The render-matrix listener remains.

GPU bounds already exist. `PhysicsChainBounds.comp` computes one
conservative bound for each chain from its particles. Path A in
`SkinnedMeshBoundsCalculator` reduces skinned vertex positions. Both write
only `GPUScene.CommandAabbBuffer`, which the GPU BVH and indirect culling
read. With `CpuDirect` submission, CPU culling uses the CPU skinned bounds.
Advanced visibility candidates get their bounds from `AdvancedGeometryRecord`,
which the publisher copies from `GPUScene.CullBoundsBuffer`. Thus no GPU
bound reaches the Advanced visibility candidates. Those CPU bounds change in
each frame and are part of the publisher's content signature. This is a
probable cause of the per-frame publisher work, but it is not measured.

At 100 Hz with 2,000 chains, the whole frame has about 5 µs for each chain.
The measured renderer work is 30–40 µs for each chain. Local tuning cannot
close this gap. The [scale todo](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md)
tracks the remaining implementation and test code.

A benchmark start through `SetBenchmarkRunToggle` once threw
`NullReferenceException` and left 70 copies alive. A clean restart avoided
the fault. The cause is not known.

## Scale implementation baseline

The repeatable scale script measured the current Release Vulkan Advanced path
with `CpuDirect` submission. The named session was `chain-scale-impl`. Stage
telemetry was enabled. The accepted window retained 2,000 registered chains
and palette bindings. It completed 333 rendered frames in 30.493 seconds
(10.920 Hz), with 30 samples and no new rejected, deferred, or failed outcomes.
Physics readback and dispatch failure counters did not increase.

The same binary also passed a separate timing window with world tick telemetry
disabled: 406 completed frames in 30.399 seconds (13.356 Hz). It retained
2,000 chains and reported no new rejected, deferred, or failed outcomes.
The script stored raw samples and checked the registered count and Vulkan
outcomes before and after the window. Neither sampled window establishes a
per-frame p95.

The measured renderer costs agree with the earlier investigation: pending
matrix updates cost 27.237 ms per completed frame, including 13.559 ms for
bone bounds and 9.343 ms for command swaps. `BeforeAdd` cost 13.730 ms,
including 8.005 ms for bounds refresh. Bone callbacks cost 5.707 ms. The mean
world late tick was 16.403 ms. These are diagnostic results, not acceptance
results. The one-second samples do not establish a per-frame p95.

The first script attempt encountered cold material readiness rejection. The
script now resets its warmup anchor after such an outcome and requires 30
consecutive clean completed frames before sampling. It still rejects any new
bad outcome during the measured window.

Failed-start cleanup now destroys a partial benchmark root, restores source
activation, resets benchmark state, and logs the original exception with its
stack. The baseline binary predates this cleanup change; its runtime check
requires the next build.

The broker route tool returned a deprecated model ID for the bounded GPU
review. No paid worker was started. The native architecture review identified
the missing Advanced candidate route, missing GPU-only BVH refit notification,
CPU uploads that can overwrite GPU-owned bounds, and the indirect recording
path's missing materialization snapshot activation. These are code findings
until the changed paths pass runtime validation.

## Bone ownership validation

The reviewed renderer change passed the Release editor build and six focused
`GpuDrivenBoneCoverageTests`. The tests cover complete and partial coverage,
identity indices, release after replacement, registration after a same-size
replacement, listener recovery, and coverage notifications.

The live Vulkan Advanced window completed 387 frames in 30.333 seconds
(12.758 Hz). It kept 2,000 registered chains, 2,000 renderer palette bindings,
and 14,000 bone mappings before and after timing. No deferred, rejected, or
failed frame outcome increased. Physics readback submission, completion, and
enqueue counters stayed at zero. Timing counters were disabled.

The wide and near PNG captures were saved after timing ended. Both were viewed.
The wide capture shows the complete grid. The near capture shows bent meshes.
Evidence is in `Build/_AgentValidation/20261006-195917-chain-scale/reports/bone-generation-reviewed-summary.json`
and that run's `mcp-captures/` folder.

A live settings cycle also passed. Before the cycle, the source renderer had
seven utilized bones and seven GPU-owned bones. After compute skinning was
disabled and the next input copy completed, ownership fell to zero and the
external source cleared. Enabling compute skinning restored all seven owners
and complete coverage. The renderer generation stayed at two. Evidence is
`reports/coverage-mode-cycle.json` in the same run.

The first functional window also passed, but its camera changed during timing.
Its rate is not a comparable performance result. The review then repaired
registration during replacement, rollback CPU reseeding, notification timing,
and settings-mode ownership. The second window uses those repairs.

The benchmark cleanup change is present in both live builds. Each completed
benchmark stop restored the source rig and removed its spawned copies.

## Canonical bounds integration

The shared, OpenGL, Vulkan, and isolated Release editor builds passed with
zero warnings and zero errors. The new bounds patch shader passed GLSL
validation for Vulkan 1.3 and OpenGL. The patch checks the draw generation,
candidate index, bounds slot generation, producer epoch, and bone generation.
It rejects invalid routes and non-finite or reversed bounds through delayed
diagnostic counters. It writes the candidate before early visibility.

The first live attempt did not produce a valid measurement. The MCP request
timed out before timing started. The owned session was stopped. Its rejected
result is `reports/canonical-bounds-summary.json` in the evidence run.

The source review found three defects before acceptance:

- OpenGL restored all slot bindings after the patch. This replaced the
  retained history and geometry bindings. The patch now restores only the
  three borrowed bindings.
- Bounds construction required previously published complete coverage. On
  first use, the palette could establish coverage with an empty bounds page.
  Construction must use the validated complete binding and current bone
  generation. Commit must require both bounds and metadata storage.
- The legacy bounds copy captured updating command indices. These can advance
  ahead of the published render buffers. It must capture a frozen index map
  at the command-buffer publication boundary.

Device replacement also needs a fresh output-page ring. Retired tokens must
remain releasable, but consumers must not acquire retired storage. Resource
destruction requires an explicit successful GPU-idle boundary in the old
renderer context. An inactive renderer or failed fence does not prove idle.
The route and lifetime repairs require another live build before acceptance.

The repaired live attempt kept 2,000 chains and completed 200 frames in
43.902 seconds (4.556 Hz), with no new bad frame outcome. This is a failed
functional result. Both viewed PNGs were black. Dispatch failures increased
by 203. The output producer epoch did not advance, and all page attempts were
busy. Physics readback counters stayed at zero. Evidence is
`reports/canonical-bounds-reviewed-summary.json` in the same run.

The timing script had accepted the stable frame-outcome window. It now also
rejects new physics dispatch failures and an output producer epoch that does
not advance. A completed Vulkan frame does not by itself prove that the
animated scene is valid.

The direction review confirmed the ordered design. Bone ownership has live
evidence. CPU bounds work must remain until the canonical route passes live
validation. The next decision uses per-page retain, producer-fence, and
native-reuse diagnostics. Increasing the page ring does not fix an unreleased
input lease or a failed producer marker.

The page blocker capture confirmed two rejected producer submissions. Pages
zero and one had no CPU retain, but their fence status was `Failed`. The native
bounds, metadata, and current palette buffers reported `Ready`. Page one's
previous palette also reported `Ready`. Page zero's unused previous palette
reported `Unsupported`. The producer epoch stayed at four. The current page
had one retain and a pending native palette consumer. Evidence is
`reports/canonical-bounds-page-blockers-active.json`. The near PNG was black
and was viewed. The owned session was then stopped.

The reuse rule must distinguish a rejected submission from unfinished GPU
work. A healthy Vulkan device and exact native buffer reuse authority must
both permit reuse. Failed output must not become valid history. Input leases
must release their page on the final reference, before the pool can capture
that input again. An owning operation stream must also release its retained
inputs at its safe reset boundary. Borrowed logical streams do not own that
cleanup.

RenderDoc tooling passed its environment check. An attached target accepted a
capture trigger, but two bounded capture-list calls returned no capture. No
capture file was produced. This attempt does not establish GPU pass or buffer
correctness. The target belonged to the isolated session and was stopped by
the session manager.

The next small run stopped the collect loop before timing. The caller-thread
`get_time_state` diagnostic reported a terminal fault in `DispatchSwapBuffers`:
the canonical publisher threw when a covered draw had no committed route.
The publisher now rejects that publication with a diagnostic, so the next
valid GPU page can recover the scene. It does not substitute CPU bounds.
Evidence is `reports/canonical-bounds-lifetime-small-terminal.json`.

The concrete backend admission diagnostic then identified a shader compile
failure in `BuildDepthPyramid.comp`. Source optimization removed an unused
candidate buffer but preserved its layout macro before a conditional. This
left a useless layout qualifier. The compiler treats that warning as an
error. The layout now stays inside each conditional branch with its buffer.
The optimized depth-pyramid source reproduces the warning before the change
and compiles without it after the change. The optimized patch source also
compiles for Vulkan and OpenGL. Evidence is
`reports/canonical-bounds-admission-before-active.json` and
`scratch/shader-check-output-before/` and `scratch/shader-check-output-after/`.

The small recovery window was rejected by the new dispatch-failure gate. Its
page capture still showed unsupported previous palette storage on every
page. The native initialization contract requires repair before another live
window. No canonical bounds check has passed yet.

The native storage repair initializes the previous palette before output
publication. The next one-chain window completed 335 frames in 5.139 seconds
(65.187 Hz), with no new bad frame outcome, dispatch failure, or busy skip.
The producer epoch advanced by 152. Physics readback stayed at zero. Advanced
admission reported `Ready`, with a current output reservation. Evidence is
`reports/canonical-bounds-native-ready-small-summary.json` and
`reports/canonical-bounds-native-ready-state.json` in the same run.

The MCP near and wide PNGs were black and were viewed. An explicit capture
through `RenderDocCaptureBridge` then produced a warm admitted GPU capture.
The chain raster at event 63 has 104 finite transformed vertices and 52
triangles. The opaque HDR target at event 399 contains the bent cyan mesh.
The final target at event 573 also contains that mesh and was exported and
viewed. Evidence is `renderdoc/chain-bounds-admitted_capture.rdc`,
`renderdoc/admitted-chain63.obj`, `renderdoc/admitted-opaque399.png`, and
`renderdoc/admitted-final573.png`.

The native targets rule out a black rendered scene in this captured frame.
The MCP viewport readback does not match the final native target. Keep that
capture-path issue separate from the bounds and deformation contracts. The
source review found no causal bounds or deformation defect. The next check
uses 2,000 chains and native targets from wide and near views. Bounds
validation remains open until that check and the lifetime checks pass.

The full scale window completed 338 frames in 30.188 seconds (11.197 Hz).
It retained 2,000 chains, 2,000 palette bindings, and 14,000 bone mappings.
No bad frame outcome, dispatch failure, or busy page skip occurred. The
producer epoch advanced by 349. Physics readback stayed at zero. This window
had the RenderDoc layer enabled but no active capture. Evidence is
`reports/canonical-bounds-native-scale-summary.json`.

A short capture interval produced empty captures at this workload. A fresh
session with a longer interval produced native wide and near targets. The
wide capture contains 2,000 main visibility draws and 8,000 shadow draws.
The exported wide target shows the complete grid. The near target shows bent
meshes, and a second frame in that capture shows a different pose. All three
PNG exports were viewed. Evidence is
`renderdoc/canonical-scale-fresh-native-wide_capture.rdc`,
`renderdoc/canonical-scale-fresh-native-near_capture.rdc`,
`renderdoc/scale-fresh-wide-final.png`, `renderdoc/scale-fresh-near-final.png`,
and `renderdoc/scale-fresh-near-motion-final.png`.

The third explicit capture ended with a refused MCP connection. The process
was no longer running. The owned session log has no terminal exception or
device fault. This attempt does not provide a third capture or a successful
cleanup check. The two completed captures remain valid image evidence.

Fourteen focused bounds tests passed after live image validation. They cover
the thick mesh envelope, current and previous pose inclusion for fast motion,
interpolation, teleport, sleep, and offscreen wake, affine shear, geometry and
blendshape cache invalidation, frozen command compaction and slot reuse, and
invalid page tokens. The first blendshape test failed because its fixture did
not build the blendshape buffers. The corrected fixture passes. Evidence is
`reports/bounds-tests/bounds-reviewed.trx`.

A restart check reported a rejected canonical publication while producer
pages continued to advance. The source review found an ordering gap:
dispatcher removal can race with a previously captured output batch that
publishes the removed component's palette source. Normal bridge deactivation
already releases the palette binding; that release alone cannot prevent a
later stale publication. Final source publication must check exact request
identity under the same transition authority as removal and release. The
ordering repair requires another live restart check.

The ordering repair passed three restart windows with 64, 2,000, and 96
chains. Each stop restored one source chain, one palette binding, seven bone
mappings, and one resident draw. Canonical publication recovered without a
rejection. The observer-off 2,000-chain window completed 62 frames in 5.533
seconds (11.206 Hz). The producer advanced and physics readback stayed at zero.
Evidence is `reports/bounds-owner-cleanup-cycles.json` and its window reports.

GPUScene now prunes bounds owners when the frozen renderer route snapshot
changes. The pruning uses retained storage and linear work. Destruction clears
that snapshot and both owner sets under the scene lock. A destroyed scene
rejects new bounds owners. The next 64, 2,000, and 96-chain cycles also restored
the source. Evidence is `reports/bounds-final-cycles.json`. This run had the
RenderDoc layer enabled.

Live policy checks kept full GPU coverage while the camera layer mask removed
and restored the main draw. Hiding and showing the render info also removed
and restored the draw. Disabling shadow casting kept the main draw. The small
capture had no shadow pass in either state, so it does not prove shadow policy
by itself. Evidence is `reports/bounds-policy.json` and
`logs/bounds-policy-native.log`.

The motion review found a gap in the particle envelope. Verlet history can
describe the last simulation substep instead of the previous rendered pose.
The bound now reduces the mesh-local box through every current and previous
palette matrix on the GPU. This also removes the CPU bone-scale scan. The
palette already contains the bind-root and inverse-bind transforms. The
bound must not apply those transforms again. Positive normalized skin weights
keep each vertex inside the union of the transformed boxes.

The local box includes current and prior published blendshape extents. Only
the raw current box becomes the next history value, so the box does not grow
with all past poses. Output pages keep renderer slice identities across atlas
layout changes. Compatible slices copy their prior GPU palette. New or
incompatible slices seed previous from current and clear Advanced velocity
history. Shared destination slices validate the selected prior source for
each renderer. The palette barrier includes buffer-copy visibility before
history copies.

The final palette correction passed the 64, 2,000, and 96-chain restart
windows. Each stop restored one source chain, one palette binding, and one
resident draw without a rejected canonical publication. Evidence is
`reports/palette-bounds-cycles.json` and the three window reports.

The native 2,000-chain wide frame passed direct vertex containment. The check
resolved each candidate's stable draw handle through the captured lookup
table, then read the current and previous aggregate deformation slices. All
416,000 positions were finite and inside their routed candidate AABBs. All
208,000 previous-pose vertices differed from the current pose. The first main
pass has 2,000 draws and the shadow pass has 8,000 draws. The exported final
target was viewed and shows the complete grid. Evidence is
`reports/palette-bounds-native-containment.json`,
`renderdoc/palette-bounds-native-wide_capture.rdc`, and
`renderdoc/palette-bounds-native-wide-final.png`.

The process then terminated before the near capture. Windows Application
event 1026 reports an access violation with the top managed method
`VulkanCommandRuntime.RecordAdvancedDirectionalShadowRasterPayload`.
Event 1000 reports exception `0xc0000005` in `coreclr.dll`. The captured frame
does not prove that later native recording is safe. Evidence is
`reports/palette-bounds-native-crash.json`. The cause remains open.

The same camera and timing path passed in a fresh session without the
RenderDoc layer. It completed 334 frames in 31.072 seconds (10.749 Hz), with no
bad frame outcome, dispatch failure, or busy page skip. Physics readback stayed
at zero. The engine continued to run after the near camera change and
benchmark stop. Evidence is `reports/palette-bounds-observer-off-summary.json`.
This window does not close the native recording failure.

The capture-thread review found that `invoke_method` in `MainThread` mode runs
on the app/update thread. Direct bridge start and end calls do not reserve a
render-frame boundary. They can change capture state during Vulkan recording.
This is a possible trigger, not a proven cause. The next isolation uses
`TryTriggerCapture` for one presented frame. The pointer and native-retention
review found no missing physics-output-page retention edge in shadow
recording. Preserve the bounds code until a reproducer or a native stack
identifies the fault.

Single-frame trigger captures completed from wide and near cameras. The near
capture passed the same 416,000-position containment check. The exported near
target was viewed and shows bent meshes. After benchmark stop, the engine was
running with no terminal fault and one resident source draw. Evidence is
`renderdoc/palette-bounds-trigger-wide_frame7700.rdc`,
`renderdoc/palette-bounds-trigger-near_frame7736.rdc`,
`renderdoc/palette-bounds-trigger-near-final.png`, and
`reports/palette-bounds-trigger-near-containment.json`.

The covered CPU bounds review found that a constant root-local bound multiplied
by the full root matrix is not valid for the existing solver. GPU constraints
use fixed world-space segment lengths. Root scale can shrink while chain reach
stays constant. Authored bone scale can change independently, a component can
have several tree anchors, and held GPU production can display an older root
pose. The implementation item now requires a committed CPU spatial snapshot
with cached tree reach and mesh influence radius, plus anchors and basis
stretch from the existing input pass. It must retain matching previous output
history. CPU tree movement must remain separate from draw-command invalidation.
This preserves the optimization goal without changing solver behavior.

The material review found a separate bounds contract gap. The palette box
includes mesh positions and morphs. It does not include standard material
vertex effects applied after skinning. Advanced visibility currently admits
only displacement mode `None`, but the GPUScene copy also serves legacy
submission. A translated vertex-effect material can exceed the copied bound.
Keep this case open until the route applies a declared conservative effect
bound or rejects the unsupported bounds contract explicitly.

### Native capture fault isolation

Explicit capture start/end reproduced the access violation under CDB. The
first failing instruction is in `renderdoc.dll`, reached through the Vulkan
loader from `VulkanCommandRuntime.PushConstantsTracked` during directional
shadow recording. The exception is `0xc0000005`. The instruction uses an
invalid noncanonical pointer. The debugger's nearby export label is not an
exact function name. The earlier `coreclr.dll` event identifies secondary
exception handling, not the first failing instruction.

Evidence is `logs/native-shadow-first-av.log` and
`reports/native-shadow-fault.dmp`. This narrows the fault to the capture-layer
call path. It does not prove the internal RenderDoc cause. Direct MCP
start/end can change capture state while the render thread records commands;
this remains the leading trigger hypothesis. The observer-off window and
presented-frame trigger captures passed. Use `TryTriggerCapture` for editor
frames. Keep explicit start/end as a separate investigation with a controlled
recording boundary. No bounds-page lifetime repair is justified by this
stack alone.

### Deformation history audit

`AdvancedDeformedVertexArena.ResolveVelocityValidity` rejects frame gaps.
Same-frame preparation reuse retains the same bounds source. These checks
cover those two scheduling cases. `AggregateDeformation.comp` writes only
current vertices. Previous vertices are retained output, not a new deformation
of the physics previous palette.

The audit found a confirmed failure-handling gap.
`AdvancedGpuDeformationResources.TryOpenFrame` selects previous history from
`_slotOutputValid` without checking its producer fence. `TryExecute` sets that
flag after enqueue. Failed-fence cleanup occurs later when the slot is
acquired for reuse. A failed aggregate submission can therefore remain
eligible as previous history. Reject known failed or poisoned production at
history selection. Ordered submitted work does not need a CPU completion wait.

One scheduling question remains: does every consecutive Advanced preparation
consume the same physics page or its direct successor? The current checks do
not compare consumed physics producer tokens. Prove this invariant, or record
the consumed token and invalidate history when the new bounds page does not
cover that prior output. The code todo retains both items. The normal native
containment checks do not prove failed-submission or skipped-page behavior.

### Material vertex-effect contract

The generated and Uber vertex shaders apply material effects after compute
skinning, in world space. Translation-class effects have a bounded
displacement, so a padding contains them in any raster order. Scale, rotation,
look-at, and barrel effects multiply positions about the world origin, so a
padding cannot contain them. The implementation pads the first class and
rejects the second. The architecture document records the contract.

The route snapshot now records the material of each published draw from the
draw metadata. This includes command overrides. A draw-metadata change
republishes the routes, because an override change does not advance the
command content version. The dispatcher captures the scene routes once, before
it builds bounds work items, and uses the same capture for the GPUScene copy.

The first live check appeared to show no padding. The cause was the RenderDoc
tool session: `rdc open` did not replace the open baseline capture, so the
translation and rotation checks read the baseline frame. Run `rdc close`
before each `rdc open`, and confirm the file with `rdc status`.

One-chain Release checks used the Vulkan Advanced pipeline with CpuDirect
submission and `XRE_PHYSICS_CHAIN_TEST_VERTEX_EFFECTS=1`:

- Baseline: 208 of 208 current and previous positions were inside the routed
  bound, with zero material rejections.
- Translation `(0, 3, 0)`: the routed bound grew by about 3 units on each
  axis. All 208 positions plus the offset were inside it. The candidate view
  mask remained set.
- Rotation `(0, 15, 0)`: the atlas slot was NaN and the candidate view mask
  words were zero. The dispatcher counted one rejection per publication. The
  canonical publication was not rejected, and physics readback stayed off.
- Recovery: after the rotation was removed, the last-publication rejection
  count was zero. Positions plus `(1, 0, 0)` and `(-1, 0, 0)` were inside the
  new bound.

Views of the rejected and recovered states had 28 draw calls each. The
rejected view showed the rotated mesh, and the recovered view showed the bent
chain. CpuDirect collection admits covered renderers without CPU culling, so
the rejection removes only the GPU-bound consumers in this configuration.

Evidence is `reports/material-effects-summary.json`, the
`renderdoc/material-fx-*` captures and thumbnails, and
`scratch/check-material-effect-bounds.py`. Command overrides, a GPU-culled
consumer of the rejected GPUScene copy, and a 2,000-chain timing window are
not checked. The owned session is stopped
(`reports/material-effects-stop.json`).

### Progress handoff

Implementation stopped at the user's request. The measurement command and
bone coverage work are complete. The canonical route, retained pages, GPUScene
copy, candidate patch, palette-box reduction, and restart ordering are present.
Deformation history keeps the bounds contract open. The material
vertex-effect contract is implemented and has one-chain live evidence; see
the section above.

The final palette-box Release editor build passed with zero warnings and
errors. Earlier focused tests passed before the palette-box correction.
They were not rerun after that correction. `PhysicsChainGpuBoundsContractTests`
still expects a 16-byte work item; the current layout is 48 bytes. Existing
particle-envelope tests need replacement or extension for palette-box and
consumer-history behavior. Test edits wait for live feature validation and
the clearance required by the repository validation rule.

The latest clean timing result remains 334 completed frames in 31.072 seconds
(10.749 Hz), with zero physics readback. It fails the 100 Hz target. The
measurement command does not provide frame-interval p95. Covered CPU spatial
publication, incremental Advanced publication, GPU-driven submission, and
world-owned input gathering remain unimplemented in this todo.

The CPU spatial plan now follows committed output. It must preserve fixed
world-space tree reach, independent anchors, basis stretch, material limits,
and matching previous history. It must not assume that root scale shrinks
solver reach or that the latest live root matches held GPU output.

The owned `chain-scale-impl` editor session is stopped. The RenderDoc replay
session is closed. The final stop report is `reports/final-review-stop.json`.

### Retained history and completed-frame intervals

Work resumed on 7 October. The aggregate previous-output check now rejects
known failed, poisoned, missing, disposed, and unsubmitted producer markers.
It accepts ordered submitted work without a completion wait. Aggregate output
slots record their consumed physics page token. Only the same page or its
direct copied predecessor can supply valid previous vertices in consecutive
aggregate frames. Each page also retains the exact active blendshape values
used by both its envelope and aggregate deformation.

The first source build found a missing namespace import and a span escape
annotation. Both were corrected. The Release editor build then passed with
zero warnings and zero errors. The owned session is `chain-scale-finish`.

Wide and near captures from the failed-history build each contained all
13,312 current and previous vertex positions for 64 chains. Both native targets
were exported and viewed. A near capture from the retained-input build passed
the same count. Evidence is `reports/history-guard-*-containment.json`,
`mcp-captures/history-guard-*-native.png`, and
`reports/retained-input-near-containment.json` in the existing task run.

The first 2,000-chain warm-up did not produce 30 consecutive clean frames
within its deadline. The readiness record identifies cold mesh admission,
with 2,000 cold requests. A later probe completed clean frames with 2,000 draws.
The warmed 30-second window then completed 419 frames in 32.211 seconds:
13.008 Hz, with interval p95 89.536 ms. No frame was deferred, rejected, or
failed in that window. The timestamp query returned 421 samples with no drops.
The two targets remain unmet. Evidence is
`reports/retained-input-scale/retained-input-scale-summary.json`,
`reports/retained-input-readiness-samples.json`, and
`reports/retained-input-warmed-scale/retained-input-warmed-scale-summary.json`.

The CPU spatial review found an input ownership gap. Update runs independently
of render. `SubmitData` overwrites request arrays and fields while render can
read them. The bridge also mutates palette binding state. A CPU spatial
snapshot must use one coherent retained input publication, not only a new
root box. Resident particle state can advance before output publication fails,
and held particles can use a newly uploaded authored basis. Keep resident
particle-position bounds separate from committed render bounds.

## Committed spatial bounds and render input ownership

The update-to-render handoff now uses two retained input banks. The render
thread accepts one sequence and keeps it unchanged through simulation and
output publication. A later update writes the other bank. Output retry does
not advance an already consumed simulation input a second time.

The dispatcher keeps possible resident particle bounds separate from committed
render bounds. Input uploads and queued solves can change native storage before
output admission succeeds. A bounded simulation-receipt ring observes those
native outcomes without a CPU wait. Successful output uses the same retained
page fence. Failed output admission uses a separate ordered marker. A held
receipt retains the possible resident state; it cannot confirm an older failed
solve as a tight checkpoint. Known fence failure and device loss invalidate the
page and request an input retry.

The CPU spatial snapshot captures independent root reach, seed positions, and
an affine basis-stretch bound in the existing input pass. The snapshot uses
exact request, execution, static, particle, renderer-binding, and bone-buffer
generations. Current and previous raw spatial inputs retain their own mesh
influence radius. Constraint shaders use scaled normalization and final
parent-relative projection to avoid overflow and large-offset cancellation.

Covered renderers stop CPU bone-bound subscriptions and command-bound mutation.
CPU culling and picking read the committed snapshot. A query overlay checks
covered items even when an old CPU hierarchy node would prune their new bound.
The asynchronous raycast queue processes a bounded number of requests after
the spatial swap.

The committed-spatial Release build passed with zero warnings and errors in
39.95 seconds. Its 2,000-chain timing attempt did not pass warm-up. A later
64-chain callback inspection timed out on an MCP MainThread request. A
five-second EventPipe sample was dominated by waits. It does not establish the
cause of the timeout. The owned editor was stopped.

Source review found a separate high-count cost: every renderer-bound query
copied the full scene route table. The query now reads only the selected
renderer command materials under the scene publication lock. This removes a
quadratic route-copy cost. A new build and timing window must measure the
result. The world-owned clock and repaired unsupported-mesh preparation witness
also remain unvalidated in the live binary.

The material review found a compatibility route limitation. A traditional
indirect PendingMeshDraw has no exact retained physics page or source lease.
Its material payload cannot establish agreement with the bound used for
culling. Reject only that compatibility copied bound and report the reason.
Keep the real atlas slot and retained canonical source. The canonical publisher
plans all source commands, and its retained-page bounds patch runs before early
visibility. No canonical pre-patch filter consumes the compatibility copy.

## Final committed-spatial runtime checks

The final committed-spatial Release build passed with zero warnings and errors
in 46.80 seconds. Near and wide CpuDirect frames each contained all 13,312
current and previous positions for 64 chains. Confirmed strict indirect near
and wide frames passed the same containment check. Their native main draws use
`vkCmdDrawIndexedIndirectCount`. Both sets of presented targets were exported
and viewed. Evidence uses the prefixes `committed-spatial-final` and
`committed-spatial-strict-confirmed` in the task run. Earlier
`committed-spatial-strict-final` captures used CpuDirect and do not prove strict
submission.

A clean CpuDirect window completed 510 frames in 30.6847868 seconds:
16.620614 Hz, with interval p95 69.6387 ms. It returned 517 interval samples,
no drops, and no new deferred, rejected, or failed outcomes. It retained 2,000
bindings and 14,000 bones, with zero physics readback and no output-page or
dispatch failure. Static upload was 325,752 bytes and dynamic upload was
571,319,776 bytes. RenderDoc and timing observers were off. Both performance
targets fail. These changes include several repairs and a different capture
layer state, so the result does not isolate one cause. Evidence is
`reports/committed-spatial-final-unobserved/committed-spatial-final-unobserved-summary.json`.

A strict 2,000-chain probe recorded six clean readiness samples and 2,000
canonical draws. Its later timing attempt failed the warm-up gate. No strict
30-second result is accepted. See `reports/committed-spatial-strict-unobserved-readiness-*.json`
and `reports/committed-spatial-strict-unobserved/`.

A focused getter confirmed committed CPU bounds on a covered renderer.
The all-zero callback counters from that session are not valid evidence:
`XRE_WORLD_TICK_TELEMETRY` is read at startup, and a runtime override did not
enable it. The next callback check must start with telemetry enabled. Broad
`get_object_properties` inspection of a RenderableMesh timed out while engine
time continued and other MCP calls worked. Use focused getters for this check.

The world input review requires compatibility preparation for both the owner
and reader of a hierarchy dependency. An excluded child can form another chain
without overlapping particle ownership. Custom transform inputs can also read
sources outside parent ancestry. A world prepass preserves these dependencies.
Raw rest translation and rotation must match their initial values bit for bit,
so skipped resets retain their side effects. `ForceManualRecalc` particles also
keep explicit hierarchy preparation. Topology shrink clears discarded transform
references. These gates and the shared Vulkan interface cache are in source;
their first coordinated build and live checks are complete, as recorded below.

## World input checks and mapped input admission

The world-input Release build passed with zero warnings and errors in 46.70
seconds. A live authored-root change caused one compatibility reset. Later
normal ticks recorded no rest-pose, hierarchy, or particle-transform work.
`ForceManualRecalc` with scale 2 kept the expected matrices and compatibility
work. Restoring the normal route removed that work after one final reset.
Evidence is `reports/world-input-live.json`.

A diagnostic 2,000-chain window completed 253 frames in 10.7384773 seconds:
23.560137 Hz, with interval p95 49.8979 ms. It had 266 interval samples, no
drops, no new bad frame outcomes, and zero physics readback. Timing observers
were enabled, so this is not a final performance acceptance window. The world
body averaged 20.5911 ms per tick. GPU rest, hierarchy, and particle-transform
read counters stayed zero. The renderer stage parser still expected numeric
keys and returned an empty result. That part of the report is invalid. The
harness now accepts named keys and saves the raw snapshots. Evidence is
`reports/world-input-interface-long-readiness/world-input-interface-long-readiness-summary.json`.

Cold preparation can take more than 45 seconds for 2,000 chains. The harness
now allows up to 80 seconds, while reserving the selected measurement window
and a shutdown margin within the benchmark controller's 120-second run.

Review of mapped input pages found two ordering faults before live validation.
An early group could copy new authored transforms for a later group whose
particle upload had not run. The transform catalog now changes at each actual
group upload. A reused journal restores it on rollback. World-wide selective
gathers now run only after all solve groups commit. Upload witnesses also
invalidate in the rollback `finally`, including when backend rollback throws.

The resulting Release build passed with zero warnings and errors in 43.30
seconds. The first 64-chain live check failed. Input page diagnostics reported
eight failed markers, eight quarantined pages, and no further solve progress.
This check does not prove covered mode. Evidence is
`reports/mapped-pages-dispatcher.json` and `reports/mapped-pages-covered-mesh.json`.

Cold Vulkan frame admission can omit a submission marker before native
submission. A failed marker does not prove that a buffer remains in use.
`QueryBufferContentReuse` separately checks the exact native generation,
queued references, and completed queue sequences. Output pages already use
that proof to reclaim uncertain production on a healthy renderer. Input pages
must use the same complete proof before reuse. Device loss, an unavailable
proof, or pending native work must continue to reject reuse.

The repaired Release build passed with zero warnings and errors in 47.77
seconds. Each failed input marker is counted once. A healthy Vulkan renderer
can reclaim that page only after every exact input-buffer generation has no
queued references and all native queue sequences have completed. Pending
markers, device loss, and an unavailable proof still prevent reuse.

The 64-chain diagnostic window completed 2,380 frames in about 10.15 seconds:
234.435 Hz, with interval p95 5.6393 ms. New bad frame outcomes, input failures,
quarantined pages, output failures, and physics readback were zero. The page
acquisition count advanced by 1,568. Mapped writes advanced by 63,149. Input
buffer allocations stayed at 15. Evidence is
`reports/mapped-pages-recovery-64/mapped-pages-recovery-64-summary.json`.
The later authored-input check kept covered mode and repeated the normal,
reset, and manual-matrix results. Evidence is
`reports/mapped-pages-recovery-inputs.json`.

Two 2,000-chain windows failed the 80-second readiness limit. They have no
accepted timing result. A separate probe reached 2,000 canonical draws and
completed frames. Failed input markers stopped increasing at 537; no page was
quarantined and acquisition continued. The last native retry named
`visible-mesh-cold-admission`, with 2,013 requests, 427 warm meshes, and 1,586
cold meshes. This probe confirms recovery after cold admission on this build.
It does not prove why each earlier marker failed. Evidence is
`reports/mapped-recovery-2000-readiness-samples.json` and the matching profiler,
state, and dispatcher files. The harness now saves the final readiness
counters, profiler, and dispatcher on a readiness timeout.

## Frozen strategy and covered collection checks

A later source-one session repeatedly lost its committed physics bounds. Native
frame admission reported `RangeExecutionLaneMismatch` during stable-bin sealing.
The extractor used mutable current strategy state while the frozen package used
the last resolved strategy. Switching between CpuDirect and strict indirect
submission could produce two lanes in one package. The package now captures the
requested strategy at collection. Consumer preparation uses that captured lane.
Shared deformation stays keyed by world, scene, and frame, so a second viewport
does not reopen a sealed deformation slot.

The resulting Release build passed with zero warnings and errors in 47.97
seconds. Six 64-chain readiness samples completed with no new rejected or failed
native outcomes. The input marker failure count stayed at one; no input page was
quarantined. Output publication advanced. Preparation had 64 draws, 64 jobs,
one deformation dispatch, and one indirect range. A covered renderer had a valid
committed CPU bound, complete coverage, and zero tracked bone subscriptions.
These samples are readiness evidence, not an accepted timing window.

Main-view CPU collection still included 64 mesh commands. The scene collection
gate required the exact base `RenderInfo3D` type. Editor meshes use
`EditorRenderInfo3D`, so the gate excluded them. Both types now expose an explicit
canonical collection policy. Unknown subclasses must opt in. Camera owners and
editor-only owners keep their existing collection policy. This correction awaits
the next build and live check.

Review also found stale input witnesses when a chain changed between aggregate
and component transform storage. Leaving a world range now forces a fresh full
pack. Parameter-only rebuilds preserve the exact submitted particle seed.
Particle setup advances the seed version. Cached static validity checks child
offsets; live capture checks roots. These corrections await the same build.

The session was stopped while shared instance-group shaders changed their
descriptor ABI. Running those new shaders with the older binary produced an
expected binding-63 layout rejection. That result does not test the new source.
The next session must build the coordinated C# and shader changes together.

## Indexed instance groups

The coordinated instance-group Release build passed with zero warnings and
errors in 46.77 seconds. Six strict 64-chain readiness samples kept canonical
publication valid. Rejected, deferred, failed, input-marker failure, and input
allocation counters stayed constant. Output epochs and mapped writes advanced.
The main viewport had zero CPU mesh commands, 64 canonical draws, one deformation
dispatch, and one indirect range. A covered renderer kept its committed CPU
bound and zero tracked bone subscriptions. Global callback counters still
include other views and renderers; they do not prove shadow collection removal.

A second Release build passed in 40.99 seconds with zero warnings and errors.
Its trigger captures use exact content-equivalent indexed instance groups.
The wide frame has one `vkCmdDrawIndexedIndirectCount` draw with 64 instances.
The near frame has one such draw with 25 visible instances. Member payload IDs
are unique and use one group. In both frames, all 13,312 current and matching
previous vertex positions fit their own routed bounds. All 6,656 previous
vertices differ from current vertices. The presented near and wide targets
were exported and viewed. The captures use `instance-groups-native-64`; replay
sessions were closed.

The publisher retains static plans for covered, untextured, identity-world
draws. It still scans exact command, mesh, and material witnesses. Textured and
custom nonidentity sources keep the full plan path. Review found that a cached
plan must also prove its prior matrices were identity. The added `IdentityWorld`
witness prevents a nonidentity-to-identity transition from retaining old
transforms. It was added after the 40.99-second build.

Review also found that the late meshlet shader still read the early payload
table. It now selects the phase table from the sealed raster phase bit. These
two corrections require the next coordinated build. The indexed captures do
not validate late meshlet recovery or OpenGL execution.

## World gather and retained-plan evidence

The later coordinated Release builds passed with zero warnings and errors in
44.98 and 46.64 seconds. They include the prior-identity witness and late meshlet
table correction. The 46.64-second build adds the serial world input gather.
Normal one-chain motion kept rest reset, hierarchy refresh, and particle-read
ticks at zero for 182 world ticks. An authored rotation selected reset once.
A manual matrix refresh used the hierarchy input route. Normal input capture
resumed after restore. Evidence is `reports/world-gather-live.json`.

The strict 2,000-chain window completed 507 frames in 36.268 seconds: 13.979 Hz,
with interval p95 83.510 ms. No new rejected, deferred, failed, dispatch-failure,
or input-failure outcome occurred. Input allocations stayed constant, mapped
writes and output epochs advanced, and physics readback stayed zero. Evidence
is `reports/instance-groups-clean-2000`. This window disabled RenderDoc and world
telemetry. Later tracing showed that the editor code profiler was still active.
The window is valid readiness and lifetime evidence, but it is not unobserved
performance acceptance.

A diagnostic window on the world-gather build completed 195 frames in 12.647
seconds: 15.419 Hz, with interval p95 80.677 ms. World body time was 21.333 ms
per tick. The gather took 5.956 ms and component preparation took 9.827 ms.
Collection took 11.677 ms per call; scene swap took 8.463 ms. Moving capture to
the world stage did not remove its cost. Evidence is
`reports/world-gather-diagnostic-2000`.

A second diagnostic window completed 611 frames in 36.981 seconds: 16.522 Hz,
with interval p95 74.370 ms. Both 2,000-chain render-state snapshots have 2,000
reused static plans, zero material-resolution calls, zero topology deltas, and
zero content deltas. The main viewport retains canonical collection. These
checks confirm the retained-plan behavior. They do not prove the timing target.
Evidence is `reports/world-gather-profile-2000`.

The 15-second EventPipe CPU trace ran inside that window with all 2,000 chains
active. `CPU_TIME` samples show costs in GPU component preparation, world input
capture, hierarchy activity checks, and repeated committed spatial-bound
queries. The collect-visible thread spent about 4.374 sampled CPU seconds in
`TryGetCommittedSpatialBounds`, 1.602 in material contract evaluation, and 1.392
in GPU bounds-source capture. The profiler stats thread was active. These are
sampled stack attributions, not exact exclusive stage timers.

Review found a stale-parent case in world capture. Root tracking and distance
checks can recalculate an external parent after capture and before consumption.
The new prepass collects every root tracking target and enabled distance
reference. It rejects affected external-parent ranges. It also protects reset
node owners read by distance references and detects opaque root or reference
inputs. These corrections await the next live check. Scene collection now
updates committed CPU tree membership in the same mesh pass as covered command
refresh. Per-view and provider admission still check live committed bounds.

The first directional-shadow build failed with one missing local name and zero
warnings. The source now uses its sealed family lookup segments. The caster
shader also rejects invalid geometry, material, and current-transform handles
before member append. The next build includes both corrections.

## Input restoration and strict shadow authority

The parent-input live check passed prerequisite refresh, root inertia, manual
external-parent scale change from 2 to 3, and distance reentry. Evidence is
`reports/parent-input-live.json`. The restored world-input check recorded 183
baseline and 91 restore normal ticks. `GpuRestPoseTicks`,
`GpuHierarchyTicks`, and `GpuParticleTransformReadTicks` did not advance in
either interval. Evidence is `reports/world-input-restored-final.json`.

Three coordinated Release builds completed in 30.72, 18.98, and 41.95 seconds.
Each had zero warnings and zero errors. The strict 64-chain shadow check used
requested and resolved strategy value `2`, with no downgrade. Across six
samples, completed frames advanced from 52,831 to 55,467. Failed outcomes
stayed at zero, rejected outcomes stayed at 32, deferred outcomes stayed at
82, physics readback stayed at zero, and material rejection stayed at zero.
The `reports/shadow-lifetime-strict-64-*` records contain this evidence. The
earlier `shadow-lifetime-64` check used invalid `GpuIndirectIndexed` text. It
does not prove strict GPU shadow behavior.

The later `reports/unobserved-route-cache-2000` check rejected its final
frozen strategy snapshot. It supplies no accepted timing result. The old MCP
state query did not hold a command-buffer read scope while it copied the
package. It could also select a transient shadow viewport. Source now holds
the effective command owner's read scope and accepts `viewport_index: 0` for
the desktop viewport. This repair still needs a live check after the next
build. Do not infer a performance gain from this rejected window.

## Unobserved windows and animated shadow cache

The render-state snapshot repair passed in the 45.28-second Release build,
with zero warnings and errors. It holds the effective command owner's read
scope and selects desktop viewport zero explicitly. The 64-chain window
completed 697 frames in 5.362 seconds: 129.993 Hz, with interval p95 9.438 ms.
The 2,000-chain window completed 484 frames in 30.624 seconds: 15.804 Hz,
with interval p95 72.915 ms. All four Debug observer settings were false,
RenderDoc and world telemetry were off, input allocations stayed constant,
and no new bad native frame outcome or physics readback occurred. Evidence is
`reports/unobserved-snapshot-64` and `reports/unobserved-cache-final-2000`.
The large window failed the performance target.

Those windows did not prove animated shadows. Their directional accepted and
deferred group counters stayed constant even while physics pages advanced.
The cached depth hash used light, camera, scene membership, and CPU command
state. Covered GPU draw records keep that state stable, so a new palette did
not dirty the cached cascade. The displayed retained-page decline was old:
both clean snapshots had successful aggregate preparation and no deferral.
The fix publishes a coalesced covered-caster output revision and includes it
only for matching caster layers. A held page does not publish a new revision.
Failed and retired pages report source loss. Shadow eligibility must be
independent of the main-view CPU collection bypass; textured and multi-LOD
covered casters also need this invalidation.

The 45.86-second Release build had zero warnings and errors. Its 64-chain
window completed 995 frames with no new bad outcome. Accepted shadow groups
increased from 353 to 1,412; rejected groups stayed at six, unconsumed groups
at zero, and generic groups at 112. The 2,000-chain window completed 397
frames in 30.971 seconds: 12.818 Hz, with interval p95 83.689 ms. It added
471 accepted shadow groups; rejected, unconsumed, and generic counts stayed
constant. Evidence is `reports/shadow-dirty-64` and
`reports/shadow-dirty-2000`. The moving-shadow result adds real work compared
with the earlier cached-shadow windows. The large window still fails the
timing target. Native cascade membership and the wider policy matrix remain
unverified.

The valid 15-second sampled-thread trace ran entirely inside
`reports/cache-profile-bounded-2000`. The harness now starts the bounded trace
itself. Two earlier externally started traces began too late and do not
provide full 2,000-chain trace evidence. CPU samples still identify bridge
input copying, rest capture, hierarchy activity and dependency checks, and
committed spatial queries. The bridge's staging copy is being removed; the
pending packet still owns the data until renderer acceptance. Do not infer a
speed gain before the next matched window.

The world now retains one particle graph and saved rest-pose map through a
weak pending binding and its live registration slot. The component keeps a
nonserialized graph reference. Other packing, quality, and worker state still
reside in the component. Review found that an old-world queued removal must
not clear a new world handle. It also found deferred structural mutations
that assumed admission, and a transfer that could wait forever for a stopped
source world. These corrections are in progress. Runtime handle notifications
must not run authored parameter validation.

## Affine inputs and strict material routes

The 47.71-second Release build has zero warnings and errors. It includes the
48-byte affine input record, shared shader decoder, direct borrowed-span
bridge, exact local-rest matrix cache, and shadow eligibility correction.
The wide 64-chain native frame has one indexed indirect draw with 64 unique
members. The near frame has 25 unique members. All 13,312 current and valid
previous positions fit their own routed bounds in both frames. Of these,
6,656 prior positions differ from current positions. Evidence is
`reports/affine-wide-bounds.json` and `reports/affine-near-bounds.json`.
The near presented export was viewed.

The strict one-chain material check passed with frozen strategy value `2`.
Translation (0, 3, 0) writes padding 3 and contains all 208 current and valid
previous offset positions. Rotation 15 degrees writes the rejected work-item
flag and NaN atlas slot. Its patched candidate has flags `0x600` and view mask
zero. No main or shadow chain draw appears. A neutral command override clears
the rejection and restores the draw while source-material rotation remains
set. Rotation on the override rejects the bound and removes the draw again.
Clearing the effects and override restores valid output. The counter follows
each transition, and canonical scene publication stays accepted. Evidence is
`reports/material-gpu-native-summary.json` and
`reports/material-gpu-translation-containment.json`. The baseline, rejected,
neutral-override, and rejected-override presented exports were viewed.

The one-chain baseline, translation, and recovered captures each contain four
native shadow draws in addition to the main draw. The earlier 64-chain frames
contain no native shadow operation. Those frames do not prove cascade member
correctness. Accepted-group counters measure enqueue acceptance, not the
specific captured native operation. The source review found no caster-bit,
material-pass, matrix, or slice-layout mismatch. A capture after a light
movement or directional-atlas reset can force fresh shadow work. Per-cascade
arguments and members still need inspection.

The source now reduces committed spatial queries to the current published
page. It keeps complete invalidation scans on failure and maintenance paths.
The 46.13-second Release build has zero warnings and errors. It includes the
global debug integration and the world-transition boundary. Parent and
Children entry points acquire the transition gate before hierarchy writes
and locks. Destination admission stays blocked through propagation. The
existing scheduler, world, lifecycle, route, and coverage tests pass: 22
tests in total. The live one-chain parent move, parent restore, deactivation,
and reactivation checks keep the expected registration and recover a valid
strict publication. The first direct World detach check stopped at an MCP
reference type limitation before it changed a field. The tool now accepts
compatible existing interface references. The combined editor build passed.
The repeated live check passed world detach and reattach, parent move and
restore, and deactivation and reactivation. Every active state recovered one
registered chain and a valid strict publication. Evidence is
`reports/scene-boundary-*.json` and `logs/world-transfer-boundary.log`.

The 64-chain boundary window completed 974 frames in 5.327 seconds:
182.833 Hz, with interval p95 7.369 ms. Input storage stayed stable. Bad
native outcomes and physics readback stayed zero. Moving shadow admission
advanced by 1,037 groups, without a new shadow rejection or fallback.

The separate 2,000-chain diagnostic window completed 390 frames in 37.606
seconds: 10.371 Hz, with interval p95 115.631 ms. A 15-second CPU trace ran
inside this window. RenderDoc and the material-effect fixture were enabled.
This is diagnostic evidence, not a matched observer-free comparison. The
largest update-thread leaf costs were bridge submission, hierarchy activity,
rest capture, and dependency checks. Committed spatial queries remained the
largest collection-thread leaf cost. Evidence is
`reports/boundary-profile-2000-summary.json` and
`reports/boundary-profile-2000-cpu-stacks.json`.

## Selective readback and debug lifetime review

Review found that affine gather omitted translation and read the authored
pose. It also found shared physical slots across worlds, missing resident
range checks, and no source witness for a same-capacity template change.
The source now uses shared solved-bone composition, independent world banks,
exact residency checks, and an instance source generation. Failed transfer
markers retain their physical buffers until native reuse is safe. Requests
under another world's tick reject a busy source boundary without waiting.
Renderer replacement retains old banks. Held chains can gather without a
simulation step. Compute and copy operations retain their slot through the
physical frame payload. The Core Release build, both changed expanded shaders,
and the combined editor Release build pass. Unit-test edits still await
clearance.

Five live requests each selected particles and bones 0, 1, and 2, for 216
bytes. All five became available in three frames. Every returned bone
translation exactly matched its solved particle position from the same
result. Nonidentity rotations confirmed solved-pose composition. The service
delivered 1,080 bytes with no stale discard or failure. Evidence is
`reports/live-readback-summary.json` and `reports/live-readback-result-*.json`.
Cross-world simultaneous requests, reset during transfer, held-input gather,
and device-fault handling still need live checks.

The debug indexed-indirect source builds in both native projects. Review
required an exact renderer-owner guard and one frozen batch per render
frame. Both fixes are in the source. A further review found that post-render
callbacks can run before Vulkan consumes deferred mesh requests. A successful
window call or a frame delay does not prove that these authoring references
are gone. The source now retains raw requests, authoring operations, compute
and copy operations, and physical frame payload rows. The 33.25-second combined
editor Release build passed with zero warnings and errors.

The next review found pooled operation reuse, shared compute snapshot reuse,
debug regeneration before readers retired, and direct debug teardown. The
corrections are now in source. An atomic shared lease owner also removes a
readback release-versus-disposal race. Debug programs and shaders belong to
their batch. Public deferred disposal runs after the final counted use.
Vulkan regeneration also requires exact native Ready. Occupied pooled
operations are skipped, and leased compute snapshots are sealed. The latest
clean combined editor Release build passed in 8.43 seconds with zero warnings
and errors. Native validation of these final changes was not run. Descriptor
validation rejects a deferred debug operation if its shared particle source
was retired before materialization.

The final bounded review found two remaining code issues. Debug and readback
preparation access resources before acquiring their first authoring use. A
concurrent retirement can therefore win before the CAS owner rejects the
late retain. They need a scoped active admission lease before the first
resource access. The leased Vulkan compute path also allocates a new
`ComputeDispatchSnapshot` for each dispatch. Each available operation needs
its own reusable sealed storage. Both items are recorded in the active todo.

The diagnostic 2,000-chain report has no usable GPU pipeline timing: profiling
was disabled, timing readiness was false, and reported frame time was zero.
Its dynamic upload delta was 607,633,792 bytes over 463 short-linear
dispatches, with zero static upload delta and no input buffer allocations.
These aggregate counters do not isolate upload or GPU physics wall time.
The 15-second sampled thread totals likewise do not establish a per-frame
critical path. GPU pass timing and CPU wall-time/synchronization attribution
are required before assigning the 10 to 13 Hz delay to a specific subsystem.

Work stopped at the owner's request on 2026-10-07. The owned editor session
`chain-scale-finish` is stopped. No replay session remains open. No unit-test
edits, commit, staging, or push were made in this continuation. The
[implementation status](../../progress/physics/physics-chain-scale-status-2026-10-07.md)
records completed code, validation limits, and the resume order.

## Resume after the latest pull

Work resumed on 2026-10-07 on the Intel Core Ultra 9 185H and RTX 4070
Laptop GPU. These results must stay separate from the prior Ryzen 9 7950X3D
and RTX 3090 results. The scene uses strict Vulkan GPU-indirect submission,
the Advanced pipeline, animated directional shadows, and uncapped presentation.
The desktop output is 1920 by 1080; the observed internal target is 1286 by 723.

Debug and selective readback now acquire active admission before storage
access. The bounded review also found exception paths that retained logical
staging slots and buffer growth that destroyed the old buffer before a new
one was ready. Both paths now release or retain the correct owner. Quarantined
Vulkan slots retire only after counted users finish and native reuse reports
Ready. Missing native proof keeps the slot until dispatcher teardown.
Available compute operations reuse their own sealed snapshot storage.
Detached copies keep independent storage. The combined Release build passed
with zero warnings and errors in 52.79 seconds. Per-frame allocation profiling
and the native fault matrix remain open.

The current 32-chain smoke window completed 284 frames in 6.038 seconds.
No new deferred, rejected, or failed frame appeared. Input storage did not
grow, and physics readback stayed at zero. Wide and near images showed visible
bent meshes. Four explicit selective requests then returned 216 bytes each in
three frames. Bone translations matched the selected solved particle positions.
Successive results changed. These requests transferred 864 bytes in total;
they were outside the zero-readback timing windows.

Dense GPU timing and CPU frame timing identified a GPU wait. One active CPU
frame spent 366.717 ms in the next-slot wait out of 400.448 ms total. A dense
GPU frame reported 233.178 ms around HiZ testing and 123.431 ms around
directional cascades. These timestamp ranges include upstream work drain;
they are not exclusive shader costs. Group append paths had shared CAS retry
loops. Early visibility, late visibility, and directional shadow membership
now use one atomic reservation. Capacity checks still guard writes, and the
finalizer clamps attempted counts to each group's reserved capacity.

After all group-counter changes, the strict 2,000-chain window completed
656 frames in 30.403 seconds: 21.577 Hz, with frame-interval p95 52.948 ms.
It added no deferred, rejected, or failed frames. All 2,000 chains remained
registered. Physics readback, dispatch failure, input allocation, and input
failure deltas were zero. Accepted shadow groups advanced by 768; rejected,
unconsumed, and generic counts did not change. Wide and near images were
viewed. The four Debug observers and RenderDoc were off; world telemetry
remained on. This is a diagnostic result and does not meet the 100 Hz target.
Static upload increased by 3,601,500 bytes; this is not proof of complete
steady-state GPU residency.

Earlier 30-second benchmark attempts ran past the controller's 120-second
limit because the script tried to recover every missed one-second sample.
Those windows ended with zero chains and are invalid. Sampling now uses a
wall-clock deadline and skips missed sample positions. Actual elapsed time
includes slow calls. The optional trace tool is checked before scene mutation.

The next memory experiment changes only Advanced visibility storage. It
prefers host-visible device-local memory and retains the existing mapping,
flush, and fence contracts. Cold allocation logs expose the actual memory
type and flags. Other lanes retain their existing policy. The policy and
capacity-failure retry passed review; build and measured results follow below.

The memory build passed with zero warnings and errors in 57.81 seconds after
the frame-slot growth caller received the same lane policy. A live allocation
registry query confirmed five 16 MiB visibility chunks. Each used memory type
4 with DeviceLocal, HostVisible, and HostCoherent flags. The ordinary Vulkan
log category did not emit the cold allocation lines in this session, so the
registry query supplied the placement evidence.

The first telemetry-on memory run completed 568 frames in 30.065 seconds:
18.892 Hz, p95 64.582 ms. A separate dense GPU sample then recorded 34 active
frames. The directional cascade interval averaged 0.228 ms; HiZ averaged
0.181 ms. Pipeline p50 was 6.716 ms. These intervals are inclusive and do not
cover all physics or CPU work. A CPU snapshot had 0.022 ms next-slot wait and
10.729 ms command recording. Collection and update still had large wall times.
No matched measurement isolates a frame-rate gain from memory placement.

With world telemetry, the four Debug observers, and RenderDoc disabled, three
60-second strict 2,000-chain cycles produced:

| Cycle | Completed frames | Completed Hz | Frame-interval p95 |
| --- | ---: | ---: | ---: |
| 1 | 1,338 | 21.805 | 52.598 ms |
| 2 | 1,195 | 19.879 | 62.596 ms |
| 3 | 1,176 | 19.458 | 60.881 ms |

Every window held 2,000 registered chains and passed strict package, moving
shadow, frame-outcome, input-storage, and zero physics-readback checks. After
each stop, counters returned to one chain, one palette slice, and one renderer
binding. All wide and near images were viewed. Restored-source image pairs
from all three cycles show continuing motion. Exact all-member visibility
and native output containment still need a current capture. The 100 Hz target
failed in all three cycles.

A same-call selective request/cancel/release returned Cancelled and released
the handle. A subsequent request returned 216 bytes in three frames. Bone
translations matched particles and the returned linear rows had nonidentity
rotations. This makes five successful 216-byte requests in this continuation.

The primary directional shadow collection bypass was rejected during source
review. The primary tile uses ShadowRenderPipeline, which clears canonical
publication. Its general GPU path still executes collected CPU-owned mesh and
non-mesh commands. Preparing an empty GPU package would remove real casters.
The generic GPU culling shader also lacks CastShadow and mirror policy checks.
An explicit native primary-shadow consumer and ownership contract must exist
before this collection can be removed.

The update profile contains a large RuntimeWorld.Update scope with no inner
tick detail. Fixed update can also wait on the world's tick gate. Repeated
rest-input ancestry checks are a candidate, but a shared cache needs an exact
same-phase hierarchy/topology witness. Measure quality and GPU input-gather
stage deltas before choosing that change. Do not cache across frames using
only registration or particle-rebuild dirtiness.

Static upload counters include template and depth-topology writes. The
2,111,928-byte delta in the memory run does not prove a per-frame upload bug.
Cold and structural publication can account for part of it. UpdateParameters
also advances the particle version for edits that do not change particle
static data. Compare exact per-request static versions and the depth-topology
signature across a stable window before changing this invalidation contract.

### Advanced debug drawing and shader ownership

The Advanced command chain did not invoke selected GPU chain debug drawing.
A conditional depth-tested pass now loads the scene color and depth targets.
It runs only when GPU chain debug selection is active. It excludes unrelated
world physics debug drawing. This change does not enable debug work in the
clean benchmark windows.

The first native capture did not contain the two expected debug indirect
draws, although the authoring snapshot reported them. Those counters can
retain an earlier successful submission and are not native execution proof.
The native frame did contain the main skinned mesh and four shadow draws.

An off/on cycle then exposed a separate shader ownership defect. A cold live
probe found seven debug items, a destroyed cached compute shader, no program,
and no buffers. ShaderHelper returned its completed cached shader load.
The debug batch had destroyed that shared shader when its selection retired.
The batch now borrows the shader and destroys only its owned program. The
next build must validate the first debug draw and repeated reselection.

RenderDoc capture and replay must use the same compatible build. The first
capture loaded the installed 1.44 layer, while the available replay module was
1.41. A process-only VK_IMPLICIT_LAYER_PATH override selected the existing
1.41 layer for the owned session. Module inspection and replay confirmed the
match. No installation or registry setting changed. Native targets were
exported and viewed; replay sessions were closed.

### Full-grid native evidence and input-bank growth

The repaired debug batch passed three off/on cycles. One native frame contains
the compute dispatch and both indexed indirect debug draws with seven
instances. Both draws use depth test and depth writes. Other captures caught
busy frames without debug draws. The bounded batch can skip these frames;
continuous debug overlays are not proven.

Two MainThread MCP captures contain one indexed group with 2,000 members.
Each has 2,000 unique payloads and draw handles. Replay checked the actual
index stream, shader push constants, per-member deformation offsets, route
generations, and bounds. All 208,000 current and 208,000 valid previous
positions are finite and inside their own bounds. Each of the 2,000 members
changes its centered radii and anchor distances by more than 0.001 world
units. This excludes rigid translation and rotation alone; world scaling
can also change those distances. All 2,000 current-position digests also
change between the two captures. The final native images were viewed.

The replay reports are `native-mainthread-wide-exact-members.json` and
`native-mainthread-second-exact-members.json` in the current run's `reports/`
folder. One incorrect helper assertion was removed: the first atomically
appended member is not necessarily the group's fixed representative. The
actual native index decode and per-member range checks remain.

An earlier restart retained Direct MCP dispatch. Its long spawn call timed
out and the captured frame was incomplete. That run is discarded. Set
MainThread MCP dispatch after every restart, as the canonical harness does.

The corrected full-grid camera is (0, 400, 450), looking at the origin.
The older (0, 260, 310) camera could clip front rows, so the older clean
timings do not prove performance with every chain visible. The harness now
uses the corrected camera and scales its distance above 2,000 chains.

The first clean full-grid window failed measurement acceptance because input
buffer allocations increased from 10 to 15. Native failures, quarantine,
and arena generation did not increase. Allocation occurs in groups of five
when an input bank is first used or grows. Capacity planning now provisions
all eight safe banks at rounded, monotonic capacities. It preserves native
reuse checks, renderer ownership, and the selected-bank fallback. Failed
provisioning remains pending and reports a diagnostic. A complete warm target
does not scan or poll the banks. This requires a fresh three-cycle check.

### Math world camera and label defaults

Only the Math Intersections pawn camera now starts with bloom disabled and
manual exposure 1.0. Live Advanced pipeline settings confirmed both values
before and after source-chain activation. No shared camera default changed.

The world labels already calculated camera-facing text matrices, but cached
them while the camera moved. Math labels now invalidate their text matrices
before rendering. Front, side, rear, and far-to-near views were captured and
viewed. Names, descriptions, and sublabels remain readable and face the
camera. The mesh stays visible against the black background. The combined
input-bank and camera build passed with zero warnings and errors in 108.19
seconds. User confirmation of the visual result is not yet recorded.

### Repeated-run resource blocker

The first three full-grid windows after input provisioning passed measurement
checks at 22.243, 19.969, and 18.987 Hz. Interval p95 was 51.739, 63.093,
and 62.536 ms. All had zero new input allocations and failures. These are
not performance acceptance. An immediate post-stop probe ran before gradual
teardown ended, so it reported zero registered chains. A later probe found
the restored source. The lifecycle check now waits for that restoration.

A fourth window passed at 21.152 Hz and p95 59.902 ms. Its viewed wide and
near images show the grid and deformed meshes. After teardown, one source,
one palette slice, and one binding returned. Viewed source images show motion.

The fifth window failed after 189 completed frames and 18 deferred frames.
The following snapshot reported persistent `ResourceGenerationBlocked` at
FramePacing. No device loss or renderer reload occurred. The main viewport
reported a valid 1920 by 1080 display, 1286 by 723 internal size, an active
resource generation, no pending generation, and no generation error. The
source returned to one chain, but completed rendering stopped. Input and
output fence failures then increased. Their ordering does not prove they
caused the global viewport resource blocker. This failure remains under
investigation; do not report the extended restart sequence as passed.

Evidence: `final-lifecycle-2-summary.json`, `final-defer-profiler.json`,
`final-defer-render-state.json`, and `final-defer-restored.json` in the run's
`reports/` folder. The same combined binary passed all 40 existing targeted
Release tests in 35.036 seconds. No unit tests were added or changed.

Rendering later recovered without a restart or a setting change. Two
read-only probes found no blocker, no pending generation, and a cleared skip
marker. Completed-frame counters advanced again. Resource preparation runs
before desktop preflight, so a simple preflight-before-preparation deadlock
is ruled out. Unsubmitted frames fail their input/output markers; the later
fence failures can be consequences of the viewport blocker. The old
MeshMaterialization failure predates this episode and is not its cause.

The benchmark harness now saves failure snapshots before teardown. A retained
first/latest resource-blocker diagnostic was added because live polling
after automatic recovery loses the decline reason. No recovery policy change
is justified by the current evidence.

Two runs after recovery passed at 14.856 and 16.574 Hz, with p95 85.304 and
70.603 ms. Both restored the source and its palette binding; wide, near, and
source motion images were viewed. The next start request timed out after 180
seconds although the editor started the benchmark. This is a separate failed
automation check. The harness now marks a start as pending before sending it,
so an uncertain reply still triggers failure snapshots and stop cleanup.

The final diagnostic build passed with zero warnings and errors in 48.27
seconds. Its retained startup episode spans frames 1 through 78, with the
resource guard passing at frame 79. The snapshot retains the pending Building
key and the resource-profile decline after recovery. Its synchronized getter
prevents a torn read across app and render threads. No admission policy changed.

The final clean 2,000-chain window completed 603 frames at 19.985 Hz, with
interval p95 59.574 ms. There were no new deferred, rejected, or failed frames,
input allocations, input failures, or physics readback. Strict submission and
moving-shadow admission passed. Stop restored one source chain, one palette
slice, and one binding. Wide, near, source motion, and front/side label images
were viewed. Bloom remains disabled; auto exposure remains disabled with
exposure 1.0. The retained episode stayed unchanged after the run. The current
owned session was stopped. The 100 Hz target and extended restart checks
remain open; the successful final window does not erase the earlier faults.

### Six repeat windows and CPU diagnostic follow-up

The later `async-repeat-1` through `async-repeat-6` summaries all passed the
harness correctness checks. The report label does not indicate asynchronous
physics submission. Physics still uses the graphics queue.

| Window | Completed frames | Elapsed seconds | Completed Hz | Interval p95 ms |
| --- | ---: | ---: | ---: | ---: |
| 1 | 650 | 30.421 | 21.367 | 57.320 |
| 2 | 616 | 30.750 | 20.032 | 58.574 |
| 3 | 625 | 30.435 | 20.535 | 58.445 |
| 4 | 583 | 30.123 | 19.354 | 57.377 |
| 5 | 489 | 30.588 | 15.987 | 75.280 |
| 6 | 551 | 33.949 | 16.230 | 70.183 |

World telemetry and the four profiler observers were off. All windows held
2,000 chains and strict directional-shadow admission. They added no deferred,
rejected, or failed frames, input growth, or physics readback. All 24 wide,
near, and restored-source images were viewed. Each stop restored one source
chain. No window meets 100 Hz or 10 ms p95. The retained snapshot after all
six windows contains only the cold startup episode at frames 1–80 and recovery
at 81. This does not resolve the earlier intermittent resource blocker.

The next Release build includes the opt-in MCP request trace. It passed with
zero warnings and errors in 34.44 seconds. The broker review completed and
found that `SetBenchmarkRunToggle` returns `void`. Its invocation does not
wait for settling or measurement. The supplied path does not establish a
synchronous Task wait or a lost completion signal. Trace request enqueue,
callback entry, reflection entry/exit, completion, and response boundaries
before changing synchronization or timeouts. A timed-out client request does
not prove that the server mutation stopped.

The `cpu-diagnostic-trace-1` window enabled world telemetry and passed the
harness checks. It completed 661 frames in 30.1239 seconds: 21.94269 Hz, with
p95 53.7217 ms. The four profiler observers remained off. All four images were
viewed. After stop, the source count is one and the source still deforms.
The post-stop resource snapshot is healthy. Its retained episode covers only
startup frames 1–78 and recovery at 79. The intermittent blocker remains open.

The full-world before/after snapshots span 20.6824944 seconds. Callback rows
were matched by `DeclaringType`, `Method`, `TargetType`, `InnerDeclaringType`,
`InnerMethod`, `Group`, and `Order`, not by array position. `PhysicsChainWorld`
recorded 624 `LateTick` calls at 23.788 ms per call, 621 `FixedTick` calls at
10.322 ms per call, and 624 `UpdateTick` calls at 0.978 ms per call. Benchmark
root motion averaged 1.238 ms over 624 calls. All four callbacks recorded zero
allocation on their calling thread. Fixed tick can wait on the shared tick
gate; its measured duration is not exclusive physics computation.

The separate detailed telemetry pair covers 1,064 late ticks. Body time
averaged 23.790 ms per tick. Quality evaluation plus prerequisite/dependency
preparation averaged 5.588 ms; world rest-input gathering averaged 7.161 ms.
GPU component preparation averaged 3.112 ms, input packing 1.905 ms, bridge
submission 1.424 ms, and activity scanning 1.415 ms. These timers are nested.
Do not sum parent and child timers or combine concurrent callback time into
exclusive frame time. Quality is not isolated from dependency preparation.
World gathering also includes compatibility and collider-dependency checks.
Bridge time includes submission locks and packet work. Committed spatial
queries still have no separate exclusive timer.

Evidence is in `Build/_AgentValidation/20261007-102900-chain-scale-resume/reports/`:
`async-repeat-1-summary.json` through `async-repeat-6-summary.json`,
`async-repeat-after-six-episode.json`, `mcp-timeout-broker-review.json`,
`cpu-diagnostic-trace-1-summary.json`, the matching telemetry pair,
`cpu-diagnostic-window-world-before.json` and `-after.json`, and
`cpu-diagnostic-post-stop-resource.json`. These files are disposable; the
measurements and limits above are the durable record.

Nsight is installed. Its environment check reports no OS administrator access
and failed sampling support. An API-only trace trial has not been attempted.
Queue availability does not prove hardware overlap. The remaining experiment
needs actual queue timelines, same-frame producer/consumer dependencies, and
matched total-frame timing. No asynchronous physics path or performance gain
is established. The owned editor is running for further diagnostics.

### API-only Vulkan trace and activity-scan scope

The Nsight API-only trial succeeded despite the unavailable OS sampling
support. The benchmark window completed 650 frames in 30.767 seconds at
21.126 Hz, with p95 55.535 ms. Harness correctness checks passed with no new
deferred, rejected, or failed frames. The source returned to one chain.
Both restored-source images were viewed and show different deformation.
The owned editor is now stopped, and the profiler launch session has exited.

The trace began at 20:52:58.467 UTC. The benchmark window began at
20:52:43.447 UTC. Analysis uses trace offsets 1 through 12 seconds, an
11-second interval that ends before the benchmark window ends. It contains
684 workload rows, 229 submit calls, and one queue context, ID 2. Correlation
grouping finds 228 complete submission groups with three workloads each.

| Trace measure | Result |
| --- | ---: |
| Mean complete-group GPU span | 8.798 ms |
| Median complete-group GPU span | 8.188 ms |
| Complete-group GPU span p95 | 12.826 ms |
| Maximum complete-group GPU span | 15.764 ms |
| Mean submit spacing | 48.082 ms |
| Mean submit-return to GPU-start delay | 0.177 ms |
| Observed workload coverage | 18.22% |

The full-trace API summary reports mean `vkQueueSubmit2` duration of 0.094 ms,
mean `vkQueuePresentKHR` duration of 0.066 ms, and mean `vkWaitSemaphores`
duration of 0.019 ms. These API means use the full trace, not only the stable
interval. The warning states, "Not all Vulkan events might have been
collected." Workload rows are submission units, not individual shader scopes.
Observed coverage is not device utilization, and missing events limit precise
busy/idle claims. The short observed GPU spans and larger submit spacing
support a CPU submission-gap hypothesis. They do not isolate an exclusive CPU
cause or prove that asynchronous compute will help.

Evidence is `vulkan-api-trace.nsys-rep`, `vulkan-api-trace.sqlite`,
`vulkan-api-trace-analysis.json`, `vulkan-api-stats_vulkan_api_sum.csv`, and
`nsys-api-diagnostic-summary.json` in the same run's `reports/` directory.
The bounded analysis is in `scratch/analyze-nsys.py`.

Source review limits the 1.415 ms activity-scan measurement to
`PhysicsChainWorld.PublishActivityDiagnostics` in
`PhysicsChainWorld.ActivityDiagnostics.cs`. It scans live slots, reads sleep
and wake state, checks hierarchy activity for awake components, and updates
changed sleep/wake counters. Selected diagnostics run under a separate timer.
`IsRuntimeSleeping` and `WakeCount` are direct field reads. The hierarchy path
is `XRComponent.IsActiveInHierarchy` to `SceneNode.IsActiveInHierarchy`, which
recursively visits `Parent` until the root or an inactive node. `SceneNode.Parent`
reads the transform's virtual parent and scene-node properties. The default
getters do not recalculate matrices, allocate, or take a lock.

This scan is linear in live slots times ancestor depth, not a scan of all
other chains. Its timer also includes slot access and counter work, so the
1.415 ms does not prove hierarchy traversal owns that time. The next narrow
measurement should separate hierarchy checks from the rest of this loop.
Do not replace the current check with a cached active flag without an exact
activation, world-membership, and parent-change witness. A slot-copy reduction
can be reviewed separately, but no speed gain is measured. The larger quality
and world input-gather intervals remain higher-priority CPU measurements.

### Rejected ancestry-cache experiment

`ancestry-cache-diagnostic-1-summary.json` passed the harness checks at
21.129 Hz and p95 60.0716 ms. The detailed telemetry pair measured mean
late-tick body time of 31.864 ms, against the earlier 23.790 ms baseline.
Quality plus dependency preparation rose from 5.588 to 11.926 ms. World
rest-input gathering changed from 7.161 to 7.393 ms. These nested stage
measurements reject the cache as a performance improvement; the similar
completed-frame rate does not override the CPU regression.

All 13 compatibility-reason counts stayed at zero. The run added no physics
readback or new deferred, rejected, or failed frames. All four images were
viewed, and stop restored one source chain. The cache was removed.
Its patch is retained at `reports/ancestry-cache-rejected.patch` in the current
run. Do not retain this experiment as finished cache architecture.

The next authorized design permits lower physics rates by distance with
interpolated GPU poses. Current solver records retain `PreviousPhysicsPosition`,
but `PhysicsChainBonePalette.comp` uses only current solved positions. No
palette interpolation alpha or mode is bound. The debug shader has an
interpolation helper, but the bridge currently supplies alpha zero. A zero-step
Core update returns before submission, and GPU active-work compaction excludes
zero-step and cadence-skipped trees. The solvers' skip branches therefore do
not supply root-motion maintenance for this route.

The design must separate solver endpoints from previous rendered output.
Output pages already retain the prior published palette with exact source
witnesses; Advanced deformation separately checks the preceding render frame
and its output-page token. Interpolation needs render-pose publication between
solves, consistent root motion for both endpoints, and bounds for the actual
current and previous rendered palettes. The existing lower-rate metadata alone
does not provide these contracts. No interpolation implementation or gain has
been validated.

### Existing reduced-rate comparison and temporal design review

After the ancestry cache was removed, `cadence-full-rate-baseline-summary.json`
passed the harness checks with 675 completed frames in 30.809218 seconds:
21.909 Hz and p95 53.9809 ms. `cadence-hz15-existing-summary.json` also passed,
with 681 frames in 30.520027 seconds: 22.313 Hz and p95 52.0613 ms. Neither
window added deferred, rejected, or failed frames. Neither meets the target.

The detailed full-rate pair covers 1,124 late ticks: body time averaged
24.389 ms, quality plus dependency preparation 5.848 ms, and world rest
gathering 7.123 ms. The existing 15 Hz pair covers 1,116 late ticks: body
22.294 ms, quality plus dependency preparation 5.824 ms, and rest gathering
7.304 ms. These are nested stage measurements. Reduced cadence still pays
the world gathering cost before the current due decision.

All four 15 Hz images were viewed. The wide grid is present; the near view
contains bent skinned meshes. The restored-source images show different bent
poses. Stop restored source count one and the `Strict`/`Discrete` settings.
Still images do not prove smooth interpolation. The owned editor was stopped;
`pre-distance-lod-stop.log` records that cleanup.

The current `Interpolate` setting does not interpolate the GPU palette.
Existing reduced tiers also change the physical time reference instead of
batching equivalent authored fixed steps. The comparison therefore supports
moving due admission before expensive CPU gathering. It does not validate
smooth GPU LOD or equivalent wall-time physics.

The completed temporal review in `distance-lod-temporal-review.json` rejects
a single endpoint pair with cadence-derived alpha as a general rate-transition
contract. Normalized cadence phase does not preserve displayed pose when the
endpoint interval changes. A new batch can replace endpoints still needed
for presentation. The reviewed safe reference uses authored 60 Hz physical
ticks, retained timestamped endpoints, and a fixed presentation delay of at
least four ticks, about 67 ms, plus bounded endpoint delivery. This is not a
final latency decision. Delivery bounds, continuous recovery, and latency
reduction remain open.

The user-authorized full feature also needs retained GPU tick debt, sampled
root motion across fixed substeps, root-relative presentation on render-only
frames, separate previous-render history, conservative skin/root bounds, and
multi-view relevance. The first bounded code slice is early due admission for
the existing reduced tiers. It must not be reported as the full smooth-LOD
feature. See the [distance cadence design](../../design/physics/distance-cadence-gpu-presentation.md)
for the proposed ownership and temporal contracts.

### Workgroup comparison with invalid marker windows

The 32, 64, and 128 workgroup trials used the unchanged editor binary, full-rate
source settings, and world telemetry off. The new CPU early-admission patch
was not built into these runs. The results are:

| Workgroup size | Completed frames | Elapsed seconds | Reported Hz | Reported p95 ms | Validity |
| --- | ---: | ---: | ---: | ---: | --- |
| 32 | 634 | 30.287 | 20.933 | 55.0141 | Harness passed; interval samples valid. |
| 64 | 657 | 30.443 | 21.581 | 52.1825 | Failed `InputPageFenceFailed`; interval telemetry invalid, four dropped samples. |
| 128 | 595 | 30.249 | 19.670 | 63.3182 | Failed `InputPageFenceFailed`; interval telemetry invalid, two dropped samples. |

All three windows reported zero new deferred, rejected, or failed native
frame outcomes. That does not override the input-page failures. At 64 threads,
both input- and output-page failure counts rose from one to six. Dispatch
failure count stayed zero; input quarantine stayed zero. These are rejected
measurement windows, not evidence that a particular workgroup size causes
the marker fault.

`solver-artifact-workgroups.json` confirms compiled local sizes of 32, 64,
and 128. The current 32-thread artifact was created at 17:42:03 UTC; the new
64- and 128-thread artifacts were created at 21:28:14 and 21:33:21 UTC.
These artifacts confirm the compiled variants, not a performance winner.
The shader include returns to 32 for the next run.

GPU CSV timestamps are local PDT. They were converted with UTC offset -07:00
and filtered to each `window-start` timestamp through that timestamp plus the
summary's actual elapsed seconds. No startup, teardown, or source-only samples
were included in the following table.

| Workgroup | Samples | Graphics MHz mean (range) | Memory MHz mean | Power W mean | Temperature C mean | Sampled utilization mean |
| --- | ---: | --- | ---: | ---: | ---: | ---: |
| 32 | 30 | 1,266.5 (885–1,635) | 7,468.3 | 13.31 | 57.8 | 25.4% |
| 64 | 29 | 1,239.8 (780–1,470) | 7,270.9 | 13.14 | 57.8 | 28.3% |
| 128 | 30 | 872.0 (420–1,365) | 3,483.4 | 10.68 | 59.3 | 30.6% |

The 32- and 64-thread windows include P0, P3, and P5 samples. The 128-thread
window includes P0 and P5. Memory clocks range from 810 to 8,101 MHz in all
three windows. These are coarse device samples, not per-pass GPU timings.
Clock and power variation further limits comparisons. Do not infer a winner
or a causal thermal explanation from this small set.

All four 32-thread images were viewed. The wide grid is visible, near meshes
are bent, and the restored source changes pose. The failed 64- and 128-thread
harness runs ended before wide/near capture. Their separately saved restored
source pairs were viewed. Both show a visible source with different bent
poses. No screen image establishes all-member visibility or smoothness.

Source review shows that `CanReuseInputPage` records a failed fence marker,
then can reuse a Vulkan page if native buffer reuse checks pass. Zero
quarantine therefore does not establish that a failure was harmless.
`VulkanTimelineGpuFence.Fail` is also used for abandoned or unsubmitted plans,
as well as native query failure. The 64-thread profiler snapshot retains a
strict directional-shadow pipeline compilation retry at frame 1192. Later
frames completed with unchanged bad-outcome totals. Delayed observation of
an earlier marker failure is possible, but there is no marker origin identity
to prove it. The available session logs do not identify the first failure
site. Keep the failure classification until creation, bind, failure, and
observation identities can be correlated.

Evidence is in the `workgroup32-clean-1`, `workgroup64-clean-1`, and
`workgroup128-clean-1` summary, window-start, and GPU-clock reports, plus the
64/128 failure and restored-source reports. The 128-thread session was being
stopped before the next build. No CPU optimization validation is implied by
these old-binary trials.

### Provisional early-admission comparison

Last checked: 2026-10-07 21:49 UTC. The early-admission Release build passed
in 64.31 seconds with zero warnings and errors after two missing imports
were corrected. This is an attempted optimization. It is not yet a retained
performance fix or a complete smooth distance-cadence implementation.

| Diagnostic | Harness result | Completed Hz | Interval p95 ms | Mean late-tick body ms | Mean rest gathering ms | Mean component preparation ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Previous Hz15 | Accepted | 22.313218 | 52.0613 | 22.294 | 7.304 | 2.834 |
| Early admission, run 1 | Accepted | 21.085118 | 59.8461 | 24.793 | 7.699 | 4.413 |
| Early admission, run 2 | Accepted | 21.462904 | 56.5694 | 23.967 | 7.373 | 4.328 |
| Early admission, run 3 | Accepted | 21.653223 | 54.7638 | 24.076 | 7.418 | 4.300 |
| Current Strict control | Accepted | 19.571711 | 62.6706 | 27.043 | 8.409 | 3.271 |

The CPU stages are nested. Do not add their means. Run 1 recorded 1,076,000
capture skips and matching preparation skips across 1,112 ticks. Run 2
recorded 1,096,000 of each across 1,080 ticks. Run 3 recorded 1,064,000 of each
across 1,052 ticks. The Strict control recorded zero skips across 1,016 ticks.
The admission gate executes,
but these results show no measured gain. Rest gathering remains near the
previous cost, and component preparation is higher in all three new reduced-rate runs.
The current Strict body mean is 27.043 ms, against the earlier 24.389 ms.
System drift limits the old-baseline comparison; it does not establish the
cause of the change.

All four images from each reduced-rate run and the Strict control were viewed.
The wide images show the grid;
the near images show bent skinned meshes. Each restored source pair has
different bent poses. In run 2, the first source bends upward, while the
second has a lower, flatter bend and a different position. These images do
not count all 2,000 members or prove smooth interpolation. The Strict source
pair shows a small change in slope and position. The third reduced-rate pair
shows a stronger upward bend followed by a flatter pose. All three reduced-rate
restarts restored the source count to one.

The Strict control and third repeat are complete. A temporary private flag
and an ignored setter are being added for an admission-on/off comparison in
the same process. This measurement control must be removed after the decision.
Keep the implementation provisional; it can still be rejected. The ancestry-cache experiment remains
rejected and removed. The workgroup baseline remains 32; the invalid 64/128
windows establish no winner. The 100 Hz target is not met.

Evidence is in the `early-admission-hz15-1`, `early-admission-hz15-2`,
`early-admission-hz15-3`, and `early-admission-strict-control`
summary, world telemetry, and restored-source reports. Keep these scoped
correctness results separate from performance acceptance and from the open
fixed-tick GPU presentation design.

### Retained fence diagnostic build

Last checked: 2026-10-07 21:53 UTC. The Release build with retained fence
diagnostics and the temporary same-process admission comparison control
passed in 82.37 seconds with zero warnings and errors. The helper build
passed in 3.55 seconds.

The implemented diagnostics assign a marker rental ID, preserve its first
failure site and native submission evidence, and retain 512 value records
in the Vulkan renderer. The dispatcher copies the input ordinal or output
epoch with the marker identity before disposal. The finished contract is in
[resource lifecycle diagnostics](../../../architecture/rendering/render-pipeline-resource-lifecycle.md#retained-vulkan-fence-failures).
The existing inspection API can read the snapshots without a new MCP tool.

Runtime capture and correlation are pending. No failure cause is established
by this build result. Failure counters and reuse rules are unchanged, and
the earlier invalid workgroup windows remain invalid.

### Same-process admission comparison setup and first valid OFF window

Last checked: 2026-10-07 21:55 UTC. `admission-off-a1` is invalid as an OFF
comparison. Its setup helper reported `enabled=false` but `worlds=0`: the
physics world did not yet exist, so no world received the setting. The window
recorded 1,038,000 capture skips and matching preparation skips across 1,040
ticks. Admission therefore remained on. Harness correctness passed at
21.045436 Hz and p95 55.8750 ms, but exclude this result from the A/B comparison.

The setup guard now requires at least one world. `admission-off-a2` reported
`enabled=false` and `worlds=1`. Both skip deltas were zero across 1,056 ticks,
which confirms the intended OFF path. This window passed the harness checks
at 20.336777 Hz and p95 62.5163 ms. Mean late-tick body time was 23.8537 ms;
rest gathering was 7.5057 ms and component preparation was 3.2333 ms. These
are nested CPU stages. The ON window `admission-on-b1` is running in the same
process. Keep the result provisional until the matched comparison completes.

Both restored-source image pairs were viewed. Each pair shows a visible source
with a different pose. A1 changes from a strong upward bend to a flatter pose;
A2 changes its slope and position. These images do not prove smooth GPU
interpolation or establish the validity of A1's OFF setting.

The retained fence history increased from 82 to 104 records across A1. It
stayed at 104 across A2. The captured records contain only `UnsubmittedMarker`,
`PlanUnsubmitted`, and `RequiredProducerMissing` sites. All report
`EGpuFenceNativeSubmission.NotCalled` (value 1), with `NativeResultValid=false`.
These records describe this new diagnostic session. They do not identify the
origin of the older 64/128 workgroup failures, and they do not justify changing
failure counters or classifying all marker failures as benign.

Evidence is in the `admission-off-a1` and `admission-off-a2` admission, summary,
before/after fence, and restored-source reports. The setup failure remains part
of the attempt history; it is not a rejected implementation result.

### Same-process ON/OFF repeats, decision pending

Last checked: 2026-10-07 21:59 UTC. The next two windows used the same process
and `Hz15` profile as valid OFF window A2. Both passed harness correctness.
The table contains nested mean CPU costs per late tick, not exclusive costs.

| Window | Admission | Completed Hz | Interval p95 ms | Body ms | Rest gathering ms | Component preparation ms | Capture / preparation skips | Late ticks |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| A2 | OFF | 20.336777 | 62.5163 | 23.8537 | 7.5057 | 3.2333 | 0 / 0 | 1,056 |
| B1 | ON | 19.271190 | 60.5043 | 26.6603 | 7.9385 | 4.7142 | 894,000 / 894,000 | 984 |
| A3 | OFF | 19.825627 | 65.5226 | 24.4788 | 7.6949 | 3.3858 | 0 / 0 | 1,012 |

B1's CPU body, rest gathering, and component preparation means are higher
than both OFF windows. This supports rejection of the current optimization,
but the final ON repeat B2 is still running. Keep the decision provisional.
Do not infer smooth GPU interpolation or completion of distance-based LOD.

Both restored-source pairs were viewed. Each source is visible and changes
pose. B1 changes from an almost flat pose to a lower curved pose; A3 changes
from an upward curve to a flatter downward slope. The images do not establish
smooth interpolation.

Fence history remained at 104 records before and after both B1 and A3. The
retained sites remain `UnsubmittedMarker`, `PlanUnsubmitted`, and
`RequiredProducerMissing`, with `NativeSubmission=NotCalled` and no valid
native result. This is evidence for the captured diagnostic session only.
It does not prove the origins of the older invalid 64/128 workgroup windows.

Evidence is in the `admission-on-b1` and `admission-off-a3` summary,
before/after fence, and restored-source reports. A1 remains excluded because
its OFF setting did not reach a world.

### Early-admission prototype rejected after final repeat

Last checked: 2026-10-07 22:04 UTC. `admission-on-b2` passed harness correctness
at 15.684604 Hz and p95 78.1362 ms. Its mean late-tick body time was 24.2712 ms,
rest gathering was 7.1681 ms, and component preparation was 4.3445 ms. It
recorded 1,418,000 capture skips and matching preparation skips across 1,412
ticks. These CPU values are nested per-call measurements.

Decision: reject the early-admission prototype. The same-process sequence
shows no measured end-to-end benefit. Both ON windows have higher component
preparation cost than both OFF windows: 4.7142 and 4.3445 ms ON, against
3.2333 and 3.3858 ms OFF. B2's body mean is close to the OFF means despite
its much lower completed rate. Do not attribute all of B2's slowdown to this
change. System drift and other frame costs remain possible contributors.
The coordinator is arranging removal of the prototype and its temporary
comparison control. Keep the retained fence diagnostics. This rejects this
implementation, not the user-authorized distance-cadence feature.

Both B2 restored-source images were viewed. The source changes from a clear
upward bend to a flatter downward slope and a different position. This is
pose-change evidence, not smooth-interpolation proof.

Fence history remains 104 records before and after B2. Only the already
recorded `UnsubmittedMarker`, `PlanUnsubmitted`, and `RequiredProducerMissing`
sites occur, with `NativeSubmission=NotCalled` and `NativeResultValid=false`.
No new failure origin is shown. The old 64/128 workgroup windows remain
invalid; their origins cannot be reconstructed from this session's history.

Evidence is in the `admission-on-b2` summary, before/after fence, and
restored-source reports. Keep A1's setup failure separate from this measured
implementation rejection. The 100 Hz target and full smooth LOD remain open.

### Prototype removal and next rest-input cache experiment

Last checked: 2026-10-07 22:14 UTC. Removal of the rejected early-admission
prototype is complete. The six prototype-only Core files have no remaining
content diff. The skip counters and temporary comparison flag are removed.
The `QualityAssignment` and `GpuRestDependency` timing split and all retained
fence diagnostics remain. Build and live validation after removal are pending.

The rejected patch is saved as `reports/early-admission-rejected.patch` in the
run evidence. It also contains pre-existing timing-split changes. Treat it as
evidence only; do not replay the whole patch to restore the experiment.

The next attempted change is the explicit runtime-only
`EnableRigidGpuRestInputCache` option. Its Core path reuses root-relative rest
capture while preserving all dependency checks. It targets the measured
approximately 7 ms gathering stage. It does not change the GPU ABI, shaders,
or simulation timing. The Math source and clone flag are added. Implementation
and review are in progress, and the code item is open. No build, runtime, or
performance result exists yet. This is an experiment, not a retained gain.

### Rigid rest-input cache build and mutation checks

Last checked: 2026-10-07 22:25 UTC. The first cache build passed in 72.60
seconds, but it received no live validation before the invalidation review
correction. The revised Release build passed in 70.61 seconds with zero
warnings and errors. The updated comparison helper also built successfully.

The runtime-only option preserves the ordinary dependency checks. Review
required a child-input mutation to latch the range as blocked. Explicitly
disable and enable the option to rearm it. Root-parent affine changes remain
eligible and must preserve ordinary transform and stretch results. No GPU
ABI, shader, or simulation timing change is part of this cache experiment.

All nine `rigid-cache-*-comparison.json` reports passed. Each compared one
range of seven inputs against ordinary capture; all values were finite.

| Check | Maximum absolute difference | Blocked ranges | Candidate/reference stretch |
| --- | ---: | ---: | --- |
| Initial, before mutations, after mutations | 0 | 0 | 1 / 1 |
| Child translation, rotation, and order | 0 | 1 | 1 / 1 |
| Child scale | 0 | 1 | 1.2 / 1.2 |
| Explicit scale rearm | 0 | 0 | 1 / 1 |
| Root-parent affine change | 1.1920929e-7 | 0 | 1.3989542 / 1.3989542 |

These reports support the bounded input-equivalence and invalidation checks.
They do not establish performance or cover every hierarchy mutation. The first
cache-OFF Strict 2,000-chain diagnostic window is running. Keep the experiment
undecided until the matched ON/OFF evidence is available. No benefit is claimed.

### Rigid cache first OFF timing window

Last checked: 2026-10-07 22:26 UTC. `rigid-cache-off-a1` passed harness
correctness at 18.511600 Hz and p95 65.8224 ms with 2,000 chains on the Strict
profile. Across 964 late ticks, mean body time was 28.73283 ms, gathering was
9.17030 ms, and dependency preparation was 5.27137 ms. These CPU costs are
nested. Cache hit and miss deltas were zero, as required for the OFF path.

All four referenced images were viewed. The wide grid is visible and the near
meshes are bent. The restored source pair has a different slope and position.
These images do not count all members or prove smooth interpolation. After
restoration, the source cache was enabled and the seven-input comparison
passed: finite values, no blocked range, maximum absolute difference
1.1920929e-7, and matching stretch 1.

The matched ON window B1 is running in the same process. This OFF result is
a baseline, not evidence of cache benefit. Retention remains undecided.
Evidence is in the `rigid-cache-off-a1` summary, cache, matrix-comparison,
and restored-source reports.

### Rigid cache first ON timing window

Last checked: 2026-10-07 22:28 UTC. `rigid-cache-on-b1` passed at 20.536405 Hz
and p95 59.6709 ms. Across 1,024 late ticks, mean body time was 23.80021 ms,
gathering was 6.18292 ms, and dependency preparation was 4.87492 ms. These
are nested CPU stages. The window recorded 2,040,000 cache hits, zero misses,
and zero blocked ranges. Against OFF A1's 18.511600 Hz and 9.17030 ms gathering,
this is a preliminary improvement. It is not a final retention decision.
Repeated OFF A2 is running in the same process, with ON B2 still required.

All four images were viewed. The grid and bent near meshes are visible.
The restored source changes from a curved, downward pose to an upward slope.
The images do not count all members or prove smooth interpolation.

The before source-witness report contains one cache with six subscriptions.
The after report passes with one witness, zero alive, zero subscribed, and
zero subscriptions. This supports retirement of that observed cache instance.
It is not a general lifetime proof. Fence history stayed at 135 records, with
the same `UnsubmittedMarker`, `PlanUnsubmitted`, and `RequiredProducerMissing`
sites and `NativeSubmission=NotCalled`. The last retained rental remains
100735. These snapshots do not resolve the earlier workgroup failures.

Evidence is in the `rigid-cache-on-b1` summary, source-witness, before/after
fence, and restored-source reports. Keep the cache decision open until the
matched repeats complete.

### Rigid cache repeated OFF window

Last checked: 2026-10-07 22:31 UTC. `rigid-cache-off-a2` passed at 20.260129 Hz
and p95 55.8190 ms. Across 1,096 ticks, cache hit, miss, and blocked-range deltas
were zero. The current same-process comparison is:

| Window | Completed Hz | Interval p95 ms | Mean body ms | Mean gathering ms | Mean dependency preparation ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| OFF A1 | 18.511600 | 65.8224 | 28.73283 | 9.17030 | 5.27137 |
| ON B1 | 20.536405 | 59.6709 | 23.80021 | 6.18292 | 4.87492 |
| OFF A2 | 20.260129 | 55.8190 | 25.10158 | 7.21414 | 4.472225 |

CPU stages are nested. A1 was slower than A2 without the cache, which shows
variation between windows. Do not attribute all of B1's gain over A1 to the
cache. B1 gathering is lower than both OFF values, but final ON repeat B2 is
still running. Retention remains undecided.

All four A2 images were viewed. The grid and bent near meshes are visible;
the restored source pair changes its slope and position. The restored cache
comparison passed with seven finite inputs, zero blocked ranges, maximum
absolute difference 1.1920929e-7, and matching stretch 1. Fence history stayed
at 135 records with the same last rental 100735 and the same known sites.
No earlier failure origin is established. Evidence is in the
`rigid-cache-off-a2` summary, matrix-comparison, fence, and restored-source
reports. These image checks do not prove all-member visibility or smooth LOD.

### Rigid cache complete A/B/A/B timing set

Last checked: 2026-10-07 22:33 UTC. `rigid-cache-on-b2` passed harness
correctness. All four windows used the Strict profile in the same process.
CPU costs below are nested means per late tick.

| Window | Completed Hz | Interval p95 ms | Body ms | Gathering ms | Dependency preparation ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| OFF A1 | 18.511600 | 65.8224 | 28.73283 | 9.17030 | 5.27137 |
| ON B1 | 20.536405 | 59.6709 | 23.80021 | 6.18292 | 4.87492 |
| OFF A2 | 20.260129 | 55.8190 | 25.10158 | 7.21414 | 4.472225 |
| ON B2 | 21.342219 | 54.3646 | 22.910193 | 5.900462 | 4.677170 |

B2 recorded 2,200,000 hits, zero misses, and zero blocked ranges across
1,100 ticks. Both ON gathering means are lower than both OFF means. This
repeats the targeted stage reduction. OFF A1/A2 variability still limits the
size of the gain that can be assigned to the cache. Keep the candidate decision
pending the remaining reparent and lifecycle checks. The initial mutation
checks have passed. Neither 100 Hz nor complete smooth LOD is achieved.

All four B2 images were viewed. The wide grid and bent near meshes are visible.
The restored source changes from an upward bend to a flatter downward slope.
The restored matrix comparison passed with seven finite inputs, zero blocked
ranges, maximum absolute difference 1.1920929e-7, and matching stretch 1.
Fence history remained at 135 records, with last rental 100735 unchanged and
the same known unsubmitted sites. No earlier failure origin is established.

A sampled six-part diagnostic timer is planned to separate remaining gathering
and dependency costs. This is diagnostic work only; no further optimization
or result is implied. Evidence is in the `rigid-cache-on-b2` summary,
matrix-comparison, fence, and restored-source reports.

### Rigid cache full-batch input and retirement check

Last checked: 2026-10-07 22:36 UTC. A third cache-ON benchmark start compared
all 2,000 ranges and 14,000 matrices with ordinary capture. The summary reports
zero failures and maximum absolute difference 1.5258789e-5. This was a
correctness check; it adds no timing claim to the A/B/A/B set.

The before witness report counted 2,000 caches and 12,000 tracked child
registrations. Each registration uses two filtered handlers, so the reported
registration count is not a raw delegate count. After teardown, 2,000 witnesses
were still alive, but `subscribed` and registration counts were both zero.
The retirement check passed. This proves removal of the observed registrations,
not garbage collection of those objects.

Both wide images and both restored-source images were viewed. The grid remains
visible in both wide views. The restored source changes its slope and position.
These images do not count all visible members or prove smooth interpolation.
The all-range count comes from the input comparison report.

Reparent/reset checks remain before the keep decision. The sampled six-part
timer source is written and review is pending. No new timer result or further
optimization is claimed. Evidence is in the `rigid-cache-all-inputs` summary,
comparison, before/after witness, wide, and restored-source reports.

### Rigid cache reparent/reset checks and keep decision

Last checked: 2026-10-07 22:38 UTC. Moving `Bone6` under `Bone4` set
`BlockedRanges=1`. World capture rejected the unsupported hierarchy, and
`HierarchyInputDependency` rose from 0 to 74. The comparison report has
`captured=false` and `passed=false` because no capture was admitted. This is
the expected route rejection, not an input matrix equality failure.

Restoring the parent and rearming returned seven finite inputs, zero blocked
ranges, maximum absolute difference 1.1920929e-7, and matching stretch 1.
Disabling and enabling the source passed the reset check. The old cache
witness remained alive but had zero subscriptions and registrations. The
new seven-input capture passed with the same maximum difference. Both reset
restored-source images were viewed; the visible source changes slope, bend,
and position. No smooth-interpolation claim follows from these images.

Decision: keep the explicit `EnableRigidGpuRestInputCache` option. Both ON
windows reduced gathering against both OFF windows, and the full-batch input,
child mutation, rearm, reparent rejection/recovery, and registration retirement
checks passed. This is bounded validation of the explicit cache. The 100 Hz
target, complete quality coverage, and smooth distance LOD remain open.

The sampled six-part diagnostic timer passed review. Rebuild and runtime
measurement are pending. This adds diagnostic detail only, not another
optimization. Evidence is in the `rigid-cache-reparent-*` and
`rigid-cache-reset-*` reports and restored-source images.

### Sampled rest-input diagnostic

Last checked: 2026-10-07 22:44 UTC. The sampled telemetry Release build passed
in 98.85 seconds with zero warnings and errors. `rigid-cache-sampled-1` passed
harness correctness at 19.671051 Hz and interval p95 68.8781 ms. Mean late-tick
body time was 26.39866 ms, rest gathering was 6.84516 ms, and dependency
preparation was 5.35305 ms. These stage timers are nested. Do not add them or
treat concurrent costs as exclusive frame time. This instrumented window does
not establish a timing benefit from the sampled build.

| Sampled rest-input stage | Actual samples | Mean microseconds per sample |
| --- | ---: | ---: |
| Opaque dependency check | 62,996 | 1.881916 |
| Owner dependency check | 62,996 | 0.785317 |
| Collider dependency check | 62,996 | 1.773095 |
| Root preparation | 62,996 | 0.851897 |
| Cached input expansion | 62,996 | 0.315760 |
| Input publication | 62,996 | 0.206396 |

These means describe sampled operations, not complete update totals. The
window recorded 1,992,000 cache hits, zero misses, and zero blocked ranges.
The next bounded source review will inspect repeated hierarchy walks. No
optimization follows from this diagnostic alone.

All four images were viewed. The wide grid and bent near meshes are visible.
The restored source changes from a gentle bend to a strong downward bend,
with a different position. These images do not prove all-member visibility or
smooth interpolation.

Fence history grew from 111 to 135 records. Comparing rental identities found
24 new records, all with `Site=PlanUnsubmitted`,
`NativeSubmission=NotCalled`, and `NativeResultValid=false`. Their failure
frames range from 2637 through 3463. Keep these observed origins separate from
the earlier 64/128-thread failures, whose origins were not retained. Do not
classify these failures as benign. Evidence is in the
`rigid-cache-sampled-1-*` summary, telemetry, fence, and image reports.

### Pending phase-local collider dependency proof

Last checked: 2026-10-07 22:52 UTC. The next attempted optimization targets
repeated collider ancestor walks during GPU rest-input preparation. The
sampled collider dependency check averaged 1.773095 microseconds per operation,
while the whole dependency preparation stage averaged 5.35305 ms per late
tick. These are different scopes. Neither gives a proven saving for the
proposed change.

The proposed `PhysicsChainWorld.GpuInputs` path records the collider references
used by owner-marking ancestor walks in a per-range witness. Capture can reuse
the result only when the phase, handle, ownership generation, list identity,
list count, each element, activity, `Transform`, `RootTransformOverride`, and
type-specific `ColliderTransform` still match. Publication requires a complete
valid walk of built-in collider types with no forced slot. Every mismatch
runs the original fresh `HasGpuRestColliderDependency` path. No proof survives
the phase boundary.

Implementation and review are pending. Matching and mutation/fallback checks
must precede a retention decision. No success, performance gain, or quality
change is claimed. The pending manual check is in the
[rigid rest input cache validation](../../testing/physics/physics-validation.md#rigid-rest-input-cache).

### Priority change: isolate physics and rendering before more optimization

Last checked: 2026-10-07 22:52 UTC. The user's critique is accepted. The API
trace shows gaps between GPU submissions, but it does not identify the CPU
critical thread, the work on that path, or its waits. The cache repeats a
gathering-stage reduction in the diagnostic comparison. End-to-end frame-rate
gain remains unproven because the windows vary.

Defer the phase-local collider proof. Its attempted source is being removed;
it was not built or validated. It is not a retained optimization. The proposed
witness contract above records the attempted design, not finished behavior.

First compare frozen physics with mesh rendering retained. Then compare
active physics with mesh rendering off. Keep the full scene as the control
and label each ablation. Next collect CPU scheduling and stack evidence to
identify the critical thread and blocking dependencies. Do not use summed
nested timers or a GPU submission gap as that proof. Grouped GPU indexed-instance
draws already exist, with native all-member evidence. Do not describe this
scene as lacking instancing.

The current cache-OFF control window is running. No new ablation or thread
timeline result is available at this boundary.

### Invalid clean cache window and ablation timeline setup correction

Last checked: 2026-10-07 22:59 UTC. `rigid-cache-clean-off-a1` is invalid.
The harness recorded 42 completed and two rejected outcomes during readiness
or measurement and set `accepted=false`. Do not use this result in a cache
comparison. No further cache A/B window is planned; the ablations now take
priority.

The first full-scene control, `ablation-full-a1`, held 2,000 chains before and
after its timed interval. It completed 580 frames in 30.237905 seconds at
19.181223 Hz, with p95 64.6672 ms and zero new deferred, rejected, or failed
outcomes. This is a diagnostic control, not target acceptance.

The later `ablation-full-timeline.json` does not describe that population.
Before timeline capture, the benchmark reached its 120-second maximum and
automatically restored the single source. Exclude that timeline from
2,000-chain evidence. The `SetChainVisibility` guard found one chain and
changed nothing. The `ablation-full-wide` image was viewed: it shows the test
label on a black background, with no full grid visible. It is an invalid
single-source capture for this comparison. The large invalid timeline was
not analyzed.

The scratch `start-ablation.ps1` helper now holds only the benchmark duration
stopwatch after settling. It does not pause simulation time. A fresh
2,000-chain batch is starting to repeat the evidence collection with a valid
population. No corrected ablation or timeline result is claimed yet.

WPR could not collect the planned scheduling trace. `wpr -start CPU -filemode`
returned "Failed to enable policy to profile system performance" with code
`0xc5585011`; status reports no active recording. This is an OS privilege
failure, not an automatic approval rejection. A scratch `CriticalTimelineProbe`
will use existing `CodeProfiler` absolute scope timestamps. It can correlate
instrumented scopes, but it does not provide kernel scheduling events or
sampled call stacks.

### Active physics with chain rendering hidden

Last checked: 2026-10-07 23:03 UTC. `ablation-render-hidden-b1` retained
2,000 chains before and after its 30.497386-second window. It completed
1,797 frames at 58.923083 Hz, with interval p95 23.5708 ms and zero new
deferred, rejected, or failed outcomes. The earlier full-scene control was
19.181223 Hz with p95 64.6672 ms.

Physics remained active. Palette slices and renderer bindings remained at
2,000. Output producer epoch advanced from 31803 to 33635; input acquisitions
advanced from 31806 to 33638. Both deltas are 1,832. Input and output failures
remained at nine, dispatch failures remained at zero, and physics readback
remained at zero.

This ablation removes the whole chain rendering path: scene collection,
publication, deformation, and drawing. It is not a raster-only comparison.
The result supports further investigation of rendering-path CPU and GPU work,
but it does not identify the critical thread or separate these costs. It
does not meet 100 Hz or validate the full-scene quality target.

Four images were viewed. `ablation-full-wide-held` shows the grid, and
`ablation-hidden-wide` shows no chains. The immediate `ablation-show-wide`
image is black. The later `ablation-frozen-wide`, taken after about ten
seconds of warmup, shows the grid again. Settled visual restoration passed;
the immediate image alone did not prove it. Two frozen state checks two
seconds apart retained 2,000 chains and bindings, producer epoch 35417,
input acquisitions 35420, and unchanged failures. Frozen-physics timing is
still pending.

A separate valid 2,000-chain timeline captured 156,959 scopes in 341 snapshots.
Its analysis is in progress. Keep it separate from the excluded single-source
timeline. The batch remains active with only its duration stopwatch held.

### Valid 2,000-chain scope timeline

Last checked: 2026-10-07 23:04 UTC. The compact
`ablation-full-2000-timeline-analysis.json` covers 12.005951 seconds,
341 snapshots, and 156,959 recovered scopes. It includes 252 render scopes.
The raw timeline was not needed for this summary.

| Scope or relation | Mean wall ms | Median wall ms |
| --- | ---: | ---: |
| Render start spacing | 47.320 | Not reported |
| `EngineTimer.DispatchRender` | 30.243 | 28.7649 |
| `DispatchCollectVisible` | 25.319 | 24.4265 |
| Collection overlap with render | 25.053 | Not reported |
| `WaitForRender` | 5.081 | 4.8487 |
| `DispatchSwapBuffers` | 16.914 | 16.6715 |
| Gap between render scopes | 17.089 | Not reported |
| Swap within that gap | 16.813 | Not reported |
| Swap publication to next render | 0.035 | Not reported |

The observed start spacing is about 21.13 Hz. The overlap and gap pattern
supports `max(render, collect) + serial swap` as the instrumented cycle.
Update duration alone does not explain this render cadence. The next source
review should inspect the render path and serial swap/publication work.

| Producer path | Largest residual scope | Mean exclusive wall ms per call | Calls |
| --- | --- | ---: | ---: |
| Render | `XRWindow.GlobalPreRender` | 10.544306 | 252 |
| Render | `Vulkan.FrameLifecycle.RecordCommandBuffer` | 6.702438 | 253 |
| Render | `Advanced.VisibilityPreparation` | 6.176109 | 252 |
| Render | `Vulkan.RecordPrimary.MainOpLoop` | 2.588775 | 253 |
| Collection | `VisualScene3D.CollectRenderedItemsGpu` | 6.167977 | 504 |
| Collection | `RuntimeWorldRenderer.GlobalPreCollectVisible` | 8.946527 | 252 |
| Swap/publication | `GpuIndirect.AdvancedPublication.ScenePlan` | 6.962563 | 253 |
| Swap/publication | `RuntimeWorldRenderer.GlobalSwapBuffers` | 4.589236 | 253 |
| Swap/publication | `CpuBvhRenderTree.Swap` | 1.628615 | 505 |

Collection and BVH swap occur about twice per rendered cycle. Do not confuse
their per-call means with per-cycle totals. These names identify source-review
targets; they do not establish a specific faulty algorithm.

All 341 snapshot publications were incomplete. The report records maxima of
39 active, 49 queued, and 178 pending scopes, zero unresolved linked children,
and 341 stale completed scopes. Exclusive time here subtracts recovered
instrumented children only. Residual time can include uninstrumented work,
waits, and missing children. These are diagnostic wall times, not CPU-on-core
time or OS scheduling evidence. Do not sum concurrent thread totals. The
frozen-physics C1 comparison is still measuring and has no result here.

### Frozen physics with chain rendering retained

Last checked: 2026-10-07 23:05 UTC. `ablation-physics-frozen-c1` completed
753 frames in 30.069258 seconds at 25.042188 Hz, with interval p95 53.021 ms.
There were zero new deferred, rejected, or failed outcomes. The window kept
2,000 chains before and after, with 2,000 renderer bindings. Output producer
epoch stayed at 35417, input acquisitions stayed at 35420, and input/output
failure counts stayed at nine.

The ablation removes physics callbacks, but root motion remains active and
CPU root/hierarchy work continues. It is not an all-static scene. Do not
infer an all-static frame-rate ceiling or assign all remaining cost to drawing.
The earlier full control was 19.181223 Hz; active physics with chain rendering
hidden was 58.923083 Hz. These first-cycle diagnostic results require repeats
and do not meet the full-scene 100 Hz target.

Callbacks were restored before teardown. The source returned to one chain.
Both `ablation-first-cycle-restored-source` images were viewed: the source
changes position and bend. This confirms visible restored-source motion,
not continuous-motion quality for the full grid.

The second cycle is running three 45-second windows, with a third cycle
planned. No production edits came from these ablations. The next structural
decision is whether `ScenePlan` can separate stable registration work from
pose and bounds publication while preserving invalidation and ownership.
That is a source-review candidate, not an implemented fix or measured gain.

### Second ablation cycle and measurement limits

Last checked: 2026-10-07 23:11 UTC. All three `ablation-cycle2` windows kept
2,000 chains and recorded zero new bad native outcomes or input/output
failure deltas.

| Mode | Elapsed seconds | Completed frames | Completed Hz | Interval p95 ms |
| --- | ---: | ---: | ---: | ---: |
| Full scene | 50.745166 | 841 | 16.573007 | 73.4802 |
| Chain rendering hidden | 45.143268 | 2,346 | 51.967882 | 26.4986 |
| Physics callbacks frozen | 45.361378 | 591 | 13.028705 | 84.9136 |

The frozen window retained producer epoch 43589 and 2,000 bindings. Root
motion and CPU hierarchy work continued. Its lower rate than the full scene
weakens any quantitative claim of benefit from freezing physics. Do not
average the two frozen cycles into a small claimed improvement. The broad
increase when chain rendering is hidden repeats, but its exact size still
needs the improved measurement method.

The first two cycles used a client stopwatch and separate counter queries.
Transport latency means the counter boundaries were not atomic with that
clock. Keep these method limits explicit; they do not discard the repeated
large rendering-hidden effect. Cycle 3 uses an engine-side meter with counter
timestamps, GPU clock samples, GC counts, and process CPU evidence. Its
45-second full-scene baseline is running; no result is recorded here.

The hidden, frozen, and two restored-source images were viewed. The hidden
view shows no chains, the frozen view shows the grid, and the restored source
changes bend and position. The restored canonical resident draw count is one.
The next source-review priority remains incremental stable registration in
`ScenePlan`, not another local physics micro-optimization.

### Engine-side ablation meter first results

Last checked: 2026-10-07 23:15 UTC. Cycle 3 uses engine-side counter captures
bracketed by stopwatch timestamps. The full captures took 2.136 and 1.005 ms;
the frozen captures took 1.501 and 0.240 ms. This bounds local snapshot skew
and removes client transport time from the elapsed interval. It does not make
all counters a simultaneous atomic hardware measurement.

| Mode | Elapsed seconds | Completed frames | Completed Hz | Interval p95 ms | Producer epoch delta |
| --- | ---: | ---: | ---: | ---: | ---: |
| Full scene | 45.137701 | 737 | 16.327814 | 70.5093 | 736 |
| Physics callbacks frozen | 45.117139 | 975 | 21.610413 | 57.0191 | 0 |

Both summaries are valid, with 2,000 registered chains, slices, and bindings
before and after. Bad native outcomes, input/output failures, dispatch failures,
and physics readback did not increase. Frozen producer epoch remained at 57148.
Root motion and hierarchy work still continued. The third rendering-hidden
window is pending. No production change came from these ablations.

The full window allocated 978,770,192 managed bytes process-wide and recorded
78/4/0 generation 0/1/2 collections. Process CPU time averaged 2.732610 logical
core equivalents. The frozen window allocated 1,365,504,616 bytes, recorded
109/5/0 collections, and averaged 3.745459 core equivalents. These are whole
process counters, not physics allocation or critical-thread attribution. The
windows also completed different frame counts. Do not assign their allocation
or CPU difference to physics alone.

The continuous GPU clock CSV files are empty, so full-window clock evidence
is unavailable. The frozen before/after samples changed from graphics/memory
495/810 MHz at P5 to 1560/8101 MHz at P0. This proves varying endpoint states,
not the clock distribution within the window or the cause of all timing
variation. Do not claim clocks were controlled.

Both cycle 3 images were viewed. The frozen image shows the grid, and the
hidden image shows no chains. A separate 12-second frozen scope timeline is
available for worker analysis. Its findings are not included in this result.

### Frozen physics scope timeline comparison

Last checked: 2026-10-07 23:18 UTC. The compact
`ablation-frozen-2000-timeline-analysis.json` covers 12.009805 seconds,
340 snapshots, 934,421 recovered scopes, and 275 render scopes. The batch
still contained 2,000 chains after capture. The raw timeline was not read
for this summary.

| Wall-time measure | Full mean ms | Frozen mean ms |
| --- | ---: | ---: |
| Render start interval | 47.320 | 43.442 |
| Render scope | 30.243 | 30.186 |
| Collection scope | 25.319 | 21.921 |
| Serial swap scope | 16.914 | 13.316 |
| `RuntimeWorld.Update` callback per call | 26.527 | 1.583 |
| Fixed-update dispatch per call | 9.764 | 0.0075 |
| `XRWindow.GlobalPreRender` residual per call | 10.544 | 0.347 |
| `Advanced.VisibilityPreparation` residual per call | 6.176 | 19.466 |
| Command-recording residual per rendered cycle | 6.729 | 5.371 |

The frozen render gap averaged 13.254 ms, with 13.201 ms occupied by swap.
Publication-to-render delay averaged 0.038 ms. World callback cost falls,
but the observed cycle still contains render work followed by serial swap.
The distribution of render work also changes: pre-render falls while
visibility preparation rises. Do not select one fixed hotspot or assign the
whole interval change to physics from these comparisons. GPU clock state
varied, and these are wall scopes rather than on-core measurements.

All 340 frozen snapshot publications were incomplete. Exclusive residuals
subtract only recovered instrumented children and can include missing work
or waits. The different scope populations also limit direct comparison.
The full and frozen process allocation totals normalize to approximately
1.33 and 1.40 MB per completed frame, respectively. These remain process-wide
ratios, not proven render-specific or physics-specific allocation.

The third hidden 45-second window is running. No production code change or
timing-target claim follows from this diagnostic comparison.

### Third hidden result and ablation restoration

Last checked: 2026-10-07 23:19 UTC. The engine-side
`ablation-cycle3-hidden-summary.json` is valid. It completed 2,597 frames in
45.288487 seconds at 57.343492 Hz, with interval p95 24.2503 ms. The window
kept 2,000 chains, palette slices, and bindings. Producer epoch advanced by
2,454. Bad native outcomes, input/output failures, dispatch failures, and
physics readback did not increase. Hidden render state had zero resident
draws and four commands; requested and resolved strict submission values
both remained 2.

Process-wide allocation increased by 640,818,504 bytes. Generation 0/1/2
collection deltas were 52/6/1, and process CPU averaged 2.409208 core
equivalents. The before and after snapshot brackets took 3.0644 and 1.7122 ms.
These counters describe the process, not isolated physics or rendering work.

| Cycle | Full scene Hz | Hidden chain rendering Hz | Frozen physics Hz |
| --- | ---: | ---: | ---: |
| 1, client timer | 19.181223 | 58.923083 | 25.042188 |
| 2, client timer | 16.573007 | 51.967882 | 13.028705 |
| 3, engine-side timer | 16.327814 | 57.343492 | 21.610413 |

Whole-chain rendering removal has a large repeated effect. It removes more
than raster work. Frozen-physics results remain variable and do not establish
a stable quantitative benefit. None of these modes meets 100 Hz, and ablated
modes cannot satisfy full-scene quality acceptance. Controlled-clock hardware
and complete scheduling/profile acceptance remain open.

Physics callbacks, mesh visibility, the duration-clock hold, and the camera
were restored before teardown. The source count returned to one. The
disposable session retained benchmark UI copy/duration values of 2000/120;
not every UI setting returned to its original value. Both cycle 3
restored-source images were viewed; the
source changes from a tight downward bend to a long upward curve and moves
position. The final time-state query reported no terminal fault, running true,
paused false, and render time 6.13 ms after source restoration. The owned
session is closing; final existing tests are next. No production optimization
came from the ablations.

### Final validation and scoped allocation exclusion

Last checked: 2026-10-07 23:23 UTC. The owned editor is confirmed stopped in
`logs/ablation-final-session-stop.log`. All 40 existing targeted tests passed
on final source in 9.9753 seconds, as recorded in
`logs/physics-chain-post-ablation-tests.log`. No tests were added or changed.
The full test suite was not run. Test-log warning review remains separate.

The owned process session-log scan found zero matches for `Unhandled`,
`DeviceLost`, `VK_ERROR`, terminal-fault, `[Error]`, and `Exception` patterns.
This bounded scan does not prove that all warnings or other failures are absent.

The compact `advanced-preparation-scoped-existing-windows.json` derives
Advanced preparation deltas from saved render-state snapshots:

| Earlier window | Builds | Scoped allocated bytes | Build ms per call | Extract ms per call | Deform ms per call |
| --- | ---: | ---: | ---: | ---: | ---: |
| Cache OFF A2 | 736 | 0 | 5.728234 | 4.089726 | 0.122271 |
| Cache ON B2 | 751 | 0 | 5.949250 | 4.379416 | 0.113800 |
| Sampled cache diagnostic | 721 | 0 | 6.267012 | 4.564788 | 0.125094 |

These earlier warm windows are not the cycle 3 process-allocation window.
They provide no basis to assign its process-wide allocation to the shared
Advanced build scope. They also do not prove that all rendering allocation
is zero. Keep allocation profiling as a separate next measurement.

The current structural priority is stable scene registration and per-draw/group
templates, with pose and bounds publication kept separate. No new structural
optimization was implemented or measured by these ablations. The 100 Hz
target remains unmet.

### Retained registration lookup after the next pull

Last checked: 2026-10-08 05:22 UTC. The resumed checkout starts at
`8a30b32a6f32129749ed35378170b793be176e63`. The pulled source already contains
active resource admission and operation-owned sealed compute snapshots. Do
not repeat those repairs. This run used a Ryzen 9 7950X3D and RTX 3090 with
driver 617.14. Earlier laptop results are not a matched control.

The new change retains the publisher registration hash and ordered
source/primitive/registration/draw identities. Exact source identity and draw
generation checks guard reuse. Membership changes, lookup growth, rejection,
and failed identity delivery invalidate it. Cache promotion follows successful
commit and source identity delivery. Review found retained references beyond
the live array after shrink; the final code clears that tail and clears the
array on disposal. It also avoids a redundant full promotion when every row
has matched. Existing material and transaction handle checks remain active.

Final review found one more cleanup case. A growth promotion can write new
rows, fail, and leave the old live count unchanged. A later shrink would then
miss those new references. The final correction marks the cache invalid and
records the full planned cleanup extent before writing rows. It preserves the
warm early return. This correction followed the live windows below. The final
Rendering Release build passed in 14.86 seconds with zero warnings and errors.
Fault injection remains open.

Current poses, bounds sources, command metadata, materials, geometry, temporal
state, resources, and transaction capacity still pass through the existing
capture and preflight. Source identity grouping and structural transaction
planning remain uncached. No new frame lease is stored in the identity cache.

Both isolated Release editor builds passed with zero warnings and errors.
The first baseline timing attempt failed when native frame rejection counters
changed. Its retained reason names pending directional-shadow compute pipeline
compilation. Startup also recorded bounds-page mismatches and input marker
failures. The warm repeats did not add those failures. Keep the first attempt
excluded; it does not establish a steady-state regression.

All accepted windows used 2,000 registered chains, Strict quality, Discrete
presentation, Vulkan Advanced, and requested/resolved
`GpuIndirectZeroReadback`. Directional-shadow admission was required. World
telemetry, the four Debug profiler observers, and RenderDoc were off during
timing. Captures followed timing.

| Window | Completed frames | Elapsed seconds | Completed Hz | Interval p95 ms |
| --- | ---: | ---: | ---: | ---: |
| Baseline warm | 732 | 30.182714 | 24.252292 | 47.4651 |
| Baseline repeat | 673 | 30.429021 | 22.117044 | 53.0824 |
| Candidate | 630 | 30.191570 | 20.866752 | 58.9833 |
| Candidate repeat after restart | 728 | 30.424309 | 23.928235 | 52.2009 |

Each accepted window had zero new deferred, rejected, or failed frames, input
buffer growth, input failures, and physics readback. Current physics output
continued to advance. The candidate snapshots show 2,002 reused command
identity rows, zero registration hash rebuilds, 2,000 supported resident draws,
2,000 static draw-plan reuses, and zero material resolution attempts.

The frame-rate results do not establish an improvement. CPU clocks were not
controlled. The baseline continuous GPU clock file was empty. A later polling
capture recorded candidate P0 operation at 1,935 MHz graphics and 9,751 MHz
memory, but this does not repair the missing matched baseline. All six timing
images were viewed. They show the chain grid and bent meshes. The last repeat
overview clips front rows, so equal visible coverage is not proven. Native
member counts were not recaptured in this run.

Separate 12-second scope captures used an ignored runtime probe and enabled
frame logging only during capture. It deduplicated completed scopes by session
epoch and scope ID, then restored the prior logging value.

| Scope sample | Captured snapshots | ScenePlan calls | Mean ms | Median ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline | 338 | 270 | 7.247619 | 7.2065 | 8.0514 |
| Candidate after restart | 337 | 287 | 6.970134 | 6.9424 | 7.5047 |

Every snapshot reported incomplete scope publication. These are wall-time
samples of completed scopes, not complete CPU attribution or unobserved frame
timing. The observed mean decrease is 0.277485 ms. Keep the narrow cache because
it removes confirmed repeated work and passes the live checks; do not claim an
end-to-end speed gain. One candidate scope attempt lost the MCP connection
across the interrupted turn and produced no usable result. The same built
binary was restarted before the valid capture.

The one-chain mutation check restored one chain, one palette slice, and one
renderer binding after the scale run. Deactivation reduced all three counts
to zero. Reactivation reused draw slot 1 with generation 3 instead of generation
2. A command material override changed the canonical material from slot 1,
generation 3 to slot 2001, generation 2. Removing it returned slot 1 with
generation 4. The draw retained generation 3 through the material changes.
Each active case returned to identity reuse with zero lookup rebuilds and no
publication rejection. Four source images were viewed; the source remained
visible and its bend changed. Final scale teardown again restored the three
counts to one, with zero physics readback.

The existing scene, shared-scene, geometry, and material contract test filter
passed 24 of 26 tests. Both failures are direct
`AdvancedMaterialDatabaseContractTests` dirty-range assertions. The fixed-slot
implementation marks eight constant words per material. The tests expect
12 words instead of 16 for two rows, and four instead of eight for replacement.
The database and test files are unchanged by this patch. The tests do not call
the publisher. Review also found texture range expectations that use payload
length rather than the current two-entry slot. No tests were added or changed.
Correct these contract expectations when test work is cleared; do not change
production slot clearing to satisfy the old expectations.

Evidence is under `Build/_AgentValidation/20261006-195917-chain-scale`:
`reports/scene-plan-*-summary.json`, `reports/scene-plan-scope-*.json`,
`reports/scene-cache-*.json`, and `reports/scene-plan-contracts.trx`.
The next source change is retained source identity grouping and structural
transaction planning. Exact primitive-count and removal delivery checks must
remain. Failure injection, compaction, renderer replacement, multi-consumer
checks, matched visibility, and controlled timing remain open.

Both named editor sessions are stopped. The final session-log scan found no
matches for unhandled exceptions, device loss, Vulkan error codes, terminal
faults, `[Error]`, or `Exception`. This bounded scan does not prove that all
warnings or other failures are absent.

## Retained Source Groups And Identity Delivery

Last checked: 2026-10-08 05:54 UTC. Same checkout and RTX 3090 host as the
registration lookup comparison above.

`AdvancedGpuScenePublisher.SourceGroups` now retains unique source groups and
flat primitive draw-handle slices. Exact ordered source, primitive count,
support state, membership generation, and complete draw handles control reuse.
A mismatch rebuilds the whole grouping from the current captured plans and
active registrations. Late boundary growth also forces a cold rebuild. Source
capture, material and geometry checks, poses, bounds, output witnesses, and
transaction capacity checks remain active. The architecture document owns the
full contract.

Failed committed identity delivery retains every recipient and its required
clear length, including removed sources and recipients reached before a
callback throws. Preflight reserves retry capacity. Retry recipients survive
rejection. Successful full delivery clears the list. Membership-change and
retry groups are not promoted. The next stable publication builds a reusable
plan. No frame resource or output-page lease is cached.

The bounded review found one further exception boundary: the profiler scope
could throw on disposal after database commit but before the publisher recorded
acceptance. The final source records the committed state and hides the current
image inside that scope, immediately after successful commit. This correction
followed the timing windows below. It does not change the normal delivery path.

### Live source transitions

The named Release session was `chain-identity-groups`. The first isolated
Editor build passed with zero warnings and errors in 62.92 seconds. The next
build included exact group counts and late-preflight invalidation. It passed
with zero warnings and errors in 21.72 seconds.

An ignored diagnostic assembly observed the actual fixture command. It changed
the renderer from one primitive to two, then restored it. Two primitives had
two resident draws and a two-entry identity set. Restore returned to one entry
and one draw. Removal delivered an invalid handle. Reactivation reused draw
slot 1 with generation 2 instead of generation 1. Settled snapshots had three
source groups, zero group rebuilds, and zero pending recipients. Removed state
had two groups and zero draws. Both update and render identity snapshots
matched the expected state. The source PNGs were viewed; the mesh remained
visible and changed bend.

The first helper attempt forgot to activate the initially inactive fixture.
It reported zero resident draws and no source identity. The helper now
activates the source before checking it. This was a setup failure.

### Callback exception limit

A second ignored probe threw once from `PropertyChanged`, after the update
identity changed during removed-source clear delivery. At 05:42:55 UTC,
`get_time_state` reported `isRunning=false` and a terminal
`CollectVisibleThread` / `DispatchSwapBuffers` fault with the exact injected
exception. Main-thread MCP requests then timed out. The named session was
stopped and rebuilt. The exception was not hidden and no CPU fallback was
added.

This check proves the callback reaches the existing terminal-fault path. It
does not prove a later caller retry clears retained recipients, because the
normal editor scheduler stops. Keep that runtime limit explicit. Direct
publisher retry, repeated failures, and retry-capacity cases need contract
tests after clearance. Do not change the timer error policy to make the probe
pass.

### Scale and scope evidence

All timing windows used 2,000 chains, Vulkan Advanced rendering,
`GpuIndirectZeroReadback`, directional shadows, and the four profiler observer
preferences disabled. The source profile was Strict and Discrete.

The first window was rejected after 530 completed frames and one rejected
frame. Its latest native failure was `MeshMaterialization` with
`visible-mesh-cold-admission`, 15 deferred requests, and no unavailable request.
It is excluded from timing acceptance.

| Window | Completed frames | Seconds | Completed Hz | Interval p95 ms |
| --- | ---: | ---: | ---: | ---: |
| `identity-groups-candidate-warm` | 715 | 30.2478504 | 23.6380 | 48.5035 |
| `identity-groups-candidate-repeat` | 761 | 30.0932644 | 25.2881 | 46.0564 |

Both accepted windows added zero deferred, rejected, or failed native frames.
Physics readback, input failures, input buffer allocations, output failures,
and solve failures stayed unchanged. Output epochs advanced by 764 and 823.
Accepted shadow groups advanced with no new rejected, unconsumed, or generic
shadow group. The repeat's before and after snapshots each had 2,000 resident
draws, 2,002 reused source groups and registration rows, zero lookup or group
rebuilds, zero pending recipients, and zero material resolutions. The first
warm snapshot still contained 13 extra unsupported source rows; its final
snapshot had 2,002 rows. These live counters are not atomic snapshots.

The two wide PNGs show the grid inside the viewport. The two near PNGs show
bent meshes. All four images were viewed. No new native visibility-member
count was captured. CPU and GPU clocks were not controlled. The preceding
registration-cache windows ranged from 20.867 to 23.928 Hz. These observations
do not establish an end-to-end gain or meet the 100 Hz / 10 ms p95 targets.

A separate 12-second scope probe collected 278 completed ScenePlan scopes
from 338 profiler snapshots. All snapshots contained some incomplete scopes.
ScenePlan mean/median/p95 was 6.8687 / 6.7206 / 8.1302 ms. IdentityDelivery mean
was 0.5069 ms. The preceding registration-cache scope sample averaged
6.9701 ms for ScenePlan and 0.4963 ms for IdentityDelivery. This single pair is
diagnostic evidence, not a controlled CPU-cost result.

Evidence is under `Build/_AgentValidation/20261006-195917-chain-scale/`:
`reports/identity-groups-live-*.json`, `reports/identity-groups-fault-time-state.json`,
`reports/identity-groups-candidate*-summary.json`, and
`reports/identity-groups-scope.json`. The final existing scene record,
shared-scene, geometry, and fault-injection test filter passed 25 of 25 tests.
It does not cover source grouping. The two earlier fixed-slot material
expectation failures remain open. No unit tests were added or changed.

### Final source validation

The exception-boundary correction passed the isolated Editor Release build
with zero warnings and errors in 21.47 seconds. The normal primitive and
removal checks passed again on these binaries. Material override and restore
kept draw slot 1 at generation 3, while the material changed from slot 1 /
generation 3 to slot 2 / generation 1 and then slot 1 / generation 4. Settled
snapshots kept three reused groups, zero group rebuilds, and zero pending
recipients. Four final source/override PNGs were viewed. The last timer snapshot
reported `isRunning=true` and no terminal fault before the owned session was
stopped.

The scale and scope windows preceded this final exception-only correction.
No timing gain is attributed to it. Evidence: `reports/identity-groups-override-*.json`,
`reports/identity-groups-final-time-state.json`,
`logs/identity-groups-commit-guard-live-checks.log`, and
`logs/identity-groups-commit-guard-editor-start.log`. Focused new tests and the
two stale material expectation fixes were requested under the repository's
explicit clearance rule. No clearance was assumed.

## Focused Identity And Material Contract Tests (2026-10-08 UTC)

The user then cleared the focused tests. The change adds twenty publisher
cases and fixes the two stale material test expectations. Production code is
unchanged by this test work.

The publisher fixture uses real `GPUScene`, `RenderCommandMesh3D`, triangle
meshes, and direct `AdvancedGpuScenePublisher.Publish` calls. It swaps command
state before sealing the scene and selects the supported `OpaqueForward`
pass. The initial fixture omitted the command swap and used pass zero
(`Background`). Those setup errors produced unsupported geometry and render
pass results. Correcting the fixture produced valid canonical draws. No GPU
or running editor is required.

The new cases cover:

- Stable registration and group reuse, with exact draw generations and a
  fresh accepted publication after a content change.
- Pinned unchanged-publication delivery, including a callback failure and
  successful retry against the same accepted publication.
- Primitive growth and shrink, removed sources, slot generation reuse,
  command reorder, missing lookup entries, null sources, and unsupported
  render passes. Each invalidation check starts with a warm cache.
- Real lookup growth across 64 resident sources, with unique handles and
  exact per-source mapping. Pinned old publications also force scene-journal
  growth while preserving the logical draw and group reuse.
- Preflight rejection, partial callback delivery, repeated failure before a
  removed recipient receives its clear, and successful caller retry. Pending
  recipients survive an intervening rejection.
- Exceptions on disposal of the commit profiler scope, on entry to the
  identity-delivery scope, and on disposal after callbacks. The accepted
  image stays hidden, the database stays unfaulted, and all recipients remain
  pending until retry succeeds.
- Removed source references leaving the ordered and group caches after
  stable recovery, and all cache references clearing on disposal with or
  without pending recipients.

The material tests require the dirty ranges to cover complete fixed slots.
The new shrink case replaces eight constant words and two texture bindings
with four words and one binding. It verifies the cleared tails and zero
managed allocation during replacement. Production slot clearing is unchanged.

The combined Release filter passed **52 of 52** tests in **491 ms**, with no
compiler warnings or errors. It includes the twenty new publisher cases,
seven material cases, and twenty-five existing scene record, shared-scene,
geometry, and fault-injection cases. The command was:

```powershell
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AdvancedGpuSceneIdentity|FullyQualifiedName~AdvancedMaterialDatabaseContractTests|FullyQualifiedName~AdvancedGpuSceneRecordContractTests|FullyQualifiedName~AdvancedSharedGpuSceneContractTests|FullyQualifiedName~AdvancedGeometryDatabaseContractTests|FullyQualifiedName~ValidationFaultInjectionTests'
```

The log and TRX are `logs/identity-material-regressions-final.log` and
`reports/identity-material-regressions-final.trx` under the existing chain-scale
evidence run. These tests close the three focused code items. They do not
change the editor timer's terminal callback-exception policy or establish
automatic editor recovery. No performance window was repeated for test-only
changes. Structural transaction-plan reuse, the other test backlog, and the
manual hardware matrix remain open.

The final review added explicit hash-rebuild assertions to the stable and
65-source growth cases. Both passed a targeted Release rerun in 273 ms, with
no compiler warnings. Evidence: `logs/identity-lookup-final.log` and
`reports/identity-lookup-final.trx` in the same run.

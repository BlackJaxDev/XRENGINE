# Skinned GPU chain benchmark validation

## Objective

Validate `Physics Chain GPU Dispatcher Skinned Mesh Test` in the Math
Intersections world. Verify visible mesh deformation, GPU palette motion,
zero simulation readback, and repeated benchmark start and stop. Measure
scaling before choosing performance changes.

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

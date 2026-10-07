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

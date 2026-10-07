# Physics Chain Scale Implementation Status

Date: 2026-10-07
Last checked: 2026-10-07 23:23 UTC
State: Work resumed after the latest pull. Implementation and acceptance remain open.
Code items: [Thousands-scale optimization](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md)
Validation: [Physics validation](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale)
Investigation: [Skinned GPU chain benchmark](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md)

## Completed Code

| Area | Implemented behavior | Limit |
| --- | --- | --- |
| GPU inputs | Retained mapped input banks, 48-byte affine records, owned pending packets, and exact local-rest caches replace transient input staging. Capacity planning provisions all eight safe banks at rounded high-water targets. A complete target requires no bank polls. | Smaller index/flag packing and the full residency backlog remain open. |
| Bounds and materials | Palette-box bounds contain current and compatible previous poses. Draw-material snapshots include command overrides. Translation-class effects add padding; unsupported and non-finite effects reject the GPU route. | The full effect and backend matrix remains open. CpuDirect can still draw a command whose GPU bounds consumers reject it. |
| Output history | Producer tokens and failure guards prevent unrelated or failed output from becoming valid previous deformation. | History and failure unit tests remain open. |
| Covered rendering | Fully covered renderers use committed GPU output and retained static draw plans. Root motion does not require ordinary per-bone CPU bounds work. Indexed groups preserve each member's palette, deformation, and draw identity. | Partial coverage, source edits, multiple consumers, and OpenGL need their remaining checks. |
| Directional shadows | GPU culling and grouped native submission exist. Covered output publication dirties matching cached cascades; strict admission does not substitute generic CPU collection. | Native cascade slices, caster policy, and failure recovery are not fully validated. |
| Visibility storage and counters | Group membership uses one atomic reservation with bounded writes and a clamped finalizer. Only the Vulkan visibility arena prefers mapped device-local memory. Actual memory flags control flushes; only device-memory exhaustion permits a type retry. | Matched timing and the full overflow/hardware matrix remain open. |
| World runtime | The world owns clocks, rest-input ranges, and the bound particle graph. World/hierarchy transitions retire the old owner before propagation and block destination admission until propagation ends. | Component packing, quality, and other runtime state still need the ownership cleanup. Two-world concurrent transfer is not checked. |
| CPU workers | Aligned counters avoid shared cache lines. Worker faults preserve completion signals and the join boundary. | Full CPU pipeline parallelism, specialization, and allocation cleanup remain open. |
| Selective readback | Independent world banks, source-generation witnesses, exact resident ranges, held-input gathering, and shared solved-bone composition replace unsafe shared or authored-pose reads. Affine rows include translation and have a typed matrix decoder. Active admission now precedes storage access. Preparation exceptions release logical staging slots and resource guards. | Cross-world and failure checks remain open. |
| Debug and deferred lifetime | One bounded compact world batch feeds two indexed indirect debug draws. Batches own their buffers, renderers, and compute program, and borrow the cached shader. Advanced invokes selected debug drawing with scene depth. Active admission rejects retirement before storage access. Deferred requests and payloads retain leases. Available compute operations reuse sealed snapshot storage; detached copies own separate storage. | Busy batches skip debug frames. Continuous overlays and the full lifetime matrix remain open. |
| Compute backend compatibility | The five-argument compute method remains available. The leased overload is explicit. An unknown backend returns `Unsupported` for a non-null lease. | No backend silently drops the resource owner. |
| Editor inspection | Existing compatible interface references can be assigned through MCP. This permits live World detach and reattach without creating another object. | Reference assignment still follows normal property setters and ownership rules. |

The deferred debug batch does not own the shared particle/static arenas. If a
source retires before Vulkan materialization, exact descriptor validation
rejects the request. This is an explicit failure path, not a promise that
queued debug work survives arena replacement.

## Validation At This Boundary

| Check | Result |
| --- | --- |
| Latest combined editor Release build | The sampled rest-input telemetry build passed in 98.85 seconds with zero warnings and errors. The earlier revised cache build passed in 70.61 seconds and supplied the matched cache comparison. |
| Existing targeted tests | All 40 selected Release scheduler, world, lifecycle, readback request, and readback transfer tests passed on the final source after the ablations in 9.9753 seconds. The full suite was not run. No unit tests were added or changed. |
| World transitions | Live world detach/reattach, parent move/restore, and deactivation/reactivation passed on the earlier combined binary. Active states recovered one chain and valid strict publication. |
| Selective solved-pose readback | Five live requests each delivered 216 bytes in three frames. Bone translations exactly matched the selected particle positions in the same result. Rotations were nonidentity. Total delivered data was 1,080 bytes, with no stale discard or failure. |
| Strict material route | Translation, rotation rejection, neutral command override, rejected override, and recovery passed. Native main and shadow admission followed the route. Presented exports were viewed. |
| Indexed instance groups | Native wide and near captures had 64 and 25 unique members. All 13,312 current and valid previous positions fit their own bounds. Of these, 6,656 previous positions differed from current positions. |
| Current smoke run | The 32-chain strict GPU window completed 284 frames in 6.038 seconds with no new deferred, rejected, or failed frames. Input buffers did not grow. Physics readback stayed at zero. Wide and near exports were viewed; the skinned meshes were visible and bent. This is a smoke check, not performance acceptance. |
| Current selective readback | Five explicit requests each returned 216 bytes in three frames. Bone translations matched solved particle positions. Successive results changed. A same-call cancel/release also passed. Requests were released; timing windows had no new physics readback. |
| Actual visibility memory | A read-only registry query found five 16 MiB chunks, all memory type 4 with DeviceLocal, HostVisible, and HostCoherent flags. This confirms selected placement on the RTX 4070 Laptop GPU. |
| Earlier three-cycle lifecycle | Each clean 2,000-chain run retained strict submission, moving shadows, zero new bad native outcomes, no input growth, and no physics readback. Each stop restored one chain, one palette slice, and one renderer binding. All wide, near, and restored-source motion images were viewed. The older measurement camera could clip front rows. |
| Full-grid native output | Two captures each contain one indexed group with 2,000 unique members. Actual native indices are in range. All 208,000 current and 208,000 previous positions fit their own bounds. All members change shape-distance measures and current-position digests. Native images were viewed. |
| Math camera and labels | Only this world disables bloom and uses manual exposure 1.0. Live Advanced settings match. Viewed front, side, rear, and far-to-near images show readable camera-facing labels. |
| Retained resource blocker | The final build retained a cold startup episode from frames 1 through 78 and its recovery at frame 79. A synchronized read returned first/latest viewport keys, pending Building status, and the exact decline reason after recovery. This validates the diagnostic, not the later intermittent fault's cause. |
| Final debug/lifetime runtime | Native replay contains one debug compute dispatch followed by two point-topology indexed indirect draws with seven instances. GPU arguments are [1, 7, 0, 0, 0]. Both draws use depth test and depth writes. Three off/on cycles restore valid resources and preserve the shared shader. Busy batches skip debug frames; continuous overlays and the full lifetime matrix remain open. |
| Final full-grid window | The final build completed 603 frames at 19.985 Hz, with p95 59.574 ms. Strict submission and moving-shadow admission passed. No new bad native outcomes, input allocations, input failures, or physics readback occurred. Viewed images show bent meshes and source motion after restoration to one chain, one palette slice, and one binding. |
| Extended restart limit | One earlier window entered a resource blocker that later recovered. A later start request timed out although the editor started the benchmark. These checks remain failed. Retained blocker snapshots and uncertain-start cleanup are now implemented. |
| Six later repeat windows | All six passed the harness correctness checks at 2,000 chains. Rates ranged from 15.987 to 21.367 Hz. All 24 images were viewed, and each stop restored one source chain. No timing target passed. |
| CPU diagnostic window | With world telemetry enabled, 661 frames completed in 30.124 seconds: 21.943 Hz and p95 53.722 ms. Harness checks passed. All four images were viewed; the restored source deforms and its count is one. |
| Current resource state | The post-diagnostic resource snapshot is healthy. Its retained episode covers only startup frames 1–78, with recovery at 79. The six-repeat snapshot likewise retains only startup frames 1–80, with recovery at 81. Neither resolves the earlier intermittent blocker. |
| API-only Vulkan trace | Nsight collected one queue context. In a stable 11-second interval, 228 complete submission groups averaged 8.798 ms of GPU span, against 48.082 ms mean submit spacing. The trace warns that Vulkan events can be missing. This supports a CPU submission-gap hypothesis, not a precise device-utilization claim. |
| Admission comparison state | The same-process A/B comparison is complete. The rejected early-admission prototype, skip counters, and temporary flag are removed. The quality/dependency timing split and fence diagnostics remain. The later cache build includes the removal; final scale timing is pending. |

The world-transition, readback, material, and indexed-group checks above came
from the earlier machine and binary. They do not validate the latest changes.

## Performance Findings

The earlier desktop observer-free moving-shadow window completed 397 frames in 30.971
seconds: 12.818 Hz, with completed-frame interval p95 83.689 ms. It added 471
accepted directional groups without a new rejection, generic fallback, input
growth, or physics readback. The required 100 Hz and 10 ms p95 targets failed.

A separate instrumented 2,000-chain window reached 10.371 Hz and p95 115.631
ms. RenderDoc and the material-effect fixture were enabled. This is diagnostic
evidence, not a matched timing comparison. A 15-second CPU sample trace placed
substantial work in bridge submission, hierarchy activity, rest capture,
dependency checks, and committed spatial queries. Sampled CPU time is not
per-frame wall time. GPU pipeline timing was disabled in that run, so no GPU pass timing
isolates the frame delay to physics. CPU wall-time and synchronization
attribution are also missing. Do not claim a measured speed gain from these
different observer configurations.

The resumed laptop run after the group-counter changes completed 656 frames
in 30.403 seconds: 21.577 Hz, with interval p95 52.948 ms. The first
device-local run completed 568 frames in 30.065 seconds: 18.892 Hz, with p95
64.582 ms. Both held 2,000 chains, strict GPU submission, advancing accepted
shadow groups, zero new bad native outcomes, no input growth, and no physics
readback. World telemetry was on. Neither is acceptance or proof of a frame
rate gain from memory placement. Wide and near images from both were viewed.

Separate dense timing after the memory change recorded 34 active frames.
The directional-cascade interval averaged 0.228 ms and HiZ averaged 0.181 ms.
These intervals include upstream work drain. The CPU dump still shows large
collection and update costs. A frame snapshot reports only 0.022 ms next-slot
wait and 10.729 ms command recording. Clean telemetry-off timing and focused
CPU collection review followed. The proposed primary shadow bypass is unsafe:
the general primary shadow pipeline still consumes CPU-owned mesh and
non-mesh commands. An explicit native consumer with caster policy is now an
open implementation item.

Three subsequent clean 60-second laptop cycles completed 1,338, 1,195, and
1,176 frames at 21.805, 19.879, and 19.458 Hz. Their interval p95 values were
52.598, 62.596, and 60.881 ms. World telemetry, the four Debug observers, and
RenderDoc were off. All three failed the 100 Hz target.

The shader inventory found existing precomputed rest lengths and capsule
inverse terms. Solved-position distances and directions are dynamic. They
must not be cached as invariant data. Remaining coefficient work needs exact
parameter/version witnesses and a matched measurement.

With the corrected full-grid camera and Math-only manual exposure, three
windows after input-bank provisioning reached 22.243, 19.969, and 18.987 Hz.
Their p95 intervals were 51.739, 63.093, and 62.536 ms. Input allocations and
failures did not increase. Later windows varied from 14.856 to 21.152 Hz; two
extended-run faults prevent restart acceptance. The final fresh-build window
reached 19.985 Hz with p95 59.574 ms. No result meets 100 Hz or 10 ms p95.
These runs do not prove a matched speed gain from the implementation changes.

Six later observer-off windows reached 21.367, 20.032, 20.535, 19.354,
15.987, and 16.230 Hz. Their p95 intervals were 57.320, 58.574, 58.445,
57.377, 75.280, and 70.183 ms. The `async-repeat` report label does not mean
that asynchronous physics submission was enabled. These remain graphics-queue
physics measurements. All six passed the harness correctness checks, with no
new deferred, rejected, or failed frames, input growth, or physics readback.
The earlier intermittent blocker and start timeout remain unresolved.

A separate 20.682-second world callback pair measured 624 late ticks at
23.788 ms per call and 621 fixed ticks at 10.322 ms per call. Both recorded
zero allocation on the callback thread. Fixed-tick time can include the
world tick gate wait. The separate detailed stage pair measured quality plus
prerequisite work at 5.588 ms, world rest-input gathering at 7.161 ms, GPU
component preparation at 3.112 ms, packing at 1.905 ms, bridge submission at
1.424 ms, and activity scanning at 1.415 ms per late tick. These are nested
timers. Do not add them or treat concurrent thread costs as exclusive frame
time. The diagnostic window is not an observer-free performance comparison.

The broker review found that benchmark start returns `void`. The supplied
path does not wait for benchmark settling or measurement, and it does not
prove a synchronous Task wait. The new opt-in request trace must locate the
delay across queue, reflection, completion, and response boundaries before a
synchronization change. Nsight's environment check reports no OS administrator
access and failed sampling support, but the API-only Vulkan trial succeeded.
Its benchmark window passed the harness checks at 21.126 Hz and p95 55.535 ms.
The source returned to one chain, and both viewed source images show different
deformation. The trace's stable interval contains one queue context and 228
complete groups of three workloads. GPU span averaged 8.798 ms, with median
8.188 ms and p95 12.826 ms. Mean submit spacing was 48.082 ms; GPU work started
0.177 ms after submit returned, on average. Observed workload coverage was
18.22%, not device utilization. Nsight warns that some Vulkan events can be
missing. Prioritize the measured CPU update and submission path, then repeat
matched frame timing. No GPU overlap or asynchronous physics gain is established.

The ancestry-cache experiment is rejected. Its diagnostic window passed the
harness checks at 21.129 Hz and p95 60.0716 ms, but mean late-tick body time
rose from 23.790 to 31.864 ms. Quality plus dependency preparation rose from
5.588 to 11.926 ms; rest gathering changed from 7.161 to 7.393 ms. All 13
compatibility counts stayed at zero. No physics readback or new outcome
failure occurred. All four images were viewed, and stop restored one source.
The cache was removed. The rejected patch is retained as disposable
evidence; the cache is not finished architecture.

The user authorized lower physics rates by distance with interpolated GPU
poses. Design work is open. The current GPU palette shader uses solved
positions directly and has no interpolation parameter. Solver history exists,
but render-pose refresh, root motion between solves, and render-history bounds
must be connected before this policy can be validated. No such implementation
or performance result is claimed here. The proposed contracts are in the
[distance cadence design](../../design/physics/distance-cadence-gpu-presentation.md).

After cache removal, the full-rate diagnostic baseline completed 675 frames
in 30.809 seconds at 21.909 Hz and p95 53.981 ms. The existing `Hz15` plus
`Interpolate` setting completed 681 frames in 30.520 seconds at 22.313 Hz and
p95 52.061 ms. Both passed harness correctness checks. World rest gathering
still averaged 7.304 ms per late tick at 15 Hz, against 7.123 ms at full rate.
The existing GPU palette has no interpolation path, and reduced-tier physical
time is not equivalent to authored fixed ticks. This comparison does not
validate smooth LOD or a physical-equivalence claim. All four 15 Hz images were
viewed: the grid is present, near meshes are bent, and restored-source poses
differ. Still images do not prove smooth interpolation. The source returned
to count one with `Strict` and `Discrete` restored. The owned editor is stopped.

The first bounded experiment tested early admission before expensive input
gathering for existing reduced tiers. The experiment is rejected after the
same-process comparison below. Full smooth LOD remains separate work:
fixed physical ticks, retained GPU tick debt and endpoint history, root-relative
palette updates between solves, a continuous presentation clock, matching
bounds, and multi-view relevance. The temporal review accepts a fixed delay
of at least about 67 ms plus bounded endpoint delivery as a safe reference.
The final latency policy and latency reduction remain open.

The workgroup comparison used the unchanged editor binary with world telemetry
off. The 32-thread run passed at 20.933 Hz and p95 55.014 ms. The 64- and
128-thread runs failed the input-page marker check. Their reported rates were
21.581 and 19.670 Hz, but neither is a valid comparison result. Their p95
values also have invalid interval telemetry, with four and two dropped samples.
Compiled SPIR-V artifacts contain the requested 32, 64, and 128 local sizes.
This does not establish a workgroup winner or a cause for the marker failures.
The next run returns to 32 threads.

Only samples within each measured window were used for GPU clock comparison.
Mean graphics clocks were 1,266.5, 1,239.8, and 872.0 MHz for 32, 64, and 128
threads. Power states and memory clocks also varied. All four 32-thread images
were viewed: the grid and bent near meshes are visible, and the restored source
changes pose. The failed runs stopped before wide/near captures. Both restored
source pairs were viewed and show different poses. These images do not count
all 2,000 chains. The marker cause remains unknown; native completed frames
and zero quarantine do not prove benign cancellation. These trials do not
contain the new CPU early-admission patch, which was not built for them.

## Rejected Early-Admission Experiment

Retained fence diagnostics are implemented and build-checked. A marker rental
has a stable diagnostic ID and first-failure record. The Vulkan renderer keeps
512 value records; the dispatcher retains the page identity at observation.
New-session captures contain unsubmitted-marker failures. They do not establish
the cause of the earlier input-page failures or change their classification.

Early admission is a rejected optimization, not a retained performance fix.
All three `Hz15` runs and the Strict control passed the harness correctness checks. They do not
show a measured gain against the previous `Hz15` diagnostic. CPU values below
are mean milliseconds per late tick. The stages are nested; do not add them.

| Diagnostic | Completed Hz | Interval p95 ms | Late-tick body ms | Rest gathering ms | Component preparation ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Previous Hz15 | 22.313218 | 52.0613 | 22.294 | 7.304 | 2.834 |
| Early admission, run 1 | 21.085118 | 59.8461 | 24.793 | 7.699 | 4.413 |
| Early admission, run 2 | 21.462904 | 56.5694 | 23.967 | 7.373 | 4.328 |
| Early admission, run 3 | 21.653223 | 54.7638 | 24.076 | 7.418 | 4.300 |
| Current Strict control | 19.571711 | 62.6706 | 27.043 | 8.409 | 3.271 |

Run 1 recorded 1,076,000 capture skips and the same number of preparation
skips across 1,112 ticks. Run 2 recorded 1,096,000 of each across 1,080 ticks.
Run 3 recorded 1,064,000 of each across 1,052 ticks. The Strict control recorded
zero skips across 1,016 ticks.
The skips show that the gate executes. They do not establish a lower total
cost. The current Strict body mean is 27.043 ms, against the earlier 24.389 ms.
System drift limits comparison with the older baseline. A temporary private
flag and an ignored setter permitted an admission-on/off comparison in one
process. Remove this measurement control with the rejected prototype.

The same-process `Hz15` comparison passed harness correctness in all four
valid windows. A1 is excluded: its OFF setter reached zero worlds, and its
skip counts prove admission stayed on. The corrected guard requires a world.

| Window | Admission | Completed Hz | Interval p95 ms | Body ms | Rest gathering ms | Component preparation ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| A2 | OFF | 20.336777 | 62.5163 | 23.8537 | 7.5057 | 3.2333 |
| B1 | ON | 19.271190 | 60.5043 | 26.6603 | 7.9385 | 4.7142 |
| A3 | OFF | 19.825627 | 65.5226 | 24.4788 | 7.6949 | 3.3858 |
| B2 | ON | 15.684604 | 78.1362 | 24.2712 | 7.1681 | 4.3445 |

B2 recorded 1,418,000 capture skips and matching preparation skips across
1,412 ticks. The valid OFF windows had zero skips. Reject the prototype:
there is no measured end-to-end benefit, and both ON windows show higher
component preparation cost than both OFF windows. Do not attribute all of
B2's lower frame rate to admission. Other costs and system drift are not
isolated. Code removal is complete; the retained fence diagnostics stay.
The six prototype-only Core files have no remaining content diff. The
`QualityAssignment` and `GpuRestDependency` timing split remains. The saved
rejected patch also contains pre-existing timing-split changes. It is evidence,
not a patch to replay as a whole. The later rigid-cache build includes this
removal. Its bounded mutation checks passed; final scale timing is pending.

All restored-source pairs in the same-process comparison were viewed and show
different visible poses. Fence history stayed at 104 records from A2 through
B2. Its sites are `UnsubmittedMarker`, `PlanUnsubmitted`, and
`RequiredProducerMissing`, all with `NativeSubmission=NotCalled` and invalid
native result values. This does not prove the old 64/128 failure origins.

All four images from each reduced-rate run and the Strict control were viewed.
The wide grid and bent near meshes
are visible. Each restored source pair shows different poses. All three
reduced-rate restarts restored the source count to one. The images
do not prove all-member visibility or smooth interpolation. This change does
not implement the full distance-cadence design. The rejected ancestry cache
remains removed; 32 threads remain the workgroup baseline without a declared
winner from the invalid 64/128 windows.

## Retained Explicit Rest-Input Cache

The explicit runtime-only `EnableRigidGpuRestInputCache` option is implemented.
The Core path caches root-relative rest
inputs while preserving all dependency checks. It targets the measured
approximately 7 ms rest-gathering stage only. It does not change the GPU ABI,
shaders, or simulation timing. The Math source and clone flag are added.
The invalidation review required child-input changes to latch the range as
blocked until the option is explicitly disabled and enabled again.

Nine saved live comparisons passed against ordinary capture. Each compared
seven inputs with finite values. Child translation, rotation, scale, and
order changes each set `BlockedRanges=1` and matched the ordinary values
exactly. Explicit rearm cleared the block. Root-parent affine motion remained
eligible; its maximum absolute difference was 1.1920929e-7, with matching
stretch 1.3989542. Initial and restored comparisons also passed.

These checks validate only the recorded mutation cases. The first cache-OFF
Strict 2,000-chain diagnostic passed at 18.511600 Hz and p95 65.8224 ms.
Across 964 ticks, mean body time was 28.73283 ms, gathering was 9.17030 ms,
and dependency preparation was 5.27137 ms. Cache hit and miss deltas were zero.
All four images were viewed: the grid and bent near meshes are visible, and
the restored source pair changes pose. Cache enablement was restored on the
source; its seven-input comparison passed with maximum difference 1.1920929e-7.
The first matched ON window passed at 20.536405 Hz and p95 59.6709 ms.
Across 1,024 ticks, mean body time was 23.80021 ms, gathering was 6.18292 ms,
and dependency preparation was 4.87492 ms. It recorded 2,040,000 cache hits
with zero misses or blocked ranges. This is a preliminary improvement over
OFF A1. The repeated OFF A2 and ON B2 also passed. The explicit cache is
retained after the input, mutation, reparent, and lifecycle checks below.
All four ON images were viewed and show the grid, bent near meshes, and a
changed restored source pose. The source retirement check passed: its prior
cache witness is no longer alive or subscribed. Fence history stayed at 135
records. This does not resolve earlier failures or meet the timing target.

| Cache window | Completed Hz | Interval p95 ms | Body ms | Gathering ms | Dependency preparation ms | Late ticks |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| OFF A1 | 18.511600 | 65.8224 | 28.73283 | 9.17030 | 5.27137 | 964 |
| ON B1 | 20.536405 | 59.6709 | 23.80021 | 6.18292 | 4.87492 | 1,024 |
| OFF A2 | 20.260129 | 55.8190 | 25.10158 | 7.21414 | 4.472225 | 1,096 |
| ON B2 | 21.342219 | 54.3646 | 22.910193 | 5.900462 | 4.677170 | 1,100 |

A2 had zero cache hit, miss, and blocked-range deltas. It was faster than
OFF A1, so do not assign all of B1's improvement to the cache. B1 gathering
remains lower than both OFF values. All four A2 images were viewed and show
the grid, bent near meshes, and a changed source pose. Its restored seven-input
matrix comparison passed with maximum difference 1.1920929e-7 and matching
stretch 1. Fence history stayed at 135 records.

B2 recorded 2,200,000 hits with zero misses or blocked ranges. Both ON
gathering means are below both OFF means. The repeated stage reduction is
promising, but it does not remove the observed window variability. All four
B2 images were viewed; the grid, bent near meshes, and changed source pose
are visible. Its seven-input matrix comparison passed with maximum difference
1.1920929e-7 and matching stretch 1. Fence history stayed at 135 records.
The initial mutation checks passed. The sampled six-part diagnostic timer
passed review and its first runtime check, as recorded below. It is measurement
work, not another optimization. The 100 Hz target and smooth LOD remain open.

A third cache-ON start checked all 2,000 ranges and 14,000 input matrices
against ordinary capture. All passed, with maximum absolute difference
1.5258789e-5. This was a correctness run, not a timing window. Before teardown,
the witness report counted 2,000 caches and 12,000 tracked child registrations.
Each child registration has two filtered handlers; this is not a raw delegate
count. After teardown, all 2,000 witnesses were still alive, but none was
subscribed and all registrations were removed. The retirement check passed;
it does not prove object collection. Both wide images and both restored-source
images were viewed. The grid is visible, and the source changes pose.
Reparent and reset checks also passed. Moving `Bone6` under `Bone4` set
`BlockedRanges=1`, and world capture rejected the changed hierarchy. The
comparison's `captured=false` and `passed=false` record this expected route
rejection, not unequal matrices. `HierarchyInputDependency` rose from 0 to 74.
Restoring the parent and rearming returned seven finite inputs, zero blocked
ranges, and maximum difference 1.1920929e-7. Disabling and enabling the source
removed all old-cache registrations; the old witness remained alive but was
not subscribed. New capture passed. Both restored images were viewed and show
a changed source pose. Keep the explicit cache based on repeated gathering
reduction and these bounded correctness checks. Do not infer complete quality
coverage or performance acceptance.

The sampled diagnostic window passed harness correctness at 19.671051 Hz and
p95 68.8781 ms. Mean late-tick body time was 26.39866 ms, gathering was
6.84516 ms, and dependency preparation was 5.35305 ms. These are nested
whole-callback stage means. The cache recorded 1,992,000 hits with zero misses
or blocked ranges. No timing benefit is claimed for this instrumented build.

| Sampled rest-input stage | Mean microseconds per sample |
| --- | ---: |
| Opaque dependency check | 1.881916 |
| Owner dependency check | 0.785317 |
| Collider dependency check | 1.773095 |
| Root preparation | 0.851897 |
| Cached input expansion | 0.315760 |
| Input publication | 0.206396 |

Each stage has 62,996 actual samples. These means are per sampled operation,
not whole-update totals. All four images were viewed: the grid and bent near
meshes are visible, and the restored source changes bend and position. Fence
history grew from 111 to 135 records. The 24 new records all report
`PlanUnsubmitted`, `NativeSubmission=NotCalled`, and no valid native result.
This does not classify the failures as benign or establish the origin of older
failures. The next source review will check repeated hierarchy walks; no further
optimization is implemented from these samples.

## Current Work

The phase-local collider dependency proof is deferred. Its attempted source is
being removed. It was not built or validated and is not retained architecture.
The sampled collider check motivated the attempt, but it did not establish
the frame's critical path.

The next priority follows the user's critique: compare a frozen physics pose
with mesh rendering retained, then active physics with mesh rendering off.
Keep the controls explicit and compare completed-render intervals. Follow
these ablations with a CPU scheduling and stack timeline to identify the
critical thread and its waits. The observed CPU submission gap does not prove
which CPU work limits frame rate. The cache's repeated gathering-stage gain
is established within the diagnostic comparison; an end-to-end gain remains
unproven because the windows vary. Grouped GPU indexed-instance draws already
exist and have native validation. Missing instancing is not an established
cause.

The clean cache-OFF attempt is invalid: readiness or measurement added 42
completed and two rejected outcomes. It has no comparative value, and no
further cache A/B window is planned. The first full-scene ablation control
held 2,000 chains before and after its timing window. It completed 580 frames
in 30.237905 seconds at 19.181223 Hz, with p95 64.6672 ms and zero new bad
outcomes. Its later timeline is invalid for 2,000 chains: the benchmark's
120-second limit had already restored the single source. The visibility
guard found one chain and made no change. The later wide image was viewed;
it shows the label on a black background, not the full grid.

The scratch start helper now holds only the benchmark duration stopwatch
after settling. Simulation time continues. A fresh 2,000-chain batch is
starting; no corrected timeline result is available yet. WPR could not start:
the OS returned `0xc5585011`, "Failed to enable policy to profile system
performance." Recorder status confirms no recording. This is an OS privilege
failure, not an approval rejection. A scratch probe will capture existing
`CodeProfiler` absolute scope timestamps. Those scopes do not replace OS
scheduling and sampled stacks.

The corrected rendering-hidden window retained 2,000 active chains before
and after timing. It completed 1,797 frames in 30.497386 seconds at
58.923083 Hz, with p95 23.5708 ms and zero new bad outcomes. It retained
2,000 palette slices and bindings. Output producer epoch and input acquisitions
each advanced by 1,832. Input and output failure counts stayed at nine,
dispatch failures stayed at zero, and physics readback stayed at zero.
This removes the whole chain rendering path, including scene collection,
publication, deformation, and drawing. The increase from the earlier
19.181223 Hz full control does not isolate raster cost or establish the
critical thread. The 100 Hz target remains unmet, even in this ablation.

The held full-scene image shows the grid. The hidden image shows no chains.
The immediate show-again image is black, but the settled frozen-physics image
shows the grid again. Two state checks two seconds apart retained 2,000
chains and bindings, output producer epoch 35417, input acquisitions 35420,
and unchanged failures. Frozen-physics timing is still pending. A valid
2,000-chain scope timeline contains 156,959 scopes from 341 snapshots; its
analysis is separate from the invalid single-source trace. The batch remains
active with its duration stopwatch held.

The valid scope timeline places the observed cycle at approximately
`max(render, collect) + serial swap`. Across 252 render scopes, start spacing
averaged 47.320 ms, or about 21.13 Hz. Render averaged 30.243 ms; collection
averaged 25.319 ms, with 25.053 ms overlapping render. Collection then waited
5.081 ms, and swap averaged 16.914 ms. The 17.089 ms gap between render scopes
contained 16.813 ms of swap; publication-to-render delay averaged 0.035 ms.
This points to render work and serial swap/publication for the next source
review, rather than update time alone.

Largest residual wall scopes include `XRWindow.GlobalPreRender` (10.544 ms
per call), command recording (6.702 ms), visibility preparation (6.176 ms),
collection (6.168 ms per call, two calls per cycle), pre-collection (8.947 ms),
scene-plan publication (6.963 ms), global swap (4.589 ms), and BVH swap
(1.629 ms per call, about two per cycle). These are exclusive only relative
to recovered instrumented children. All 341 snapshot publications were
incomplete. These diagnostic wall times include possible waits and omitted
children; they are not CPU-on-core measurements or a complete scheduling
trace.

Frozen-physics C1 completed 753 frames in 30.069258 seconds at 25.042188 Hz,
with p95 53.021 ms and zero new bad outcomes. It retained 2,000 chains and
bindings. Producer epoch 35417 and input acquisitions 35420 did not advance;
failure counts stayed at nine. Root motion and CPU hierarchy work continued.
This disables physics callbacks; it is not an all-static scene or proof of
an all-static frame-rate ceiling. Callbacks were restored before teardown.
The source count returned to one, and both viewed source images show different
positions and bends. No production change came from these ablations.
The next structural source review will separate stable scene registration
from pose/bounds publication in `ScenePlan`.

Cycle 2 retained 2,000 chains and zero bad native outcome or input/output
failure deltas in all three modes. Full rendering reached 16.573007 Hz
(p95 73.4802 ms); hidden chain rendering reached 51.967882 Hz (26.4986 ms);
frozen physics reached 13.028705 Hz (84.9136 ms). Frozen producer epoch
43589 and 2,000 bindings stayed fixed. This weakens any quantitative
physics-freeze benefit claim; do not average the cycles into a small gain.
The four viewed images confirm hidden chains, a visible frozen grid, and
changed restored-source poses. Canonical resident draw count returned to one.

The first two cycles used a client timer with separate counter queries, so
transport did not give atomic boundaries. The repeated large rendering-hidden
effect remains useful, but its exact magnitude is not settled. Cycle 3 is
running an engine-side counter/timestamp meter with GPU clocks, GC counts,
and process CPU evidence. No result is available yet. Incremental stable
registration in `ScenePlan` remains the next structural candidate, not a
retained optimization.

Cycle 3 engine-side counter/timestamp measurements are available for full and
frozen physics. Full completed 737 frames in 45.137701 seconds at 16.327814 Hz
(p95 70.5093 ms). Frozen completed 975 in 45.117139 seconds at 21.610413 Hz
(57.0191 ms). Both retained 2,000 chains, slices, and bindings, with zero new
bad outcomes, input/output or dispatch failures, and physics readback. Producer
epoch advanced by 736 in full mode and stayed at 57148 while frozen. Snapshot
brackets span 0.240–2.136 ms; they bound local capture skew without relying
on transport timing. The third hidden result remains pending.

Process-wide managed allocation was 978,770,192 bytes for full mode and
1,365,504,616 bytes for frozen mode. Generation 0/1/2 collection deltas were
78/4/0 and 109/5/0; process CPU averaged 2.733 and 3.745 core equivalents.
These counters do not attribute allocation or on-core work to physics.
Continuous GPU clock CSV files are empty. Frozen endpoint samples changed
from 495/810 MHz graphics/memory at P5 to 1560/8101 MHz at P0. Clocks varied,
but the samples do not explain all timing variation. Both viewed cycle 3
images show the expected frozen grid and hidden chains. No production edit
or 100 Hz claim follows from these ablations.

The separate frozen 2,000-chain timeline retains the render-plus-serial-swap
pattern. Render averaged 30.186 ms versus 30.243 ms in full mode; swap was
13.316 versus 16.914 ms, and start spacing was 43.442 versus 47.320 ms.
The world update callback fell from 26.527 to 1.583 ms per call and fixed
dispatch from 9.764 to 0.0075 ms. Within render, pre-render residual fell
from 10.544 to 0.347 ms, but visibility preparation rose from 6.176 to
19.466 ms. Command-recording residual per rendered cycle was 6.729 versus
5.371 ms. Work composition changes, varying GPU clocks, incomplete snapshot
publication, and wall-time attribution prevent a single-hotspot causal claim.
Process-wide allocation normalizes to about 1.33 MB per full completed frame
and 1.40 MB per frozen completed frame; no subsystem attribution is proven.
The third hidden window is complete: 2,597 frames in 45.288487 seconds at
57.343492 Hz, with p95 24.2503 ms. It retained 2,000 chains, slices, and
bindings; producer epoch advanced by 2,454 with zero new bad outcomes,
failures, or physics readback. Resident draws were zero, command count was
four, and requested/resolved strict submission remained 2. Process-wide
allocation was 640,818,504 bytes, GC deltas were 52/6/1, and process CPU
averaged 2.409208 core equivalents. These are not isolated subsystem costs.

Across all three cycles, full rendering reached 16.3–19.2 Hz, whole-chain
rendering hidden reached 52.0–58.9 Hz, and frozen physics reached 13.0–25.0 Hz.
The rendering-hidden effect repeats; a stable quantitative physics-freeze
benefit is not established. Physics callbacks, mesh visibility, the duration
clock hold, and the camera were restored; the count returned to one. The
disposable session retained benchmark UI copy/duration values of 2000/120.
Both viewed final source images show different bends and positions. Final
time state had no terminal fault, running true, paused false, and render time
6.13 ms. The owned editor is confirmed stopped. All 40 existing targeted
tests passed on final source in 9.9753 seconds; no tests were added or changed.
Controlled-clock hardware/profile acceptance and the 100 Hz target remain
open. No production optimization came from these ablations.

A bounded saved-window check found zero scoped Advanced preparation build
allocation across 736, 751, and 721 builds in the earlier cache-OFF A2,
cache-ON B2, and sampled windows. Build means were 5.728, 5.949, and 6.267 ms;
extraction means were 4.090, 4.379, and 4.565 ms. These are not the cycle 3
process-allocation windows. Do not assign the process-wide allocation to the
shared build scope or claim all rendering allocation is zero. Allocation
profiling remains needed. The next structural priority is stable registration
and per-draw/group templates, with pose/bounds updates kept separate.

The owned session log scan found no matches for the selected unhandled,
device-loss, Vulkan-error, terminal-fault, error, or exception patterns. It
does not prove the absence of all warnings or other failures. Final test-log
warning review is separate from the recorded pass result.

1. Resolve the persistent viewport `ResourceGenerationBlocked` failure from
   the fifth full-grid window. Preserve its evidence. Repeat the extended
   lifecycle check after the cause is corrected.
2. Run the two controlled physics/rendering ablations, then capture CPU
   scheduling and stacks to identify the critical thread. Preserve the full
   scene as the control. Do not treat an ablated result as quality acceptance.
   Keep collider-proof work deferred until critical-path evidence supports it.
3. After live feature validation, obtain the requested unit-test clearance.
   Update the stale layout/source expectations and add the listed history,
   bounds, readback, ownership, and lease tests.
4. Collect separate diagnostic GPU timing and CPU wall-time evidence. Then run
   a matched observer-free 2,000-chain window on the corrected binary. Optimize
   the measured critical path. Apply only the user-authorized distance-based
   rate changes, with interpolated GPU poses and explicit quality controls.
5. Continue the remaining world ownership, CPU, residency, kernel, collision,
   skinning, activity/sleep, and cleanup code items. They remain listed in the
   active todo. Do not close that document while these items or its acceptance
   gates remain open.

The 100 Hz target remains unmet. No pending approval was treated as clearance.

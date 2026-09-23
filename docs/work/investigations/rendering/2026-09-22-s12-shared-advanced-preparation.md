# S12: Shared Advanced Preparation

Status: Active. S12 is a measurement-led disposition, not authorization to replace the shared extractor's lifetime model.

## Entry Evidence And Hypothesis

The Vulkan/Advanced Unit Testing World currently publishes 393 canonical draws and 393 indirect ranges for one desktop view. The first live Release sample showed 158 rebuilds, 790 same-frame cache hits, 131 successful family copies, no copy failures, and 108,468 bytes copied per family. After warm-up, an additional 1,144 rebuilds consumed approximately 0.449 ms each in `AdvancedPreparationExtractor.Build`, of which approximately 0.159 ms was command extraction. The original 153-165 ms stall has not been reproduced by this workload.

The first candidate for the residual cost is `AdvancedIndirectRangePlanner.Build`: it linearly searches existing ranges once per payload in each of two passes. With 393 unique range keys, that is 393 squared key comparisons per rebuild. This is a source-level hypothesis, not yet a measured attribution. The check that can reject it is a per-phase timer showing that range planning is small relative to the rebuild or that warmed build time is already below the entry threshold. Arena growth and shared-lock contention are separate candidates and must not be inferred from the rebuild duration.

## Ownership And Acceptance Set Before Behavioral Change

- `RenderWorldSnapshotPublication` is first-wins for a frame. Actual callers cannot alternate worlds within one published frame today. The singleton service holds one extractor under `_sync`; changing that cache key or introducing per-world storage would require a separate upstream world-publication and lifetime review.
- Extractor columns mutate on the next build and when views are added. OpenGL copies into renderer-owned arrays; Vulkan copies into an authoring lease under the service lock and copies once more into a deferred output-family plan. Keep those coherent copy boundaries and exact publication/view/generation checks. Do not pass mutable spans to deferred consumers or build outside `_sync` in S12.
- Gather three settled 60-second stationary windows before and after any behavioral change, with identical Release Vulkan/Advanced settings and a 393-draw/393-range Sponza scene. Record frame/rebuild/hit/copy counts, draw/range/job counts, stopwatch-frequency-normalized phase and lock timings, copied bytes, allocations, arena growth and retired generations. Sample diagnostics only at window boundaries. Also exercise camera motion and one scene/view change. If the workload or accepted frame identity changes, discard that comparison.
- Treat range planning as actionable only if its warmed mean is at least 0.15 ms per rebuild and at least 25% of total build mean. If changed, require at least a 20% reduction in warmed build mean across matched windows, with no more than 0.05 ms mean lock wait per acquisition and 0.10 ms mean family-copy time, zero copy/publication mismatches, no new per-build allocation, no unbounded arena or lease retention, and no correctness change in stationary/moving images or publication identities. A slower neighboring stage or a changed scene invalidates a claimed speedup.
- If those entry conditions fail, retain low-cost telemetry and explicitly defer the range/storage optimization. A live check of the ordinary Vulkan path is still required; a successful build is not the gate.

The named isolated editor session is `s12-sharedprep-0922`; task evidence is under `Build/_AgentValidation/20260922-164422-s12-shared-prep/`. No regression tests are added or run without the repository's post-validation test clearance.

## Evidence And Disposition

The instrumented `XREngine.Runtime.Rendering` build passed with zero warnings and errors. The same source was built into the named isolated Release editor with zero warnings and errors. A Vulkan viewport capture was saved and viewed; it showed the running scene with a highlighted edge, though the initial camera was too close to a surface to use as a broad visual-correctness comparison. Runtime introspection confirmed 393 draws and 393 ranges, one view, one rebuild with five same-frame hits per prepared frame, a valid canonical publication, zero deformation jobs, zero copy failures, and no per-frame managed allocation after startup.

Three consecutive, settled, approximately 60-second windows from that process:

| Window | Rebuilds / hits / copies | Build mean | Extraction mean | Family copy mean | Acquire lock wait mean | Copied bytes / family |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 750 / 3,750 / 750 | 0.439 ms | 0.156 ms | 0.0109 ms | 0.000049 ms | 108,468 |
| 2 | 798 / 3,990 / 798 | 0.423 ms | 0.139 ms | 0.0103 ms | 0.000041 ms | 108,468 |
| 3 | 817 / 4,085 / 817 | 0.422 ms | 0.150 ms | 0.0102 ms | 0.000040 ms | 108,468 |

The copy byte counter covers extractor-to-authoring retention only. Vulkan subsequently copies the same six columns from that lease into an output-family plan; its count/timing is not separately instrumented here. `BuildTicks` excludes cache-hit view additions, which could matter with new XR view sets. No lock-wait tail distribution or arena growth/retirement snapshot was captured. Thus these means disprove shared-lock contention as a *steady-state mean* issue in this one desktop scene, but they do not clear the S12 contention/lifetime gate or prove the range planner is the remaining owner. Deformation, changed geometry/materials, multiple views, actual alternating worlds, supersession, and teardown still need focused live checks. Upstream first-wins frame publication currently prevents same-frame alternating worlds; its exact-publication stability is an assumption to verify if that contract changes.

The last unvalidated phase-timing extension was removed before wrap-up. The retained source change is only the telemetry that produced the live numbers. No planner algorithm, cache key, storage ownership, or launch setting was changed. S12 remains **Active** and S13 must not start from this partial gate. The named validation editor was stopped; no regression tests were added or run pending explicit post-validation clearance.

## 2026-09-22 Continuation: Phase Attribution

At `236250e86`, the source now also accumulates stopwatch ticks for indirect
range planning, initial-view planning, and same-frame view checks (including
unchanged views). The prior build, extraction, deformation, copy, and
lock-wait counters remain intact.
This is measurement only: publication identity, extractor storage, and deferred
consumer copy boundaries are unchanged. The owning
`XREngine.Runtime.Rendering` Release build passed with zero warnings and errors.
The integrated `XREngine.Editor` Release build also passed with zero warnings
and errors after the instrumentation change.
The new counters have not yet been exercised in a matching live editor binary,
so the range-planner entry threshold remains untested and no optimization is
authorized by this update. The separate AA validation editor was running at
the time of this build; S12's live run must use its own named isolated session
after that overlapping runtime work finishes. Fresh mesh output remains an
entry condition for any S12 behavior or performance claim.

## 2026-09-23: Range Attribution And Planner Change

The isolated Release Vulkan/Advanced session used the checked-in main Sponza
FBX with `CpuDirect` submission. Its settled publication contained 465 draws,
465 ranges, one desktop view, and no deformation jobs. The viewport capture
showed fresh mesh output. The material/texture appearance remains under the
separate AA investigation; the S12 comparison uses identical draw/range and
publication identities, not a claim of final image quality.

Three stationary, settled baseline windows (64–80 seconds, 17–22 rebuilds
each) measured build means of 0.774, 0.936, and 0.868 ms. Indirect planning
alone measured 0.446, 0.546, and 0.516 ms per rebuild, exceeding both the
0.15 ms and 25% action thresholds. Extraction measured 0.160–0.189 ms;
initial-view planning measured 0.153–0.175 ms. There were no warmed build
allocations or copy failures; shared copies measured 0.014–0.016 ms each.

`AdvancedIndirectRangePlanner` now records the first-pass range index in
preallocated payload-index storage and reuses it during grouping. It removes
the second linear range search while preserving range ordering and immutable
consumer copies. The Release rendering build passed with zero warnings and
errors. Three settled post-change windows on the same 465-draw fixture
(55–64 seconds, 20–24 rebuilds each) measured build means of 0.420, 0.424,
and 0.396 ms and indirect-planning means of 0.170, 0.173, and 0.171 ms.
Each window exceeded the required 20% build-time reduction. Shared-copy means
were 0.010–0.011 ms, shared-lock waits were below 0.001 ms per rebuild,
and warmed build allocation and copy-failure deltas remained zero.

The Vulkan authoring-lease-to-frame-plan copy now reports its own count,
bytes, elapsed time, maximum, and rejection count through the Advanced profile
diagnostics. The settled session reported no rejected second copies. A camera
focus change produced different fresh geometry output. A live `ornament_01`
material `BaseColor` change to green read back exactly and the subsequent
canonical publication remained accepted with 465 draws and unchanged topology.
The process-wide maximum lock/copy counters include cold import and therefore
are not warm-window p99 evidence. Separate tail, multiview, deformation,
alternating-world, structural-churn, and teardown gates still require the
integrated lifetime build and focused live checks.

## 2026-09-23: Integrated Lifetime And Deformation Checks

The integrated Release Vulkan/Advanced editor built and launched in the named
isolated `s12-final-0923` session. The canonical geometry database now stages a
packed successor generation before publication, rewrites current geometry
bindings in the accepted publication, and retains prior immutable arena bytes
for older package/GPU pins. A cold-growth estimate skips this work unless the
packed live set reduces the required capacity. Deformation output slots reset
static append/owner metadata only after their native consumers complete; view
history and feedback generations are invalidated on scene/database changes.

In the 124-draw Sponza2 fixture, three deactivate/reactivate cycles restored
124 draws each time. Geometry committed bytes stayed at 18,693,252 and arena
capacity stayed at 28,640,708; three compactions reclaimed 56,079,756 bytes
in total. In the two-import fixture, 25 draws stayed live while the other
124-draw root was inactive. Two restore cycles returned to 149 draws and
triggered two compactions, reclaiming 37,386,504 bytes in total. Committed
bytes stayed at 37,282,100 and capacity at 48,892,808. Both fixtures showed
accepted canonical publication, no family-copy failures or preparation
deferral, and fresh restored mesh output. The two-import fixture exercises
copy-forward with live geometry records; these short cycles establish a
plateau for this workload, not a long-duration memory bound for every scene.

The checked-in `skinned-morph-animated.gltf` fixture produced one canonical
draw, one deformation job and dispatch, four deformed vertices, and 112 upload
bytes in the Vulkan backend. Four deactivate/reactivate cycles returned to
one draw/job/dispatch with no deformation arena allocation or family-copy
failure. Geometry committed bytes stayed at 1,920. Paired viewport captures
showed the mesh absent while inactive and present after restoration. The
settled preparation output reported `Ready`, `NativeConsumers`, and no
deferral; the canonical retention snapshot showed two retained publications,
two package pins, and four GPU pins rather than an accumulating unbounded
sequence. The named editor session was stopped through its owner manager.

An emulated sequential-eye fixture created left and right eye viewports, but
both eye captures were black in edit and play modes while desktop output was
live. This does not validate the multi-view output gate. The editor's
in-memory world snapshot command also rejected serialization of the imported
animated model, so it could not provide an actual alternating-world fixture.
Both limitations were observed outside the shared-preparation publication
itself; neither is counted as a passing S12 gate. Existing preparation tests
were attempted after live validation, but the unit-test project currently
does not compile because `AdvancedNativeShadingClosureContractTests` references
the absent `IAdvancedGlobalIlluminationProvider` type. No tests were changed.

## 2026-09-23: Bounded Deformation Generations And Stereo Validation

Review identified that resetting one global deformation static image before
acquiring the reusable output slot would drain all slots when real worlds
alternate. The implementation now selects one of `FrameSlotCount + 1` static
generations by exact scene/database/topology identity after acquiring the
current output slot. Output slots pin their static inputs through native
completion; an uncertain enqueue or missing producer fence poisons its slot
instead of releasing the inputs. A pinned generation forks to an unpinned
successor on static append, copying its mesh-slice map with its payload. Spare
generations allocate lazily, while the count and high-water storage remain
bounded. CPU deformation owner metadata resets only after both current slots
are acquired. A follow-up lifetime review found no remaining blocker. The
Release rendering project built with zero warnings and errors, followed by a
fresh isolated Release editor build from the integrated source.

The repaired build's animated glTF fixture returned one draw, one deformation
job/dispatch, four deformed vertices and 112 upload bytes. Two further
deactivate/reactivate cycles alternated zero and one draw without preparation
deferral, deformation arena allocation failure, or family-copy failure. The
settled Vulkan viewport capture was viewed and showed the quad. Copy failures
remained zero across the repaired run. The earlier keyed build had seven
startup family-copy failures but no increase over more than 42,000 frames;
that observation is not used as a clean startup result for the repaired build.

The emulated **single-pass** stereo fixture exercised the formerly missing
view path without changing runtime code. One canonical 124-draw publication
served the desktop view plus two stereo layers (`visibilityViewCount=3`).
The two captured `FinalPostProcessOutputTexture` layers were viewed: both
showed the Sponza2 brick wall and floor from slightly different eye positions.
Their float hashes differed, and the images had finite pixel samples. At
render frames 166 and 1547, family-copy failures stayed at zero while copies
rose from 65 to 2807. The earlier sequential-eye black captures remain a
separate fixture limitation; they do not negate this passing single-pass
multi-view check.

The editor's `snapshot_world_state` succeeds for procedural primitives, but
`restore_world_state` retargets the existing runtime host. Its `GPUScene`
identity and database epoch stayed constant while draw records rose from one
to two to three across A, B, and restored A. This is **not** an actual
alternating-world exercise; it is excluded from that gate. The engine has no
existing MCP control to create and render a second runtime world host in the
same process. The separate-world lifetime path remains unvalidated live.
The repository's testing policy requires explicit user clearance before
adding tests for this integration; that clearance is pending. Existing
preparation tests remain blocked by the unrelated missing GI interface in
the unit-test project. S12 remains Active, and S13 must not start.

After the other named editor session was stopped by its owner, a fresh
Release Vulkan/Advanced run admitted the same 465-draw, 465-range main
Sponza fixture. Three settled windows of 73.6, 94.7 and 69.9 seconds
contained 25, 31 and 21 rebuilds. Mean build times were 0.477, 0.467 and
0.500 ms; indirect planning was 0.203, 0.209 and 0.207 ms. Every window
remained more than 20% below the recorded baseline means of 0.774, 0.936
and 0.868 ms. Warm build allocation and family-copy failure deltas were zero
in every window. The Sponza viewport was captured and viewed after moving
the camera near its ornament geometry; fresh geometry remained visible.
This clean session was stopped through its named owner manager.

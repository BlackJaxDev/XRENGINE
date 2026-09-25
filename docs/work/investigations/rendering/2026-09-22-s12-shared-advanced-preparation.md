# S12: Shared Advanced Preparation

Status: Validated for its reachable scope. The local reachable-path gate passed, and the separate-world lifetime gate was dispositioned Not Applicable on September 25 by explicit user decision because no supported runtime path alternates distinct `GPUScene` owners (see [September 25 distinct-world disposition](#september-25-distinct-world-disposition)). Neither parent reproduced the reported 153-165 ms stall, and neither closes the overall Vulkan stall investigation.

## September 23 Merge Disposition

The merge combines local `3893e7e6d` and incoming `4a0d4a2f6`, both based on `236250e86`. They independently performed S12 range-planner work. Keep one implementation: the local preallocated generation-stamped hash lookup plus remembered payload-to-range indices. The incoming remembered-index optimization is already included in that implementation; do not apply or count it twice.

Preserve the incoming work that is independent of that optimization: exact scene-publication cache identity, scene/epoch temporal invalidation, bounded view-history capacity, delayed-feedback epoch checks, packed geometry compaction, immutable geometry shared across Vulkan frame slots, and bounded pinned static-deformation generations. Keep the local exact-range deformation write, cached OpenGL capability resolution, and arena/frame-upload diagnostics. Both immutable visibility-copy boundaries and their publication checks remain required.

Telemetry has one canonical counter per scope: `IndirectPlanningTicks`, `MaximumAcquireLockWaitTicks`, and `MaximumCopyLockWaitTicks` replace the equivalent local `RangePlanningTicks` and `*LockWaitMaxTicks` names. Initial-view planning, cache-hit view checks, and maximum successful-copy duration remain. The authoring-to-frame-plan copy is reported by renderer-owned `framePlanInputCopyDiagnostics`; the duplicate process-global `DeferredFamilyCopy*` counters are removed. Its timer measures the six column copies after capacity acquisition; the older local measurement also included capacity acquisition. Historical reports retain their original meanings and must not be combined as one timing series.

Merge review also found that an oversized view set could be assigned to `_lastVisibilityViewSet` before capacity validation threw. Repeating that request would incorrectly take the unchanged-view shortcut. Capacity is now checked before incremental view mutation, and a view set is remembered only after its plan construction succeeds. Initial Build checks capacity after clearing old authoring identity/plans but before scene access or frame-slot acquisition, so invalid initial requests cannot consume and seal slots needed by a corrected retry. The focused failure/retry probe below passed.

The two parent evidence sets below are historical, separately identified records. The local 393-draw and incoming 465-draw planner comparisons use different workloads and implementations; their reductions are not additive and are not measurements of the merged binary. Historical statements such as "no planner change", "S12 validated", and "S13 must not start" describe the named parent's stage of work, not a replacement for this disposition.

First-wins publication rules out alternating worlds within one published frame. It does not prove lifetime safety when distinct runtime `GPUScene` owners alternate across frames while older GPU work is retained. Incoming world restore reused the same owner/epoch, so that separate-world gate remains open. Retain the passing named subgates, but do not promote the combined S12 scope to Validated solely from the local same-frame argument.

Remaining integration gates:

- [x] Build the merged Release editor and inspect an isolated live Vulkan/Advanced run; verify canonical draws/ranges, both copy diagnostics, zero warmed allocations/copy failures, and viewed fresh output. This is smoke coverage, not a repeated three-window benchmark or proof of every mutation/lifetime path; see the camera/output limits below.
- [x] Exercise oversized view request -> rejection -> identical retry -> rejection -> valid request; verify no false cache hit or omitted view is admitted. The isolated public-method probe below passed; it is not a GPU publication/lifetime test. No new regression tests without the repository-required post-validation clearance.
- [x] Dispositioned Not Applicable (September 25, user decision): exercise two distinct runtime-world owners across frames with in-flight consumers, deformation/static inputs, same-owner topology replacement, and teardown. No supported runtime fixture exists; the evidence and reopening condition are recorded below. The same-host world-restore experiment was not used as proof.
- [ ] Retain the incoming AA output/resource-transition dependencies and the explicitly untested forced growth/failure, duplicate-key, hardware XR and long-duration churn limits. Narrow passing churn/stereo checks do not close those separate gates.

The September 23 `render<-collect` experiment and the full S13a-S13i plan remain in the [stall TODO](../../todo/rendering/vulkan-stall-remediation-todo.md) and [collect-wait investigation](2026-09-23-vulkan-render-collect-wait.md). The temporary identity filter was reverted; this merge does not implement S13 or claim the roughly 60 ms wait is fixed. Resolve or explicitly disposition the remaining S12 integration gate before promoting dependent S13 implementation work.

### Merged Source Validation

The final isolated Release editor build passed with zero warnings and errors.
Named session `s12-merge-0923` ran Vulkan/Advanced/CpuDirect with TSR at
1920x1080 output and 1286x723 internal resolution. Final-binary snapshots at
render frames 1216 and 2363 retained 393 canonical draws/ranges, one visibility
view and no deformation job. Their delta was 1,147 rebuilds, zero Build allocation
bytes and zero extractor-copy failures; renderer-owned frame-plan copy diagnostics
also reported no rejected copies. Arena/upload growth, overflow and retired
generation counters were zero. These are smoke observations, not a matched
performance experiment or coverage of the incoming active-deformation lifetime
changes. Cold import allocations are excluded from the warmed delta.

Viewport PNGs were saved and viewed across camera moves and a no-build restart of
the final binaries. Origin views were dominated by the environment background and
a black near surface; the initial room-like image was not sufficient proof of
Sponza mesh output. Focusing the imported `sponza` node moved the camera to about
(18.345, 20.694, 0.319); a subsequent settled capture showed the tiled roof and
central opening. The same snapshot collected 361 pass-1 and 32 pass-4 mesh commands.
This establishes scene geometry and camera-responsive output in the smoke, not
correct interior shading, AA quality, temporal history, or a resolved startup
rendering defect. The existing AA visual gates remain independent.

A disposable console probe referenced the final built assemblies and exercised
an isolated extractor with one view slot. Two identical oversized incremental
requests both rejected with the saved plan and content generation unchanged;
unchanged and changed valid requests then succeeded. Two oversized initial Build
requests rejected before accessing a deliberately absent scene, with arena/upload
telemetry unchanged. The probe passed; no tracked tests were added or modified.
The initial capacity preflight is included in Build timing, before the narrower
initial-plan construction timer. The unit-test project's historical GI-interface
compile failure was not rerun as part of this merge.

Evidence is under `Build/_AgentValidation/20260923-130828-s12-merge/`: final build
and probe logs in `logs/`, `final-render-{settled,after}.json` and
`final-profile-{settled,after}.json` in `mcp-output/`, and the viewed screenshots
in `mcp-captures/` (the final focused image is
`Screenshot_20260923_132154_939_9705d2fcd4f64de4bfea3a63a0e226a9.png`).
The named editor was stopped through its session manager. The ignored evidence is
disposable; this section retains the result and its limits.

## Local Parent Evidence: 3893e7e6d (393 Draws)

The following records the local parent's passed reachable-path gate and its explicit limits; it is not validation of the combined lifetime changes.

### Contract And Acceptance Criteria

`RenderWorldSnapshotPublication` is first-wins for a frame, so actual callers cannot alternate worlds within one published frame. The shared service owns one extractor under `_sync`. OpenGL retains renderer-owned copies; Vulkan copies extractor columns into an authoring lease and then into a deferred output-family plan. A deferred consumer must match the exact scene publication, view/content generation and retained lease. S12 kept those boundaries; it did not add a per-world cache, move mutable extraction outside the lock, or hand mutable spans to a deferred consumer.

Before changing behavior, the range planner was declared actionable only at a warmed mean of at least 0.15 ms per rebuild and at least 25% of Build. The fix had to lower mean Build by at least 20% in three matched 60-second Release Vulkan windows, retain identical draw/range/publication behavior and visual output, add no warmed allocation, and keep mean acquisition wait below 0.05 ms and family-copy time below 0.10 ms. The stationary workload was Sponza, Vulkan, Advanced, CpuDirect, 393 draws and 393 ranges, without a debugger. Diagnostics were sampled at window boundaries. The named isolated editor was `s12-sharedprep-0922`; ignored evidence is under `Build/_AgentValidation/20260922-164422-s12-shared-prep/`.

### Measured Planner Cause And Fix

`AdvancedIndirectRangePlanner.Build` previously searched every accumulated range for each of 393 payloads, then repeated that search during output grouping. The workload has 393 distinct keys, so the source path performs 154,449 key comparisons per build. It was not a dictionary or lock bottleneck. The measured planner mean of 0.152-0.154 ms was 39-40% of the extractor Build mean, clearing both entry thresholds.

The planner now uses a preallocated, generation-stamped open-addressing table and remembers each payload's range index for the grouping pass. Full key equality still resolves hash collisions, and first-encounter order, checked offsets, producer selection and structural signature remain intact. The lookup uses about 1.25 MiB of one-time default-capacity storage and does not allocate on a warmed build.

| Matched window | Rebuilds before / after | Planner before / after | Build before / after | Build reduction |
| --- | ---: | ---: | ---: | ---: |
| 1 | 834 / 869 | 0.152614 / 0.053315 ms | 0.394122 / 0.275828 ms | 30.0% |
| 2 | 845 / 876 | 0.151771 / 0.042864 ms | 0.377330 / 0.248071 ms | 34.3% |
| 3 | 825 / 846 | 0.154181 / 0.044255 ms | 0.391893 / 0.263779 ms | 32.7% |

The three pre/post reports are `reports/stationary-instrumented-{1,2,3}.json` and `reports/stationary-optimized-{1,2,3}.json`. All had 393 draws/ranges, zero warmed Build allocation and zero copy failures. Mean lock waits were below 0.0001 ms; first-copy means were about 0.010 ms. Counts vary with frame rate, so the comparison is per rebuild, not total work per wall-clock second. This is a CPU preparation improvement, not evidence that Vulkan submission, recording or the GPU stall is fixed.

### Lifetime, Views And Mutation Gates

- Before and after the planner change, exact camera captures were visually identical. Moving the camera changed the rendered image. Toggling `sponza_00` off/on changed 393 to 392 to 393 draws/ranges. Changing a roughness uniform from 0.9 to 0.2 and back, and moving the Sponza root from x=0 to x=1 and back, completed without copy failures. The original scene state was restored in the editor.
- An isolated emulated VR run published three visibility views (desktop and two eyes); its stereo pipeline reported two eye views. A 30-second window had 370 rebuilds, 740 successful copies, 4,070 same-frame hits, zero copy failures, zero Build allocation and zero retired arena generations (`reports/emulated-stereo-1.json`). Left/right captures were inspected, but the capture path appeared to show the same layered output; distinct eye images are not claimed from those PNGs.
- A checked-in skinned/morph glTF temporarily added to the ignored Unit Testing World settings produced 394 draws, one active deformation job, four deformed vertices and a true GPU dispatch. An initial 30-second run allocated 480 managed bytes per extractor build, traced to `XRDataBuffer<T>.Write` creating a whole-buffer writer and explicit dirty-range list. Writing via `AllocAt<T>` commits the exact range without that list. A subsequent 30-second live run had 424 rebuilds and zero warmed Build allocation, one job, true dispatch, no new copy failures, arena high-water of four vertices and no growth/retired generations (`reports/active-deformation-1.json`, `reports/active-deformation-write-optimized-1.json`). One cold restart reported seven transient copy failures during concurrent import, then recovered; the warmed validation window had zero additional failures. The exact-publication check still defers and retries instead of copying stale data.
- Observation-only telemetry measured both Vulkan copy boundaries. In a 30-second static run, 467 successful extractor-to-authoring copies of 108,468 bytes averaged 0.010178 ms; 467 authoring-to-deferred-family copies of the same size averaged 0.012791 ms (`reports/deferred-copy-static-1.json`). Neither justified removing a lifetime-safe copy. Acquisition and copy-lock waits remained far below their acceptance budgets. Arena and frame-upload growth/overflow/retired-generation counters stayed at zero in the settled static and active-deformation runs.
- A built-in four-box shared-mesh scene published 397 draws and 397 ranges (`reports/shared-mesh-distinct-handles-1.json`). The live GPU publication gave distinct geometry handles, so it did not exercise a duplicate range key. The planner compares the full key on collision and preserves the prior first-encounter grouping, but no live duplicate-key claim is made. Same-frame alternating worlds remain unreachable under first-wins publication; per-world caching would be unjustified without an upstream contract change.
- Scene mutations and each subsequent frame superseded the previous publication without warmed copy failures. The named editor was stopped and restarted through the session manager between variants, providing the reachable consumer teardown/recreation path. No retained mutable extractor span was introduced.

The temporary ignored `Assets/UnitTestingWorldSettings.jsonc` was restored byte-for-byte to its original SHA-256 `0691A180F2A838A2D97C92E6D067AEDDB666147F9F3FE8E58C0FCD73D4F73B8A`.

### OpenGL Shared-Consumer Finding

The OpenGL consumer rendered the same 393-draw Sponza scene and had zero copy failures, but its extractor Build allocated 760 bytes per rebuild (`reports/opengl-shared-preparation-1.json`). Temporary per-stage GC probes assigned all 760 bytes to range planning/capability resolution: parsing `Version` with `Split` cost 280 bytes and repeated `GL_EXT_mesh_shader` extension queries cost 480 bytes. Span parsing and one context-initialization dialect resolution removed both costs. A matched live OpenGL window after those individual changes had 1,125 rebuilds, zero warmed Build allocation and zero copy failures (`reports/opengl-allocation-fixed-1.json`). Temporary allocation probes were then removed; cumulative Build allocation telemetry remains. OpenGL timing varied with this separate workload and is not used to claim a Vulkan speedup.

### Disposition And Validation Limit

The planner optimization, exact-range typed write and OpenGL capability fixes passed live behavior and warmed allocation gates. Retained S12 diagnostics distinguish extraction, planning, first copy, deferred copy, lock waits, allocations and arena/upload growth. No cache or lock redesign was warranted. The final isolated Release editor build, after removing the temporary allocation probes, passed with zero warnings and errors. A final 15-second OpenGL run had 1,050 warmed rebuilds, zero Build allocation and zero copy failures (`reports/opengl-final-1.json`). A Vulkan run using those exact binaries had 393 draws, 209 warmed rebuilds, zero Build allocation and zero copy failures; planner, first copy and deferred copy averaged 0.0641, 0.0117 and 0.0147 ms (`reports/vulkan-final-1.json`). The named editor was stopped afterward. No regression tests were added or run because repository policy requires explicit clearance after live feature validation.

S12 is validated for reachable desktop, emulated multi-view, deformation, mutation and teardown paths. Duplicate published geometry keys, true same-frame alternating worlds, forced capacity growth/failure and production XR hardware were not exercised; those must not be inferred from this gate. The original severe frame-time report and recurring publication/recording work remain owned by S13 and the later integrated stall closeout.

## Incoming Parent Evidence: 4a0d4a2f6 (Initial Baseline And 465-Draw Continuation)

The following preserves the incoming branch's chronology, including superseded partial-gate statements, independent planner measurements, geometry/deformation fixes, and the remaining distinct-world limitation. Its fixture-specific results remain separate from the local parent above.

### Entry Evidence And Hypothesis

The Vulkan/Advanced Unit Testing World currently publishes 393 canonical draws and 393 indirect ranges for one desktop view. The first live Release sample showed 158 rebuilds, 790 same-frame cache hits, 131 successful family copies, no copy failures, and 108,468 bytes copied per family. After warm-up, an additional 1,144 rebuilds consumed approximately 0.449 ms each in `AdvancedPreparationExtractor.Build`, of which approximately 0.159 ms was command extraction. The original 153-165 ms stall has not been reproduced by this workload.

The first candidate for the residual cost is `AdvancedIndirectRangePlanner.Build`: it linearly searches existing ranges once per payload in each of two passes. With 393 unique range keys, that is 393 squared key comparisons per rebuild. This is a source-level hypothesis, not yet a measured attribution. The check that can reject it is a per-phase timer showing that range planning is small relative to the rebuild or that warmed build time is already below the entry threshold. Arena growth and shared-lock contention are separate candidates and must not be inferred from the rebuild duration.

### Ownership And Acceptance Set Before Behavioral Change

- `RenderWorldSnapshotPublication` is first-wins for a frame. Actual callers cannot alternate worlds within one published frame today. The singleton service holds one extractor under `_sync`; changing that cache key or introducing per-world storage would require a separate upstream world-publication and lifetime review.
- Extractor columns mutate on the next build and when views are added. OpenGL copies into renderer-owned arrays; Vulkan copies into an authoring lease under the service lock and copies once more into a deferred output-family plan. Keep those coherent copy boundaries and exact publication/view/generation checks. Do not pass mutable spans to deferred consumers or build outside `_sync` in S12.
- Gather three settled 60-second stationary windows before and after any behavioral change, with identical Release Vulkan/Advanced settings and a 393-draw/393-range Sponza scene. Record frame/rebuild/hit/copy counts, draw/range/job counts, stopwatch-frequency-normalized phase and lock timings, copied bytes, allocations, arena growth and retired generations. Sample diagnostics only at window boundaries. Also exercise camera motion and one scene/view change. If the workload or accepted frame identity changes, discard that comparison.
- Treat range planning as actionable only if its warmed mean is at least 0.15 ms per rebuild and at least 25% of total build mean. If changed, require at least a 20% reduction in warmed build mean across matched windows, with no more than 0.05 ms mean lock wait per acquisition and 0.10 ms mean family-copy time, zero copy/publication mismatches, no new per-build allocation, no unbounded arena or lease retention, and no correctness change in stationary/moving images or publication identities. A slower neighboring stage or a changed scene invalidates a claimed speedup.
- If those entry conditions fail, retain low-cost telemetry and explicitly defer the range/storage optimization. A live check of the ordinary Vulkan path is still required; a successful build is not the gate.

The named isolated editor session is `s12-sharedprep-0922`; task evidence is under `Build/_AgentValidation/20260922-164422-s12-shared-prep/`. No regression tests are added or run without the repository's post-validation test clearance.

### Evidence And Disposition

The instrumented `XREngine.Runtime.Rendering` build passed with zero warnings and errors. The same source was built into the named isolated Release editor with zero warnings and errors. A Vulkan viewport capture was saved and viewed; it showed the running scene with a highlighted edge, though the initial camera was too close to a surface to use as a broad visual-correctness comparison. Runtime introspection confirmed 393 draws and 393 ranges, one view, one rebuild with five same-frame hits per prepared frame, a valid canonical publication, zero deformation jobs, zero copy failures, and no per-frame managed allocation after startup.

Three consecutive, settled, approximately 60-second windows from that process:

| Window | Rebuilds / hits / copies | Build mean | Extraction mean | Family copy mean | Acquire lock wait mean | Copied bytes / family |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 750 / 3,750 / 750 | 0.439 ms | 0.156 ms | 0.0109 ms | 0.000049 ms | 108,468 |
| 2 | 798 / 3,990 / 798 | 0.423 ms | 0.139 ms | 0.0103 ms | 0.000041 ms | 108,468 |
| 3 | 817 / 4,085 / 817 | 0.422 ms | 0.150 ms | 0.0102 ms | 0.000040 ms | 108,468 |

The copy byte counter covers extractor-to-authoring retention only. Vulkan subsequently copies the same six columns from that lease into an output-family plan; its count/timing is not separately instrumented here. `BuildTicks` excludes cache-hit view additions, which could matter with new XR view sets. No lock-wait tail distribution or arena growth/retirement snapshot was captured. Thus these means disprove shared-lock contention as a *steady-state mean* issue in this one desktop scene, but they do not clear the S12 contention/lifetime gate or prove the range planner is the remaining owner. Deformation, changed geometry/materials, multiple views, actual alternating worlds, supersession, and teardown still need focused live checks. Upstream first-wins frame publication currently prevents same-frame alternating worlds; its exact-publication stability is an assumption to verify if that contract changes.

The last unvalidated phase-timing extension was removed before wrap-up. The retained source change is only the telemetry that produced the live numbers. No planner algorithm, cache key, storage ownership, or launch setting was changed. S12 remains **Active** and S13 must not start from this partial gate. The named validation editor was stopped; no regression tests were added or run pending explicit post-validation clearance.

### 2026-09-22 Continuation: Phase Attribution

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

### 2026-09-23: Range Attribution And Planner Change

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

### 2026-09-23: Integrated Lifetime And Deformation Checks

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

### 2026-09-23: Bounded Deformation Generations And Stereo Validation

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

## September 25 distinct-world disposition

The separate-world lifetime gate requires two distinct runtime `GPUScene` owners
to alternate across frames while older GPU work is retained. A source audit of
every supported path that changes the rendered world found none that does so:

- MCP `restore_world_state` and `load_world` call `RetargetWorld`, which reuses
  the existing runtime host, its `VisualScene3D` and its `GPUScene`.
- Play mode's `SerializeAndRestore` restores scenes inside the same source world
  and host; `ReloadFromAsset` is an unimplemented stub; `PersistChanges` does not
  change worlds.
- `Engine.GetOrCreateWorld` does create a separate host per `XRWorld`, but in the
  editor it is reached only by viewport rebinding to the configured startup world
  (unset for the Unit Testing World) and by standalone dialog windows, whose
  worlds use the UI pipeline rather than Advanced shared preparation.
- `RenderWorldSnapshotPublication` remains first-wins within a frame.

On September 25 the user explicitly chose to disposition the gate rather than
add a diagnostic fixture that retargets a window to a second runtime world. The
gate is therefore **Not Applicable** for the current product paths. Reopen it,
and exercise the bounded static-deformation generations, completion-gated reuse
and teardown with genuinely distinct owners, when any of these become supported:
multiple windows or viewports rendering different worlds through the Advanced
pipeline, a play-mode or world-load path that creates a new runtime host, or an
implemented `ReloadFromAsset`. S12's other passing named subgates and its
explicitly untested limits (forced growth/failure, duplicate published geometry
keys, production XR hardware, long-duration churn) are unchanged.

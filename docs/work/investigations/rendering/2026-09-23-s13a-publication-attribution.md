# S13a publication and collect-wait attribution

Status: Validated on the September 26 final binary (see the final-binary
closeout section at the end): the four-pair observer/retention matrix, the
OpenGL harness comparison and the attached-debugger window are measured, the
historical backend divergence is dispositioned Not Reproducible with a reopening
condition, and the measured callback owner no longer executes. The elevated
WPR/GC capture was taken by the operator on September 26 at 09:36 local and
confirms zero samples in the September 23 callback owners (see "September 26
elevated CPU and GC capture"). The historical text below is preserved as the
observation record.

## Frozen workload and historical evidence

The pre-instrumentation checkout was `cee30cd5790a019c2fe42ed163208d91bd1dc8ce` with no tracked diff. Its isolated Release editor DLL SHA256 was `0FAEB94EC72780D26DB0E18289D56C3D702383468C4C706C00BEA524AE556E0`. The live observer-enabled isolated Release editor DLL SHA256 was `982D7B35E5EFE85B1759CBCB173B9EA3DF47D0E194635FADC5B172F8054409D0`. A final shared-output Release rebuild for the paired matrix has editor executable/DLL/Rendering DLL SHA256 values `48E88D219AB464BB0312B2749E016A7D29BAD842AC52E7A29F6E04E1C1C14499`, `B8E66F0FF77A06C359AFADA7283649103E5602830B58968A8F889227FC53BD3B`, and `DB8434AD70DBEFB948CE1ED4552A5ED9B920728BF61617D52083EB4110AFD133`. The observer build includes the S13a source instrumentation and no intended change to publication behavior. Both isolated Release builds and the final shared-output rebuild passed with zero warnings and errors. Observer-off and observer-on comparisons use the same final shared-output binary.

The later telemetry-corrected shared-output Release editor DLL and Rendering DLL SHA256 values are `1C2955F0F5C24D0FFD7EB3056B514E9DCF8A9503814D8159CE9BACCFF2B5785A` and `1A3014378AE6673393886AF71538FE7E351A583B74DA60E34D2B721E96AA183E`; its editor executable SHA256 remained `48E88D219AB464BB0312B2749E016A7D29BAD842AC52E7A29F6E04E1C1C14499`. The Debug editor DLL SHA256 is `87C9B38965737D7DDC640DF335D564E4E7226F605EB1BD416D81446539C4316A`. The final live Vulkan trace/mutation and separate Debug/cold captures use these later binaries. The six observer runs (three pairs) and all-frame OpenGL comparison use the earlier matrix binary above; no performance comparison silently crosses these build identities.

The subsequent read-only identity-manifest export rebuilt Release again. Its shared-output Editor DLL and Rendering DLL SHA256 values are `B33B5C383C40CC97B3D80BDA2BBE6193A0D75BFEAF5A1474C3D69A1F85263AC3` and `4890F5AC2FACCB21122EFBEC77056874B44B37624587A1F630D5C9333D03544B`. Vulkan and OpenGL manifests were copied from the same isolated Release editor build with this source and telemetry off. No frame-rate result is attributed to the manifest-only build.

The task-local settings file is `Build/_AgentValidation/20260923-183000-s13a-attribution/scratch/unit-world-s13a.jsonc` (SHA256 `4C0588F00A2C6063F177B97857863719FFE2FFF45E17CB9F24E5045BDDDBE518`). It enables the Sponza2 OBJ import without `OptimizeMeshes` or post-import merging. Other Unit Testing World settings came from the current generated settings. This fixture produces 393 canonical resident draws and 393 resident geometries, matching the historical count. It is an explicit fixture variant, not a claim that the current generated settings are identical to the user's September 23 configuration. The fixed camera is `(-20, 2, 4)` looking at `(-20, 2, -8)`. Output is 1920×1080, TSR internal size 1286×723, Advanced pipeline, CpuDirect. The HUD draw-call count is much smaller than the 393 canonical submissions. The profiler's `gpu_scene_command_count` is a maximum of commands in executed render passes, not the resident draw count; its stationary median was 109.

The runtime device reports NVIDIA GeForce RTX 4070 Laptop GPU. Windows reports driver version `32.0.15.9282`, the Balanced power scheme, and battery status `2` at the later query; power state was not captured at every measurement boundary. The current Release capture used Vulkan validation off, CPU profiling on, dense GPU timing off, no debugger, and uncapped Vulkan presentation. Effective backend, strategy, TSR, and workload identity were verified in the frame stream. A separate Debug capture without a debugger is recorded below; attached-debugger behavior remains unverified.

The [September 23 collect-wait investigation](2026-09-23-vulkan-render-collect-wait.md) preserves the sparse 20-sample results: Release Vulkan wait 49.1405–58.9100 ms (mean 54.414155), Release OpenGL 0–3.2981 ms (mean 2.906120, one zero sample), and the temporary identity-exclusion Vulkan run 3.0170–5.6852 ms (mean 3.511255). Debug Vulkan without debugger was 61.4852–73.1814 ms (mean 65.601110). The instrumented Vulkan CPU dump identified 393 callbacks in one main pass and 50.912 ms in that pass; the identity-exclusion dump had an empty main dirty queue. Those are different binary variants. Their exact source/diff and binary hashes were not preserved, and their ignored reports have expired. The current `cee30cd57` merge was committed at 13:23 local, after the historical 12:30–12:40 samples and changed shared Advanced publication code; the source drift is a plausible reason to refresh the backend comparison, not proof of which change mattered. Do not assign this checkout's hashes to those samples or treat them as all-frame percentiles.

## Current Release Vulkan reproduction

The frozen pre-instrumentation binary ran a warmed 60-second fixed-camera capture. The capture had 597 sampled frames, one frame-output workload identity (`10991459253885323059`), verified camera pose, and 426 admission-image color buckets. The admission image was viewed; Sponza is visible, but exposure is badly saturated, so this is timing evidence rather than visual-correctness evidence. The all-frame stream reported `render<-collect` p50 80.378 ms, p95 129.978 ms, p99 265.443 ms, max 710.149 ms. Render dispatch was p50 17.503 ms, p95 39.004 ms, p99 about 53.6 ms. Coarse GPU coverage was 99.832%; Vulkan validation was off. The 60-second stream saw 217 Gen0 collections and 1882.655 ms increase in process cumulative GC pause time, which is not a per-callback GC attribution.

The normal publication-quiet readiness gate timed out twice because publication never remained quiet for five seconds. The capture therefore used the harness's explicit `-NoStabilityGate` mode after a 25-second warmup. Its accepted output identity stayed constant, but native-resource/descriptor and required-backlog retention checks failed. The capture cannot pass S00/S13a's full evidence gate. The raw stream and manifest were copied to the task run's `reports/baseline-vulkan-release-capture/raw/` before runtime log retention removed them.

The harness also rejected a valid 1920×1080 MCP PNG through `System.Drawing.Bitmap`. Its admission-image reader now uses Windows Imaging Component; the same image decoded and its pixel buckets were checked. This changes measurement tooling only, not the editor binary.

## Current Debug Vulkan reproduction without debugger

The final source also rebuilt in Debug with zero warnings and errors. A separate warmed 60-second Debug Vulkan capture used the same fixture, fixed camera, Advanced/CpuDirect/TSR path and frame-output workload identity `10991459253885323059`, with the observer off and no debugger attached. It recorded 417 sampled frames: `render<-collect` p50 98.823 ms, p95 127.402 ms, p99 134.754 ms; render dispatch p50 54.806 ms, p95 68.986 ms, p99 80.731 ms. The active-pass command median was 109. Admission image and camera were verified, diagnostic loss passed, and coarse GPU coverage was 99.76%. The admission image was viewed; geometry is visible but exposure is saturated. The separate pipeline GPU-history dump was unavailable. Retention failed, including required-backlog return. This is the current Debug/no-debugger condition, not an attached-debugger result or a matched comparison against historical Debug binaries.

## Separate cold-cache Release case

A zero-second post-launch wait was attempted first. It was rejected because Vulkan had not yet submitted a matching resource-planner generation for the admission screenshot; no timings from that attempt are accepted. A second cold-cache launch used a **10-second post-launch wait** for image readiness, then captured a separate 60-second fixed-camera window with the observer off. It recorded 567 sampled frames at the same workload identity: `render<-collect` p50 87.189 ms, p95 128.814 ms, p99 156.598 ms; render dispatch p50 18.122 ms, p95 38.731 ms, p99 65.615 ms. Camera and admission image were verified and the image was viewed, diagnostic loss passed, and coarse GPU coverage was 99.824%. The active-pass command median was 109. Native live resources grew by 1,057 and descriptors by 1,965, so retention failed despite required backlogs returning to baseline. This is a cold-cache launch with a documented 10-second image-readiness wait, not a measurement of the first 10 seconds or a matched warm-versus-cold speedup.

## Frozen-binary observer comparison

Six sequential Release Vulkan runs used one shared-output executable and the same fixture, camera, CpuDirect strategy, TSR output, `DevelopmentProfile`, one-frame sampling, 25-second warmup, 60-second stationary window and 60-second controlled-camera-motion window. The observer order was off/on, on/off, off/on. The on setting enabled both aggregate telemetry and the bounded trace. All six had the same accepted workload identity `10991459253885323059`, verified camera positions, passing admission screenshots and diagnostic-loss checks, validation layers off, and 99.80–99.87% coarse GPU coverage. The median active-pass command count was 109 in each run; this is not the resident 393-draw count. Raw summaries and the paired comparison are in the task run's `reports/observer-matrix/`.

| Pair | Off/on order | Stationary render mean off → on (ms) | Stationary render p95 / p99 off → on (ms) | Motion render mean off → on (ms) | Motion render p95 / p99 off → on (ms) |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | off/on | 19.878 → 15.267 | 30.570 / 35.077 → 24.452 / 31.398 | 34.389 → 32.246 | 69.076 / 84.053 → 64.191 / 82.299 |
| 2 | on/off | 16.340 → 16.031 | 25.155 / 29.744 → 24.795 / 30.924 | 38.640 → 32.805 | 82.120 / 103.973 → 66.960 / 76.471 |
| 3 | off/on | 26.460 → 23.952 | 42.957 / 58.534 → 36.470 / 53.295 | 39.120 → 32.545 | 100.476 / 137.711 → 72.002 / 81.179 |

All three pairs meet the predeclared *positive overhead* thresholds: observer-on render mean did not increase by 0.5 ms/frame, and p95/p99 increases stayed within the larger of 5% or the three off-run spread divided by their median. Stationary off-run mean ranged 16.340–26.460 ms; that large spread limits the strength of a causal performance claim. The wait distributions also showed no positive observer penalty beyond the same allowance. This is an overhead diagnostic for this frozen binary, not a speedup claim. **Every run failed native-resource and descriptor retention** despite passing image and loss admission; the sixth run also failed the required-backlog return check. Native live-resource increases were 684–4,571 and descriptor increases were 2,000–4,055, well above the harness allowances. Therefore the full S00/S13a evidence gate remains open. Subsequent telemetry correctness edits require another final-build overhead check before any observer gate can close.

## Observer-enabled callback ownership

Observation is opt-in: `XRE_S13A_PUBLICATION_TELEMETRY=1` enables bounded aggregate counters; `XRE_S13A_PUBLICATION_TRACE=1` additionally enables a 65,536-event numeric ring, with optional command-ID filtering. Read-only MCP methods expose cumulative snapshots and paged trace events. All callback allocation deltas are read on their owning thread. The trace reports overwrite and busy-drop counts. Default-off behavior is preserved at the instrumentation branches; the paired overhead matrix remains the live validation of that claim.

In one stationary Vulkan MCP interval of 70.763 seconds, the 393-draw scene produced 766,656 identity notifications, zero other dirty notifications, 716,804 queue additions, 381,392 swap callbacks, and 381,128 `TryUpdateMeshCommand` calls. All recorded update calls returned unchanged, yet each attempted logical registration and wrote state-class and transparency metadata; no draw metadata writes were recorded. Owner-thread callback allocations totaled 673,892,448 bytes. Measured callback body time was 54,956.581 ms, including 53,703.314 ms in logical registration; lock acquisition totaled only 16.242 ms. These are cumulative elapsed scopes across callbacks, not an additive frame-level critical path. Held-body wall time can still include GC pauses or descheduling; that split remains open.

A recent bounded Vulkan trace for process-local command `687` shows publication sequence 878 publishing identity, two `PublishCanonicalDrawIdentities` notifications, queue insertion in collections 11 and 13 on the next cycle, collection 11 invoking and acknowledging the swap callback, and collection 13 skipping an already clean command. The publication step also recorded a material-mutation reuse rejection. The ring had overwritten older events but reported zero busy drops in the page. A reuse rejection is not proof that a new publication committed. Collection and command IDs are process-local and lack a durable world/source mapping, so this trace cannot establish cross-process identity equality by itself.

Windows Performance Recorder's installed CPU profile includes context-switch, ready-thread and sampled-profile stacks, which could split running CPU from ready delay and blocking while CLR suspension events provide GC overlap. A bounded `CPU.light` WPR start was attempted against the warmed owned Vulkan editor session; Windows rejected it with `0xc5585011` (failed to enable the policy to profile system performance). `wpr -status` confirmed no recorder remained active. The user subsequently completed the elevated capture described below. Process cumulative GC pause and callback Stopwatch time remain separate evidence, not subtracted estimates.

Review found two accuracy defects in the initial observer: a delayed ring producer could replace a newer event/drop marker after a full wrap, and a thrown mesh update could be counted as a successful unchanged return. The final source rejects stale slot writes, advances drop markers monotonically, and records failed mesh updates separately from changed/unchanged returns. It also emits `PublicationCommitted` only after `Database.TryCommitPreparedPublication` succeeds, carrying database epoch, sequence, frame and topology/content/lookup generations plus input frame ID. `IdentityPublished` remains an earlier provisional identity-delivery event; successful reuse is recorded after the mapping and identity refresh completes. The integrated shared-output and isolated Release Editor builds each passed with zero warnings and errors. These correctness edits were made **after** the six-run overhead matrix, so that matrix does not validate the final exact binary's overhead.

The final isolated Release Vulkan session used the same 393-draw fixture. One recent 4,096-event trace page started after a completed ring wrap (latest sequence 999,614, capacity 65,536) and advanced contiguously without page drops or busy drops. Its event 995,183 delivered an identity for process-local command 38; event 995,186 then recorded an accepted publication on the same thread: epoch 2, sequence 389, frame generation 416, topology generation 393, content generation 7,948, lookup generation 389. This directly distinguishes delivery from acceptance in that sampled publication. The session's mesh-update failure counter remained zero while changed and unchanged calls accumulated. This page demonstrates the corrected live path, not exhaustive concurrency proof or cross-process primitive identity.

## OpenGL comparison and mutation

The same observer-enabled binary, fixture, camera, Advanced pipeline, CpuDirect strategy, TSR size, and 393 canonical submissions ran under OpenGL. A stationary 94.330-second interval produced 1,097,712 identity notifications, zero other dirty notifications, 555,786 queue additions, 546,084 swap callbacks, and 545,977 unchanged mesh updates. Callback body time totaled 80,660.304 ms, including 79,088.913 ms in registration; lock wait was 22.453 ms, and owner-thread allocations were 965,369,144 bytes. State-class and transparency metadata were written on each call; draw metadata was not. The sampled command `687` showed the same identity-notification → queue → callback/ack cycle, but its process-local ID does not prove it denotes the same source primitive as Vulkan's `687`.

The OpenGL HUD showed `render<-collect` around 81–94 ms with the observer on. It still showed about 93 ms with the same instrumented binary and telemetry off, with 393 canonical submissions and 112 active viewport commands. Thus the historical near-empty OpenGL dirty queue and 2.6–3.3 ms wait were **not reproduced** in this current fixture/source state; a first differing backend event cannot honestly be named from these runs. A matched stable source/primitive map, accepted collection membership, and a controlled causal change remain necessary before classifying the historical divergence. The present trace instead shows identity feedback on both backends.

An on-demand MCP identity manifest now copies one selected published package's retained submission rows, aligned imported-source labels, views and pass membership while the collection's rendering-buffer read lock protects its publication lease. It retains no package or source objects. With the same isolated binary and settled fixture, both Vulkan and OpenGL exports were complete: 393 resident rows, 393 unique imported entity identities, one view, 14 passes, 112 package commands and 109 mesh commands each. Joining by `(ImportedEntityIdentity, PrimitiveIndex)` gave 393 unique shared pairs with no backend-only pair and no difference in pass, instance count, flags, state class, compatibility/temporal reason, source name/label, legacy command index or source order. The 108 pass members mappable to resident submissions matched exactly; four members in each run had no resident submission row. The package carried a full accepted publication tuple in each run. Raw manifests and `reports/identity-manifest-comparison.json` preserve the comparison.

All 393 imported identities have `ImportedEntityIdentityIsStable=false` because the OBJ cooker derives them from indexed hierarchy and ordinals. The manifest deliberately leaves `FixtureKey` null: it does not retain an authoritative imported-root owner path and command ordinal. This exact set equality is therefore **fixture-specific conditional correspondence**, supported by the same import settings and duplicate-key checks, not a general persistent source ID. The matched resident rows and mapped pass membership rule out those two differences in the present runs. They do not recreate the historical first differing dirty event or prove full output parity.

An all-frame OpenGL capture with the frozen matrix executable and telemetry off confirmed this on the current fixture. The 60-second stationary window recorded 243 frames, `render<-collect` p50 92.710 ms and p95 144.403 ms, and the controlled-motion window recorded p50 87.064 ms and p95 146.258 ms. The active-pass command median was 109. Backend, fixed and motion camera positions, admission screenshot, and diagnostic-loss checks passed. The admission image was viewed: Sponza geometry is present, with severe exposure saturation. OpenGL reported no usable coarse GPU timing in this harness; retention failed, including required backlogs. The OpenGL frame-output workload hash differs from Vulkan's because the backend is part of that hash, so the canonical source mapping remains to be verified directly. This capture supports the non-reproduction of a near-empty OpenGL wait; its very low frame rate and failed retention prevent a clean backend performance comparison.

For a genuine OpenGL scene mutation, the imported `sponza` node was moved from world X=0 to X=4. The viewport changed from a blue foreground curtain to a red curtain, and returning X=0 restored the blue curtain. The before, changed, and restored PNGs were viewed under this task run's `mcp-captures/`. Exposure varied between captures; the geometry/color change and reversal support output propagation, not full visual parity.

The final isolated Release Vulkan session repeated that world X=0 → 4 → 0 move at the fixed camera. Three viewport PNGs were viewed: the foreground curtain changed blue → red → blue, with geometry moving and restoring. That establishes rendered output propagation on Vulkan as well as OpenGL for this mutation. Vulkan exposure was much more stable than the earlier OpenGL screenshots; full backend visual parity remains outside this evidence. The named session was stopped through `Manage-McpEditorSession.ps1` after capture.

## Retention failure classification

The six observer runs show actual growth in tracked live resources and descriptor-set records, including observer-off runs:

| Run | Live-resource delta | Tracked descriptor-set delta | Descriptor pools created |
| --- | ---: | ---: | ---: |
| 1 off | +4,115 | +3,920 | 6 |
| 2 on | +1,489 | +2,000 | 0 |
| 3 on | +1,819 | +2,000 | 0 |
| 4 off | +684 | +2,000 | 0 |
| 5 off | +4,571 | +4,055 | 13 |
| 6 on | +2,801 | +3,800 | 6 |

Five runs passed managed-heap, private-memory and required-backlog endpoints. Run 6's backlog failure was specifically `CodeProfilerPendingCompleted` increasing from 3 to 5. Vulkan lifetime retirement, swapchain, material, upload and shader backlogs returned to their allowed endpoints. The lifetime tracker's live-resource/set gauges include retained objects, so an empty retirement queue does not explain away growing live counts.

The compute descriptor cache is a concrete candidate: `VkRenderProgram.Compute.cs` fingerprints bindings using native handles and generations; `VulkanProgramWrapperPort.cs` retains allocated sets under schema/fingerprint keys. Existing pool capacity can accommodate new sets without creating a pool, consistent with three runs adding exactly 2,000 sets and zero pools. Other owners also register descriptors, so this is a source-based hypothesis rather than measured ownership.

The later reused-session snapshot still has 393 draws and topology generation 393, but publication sequence 11,255, content generation 279,756 and 25 content deltas in its latest publication. This establishes ongoing content publication after about 13 minutes. It has no late native-gauge pair and therefore does not establish that native resources grew for all 13 minutes.

The harness used `-NoStabilityGate` because publication identity kept advancing; its five-second quiet identity includes publication sequence and frame generation. It then applies a 1% endpoint retention tolerance. This is a readiness limitation and an unresolved retention result, not proof of either a leak or harmless warmup. The next discriminating measurement is one unchanged, deeply warmed process sampled at t0/t+60/t+120 seconds: live objects grouped by type/owner, descriptor sets by owner/pool, compute cache-key counts and pool occupancy, publication/content generations and pending retirements. A bounded plateau supports cache fill; continuing owner-specific growth after natural GPU completion supports a retained-lifetime defect. This measurement remains outstanding.

## Elevated CPU and GC capture

The user ran the prepared capture script from an elevated PowerShell on September 23 at 22:38 local. The script reused this investigation's already prepared Vulkan editor (PID 37444), positioned the fixed camera, warmed for another 25 seconds, recorded with WPR `CPU.verbose` and a separate CLR GC provider (`Microsoft-Windows-DotNETRuntime`, keyword `0x1`, level 4), saved both traces, and stopped the named session through the manager. An initial script attempt incorrectly interpreted the stale native `$LASTEXITCODE` after successful PowerShell session startup; the retry checks the returned session state and MCP readiness instead. This was a capture-script defect, not an editor startup failure.

Evidence is under `Build/_AgentValidation/20260923-183000-s13a-attribution/reports/etw/20260923-223802/`. The actual isolated Editor DLL SHA256 is `A72ED1F5E4387081B3572C567502C953E8F199F5802B2E9E38200AC0AD5C2663`, and Rendering DLL SHA256 is `597C5606E17FCAA11047D678B061694669A3427F330FF8EE20EDA6C3A6E7D5CF`; these are recorded separately from the shared-output hashes above. The pre-capture state confirms Vulkan, camera `(-20, 2, 4)`, 393 resident draws and a published canonical package. Because the reused process was already running for about 13 minutes, this is a long-running warmed case, not a fresh-launch measurement or an observer overhead pair.

Both ETL files report zero lost events and buffers. The CPU trace starts at `2026-09-24T05:38:30.7342542Z`, lasts 37.9670103 seconds, and marks WPR rundown beginning at 15.748 seconds. The GC trace starts 56.9677 ms earlier and lasts about 66 seconds because it includes WPR startup and saving. Analysis uses the conservative common window CPU-relative `[1.000, 15.000]` seconds, or GC-relative `[1.0569677, 15.0569677]`, excluding recorder startup and rundown.

In that 14-second window, 48 CLR suspension intervals overlap the window. Clipped `GCSuspendEEBegin` to `GCRestartEEEnd` totals **353.9493 ms (2.5282%)**; the fully suspended interval from `GCSuspendEEEnd` to `GCRestartEEBegin` totals 351.7573 ms. The longest overlapping suspension is 11.997 ms. The window contains 25 Gen1, 21 Gen0 and one Gen2 collection start. All suspension events pair across the full GC trace. These are process runtime suspension windows, not callback-exclusive time or per-thread CPU cost; they must be intersected with the relevant owner intervals before any subtraction. `gc-steady-summary.json` preserves the individual intervals.

Context-switch accounting in the same window reports 16,883.259 ms of scheduled CPU across the editor's threads, equivalent to 1.206 cores on average. OS thread 3256 dominates at 11,600.513 ms, or 82.861% of one core; its complementary 2,399.487 ms is off-CPU time, not yet separated into ready delay, blocking or GC overlap. The next threads are 24236 at 2,441.564 ms, 26916 at 1,012.011 ms and 28080 at 737.327 ms. This establishes substantial executing CPU work rather than inferring CPU from callback Stopwatch durations. Managed stack resolution below identifies the collection-thread path on OS thread 3256; it does not map every profiler managed-thread ID to an OS thread.

Sampled leaf-module estimates are dominated by `coreclr.dll` (7,623.622 ms), `System.Private.CoreLib` (4,235.450 ms), Runtime Rendering (1,534.523 ms) and Runtime Rendering Vulkan (1,084.365 ms). These sampled weights are approximate and differ from scheduler CPU totals. Unresolved native symbols prevent assigning the CLR module cost to a particular registration operation. The capture's `cpu-trace-fitness.txt`, `cpu-rundown.txt`, `cpu-steady-scheduling.txt` and `cpu-steady-sampled-modules.txt` retain these exports.

The CLR method rundown supplies names and JIT address ranges, allowing managed sampled-stack attribution without downloading native symbols. In the 14-second window, 16,748 of 16,908 editor samples have adjacent decoded stacks (99.05%); the range map has 110,433 entries with no conflicting names at identical start addresses and no overlapping ranges at distinct starts. The reproducible task-local `scratch/analyze-s13a-cpu.py` uses exact OS thread/timestamp equality to join adjacent stack rows after xperf stack time shifting; `cpu-managed-samples.json` retains the output. It does not reconstruct inlined frames or timestamp-dependent method lifetimes, and stack presence alone does not prove complete unwinding. Inclusive sample counts, which overlap and must not be added, are:

| Method on the sampled stack | Inclusive sample count |
| --- | ---: |
| `CollectVisibleThread` on OS thread 3256 | 11,481 |
| `TryUpdateMeshCommandCore` | 10,612 |
| `TryPopulateLogicalMeshState` | 10,409 |
| `EnsureSubmeshInAtlas` | 10,276 |
| `AppendMeshToAtlas` | 10,212 |
| `SyncLegacyDynamicAtlasState` | 10,207 |

All 10,409 registration-path samples are on OS thread 3256. Registration is present in 98.09% of the mesh-update samples. Its leaf samples include 5,875 unresolved `coreclr` leaves, 3,911 `System.SpanHelpers.ClearWithReferences` leaves, and 428 `SyncLegacyDynamicAtlasState` leaves. Unresolved native CLR leaves are not automatically GC work; the enclosing managed stack identifies their caller path.

The source explains a concrete repeated full-atlas operation: `AppendMeshToAtlas` calls `EnsureAtlasBuffers` **before** its already-packed mesh early return. `EnsureAtlasBuffers` unconditionally calls `SyncLegacyDynamicAtlasState`, which clears and recopies the dynamic atlas index list and reconstructs the mesh-offset dictionary. Thus even an already resident mesh reaches these copies on re-registration. This is a measured CPU owner beneath the earlier approximately 98% registration wall-time result, not a new rendering behavior change. S13b still owns removing publication-only dirty callbacks; S13c must remeasure any remaining registration cost before changing its retention or atlas synchronization.

The CPU profiler dump was taken after WPR saving and reports incomplete active scopes. It is retained for context but cannot join exact callback boundaries to the earlier ETW window. The ready/blocking split, GC intersection and exact callback intervals remain necessary before this evidence can close the correlated-span gate. Sampled stack coverage above 99% is not the same as S02's accounted critical-path-time gate. No frame-rate improvement is claimed from this observation-only capture.

## Open gates and next decisions

- Repeat the predeclared observer overhead gate on the final corrected binary if retention is brought within S00 limits. The six frozen-binary runs formed three pairs; all three pairs passed their positive-overhead tolerance, but off-run variability was large and every run failed retention, so they cannot close the gate.
- Measure attached-debugger behavior if available. The current Debug run had no debugger; the cold-cache Release case is recorded separately from the warmed windows and does not measure its initial 10-second readiness wait.
- Trace source/primitive and world/view/collection ownership through exact accepted publication and render preparation on both backends. The final trace proves one accepted commit after identity delivery, and published-package manifests match all 393 fixture-specific resident identities plus mapped pass membership. They still lack a general stable owner path/ordinal and a full callback-to-package/view join. Confirm the first historical backend divergence with one controlled change only if the differing event is reproduced.
- Split callback wall time into on-CPU work, GC pauses and descheduling. Account for remaining callback time, registration cache outcomes, dirty/upload bytes, operation scans, preparation, encoding and retained leases. The current aggregate supports a strong registration-cost hypothesis but does not complete S02's attribution gate.
- Recheck native-resource/descriptor/backlog retention after its owner resolves the failed endpoints. S12 must be validated or explicitly dispositioned before S13a can close. The later S13b candidate is tracked separately below and in its own gate record.

No regression tests were added or run while this live integration is under investigation, following the repository testing policy.

## September 24 closeout audit

S13a is **Blocked**, not Validated or Closed. The observation work establishes the
identity-feedback owner and a registration hot path, but the protocol's complete
attribution and acceptance gates have not passed. The later S13b behavior changes
do not retroactively validate the S13a observer binary.

Three additional Release Vulkan `ReleaseBenchmark` captures used the frozen
393-resident-draw Sponza fixture, Advanced/CpuDirect/TSR, the same stationary camera,
and workload identity `10991459253885323059`. All had one captured identity,
verified camera/admission images, lossless diagnostics, required backlogs at the
endpoint, and zero sampled failed Vulkan frames. They were separate processes;
the first two used an earlier September 24 candidate, while the third included
the sidecar/scratch lifetime repair but preceded the final lease and ledger edits.

| Warmup / stationary / motion | `render<-collect` p50 / p95 | Native live start -> end | Descriptor sets start -> end | Retention |
| --- | ---: | ---: | ---: | --- |
| 25 s / 60 s / 60 s | 2.827 / 3.409 ms | 15,370 -> 15,637 | 11,801 -> 12,071 | Failed |
| 180 s / 60 s / none | 3.014 / 3.875 ms | 14,131 -> 14,131 | 10,141 -> 10,141 | Passed once |
| 180 s / 60 s / 60 s | 3.253 / 4.478 ms | 13,588 -> 15,549 | 9,991 -> 11,946 | Failed |

The corresponding summaries are under
`Build/_AgentValidation/20260924-102959-s13-closeout/reports/` as
`final-clean-vulkan/summary.json`, `final-clean-vulkan-deepwarm/summary.json`,
and `final-post-lifetime-vulkan/summary.json`. The retention endpoint for
the first and third runs was taken **after** controlled camera motion, so
their increase cannot be labeled stationary-window accumulation. Coarse GPU
timing coverage was 0.906%, 0%, and
54.545% respectively, so these captures cannot support a comparable GPU-time
distribution. One deep-warm plateau does not explain the repeat failure or
establish a stationary leak. A later isolated live capture attributed the
motion increments to new immutable-resource fingerprints on existing mesh
descriptor owner groups. A subsequent sample found the same managed textures
published under new native image/view/sampler generations and a changed
sampler signature; see the S13b investigation. Whether those older fingerprints
are still needed requires an exact owner/command-lifetime trace and a bounded
retirement rule. The observer gate still needs matched final-binary endpoints.

The remaining S13a closure conditions are: disposition the S12 distinct-world
lifetime dependency; run the predeclared three observer pairs on the exact final
binary with retention and GPU coverage; obtain the attached-debugger condition
when available; correlate callback spans with on-CPU, GC, ready, and blocked
time; and either reproduce and causally classify the historical Vulkan/OpenGL
first differing event or record an explicit scope disposition for its current
non-reproduction. The historical 20-sample binary manifests cannot be recovered
from expired ignored outputs, so their identity remains unknown. Applicable
test clearance has not been given.

A later S13b lifetime probe traced the pending old texture generations to
shared material descriptor pins. Targeted material program-state retirement
cleared the image/view/sampler backlog in one isolated live run, while the mesh
full-key cache still retained additional local descriptor variants after a
distinct view. That candidate was measured with a temporary probe and does not
replace S13a's final probe-free observer pairs or its other open gates; see the
[S13b gate record](2026-09-23-s13b-identity-feedback.md#exact-retired-texture-owner-and-candidate-cleanup).
A later probe-free Release run kept native resources and descriptor sets flat
through a 60-second stationary interval after warmup, then retained another
1,641 native resources and 1,625 descriptor sets after a controlled view
transition. Its old-texture retirement backlog stayed zero. This failed motion
endpoint still prevents the S13a retention/observer matrix from passing; it
was one live run, not the predeclared three observer pairs.

The subsequent S13b local-payload probe found that the affected mesh variants
had identical physical dynamic-uniform-buffer writes despite different captured
texture signatures. The narrowed allocation identity eliminated 918 duplicate
payload variants in the diagnostic comparison. A later probe-free build kept
801 mesh variants/4,005 mesh sets flat through repeated and unseen camera views;
its final stationary interval also kept native resources/total descriptor sets
flat at 11,410/7,856 with zero pending retirement. Those results supersede the
specific mesh-duplication failure, while the full observer matrix remains open.
A supported renderer restart then exposed missing backend upload registration
for an already published logical texture generation. The
[current S13b record](2026-09-23-s13b-identity-feedback.md#renderer-restart-follow-up-exposed-during-validation)
preserves that lifecycle failure and its follow-up. The correction subsequently
passed two live Vulkan restarts and A/B/A view sequences, reaching 7,891 and
6,678 completed presents with zero retirement backlog. Cumulative validation
retained two startup descriptor-heap compatibility errors on each device, with
no additional rendering/recovery errors. These scoped results do not complete
the observer matrix or other open gates. S13a remains Blocked.

## September 25 final-binary closeout (evening session)

Evidence root: `Build/_AgentValidation/20260925-195625-s13-final-closeout/`.
The final binary is the isolated session `s13-final-0925i` (DLL hashes in the
[S13b record](2026-09-23-s13b-identity-feedback.md#final-s13b-matrix-on-the-fixed-binary)),
built from `7ab827983` plus the working-tree lifetime fixes described there.

### Historical backend divergence: dispositioned Not Reproducible

The September 23 near-empty OpenGL dirty queue was never reproduced on any
binary whose identity is known (the September 23 instrumented OpenGL run already
showed identity feedback on both backends), and the historical binaries and
manifests are unrecoverable. On the final binary the controlled comparison ran
the same session executable, fixture, camera, strategy and telemetry under both
backends: the 60-second stationary windows produced zero identity-only dirty
notifications, zero other dirty notifications and zero swap callbacks on Vulkan
and on OpenGL, and the add/remove/re-add, visibility, cube material, shared
material and view-transition mutations propagated on both with matched
publication behavior (S13b record, "OpenGL representative subset"). The
entry condition for the divergence candidate, a backend-specific dirty queue,
therefore no longer exists: the shared identity-feedback path was the cause on
both backends and S13b removed it. Reopen only if a future capture shows a
first differing dirty/publication event between backends on one binary.

### Final-binary observer and retention matrix: passed

Eight sequential Release Vulkan runs (`reports/observer-matrix/`, driver
`scratch/Run-S13aFinalObserverMatrix.ps1`) used the session `s13-final-0925i`
executable, the frozen fixture, the fixed camera `(-20, 2, 4)` looking at
`(-20, 2, -8)`, CpuDirect, TSR, `DevelopmentProfile`, one-frame sampling,
120-second warmup, a 60-second stationary window and a 60-second controlled
motion window, in the order off/on, on/off, off/on, off/on. The fourth pair was
added after the first pair's motion window exceeded the allowance, following
the protocol's rule to repeat an ambiguous result. Every run kept workload
identity `10991459253885323059`, verified both camera poses, admitted the
screenshot, lost no diagnostics, returned every required backlog to baseline,
reported 99.958-99.960% coarse GPU coverage, and ended within one native live
resource and zero descriptor sets of its start (12,071-12,090 native; 7,756 or
7,761 sets). The stationary windows had 4,771-4,997 samples each; the motion
windows 2,021-2,216.

| Pair | Order | Stationary render mean off → on (ms) | Stationary p95 / p99 off → on (ms) | Motion render mean off → on (ms) | Motion p95 / p99 off → on (ms) |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | off/on | 12.094 → 12.157 | 14.838 / 16.202 → 15.026 / 16.490 | 28.038 → 29.419 | 53.257 / 63.192 → 58.219 / 68.147 |
| 2 | on/off | 12.072 → 12.066 | 15.034 / 16.787 → 15.157 / 17.680 | 28.284 → 28.005 | 55.220 / 60.014 → 53.661 / 57.774 |
| 3 | off/on | 11.723 → 11.908 | 14.685 / 15.582 → 14.836 / 15.902 | 26.950 → 27.259 | 51.951 / 57.447 → 51.746 / 56.516 |
| 4 | off/on | 11.988 → 11.760 | 14.938 / 16.249 → 14.756 / 16.011 | 27.529 → 27.419 | 52.984 / 58.789 → 52.286 / 56.040 |

Against the predeclared allowance (observer-on render mean at most 0.5 ms above
the paired off run; p95/p99 within the larger of 5% or the off-run spread over
median), all four stationary pairs pass (mean deltas +0.063, -0.006, +0.185,
-0.228 ms; p95 at most +1.27%; p99 at most +5.32% against a 7.4% allowance) and
motion pairs two, three and four pass (mean -0.279, +0.309, -0.110 ms; p95 and
p99 negative). Only pair one's motion window failed (+1.381 ms mean, +9.32% p95),
with pair one's off run being the first process of the matrix; three later pairs
with negative or sub-allowance deltas show the excess was run-order variability,
not a repeatable observer cost. Render-collect waits were 0.520-0.592 ms p50 and
0.702-0.818 ms p95 stationary, 0.684-0.775 / 0.930-1.055 ms in motion, in every
run; their paired differences are at most 0.115 ms, which exceeds a percentage
allowance only because the base is sub-millisecond. The harness's managed-heap
endpoint fails in every run (-216 MB to +536 MB deltas), but the retained
per-frame streams show a Gen2 sawtooth whose post-collection floor is 2.30-2.34
GB in every cycle of each run, so it is sampling phase, not retention; native
resources, descriptor sets and required backlogs are the retention evidence.

Compared with the September 23 frozen-binary matrix on the same fixture, the
stationary render mean fell from 16.3-26.5 ms to 11.7-12.2 ms and the
render-collect wait p50 from about 80 ms to 0.55 ms; the motion window mean
fell from 32-39 ms to 27-29 ms. The 18-25 ms GPU workload owner is unchanged
and remains the frame-time floor in motion.

### OpenGL harness comparison on the same executable

`scratch/Run-OpenGLComparison.ps1` ran the identical harness configuration
under OpenGL with telemetry off (`reports/opengl-comparison/s13a-opengl-off/`):
workload identity `8881944379212414834` (the backend is part of the hash), both
camera poses verified, admission image captured, zero diagnostic loss, 664
stationary and 477 motion samples. Render-collect wait was 0.081 ms p50 /
0.227 ms p95 stationary and 0.137 / 0.332 ms in motion, against 92.7 ms p50 on
September 23. OpenGL render time stayed at the fixture's known low rate
(91.8 ms stationary mean, 121 ms motion mean); the harness reports no coarse GPU
timing for OpenGL, and the only backlog that did not return was the profiler's
pending-completed count (3 to 7), the same profiler-internal endpoint that
failed run six on September 23. The OpenGL wait is therefore no longer a
divergence: both backends share the sub-millisecond wait once identity feedback
is gone.

### Attached-debugger window: measured

`scratch/Run-DebuggerWindow.ps1` restarted `s13-final-0925i` without rebuilding,
sampled 60 seconds detached, attached the Visual Studio 2022 managed debugger
through automation (`Attach-VsDebugger.ps1`, debugger mode 3 = run mode reported
against the editor PID), sampled 60 seconds attached, detached, and sampled 60
seconds again (`reports/debugger-window/`). Samples are MCP profiler snapshots
at two per second (115-116 distinct frames per window).

| Window | Presents in 60 s | Render-collect wait p50 / p95 / p99 (ms) | Whole frame p50 / p95 (ms) | Present interval p50 / p95 (ms) |
| --- | ---: | ---: | ---: | ---: |
| Detached | 5,185 | 0.530 / 0.710 / 0.754 | 10.70 / 13.52 | 11.27 / 14.03 |
| Attached | 4,919 | 0.574 / 0.857 / 1.075 | 11.16 / 14.09 | 11.84 / 14.63 |
| Detached again | 5,240 | 0.531 / 0.787 / 0.914 | 10.67 / 13.73 | 11.26 / 14.26 |

The attached managed debugger costs about 0.5 ms per frame (5% fewer presents)
on this Release binary and leaves the collect wait sub-millisecond. This is the
Release attached condition; the historical Debug attached configuration that
produced the 153-165 ms report is still unreproduced, and its non-reproduction
now rests on the removal of the identity-feedback owner rather than on a
matching historical binary.

### Callback attribution and elevated capture

The 393 identity-driven `TryUpdateMeshCommand` callbacks that the September 23
elevated CPU/GC capture attributed to `SyncLegacyDynamicAtlasState` no longer
occur: every stationary window on the final binary records zero swap callbacks
and zero mesh updates, so there is no residual callback wall time to split into
on-CPU, GC and scheduling shares. A new elevated WPR capture cannot be taken from
this agent session (not elevated; no interactive UAC prompt), so
`scratch/Run-S13aElevatedCapture.ps1` is prepared for the operator to run from an
elevated Windows PowerShell against a Ready named session; it validates tools,
session and the 393-draw fixture, brackets a 15-second `CPU.verbose` plus CLR GC
recording with S13a telemetry and profiler snapshots, and writes under
`reports/etw/`. Until that runs, the attribution gate is satisfied by the
removal of the measured owner, not by a new elevated trace, and this is recorded
as such rather than as a pass of the original capture. The September 23
analysis scripts are re-prepared in this run's `scratch/` as
`analyze_etw_cpu.py` and `analyze_etw_gc.py` (both take `--pid`, export the GC
CSV with xperf when missing, and report the process share of machine samples);
they are ready for the `s13-final-0925i` process once `reports/etw/` exists.
The session stayed Ready on the unchanged final binary while the later
September 26 emulated-stereo and push-descriptor work was built and validated
in separate isolated sessions (`logs/probe-build-window.log` records those
build windows; no capture had started during any of them).

### September 26 elevated CPU and GC capture (operator-run)

The operator ran `scratch/Run-S13aElevatedCapture.ps1 -SessionName s13-final-0925i`
from an elevated Windows PowerShell at 09:36 local against the still-running
final-binary session (PID 29204; `XREngine.Editor.dll`
`FFB22642866867A62E6B3846E238C40347C556B94A27E7AAD965C1C74870DA57`,
`XREngine.Runtime.Rendering.dll` `62F2861AC1040824D04DD8E1BCB80D4B569B1B60B44A8FE8E126C51D4568B34A`,
`XREngine.Runtime.Rendering.Vulkan.dll` `3D346658C5F547FA1EC4FC1824697CEE37EAE1C96EA48CA673727AB8D1F5B272`,
recorded in `reports/etw/20260926-093600/binary-hashes.json`). Two script
defects surfaced first and were fixed in place: `$PSScriptRoot` is empty inside
`param()` defaults under Windows PowerShell 5.1 (run root now resolved in the
body), and the fixture check sampled the package state at one instant although
it cycles `Published`/`Prepared`/`Empty` every frame (now ten samples, either
settled state accepted). The camera stayed at view A with the 393-draw
fixture; the script warmed for 25 seconds and recorded 15 seconds of WPR
`CPU.verbose` plus a CLR GC provider session.

Both traces report zero lost events and buffers (`cpu-tracestats.txt`,
`gc-summary.json` metadata). The CPU trace starts at `2026-09-26T16:36:26.8040120Z`
and lasts 39.73 seconds including rundown; the GC trace starts 71.142 ms
earlier and lasts 59.5 seconds. Analysis again uses the conservative
CPU-relative window `[1.000, 15.000]` seconds, or GC-relative
`[1.071142, 15.071142]`.

**Telemetry bracket** (`telemetry-before/after.json`, about 70 seconds and
4,929 frames including warm-up): zero identity-only dirty notifications, zero
other dirty notifications, zero queue adds, zero swap callbacks, zero mesh
updates; 4,930 publications reused; native resources flat at 12,467 and
descriptor sets flat at 7,751 with zero pending retirement. The profiler
snapshots show whole-frame p50 12.27 ms and p99 16.79 ms with
render-wait-for-collect 0.72 ms (`profiler-stats-after.json`).

**GC** (`gc-steady-summary.json`, 14-second window): 230 suspension
intervals (143 Gen0 and 87 Gen1 starts, no Gen2) totalling 558.732 ms from
`GCSuspendEEBegin` to `GCRestartEEEnd` (3.991%), fully suspended 548.169 ms,
longest 3.635 ms; all events pair across the whole trace. Compared with
September 23 (48 intervals, 353.9 ms, 2.528%, longest 11.997 ms) the process
now collects more often but pauses much shorter, consistent with the removal of
the per-frame registration churn and its large temporary arrays; the remaining
Gen0/Gen1 cadence is a separate allocation owner, not a callback cost.

**CPU** (`cpu-managed-samples.json`, `scratch/analyze_etw_cpu.py --pid 29204`):
19,267 editor samples in the window (43.25% of all machine samples), 19,068
with adjacent decoded stacks (98.97%), 116,873 managed address ranges. The
render thread (OS thread 11744) holds 12,891 samples, entirely under
`VulkanFrameLoop.Render` (11,347 inclusive): `RecordPrimary` 8,207 and
`TryPrepareAdvancedVisibilityFamily` 4,577 dominate, with
`VulkanFrameLoop.DriveDesktopPresentNowReadiness` 2,266. The collect-visible
thread (14144) holds 3,698 samples, of which `XRViewport.CollectVisible` 2,552,
`VisualScene3D.CollectRenderedItems` 1,928 and `Lights3DCollection.CollectVisibleItems`
2,219 (overlapping inclusive counts). The September 23 owners
`TryUpdateMeshCommand`, `ResolveLogicalMeshRegistration` and
`SyncLegacyDynamicAtlasState` have **zero** samples in the window (the
analysis still lists them as targets); `SwapBuffers` carries 679 inclusive
samples and `RenderCommandCollection` methods 493. The largest managed leaves
are `VkMeshRenderer.ComputePassMetadataHash` (457), `FrameOperationStream.TryPrepareReadOnlyStorage`
(322), `RenderableMesh.BeforeAdd` (316) and `FrameOperationStream.TryPrepareMaterialTables`
(253); 5,402 leaves are unresolved `coreclr` frames and 3,215 samples include
`System.Buffer.BulkMoveWithWriteBarrier`, which locate the remaining CPU cost in
Vulkan primary recording and Advanced visibility family preparation rather than
in publication callbacks. This closes the elevated-capture item: the measured
callback owner is absent from a fresh elevated trace of the final binary, and
the residual profile is recorded for S13c-S13g rather than attributed here.

The September 24 [remaining closeout work](../../todo/rendering/vulkan-stall-remediation-todo.md#september-24-handoff-remaining-closeout-work)
is the consolidated resumption checklist. Later automated screenshot/log
collection is awaiting review and does not satisfy the final-binary observer
gate. The world snapshot attempt failed before restoration; its missing
exception diagnostics must be repaired before that route can support the S12
distinct-world investigation. The current session is stopped, with no waiver or
regression-test clearance granted.

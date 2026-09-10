# Vulkan 1.4 phase D: wait placement and dependency scopes

Status: D1–D3 complete. D4's early-visibility experiment evaluated and deferred;
original barrier restored. Actual GPU-gap validation and other specializations
remain deferred. No runtime speedup is claimed.

## Scope and acceptance

This follows the [phase C measurements](vulkan14-phase-c-baselines-2026-09-09.md)
and implements phase D of the [modernization plan](../../todo/rendering/vulkan-14-performance-and-shader-modernization-todo.md).
Preserve generic barrier semantics, image layouts, queue ownership, slot lifetime,
and desktop/XR scheduling. Retain a performance change only after correctness
validation and three matched runs demonstrate a total-cost improvement of at
least 5% above baseline variation without a greater than 5% tail/latency
regression. Incomplete or noisy comparisons are deferred, not claimed as wins.

Local evidence root: `Build/_AgentValidation/20260909-164308-vulkan14-d/`.
Named editor session: `vulkan14-phase-d-0909`. No physical headset is required;
use the existing Monado workflow for XR validation if scheduling changes warrant it.

## D1: collection and publication ownership

The existing comment and plan overstate what the pre-collection wait blocks.
`EngineTimer.RunCollectVisibleIteration` runs `DispatchCollectVisible` and marks
collection complete **before** waiting on `_renderDone`. CPU visibility
preparation therefore already overlaps the preceding render. The wait gates
`ProcessCollectVisibleSwapJobs`, `DispatchSwapBuffers`, and the final visibility
generation publication. Those operations replace the render-side snapshot.

The first post-wait operation capable of touching published renderer state is
`ProcessCollectVisibleSwapJobs`: arbitrary queued swap-affinity jobs run with
`Engine.IsFrameSwapThread` set. The normal scene callback then reaches
`VisualScene.GlobalSwapBuffers` and `GPUScene.SwapCommandBuffers`, which copies
dirty scene streams into render sources, resizes buffers, publishes meshlet
generations and the advanced resident scene, and commits dirty byte ranges.
This is not an independently movable CPU-only preparation block.

The Vulkan render lane separately proves GPU completion in
`PrepareDesktopFrameSlot` before releasing slot lifetimes, resetting
`FrameDataArena`, and resetting completed transient pools. The pre-wait's exact
timeline value is recorded per slot; it only substitutes for the later wait
when that value still matches. Interactive resize and active OpenXR skip the
pre-wait and use their existing nonblocking admission checks. Multiwindow
collection release is owned by the timer after the whole dispatch.

The existing `WaitFrameSlot` export combines current-slot and next-slot waits.
Phase D now exports `WaitCurrentFrameSlot` and
`WaitNextFrameSlotBeforeCollect` separately to NDJSON and MCP, preserving the
aggregate. Historical phase C's approximately 0.013 ms aggregate median is an
upper bound on the pre-wait, not a direct measurement of it. The 13–17 ms
`collect_wait_for_render_ms` interval is render backpressure and cannot be
claimed as a recoverable GPU slot stall.

### Measured decision

Telemetry-only Release control build: zero warnings/errors. Both production
captures used the unchanged phase C default fixture, 25 s warmup, 60 s capture,
separate warmed caches, diagnostics off, and one fresh process per backend.
These are direct cost measurements, not a candidate-versus-control speedup claim.

| Backend | Accepted samples | Pre-publication wait p50 / p95 / p99 / max ms | Current-slot wait | Whole frame p50 ms |
| --- | ---: | --- | --- | ---: |
| Descriptor heap | 395 | 0.013 / 0.015 / 0.02218 / 0.059 | 0 throughout | 14.530 |
| Descriptor indexing | 341 | 0.014 / 0.016 / 0.01960 / 0.033 | 0 throughout | 16.946 |

Both captures passed the harness and retained-sample validation. Exact reports
are under `scratch/control-static/reports/baseline-distributions.json` in the
evidence root. The pre-wait's median cost is below 0.1% of the full frame; even
its observed maximum cannot explain a 5% total-frame improvement. There is no
additional CPU-collection overlap to recover before the existing gate.

**D1/D2 decision: retain existing wait placement.** No wait relocation is
justified by these measurements. The conditional D2 implementation is therefore
not applied. Slot publication, interactive resize, multiwindow and OpenXR
ownership behavior are unchanged. A different GPU-bound workload would require
new direct measurements and a separately proven publication split.

## D3/D4 candidate and reachability

Only `Advanced.Visibility.EarlyToIndirect` was experimentally specialized.
The tested candidate used a stack `MemoryBarrier2` through the tracked
sync2 gateway: compute shader write to compute shader read/write. The immediate
consumer builds indirect commands in a compute dispatch; draw-indirect and
later shader consumption still use the existing raster-read and graph barriers.
Generic mask resolution, image layouts, queue ownership, native image shading,
and query dependencies are unchanged.

The compute scope matches the [Khronos synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html)
for compute storage dependencies. It was removed after the comparison below.
A future implementation must also preserve Advanced's admitted legacy-barrier
capability path by selecting the corresponding tracked legacy compute barrier
when synchronization2 is not enabled; the experimental helper did not cover
that branch. Restoring the original generic call removes this compatibility gap.

The default phase C fixture does not execute Advanced boundaries. The benchmark
wrapper now explicitly records the selected render pipeline and required
Advanced mode in each invocation's environment. An otherwise identical fixture
with `AdvancedRenderPipeline` is used for this candidate.

The unchanged control rejects Advanced + descriptor heap with the explicit
blocker: "Advanced-scene descriptor-heap realization is explicitly unsupported;
descriptor indexing is the portable implementation." Its frame operations stay
empty and output is deferred; zero validation errors in that state are not a
rendering pass. This attempt is excluded, with diagnostics retained in
`reports/advanced-control-diagnostics.json`. The subsequent Advanced comparison
explicitly selects descriptor indexing; there is no automatic backend fallback.

### Producer/consumer map

The inventory below covers all six direct generic-mask emissions in the
Advanced primary recorder. Default-pipeline `MemoryBarrierOp` and pending-mask
calls retain their generic API contract; there is no inferred narrowing based
on an operation being adjacent to a compute dispatch.

| Boundary | Producer and immediate/later consumers | Dependencies retained and decision |
| --- | --- | --- |
| Early visibility → build indirect (`Primary.Operations.cs`) | `EarlyVisibility.comp` writes counters, visible/deferred indices and persistent state. `BuildVisibilityIndirect.comp` reads early counts/visible indices and writes ranges, arguments and counters. | Tested compute write → compute read/write, after all per-view early dispatches and before all indirect-building dispatches. `EmitAdvancedVisibilityRasterReadBarrier` separately covers compute/transfer writes → draw-indirect plus vertex/fragment/mesh shader consumption. Graph buffer edges remain. Promotion deferred; original generic barrier restored. |
| Late visibility tail (`Primary.Operations.cs`) | `LateVisibility.comp` reads deferred indices, counters, persistent state and ranges; writes late visible indices/arguments/payloads, range counts, persistent state and counters. Later consumers include late raster and optional diagnostic copies. | Keep generic scope. Diagnostic copies have shader/transfer-write → transfer-read buffer edges; late raster has its own raster-read dependency. Removal additionally needs proof of disjoint multiview subranges and all late graph edges. Deferred. |
| GTAO tail (`Primary.NativeShading.cs`) | GTAO writes AO. Independent work classification does not read AO; native opaque shading later samples it. | Keep scope. A possible future deletion/move must prove the graph and `TransitionNativeInput(AO)` supply the General/write → sampled-read transition at the actual consumer. Merely narrowing an immediate compute barrier still blocks independent classification. Deferred. |
| Background → native opaque shade | Background writes HDR, velocity, reactive and shading diagnostics; shade writes those same images. | Proven compute WAW candidate, compute write → compute write, with no layout change. Deferred as a separate measured experiment. |
| Indirect shade → overflow repair | Both branches write the same four images, but normal and repair writes are mutually exclusive under stable overflow flags. | Conservative compute WAW specialization is possible. Deletion requires normal and forced-overflow validation of the shader predicate and flag stability. Deferred. |
| Native opaque tail | HDR can become an authored-background color attachment; velocity/reactive later become attachments or sampled inputs, and diagnostics can be copied. | Keep scope. Cross-stage/layout graph edges must be captured for both skybox-disabled and authored-background paths before narrowing. Deferred. |

Relevant sources are `Commands/CommandBuffers/Recording/Primary/` under the
Vulkan backend, `Build/CommonAssets/Shaders/Advanced/` early/late visibility and native shading
programs, and `VPRC_AdvancedRenderStage` resource declarations. Native work
classification already has exact compute write → compute read dependencies for
kernel counts/classification counters and compute write → draw-indirect read
for dispatch arguments. Those are unchanged. `FillNativeCounters` has a
lifetime-sensitive broad source before transfer clearing; it is not a candidate.

Another possible later experiment is to move/batch the four froxel buffer
dependencies immediately before Shade, alongside the Background WAW boundary.
Background does not consume froxel/light/decal lists, so the existing placement
unnecessarily orders it after BuildFroxels. That is a separate scheduling change
and is not combined with the first scope-only comparison.

### Physical recording and measurement admission

RenderDoc 1.44 passed `rdc doctor`; an isolated Advanced control capture timed out
after 120 seconds without an `.rdc`. Its launcher terminated only its own child.
No replay/gap result is inferred from that failure.

The first Advanced production probe demonstrated why default-pipeline counters
cannot be reused blindly: `effective_strategy=GpuIndirectZeroReadback`, the
actual output manifest named `AdvancedRenderPipeline`, and renderer-wide indirect
API recording calls were 512, consistent with the Advanced stable-bin path,
while unused generic occlusion telemetry still said `CpuDirect`
and generic GPU-scene command count was zero. This is a telemetry ownership
distinction, not a fallback.

Both measured builds now publish the process-wide count of native calls at the
exact early-visibility barrier site. It counts recording, including discarded
recordings, and is explicitly not a GPU completion receipt. The Advanced
retained-sample validator also requires an actual rendered Advanced desktop
scene with commands, resolved zero-readback strategy, corroborating indirect
recording or primary reuse, completed output, stable workload identity, and the normal
backend/production-profile/correctness checks. Generic material-table stability
checks do not apply to Advanced's independently owned native table; this cohort
uses the fixed warmup followed by these actual-path checks. The old probe lacks
the exact counter and is excluded from the comparison.

The counter and renderer-wide indirect/reuse metrics prove recording reach and
corroborate the completed Advanced output. They do not identify the contents of
each submitted/reused command buffer. Exact per-submission proof would require
carrying native recording evidence with each bank lease to queue acceptance;
that broader telemetry change is outside this scope-only experiment.

The timed comparison uses three fresh processes per variant, alternating order
control/candidate, candidate/control, control/candidate. Each build has an
isolated seeded cache. Warmup is 25 s and capture is 60 s at 1920×1080 with
Immediate presentation, diagnostics and MCP disabled. The control retains the
generic barrier; both builds contain identical recording-counter instrumentation.
Exact binary hashes and environment values are saved with every invocation.

## D4 measured result and disposition

All six production captures passed retained-sample validation: 4,310 sampled
frames, all `Completed`, with the exact barrier recording counter positive and
the expected rendered Advanced output. Cache seeds are excluded from the table.
Values below are retained-sample percentiles; they can differ slightly from the
harness's summary rounding and GPU-ready filtering.

| Variant/run | Samples | Whole-frame p50 / p95 / p99 ms | Completed GPU command-buffer p50 / p95 / p99 ms |
| --- | ---: | --- | --- |
| Control 1 | 714 | 7.664 / 8.382 / 27.287 | 4.750 / 6.423 / 7.894 |
| Candidate 1 | 717 | 7.675 / 8.446 / 28.132 | 4.735 / 6.377 / 7.935 |
| Candidate 2 | 738 | 7.374 / 8.517 / 25.456 | 4.843 / 6.390 / 7.056 |
| Control 2 | 716 | 7.618 / 8.992 / 22.537 | 4.906 / 6.642 / 7.607 |
| Control 3 | 694 | 7.829 / 9.128 / 22.804 | 5.244 / 7.135 / 8.556 |
| Candidate 3 | 731 | 7.438 / 8.577 / 26.004 | 4.970 / 6.773 / 7.974 |

Median-of-run-medians whole-frame cost changes from 7.664 to 7.438 ms (-2.95%);
GPU command-buffer cost changes from 4.9055 to 4.843 ms (-1.27%). Neither meets
the predeclared 5% threshold. Control GPU median spread is 10.07%, exceeding the
7.5% noise limit and dwarfing the apparent GPU gain. Whole-frame p99 medians
change from 22.804 to 26.004 ms (+14.03%); tail variation prevents attributing a
regression to the barrier but also prevents a tail-safe promotion claim.

Command-buffer recording allocation medians remain 654,272 bytes in both
variants. This existing allocation cost is not reduced by the candidate.
Generic GPU-submission allocation counters are zero for this native path and
are not evidence of allocation-free rendering. Detailed disabled Vulkan CPU
allocation probes remain unavailable. CPU and GPU scopes are not summed.

**Decision: defer specialization and restore the original generic call.** The
caller-specific helper was removed. The final retained changes are split wait
telemetry, the cumulative recording-reach counter, Advanced benchmark pipeline
selection/admission checks, the corrected ownership comment, and these findings.
The native-shading candidates in the map remain separate deferred experiments.
No desktop/XR scheduling or barrier semantics change remains.

Raw distributions are in `scratch/advanced-control/reports/` and
`scratch/advanced-candidate/reports/`; the paired calculation is
`reports/paired-comparison.json`. Per-invocation hashes identify the measured
binaries. These ignored artifacts are disposable; the durable numbers and
decision are recorded here.

### GPU scopes and gap limitations

After production sampling ended, both frozen builds ran separately in the named
editor session with sync validation and `XRE_GPU_TIMESTAMP_DENSE=1`. Session-only
`Debug.EnableRenderStatisticsTracking` and
`Debug.EnableGpuRenderPipelineProfiling` enabled the existing GPU profiler.
Both reported completed frames and zero Vulkan validation errors; both saved
screenshots were viewed and showed matching red-lit box grids.

The control dump contains 201 retained frames / 1,809 timer samples and a
covered-scope aggregate p50 of 2.263 ms; the candidate contains 193 / 1,737 and
2.487 ms. These are intrusive, partial-scope diagnostics, not whole-pipeline
performance comparisons. The reported render-thread-minus-covered-scopes mean
is 13.180 versus 13.126 ms; this mixes CPU/present time with incomplete GPU
coverage and **is not a GPU idle-gap measurement**.

Early visibility, its barrier and indirect generation have debug labels but no
GPU profiler scopes. Covered late-visibility/native-shading scopes write start
at top-of-pipe and end at bottom-of-pipe and retain durations only. They can
include surrounding synchronization/drain and cannot reconstruct a GPU idle
timeline. Dense queries also change recording behavior and have a bounded query
budget. The earlier RenderDoc attempt produced no capture. Actual GPU-gap proof
therefore remains unavailable, and D4 stays open rather than claiming overlap.

Dump files are `logs/control-profiler-gpu-pipeline-advanced-2026-09-09-17-22-44-841-a7ddaebc.log`
and `logs/candidate-profiler-gpu-pipeline-advanced-2026-09-09-17-23-40-063-0bc6fbd4.log`.
Viewed captures end in `172244_896_c1f03ae610184f2482f073ca84042b63.png` and
`172340_119_5517d8b46b7149708a053b0e4ce5c59f.png` in `mcp-captures/`.

### Reproduction

Build the desired Release editor first, reserve an agent-validation task root,
then use distinct child roots for control and candidate binaries:

```powershell
pwsh Tools/Benchmarks/Measure-Vulkan14Baseline.ps1 `
  -EditorExecutablePath <release-editor.exe> -RunRoot <task-root>/scratch/advanced-control `
  -Bindings DescriptorIndexing -RenderPipeline AdvancedRenderPipeline `
  -Workloads Static -Repetitions 3
python Tools/Benchmarks/Summarize-Vulkan14Baseline.py <task-root>/scratch/advanced-control
```

For paired experiments alternate fresh processes using `-Repetitions 1`,
`-FirstRepetition N`, and `-SkipCacheSeed` after each variant's first run.
Keep both builds' instrumentation identical and run synchronization/visual
diagnostics separately. The summarizer keeps Advanced and Default cohorts in
separate comparison groups. Automatic Moving setup currently times out before
this Advanced fixture is ready; exclude such captures until that fixture is fixed.

## Review availability

The independent broker ownership review requested and reported
`gpt-5.6-sol`, but failed before producing evidence because its API account had
no credits. Run `4c14a63a0a0547d0ac162ea4f316ef96` is terminal; no model
substitution or retry was used. Local inspection supplies the ownership
analysis above. A separate native worker reviews GPU barrier hazards.

## Validation

- Telemetry control, measured generic control and measured candidate Release
  builds passed with zero warnings/errors. The final restored-source Release
  editor build also passed with zero warnings/errors in 23.77 seconds
  (`logs/final-build.log`).
- Direct wait captures passed on both descriptor backends. Six Advanced
  descriptor-indexing production captures passed, with no sampled failed,
  rejected or deferred frames.
- Separate sync-validation sessions exercised material edits and a direct edit
  of the actual active camera transform. Viewed screenshots changed with both.
  The automatic Moving fixture failed its startup deadline and is excluded;
  it is not counted as motion validation. Initial Advanced heap admission failed
  explicitly as described above and is also excluded.
- Dense GPU diagnostic control/candidate captures both completed with zero
  validation errors and matching viewed output. No GPU-gap/overlap claim follows
  from their partial timestamp coverage.
- The named session's retained logs contain no `VUID-`, `Validation Error` or
  `ERROR_DEVICE_LOST` entries. Updated summarization revalidated all six Advanced
  captures and both Default wait captures; PowerShell parsing and `git diff
  --check` passed.
- No new tests were added. Feature validation preceded test changes under the
  repository policy. The named editor session was stopped after each run;
  no unrelated editor process was stopped. User confirmation is not yet recorded.

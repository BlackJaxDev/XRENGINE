# Vulkan 1.4 Phase E: command reuse and descriptor cost

Date: 2026-09-09. Status: E1 and E3–E5 complete. The
[later background-output implementation](vulkan14-background-replay-and-queue-overlap-2026-09-09.md)
demonstrated 69 native reuses in 225 frames with zero Vulkan errors. E2 remains
open for full-output resize freshness. The output-policy limitation described
in the original experiment below is historical.

## Scope and acceptance

Complete E1–E5 in the [modernization TODO](../../todo/rendering/vulkan-14-performance-and-shader-modernization-todo.md).
Preserve explicit heap/indexing selection, output admission, native-resource
lifetime proof, and existing A–D behavior. Cache keys must compare complete
identities; hashes alone cannot authorize reuse. No feature tests are added or
modified before user clearance under the repository testing policy.

## Baseline and measurement plan

The frozen instrumentation-only Release build passed with zero warnings/errors
in 52.01 seconds. It contains native sampler/resource heap bind, push, successful
descriptor-write, and payload-object allocation counters, with no E reuse/bind
optimization. NDJSON counters are process cumulative; interval differences include
discarded recordings and must not be presented as GPU completion receipts.

The initial screening matrix compares `Allowed` against `ForceRecording` for
both requested descriptor backends across Static, Moving, MaterialEdits,
Streaming, VolatileUi, Resize, and ColdStart. Each cohort uses 20 seconds warmup,
25 seconds capture, and one repetition. This screens behavior and cost; one
repetition is not sufficient for a noise-sensitive promotion decision. Warm
pipelines are seeded separately by backend. Cold caches are separate for every
backend, policy, and repetition.

Allowed enables primary and command-chain reuse. ForceRecording disables primary
reuse and forces command-chain rerecording while retaining command chains and
parallel scheduling. Actual option and forced-record telemetry are validated.
Neither policy changes the requested output strategy or permits fallback.

Run root: `Build/_AgentValidation/20260909-173240-vulkan14-e/`.
The control executable is under `temp-build/control-artifacts/`; candidate builds
must use another artifacts root. The screening output is under
`scratch/reuse-control/`. Builds and other GPU workloads are serialized outside
timed captures.

## Implementation findings

- E2: heap packets need an immutable pushed-data identity, exact root constants,
  program/layout identity, exact heap storage generations/ranges, and referenced
  resource generations in addition to the normal recorded-packet proof. Mutable
  worker scratch cannot be retained by a command chain.
- E3: compute payload objects and dword backing arrays already grow and reuse;
  descriptor publication already compares exact contents after its hash lookup.
  These paths require measured confirmation rather than a speculative second
  cache. The instrumented static heap control creates approximately five payload
  objects per rendered frame: a changing mesh resource fingerprint creates a new
  descriptor allocation with five frame slots. The candidate keeps the mesh's
  allocation owner stable and refreshes slot contents. An exact per-renderer
  owner token prevents distinct meshes from sharing mutable heap staging through
  the global allocation lookup. Conventional descriptor sets retain their
  immutable resource variants. Resource-generation snapshots also retain their
  immutable arrays when the finalized sequence is unchanged.
- E4: the same heaps are currently rebound for every program/UI push. The change
  records exact binding identity in command-buffer state, pins expected native
  generations on every use, and skips only a complete equal binding. New
  recording, conventional descriptor state, and secondary execution invalidate
  state. Inherited secondaries reject invalidated or replaced heaps; Vulkan
  forbids repairing an inherited heap by rebinding it in the secondary.
- E5: compare native activity, reuse rejections, recording allocations and CPU
  preparation/refresh/record/submit cost alongside whole-frame and completed GPU
  timing. A lower native-call count alone is not a backend policy decision.

## Validation ledger

- Instrumentation-only control Release build: passed, zero warnings/errors.
- Benchmark PowerShell syntax and Python summarizer compilation/aggregation:
  passed before capture.
- Initial control matrix: finished. Strict validation accepts 13 of 14 Allowed
  cohorts and only the two VolatileUi forced cohorts. Heap streaming rejects
  frames; ten other forced cohorts still reuse cached schedules. One indexing
  forced streaming cohort also reports 8,192 readback bytes. Rejected captures
  are not timing comparisons.
- Candidate Release build: passed, zero warnings/errors (38.67 seconds).
- Candidate static heap diagnostic: completed frames, zero validation messages,
  no payload-object growth from frame 1,220 through frame 7,346, and approximately
  one sampler/resource heap bind pair per frame. Viewed captures before and after
  moving the active play camera show the expected red-box fixture at different
  poses. Moving the bootstrap editor camera alone did not affect play output;
  validation uses the active `Player1_Pawn` transform.
- Candidate streaming diagnostic: reproduced heap capacity exhaustion. The
  retained frame terminal result reports offset 130,304, size 928, capacity
  131,072 in global material texture-array publication. Synchronization validation
  reports zero messages because preparation rejects before invalid submission.
  A capture requested after permanent rejection timed out; it is not visual
  acceptance evidence.
- The initial E2 probe used `GpuIndirectInstrumented` plus
  `XRE_FORCE_CPU_INDIRECT_BUILD=1`, but source and eligibility counters showed
  that material-scatter routing bypassed the requested CPU producer. Temporarily
  honoring that diagnostic setting produced no visible commands and a black
  capture. That routing experiment was reverted. These runs do not validate
  producer-complete reuse; a separate static indirect fixture is required.
- Final comparison: 32/36 candidate captures accepted, including all 18 heap
  captures; retain descriptor indexing by default and explicit heap selection.
  No general FPS improvement or default promotion is established.

The initial static control records 528 sampler heap binds, 528 resource heap
binds, and 528 heap pushes per rendered frame span. Native writes mostly
deduplicate after warmup. These are process-counter differences, not GPU timing
or completed-command counts.

The control matrix exposed a benchmark defect: cached command-chain schedules
can bypass `VulkanCommandChainBenchmarkForceRerecord` and report reused chains
even when the option is enabled. The candidate bypasses cached schedule replay
under that flag while retaining schedule-container pooling; indirect secondary
reuse also respects the flag. The summarizer now rejects forced-policy captures
that report reused command chains or primary buffers. Original forced control
captures are therefore not evidence of the cost of actually forcing recording.
Accepted candidate allowed/forced captures establish the comparison below.

Live scheduling diagnostics found a second measurement defect: the fresh
planner returned its counts to a desktop caller that discarded them, while
cached schedule replay published counts internally. The OpenXR caller published
again, double-counting cached schedules there. Scheduling now publishes at its
owner for both fresh and cached decisions. MCP/NDJSON also expose a cumulative
scheduling-attempt counter and the latest decision independently of optional
frame statistics. These are planning classifications; physical secondary
execution still requires the native execute counters.

Indirect preflight also ran before a render pass began but compared operations
against the active render-scope scheduling identity. A pass without a graph
barrier can leave that identity unset. Contiguous-run admission now compares
against the first immutable operation's context, retaining target/pass and
producer-completion checks. First-packet contract rejections are counted even
when they prevent entry to the secondary executor. Native indirect-artifact
record/reuse process counters distinguish a prepared key from an accepted
artifact without relying on optional frame statistics.

The schedule validator previously treated every static source group as a
scheduled secondary. Small mesh runs and a suffix beyond the scheduling budget
legitimately remain inline. Validation now checks the actual ordered, disjoint
source ranges against their exact pass/target and query brackets. Explicit
dynamic groups still stay inline. The corrected paced probe completed through
frame 2,173 with zero Vulkan messages; its current-frame indirect work remained
correctly ineligible for producer-complete reuse.

Stable heap allocation storage also required slot-specific fast-path proof.
Refreshing one frame slot must not publish a new resource fingerprint for every
other slot. Fast admission now checks each requested slot's exact resource
fingerprint and material owner generation. A global allocation fingerprint is
insufficient when old and new native buffer generations coexist.

Review found a second allocation-key fallback after the fast path that still
trusted the allocation-wide fingerprint. Final lookup, captured-resource
activation, and fast activation now all call `DescriptorAllocationSlotsMatch`.
The helper verifies the selected slot, or all slots when no frame is supplied.
The final build for this correction passed with zero warnings/errors.

Global material-texture publication now uses a table-owned sparse heap arena.
It writes only slot zero and the exact sealed material closure, tracks native
image-view/sampler generations, and grows into a fresh range at powers of two.
Recorded consumers retain material-slot leases; recycled slots cannot be
rewritten until those leases end. Unpublished growth rolls back both heap
reservations on failure. Published ranges remain reserved even if a later
payload update fails. Existing fixed heap capacity is unchanged.

The arena streaming run completed through frame 4,150 with zero Vulkan messages.
Payload objects stayed at 250 from frame 692 through 4,150. Sampler/resource
writes rose only with actual streaming changes, rather than copying an entire
table prefix each time. Two viewed captures show the streamed RIVE image and
red-box scene from different active camera poses. The later volatile screen-UI
run completed 12,109 frames with zero Vulkan messages; viewed text changed from
"Profile workload A" to "Profile workload B" and payload objects stayed at
1,537 between frames 10,114 and 12,109. These diagnostic runs establish behavior,
not a matched timing comparison.

The ignored static indirect fixture uploads one complete indexed-quad command
once and waits for the existing producer-completion capability to admit it. It
uses explicit indirect draw-state scopes and performs no readback or readiness
bypass. A viewed magenta quad and native counters proved producer-complete
secondary recording. At frame 2,109 the run had 2,087 fresh indirect recordings,
zero reuses and zero Vulkan messages; heap payload objects stayed at 5,297.
This exposed another old dead cache path: its dedicated chain had never
received a dependency signature, so the prepared key inherited an incomplete
default base packet. Exact prepared native-state construction is required before
E2 can be accepted.

The dedicated indirect chain now builds a fresh exact packet from its prepared
native target, pipeline/layout generations, ordered mesh buffer bindings,
indirect/count ranges, descriptor ownership, frame-data generation, and exact
viewport/scissor and draw scalars. It no longer inherits an absent generic
schedule packet. Heap proof includes immutable payload owner/content generation,
layout and root bytes, plus exact native heap storage. Publication checks that
secondary inheritance matches the captured heaps, and the encoder consumes the
captured indirect/count handles with expected-generation lifetime tracking.
Changed prepared state rejects the recording before publishing an artifact.

This exposed a separate policy limit: **every current output constructor requires
fresh recording**, including desktop, explicit output, OpenXR eyes and prewarm.
The apparent background mirror branch is unreachable under its earlier
foreground-output admission check. A complete key cannot authorize reuse under
these contracts. E2 remains open until a supported output path intentionally
permits artifact reuse and actual cached execution is validated there. Adding a
new background output transaction is follow-up architecture, not a diagnostic
bypass or a prerequisite for E3/E4. There is no calendar-based deferral.

Normal fresh-only recording skips the unnecessary key comparison. Explicit
`XRE_VULKAN_COMMAND_CHAIN_VALIDATE=1` evaluates it without changing policy. The
fixture reached frame 8,102 with 2,625 complete keys, 2,612 matching previous keys,
2,625 policy rejections, zero reuses and zero Vulkan messages. The viewed quad
changed from magenta to cyan after a material edit; a root toggle added a further
key mismatch and steady matching resumed. These observations validate key
construction and invalidation, **not native artifact reuse**. Final Release
encoding checks built with zero warnings/errors in 9.98 seconds.

The final build was exercised again through frame 1,026: 753 complete keys, 750
matches, 753 policy rejections, zero reuses, and zero Vulkan messages. The final
mutated cyan quad was captured and viewed. The named editor was stopped before
starting the performance matrix; no validation fixture or diagnostic flag is
present in the production timing workloads.

Independent read-only final review found no remaining consequential blocker in
this heap-key publication path: viewport/scissor arrays are sealed, indirect
commands consume the captured buffers, expected-generation tracking closes the
retirement race, inheritance matches the same heaps, and failures destroy the
secondary before publishing its proof. This bounded review does not replace
the missing native-reuse validation or cover unrelated binding-artifact caches.

Matched Monado validation used the prior A/B scene, explicit `CpuDirect` mesh
submission, and true single-pass stereo. Heap submitted 133 frames and indexing
140; both retained 120 frames, reported zero Vulkan errors, and completed
1440×900 → 1920×1080 desktop resizing. This validates descriptor and stereo
state, not the production GPU-indirect XR path. The separate heavier sequential
GPU-indirect cohort acquired/released eye images but submitted zero projection
layers and is not accepted. The existing Monado service was left untouched;
simulated pose control was unavailable. The broad allocation audit also reports
73 formatted-logging candidates in existing OpenXR paths; these are a separate
review item, not a runtime allocation measurement. It was not repeated for each
backend after that report.

## First production screen

Strict retained-sample validation accepted 26/28 candidate captures. All 14 heap
captures passed, including both streaming policies. The two indexing exclusions
are detailed below. The corrected forced-recording cohorts contain no cached
primary, mesh-chain, or indirect-secondary reuse decisions. Control forced
captures that bypassed the force flag remain unusable for that comparison.

The retained heap activity changed as follows. Values are differences of process
counters divided by the rendered frame-ID span; discarded recordings can
contribute, so these are not asserted GPU-executed calls per completed frame.

| Allowed workload | Control bind pairs / span | Candidate bind pairs / span | Control payload objects / span | Candidate payload objects / span | Recording bytes p50, control → candidate |
|---|---:|---:|---:|---:|---:|
| Static | 528 | 1 | 5.009 | 0 | 370,296 → 302,984 |
| Moving | 568.656 | 1 | 5.011 | 0 | 515,872 → 510,752 |
| MaterialEdits | 528 | 0.997 | 5.019 | 0.004 | 370,288 → 303,864 |
| VolatileUi | 529 | 2 | 30.012 | 0 | 661,408 → 413,944 |
| Resize | 422.397 | 0.627 | 4.252 | 0.155 | 370,288 → 303,864 |
| ColdStart retained window | 528 | 1 | 5.010 | 0 | 370,296 → 303,680 |
| Streaming | invalid | 1 | invalid | 0 | invalid → 401,736 |

Static, moving, UI, and retained cold-start windows made no native sampler or
resource descriptor writes after warmup. Streaming wrote only changed descriptors
(0.029 sampler/resource writes per frame-ID span) with no payload-object growth.
Static heap pushes remained 528/span and UI pushes 530/span: the native bind
reduction did not remove the program-root pushes. Resize and material edits may
legitimately introduce new storage; their small nonzero growth is recorded.

These allocation/native-call improvements do not establish an end-to-end speedup.
The first candidate static CPU/GPU p50 was 17.140/3.304 ms against the earlier
control's 14.599/2.881 ms. Indexing also slowed in that comparison, while moving
and resize results varied. This motivated the interleaved frozen-control/candidate
static repeats below instead of attributing the gap or changing the default.

## Interleaved confirmation and final policy

The follow-up alternated the frozen instrumentation-only control and final
candidate at the same static workload, then reversed build and backend order.
It retained the 20-second warmup, 25-second capture, explicit requested backend,
and seeded caches. The settings, harness, runner and streaming-asset hashes
match the first control/candidate screen; executable/assembly hashes and cache
roots are preserved in each invocation. No build or second task-launched GPU
workload overlapped a capture. GPU boost remained unmanaged.

| Backend / pair | Control CPU / GPU p50 ms | Candidate CPU / GPU p50 ms | CPU change | GPU change | CPU p99 change |
|---|---:|---:|---:|---:|---:|
| Heap / 2, control first | 16.532 / 3.361 | 17.056 / 3.545 | +3.17% | +5.48% | −34.03% |
| Heap / 3, candidate first | 15.565 / 3.063 | 15.355 / 3.170 | −1.35% | +3.48% | −38.94% |
| Indexing / 2, control first | 19.692 / 3.096 | 19.981 / 2.845 | +1.47% | −8.11% | −0.69% |
| Indexing / 3, candidate first | 19.442 / 2.783 | 19.026 / 2.820 | −2.14% | +1.35% | +1.95% |

Across all three static runs, control/candidate GPU median spreads were
15.66%/11.35% for heaps and 15.47%/20.88% for indexing. These exceed the 7.5%
noise ceiling used for promotion. Heap CPU tails improved in both interleaved
pairs, but median CPU changes reversed sign and heap GPU medians were higher.
The samples do not establish a general speedup, a causal GPU regression, or
that reduced GC pressure caused the observed tail improvement.

The repeated mutation matrix passed forced MaterialEdits and allowed Streaming,
but rejected one frame in allowed MaterialEdits and forced Streaming. Four of
36 candidate captures are therefore excluded, with 3,987 retained samples in
32 accepted captures. All 18 heap captures passed. The control has 19/32 valid
non-seed captures and 2,712 accepted samples; its invalid forced-policy and heap
streaming captures remain excluded. Each backend also had a separate seed run.

**Retain:** stable renderer-owned heap staging, exact frame-slot proof, sparse
leased material publication, immutable indirect keys, safe heap-bind suppression,
and corrected measurement/admission counters. Static heap recording allocation
fell about 18%, and UI recording allocation about 37%, in the first screen.

**Retain the current selection policy:** descriptor indexing by default, heaps
only when explicitly requested. Equivalent viewed desktop/UI output and scoped
Monado SPS evidence support the exercised paths. The allocation/native-call
improvements, mixed timing and indexing mutation rejections do not justify
promoting heaps or globally forcing recording. E5's comparison and policy
decision are complete; this is not a claim that every backend workload is stable.

**Open:** E2 needs a supported reuse-eligible output contract and actual native
replay validation. The indexing mutation rejections need a diagnostic terminal
cause and a live fix/accepted contract before those workloads can support a
stable backend comparison. Any later performance promotion needs matched runs
within the declared noise limit. None has a calendar-based waiting period.

Reproduce the screen with `Tools/Benchmarks/Measure-Vulkan14Baseline.ps1` using
both `Bindings`, `ReusePolicies = @('Allowed', 'ForceRecording')`, one repetition,
`WarmupSec = 20`, `CaptureSec = 25`, and `ContinueOnInvalidCohort`, then run
`Tools/Benchmarks/Summarize-Vulkan14Baseline.py <run-root>`. For confirmation,
run Static with Allowed at repetitions 2 and 3, interleaving control/candidate
and reversing order. Use separate frozen Release outputs and retain their
invocations; never rebuild a binary while it is being measured. All evidence
for this investigation remains under the task run root named above.

## Reuse-cost interpretation

The first-screen CPU stage medians explain the strongest policy contrasts:

| Backend / workload / policy | Resource preparation ms | Schedule evaluation ms | Recording ms | Submit ms |
|---|---:|---:|---:|---:|
| Heap / static / Allowed | 4.192 | 0 | 8.830 | 0.145 |
| Heap / static / Forced | 4.137 | 2.485 | 12.261 | 0.140 |
| Indexing / static / Allowed | 6.349 | 0 | 10.840 | 0.190 |
| Indexing / static / Forced | 6.272 | 2.391 | 13.411 | 0.185 |
| Heap / moving / Allowed | 9.542 | 0.959 | 12.104 | 0.168 |
| Heap / moving / Forced | 4.735 | 2.424 | 12.680 | 0.151 |

Static Allowed used cached schedules in every sampled frame; Forced rebuilt in
every sample. Avoiding schedule reconstruction accounts for a substantial part
of the static recording difference. The moving heap's apparent forced-policy
advantage instead appears in resource preparation; recording was slightly
cheaper with Allowed. Its Allowed samples split between 23 cached and 83 built
schedules, whereas indexing moving retained cached schedules in 81/90 samples.
This is a target for controlled resource/preparation investigation, not proof
that executable reuse is slow or grounds to disable caching globally.

Stage medians are not additive, and parent scopes can include child scopes.
Legacy frame-op signature, reusable-frame refresh, and prepared-indirect key
timers report zero on this production route because those paths are not entered;
fine-grained probes and allocation detail also remain disabled. Do not interpret
these fields as proof that all preparation or validation costs zero. The active
resource-preparation, schedule, recording, submission and whole-frame intervals,
allocation counts, decision histograms and native counters bound this comparison.
The dedicated producer-complete fixture provides the separate E2 key evidence.

## Follow-up lifetime findings

The first candidate screen also excluded two indexing captures: forced
MaterialEdits and allowed Streaming each contained one rejected retained frame.
The material-edit rejection was render frame 280 at
`2026-09-10T02:27:52.0092454Z`, about 0.61 seconds after capture began. Resource
preparation was reached, but command recording, acquisition, and submission
were not. The production trace does not retain a detailed terminal cause, so
this is not attributed to heap changes or to a particular lifetime race. The
original failures remain excluded and retained even if repeat captures pass.

The diagnostic DrawMetadataBuffer rejection is separate from stable heap
allocation storage. `VulkanComputeBufferBinding` currently stores a captured raw
native handle without its native generation. The direct indirect producer seals
that snapshot without going through the persistent binding-artifact cache. A
buffer recreation between capture and preparation can therefore present a
retired handle; strict heap publication rejects it. Closing this gap requires
captured native generations, expected-generation validation at consumption, and
an explicit frame replan when the sealed producer epoch is superseded. Rebinding
the old snapshot to a new ambient buffer would be incorrect.

The persistent binding-artifact cache has a separate ownership gap: an engine
or publisher generation can admit additional ordinary buffer bindings whose
native lifetimes it does not own. A containment change can reject those
artifacts until ordinary buffer generations and leases participate in their
proof. It cannot fix the direct indirect-snapshot path above. These findings
remain visible follow-ups; no lifetime check was weakened to make E pass.

The separate [D4 barrier research](vulkan14-barrier-performance-research-2026-09-09.md)
explains why D4 was deferred and sets conditions for its next experiment. E is
independent of the unavailable NVIDIA performance-counter trace.

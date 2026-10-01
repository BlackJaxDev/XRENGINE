# Continuous-camera CPU stall attribution

Status: subscription-refresh, program-invalidation and compact-uniform allocation corrections validated; broader stall attribution remains open, October 1, 2026.

The [cumulative validation](2026-10-01-cumulative-publication-validation.md)
found long render-dispatch intervals with Vulkan validation disabled. Repeated
half-second eased camera commands also authored periodic acceleration and gaps.
This investigation replaces those commands with one uninterrupted 60-second focus
move per window and isolates CPU observer overhead before selecting a code fix.
The focus move still eases at its overall endpoints; it is not a constant-speed
trajectory or a displayed-motion acceptance test.

## Comparison contract

Use the same stopped-session Release binary and desktop Sponza fixture as the
corrected cumulative run. Preserve Advanced/CpuDirect, TSR 0.67, output size,
editor UI, coarse GPU tracking, publication telemetry and validation-off state.
Run fresh processes in on/off/off/on order. Apply CPU-profiler preferences as
explicit session-only overrides; disabling capture environment variables alone
does not override a persisted CPU-profiler preference.

The treatment disables every-frame NDJSON capture, automatic dump and CPU scope
profiling/UDP transport together. Follow a dominant result with an independent
toggle if needed to separate those mechanisms. Warm each process for at least
45 seconds and require 100 completed presents. Capture scene/effective-state
evidence outside each measurement window. Issue one 60-second camera command,
then make no MCP calls until the window ends.

Both variants use the same external EventPipe observer: GC allocation ticks,
GC suspension/restart, managed contention and sampled thread stacks. Record its
overhead limitation explicitly. Allocation ticks are sampled estimates, and
sampled managed thread time is not exclusive CPU execution or a native Windows
scheduling trace. CPU-observer-off endpoint progress does not establish all-frame
tail percentiles or displayed presentation cadence.

Evidence stays under
`Build/_AgentValidation/20261001-140000-cumulative/`: `reports/cpu-stalls`,
`traces`, `mcp-captures/cpu-stalls`, and scratch-only trace tools/analyzer. No new
test or engine instrumentation is part of the initial experiment.

## Source candidates and correlation rules

- `ProfileCapture.RecordRenderStatsSnapshot` formats roughly 1,700 fields and
  flushes a buffer synchronously from the render thread before Vulkan begins.
  Formatting allocations and file-write blocking require trace attribution.
- `CodeProfiler.BuildFrameSnapshot` recursively allocates immutable node trees,
  child arrays and history arrays. Process-wide GC can suspend the renderer even
  when the allocating producer is the profiler's background thread.
- `ThreadProducerBuffer.TryGrow` can wait for the resize lock held by `DrainTo`.
  Zero discarded/overflow events does not rule out producer blocking.
- Main-thread tasks and window pre/post-render work remain alternative sources;
  existing phase timing scopes should be used before adding instrumentation.

Capture rows sample the previous completed dispatch during the next frame's
begin-frame work. In the prior worst row, `render_frame_id=11777` but
`completed_frame_id` and Vulkan/output IDs are 11776. Correlate to the completed
frame, not merely the row label. The 1,887.757 ms difference between outer dispatch
and recorded Vulkan time is elapsed time outside Vulkan, not exclusive CPU time.
The adjacent GC-counter delta does not align precisely enough for subtraction.

## Initial attribution and observer limits

Four allocation/stack windows completed in on/off/off/on order with no lost
EventPipe events. The disabled variants contain no sampled `ProfileCapture` or
`CodeProfiler` allocations, while large engine allocations remain. In the second
disabled run, sampled allocations total 8.194 GB: about 2.833 GB falls under
`ComputeDispatchSnapshot.CreateSealedCopy`; the other allocations include
geometry-layout signature construction (1.227 GB), material-option refresh
(958 MB), material refresh (889 MB), vertex-input preparation (655 MB) and mesh
refresh (437 MB). These are sampled stack-attribution estimates, not precise
per-object byte counters or exclusive CPU costs.

The external stack sampler also generates runtime suspensions. Separate
`SuspendForGC`/`SuspendForGCPrep` from `SuspendOther`; do not label every runtime
suspension a GC pause. A subsequent GC-only disabled-observer minute recorded
680 GC-related suspension intervals totaling 5,895.49 ms, maximum 23.99 ms, with
zero lost events and no stack samples. The half-second pauses in intrusive
allocation/stack traces therefore do not establish ordinary-run maxima.

The first GC-only observer-on window overlapped host-side trace conversion and
is excluded from comparison. The clean control is repeated separately. No trace
decoding, build or screenshot is allowed inside the replacement timed windows.

The clean observer-on control repeat recorded 672 GC-related suspension
intervals totaling 7,218.40 ms, maximum 533.78 ms, without external stack samples
or lost events. The rebuilt observer-off allocation control recorded 694
intervals totaling 5,625.39 ms, maximum 14.92 ms. These are single diagnostic
windows with different external event sets; they demonstrate why intrusive
observer configurations need separate treatment, not a universal overhead ratio.

## Targeted subscription refresh correction

After the user requested removal of heavy hot-path allocations,
`RenderCommandMesh3D` was changed to retain three scratch sets and one scratch
mesh-event dictionary under its existing subscription gate. Refresh calls,
membership comparisons, event subscription order and retained `DataChanged`
identities are unchanged. Each scratch collection is cleared in `finally`,
retaining capacity but releasing removed owner references. This does not skip
refreshes simply because a renderer reference is unchanged.

The current refresh bodies do not invoke synchronous callbacks: property events
are field-like, `DataChanged` is a field, and listener operations enqueue changes.
The existing gate therefore protects scratch reuse without introducing a new
deferred-refresh or pooling protocol. Initial command construction/capacity growth
and genuine new mesh-event creation remain distinct from warmed refresh reuse.

Other rendering source files have concurrent edits. The control and candidate
are rebuilt from the same source inputs, substituting the saved original
`RenderCommandMesh3D` source only for the control. For the first candidate, only the isolated
editor's Rendering assembly was replaced; source and binary manifests verify the boundary.
The control Release build passed with zero warnings/errors. An initial candidate
incremental build reused the control DLL; hash and field admission caught it
before any candidate window. A forced candidate rebuild passed with zero
warnings/errors, contains the new fields and changes only the Rendering DLL in
the isolated editor.

The first candidate reduced sampled process allocation from 8.702 GB to 6.704 GB
in comparable 60-second allocation windows (1,482 versus 1,479 bracketed
presents). Exclusive first-engine-frame attribution across the three refresh
methods fell from 2.416 GB to 489.6 MB. These are diagnostic samples rather than
an acceptance benchmark. Remaining samples identified boxed `HashSet`
enumerators through `UnionWith` and boxed submesh-list enumerators.

A refinement replaces `UnionWith` with concrete enumeration and adds
`EventList.CopySnapshot(ref T[])`. The helper holds the list's read lock across
count, capacity growth and copying. Each refresh owns a retained snapshot array,
cleared after consumption. It continues to read the live list: the subscription
count dictionary is not an equivalent source because list index replacement,
suppressed notifications and range-add callbacks can leave it stale or partial.
The thread-safe case preserves atomic snapshot consistency. Non-thread-safe
lists retain their existing requirement for externally synchronized mutation;
the new traversal copies membership instead of using a live fail-fast enumerator.
No tests were added or modified.

For the refined comparison, Data was rebuilt once with the saved original
EventList source and once with the new helper. These were combined with the
original and refined Rendering assemblies respectively. Both candidate Release builds passed
with zero warnings/errors. Source checks show only the owned command changed
among Rendering control inputs; binary checks show only Data and Rendering
changed in the isolated editor. Vulkan and Host remain fixed. The additive Data
helper is the only behavior change between its control and candidate sources.
The original Data control build also passed with zero warnings/errors.

The first candidate completed 20 root-transform mutations and scene
activation/deactivation. Resident draws changed from 393 to zero and back to 393;
captured and viewed images moved, disappeared and restored. These checks cover
scene registration and transform continuity, not same-command subscription
replacement, duplicate submeshes or `RenderOptions` replacement. Existing dark
regions in the scene remain; images do not establish visual correctness.

The first candidate GC-only observer-off run still recorded a 637.64 ms
GC-related suspension (502 intervals, 5,559.32 ms total, zero lost events).
Therefore long pauses also occur with engine CPU observers and external stack
sampling disabled. Reduced allocation has not established smoother presentation.
## Refined candidate result

The matched control and refined candidate both completed their 60-second
allocation windows with CPU observers off and zero lost EventPipe events.
All three refreshed subscription methods have zero AllocationTick samples,
including inclusive stack attribution, in the warmed candidate window.

| Sampled allocation | Original control | Refined candidate |
|---|---:|---:|
| Process total | 8.053 GB | 6.346 GB |
| Completed presents across endpoint snapshots | 1,395 | 1,519 |
| Bytes per bracketed present | 5.773 MB | 4.178 MB |
| Material refresh, exclusive first engine frame | 841.5 MB | 0 samples |
| Material-options refresh, exclusive first engine frame | 954.6 MB | 0 samples |
| Mesh refresh, exclusive first engine frame | 442.8 MB | 0 samples |

Sampled allocation per bracketed present decreased approximately 28%. The present
counter bracket includes endpoint diagnostic work and slightly exceeds the exact
allocation window; these ratios are approximate. Zero samples does not prove
zero bytes for every possible mutation. Warmed source inspection also finds no
recurring allocation in these methods with unchanged membership; capacity growth,
new subscriptions and lazy object initialization can still allocate.

| GC-related suspensions | Count | Total | Maximum |
|---|---:|---:|---:|
| Original control, allocation observer | 653 | 6,996.37 ms | 415.44 ms |
| Refined candidate, allocation observer | 490 | 4,867.16 ms | 16.21 ms |
| Refined candidate, GC-only observer | 540 | 4,636.81 ms | 14.29 ms |

The GC-only candidate also lost zero events; allocation and CPU stack sampling
were disabled, so its zero allocation-event count is not an allocation measure.
These are single diagnostic windows, not a repeated acceptance benchmark. Earlier
controls and candidates varied widely in maximum suspension. No frame-tail,
throughput or displayed-motion pass follows from these results.

The refined candidate repeated the 20 root-transform changes and deactivation/
reactivation checks: resident draws remained 393 during movement, became zero
while inactive and restored to 393. Transform writes advanced from 0 to 7,467,
then 7,860 after restoration. All four endpoint outcomes were Completed. Start,
end, moved, inactive and restored images were captured and viewed. The original
black regions remain, and incidental hover highlighting differed between some
captures; this is not a pixel-equivalence or visual-quality gate. Same-command
subscription replacement, duplicate-submesh edits and concurrent list mutation
remain unexercised. Session logs contained no matching exception/fatal/error
entries in the inspected run logs. The named editor session was stopped.

Final evidence uses `baseline2-alloc`, `candidate2-alloc`, `candidate2-light`,
and `candidate2-alloc-mutations` under `reports/cpu-stalls`. Binary manifests
record Data candidate SHA-256
`95A5EFE5EF21A6A47569C04F5DBEE02BFE82897F7503B90E5440EEA5B022D318`
and Rendering candidate
`A1DBF8ECC2DC16845FEE886117F8AAC5B157D46B2F7CAB53042B493ADE459537`.
The retained implementation is limited to `RenderCommandMesh3D` scratch reuse
and the reusable `EventList` snapshot helper.

## Ownership remaining after subscription refresh

Snapshot copies cannot simply be marked persistent/immutable: that flag also
permits cross-frame resident-template reuse, and sealed snapshots own storage and
material-table leases. The two lowering stack paths are separate ingress routes,
not proof that every draw is copied twice. A future optimization must measure
eligible source reuse and preserve physical-stream ownership and retirement.

In the refined candidate, sealed binding copies still account for 3.059 GB
(about 2.01 MB per bracketed present), geometry-layout signature construction for
1.334 GB and vertex-input preparation for 709.8 MB. Sealed-copy allocation per
present is approximately unchanged. These were the next owners after subscription refresh. The follow-up below
removes warmed layout reconstruction and reduces uniform-copy size while
preserving snapshot ownership and retirement. The original 1.9-second outer-dispatch gap still lacks an
aligned native scheduling/file-I/O trace. No universal stall fix or displayed
smoothness pass is claimed. User confirmation of the targeted correction is absent.

## Program activation and sealed-copy follow-up

A new current-source control retains subscription-refresh reuse and rebuilds only
Vulkan. `ObserveActiveProgramLinkGeneration` unconditionally dirtied vertex input,
pipeline and descriptors on every successful activation. The retained correction
moves those invalidations into the existing real-relink/wrapper-replacement
branch. Cold activation, program switches and geometry/buffer changes retain
their invalidation paths.

Cross-frame snapshot recycling was rejected: `VkRenderProgram.ApplyBindingSnapshot`
retains its input after recording, and later binding mutations may copy from it.
Frame-data signatures also depend on snapshot identity. Frame retirement alone
cannot authorize mutating these escaped objects; reuse requires explicit
program-borrow retirement and content generations. Ordinary snapshots must not
be marked persistent artifacts merely to avoid copies.

A temporary experiment shared a fresh sealed copy among direct draws with the
same source reference, scoped separately to each Lower/Append call. Viewport and
scissor arrays still detached, lifetime ownership remained with physical streams,
and ordinary snapshots remained ineligible for cross-frame artifact reuse. It
provided no meaningful benefit and was fully reverted: 2.898 MB sampled per
completed present versus 2.872 MB for invalidation alone; sealed copies changed
only from 2.047 to 2.037 MB. No deduplication source changes are retained.

The first combined capture (`layout-dedup-alloc`) is excluded because endpoint
`get_render_state` failed with `Collection was modified` in
`BuildRenderCommandPassSummary`. There was no complete end/window record. The
scratch driver now retries only that specific read-only exception, at most three
times, outside the timed window, saving each retry. The unchanged candidate's
repeat completed without lost events. No renderer change was made for this
introspection race. Its GC maximum was 524.12 ms with allocation observation and
15.64 ms with GC-only observation. Mutation and shader-reload checks completed;
393 resident draws returned. Start/end/reload images were viewed, retaining the
existing black regions. The session stopped cleanly before removing the experiment.

Telemetry explains limited reference reuse: 393 of 396 binding fallbacks were
shadow passes with generated programs and snapshots varying per mesh. The shadow
dictionary path still consumes cascade/face matrices, caster masks, material
parameters and arbitrary callbacks. Typed shadow state does not replace that
contract, so removing the dictionaries without replacing their consumers is not
valid.

Broker reasoning was assessed useful for snapshot lifetime analysis, but the
required broker tools were unavailable. Native source review was used instead.
No tests were added or modified.

## Compact uniform storage result

The second retained correction replaces simultaneously stored numeric fields with
a reference-free 64-byte union and constructor-kind discriminator. The managed
reference remains outside the union. Compiled Release sizes are **80 bytes per
uniform value and 96 bytes per dictionary entry**, down from 200/216 bytes.
Inactive getters still return default even when the supplied shader type differs
from the constructor overload. Reference precedence, arrays and constructor
forwarding are preserved; `Value` and `TryGetVector4` bodies are unchanged.
Review found no raw-layout/native serialization or inherited struct equality/hash
consumers. Inherited `ValueType.Equals/GetHashCode` are not promised to remain
representation-stable.

All targeted Release builds passed with zero warnings/errors. Only Vulkan changes
in the isolated editor binary manifest; Data/Rendering/Host stay fixed. The final
Vulkan SHA-256 is
`3E12DDC66BEE5B6C45F9857C8F8C02AECB955BF95FEB16603401038164EE1928`.
The existing-source differences from the control manifest are only preparation
invalidation and `ProgramUniformValue`, plus new numeric-union and kind-enum
files. The ineffective deduplication is absent.

| 60-second allocation window | Completed presents | Sampled bytes / present | Sealed-copy bytes / present |
|---|---:|---:|---:|
| Current-source control | 1,509 | 4.269 MB | 2.058 MB |
| Program invalidation corrected | 1,556 | 2.872 MB | 2.047 MB |
| Invalidation plus compact uniforms | 1,654 | 2.093 MB | 1.271 MB |

Invalidation alone reduces sampled total allocation by 32.7%. Layout-signature
construction falls from 895 KB per present to zero samples, and vertex-input
preparation from 479 KB to zero. The compact-uniform increment reduces total
allocation by a further 27.1%; both retained corrections reduce it 51.0% relative
to the current-source control. Sealed-copy allocation falls 37.9% for the uniform
increment. Uniform dictionary-entry arrays fall from 1.419 to 0.688 MB per present
(51.5%). All three subscription-refresh sites and both layout/preparation sites
remain at zero allocation samples in the final window.

Allocation ticks are sampled estimates; presents use the completed-retirement
meter, and endpoint brackets slightly exceed 60 seconds. These single windows
are not a repeated throughput or frame-tail acceptance benchmark.

| GC observation | Count | Total suspension | Maximum suspension | Lost events |
|---|---:|---:|---:|---:|
| Control, allocation stacks | 518 | 5,208.68 ms | 372.73 ms | 0 |
| Invalidation only, allocation stacks | 349 | 3,865.02 ms | 390.85 ms | 0 |
| Compact uniforms, allocation stacks | 256 | 3,093.09 ms | 459.31 ms | 0 |
| Compact uniforms, GC-only | 274 | 2,402.52 ms | 23.96 ms | 0 |

GC suspension includes rendezvous/preparation, not only exclusive collection
execution. The GC-only run intentionally records no allocation ticks. Both final
candidate endpoint outcomes were Completed with zero pending retirements.
Maximum suspensions still vary substantially, so no smoothness or general stall
fix is claimed. Vulkan validation and engine CPU observers were disabled.

Twenty root-transform mutations preserved 393 resident draws; deactivation
reduced that to zero, and reactivation restored 393. Transform writes advanced
from 0 to 7,467 to 7,860, and all mutation endpoints completed. Shader reload
invalidated 122 sources and completed with 393 resident draws. Start/end, moved,
inactive, restored and reload PNGs were captured and viewed. The inactive scene
exposed the environment background; restoration recovered Sponza. Existing black
regions remain, so this does not establish visual-quality acceptance. The
inspected session diagnostic logs had no exception/fatal/error matches. The
isolated named editor stopped cleanly. No tests were added or modified.

Evidence labels are `layout-control-alloc`, `layout-root-alloc`,
`layout-dedup-retry-alloc`, `layout-dedup-retry-light`, `uniform-compact-alloc`,
`uniform-compact-light`, and the associated mutation directories. Exact windows,
source/binary manifests, size measurement and EventPipe reports remain under the
existing task evidence directory.

Remaining allocation is still substantial: sealed binding snapshots account for
about 1.271 MB per present and other paths about 0.823 MB. Leading other sampled
owners include queued mesh-request materialization (~101 KB per present),
prepared-operation cohort comparison (~71 KB) and submission-contract sealing
(~69 KB). Next work should remove recurring temporary construction in those
paths and design explicit program-borrow retirement before considering sealed
snapshot storage reuse. The unaligned outer-dispatch gap and displayed-motion
acceptance remain open.

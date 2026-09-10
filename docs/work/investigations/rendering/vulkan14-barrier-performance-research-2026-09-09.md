# Vulkan 1.4 barrier performance: D4 causal analysis and reopening criteria

Date: 2026-09-09. Target: RTX 3090, driver 610.88, native Advanced pipeline,
descriptor indexing, 1920×1080. This report distinguishes measured results,
source-proven dependencies, and hypotheses requiring a GPU scheduling trace.

## Findings and disposition

The D4 experiment did not establish a useful performance improvement. It
specialized the `EarlyVisibility → BuildIndirect` barrier, but retained the
compute dependency that dominates that boundary. The recorder places these
dispatches next to one another, with no independent graphics work between them.
Consequently, removing graphics stages from this barrier offered little known
same-frame work to overlap. It also retained a global memory barrier, so this
experiment did not isolate the benefit of restricting memory dependencies to
individual buffers.

The measured change was smaller than the acceptance threshold and smaller than
control-run drift. That supports rejecting this particular optimization for
promotion. It does **not** demonstrate that all Advanced barriers are optimal,
that the candidate caused the observed tail increase, or that synchronization
cannot be improved elsewhere.

D4 was deferred after the failed experiment, not scheduled for a later date.
Leaving the deferral without an explicit next experiment was incomplete. The
reopening criteria below replace that ambiguity. Research is reopened now;
promotion remains conditional on correctness and meaningful measurements.
Phase E can proceed independently because its changes concern command reuse,
descriptor payload ownership, and native binding overhead.

## What was measured

The retained [D investigation](vulkan14-phase-d-waits-and-barriers-2026-09-09.md)
contains the workload, command lines, exact dependency map, run percentiles,
capture admission checks, and visual synchronization validation. Six production
captures yielded 4,310 accepted samples, all with completed output. Three control
and three candidate runs were alternated. Validation layers and dense GPU queries
were disabled for timing.

| Metric, median across run percentiles | Control | Candidate | Change |
|---|---:|---:|---:|
| Whole-frame p50 | 7.664 ms | 7.438 ms | −2.95% |
| Completed GPU command-buffer p50 | 4.9055 ms | 4.843 ms | −1.27% |
| Whole-frame p99 | 22.80399 ms | 26.0042 ms | +14.03% |
| Recording allocation p50 | 654,272 bytes | 654,272 bytes | unchanged |

Control GPU run medians ranged from 4.7495 to 5.2435 ms: 10.07% spread relative
to their median. The declared noise ceiling was 7.5%, and promotion required at
least 5% end-to-end improvement without a greater than 5% tail/latency
regression. Neither the gain nor the tail evidence supported promotion. Three
run pairs are also insufficient to assign the tail increase confidently to this
one barrier change.

The 5% threshold is a project acceptance rule, not a property of Vulkan. A
smaller repeatable benefit could justify a separately proposed, lower-risk
cleanup. It cannot be relabeled a passed D4 result after seeing these numbers.
Lowering the threshold would not solve the larger problem that observed control
variation exceeds the apparent improvement.

### Clock and measurement limits

The old production runs used unmanaged GPU boost. Endpoint samples usually
reported 1,800 MHz graphics and 9,751 MHz memory. Candidate run 1 started at
1,965 MHz and 183 W; most other endpoints reported approximately 134–149 W.
Temperatures were roughly 56–62°C. These snapshots identify an uncontrolled
variable; they do not prove that clocks or temperature explain the timing drift.
There is no continuous clock/power trace for those runs.

Whole-frame CPU scheduling time, CPU recording time, and completed GPU command
buffer time measure different intervals. Summing them double-counts overlap.
Subtracting a partial set of GPU pass durations from CPU render time does not
produce a GPU-idle measurement. Khronos explicitly distinguishes asynchronous
GPU work from the CPU time spent issuing commands, and GPU timestamp placement
requires care because execution can overlap. [Khronos profiling](https://docs.vulkan.org/guide/latest/profiling.html),
[Khronos timestamp queries](https://docs.vulkan.org/samples/latest/samples/api/timestamp_queries/README.html).

A 5% reduction of the 4.9 ms GPU interval is about 0.25 ms; a 5% reduction of the
7.664 ms whole-frame interval is about 0.383 ms. The former is a useful trace
screening target, not satisfaction of the latter. CPU preparation and GPU work
can overlap, so even a real GPU saving may not shorten the frame's critical
path. The retry must demonstrate the end-to-end effect rather than transferring
a GPU percentage to the whole-frame result.

The separate dense-query experiment covered nine native shading/late-visibility
scopes, not the full GPU timeline. Early visibility and indirect generation had
debug labels but no corresponding GPU timestamp scopes. Its aggregate durations
cannot establish a barrier bubble or distinguish useful overlap from idle time.

## Why this barrier offered little overlap

`RecordAdvancedVisibilityPreparationPayload` in
`VulkanRenderer.CommandBufferRecording.Primary.Operations.cs` records all
per-view Early dispatches, then the Early-to-indirect barrier, then all per-view
BuildIndirect dispatches inside one `AdvancedVisibilityOp`.

```mermaid
flowchart LR
    A[Early visibility compute] -->|writes counters and visible indices| B[Compute memory dependency]
    B --> C[Build indirect compute]
    C -->|writes arguments and ranges| D[Raster-read dependency]
    D --> E[Early visibility raster]
```

`EarlyVisibility.comp` writes the early count and visible-index stream.
`BuildVisibilityIndirect.comp` reads those results and writes indirect arguments,
range data, and counters. Those are real read-after-write and write-after-write
hazards. The candidate still had to make those compute writes available and
visible before dependent compute accesses.

The original generic `ShaderStorage` barrier covered
`ALL_GRAPHICS | COMPUTE_SHADER` with shader-read/write access on both sides. The
candidate used a synchronization2 global `MemoryBarrier2`, with compute shader
write as its source and compute shader read/write as its destination. The
optimization removed stages and unnecessary source reads; it did not remove the
producer/consumer dependency, move work across it, or introduce a new resource
boundary.

The experiment also changed the native command encoding: the generic control
used the legacy pipeline-barrier wrapper and the candidate used the
synchronization2 wrapper. A retry must hold the encoding constant to isolate
scope changes. This confound does not create evidence of a missed promotable
gain; it limits attribution of the small observed difference.

Vulkan synchronization defines which execution and memory accesses must be
ordered. Access masks govern availability/visibility; stage masks define the
execution scopes. Restricting a mask permits additional overlap where independent
work exists. It does not require the implementation to create overlap or erase
real hazards. Dependency chains and image-layout transitions must also remain
correct. [Vulkan synchronization specification](https://docs.vulkan.org/spec/latest/chapters/synchronization.html).

There is no independent current-frame graphics command inside the recorded
Early/barrier/Build sequence. The earlier FrameBegin and Deformation stages are
markers in this path, not native work enqueued between the kernels. Temporal
Begin updates CPU state. Production stage-query operations are disabled. Later
raster work has its own dependency on the indirect results. The scope reduction
could release unrelated earlier graphics, including a preceding submission's
tail, but that opportunity has not been demonstrated in a scheduling trace.

The fixture reports 256 GPU scene commands/capacity. Runtime publication sets
the visibility payload capacity from its draw count, and the relevant kernels
use 256-thread workgroups. This suggests a small, potentially single-workgroup
mono workload. The exact Advanced publication count was not exported in the
retained report, so kernel occupancy/workgroup count remains an inference,
not a measured explanation.

The conditional nature of a barrier benefit is also visible in Khronos's
pipeline-barrier sample: a partially narrowed barrier still serialized work,
whereas placing the dependency at the actual fragment consumer enabled overlap
in its fragment-bound Mali fixture. That sample's reported improvement is not a
prediction for this NVIDIA compute chain. [Khronos pipeline-barrier sample](https://docs.vulkan.org/samples/latest/samples/performance/pipeline_barriers/README.html).

## What vendor guidance changes about the next experiment

NVIDIA recommends precise dependencies, reducing redundant barriers, and batching
compatible barriers. Its guidance also distinguishes resource-specific barriers
from cases where a single global barrier is appropriate. Thus replacing a global
barrier with several buffer barriers is an experiment, not an unconditional
optimization. [NVIDIA Vulkan guidance](https://developer.nvidia.com/blog/vulkan-dos-donts/).

Khronos includes compute-write-to-compute-read examples using a global memory
barrier. That is a valid expression of this hazard. A dependency with only
write-after-read ordering can require execution ordering without a corresponding
memory visibility operation, but the Early boundary also contains actual RAW/WAW
hazards. Applying the WAR simplification to this whole boundary would be wrong.
[Khronos synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html).

AMD's producer/consumer explanation likewise emphasizes selecting the actual
producing and consuming stages so unrelated stages may proceed. Hardware may
implement stages differently; the API's precision is an opportunity for the
driver, not a portable speedup guarantee. [AMD barrier explanation](https://gpuopen.com/learn/vulkan-barriers-explained/).

NVIDIA's synchronization guidance recommends complementary work for asynchronous
execution and measuring real overlap with a GPU timeline. Merely moving tiny,
dependent work to another queue can add overhead. Phase F's eventual queue work
should therefore use the same dependency evidence, but queue conversion is not
required to investigate unnecessary serialization on the current graphics queue.
[NVIDIA synchronization guidance](https://developer.nvidia.com/blog/advanced-api-performance-synchronization/).

## Adjacent barriers and stronger candidates

The D3 map covers six Advanced boundaries. They have different hazards and should
not be changed together in the next experiment.

| Boundary | Proven dependency / current concern | Next useful action |
|---|---|---|
| Early → BuildIndirect | Compute RAW/WAW; little known independent work | Keep the current dependency. Revisit only with trace evidence or a larger relevant fixture. |
| Late visibility tail | Visibility outputs feed raster/copy operations and multiple views | Complete range and view coverage before removing or combining barriers. |
| GTAO tail → work classification | Classification can be independent; the actual AO consumer is later native shading | Test placing the AO visibility dependency at its actual consumer, after auditing every image reader/layout transition. |
| Background → shade | Both compute paths write HDR, velocity, reactive, and diagnostic images | Restrict the known WAW dependency to its true accesses; check whether nearby froxel dependencies can be combined at shade entry. |
| Shade → overflow repair | Shared image outputs; predicates select regular versus repair work | Preserve conservative ordering until forced overflow validates all paths. |
| Opaque tail | Later graphics, post-process reads, and copies consume outputs | Retain cross-stage visibility until consumer coverage is complete. |

The stronger overlap question is often **where the dependency is placed**, not
only which flags it contains. An immediate compute-to-compute barrier after GTAO
would still order otherwise independent classification. Delaying the appropriate
resource dependency until the real consumer could release useful work, provided
all intervening uses and image layouts are accounted for.

The prior intrusive scopes put GTAO, classification, BuildFroxels, and Background
at approximately 0.191, 1.515, 0.214, and 0.022 ms. Treating those as independent
serial workloads gives an ideal overlap ceiling of about 0.427 ms (8.7% of the
4.9 ms GPU baseline). This is only a screening estimate: the scopes can include
waits, and the device may not overlap these compute kernels. Moving only the
four froxel barriers across Background would expose at most its roughly 0.022 ms,
about 0.45% of that GPU cost. The more consequential experiment needs the graph
and native producer phases separated from their shared Shade consumer; it is
not a four-line barrier relocation.

There is also a graph correctness prerequisite. Graph barriers emit at pass entry,
so the preparation pass's graph barriers precede Early; they cannot synchronize
Early's later writes with BuildIndirect inside that same operation. The raster
pass has graph barriers immediately before an explicit raster-read dependency,
but their coverage is not equivalent: early raster declarations omit RangeCounts,
and the current `IndirectBuffer` usage mapping describes draw-indirect reads even
when attached to a producer `WriteBuffer`. `AdvancedSynchronizationContract`
describes the desired conceptual states but is not consumed by the production
recorder. Those facts prevent using the graph as proof that the explicit raster
barrier is redundant.

## Concrete D4 reopening and completion criteria

The next D4 attempt should begin when one candidate has a traceable critical-path
opportunity and complete dependency coverage. It does not need to wait for Phase
E or a calendar date. Apply these gates:

1. Capture the native Advanced frame with pass labels and scheduling evidence.
   Identify the blocked consumer, the producer it actually depends on, and the
   independent work that a changed boundary could release. Include useful work
   duration and existing overlapping work, not just a barrier API count.
   For the Early experiment, look for approximately 0.25 ms of recoverable GPU
   time against the current 4.9 ms baseline before investing in another 5%-gain
   candidate. Exact view and dispatch-group counts belong in that trace evidence.
2. Select one boundary. For graph-based removal, first correct producer access
   descriptions and enumerate every consumer, view, buffer range, and image
   subresource. For a moved image dependency, verify the complete layout history.
3. Implement the candidate with synchronization2 capability handling consistent
   with runtime admission. Do not silently turn an optional feature into an
   unconditional required API call. Preserve explicit unsupported-mode failures.
4. Validate static and moving views, material changes, resize, and relevant
   overflow/multiview paths with synchronization validation and viewed captures.
   Keep diagnostic and production timing cohorts separate.
5. Run paired, alternating production captures with a stable clock policy and
   equivalent cache, workload, presentation, and actual-path metadata. Establish
   baseline variation first; collect more independent runs if it dominates the
   expected gain. Dense instrumentation must be identical on both sides when used.
6. Promote only when the declared end-to-end and tail criteria pass, supported
   by the GPU scheduling explanation. Otherwise retain the current behavior and
   record a candidate-specific negative result.

As of this investigation, Nsight Graphics 2026.2.0 is installed. Its GPU Trace
CLI launched the instrumented Advanced renderer and reached presentation, but
capture failed with `GPU Performance Counters unavailable`. The trace process
and its verified child processes exited; no GPU trace was produced. This is a
driver permission barrier, not missing capture software. NVIDIA documents the
required Windows control-panel setting or an administrator-run profiler.
[NVIDIA counter permissions](https://developer.nvidia.com/nvidia-development-tools-solutions-err_nvgpuctrperm-permission-issue-performance-counters).

Nsight GPU Trace is suitable for the missing scheduling and hardware-utilization
evidence; its consistent-clock capture option can help separate boost variation
from the experiment. That does not retroactively correct the old measurements.
[Nsight GPU Trace](https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-overview.html).

## Evidence preservation

Durable numeric results and reproduction details are in the linked D
investigation. Disposable evidence is under
`Build/_AgentValidation/20260909-164308-vulkan14-d/` and
`Build/_AgentValidation/20260909-173240-vulkan14-e/`. The E root contains the
Nsight launch arguments, stdout/stderr, verified process exit records, and the
instrumentation-only control build. The historical candidate binary was replaced
by the restored build; it must not be reused as if it still contains the tested
specialization.

The performance conclusion is bounded to the measured GPU, driver, fixture,
pipeline, and descriptor backend. Cross-vendor behavior, actual GPU idle time at
the Early boundary, and clock-induced versus workload-induced drift remain
unresolved. None is needed to justify rejecting an unproven optimization or to
begin Phase E.

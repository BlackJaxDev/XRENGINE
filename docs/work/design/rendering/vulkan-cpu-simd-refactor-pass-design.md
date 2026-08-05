# Vulkan CPU SIMD Refactor Pass Design

Last Updated: 2026-08-05

Owner: Rendering / Core Data

Status: Proposed execution design

Target Architecture: [Vulkan Render Loop Target Architecture](vulkan-render-loop-target-architecture.md)

Implementation Tracker: [Vulkan Core Hardening And Recording Code Changes TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)

Validation Tracker: [Vulkan Core Hardening And Recording Testing TODO](../../testing/rendering/vulkan-core-hardening-and-recording-testing-todo.md)

## Purpose

This pass introduces explicit SIMD only where a rendering CPU stage performs
the same arithmetic over enough independent elements to improve that stage and
the complete frame. It is not a request to vectorize the Vulkan API boundary or
to create a renderer-wide intrinsics framework.

The pass has four outcomes:

1. replace speculative or unreachable intrinsics with measured kernels;
2. establish one scalar correctness oracle and one small selection policy;
3. vectorize bulk CPU culling and masked-occlusion work over stage-native data;
   and
4. retain only implementations that improve the owning lifecycle stage and
   full-frame p95 without making the renderer harder to follow.

Although Vulkan hardening drives the work, numeric kernels that do not issue
Vulkan commands are backend-neutral. They belong in `XREngine.Data` or
`XREngine.Runtime.Rendering`, and OpenGL or future backends may call the same
code. `XREngine.Runtime.Rendering.Vulkan` selects and invokes those kernels; it
does not fork them into Vulkan-only copies.

## Decision Summary

- Keep an allocation-free scalar implementation as the executable correctness
  oracle for every retained SIMD kernel.
- Start new explicit vector implementations with portable `Vector128<T>`.
  Add `Vector256<T>` only when representative measurements show an additional
  end-to-end win. Do not add `Vector512<T>` in this pass.
- Vectorize across many objects or pixels, not across the six planes of one
  ordinary frustum when that leaves lanes idle or requires padding.
- Select a kernel once at the outer batch boundary. Never branch on hardware
  support inside the per-item loop.
- Use stage-native SoA for fields scanned independently across many elements;
  retain compact vector AoS where a consumer uses the entire vector together.
  Do not build an unconditional per-frame transpose solely for SIMD.
- Prefer bounded `Span<T>`/`ReadOnlySpan<T>` and portable vector APIs. SIMD is
  not sufficient justification for pointer APIs or type-wide `unsafe`.
- Treat SIMD and multithreading as separate optimizations. Each worker receives
  a disjoint range and private output; SIMD does not introduce shared mutation,
  locks, or per-item atomics.
- Auto selection may choose scalar for small batches. A diagnostic request for
  a specific unsupported width fails visibly and reports the missing
  capability; it never silently changes width.
- Promote on live render-stage and whole-frame evidence. A microbenchmark win
  alone is not an acceptance result.

## Current Baseline

The source tree contains useful vectorized building blocks, but it has no
coherent rendering SIMD policy:

- `PreparedFrustum` stores six plane coefficients in SoA arrays. Its current
  AVX branch requires at least eight planes, so it is unreachable for every
  valid `PreparedFrustum`; the SSE branch handles four planes and a two-plane
  scalar tail. The method is also `unsafe` only to load pinned arrays.
- `MaskedOcclusionRasterizer` still evaluates covered pixels in a scalar inner
  loop and calls `WritePixelUnchecked` for each accepted pixel. The backing
  masked-occlusion tile is exactly 8 x 4 pixels, which is a natural measured
  candidate for an eight-lane horizontal kernel.
- `MaskedOcclusionAabbTester` and other geometry code already use
  `System.Numerics` transforms. Replacing those calls with hand-written
  intrinsics is not assumed to be faster; batching multiple independent bounds
  is the more promising loop transformation.
- The CPU physics subsystem already demonstrates the useful shape of a small
  outer selector: check topology, batch count, and hardware capability once,
  then invoke a scalar or eight-instance kernel. Rendering should reuse the
  pattern, not depend on the physics implementation.
- Vulkan frame operations, graph nodes, barriers, descriptors, resource
  generations, and lifecycle state are branch-heavy ownership records. They
  are not arithmetic SIMD workloads.

The first pass therefore fixes loop shape and ownership before adding more
instructions. Existing intrinsics do not receive a presumption of value merely
because they compile to vector instructions.

## Goals

- Reduce CPU time and instruction count in measured bulk render-preparation
  stages at medium and high scene counts.
- Preserve conservative culling: a vector path may retain extra work, but it
  may never hide geometry the scalar contract considers potentially visible.
- Preserve deterministic output ordering and exact capacity accounting.
- Keep warmed per-frame execution allocation-free and bounded.
- Make active width, vector iterations, scalar tail, elements, bytes, output
  count, and owning-stage cost observable without per-item instrumentation.
- Keep the new production surface smaller than the obsolete selectors and
  helper paths it replaces.
- Retain a portable 128-bit path suitable for x64 and a future supported
  Windows Arm64 profile.

## Non-Goals

- Do not vectorize render-graph traversal, `FrameOp` ordering, barrier planning,
  descriptor lifetime checks, generation validation, frame settlement, device
  loss, WSI, OpenXR ownership, or command-buffer recording calls.
- Do not read GPU-resident visibility or scene data back to the CPU to feed a
  SIMD kernel. GPU-indirect zero-readback strategies remain GPU-owned.
- Do not replace `Span<T>.CopyTo`, `Clear`, `Fill`, sequence comparison, or
  other runtime-vectorized bulk operations with custom intrinsics without a
  measured gap.
- Do not add a generic vector math layer, ISA service locator, interface per
  width, source generator, or one source file per ISA.
- Do not hand-vectorize small occasional cascade, camera, or matrix work unless
  stage evidence later promotes it into this design.
- Do not add manual prefetching, non-temporal stores, approximate reciprocal,
  or fused operations in the initial implementation. Each requires a separate
  cache or numeric proof.

## SIMD Policy And Selection Contract

The renderer exposes one diagnostic mode shared by the accepted CPU kernels:

| Mode | Contract |
|---|---|
| `Auto` | Select the measured implementation for this kernel, hardware, and batch-count range. The selected width is reported. |
| `Scalar` | Run the scalar oracle. Used for diagnosis, parity, and low-count baselines. |
| `Vector128` | Require a hardware-accelerated 128-bit implementation for the selected kernel. Fail explicitly if unavailable. |
| `Vector256` | Require a hardware-accelerated 256-bit implementation and every operation required by the selected kernel. Fail explicitly if unavailable. |

The final enum name may follow the surrounding settings vocabulary, but there
is only one such mode. The existing `CpuSocUseAvx2` boolean is retired when
masked SOC moves to the shared policy. Public configuration does not expose
`Sse`, `Avx`, `Avx2`, or architecture names.

Each kernel owns a small, immutable capability record resolved after runtime
and CPU feature detection. `Auto` then selects once per outer invocation using
that record and benchmarked low/medium/high count thresholds. Its hot loop
contains no capability tests, mode switches, virtual calls, delegates, or
hardware-dependent exception path.

The selector checks the operations a kernel actually needs, not merely the
nominal vector width. A 256-bit float path does not imply that every required
integer mask or permutation operation is accelerated. Unsupported forced modes
produce a structured reason before work begins. `Auto` may use scalar below a
kernel's crossover threshold, but telemetry still records that decision.

## Kernel API Contract

Every accepted kernel has one owning type with scalar, 128-bit, and optional
256-bit methods kept together by responsibility. The public/internal entry
point accepts bounded spans or a small `readonly ref struct` view over related
spans and validates all lengths and output capacity before entering the loop.

The common contract is:

- zero elements return without touching input or output;
- input streams have equal logical length and may not overlap writable output;
- the vector loop reads only complete lanes;
- the scalar implementation handles the tail without padded or uninitialized
  reads;
- output order matches increasing input index;
- the method returns exact produced count and a typed status when capacity or
  input validation fails;
- no execution path allocates, rents, formats, logs, grows a collection, or
  captures a closure; and
- the caller, not the kernel, owns frame generation, worker partitioning, and
  output settlement.

Safe unaligned vector loads are the default. Input alignment or padding becomes
an API requirement only after a representative end-to-end benchmark proves it
worth the publication and storage cost. A pointer implementation may coexist
temporarily during comparison, but it is deleted if the safe form is equivalent
within the accepted noise band.

## Data Layout And Work Partitioning

### CPU culling streams

The first bulk-culling candidate consumes CPU-resident stage-native streams:

- center X, Y, and Z;
- sphere radius;
- layer mask and compact eligibility flags; and
- stable source index or range identity when output compaction needs it.

For a width `W`, one iteration evaluates `W` independent bounds against a
frustum plane. All lanes therefore perform useful work for each of the six
planes. This is preferable to evaluating one point against six planes with an
eight-lane vector.

The desired logical loop is:

```text
for each complete W-object batch
    active = layer, flag, and distance eligibility mask
    for each of six frustum planes while active is nonzero
        distance = nx * centerX + ny * centerY + nz * centerZ + d
        active &= distance >= -radius - conservativeTolerance
    emit active source indices in ascending lane order
run the scalar oracle for the remaining elements
```

The final lane mask is compacted with bit scanning into a caller-owned output
range. The implementation does not use a scatter instruction, per-lane branch
chain, or atomic append. Parallel callers partition contiguous source ranges,
write to worker-private output ranges, and merge them in deterministic range
order after completion.

The streams must already be a useful canonical representation for their CPU
consumer. If the only canonical producer has compact `Vector4` bounds consumed
as a unit by GPU upload, the pass compares direct AoS, persistent stage-native
SoA publication, and a measured AoSoA tile. It does not introduce an
unconditional per-frame AoS-to-SoA copy. Construction, dirty publication,
transpose, tail, and extra bytes all count against the kernel's result.

### Masked software occlusion tiles

The detailed conservative rasterization and tile representation remain owned by
the [Masked Software Occlusion Culling Design](masked-software-occlusion-culling-design.md).
This pass changes only the shared width policy, integration order, telemetry,
and promotion gates.

The stored tile remains 8 x 4 pixels. A 256-bit candidate evaluates eight
horizontal pixel centers at once, derives an eight-bit coverage mask, computes
reciprocal-depth candidates for covered lanes, and performs one bounded tile-row
merge instead of calling the buffer once per pixel. A portable 128-bit candidate
handles each row as two four-lane groups. The scalar tile implementation remains
the oracle.

Vectorization does not weaken masked SOC's core rule: approximation or rounding
may produce more visible geometry, never false occlusion. The pass does not
change traversal grain, stored tile dimensions, depth convention, occluder
selection, or the distinction between a traversal block and a stored tile.

### Layout ownership

Logical SoA does not mean one array wrapper, allocation, descriptor, source
file, or lifetime owner per field. Related fields remain under one scene or
occlusion schema and one generation transaction. Compatible streams may be
typed ranges in a shared frame-slot allocation.

AoSoA is considered only for a kernel that demonstrates a stable tile width and
shows a whole-stage improvement after packing and tail cost. It is not the
default representation. Worker-local output blocks align both allocation base
and stride when measurements show false sharing; padding a field alone is not a
proof of isolation.

## Numeric And Correctness Rules

Rendering SIMD is governed by the following stricter-than-average rules:

- A frustum, distance, or occlusion false negative is prohibited. Per-element
  invalid bounds, including non-finite components or negative radii, are treated
  as visible and increment a bounded diagnostic counter.
- Structural failures such as mismatched lengths, output overflow, and
  unsupported forced modes return typed failures; they do not read outside a
  span or silently discard work.
- Plane tests use the same operation order and conservative tolerance as the
  scalar oracle initially. FMA, reciprocal estimates, relaxed comparison, and
  architecture-specific reassociation remain disabled until a proof shows the
  vector result cannot become less conservative.
- Exact bitwise equality is required for packing and conversion kernels. For
  floating-point visibility kernels, scalar/vector results may differ only by
  retaining additional visible work at a documented boundary tolerance.
- Mask compaction preserves stable source order across scalar, 128-bit,
  256-bit, worker-count, and tail variations.
- No kernel reads a padded lane, depends on zeroed pool memory, or writes a
  partially valid output record.
- Scalar and vector paths consume the same immutable frame snapshot and resource
  generation. SIMD does not bypass lifecycle or ownership validation.

## Candidate Disposition

| Priority | Candidate | Initial disposition | Reason |
|---|---|---|---|
| P0 | Batched CPU sphere/frustum/distance/layer/flag culling | Implement and measure | Independent bounds provide dense lanes, simple arithmetic, stable masks, and useful SoA reuse. |
| P0 | Masked SOC 8 x 4 tile rasterization | Implement after scalar live-path validation | The stored tile naturally exposes eight horizontal pixels and the current inner loop is per pixel. |
| P1 | `PreparedFrustum` single-point containment | Refactor and remeasure; SIMD is optional | The current eight-plane AVX condition is dead for a six-plane type. Scalar, 128-bit plus tail, neutral padding, and cross-object batching must be compared. |
| P1 | Batched masked-SOC AABB projection/testing | Measure after tile rasterization | `System.Numerics` already covers individual transforms; cross-bound batching may help only at sufficient counts. |
| P2 | Bulk transform/bounds publication, float/half packing, and readback decoding | Profiler-directed only | These can be arithmetic-dense, but BCL/runtime vectorization or memory bandwidth may already dominate. |
| Reject | Graph, frame-op sorting, barriers, descriptors, command encoding, lifecycle, and device loss | Keep scalar/branch-oriented | Ownership, branches, references, native calls, and small records dominate; SIMD adds complexity without dense lanes. |
| Reject | Small cascade/camera setup and ordinary copy/fill/clear | Keep existing implementation | Work is too small or the runtime already owns vectorization. |

## Refactor Pass

### Pass 0: Establish The Evidence Baseline

1. Record scalar Release p50/p95/p99 and bytes/elements for each candidate in
   its owning lifecycle stage and in the full frame.
2. Capture low, medium, and high realistic counts for static and moving-camera
   scenes. Include an occluder-heavy masked-SOC case when that mode is enabled.
3. Record runtime/JIT, tiered-PGO state, CPU model, vector capabilities, cache
   counters when available, allocation, output count, and correctness hash.
4. Inventory current intrinsics, unsafe blocks, settings, source files, and
   callers. Mark unreachable or uncalled paths explicitly.
5. Reject candidates whose owning stage is below the measurement noise floor or
   whose arithmetic is not a material share of that stage.

Exit: every retained candidate has a scalar baseline, a named owning stage, a
representative count range, and a reason to continue.

### Pass 1: Introduce One Policy And Scalar Oracles

1. Add the shared `Auto | Scalar | Vector128 | Vector256` diagnostic contract
   in a backend-neutral rendering assembly.
2. Add one small capability/threshold selector with no service registration or
   per-kernel object allocation.
3. Make each candidate's scalar path explicit, allocation-free, bounded, and
   callable under the forced scalar mode.
4. Add aggregate counters to the existing lifecycle telemetry schema. Do not
   create a second SIMD profiler or stage taxonomy.
5. Remove the per-feature `CpuSocUseAvx2` boolean when its consumer has moved to
   the shared contract.

Exit: forced modes have deterministic behavior, scalar is the executable oracle,
and normal execution still produces identical live output before a vector path
is enabled.

### Pass 2: Batch CPU Visibility Across Objects

1. Identify the CPU-resident visibility consumer and publish only the fields it
   actually scans. Do not affect GPU-indirect zero-readback paths.
2. Implement the portable four-object kernel with `Vector128<float>` and scalar
   tail, using one outer selection branch.
3. Add the eight-object `Vector256<float>` kernel only after the 128-bit result
   passes correctness and stage measurements.
4. Fuse cheap layer, flag, distance, and frustum masks only when doing so removes
   an existing pass and does not force unrelated data into cache.
5. Compact visible indices into frame-owned or worker-owned ranges in stable
   order, with explicit capacity failure.
6. Compare scalar, 128-bit, and 256-bit paths at all representative count bands;
   set `Auto` thresholds from crossover evidence rather than a guessed constant.

Exit: the accepted path improves the owning culling stage and whole-frame p95,
allocates nothing, and produces no false negative under camera motion or
boundary stress.

### Pass 3: Repair `PreparedFrustum`

1. Remove the unreachable eight-plane AVX branch and unsafe pinning unless a
   benchmarked replacement needs them.
2. Compare the simple six-plane scalar path, portable 128-bit four-plus-two
   path, neutral eight-lane padding, and reuse of the batched-bounds kernel.
3. Prefer cross-object batching when the caller owns many tests. Do not make the
   six-plane data type permanently eight planes just to fill a vector.
4. Keep the simplest implementation within noise of the best result and delete
   the losing branches in the same change.

Exit: no dead intrinsics remain, the normal six-plane case is obvious from the
source, and any retained SIMD has live caller and end-to-end evidence.

### Pass 4: Integrate Masked SOC SIMD

1. Complete and validate the scalar masked-SOC live path first, following its
   owning design.
2. Refactor the raster inner loop around the stored 8 x 4 tile and a row coverage
   mask instead of per-pixel buffer calls.
3. Implement the portable 128-bit row-pair path, then the 256-bit eight-pixel
   path when supported and measured.
4. Measure whether eight-corner/AABB work benefits more from cross-bound batches
   than from rewriting existing `Vector4.Transform` calls.
5. Retain bounded fallback behavior in `Auto`; forced unsupported widths fail
   before occlusion work begins.

Exit: scalar and vector captures remain conservative across near-plane,
masked-edge, camera-motion, and large-bound cases, and the enabled SOC mode has
a positive target-scenario full-frame p95 result.

### Pass 5: Admit Only Profiler-Directed Secondary Kernels

Consider transform/bounds publication, half packing, upload packing, readback
decoding, or telemetry reduction only when the complete owning stage remains a
measured bottleneck after earlier passes. Use existing `System.Numerics`, BCL
span operations, or runtime numeric primitives before adding custom intrinsics.

Each new candidate requires a design-table entry, scalar oracle, data-layout
decision, count thresholds, telemetry identity, source budget, and the same
promotion gates. Otherwise it is rejected without prototype code entering the
production tree.

### Pass 6: Consolidate And Cut Over

1. Delete losing widths, obsolete selectors, dead unsafe helpers, conversion
   buffers, and benchmark-only branches immediately after a decision.
2. Keep scalar plus at most the accepted 128-bit and 256-bit implementations for
   one kernel. Do not retain every experimental permutation.
3. Record final automatic thresholds per reference hardware class and keep the
   diagnostic override available for regression isolation.
4. Update the target architecture, implementation tracker, testing tracker, and
   masked-SOC design with accepted/rejected dispositions and retained evidence.
5. Re-run source/file counts and verify this pass did not create a parallel
   renderer utility hierarchy.

Exit: production contains only measured kernels, one policy, one telemetry
interpretation, and no obsolete ISA-specific configuration.

## Telemetry And Performance Contract

SIMD counters extend `VulkanFrameTelemetry`; they do not create a separate
profiler. A stable numeric kernel ID aggregates:

- requested and selected mode;
- hardware capability or rejection reason;
- batch count and element count;
- vector iterations and scalar-tail elements;
- estimated bytes read, written, copied, or transposed;
- output/visible/rejected count;
- invalid-input and conservative-visible count; and
- owning lifecycle stage and elapsed interval when the kernel is large enough
  to justify a direct timing scope.

Tiny kernels inherit the surrounding stage timing and report counts only so
instrumentation does not dominate them. No measured thread formats kernel
names. Exporters resolve numeric IDs after frame settlement.

Acceptance compares identical Release workloads with warm JIT/tiered PGO and
observer-disabled, aggregate, and targeted runs kept distinct. At minimum the
comparison covers:

- low, medium, and high element counts;
- warm static and deterministic moving-camera frames;
- scalar, forced 128-bit, forced 256-bit, and `Auto` where supported;
- one supported Intel x64 and one supported AMD x64 machine; and
- Windows Arm64 before 128-bit SIMD is promoted as supported there.

Record instructions/cycles, branch misses, cache misses, bytes, allocation,
owning-stage p50/p95/p99, frame-root p50/p95/p99, and tail outliers when the
available profiler supports them. Microbenchmarks help establish crossover
thresholds, but promotion requires the live render path and full frame to beat
the run-to-run noise band without a low-count or tail regression.

## Source And Complexity Budget

- The shared surface is limited to one mode enum and one small policy/selector
  owner. It has no dependency injection, registration, reflection, or mutable
  global cache.
- Scalar, 128-bit, and optional 256-bit methods for one kernel stay in the same
  owning type and are not split into `Scalar`, `Sse`, `Avx`, `Avx2`, `Arm`, or
  width-named source hierarchies.
- One top-level type remains in each file as required by repository style, but a
  width is never promoted into its own strategy object merely to satisfy that
  rule.
- Backend-neutral kernels do not live under the Vulkan project, and Vulkan does
  not wrap them in forwarding-only classes.
- A new production file must own a real kernel or the single shared policy.
  Benchmark variants remain under `Build/_AgentValidation/` until accepted.
- Each cutover deletes the obsolete selector, helper, unsafe block, or duplicate
  loop it replaces. Net file/type/setting growth is part of the phase review.
- No kernel introduces a per-element object, managed reference stream, array of
  spans, iterator, LINQ query, task, or diagnostic string into a hot path.

## Validation And Promotion Gates

Runtime validation precedes new automated test work for an active integration.
After the live path is functionally sound and test work is cleared, the testing
tracker adds deterministic parity and source-contract coverage.

Every promoted kernel must prove:

- scalar and every retained hardware-supported width pass parity under zero,
  one, lane-minus-one, exact lane, lane-plus-one, representative, and maximum
  accepted counts;
- no out-of-range read/write, uninitialized tail lane, capacity truncation,
  managed allocation, or pooled-buffer escape;
- stable output order across widths and supported worker counts;
- conservative visibility at plane boundaries, near-plane intersections,
  non-finite inputs, camera cuts, moving cameras, and masked-occlusion edges;
- explicit failure for unsupported forced modes and reported selection for
  `Auto`;
- equivalent screenshots from at least two camera positions, plus suspicious
  target inspection when masked SOC changes;
- no Vulkan validation, device-loss, resize, generation, or retirement
  regression; and
- a repeatable owning-stage and full-frame p95 improvement outside the measured
  noise band, with bounded p99/worst and no material low-count loss.

A candidate that fails a promotion gate returns to the scalar/BCL form and its
experimental code is deleted. Rejection is a successful outcome when it keeps
the renderer smaller.

## Completion Definition

The SIMD refactor pass is complete when:

- every candidate in this document has a recorded accept/reject disposition;
- `PreparedFrustum` contains no unreachable width path or unproven unsafe load;
- retained rendering kernels share one width policy and scalar oracle contract;
- the accepted CPU-culling and masked-SOC paths, if any, pass the live visual,
  conservative-correctness, allocation, hardware, and full-frame gates;
- no CPU SIMD path introduces same-frame GPU readback or duplicates a
  backend-neutral kernel under Vulkan;
- source, type, setting, and unsafe-block counts are no larger than the accepted
  responsibility requires; and
- the architecture, implementation, testing, and masked-SOC documents agree
  with the final code and retained evidence.

## Research Basis

- Microsoft's [.NET SIMD guidance](https://learn.microsoft.com/en-us/dotnet/standard/simd)
  recommends beginning new vectorized algorithms with `Vector128<T>`, using
  wider vectors when measurements justify them, and accounting for memory
  bandwidth and maintenance complexity.
- Microsoft's [.NET unsafe-code guidance](https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices)
  favors idiomatic span-based operations and bounded ownership over speculative
  pointer code.
- Intel's [SIMD Made Easy with Intel ISPC](https://www.intel.com/content/dam/develop/external/us/en/documents/simd-made-easy-with-intel-ispc.pdf)
  demonstrates why field-wise loops benefit from SoA and full-lane work; it does
  not justify converting every record to SoA.
- Intel's [false-sharing analysis](https://www.intel.com/content/www/us/en/docs/vtune-profiler/cookbook/2024-2/false-sharing.html)
  supports independently aligned worker storage rather than field padding
  without an aligned allocation base.

## Related Documents

- [Vulkan Render Loop Target Architecture](vulkan-render-loop-target-architecture.md)
- [Masked Software Occlusion Culling Design](masked-software-occlusion-culling-design.md)
- [Vulkan Core Hardening And Recording Code Changes TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)
- [Vulkan Core Hardening And Recording Testing TODO](../../testing/rendering/vulkan-core-hardening-and-recording-testing-todo.md)

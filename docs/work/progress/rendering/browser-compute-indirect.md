# Browser compute and indirect commands

**Date:** 2026-09-29. **Status:** Source implementation. Builds, shader compilation,
browser/GPU execution and physical-device measurements are deferred at the user's
request. The module still reports `baselineQualified: false`.

## Command capabilities

The existing `IBrowserCommandCapability` now admits direct and indirect indexed or
non-indexed draws. Storage bindings, compute pipelines, dispatch, render and copies
use the same session-owned resource table and retained prepared-command handles.
Preparing a sequence takes references to every pipeline, binding group and draw
buffer. Release the sequence before destroying its dependencies. Replay traverses
the prepared operations without parsing JSON or creating per-draw descriptions.

Compute output can feed a later render command in the same ordered submission.
Each compute command opens a separate compute pass containing one dispatch; each
render command opens a separate render pass. This permits storage-write output to
be consumed later as vertices or indirect arguments without a CPU readback or a
desktop-style explicit barrier API. Preparation rejects incompatible resource
aliases within a usage scope; it does not reject legal transitions between passes.
The host's alias rules are deliberately conservative. Native WebGPU validation
remains authoritative for shader accesses and complete usage compatibility.

Workgroup dimensions and optional workgroup-storage metadata are checked against
the actual selected device. Shader compilation and asynchronous pipeline creation
still validate the WGSL itself; metadata is not reflection and cannot prove that a
shader implements its declared layout. Buffer ranges, binding sizes and alignment,
layout counts, dispatch counts and indirect argument ranges use device limits as
well as the existing bounded command/resource budgets.

Indirect arguments use four-byte alignment and 16 bytes for non-indexed draws or
20 bytes for indexed draws. Newly created WebGPU buffers are zero-initialized;
producers must write a complete intended record before consumption and overwrite
culled slots each dispatch. Indexed records preserve the signed `baseVertex` word
and the explicitly bound `uint16`/`uint32` index format and byte range. GPU-produced
index and instance values are not synchronously mapped for host inspection.

The portable producer contract requires `firstInstance = 0`. A caller can request
the feature-dependent contract only when the **enabled device**, not merely the
adapter, exposes `indirect-first-instance`. Startup does not request that optional
feature automatically. No shader draw ID, indirect-count draw, multi-draw or
desktop descriptor indexing is inferred. Instance data can instead be addressed
by an explicitly bound per-draw vertex-buffer slice, as in the reference cases.

See [the command schema](browser-gpu-command-schema.md) for the exact JSON API.

## Opt-in reference cases

Open **Renderer counters → Run GPU reference cases** in the browser sample. The
host pauses scene frames and resets the simulation clock on completion. The action
uses a small offscreen color target; it does not replace the scene's renderer or
run automatically at startup. Result publication is checked against the canvas
session epoch, and stop/restart cancels work through the existing renderer lifetime.

`gpu-baseline-reference.js` constructs bounded storage scene records, selects a
small workgroup variant from actual limits, computes conservative clip-space AABB
visibility, and writes every fixed indirect slot with either zero or one visible
instance. A separate render pass consumes those arguments and compute-written
instance vertices using one bounded material group. This is reference-only GPU
culling, not a selectable scene optimization.

The cases cover empty input, zero dispatch, zero visible objects, mixed visibility
and all eight reference slots; indexed 16-bit and 32-bit addressing and non-indexed
submission, nonzero index binding offsets/first indices and signed base vertices;
arithmetic/argument and offscreen-pixel results; and rejected malformed
descriptions. The eight-slot case is the maximum of this reference workload, not
the maximum draw count or allocation supported by the API or device. Readback uses
bounded asynchronous tickets after submission, outside the scene frame loop. A
passing result establishes only these cases on that session; it does not mark the
broader profile qualified or select a faster strategy.

## Scene strategy policy

`?strategy=Auto` permits only `CpuDirect`; `?strategy=CpuDirect` requests it
explicitly. Existing CPU visibility collection remains controlled by the culling
checkbox. Capability snapshots report the request, actual selection, permitted
automatic choices and reason, separately from low-level compute/indirect support.

Forced `ComputeCulling`, `GpuIndirect`, `ComputeSkinning`, `HiZ` or `Meshlet` scene
requests fail with the missing implementation/qualification reason. Unknown
strategies also fail. The reference does not silently turn a forced GPU request
into CPU scene rendering. Existing focused-pipeline material grouping stays bounded
by explicit bind groups and preserves transparent ordering.

## Remaining work

- Run the reference cases and broader browser/GPU correctness and lifetime matrix,
  including device loss, cancellation, resize and native validation errors.
- Qualify optional nonzero indirect instance addressing before enabling it in a
  published profile, and expand argument/vertex/instance boundary coverage.
- Integrate GPU scene culling into the focused pipeline only after correctness and
  physical-device total-frame-cost measurements justify it.
- Implement compute skinning/blendshapes with CPU parity, normal/tangent handling,
  bone/morph bounds and visible-avatar budgets when measurements justify the path.
- Implement Hi-Z only after depth construction, conservative reductions and resize
  invalidation are qualified.
- Compare CPU-direct, CPU-culling, compute-culling and skinning under sustained
  mobile load before changing defaults. No measured performance claim is made here.

# WebGPU indirect submission

The engine WebGPU backend records direct raster, compute, indexed-indirect and
GPU-count-lowered indexed-indirect work into the same ordered engine frame.
Pipeline assets retain their authored submission strategy. Browser execution
does not force `CpuDirect`, infer a different strategy from a missing extension,
or read GPU counts back to choose a submission loop.

## GPU-written arguments and counts

`MultiDrawElementsIndirect` and its offset overload consume standard five-word
indexed arguments: index count, instance count, first index, signed base vertex
bits, and first instance. A zero stride means the packed 20-byte record. The
bound canonical geometry may be an ordinary mesh or the GPU scene's renderer-
owned atlas streams and index buffer; the backend does not construct a second
scene or synthetic mesh.

WebGPU has no native indirect-count operation. The count variant runs a retained
compute kernel that reads the original arguments and count, clamps the count to
the declared capacity, copies active records, and zeros every word of every
inactive record. A following render pass submits the bounded argument slots.
This is GPU-count-driven drawing with zero count/visibility readback. It still
has CPU encoding cost proportional to the declared capacity, so it is not a
claim of native multi-draw performance equivalence.

A retained batch admits at most 65,536 slots; a complete frame admits at most
262,144 replay draws. These are upper-bound encoding budgets, not GPU-visible
counts. Range, alignment, resource ownership and total replay budgets are
checked before encoding. Ordinary command, packet and upload arena bounds are
unchanged. Exceeding a budget reports an explicit error instead of truncating
work or selecting CPU submission.
Replay draw counters describe encoded slots, not the GPU-visible count.

Traditional shared GPU scene indexed shaders use nonzero `firstInstance` for draw identity. The
engine indexed-indirect capability therefore requires the enabled device feature
`indirect-first-instance`. It is requested when the adapter exposes it and is
checked again at submission. A generic authored retained indirect operation may
declare the baseline `zero` first-instance policy instead. Producers must honor
that policy; the renderer never rewrites draw identity to bypass the feature.

`DrawVertexlessIndirect` provides a separate native four-word argument path:
vertex count, instance count, first vertex and zero first instance. A cooked
vertex shader with no vertex-buffer inputs pulls geometry and stable identity
from canonical storage using `vertex_index`. This permits one GPU-produced draw
per compacted state/coverage bucket without a synthetic mesh, index buffer,
hardware mesh shader or per-triangle CPU encoding loop. Both this raster entry
and indirect compute accept slot-owned backend storage directly, retaining its
owner and exact physical generation rather than constructing a CPU mirror.

## Ordering and completion

Every retained compute and render operation ends its WebGPU pass. Ordered
buffer uploads are encoder copies at their authored command boundary, including
indirect, count and atlas buffers. `MemoryBarrier` expresses visibility across
these existing pass/copy boundaries; it neither blocks the browser thread nor
maps GPU memory.
Fresh, unreferenced physical buffer candidates are initialized with bounded
preparation queue writes, so large initial geometry is not limited by the 8 MiB
steady-state upload arena. Mutations of published storage retain ordered-copy
semantics. Abandoned frames retain pending mutations for the next draw attempt;
replacement generations invalidate retained command users immediately while
earlier packet handles remain alive through submission.

`DispatchComputeIndirect` reads three GPU-produced uint workgroup dimensions
from an aligned argument range. The generic descriptor's `indirect` field and
`workgroups` field are mutually exclusive. Producer storage writes must precede
the indirect consumer in a separate pass; writable aliases in the consuming
pass are rejected.

Completion receipts continue to use asynchronous queue completion. Receipts
covering abandoned frames report failure and cannot authorize resource reuse.
Synchronous GPU waits and mapped pointer APIs remain unavailable.
Instrumented algorithms that require those synchronous mappings are rejected;
declaring an instrumented strategy does not authorize a hidden readback-based
replacement for a zero-readback algorithm.

## Shader and meshlet boundaries

Backend indirect operations do not manufacture missing program companions.
Every scene cull/scatter, vertex, material and compute pass still needs its exact
cooked WGSL resource/entry-point contract. Missing shaders, unsupported material
resources and incompatible output profiles fail explicitly.

WebGPU does not provide hardware task/mesh shader stages. The installed Advanced
native family expands resident static meshlets through compute and submits
vertex-pulled indirect rasterization with zero first instance. Its output-scoped
capability is independent of hardware task/mesh probes and the optional generic
indexed first-instance feature. Missing native meshlet residency rejects the
draw; indexed or CPU substitution is not allowed.

The generic Default meshlet route still requires its own production lowering
and cooked shader family. Its capability remains false, and a requested generic
meshlet operation fails at its boundary. See the
[native-family admission record](../../work/progress/rendering/browser-advanced-admission-2026-10-02.md)
for exact output reservation, program, and device-limit checks.

Source was reconstructed after the October 3 workspace loss. Historical build
results do not replace a fresh combined validation gate before publication;
the earlier local build logs are no longer present.

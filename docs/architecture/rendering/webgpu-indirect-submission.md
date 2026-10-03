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

## Native aggregate GPU palettes

Advanced aggregate deformation captures each admitted GPU-owned palette's exact
resident buffer generation and compact 48-byte matrix range while the selected
renderer/mesh pose is leased. Source base, count, and bone order are preserved;
multiple poses may copy overlapping source ranges into disjoint packed ranges.
An absent, replaced, destroyed, or layout-mutated GPU source is rejected before
recording. Replacement detected before any slot work returns an invalid-resource
result so the next publication can recapture it without poisoning the output
slot. Capturing a source never generates or uploads its retained CPU seed.

CPU-authored input sections are uploaded first, excluding every GPU-owned
palette range. The retained engine frame then copies each captured GPU range
after its ordered producer and before aggregate deformation. All native copy
plans retain their physical source and destination dependencies. The packed
input arena cannot receive further preparation writes after its first recorded
copy or dispatch in that frame. Copy metadata belongs to the completion-protected
output slot; a new input publication invalidates same-frame reuse even when its
resource capacity is unchanged.

The current aggregate output remains the only newly produced deformed stream.
Previous-frame geometry continues to use its existing independently owned
output and validity fence. Neither palette packing nor previous-output selection
maps or reads GPU pose, visibility, or count data. Copy-plan storage is bounded
and retains up to four recurring physical source/range shapes per packed copy.
This keeps double-buffered GPU palettes warm across the three-slot aggregate
ring. Replaced generations are evicted; bounded cache eviction uses the existing
deferred command retirement path. The ordinary engine frame command budget
still applies. Backends without an ordered palette-copy producer retain
the explicit unsupported-path rejection.

This producer covers compact GPU-owned bone palettes. GPU-produced active morph
weights/counts have no corresponding packed-input publication and remain
explicitly rejected before CPU reads; retained CPU mirrors are not a substitute.

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

Generic Default/custom authored graphs use the canonical meshlet
cull/refit/finalize family and their original cooked material programs. The
implemented profile retains one LOD and one logical instance; dynamic LOD,
multiple instances and view-dependent transparent ordering require their own
producers and reject explicitly until those producers are installed. Distinct
submeshes retain their own exact deformation inputs. See the
[authored meshlet record](../../work/progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md)
and the
[native-family admission record](../../work/progress/rendering/browser-advanced-admission-2026-10-02.md)
for exact output reservation, program, and device-limit checks.

The October 3 reconstructed source passed fresh combined builds before its
publication. Build/cook results establish source integration; browser execution,
numeric deformation comparisons and completion/recovery behavior have separate
acceptance records.

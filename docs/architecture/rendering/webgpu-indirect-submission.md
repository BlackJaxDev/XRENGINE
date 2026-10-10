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
Fresh physical buffer candidates and complete reusable-slot images own immutable
preparation snapshots until the engine frame's single acceptance import. Their
bounded 256 MiB/4096-record journal is separate from the 8 MiB dynamic upload
budget and its equally sized retry preamble. Incomplete or aborted scenes use
that same import for preparation-only work; no partial scene commands or
frame-local dynamic bytes are submitted. Mutations of published storage retain
ordered-copy semantics. Abandoned frames retain authored mutations for the next
draw attempt; replacement generations invalidate retained command users
immediately while earlier packet handles remain alive through submission.

CPU texture snapshots and lazy array copies share the preparation journal.
Padded buffer-to-texture copies preserve upload/copy/upload ordering in one
encoder, including single-byte mip rows. A copy of a current-frame producer is
instead an ordered retained frame command. Rejecting that producer also rejects
its dependent copy; the array rebuilds before reuse. Sources from an earlier
uncommitted producer keep the scene pending until the source is produced again.
Committed sources remain supported, and a pending copy retains its exact source
generation through acceptance. Physical buffer, texture, view, sampler, binding
group and command-plan creation are still explicit resource creation calls.

`DispatchComputeIndirect` reads three GPU-produced uint workgroup dimensions
from an aligned argument range. The generic descriptor's `indirect` field and
`workgroups` field are mutually exclusive. Producer storage writes must precede
the indirect consumer in a separate pass; writable aliases in the consuming
pass are rejected.

Completion receipts continue to use asynchronous queue completion. The cached
watermark returns with each acceptance, including preparation-only attempts;
the hot frame loop does not issue separate completion polling imports. Accepting
preparation never marks an abandoned scene's frame slots as submitted. Receipts
covering abandoned scene commands cannot authorize resource reuse.
The shipping canvas and mesh diagnostic frame pumps check owner-local receipt
capacity before entering managed simulation or recording. When all 64 receipts
are active, they defer the entire next attempt until the existing asynchronous
observers release a receipt; both error scopes and queue completion must settle
before reuse. Deferred attempts do not accept bytes, advance submission sequences,
or repeat scene capture. Active preparation deadlines continue to run, and the
shipping clock passes accumulated simulation time to the next admitted step's
existing bounded catch-up policy. Receipt saturation is bounded to 45 active
seconds even after the world has presented; successful admission resets this
wait independently of preparation and simulation timing. Surface suspension,
replacement, and recovery clear deferred simulation time along with the frame
clock. Saturation is reported
through the `engineFrame.errorScopes.admissionDeferrals` counter; the original
capacity error remains an invariant guard for direct ungated submission callers.
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
cull/refit/finalize family for meshlet strategies. Traditional indirect instead
uses `indirect::cull-primitive`, retaining the exact original uint16/uint32 index
buffer and cooked material program without requiring meshlet data. Its 64-lane
GPU reduction reads the exact raster position stream, including current
deformation, before publishing a single whole-primitive indexed argument.
Unproven vertex behavior, invalid bounds, or a position stream exceeding the
refit/storage budget retains conservative visibility. There is no geometric
readback and no CPU-generated visibility decision.

Both families share the complete resident source publication, frozen view,
four-level GPU LOD selection, exact submesh/material binding closure, modeled
outline participation, and completion-owned frame slots. Original index
identity, scalar encoding, count and committed revision are frozen separately
from vertex buffers. Missing asynchronous index preparation defers the atomic
frame; terminal preparation failure or later ownership mutation rejects it
explicitly. Neither case enters desktop GLSL or CPU fallback.

Runtime authored instance publishers supply real current/previous transforms and
pre-instance bounds through the same renderer-owned storage used by raster. GPU
union visibility retains one native instanced draw with its exact command count
and zero first instance; invisible instances may remain in a visible cohort.
Unknown vertex behavior is conservatively uncullable. Shared non-temporal
skin/morph output requires an explicit pre-instance contract; temporal deformation
without previous vertex geometry rejects precisely. Unproven mixed transform
conventions, differing LOD primitive membership and view-dependent transparent
ordering still require their own producers. Distinct submeshes retain their
own exact deformation inputs. Strict and instrumented strategy identities remain
distinct even when no instrumentation is enabled. See the
[authored meshlet record](../../work/progress/rendering/browser-authored-meshlet-indexed-2026-10-03.md)
and the
[native-family admission record](../../work/progress/rendering/browser-advanced-admission-2026-10-02.md)
for exact output reservation, program, and device-limit checks.

The October 3 reconstructed source passed fresh combined builds before its
publication. Build/cook results establish source integration; browser execution,
numeric deformation comparisons and completion/recovery behavior have separate
acceptance records.

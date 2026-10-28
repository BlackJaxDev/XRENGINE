# Native UI Change Tracking

The native UI records authored changes for bounded worker layout and retained
rendering. The journal separates layout, visual, transform, clip, and hit changes.
See [retained execution](05-Retained-Execution.md) for snapshot, publication, and
render-resource ownership. A journal lock does not make live scene objects safe
for worker access.

Design: [Retained rendering](../../work/design/ui/native-ui-retained-rendering-design.md)
Roadmap: [Open code work](../../work/todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
Validation: [Native UI checks](../../work/testing/ui/native-ui-validation.md)

## Ownership and identity

The update owner serializes authored tree and component changes. Existing update,
collection, and render boundaries must serialize derived writes too. A journal
lock protects journal storage only. It does not protect the live tree. Never pass
live transforms, components, material callbacks, or mutable glyph lists to a
worker.

`UITransform.ElementIdentity` contains a process-unique ID and an attachment
incarnation. IDs are runtime values. They are not asset IDs and are not serialized.
A change of canvas attachment retires the old identity with a removal tombstone
and advances the incarnation. Destruction also retires the identity. The journal
contains value records, not references to scene objects. A tombstone wins over
other pending records for the same full identity.

The outer layout-owning canvas owns the journal for nested canvases. Reparenting
can affect both old and new owners. Custom mutation code must respect both owner
boundaries. Callers must serialize writes even outside a transaction.

## Dirty domains

Each transform exposes separate `ChangeRevisions` for measure, arrange,
transform, visual, clip, hit test, topology, and text. These revisions describe
local changes. Ancestor cache invalidation does not pretend that each ancestor
has an authored change.

| Input or change | Recorded domains and behavior |
|---|---|
| Size, padding, margins, auto-size input | Measure, arrange, transform, clip, hit test. Invalidate measure and arrange caches on the ancestor path. |
| Layout translation or anchors | Arrange, transform, clip, hit test. Keep valid measured sizes. |
| Placement-info replacement | Topology and conservative measure/arrange invalidation. |
| Scale, rotation, depth | Transform, clip, hit test. Keep layout caches. |
| Visibility | Visual, clip, hit test and conservative layout invalidation. Collapsed layout can change size. |
| Add, remove, reparent, child order | Topology and hit test, plus affected layout caches. Canvas moves retire old attachments. |
| Text content, font, size, wrapping, alignment | Text, measure, arrange, visual. Existing text layout behavior remains separate from this notification contract. |
| Glyph transform edit | Text and visual. Mutable glyph arrays require `MarkGlyphTransformsChanged`. |
| Component color or decorative state | Visual by default. Custom controls override `UIComponent.GetUIChangeDomain` when their input affects other domains. |
| Material, render pass, batching eligibility | Visual and topology. Clip eligibility also changes clip and hit domains. |
| Canvas output space or camera | Transform, clip, hit test; output-space changes also mark topology. |

`UILayoutSystem.InvalidateMeasure` and `InvalidateArrange` record the change and
advance layout versions on the source and its ancestors. They do not scan
descendants or copy a canvas. At the next request, `UILayoutCanvasSession` drains
the journal. `UILayoutSnapshot.CaptureIncremental` then recaptures only the changed
nodes and their ancestors from the previous snapshot. A removal, a `Topology`
change, an unknown element, or an overflow causes a full capture.
`InvalidateCanvasLayout` invalidates the canvas measure and counts a global
invalidation. It does not scan descendants. Do not use it for a local color or
position change.

Nested mutable resources need explicit notification. Call
`UITransform.RecordUIChange` on every affected owner **before** an authored write
to a shared material, shader value, texture binding, or custom resource. Use
`Visual` for color/UV data and `Visual | Topology` for shader, texture, render
state, or batch membership. The built-in button background and native field
focus callbacks follow this contract. Render-time uniform writes do not notify
authored changes; doing so would dirty the UI during every draw. Custom layout
setters must also call `InvalidateMeasure` or `InvalidateArrange` as appropriate;
recording a domain alone does not schedule layout.

## Bounded journal and transactions

`UICanvasTransform.BeginMutationTransaction()` opens a nestable, thread-bound
scope on the owning canvas. It batches journal records, not live-state writes.
It neither delays existing setters nor starts a worker. Dispose scopes in reverse
order on the opening thread. The outer dirty scope advances the committed
revision once. Empty scopes do not advance it. A competing writer during a scope
is rejected. Cross-canvas changes still require serialized access to both owners.

`UIChangeJournal` preallocates storage for 4,096 distinct pending identities by
default. Repeated writes combine domain flags and keep the latest parent.
`TryDrain` copies into caller-owned storage and clears records only on success.
It returns false during a transaction or when the destination is too small.

Overflow sets `RequiresFullResync`; it never grows storage or silently reports a
complete delta. A consumer must rebuild its model from the owner at a safe
boundary when this flag is returned. It must not apply the partial records as a
complete update. `UILayoutCanvasSession` is the destructive drain consumer. When
the flag is set, it captures the full canvas instead of an incremental snapshot.

Publication readers use a separate committed log. `TryGetCommittedCursor` and
`TryCopyCommittedSince` copy whole committed transactions without consuming
layout records. An open transaction returns `Busy`. An evicted cursor returns
`FullResync`. Sparse publication capture in `UICanvasComponent` reads this log.

Recording and draining within warmed capacity allocate no managed storage.
Opening a transaction allocates its scope once. Creating a canvas journal also
allocates its bounded storage. These facts do not establish allocation-free
layout or rendering.

## Work counters

`UIRuntimeCounters.Enabled` is false by default. Enable it before a measurement
interval and subtract cumulative samples at controlled frame boundaries. Use
`CopyTo(Span<long>)` with reusable storage. Counts are process-wide, not per
canvas. Each counter is atomic; a multi-counter sample is not one atomic frame.

| Counter group | Meaning |
|---|---|
| Mutations, ancestors, global invalidations | Recorded change calls, ancestor cache invalidations, explicit global invalidation calls. Repeated calls can count the same element. |
| Measure/arrange visits and cache hits | Defined in `UIWorkCounter`, but no production path increments them. They stay zero. |
| Hit queries and candidates | Hit tests against the accepted publication, and the count of hit records that each test scans. A repeated query with unchanged inputs does not count. |
| Glyph copies and glyphs copied | Glyph buffer writes that changed at least one glyph, plus the changed glyph count. |
| Batch entries and resets | Entries collected and collection-buffer reset operations. |
| Buffer bytes and ranges committed | UI CPU buffer ranges committed by the collector, including capacity where that path commits capacity. These are not GPU-driver upload measurements. |
| Publication record and segment counters | Record-store cells, paths, and claims copied or released, and flat cells and composition segment slots materialized or copied. |
| Publication captures | Nodes visited by capture walks, full captures, sparse captures, sparse splices, and sparse fallbacks to a full capture. |
| Text layout cache hits and misses | Text layouts that reused a cached glyph placement, and text layouts that placed glyphs again. |
| Failures reported | Native UI failures reported through `UIFailureDiagnostics`. |
| World unchecked draws | Direct-world UI draws that used the route without checked indexed-draw proof. |

Use a profiler for main-thread and total CPU time, allocations, and latency. Use
backend measurements for actual GPU uploads and GPU time. These counters supply
work counts; they do not prove a speed advantage over ImGui.

## Source map

- [Journal, value records, domains, counters](../../../XREngine.Runtime.Rendering/Rendering/UI/State/)
- [Transform identity and mutation ownership](../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UITransform.Changes.cs)
- [Layout invalidation and invalidation counters](../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UILayoutSystem.cs)
- [Journal drain and incremental layout capture](../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UILayoutCanvasSession.cs)
- [Component domain contract](../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/UIComponent.cs)
- [Text notifications and copy counters](../../../XREngine.Runtime.Rendering/Scene/Components/UI/Text/UITextComponent.cs)
- [Batch counters](../../../XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs)
- [Input counters](../../../XREngine.Runtime.InputIntegration/Scene/Components/Pawns/UICanvasInputComponent.cs)

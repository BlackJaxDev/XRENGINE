# Retained UI Execution

The native UI uses the existing canvas, material, and `UITextComponent` renderer.
It does not introduce an immediate-mode UI renderer. ImGui remains the default
editor. Use the [implementation TODO](../../work/todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
and [validation record](../../work/testing/ui/native-ui-validation.md) for open
work and acceptance results.

## Native tool state and wheel continuation

`NativeToolPanelState` supplies the asset, console, profiler, and scroll bindings
for both `EditorPanelHost` and the active `NativeDockTreeHost`. It restores view
and filter fields before scroll offset. Console category and auto-scroll and the
profiler view use the existing layout fields. Scroll events request the existing
`NativeLayoutSaveDebouncer`; they do not write the layout on each wheel step.
The bindings retain object-lifetime receipts and reject detached or replaced
components. The dock host additionally checks its current slot ownership.

Dock rebuilds detach retained slots through `NativeDockSlotDetachment`. This
structural operation captures node, transform, parent, and world identity before
deactivation. It rechecks ownership after deactivation and parent notifications.
A newer reparent or transform replacement is preserved. The normal unchanged
detach clears the retained world context. Canonical hierarchy errors propagate;
the helper does not suppress an interrupted parent edit.
The registered route removes an exact old slot mapping after a transfer, so a
later stack build cannot take the panel back. A changed slot that remains under
the same host stops rebuild before chrome teardown with an explicit error.

Wheel dispatch holds the presented publication and checks the output's current
publication after callback-bearing receivers. A callback that publishes a new
packet stops propagation through the old ancestry even when the input cache has
not yet refreshed. Receiver-local coordinates still come from the held hit record.

## Layout ownership

`UICanvasTransform.UpdateLayoutAsync` requests layout. `UILayoutCanvasSession`
keeps one running task and one replacement pending snapshot per canvas. The
scheduler permits two workers. It reserves a 32 MiB working page before capture
and permits 128 MiB of leased working pages. Capture checks estimated storage
before adding nodes, child edges, grid tracks, or text metrics. Oversized inputs
produce an explicit diagnostic. These quotas cover in-flight layout work; they
do not include every accepted publication held by a live canvas or output.

`UILayoutSnapshot` captures numeric constraints, topology, transform values,
identities, and dependency revisions on the owner thread. Workers receive no
scene objects or callbacks. `UILayoutValueKernel` performs measure and arrange.
The serial oracle uses the same kernel. Built-in operators include base and
boundable transforms, lists, grids, split panels, docking, tabs, rotation, fitting,
and scrolling. Custom transforms must supply an `IUILayoutValueAdapter` payload.
An unknown transform does not silently use a different layout algorithm.

Public `Measure` on an attached transform requests canvas work and returns its
last accepted desired size. Before first acceptance it reports that no result is
available. `Arrange` and `FitLayout` request canvas work; they do not mutate live
geometry synchronously. The canvas owns attached constraints. Detached `Measure`
uses a bounded snapshot and the same value kernel without applying geometry.
Detached arrange requires attaching a canvas first. Legacy child-resize overrides
are no longer an alternate layout entry point.

Measure reuse requires matching identity, aggregate measure revision, text
inputs, and constraint. Arrange reuse also requires matching child edges,
allocated slot, placement, visibility, and geometry. Topology-stable capture
uses immutable pages of 256 nodes. A changed page is copied before writing;
unchanged input and output pages remain shared. The worker skips cached subtrees,
and owner application visits changed output pages. Page-table copies still scale
with page count. Topology changes require a new capture. This is not a claim of
constant-time work for every leaf edit.

A font generation guard detects metric changes that precede queued owner
notifications. Text dependencies are scanned when that generation changes.
The flat output accessor is a diagnostic compatibility path; normal publication
uses the immutable indexed page view.

Scroll extents use the offset that placed the applied child geometry. A newer
authored offset cannot increase the measured content size while layout is
pending. Owner application marks scroll geometry provisional before writing.
An interrupted apply preserves the last settled limits. Recovery reapplies all
owner output pages before accepting a generation; ordinary successful updates
retain sparse application.

Before applying a result, the owner checks canvas lifetime, element identity and
incarnation, parent and child order, layout dependencies, text metrics, and root
constraints. A visual-only edit does not invalidate an otherwise valid layout.
Cancellation checks occur at subtree boundaries. A running task retains its
storage until completion. Canvas deactivation cancels its session and revokes
its publications.

## Text values

`FontGlyphSet.LayoutSnapshot` copies glyph metrics into numeric inputs.
`UITextLayoutKernel` returns immutable glyph placements, intrinsic size, and
UTF-16 caret stops. Caret stops follow grapheme boundaries and include whitespace
and line breaks. Measurement precedes alignment. `UITextLayoutResult` carries
text and font revisions. Its public constructor copies caller-owned arrays.

`UITextLayoutKernel.Measure` reuses layouts through `UITextLayoutCache`. The key
holds every glyph-placement input: text, the font metric page (by reference) and
its revision, em and atlas size, font size, wrap, spacing, line spacing, alignment,
margins, DPI scale, first-bearing trim, the shaped run (face content identity,
language, direction, features, and glyphs), and the available size. Text revisions
are not part of the key. A hit with another revision returns a result that shares
the cached glyph and caret storage and carries the requested revisions. The cache
is direct-mapped with 512 slots and is thread-safe; a lookup does not allocate.
`TextLayoutCacheHits` and `TextLayoutCacheMisses` count lookups. Shaped runs are
reused per node when the captured shaping inputs do not change.

Text rendering retains the accepted result instead of copying a mutable glyph
list each frame. Color, transform, outline, and bounds are text-instance values.
A glyph payload change is separate from an instance-only change. Fonts own copies
of authored glyph maps. Reassign an edited map to publish a new metric revision.
Unchanged captures reuse the immutable metric page. Full shaping, bidirectional text,
and fallback-font behavior are not established by the basic rune-layout kernel.

`UIFontFaceSnapshot` owns font bytes and frozen glyph-ID metrics. The optional
`SkiaTextShapingBackend` uses the existing HarfBuzz dependency to return glyph IDs,
advances, offsets, and UTF-16 clusters. Rasterization and shaping use the same
owned bytes. Missing atlas coverage produces a named failure. `UITextComponent.UseShapedText` opts into one direction run with one atlas and
no wrapping or line breaks. Owner capture freezes the shaping request. The bounded
layout worker shapes it, then measures and arranges the resolved numeric values.
Only backends that declare worker-safe inputs enter this path. The cache checks
text/style/font inputs and the atomic backend identity and generation. Accepted
shaped pages remain shared on unchanged captures. Pending work compares dirty
inputs with the accepted output, including changes from rejected earlier tasks.
Stale worker faults cannot block a newer valid input. Feature, glyph, and scratch
limits reject oversized requests before expansion. Skia reserves a conservative
managed-copy allowance; this is not a hard limit on native allocator use.
Backend replacement invalidates active shaped owners. Bidi analysis, fallback,
and wrapped shaping remain open. Unsupported direction mixes,
digits in RTL runs, and bidi controls report a named failure. Runtime face data
does not change the serialized font format.

## Owned custom draws

Unbatched records use `UIDrawPayload` with the existing mesh command path.
Capture copies mesh buffers, mutable triangle indices, shader source and
frontend identity, numeric parameters and arrays, and nested raster state.
Deferred commands do not retain source callbacks. The built-in text adapter
copies the accepted layout and owner-captured animated glyph adjustments into
private transform, UV, and rotation buffers. Required resource publishers defer
unready descriptors instead of drawing with a placeholder.

`UIDrawGeometry` shares immutable mesh and fixed-buffer owners between material
captures. Paint changes reuse those owners. Animated glyph changes replace the
changed buffers while retaining unchanged UV storage. Each physical copy spends
the shared byte budget once; geometry owners do not consume draw-payload slots.
The component writer reads accepted glyph spans, freezes animation overrides
when `MarkGlyphTransformsChanged` is called, and commits changed used ranges.
Storage resize and writes share one lock and validate actual byte lengths.
Explicit raw-buffer invalidation forces byte validation before reuse. The standalone `UIText` helper uses the same immutable layout kernel and used-span
writer. Its font observer only marks dirtiness. The render/preparation owner
consumes that mark under the same gate used for mesh changes and draw admission.
Pool return and destruction detach observers; Math label owners call the full
teardown path. Built-in text captures copy only used glyph spans. Generic/custom buffer captures
retain their full storage semantics. Both text paths use `UIGlyphBufferWriter`
for bounds checks and dirty writes.

`TryCapturePublicationPayload` is the explicit adapter boundary. Derived custom
components must declare `CanCapturePublicationUniforms` only when capture owns
all values emitted by their material callback. Unknown callbacks, deformation,
and unsupported resources reject the complete candidate with a named failure.
The previous complete canvas publication remains active.

Static 2D image captures share `UITextureSnapshot` pixel and sampler copies.
Warm checks read bounded mip identities, mip revisions, and sampler values;
they do not scan atlas pixels. After direct pixel storage edits, the owner must
call `Mipmap2D.Invalidate()` or `XRTexture.PushData()`. Replacing mip storage also
invalidates capture. GPU outputs and non-2D or custom texture types require an
`IUIDrawProducerLease`. A lease pins one immutable texture generation and can
replace one material texture slot without changing the source material.

Draw copies reserve at most 16 MiB each, 128 MiB in total, and 4,096 retained
payloads. Shared texture copies reserve at most 64 MiB each, 128 MiB in total,
and 256 retained snapshots. These bounds cover captured storage, separately
from source asset and producer pools. Capacity failure rejects the candidate
before output. Publications and backend artifacts retain payload claims until
their use ends. Terminal cleanup runs on the render owner. A cleanup failure
quarantines its resource owner and keeps its budget reserved. A failed native
owner destruction does not release dependent texture or producer claims.

Canvas, window, input, collector, and backend holders retain explicit publication
claims. Readers acquire a claim while the holder lock is held and release it in
`finally`. Holder replacement detaches the old entry before releasing it. The last
claim retires the packet's draw resources; normal reuse does not wait for garbage
collection. Finalization is a fallback for abandoned packets, not the normal
retirement path. The four-surface producer pool remains bounded and can refuse a
new generation while readers or GPU artifacts still hold its surfaces.

Direct-backend receipts retain even failed draw attempts until their fence retires.
Collector admission failure cancels the reservation before authoring. Vulkan
receipts also wait for command-artifact retirement. Shutdown drains an exact
renderer generation only after a successful native idle wait and artifact teardown.
A timeout, skipped wait, device fault, or cleanup failure does not establish idle
proof. These ownership rules do not establish a live frame-latency guarantee.

Draw source observers mark the canvas dirty when shader source, numeric parameters,
notifying raster state, buffer commits, buffer collections, or texture snapshots change.
An observer detaches when its cache entry changes or its component stops. Queued
notifications from the old observer cannot change the new owner state. Direct writes
to index lists, array storage, raster dictionaries, or properties without change events
require `UIRenderableComponent.InvalidatePublicationDraw()` on the app thread.
Text refresh uses the current font atlas, atlas shader mode, and distance range values.

## Publication and input

`UIPublication` contains a canvas lifetime, layout and hit generations, numeric
layout, render records, clip data, and hit records. Render membership follows the
captured `VisualScene2D` registration order. Frozen records carry material or
text instance values. Output mappings are cached per viewport and generation.
Screen mappings include viewport origin, scale, and the top-left pointer origin.
World and camera mappings retain the camera and canvas matrices for ray-plane
intersection.

`XRWindow` records UI output receipts. `UICanvasInputComponent` queries the
accepted publication for its output. Mouse, touch, and virtual controls use the
same frozen hit shapes and clips. They reject detached or recycled identities
and changed interaction revisions. An output without an accepted packet is not
interactive. Pointer capture retains its target lifetime; it does not transfer
to another recycled row. Tab navigation uses authored tab order. Directional
navigation uses the presented bounds and active popup scope. Scroll routing uses
the captured ancestor ranges. An `IUIPointerScrollReceiver` with a current hit
record receives the cursor in its presented local coordinates and that record's
pointer geometry; without a record it receives only the delta. Focus changes remove the previous target's input
registrations before attaching the next target. Re-entrant focus callbacks settle
on the latest request, and canvas deactivation removes its input mappings.

Hit transitions use reusable lists and identity sets. Idle queries reuse prior
hits when pointer coordinates, output mapping result, hit generation, and capture
state agree. `RenderInfo2D.MembershipId` provides stable membership independent of
mutable painter order.
Leaving the output region invalidates that cache before clearing intersections.
A return to the same point must perform a fresh query. Blocker filtering compacts
the existing hit list in place and preserves only the frozen blocker subtree.

Press capture retains the displayed publication, its canvas-to-local transform,
and `UIPointerGeometry` until release or cancellation. Scroll metrics come from
accepted layout output. A callback can replace capture; dispatch uses a local
copy and a capture token so the old handler cannot change the replacement.
Scene viewport input uses the same frozen hit record for normalized coordinates.
Deferred pick and hover work validates the pane, input owner, window, camera,
world, focus, and current presentation before applying its result. An inactive
input dispatcher cannot revive its old presented mapping.

### Sparse publication capture

`UICanvasComponent` captures a publication in layout preorder. A complete capture
records, for each node, its transform, inherited capture context
(`UIPublicationCaptureContext`), subtree end, record starts, hidden-subtree root,
and pointer dependencies. The accepted publication then becomes the sparse basis.

The next capture reads the committed change journal from the basis cursor. It
also compares layout output pages; a shared page has no changes. A changed node
becomes a capture root. A scrollbar depends on its scroll target and thumb, and a
splitter depends on its split target, so a change on the target also captures the
dependent control. Changes inside a hidden subtree are skipped, because a hidden
node publishes no records for its descendants.

The canvas uses a complete capture when any of these apply:

- No basis exists, or the basis capture did not visit the tree in preorder.
- The journal read is incomplete, an element was removed, or an element is unknown.
- Identities, painter order, canvas bounds, world matrix, draw space, offscreen
  mode, world collection revision, or canvas lifetime changed.
- A capture root is the canvas root, or capture roots cover half the nodes or more.
- A recaptured draw cannot publish (capture failure or missing command).

A sparse capture visits only the capture-root subtrees. When each subtree keeps
its record counts, render memberships, and focus-scope identities, the canvas
replaces only changed pages. `UIRecordStore.WithReplacedPages` shares unchanged
leaves and branches, and composition segments outside changed pages keep their
identity and revision. A rejected sparse capture clears the basis before the
complete capture.

When record counts change (visibility, caret, activation), the canvas describes
the capture as `UIRecordSplice` edits of the previous stores:

- Hit, scroll, and focus-scope records follow tree order, so each recaptured
  subtree is one edit at its old record start.
- Render records follow painter order. The painter order did not change, so a
  `UIRenderPainterIndex` maps each membership to its painter position. The canvas
  finds each removed record and places each new record by binary search over the
  previous store. If a record does not follow the painter order, the canvas uses a
  complete capture.
- `UIRecordStore.Splice` shares every page that no edit touches and copies only
  touched pages, so it copies at most the inserted records plus two pages for each
  edit. Spliced pages can be shorter than 256 records; the store keeps the page
  starts for exact index lookup. When the page count exceeds two times the full-page
  count plus four, the store packs all records again.
- `UICompositionSegmentSet.WithSplicedRenders` keeps each untouched segment, moves a
  shifted segment with a new revision, and makes new ranges only for runs that hold
  inserted or lost records.
- Focus scopes find their last painted record again through the walk-order
  memberships of their subtree.

Scroll receivers and open focus scopes stay flat arrays, because they are few. The
canvas walk state still moves record starts with one pass of integer additions over
the node arrays. The new publication is complete before acceptance, so no reader
sees a partial splice.

### Collector page reuse

`UIBatchCollector` collects a published render store one record page at a time
(`UIBatchCollector.PublishedPages.cs`). A batch run never crosses a page boundary.
This adds at most one run for each render pass at each page boundary. Each
collected page keeps:

- its runs and its unbatched draw positions, in painter order;
- the primitive handle of each record, including hidden records;
- the composition segments that overlap the page, with their identity, revision,
  and page-relative range;
- its unbatched draw count and its pending text-layout flag.

The collector keeps the pages of its latest completed collection, keyed by leaf
object. A leaf is immutable, so the next collection reuses the page when the same
leaf holds the same segments at its new position. A reused page adds its shared
runs and markers to the collect side and does not visit its records. A page moves
without a visit when a splice shifts it, unless it overlaps a segment that the
splice moved. A moved segment has a new revision, and runs store that revision.

Only new runs get a run identity from old runs, and only from runs of pages that
the collection dropped, so reconciliation work depends on the changed pages. A
primitive slot counts the collected pages that draw it. A dropped page releases
its counts; a slot with no count and no owner registration becomes free. An
unregistered owner keeps its handle while a page still draws it. Runs of dropped
pages return to a pool only at the next swap that replaces the render side,
because the render side can read them until then. A discard keeps the committed
render side, and the discarded collection stays the basis for the next one. An
unchanged store replays the committed painter stream without a record visit.

`ExpectedPublishedDrawIntentCount` and `HasPendingPublishedTextLayout` read page
summaries that the swap commits, so the per-frame canvas checks do not walk the
records. `LastCollectedPageCount`, `LastReusedPageCount`,
`LastCollectedPageRecordCount`, and `LastRunReconcileVisitCount` report this work
to tests.

`UIWorkCounter.PublicationCaptureNodesVisited`, `PublicationFullCaptures`,
`PublicationSparseCaptures`, `PublicationSparseSplices`, and
`PublicationSparseFallbacks` report this work. `PublicationRecordCellsCopied`,
`PublicationRecordPathsCopied`, and `PublicationFlatCellsMaterialized` report store
copies and flat-array use.

The output receipt integration still requires graphics validation. CPU command
completion alone does not prove that deferred GPU draws reached an output.
Do not infer atomic rendering, Vulkan replay correctness, or visual parity from
headless packet tests.

## Native failure reporting

`UIFailureDiagnostics.Report` records native UI failures that withhold or cancel
accelerated output. Each reason starts with a failure code and a colon. A report
increments `ReportedCount` and `UIWorkCounter.FailuresReported`, stores
`LastFailure`, and logs a UI warning at most once per code every two seconds.

OpenGL and Vulkan do not require atomic frames, so these paths report on those
backends where an atomic backend throws:

| Code | Cause |
| --- | --- |
| `UI.Canvas.BatchCapacityPending` | No isolated GPU cohort or receipt lease is free for the canvas. |
| `UI.Canvas.ReceiptRejected` | The renderer did not reserve a publication receipt. |
| `UI.Canvas.PublicationReceiptIncomplete`, or the first named authoring reason | A receipt did not complete. `CompleteUiPublicationReceipt` reports the reason that `FailCurrentUiPublicationAuthoring` stored. |
| `UI.Canvas.RequiredPassMissing` and other abort reasons | `AbortUiPublicationReceipt` cancelled a receipt. |
| `UI.Canvas.SurfacePending`, `UI.Canvas.QuadPending` | An offscreen surface belongs to another renderer generation. |
| `UI.BatchRegistrationFailed` | A published batch draw did not register. The live command is not drawn in its place. |
| `UI.Batch.GroupMissing`, `UI.TextCapture.AtlasPending` | A batch marker has no collected group, or a text record or group has no layout or atlas. |
| `UI.WorldOutputUnsupported` | `XRViewport.EnableRetainedWorldUiColor` is set, but the output cannot admit checked world UI. The reason names the first failed condition. |

Direct-world draws without checked indexed-draw proof use the unchecked route and
increment `UIWorkCounter.WorldUncheckedDraws`. Eligibility routes are not
failures: `StrictOneByOneRenderCalls`, standalone text that the shared text batch
does not admit, and material quads without batch support draw individually by
design. WebGPU keeps its atomic exceptions.

## Batch storage

`UIBatchCollector` distinguishes owned solid, border, image, and text semantics.
Desktop intrinsic quad controls keep color, border, texture, and UV values without
allocating a material or shader per control. Explicit `Material` access creates an
isolated legacy material when that path can preserve the primitive. Border or
atlas-subregion escape reports an unsupported-path failure.
`UIBatchMaterialPrototype` provides immutable shader and raster definitions for
solid, border, image, and text cohorts. Each cohort creates its own mutable
material and raster options. Deferred draws must not share those mutable values.
Common combo rows use the intrinsic solid path. Per-control color, border,
texture, and glyph values remain in instance records.
Unknown custom shaders retain normal draw commands. Batch eligibility fixes
shader, sampler, blend/depth, and feature contracts. Cohort and painter-run keys
include primitive type, atlas or image snapshot, shader mode, clip values, pass,
and material features. An unsupported profile uses an owned draw.

Primitive slots use membership plus element incarnation. Activation registers
the current owner; deactivation unregisters it. A slot stays allocated while a
collected record page draws it. Old packets retain distinct historical slots. A shared slot lock prevents collector and owner races. A stale
unregister cannot remove the new owner.
 Common editor borders use
`SetBatchBorderStyle`; solid backgrounds use `SetBatchSolidColor`. Per-control
outline values do not require a new shader source.

Desktop image cohorts use `UIImageBatched` shaders with per-instance UVs and tint.
Linear RGBA8 and hardware sRGB8-alpha8 images require matching color metadata.
Resizable image mip formats must also agree with GPU storage so OpenGL and Vulkan
sample the same color space. A known mismatch rejects publication with a named
failure. Pending image storage follows the ordinary readiness path. This does
not establish runtime shader or GPU acceptance.

`UIImageAtlas` owns fixed RGBA8 pixels and normalized image regions. A page accepts
1..256 images, with each dimension at most 512 pixels and a power-of-two page
size of 4..2048. All images share one declared color space. One-pixel edge gutters
support linear sampling without adjacent-image bleed. Pages do not repack in
place. `UIImageAtlasComponent` owns the source texture and binds descendants
through `NativeUIElements.CreateImage` or `Bind`. Packets retain private texture
snapshots. Teardown waits for observable binding setters to unwind, clears only
matching bindings, and keeps ownership if a detach veto prevents cleanup. No
bindless image path is selected.

Painter runs retain pass and order boundaries. GPU cohorts isolate storage still
in use by previous draws. Reuse requires the backend completion and buffer reuse
checks. A failed submission is reusable only when the renderer proves that no
native submission occurred and the buffers are free. Unknown failures keep their
storage unavailable. The cohort count is bounded; inactive cohorts retire only
with completion evidence.

Unchanged render storage reuses collected runs without logical clear/re-add.
Changed storage still traverses records; a sparse operation queue remains open. Material fields use coalesced
dirty upload ranges. Text instance edits leave glyph buffers unchanged. Stable
glyph ranges retain unchanged runs when another run changes length; an index
stream selects each run without moving unrelated glyph payloads. These mechanisms
do not establish independent composition layers or a measured performance
advantage over ImGui.

Set `XRE_UI_BATCH_DIAGNOSTICS=1` to enable text and batch diagnostic logging.
Leave it unset for ordinary workloads.

## Controls and virtual data

`IUIValueBinding<T>` defines typed retained values. Controls subscribe while
active and unsubscribe on deactivation or row recycling. Editor adapters cache
property access, marshal source notifications to the scene owner, and group
mutations through `Undo`. Engine targets use property-change tracking. Plain
object targets record their committed before/after values in the same undo group.
`UINumericInputComponent<T>` retains an invalid draft until valid commit or
cancellation. It supplies culture-aware parsing, finite limits, keyboard steps,
and an optional Alt-drag edit. Enter or Escape ends that drag before dispatching
submit or cancel. Later pointer movement cannot resume the old edit. Integer
steps use a wider intermediate value before range checks.
`UIScrollbarComponent` uses the press packet's thumb and scroll extents. Slider
and scrollbar input must not combine displayed coordinates with pending live
geometry. A changed target identity or configuration rejects the old capture.
Combo typeahead searches at most 256 local options. Larger virtual sources must
provide `IUIComboPrefixSearch` for prefix lookup and `IUIComboEnabledNavigation`
for adjacent enabled-option lookup. Capability and result properties distinguish
an unavailable index, no match, changed source, invalid result, and selection.
Lookup checks source identity, count, revision, stable ID, and enabled state before
commit. Prefix matching uses ordinal case-insensitive comparison. The gallery and
enum providers build bounded-prefix indices when they are created. They do not
scan all options on a key press. Toggle commits also recheck the binding after
callbacks. Keyboard focus updates checkbox borders and a separate button border;
replacement and recycling clear the old button border. Focus-loss visuals update
after the focus field changes. The gallery uses buttons, checkboxes, radio groups,
numeric inputs, virtual dropdowns, range sliders, color selectors, and scroll
regions. These controls provide keyboard, focus, state, commit/cancel, clip, and
binding contracts; live platform acceptance remains separate.
Shaped text distinguishes glyph provenance from complete
grapheme caret geometry. Complete runs use visual left/right navigation while
keeping logical UTF-16 edit positions. Incomplete runs reject geometric caret,
selection, and candidate placement rather than inventing logical geometry.
Logical edits can continue while a newer layout is pending. Text fields provide selection, grapheme navigation,
undo/redo, and composition state.

A text field hit freezes `UITextCaretHitMetadata`: the field, the text layout that
the same capture gives to the text render record, and the text that the layout
places. A press places the caret at the grapheme boundary nearest to the point on
the nearest placed line. Shift extends the selection. A drag and its release use the
press packet's layout, so a newer layout cannot move the anchor. The field uses the
frozen layout only while it holds the same text. After an edit the old layout does
not place the caret, and the next capture freezes the layout of the new text. A
field with incomplete grapheme stops publishes no caret metadata. Text fields
receive pointer moves for drag selection.

The desktop IME adapter uses existing Windows IMM32 APIs. Composition events carry
an immutable focus identity and focus epoch. A separate interaction revision
rejects recycled or disabled targets. Candidate placement uses
the caret overlay's frozen record in the displayed packet. A newer pending
layout does not move the native candidate window. Deactivation removes the
candidate update registration before cancelling composition. Other hosts report unsupported IME capability. This
boundary does not imply tested Windows IME behavior or a Linux IME implementation.

`IVirtualListProvider` supplies item count, stable IDs, and row binding.
`UIListTransform` realizes the viewport plus overscan and a bounded reusable row
pool. Source extent uses count and fixed row height. Leases distinguish a physical
row from its current logical item. `UIVirtualTreeProvider` keeps cached visible
ranges for tree mutations.

`RequestVirtualItemFocus` requests indexed item realization on the owner thread.
It waits for a presented hit with the requested list identity, row identity, item
ID, and lease incarnation. Input does not query the live provider to validate the
packet. Recycling invalidates interaction revisions. An explicit focus change or
removal cancels the request. Providers without an item-ID index cannot service
this request. The tree provider maintains an item-ID index across visible-range
splices. Hidden collapsed descendants must be expanded before requesting focus.

The hierarchy uses virtual rows and retains canonical scene commands. Plain
scene/root lists have no mutation events, so the panel checks their shallow
membership every 250 ms. Child-list notifications handle subtree edits.
The inspector virtualizes primitive property rows; complex editors open in a
separate detail view. Inline variable-height virtualization remains a decision.
The inspector component picker uses the canonical component catalog and queued
scene mutation path. Add/remove operations group structural Undo and dirty state,
including automatically added required components. Failed creation removes its
partial component delta. Deferred completion callbacks reject rebuilt or changed
inspector targets.

Virtual tree/table accessibility exposes stable IDs, names, roles, selected state,
and tree depth/expansion without creating offscreen rows. The platform adapter
boundary reports unsupported OS integration. Gamepad Back dismisses the nearest
open popup and removes its input subscription when hidden.

Radio groups request focus through the presented packet before selecting the
next item. Binding callbacks cannot resume a stale commit after replacement or
disposal. Scroll controls consume directional input only while they can move.
Popup teardown destroys owned child nodes through their normal lifecycle.

Native asset file moves and rename/folder Undo operations check root boundaries
and linked paths at execution time, including Undo and Redo. Moves stay within
one watched asset root. Inspector component drops use the canonical property
mutation path. Watcher events carry the exact monitor installation, file backend,
and backend generation. Project root changes invalidate that context, including
same-path project reloads. Metadata work checks live paths under the metadata
lock. External hooks run outside that lock and recheck their context between
calls. Monitor creation and disposal cannot reinstall a retired owner through
callback reentry. This admission contract does not cancel an import or reload
that already started. Context-aware path reservations and complete companion-file
transactions remain open. Windows watcher ordering and complete workflow parity
remain unverified. `NativeDockTree` provides a bounded, versioned split/tab model with
move, resize, close, reopen, and malformed-state checks. `NativeDockTreeHost` connects that model to the primary editor canvas. Structural
changes retain panel instances, deactivate/detach closed slots, and destroy only
obsolete structural nodes. `NativeDockRenderGate` serializes rebuilds: a rebuild
that a callback requests during a rebuild runs once after it, and repeated requests
stop after four passes with an error. A failed pass retires its revision before
the gate opens, so its partial controls cannot act. The original error propagates
and a later rebuild can run. Each pass has a revision. Tab, split, drop,
drag, and action-menu callbacks act only when their pass is current, no rebuild
runs, and their stack is still in the tree. Slot deactivation callbacks can
reparent, replace, or destroy a slot; the host then detaches only the registered
slot and recreates a slot that teardown destroyed. Closing the last tab keeps an
empty stack with its Actions menu, so the panel can be reopened.
Existing tool search and scroll state survive. The window layout also stores the
selected profiler view and the console category and auto-scroll setting. The host
restores them before the panels activate. An unknown profiler view falls back to
CPU Timings, and an unknown console category shows all categories. Wheel scrolling
updates the stored offset in memory. `NativeLayoutSaveDebouncer` writes the layout
once after scrolling stops for 500 ms, or when the tool host leaves the scene. A
discrete layout change writes the complete layout immediately and clears the
pending scroll save. The scene/tools split and the dock tree splits use the shared
`UISplitterHandleComponent`. A splitter drag updates the stored size in memory, and
the same debouncer writes it after the drag settles. All seven primary panel types use the detached-window path. Legacy numeric
window records migrate an existing saved layout; absent records seed the requested
panel. Detached windows use their own camera, pawn, controller, and input dispatch
in the source runtime world. They clone camera projection and pose. The main pawn
alone owns the global selection subscription. Shared transform-tool drags belong
to an exact pawn, viewport, camera, and world; foreign input cannot end that drag.
Deactivation and direct destruction both retire subscriptions, picks, and drag
ownership. Cross-window pointer drag, presented focus restoration, and full
play-mode state persistence remain open.

Offscreen world canvases use render-command publication claims and separate
producer and consumer receipts. Direct world UI uses the existing component
route unless the bounded OpenGL profile below admits an exact replacement.
Broader scene and shadow coverage remains open. An inactive canvas proxy
can filter frozen child layer and culling values. Exact indexed draw scopes match
native topology, index range/type, instances, renderer identity, and geometry
revision. Missing, repeated, failed, or mismatched native draws reject the scope.
The desktop OpenGL dispatch can withhold a swap and reports presentation only
after the actual swap succeeds. The bounded route connects selected-pass
manifests, packet pinning, and terminal-output proof. This does not establish
live platform acceptance or admission for other output profiles.

## Media and callback teardown

`UIListTransform.DetachVirtualProviderForOwnerTeardown` releases the exact old
provider and row leases without removing the child topology during destruction.
Retiring rows cannot be rebound by the same or another list until their cleanup
finishes. Callback failures do not skip later rows. A reentrant replacement
provider retains its own leases.

Native hierarchy, asset, animation, and inspector panels release their stored
event, row, and drop-target owners on direct destruction. Hierarchy deactivation
keeps the child topology until a later rebuild; activation requested during
cleanup waits for the Late tick. Drop disposal removes registry and hover owners
before user callbacks. Source retirement cancels its current drag immediately.

Hierarchy selection updates the shared virtual tree by stable ID, including
offscreen rows, without realizing row visuals. Unchanged selection does not
change provider revisions. A mapped foreign or destroyed node cannot acquire
the selected identity.

Toggle binding subscriptions carry their exact source and revision. Direct
destruction releases the binding and update tick. Stale delivery, replacement,
and deactivation during subscription cannot restore a retired owner. These
contracts do not establish that all editor subtree lifetimes are complete.

Video starts, open completions, and retries carry a lifecycle epoch. Teardown
detaches the exact session, audio sources, and GPU upload helper under the
producer gate. It resets owner state before external cancellation and close
callbacks. Old audio cleanup cannot change a replacement that claims the same
source. Each cleanup step runs even if another step throws. GPU helper disposal
runs on the render owner and retains the exact retired helper.

Viewport teardown removes collection, swap, and mesh callbacks. Property-text
drivers stop polling on deactivation and direct destruction.

Web replacement detaches the exact backend and upload buffers. Retirement waits
for producer callbacks to unwind, blocks reuse while native disposal is active,
and releases old upload buffers independently. Failed native retirement keeps a
strong quarantine claim and exposes a diagnostic. GPU web production requires
an unambiguous output owner. Screen canvases use their bound viewport output;
world canvases require a unique output. The Windows OpenGL backend saves and
restores both the native context and renderer scope. Its shared Ultralight driver
cannot move to a different context. Input queue entries carry the native view
lifetime and cannot enter a replacement view. These contracts require native
Windows/Ultralight validation; headless lifecycle tests do not establish native
resource correctness. Owner and producer failures remain visible through
`LastBackendFailure`, even when a prior valid web frame remains displayed. A
changed failure queues one owner-thread visual invalidation.

## Opt-in fixtures

Use the native editor in the unit-testing world. Set
`XRE_UI_CONTROL_GALLERY=1` for the common-control gallery. Set
`VirtualCollectionFixtureItemCount` to `100000` or `1000000` for the large-list
fixture; its default is `0`. The fixture reports realized rows, selected logical
ID, and UI work counters. It uses the production canvas and text paths.
Run the settings generator after changing the settings type. See the validation
record for exact procedures and platform limits.

Captured custom draws share unchanged private mesh and fixed buffer storage across
material changes. The existing draw budget charges each physical copy once and
also checks each draw's logical size. A failed native retirement keeps dependent
claims and budget in quarantine. Text uses committed glyph buffer revisions to
reuse unchanged storage without scanning its bytes.

Legacy text writes read the accepted immutable layout. They commit only the
changed used glyph spans. Paint and canvas transform changes do not copy glyphs.
`MarkGlyphTransformsChanged()` captures sparse animation values on the app thread.
Later direct dictionary edits require another call before a draw can use them.
Animation changes retain unchanged UV buffers and mesh storage. Known built-in
glyph snapshots contain only used elements. Reusable source buffers can retain
spare capacity; generic custom shader snapshots preserve their full storage.

## Sparse quad uploads and editor resources

Desktop quad cohorts retain stable payload slots. A dense draw-index buffer at
shader binding 5 preserves painter order while removed slots can be reused.
Changed contiguous payload spans upload independently. A full buffer rebuild
clears slot-initialization metadata with the storage, so later hole reuse writes
all fields. Historical cohorts retain their own slots and buffers until retirement.
This contract does not establish live GPU upload cost or independent static layers.

Editor panel and toolbar backgrounds use intrinsic solid colors. They no longer
request an implicit grab-pass blur. `UIBackdropBlurComponent` is disabled by
default. Its immutable render record carries a validated blur strength, rather
than a mutable grab texture. Compatible panels share one canvas-prefix capture;
each panel samples that capture with its own strength. This captures the scene
before the canvas, not UI that precedes an individual panel in painter order.

The supported capture path is an OpenGL screen canvas on a single-sample,
eight-bit default backbuffer. The renderer must prove the current canonical
full-output replacement, exact native program, raster state, context, window,
dispatch, and region. Visible `PreRender` UI, offscreen/world canvases, other
backends, and unproven output fail explicitly. A required capture or consumer
failure prevents window presentation and input publication. The feature does
not enable itself or change an editor default.

The process-wide capture pool has four slots and a 128 MiB limit. Slots remain
affine to their renderer and backend generation. Copy issuance does not release
the surface: the outer UI submission receipt and native completion retire its
writer claim. Unknown completion and failed native destruction retain their
budget and ownership. Required consumer runs are frozen before callbacks, and
each exact run must complete once. Unbind or disposal cannot erase this proof.
This managed contract still needs OpenGL visual, fence, and multiwindow checks.

Toolbar rebuild and teardown detach item callbacks and destroy owned child nodes.

Inspector asset drops finish on the scene-edit owner queue. Request identity,
property notifications, captured values, target lifetime, and world identity reject
stale loads. Multi-node component removal records inverse actions before callbacks
and verifies the whole target set after success and rollback. Partial rollback
retains the inverse action and reports the failure.

The native animation panel and ImGui panel share canonical key/member mutation
actions and Undo recording. The native panel uses paged path discovery, stable
key IDs, and a retained plot bounded to 512 segments. Full timeline interaction
and animation editor parity remain open. The profiler virtualizes the full source
row set instead of truncating it at 1,000 rows.

`UIRenderSurfacePool` owns up to four bounded leased color targets. Reservation and
GPU completion are separate generations. An offscreen canvas captures its target,
quad payload, pose, render data, and hit data in one immutable packet. The producer
receipt must retire with successful authoring and a signaled submission fence
before that surface becomes displayable. The newest completed packet can advance
while newer authoring is still pending. Completion belongs to one renderer and
backend generation; replacement requires recapture and new producer work.

The sampled quad has a separate output receipt. Window input uses its accepted
packet after presentation. A shared receipt probe freezes completion before a
pooled fence wrapper can be reused. Old packets retain surface and payload claims
until their holders and backend readers release them. These contracts do not
establish visual or GPU acceptance.

## Stable text slots and editor state

Painter order and GPU run identity are separate. Inserting an earlier compatible
run does not reassign all later GPU storage. Text instances have stable sparse
slots, and glyph indices refer to those slots. Adjacent dirty glyph spans combine
into bounded uploads; buffer growth writes the used high-water range. Active-run
lookup and retirement use reusable sets rather than nested run scans.

Eligible built-in canvas text uses shared batch resources. It creates private
mesh, material, and glyph buffers only when standalone rendering is required.
Custom and direct-world text retain their own route. Owned private resources
retire on the render owner after renderer claims release. Caller-supplied resources
and the borrowed font atlas remain with their owner. Failed retirement retains
the owned bundle and reports the failure.

Editor drag sessions capture the source interaction revision and stable identity.
Recycled rows cancel old drags before hover or drop dispatch. Play-mode selection
restoration belongs to `EditorPlayModeController`; each Inspector restores only
its own targets. Keep-changes mode preserves play-time state. Deferred focus
restoration checks the target and property identity and yields to new user focus.

Undo keeps an action on its source stack until replay succeeds. Reentrant replay
is rejected. A history revision prevents a callback from restoring an entry after
history was cleared or replaced. This does not roll back partially executed
multi-step actions; their mutation owner must provide that guarantee.

Ordinary scroll content uses positive X offsets to move content left and positive
Y offsets to move top-anchored content up. Extent measurement removes accepted
scroll movement and padding before computing limits. Virtual lists apply their
logical offset once. Context menus wait for accepted viewport size and provide
bounded scroll content. During owner deactivation, a menu removes its input and
placement callbacks without destroying children inside a parent world transfer.
Its normal Hide or next Show clears those owned nodes.

Each context menu row is a `UIContextMenuItemComponent` bound to the show that
created it. Up and Down move presented focus to the adjacent enabled row and wrap.
Home and End move focus to the first and last enabled rows. Separators and
disabled rows do not take focus. A row from an earlier show is released and
cannot click or move focus in a replacement menu. The show pins the component,
node, and transform lifetime. A row pins its own component, node, transform, and
parent node/transform lifetime and must still belong below that menu. Actions,
navigation targets, and submenu requests reject stale bindings. Navigation checks
again after focus callbacks. Submenu admission also checks the owner row and
each owner menu. Releasing a row clears its captured references. These checks
do not allocate per dispatch and do not replace presented-input admission.
Show copies the item array and
the row and separator heights, so callbacks during Show cannot change the rows
after the menu height is computed. When a callback moves the menu to another
canvas, placement waits for that canvas. When it leaves the menu without a canvas,
the menu closes.

`ContextMenuItem.Submenu` creates a row that opens a submenu and keeps a copy of
its items. A click on the row, or Right on the focused row, opens the submenu as a
sibling node of the menu, so the menu clip does not cut it. It opens beside the
row, on the left when the right side has no room. Right also focuses the first
enabled submenu row after the submenu is placed and presented. It uses the same
bounded focus request as ordinary navigation and retains the owner row's focus
intent. Left closes the submenu that contains the focused row and
returns focus to the row that opened it. Only the root menu subscribes to dismiss
input. Escape and Back close the innermost submenu first. A click inside any open
menu of the chain does not dismiss it. An action row in a submenu runs its action
and hides the root, which hides the whole chain. A replaced show or Hide destroys
the submenu node. Deactivation only hides the submenu, and the next open or Hide
destroys the node, so teardown does not change the hierarchy. A row from an
earlier show cannot open a submenu.

For ordinary menus, arrows, Home, and End request one intended enabled row.
The row stores its top-down content bounds from Show, including separator and
disabled-row spacing. `UIContextMenuComponent.Navigation` scrolls that rectangle
into the accepted viewport and waits for `TryFocusPresented` to accept the target.
It does not skip an unpresented target to focus another visible row. Repeated
keys advance the pending target. One request retries for at most 120 ticks and
yields to newer focus intent or a pending virtual focus request. It requires the
same focused source, show, row bindings, canvas, input source, and scroll owner.
Hide, replacement, deactivation, or ownership loss retires the request. A scroll
callback can replace it without the old continuation clearing the newer request.
The retry delegate is cached, and canvas input lookup uses indexed traversal.
Keyboard-opened submenus use that same request with their owner row as the
focused source. Hidden targets wait for placement without attempting focus.
Newer focus intent, another virtual request, or a retired input source cancels
the initial request. Mouse opening cancels a pending keyboard-focus request.
Deferred focus requires a nonzero `FocusIntentRevision` from the input adapter.

Show and Hide use `UIContextMenuComponent.Transitions` to serialize callback
reentry. One pending request keeps the latest intent. It captures the item array,
row sizes, and component/node/transform/parent identities. A stale owner rejects
the request. A request from old-content dismissal supersedes the outer Show
before it builds rows. A callback loop stops after 32 transitions and reports an
error. A failed content builder closes and releases its partial menu; cleanup
errors remain visible rather than being swallowed.

The menu records its content root, transform, and parent. Hide destroys that
captured root only while those identities and its attachment still match. It
does not sweep unrelated children or destroy content transferred to another
owner. Requests and content receipts are cleared on terminal component cleanup.
Full callback-safe construction/teardown, including failures and ownership
changes inside individual child construction/destruction callbacks, remains open.
Content-root attachment checks the exact root, transform, parent, and show after
SetParent callbacks return. A changed lifetime stops construction before root
configuration or a custom builder callback. Cleanup preserves the transferred root.
This contract does not establish native keyboard or IME acceptance.

## Native asset row actions

`AssetsPanel.Selection` listens to the shared table's SelectionChanged event.
Presented keyboard navigation updates the selected path and inspects a file.
Selecting a folder clears the asset inspector but does not open that folder;
activation opens it. Activating the same selected file does not start another
load. A scene selection clears the shared asset path, so later file activation
can restore asset inspection. Clearing the selected row clears the inspector only when it still owns that path.
Owner retirement detaches the selection callback.
After inspector callbacks, the panel checks its intent, lifetime, transform,
source revision, selected item, and path mapping before publishing AssetSelected.
This prevents an obsolete notification after a newer selection or owner change.

`AssetsPanel.Menu` uses the shared context menu for Open Folder or Inspect,
Rename, Reimport, and Open/Show in Explorer. `NativeAssetMenuRequest` captures a virtual row lease and
its provider/source revision, owner, canvas, input, and object lifetimes.
The panel opens the menu on its next owner tick, after the right-click event has
finished. An old dismissal handler therefore cannot dismiss the new menu during
the same dispatch. Row recycling, refresh, lost ownership, and input replacement
cancel the request. Actions recheck it before calling the existing panel paths.
Explorer actions recheck after selection callbacks, require a current file or
folder, and use the same EditorFileBrowser helper as ImGui. A folder opens in
Explorer; a file is selected there. This does not add file mutation or recovery.

Rename fills the existing name field and requests presented focus. Enter uses
`RenameSelection`; Escape cancels the draft. `NativeAssetRenameDraft` preserves an
unchanged path across row refresh. Submission checks its field and owner lifetimes,
current input owner, selected path, and current path mapping. Selecting another
asset, folder, source, or search clears the draft. A newer focus intent prevents
rename preparation from taking focus. Reimport retains the existing asynchronous import
behavior. Menu creation allocates only on user interaction or input-owner change;
the per-frame admission path does not create callback closures. Deactivation
retires menu input and defers canvas-child removal through the existing scene
destruction queue. Delete and type-specific ImGui window actions are not exposed.

## Composition boundaries and split handles

`UICompositionLayerComponent` assigns an explicit owner and kind to its subtree.
Nested owners replace the inherited owner. `UIPublication.CompositionSegments`
contains immutable contiguous painter spans. Separate spans do not merge across
an intervening owner. An unchanged span retains its identity and content revision
when another span changes or an earlier overlay is inserted. Changed sibling
output variants receive distinct revisions. Readers retain the parent publication
while using its segments and resources.

Text and material batching respect both owner and contiguous segment boundaries.
Vulkan can admit supported solid, border, bindful image, and text screen-space
chrome runs to its existing secondary command cache. The draw-local contract binds the canvas lifetime, segment identity
and revision, run identity, exact mesh and geometry revision, indexed draw shape,
and backend generation. The current receipt remains separate from reusable
identity. Native pipeline, descriptor, buffer, target, and cohort keys remain
required. Only the dynamic UI/text cache-policy bits can use this exception;
resource, debug, and output-sensitive restrictions still apply. Images retain a
private texture snapshot and exact UV storage. Text retains its private atlas,
five committed glyph buffers, and exact shader parameters. Cache hits check
current native resource authority again. Bindless images, custom draws, offscreen
output, and world UI remain dynamic. Each admitted run owns its retained
buffers, so unrelated overlays do not change those revisions. An in-flight
resource cohort can still require a new capture until its fence retires. GPU cache
acceptance is unverified. A count-preserving sparse capture replaces only changed
render pages, and a count-changing capture splices only touched pages. A complete
capture still compares the record set.

`UISplitterHandleComponent` uses accepted divider metrics from the layout kernel.
The metrics describe the split before child size constraints alter pane geometry.
Pointer capture holds that presented geometry. Target or configuration changes
cancel the old drag. Keyboard resizing uses the same limits. `NativeDockTreeHost`
uses this shared control.

Hierarchy panels carry their owning world into queued edits and Undo actions.
Deferred model and prefab drops recheck that owner on the scene-edit queue.
Streaming volumes retain the exact attachment host and world. Direct destruction
invalidates pending loads before detach, so late completion cannot attach to a
replacement owner.

## Deferred authoring admission

Watcher-triggered imports receive an opaque `AssetAuthoringTicket`. It binds the
captured project context, roots, filesystem provider, and provider generation.
A captured absent cache root remains absent. Named options moves, options deletes,
and generated-root publication recheck admission after provider callbacks and
before mutation. Options moves do not overwrite an existing destination.

A stale import preserves its recovery backup instead of applying rollback to a
replacement project. The diagnostic reports the retained backup location.
Generated closure writes and already-running import callbacks do not yet share
full transaction reservations. These guards do not establish crash recovery.


## Hidden panels and terminal callbacks

Inspector panels retain play-mode transition subscriptions while hidden. They
capture stable target identities and restore them when the view activates again.
The dock host assigns the exact window owner. A changed owner world or missing
target clears stale inspection state. Destruction removes the subscriptions.

Menus, tooltips, and combo boxes detach their input, timer, target, popup, and
binding owners during direct destruction. Callback reentry cannot reopen a
terminal control or restore its focus. Audio source teardown removes exact
listener/source pairs before native release. Admission revisions reject sources
created during a lifecycle change. Failed native releases remain retained for
diagnosis; teardown does not guess whether an uncertain native call can repeat.

The world-pass manifest supports closed conditional and Switch graphs,
bounded canvas packet claims, and a reserved owner-thread attempt. It rejects
changed command graphs and reentrant attempt reset. The bounded route below
connects native receipts and presentation. Exact direct-world clipping remains
open; clipped candidates are not admitted. Authored shadow behavior is unchanged.

## Canvas Collection And Scene Camera Ownership

`UIRenderableComponent` starts with 3D collection suppressed. It subscribes to
its exact canvas and UI ancestors when active. Screen, hidden, inactive, and
offscreen items stay outside 3D collection. Draw-space, parent, visibility,
activation, transform replacement, and canvas component changes restore valid
direct-world membership. This changes registration, not authored `IsVisible` or
`CastsShadows`. Destruction makes the subscription owner terminal. The direct
world draw path remains the existing path outside the explicit bounded profile.

Gallery chrome, FPS overlays, dock controls, and virtual text rows declare
composition owners. Gallery chrome uses `StaticChrome`; FPS and interactive dock
controls use `Overlay`; virtual text rows use `ScrollingContent`. These owners
preserve contiguous painter boundaries. They do not prove native command reuse
or complete isolation for every editor control.

The control composition fixture uses real text fields, caret and selection
overlays, hover buttons, and recycled list rows. It compares unchanged chrome
span identities, revisions, records, passes, and painter order across control
updates. This checks retained-record invalidation. Native command reuse and
measured GPU work remain separate contracts.

When topology changes the node count, the layout worker recomputes its output
instead of using cached branch skips. The output builder cannot seed pages of
a different length. Reusing an unchanged branch without those seeded values
would publish zero size and false visibility for that branch. Same-size input
updates retain the existing identity and revision checks for incremental reuse.

`UIVirtualTreeRowButtonComponent` adds keyboard and DPad navigation to provider
rows. A request carries the exact list, provider incarnation, stable item ID,
row lease, canvas lifetime, and focus scope. Input waits for a matching presented
hit record before moving focus. Provider replacement, recycling, modal changes,
or destruction cancel stale requests. Navigation handlers are cached per row.
The hierarchy accepts keyboard selection only during that exact focus dispatch;
a cancelled request cannot change a later pointer selection.

Hierarchy source bindings retain the exact node, transform child list, and scene
callbacks. Name, active state, and scene metadata changes refresh realized rows.
Transform replacement retires old listeners before topology is read again. Live
world roots and the existing editor scene are distinct sources. Rename sessions
retain their draft across row rebinds. `HierarchySceneOwnership` resolves the
authored scene first, then the exact world's editor scene; detached Undo checks
both. Guarded create, delete, reparent, and promotion commands preserve dual scene membership; broader hierarchy workflows remain open.

Direct component destruction releases radio membership, slider and color
bindings, text editing callbacks, and canvas input owners. Binding callbacks
carry a source revision, so queued delivery from an old source cannot update a
replacement. Teardown captures owners before callbacks and attempts the remaining
releases when one callback throws. Text fields queue only their own edit-overlay
nodes for destruction; ordinary deactivation keeps those nodes for reuse.

`UIWorldCollectPacketSet` bounds a world collection selection to 64 canvases and
four reusable slots. A generation-checked reader claim spans each collection
traversal. The last release marks a slot as retiring before it releases packet
claims; producers cannot reuse that slot during callback reentry. World close,
disposal, and swap use one ownership gate. The bounded OpenGL route connects
these claims to native output completion and accepted input promotion. Other
output profiles retain the existing route.

`SceneViewPanel` resolves a camera from its exact window and runtime world. It
rejects foreign cameras and callback replacement of an admitted binding.
`XRViewport.DetachSceneBinding` clears camera, world, and window owners before
notifications. It rejects callback assignments during detach and invalidates
pending property assignments. The old camera list uses exact non-vetoable
removal. Post-removal observers each run even if an earlier observer fails;
the first failure is reported after cleanup. Observer snapshots allocate only
on this ownership-retirement path. Normal collection removal remains vetoable.

## Retained Cell Refresh And Editor Ownership

Virtual table columns can provide `RefreshCell`. The provider uses it only for
the same source, item ID, list, provider incarnation, and row lease. A column
without this callback releases and binds its value again. Cell callbacks cannot
replace the row owner before the old callback returns. Pending transitions are
coalesced and bounded. Retirement attempts every bound cell; failed preparation
releases only cells whose binding began. A changed source or lease stops a
partial update. Property-grid columns use the same refresh path.

Inspector list details retain controls for unchanged rows. Occurrence IDs are
assigned only to visited rows, so opening a large collection does not create one
identity per item. Canonical insert/remove operations move these IDs and preserve
duplicate occurrences. Observed external changes retire stale identities. The
source cannot infer unobserved duplicate or remove/reinsert history from an
ordinary BCL list.

`EditorListMutation` supports exact `List<T>` and `ArrayList` Set/Insert/Remove,
and array Set. A non-overridable compiler-generated property getter and backing field
provide a callback-free final source check. Custom getters and other collection
types remain read-only. All targets, source fields, owner documents, before
sequences, and replay tokens are admitted before writes. Shared lists are edited
once. Value preparation cannot redirect a later target to new data. A structural
inverse is recorded before callbacks, and a replay hold lasts through history
commit. This does not suppress unrelated property recording. Dirty state uses
the captured source document. Post-commit UI notification failure does not turn a
completed mutation into a rejected command. Dictionary, linked-list, and generic
detail builders reject retired callback generations. Broader collection accessor
support and full inspector parity remain open.

Dock moves require a destination commit confirmation and unchanged source and
destination registration owners. Source closure affects only the admitted panel.
A changed callback owner rejects closure. Destination compensation restores only
its own unchanged tree revision. A panel can still be reopened in its source
window; panel types are not global singletons.

Detached saved-window records have optional client bounds. Old records keep their
default placement. Restore validates dimensions and current monitor work areas,
then keeps a usable title strip visible. An immutable approved-close receipt
binds one window, request sequence, and shutdown intent. A veto, failed native
request, nested request, stale queued callback, or replacement window cannot
reuse that approval. Shutdown preserves the saved entry; manual close removes
it. Native geometry is read on the native window owner. Restore, resize, and
close work executes immediately when already on the render owner.

The primary layout key is still based on the current window index and title.
Its [compatibility proposal](../../work/design/ui/native-window-layout-key-compatibility-design.md)
awaits approval. The explicit SDL/Vulkan split-pump prototype has a separate
terminal render-job handoff defect; see the
[investigation](../../work/investigations/ui/retained-ui-integration.md).
Default collapsed-window cleanup does not need that cross-thread handoff.

Grid destruction releases the exact row/column lists, sizing definitions and
values, and child-placement listeners. Renderable material listeners attach only
to the admitted active owner and detach on deactivation or direct destruction.
Reactivation binds the current material. Frozen draw payload claims keep their
separate reader lifetime; detaching the shared material event does not release
those readers.

The native editor root records the one subtree that it creates. Component
disable, rebuild, and destruction retire that subtree without removing foreign
siblings or a subtree transferred to a different parent during callbacks. A
construction callback that destroys or disables the owner stops later setup.
Console, profiler, browser, asset, and scene-view owners retire their external
subscriptions before callback-prone teardown. Dock cleanup attempts every old
drop registration even when one release fails. These code contracts do not
establish Windows, native media, or GPU retirement acceptance.

Hierarchy create, delete, reparent, and promotion use guarded placement receipts.
A receipt records exact parent, root-list identities and indices, loaded scene
memberships, runtime visibility routes, editor integration routes, local transform,
and active state. Hidden roots do not require a non-null live `World` merely to
be authorable. Moving a root updates the runtime's old scene route as well as the
authored lists; hiding the old scene cannot remove the moved root later.

A structural inverse exists before callback-bearing preparation. Ownership is
checked before inverse property writes and after the final state. Undo restores
placement before activation so a component binds to the restored world. Only the
operation-owned active step is structural; local transform and unrelated callback
properties remain canonical records. Exact property reconciliation occurs only
in the owning open scope. A setter or matrix callback that leaves completion
uncertain prevents history from transferring as success. The failure remains
visible and requires explicit recovery or reissue.

The shared hierarchy spawn path supports first-time editor-scene allocation and
hidden-scene parents. Native context/scene menus and single-root or grouped
Duplicate use the guarded command routes. Broader clone profiles and document-end
recovery remain open. Windows workflow acceptance is recorded separately.

## Accepted text measurements and editor navigation

`UITextComponent.AcceptedLayoutChanged` reports an immutable text result after
`UILayoutCanvasSession` has applied and committed the canvas output. It does not
report GPU presentation. Input still uses the window's presented publication.
The owner visits changed output pages, checks the exact text identity and committed
page, and invokes subscribers without an invocation-list allocation. An observer
cannot reenter the layout pump. Each callback rechecks its source; cancellation
or replacement stops delivery from the retired result. An unrelated size change
does not invalidate another text component's committed measurement.

`TryGetAcceptedLayoutResult` checks the cached page and current text inputs without
capturing another layout input. Inspector string details subscribe to their exact
text child, apply the accepted intrinsic height plus padding, and retire the
subscription with the detail owner. Initial height is provisional. This removes
the character-count estimate without adding per-frame measurement polling.
The shaping face identity includes face bytes, face index, em size, and sorted
immutable glyph metrics. Paragraph analysis and fallback-chain behavior remain
outside the installed single-run shaping contract.

Virtual table rows share the same exact lease admission as tree rows. Up, Down,
Home, End, and vertical DPad navigation use indexed stable IDs. A request can
realize a row, but selection changes only after that exact row is presented and
receives the pending focus request. Focused child editors retain their arrow
keys. Focused tabs route Ctrl+W through the shared close event and
Ctrl+Shift+PageUp/PageDown through the shared reorder event. Reused slots use their
current tab ID.

Table activation captures the item ID, binding epoch, source revision, exact
object/canvas/input ownership, and selection revision before it selects the row.
It emits Activated only if those checks still hold after selection callbacks.
It never rereads a recycled row's new ID as the target of the old click. A newer
selection, including a change away and back to the same ID, cancels the old
continuation. Selection visual callbacks cannot emit an obsolete outer selection
notification. `UIVirtualTableRowComponent` also rejects a pending IsSelected write
if a changing callback replaces its binding, so a recycled row keeps its own
selection visual.

A table row also records the source revision of its completed binding. Bind,
refresh, and unbind clear that record before source or cell callbacks run. Only
a successful cell, accessibility, selection-visual, and navigation update sets
it again. Activation rejects a partial binding and a changed source that has not
yet refreshed. These checks apply before and after selection callbacks.

## Hover callbacks and displayed pointer geometry

Hit collection records the accepted input revision. Before hover dispatch, a
new publication requires another query. Nested collection or dispatch cannot
modify the active transition lists. The dispatcher records a target before its
enter callback, so an exception or replacement generation cannot lose the
partially entered target's cleanup. Recycled targets cannot reach direct hover
or movement callbacks through an old record.

Focus callbacks can replace the packet during `RefreshPresentedInput`. The
outer read rechecks the packet, canvas, and output before resolving more focus
or returning its acquired claim. A stale read releases only its own claim.
Camera input resolves the current displayed hit and explicit blocker; it does
not compare a displayed target with live bounds from a pending layout. An owned
pointer drag also blocks camera input. `TopMostInteractable` remains the current
interactable result; a decorative modal blocker can block without one.
`TryGetPresentedPointerPosition` gives menu actions the same accepted canvas
point and rejects a replaced target.

Collection detail retirement invalidates the generation before unbinding rows.
Dictionary, linked-list, and generic builders own one local branch. Stale or
throwing source callbacks retire that branch through final cleanup. Retained
value editors also check the captured detail generation and source collection
before writes. This terminal admission does not replace their existing mutation
transactions; full canonical Undo coverage remains open.

Hierarchy context menus resolve auxiliary controls through the exact leased row.
A request captures its panel, world, source, row lease, input owner, and scene or
node before dismissal callbacks run. It rechecks those owners before opening and
before a queued command runs. The popup belongs to the canvas and uses the
accepted canvas point directly. Setup failure retires only the candidate node
and its handlers; it cannot clear a newer session. Node actions cover rename,
child creation, camera focus, and deletion. Scene actions use canonical visibility
and unload commands. Duplicate and Deep Duplicate use the owned single-root or
forest clone request described below. Unsupported sharing shapes remain explicit
follow-on work.


## Draft owners, tab focus, and linked-list edits

Multiline text uses Enter for a newline, Ctrl+Enter to submit, and Escape to
cancel a deferred draft. Escape during composition cancels composition first.
Submit, cancel, and blur capture the binding revision, property/targets, focus
epoch, target-element identities, and draft before callbacks. Property writes use
an event-time target snapshot and recheck the owner before each setter. A callback
cannot redirect the old draft to
a replacement owner. Replacing a focused binding adopts its current value and
starts a new draft baseline. Numeric drag submission also works without a
focused text editor.

Tab reorder preserves the stable tab ID through deferred presented focus.
`FocusIntentRevision` advances for explicit focus requests, including a request
for the current target. Automatic cleanup of a retired target does not advance
it. A later explicit or virtual focus request cancels tab restoration. Restoration
requires the exact accepted strip revision, canvas/input owner, and presented
target. Retries stop after 120 ticks. Nested `SetTabs` requests copy only the
reentrant request, coalesce to the latest one, and apply after the current writer.
The update rejects callback loops after 32 transitions.

Combo selection captures its intent before provider or binding callbacks. It
rechecks exact source, options, and binding owners before commit and notification.
A nested same-ID selection can cancel an outer pending selection. Context-menu
actions own one show revision; an old action cannot dismiss a newly opened menu.
Disabled menu actions also disable interaction and tab navigation.

`EditorLinkedMutation` supports exact BCL `LinkedList<T>` sources with supported
values and a nonvirtual compiler-generated property getter/backing field. Set,
AddLast, and Remove share one structural Undo receipt across admitted owners.
The receipt retains node identity, order, values, source fields, and source dirty
documents. Shared list aliases mutate once. Undo verifies every owner and exact
sequence before it writes. Removed nodes remain retained for replay; a node
attached to another list rejects that replay. History does not retain the
inspector view. Unsupported sources are read-only. Exact supported dictionaries use the adapter described below. Other generic
collection sources remain read-only until they have an ownership-safe adapter.

`XRObjectBase.ConstructOwned` supplies constructor-origin proof for synchronous
factories. It separates new allocations from later cache enrollment, retains
partial allocations on failure, and rolls back only exact cache entries from
that publication. Temporary object guards reject foreign-thread identity changes
and enrollment in another batch. Shared nested scopes remain valid; independent
nested factories keep separate origins. The guards end after completion or
rollback. Ordinary objects do not allocate a guard. An allocation ledger does
not authorize recursive scene-graph destruction after callbacks transfer owners;
clone adoption and graph retirement require their own checks.

The world source registry can register a fixed packet source outside the drawable
scene tree. Removal uses the recorded source registration. Unsupported output
profiles retain legacy child draws before replacement. The bounded OpenGL route
below requires the exact collected image, native target lineage, and accepted
presentation before it promotes input.


## Console, Profiler, And Dictionary Owners

`ConsoleRowCache` assigns row IDs to exact `LogEntry` references. A refresh keeps
unchanged strings and row sources. `Debug.CopyConsoleEntriesTo` copies a changed
image into caller-owned storage under the console lock. It captures repeat counts
with entry references. Unchanged revision, category, and filter skip both copying
and projection. Full-entry copy uses the same cached displayed text. Categories,
count text, clear commands, copy,
and auto-scroll share the existing virtual text panel. Delayed menu commands
retain their panel, category, and selected-entry owners. Clipboard failures are
explicit. New log entries are formatted once; this is not viewport-only log
formatting. The existing console source limits history to 5,000 entries.

`NativeProfilerProjection` builds a replacement preorder index in two reused
buffers. `ProfilerPanel` permits at most 2,048 index operations per tick. Node and
group visits, stack removal, and old-tail retirement use that budget. Readers
keep the last complete snapshot. Requests coalesce while a candidate is built.
Pause preserves the displayed snapshot and cancels candidate work. Later pumps
retire cancelled references within the same budget. Owner destruction clears the
remaining managed references. `NativeProfilerRows` formats requested rows in a
bounded 2,048-entry cache. Stable IDs belong to one exact published snapshot;
a new sample cannot retarget an old row ID. These limits bound index operations,
not elapsed time or engine snapshot acquisition.

Selected diagnostic views show component timings, BVH work, jobs, allocations,
main-thread invokes, FPS spikes, render counters, GPU timing, renderer state,
assets, frame lifecycle, outputs, and settings. CPU-root aggregation reuses two
buffers and caps distinct names at 4,096 with an explicit overflow row. Nested
child aggregation uses a separate 4,096-path cap and the same bounded pump. Spike
capture inspects at most 64 threads and 2,048 path nodes per frame and retains
256 groups. GPU history contains 1,200 summaries and 24 worst frames; the visible
tree shows 256 stages and offers a canonical dump. Output rows show 64 outputs
and an explicit remainder. Plots retain up to 720 samples, reduce them to 64 peak
bins, and use at most 128 segments.

Setting drafts capture exact effective, override, global, and override-leaf
owners. A replaced owner rejects commit. Worst-frame activation copies at most
256 immutable tree rows for the exact pipeline, history generation, and frame.
Formatting occurs on selection and the panel retains those strings. Historical
GPU rankings use three producer-maintained indexed heaps for worst frame, active
frame average, and total captured time. A completed frame publishes at most 256
ranked rows per metric. Native pages copy at most 64 rows for an exact history
generation and ranking revision, and report omitted paths. A changed revision
rejects an older page request. Existing canonical dumps keep their full details.

The native profiler exposes the canonical external-profiler action, the existing
UDP preference, and the configured update interval. Preference writes use the
same captured owner policy. CPU hierarchy selection keeps the worst sampled
thread-time snapshot in each completed 0.5-second window. The selector retains
only its candidate and displayed frame, resets on session changes and backward
clock observations, and follows the canonical boundary-frame rule. Stale roots and child paths retain their last values for the configured persistence
period. Same-frame reads do no index work until the next inactivity or expiry
deadline. Current roots have priority at capacity, and session or clock changes
reset carry state. All-root plots and detailed diagnostics remain open. Desktop test execution
and live Windows/GPU acceptance remain separate.

`EditorDictionaryMutation` supports exact BCL dictionaries with string keys and
the default or ordinal comparer, or integer keys and the default comparer. The
adapter requires a supported value type and an exact compiler-generated property
owner. Grouped Set, Add, and Remove use one Undo receipt. The receipt checks source,
key/value identity, owner, and dirty document before replay. Shared aliases mutate
once. Immutable key indexes avoid repeated linear searches. Unsupported dictionary
and generic collection sources are read-only; no reflective Clear/Add rewrite is
used. Read-only display callbacks are also checked after user `ToString` calls.

## Owned Clones And World Image Preparation

`SceneNodePrefabUtility.CloneHierarchyOwned` combines constructor origins with
pre-adoption guards and final structural validation. It verifies ownership again
after cache publication. A failed clone retains its allocation ledger and removes
only its exact cache entries. The original `CloneHierarchy` path is unchanged.
The clone API alone does not authorize recursive graph destruction. Editor
Duplicate adds document, lifetime, and history custody before activation.
`XRScene.SceneRoots` now tracks exact root occurrences and owner back-links.
The legacy serialized `RootNodes` list remains a copied serialization facade;
runtime mutations use the guarded collection. YAML, cooked, and envelope fixtures
preserve the existing format. Bounded clone retirement uses the ownership
ledger and retains unknown allocations or uncertain cleanup.

Child and component lists install `HierarchyListAuthority` in their constructor.
The authority cannot be replaced or removed. Silent, interface, indexed, range,
and mandatory-removal operations maintain exact occurrence back-links at storage
commit. Endpoint reservations span callbacks; storage locks do not span those
callbacks. Ordinary `EventList` instances keep their existing path.

Replacing `TransformBase.Children` copies the supplied list into new owned
storage. Old aliases lose write authority. Source-bound handlers and conditional
Parent/World updates reject an older continuation after a callback installs a
new owner. Runtime-root storage also records links before world binding, so a
failed bind cannot hide a retained root. Queued ownership remains visible in
snapshots until storage actually retires. Normal writes reject queued owners;
mandatory removal can release an exact terminal component when its owner is
queued and that destruction is later vetoed. These ownership facts do not
authorize destruction. `HierarchyStructuralReservation` only blocks new structural
mutations after admitted operations finish. Active clone commands also require
the lifetime and custody proofs described below.

`UndoHistoryResource` attaches a resource to its exact open structural step and
then to the committed history action. History pruning, cancellation, and clear
queue releases. Release callbacks run outside the history lock while replay is
held. Each dequeue checks for a new structural hold caused by an earlier
callback. A failed release keeps its recovery owner and requires an explicit
retry after repair. This mechanism does not itself authorize graph destruction.

World image preparation now records child command/source registrations and their
incarnations. Every normal or masked collection traversal stamps the exact world
and command-set generation. An unstamped traversal rejects replacement. Packet
claims move with the command image through swap; mutation invalidates prior mesh
order proof. A staged canvas replacement can validate one complete canonical
transparent-forward image. `XRViewport.EnableRetainedWorldUiColor` and
`UICanvasComponent.EnableRetainedWorldRendering` explicitly enable the bounded
OpenGL route. Both default to false. Admission requires one full-window mono
viewport, CPU-direct submission, one canonical canvas with authored
non-shadow-casting TransparentForward commands, and no other 3D commands,
screen UI, or late window output. The pipeline profile rejects VR,
AA, temporal/history features, bloom, motion blur, depth of field, occlusion,
atmosphere, fog, auto-exposure, lens distortion, and output debug overrides.

An ordinary frame first establishes native resource readiness. Retained attempts
then pin exact resource incarnations and verify the Deferred clear, Forward HDR
clear, light combine, UI draws, post-process outputs, and final BACK write.
Unscoped native clears, draws, or blits to pinned output attachments invalidate
the proof. A selected replacement cannot silently become a legacy frame after
collection. Failure prevents presentation and discards the receipt and pending
frame-view history. Renderer retirement also resets unsubmitted capture ownership. Input uses the accepted frozen camera descriptor
and region. This is an implemented bounded route, not live OpenGL acceptance.
Canonical direct-world solid, image, and text draws can carry a frozen nested
canvas crop. A private fragment variant transforms the interpolated model-local
point into captured canvas coordinates and discards points outside half-open
bounds. Empty crops discard all fragments. Nonfinite transforms and bounds
outside the exact supported float-integer range reject capture. Custom shader
pairs require a separate adapter. Ordinary shader files remain unchanged.

The OpenGL issue check verifies the actual crop, model, view, and projection
uniforms and exact linked shader inputs. This route requires the combined-program
mode; readiness rejects the global separable-program mode before collection
replacement. The same isolated GLSL variant and typed material parameters define
the Vulkan binding contract, but Vulkan world publication remains inactive.
Mixed scenes, Vulkan world output, separable OpenGL programs, and shadows remain
open. This does not establish native clipping acceptance.

The bounded Vulkan static-chrome cache also admits its supported border variant.
The contract includes exact UV/color and width buffer references, committed
revisions, native allocation generations, and descriptor resources. Existing
receipt and fence ownership protects pending commands. This does not establish
live Vulkan acceptance. The current OpenGL backend reuses retained geometry and
buffers, but still issues draw calls for each frame; it has no retained command
cache path.

## Animation Row And Edit Ownership

The native animation panel assigns monotonically increasing dynamic row IDs
outside its fixed command range. It presents member and key pages of 128 rows
and formats projected rows on demand. Playback updates do not rebuild an
unchanged member tree. Dirty clip changes still rebuild subscriptions and the
member index; incremental source capture remains open.

`AnimationClipAuthoringActions` captures the exact clip, source asset, member
route, child container, track order, and key payload needed by each edit.
Structural Undo retains a guarded inverse before callbacks run and installs a
terminal conflict guard before property steps can replay. Failed actions remove
only their restored steps. Unrelated callback edits remain in history. Unknown
conflicts preserve the newer state and retain guarded recovery history. Dirty
notification targets the captured source after settlement.

Built-in vector edits stage a complete authored handle and commit all fields
before one track notification. A throwing notification cannot leave the public
synchronization latch set. Exact typed track edits validate key identities,
order, membership, and times before committing links and values without
callbacks. One nonserialized edit generation advances before the final Changed
notification. Cached vector and lerpable playback cursors reselect after that
generation or their track owner changes. Remove, time, and frame Undo can restore
earlier equal-time keys in their original order.

Native commands include incoming values, direct interpolation choices, planar
Unify and Auto tangents, clip traversal, and clip kind. Auto captures exact
neighbor values and topology before it stages a complete handle edit. Presented
plot-pointer interaction, multi-child loop and length changes, and the remaining
authoring controls are open code items. These contracts do not establish Windows
editor execution or complete panel parity.

## Candidate Admission And Cold Object Lifetime

Publication capture stages painter membership in reusable scratch storage. It
commits the order and revision only with the accepted packet. A rejected capture
keeps the prior census. Recursive capture marks the outer operation stale, and
final admission repeats layout, canvas, world, and painter checks after command
construction. Canvas collection acquires its own packet claim under the
publication gate and releases it after all collection callbacks. Teardown can
remove holder claims during that interval without retiring the reader's resources.
This protects retries and collection lifetime; changed publication capture still scans the
full visible tree.

Cold object identity, generation, cache publication, and destruction calls take
a lifetime admission lease. Core structural reservations install with an atomic
compare/exchange and then recheck active leases. The hook does not wait on the
structural gate while a cache lock is held. Owned cache rollback admits each
exact unpublication independently, attempts the remaining entries after failure,
and retains rejected entries for recovery. Clone graph retirement and Duplicate
use additional custody proofs; this lease alone cannot authorize destruction.

Material parameter discovery excludes the exact-transparency pipeline globals
PpllMaxNodes, DepthPeelLayerIndex, DepthPeelEpsilon, and DepthPeelReversedDepth.
Their declarations can occur in resolved inactive snippet branches, but the
pipeline owns their uploads. Canonical image materials retain only their authored
MatColor parameter through discovery and resync; unrelated user uniforms remain
editable. The strict image batch parameter contract remains in effect.


## Independent committed changes and inspector previews

`UIChangeJournal` exposes a journal-bound committed cursor in addition to the
existing destructive layout drain. Publication readers copy whole committed
transactions from a fixed-capacity log. Open transactions return Busy. Separate
transaction staging coalesces identities without depending on unread layout
changes. Eviction removes complete transactions; an expired cursor requires full
resynchronization. Reading the current cursor does no log scan. Capture requires a committed cursor at entry and rechecks the exact journal
and cursor before and after packet construction. An open transaction cannot
publish a partial candidate. This API prepares sparse publication; the current
record capture still scans the full tree.

Virtual inspector string previews subscribe through `EditorPropertyAccess`.
Changes update the existing label on the scene edit queue. Multiple selected
values show an explicit mixed state. The preview stops at its first line and
limits text by complete text elements. Recycling and owner cleanup unsubscribe
the preview; a getter that retires the binding cannot publish stale text.


## Paged record claims and guarded authoring

`UIRecordStore<T>` groups records into leaves of at most 256 cells under a 32-way
immutable tree indexed by leaf order. Copy-on-write replacement copies one leaf and
its path. A splice reuses untouched leaves and builds new branches over the leaf
sequence. Composition ranges keep their own slice layout, so a range stays valid
when a splice moves its records; a page patch gives a range current pages when its
leaf layout differs from the store. A render leaf owns
its payload, texture, and atlas claims. Mapped publications share the root claim.
Constructor failure unwinds acquired claims; terminal release attempts all
claims and reports failures. Final cleanup closes admission before callbacks.
Render collection and input/focus readers use indexed store access. A collector
retains the store identity while it uses persistent runs. Collection epochs reject
callback reentry, and old claims retire after the outer collection stops writing.
Hit records use the same paged storage. Composition segments retain only the
record leaves that intersect their ordered range. Reused segments do not keep an
unrelated old store alive. Packets and collector sides hold separate segment-set
claims; construction failure unwinds prior claims, and release attempts every
owned claim. Internal collection reads the ranges directly. The public flat
record view is a lazy compatibility copy with a materialization counter. A canvas
publication finds a render membership by binary search in painter order; other
publications, such as output variants, keep a membership map. The collector walks
segments with a cursor in record order. A complete capture and composition
discovery still process the full record set. These changes do not establish lower
frame cost.

`OwnedHierarchyLifetime` retains a constructor ledger and attempts cleanup once.
It accepts an exact detached structural forest, rejects unsupported allocations
and public destruction overrides before effects, and reserves lifecycle and
structural ownership through callbacks. Private one-use permits perform exact
removals and synchronous canonical cleanup. A failed attempt keeps its state and
ledger; it does not retry uncertain destruction. The caller must retain the
receipt. `HierarchyCloneCustodyStore` reserves a strong entry before canonical
construction and keeps it through the exact structural history action. Detached
history release attempts one retirement; applied history release keeps a verified
document claim. Unknown allocations, failed cleanup, or a document that ends
without release retain the receipt and block new cloning. Exact owned-node
destruction snapshots are suppressed during retirement; unrelated callback edits
remain recordable. Single-root Duplicate is active through these guards.
Document-end cleanup and broader duplication profiles remain open.

Native loop edits capture the full member tree, unique child animations, source
owners, and prior values. The receipt checks prechange callbacks, commits all
loop fields before notifications, and guards Undo/Redo against changed ownership.
Length and stretch edits use exact receipts for supported built-in keyframed
and baked animations. It captures member aliases, source ownership, track order, key times,
authored frame indices, and payloads. All timing fields commit before callbacks.
Undo restores captured values and keeps playback clocks unchanged. Track
generations detect edits during notifications, while later replay checks exact
snapshots so an intervening edit and Undo do not invalidate earlier history.
Nested ordinary track-length edits still reach the canonical animation relay.
Baked and authored frame-cadence adapters retain exact sample storage and
reject unsafe interpolation bounds. Custom, shared-track, malformed, and
overflowing profiles reject before writes with a diagnostic; they do not receive
a scalar-only fallback.

Inspector enum dispatch precedes numeric type dispatch. Ordinary enum rows use
the shared combo; flags retain named text input. The row owner coalesces nested
bind and label requests, restores its popup after recycle, and retires old value
access before control detachment. A veto cannot leave the retired binding able
to edit its old property. Optional scalar write admission is checked after
getters and before each setter; completed writes retain their Undo and dirty
settlement before a later target is rejected. Arbitrary setters already entered
are not claimed cancellable.

Vulkan authoring can carry an exact world draw token into a pending mesh request.
The checked world capability remains disabled until native stage lineage,
submission, no-present failure settlement, and accepted input promotion are
complete. The accepted-plan seam binds the engine render-frame ID separately from the
Vulkan attempt ID. Required output checks its exact staged input tuple, native
proof, and submitted output before presentation. Rejected required output uses
a no-present settlement path. Input promotion uses the plan-owned tuple rather
than a mutable window field after presentation callbacks. Pooled proof ownership
survives a failed verdict; uncertain release quarantines the slot. Native recording
completion remains unreachable, so these fields do not enable Vulkan world publication.

At native issue, a world UI indexed draw resolves its pipeline again without
creation and requires the handle bound before uniform callbacks. It then binds that
pipeline and the witnessed descriptor sets with forced binds that ignore tracked
bind state. A mismatch fails the draw. World UI draws never use the non-indexed
fallback.


## Virtual profiler histories

CPU roots retain up to 720 per-call frequency samples by root name and scope kind.
Unobserved retained roots append zero; expired roots release their histories. A
virtual table cell owns one reusable plot and fixed scratch arrays. It draws at
most 31 line segments and clears the old series on recycle. GPU root cells use
the producer's bounded timing history and show milliseconds. Render Stats also
exposes GPU memory, FBO bandwidth, render-matrix swaps, and CPU spatial-tree
counters. Resource event counts and the top 32 detail rows publish under one
lock with an epoch and an omitted-key count. Frame publication uses fixed row
buffers; the native panel copies into fixed scratch storage. Shadow diagnostics
include request formats, solve failures, demotions, reuse, page allocation, and
group placement. Native runtime acceptance and detailed Vulkan output
diagnostics remain open.

## Frozen animation plot input

An interactable can attach immutable managed metadata to its accepted pointer
geometry through `IUIPointerMetadataSource`. Hit packets retain that value with
their geometry. A source returns no metadata while its build, source, or size is
inconsistent, and records a hit invalidation when it replaces the value. The
metadata contract does not transfer native resource ownership. Changed plots
allocate bounded immutable snapshots so held packets cannot observe later edits;
idle capture reuses them. Allocation and latency measurements remain required.

Animation plots capture bounded lane and key targets, time bounds, exact panel
and input owners, and source, view, and build revisions. Clicks select the frozen
lane or key; a blank double-click uses the canonical key authoring action. A pan
keeps the pressed coordinates through its own new frames, while an unrelated
view change or resize rejects it. The mouse wheel zooms the time axis about the
time under the presented pointer (`AnimationPlotWheelZoom`): each step scales zoom
by 1.25 within 1 to 32, and the window clamps to the clip. The wheel needs current
plot metadata and does nothing during a press. Input dispatch generations reject
owner changes that return to the same object during a callback.

The bottom 30 pixels of the plot are the time axis strip (`AnimationPlotFrameAxis`).
A press in the strip that does not hit a key scrubs instead of panning. The press
and each move seek the selected matching animation component to the time under
the pointer, snapped to the nearest whole frame of the sample rate frozen in the
snapshot and clamped to the clip end. A scrub starts only on the current
published plot and continues while the view and size stay the same. Wheel zoom
does nothing during a scrub. A snapshot with another sample rate is not current.

Plot builds hide the visual root inside a canvas change transaction before they
write strokes. A completed exact build can restore visibility and publish its
metadata. Aborted builds stay hidden. Stroke writes check owner validity after
callback boundaries, and nested build requests coalesce into a bounded retry.
Method details limit argument rows and text length; they do not call arbitrary
argument `ToString` implementations. Source subscriptions change only when their
topology changes. Channel editing and built-in baked/frame-cadence length
adapters use the guarded routes below. Native runtime acceptance remains open.


### Composition page claims and drop admission

`UICompositionSegmentSet` owns paged segment slots. A local render-page replacement
copies only the affected slot page and record-leaf claim page. Segment ranges use
their actual source offsets. A full rebuild rebases moved ranges before later
local edits. Failed ownership transfer releases only the claims it owns. This is
a storage contract; full canvas capture and segment discovery still remain on
the fallback path until sparse packet capture is integrated.

Hit-order and transform invalidation survive a property observer that throws.
Scale changes mark local and world matrices dirty before observers run. Matrix
values still change through the normal matrix update. Publication rejects the
canvas while its layout is invalid.

Inspector reference drops retain an exact row token, provider revision, target
array, and object lifetime. Loaded asset identity is captured before completion
is queued. A retired row or changed asset lifetime cannot acquire a later write.
`InspectorAssetDropRequest` observes reference changes through the whole admitted
setter, Undo, and dirty-state settlement. It allows one exact same-thread engine
notification for that setter. Another relevant change invalidates the remaining
writes, even if the value returns to its original reference. A completed setter
keeps its Undo record. Non-notifying targets are rechecked at getter boundaries;
this does not provide a lock against concurrent application writers.

### Profiler output pages and animation channels

The native profiler copies one bounded output page under the frame-manifest lock.
The copy returns its publication revision, total count, and clamped page index.
All output pages remain reachable. Pause takes one immutable full snapshot so
page changes stay on the selected frame; resume returns to bounded page copies.
Vulkan stage and wait rows use one correlated telemetry publication. Separate
render-stat counters are labeled with their separate sampling cadence.

Native animation channel edits reconstruct the selected float, vector, or
quaternion component and use the existing authoring Undo actions. Value-field
refresh pins the authoring epoch and denies submission during refresh or while
text settlement is pending. Built-in baked length edits retain the existing
sample store and authored cadence. They reject timing bounds that could address
outside that store. These edits do not resample or rebake an animation.


### Vulkan compiled program and descriptor witnesses

The indexed world-UI preparation retains the exact compiled shader artifacts and
shared program interface through draw issue. It captures each native descriptor
set publication and its dynamic offsets at binding. After callback-bearing
preparation, the selected proof path revalidates those publications and forces
the exact native binds immediately before the indexed draw. Ordinary descriptor
deduplication remains unchanged.

This witness does not prove uniform bytes, sampled image identity, pipeline
identity, intervening writers, or final presentation lineage. Checked Vulkan
world-UI output remains disabled until all required proofs are complete.


### Native duplicate command admission

Native hierarchy menus expose Duplicate and Deep Duplicate through the canonical
clone route for one or more selected top-level roots. Duplicate preserves supported direct asset references;
unknown containers and callback-defined getters report that ordinary duplication
is unavailable. Deep Duplicate retains the canonical serialized asset behavior.
Multiple roots use one owned forest transaction with ordered replay and reverse
compensation. The grouped transaction contract is described below.

The request pins its source graph, document placement, asset slots, and object
lifetimes before construction. It admits execution once. Reciprocal parent links,
structural-list lifetimes, descendant worlds, and hidden scene-root membership
must agree at command boundaries. Command replay checks the original clone graph
shape; history release uses current document custody so a valid later edit does
not cause false recovery. Failed compensation retains its custody receipt and
diagnostic instead of destroying a graph whose ownership changed.

`HierarchyMutationOperation.RestoreInitial` repairs a partial write that the edit
made itself before its apply completed. After the apply completes, a placement,
activation, or local transform value that differs from the applied result came
from a callback. Rollback keeps that newer edit, reports `PreservedNewerEdit`, and
records the remaining state as the step's result; the clone stays in recovery
custody instead of being retired.

Selection restoration applies only while the action still owns the exact ordered
selection. A callback or later user selection takes priority. Selection callbacks
are followed by graph and placement checks before the command settles.

## Guarded Component Construction

The internal SceneNode.AddComponentGuarded path retains a
RuntimeComponentCreationReceipt before the component base constructor binds its
node. The receipt captures exact node, transform, parent, component-list, world,
and component identity. It checks callback boundaries, the final Name and World
writes, and structural-list insertion. List commit checks use intrinsic ownership
only; they do not invoke the caller's validator while the list lock is held.
Required transforms and component dependencies must already exist. The public
component factory keeps its existing behavior.

A failed attempt retains its original exception and claimed component. Only a
failed component that never entered a structural list can use guarded constructor
abort. A vetoed insertion is such a case. A component that entered a list, was
later detached, changed lifetime, or acquired a foreign list owner cannot use
that abort. Cleanup checks ownership after callbacks before it commits destruction.
It retains cleanup failure. Once cleanup starts, the receipt prevents a second
attempt. An admission failure before cleanup starts can retry only while the
same intrinsic ownership remains valid.

This is a construction prerequisite. Context-menu child construction and full
content retirement do not yet use it. It does not authorize arbitrary callback
statements, destruction of borrowed objects, or rollback of inserted partials.
Those paths need explicit content ownership and canonical retirement.

## Initial Detached Leaf Retirement

DetachedLeafRetirementReceipt records a node, transform, and their exact storage
before guarded component creation starts. Each component attempt must register
before its factory runs. Retirement uses the original claims and a frozen object
set. It accepts only a leaf that was never structurally adopted, has no current
world context or hierarchy store in its original null-world mode, and contains
only registered components. Prior
parent, scene-root, or runtime-root adoption permanently rejects this initial-leaf
path, including after detach. A direct world-context change is not itself proof
of structural adoption; all current detached-state checks still apply.

The receipt supplies a separate input to OwnedHierarchyRetirement. It reuses the
existing reservation, permits, and canonical cleanup. Constructor-ledger clone
admission stays strict. Component-owned resources remain the component's cleanup
responsibility. Canonical cleanup leaves empty non-cache list containers alive
and retires their mutation authority. Partial cleanup retains failure and exact
executor progress without replay. A Destroyed observer can throw after destruction
commits; that completed destruction remains recorded while the receipt retains
its failure. Completed guarded aborts are omitted only with their completion
proof. Incomplete guarded aborts prevent another cleanup attempt.

Guarded creation holds a node and transform mutation lease before factory code
runs. Reserved retirement therefore rejects a new factory before it allocates.
Guarded abort prepares subscriptions only after lifetime admission succeeds.
This adapter is not yet the live-menu retirement path. Parent detachment, permitted
ownership transitions, early owner-node teardown, and arbitrary custom content
registration remain separate integration work.

Menu setup checks for newer queued Show/Hide requests at construction boundaries.
An obsolete setup returns to the existing transition loop before invoking its old
content builder. Ownership errors and builder exceptions still propagate; a queued
request does not hide them. These cancellation checks do not replace individual
child ownership checks or guarded retirement.


### Exact Source-World Removal

The explicit world-bound receipt captures its source world and transform store
before guarded component factories run. It accepts the same never-adopted leaf
shape. The original null-world receipt and clone cleanup still require null worlds
and detached transform storage.

The executor retains one reservation across lifecycle stop, world removal, and
canonical destruction. It stops lifecycle callbacks while the old world remains
available. Each callback step records entry and completion; an interrupted step
cannot replay. Inactive uninserted component claims can be retained, but active
uninserted components cannot enter this path.

A one-use permit admits only the captured world-to-null write. It preserves
changing/changed notifications, physics mutation scopes, tick cleanup, and actual
transform-store removal. Only engine-owned recursive propagation is suppressed
for that exact permit. The executor advances expected state at each field/store
commit and retains partial failures. A rejected nested setter cannot dispose the
outer permit. No parent or root removal is implied by world removal.

## Float Animation Inspector Details

The retained PropAnimFloat property detail uses the shared virtual table and a
97-sample retained plot. It exposes key selection, time/value editing, insertion,
removal, In/Out interpolation modes, length changes, and stretch. Sampling does not seek playback. Rows show
at most 8,192 keys and report that limit. The plot and row data refresh on authored
changes; playback clock notifications do not rebuild them.

InspectorFloatCurveSource retains each outer owner, the exact curve and track,
and source documents. Owner, curve, and document proofs include their admitted
lifetime revisions, so restoring an old GUID does not revive a stale binding. Compiler-owned properties and the explicit transition blend
curve accessor provide callback-free reference checks. Idle refresh checks do not
resolve scene documents. Commands and history validate complete document ownership.
Transition curves require an explicit path from the selected state machine. The
Layers detail shows a virtual list for layers, states (including AnyState), and
transitions, then embeds the shared curve detail. Each level reports its 8,192-row
limit. Row IDs remain stable for each object occurrence within the current level.
The path retains exact list references, positions, counts, construction identities,
and the state-machine source document. Commands also reject duplicate membership.
It does not depend on runtime owner back-references or initialization. Bare
transition properties cannot establish document ownership and remain unavailable.
Selected targets that share the same curve edit it once. Distinct curves support
Set length and Stretch length as one transaction; their key controls remain
unavailable. Unsupported getter ownership rejects the whole selection.

AnimationAuthoringTransaction supplies the existing clip and new property paths
with the same reservation, exact Undo/Redo, rollback, conflict, and dirty-document
behavior. Exact key edits preserve frame indices and handles on reversal. Length
changes reuse AnimationLengthEdit. UI retirement rejects new commands; it does not
invalidate a still-owned history operation. Source replacement detaches authored
change subscriptions and rejects stale commands and history.

The state-machine navigator coalesces active-list changes. Idle checks compare
identities and forward references without allocating. Container replacement or
regeneration rejects old rows and curve actions. Closing the detail releases its
subscriptions; still-owned Undo operations retain their source path. Detail
construction stops after retirement at component, child, and property boundaries.
Provider cleanup requires the captured list and parent chain. These checks do not
replace the remaining general compound-construction ownership contract.

Menu construction also checks queued requests after row, label, arrow, separator,
and virtual-scroll helper calls. A queued replacement prevents later helper calls
and event wiring. Exceptions from the current callback retain their original
identity. Property commits inside those helpers and full owned-child retirement
remain separate open work.


Float key mode controls cycle through Step, Linear, Smooth, and Hermite. They use
a separate compact row and disable when the selected key or its authored state
is unavailable. Value and mode changes share the same staged authored-state
transaction. Their ownership predicates also retain key time and authored frame;
a callback that changes either value causes a guarded conflict rather than stale
Undo history. Mode changes preserve values, tangents, and synchronization flags.
Sampling does not seek playback.

Selected float keys also expose incoming value, outgoing value, both tangents,
and the three synchronization flags. Detached staged setters compute the full
handle state, including the existing unification effects. The transaction applies
that complete state and reverses it exactly. Nonfinite input or computed handles
are rejected before a live-key mutation. Compact fields and toggle rows refresh
only when dirty and disable for unavailable selections. Hermite sides also expose
weighted-mode toggles and normalized weight inputs. Weight inputs require the
corresponding toggle and accept only finite values from zero to one. Changing a
mode does not discard its stored weights.

The internal authored-state snapshot includes scalar weights even when captured
through `VectorKeyframe<float>`. Clip and inspector edits restore those weights
into detached staging keys. Exact commits validate the complete prior and desired
state, write all handle fields, then publish one track change. Non-scalar keys
accept only neutral weight state. Serialization fields and interpolation math are
unchanged.

Public scalar weight setters publish a track change for each accepted write.
Nested writes publish their own accepted values. If a property observer throws
after a changed value is stored, the setter conservatively invalidates the track
and preserves the original exception. Secondary notification or diagnostic
failures cannot replace that exception. This recovery can notify more than once
under nested callbacks; it does not use the exact-commit notification guarantee.

## Child Destruction Dispatch

Ordinary SceneNode teardown snapshots each child node and transform with their
construction identities. Before each Destroy(true), it checks the original
parent transform and child-list identities, reciprocal node/transform links, and
exact current list membership. It stops consuming the snapshot if source storage
changes. A moved, regenerated, or replaced child is skipped. Duplicate snapshot
references do not cause repeated dispatch. Queued children still use immediate
canonical destruction. Vetoes and exceptions retain their existing behavior.

This check protects later children from earlier child callbacks. Native menu
Hide reaches it through content-root destruction; released transferred rows keep
their descendants and no active binding to the old menu. The reserved
OwnedHierarchyRetirement route is unchanged. Snapshot allocations occur only
during destruction. This is not concurrent mutation admission and does not guard
transfer inside the selected child's own Destroying callback, away-and-back
history, or TransformBase's separate detach loop. Full menu ownership remains
open.


## Hierarchy Model Adoption

The native hierarchy uses the live scene/world model.
`HierarchyPanel.VirtualTree.RebuildVirtualTree`, `AppendSourceScene`, and
`AppendSourceNode` populate scene sections, editor scenes, and unassigned roots.
`UIVirtualTreeProvider` reuses the controls from `CreateHierarchyRow` and
`BindHierarchyRow`. Source subscriptions update names, visibility, active state,
and dirty markers. Modifier selection, menus, and rename use the panel's shared
selection and mutation paths.

`HandleVirtualNodeDrop` handles scene-node targets. Root and node asset drops use
the existing `TryHandleDroppedSpawnableAsset` path for prefab and model sources,
including hidden-scene parents. This completes the source-level hierarchy adoption
contract. It does not establish Windows interaction or model-drop acceptance.
Grouped multi-root duplication uses the separate transaction described below.
General control-lifetime work remains open.


## Grouped Hierarchy Duplication

The native hierarchy duplicate request captures the ordered top-level selection,
source graph identities, document placements, names, and ordinary asset-sharing
plans before construction. It rejects an unsupported ordinary profile for any
member before allocating a clone. Deep duplication keeps canonical per-source
cloning. Planned names remain unique in overlapping destinations. Callback
changes to a source name or an occupied planned name reject the request.

`SceneNodePrefabUtility.CloneHierarchiesOwned` constructs all roots in one owned
ledger. It snapshots source references before callbacks and serializes each YAML
source separately before allocating. AOT mode uses the registered clone handlers.
Temporary structural reservations protect completed peer roots during later
construction, configuration, generation, and initialization. The reservation
covers their nodes, transforms, and components, including linked structural-list
lifetimes. A forest graph checks disjoint ownership and exact links. Reservations
close before cache publication; final structural validation and the existing
publication identity guard precede sealed ownership transfer. Returned roots are
an ordered read-only view over private storage.

The forest path preserves the existing current-root identity semantics. It does
not reject all same-target reentrant Generate calls or prove that no temporary
structural change occurred inside publication callbacks. Peer reservations also
do not freeze ordinary property values. Editor mutation checks cover the fields
owned by the command after construction.

`HierarchyCloneCustody` retains one forest receipt, lifetime, selection owner,
and history resource. Preparation captures all detached placements and local
values before deactivating any clone. `HierarchyMutationBatchOperation` applies
members forward and compensates or undoes started members in reverse. It checks
peer placement, activation, and local-value expectations at callback boundaries.
Scene-list changes check peers before and after each collection mutation. Failed
or mixed compensation keeps the whole receipt in recovery; it never retires a
detached subset of the group. Failed compensation marks captured scene/document
targets through their current authority, independently of the rejected clone
values. Changed or replaced document targets are rejected, and secondary dirty
failures cannot replace the original command failure.

`HierarchyBatchReplayExpectation` advances only the admitted Transform local key
through Undo/Redo property replay. It checks all peers before the setter and
checks the requested state after it. Custom transform matrices remain structural
inverse work. A callback that changes another member cannot become the next
baseline. Selection changes once after complete placement, and newer selection
wins. History release uses existing whole-ledger document custody or strict
forest retirement. Unknown asset allocations and document-end recovery keep their
existing restrictions. The single-node command uses the same history runner and
keeps its existing clone entry point.


## Deferred Destruction Request Ownership

Deferred destruction retains an exact request token, separate from the object's
lifetime revision. Generate cancels the current request. A stale queue entry
cannot dispatch into the regenerated lifetime or consume a later request. The
pending count describes current requests, not stale physical queue entries. A
request queued during direct destruction is discarded without virtual dispatch
if that exact object is already destroyed when the dispatcher claims it.

Queue dispatch claims an idle object without waiting. That claim excludes
concurrent lifetime writers through the virtual Destroy call. The canonical base
entry consumes the request before callbacks. A normal override return that defers
teardown retains the request for a later drain. Recursive drains do not run a
second dispatcher, and one deferred override cannot spin in the same drain. A
throw after virtual dispatch starts cancels that exact request without automatic
retry. Vetoes retain their existing alive, unqueued result. No callback runs
under a newly introduced object lock.

Component list installation and final insertion reject queued, destroyed, and
actively destroying components. An ordinary prequeue detach/reattach remains
supported. This closes owner-thread callback transfers during teardown; it does
not establish general concurrent scene-list insertion safety. Custom override
effects before canonical direct destruction remain the override's responsibility.
The broader menu construction and adopted-content retirement work remains open.


## Selected Float Curve Lengths

`InspectorFloatCurveLengthSelection` retains every selected owner and deduplicates
exact curve references. It combines the existing source documents and reservation
owners. Capture limits the selection and total key count to 100,000. Idle checks
use stored references without allocation. Shared curves mutate once; unchanged
members still participate in ownership and conflict checks.

The batch length action captures one `AnimationLengthEdit` per distinct curve.
It validates every member before raw timing writes, commits all members before
notification, then checks every member at each notification boundary. One
`AnimationAuthoringTransaction` retains all documents, owners, tracks, and keys.
Undo restores each curve's own length, key times, and frame indices. Callback edits
to peers reject later owned notifications and retain guarded conflict history.
Playback time does not change. Dirty marking checks each captured document
lifetime independently. A callback can invalidate a later document without
preventing dirty marks for other valid originals. These checks do not acquire
concurrent lifetime admission.

The retained detail enables length input and length actions for a distinct-curve
selection. Mixed lengths display an empty input. Key and plot editing stay
unavailable. The length control retains the displayed selection and revision;
replacement, invalidated presentation, or retirement rejects stale submissions.
Subscriptions attach once per distinct curve and track and detach on rebind.
This does not establish live multi-selection or Windows Undo acceptance.

## Native Dock Pointer Transfer

Native dock drags use an optional `IRuntimeDesktopDragPointerSource`. Ordinary
editor drags keep their existing input route. The Windows backend observes normal
mouse capture; it does not lock the cursor or take capture from another owner.
Each primary press gets a sequence. A release receipt retains that sequence,
the message point, the exact target HWND, and the target client origin and size.
Polling can update hover or cancel a press. It cannot authorize a drop. A later
press cannot replace the release point of the current editor session.

The observer decodes signed `WM_LBUTTONUP` coordinates and calls `ClientToScreen`
before downstream message callbacks. Native queries run on the window owner
thread. Complete value snapshots cross threads under the existing backend lock.
Capture loss, cancellation, shutdown, and unavailable target geometry reject the
transfer. Failed subclass removal retains the callback until native destruction.
The observer uses the same lifetime pattern as the existing IME observer.
See Microsoft's [release-message contract](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-lbuttonup),
[capture query](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcapture),
and [coordinate conversion](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clienttoscreen).

`WindowFromPoint` excludes disabled windows. The observer also checks the first
visible top-level rectangle at the point and rejects ambiguous coverage. Unknown
child windows and overlays do not map to an underlying registered editor window.
This conservative check can reject a valid drop; Windows acceptance must cover
overlapping windows and mixed DPI. See the [target-query contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint).

The editor captures eligible window, backend, root, and registration lifetimes
when a drag starts. Release selects that exact destination and stack, rather than
the first other window. The target client geometry must still match the release
receipt. A disabled, hidden-from-accessibility button supplies a tab-strip hit
record without receiving ordinary pointer input. `TryGetPresentedDropRegion`
uses one displayed publication for mapping, shape, nested clip, active focus
scope, and blockers. Full-client output must match the sampled native extent.
The query checks the actual presented latch after focus callbacks and does not
convert replaced transforms. Missing or stale geometry has no live-layout fallback.

The editor removes the drag session before terminal callbacks and checks its
source and release receipt again before dispatch. The destination receives the
panel through the explicit stack route. Source closure follows a confirmed
destination commit. Callback-owned replacements must survive source slot cleanup.
The exact old slot stays registered during destruction. A veto or exception keeps
the live entry; successful destruction removes only that entry. Active destruction
admission prevents recursive slot cleanup through host disposal. Transfer is
receive-first, not an atomic transaction across both windows. If a callback changes
the source after delivery, the destination can retain its accepted panel while the
source remains unchanged. Rollback never overwrites a replaced root or registration
lifetime.
Unsupported desktop backends retain same-canvas tab movement. Saved window-key
migration and live multi-window acceptance remain separate requirements.

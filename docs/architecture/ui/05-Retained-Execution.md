# Retained UI Execution

The native UI uses the existing canvas, material, and `UITextComponent` renderer.
It does not introduce an immediate-mode UI renderer. ImGui remains the default
editor. Use the [implementation TODO](../../work/todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
and [validation record](../../work/testing/ui/native-ui-validation.md) for open
work and acceptance results.

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

Measure reuse requires matching identity, aggregate measure revision, text
inputs, and constraint. Arrange reuse also requires matching child edges,
allocated slot, placement, visibility, and geometry. Topology-stable capture
reuses numeric descriptors. Capture and result validation still inspect the
retained target list; this is not a claim of constant-time capture for a leaf edit.

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

Text rendering retains the accepted result instead of copying a mutable glyph
list each frame. Color, transform, outline, and bounds are text-instance values.
A glyph payload change is separate from an instance-only change. Fonts own copies
of authored glyph maps. Reassign an edited map to publish a new metric revision.
Unchanged captures reuse the immutable metric page. Full shaping, bidirectional text,
and fallback-font behavior are not established by the basic rune-layout kernel.

## Owned custom draws

Unbatched records use `UIDrawPayload` with the existing mesh command path.
Capture copies mesh buffers, mutable triangle indices, shader source and
frontend identity, numeric parameters and arrays, and nested raster state.
Deferred commands do not retain source callbacks. The built-in text adapter
copies the accepted layout and owner-captured animated glyph adjustments into
private transform, UV, and rotation buffers. Required resource publishers defer
unready descriptors instead of drawing with a placeholder.

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

Publication claims currently retire through finalizers when their holders become
unreachable. This prevents early reuse but does not guarantee timely reuse. A
four-surface video, web, or viewport pool can stall until garbage collection and
render-owner cleanup retire old packets. Deterministic holder and reader leases
remain open work. Do not interpret the bounded pool as a frame-latency guarantee.

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
the captured ancestor ranges. Focus changes remove the previous target's input
registrations before attaching the next target. Re-entrant focus callbacks settle
on the latest request, and canvas deactivation removes its input mappings.

Hit transitions use reusable lists and identity sets. Idle queries reuse prior
hits when pointer coordinates, output mapping result, hit generation, and capture
state agree. `RenderInfo2D.MembershipId` provides stable membership independent of
mutable painter order.

The output receipt integration still requires graphics validation. CPU command
completion alone does not prove that deferred GPU draws reached an output.
Do not infer atomic rendering, Vulkan replay correctness, or visual parity from
headless packet tests.

## Batch storage

`UIBatchCollector` distinguishes owned solid, border, image, and text semantics.
Unknown custom shaders retain normal draw commands. Common editor borders use
`SetBatchBorderStyle`; solid backgrounds use `SetBatchSolidColor`. Per-control
outline values do not require a new shader source.

Painter runs retain pass and order boundaries. GPU cohorts isolate storage still
in use by previous draws. Reuse requires the backend completion and buffer reuse
checks. A failed submission is reusable only when the renderer proves that no
native submission occurred and the buffers are free. Unknown failures keep their
storage unavailable. The cohort count is bounded; inactive cohorts retire only
with completion evidence.

Unchanged render storage can reuse collected runs. Material fields use coalesced
dirty upload ranges. Text instance edits leave glyph buffers unchanged. Stable
glyph ranges retain unchanged runs when another run changes length; an index
stream selects each run without moving unrelated glyph payloads. These mechanisms
do not establish complete sparse capture,
independent composition layers, or a measured performance advantage over ImGui.

Set `XRE_UI_BATCH_DIAGNOSTICS=1` to enable text and batch diagnostic logging.
Leave it unset for ordinary workloads.

## Controls and virtual data

`IUIValueBinding<T>` defines typed retained values. Controls subscribe while
active and unsubscribe on deactivation or row recycling. Editor adapters cache
property access, marshal source notifications to the scene owner, and group
mutations through `Undo`. Numeric fields retain an invalid draft until valid
commit or cancellation. Text fields provide selection, grapheme navigation,
undo/redo, and composition state.

The desktop IME adapter uses existing Windows IMM32 APIs. Composition events carry
an immutable focus identity and focus epoch. A separate interaction revision
rejects recycled or disabled targets. Candidate placement uses
accepted caret geometry. Other hosts report unsupported IME capability. This
boundary does not imply tested Windows IME behavior or a Linux IME implementation.

`IVirtualListProvider` supplies item count, stable IDs, and row binding.
`UIListTransform` realizes the viewport plus overscan and a bounded reusable row
pool. Source extent uses count and fixed row height. Leases distinguish a physical
row from its current logical item. `UIVirtualTreeProvider` keeps cached visible
ranges for tree mutations.

The hierarchy uses virtual rows and retains canonical scene commands. Plain
scene/root lists have no mutation events, so the panel checks their shallow
membership every 250 ms. Child-list notifications handle subtree edits.
The inspector virtualizes primitive property rows; complex editors open in a
separate detail view. Inline variable-height virtualization remains a decision.

## Opt-in fixtures

Use the native editor in the unit-testing world. Set
`XRE_UI_CONTROL_GALLERY=1` for the common-control gallery. Set
`VirtualCollectionFixtureItemCount` to `100000` or `1000000` for the large-list
fixture; its default is `0`. The fixture reports realized rows, selected logical
ID, and UI work counters. It uses the production canvas and text paths.
Run the settings generator after changing the settings type. See the validation
record for exact procedures and platform limits.

# Native UI Retained Rendering Design

Status: Proposal; documentation only. No runtime or performance result is claimed.

Updated: 2026-10-23

Source baseline: `e61559409b441eae3fdd8c4a8dc74297869ff7ff`

Roadmap: [Native UI retained performance and editor parity][todo]

Validation: [Native UI validation][validation]

## Goal and scope

Extend the native UI that renders the FPS text into a responsive retained UI
library. Keep controls, layout results, glyph data, and render records between
frames. Run measure and arrange asynchronously on immutable worker input. Reuse
cached results and visit only affected branches. Measure the cost against an
equivalent ImGui workload before claiming that the native path is faster.

The first deliverable is an opt-in FPS overlay, a common-control gallery, and a
large virtual list. The gallery covers buttons, checkboxes, radio groups, text
and numeric inputs, dropdowns, range sliders, color selectors, and scroll
regions. It must work through the existing canvas/text/batch path.

Do not migrate the full editor, switch the default editor, install a UI library,
or implement an immediate-mode facade in this work. Docking, complete panel
parity, variable-height trees, rich text, and browser/HTML replacement are later
work. ImGui remains the default editor. This proposal supersedes the roadmap's
old synchronous-only layout direction. A serial execution mode can serve as a
deterministic diagnostic oracle for the same layout kernel; it is not a second
production layout algorithm.

## Verified source and gaps

The source audit checked these symbols at the baseline above. Static inspection
shows structure and missing contracts. It does not establish measured cost.

| Source | Current behavior | Design consequence |
|---|---|---|
| [UnitTestingWorld.UserInterface.AddFPSText][fps] | Creates `UITextComponent`, enables batching, and registers `TickFPS`. | Use this as the first unchanged integration fixture. |
| [UITextComponent.RegisterWithBatchCollector][text] and [UIBatchCollector][batch] | Text enters `AddTextQuad`; the collector builds grouped text data. | Extend this route, not a parallel widget renderer. |
| [UILayoutSystem.InvalidateCanvasLayout][layout] and [UICanvasTransform.InvalidateLayout][canvas-transform] | Canvas invalidation recursively invalidates descendant arrange state. | Separate local dirty roots from global bounds/DPI changes. |
| `UICanvasTransform.UpdateLayoutAsync` and `UILayoutSystem.LayoutCoroutine` | Schedules a coroutine over live transforms; yields between measure and arrange. `UpdateCanvasLayout` cancels that job. | This is not immutable worker layout. Do not move these methods into `Task.Run`. |
| [UIBoundableTransform.GetMaxChildWidth/GetMaxChildHeight][bounds] | Uses `Where`/`Cast` over children. | Remove hot-path LINQ as the incremental kernel replaces live traversal. |
| `UIBatchCollector.SwapBuffers`, `RenderTextGroup`, `FinalizeAndUploadTextData` | Clears collection groups and rebuilds grouped instance/glyph writes. Some unchanged text upload reuse exists. | Replace logical reconstruction with persistent records; preserve valid existing optimizations. |
| `UITextComponent.WriteData` | Copies the glyph list under `_glyphLock` before writing. | Publish owned glyph ranges with explicit versions and lifetime. |
| [NativeUIElements.CreateCheckboxToggle][elements] and [UIToggleComponent.SetValue][toggle] | Uses `Property`/`Targets` and reflection to write values. | Add typed retained state/binding contracts; use reflection only in a cached editor adapter. |
| [UITextInputComponent.SetCursorPositionWithLocalPoint][text-input] | Reaches a TODO after initial guards; the old glyph hit logic is commented out. | Complete text navigation and editing; a rendered text field is not a finished input control. |
| [UIScrollableTransform][scroll] | Resize and placement overrides only delegate to base. | Define viewport, extent, scroll offset, clipping, and wheel behavior as one control contract. |
| [UIListTransform.Virtual][list] | Still scans `Children`; hides offscreen child subtrees. | Implement provider-based realization and recycling instead of one subtree per item. |
| [UICanvasInputComponent][input] | Has pointer-contact generation and reusable removal scratch, but queries live hit data and diffs intersection sets. | Preserve those protections; add a coherent UI publication/hit generation and idle-query cache. |
| [UIClipRegion.ContainsHit][clip] | Checks live bounds and clip state. | Derive render clipping and input clipping from the same immutable publication. |
| [HierarchyPanel][hierarchy] | Uses `RemakeChildren`, `Transform.Clear`, and a 2,000-row cap. | Keep the editor port separate until the reusable list contract is proven. |

Existing [UI architecture][architecture] documents describe the present system
and include historical claims. This proposal defines future contracts. It does
not treat those claims as completed async or performance work.

## Ownership and data flow

Use one owner thread for authored UI state, binding callbacks, input routing,
and scene-tree mutation. Workers must never access `SceneNode`, mutable
`UITransform` instances, renderer objects, event subscribers, or property getters.
The render owner alone creates, uploads, and retires graphics resources.

Proposed records and services below name responsibilities. They are not existing
types. Put reusable contracts and layout data in the runtime UI subsystem;
keep platform input/IME adapters in input integration and editor bindings in the
editor. Do not add editor dependencies to runtime controls.

| Data | Owner and contents | Lifetime |
|---|---|---|
| Authored state | UI owner: stable element IDs, parent/child order, values, styles, local revisions, semantic control state | Until removal; IDs include an incarnation so reuse cannot revive a removed control. |
| Layout input snapshot | Immutable pages: element ID/incarnation, layout inputs, topology, constraints, font metrics, dirty roots, dependency revisions | Leased by one task and its result; no mutable object references. |
| Layout result | Worker: desired sizes, arranged rectangles, transforms, resolved clips, dependency stamps, visited-node counters | Accepted once or discarded; does not mutate live state. |
| UI publication | Owner-built immutable packet: layout result, painter order, hit index, clip table, render deltas, focus/navigation metadata, generation | Retained while render or input readers use it. |
| Render storage | Render owner: persistent instance slots, glyph/geometry ranges, atlas references, upload ranges | Retire only after the last referencing frame completes. |

Data flow is `authored transaction -> immutable input -> worker result -> validated
publication -> render frame + input view`. Each arrow has an explicit ownership
transfer. The owner can prepare new input while consumers use the previous
complete publication. There is no partially updated visible tree.

### Immutable snapshots without a full copy each frame

Build the initial indexed layout model once. Track mutations with a bounded
change journal. Freeze changed pages and share unchanged immutable pages by
lease. Snapshot work is proportional to changed pages plus dependency paths;
it must not serialize the whole canvas for every FPS update. Cap retained page
memory. When the cap is reached, coalesce pending mutations and defer publication
instead of queuing more snapshots.

Resolve inherited styles, scalar auto-size inputs, and data bindings on the
owner. Replace `CalcAutoWidthCallback`/height callbacks with captured results or
registered pure layout operators. Never invoke arbitrary callbacks on a worker.
An unsupported custom layout operator keeps its previous valid output and emits
a diagnostic; it must not silently run live-tree layout on a background thread.

Text measurement uses immutable font metrics and shaping results. Cache keys
include text content/revision, font/fallback revision, size, locale/direction,
features, wrap width, line policy, and DPI. Use a thread-safe shaping service or
exclusive worker-owned instances. Glyph atlas insertion and resource uploads
remain render-owner work. Pin the atlas revision used by each publication.

## Incremental layout and invalidation

Keep separate revision counters and dirty sets. Do not use one canvas revision
to invalidate every domain. A frequently changing FPS color must not make an
otherwise valid layout result stale.

| Change domain | Propagation | Cached work retained |
|---|---|---|
| Measure | Leaf to auto-size-dependent ancestors, stopping at a proven size boundary; arrange affected descendants when desired size changes | Unrelated subtrees and equal constraint results |
| Arrange | Affected layout parent/child slots; include later siblings when list placement depends on an earlier size | Valid desired sizes |
| Transform | Local/world transform dependencies and affected descendant hit/render bounds | Measure and arrange for a pure visual transform |
| Visual | Color, border, image state, caret visibility, selection highlight | Layout and hit index unless eligibility/geometry also changes |
| Clip | Changed clip plus affected descendants' resolved clip IDs | Geometry and text layout when the text constraints did not change |
| Hit eligibility/order | Visibility, enabled state, blockers, painter order, transforms, clip changes | Previous hit result only if both pointer and hit generation match |
| Topology | Add/remove/reparent/reorder, style inheritance and affected layout roots | Independent branches with matching dependency stamps |
| Global bounds/DPI/font scale | Affected canvas roots and dependent text/layout inputs | Only cache entries whose full input keys still match |

Use cached measure keys `(element incarnation, input revision, constraint,
child aggregate revision, font/style revision)`. Use arrange keys that also
include allocated bounds, parent layout dependencies, and child order. Key
comparison must validate equality, not just a hash. Incremental work can include
siblings and ancestors; claiming O(1) for every leaf size change is incorrect.

Dirty roots form a minimal non-overlapping set. Merge nested dirty roots at
transaction completion. A transaction batches changes but does not block until
layout completes. Global resize can touch the whole canvas; it must still use
the same snapshot/kernel/publication path. Remove the competing active
`OnResizeChildComponents` path only after its required behavior is in the kernel.
Use dependency analysis to reject layout cycles or unstable auto-size feedback
with element IDs and the responsible constraint; do not retry without a bound.

## Worker scheduling, stale rejection, and publication

Start with at most one running layout task and one coalesced pending input per
canvas. Use a global bounded worker pool; initially evaluate one and two layout
workers. Coalesce by canvas/root and keep the latest authored mutations. Do not
queue one task per setter. Give interactive roots priority with an age limit so
background roots cannot starve. Record queued age and task CPU time.

Cancellation is cooperative at subtree boundaries. Cancellation does not mean
the worker has stopped; retain its input pages until task completion. Under
continuous edits, let bounded tasks finish and validate their dependency sets.
Do not discard a whole canvas result because an unrelated decorative property
changed. Accept an independent root only if its ancestor constraints, topology,
and all layout dependencies match. Rebase accepted roots onto the current
immutable publication. Reject overlapping or incompatible results and reschedule
the smallest affected roots. Never patch a stale result into current live nodes.

Publish in this order:

1. Validate canvas lifetime, element incarnations, dependency revisions, font
   revision, bounds/DPI, and the result's base publication.
2. Assemble a complete candidate packet. Resolve compatible current visual
   deltas by element ID; derive hit bounds, painter order, and clip IDs from the
   same layout/transform state as render data.
3. Prepare the render storage updates in an available frame slot. If required
   resources or uploads are not ready, keep the previous complete packet active.
4. Commit the packet at the frame boundary. Tag the rendered output with its
   publication generation. Do not expose half the new hit index or clip table.
5. For windowed input, latch the most recently presented packet for that output.
   Route events using its generation and stable IDs. Offscreen consumers use the
   explicitly accepted output generation. A pointer event must not use newer
   layout that the user has not seen.
6. Before dispatching a command, verify that its stable target is still alive,
   enabled, and owned by the same canvas. Removal cancels capture/composition
   immediately, even if an old image remains visible. Defer callback-induced
   mutations into the next authored transaction to prevent reentrant publication.

Focus and capture use logical IDs, not render slots or row objects. A press and
release keep the same target incarnation; they cannot jump to a recycled row.
Keep `TopMostInteractable` as the camera-input blocking contract. Cache idle hit
queries by pointer state, output/view transform, hit generation, and capture
state. Decorative elements are absent from the hit index unless they block input.

At teardown, increment the canvas lifetime token, stop accepting mutations,
cancel pending work, and retire snapshots only after workers/readers release
them. Unregister event/binding callbacks on the owner. Retire GPU ranges after
frame completion, not when cancellation is requested. A timed-out consumer must
cause a bounded admission stop and diagnostic, not unsafe pool reuse.

Initial layout has no old packet. Keep the canvas noninteractive and show an
explicit loading state until the first complete packet is ready. A debug serial
oracle runs the same immutable kernel. Production layout must not fall back to
an unbounded synchronous pass under load.

## Persistent drawing and text

Give each renderable a stable `(slot, incarnation)` handle. Register on activation
and enqueue typed create/update/remove operations. Store records in persistent
pools. Replace collect-time membership reconstruction with a generation-aware
change queue. Reuse static layers while the FPS overlay, caret, or hover changes.

Batch only explicit solid, border, image, and text primitives. A batch key includes
pipeline/shader variant, atlas or texture, sampler, blend/depth state, render pass,
clip mode, and material features. Preserve painter-order runs; do not globally
sort translucent controls by texture. Keep incompatible custom materials on
normal render commands. Share shader/material prototypes and put per-control
values in instance data. A missing color is a diagnostic, not a generic magenta
primitive. Rectangular clip stacks use shared clip records; unsupported rotated
or complex clips require an explicit stencil/scissor path or a named rejection.

Keep geometry and shaped glyph ranges stable across frames. Text content,
font, and wrap changes can replace only that text range. Color, transform, and
outline changes update instance data. Coalesce adjacent dirty slots and upload
only changed ranges. Bound range fragmentation; compact in a new resource
generation only when thresholds justify the cost. Never repack all glyphs because
one FPS label changes. Preserve atlas ownership until all referencing frames
retire. A font/atlas replacement invalidates only dependent runs.

Separate logical dirty bytes from actual uploaded bytes and allocated capacity
in counters. An unchanged frame has no UI layout, glyph copy, logical batch
rebuild, or UI buffer upload. Drawing a retained buffer each frame is still work;
measure command submission and raster cost. Follow the existing [render resource
invariants][render-invariants] and [mesh contracts][mesh-contracts]. Screen-space
UI must not acquire unrelated 3D shadow/scene work through [UIRenderableComponent][renderable].

## Retained controls and data binding

Use a shared control state model: visible/collapsed, enabled, focused, hovered,
pressed, captured, selected, invalid, and read-only. Specify which state changes
are visual, layout, or hit invalidations. A value-change event carries old/new
values and change origin. A binding echo must not trigger recursive updates.
Use typed get/set/change subscriptions for runtime controls. Resolve reflection
once in an editor adapter; never poll `PropertyInfo` per frame. Unbind on recycle
or teardown. Route later editor mutations through undo and dirty tracking.

| Control | Required behavior before gallery acceptance |
|---|---|
| Button | Pointer and keyboard activation, disabled state, focus ring, capture, click cancellation outside bounds. |
| Checkbox | Two-state and explicit mixed state; space toggles; typed binding and one committed change event. |
| Radio group | One selected stable option, group keyboard navigation, disabled options, correct removal policy. |
| Scroll region | Viewport/extent/offset, clamp, wheel and drag scrollbar, nested event consumption, clipping, scroll-into-view. |
| Dropdown | Stable selection IDs, popup placement/clipping, keyboard search/navigation, outside-click close, focus restore; virtual options for large sources. |
| Range slider | Min/max/step, pointer capture, keyboard increments, disabled/read-only state, preview versus committed value. |
| Numeric field | Shared text editing, parse policy, culture, bounds, step/drag input, overflow/NaN policy, invalid intermediate text, commit/cancel. |
| Color selector | Explicit linear/sRGB interpretation, alpha, component and hex input, preview/commit/cancel; retain selection while popup opens. |
| Text field | Single/multiline selection, caret hit testing, grapheme navigation, word/line movement, clipboard, undo/redo, submit/cancel, password policy where needed. |

Text editing state owns text, selection anchor, caret, composition range, and
revision independently of glyph storage. Platform IME adapters send composition
updates without committing each preedit value. Derive candidate-window position
from the presented caret generation and DPI. Preserve Unicode grapheme clusters,
combining marks, bidirectional text, and font fallback in hit testing and movement.
Do not equate a UTF-16 index with a displayed glyph. Clipboard/IME calls stay on
the required platform thread. Defer shaping of changed text through the versioned
text/layout service; keep caret/highlight publication consistent with that revision.

Add an accessibility semantic tree with stable IDs, role, label, value, enabled
state, bounds generation, and actions. Define tab order, keyboard navigation,
focus visibility, scaling/high contrast, and screen-reader adapter ownership.
Virtualized items expose logical identity and realization/scroll actions without
creating every visual row. The first milestone must state which platform adapters
are tested and which remain unavailable; do not claim accessibility parity from
keyboard-only checks. New text or accessibility dependencies need separate review.

## Real virtualization

Use a provider contract with count, stable item ID, data revision, indexed access,
change ranges, and bind/unbind callbacks. UI rows must not own source data or be
created for every item. Start with fixed-height rows. For viewport height `H`,
row height `h`, scroll offset `s`, and per-side overscan `o`, calculate the range
from `floor(s/h)` through `ceil((s+H)/h)`, expanded by `o` and clamped to count.
The realized capacity is at most `ceil(H/h) + 1 + 2*o`, except at a documented
bounded pinned edit row. Extent is `count*h`, computed without scanning rows.

Maintain a bounded pool by template type. Bind only entering/changed rows and
release leaving rows. Scrolling updates the visible range, clip, and transform
without scanning all source items. Keep item ID, selection, expansion, focus,
and edit draft in the logical model. Each bind increments the row incarnation;
asynchronous text/image results must match both item ID and incarnation.

On recycle, detach callbacks and clear hover/press/validation state. Keep keyboard
focus as a logical item ID and realize it on scroll-into-view. For active capture
or IME editing, either retain one bounded pinned row or end the interaction with
an explicit commit/cancel policy before reuse. Deletion cancels interaction with
that ID; it must never transfer a pending edit to another item.

Later variable-height support needs an indexed extent structure and stable scroll
anchor. Later trees need cached visible-order ranges and expansion state. Do not
implement either by rebuilding all scene subtrees or copying the entire data set
on each scroll. The first fixture tests 100,000 and 1,000,000 logical items with
the same viewport capacity.

## Work sequence and decisions

All work below is proposed. The linked roadmap owns open code items; the linked
validation document owns procedures and results. No implementation item is
complete because this design was written.

| Order | Work and integration points | Exit condition |
|---|---|---|
| 1. Contracts and instrumentation | `UILayoutSystem`, transforms, input, text, batch collector: stable IDs, dirty domains, transaction journal, counters. | Each mutation has a defined owner/domain; local edits do not require a full snapshot or canvas walk. |
| 2. Worker kernel | Extract pure measure/arrange from live transforms; implement immutable pages, bounded scheduling, dependency validation, and serial oracle. | Worker and serial output match for deterministic inputs; stale/cancelled results cannot mutate a live canvas. |
| 3. Coherent publication | Canvas/input/clip/render integration: complete packet, presented-generation latch, lifetime leases and teardown. | Hit/render/clip state always refers to one accepted generation; stress removal/reparent/resize is safe. |
| 4. Retained render data | `UIBatchCollector`, `UITextComponent`, renderer upload path: stable slots, glyph ranges, dirty uploads, prototype materials. | Idle UI causes no logical reconstruction or upload; a changed text run leaves unrelated ranges intact. |
| 5. Common controls and list | Runtime controls, typed bindings, input/IME adapters, provider-based fixed-height virtual list. | All gallery interactions pass; realized rows stay bounded and stable focus survives recycling. |
| 6. First milestone acceptance | Opt-in FPS/gallery/list fixture on OpenGL; separate Vulkan qualification. | Correct visuals/input, measured latency/CPU/allocations, and equal-workload ImGui comparison. Publish limitations. |
| 7. Later editor work | Reuse accepted controls for hierarchy, inspector, console and other panels. | Separate owner-approved scope and panel validation; no default switch in the first milestone. |

Decisions fixed here: retained control state; asynchronous immutable layout;
independent dirty domains; bounded work; coherent publication; persistent drawing;
fixed-height recycling first; no performance claim without evidence.

Open decisions: exact snapshot page size and memory cap; pure custom-layout
extension API; platform IME/accessibility adapters; variable-height need; worker
budget under CPU rendering; minimum benchmark hardware. Measure these choices
with the [first-milestone matrix][milestone-matrix] before fixing defaults.

## Risks and acceptance contract

Asynchronous layout can reduce owner-thread work while increasing total CPU and
input delay. Snapshot copying, cancellation storms, invalidation propagation,
and excessive publication copies can erase the benefit. Measure all of them.
Continuous resize and large global changes need bounded progress, not a silent
synchronous escape. Fonts, clip transforms, recycled rows, and callback teardown
are correctness risks as well as performance risks.

First-milestone acceptance requires correct FPS text, every gallery control,
bounded large-list realization, worker/serial parity, stale rejection, coherent
hit/render/clip publication, and safe teardown. The [validation matrix][milestone-matrix]
sets workloads, counters, provisional budgets, and the equal ImGui comparison.
Report wins and regressions separately for idle and changing workloads. A result
on one backend, CPU, viewport, or input platform does not qualify all others.

## Source references

[fps]: ../../../../XREngine.Editor/Unit%20Tests/Default/UnitTestingWorld.UserInterface.cs
[text]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Text/UITextComponent.cs
[batch]: ../../../../XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs
[layout]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UILayoutSystem.cs
[canvas-transform]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UICanvasTransform.cs
[bounds]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UIBoundableTransform.cs
[elements]: ../../../../XREngine.Editor/UI/NativeUIElements.cs
[toggle]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Interactable/UIToggleComponent.cs
[text-input]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Interactable/UITextInputComponent.cs
[scroll]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UIScrollableTransform.cs
[list]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/Arrangements/UIListTransform.cs
[input]: ../../../../XREngine.Runtime.InputIntegration/Scene/Components/Pawns/UICanvasInputComponent.cs
[clip]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/UIClipRegion.cs
[hierarchy]: ../../../../XREngine.Editor/UI/Panels/HierarchyPanel.cs
[renderable]: ../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Core/UIRenderableComponent.cs
[architecture]: ../../../architecture/ui/README.md
[todo]: ../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md
[validation]: ../../testing/ui/native-ui-validation.md
[milestone-matrix]: ../../testing/ui/native-ui-validation.md#first-milestone-async-retained-ui
[render-invariants]: ../../../architecture/rendering/default-render-pipeline-notes.md
[mesh-contracts]: ../../../architecture/rendering/mesh-submission-strategies.md

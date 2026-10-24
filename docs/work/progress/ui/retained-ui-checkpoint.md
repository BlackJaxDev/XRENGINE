# Retained UI Implementation Checkpoint

Date: 2026-10-23
Status: Active implementation
Scope: [Native UI TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
Architecture: [Retained execution](../../../architecture/ui/05-Retained-Execution.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

Latest code reconciliation: **40 of 60 implementation items are complete**.
The active TODO has 20 code items and four owner decisions. Later sections
record each checkpoint; earlier counts describe their historical source state.

The first checkpoint closes **16 of the original 60 implementation items**.
The active TODO retains 44 implementation items and four owner decisions, for
48 open boxes. This count does not mark platform or performance acceptance complete.
The work continues on the same branch. ImGui remains the default editor.

## Completed code contracts

| Contract | Evidence |
|---|---|
| Immutable versioned worker inputs | `UILayoutSnapshot` contains values and topology; no live scene callbacks reach the kernel. |
| Shared pure measure/arrange kernel | `UILayoutValueKernel`; serial/worker equality and cache tests. |
| Bounded canvas and global scheduling | `UILayoutCanvasSession` and `UILayoutPageLease`; capacity, pending-input, and worker tests. |
| Stale result rejection | Dependency and lifetime checks; visual-edit versus geometry/detach regression. |
| Worker-page lifetime through cancellation | Leases wait for worker completion and owner retirement; quota/cancellation regressions. |
| Remove panel synchronous layout and live-tree coroutine | Editor UI has no forced layout calls; canvas entry points request the value worker and apply through the owner pump. |
| Stable render membership identity | `RenderInfo2D.MembershipId`; scene painter-order membership regression. |
| Explicit custom shader batch routing | Owned batch semantics, missing-color diagnostic, derived/custom-material exclusion tests. |
| Painter-run ordering | `MaterialRunsSplitAtPainterOrderBoundaries` and unchanged-run tests. |
| Diagnostic gating | Text and batch diagnostic work is guarded by the diagnostics setting. |
| Fixed-height virtual provider/list | `IVirtualListProvider` and arithmetic extent; million-item bounded realization test. |
| Viewport and overscan row recycling | Stable row leases; pool reuse and recycle-before-rebind tests. |
| Grid child placement | Placement offset/span implementation and invalid-span regression. |
| Opt-in control and large-list fixture | Unit-world gallery/list source and counters; editor/default fixture settings remain opt-in. |
| Separate test code and live procedures | Deterministic tests are under `XREngine.UnitTests`; hardware and performance procedures are in validation docs. |
| Focused hierarchy source ownership | `HierarchyPanel` data, rows, commands, drag/drop, and menu partial files; editor compilation. |

These entries close the matching code items only. For example, the million-item
headless test proves bounded realization for its provider; it does not prove live
scroll latency, editor workflow parity, or GPU performance.

## Checkpoint validation

The checkpoint passes the headless build with zero warnings
and errors and **275 of 275 tests**, with no failures or skips. The matching
editor build passed with zero warnings and errors. All builds use `Tools/Invoke-AgentBuild.ps1`;
NUnitLite runs the built headless project with `--no-build`.

The settings generator built with zero warnings/errors and ran with `--no-build`.
It regenerated the schema and both ignored settings files. A bounded source review
verified fixes for producer teardown, shared receipt retirement, and focus teardown.
See the investigation record for intermediate failures and repairs.

## Known implementation and acceptance limits

The following limits describe the first published checkpoint. See the next
section for later changes.

- Capture/output work still includes full retained-array copies and scans.
- Publication resource claims still depend on finalization. Bounded producer pools
  can stall until old packets retire. Deterministic holder and reader leases are next.
- Exact world-space producer/consumer receipts and frozen direct-world commands remain open.
- Independent composition layers, static command reuse, shared material prototypes,
  image atlasing, and shared backdrop capture remain open.
- Native editor workflow parity, rebuild/play-state persistence, complete drag payloads,
  multiline navigation, and virtual accessibility realization remain open.
- Windows execution, native IME, multi-window workflows, OpenGL/Vulkan graphics,
  visual correctness, performance, and equivalent ImGui comparisons have not run here.
- Variable-height virtualization, bindless policy, world-space shadows, and the
  default-editor stabilization period remain owner decisions.

## Shared Pages And Explicit Packet Claims

The next checkpoint closes three more original code items: incremental layout
invalidation, decorative-item hit exclusion, and routine virtual hierarchy updates.
The total is **19 of 60 implementation items**. The active TODO retains 41 code
items and four owner decisions. A partially completed broad item stays open.

- Layout inputs and outputs share immutable 256-node pages. Leaf-edit tests check
  page sharing and visited/copied node counts. Global font revisions reject stale
  capture without scanning every text node on each unchanged capture.
- Packet holders and local readers use explicit claims. Tests cover output
  replacement, canvas teardown, and four producer slots reused without garbage
  collection. Backend review checks failed attempts, admission cancellation, and
  exact-generation shutdown retirement. Full world-space publication is still open.
- Native hierarchy updates use the virtual provider without the old 2,000-row cap.
  Decorative render records enter hit queries only when they block input.
- Multiline controls preserve vertical caret intent and reject stale placement.
  Stable-ID list requests can realize an offscreen row; presented-row focus remains
  open. Native asset mutations reject linked paths and use platform case rules.
- Warmed idle and moving-pointer queries with an unchanged hit set allocate zero
  bytes in the headless fixture with a concrete no-op profiler. This is not a live
  editor or whole-frame allocation result.

The final headless source at this boundary builds with zero warnings/errors and
passes **293 of 293 tests**, with no skips. The editor also built with zero
warnings/errors. The matching desktop test-project build, which includes the
editor, also passed with zero warnings/errors after removal of unused borrowed
packet getters. The editor Undo regression compiled but did not execute: NUnit
rejects the Windows-targeted assembly on Linux with "Only supported on Windows7.0".
Its zero-test exit code is not counted as a pass.
No Windows execution, GPU presentation, native IME, visual acceptance, or comparison
with ImGui is implied.

## Shared Glyph Storage And Presented Row Focus

This checkpoint closes five more original code items: the authoritative layout
entry path, the layout allocation audit, hit-identity diff allocation, bounded
blocker filtering and idle hit reuse, and separate visual/hit generations.
The total is **24 of 60 implementation items**. The TODO retains 36 code items
and four owner decisions. Broader rendering and control items remain open.

- Attached public measure/arrange calls request the canvas worker. Detached
  measurement uses the same value kernel. Legacy child-layout overrides no longer
  compete with the accepted result. Warmed idle canvas pumps allocate zero bytes
  in the regression fixture. Edited immutable pages still require bounded storage.
- Mesh and unchanged glyph buffers have explicit shared ownership. Paint changes
  reuse geometry; animated overrides replace only their own buffers. Dirty glyph
  writes preserve old publications. Standalone `UIText` work remains open.
- Desktop image batching checks storage, color space, sampler, and mip contracts.
  Normal owned draws remain available for unsupported batch profiles. Known
  color/storage mismatches fail with a diagnostic. GPU execution remains pending.
- Virtual trees maintain item-ID indexes. Focus requests wait for a presented
  row lease and cannot target an arbitrary recycled row. Input tests cover
  leave/return transitions, blockers, paint-only reuse, and generation changes.
- The inspector exposes canonical component add/remove actions, deferred callback
  guards, and structural Undo integration. The editor build checks this code;
  Windows workflow acceptance remains pending.

The matching editor build passed with zero warnings/errors in 3m57.06s. The
final headless build passed with zero warnings/errors in 15.16s. All **310 tests
passed**, with no skips, in 7.482s. These results cover the exact source in this
checkpoint. No Windows execution, shader execution, visual, GPU upload, native
IME, or performance comparison result is claimed.

## Primitive Ownership, Standalone Text, And Control Lifetime

This checkpoint closes seven more original code items: admitted primitive batch
contracts, activation-owned handles, unchanged persistent run reuse, revision-owned
glyph payloads for both text paths, stable glyph ranges, typed retained bindings,
and the 100,000/1,000,000-item realization and presented-focus fixture. The total
is **31 of 60 implementation items**. The TODO retains 29 code items and four
owner decisions. Complete sparse updates, world publication, composition layers,
full text shaping, and editor parity remain open.

- Primitive slots separate current registration from historical packet identity.
  Locking and stale-unregister tests cover owner/collector transitions.
- Standalone text uses immutable metrics and the shared layout kernel. Font
  callbacks only mark dirtiness; the render/preparation owner applies it under
  the draw gate. Math label teardown releases font observers. A focused review
  found no remaining high-priority issue in those repaired paths.
- Popup teardown, radio binding re-entry, directional scroll, and presented radio
  focus use retained lifetime checks. Registered-input tests cover popup scope
  and gamepad slider routing.
- Large-list tests use 100,000 and 1,000,000 logical items. Each keeps at most 57
  realized rows for an 800-pixel viewport, 20-pixel rows, and overscan eight.
  Old packets cannot focus recycled rows; new packets focus the requested ID.
- Asset file mutations validate root and linked-path boundaries during Undo/Redo.
  Component drops use the editor mutation path. The new dock-tree model is tested,
  but live docking integration remains open.
- An optional HarfBuzz backend returns glyph IDs and clusters from owned font
  bytes. It does not yet replace the production rune-layout path. Render-command
  publication claims are also groundwork, not completed world-space publication.

The matching headless build passed with zero warnings/errors in 1m11.89s.
All **328 tests passed**, with no skips, in 8.389s. The matching editor build
passed with zero warnings/errors in 2m49.10s. Desktop-only shaping tests require
Windows execution; their compilation result is recorded in the validation doc.
No Windows execution, GPU, visual, native IME, or ImGui comparison pass is claimed.

## Shared Images, Shaped Values, And Live Dock Structure

This checkpoint closes three more original implementation items: compatible image
atlasing, accessibility semantics/adapter boundaries, and cached virtual-tree
operations. The total is **34 of 60 implementation items**. The active TODO
retains 26 implementation items and
four owner decisions. No hardware or performance acceptance item is closed.

- The fixed image atlas owns its source pixels, packs padded image regions, and
  uses one color/sampler profile per page. The opt-in gallery displays two images
  from one page through the production material/collector path. Atlas teardown
  handles callback re-entry, property vetoes, and later custom material changes.
  Existing editor icon text continues to use the shared icon-font atlas. Bindless
  selection remains an owner decision; this path uses ordinary texture cohorts.
- Virtual tree/table accessibility exposes stable offscreen IDs and state without
  creating rows. Existing actions and presented-focus realization form the adapter
  boundary. The window reports that no OS accessibility bridge is installed.
- Desktop quads now keep intrinsic values without one material/shader per control.
  Explicit legacy material access stays isolated and rejects unsupported border
  or cropped-image escapes. Shared text prototypes remain open.
- Known text snapshots copy used glyph prefixes. Both text paths use one checked
  dirty-span writer. Generic custom buffer captures retain full-buffer semantics.
- Opt-in shaping connects a single direction run and existing atlas to the value
  kernel. Callback-safe capture and backend-generation invalidation prevent stale
  cache installation. Shaping still runs on the owner; full asynchronous shaping,
  bidi, fallback, wrapping, and shaped editing remain open.
- The live dock host mounts the versioned tree and retains panel instances across
  structure changes. Closed panels deactivate and leave their world context.
  Full primary-panel detach, cross-window drag, focus, and play-state parity remain
  open. Popup Back and radio-group teardown use explicit input/event lifetimes.
- World draw templates and packet claims are prepared but the new route remains
  inactive. Complete shadow-pass/output receipts and immutable offscreen consumer
  surfaces remain open.

Final matching build and test results are recorded in the validation and
investigation documents. No Windows/GPU execution or speed comparison is implied.

The tree reconciliation checks `UIVirtualTreeProvider` and the existing
`TreeEdits_UpdateVisibleOrderWithoutRecreatingRows` and
`InsertBeforeVisibleItem_KeepsItsRowLease` tests. Tree edits splice affected visible
ranges and reindex shifted suffixes. They do not rebuild the source tree or
create rows. Large edits can still cost time proportional to the shifted suffix.
Hierarchy source refresh is a separate, open editor integration item.

The final matching headless build passed with zero warnings/errors in 13.11s;
all **353 tests passed**, with no skips, in 7.657s. The final editor build passed
with zero warnings/errors in 47.53s. The full desktop test project compiled with
zero warnings/errors in 43.85s. Desktop execution remains blocked by its Windows
assembly requirement. The checked source includes the dock review repairs.

## Worker Shaping, Sparse Quads, And Editor Mutations

This checkpoint keeps the reconciliation at **34 of 60 implementation items**.
The remaining 26 implementation items and four owner decisions stay open. The
changes complete useful parts of these broad items but do not establish their
full done conditions.

- Opt-in single-run shaping now executes within the bounded layout worker.
  Pending snapshots compare with accepted output, stale faults cannot block
  newer input, and backend/font generation changes invalidate cached subtrees.
  Visual-only edits retain layout reuse. Language, feature, glyph, and scratch
  limits reject oversized requests. Full bidi, fallback, and wrapped shaping
  remain open.
- Desktop quad cohorts use stable payload slots and a separate painter-order
  index stream. Dirty adjacent payload fields upload independently. Full storage
  resets clear slot initialization state. Run insertion can still rebase later
  cohorts; dense text-instance indices and text growth uploads remain open.
- Shaped controls distinguish glyph provenance from complete grapheme caret
  geometry. Complete runs use visual caret movement; incomplete geometry reports
  unsupported placement. Ordinary context menus use bounded scroll containers.
  Recycled editor drag sessions and accepted-layout popup checks remain open.
- All seven primary panels can use the detached-window host. Inspector asset
  drops validate request identity and target changes on the scene-edit owner.
  Multi-target component removal verifies rollback after callbacks. Toolbar
  teardown coalesces callback-driven rebuilds. Native panels use solid retained
  backgrounds; shared opt-in backdrop capture remains open.
- Animation panels share inverse-first key/member mutation actions and dirty
  tracking. The native panel adds paged path discovery and bounded retained
  curve/timeline display. A required keyframe removal repair completes ownership
  changes before notifying listeners. Full animation workflow parity remains
  open. The profiler no longer truncates its source at 1,000 rows.
- The bounded render-surface pool separates reservation from producer completion.
  The new offscreen/direct-world route is still inactive. Exact consumer receipts,
  direct-world clipping, and independent composition layers remain open.

Windows execution, GPU uploads, shader execution, visual checks, multi-window
interaction, IME acceptance, and equivalent ImGui measurements remain pending.

The final headless build passed with zero warnings/errors in 16.09s; all
**391 tests passed**, no skips, in 8.844s. The matching editor build passed with
zero warnings/errors in 40.30s, and the desktop test project compiled with zero
warnings/errors in 47.19s. Source hashes confirmed the matching snapshot. The
desktop regression tests did not execute on Linux. All 31 local document links
resolve, and the diff whitespace check passes.

## Offscreen Frames, Sparse Text Slots, And Editor State

This checkpoint closes three original implementation items: desktop stable
instance slots and coalesced uploads, identity-safe recycled-row interaction,
and consistent retained focus/navigation state. The total is **37 of 60**.
The TODO retains 23 code items and four owner decisions.

| Completed contract | Evidence |
|---|---|
| Stable quad and text slots | `UIBatchCollectionTests` verifies inserted painter runs, reused slots, growth, glyph indices, and coalesced ranges. GPU run identity is independent of painter position. |
| Recycled interaction identity | Virtual collection/presented-input tests and `EditorDragDropUtilityTests` reject old row identity before drop; capture and focus do not transfer to its replacement. |
| Retained navigation state | Focus, popup, radio, scroll, gamepad, clipping, disabled-state, and stale-visibility tests use the presented identity contracts. |

Other changes advance open items:

- Offscreen world canvases own reserved color targets and frozen sampled quads.
  Separate producer and output receipts preserve the displayed packet. Tests
  reject stale backend completion, late fence reuse, and backward promotion.
  Direct-world frozen draw/shadow collection remains open.
- Eligible canvas text avoids private renderer/material/buffer allocation.
  Standalone resources retire on the render owner. Reattachment, callback veto,
  external mesh replacement, and borrowed resources have deterministic regressions.
  The full shared-material prototype item remains open.
- Popups scroll within accepted bounds. Tests verify actual vertical and
  horizontal content movement and stable extents. Owner deactivation avoids
  child destruction inside a parent world transfer.
- The play controller restores global selection once; Inspectors restore their
  own targets and property focus. Keep-changes mode preserves play-time state.
  Failed Undo replay retains its source entry and rejects stale history transfer.
  These editor behaviors require Windows runtime acceptance.

The final headless build passed with zero warnings/errors in 16.15s. All **410 tests
passed**, with no warnings or skips, in 8.269s. The matching editor build passed with zero warnings/errors in 52.46s; desktop
test compilation passed with zero warnings/errors in 47.18s. Source hashes
confirmed the same snapshot. No GPU, visual,
Windows execution, native IME, multi-window, or ImGui comparison pass is claimed.

## Presented Control Geometry And Media Teardown

This checkpoint closes the shared-material prototype item. The total is
**38 of 60 implementation items**; 22 code items and four owner decisions remain.
It adds immutable solid, border, image, and text material definitions. Mutable material state remains private to each retained draw cohort.
Common controls keep their values in instance records. Tests check independent
primitive values, separate raster state, and virtual combo row materials.

The other changes advance open items:

- Slider and scrollbar dispatch retains the displayed press packet and its
  control geometry. Callback reentry cannot replace the old handler's geometry
  or release a new capture. Scrollbar capture rejects stale content limits and
  schedules a coalesced follow-up layout.
- Numeric fields share parsing, limits, steps, and Alt-drag edits. Submit or
  cancel ends a drag. Combo typeahead supports bounded local options and indexed
  virtual sources. Large-provider capability reporting and bounded disabled-row
  navigation remain open.
- IME candidate placement uses the displayed caret record. Candidate updates
  stop before composition cancellation. Full paragraph analysis remains pending
  an owner decision; no new text dependency has been added.
- The primary native scene view uses presented geometry for pointer mapping and
  validates scene, pane, input, camera, and window ownership before delayed pick
  results apply. It exposes canonical transform mode and space controls.
  Detached scene input remains open.
- Video teardown settles the old session, audio callbacks, and GPU helper even
  when external callbacks reenter or throw. GPU helper disposal runs on its
  render owner. Viewport callbacks and property-text polling stop on destruction.
  Web backend teardown remains open.
- World records freeze layer and culling values. Registration suppression now
  preserves authored visibility. The direct-world proxy/receipt route remains
  inactive; this does not close world publication or clipping.

The final headless build passed with zero warnings/errors in 1m45.89s. All
**437 tests passed**, with no warnings or skips, in 11.643s. The matching editor
build passed with zero warnings/errors in 3m21.83s; desktop test compilation
passed with zero warnings/errors in 47.16s. Hashes confirmed the same 9,882-file
source snapshot. All 31 local document links resolve; the whitespace check
passes. Windows execution, GPU and visual checks, IME runtime acceptance,
multi-window workflows, and equivalent ImGui measurements remain pending.

## Indexed Controls And Exact Callback Owners

This checkpoint closes the first-milestone common-controls code item. The total
is **39 of 60 implementation items**; 21 code items and four original owner
decisions remain. Full paragraph analysis has a separate pending dependency or
platform-adapter decision. No pending decision is treated as approval.

- Large dropdown sources use indexed prefix and enabled-option navigation, with
  explicit capability and result status. Toggle commits reject replaced bindings.
  Button and checkbox focus visuals respect callback order and recycling.
- Detached scene windows use their own camera and input owner in the source
  runtime world. Transform-tool drags reject foreign owners and retire on direct
  destruction. Windows interaction tests compile; live workflow acceptance stays
  open.
- Asset watchers carry exact installation and project-root identities. Rebinding,
  synchronous monitor callbacks, and disposal cannot reinstall an obsolete owner.
  An import already in progress still needs context-aware commit guards.
- Web production binds the exact output owner and native view lifetime. Retired
  backends and upload buffers stay owned through delayed disposal. A retired
  backend cannot publish diagnostics on its replacement. Native context behavior
  still requires Windows and Ultralight validation.
- Frozen world collection, indexed native draw checks, and an explicit desktop
  swap verdict prepare coherent direct-world output. The new route stays inactive
  until selected-pass manifests, packet pinning, terminal output proof, and
  persistent-target recovery are complete. These seams do not close publication.

The matching headless build passed with zero warnings/errors in 2m58.96s.
All **478 tests passed**, with no warnings or skips, in 10.154s. The editor
build passed with zero warnings/errors in 2m32.24s; the desktop test project
compiled with zero warnings/errors in 56.13s. All checks match the same 9,905
source/project files. The 32 local document links and whitespace check passed.
Desktop tests did not execute. No Windows, graphics, visual, or performance
acceptance is claimed. See the
[investigation](../../investigations/ui/retained-ui-integration.md) for repairs
and intermediate results.


## Composition Boundaries And Deferred Ownership

This increment keeps reconciliation at **39 of 60 implementation items**. The
remaining 21 code items and four original owner decisions stay open.

- Explicit subtree composition owners preserve contiguous painter spans. Unchanged
  chrome spans retain identity and content revision across overlay edits, insertion,
  and output mapping. Batch runs respect those boundaries. Native command reuse and
  sparse publication capture remain open.
- The dock host uses a shared splitter with accepted layout metrics and captured
  pointer geometry. Hierarchy mutations use the panel's exact world through queue
  execution and Undo. Full cross-window editor workflow acceptance remains open.
- Deferred watcher imports carry exact context tickets. Provider callback reentry
  cannot continue stale options or generated-root commits. Stale work preserves
  recovery backups. Generated closure reservations and full import transactions
  remain open.
- Streaming volume destruction invalidates pending loads and releases the exact
  attachment owner. A conditional world-pass tracker distinguishes selected,
  pruned, empty, missing, and failed work. It remains inactive until supported
  graph shapes, multiple-canvas claims, native receipts, and output recovery are
  complete.

The matching headless build passed with zero warnings/errors in 1m29.43s. All
**510 tests passed**, with zero warnings or skips, in 9.129s. The matching editor
build passed with zero warnings/errors in 2m17.23s, and the desktop test project
compiled with zero warnings/errors in 45.06s. All final checks match 9,924
source/project file hashes. All 32 local document links and the whitespace check
pass. Desktop tests did not execute. No platform acceptance is claimed.


## Static Chrome And Terminal Callback Ownership

Reconciliation remains **39 of 60 implementation items**. The 21 broader code
items and four original owner decisions remain open. Paragraph analysis remains
pending.

- Vulkan can reuse a compatible singleton solid screen-space chrome draw. Exact
  segment, run, mesh, backend, native resource, and pass identities remain part
  of admission. Current output receipts still prove actual execution. Text,
  images, borders, custom draws, offscreen output, and direct world UI remain
  outside this cache path. No GPU cache result is claimed.
- Menus, tooltips, and combo boxes remove their exact callbacks on direct
  destruction. Replacement setters reject callback reentry that destroys or
  replaces the owner. Audio teardown releases exact listener/source pairs,
  rejects stale creation, preserves reactivation ticks, and retains uncertain
  native releases without unsafe retries.
- Hidden inspectors keep play snapshots until activation and validate their
  assigned window world. Mixed target lists do not enter component-only casts.
- The inactive world-pass manifest supports bounded Switch cases, exact canvas
  packet sets, and reserved owner-thread attempts. Production output grouping,
  repeated shadow/color invocation proof, and partial-target recovery stay open.

Headless build 75 passed with zero warnings/errors in 1m28.85s. Full run 44 passed
**531 of 531 tests**, with zero warnings or skips, in 9.286s. Editor build 33
passed with zero warnings/errors in 2m12.98s. The desktop test project compiled
with zero warnings/errors in 45.41s. Desktop tests did not execute. The two Vulkan-only pass-admission edits after the headless build are
outside its project inputs. Final source verification covers 9,936 files.
No Windows execution, graphics, visual, IME, or performance acceptance is claimed.

## Canvas Membership And Scene Camera Ownership

Code reconciliation is **40 of 60 implementation items complete**. The active
TODO retains 20 code items and four original owner decisions. Paragraph analysis
remains pending. The completed item is 3D UI collection membership.

- Screen, hidden, inactive, and offscreen UI stays outside 3D collection. Canvas
  mode, component replacement, nested transform replacement, and reparenting
  restore valid membership. Authored visibility and shadow flags stay intact.
  Late transform attachment also restores the exact retained batch handle.
- Scene panels select cameras only from their exact window and runtime world.
  Callback replacement is rejected. Mandatory viewport detachment clears camera,
  world, and window owners before notifications. List vetoes and failing observers
  cannot prevent the tested owner and resize-subscription cleanup.
- Gallery chrome, FPS, dock controls, and virtual text rows declare composition
  owners. The actual gallery/FPS capture fixture compiles in the desktop suite;
  it has not executed here. The broader layer and backend-reuse items stay open.

The final headless build passed with zero warnings/errors in 15.96s. Full run 47
passed **541 of 541 tests**, with zero warnings or skips, in 9.028s. The matching
editor build passed with zero warnings/errors in 2m44.39s. All checks use the
9,941-file source snapshot. Windows execution, graphics, visual checks, IME,
performance, and equal-workload ImGui comparison remain unverified.

Desktop test compilation 14 passed with zero warnings/errors in 50.22s. No
desktop tests executed. All 32 local documentation links and whitespace checks
pass. The source snapshot remains unchanged.

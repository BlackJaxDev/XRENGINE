# Retained UI Implementation Checkpoint

Date: 2026-10-25
Status: Active implementation
Scope: [Native UI TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
Architecture: [Retained execution](../../../architecture/ui/05-Retained-Execution.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

Latest code reconciliation: **51 of 60 implementation items are complete**.
The active TODO has nine code items and four owner decisions. Later sections
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

## Shared Backdrop Capture And Direct Owner Teardown

Code reconciliation is **41 of 60 implementation items complete**. The active
TODO retains 19 code items and four original owner decisions. Paragraph analysis
and the separate native asset recovery proposal remain pending approval. The
completed item is opt-in shared backdrop capture for the supported GL screen
canvas profile. Native visual and completion-fence acceptance remain open.

| Implementation area | Complete / original total | Open |
|---|---:|---:|
| Immutable layout and bounded scheduling | 9 / 9 | 0 |
| Coherent publication and input | 5 / 8 | 3 |
| Persistent render data and clipping | 13 / 17 | 4 |
| Retained controls and virtual collections | 9 / 11 | 2 |
| First-milestone fixture | 3 / 3 | 0 |
| Editor adoption | 2 / 12 | 10 |
| Total | 41 / 60 | 19 |

- Disabled blur adds constant-time checks. Immutable publication flags identify
  visible requests and incompatible pre-render content. Compatible panels use
  one canvas-prefix capture with separate strengths. Native output proof and
  exact consumer coverage gate presentation. Four renderer-affine surfaces and
  a 128 MiB budget bound capture storage; receipt retirement prevents early reuse.
- Native panels release exact callback and provider owners on direct destruction.
  Retiring list rows cannot move into another provider during cleanup. Drop
  disposal clears retained owners before callbacks, and source retirement stops
  the current drag. Toggle bindings reject stale delivery and release their tick
  even when destruction bypasses deactivation.
- Hierarchy selection reaches offscreen virtual nodes without realizing rows.
  Direct hierarchy destruction releases stored source, input, row, menu, and
  drop owners. Broader hierarchy parity stays open because incremental reparent,
  source refresh, root updates, and keyboard/rename workflows need more work.
- The asset-creation prototype is archived as an unpublished patch and exact
  source copies. No asset-creation command or persistent journal is implemented.
  The [recovery proposal](../../design/ui/native-asset-creation-recovery-design.md)
  is available for owner review.

Headless build 83 passed with zero warnings/errors in 1m24.06s. Full run 49
passed **567 of 567 tests**, with zero warnings or skips, in 10.446s. The frozen
snapshot contains 10,394 source, project, and shader files. No Windows execution,
native shader compilation, GPU/visual result, IME result, or performance gain
is claimed.

Editor build 36 passed with zero warnings/errors in 1m00.76s. It includes two
editor-only corrections after the headless run: menu-transform lookup and a
captured non-null scene world. Those files are outside headless project inputs.
The final source snapshot retains those two differences explicitly.

Desktop test compilation 15 passed with zero warnings/errors in 57.51s. No
desktop tests executed. All 37 local document links and whitespace checks pass.
The 10,394-file final source snapshot is unchanged.

## Stable Composition And Exact Control Lifetimes

Code reconciliation is **42 of 60 implementation items complete**. The active
TODO retains 18 code items and four original owner decisions. Paragraph analysis
and persistent asset recovery still await approval. The completed item is
independent composition-layer invalidation, backed by real control fixtures.
Broader native command reuse and all platform/performance acceptance remain open.

| Implementation area | Complete / original total | Open |
|---|---:|---:|
| Immutable layout and bounded scheduling | 9 / 9 | 0 |
| Coherent publication and input | 5 / 8 | 3 |
| Persistent render data and clipping | 14 / 17 | 3 |
| Retained controls and virtual collections | 9 / 11 | 2 |
| First-milestone fixture | 3 / 3 | 0 |
| Editor adoption | 2 / 12 | 10 |
| Total | 42 / 60 | 18 |

- Layout recomputes outputs when a node-count change prevents prior-page reuse.
  Caret insertion/removal no longer hides an unrelated unchanged sibling.
  Same-size input keeps incremental reuse.
- Actual text, caret, selection, hover, and distinct virtual rows preserve chrome
  span identities, revisions, records, passes, and painter order.
- Tree navigation waits for exact presented focus. Callback replacement, equal
  numeric leases, modal changes, and cancelled requests cannot redirect selection.
- Radio, slider, color, text, and canvas input release their exact owners during
  direct destruction. Stale events and reentrant replacement are covered.
- Hierarchy source changes and live/editor roots update without losing unrelated
  bindings. Rename drafts survive reuse. Scene-owner lookup includes the exact
  editor scene. Broader hidden-scene and dual-membership mutations remain open.
- World collection retains bounded packet selections across traversal, swap, and
  teardown. This foundation does not enable frozen direct-world output or input.

Headless build 95 passed with zero warnings/errors in 18.25s. Focused run 58
passed 39 of 39 tests. Full run 59 passed **616 of 616 tests**, with zero warnings
or skips, in 11.988s. The frozen source snapshot contains 10,415 files. Editor build 37 passed
with zero warnings/errors in 3m03.39s. Desktop-test compilation 17 passed
with zero warnings/errors in 44.86s. One desktop-only fixture argument was
corrected after the headless/editor checks; their inputs remain unchanged. No Windows execution,
GPU, visual, IME, benchmark, or faster-than-ImGui result is claimed.


## Owned Editor Mutations And Window Lifetimes

The original implementation reconciliation remains **42 of 60 complete**.
This increment completes subparts of the 18 open code items. It does not close
full inspector, hierarchy, docking, or text parity. Non-IList detail builders
still need terminal callback admission. The three pending proposals and four
original owner decisions remain open; no approval was inferred.

- Table revisions refresh compatible cells without losing their row lease or
  focused control. Callback transitions wait until the current callback returns;
  old handlers retire before replacement bindings. Failed preparation cannot
  admit a hidden replacement.
- Inspector list details use sparse occurrence IDs and guarded grouped edits.
  Supported BCL lists require a non-overridable compiler-generated source getter
  and backing field. Custom/virtual sources and nested ownerless collections
  remain read-only. Broader collection editing remains open.
- Hierarchy create/delete/reparent/promotion preserve exact hidden and dual scene
  memberships, runtime route metadata, and source dirty documents. Undo restores
  placement before activation. Uncertain setter or matrix callback completion
  keeps history and requires explicit recovery/reissue. The first promoted
  editor-only child allocates the editor scene. Prefab and model drops accept a
  parent in a hidden scene, and the spawned prefab stays detached like its parent.
  The native node and scene menus match the ImGui actions.
- Dock transfer requires a confirmed destination and unchanged registration
  owners. Detached windows store optional bounds and consume exact approved-close
  receipts. Primary-key migration is pending. The optional SDL/Vulkan split-pump
  terminal handoff defect is documented separately; no shutdown rewrite landed.
- Grid, material, editor-root, panel, watcher, and drop-target teardown release
  exact owners. Material deactivation removes its shared listener while retained
  draw payloads keep independent reader claims. Foreign editor child trees are
  preserved. Asset load completion also executes when already on the render owner.

Headless build 99 passed with zero warnings/errors in 1m13.26s. Full run 62
passed **667 of 667 tests**, with zero warnings, failures, or skips, in 10.551s.
Editor build 40 passed with zero warnings/errors in 53.36s. Desktop test-project
compilation 19 passed with zero warnings/errors in 51.78s; those tests did not
execute. The final snapshot contains 10,473 source files. One desktop-only test
parameter changed after the editor build; headless and production inputs remain
unchanged. All checks ran through the build wrapper or a previously built test
executable. Windows, GPU, visual, IME, native resource, benchmark, and
faster-than-ImGui acceptance remain unverified.


## Displayed Input And Accepted Text Measurements

Code completion is **43 of 60**. The collection teardown item is complete in
code: every detail branch and retained value binding checks its owner generation,
and partial branches retire through final cleanup. The active TODO retains
17 code items and four original owner decisions. Paragraph analysis, recovery
journaling, and primary-window key migration are still pending separate approval.

| Area | Completed / original code items |
|---|---:|
| Worker layout | 9 / 9 |
| Publication and input | 5 / 8 |
| Persistent rendering and clipping | 14 / 17 |
| Controls and virtual collections | 10 / 11 |
| Fixtures and integration | 3 / 3 |
| Editor adoption | 2 / 12 |

This checkpoint adds accepted text measurements for inspector sizing, complete
font-face cache identities, stable-ID table navigation, tab shortcuts, and exact
hierarchy menu ownership. Hover callbacks retain cleanup owners across reentry
and failed callbacks. Camera and menu input use displayed geometry. None of these
narrow changes closes the broader text, docking, or editor parity items.

Headless build 104 passed with zero warnings/errors in 1m10.59s. Full run 66
passed **682 of 682 tests**, with no warnings, failures, or skips, in 10.069s.
Editor build 42 passed with zero warnings/errors in 40.99s. Desktop compilation
21 passed with zero warnings/errors in 45.76s; its tests did not execute. The final
source snapshot has 10,488 files. One editor-only nullable-flow guard changed
after the headless run, and one desktop-only assertion changed after the editor
build. Headless and production inputs for those checks remain unchanged. Windows, GPU, visual, native IME, benchmark,
and faster-than-ImGui acceptance remain unverified.

## Draft, Focus, And Collection Ownership

The original reconciliation remains **43 of 60 code items complete**. This
increment completes parts of the remaining controls, inspector, publication, and
hierarchy work. The active TODO still has 17 code items, four original owner
decisions, and three pending proposals. No approval was inferred.

- Multiline controls use Enter for a newline, Ctrl+Enter to submit, and Escape to
  cancel. Draft commits, cancellation, and blur reject replaced bindings and
  changed target identities. Text and numeric setter loops stop after an owner
  changes. Stable tab focus waits for the accepted strip revision and loses to
  newer focus requests. Nested tab updates coalesce within a bounded loop.
- Combo selection and context-menu actions reject superseded callback owners.
  Screen and offscreen-authored publication tests retain nested clip records and
  painter order across mixed batch/fallback draws. They do not prove native
  offscreen surface or GPU behavior.
- Exact BCL linked lists use grouped Undo receipts with retained node identity,
  exact source/owner checks, and captured dirty documents. Dictionary and generic
  collection editing still need safe adapters. Console and Profiler action and
  projection gaps are now explicit in the active TODO.
- Synchronous construction support retains exact constructor origins and partial
  failure allocations. Temporary guards reject foreign-thread and foreign-batch
  identity/publication changes. Rollback removes only its exact cache entries.
  Clone graph adoption and safe retirement remain unimplemented.
- World packet sources can register outside the drawable scene tree while legacy
  child draws remain active. The full retained world-output route remains open.

Headless build 111 passed with zero warnings/errors in 15.36s. Focused publication
run 70 passed 5 of 5 tests in 0.928s. Full run 71 passed **728 of 728 tests**, with
no warnings, failures, or skips, in 10.870s. Editor build 43 passed with zero
warnings/errors in 2m37.59s. Desktop test compilation 22 passed with zero warnings/errors in 48.02s; those tests did not execute. The frozen source
snapshot contains 10,519 files. Only the clipping test fixture changed after the
editor input; production inputs match. Windows execution, native IME, OpenGL and
Vulkan acceptance, visual results, benchmarks, and faster-than-ImGui claims remain
unverified. ImGui remains the default editor.


## Cached Diagnostics And Guarded Collection Edits

The original reconciliation remains **43 of 60 code items complete**. The active
TODO has 17 code items, four original owner decisions, and three pending proposals.
No platform acceptance or owner approval was inferred.

- Console categories, clear/copy actions, auto-scroll, counts, and cached row
  formatting use shared retained controls. Stale menu owners cannot change a
  later category or selection. Unchanged log entries keep their IDs and strings.
- Profiler CPU indexing uses a 2,048-operation tick budget, two reused index
  buffers, coalesced requests, and lazy row formatting. Cancellation preserves
  the displayed snapshot and retires candidate references within later budgets.
  Other diagnostic views remain open.
- Supported exact BCL string/int dictionaries use guarded grouped Set/Add/Remove
  receipts. Unsupported collections are read-only. Display callbacks cannot
  write into a retired inspector after user formatting code runs.
- Owned-clone construction checks adoption and publication ownership. Failed
  allocations remain retained. Editor Duplicate and safe graph retirement are
  still gated by hidden root ownership and failure-retention integration.
- World command-image leases now check traversal, registration, and package
  generations. The viewport replacement remains inactive. The Vulkan static
  cache now admits its bounded border variant with exact buffer ownership.

Headless build 112 passed with zero warnings/errors in 2m26.02s. Full run 72
passed **758 of 758 tests**, with no warnings, failures, or skips, in 11.663s.
The frozen source snapshot contains 10,556 files. Editor build 44 passed with
zero warnings/errors in 2m35.43s on the same inputs. Desktop compilation 23 passed
with zero warnings/errors in 50.98s; its Windows fixtures did not execute. These checks do not establish Windows
execution, native IME, GPU/visual acceptance, or performance against ImGui.


## Retained Command Reuse And Structural Ownership

Code completion is **44 of the original 60 items**. The bounded Vulkan static
command-cache contract is complete in source. Native cache execution remains
unverified. The active TODO retains 16 implementation items, four original owner
decisions, and three pending proposals.

| Area | Completed / original code items |
|---|---:|
| Worker layout | 9 / 9 |
| Publication and input | 5 / 8 |
| Persistent rendering and clipping | 15 / 17 |
| Controls and virtual collections | 10 / 11 |
| Fixtures and integration | 3 / 3 |
| Editor adoption | 2 / 12 |

- Supported Vulkan solid, border, bindful image, and text chrome runs retain
  private buffers and stable run identity across unrelated overlays. Native
  resource, clip, output, and fence ownership still control admission and reuse.
  OpenGL retains geometry/buffers and issues its draws each frame.
- An explicit canvas-only OpenGL world route connects command-image ownership,
  native output lineage, accepted presentation, and frozen input. Defaults stay
  unchanged. Mixed scenes, world clipping, Vulkan world output, and shadows
  remain open.
- Scene roots and child/component lists retain exact occurrence ownership,
  including silent mutations, failed world binding, callback replacement, and
  queued destruction vetoes. Legacy scene serialization fixtures preserve the
  existing formats. Safe graph retirement and active Duplicate remain open.
- Console snapshots reuse storage and capture repeat counts with displayed text.
  Profiler views add bounded CPU aggregates, engine diagnostics, GPU summaries
  and selected worst-frame trees. Full profiler parity remains open.
- Animation rows have stable IDs and lazy formatting. Canonical edit receipts
  preserve unrelated callback history and reject stale owners. Built-in vector
  edits commit a complete handle before notification and recover synchronization
  after exceptions. Plot interaction, incremental dirty capture, complete authoring
  controls, and exact equal-time order restoration remain open.

Headless build117 passed with zero warnings/errors in 19.16s. Full run75 passed
**837 of 837 tests**, with no warnings, failures, or skips, in 12.465s. Editor
build50 passed with zero warnings/errors in 46.64s. Desktop test compilation26
passed with zero warnings/errors in 49.54s; those Windows tests did not execute.
The source snapshot contains 10,631 files. After the headless run, only an
unlinked editor helper's nullable flow and two desktop-only test fixtures changed;
headless production and test inputs match the final source. See the investigation
for all intermediate failures and repairs.

No Windows execution, native IME, OpenGL/Vulkan output, visual acceptance,
benchmark, or faster-than-ImGui result is claimed. ImGui remains the default.

## Publication Safety And Native Authoring

Code completion is **46 of the original 60 items**. Explicit publication lifetime
and pointer capture now close in code. The active TODO retains 14 implementation
items, four original owner decisions, and three pending proposals.

| Area | Completed / original code items |
|---|---:|
| Worker layout | 9 / 9 |
| Publication and input | 7 / 8 |
| Persistent rendering and clipping | 15 / 17 |
| Controls and virtual collections | 10 / 11 |
| Fixtures and integration | 3 / 3 |
| Editor adoption | 2 / 12 |

- Collection holds an exact packet claim through callbacks and teardown. Painter
  census changes commit only with an accepted candidate; recursive capture
  rejects the outer packet. Sparse capture remains open.
- Registered mouse tests preserve pressed geometry across output replacement,
  block camera movement during capture, and release capture after recycle or
  mouse-up. Worker, producer, output, input, and in-flight holders use explicit
  lifetime claims. Normal retirement does not wait for garbage collection.
- Canonical direct-world solid, image, and text draws carry frozen fragment crop
  through the bounded combined-program OpenGL route. Native shader, model, and
  camera checks are implemented. Vulkan world publication and broader output
  profiles remain open; native clipping acceptance is unverified.
- Exact typed track edits preserve equal-time order and invalidate cached playback
  cursors before notification. Native controls add incoming values, planar tangent
  actions, interpolation choices, traversal, and clip kind. Full animation parity
  remains open.
- GPU rankings publish bounded versioned pages. Native profiler actions reuse
  the canonical external launcher and UDP setting, with captured preference
  ownership and an editable update interval. Full profiler parity remains open.
- Cold object lifetime admission protects structural reservations through
  identity, cache, and destruction operations. Safe graph retirement and editor
  Duplicate remain inactive.
- Material discovery excludes four existing pipeline-owned transparency uniforms.
  Resolved image shaders retain the strict single-MatColor batching contract.

Headless build124 passed with zero warnings/errors in 11.29s. Full run79 passed
**861 of 861 tests**, with no warnings, failures, or skips, in 14.420s. All 10,647
recorded source inputs stayed unchanged through the final build and test run.
Matching editor build53 passed with zero warnings/errors in 2m15.57s. Desktop
test build27 passed with zero warnings/errors in 51.25s. The same source manifest
remained unchanged through both builds. Desktop tests were compiled only.

Windows execution, native IME, visual/GPU acceptance, soak tests, and equivalent
ImGui performance measurements remain open. ImGui remains the default editor.


## Committed Journal Reader And Native Refresh

The code reconciliation remains **46 of 60**. The journal reader is a prerequisite
for sparse publication; it does not close that item. Independent readers copy
complete transactions without consuming layout changes. Capacity loss requires
full resynchronization. Unchanged cursors return without scanning the log.

The native CPU hierarchy selects the worst sampled thread time from each
completed 0.5-second window. Session replacement and backward clock movement
reset its retained candidate and display. Inspector string previews now use the
existing property notification path, show mixed values, preserve complete text
elements, and unsubscribe when recycled. Complete editor parity remains open.

Headless build127 passed with zero warnings/errors in 18.37s. Full run80 passed
**872 of 872 tests**, without failures, warnings, or skips, in 14.422s. The
headless harness uses the existing portable Runtime.Host project to test the
real profiler selector. No package version changed. Editor build54 passed with zero warnings/errors in
2m25.55s. Desktop test build28 passed with zero warnings/errors in 50.36s.
All 10,655 source inputs remained unchanged. Inspector-specific fixtures were
compiled only; Windows execution and live acceptance remain open.


## Shared Record Pages And Guarded Lifetime Support

The code reconciliation remains **46 of 60**. The full sparse publication and
editor adoption items remain open.

- Render pages own shared resource claims. Mapped packets share the immutable
  store; full capture and flat consumers remain in use.
- A one-shot hierarchy lifetime receipt supports exact detached graph retirement.
  Preflight, lifecycle admission, private removal permits, and failure retention
  are implemented. History custody and Duplicate activation remain open.
- Loop edits stage the clip and unique children before callbacks. Length/stretch
  still need a separate complete receipt.
- Native profiler roots and child paths persist for the configured period, mark
  inactive values, and prune at deadlines. Unchanged same-frame reads do no index
  work before a transition. All-root plots and detailed diagnostics remain open.
- Enum rows use the combo path and guard recycled property ownership. Flags use
  named text input. Retired bindings cannot start a new write; admitted writes
  retain Undo settlement before later targets are rejected.
- Vulkan authoring carries exact world draw identity, but its checked world
  capability remains disabled pending the complete native proof.

Headless build132 passed with zero warnings/errors in 26.61s. Full run82 passed
**917 of 917 tests**, without warnings, failures, or skips, in 13.830s. All 10,679
recorded source inputs stayed unchanged through all checks. Editor build55 passed
with zero warnings/errors in 2m36.95s. Desktop test build29 passed with zero
warnings/errors in 57.71s. Inspector and animation fixtures are desktop-only;
no Windows execution or GPU acceptance is claimed.


## Indexed Consumers And Editor Timing

The code reconciliation remains **46 of 60**. This increment extends existing
items without closing sparse capture, world rendering, or editor parity.

- Input/focus and render collection read immutable record pages. Collector
  ownership handles callback reentry and failed cleanup. Composition still uses
  contiguous records, and capture still scans the full tree.
- Built-in unbaked keyframed length and stretch edits commit complete timing
  before notifications and restore exact Undo snapshots. Baked and frame-cadence
  adapters remain unsupported with an explicit diagnostic.
- Profiler CPU/GPU root history cells are virtualized and reuse bounded scratch
  storage. They handle first layout, resize, recycle, and teardown reentry.
- Clone custody reserves ownership before construction, keeps receipts through
  history, and retains unknown or failed cleanup. Duplicate remains inactive
  until lifecycle incarnation checks can prove continuous ownership.
- Vulkan accepted-plan and presentation support retains exact required input and
  rejects failed output without presentation. Native recording completion is
  still unavailable, so the checked world capability stays disabled.

Headless build134 passed with zero warnings/errors in 1m39.20s. Full run84 passed
**937 of 937 tests**, without warnings, failures, or skips, in 12.715s. All 10,699
source inputs stayed unchanged. Matching editor build58 passed with zero
warnings/errors in 2m17.11s. Desktop test build30 passed with zero warnings/errors
in 55.72s. All source inputs stayed unchanged. No Windows execution or GPU
acceptance is claimed.

## Composition ownership and callback-safe plot follow-up

The current increment keeps the reconciliation at **46 of 60**. It advances
several open items without claiming sparse capture, complete editor parity, or
native runtime acceptance:

- Composition segments own intersecting record leaves. Packet and collector
  claims preserve reused segments without retaining unrelated old store pages.
  Public flat compatibility views materialize lazily and count that work.
- Full canvas capture rejects open change transactions and validates the exact
  committed cursor before and after packet construction. Pointer metadata stays
  with the accepted hit packet, including when newer authoring data exists.
- Animation has bounded member lanes and method details, frozen plot selection,
  double-click insertion, and pan. Hidden transactional builds and input/build
  generations reject callback reentry. Baked/frame-cadence length and complete
  channel editing remain open.
- Resource rows and counters publish in one coherent epoch with fixed storage.
  Shadow solve diagnostics include formats, demotions, reuse, allocation, and
  group results. Detailed Vulkan profiler output remains open.
- Clone construction seals lifetime revisions before transfer. Exact replay
  admission spans owned transform setters and matrix callbacks. Duplicate is
  still inactive pending integration; unknown allocations retain their ledger.
- Vulkan records exact indexed issue order and emitted clear evidence for the
  selected output path. Its capability remains disabled until compiled program,
  descriptor, sampled-resource, writer, and final output proofs are complete.

Headless build137 passed with zero warnings/errors in 18.38s. Full run86 passed
**964 of 964 tests**, with no warnings, failures, or skips, in 11.408s. Editor
build59 passed with zero warnings/errors in 2m26.63s. Desktop test build32 passed with zero warnings/errors in 50.45s. The final checked snapshot
has 10,723 files. A desktop-only fixture import was corrected after the editor
build; production and portable test inputs were unchanged. The three
owner proposals and four original owner decisions remain pending.


## Paged composition and guarded native editor commands

The reconciliation remains **46 of 60**. This increment advances the open
publication, control, and editor items:

- Composition ownership uses paged segment slots and paged leaf claims. Local
  page changes retain unaffected pages. Full packet capture is still open.
- Inspector drops pin the row, source values, and queued asset lifetime. A
  completed write keeps its Undo record when a later callback retires the row.
- Native animation edits individual vector/quaternion channels through canonical
  Undo actions. Baked and frame-cadence length adapters preserve exact samples
  and reject unsafe bounds. Wheel and frame-axis pointer behavior remain open.
- Profiler frame-output pages expose every output through bounded page copies.
  Detailed Vulkan rows use one correlated frame publication and label counters
  sampled at a separate cadence.
- Single-root Duplicate and Deep Duplicate use native confirmation, exact
  source/clone admission, guarded history custody, and selection restoration.
  Multi-root requests and unsupported ordinary asset-sharing profiles remain
  explicit follow-on work. The asset-recovery proposal remains pending.
- Vulkan selected-world preparation retains compiled shader and descriptor
  witnesses and forces the witnessed descriptor bind after callbacks. Uniform,
  sampled-resource, pipeline, writer, and final-output proofs remain open; the
  checked world capability stays disabled.

Headless build141 passed with zero warnings/errors in 38.98s. Full run90 passed
**979 of 979 tests**, without warnings, failures, or skips, in 15.220s. Editor
build61 passed with zero warnings/errors in 53.89s. Desktop test build34 passed
with zero warnings/errors in 58.93s. All 10,763 source inputs remained unchanged. Desktop execution, native graphics, IME, visual, and
performance acceptance remain open. The three owner proposals and four original
owner decisions remain pending.

## Sparse publication capture

The reconciliation remains **46 of 60**. This increment advances the open
publication item. It does not close it.

- A complete capture keeps a per-node basis: transform, inherited capture
  context, subtree end, record starts, hidden-subtree root, and pointer
  dependencies. The next capture reads the committed journal and the changed
  layout pages, and visits only the changed subtrees.
- A count-preserving edit replaces only the changed record pages. Unchanged
  leaves, branches, and composition segments are shared with the previous
  publication. Flat hit and render arrays are made only on demand.
- An edit that changes record counts splices the new subtree records between the
  previous records. It visits no clean node, but it copies clean records into new
  stores. This copy is the remaining work on the item.
- Scrollbars and splitters are captured again when their targets change. A
  rebound virtual row records a hit-test change. An unchanged layout identity
  array keeps its instance, so an identical capture can use the sparse path.
- Topology, painter order, canvas, and journal changes use a complete capture.

Headless build13 passed with 0 warnings and errors. The sparse tests passed 9/9,
and the full headless run passed **988 of 988 tests** in 17.5s. Editor build3 and
desktop test build2 passed with 0 warnings and errors. See the
[validation plan](../../testing/ui/native-ui-validation.md) for the environment.
Desktop execution, native graphics, IME, visual, and performance acceptance remain
open. The three owner proposals and four original owner decisions remain pending.

## Context menu keyboard navigation and stale-show guards

The reconciliation remains **46 of 60**. This increment advances the open control item.

- Context menu rows are `UIContextMenuItemComponent` instances bound to their show.
  Up and Down move presented focus between enabled rows and wrap. Home and End move
  to the first and last enabled rows.
- A row from an earlier show is released. It cannot click or move focus in the
  replacement menu.
- Show copies the item array and the row heights before callbacks run. A callback
  that moves the menu to another canvas restarts placement there; a callback that
  leaves the menu without a canvas closes it.

Headless build17 passed with 0 warnings and errors. The full headless run passed
**993 of 993 tests** in 19.8s. Editor build4 and desktop test build3 passed with
0 warnings and errors. The changed source files were unchanged through the checks.
Submenus, native keyboard checks, and visual checks remain open.

## Pointer-anchored animation plot zoom

The reconciliation remains **46 of 60**. This increment advances the open panel item.

- `IUIPointerScrollReceiver` gives a scroll receiver the cursor in its presented
  local coordinates and the pointer geometry of its current hit record. A receiver
  without a current record gets only the wheel delta.
- The animation plot zooms its time axis about the time under the presented
  pointer. `AnimationPlotWheelZoom` holds the zoom limits and window clamp. The
  wheel needs current plot metadata and does nothing during a press.

Headless build20 passed with 0 warnings and errors. The full headless run passed
**1003 of 1003 tests** in 17.6s. Editor build5 and desktop test build4 passed with
0 warnings and errors. The changed source files were unchanged through the checks.
Frame-axis pointer behavior and native wheel checks remain open.

## Dock rebuild and stale-callback guards

The reconciliation remains **46 of 60**. This increment advances the open docking item.

- `NativeDockRenderGate` serializes dock rebuilds. A nested request runs one more
  pass after the current one; endless requests stop after four passes.
- Tab, split, drop, drag, and action-menu callbacks check the pass revision and
  that their stack is still in the tree. A callback from an older pass does nothing.
- Slot detachment iterates a copy of the slot map and detaches only the registered
  slot. A slot destroyed during teardown is recreated by the next stack render.
- Closing the last tab keeps an empty, reopenable stack. A panel detached to its own
  window is closed here even when a callback changed this dock during the open.

Headless tests cover the gate and last-tab behavior. The host itself compiles only
in the editor and desktop test projects; native docking checks remain open.
Headless build22 passed with 0 warnings and errors, and the full headless run
passed **1009 of 1009 tests** in 19.2s. Editor build6 and desktop test build5
passed with 0 warnings and errors on unchanged source.

## Hierarchy rollback keeps newer callback edits

The reconciliation remains **46 of 60**. This increment advances the open drag/drop
and Undo item.

- `HierarchyMutationOperation.RestoreInitial` repairs only its own partial write.
  After the apply completes, a changed placement, activation, or local value came
  from a callback; rollback keeps it and the step records the remaining state.
- Grouped and multi-root duplication remain unavailable. Parent, world, and
  root-list safeguards for grouped replay remain open.

The regression test is in the desktop test project, so it compiles here but does
not run; Windows execution remains open.
Editor build7 and desktop test build6 passed with 0 warnings and errors, and the
full headless run passed **1009 of 1009 tests** on unchanged source.

## Vulkan world UI draws re-verify the pipeline at issue

The reconciliation remains **46 of 60**. Vulkan world publication stays disabled.

- Before a world UI indexed draw issues, the renderer resolves the pipeline again
  without creation. If the result differs from the pipeline bound before uniform
  callbacks, the draw fails with a diagnostic reason.
- The renderer then binds the verified pipeline again, before it forces the
  witnessed descriptor sets. The forced bind ignores the tracked bind state, so a
  callback that binds another pipeline on the same command buffer cannot change
  the pipeline the draw uses. Forced binds do not count as bind churn.
- World UI draws never use the non-indexed fallback, so that path is unchanged.

No Vulkan device ran here. The change compiles in the editor and desktop test
builds only; GPU correctness and live Vulkan acceptance remain open.

## Text layout cache

The reconciliation is now **47 of 60**. The text layout cache item is complete.

- `UITextLayoutKernel.Measure` reuses results through `UITextLayoutCache`. Equal
  text, font metric page and revision, shaped run (face, language, direction,
  features, glyphs), wrap, spacing, line spacing, alignment, margins, DPI, and
  available size reuse one layout. A changed text revision shares glyph and caret
  storage and carries the new revision.
- `UITextLayoutKernelTests` checks reuse, and checks that each placement input and
  the shaped locale, direction, and features select a separate layout.

The full headless run passed **1012 of 1012 tests**. Editor and desktop test
builds passed with 0 warnings and errors. No runtime text profiling ran here.

## Context menu submenus

The reconciliation remains **47 of 60**. This increment advances the open shared
controls item.

- `ContextMenuItem.Submenu` rows open a sibling submenu beside the row. Right opens
  it and focuses its first row after presentation. Left, Escape, and Back close the
  innermost submenu and return focus to its row.
- The root menu owns dismiss input for the chain. A submenu action hides the root.
  Stale rows cannot open submenus. Teardown hides a submenu without destroying nodes.
- `UIContextMenuNavigationTests` covers opening, focus, chain dismissal, innermost
  close, stale rows, replaced shows, and canvas teardown.

Native pointer, hover-open, and visual placement checks remain open.
The full headless run passed **1016 of 1016 tests**. Editor and desktop test builds
passed with 0 warnings and errors.

## Animation time axis scrubbing

The reconciliation remains **47 of 60**. This increment advances the open panel item.

- A press in the plot time axis strip seeks the selected matching component to the
  frame under the pointer. Moves continue the scrub. Times snap to the sample rate
  frozen in the plot snapshot and stay inside the clip.
- `AnimationPlotFrameAxisTests` covers frame snapping, the clip-end clamp, and the
  axis strip bounds. Native pointer checks remain open.
The full headless run passed **1030 of 1030 tests**. Editor and desktop test builds
passed with 0 warnings and errors.

## Explicit native UI failures

The reconciliation is now **48 of 60**. The explicit failure item is complete.

- An audit of the native batch, receipt, offscreen, world, and text paths found
  branches that returned or drew another route without a report on OpenGL and
  Vulkan, because those backends do not require atomic frames.
- `UIFailureDiagnostics` now reports each of these by code, with a counter and a
  throttled warning. A failed batch registration no longer draws the live command.
  Checked world UI that cannot be admitted names the first failed condition.
- `UIFailureDiagnosticsTests` covers counting, the per-code throttle, and empty
  reasons. Forcing each failure on a live backend remains in the validation plan.

The full headless run passed **1033 of 1033 tests**.

## Sparse publication splice

The reconciliation remains **48 of 60**. This increment advances the open publication item.

- `UIRecordStore.Splice` applies ordered edits. It shares untouched pages and copies
  only touched pages. Spliced pages can be short; the store keeps page starts and
  packs all records again when short pages accumulate.
- A count-changing sparse capture now describes its changes as splice edits. Render
  records are placed by painter position through `UIRenderPainterIndex`, which also
  replaces the per-publication membership map for canvas publications.
- `UICompositionSegmentSet.WithSplicedRenders` keeps untouched segments and moves
  shifted segments with a new revision. Composition ranges no longer depend on
  their store position.
- `CountChangingEditsCopyOnlyTouchedRecordPages` and the store and segment tests
  cover the bounds. The collector still traverses every record of a changed store,
  so the item stays open.

The full headless run passed **1041 of 1041 tests**. Editor and desktop test builds
passed with 0 warnings and errors.

## Collector page reuse

The reconciliation is now **49 of 60**. This increment closes the publication item.

- `UIBatchCollector` collects published records one page at a time. Runs end at
  page boundaries, so a page that a later store shares keeps its runs, unbatched
  draw positions, and primitive handles without a record visit.
- Run identity reconciliation and primitive slot release depend only on changed
  pages. Slots count the pages that draw them.
- The per-frame draw-intent count and pending text-layout check read page summaries
  instead of all records.
- `BatchCollectorVisitsOnlyChangedRecordPages` and
  `DiscardedCollectionKeepsCommittedRunsUntilSwap` cover the bounds and the discard
  path.

The canvas walk state still moves record starts with one pass of integer additions
over its node arrays; this is recorded as a limit in the architecture document. The
full headless run passed **1043 of 1043 tests**. Editor and desktop test builds
passed with 0 warnings and errors.

## Text pointer carets

The reconciliation is now **50 of 60**. This increment closes the text editing item.

- A text field hit freezes the layout that the same capture gives to the text render
  record, and the text that it places (`UITextCaretHitMetadata`).
- A press places the caret at the nearest grapheme boundary on the nearest line. A
  drag extends the selection with the press packet's layout. A layout of other text
  never places the caret.
- Selection, grapheme navigation, undo/redo, composition state, and presented IME
  candidate placement were already implemented.

The full headless run passed **1045 of 1045 tests**. Editor and desktop test builds
passed with 0 warnings and errors. Native mouse and IME acceptance stays in the
validation plan.


## Continuation Comparison And Active Panel State

The continuation was reconciled against published `7648531f02f7820cc65cf0029e1aa8055bf715b5`.
The earlier unfinished candidate is preserved locally; it was not applied or
published. See the [source comparison](../../investigations/ui/native-ui-continuation-comparison.md).
The recorded reconciliation remains **50 of 60**, with 10 open code items.

- Wheel propagation now stops if a callback replaces the displayed packet before
  the input cache refreshes. The new regression failed before the repair.
- Both native hosts share tool-state bindings. The active dock-tree route now
  restores console category/auto-scroll and profiler view. Scroll changes request
  the existing save debouncer. No saved schema or window key changed.
- The fetched baseline passed 1,052 tests. The repaired portable runtime passed
  1,054 tests without failures, warnings, or skips. The editor and desktop tests
  compiled with zero warnings/errors; new panel binding tests are compile-only.

Dock ownership work and the other open editor contracts remain unfinished.
Windows, native GPU, IME, visual, and performance acceptance remain open. The
three pending proposals and four original owner decisions remain pending.


## Dock Slot Detachment Ownership

The active rebuild route now pins the slot's node, transform, parent, world,
and dictionary ownership across deactivation and parent callbacks. It preserves
callback transfers and removes the old mapping so a later stack build cannot
reclaim the panel. A changed slot still inside the same host stops rebuild before
chrome teardown. Canonical hierarchy errors remain visible.

The original callback regression failed before the repair. The final six cases
cover registered transfer and retry, transform replacement, destroyed/lost slots,
and normal detachment. Headless build156 and full run101 pass: **1,060/1,060**,
zero warnings/failures/skips. Desktop build38 compiles the editor and tests with
zero warnings/errors. All 10,795 source inputs stayed unchanged. This establishes
the helper used by the active host, not live Windows, multi-window, or GPU behavior.
The recorded count remains **50/60**; broader host and transfer lifetime work is open.

## Failed Dock Rebuild Admission

A failed rebuild now retires its callback revision before the gate opens. The
original error remains visible and a later rebuild can run. Two new regressions
failed before the repair. Build158 and full run103 pass **1,062/1,062** tests with
zero warnings/failures/skips. Desktop build39 compiles the editor and tests with
zero warnings/errors. The 10,795-file source snapshot stayed unchanged. Live
window recovery and the broader docking item remain open; the count is **50/60**.

## Scrollable Menu Keyboard Navigation

Ordinary menus now scroll to the intended arrow/Home/End target and wait for
presented focus. Repeated keys advance the pending target. Requests stop after
120 retry ticks and cancel on newer focus intent, replacement, deactivation,
or loss of exact row/canvas/input/scroll ownership. A scroll callback can replace
the request. Menu and submenu actions also reject stale lifetime and owner
bindings. This uses the existing retained controls and input publication path.

The regressions failed before repair. Final headless build167 passed with zero
warnings/errors, and full run110 passed **1,107/1,107** without failures, warnings,
or skips. Desktop build40 compiled the editor and tests with zero warnings/errors.
All 10,797 source inputs stayed unchanged. Twenty waiting retries allocated zero
managed bytes in the presentation-probe harness; this is not a native performance
benchmark. Windows keyboard/presentation, IME, GPU, and editor acceptance remain
open. Initial submenu-focus cancellation and full menu construction/teardown
ownership remain open code work. The broader reconciliation remains **50/60**.

## Submenu Initial Focus Ownership

Keyboard submenu opening now uses the same bounded request as ordinary menu
navigation. It keeps the owner row's input intent and waits while the child is
hidden. Newer focus intent, virtual focus, or input regeneration cancels it.
Mouse opening cancels pending keyboard focus. The separate eight-frame retry
was removed. The registered Right-arrow regression failed before this repair.

Headless build169 passed with zero warnings/errors. Full run112 passed
**1,112/1,112**, without failures, warnings, or skips. Desktop build41 compiled
the editor and tests with zero warnings/errors. The 10,797-file source snapshot
stayed unchanged. This closes initial submenu-focus cancellation in code and
headless tests; construction/teardown and native acceptance remain open. The
broader reconciliation remains **50/60**.

## Menu Transition Ownership

Show and Hide now serialize callback requests through a bounded loop. The latest
pending request wins and retains its item snapshot and exact owner identities.
Dismissal can replace an outer Show without building its stale content. A failed
content builder closes its partial menu. Cleanup removes only the captured content
root and preserves unrelated children or content transferred to another owner.

Headless build174 passed with zero warnings/errors. Full run114 passed
**1,126/1,126**, with no failures, warnings, or skips. This adds 14 cases; existing
dismissal/reopen regressions also pass. Desktop build42 compiled the editor and
desktop tests with zero warnings/errors. All 10,800 source inputs stayed unchanged.
Individual child callback failure/ownership paths remain open, as do native
keyboard, IME, GPU, and editor acceptance. The count remains **50/60**. The asset
browser audit did not add a row menu, permanent deletion, or ImGui-only actions.

## Native Asset Row Actions

The native asset browser now uses the shared context menu for Open Folder or
Inspect, Rename, and Reimport. The request retains the clicked row lease and
owner/input lifetimes. It opens after right-click dispatch finishes and rejects
recycled or refreshed rows. Actions synchronize the selected row and recheck
callbacks before acting. Rename uses the existing name field and mutation path;
its draft survives an unchanged refresh and rejects a changed field or target.

Headless build179 passed with zero warnings/errors. Final run118 passed
**1,148/1,148**, without failures, warnings, or skips. The 22 new cases exercise
request and draft admission. Final desktop build47 compiled the editor and tests
with zero warnings/errors. All 10,804 source inputs stayed unchanged through that
build and the final test run. Live context dispatch, rename focus, filesystem
actions, and Windows/GPU acceptance remain pending. Delete, asset creation, and
ImGui-only tool actions were not added. The broader count remains **50/60**.

## Table Selection And Activation

Table actions retain the clicked item and binding across selection callbacks.
A recycled row, newer selection, changed source, or retired owner cancels the
old action. Selection callbacks cannot apply an old highlight to a new binding
or publish a superseded selection notification.

Headless build183 passed with zero warnings/errors. Full run122 passed
**1,160/1,160**, without failures, warnings, or skips. Desktop build48 compiled
the editor and tests with zero warnings/errors. All 10,805 source inputs stayed
unchanged. Twelve cases cover these contracts, including failures reproduced
before repair. Native input and broader editor acceptance remain pending.
The broader reconciliation remains **50/60**.

## Asset Keyboard Selection

The asset panel now follows the table's presented keyboard selection. File
selection updates its path and inspector; folder selection stays in the current
folder until activation. One click does not inspect the same file twice. A later
activation restores asset inspection after scene selection. Callback reentry
cannot publish an obsolete AssetSelected event. Deselecting clears only the
inspector path owned by that selection. Lifecycle cleanup removes the subscription.

Desktop build50 compiled the editor and tests with zero warnings/errors. Final
run124 passed the existing **1,160/1,160** portable tests with no failures, warnings,
or skips. All 10,808 source inputs stayed unchanged through these final checks.
Six editor cases compiled; they did not execute on this Linux host. The desktop
test probe executed zero tests and reported `Only supported on Windows7.0`.
The portable suite excludes AssetsPanel. Windows keyboard, inspector, and owner
lifecycle acceptance remain pending. The broader reconciliation remains **50/60**.

## Completed Table Bindings

Table actions require the source revision of a completed row binding. Initial
bind, refresh, and unbind invalidate this record before callbacks. Cells,
accessibility data, selection visuals, and navigation must finish before actions
are admitted again. A source change also rejects an old row until it refreshes.

Five baseline cases reproduced activation during unfinished binding. Six new
cases now pass, including source changes before refresh. Headless build186 passed
with zero warnings/errors; full run127 passed **1,166/1,166**, without failures,
warnings, or skips. Desktop build51 compiled the editor and tests with zero
warnings/errors. All 10,808 source inputs stayed unchanged. Windows input and
broader controls acceptance remain pending. The reconciliation remains **50/60**.

## Menu Content Root Ownership

Menu construction checks its captured content root after attachment callbacks.
A changed node or transform lifetime stops construction before configuration
or a custom builder call. Cleanup preserves the transferred root. Six cases
cover parent transfer and node/transform lifetime changes for ordinary and custom
content. Four new cases failed before repair; parent transfer already failed safely.

Headless build188 passed with zero warnings/errors. Run129 passed **79/79** menu
cases; full run130 passed **1,172/1,172**, without failures, warnings, or skips.
Desktop build52 compiled the editor and tests with zero warnings/errors. All
10,808 source inputs stayed unchanged. Individual child property, construction,
and destruction callbacks remain open. The broader count remains **50/60**.

## Guarded Component Construction

The internal guarded factory retains the component claim before constructor and
creation callbacks. It checks exact owner and list identities, prevents stale
insertion and field writes, and requires pre-existing transform/component
dependencies. The public factory behavior remains unchanged. Failed construction
can abort only a component that never entered a structural list. Cleanup runs at
most once after admission, preserves both failures, and reports only its own
confirmed destruction commit.

Headless build195 passed with zero warnings/errors. Targeted run133 passed
**28/28** cases; full run134 passed **1,200/1,200**, without failures, warnings,
or skips. Desktop build53 compiled the editor and tests with zero warnings/errors.
All 10,810 source inputs stayed unchanged through these checks. This is a
prerequisite for menu child construction and retirement. Those integrations remain open,
and the broader count remains **50/60**.

## Initial Leaf Retirement And Menu Cancellation

An explicit receipt records a currently world-null leaf before guarded component
creation. It reuses canonical retirement and rejects unregistered components,
foreign or duplicate links, stale lifetimes, and prior parent/root adoption.
Failure retains cleanup progress without replay. Confirmed destruction remains
recorded if a Destroyed observer throws. Factory admission and guarded abort
preparation also respect existing retirement reservations.

Menu construction returns to its transition loop when a newer Show/Hide request
arrives at setup boundaries. Six baseline cases reproduced obsolete builder calls;
they now pass. Two further cases preserve real builder failures after a request
is queued. Full child ownership and live-menu retirement remain open.

Headless build198 passed with zero warnings/errors. Run137 passed **130/130**
focused cases, and run138 passed **1,223/1,223** portable tests without failures,
warnings, or skips. Desktop build54 compiled the editor and tests with zero
warnings/errors. All 10,812 source inputs stayed unchanged through these checks.
This closes bounded contracts inside the open shared-control item; the broader count remains
**50/60**. Windows, graphics, and native input acceptance remain pending.


## Retained Float Curve Details And Exact World Removal

The native inspector now provides a PropAnimFloat detail with a 97-sample retained
plot, a virtual key table, key time/value edits, insertion/removal, and length or
stretch commands. It shares exact animation transactions with clip authoring.
Shared selected references edit once; distinct curves remain unavailable. Attached
transition curves retain their exact state/layer/machine membership and dirty the
state-machine source document. That checkpoint left incomplete and AnyState
ownership unavailable; the forward-path update below replaces this restriction.
Idle refresh uses a nonallocating identity check; authored changes coalesce on the
owner thread. Playback sampling does not seek the animation.

Registered, never-adopted leaves can now retire from an exact source world. The
same executor retains lifecycle, world-write, and transform-store progress before
canonical destruction. Nested rejected writes cannot dispose the outer permit.
Clone and original null-world admission remain strict. Parent/root detachment,
permitted ownership transitions, and live menu integration remain open.

Headless build201 passed with zero warnings/errors in 21.91s. Run141 passed all
59 targeted cases in 0.411s. Full run142 passed **1,239/1,239** tests in 30.355s,
with no failures, warnings, or skips. Desktop build57 compiled the editor and
Windows-targeted tests with zero warnings/errors in 1m20.67s. All **10,823** source
inputs stayed unchanged through final validation. The four transition ownership
cases executed portably; nine new inspector action/history cases only compiled.
No Windows execution, native visual, GPU, IME, or performance-comparison result is
claimed. The original reconciliation remains **50/60**, with 10 broad code items
and four original decisions open. The three pending proposals are unchanged.


## Native Asset Reveal Action

The native asset menu adds Open in Explorer for folders and Show in Explorer for
files. It reuses the ImGui launch helper, checks the current row and selection
after callbacks, and skips removed paths. It adds no asset mutation or recovery.

Desktop build58 passed with zero warnings/errors in 6m30.24s. The unchanged
portable menu-request tests passed **22/22** in run143, in 2.100s, using build201.
All 10,824 source inputs stayed unchanged through validation. The full portable
result remains **1,239/1,239** at the preceding checkpoint. The new action compiled;
Windows Explorer launch and native interaction remain pending. No broad code item
closes: the original reconciliation remains **50/60**.


## State-Machine Curve Navigation

The native inspector now routes a state machine's Layers detail through virtual
lists for layers, ordinary states or AnyState, and transitions. Selecting a
transition embeds the existing retained float curve detail. The explicit forward
path works without runtime initialization or owner back-references. It retains
source-document and container lifetimes, rejects stale or duplicate membership,
and permits valid Undo after the view closes. Bare transition properties require
this route. Active-list changes coalesce; unchanged idle checks do not allocate.

Detail construction checks retirement before later controls and provider wiring.
Menu construction also stops later row, label, arrow, separator, line, and scroll
helpers after a queued replacement. Full compound construction and adopted-child
retirement remain open.

Headless build203 passed with zero warnings/errors in 1m18.59s. Targeted run145
passed 100/100 in 3.469s; full run146 passed **1,248/1,248** in 24.404s with no
failures, warnings, or skips. Desktop build61 compiled the editor and tests with
zero warnings/errors in 49.90s. The final snapshot has **10,827** source inputs.
Only eight editor-only test assertion casts changed after the portable run; no
portable input changed. Nine action/history cases and five new construction
retirement cases compiled only. Windows interaction, GPU, IME, visual acceptance,
and performance comparisons remain pending. The original reconciliation remains
**50/60**. Owner decisions and ImGui default are unchanged.


## Preserve Children Transferred During Cleanup

Ordinary SceneNode child destruction now checks captured lifetime and exact
attachment before each dispatch. The native menu Hide regression confirms that
a row moved by an earlier row's callback survives with its label and releases its
old menu binding. Changed lifetimes or source storage stop stale dispatch.
Duplicate references still destroy once; queued children, vetoes, and exceptions
retain canonical behavior. Reserved owned-hierarchy retirement is unchanged.

The tests-first baseline failed five of seven cases. Final headless build206
passed with zero warnings/errors in 37.75s. Targeted run149 passed 13/13 in 1.269s.
Full run150 passed **1,261/1,261** in 29.820s, with no failures, warnings, or skips.
Desktop build62 compiled editor and tests with zero warnings/errors in 3m42.70s.
All **10,829** source inputs stayed unchanged through final validation. The 39
relative links across the maintained UI records resolve; diff checks pass.

This closes one synchronous dispatch contract. Transfer inside the selected
child's own callback, concurrent mutation, away-and-back history, the transform
detach loop, and full menu construction/retirement remain open. The original
reconciliation remains **50/60**. No Windows, graphics, IME, visual, or benchmark
acceptance is claimed. Pending owner decisions and ImGui default are unchanged.


## Float Key Interpolation Controls

The retained inspector now exposes In and Out interpolation buttons for the
selected float key. A separate compact row cycles Step, Linear, Smooth, and
Hermite. Missing or unsupported selections disable the controls. Value and mode
edits share the existing exact authored-state transaction; both also check the
key's captured time and authored frame. Conflicting callbacks preserve their
newer edits and cannot produce unguarded stale Undo.

Headless build207 passed with zero warnings/errors in 2m55.46s. Run151 passed
7/7 authored-state cases in 0.158s. Full run152 passed **1,262/1,262** in 23.780s,
with no failures, warnings, or skips. Desktop build63 compiled editor and tests
with zero warnings/errors in 3m41.54s. All **10,829** source inputs stayed unchanged
through validation. The six new editor action/history cases compiled only; the
portable sampling/reversal case executed. The 39 maintained relative links resolve
and diff checks pass. Windows controls, Undo execution, visuals, and GPU checks
remain pending. The original reconciliation remains **50/60**.


## Unweighted Float Handles And Hierarchy Reconciliation

The native float detail adds incoming value, both tangents, and three sync
controls through the existing exact authored-state transaction. The controls
show complete unification effects and reject nonfinite computed handles before
editing the live source. Weighted-handle authoring remains open.

Headless build208 passed with zero warnings/errors in 3m11.65s. Run153 passed
10/10 authored-state cases in 0.167s. Full run154 passed **1,265/1,265** in 26.713s,
with no failures, warnings, or skips. Desktop build64 compiled editor and tests
with zero warnings/errors in 3m19.50s. All **10,829** source inputs stayed unchanged
through validation. Three new sync cases execute portably; two new editor
history/overflow cases compiled only. The 39 relative documentation links resolve
and diff checks pass.

The hierarchy adoption item is now source-complete. A source audit confirmed all
listed model, row, visibility, selection, rename, menu, and shared asset-spawn
routes. Its remaining Windows workflows belong to the validation document. This
is a reconciliation of existing implementation, not new runtime acceptance. The
original count is now **51/60**, with nine implementation items and four original
owner decisions remaining. Grouped duplication, control lifetimes, three pending
proposals, Windows/GPU/IME checks, and performance acceptance remain open. ImGui
remains the default.

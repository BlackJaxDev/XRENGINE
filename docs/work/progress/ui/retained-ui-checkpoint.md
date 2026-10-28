# Retained UI Implementation Checkpoint

Date: 2026-10-25
Status: Active implementation
Scope: [Native UI TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
Architecture: [Retained execution](../../../architecture/ui/05-Retained-Execution.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

Latest code reconciliation: **57 of 60 implementation items are complete**.
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


## Weighted Float Handles

Retained float details now edit normalized incoming and outgoing weights on
Hermite sides. Complete snapshots preserve weights across ordinary handle edits
and guarded Undo/Redo. Public weight setters invalidate the track after accepted
writes, including nested writes and committed writes whose observer throws.
Serialized fields and interpolation math remain unchanged.

Headless build210 passed with zero warnings/errors in 55.07s. Run155 passed 16/16
cases in 0.136s. Full run156 passed **1,271/1,271** in 24.418s, with no failures,
warnings, or skips. Four added editor cases require Windows execution. The code
reconciliation remains **51/60**; the broader inspector item stays open.

Desktop build65 passed with zero warnings/errors in 3m12.96s. All 10,829 source
inputs stayed unchanged through validation. The 39 maintained relative links
resolve and diff checks pass. No Windows runtime, GPU, visual, or performance
acceptance is inferred.


## Grouped Hierarchy Duplication

Native hierarchy duplication now uses one owned forest receipt and one Undo
transaction for selected top-level roots. It keeps ordered selection, shared or
deep asset behavior, unique names in shared destinations, guarded member progress,
and complete-ledger recovery. Callback edits to peers reject later owned writes.
Failure dirty marking validates captured document authority independently of the
rejected clone values. Existing single-root construction remains available.

Headless build214 passed with zero warnings/errors in 23.44s. Run161 passed 44/44
forest and replay-helper cases in 0.462s. Full run162 passed **1,289/1,289** in
27.160s, with no failures, warnings, or skips. Ten added editor cases and one
revised native menu case need Windows execution. These portable results cover
actual forest construction and replay expectations, not the full editor Undo
transaction. The code reconciliation remains **51/60**. OS texture-file imports
still lack canonical mutation/recovery handling, so the broad mutation item stays
open. The existing asset-recovery decision remains pending.

Desktop build67 passed with zero warnings/errors in 2m09.08s. All 10,838 source
inputs stayed unchanged through final validation. The 39 maintained relative
links resolve and diff checks pass. Review found no remaining publication blocker
in this bounded transaction. Windows command execution, visual checks, and
performance acceptance remain pending.


## Deferred Destruction Ownership

Deferred destruction now retains exact request tokens. Old entries cannot destroy
a regenerated lifetime, and terminal self-queued requests settle without another
virtual call. Nonblocking dispatch admission protects the request through virtual
Destroy. Normal deferral stays pending; uncertain thrown cleanup does not retry.
Component insertion rejects queued, destroyed, or actively destroying objects at
owner-thread callback boundaries. Prequeue detach/reattach and veto remain valid.

Headless build217 passed with zero warnings/errors in 44.26s. Run165 passed 25/25
cases in 0.189s. Full run166 passed **1,314/1,314** in 20.781s, with no failures,
warnings, or skips. The initial baseline reproduced six failures in nine cases.
The code reconciliation remains **51/60**. General concurrent scene-list editing
and broader menu construction/retirement remain open.


Desktop build68 compiled the editor and tests with zero warnings/errors in
3m25.50s. All 10,841 source inputs stayed unchanged through final validation.
The 39 maintained relative links resolve and diff checks pass. Review found no
remaining blocker in this bounded contract. Windows execution, live menu behavior,
GPU checks, and performance acceptance remain pending.


## Selected Float Curve Lengths

Distinct selected float curves now support Set length and Stretch length through
one guarded transaction. Shared references edit once; every selected owner and
unchanged member remains guarded. Undo retains each curve's own prior timing.
Length input uses the source and revision that completed field refresh, including
group-to-single rebinds. Lifetime revisions reject identity round trips. Dirty
marking skips changed document lifetimes while continuing with valid originals.

The original implementation reconciliation remains **51/60**. Distinct key
authoring and the broader inspector item remain open. ImGui stays the default.


The revised snapshot passed desktop build71 with zero warnings/errors in
1m22.18s. Headless build218 passed with zero warnings/errors in 22.37s. Full
run167 passed **1,314/1,314** portable tests in 21.537s, with no failures, warnings,
or skips. Ten new editor cases compiled only: nine action/history/lifetime cases
and one retained detail case. They did not execute on Linux. All 10,843 source
inputs stayed unchanged. The 39 maintained relative links resolve and diff checks
pass. Final review found no remaining blocker in the bounded length-edit contract.
Windows input, Undo execution, visual behavior, and performance remain unverified.
## Cross-Window Dock Pointer Receipts

Native tab drags can now select an exact stack in another admitted editor window.
The Windows observer supplies a press sequence and a native release receipt with
target client geometry. Polling cannot authorize release. Capture loss, stale
native observations, replaced windows, and changed source owners reject the drop.
Other editor drags keep their input path, and unsupported backends retain
same-canvas tab movement.

The target query uses displayed mapping, shape, clip, scope, and input blockers.
It rejects mismatched native extents, changed component lifetimes, and replaced
presentations. Source closure follows destination acceptance. Exact old slots stay
owned through vetoes and exceptions, while callback replacements survive cleanup.
Active destruction admission prevents nested retirement. Receive rollback pins
its original root and registration. This is a receive-first contract, not atomic
rollback across both windows.

The final headless build and editor/test compilation have zero warnings/errors.
The targeted run passes **59 of 59** cases. The full portable run passes **1,368 of
1,368**, with no failures, warnings, or skips. All **10,862 source inputs** remain
unchanged through the final checks. Source review, 39 relative document links,
and `git diff --check` pass. New portable tests cover production pointer state,
the editor drag-session path, explicit registry delivery, displayed drop regions,
and slot retirement. No native Windows window or User32 call runs in those tests.

The original code reconciliation remains **51 of 60**. Primary-window key
compatibility, complete panel-state transfer, broad menu ownership, and other
remaining code items stay open. Windows capture, overlap, mixed DPI, outside-window
release, native teardown, visual/GPU acceptance, and equivalent ImGui benchmarks
remain in the validation plan. ImGui remains the default editor.


## Live Tool State Across Dock Transfers

Both native hosts now transfer live asset navigation, console settings, profiler
view, and effective tool scroll through one retained binding per slot. Existing
destination slots restore without duplicate subscriptions. Guarded setters preserve
callback edits, reject replaced owners, and distinguish accepted viewport clamping
from a veto. Rows ownership includes its node, parent, and panel ancestry. Both
virtual-bound writes use the same admission. Source closure requires a current
source receipt. Partial failure can leave both hosts open; the transfer does not
provide atomic rollback across windows. Existing saved fields and keys are unchanged.

Final headless build229 passed with zero warnings/errors in 1m35.70s. Targeted
run177 passed **57/57** in 0.379s. Full run178 passed **1,420/1,420** portable tests
in 22.233s, with no failures, warnings, or skips. Desktop build74 compiled the
editor and tests with zero warnings/errors in 2m52.89s. All **10,874 source inputs**
stayed unchanged. Ten new real-panel/host test cases compiled only; they did not
execute in this environment. Final source review, relative-link checks, and
`git diff --check` passed.

The original code reconciliation remains **51/60**. Animation member/key and view
transfer, focus, expansion, complete play-transition state, other open code items,
and the existing owner decisions remain open. Windows interaction, native graphics,
IME, visual behavior, and equivalent-workload performance acceptance remain pending.
A separate Windows validation task is not evidence until it reports executed results.
ImGui remains the default editor.

## Panel activation and cleanup repairs

The repair snapshot passes **20/20** focused portable cases and **1,440/1,440**
full portable cases, with no failures, warnings, or skips. Headless and editor/test builds have
zero warnings/errors. All 10,885 source inputs remained unchanged. It repairs exact transform unlink during activation,
independent debug-handle allocation ownership, queued structural-list removal,
private YAML read/write discovery parity, enum binding retirement, and accepted
text layout on reactivation. The float fixtures now author their expected values
and observe the current child list. Existing behavior assertions remain intact.

The original Windows snapshot exposed the failures; it does not validate this
repaired snapshot. Windows reruns remain pending and are recorded in the
[validation record](../../testing/ui/native-ui-validation.md#windows-fixture-repair-checks).
The two hierarchy stalls are narrowed to row realization; their cause and runtime
acceptance remain open. The separate animation view-transfer draft is preserved
locally and is not included. The reconciliation remains **51/60**, with nine broad
code items and four original owner decisions open. ImGui remains the default.

## Explicit test icon fonts

Retained-control fixtures now install preloaded icon metrics with their text font.
The scoped API validates both fonts before installation and keeps resource ownership
with the caller. The text-only API and normal platform lookup remain unchanged.
Five targeted scope cases and **1,445/1,445** full portable tests pass. Headless and
editor/test builds have zero warnings/errors. All **10,886 source inputs** stayed
unchanged through validation; read-only review found no blocker.

The prior repair checkpoint `68ab6d167` has no repaired Windows result yet:
revalidation is blocked before build/tests by disk capacity, with targeted cleanup
approval pending. The original row-realization stalls and normal font import remain
unresolved. This fixture change does not establish their cause. The implementation
reconciliation remains **51/60**. The animation draft and owner proposals remain
separate, and no platform acceptance or default-editor decision is closed.

The scoped-font checkpoint is now published at `c1bbb973d2946dabdaa4bc7c299600cde8c3dded`.
The unchanged Git route recovered on one authorized retry; the remote SHA matches.
No CI checks or workflows are attached. The revised Animation draft remains stashed
and unpublished. Its toolbar source defects and bounded source/test plan are in the
[continuation investigation](../../investigations/ui/native-ui-continuation-comparison.md#guarded-animation-toolbar-publication-plan).
Its 17 portable value/routing cases do not establish actual panel or host acceptance.

## Guarded shared text publication

Shared input and display controls now provide opt-in guarded text writes. They
retain exact component membership and caller admission through callbacks, stop
stale caret/display writes, and preserve newer edits. Fresh unsuppressed no-op
calls repair derived caret/selection/display state. Ordinary notification behavior
and suppression remain intact. Caller parent/transform proof and renderer rollback
remain outside this bounded API.

The focused run passes **29/29**, and the full portable suite passes
**1,474/1,474**, with no failures, warnings, or skips. Headless and editor/test
builds have zero warnings/errors. All **10,890 source inputs** stayed unchanged.
The no-op recovery case first failed, then passed after the shared-effects fix.

Animation toolbar adoption and actual host compensation remain the next separate
slice. The unsafe draft stays preserved; it is not included in this checkpoint.
Windows execution remains blocked by validation-disk capacity and pending cleanup
approval. No platform acceptance is closed, and the reconciliation remains **51/60**.

## Animation view transfer and guarded toolbar

Both native hosts now transfer clip member/key selection, pages, graph mode, zoom
and window, stretch choice, and value channel. The same fixed receipt guards row
scroll/bounds, both text writes, final scroll, and host saving. Different-clip
compensation is rejected without changing global selection. Deferred ordinary
refresh restores scene-path candidate rows after transfer. Search, draft text,
focus, expansion, and broader play transitions remain open. All stashes are preserved.

Targeted portable tests pass **47/47**; the full portable suite passes
**1,480/1,480**, with no failures, warnings, or skips. Production editor compilation
passes. Two compiler errors in the new host fixture were corrected; test-assembly
compilation then passed with zero warnings/errors. The 33 panel and 4 real-host cases
are **compile-only here**. The final manifest has 10,896 source inputs; only that
editor fixture changed after the portable run, and production inputs stayed fixed.

Windows revalidation at exact `68ab6d167` passed 71 original focused cases. It still
has combined-only asset teardown failures, six canvas-less tool-panel setup
failures, and three incomplete stall cases. That snapshot predates the explicit
fixture-font input. Remaining failures take priority over new focus implementation.
See the investigation for per-run counts; this is not one passing Windows suite.
No GPU, live UI, IME, or performance acceptance is closed. Reconciliation remains
**51/60**, with nine broad code items and four original decisions open.

## Canvas-backed tool-state fixtures

`NativeToolPanelStateTests` now attaches a canvas before constructing console and
profiler panels. This corrects setup for the popup-host contract without changing
assertions or production behavior. Test-assembly build 81 passes with zero warnings
or errors. Windows rerun is pending. The source includes the explicit test-font
input; production font import and combined asset teardown remain unresolved.
The full portable result remains 1,480/1,480 from the preceding source checkpoint.
No broader TODO or platform acceptance is closed.

## Windows fixture and terminal-scroll repairs

The completed Windows run at `a5e2c0872` reports 381/398 passes and 17 failures,
with no skipped or incomplete cases. The canvas-backed tool-state tests pass 12/12;
real-host Animation transfer passes 4/4. The prior stalls now complete with explicit
fixture fonts; production font import remains separate.

The next candidate corrects menu viewport setup, synchronizes provider bindings
before fixture focus publication, and unwraps expected Animation callback
exceptions. It also makes public scroll writes inert after panel destruction while
keeping owned restore guards strict. Four focused portable controls pass; the full
portable suite passes **1,484/1,484**. Windows acceptance of this repair is pending.
The original implementation reconciliation remains **51/60**.

The matching editor/test build 82 passes with zero warnings/errors. All 10,897
source inputs stayed fixed through validation. The working candidate has no
Windows execution result yet; the 17 failures remain acceptance checks.


## Same-panel hierarchy focus checkpoint

Ordinary hierarchy rows retain transient focus across stop/reactivation of the
same panel. Exact input intent, source and owner lifetimes, row lease, and row
presentation must remain current. Newer focus and callback vetoes stop the old
request. A vetoed exact clear keeps the prior keyboard registration. Selection
is unchanged. Cross-host focus, draft/search state, and full play-mode parity
remain open, so the code reconciliation stays **51/60**.

Headless build 252 and editor/test build 84 pass with zero warnings/errors.
Targeted run 207 passes 152/152; full run 208 passes **1,508/1,508**, with no
failures, warnings, or skips. All 10,905 source inputs remained fixed through
those checks. Twenty-three new portable guarded-focus cases execute; sixteen
new editor lifecycle cases remain compile-only. The text-layout cache fixture
now verifies deterministic direct-mapped eviction without requiring storage
retention across competing keys. Production cache code is unchanged.

Parent-reported Windows validation at `33340544` passes all 17 earlier failures,
but its broader run passes only 398/400. Two combined-only asset-spawn teardown
failures remain at the exact structural-list guard. A three-test selection
reproduces one failure twice. The finalizer-order hypothesis needs a throw-site
diagnostic; this checkpoint contains no cleanup production repair. The focus
candidate has not had Windows execution. Owner decisions and live acceptance
remain open; ImGui stays the default editor.


## Private structural-list finalization checkpoint

Windows execution at `2dc5df71` passes all 16 new editor focus cases, 40
hierarchy/lifecycle cases, and 32 input-focus cases. Its broad run passes
415/416 with one hidden-scene prefab teardown failure. Separate throw-site
evidence shows that the exact active private list is destroyed while its
transform owner is alive. The first destroyer is not identified.

A deterministic portable baseline reproduces the same ownership rejection when
the private list finalizer runs before its owner finalizer. The repair defers
independent finalizer cleanup of exact installed private lists to their owner.
Explicit destruction, queue tokens, owner vetoes, and exact-owner guards retain
their existing contracts. Replaced and ordinary lists still finalize themselves.

Headless build 257 passes with zero warnings/errors. Targeted run 212 passes
56/56, including eight finalizer regressions; full run 213 passes **1,516/1,516**.
The two baseline failures now pass under unchanged assertions. Windows
revalidation of this repair remains pending. Rename restoration is preserved
separately and is not published by this checkpoint. The implementation
reconciliation stays **51/60**; owner decisions and live acceptance remain open.

Matching editor/test build 85 passes with zero warnings/errors (4m47.57s).
All 10,906 source inputs stayed unchanged through build 257, runs 212/213, and
build 85. This is managed compilation on Linux; Windows execution of this repair
is still pending. The separate rename draft is not part of the tested snapshot.

Windows validation now accepts exact `a30061f4c` for the reported cleanup scope:
8/8 finalizer regressions, 3/3 minimal reproducer cases, and 448/448 combined
UI/focus cases pass, with no failures, skips, or incomplete cases. Both prior
asset-spawn teardown cases pass. Build warnings/errors are zero; assertions and
time limits are unchanged. This does not cover the separate rename candidate
or live editor, GPU, native IME, and performance acceptance.


## Same-panel hierarchy rename checkpoint

Ordinary focused rename inputs retain transient draft text, caret, and selection
across stop/reactivation of the same hierarchy panel. Source and destination
ownership, row bindings, visibility intent, and newer text/focus intent bound
the operation. The shared draft-preserving release ends the exact IME host
registration and edit overlay without committing, canceling, or formatting the
draft. Ordinary focus clear keeps its existing behavior. Rejected gains preserve
callback edits; vetoed release retains keyboard registration.

The implementation does not add saved fields or cross-window transfer. It does
not restore an active IME composition or a CommitOnSubmit draft. Search, broader
play-mode and cross-host state, paragraph analysis, asset recovery, window-key
compatibility, and live acceptance remain open. The original reconciliation
stays **51/60**. ImGui remains the default.

The full portable suite passes **1,574/1,574** in run 228, with no failures,
warnings, or skips. Targeted run 216 passes 122/122. Build 261 and test-only
build 262 pass with zero warnings/errors. The exact cleanup reproducer passes
2/2 after the fixture removes its test callback before destruction; the original
focus assertions and production ownership guards are unchanged. The matching
editor/test build 87 passes with zero warnings/errors (4m02.09s). All 10,918
source inputs stayed unchanged through build 262, runs 227/228, and build 87.
The 40 new editor cases compile; their Windows execution remains pending.


## Hierarchy rename display repair

Windows `24621256` passes 581/583 combined cases; all prior 448 cases remain
passing. One new fixture reads retired state through a creating getter. It now
reads the captured original display. The other failure exposes duplicate text
components in hierarchy rename rows. Creating text before input removes the
duplicate and makes the input use the intended configured display. Original
ownership and replacement assertions remain intact; a new editor case checks
the single-display contract across reactivation. Windows revalidation is pending.

The unrelated animation candidate-view implementation remains in local stash
`072cee153cbd89afdeb448afb0fddca31986222c`; it is not part of this repair. Its
portable tests and compilation do not establish Windows acceptance.

For the isolated rename repair, headless build 264 passes with zero warnings/errors
(15.60s), and full portable run 231 passes **1,574/1,574** (27.108s), with no
failures or skips. Editor/test build 89 passes with zero warnings/errors (1m28.75s).
All 10,918 source inputs stayed fixed through these checks. The 41 rename cases
compile, including the new single-display case. Windows reruns are still pending;
this is not live editor, GPU, native IME, or performance acceptance.



## Animation candidate-view checkpoint

The live dock-transfer receipt now carries the committed animation path query,
result page, and selected stable path ID. Destination discovery remains deferred;
it resolves selection against fresh results for the same scene root and clamps
the page. Root changes discard the old position. The search field joins guarded
time/value publication and host-save compensation. No saved fields change.
Unsubmitted drafts and cross-host keyboard focus remain open.

Headless build 263 passes with zero warnings/errors. Targeted run 229 passes
23/23; full run 230 passes **1,579/1,579**, with no failures or skips. The matching
editor/test build is pending. Fifteen new editor cases require Windows execution;
their results are not covered by the earlier frozen rename validation. The
original code reconciliation stays **51/60**, and all owner-decision holds remain.

Documentation still uses its existing paths. PR 87 reconciliation owns a frozen
`2462125639af4750609c88730e96866d547a1f86` snapshot. This later checkpoint changes
retained execution, continuation investigation, progress, validation, and the UI
TODO; those five documents need later reconciliation by that task.

Editor/test build 88 passes with zero warnings/errors (3m58.24s). The 10,922
source inputs stayed fixed through full portable run 230 and this build. Windows
execution of this candidate remains pending. The candidate is preserved locally
while two separately reported Windows rename failures at `24621256` take priority.


The integrated candidate passes editor/test build 90 with zero warnings/errors
(6m46.33s). All 10,922 source inputs stayed unchanged from headless build 265
through full run 232 and editor/test build 90. The portable result is
**1,579/1,579** (31.839s), with no failures or skips. The 15 added editor cases
compile but have not run on Windows. The published rename repair at `fdf190c3`
also awaits its Windows rerun. These checks do not establish live editor, GPU,
native IME, or performance acceptance.


## Animation empty-clip fixture correction

Windows validates the published rename repair `fdf190c3`: **584/584** combined
cases pass. The affected animation checks at `e4a1d9a7` pass **74/75**. The one
failed fixture bypassed the production asset event with a reflected private
setter, leaving the old clip displayed. Its capture guard correctly rejects
that mismatch. The test now proves this rejection, refreshes both panels, and
then runs the original positive empty-clip assertions. Production is unchanged.
The exact repaired case still needs Windows execution.

Read-only collection preview work is preserved separately in stash `53c888c5`.
It is not included in this repair. The code reconciliation remains **51/60**;
all existing owner decisions and live acceptance checks remain open.


The isolated fixture correction passes focused editor build 95 (43.66s) and
test build 96 (43.72s), each with zero warnings/errors. All 10,922 source inputs
stayed unchanged through these checks. No portable suite was repeated: this
change modifies one Windows fixture and documentation only. The corrected case
still requires its focused Windows execution; compilation is not that result.



## Read-only collection preview checkpoint

The inspector's remaining eager `ICollection` preview now uses shared fixed-height
virtual rows. It formats only realized cells and preserves the existing read-only
snapshot contract. It does not enumerate or format the entire collection on each
frame. The initial snapshot still enumerates all items once.

Windows validation of published rename repair `fdf190c3` passes **584/584**
combined cases, including both reported failures. The affected animation results and the isolated fixture correction at
`44d60fb72` are recorded above. This new
collection slice has four new editor cases and six existing affected retirement
cases; their execution remains pending. The unchanged portable suite is not
repeated. The original reconciliation stays **51/60**.

The documentation changes after the private-doc reconciliation snapshot
`24621256` remain the same five paths: retained execution, this checkpoint,
continuation investigation, native UI validation, and the native UI TODO. No
private repository, PR branch, or documentation destination is changed here.


The final preview editor build 92 passes with zero warnings/errors (44.77s).
Test build 93 found two fixture API errors (`SetParent` overload and the list
scroll property). The fixture now uses `Parent` and `ContentScrollOffset`; no
assertion changed. Focused test build 94 passes with zero warnings/errors
(40.81s). All 10,925 source inputs stayed unchanged through the final test build.
No new case executed on Linux; the editor assembly has a Windows platform guard.
The 10-case focused Windows selection remains pending. This candidate is preserved
separately while the reported animation empty-clip capture failure is repaired.


The preview candidate is restored on top of `44d60fb72`. Integrated editor
build 97 passes in 34.89s and test build 98 passes in 42.07s, both with zero
warnings/errors. All 10,925 source inputs stayed unchanged through these checks.
The focused builds reuse unchanged project dependencies; no full portable suite
was repeated. The four new preview cases and six affected retirement cases
compile. Their Windows execution and the live preview check remain pending.
All six stashes remain preserved.


## Selected float-curve Add checkpoint

Distinct selected curves now accept Add at one common time through one guarded
history action. Each unique curve receives its own default value. The retained
control rejects stale presentation and owner changes. The action limits the
selection to 64 curves and the resulting total to 100,000 keys. Existing-key
editing and removal across distinct curves remain open. The original code
reconciliation stays **51/60**; this is a bounded part of inspector parity.

Editor build 99 and test build 100 pass with zero warnings/errors (44.24s and
48.63s). All 10,927 source inputs stayed unchanged. Eleven new editor cases
compile; Windows execution of the two affected float-curve fixtures is pending.
No unchanged full portable suite was repeated. The existing platform guard
remains in place.

Windows checks at exact `ecaecde29` pass **11/11**, including the empty-clip
fixture correction and collection-preview retirement checks. No case was
skipped or incomplete. These results do not establish live editor, GPU, IME,
cross-host focus, or performance acceptance. The seven owner decisions remain
held; ImGui remains the default. The same five documentation paths changed after
the private-doc snapshot `24621256`. All six stashes remain preserved.


## Fixed-height linked-list checkpoint

Editable linked lists now create virtual rows for known fixed-height built-in
editors. Shared source state preserves Set and Remove history; exact row/control
admission rejects stale recycled bindings. Taller float-animation and custom
editors keep their existing eager layout. Dictionary rows, variable-height
virtualization, nested collection ownership, and broader inspector parity remain
open. The original reconciliation remains **51/60**.

Editor build 102 and test build 104 pass with zero warnings/errors (38.40s and
46.93s). The final source manifests contain 10,931 inputs. Seven new Editor cases
compile but are **not run on Windows**. The prior grouped-key cases also remain
not run. No full portable suite was repeated. Laptop work remains stopped.

The branch resumes from the signed, tree-equivalent tip `c5895511`; new work uses
the configured `BlackJaxDev` identity and verified commit signing. Six stashes
remain preserved. Documentation changes after the private migration snapshot
remain limited to retained execution, this checkpoint, the continuation
investigation, the validation plan, and the native UI TODO. No private repository
or migration branch is changed.

Remaining code includes broader world rendering/clipping, dictionary and tall
inspector rows, panel/animation parity, shared-control integration, cross-host
focus and lifecycle state, and mutation routes held by the asset-recovery decision.
The seven owner dependencies remain open. Live Windows input, GPU/visual checks,
IME/accessibility, cross-window behavior, and equal-workload performance acceptance
remain separate from code and compilation. ImGui remains the default.


## Fixed-height dictionary checkpoint

Eligible editable dictionaries now share the fixed-height row presenter with
linked lists. The route uses captured keys, existing dictionary bindings, and the
existing mutation/Undo contracts. Row reuse rejects stale edits and Remove
callbacks. Partial-key selections and multiline key labels keep their eager
presentation. Custom and taller editors remain outside this fixed-height route.
The original reconciliation remains **51/60**; this closes a bounded dictionary
subtask, not the broader inspector item.

Four new Editor cases cover bounded 10,000-entry realization, Set/Remove replay
with exact key identity, stale recycled callbacks, and both eager fallbacks.
They are compile-only here. Laptop work remains stopped, and no Windows, live UI,
GPU, or performance result is claimed. The full portable suite was not repeated
for this Editor-only change. Opening still captures O(N) source state.

Remaining independent code includes broader property-row virtualization, panel
and animation parity, and retained cross-host state. Variable-height/custom
editor policy and the other six owner dependencies remain open. World rendering,
live input, IME/accessibility, docking, and equal-workload performance acceptance
still need their recorded platform checks. ImGui remains the default. Six stashes
remain preserved; no private documentation or migration branch is changed.

Editor build 105 and test-project build 106 pass with zero warnings/errors
(42.02s and 47.34s). Both use `Tools/Invoke-AgentBuild.ps1`, `--no-restore`, and
`-p:BuildProjectReferences=false`. Each manifest covers 10,934 source inputs,
unchanged during its build. Only the fixture lookup changed between builds;
production sources stayed fixed. Existing unchanged dependency assemblies were
reused. These are compilation results, not Windows test execution.


## Same-panel Animation search draft checkpoint

Animation search now has an explicit transient stop/reactivation contract. It
retains the existing command field and leaves unchanged draft text, caret, and
selection alone. It does not submit the draft or acquire focus. Scene/asset
publication revisions and a temporary component observer reject source changes
away and back. A changed source resets only a still-owned old draft to the
committed query. Newer edits, regenerated owners, and transfers cancel that write.

The input pre-activation hook and panel activation hook support subtree and
component-only stops respectively. The lifecycle serial is separate from view
refresh. Reentrant activation does not register duplicate selection handlers.
The [lifecycle matrix](../../../architecture/ui/05-Retained-Execution.md#transient-animation-search-drafts)
lists unsupported composition, host, and saved-state paths. Normal base input
activation still publishes its display; no shared text-control behavior changed.

The original reconciliation remains **51/60**. This is one state-preservation
subtask, not full docking, text, or play-mode acceptance. The prior grouped-key
**11**, linked-list **7**, and dictionary **4** new cases remain separately not
run on Windows. No laptop worker or full portable suite was started. No private
repository, saved format, owner-held proposal, or editor default changed.

Final editor build 111 and test-project build 112 passed with zero warnings and
zero errors (49.18s and 48.87s). Both ran through `Tools/Invoke-AgentBuild.ps1`
with `--no-restore` and `-p:BuildProjectReferences=false`. Only Editor and
Editor-test code changed; unchanged dependency assemblies were reused. The
10,937 source inputs were identical across both final builds. All 25 new cases
compile; none ran on Windows. No live/GPU/IME/performance result is claimed.


## Guarded ordinary menu construction checkpoint

Built-in menu rows, labels, submenu arrows, separators, and separator lines now
use the existing guarded component and detached-leaf retirement receipts.
Configuration completes before attachment. Callback checks retain the exact
show, content root, parent, component, and row source. Only completed rows join
navigation and click handling. Cancellation retires an unchanged detached leaf;
foreign adoption stays intact and keeps failure evidence.

This closes the ordinary row/decoration construction subtask. Content-root and
scroll helper factories, arbitrary custom builders, and adopted-subtree retirement
remain open. The original reconciliation stays **51/60**; shared-control parity
is not complete. No owner-held proposal, saved format, editor default, or private
documentation destination changed.

Final headless build 115 passed with zero warnings/errors (1m28.14s), through
`Tools/Invoke-AgentBuild.ps1` with `--no-restore` and project references enabled.
All 10,939 source inputs stayed unchanged through the build and tests. The 19 new
cases passed first. Then the five affected fixtures passed **187/187**, including
those 19 cases (4.675s; no failures, skips, or incomplete cases). Review found no
remaining blocker in this scope. No full suite was repeated. Windows/live/GPU
acceptance remains not run. The 25 Animation search cases and prior grouped-key
11, linked-list 7, and dictionary 4 Editor cases remain separate and unrun.


## Ordinary menu root and scroll checkpoint

The ordinary scroll root and list now use the existing menu leaf builder. Their
final transforms and settings are ready before attachment. The scroll component
factory is guarded, root metadata is captured immediately before publication,
and rejected attempts preserve transferred content and newer requests. This
closes ordinary root/list construction, not all menu ownership work.

Headless build 116 passed with project references enabled: zero warnings/errors,
2m44.79s. The affected run passed 198/201. Three existing assertions expected the
old pre-configuration root name. The reviewed fixture correction now writes a
new-owner sentinel name inside the ownership callback and requires it to survive;
all other ownership, failure, cleanup, and recovery assertions remain unchanged.
Only that fixture changed after build 116. Build 117 reused the unchanged project
references and passed with zero warnings/errors in 12.83s. The final affected
run passed **201/201**, including 14 new cases, in 4.728s. The 10,941 final source
inputs stayed unchanged through the final build and run.

No full suite, laptop worker, or Windows/live acceptance run started. The separate
25 Animation, 11 grouped-key, 7 linked-list, and 4 dictionary Editor cases remain
unrun. The original reconciliation remains **51/60**. Existing-node backdrop and
virtual scroll factories remain separate bounded code work. Custom-content and
adopted-subtree retirement need an explicit ownership contract; the current
receipts do not admit a previously adopted arbitrary menu graph. No private
repository, owner-held proposal, saved format, or editor default changed.


## Existing-owner menu component checkpoint

Backdrop and virtual-scroll factories now use guarded component receipts on their
existing nodes. Failed never-inserted claims abort only while their owner is
unchanged. Inserted/foreign components are preserved. Committed creation finishes
before queued cancellation; a genuine incomplete backdrop cannot be reused as
ready. Virtual setup pins the provider reference and incarnation before callbacks
and validates publication. Arbitrary provider/custom graph ownership is unchanged.

Build 118 passed with project references enabled (zero warnings/errors,
2m57.92s). The first affected run passed 219/221. Two new tests assumed immediate
component destruction after virtual-menu cleanup; the existing adopted-owner path
queues component destruction. The reviewed fixture now checks queued destruction,
cleared activation, destroyed owner, and activation before cleanup. Production
code did not change. Build 119 reused those references and passed with zero
warnings/errors in 11.85s. The final **221/221** affected cases passed in 5.548s,
including 20 new cases, with no skips or incomplete cases. All 10,943 final source
inputs stayed fixed. No full suite, Windows run, or laptop worker was started.
The 25 Animation, 11 grouped-key, 7 linked-list, and 4 dictionary Editor cases
remain separately unrun.

## Remaining scope after the menu factory work

The original reconciliation remains **51/60**, with nine broad items open. This
map separates code from owner decisions and platform acceptance; it is not a new
checklist or permission request.

| Open area | Concrete code/contract remainder | Holds and acceptance |
|---|---|---|
| Direct-world clipping | Vulkan direct-world publication/clipping and unsupported world output profiles; bounded OpenGL canvas output already exists. | World-shadow and bindless-use choices remain open. OpenGL/Vulkan GPU and visual checks remain required. |
| Inspector | Existing-key time/value/handle edits across distinct selected curves; explicit one-key-per-curve removal is implemented; multiline dictionary cases; tall/custom collection editors and remaining nested row coverage. Top-level property rows and supported fixed-height collections are already virtual. | Variable-height priority is an owner decision. Real-host multi-object Undo and scale checks remain. |
| Native panels | Remaining AnimationPanel authoring, broader hierarchy clone profiles, and document-end cleanup; full asset creation/recovery remains absent. Existing asset navigation, console, and bounded profiler views must not be reimplemented. | Persistent asset recovery proposal is held. Native panel workflow/visual acceptance remains. |
| Shared controls | All ordinary menu factories now use existing guarded construction, including submenu host/component creation and matching close ownership. Custom-content ownership and reserved adopted-subtree retirement need a clear graph contract. Full paragraph/fallback/accessibility coverage remains. | Paragraph analyzer choice is held. Native input, IME, accessibility, and visual checks remain. No arbitrary adopted graph was admitted. |
| Docking | Remaining cross-host focus/transfer-state parity. Existing dock tree mutation and tool-view transfer receipts are implemented. | Primary-window key migration is held. Mixed-DPI, pointer capture, overlap, and multi-window checks remain. |
| Lifecycle state | Cross-host search drafts/focus, expansion, and complete play-mode paths. Same-panel hierarchy focus/rename and Animation search continuation are implemented. | Windows/live acceptance remains separate; the laptop is stopped. No new saved-state format is authorized. |
| Drag/drop and Undo | `EditorFileDropHandler.ProcessFileDrop` still copies/imports and assigns a material slot outside shared Undo/dirty transactions. Canonical hierarchy mutations and guarded reference drops already exist. | The filesystem recovery design is held; do not implement a journal or change asset formats without that decision. |
| Documentation | Keep public descriptions current and reconcile the native branch's post-migration changes with the private snapshot through its owning task. | This task changes only the same five public documentation paths. No private writes or migration merge. |
| Native default | Default switch is deliberately absent. | Owner stabilization decision plus Windows/GPU/workflow/performance acceptance; no faster-than-ImGui claim without equivalent workloads. |

The three pending proposals are paragraph analysis, persistent asset recovery,
and primary-window key compatibility. The four original owner choices remain
variable-height priority, bindless image policy, world shadows, and stabilization.
None was resolved by the menu work. The adopted-graph issue is a missing contract,
not a reported permission denial.

The next product code work can focus on existing multi-curve key edits
or a bounded remaining inspector collection case under the current fixed-height
policy. Ordinary menu factory integration is complete; menu implementation stops
at the custom/adopted ownership boundary. Do not replace these concrete gaps with
unrelated callback hardening.


## Guarded submenu construction checkpoint

`OpenItemSubmenu` now uses the existing detached leaf and guarded component
receipts with its final transform. The candidate stays private through
attachment. Opening uses existing transition gates for the owner chain and
candidate; captured requests drain only after scene mutation ends. Exact source,
attachment, and show-version checks preserve transferred and independent shows.
Close ownership survives hidden retention, and queued child Hide cleanup waits
until the child transition ends. The opening guard covers switching dismissal;
post-focus results cannot accept a replacement show.

All ordinary menu factories are now covered, including matching submenu close
ownership. Existing-node backdrop/virtual-scroll factories are covered as well.
Custom/provider content graphs and stronger reserved retirement of arbitrary
adopted subtrees remain outside these contracts. No borrowed-graph policy was
invented. Ordinary menu implementation stops here. The next bounded product
chunk should come from the remaining-scope map above.

Wrapper build 122 passed with zero warnings/errors. The matching unchanged
source snapshot passed **243/243** affected portable cases, including all **22**
new submenu cases. No full suite ran; the earlier 235/239 run and its repairs are
recorded in the investigation and validation documents. The **47 Editor cases**
remain separately unrun. No Windows/live/GPU/IME/performance acceptance is added.
The broad implementation reconciliation remains **51/60**.

This checkpoint changes only the same five public documentation paths listed
for post-migration reconciliation. The private snapshot remains frozen at
`2462125639af4750609c88730e96866d547a1f86`; this task makes no private writes.


## Explicit grouped key removal checkpoint

Distinct selected float curves now support an explicit removal target on each
curve. Previous/Next removal curve chooses the displayed curve; Mark / Unmark
key for removal records the exact selected key. Row focus alone does not mark.
Remove marked keys requires every participating target. Shared curves mutate
once while all selected owners remain validated. Limits remain 64 distinct
curves and 100,000 captured keys, with the existing 8,192-row display limit per
chosen curve. No time matching, grouped value/time edits, or general table
multi-selection was added. The single-curve workflow remains in place.

One existing animation transaction owns the removal and Undo/Redo. Full payload,
frame, order, peer, and generation checks preserve callback edits; incomplete
compensation retains guarded recovery. UI commands and refresh require exact live
view ownership. Retirement preserves table data transferred during recycling.
Newer marking intent survives an older completion.

The matching editor and test source compiles. The final test-project and headless
wrapper builds pass with zero warnings/errors. **77/77 affected portable tests**
pass, including **nine new model cases**. The new **nine backend plus eleven UI
Editor cases are compiled but unrun**. With the prior 47, **67 Editor cases now
await Windows execution**. No laptop, full-suite, GPU, IME, visual, or performance
run is implied. The investigation records the intermediate compiler corrections.

This closes the bounded grouped-removal subtask and stops implementation for
review. Existing-key time/value/handle editing and other inspector gaps remain;
the broad reconciliation stays **51/60**. All owner-decision holds remain. Only
the same five public documentation paths changed; private reconciliation remains
with its owning task and the frozen `2462125639af4750609c88730e96866d547a1f86` snapshot.


## Partial-key dictionary virtualization checkpoint

Supported partial-key dictionary selections now use the shared fixed-height row
presenter. Primary key order and displayed keys stay unchanged. Common keys use
existing multi-owner mutation and Undo; missing-key rows show the primary value
read-only, without creating keys in another owner. Recycled rows detach old
bindings and Remove actions before switching presentation. Single-line key and
read-only text admission retains multiline/custom fallback. Admission still
captures source state and formats missing-key text; only controls are bounded
to viewport plus overscan.

The affected portable run passed **61/61**, including **14 new row-plan cases**.
Headless wrapper build 127 and full editor/test wrapper build 128 passed with
zero warnings/errors. The seven dictionary Editor cases compile but remain unrun:
four prior cases (one revised) plus three new cases. The prior **67** unrun Editor
cases therefore become **70**, not 74. No full suite, laptop, GPU, native input,
IME, visual, or performance run occurred. The broader reconciliation stays
**51/60**; multiline/custom/nested inspector coverage and grouped key editing
remain open. Value-assignment planning adds no code in this checkpoint.

Only the same five public documentation paths changed after the private migration
snapshot `2462125639af4750609c88730e96866d547a1f86`. No private writes occurred.


## Grouped values, hierarchy views, and Vulkan crop proof

The grouped inspector now assigns an outgoing value to one explicitly marked key
per distinct curve. It preserves each key's synchronization behavior, full authored
payload, time/frame/order, and peer state in one Undo transaction. Notification
revisions detect callback value changes that return to their old value. Marking
labels now describe shared key targets rather than removal-only targets. Incoming
values, other handles, and grouped time edits remain follow-up code.

Native dock moves transfer hierarchy expansion, effective scroll, and editor-scene
visibility for the same live document. Exact path, provider, intent, and scrollbar
proofs protect restore and admitted partial-restore compensation. Selection, focus,
and rename drafts are not part of this receipt. No saved format changed.

The canonical Vulkan direct-world route checks compiled stages, reflected members,
descriptor offsets, mapped allocation lifetime, and actual crop/model uniform bytes.
A new multi-record publication test verifies painter order and frozen nested crops.
Cropped custom/stereo shaders and arbitrary 3D overrides remain unsupported; graphics
acceptance is separate. The broad reconciliation remains **51/60**.

Focused portable runs now have **138 distinct passing latest results**. These are
separate affected runs, not one full-suite pass: 102 cases with one corrected
notification fixture rerun, 35 geometry/visibility cases, and 35 world-publication
cases with 34 overlapping earlier cases. No full suite was repeated. The new
**13 grouped-value backend, seven grouped-value UI, and 15 hierarchy-transfer
Editor cases are compile-only**. With the previous 70, **105 Editor cases remain
unrun**. Laptop, GPU, native input, IME, visual, and performance checks did not run.

The one-level nested-list draft is not published in this checkpoint. Review found
that callback-transferred or replaced detail panes need exact attempt ownership
through construction and retirement. The draft is preserved in a separate local
stash; all six earlier stashes remain unchanged. No owner-policy choice is implied.
Only the same five public documentation paths changed after the frozen private
migration snapshot. This task made no private writes.

The frozen checkpoint passes Headless, Editor, and test-project compilation through
wrapper builds 143–145 with zero warnings/errors. All 10,975 source/project hashes
remained unchanged. Runtime Rendering and Vulkan also compile cleanly.


## Exact curve edits, clone handoff, and shared authoring actions

Marked keys now support explicit time, incoming/outgoing value, tangent,
interpolation, synchronization, weight, and weighted-mode assignments. Time moves
retain exact order and authored frames for Undo; handle staging keeps each key's
own synchronization behavior. Shared batch code rejects callback changes and
compensates only still-owned writes. The single-curve workflow is unchanged.

Applied structural clone history now settles into its exact live authored scene
owners. Only admitted private built-in constructor storage is retired. Borrowed,
nonempty temporary, custom, foreign, and unsupported asset allocations keep their
recovery receipts. No document-close destruction policy was added.

Model-submesh material drops now retain exact LOD materials in one Undo action and
mark explicit source documents dirty on apply and replay. Shared scene visibility
and unload actions preserve exact scene order, independent loaded state, and
selection intent. Dirty callbacks cannot redirect writes to a former source or
suppress another still-owned document's dirty mark.

Focused portable runs pass **72 distinct cases**: 31 construction/retirement,
10 grouped-edit batch, and 31 scene/material/root-ownership cases. No full suite
was repeated. New Editor coverage is **89 cases**: 21 added handle-backend cases,
18 time-backend cases, 13 added handle/time UI cases, 10 clone-handoff cases,
13 material-action cases, and 14 scene-action cases. They remain Windows-unrun;
with the previous 105, the backlog is **194 cases**. Previously executed custody
fixtures with changed assertions also need affected Windows revalidation.

The separate 19-file nested-list draft and its 21 cases remain unpublished and
outside that count. Two local draft stashes preserve the original and reviewed
forms; the six earlier stash IDs are unchanged. Remaining independent code starts
with that fixed-height detail integration and transient cross-host Animation
search draft/focus state. Broader custom/stereo/3D shader profiles need an explicit
render contract. Custom collection/adopted-content policy, analyzer choice, asset
recovery/creation, window-key migration, shadow/bindless policy, and the native
default remain held. Windows/live/GPU/IME/performance acceptance remains separate.
The broad code reconciliation remains **51/60**. No private documentation or laptop
work occurred; the same five public document paths changed after the migration
snapshot.

The final independent snapshot contains 10,998 source/project inputs. Editor
wrapper 156 passed in 46.79s and test-project wrapper 157 in 46.44s, each with zero
warnings/errors. The source manifest remained unchanged. Data subset build 146,
Core 150, Rendering 151, and Headless 153 also pass. Only the two Editor dirty
helpers and their fixtures changed after the earlier 154/155 compile; no portable
production input changed, so the passing focused runs were not repeated.


## Fixed-height nested collection details

One-level built-in inner lists now have retained details reached from list,
linked-list, and dictionary rows. Scalar and nonflags-enum editors reuse the
existing binding/Undo paths. Durable outer paths survive row recycling, while
source replacement and owner/node/document lifetime changes reject writes and
replay. Dirty settlement preserves valid independent source documents after
history or dirty callbacks. Exact pane/control construction preserves transferred
objects and retains failed cleanup receipts.

This closes the supported one-level fixed-height nested-detail subtask. Recursive,
custom, and variable-height details remain open. The broad reconciliation stays
51/60. The animation search-transfer candidate is isolated for a separate
pre-receive ownership repair; it is not part of this checkpoint. Its 12 Editor
cases remain unpublished and outside the published count. All six original
stashes and the two earlier nested-draft stashes remain unchanged.

The isolated source snapshot contains 11,010 inputs, unchanged through validation.
Editor wrapper build 163 passed with dependencies in 6m19.36s; test-project build
164 passed in 48.05s; Headless build 165 passed in 14.86s. All report zero warnings
and errors. The focused pane, detached-leaf, and guarded-component run passed
**47/47** in 0.370s, with no skips. No full suite was repeated. The 30 new Editor
cases compiled but did not run, bringing the published new-case backlog to **224**.
No Windows, GPU, live Editor, native input, IME, or performance checks ran.


## Remaining scope after the bounded authoring changes

| Open item | Implemented boundary | Remaining code or decision | Acceptance still needed |
|---|---|---|---|
| Direct-world clipping | Canonical OpenGL/Vulkan crop and uniform proofs | Define custom/stereo and arbitrary 3D override shader profiles before extending `UIWorldCropShaderContract` | GPU crop, painter order, stereo and live output |
| Inspector collections | Fixed-height ordinary, linked, dictionary and one-level nested details; exact grouped curve edits | Variable-height/custom policy and recursive/custom nested-value contracts | New Editor fixtures, visual layout and measured large-list cost |
| Native panels | Shared primary panels, bounded diagnostics, exact scene/material actions and structural clone handoff | Broader clone container/custom-transform ownership and authored document-end cleanup; asset recovery/creation decision | Changed custody and panel fixtures; live authoring workflows |
| Shared controls | Ordinary control and menu factories use guarded construction | Paragraph analyzer and arbitrary custom/adopted-content retirement policy | Native input, IME, accessibility, visual and stress checks |
| Docking | Exact pointer transfer and live tool-state receipts | Primary-window saved-key decision; broader focus parity beyond ordinary admitted toolbar targets | Native capture, mixed DPI, overlap and multi-window behavior |
| Lifecycle state | Same-panel hierarchy rename/search and live hierarchy/view receipts | Define any additional panel/play-mode state contract rather than infer it | Windows lifecycle and cross-host focus cases |
| Drag/drop | Exact scene, duplication and model-material mutation history | `EditorFileDropHandler.ProcessFileDrop` copy/import Undo depends on held asset-recovery policy | Live drops, persistence and error recovery |
| Documentation | Public architecture, investigation and validation records updated with each checkpoint | Reconcile later public changes with the separate private migration when authorized | Link and claim review at closeout |
| Native default | ImGui remains default | Owner stabilization decision after acceptance | Full agreed platform and performance acceptance |

The source audit found no further concrete independent Animation authoring control
beyond the implemented clip, channel, curve, key, playback, and view operations.
The remaining profile and policy entries are unresolved contracts, not completed
features. No custom shader injection, borrowed-container destruction, file-journal
policy, private-document write, or default switch was inferred from this audit.


## Animation search draft transfer

Both hosts now transfer unsubmitted search text and caret/selection separately
from committed query/results. Existing inactive destinations retain a pre-receive
intent receipt through activation and first save. Later rejected transfers
compensate only still-owned destination writes. Structural rollback suppresses
search authoring for the exact captured input/display and preserves newer edits.
Ordinary presented focus is conservative; virtual targets keep their existing
lease-aware dispatch path. No saved format, window activation, or default changes.

The candidate adds **23 Editor cases**, compiled but Windows-unrun. With the
previous 224, the published backlog becomes **247 new Editor cases**. The earlier
11 grouped-key, seven linked-list, and four dictionary cases remain included and
unrun. Native/GPU/IME/multi-window/performance acceptance remains open. The broad
reconciliation remains **51/60**; the table above separates unresolved contracts,
owner decisions, and platform acceptance. All nine local stashes are preserved,
including the six original stashes. Only the same five public document paths have
changed after the frozen private migration snapshot; no private writes occurred.

The final 11,016-input source snapshot remained unchanged through Editor build 172
(43.99s) and test-project build 173 (48.63s), both with zero warnings/errors. The
last repair changed only Editor rollback logic and its tests. Runtime inputs remain
identical to the 88/88 passing focused run, so that run was not repeated. The 23 new
Editor cases compiled but remain Windows-unrun; cumulative published backlog: 247.

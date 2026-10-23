# Retained UI Implementation Checkpoint

Date: 2026-10-23
Status: Active implementation
Scope: [Native UI TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
Architecture: [Retained execution](../../../architecture/ui/05-Retained-Execution.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

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

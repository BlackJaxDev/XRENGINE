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

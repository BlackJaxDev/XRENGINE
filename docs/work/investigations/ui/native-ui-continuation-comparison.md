# Native UI Continuation Comparison

Date: 2026-10-25
Status: Active

## Compared inputs

The published base was `8a44a1c33548e02a597bc32291f2112978764e58`.
The fetched continuation is `7648531f02f7820cc65cf0029e1aa8055bf715b5`:
22 commits and 78 changed paths after that base. The previous local work was
not a published commit. It is preserved in local stash
`7c555082f1bef5755f00998e4d546479c017704f`, with an additional archive and binary
patch in the task's ignored validation evidence directory. The stash includes
the 28 untracked paths in its third parent; do not apply it over the continuation.

Of the 62 preserved local task paths, 35 exist with different contents at the
fetched tip and 27 are absent there. No preserved path is byte-identical to its
fetched counterpart. These are path and content comparisons, not a claim that
all behavior differs. Several features use different types and algorithms.

The last old local compile, headless build147, failed with two errors in
`UIContextMenuNavigationTests`: inaccessible `BoundableTransform` use and an
invalid event subscription. The prior 994-test result applies to an earlier
local snapshot. Neither result validates the fetched continuation.

## Source comparison

| Area | Earlier local work | Published continuation |
|---|---|---|
| Sparse capture | Packet-owned baseline, COW candidate, exact pointer-source checks; admission disabled. | Active canvas-owned sparse state in `UICanvasComponent.SparsePublication`, count-changing record splices, and shared-page batch collection. This is a different implementation; its full ownership and producer-invalidation contract needs a separate audit. |
| Wheel input | `IUIScrollGeometryReceiver`, exact presented-packet continuation checks, and frozen geometry tests. | `IUIPointerScrollReceiver`, frozen pointer geometry, and pointer-anchored animation zoom. Dispatch checks the cached input packet after callbacks; an output replacement without input refresh is a remaining gap. |
| Animation axis | Explicit seconds/frame display helpers and a stroke budget that reserves published key markers. | `AnimationPlotFrameAxis` maps the time-axis strip to whole-frame scrubbing. Scrubbing and displaying authored frame indices are different behaviors. |
| Context menus | Ordinary keyboard navigation with lifetime receipts and deferred focus attempts. | Ordinary navigation plus keyboard submenus and copied row sizes. Old-show revision checks exist; exact object-lifetime and build-callback protection is not equivalent to the local candidate. |
| Docking | Unpublished host identity and rebuild work with unresolved ownership defects. | `NativeDockRenderGate` limits rebuilds to four passes, stale-revision checks, and coalesced saves. Slot detachment still needs a check against callback-driven parent or transform changes. Neither version should be copied wholesale over the other. |
| Hierarchy rollback | Inactive grouped admission/compensation helpers and guarded final value writes. | Initial rollback preserves newer edits after apply completes. Grouped duplication remains unavailable. The published initial-rollback guard is narrower than the local inactive support. |
| Vulkan | Exact pipeline-key and sampled-image/view/sampler witness preparation. Capability remained disabled. | Pipeline is resolved again and forcibly rebound at issue. The additional local sampled-resource witness is not present. Full world-output capability remains unsupported. |
| Additional published work | Not in the preserved candidate. | Text-layout cache, presented text caret placement, explicit failure diagnostics, asset-spawn fixes, console/profiler state, coalesced scroll saves, shared splitter, and focused field outline. |

The state-persistence additions were wired through `EditorPanelHost`, but
`UIEditorComponent` constructs `NativeDockTreeHost`. At the fetched tip, that
active host restored only the console filter and saved every scroll event.
It omitted console category/auto-scroll and profiler-view restore and events.
This is a source finding; no live editor failure was reproduced here.

The published TODO records 50 of 60 implementation items complete. That is the
continuation's recorded reconciliation, not a fresh independent acceptance
result. Windows, native GPU, IME, visual, and equal-workload performance checks
remain separate from Linux compilation and portable tests.

## Continuation boundaries

Use the fetched implementation as the base. Port only a confirmed missing
contract, with tests against the current types. Preserve the old stash and
archive. Keep paragraph analysis, persistent asset recovery, and window-key
migration on hold. Keep ImGui as the default editor. Do not publish the old WIP.

## Checks and repairs

The fetched tip passed headless build148 with zero warnings/errors in 2m27.48s.
Run94 passed 1,052/1,052 tests, without failures, warnings, or skips, in 17.813s.
Two old wheel tests were adapted to the current `IUIPointerScrollReceiver` API.
Build149 passed; targeted run95 passed one and failed one. The failing case
showed an ancestor receiving scroll after a child replaced the presented packet.

The input route now acquires and compares the current displayed packet before
and after dispatch. It rejects non-finite deltas. Build150 passed with zero
warnings/errors in 27.98s. Run96 passed 1,054/1,054 tests in 18.732s, with no
failures, warnings, or skips.

`NativeToolPanelState` shares restore and event bindings across both hosts.
The active dock host now restores console category/auto-scroll and profiler view,
and requests the existing debouncer for scroll changes. Bindings reject an old
component lifetime, a replaced transform, or a component moved out of its slot.
The dock host also rejects callbacks while rebuilding, receiving a panel, or
after losing its slot mapping. No saved field, schema, or key migration changed.

Editor build63 and desktop test build35 failed on a missing `ConsolePanel`
namespace import in the new helper. The first import repair selected its parent
namespace; the source declares `XREngine.Editor.UI.Components`. Both the helper
and desktop fixture now use that exact namespace. No production behavior changed
in these compile repairs.

Desktop test build36 then passed with zero warnings/errors in 1m10.21s. It
compiled the editor and `NativeToolPanelStateTests`. The 10,793-file source
snapshot stayed unchanged during that build. The panel tests are compile-only
here; they do not establish Windows execution or live preference persistence.
The portable runtime/test sources are unchanged from the passed 1,054-test run.

No TODO item was closed by this repair. The recorded count remains 50/60.
Callback-safe dock detachment, broader editor parity, world rendering, owner
decisions, and platform acceptance remain open. The previous quota error did
not recur in this continuation. No model, account, credential, or dependency
configuration was changed.

## Dock detach callback reproduction

The active host's detach operation was extracted without a behavior change into
`NativeDockSlotDetachment`, then exercised with real scene nodes and parent
callbacks. Build151 passed with zero warnings/errors. Targeted run97 passed the
unchanged-slot case and failed the deactivation-callback case: the callback's new
parent was replaced by null. This confirms the source-review finding for that
callback boundary.

The repair captures the node, transform, parent, and world before deactivation.
It checks the slot mapping before mutation and checks captured identities after
deactivation and parent notification before clearing the world. A callback's
new parent, new transform, or restored active state stops the old continuation.
The helper remains on the actual `NativeDockTreeHost.RenderPass` route.

Run98 passed 1,058/1,059. The extra parent-notification case correctly triggered
the canonical hierarchy error, which the first fixture did not expect. The
fixture now asserts that error and verifies that the new parent and world remain.
No exception bypass was added. Build153 passed with zero warnings/errors in
17.77s; run99 passed **1,059/1,059** in 19.680s, without failures, warnings, or
skips. The 10,795 source inputs remained unchanged through this build and run.
These tests cover the detach helper used by the host, not a live window rebuild.

Final source review extended the check to the host's slot map. A transferred slot
must not be reused by a later stack build. `DetachRegistered` now removes only
the exact old mapping for transferred or destroyed slots. A changed slot that
still belongs under the same host stops rebuild with an error before chrome
teardown. The tests exercise that registered route and a subsequent rebuild
attempt. This also keeps an old host's state bindings from saving after transfer.

Build156 passed with zero warnings/errors in 17.42s. Run101 passed **1,060/1,060**
tests in 19.570s, without failures, warnings, or skips. It includes six dock-slot
cases. The 10,795-file source snapshot stayed unchanged. Broader host rebuild,
cross-window transfer, play-mode, and native-window acceptance remain open.

Desktop test build38 passed with zero warnings/errors in 1m32.21s. It compiled
the editor and desktop tests against the unchanged 10,795-file source snapshot.
No Windows or GPU execution is implied. No broad TODO item was closed.

## Failed Dock Rebuild Callbacks

`NativeDockRenderGate` reopened after an exception without retiring the failed
pass revision. Partial controls from that pass could then pass `IsCurrent`.
Build157 passed, but targeted run102 failed both new cases: a construction error
and the four-pass nested-request limit each admitted the failed revision.

The catch path now advances the revision before the gate opens. It propagates
the original error and permits a later rebuild. Build158 passed with zero
warnings/errors in 20.86s. Full run103 passed **1,062/1,062** in 21.643s, without
failures, warnings, or skips. This is a callback-admission check, not live dock
recovery acceptance.

Desktop build39 compiled the editor and desktop tests with zero warnings/errors
in 3m19.60s. All 10,795 source inputs stayed unchanged through the final headless
build, test run, and desktop build. No broad TODO item was closed.

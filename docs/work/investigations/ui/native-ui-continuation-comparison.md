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

## Menu Row Lifetime Reproduction

The existing show revision did not pin the menu's or row's object lifetime.
Build159 failed on two fixture calls to a nonexistent `SceneNode.SetParent`;
the fixture now uses the transform parent property. Build160 passed with zero
warnings/errors in 18.94s. Targeted run104 passed the nine existing menu cases
and failed all 21 new cases in 1.335s.

The new cases show that regenerated menu/row objects, nodes, transforms, or row
parents and moved rows/lists still admitted old actions and submenu requests.
They also show that an action could dismiss a regenerated menu and that focus
callbacks could retire the menu while navigation still reported success. Each
action case checks that the row is interactable, so visibility does not hide the
admission defect.

The first repair pins show and row identities, checks row attachment, and checks
again after focus callbacks. Build161 passed with zero warnings/errors in
1m33.28s; targeted run105 passed 30/30 in 1.565s. Review then added valid-target
navigation, explicit reopening, and submenu-owner coverage. Build162 passed
with zero warnings/errors in 20.21s. Run106 passed 32/35 and failed all three
new owner cases: a child action still ran after the owner menu or row regenerated
or the owner row moved. Submenu admission now includes the full owner chain.
Row release clears captured references. Full construction/teardown ownership
and offscreen keyboard focus remain separate open work.

The lifetime candidate passed build163 with zero warnings/errors in 1m11.42s
and full run107 **1,088/1,088** in 23.449s. It remained local while the same
control's scroll-navigation contract was completed.

## Scrollable Menu Keyboard Navigation

Three regressions used a menu with accepted 120-pixel viewport geometry.
Build164 passed with zero warnings/errors in 22.19s. Run108 passed the prior
35 cases and failed all three new cases in 2.253s. End left the scroll offset at
zero instead of 232 pixels. The menu did not retain an unpresented target for a
later focus attempt or advance it on repeated keys.

The implementation uses the existing scroll transform and presented-focus API.
Rows retain their copied top-down content bounds, including separator and
disabled-row space. One pending request holds exact ownership and focus intent,
scrolls to its target, and retries at most 120 ticks. Repeated keys advance the
pending target. Hide, replacement, deactivation, lost ownership, or newer focus
intent cancels it. A cached tick delegate and indexed canvas input lookup avoid
allocations from retry registration and input enumeration.

Build165 failed with four compiler errors because the cached tick delegate used
`Action` instead of the engine's `WorldTick`. The delegate type was corrected.
The request guard checks lifetime before reading interaction state. Only a
registered waiting request advances its pending target during repeated keys;
a callback inside initial scroll setup starts a fresh request from its source.

Build166 passed with zero warnings/errors in 1m35.04s. Run109 passed 51/52 in
3.143s, including 20 waiting retries with zero measured managed allocation in
the presentation-probe harness. The failed fixture expected offset zero after
a callback selected row two. `ScrollIntoView` correctly selected its top edge,
22 pixels. The fixture now checks that minimal offset. Coverage also includes
Home waiting, separator/disabled-row bounds, and a stale keyboard-focus flag.

Final headless build167 passed with zero warnings/errors in 20.99s. Full run110
passed **1,107/1,107** in 21.526s, with no failures, warnings, or skips. Desktop
build40 compiled the editor and desktop tests with zero warnings/errors in
3m31.07s. All 10,797 source inputs stayed unchanged through those checks.
This completes ordinary-menu deferred arrow/Home/End focus in the headless
harness. It does not establish native window presentation, hardware keyboard,
IME, GPU, or end-to-end editor acceptance. The recorded TODO count stays 50/60.

## Initial Submenu Focus Intent

The separate eight-frame submenu retry did not retain the owner row's input
intent. Build168 passed with zero warnings/errors in 2m58.68s. Targeted run111
passed 55/58 in 2.789s. The registered Right-arrow path still attempted child
focus after a newer focus intent, another virtual focus request, or input-source
regeneration. Its unchanged-intent control passed.

Submenu opening now uses the ordinary navigation request with the owner row as
its focused source. The old retry and frame counter were removed. Hidden targets
wait for placement and do not call presented focus. Mouse opening cancels a
pending keyboard request. Build169 passed with zero warnings/errors in 1m48.57s.
Full run112 passed **1,112/1,112** in 19.585s, without failures, warnings, or skips.

Desktop build41 compiled the editor and desktop tests with zero warnings/errors
in 3m40.59s. All 10,797 source inputs stayed unchanged through the final checks.
The old retry is absent from source. Full construction/teardown, native window,
keyboard/IME, and GPU acceptance remain open. No broad TODO item was closed.

## Menu Transition Reentry

The next panel audit found that the native asset browser has no row context menu.
The existing ImGui delete helper can fall back to permanent deletion. Some asset
type actions open only ImGui tools. Those actions were not added to the native
route. The persistent recovery proposal still needs owner approval.

Shared menu construction needs a prerequisite repair. A Show callback could
replace the menu before the outer call finished setting its size. Show requests
during Hide or child cleanup were discarded. A failed custom builder could leave
partial live content. Cleanup also removed every child of the menu node, including
children that the menu did not create.

The candidate serializes Show and Hide through one bounded transition loop. A
callback replaces one pending request; the latest request runs after the current
transition. The request captures the item array, row sizes, and owner identities.
A changed owner lifetime or parent rejects the queued request. Reopen loops stop
with an error. Cleanup tracks the content root and its exact transform and parent;
it preserves unrelated children and content transferred to another owner.

Build170 observed an incomplete edit snapshot and failed with one missing-type
error. It is not a baseline or candidate validation result. Fresh build171 passed
with zero warnings/errors in 1m46.17s. Targeted run113 passed 56/64 in 2.484s.
The candidate replaced its content transform after capturing its identity, so
cleanup skipped its own content. Construction now uses that captured transform.
Build172 then failed with one local-name shadowing error in the request guard.
The local name was corrected. Build173 failed with one fixture error: `SceneNode`
does not expose `Children`. The test now reads children through its transform.
Review also found that a queued request from Dismissed must stop the outer Show
before it builds stale content. The existing dismissal/reopen test covers that
case and remains unchanged.

Final headless build174 passed with zero warnings/errors in 1m21.59s. Full
run114 passed **1,126/1,126** in 20.780s, with no failures, warnings, or skips.
This adds 14 cases for replacement size, callbacks during Hide, builder failure,
unrelated/transferred children, latest-request coalescing, stale owner identities,
and bounded reopen loops. All 10,800 source inputs stayed unchanged through the
headless checks and matching desktop build42. That build compiled the editor and
desktop tests with zero warnings/errors in 3m40.47s. Individual child callback
failure/ownership paths and native acceptance remain open; no broad item closes.

## Native Asset Row Menu

`AssetsPanel.Menu` adds Open Folder or Inspect, Rename, and Reimport through the
existing panel paths. It does not expose the ImGui deletion fallback or actions
that only open ImGui windows. Asset creation and the recovery proposal remain held.
Right-click captures `NativeAssetMenuRequest` and opens the shared menu on the next
owner tick. The request checks the exact row lease, provider/source revision,
component/node/transform lifetimes, canvas, input, and current path mapping.
Refresh, recycling, and owner retirement cancel it. Rename prepares the existing
name field; Enter uses the existing rename helper, and Escape cancels the draft.

Input callback registration and menu action creation use separate methods, so
the idle per-frame update does not allocate captured closures. Popup release
retires input first and queues node removal after transfer callbacks. Other file
watcher and drop-target cleanup still runs if popup release throws.

Build175 failed with two missing namespace imports for `UICanvasComponent`.
Build176 passed with zero warnings/errors in 21.58s after the imports were added.
Targeted run115 passed **15/15** in 0.899s. Build177 passed with zero warnings/errors
in 20.77s after the per-frame callback extraction and an allocation check were
added. Full run116 passed **1,141/1,141** in 23.089s. Desktop build43 then failed
with one owner-type error: `AssetsPanel` derives from `XRComponent`, not
`UIComponent`. The request now accepts that actual base type and checks its UI
transform explicitly.

Review found that periodic refresh would cancel an unchanged rename draft.
`NativeAssetRenameDraft` now keeps the field and owner identities independently
of row recycling. Submission still requires the selected path and current path
mapping. Source/search/folder changes and owner retirement cancel the draft.
Rename focus also yields to newer focus intent. Seven more cases cover changed
fields, selection, and path mappings. Build178 passed with zero warnings/errors
in 20.46s. Final headless build179 passed with zero warnings/errors in 11.40s.
Run117 passed **1,148/1,148** in 22.473s. Desktop build44 passed with zero
warnings/errors in 1m20.51s. Final review synchronized the selected row for menu
actions, rejected a selection changed by a callback, and tied dismissal callbacks
to their exact popup. Desktop builds45 and46 passed those intermediate snapshots.

Final desktop build47 compiled the editor and tests with zero warnings/errors in
1m16.22s. Final headless run118 passed **1,148/1,148** in 34.467s, without failures,
warnings, or skips. All 10,804 source inputs stayed unchanged through the final
desktop build and headless run. One hundred request-admission checks allocated
zero managed bytes in the harness. This is not an editor performance benchmark.
The 22 portable cases exercise request and draft admission, not live right-click
dispatch, rename focus, or file mutations. The Windows procedures remain pending.

## Table Activation Across Selection Callbacks

Source review found that the table interaction handler selected its current
BoundId and then read BoundId again after callbacks. A callback could recycle the
row between those reads. Build180 passed with zero warnings/errors in 2m44.42s.
Baseline run119 passed **1/11** in 0.932s. It reproduced activation of replacement
ID22, activation of ID0 after provider removal, a duplicate nested action, stale
actions after other ownership changes, and an obsolete selection notification.

The provider now captures the action's item and binding before selection and
checks object, canvas, input, source, and selection ownership afterward. A monotonic
selection revision rejects a change away and back to the same value. The selection
setter stops its old visual/notification pass after a newer selection. Build181
passed with zero warnings/errors in 1m46.14s; run120 passed **11/11** in 0.982s.

A related changing-callback test then reproduced a recycled row receiving its
predecessor's highlight. Build182 passed with zero warnings/errors in 20.03s;
run121 passed **11/12** in 0.872s. The row now checks its lifetime and navigation
binding after IsSelected changing callbacks and rejects the old pending write.

Final headless build183 passed with zero warnings/errors in 1m30.44s. Full run122
passed **1,160/1,160** in 22.148s, with no failures, warnings, or skips. The matching
desktop build48 compiled the editor and tests with zero warnings/errors in
3m01.39s. All 10,805 source inputs stayed unchanged. Native window input and broader table/editor acceptance
remain in the validation plan; no broad TODO item closes.

## Asset Selection And Activation

The asset panel subscribed to table activation but not selection changes. Shared
keyboard navigation changed SelectedId after presented focus, while SelectedPath
and the asset inspector still referred to the old file. The panel now subscribes
to selection, keeps folder opening on activation, and avoids duplicate inspection
when one click first selects and then activates the same file. It reuses existing
object/transform admission and rejects obsolete notifications after callbacks.

A wrapper-routed desktop test probe ran on Linux against the existing
NativePanelDirectDestroyTests.AssetsPanel_DirectDestroy filter. It executed zero
tests. NUnit reported `Only supported on Windows7.0`; the outer log also reported
no matching tests. This is not a passing test result. No platform attribute was
removed. Six new editor cases cover file selection, repeated activation, restoring
asset inspection, callback changes, and folder selection versus activation.
Their Windows execution remains pending. Existing teardown cases now also check
that SelectionChanged handlers detach.

Desktop build49 compiled the editor and tests with zero warnings/errors in
5m09.59s. Run123 passed the existing **1,160/1,160** portable tests in 23.404s.
Final review also made deselection clear the inspector only when it still owns
the prior path. Desktop build50 compiled that final change and its test assertion
with zero warnings/errors in 1m17.88s. The new editor fixture remains unexecuted on
this host; the portable suite does not contain AssetsPanel or these six cases.

Final portable run124 passed **1,160/1,160** in 23.307s with no failures, warnings,
or skips. It used the unchanged portable inputs built by headless build183.
All 10,808 source inputs stayed unchanged through final desktop build50 and
run124. This is compilation plus portable regression evidence, not execution of
the new editor cases or native keyboard acceptance.

## Actions During Table Binding

Five new cases called OnInteract from initial cell or accessibility-label binding,
and from cell, label, or source-read callbacks during refresh. Headless build184
passed with zero warnings/errors in 2m31.26s. Baseline run125 passed **12/17** in
0.989s. All five new cases activated item11 before the binding operation finished.

The row now records the source revision only after binding completes. Bind and
unbind invalidate that record before callbacks. Actions require the completed
revision to match the current source. A sixth case changes the source before
refresh and checks that the old row cannot activate until refresh completes.
Build185 passed with zero warnings/errors in 1m38.42s; run126 passed **18/18** in
1.529s. Final review checks the completed revision after the source getter in the
binding-admission check, so a callback cannot clear the record after its last read.
Final headless build186 passed with zero warnings/errors in 1m35.41s. Full run127
passed **1,166/1,166** in 23.189s, without failures, warnings, or skips. Desktop
build51 compiled the editor and tests with zero warnings/errors in 3m35.64s. All
10,808 source inputs stayed unchanged through these final checks. Native input
acceptance remains pending; the broad shared-control item stays open.

## Menu Content Root Attachment

Six new cases changed the content root's parent, node lifetime, or transform
lifetime inside its attachment callback. Both ordinary and custom-content menus
were covered. Headless build187 passed with zero warnings/errors in 2m41.41s.
Baseline run128 passed **75/79** in 2.909s. Parent transfer already failed safely;
the four node/transform lifetime cases did not abort construction.

CreateContentRoot now validates the captured root and show immediately after
SetParent returns. It stops before configuring a root from another lifetime or
invoking a custom builder. Cleanup reuses the same ownership predicate and keeps
the transferred root. Build188 passed with zero warnings/errors in 1m07.58s;
run129 passed **79/79** in 2.526s. Full run130 passed **1,172/1,172** in 22.308s,
without failures, warnings, or skips. Desktop build52 compiled the editor and
tests with zero warnings/errors in 3m20.33s. All 10,808 source inputs stayed
unchanged through these checks. This does not close the broader child
construction/teardown item.

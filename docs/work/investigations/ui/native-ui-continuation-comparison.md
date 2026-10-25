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

## Guarded Component Creation For Menu Builders

The remaining menu item needs checks inside shared creation and cleanup methods.
Detached staging alone is insufficient: XRComponent.New invokes ComponentCreated
before AddComponent returns. The compound SceneNode.NewChild factories attach a
node and create multiple components without giving their caller an intervening
ownership check. Ordinary SetField also writes after PropertyChanging, and the
world/component notification methods resume engine work after external observers.

The current implementation candidate adds an internal guarded component factory
with a caller-retained receipt. It captures the exact node, transform, parent,
world, and claimed component lifetime before public creation callbacks. It checks
callbacks before later engine work, retains failures and inserted partials, and
keeps the existing public factory route. Menu integration and complete content
retirement remain open. Constructor origin alone does not authorize destruction
of a component that moved or changed lifetime.

Headless build189 compiled the core changes with zero warnings/errors in 2m43.44s.
The command's first test-link marker assertion failed, so this build did not
include the new GuardedComponentCreationTests fixture. The fixture was linked
afterward. No test execution for that candidate is recorded yet.

Review found that the legacy AbortFailedConstruction method marks an object
destroyed in a finally block even when cleanup throws. A guard inside OnDestroying
cannot prevent that final commit after ownership changes. The guarded creation
path therefore needs a separate opt-in abort admission before the cache/destruction
commit. It must retain cleanup failure and permit a later retry while preserving
changed ownership. The legacy abort behavior remains outside this opt-in path.
The candidate is unpublished while this repair and its regression checks are open.

The next menu integration must capture construction parent/ancestor/canvas/world
ownership after initial placement accepts its current canvas. It must preserve
later canvas migration in CompletePlacement. A newer queued Show/Hide is
cancellation and must reach the transition loop. Custom builder delegates retain
responsibility for arbitrary statements inside their own body; exact owned
content must be registered before callback publication for guarded retirement.

Headless build190 failed with CS1061 because the receipt read the Data assembly's
internal lifetime property. The repair uses ObjectConstructionIdentity.Capture.
Build191 then failed with CS0200 in the new same-ID test, which tried to assign
the read-only ID property. No tests ran from these two failed builds.

Review found three further admission gaps before publication. A creation callback
can add a claimed component to a foreign structural list while its SceneNode
still points to the original node. List pre-add callbacks can also change ownership
before the insertion write; post-add callbacks run before the node's own handler.
Finally, a successful creation receipt must not authorize abort after a later
normal detach. The candidate needs exact list ownership, a pre-insertion check,
and a record of actual insertion before its failed-uninserted cleanup contract
is ready. A vetoed insertion remains a separate, abortable case.

Build192 passed with zero warnings/errors in 1m51.92s. Targeted run131 passed
23 of 24 creation cases in 0.225s. The vetoed-insertion case did not throw:
pre-insertion validation correctly permitted zero membership, but the caller
also needed to require a committed insertion after Add returned. The fix must
stop before subscribing destruction handlers or starting component lifecycle.
Review also identified a derived world-change override that can change ownership
after its base callback checks. World assignment needs an intrinsic check at its
actual write boundary, including when property notifications are suppressed.

Build193 passed with zero warnings/errors in 1m49.40s. Run132 passed all 27
creation cases in 0.377s. Review then added the case where a failed component
enters a foreign list and is later detached before abort. Current membership alone
cannot prove that the component was never published. Constructor abort needs a
monotonic component publication marker, including insertions after the failed
creation method has cleared its active receipt. This check does not revoke normal
component detach or reuse.

Build194 passed with zero warnings/errors in 1m54.72s. No tests ran on this
intermediate snapshot. Final review rejected the proposed retry after arbitrary
OnDestroying failure: a callback can release a resource and then throw. Repeating
it can release that resource twice. The final contract marks cleanup as started
before OnDestroying and retains failure without repeating cleanup. An admission
failure before cleanup begins remains distinct. A nested external destruction
must not be reported as successful guarded abort merely because IsDestroyed is
true after its callback.

The next retirement integration can reuse OwnedHierarchyRetirement through a
separate explicit structural-receipt input. It must not convert a current tree
snapshot into constructor ownership or relax clone admission. The first bounded
unit is an already detached, world-null leaf with captured node, transform,
component/child storage, and explicitly registered components. Component-owned
render resources remain the component's cleanup responsibility. Live menu use
also needs guarded parent detach, exact lifecycle/world removal, and retention
of partial cleanup results. That preparation is not implemented by the component
creation receipt. Owner-node teardown must enter guarded content retirement before
ordinary child recursion; a component OnDestroying hook alone runs too late.

Headless build195 passed with zero warnings/errors in 2m50.99s. Targeted run133
passed all 28 guarded creation cases in 0.209s, with no warnings or skips. This
includes one-attempt cleanup and confirmed abort completion after nested
independent destruction. Full portable validation and matching editor/test
compilation follow on the frozen source snapshot.

Full run134 passed **1,200/1,200** tests in 22.640s, without failures, warnings,
or skips. Desktop build53 compiled the editor and tests with zero warnings/errors
in 3m56.54s. All 10,810 source inputs stayed unchanged through final validation.
The five maintained UI documents have 39 resolving relative links, and
`git diff --check` passes. This prerequisite does not close menu integration,
retirement, Windows execution, graphics acceptance, or the broad control item.

## Explicit Detached Leaf Retirement And Menu Cancellation

The next candidate records an already detached, world-null leaf before component
construction. It registers guarded component attempts before their callbacks and
feeds their original identities to the existing retirement executor. Clone
constructor-ledger admission remains separate. The candidate retains partial
failure and records canonical destruction that completes before a Destroyed
observer throws. It does not yet prepare live-world nodes or replace menu teardown.

Six new menu cases also cover a newer Show or Hide request from the open, width,
and content-attachment callbacks. They require the obsolete custom content builder
not to run. The first compilation includes these cases before the menu fix, to
establish their baseline. No baseline result is recorded yet.

Review identified a shared cleanup ordering issue: a failed component's guarded
abort changes subscriptions before lifetime admission rejects a competing reserved
retirement. That guarded preparation must move after successful admission. The
explicit retirement input must also be frozen rather than consult mutable creation
attempts during cleanup. Both repairs await the current build/test boundary.

Build196 passed with zero warnings/errors in 2m39.72s. Run135 passed 9 of 16
cases in 1.087s. All six new menu cases reproduced an obsolete builder call.
The leaf test incorrectly expected structural list containers to be destroyed;
canonical cleanup retires their authorities and keeps the non-cache containers.
The test will check empty storage and rejected mutation instead. The other nine
leaf cases passed on this intermediate snapshot.

Menu construction now checks for a newer queued request at setup boundaries and
returns to the transition loop. It leaves ownership failures distinct. Two
additional cases require a builder exception to remain observable even after it
queues another request. Review also found that an unused registered component
attempt could allocate during reserved retirement before binding was rejected.
Guarded factory admission must reject that request before it runs the factory.

Build197 passed with zero warnings/errors in 2m14.31s. Run136 passed all 127
targeted cases in 3.671s: 87 menu cases, 28 guarded creation cases, and 12 detached
leaf cases. The six cancellation regressions now pass, and builder exceptions
still propagate after queued requests. This is not the final snapshot.

Review found an adoption-history gap in the new initial-leaf receipt: attachment
to a foreign parent followed by detach restores the same current graph without
restoring the original ownership claim. The bounded contract will reject nodes
or transforms with any prior structural/root adoption. This permanent marker
must never be reset to admit live menu content. A later live-content adapter needs
explicit permitted transition receipts. Review also tightened the unused-factory
regression to run from an orphan component before the node's lifetime advances,
so it tests the new admission lease rather than the existing identity check.

Build198 passed with zero warnings/errors in 2m29.93s. Run137 passed all 130
targeted cases in 3.271s, with no warnings or skips. The additional cases cover
parent, scene-root, and runtime-root adoption followed by removal. Final read-only
review found no remaining serious issue in this bounded detached-only contract.
Full portable validation and matching editor/test compilation follow on the
frozen source snapshot.

Full run138 passed **1,223/1,223** tests in 23.768s, without failures, warnings,
or skips. Desktop build54 compiled the editor and tests with zero warnings/errors
in 3m39.35s. All 10,812 source inputs stayed unchanged through final validation.
The broader reconciliation remains 50/60. Live-world preparation, guarded parent
removal, menu integration, and platform acceptance remain open.

## Live Leaf Preparation And Inspector Float Curves

The next candidate extends the same retirement executor with exact source-world
removal for registered, never-adopted leaves. The old null-world receipt and clone
admission remain strict. Lifecycle callbacks run while the source world is still
available. World and store removal retain entered, committed, and completed
progress; failures cannot replay notifications. Parent/root detachment and live
menu integration remain outside this increment.

Read-only review found two regressions before compilation: capturing the current
world in each entry had weakened clone admission, and a rejected nested world
setter could dispose the outer removal permit. Explicit null-world/store admission
and disposal only after successful permit entry repair those paths. New cases
cover strict clone admission, caught nested world writes, real transform-store
detachment, and failures before or after world/store commits.

The inspector candidate fills the empty PropAnimFloat detail route. It uses the
existing retained plot, virtual table, and shared animation transaction. Shared
references are edited once; distinct selected curves remain unavailable. Review
removed idle document-resolution allocations and added exact time/frame checks
for inserted keys after callbacks. The editor test cases require the existing
Windows test target; portable results will not count those cases as executed.

Headless build199 ran on 10,821 frozen source inputs and failed with zero warnings
and one CS0234 error in 2m56.19s. The new test referenced the Jitter physics
namespace, which the portable project does not include. The test now uses the
existing HierarchyTestPhysicsScene to exercise the actual RuntimeWorld hierarchy
store without a new project dependency. The broader reconciliation remains 50/60.

The inspector review also added a refresh reentry guard, source revisions after
UI writes, explicit plot-source release, visible field labels, and rejection of
unavailable tracks. Build200 passed with zero warnings/errors in 22.89s. Run139
passed all 53 targeted cases in 0.314s: 10 live-world leaf cases, 15 original
leaf cases, and 28 guarded creation cases. Full run140 passed **1,233/1,233**
portable tests in 23.586s, with no failures, warnings, or skips. Matching desktop
build55 follows on the same frozen source. No new editor case has executed here.

Desktop build55 failed with three warnings and 11 errors in 2m50.26s. The new
inspector source used XRObjectBase lifetime members on XRBase and KeyframeTrack.
The fix must check lifecycle identities only for objects that own them, keep
exact non-null track/reference checks, and remove the invalid track destruction
assumption. Portable tests do not compile this editor-only source.

Desktop build56 compiled the editor and test project in 1m47.53s, with zero errors
and three nullable warnings in the new inspector tests. The candidate remains
unpublished while those warnings and an attached-transition ownership gap are
fixed. Transition properties belong through a state/layer/machine chain; checking
only the curve reference would miss transfer and state-machine dirty state. The
new adapter must retain that chain and its source document, and reject incomplete
attached ownership. Two additional live-leaf cases cover a World veto and a stop
callback that throws after a side effect. They have not run at this point.

The transition adapter now retains exact state/layer/machine lists, indexes,
counts, identities, and the machine source document. Its fast check is bounded
and nonallocating; command checks also verify unique membership. Detached owners
must remain detached. Incomplete attached chains and AnyState ownership are
unavailable. Four pure owner-chain cases are linked into the portable project.
Test fixtures use the canonical Transitions setter to establish ownership; no
unrelated animation constructor behavior was changed. Build201 starts on 10,823
frozen inputs. The editor action/history cases remain Windows-targeted.

Build201 passed with zero warnings/errors in 21.91s. Run141 passed all 59 targeted
cases in 0.411s, including the 12 live-world cases and four portable transition
ownership cases. Full run142 passed **1,239/1,239** in 30.355s, without failures,
warnings, or skips. Desktop build57 passed with zero warnings/errors in 1m20.67s.
All 10,823 source inputs stayed unchanged through final validation. Nine new
inspector action/history cases compiled but did not execute on this Linux host.
The five maintained documents have 39 resolving relative links; diff checks pass.
The bounded contracts are ready for publication. Parent/root transitions, live
menu integration, distinct-curve editing, and platform acceptance remain open.


## Native Asset File-Browser Action

Source comparison found that ImGui could reveal a file or folder in Explorer,
but the native asset row menu could not. The native action now reuses the existing
launch implementation through EditorFileBrowser. It runs the same row/selection
admission as other menu actions, confirms the path still exists, and rechecks the
request after selection callbacks before launch. The existing ImGui callers use
the shared helper with the same arguments and error handling. No delete action,
new storage behavior, or dependency was added.

Desktop build58 starts on 10,824 frozen source inputs. Windows process launch is
not available for acceptance on this Linux host. Existing portable request tests
cover the unchanged admission contract; they do not execute the new editor action.

Desktop build58 passed with zero warnings/errors in 6m30.24s. Run143 passed all
22 existing portable menu-request cases in 2.100s without failures or skips.
It reused build201 because the portable project inputs did not change; the new
menu action and shared launch helper are editor-only. All 10,824 source inputs
stayed unchanged through this validation. The full portable result remains
run142 at the preceding checkpoint, 1,239/1,239. Windows Explorer launch is still
pending; the request tests do not execute that process boundary.


## Transition Authoring Context

Review after the float-detail checkpoint found that runtime back-references do
not prove authoring ownership. A layer gains OwningStateMachine during Initialize
and loses it during Deinitialize. AnyState has no matching OwningLayer link. A
default state can also contain transitions without their Owner back-reference.
Thus Owner == null cannot establish that a transition is detached, and a direct
transition editor can miss the containing machine's dirty document.

The replacement starts from the inspected machine's Layers detail. A retained
navigator shows one selected level at a time, then opens the existing curve detail
with a durable forward path through exact containers and positions. Ordinary
states and AnyState use separate explicit edges. The proof includes machine,
source-document, and container lifetimes. It does not use runtime back-references
or search all assets. Bare transition editing now requires that explicit context.
UI leases do not become requirements for a later valid Undo operation.

Build202 passed with zero warnings/errors in 2m54.33s. Run144 passed the first five
portable forward-path cases in 0.082s. The frozen source had 10,826 inputs. Further
coverage and navigator callback repairs follow before final validation: the exact
default-constructor gap, duplicate/reordered containers, source-document changes,
active container generation changes, and preservation of transferred detail nodes.
No source changes to animation ownership, asset formats, or persistence are planned.


## Remaining Ordinary Menu Integration Contract

Read-only review confirms that the initial leaf receipt cannot replace ordinary
menu cleanup directly. Ordinary row factories attach adopted trees and can fail
before returning their node. Full guarded Show(items), submenus, Hide, and owner
teardown need one connected runtime path: constructor-origin node/transform/list
claims, opt-in property-commit and callback-return validation, explicit parent
transitions with monotonic ownership revisions, recorded leaf-first retirement,
and a canonical owner-node hook before recursive child destruction. Existing
component receipts, list authority, and retirement executors should be reused.
Permanent adoption markers must stay intact. Unknown or transferred content and
partial failures must remain retained, not enter recursive destruction.

The relevant seams are TransformBase construction, SceneNode constructors and
lifecycle teardown, XRBase field/notification dispatch, and UIContextMenuComponent
BuildMenuItem/BuildSeparator/ClearMenuItems. Runtime menus must not depend on the
editor's NativeUIElements. Raw ShowContent callbacks do not supply arbitrary
allocation ownership; stronger custom-content cleanup needs explicit context.
No new dependency, storage decision, or owner approval is required for these
remaining code contracts.

A smaller active-route increment is safe first: check CanContinueConstruction
between existing row, label, arrow, separator, line, and scroll-component helper
calls. This can stop later factories after an earlier helper queues Show/Hide.
It cannot interrupt a compound factory, stop an internal stale setter write, or
establish full retirement safety. Tests must preserve callback exceptions even
when a newer menu request was queued. This bounded menu step is implemented in the forward-context checkpoint. Broader
construction and retirement work remains open.

## Forward Context And Construction Validation

Headless build203 passed with zero warnings/errors in 1m18.59s. Run145 passed
100 targeted transition-ownership and menu cases in 3.469s. Full run146 passed
1,248/1,248 tests in 24.404s, with no failures, warnings, or skips. The source
snapshot held 10,827 inputs. Eight portable forward-path cases now include default
lists with absent runtime back-references, AnyState, list regeneration, duplicate
and reordered membership, document changes, and 1,024 allocation-free fast checks.

Desktop build60 compiled with zero errors and eight CS8604 warnings in 1m24.63s.
The new retirement tests passed nullable Action values to NUnit, which selected
the delegate overload. Eight explicit object casts now test the stored callback
value. Only that editor-only test file changed after the portable run; it is not
linked by the headless project. A new frozen snapshot records that correction.

Desktop build61 passed with zero warnings/errors in 49.90s. All 10,827 source
inputs stayed unchanged after the assertion correction. The portable source and
its run146 result remain valid. Nine action/history tests and five new detail
construction tests compiled only; no Windows execution is claimed.

## Child Destruction Snapshot Ownership

The ordinary SceneNode teardown path snapshots child nodes and then calls
Destroy(true) on each reference. It does not recheck the child's transform,
identity, or attachment after an earlier child's callback. The native menu Hide
path reaches this code through its owned content root.

Headless build204 passed with zero warnings/errors in 2m51.43s. Tests-first run147
executed seven cases in 0.921s: two passed and five failed. The actual menu Hide
case destroyed a later row after an earlier row moved it to the canvas. The core
transfer case also destroyed that child's descendant. Node regeneration,
transform regeneration, and transform replacement each still dispatched the old
child destruction. Normal canonical destruction and exception propagation passed.
NUnit classified the menu's Shouldly assertion as an error; it was the expected
IsDestroyed mismatch, not an unrelated runtime exception.

The bounded repair checks each captured child immediately before dispatch. It
must retain exact parent transform/list and child node/transform identities,
reciprocal references, and current list membership. This does not establish
protection against mutation inside that selected child's own Destroying callback,
concurrent mutation, transfer-away-and-back history, or general menu retirement.

Build205 passed with zero warnings/errors in 1m50.65s. Intermediate run148 passed
five of 12 cases in 0.976s, including actual menu Hide, core transfer, unchanged
children, queued children, and exception propagation. Seven cases reported errors
from their final cleanup: the parent schedules transform destruction, and the
fixture tried to remove a preserved child while that transform was still queued.
The next fixture revision must let scheduled transform cleanup finish before
it destroys a survivor. It must not bypass structural-list authority.

Review also found that the candidate required one occurrence in the child list.
The existing list contract permits duplicate references in one exact owner/list.
The dispatch de-duplication must preserve one canonical destruction for that case;
a separate regression covers this compatibility requirement.

Build206 passed with zero warnings/errors in 37.75s. Run149 passed all 13 targeted
cases in 1.269s. Full run150 passed 1,261/1,261 in 29.820s with no failures,
warnings, or skips. Read-only review found no further blocker in the bounded
before-dispatch contract. The source snapshot holds 10,829 inputs.

Desktop build62 passed with zero warnings/errors in 3m42.70s. All 10,829 source
inputs stayed unchanged through final validation. The 39 relative links in the
five maintained UI documents resolve, and diff checks pass. No platform
acceptance or broader menu-retirement completion is inferred.


## Inspector Float Interpolation Modes

The native float detail previously exposed time and value but no In/Out mode
commands, while the clip panel already exposed modes. The detail now uses the
existing staged authored-state transaction for both value and mode edits. The
shared predicate also retains key time and authored frame. This rejects a callback
that changes those fields without changing key order. Newer callback edits remain
intact and conflicting Undo remains guarded. No new mutation framework is added.

Two mode buttons use a separate compact row, cycle the four defined modes, and
disable for missing or unsupported selected keys. Dirty refresh updates labels
and enabled states with revision checks. The change does not add idle allocations.
Review found no blocker. An editor-only fixture correction accounts for the
existing Second setter clearing AuthoredFrameIndex to -1 after a retime.

Headless build207 passed with zero warnings/errors in 2m55.46s. Run151 passed
seven authored-state cases in 0.158s, including sampled-curve change and exact
reversal without seeking playback. Editor action/history cases require separate
Windows execution; portable tests do not exercise the editor Undo implementation.

Full run152 passed 1,262/1,262 in 23.780s, with no failures, warnings, or skips.
Desktop build63 passed with zero warnings/errors in 3m41.54s. All 10,829 source
inputs stayed unchanged through final validation. Six new editor action/history
cases compiled only. The portable case tests exact authored-state sampling and
reversal; it does not execute the new editor command or controls. The 39 maintained
relative links resolve and diff checks pass.


## Hierarchy Code And Acceptance Reconciliation

A read-only source audit found no missing implementation in the first hierarchy
adoption item's exact done condition. Root review confirmed the live scene/tree
population, reusable row binding, visibility mutation, and shared asset-drop
routes. The remaining reason recorded in the checkbox was Windows execution.
Repository guidance assigns those checks to the validation document, so the
source-complete item is removed from the code TODO. The reconciliation is now
51/60 code items, with nine broad implementation items remaining. This is a
reclassification of existing code, not a newly executed runtime result.

The source seams are HierarchyPanel.VirtualTree, Sources, Rows, Menu, Rename, and
EditorImGuiUI.ModelDropSpawn.TryHandleDroppedSpawnableAsset. Existing hierarchy
workflow, selection, mutation, scene-ownership, menu, and prefab-spawn cases
corroborate the routes but remain compile-only on Linux. Model drops have no
fixture, and native mouse/menu checks remain pending. Grouped multi-root
duplication stays in its separate open implementation item.


## Unweighted Float Handle Authoring

The retained detail adds incoming value, both tangent inputs, and the three sync
flags. The existing value field is labeled Out value. All actions use the same
staged authored-state transaction. This retains complete setter side effects
and rejects nonfinite computed handles before the live key or Undo changes.
Weighted mode and weights are preserved but remain outside these controls.

Read-only review found no blocker in state ownership, sync math, callback guards,
or idle allocations. Headless build208 passed with zero warnings/errors in
3m11.65s. Run153 passed ten authored-state cases in 0.167s. Full run154 passed
1,265/1,265 in 26.713s, with no failures, warnings, or skips. Two new editor cases
cover six consecutive edits with Undo/Redo and pre-mutation overflow rejection;
they need Windows execution. Portable tests cover sync effects and exact reversal.

Desktop build64 passed with zero warnings/errors in 3m19.50s. All 10,829 source
inputs stayed unchanged through validation. The 39 maintained relative links
resolve and diff checks pass. The active TODO has nine implementation checkboxes,
consistent with the 51/60 source reconciliation. No Windows, graphics, input,
visual, or benchmark acceptance is inferred.


## Weighted Float Handle Authoring

The retained detail now exposes Hermite weight toggles and normalized inputs.
The generic authored-state snapshot includes scalar weights. Both clip and
inspector staging restore them before ordinary edits, so value, mode, tangent,
and sync commands preserve weighted curves. Exact commits validate all fields
before mutation and notify only after the complete state is visible. Existing
serialized fields and interpolation math are unchanged.

Review found two setter-publication hazards. A throwing property observer can
leave an accepted write unpublished. A nested setter can publish an intermediate
value before the outer setter stores its final value. Public setters now notify
for each accepted write and conservatively invalidate a changed field when a
property observer throws. Secondary notification failures cannot replace the
original setter exception. An attempted revision-based suppression was removed
because it could suppress that final outer value.

Headless build209 failed with one CS0103 error and zero warnings in 2m19.18s:
the animation library cannot call the runtime Debug logger. The repair uses
System.Diagnostics.Trace inside the existing guarded diagnostic path. It adds no
reference or dependency. Build210 passed with zero warnings/errors in 55.07s.
Run155 passed 16/16 authored-state cases in 0.136s. Full run156 passed
1,271/1,271 in 24.418s, with no failures, warnings, or skips. Six new portable cases
cover weighted samples, complete-state publication, stale-state rejection,
nested setters, observer failure, and invalid/non-scalar weighted state.

The editor fixtures add four cases across clip and inspector actions, including
weight-preserving Undo/Redo, removed-key payload conflicts, and neighboring-key
callback conflicts. These require Windows execution; the portable suite does not
execute the editor action or Undo implementations.

Desktop build65 passed with zero warnings/errors in 3m12.96s. All 10,829 source
inputs stayed unchanged through validation. The 39 maintained relative links
resolve and diff checks pass. No Windows runtime, GPU, visual, or performance
acceptance is inferred.

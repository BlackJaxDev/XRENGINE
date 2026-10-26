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
to their exact popup. Desktop builds45 and 46 passed those intermediate snapshots.

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


## Grouped Hierarchy Duplication Design Review

The existing request, custody receipt, placement operation, and property replay
guard each cover one cloned root. Repeating the single-root command would permit
partial attachment before later construction fails. The grouped command needs
one forest construction ledger, one custody entry, and one structural history
transaction with explicit member progress.

The runtime contract keeps separate per-source YAML serialization and registered
AOT handlers. It constructs all roots under one existing construction scope.
Temporary structural reservations protect completed peers while another root is
constructed, configured, regenerated, or initialized. The reservation covers all
peer nodes, transforms, and components, including linked-list lifetime admission.
It does not hold the structural gate across callbacks. Final publication uses the
existing identity guard and whole-forest structural/live validation, then seals
the ownership identities for editor custody.

This bounded contract preserves current-root identity semantics. It does not
claim to reject same-target reentrant Generate or temporary structural changes
inside publication callbacks. No generic Data identity-admission redesign is
needed. Existing unknown-asset retirement and document-end recovery restrictions
remain. The editor batch must independently validate peer placement and property
progress during initial apply, compensation, Undo, and Redo. Group lifetime
admission alone cannot prevent a callback from editing another clone's values.

Implementation and validation are in progress. The native grouped command remains
an open code item until the request-to-history path and its tests are complete.


The first runtime candidate passed headless build211 with zero warnings/errors in
2m42.36s. Targeted run157 passed 34/34 owned-clone cases in 0.490s, including eight
new forest cases and 26 existing single-root cases. This was an interim runtime
check while unlinked editor transaction files remained in progress. It was not a
complete grouped-command snapshot or a full-suite acceptance result. Review then
requested additional partial-factory, structural-peer, and publication-failure
coverage before final validation.


The expanded runtime candidate passed build212 with zero warnings/errors in
28.81s. Run158 passed 40/40 owned-clone cases in 0.430s. Added cases reject peer
reparenting, child/component storage replacement, and linked-list regeneration.
A later AOT factory failure retains earlier roots and partial allocations with
the original exception. A cross-root publication change fails final validation
and removes exact cache publications without destroying the retained objects.

Editor review found three integration hazards before final validation. Initial
clone deactivation needed captured placement and local-value checks for every
peer before any baseline could advance. Property replay needed direction-aware
expectations for the four local Transform keys; custom transform matrices remain
structural-inverse work. The generalized runner also needed to distinguish an
unallocated rejected request from a retained partial forest before removing its
inverse. The old typed single-operation entry point remains a forwarding wrapper
so existing target-typed factories keep their source contract.


The combined candidate passed headless build213 with zero warnings/errors in
22.39s. Targeted run159 passed 44/44 forest and actual replay-helper cases in
0.427s. Full run160 passed 1,289/1,289 in 30.138s, with no failures, warnings, or
skips. The portable additions comprise 14 forest cases and four replay-helper
cases. The latter use real transforms and cover key-by-key Undo/Redo, peer edits,
setter reentry, and custom matrix phase boundaries. Nine added editor cases and
one revised menu case still require Windows execution.


Final review found a failure-settlement blocker after the portable tests passed:
when onApplied changes a peer value, batch MarkDirty rejected the stale clone
expectation before marking the original scene. The corrective path must mark
captured live document/scene targets independently of rejected clone values,
retain callback boundary checks, and preserve the original failure. The existing
peer-conflict editor case needs an explicit clean-to-dirty assertion.

A separate read-only audit keeps the broad mutation TODO open.
EditorFileDropHandler.ProcessFileDrop still copies an external file, saves a new
texture asset, then directly assigns material.Textures[0] without shared Undo or
dirty tracking. Its event-wiring test does not cover the mutation. The persistent
asset recovery and asset-creation decisions remain pending; this route was not
changed during the grouped duplicate work.


Desktop build66 passed with zero warnings/errors in 3m20.02s before the dirty
repair. Review accepted the revised captured-document dirty path. It checks live
scene identity, current root collection, and current document ownership before
and after callbacks. It does not require rejected clone values to match. The
runner preserves the original failure, and a positive complete-compensation case
checks retained unrelated history. Repeated captured dirty targets are harmless
redundancy under the existing setter; each attempt rechecks authority.

The revised snapshot passed headless build214 with zero warnings/errors in
23.44s. Run161 passed 44/44 targeted cases in 0.462s. Full run162 passed
1,289/1,289 in 27.160s, with no failures, warnings, or skips. Ten added editor
cases and one revised menu case remain compile-only until Windows execution.

Desktop build67 passed with zero warnings/errors in 2m09.08s. All 10,838 source
inputs stayed unchanged through final validation. The 39 maintained relative
links resolve and diff checks pass. Review found no remaining publication blocker
in this bounded transaction. Windows command execution, visual checks, and
performance acceptance remain pending.


## Deferred Component Destruction Admission

A nine-case baseline reproduced two lifetime defects. The deferred queue held raw
object references, so Generate cancelled the pending flag but left a stale entry
that could destroy the new lifetime. Generate followed by requeue also caused two
virtual immediate-destruction calls. Structural component lists accepted queued,
destroyed, and actively destroying components, including a transfer from an
external node-list removal callback. Normal prequeue detach/reattach, duplicate
queue requests, and destruction vetoes passed.

Headless build215 passed with zero warnings/errors in 2m44.44s. Baseline run163
passed three of nine cases and failed six in 0.196s, with no errors, warnings, or
skips. No production change was present in this baseline. The repair must use an
exact queued request identity, retain virtual dispatch, and prevent a regeneration
race between queue validation and dispatch. Pending counts must describe live
requests, and an override that defers destruction must not spin in one drain.


The first repair candidate passed headless build216 with zero warnings/errors in
2m48.65s. Run164 passed 24/24 cases in 0.181s, with no failures, warnings, or skips.
The fixture covers both writer/dispatch orderings, virtual deferral, recursive
drain, veto, exceptions, release failure, and synchronous pre-add rejection.

Review found another untested edge before publication: a direct destruction
callback can queue its own object before destruction commits. The next drain must
settle that exact terminal request without another virtual call. Otherwise an
already destroyed object can remain pending forever. This finding came from
source review; the initial nine-case failing baseline did not cover it. The
terminal-request repair and its regression are required before final validation.


The terminal-request repair passed review. Headless build217 passed with zero
warnings/errors in 44.26s. Run165 passed 25/25 destruction cases in 0.189s. Full
run166 passed **1,314/1,314** in 20.781s, with no failures, warnings, or skips. The
queue claim covers admitted base lifetime operations and the complete queued
virtual dispatch. Component insertion protection covers owner-thread callbacks;
general concurrent insertion and arbitrary override work outside the base calls
remain outside this contract.


Desktop build68 compiled the editor and tests with zero warnings/errors in
3m25.50s. All 10,841 source inputs stayed unchanged through final validation.
The 39 maintained relative links resolve and diff checks pass. Review found no
remaining blocker in this bounded contract. Windows execution, live menu behavior,
GPU checks, and performance acceptance remain pending.


## Length Editing Across Selected Float Curves

The inspector previously rejected every distinct-curve selection. The bounded
extension adds Set length and Stretch length through the existing exact length
adapter and animation transaction. Each owner remains in the proof even when
several owners share one curve. Each distinct curve commits once. Unchanged
members remain guarded, and aggregate key limits apply again at command capture.
Key editing retains its single/shared-curve contract.

Review during implementation found two UI admission gaps. A replacement selection
needed a retained length-field source and revision. A group-to-single transition
also needed that proof on the single-source action path; otherwise a callback
during field refresh could submit the old draft to the replacement source. Both
paths now publish their input proof after the field refresh and clear it when
refresh is requested. Root identity and retirement also gate length input. The
existing inspector button helper now returns its control for enable/disable
updates; no duplicate button action wrapper was added.

The new action and retained-UI regressions require the editor test project. Linux
compilation can check these cases, but cannot execute them. Portable adapter and
engine regression results must remain separate from their runtime acceptance.


Desktop build69 compiled the editor but failed the new UI fixture with CS0246: a
namespace import for InspectorListDetailRefreshComponent was missing. It reported
zero warnings and one error in 5m11.89s. The one-line test import repair passed
desktop build70 with zero warnings/errors in 46.92s.

Final review found a shared dirty-marking gap. An earlier document's callback
could change a later document's lifetime, but the loop still marked that later
object. The repair captures ObjectConstructionIdentity and checks each document
independently before marking it. Valid original documents still become dirty
after partial failure. Logging failures cannot stop the remaining dirty marks.

The new round-trip identity regression also exposed weak inspector source proofs:
restoring an old Guid made a stale source look current. Merely changing the test
assertion would leave old group commands able to capture a new transaction over
the replacement lifetime. The source proof therefore retains lifetime revisions
for its existing owner, curve, and document fields as well. This is an identity
check, not general concurrent mutation admission.


The revised snapshot passed desktop build71 with zero warnings/errors in
1m22.18s. Headless build218 passed with zero warnings/errors in 22.37s. Full
run167 passed **1,314/1,314** portable tests in 21.537s, with no failures, warnings,
or skips. Ten new editor cases compiled only: nine action/history/lifetime cases
and one retained detail case. They did not execute on Linux. All 10,843 source
inputs stayed unchanged. The 39 maintained relative links resolve and diff checks
pass. Final review found no remaining blocker in the bounded length-edit contract.
Windows input, Undo execution, visual behavior, and performance remain unverified.
## Cross-Window Dock Pointer Continuation

The existing dock drag route restricted drops to the source canvas. Its local
mouse state did not supply an event-time release point outside the source window.
The new route uses an optional native pointer observer, exact window admissions,
one presented drop-region query, and an explicit destination stack.

Review found and repaired these issues before publication:

- Cursor polling alone could invent a release or use the point of a later press.
  Only a matching release receipt can dispatch a native drop.
- Native target queries can run callbacks. Expected HWND, press, and state
  sequence now guard the subsequent state write.
- A focus callback could replace the displayed packet or change a query owner's
  lifetime. The query checks the actual presented latch and captured owners.
- Coercing transform getters could mutate a stale input owner during hit testing.
  The touched hit-query path now checks existing transform types directly.
- Source slot cleanup could destroy callback replacements or lose a vetoed slot.
  Exact entries remain mapped until terminal destruction. Active admission stops
  recursive cleanup, and receive rollback retains its original owner identities.
- Repeated post-threshold mouse movement could allocate another drag payload.
  An active-session check now precedes window-list and payload capture.
- Logical primary-button messages and physical key polling differ for swapped
  mouse buttons. The observer selects the corresponding physical key.

The first two compile attempts exposed missing source links in the portable test
project. They were repaired without dependency changes. The first targeted run
passed 55 of 58 cases. One assertion compared `IntPtr.Zero` to an integer zero.
Two fixtures tried a complete non-UI transform replacement and hit an existing
world-transfer restriction before reaching the query. Those fixtures now model
the field-write/pre-relink callback boundary and restore it before teardown.
The corrected run passed 58 of 58. After the nested-retirement guard, the final
targeted run passed 59 of 59, and the full portable suite passed 1,368 of 1,368.
The final headless build has zero warnings/errors (19.81 seconds). Editor/test
compilation also passes with zero warnings/errors (4 minutes 11.27 seconds).
The targeted run takes 1.435 seconds; the full run takes 23.490 seconds. All
10,862 source inputs remain unchanged through these final checks. Source review,
39 relative document links, and `git diff --check` also pass.

Native Windows capture, coordinate conversion, overlapping windows, mixed DPI,
outside-window release, observer failure, and live teardown have not run here.
The route is receive-first; it does not roll both windows back atomically.
The next confirmed state gap is that a receiver binds its own saved panel view
state instead of the moved panel's source filter, view, and scroll state.


## Live Tool State Transfer

Source review confirmed that the active dock-tree receiver used its own saved
settings after moving a panel. Reused destination slots also skipped new binding.
The candidate retains one binding per slot and captures effective live values
before dispatch. The older four-panel host uses the same registry and now has
a state adapter. Registrations without a state adapter cannot receive tool state.

Review exposed active-path issues in the candidate before publication:

- Derived automatic scrolling could start a public edit before initial binding.
- Combo feedback could invalidate its own restore and skip the final save event.
- A foreign edit could inherit the stale outer restore's scroll intent.
- Source changes between individual destination setters needed pre-write checks.
- Source capture could replace a registration or root before destination delivery.
- Receiver host identity and revision needed checks around source-validation
  callbacks, throughout restore, and before compensation.

The first narrow renderer build passed with zero warnings/errors in 1m47.60s.
Portable build224 passed with zero warnings/errors in 2m16.83s; run172 passed
19/19 scroll and value-copy cases in 0.340s. Build225 passed in 19.80s; run173
passed 26/26 cases in 0.460s. Build226 passed in 20.59s; run174 passed 38/38
cases in 0.289s. Both later builds also had zero warnings/errors. Desktop build73
compiled the editor and tests with zero warnings/errors in 3m11.75s. All 10,870
source inputs stayed unchanged during those final targeted and desktop checks.
Receiver host-proof corrections remained pending at that snapshot, so these
results are interim evidence. Real-panel tests compiled but did not execute here.


Further review repaired normal scroll-veto behavior and initial binding of a
replacement for an already destroyed slot. A Rows list could also move during
`PropertyChanging` while its old panel retained the list field. A focused receipt
now checks the exact list/node/parent lifetimes and ancestry for owned writes and
source-state validity. Tree compensation uses the same captured root-transform
proof as forward admission.

Build227 found three CS0103 errors because a new editor-host test was placed in
a Headless-linked fixture. The test moved to the editor-only fixture; existing
portable detachment tests remain unchanged. No dependency or target change was
needed.


Build228 passed with zero warnings/errors in 18.46s. Run175 passed 48/48
targeted cases in 0.486s, and run176 passed 1,411/1,411 portable cases in 24.611s,
with no failures, warnings, or skips. Final review then found an unguarded
`SetVirtualBounds` call inside owned scroll restore. Each virtual-bound write
now uses pre-write and post-callback admission. New portable cases cover veto,
value round trips, and reparenting at either bound. Ordinary setter notification
and veto behavior remains unchanged. The following final checks use this repair.


Final build229 passed with zero warnings/errors in 1m35.70s. Run177 passed 57/57
targeted cases in 0.379s. Full run178 passed 1,420/1,420 in 22.233s, with no
failures, warnings, or skips. Desktop build74 compiled the matching editor and
tests with zero warnings/errors in 2m52.89s. All 10,874 source inputs remained
unchanged. Ten new real-panel/host cases compiled only. Source review found no
remaining blocker in this bounded transfer contract. Relative-link and diff
checks passed. Windows execution and live multi-window acceptance remain pending.

## Windows fixture failures and local reproduction

A separate Windows 11 run used .NET SDK 10.0.401 and frozen commit
`63af584ce0e2e4278bd57b9efddad2ff259b6c77`. The managed build passed with zero
warnings and errors. Across several runs, 301 distinct cases completed; their
latest outcomes were 272 passed and 29 failed. Twelve cases did not complete.
This was test-host execution, not editor, GPU, IME, or visual acceptance. It did
not cover the ten newer tool-state cases at `8bde3d635`.

The focused runs used the build wrapper, `--no-build --no-restore`, NUnit worker
count zero, and a 180-second session limit. Isolated runs also used the 30-second
blame-hang inactivity limit without dumps. No assertion, timeout, or platform
check was changed. Tool-panel state (2/2) and asset selection (6/6) passed.

| Failure group | Frozen-source evidence and next check |
|---|---|
| Hierarchy menus and animation admission | All 15 menu cases and the isolated animation admission case rejected `World = null` in `SceneNode.UnlinkTransform`, through `PhysicsChainWorld.BeginWorldObjectMutation`. Panel activation attaches a child before replacing its default transform. These shared methods are unchanged between the Windows snapshot and `8bde3d635`. Validate an exact old-transform unlink permission, with unrelated world mutations still rejected. |
| Grouped clone retirement | Both isolated grouped cases completed clone, Undo, Redo, and Undo, then retained custody after `Undo.ClearHistory`. A second Windows run added only four diagnostic output statements. Both unchanged assertions failed with `Owned storage changed lifetime during cleanup`, at `OwnedHierarchyRetirement.Validate`, immediately after debug-handle disposal in `TransformBase.DestroyOwnedHierarchy`. Source review identifies the render-info command list captured in the hierarchy construction receipt. Validate separate debug-handle ownership with the real runtime service. |
| Ordinary queued teardown | Some hidden-scene and clone cases failed teardown at `TransformBase.OnDestroying` calling `EventList.Clear`. The stack does not identify which exact-authority condition failed. A portable baseline separately reproduces rejection of removal from a queued owner. Use the existing removal admission for removal operations; keep add and replacement admission unchanged. Windows reruns must determine whether this also resolves the run-composition failure. It is not proof of the earlier custody cause. |
| Private asset alias | Canonical clone deserialization skipped the private property that contained the first asset anchor. The writer included nonpublic properties, but the reader did not. Match reader discovery to the existing writer and check private-only and inherited/shared alias roundtrips without changing the format. |
| Float key Undo fixture | The fixture's constructor synchronized incoming value 2 to outgoing value 4 before removal. Author distinct values explicitly and assert them before removal as well as after Undo. |
| Detail retirement fixtures | The callback watched a child list that `EnsureVerticalLayout` replaced before the first card. Follow the current transform and retain the first-card retirement assertions. |
| Enum binding retirement | A pre-change callback disposed the pending editor binding before the combo read it. Clear only the exact retired binding after its field commit so the combo's existing revision check rejects the stale continuation. Keep disposed-binding access invalid. |
| String detail sizing | The original test also failed locally at line 67. A single request can reject a stale capture; the host must continue the request/pump loop. Driving that loop exposed a second failure on reactivation: fixed-size text rebuilt its local result without invalidating the canvas, leaving its accepted page stale. Preserve valid accepted text on activation and request measure after a necessary local rebuild. |
| Hierarchy lifetime/navigation stalls | The active cases were `ActivationDuringStopWaitsUntilTheRowSubtreeCanBeRebuilt` and `CancelledNavigationDoesNotSelectOnLaterPointerFocus`. No hang stack was captured. Read-only review found no proven unbounded loop. These require isolated reruns with step diagnostics or thread stacks; do not increase timeouts to pass them. |

Local diagnostic build230 compiled the existing string fixture and its production
sizing component into the portable runner: zero warnings/errors, 2m56.83s.
Run179 reproduced the line-67 timeout (0/1 passed, 5.781s). Test-only builds231
and232 used the already built project references. Run180 recorded an invalidated
canvas with no pending/running task and a required full capture. Run181 drove the
normal host loop and reached the reactivation timeout at line 70 with an idle
canvas. Build233 and run182 required a current accepted text result; the same
reactivation failure remained. These are diagnostic snapshots, not validation of
the later source fixes. The five-second bound remained unchanged.

The unfinished animation view-transfer work remains separate in local stash
`95deb905c50084b4618aa1fe0ef9f04c46e80607`, with an ignored patch/archive copy.
It was not built or published and is not part of this repair checkpoint.

Test-only build234 found two compiler errors in a new test's standalone null-forgiving
expression. Assigning its result to a discard fixed that setup error. Build235
passed with zero warnings/errors (11.87s), against the unchanged production
references from build230. Separate fresh-process baseline runs produced:

- Run183: world-unlink fixture 3/10 passed. All five positive creation cases
  reproduced the reported world-transfer exception, including actual activation,
  required transforms, and a registered physics scheduler.
- Run184: queued-transform fixture 2/3 passed. Explicit removal from a queued
  owner reproduced the exact-authority rejection. Ordinary queue draining passed.
- Run185: private YAML roundtrip fixture 0/2 passed. Private-only data was lost;
  the shared private/inherited case reproduced the missing `$xrasset0` anchor.
- Run186: debug ownership fixture 1/3 passed. Failure-created storage entered the
  outer receipt. The full debug-service test hit missing profiler-worker startup
  before the cleanup assertion; isolate its real debug-handle factory from host
  preferences before claiming a cleanup reproduction.

These expected failing baselines did not replace production binaries or alter
assertions. The source repair remained unbuilt during these runs. Review then
required the unlink exception to apply only when ordinary null-world admission
is absent; existing allowed subtree removals must retain their behavior.

Build236 passed with zero warnings/errors (11.58s) using the old production
references. Run187 used the real debug-handle factory with fixed test transform
preferences, avoiding full engine startup. It passed 2/4 cases: the null-world
subtree case and the unrelated-storage lifetime rejection. The debug tests
confirmed captured render-command storage and failure-created storage in the
outer receipt. A fixed allocation-count assertion was replaced with the actual
ownership invariant: no captured `EventList<RenderCommand>` and successful
canonical retirement.

Build237 found a missing namespace import in the new unlink call (two compiler
errors). After the import repair, full headless build238 passed with zero warnings
and errors (1m51.45s). Run188 passed 19/20 focused cases. The remaining new test
observed World after the outer transfer had resumed and republished it to the
retained child. It now records the null-world state at the unlink rejection,
proves the rebind callback ran, and still checks final parent and owner retention.
No existing Windows assertion was removed or relaxed.

Build239 passed with zero warnings/errors (19.92s). Run189 passed **20/20** focused
cases (1.071s); run190 passed **1,440/1,440** full portable cases (21.781s), with
zero failures, warnings, or skips. The frozen source manifest contains **10,885**
inputs. Editor/test compilation and repaired Windows fixture runs remain separate.

Later Windows step markers on the original frozen snapshot placed both incomplete
hierarchy cases inside their row-realization helpers: after panel creation or
activation, before `SynchronizeVirtualRows(400)` returned. Neither test reached its
named navigation or teardown behavior. The realization loops are bounded in
source. A candidate cold-path delay is `NativeUiIconFont` calling
`FontGlyphSet.LoadDefaultUIIconFont`, which tries a synchronous `seguisym.ttf`
import before the installed test-font fallback. An isolated font-load diagnostic
is needed to establish that cause. No timeout or platform guard was changed, and
no font-path change is included in this repair snapshot.

Final desktop build75 compiled the editor and test assembly with zero warnings
and errors (3m16.82s). The same 10,885 source inputs remained unchanged through
headless build239, runs189/190, and desktop build75. Forty-one relative links in
the five maintained UI documents resolve, and `git diff --check` passes. Updated
Editor-only enum and float fixtures remain execution-pending on Windows; Linux
compilation is not Windows acceptance. The ordinary queued-teardown interaction
and the two row-realization stalls also require their Windows reruns.

## Explicit icon-font input for retained fixtures

The row-realization source reads `NativeUiIconFont` while creating the arrow,
toggle, and unload glyphs. The previous `UITestShaderScope` installed only the
default text font. Windows icon lookup could therefore enter the synchronous
system-font import path. The two Windows step-marker runs did not identify the
instruction that stalled. A separate, bounded cold-font diagnostic remains needed
before attributing those stalls to font loading.

The scoped default-font API now accepts an explicit icon font. It validates both
inputs before installation and releases only its installed references on disposal.
It preserves caller resource ownership, nested-scope rejection, and the original
text-only overload. Without an override, normal Windows icon lookup is unchanged.
The test shader scope supplies numeric metrics for the hierarchy icons as well as
text. This separates control behavior tests from system-font import testing; it
does not repair or validate the normal Windows import path.

Headless build240 passed with zero warnings/errors (2m28.09s). Run191 passed the
five font-scope cases (0.120s). Full run192 passed **1,445/1,445** portable cases
(21.780s), with zero failures, warnings, or skips. The font-scope tests cover
independent roles, rejected partial installation, repeated disposal, replacement
scopes, caller resource survival, and the original text-only API. Windows row
realization must still run with its original behavior assertions and time limits.

Windows revalidation of `68ab6d1675622df30bfef6c26861900a12adb76f` stopped
before build or tests because the validation disk lacked space. A large
ImageMagick temporary file from the earlier isolated run remains preserved;
targeted cleanup approval is pending. This is not a failed repaired test result.
Further source review found that desktop bootstrap registers the Skia font
rasterizer. It lays out all supported characters in cells sized by the largest
glyph, without an explicit atlas-size bound. This is a resource-use lead, not
proof that font import caused the two incomplete tests or that temporary file.
Do not run another disk-producing diagnostic until the capacity blocker is resolved.

Desktop build76 compiled the editor and tests with zero warnings/errors (3m23.36s).
All **10,886 source inputs** remained unchanged through headless build240,
runs191/192, and desktop build76. Read-only source review found no blocker in the
scoped font change. The repaired Windows fixtures and the normal system-font import
remain execution-pending; no Windows, visual, GPU, or performance result is implied.

## Guarded Animation toolbar publication plan

The scoped-font checkpoint is published at
`c1bbb973d2946dabdaa4bc7c299600cde8c3dded`. Its first push failed because the
existing HTTPS Git route could not obtain credentials. A later authorized single
retry through the unchanged route succeeded after remote ancestry inspection.
The remote SHA matches. No credential, permission, identity, remote, or account
setting changed. GitHub reports no attached check runs, workflow runs, or statuses.

The revised Animation transfer draft remains local in stash
`196632e3217b7340488f9ddd71e65eb41fe28653`. The earlier draft and comparison
stashes remain intact. Draft build241 passed with zero warnings/errors (2m36.87s).
Run193 passed 17 portable value/routing cases (1.458s), with no failures, warnings,
or skips. Those cases use fake transfer receivers; they do not execute the real
Animation panel or hosts. All 10,891 draft source inputs stayed unchanged.
The draft's editor paths were not compiled in that run. Do not publish the draft
or close a transfer item from these results.

### Confirmed source defects

The draft's `AnimationPanel.ViewTransfer.cs` calls `RefreshTimeInput` and
`RefreshValueInput`. In `AnimationPanel.cs`, these methods assign
`UITextInputComponent.Text` through its ordinary setter. That setter uses
`XRBase.SetField`, which calls `OnPropertyChanging` and then assigns the field
without rechecking caller ownership or newer writes. A callback can reparent the
input or assign newer text before the outer assignment. The draft's checks run
after that assignment and therefore cannot prevent the stale write.

The value refresh can retry up to three times against changed state. A transfer
must instead publish one fixed pair of values under its original ownership proof.
The draft also checks time text after value publication only when a key is selected.
A value callback can therefore change the time field in the no-key case without
causing transfer rejection.

There is a second callback boundary inside `UITextInputComponent.OnPropertyChanged`.
It raises external notifications through the base method, then updates cursor,
selection, and `TextComponent.Text`. A guard only around the initial `_text`
assignment does not protect these later writes. These are source findings, not
newly executed Windows failures.

### Bounded implementation sequence

1. Add an opt-in guarded text-publication path in a focused
   `UITextInputComponent` partial file. Reuse `XRBase.SetFieldValidated` and the
   existing typed-state validator pattern in `UIListTransform.ScrollWrites.cs`.
   Track every text-write attempt, including ordinary and equal-value attempts,
   so a callback that writes a value and then restores it cannot revive an old
   guard. Preserve ordinary deserialization and text-editing behavior. Do not
   change the global `XRBase` event contract.
2. Carry the fixed guard through the text notification path. Recheck the input
   lifetime and caller proof before and after callbacks and before later cursor,
   selection, or display-text writes. A display sibling must retain its exact
   node/component ownership. Use an explicit guarded display-text write where
   needed; a final check after an unguarded sibling write is insufficient. Nested
   writes must retain their own admission and restore any outer guard in `finally`.
   On conflict, stop subsequent work and preserve the callback's edit or adopted
   object. Do not roll back into a newer owner.
3. In the preserved `AnimationPanel.ViewTransfer.cs`, capture the exact two inputs,
   node/transform/parent identities, clip/root/track proof, and both expected text
   values before the first publication callback. Use the guarded path once per
   input instead of the ordinary refresh methods. Recheck both text results after
   the second publication, including the no-key case. Capture and check any
   playback component/time used for that no-key display; do not seek or change
   global selection to make a restore pass.
4. Retain the draft's binding proof at each final scroll/bounds write. Verify row
   projection and source ownership before restoring scroll. Keep the receive-first
   contract: a rejected destination does not authorize source closure, and partial
   destination changes do not justify rollback into a callback's newer state.

This work needs no new dependency, persistent format, window key, renderer default,
or owner decision. Keep it separate from system-font import diagnosis and from
the pending asset recovery and paragraph-analysis proposals.

### Regression and acceptance matrix

| Layer | Required case | Required result |
| --- | --- | --- |
| Portable text input | Initial admission fails, including an equal-value request | No field write or notification; caller cannot report acceptance. |
| Portable text input | `PropertyChanging` assigns newer text; writes away and back; or makes an equal-value attempt | Old guarded write rejects and preserves the newer attempt. |
| Portable text input | Callback reparents the input, replaces its transform/sibling, queues destruction, or destroys it | No later write through the old owner and no recreated retired sibling. |
| Portable text input | `PropertyChanged` changes ownership or text before caret/display propagation | Stop remaining old-owner work; preserve callback text and adopted display state. |
| Portable text input | Veto, thrown callback, suppressed notifications, or validator reentry | No stale write; cleanup releases only its own guard; a fresh later write can proceed. |
| Portable text input | Successful guarded write, nested guarded write, and unchanged ordinary editing | Exact accepted text, normal caret/binding behavior, and no retained guard. |
| Animation panel | Time/value callback writes newer text or reparents either input | Transfer rejects and preserves the callback-owned text/input. |
| Animation panel | Value callback changes time text without a selected key | Transfer rejects; both fixed text values are required for acceptance. |
| Animation panel | Clip/root/track changes, pending row/value refresh, or scroll callback changes the view | Reject before a stale later write; retain the source panel. |
| Actual hosts | Inactive destination retains clip A while source/current clip is B | Activate and accept B without changing global clip selection. |
| Actual hosts | Save fails after B restore; prior view is A | Source stays open; compensation does not restore A or change global selection. |
| Actual hosts | Save fails with a still-current same-clip prior view | Restore only the exact retained prior view, or report compensation failure. |

Add portable cases under `XREngine.UnitTests/Rendering/` and link them in the
Headless project. Extend the preserved Animation fixture for panel cases; add real
host cases rather than treating fake receiver routing as host coverage. Run builds
sequentially through `Tools/Invoke-AgentBuild.ps1`, then focused portable cases,
the full portable suite, and editor/test compilation against one frozen snapshot.
Run actual panel/host cases on Windows when disk capacity permits. Keep existing
assertions and time limits. No live graphics, IME, or performance acceptance follows
from portable tests or managed compilation.

Windows revalidation remains held by disk capacity and pending targeted cleanup
approval. No additional laptop operation is part of this plan. The original
implementation reconciliation remains 51/60; this plan closes no code item.

## Guarded shared text writes

The shared controls now provide opt-in `SetTextGuarded` methods. The input path
retains the existing display sibling before callbacks, along with text, caret,
focus, and formatting intent. It rejects detached components and retired nodes,
including a node already queued at entry. Nested ordinary edits keep their normal
behavior and invalidate the older guarded attempt. Equal-value attempts also
invalidate it. The display component checks its own caller proof before its field
write and after notifications. These changes reuse `XRBase.SetFieldValidated`;
they do not replace the base event system.

Review required explicit membership checks because `DetachComponent` can leave the
component's `SceneNode` pointer intact. It also required distinguishing an actual
reference no-op from a veto of an equal-content, distinct string. Suppressed calls
retain normal notification suppression and do not update caret/display fields.

Build242 compiled production code but found two errors in the new fixture: a
conditional expression without a common target type and a missing transform
namespace. After those test corrections, test-only build243 passed with zero
warnings/errors (11.53s). Focused run194 passed 28/29 cases (0.834s). Its only
failure reproduced a recovery gap: an earlier text change could commit while a
caret callback rejected its clamp. A fresh guarded request for the same text then
left cursor8 instead of clamping to5. The test retained its assertions.

`PublishGuardedTextEffects` now provides one validated caret, selection, and display
sequence for changed text and fresh unsuppressed no-op recovery. It raises no extra
input Text notification, and a no-op still returns false. Build244 passed with zero
warnings/errors (1m34.51s). Run195 passed **29/29** focused cases (0.822s); full run196
passed **1,474/1,474** portable cases (22.388s), with zero failures, warnings, or skips.
Read-only review found no remaining blocker in this bounded field-publication
contract. Parent/transform ownership remains the caller's responsibility. Renderer
allocation and rollback are not made transactional by this guard.

The Animation integration remains separate from this shared-control checkpoint.
Its preserved draft is not restored wholesale, and its ordinary toolbar refresh
tail must be replaced before publication. Actual host compensation and Windows
input execution remain pending. The shared-control tests are not evidence for
those paths or for the original Windows row-realization stalls.

Desktop build77 compiled the editor and tests with zero warnings/errors (2m59.97s).
All **10,890 source inputs** remained unchanged through build244, runs195/196, and
desktop build77. Source review and `git diff --check` passed. No Windows test,
graphics, IME, or performance acceptance is implied by this compilation.

## Guarded Animation transfer implementation

The reviewed values and routing pieces were rebuilt selectively from the preserved
draft. No stash was applied or removed. The unsafe ordinary toolbar writes were
replaced with two fixed shared guarded calls. Source review found two later gaps:
the applied toolbar proof was discarded before host save, and early row scroll
callbacks ran before the binding held that proof. One fixed receipt now covers both
boundaries and remains attached to the returned destination receipt.

Owned row projection also omitted destination scene-path candidate rows without
scheduling their return. A transient deferred flag now schedules ordinary refresh
after transfer. A fixture checks that the candidate section returns on the next
tick without changing restored member/key selection. This does not add saved state.

Headless build 245 passed with zero warnings/errors (2m32.46s). Targeted run 197
passed **47/47** value, routing, and shared text cases (0.849s). Full run 198 passed
**1,480/1,480** portable cases (22.009s), with no failures, warnings, or skips.
Editor build 78 compiled production code, then found two fixture-only compiler
errors: a missing component namespace and a target-typed constructor before a
method call. Only `NativeDockAnimationTransferTests.cs` changed to fix them.
Test-assembly build 79 with `BuildProjectReferences=false` passed with zero
warnings/errors (40.63s). All **10,896 final source inputs** then stayed unchanged.
Portable and production compilation inputs did not change after their checks.

There are **33 Animation panel cases** and **four real dock-host cases** in the
editor assembly. They cover accepted text, nested writes, reparenting, caret and
display callbacks, no-key time checks, row/scroll admission, candidate refresh,
inactive destination clips, save failure compensation, and edits during save.
These cases are compile-only here. The real-host fixture uses isolated in-memory
preferences and an empty platform-path provider. It injects its save callback at
the second path lookup; it does not depend on duplicate JSON setters raising events.

The separate Windows task completed the approved one-file cleanup and reran exact
`68ab6d1675622df30bfef6c26861900a12adb76f`. Its clean build passed with zero
warnings/errors. Seven original focused fixtures passed **71/71** cases, including
clone cleanup, enum and float inspectors, string sizing, and animation admission.
The broader run passed 224, failed 2 in asset-spawn teardown, and left 129 incomplete.
Those two asset cases passed in isolation. Six of 12 tool-panel cases failed during
console activation because their fixture had no attached canvas. Menu, lifetime,
and workflow runs still hit the unchanged 30-second inactivity limit. These are
separate runs: 238 distinct completed cases comprise 230 consistent passes, 6 failures,
and 2 mixed outcomes. No skipped cases or GPU/live-UI checks were reported.

That Windows snapshot predates the explicit test icon-font commit `c1bbb973d`.
Use the later fixture inputs for a bounded stall rerun after checking disk space;
do not infer that normal production font import is fixed. Triage the canvas setup
and combined teardown separately. No laptop operation ran in this cloud task.
Analyzer, recovery-journal, and window-key decisions remain held.

## Tool-state fixture canvas setup

Windows at `68ab6d167` showed six tool-state failures before their intended
assertions: active console commands construct a popup, which requires an attached
canvas. The fixture provided a plain boundable root. Production dock hosts already
provide canvases. All 11 console/profiler fixture roots now use `UICanvasTransform`
with `UICanvasComponent`; detached foreign targets and mutation assertions are
unchanged. No production code changed.

Test-only build 80 found one missing component namespace in the helper. After that
import correction, wrapper build 81 with `BuildProjectReferences=false` passed
with zero warnings/errors (38.84s). All 10,896 source inputs stayed unchanged during
that final check. Portable inputs and production assemblies remain those of the
previous Animation checkpoint; no new portable result is claimed. Windows execution
of the corrected setup is pending.

The combined asset teardown failure remains unattributed. The global destruction
drain stops at its first failed entry, so the next fixture can encounter a later
entry from an earlier producer. Do not assign cause from test order. A bounded
diagnostic should record the failing target's type/construction identity and
destroyed/queued flags, its queue token, its exact child-list identity/flags/count,
and the authority's exact owner/collection matches and current flag. Compare the
list reference before dispatch and at failure. Keep the original exception and
guards; exclude names, paths, and object-cache dumps.

## Windows revalidation with explicit fixture fonts

The separate Windows executor validated exact
`a5e2c0872f7d428ecedbec564a4f7e176f730368`. Its wrapper build passed with zero
warnings/errors. The broad run completed all 398 cases: 381 passed and 17 failed,
with none skipped or incomplete. All 12 tool-state cases and all 4 real-host
Animation transfers passed. The earlier individual lifetime and workflow stalls
completed. Both previously mixed asset-spawn cases passed in the broad run; their
earlier cause remains unproven. Explicit fixture fonts establish these test results
only, not production system-font import correctness.

The 17 failures divide into four bounded paths:

- 13 hierarchy menu cases used synthetic pointer input and realized 400 pixels of
  rows, but their applied list geometry was zero. Menu Show captures layout and
  synchronizes sibling lists from actual size. That can recycle the source row;
  the existing session guard then closes the menu. The fixture now applies matching
  canvas, panel, and list geometry before realization. Its explicit recycling case
  also updates applied geometry. Positive and zero-viewport controls keep the strict
  lease rejection visible.
- 2 Animation guard cases called RefreshRows through reflection from callbacks. The
  expected InvalidOperationException was wrapped in TargetInvocationException. One
  fixture helper now rethrows the inner exception with ExceptionDispatchInfo.
  Production behavior and assertions are unchanged.
- 1 keyboard-selection case changed the provider selection after row realization,
  then fabricated a presented packet without synchronizing the new provider
  revision. The ordinary row-lease query does not prove the binding revision; the
  accepted virtual-focus dispatch does. PresentRow now synchronizes and checks the
  exact realized row and lease before packet construction. Portable stale/fresh
  controls exercise this distinction without changing the focus guard.
- 1 console teardown case called the public ScrollOffset setter after destruction.
  Other terminal panel setters are inert, but scroll started a new intent and
  threw inside its owned-write guard. The public setter now returns for a terminal
  panel. Internal owned restore checks remain strict. The existing teardown test
  also requires unchanged state and scroll intent revisions after late writes.

The Windows executor stopped at its disk start threshold before extra focused
Animation reruns. Those cases already ran in the broad run (31 passed, 2 wrapped
failures). No further deletion is approved. This cloud task performs no laptop
actions. The fixes above remain candidates for a frozen Windows rerun.

Validation of this candidate: wrapper headless build 246 passed with zero warnings
and errors (2m43.42s). Run 199 passed 4/4 focused viewport/provider-revision cases
(0.979s); full run 200 passed 1,484/1,484 (25.543s), with no failures, warnings, or
skips. Wrapper editor/test build 82 passed with zero warnings/errors (3m10.77s).
All 10,897 source inputs remained unchanged through those checks. Relative links
in the five UI documents resolve 42/42, and diff whitespace checks pass. These are
Linux compilation and portable execution results. The corrected editor cases
still require a frozen Windows run; none of the 17 failures is marked accepted
from these checks alone.


## Ordinary hierarchy focus across panel reactivation

At the start of this slice, the repair at
`33340544b25e5a5fb66dbd2c6ce1f9bcdf95d88f` awaited Windows validation.
Capacity preflight reported only 18,313,216 bytes above its existing
2 GiB start threshold, below the previous incremental interval's 52,531,200-byte
use. That preflight performed no fetch, build, test, or cleanup. The later Windows
result below supersedes this initial capacity block.

The bounded focus contract retains an ordinary source row across a stop and
reactivation of the same panel. It does not persist focus or change document,
layout, or asset formats. Review required an early exact-intent clear, identity
checks for the source and owner graph, a per-stop skip target that prevents
cleanup from retrying a rejected clear, and removal of the receipt after completed
or canceled input work. A cleanup failure cancels the receipt. Row focus waits for
the replacement's realized binding and presented packet. Selection is unchanged.

Shared guarded virtual focus carries a pure caller proof and exact target through
request callbacks, deferred resolution, the focus field write, and registration.
Newer null, equal, or away-and-back focus intents cancel older work. It preserves
callback-created requests and rejects lifetime changes. The resolver uses an
instance predicate; it does not allocate a closure on each presentation poll.
The panel creates its request closure only when a realized row is ready.

Initial validation: headless build 247 passed with zero warnings/errors (2m33.69s).
The first 16 portable cases passed in run 201 (1.298s). Review then found that the
new reconciliation path ignored a control's focus veto and did not finish a newer
focus chosen during clear callbacks. Test-only build 248 passed with zero
warnings/errors (14.13s). Run 202 reproduced all three added cases: 16 passed and
3 failed (1.387s). The failures left a vetoed target accepted, activated a new
target after a vetoed old-target release, or left the newer target unfocused.
The candidate was not published with these failures.


Build 249 passed with zero warnings/errors (1m55.09s). Run 203 covered the new
fixture and five existing focus/input/menu fixtures: 128/129 passed (4.429s).
The existing fresh-row dispatch control exposed a regression: the new final
intent check also applied to ordinary accepted focus, whose setter advances its
intent. The check is now conditional on a guarded request. The other epoch and
identity checks still apply to both routes. A provider-callback case also checks
caller proof immediately after the final row lookup.

### Windows repair result and remaining combined cleanup failure

The parent later completed Windows validation at exact `33340544b`. All 17 prior
failures passed. Focused results: hierarchy menus 17/17, workflow 9/9, Animation
view transfer 33/33, owner teardown 3/3, and separate real-host transfer 4/4.
The build had zero warnings/errors. The broad run completed 400 cases: 398 passed,
2 failed, with no skips or incomplete cases. Both asset-spawn cases failed only
in teardown at the exact structural-list removal guard. They passed 2/2 when
run separately afterward. Authorized cleanup restored disk capacity; this cloud
task did not perform laptop operations.

The failed guard rejects list/authority mismatch, inactive or replaced authority,
a destroyed owner, or a destroyed list. Queued flags alone are admitted. A drain
stops at the first failure after clearing that request's token, so two successive
teardown failures can consume two older invalid targets. Test order does not
identify their producer. Partial transform cleanup that destroys its child list
before a later callback fails is a hypothesis, not an established cause.

The next diagnostic snapshots the raw destruction queues in asset fixture setup
and before its existing drain. A first-chance handler, limited to the exact guard
message and that drain, identifies the current dispatch target and logs the
specific owner/list/authority predicate. It preserves queue order, exceptions,
timeouts, and assertions, with no preflush or retry. No ownership guard is relaxed.

Windows bisection at the same repair commit found that lifecycle plus asset-spawn
fixtures pass 13/13. Dock fixtures plus asset spawning reproduce the two teardown
failures. One six-fixture dock half reproduces; the other six-fixture half passes.
The exact producer and failed guard predicate are still pending. This rules out
the immediate lifecycle predecessor as a sufficient reproducer; it does not
attribute the failure to a specific dock implementation.

The focus repair built in headless build 250 with zero warnings/errors (1m30.41s).
Run 204 passed all 130 focused input/menu/focus cases (4.999s), including 22 new
cases. Full run 205 passed 1,505/1,506 (28.292s). Its single failure was the final
storage-identity assertion in `EachGlyphPlacementInputSelectsItsOwnLayout`.
The cache is explicitly direct-mapped: a competing key can evict the baseline.
The test had assumed storage retention across 15 competing keys. The exact failed
run did not record hashes, so eviction in that run is not proven. The assertion
nevertheless demands a guarantee absent from the cache contract.

The fixture correction seeds the baseline before each key-separation check,
retains all placement-key variants, also checks caret-storage separation, and
compares complete placement output after the intervening keys. Immediate repeated
lookup still requires reuse. A deterministic collision case finds two keys in one slot
among one more key than the cache capacity, then checks eviction, equal recomputed
placement, and immediate reuse. Production cache behavior is unchanged.

Editor/test build 83 passed with zero warnings/errors (4m47.69s), including the
16 new hierarchy lifecycle cases. This is compile-only evidence for those cases.
All 10,905 source inputs remained fixed through builds 250/83 and runs 204/205.
A final focus review also found that a vetoed exact clear could unregister the
old keyboard target before discovering the veto. A focused registration test is
added before changing that order; it does not relax the veto or lifetime checks.


### Focus validation and exact cleanup reproducer

The registration test first failed in run 206: 43/44 selected cases passed, but
`VetoedExactClearRetainsThePreviousKeyboardRegistration` observed one removal.
The guarded path now waits for accepted focus loss before it removes the prior
keyboard registration. Headless build 252 passed with zero warnings/errors
(25.09s). Run 207 passed 152/152 targeted cases (4.873s). Full run 208 passed
1,508/1,508 (42.672s), with no failures, warnings, or skips. These include 23 new
portable guarded-focus cases and the deterministic text-cache collision case.
The editor lifecycle cases still require Windows execution.

The Windows validator reproduced one failure twice at unmodified `33340544`
with this exact selection: `NativeDockAnimationTransferTests.InactiveDestinationReceivesSourceClipWithoutChangingGlobalSelection`,
`NativeDockAnimationTransferTests.SaveCallbackTextMutationInvalidatesAppliedReceiptAndKeepsSourceOpen`,
and `NativeHierarchyAssetSpawnTests.PrefabSpawnsUnderAHiddenSceneParentWithoutJoiningTheWorld`.
The two dock cases pass; the prefab case fails teardown. Either dock case paired
alone with the prefab case passes. Both dock cases followed by the other asset
spawn case also pass. This is a sufficient selection, not a causal attribution.
Exception-time queue/cache diagnostics reproduce the failure but find no active
cached target: 1,155 saved queue entries, three pending requests, zero matching
active cached targets. Formatting saved objects before the drain masks the
reproduction. No guard, assertion, or timeout was relaxed.

Source review found an unconfirmed finalizer-order hypothesis. `CloneHierarchy`
suppresses cache registration during YAML construction. Transform replacement
can leave the constructor-created transform unreachable from the final clone.
That transform and its private `EventList` both inherit `XRObjectBase` finalization,
which independently queues destruction. A list finalized before its owner could
leave an exact installed authority with a destroyed list. No executed diagnostic
yet proves this path. The next bounded diagnostic records the exact failed
owner/list predicates at `HierarchyListAuthority.RequireRemovalCurrent`, then
executes the unchanged throw. No cleanup production patch is part of the focus
checkpoint.

Editor/test build 84 passes with zero warnings/errors (1m34.34s). All 10,905
source inputs stayed unchanged from headless build 252 through runs 207/208 and
this editor/test build. The 16 new editor focus cases compile; they have not run
on Windows. No GPU, native input, IME, or performance acceptance is claimed.


### Structural-list lifetime ordering

The parent reports clean Windows execution at `2dc5df71`: the 16 new focus cases,
40 hierarchy/lifecycle cases, and 32 input-focus cases pass. Its broad run passes
415/416, with one hidden-scene prefab teardown failure and no skips or incomplete
cases. The separate diagnostic variant reproduces the minimal three-test sequence
with two passes and one failure. Owner and dispatch target are the same transform;
the supplied list is the exact authority list. Active/current/list-match are true,
the owner is alive, and the list is destroyed (dispatch token 1158, lifetime 4).
This proves the failed predicate, but not the origin of the earlier list destruction.

The independent rename draft candidate is preserved locally and is not part of
this repair. Its shared-control build 254 passed with zero warnings/errors;
focused run 209 passed 86/86 (5.142s). Full run 210 passed 1,535/1,536 (38.582s).
`UITextLazyStandaloneTests.DestroyingCallbackReattachmentDoesNotQueueAnUnguardedDestroy`
failed at a destruction drain with the structural-list ownership exception. Its
producer is not established. Review also found that a rejected text-focus gain can
start a fresh focus-clear operation that overwrites a callback display edit.
That rename continuation needs further work and must not be published as complete.

The cleanup regression invokes the existing `XRObjectBase` finalizer body through
reflection on a displaced uncached transform and its private list in both orders,
then uses the normal destruction drain. Strong references and suppressed automatic
finalization prevent a second CLR finalization of these test objects. It does not
force GC, depend on timing, edit queues, or weaken the structural guard. Additional
controls cover component lists, replaced lists, ordinary lists, and explicit list
destruction. Test-only changes are built first against unmodified production.


Baseline build 255 found one fixture compile error: `SceneNode.Transform` has a
private setter. The fixture now uses public `SetTransform`; test-only build 256
passes with zero warnings/errors (24.19s). Baseline run 211 passes 5/7 (0.217s).
The two failures are list-first displaced-transform cleanup and list-first
component-owner cleanup, both at the unchanged exact-owner guard. The reverse
transform order, retired/ordinary lists, and explicit-destruction controls pass.
This establishes a deterministic finalizer-order defect independently of the
unproven first destroyer in the Windows trace.

The repair adds finalizer-only deferral for exact installed private lists. It does
not change explicit destruction, queue tokens, structural guards, or canonical
owner cleanup. An added owner-veto control checks that a refused owner destruction
leaves its list usable and that a later permitted cleanup destroys both. Production
and all eight cases are now being rebuilt together.


Repair build 257 passes with zero warnings/errors (4m20.55s). Focused run 212
passes 56/56 (1.813s), including all eight finalizer cases, queued transform and
component destruction, child cleanup, and standalone text lifetime checks. The
two baseline failures now pass without changing their assertions. The added veto
control passes; explicit immediate/deferred private-list destruction still causes
the required canonical owner-cleanup rejection. Full-suite and editor compilation
checks follow on the same frozen production snapshot.

Full run 213 passes **1,516/1,516** (37.584s), with no failures or skips.
All 10,906 source inputs stayed unchanged through build 257 and runs 212/213.
The Windows minimal sequence and broad combination still require execution on
the published repair. The diagnostic first-destroyer provenance remains unknown.

Matching editor/test build 85 passes with zero warnings/errors (4m47.57s).
All 10,906 source inputs stayed unchanged through build 257, runs 212/213, and
build 85. This is managed compilation on Linux; Windows execution of this repair
is still pending. The separate rename draft is not part of the tested snapshot.


### Rename continuation after the cleanup checkpoint

The independent rename candidate extends the existing transient focus receipt.
It captures a focused ordinary draft and selection before stop, suppresses stale
saved-ID focus fallback, resolves the replacement row, then uses guarded text,
selection, and focus writes. Active IME composition is not restored. A shared
selection-pair writer and guarded text-focus display publication are required to
prevent callback edits from being overwritten. Existing saved formats and
cross-window behavior remain unchanged. Validation follows below.

The candidate is restored from its preserved stash after publishing `a30061f4c`.
Review must repair failed-focus compensation and capture source display ownership
before clear callbacks. No rename acceptance is claimed until those gaps and
the matching full validation are complete.

Review found two more bounded ownership gaps in the unpublished rename candidate.
A failed destination write left its rename session active, so ordinary row
rebind could replay the old draft. Source capture also lacked the original row
lease and provider incarnation. Teardown must check newer draft intent through
external recycling callbacks while permitting canonical row retirement. These
are implementation blockers for the rename candidate, not failures attributed
to the published cleanup repair.

Editor/test build 86 passes with zero warnings/errors (8m42.43s) for the
restored rename candidate, before the review repairs. Its 22 new editor cases
compile but have not executed on Windows. The separate eight real-canvas
regressions are now being built against the unfixed focus compensation path.


### Windows cleanup acceptance at a30061f4c

The parent reports clean Windows execution at
`a30061f4cff175dbcd8e1e3d7d63efa50c86e10f`: `StructuralListFinalizerTests`
passes 8/8, the minimal dock/prefab sequence passes 3/3, and the combined native
UI/focus selection passes **448/448**. It includes all original 416 broad cases,
all 16 editor focus cases, and 32 additional input-focus cases. Both asset-spawn
teardown cases pass. No case failed, skipped, or remained incomplete. Explicit
private-list destruction still satisfies the expected rejection assertions.

The wrapper build has zero warnings/errors. Assertions and the 30-second
inactivity / 180-second session limits are unchanged. The source is clean,
without diagnostic instrumentation; all 10,397 source/project hashes and 231
prior evidence hashes stayed unchanged. No validation process remains. This
closes the reported cleanup failure for this executed scope. It does not cover
the later rename candidate, live editor, GPU, native IME, or performance acceptance.

Headless build 258 passes with zero warnings/errors (31.73s). Initial real-canvas
run 214 passes 2/8 (1.155s). One failure is a fixture setup error: a virtual button
disables focus input registration by default. The fixture now explicitly enables
registration for its newer-focus mapping assertion. Test-only build 259 passes
with zero warnings/errors (27.48s). Corrected baseline run 215 passes 3/8
(1.747s), with no skips. The five failures reproduce callback display overwrite
(two cases), missing input registration after release veto (two cases), and a
retained IME candidate tick after rejected focus gain. Production remains unfixed
for this baseline; normal gain/clear, gain veto, and newer-focus ownership pass.

The source-clear repair uses an explicitly named draft-preserving release rather
than changing ordinary focus-clear semantics. It retains the original owner and
content proof before canvas callbacks. Cleanup may finish for that original owner
without writing draft, caret, or display. Caller rejection must remain latched:
ending a callback-started IME session must not make the old caller proof valid
again. The real-canvas regression set will cover this boundary.

Integrated build 260 passes with zero warnings/errors (3m09.25s). Targeted run
216 passes **122/122** (2.844s), with no failures or skips. It includes all 22
real-canvas focus cases, eight visibility cases, and existing guarded text,
selection, focus, navigation, tab, provider, and presented-IME checks. The five
corrected baseline failures now pass under unchanged assertions. All 10,918
source inputs stayed fixed through this build and run.

Finite review found no further ownership blocker. The new visibility intent
property now has the same `YamlIgnore` and `Browsable(false)` metadata as other
UI intent counters. Final full-suite and editor/test checks follow this metadata
correction; no Windows execution of rename restoration is claimed.

Final headless build 261 passes with zero warnings/errors. Full run 217 passes
1,573/1,574; the failure occurs in the later standalone-text fixture's global
destruction drain. Runs 218–226 isolate the producer to
`LossPrechangeProofFailureStopsTheOldFocusEffects`. That test leaves its
rejection callback attached. During deferred input destruction, the callback
uses the creating `TextComponent` getter after the node has cleared and destroyed
its component list. The exact-owner guard correctly rejects this new insertion.
This is separate from the private-list finalizer failure repaired at `a30061f4`.

The fixture now removes that exact callback before teardown and explicitly checks
the normal deferred-destruction drain. Its original focus assertions are unchanged;
no production guard or queue behavior changed for this fixture repair. Test-only
build 262 passes with zero warnings/errors (15.49s), and the exact two-test
reproducer passes 2/2 in run 227. Full-suite and editor/test checks follow.

Full portable run 228 passes **1,574/1,574** (32.061s), with no failures,
warnings, or skips. All 10,918 source inputs stayed unchanged after the fixture
repair through build 262 and runs 227/228. The matching editor/test build is
pending. The new editor rename cases still require Windows execution.

Matching editor/test build 87 passes with zero warnings/errors (4m02.09s).
All 10,918 source inputs stayed unchanged through headless build 262, runs
227/228, and this build. All 40 new editor rename cases compile. They have not
run on Windows. No live editor, GPU, native IME, or performance result is claimed.


### Windows rename display failures at 24621256

The parent reports a clean Windows build at `2462125639af4750609c88730e96866d547a1f86`,
with zero warnings/errors. Rename cases pass 38/40, shared cases pass 119/119,
and the combined selection passes 581/583. No case is skipped or incomplete.
All prior 448 cases, the eight finalizer cases, and the asset-spawn cases still
pass. The published private-list finalizer repair at `a30061f4` remains accepted.

`FocusClearRejectsChangedRenameRowOwnership("display")` uses the old rename
input's creating `TextComponent` getter after its row has retired. The getter
tries to insert into a destroyed component list. This is a test-body lookup,
not the earlier teardown defect. The fixture now captures the original display
before retirement, uses it for the callback edit, and reads that same display
for the final assertion. It does not create another sibling to inspect retired
state.

`VisibilityCallbackCannotReplaceTheCapturedRenameDisplay(true)` exposes a real
construction error. The hierarchy row creates `UITextInputComponent` before its
explicit `UITextComponent`. The input's required-component attribute creates
display A; the next explicit add creates display B. The fixture removes A and
adds C, so the first-component lookup returns pre-existing B instead of C. The
row also styles B while the input uses A. The repair creates text before input,
as the shared native text-field factory already does. This makes the input use
the intended single 14pt white display. The replacement `SameAs` assertion is
unchanged. A new editor case checks count, identity, style, and retained text
before and after panel reactivation.

The animation candidate-view work is preserved in local stash
`072cee153cbd89afdeb448afb0fddca31986222c`, without publication. Its portable
suite passed 1,579/1,579 and editor/test build 88 passed with zero warnings/errors.
The rename repair is validated and published separately; its Windows acceptance
remains pending until the parent reruns the candidate.

For the isolated rename repair, headless build 264 passes with zero warnings/errors
(15.60s), and full portable run 231 passes **1,574/1,574** (27.108s), with no
failures or skips. Editor/test build 89 passes with zero warnings/errors (1m28.75s).
All 10,918 source inputs stayed fixed through these checks. The 41 rename cases
compile, including the new single-display case. Windows reruns are still pending;
this is not live editor, GPU, native IME, or performance acceptance.



### Animation candidate-view continuation audit

At `2462125639af4750609c88730e96866d547a1f86`, the live Animation view values
omit `_pathSearch`, `_candidatePage`, and `_selectedCandidate`. `CreateCommands`
also creates the search field without its committed query. Owned row restore
defers candidate discovery, and the later ordinary rebuild resets page and
selection. This is a concrete missing view-transfer behavior. Other audited gaps
are cross-host input focus, nested collection owner paths, and distinct-curve
key correspondence; they are not silently treated as complete.

The bounded candidate carries query/page/stable ID and the scene-root lifetime
through the existing view receipt. Fresh discovery resolves the ID and clamps
the page. A changed root drops the old position. The search field uses the shared
guarded text writer and contributes its intent to source and applied receipts.
No saved fields, asset writes, dependencies, or private-doc changes are included.
Review found a missing null-root comparison; capture now records current scene
selection even for an empty clip, and validation compares root identity for both
null and non-null roots.

Headless build 263 passes with zero warnings/errors (4m04.70s). Targeted run 229
passes 23/23, including five new candidate-value tests and existing routing tests.
Full run 230 passes **1,579/1,579** (34.761s), with no failures or skips. Changes
after the headless build capture affect only editor panel code and editor-only
fixtures, not portable compilation inputs. The final snapshot has 10,922 source
inputs; the matching editor/test build follows. The 14 new candidate-transfer
cases and added real-host search-save case remain unexecuted on Windows here.

Editor/test build 88 passes with zero warnings/errors (3m58.24s). The 10,922
source inputs stayed fixed through full portable run 230 and this build. Windows
execution of this candidate remains pending. The candidate is preserved locally
while two separately reported Windows rename failures at `24621256` take priority.


### Candidate-view integration after the rename repair

The preserved candidate-view changes were restored on `fdf190c3`. The append-only
document conflicts retain both the rename findings and candidate-view evidence.
All five stashes remain. Headless build 265 passes with zero warnings/errors
(18.15s). Full portable run 232 passes **1,579/1,579** (31.839s), with no failures
or skips. This run validates the integrated checkpoint; it is not a Windows
result. The matching editor/test build is recorded below when complete.

The user requested fewer full-suite runs. The validation guide now specifies
affected fixtures during iteration, one full run for a coherent checkpoint or
a stated shared-code risk, and reuse of results for unchanged source inputs.
Documentation-only edits do not trigger tests. The current full result will not
be repeated for the documentation and publication steps.


The integrated candidate passes editor/test build 90 with zero warnings/errors
(6m46.33s). All 10,922 source inputs stayed unchanged from headless build 265
through full run 232 and editor/test build 90. The portable result is
**1,579/1,579** (31.839s), with no failures or skips. The 15 added editor cases
compile but have not run on Windows. The published rename repair at `fdf190c3`
also awaits its Windows rerun. These checks do not establish live editor, GPU,
native IME, or performance acceptance.


### Animation empty-clip fixture transition

Windows execution at exact `e4a1d9a7c099589792a12b9835dce3de7e78e30d` passed
74 of 75 selected cases. The view-transfer fixture passed 46/47; value-model
cases passed 9/9, real-host animation transfer 5/5, and dock routing 14/14.
All 15 new editor cases ran: 14 passed and
`EmptyClipStillCapturesItsCurrentSceneCandidateContext` failed in `Capture()`.
The failure reproduced alone. No case was skipped or incomplete.

Source inspection identifies a fixture transition that bypasses production
notification: `SelectClip(null)` invokes the private auto-property setter by
reflection. It does not call `NativeAssetSelection.Publish`, so neither panel
receives `SelectionChanged` and `_displayedClip` still holds the fixture clip.
`TryCaptureViewReceipt` requires that displayed clip to match `CurrentClip()`.
Rejecting that stale presentation is correct.

The fixture now asserts that the old displayed clip remains and capture rejects
it, then calls each panel's existing `RefreshRows` path before the original
empty-clip capture, candidate-root, and restore assertions. No production guard
or assertion was removed. This is a test setup correction, not a production
selection or transfer change. Its Windows rerun is pending.

The independent read-only collection preview passed editor build 92 and test
build 94 with zero warnings/errors, but its four new cases remain unexecuted.
That source, its tests, and its documents are preserved in stash
`53c888c50f36c8367c8d8d475566cd498a528f5b`; none is part of this repair.


The isolated fixture correction passes focused editor build 95 (43.66s) and
test build 96 (43.72s), each with zero warnings/errors. All 10,922 source inputs
stayed unchanged through these checks. No portable suite was repeated: this
change modifies one Windows fixture and documentation only. The corrected case
still requires its focused Windows execution; compilation is not that result.



### Read-only collection preview audit and repair

The current inspector already virtualizes property rows and editable IList
rows. Its separate `RenderGenericCollection(ICollection)` fallback still built
one child subtree and called `ToString()` for every item. This is a concrete
remaining part of inspector virtualization, independent of the pending owner
decisions. The repair reuses the existing fixed-height virtual table over the
existing captured buffer. It preserves the read-only and snapshot contracts.

Review found that the table provider checks its lease only after a cell callback
returns. A value's `ToString()` could therefore change the same display or move
the row before the cell wrote text. The new preview captures the text intent and
exact row/list/provider lease, checks them after formatting, and uses the shared
guarded display write for later callbacks. This fix is limited to the new path.

Four new editor cases cover a 50,000-item snapshot, unchanged row reuse, deferred
formatting, empty content, later display writes, and row transfer. Two existing
generic-collection retirement cases now explicitly realize visible rows before
their original assertions; their formatting moved from construction to binding.
The dictionary variants retain the same call path. These fixtures use numeric
test fonts. They do not validate production font import.

The first focused editor build (91) passed with zero warnings/errors in 43.63s.
The final callback repair requires the next focused build; validation results
appear in the checkpoint and validation documents. No portable production inputs
changed, so the unchanged 1,579-case suite is not repeated for this editor-only
slice. Windows execution of the new behavior remains pending.


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


### Grouped float-key insertion review

The next inspector gap was common-time key insertion for distinct selected
curves. The retained detail previously enabled only length/stretch for this
selection. The new action reuses selected-owner capture, exact track insertion,
and the shared authoring transaction. It preserves each curve's default value
and rejects time outside any curve or track. It deduplicates shared references,
limits a command to 64 curves and 100,000 keys after insertion, and retains one
Undo action. Existing-key editing and removal remain outside this slice.

Review found two history issues in the candidate before publication. Persistent
exact-edit generations rejected valid consecutive Undo operations; durable
history now checks authored content while each operation captures fresh peer
generations. A second issue allowed outer transaction rollback to recapture
foreign generations after incomplete compensation. A retained recovery latch
now rejects that reverse operation. The regression preserves both inserted keys
when the last callback edits both tracks away and back. Other cases cover peer
changes before later insertion, source replacement, and Undo/Redo callbacks.

Editor build 99 passed with zero warnings/errors in 44.24s. Test-project build
100 passed with zero warnings/errors in 48.63s. Both used the repository wrapper,
`--no-restore`, and `-p:BuildProjectReferences=false`; unchanged dependency
assemblies came from the prior matching build. All 10,927 source inputs stayed
unchanged. Eleven new editor cases compile but have not executed here. No shared
runtime or portable test source changed, so the 1,579-case portable suite was not
repeated. The next Windows selection is the two affected float-curve fixtures.

The parent also supplied clean Windows evidence at exact `ecaecde29`: 11/11
selected cases passed (one empty-clip correction, four preview cases, six
retirement cases), with no skipped or incomplete cases and a zero-warning/error
build. This closes those fixture checks only; live preview and performance
acceptance remain open.


### Fixed-height linked-list virtualization

Source audit found that `RenderLinkedList` constructed every node editor eagerly.
Known fixed-height editors now reuse the existing virtual table provider; custom
and taller editors keep their old route. The first source capture and structural
rebuild remain O(N). This bounds realized UI nodes without claiming that all
linked-list work is constant time.

Review corrected draft defects before publication. Separate row source snapshots
would become stale after a value edit, so value rows retain shared authoritative
leases and add row admission separately. Remove creates its row leases at click
time. Vector cleanup preserves child bindings to the transformer. Enum reuse
calls the existing retained enum owner. Row/control identities and exact paths
reject transferred or regenerated controls. Initial creation rejects an incomplete
built-in control set; later reuse does not recapture that incomplete editor.

Build 101 failed on a missing Data.Core import in the new helper. The helper was
split into its own matching file and its import was corrected. Editor build 102
passed with zero warnings/errors (38.40s). Test build 103 found the fixture's
missing transform namespace; adding the import fixed those errors. Test build
104 passed with zero warnings/errors (46.93s). Final source checks covered 10,931
inputs. A test-only world context activates vector transformers. No production
source changed after the passing editor build.

No portable fixture or runtime source changed, so the full portable suite was
not repeated. Seven new Editor cases compile; Windows execution is not run
because the user stopped laptop work. The preceding grouped-key cases also have
no Windows execution result. Existing historical Windows results remain evidence
for their exact tested snapshots only.

Before this slice, the branch was resynced to signed tip
`c589551143e8eb69c8fc3f9f1c2cb5dd2c47e3bd`. Its tree matches the earlier
`83a017c9822827955202a41128f0a317b36e7e29` and
`21d7f284e6ed087d880aa1df7c7e2c9b39ab4df6` tips. The soft resets preserved the
index, dirty files, and six stashes. No old ancestry was merged.


## Fixed-height dictionary presenter review

The dictionary path previously created all editable row nodes eagerly. Eligible
int/default-comparer and string/default-or-ordinal dictionaries now use the
existing linked-list presenter through `EditorFixedHeightRowContract`. The shared
presenter owns row/control lifetimes. Dictionary mutation code remains unchanged.
Each binding resolves its owner's captured key object. Shared source leases
advance on Set, and Remove copies the latest leases at click time. This preserves
exact keys and existing history behavior without a second row lifecycle.

Review kept partial-key selections eager because their missing-key labels wrap.
Multiline keys and custom/tall value editors also stay eager. This preserves the
existing layout without choosing a variable-height policy. Initial snapshot and
key admission remain O(N); no measured performance improvement is claimed.

The finite source review found no production blocker. It found a fixture lookup
that required `IsActiveInHierarchy` even though the scalar test root has no world.
The lookup now skips destroyed, queued, and inactive-self branches instead. This
also excludes old queued branches after a history-driven rebuild. Assertions and
production ownership guards remain intact.

Editor build 105 passed with zero warnings/errors (42.02s). Test-project build
106 passed with zero warnings/errors (47.34s). Both use the repository wrapper,
`--no-restore`, and `-p:BuildProjectReferences=false`; only Editor and Editor-test
sources changed, so existing dependency assemblies remain applicable. Each source
manifest contained 10,934 inputs, unchanged during its build. Between the two
builds, only the fixture lookup changed. Four new Editor cases compile but were
not run. The user stopped laptop work; no Windows/live acceptance is claimed.
No full portable suite was repeated for this Editor-only slice.


## Same-panel Animation search draft ownership

Read-only tracing found that `NativeVirtualTextPanel` already retains its command
subtree. The search field normally keeps its authored text and selection across
stops. Rebuilding it or writing the same draft back would add callbacks without
closing a missing contract. The new receipt therefore leaves an unchanged draft
alone and supplies source/owner admission around stop/reactivation.

The committed query remains separate. If the source changes while stopped, the
receipt resets only the unchanged, still-owned old field to that committed query.
It does not submit a query or request focus. Newer authored intents cancel the
old write. The input-specific pre-base activation hook is needed because scene
activation visits children before panel components. Component-only activation
uses the panel hook. Shared `UITextInputComponent` behavior is unchanged.

Finite review corrected these draft defects before publication: a display
pre-write check rejected its own in-flight write; lifecycle checks mixed the
view epoch with the stop/start serial; active stale receipts retained an observer;
and reset observer replacement missed changes away and back on the selected
component. Canvas construction identities reject regenerated focus owners.
Reentrant focus release can reactivate the panel before the outer stop finishes,
so the older stop now yields to the new lifecycle and activation avoids duplicate
selection subscriptions. Terminal cleanup releases a transferred surviving
input's panel callback reference. Bound/property-backed fields and composition
are excluded from this ordinary unsubmitted-search contract.

Editor build 107 completed with two nullable warnings and no errors. The nullable
paths were corrected. Build 108 passed without warnings/errors (37.79s), before
the subscription cleanup. Build 109 (37.74s) and test build 110 (43.25s) passed
without warnings/errors before terminal callback cleanup and explicit property
binding exclusion. These intermediate passes are not final-snapshot evidence.
All checks use the repository wrapper; no full portable suite or laptop run was
started. The source manifests cover 10,937 inputs. The new Editor cases remain
compile-only until authorized Windows execution.

Final editor build 111 and test-project build 112 passed with zero warnings and
zero errors (49.18s and 48.87s). Both ran through `Tools/Invoke-AgentBuild.ps1`
with `--no-restore` and `-p:BuildProjectReferences=false`. Only Editor and
Editor-test code changed; unchanged dependency assemblies were reused. The
10,937 source inputs were identical across both final builds. All 25 new cases
compile; none ran on Windows. No live/GPU/IME/performance result is claimed.


## Ordinary menu factory receipt integration

The prior built-in row, label, arrow, separator, and line builders used ordinary
`NewChild` component factories. Checks after a factory returned could not stop
later work inside a factory callback or retain an uninserted failed component.
The bounded repair uses `RuntimeComponentCreationReceipt` and
`DetachedLeafRetirementReceipt` through a menu-specific leaf builder. It completes
configuration before attachment and keeps foreign adoption intact.

Read-only review identified two missing source checks in the first draft: label
and arrow factories needed the exact row binding, and every leaf needed the exact
content root. Both checks and regressions were added. The content-root transform
also rejects queued destruction. A leaf that entered a root collection cannot
return to detached construction. No ownership guards or existing assertions were
weakened. The reviewer found no remaining blocker in the final bounded diff.

Build 113 passed with zero warnings/errors (3m10.46s). Build 114 passed after the
first review changes (1m47.53s, zero warnings/errors). Final headless build 115
passed after the remaining lifetime checks (1m28.14s, zero warnings/errors).
All builds used the repository wrapper with project references and no restore.
The final 10,939-input source manifest stayed fixed through validation.

The 19 new portable cases passed in 1.659s. The five affected fixtures then passed
187/187 in 4.675s, including those cases; no failures, skips, or incomplete cases.
No unchanged full suite was run. Evidence is under the existing task run in
`Build/_AgentValidation/`, with logs `menu-headless-build113.log` through
`menu-headless-build115.log`, `menu-new-cases-run1.log`, `menu-affected-run1.log`,
and corresponding NUnit XML reports. Windows and live acceptance remain not run.
This does not close custom content, root/scroll factories, adopted-subtree cleanup,
or the broad shared-control item.


## Ordinary root/list factory integration and cleanup boundary

The ordinary scroll root previously attached before configuration and used an
ordinary scroll-component factory. Its list attached with a default transform
before replacement by `UIListTransform`. Both now use the same guarded leaf
builder with final transforms. Root ownership metadata is published just before
attachment; configuration and component factories remain in the detached stage.
The list attaches only after the root passes its post-callback checks.

The audit did not find an existing reserved retirement adapter for arbitrary
adopted menu content. `DetachedLeafRetirementReceipt.ValidateLeafShape` rejects
prior parent/root adoption and all children. The other `OwnedHierarchyRetirement`
input needs a complete sealed construction ledger. Ordinary menu graphs can
contain callback-added or provider-owned nodes. The patch leaves existing
`ClearMenuItems` and exact-child destruction in place; it does not detach content
to bypass admission. Custom/provider ownership and adopted-content registration
need a clear contract before that path can change. Guarded creation on the
existing backdrop/virtual-scroll owner nodes remains a separate bounded task.

Read-only review found no blocker in the root/list draft. Build 116 passed with
zero warnings/errors in 2m44.79s. All 14 new cases passed in the first affected
run. Three existing ordinary attachment cases failed only because they expected
`ContextMenuContent`, the old pre-configuration name, instead of the final
`ContextMenuScroll` name now set before attachment. The fixture now sets
`New owner content` after its transfer/regeneration callback and asserts that it
survives. Review confirmed that this retains the intended stale-write check.
No production repair, guard relaxation, or other assertion change was needed.

Build 117 passed with zero warnings/errors in 12.83s; it reused the unchanged
references from build 116. Only the transition fixture changed between source
manifests. The final manifest contains 10,941 unchanged source inputs. Final
validation passed 201/201 affected portable cases in 4.728s, with no skips or
incomplete cases. Logs `menu-ordinary-headless-build116.log`,
`menu-ordinary-headless-build117.log`, and `menu-ordinary-affected-run1.log` /
`menu-ordinary-affected-run2.log`, plus NUnit XML, remain in the existing ignored
validation run. No full suite or Windows/laptop validation ran.


## Existing-node component factory integration

The backdrop and virtual-scroll component factories used ordinary AddComponent
on already-adopted nodes. They now reuse guarded component creation and conditional
failed-component abort. No node or child graph is claimed by this adapter.

Source review found an important readiness boundary before validation: cancellation
after committed list insertion must not stop AddComponent before its lifecycle
setup finishes. The helper permits only pending-transition cancellation to wait
for successful return; intrinsic ownership and source guards stay active. It then
cancels configuration. Genuine failed creation retains an incomplete-component
record, and backdrop reuse rejects that component. A completed creation with a
later configuration failure remains distinct. Review found no remaining blocker
in this bounded draft.

Build 118 passed with zero warnings/errors (2m57.92s). The affected run was
219/221. Both failures were new virtual-scroll cancellation tests: activation
before cleanup passed, but the tests expected IsDestroyed immediately afterward.
`SceneNode.OnDestroying` uses component.Destroy() to queue component destruction.
The tests now require IsDestroyQueued, inactive component state, and destroyed
owner, while retaining activation-before-cleanup and binding/replacement checks.
Review confirmed the correction. No production change or queue drain was added.

Build 119 passed with zero warnings/errors (11.85s), reusing build 118 references.
Only the new test fixture changed between manifests. All 10,943 final inputs
stayed fixed; final validation passed 221/221 in 5.548s with no skips/incomplete
cases. Evidence logs use `menu-existing-headless-build118/119.log` and
`menu-existing-affected-run1/2.log`, with corresponding NUnit reports in the
existing ignored validation run. No full suite, Windows, or laptop run occurred.

A final direct source search found a separate ordinary factory in
`UIContextMenuComponent.OpenItemSubmenu`: host.NewChild, SetTransform, then
AddComponent<UIContextMenuComponent> before the submenu fields are published.
It was not changed by the authorization limited to existing-node backdrop and
virtual-scroll factories. The remaining-scope map records this exact gap; the
work does not claim that every menu factory is now guarded. Custom/adopted graph
retirement still requires a clear ownership contract, not weakened receipt admission.


## Submenu factory and close ownership

The final ordinary factory audit found that `OpenItemSubmenu` attached a default
child before setting its transform and adding its component. Close paths kept
only the node reference and could destroy it after a Hide callback transferred
it. The new path uses the existing detached leaf/component receipts, private
attachment, and captured submenu show version. It preserves transferred nodes
and independent shows. Cleanup admission is separate from the expired owner row.

Read-only review found and corrected switching-dismissal reentry, cleanup after
a queued child Hide, retained hidden-node show-version ownership, and stale
success after a focus callback starts an independent show. The first build
(120) compiled production code but rejected three new fixture calls that omitted
the required `out` argument. Build 121 passed with zero warnings/errors in
1m20.33s after that correction and the final focus-result check.

The first affected run completed 239 cases: 235 passed and four failed in 4.827s.
Two attachment cases attempted root Show/Hide while the scene mutation lease was
active. The independent candidate-show attachment case also needed deferred
execution. These failures require the existing bounded transition queue; they do
not justify weaker scene guards. The fourth failure came from a new fixture
callback that reopened again during teardown. The callback now fires once for
the intended interaction; assertions remain unchanged.

The repair holds only transition gates acquired by this opening, keeps the
original captured requests, and drains from child to root after attachment.
Candidate requests also drain after attachment. Both failure/drain boundaries
preserve the original exception if draining also fails. New cases cover a nested
submenu that requests a root transition and a failed factory that queues a root
request. No general ownership or queue framework was added.

Build 122 passed through the wrapper with project references and no restore:
zero warnings/errors, 1m14.93s. The final unchanged 10,946-source-input snapshot
passed **243/243** affected cases in 6.129s. This includes all 22 new submenu
cases, all existing menu navigation/teardown cases in the filter, and guarded
component/detached-leaf retirement fixtures. No case failed, skipped, or remained
incomplete. Evidence is in `build-122-submenu.log`,
`menu-submenu-gated-affected.log`, and `menu-submenu-gated-affected.xml` under the
existing ignored task run. No full suite or laptop run was performed.

The final direct factory inventory covers ordinary rows, labels, arrows,
separators/lines, scroll root/list, existing-node backdrop/virtual-scroll
components, and submenu host/component creation. `CreateContentRoot` for custom
and provider content keeps its existing populated-graph contract. Stronger
reserved retirement of arbitrary adopted graphs remains a separate contract
question. This concludes ordinary menu factory implementation.


## Explicit grouped key removal

The source audit found no existing grouped key selection. The distinct-curve
inspector cleared its key rows, and the shared virtual table retained one
selected row. The approved bounded interaction adds a separate explicit mark per
curve, requires all participating curves, and leaves ordinary table focus and the
single-curve workflow intact. It does not infer targets from time or row position.

`InspectorFloatCurveRemoveEdit` reuses the exact track edit and animation
transaction contracts. Removal-specific review found two preflight differences
from insertion: retained keys beyond a shortened end remain valid, and original
key-time order must be checked before removal can hide a malformed key. Both
conditions now have focused Editor regressions. UI review added exact live
control/table/provider admission and post-detach ownership checks, so transfer
or recycling cannot authorize stale removal or row-source clearing. Model intent
checks reject newer marks created during admission.

Build 123 reported a missing local identity declaration and two local-name
conflicts (four compiler errors, zero warnings; 5m15.81s). Build 124 compiled the
editor and production dependencies, then found two references lacking a namespace
import in the new UI fixture (zero warnings; 1m24.68s). Only that import changed
afterward. Wrapper build 125 reused the matching project references and compiled
the test project with zero warnings/errors in 43.34s. Wrapper headless build 126
also reused those references and passed with zero warnings/errors in 12.73s.

The final 10,953 source inputs remained unchanged through the affected run.
It passed **77/77** portable cases, including all **nine new marking-model cases**,
in 1.514s. The new nine backend and eleven UI Editor cases compiled but did not
run. No full suite, laptop work, or Windows execution occurred. Logs and source
manifests remain under the existing ignored run: builds 123–126 and
`grouped-removal-affected.log` / `grouped-removal-affected.xml`.


## Partial-key dictionary implementation and validation

Source review found that one absent selected-owner key forced the complete
primary dictionary onto the eager row path. The existing per-row behavior was
already explicit: common keys editable, missing keys read-only. The new
`EditorDictionaryRowPlan` retains that distinction and primary order before
using the existing virtual collection presenter. It admits only single-line
keys/read-only text and retains formatted read-only strings. Unsupported layouts
keep the previous fallback. No dictionary mutation adapter or Undo semantics changed.

Review checked exact ownership of the retained read-only control, detachment of
old editor bindings/actions, mode reuse, linked-list compatibility and multiline
fallback. No blocker remained. The added label uses no wrapping and bounds
clipping; cleanup also checks its value-node attachment, including cells that
never created an editable control.

Wrapper build 127 compiled the headless runner (zero warnings/errors, 13.20s).
The affected portable run passed 61/61, including 14 new model cases (1.660s).
The subsequent full editor/test build 128 passed with zero warnings/errors in
6m02.39s. The final source manifest has 10,956 entries. The two files changed
after portable compilation are editor-only row presentation and the Windows
Editor fixture, neither linked into the portable runner. Seven Editor dictionary
cases compile; none ran here. Three additions increase the prior unrun total
from 67 to 70. Evidence is under the existing native UI completion run in
`logs/build-127-partial-dictionary-headless.log`,
`logs/build-128-partial-dictionary-editor-tests.log`,
`logs/partial-dictionary-affected.log` and
`reports/partial-dictionary-affected.xml`. No full-suite or laptop run occurred.


## Review of grouped values, hierarchy transfer, and world proof

The grouped-value review found a notification-revision exhaustion boundary.
Preflight now reserves the write plus compensation before any member commits.
The initial portable notification fixture expected three callbacks; existing
linked-value behavior emits four. Its focused rerun passed without changing
production notification order or count.

Hierarchy transfer review found prechange scrollbar ownership and post-commit
exception compensation gaps. Guarded height/translation assignments and an
admitted partial-restore receipt repair those paths. Vulkan compilation found
a by-reference property argument; a local mapped-slice value fixes compilation
without changing the proof.

The first geometry headless build started before its runtime dependency completed
and failed with four missing-member errors. After the runtime build completed,
the sequential headless build and 35 affected cases passed. Editor compilation
then found missing imports/nullability in the separate nested draft and a test
calling SetParent on SceneNode instead of its transform. Those compiler issues
were corrected; the nested draft remains unpublished for a separate ownership
repair. Its review also fixed stale binding revision, unchanged refresh, empty
first-item rendering, and document-authority admission. No unexecuted Editor
fixture is reported as a runtime pass.


## Exact edit and authoring-action integration review

The nested-list diagnostic compile 149 failed with 18 integration errors: missing
transform imports, incorrect parent transform types, helper visibility, and a
required admission argument on ordinary calls. Source review also found ordinary
layout/component construction invalidating its own receipts, ordinary editors
entering a nested-only path, stale enum reuse admission, and transferred-control
cleanup gaps. The 19-file candidate is preserved separately; it is not part of the
independent authoring checkpoint and its 21 Editor cases have not run.

Clone handoff review added empty-list admission before private retirement and
again at settlement. A callback cannot add borrowed contents and then lose the
construction receipt. Successful handoff clears completed retirement references;
failed settlement keeps full provenance. Shared grouped-edit batch checks pass
10/10, and subset plus affected retirement checks pass 31/31.

Runtime visibility/material builds 150 and 151 pass. Headless build 152 found one
fixture error: veto assignment used the event interface instead of the existing
typed event args. The cast repair changed no production behavior; build 153 and
31 affected scene/material/root cases pass. Editor build 154 and test-project
build 155 pass with zero warnings/errors. Final review then found two independent
dirty-authority gaps: material cleanup could mark a former source document, and
a scene dirty callback could suppress a separate current world document. Their
small Editor-only repairs add three cases; no portable production input changed.

The final independent snapshot contains 10,998 source/project inputs. Editor
wrapper 156 passed in 46.79s and test-project wrapper 157 in 46.44s, each with zero
warnings/errors. The source manifest remained unchanged. Data subset build 146,
Core 150, Rendering 151, and Headless 153 also pass. Only the two Editor dirty
helpers and their fixtures changed after the earlier 154/155 compile; no portable
production input changed, so the passing focused runs were not repeated.


## Nested detail integration review

The restored draft needed three final corrections. Inner-lifetime construction
failures now forward their receipts to the retained outer context. The empty
unattached pane uses the existing strict detached-leaf receipt even when its
container changes. Dirty settlement now retains exact owner/node/document
lifetimes and rechecks each document independently after callbacks.

Build 158 found an inaccessible nested-context retention method. Build 159 found
a missing Headless link for an independent animation value type. Both were
corrected. Source review corrected three pane fixtures to observe transform
publication instead of the component-created event. The four pane cases and
30 affected canvas-focus cases passed in the combined draft (34/34). Animation
is now isolated in a preserved local stash because review found additional
pre-receive intent and virtual-row focus admission work. Those results do not
establish acceptance of the still-changing animation candidate.

The isolated source snapshot contains 11,010 inputs, unchanged through validation.
Editor wrapper build 163 passed with dependencies in 6m19.36s; test-project build
164 passed in 48.05s; Headless build 165 passed in 14.86s. All report zero warnings
and errors. The focused pane, detached-leaf, and guarded-component run passed
**47/47** in 0.370s, with no skips. No full suite was repeated. The 30 new Editor
cases compiled but did not run, bringing the published new-case backlog to **224**.
No Windows, GPU, live Editor, native input, IME, or performance checks ran.

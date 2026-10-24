# Retained UI Integration

Status: Active
Date: 2026-10-23
Code scope: [Native UI TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Environment And Scope

Validation uses the existing Linux checkout and its installed .NET toolchain.
All builds use `Tools/Invoke-AgentBuild.ps1`. NUnitLite runs the already built
headless project with `--no-build`. No Windows execution, graphics output, native
IME session, or equivalent ImGui benchmark has run here. The owner approved new
headless regression tests before live validation on 2026-10-23.

## Integration Evidence

The following results describe individual source checkpoints. They do not certify
later edits. The validation document must identify the final checked source.

| Checkpoint | Result | Finding |
|---|---|---|
| Initial expanded headless run | 199 passed, 1 failed, 200 total | World-space input accepted a raw pointer outside the output region. The pointer mapper now rejects it unless capture explicitly permits outside movement. |
| Headless build 11 | Passed; zero warnings and errors | Runtime, rendering, input, and linked test code compiled. Test fixtures use the public world-context setter. |
| Headless run 2 | 222 passed, 8 errors, 230 total | New active UI fixtures lacked a shader-asset service. Scoped test shader assets were added. |
| Editor build 5 | Passed; zero warnings and errors | Complete managed Windows-target editor build on Linux, including Bootstrap, desktop platform, OpenGL, and Vulkan assemblies. |
| Headless build 12 | Passed; zero warnings and errors | Includes the scoped shader fixture and pure dock-layout tests. |
| Headless run 3 | 226 passed, 6 errors, 232 total | Five active-canvas fixtures needed an explicit scheduling host. The remaining failure exposed a desktop material assignment defect. |
| Headless builds 13–15 | Failed | Explicit draw copies replaced unsupported reflection. Later attempts found tooltip namespace/access errors, a DPI local-name collision, and two test compilation errors. These errors were corrected. |
| Headless build 16 | Passed; zero warnings and errors | The recovered source compiled after the environment interruption. No active build remained at recovery. |
| Headless run 4 | 242 passed, 7 errors, 249 total | Six fixtures needed a scoped numeric font. One batching check found that generic transparency setup enabled depth testing and could replace the owned UI shader. Both fixes are in source for the next run. |
| Editor build 6 | Failed; zero warnings, one error | Runtime, both graphics backends, desktop, and Bootstrap compiled. The editor used a non-text date control as a text-editor generic argument. It now uses the retained text field and typed date binding. |

| Headless builds 17–18 | Failed | A mip revision width, context menu namespace, nullable font access, and test buffer constructor needed correction. |
| Headless build 19 | Passed; zero warnings and errors | Includes owned draw payloads, font snapshots, scopes, and controls. |
| Headless run 5 | 258 passed, 4 errors, 262 total | A stretch-anchor fixture expected fixed coordinates. Empty test shader sources lost their parsed uniforms. Material normalization also removed explicit array parameters. The next run uses real repository shaders and preserves owned parameter arrays. |
| Editor build 7 | Failed; zero warnings, two errors | Native tab callbacks captured two local arrays before assignment. Subscription now follows initialization. |
| Editor build 8 | Passed; zero warnings and errors | Complete managed editor build, including detached-window source. No window was opened. |
| Headless build 20 | Failed; zero warnings, one error | A batch test needed the `ShaderVector4` namespace import. Runtime and input compiled. |
| Headless build 21 | Passed; zero warnings and errors | Includes the wrapped-text repair, focus registration ownership, owned texture claims, and pure receipt state. |
| Headless run 6 | 270 passed, zero failed or skipped | Includes all new regression checks. The later required batch-texture binding change is compiled by the next editor check. |
| Editor build 9 | Passed; zero warnings and errors | Includes required texture-binding publication and the hierarchy split with canonical mutation paths. |
| Settings generator build and run | Passed | The wrapper build passed with zero warnings/errors. `dotnet run --no-build` regenerated the schema and both ignored settings files. The schema also reflects the existing `WebAudio` enum value. |
| Headless build 22 | Failed; zero warnings, one error | A new draw test needed the canvas component namespace import. Runtime and input compiled. |
| Headless build 23 | Passed; zero warnings and errors | Includes review repairs for capture re-entry, producer teardown, source invalidation, shared receipt retirement, and focus teardown. |
| Headless run 7 | 275 passed, zero failed or skipped | All linked tests passed. No native renderer or platform runtime was exercised. |
| Editor build 10 | Passed; zero warnings and errors | Final checkpoint managed editor compilation after the review repairs; 3 minutes 7 seconds. |

Disposable logs and NUnit XML are under the task's `Build/_AgentValidation/` run.
These artifacts are not required to build or test the repository.

## Root Causes And Repairs Under Verification

- `UIMaterialComponent` constructed a mesh without assigning the material to its
  base component property. Desktop batching rejected the default quad. The
  constructor now assigns the owned material, and the regression uses the desktop
  material path.
- An IME event's focus token was compared to the pointer interaction revision.
  Focus epochs and recycle revisions have separate purposes. Dispatch now checks
  each token against its matching owner state.
- Layout task completion released its working-page quota before the owner consumed
  the result. The lease now waits for both worker completion and owner retirement.
- Layout callbacks could re-enter capture, dispatch, or application. Owner guards,
  epoch checks, terminal cancellation, and callback regression tests address those
  paths. Completed pages must retire even if a disabled canvas never pumps again.
- Mutable font glyph objects could bypass snapshot revisions. The font now owns
  copies of authored glyph maps and caches immutable metric pages. Runtime readers
  use the owned data. An edited authoring copy must be assigned back to commit it.
- A layout request drained dirty changes before it could reserve a working page.
  Capacity refusal could leave later sparse capture stale. Admission now precedes
  the drain, and failed capture requires a full resynchronization.
- Applying derived size and position changed authored transform dependencies.
  Derived geometry now changes visual, clip, and hit revisions. Authored scale,
  rotation, and position still invalidate their layout dependencies.
- Wrapped text measured against the canvas width can reserve the wrong height
  inside a narrower parent. Operator-specific constraints and bounded remeasurement
  are in source for the next regression run.

## Open Rendering Proof

A GPU fence alone does not release storage referenced by a reusable Vulkan command
artifact. UI ownership must continue through raw requests, prepared operations,
recorded artifacts, submission, presentation, replay, and native command reset.
The output's hit publication must identify the generation actually shown.

A copied render command does not freeze its material, mesh, glyph buffers, shader
definitions, or callbacks. The implementation needs owned draw payloads and
producer-generation leases for mutable viewport and video surfaces. Capacity or
preparation failure must reject a complete candidate with an explicit reason.
These checks remain open until the corresponding implementation and tests pass.

## Checkpoint Review Findings

- Capture adapters can change UI while a candidate is being collected. A final
  lifetime, identity, layout, journal, and painter-order check rejects such a candidate.
- Focus-loss callbacks can request new focus during deactivation. The input
  component now rejects new focus and registration until activation.
- Source textures, shaders, uniforms, and mesh data need publication invalidation.
  Owned capture observers and an explicit raw-storage invalidation API passed their headless regressions.
- Video and web teardown must wait for an active render callback, including
  re-entrant destruction on the render thread. The source review verified the serialized teardown fix; live producer teardown remains untested.
- Publication finalizers can delay resource release and stall bounded producer
  pools. This is a known liveness limit. Deterministic publication holder/reader
  retirement remains implementation work; enlarging pools or forcing collection
  is not the fix.

## Paged Layout And Packet Ownership Integration

The next change uses copy-on-write input and output pages for layout and explicit
packet claims for canvas, input, window, collector, and backend holders. Review
found these transition requirements:

- A failed direct-backend frame can still submit GPU work. Its receipt must retain
  the packet until completion, even when no presented holder accepts it.
- Mapping lookup needs a local claim between the mapping and rendered-holder locks.
- A retirement sweep must not erase a completion result before a cohort reads it.
  Empty and custom-only frames also need a receipt retirement consumer.
- Exception cleanup must restore renderer binding and thread-local state. A
  failed collector admission must not strand the backend reservation.
- Remove old holder entries before releasing their claims. Resource destruction
  callbacks must not find a holder whose claim has already ended.

The first build of this change compiled the runtime projects. The headless test
project failed with six type errors in `UILayoutGridKernelTests`: its fixtures
still passed flat arrays to the new paged kernel API. The fixtures were updated.
This intermediate result is not a passing validation of the combined change.

The next headless build passed with zero warnings and errors. Its 283 tests
produced 281 passes and two errors. Caret scrolling accessed a missing text child
on an unattached input component. The other test expected vertical navigation to
leave a multiline field; it needed an explicit single-line fixture. Both issues
were corrected for the next run. The matching editor build found one missing
`XREngine.Rendering` import in the multiline inspector detail view. That import
was added. These intermediate failures do not change the published checkpoint's
275-test result.

The subsequent headless build passed with zero warnings and errors; all 290
tests passed with no skips. Final review then found two exceptional retirement
gaps: exhausted collector capacity needed cancellation before authoring, and
Vulkan shutdown needed an exact-owner ledger drain after GPU idle and artifact
teardown. The first compile of those fixes found missing braces on two `finally`
blocks. Those syntax errors were corrected before the next build. Final results
must include these later fixes, not only the earlier 290-test snapshot.

The final headless build passed with zero warnings/errors. All 293 tests passed
with no skips in 7.920 seconds. The allocation fixture initially measured the
DispatchProxy profiler's argument array; a concrete no-op profiler isolates the
actual unchanged-hit-set query. No production allocation path was suppressed.
The editor build passed, then the matching full desktop test-project build also
passed with zero warnings/errors after borrowed packet getters were removed.

The filtered `EditorPropertyAccessTests` run returned zero tests. Diagnostic
discovery found 6,115 tests but rejected the assembly: "Only supported on
Windows7.0". This is a platform validation blocker, not a passing Undo regression.
The test remains compiled and available for Windows execution. No platform check
was disabled. The matching source still has 293 passing headless tests.

## Shared Glyph Geometry And Input Follow-Up

The next runtime build compiled, but a new test assigned a nullable buffer read
to a non-null value. The fixture now checks that value explicitly. Review also
found a glyph-buffer resize race, shared resources consuming payload-count slots,
and raw-buffer invalidation bypassing the glyph revision fast path. The repairs
serialize storage changes, check actual byte bounds, separate byte and payload
accounting, and force byte validation after explicit raw invalidation. Bounded
review found no remaining high-priority issue in those repaired paths.

The next headless build passed with zero warnings/errors. Its 308 tests produced
305 passes and three errors. The new glyph atlas fixture lacked CPU pixel bytes;
the fixture now supplies them. The idle-layout test found a real 56-byte closure
allocation per pump. Task closure creation moved behind worker admission. The
virtual-focus test used a provider without an item-ID index; the fixture now uses
the indexed provider. The tree provider now maintains an item-ID index through structural edits.

New input tests passed for enter/leave transitions, nested blocker filtering,
paint-only hit reuse, and output-transform/hit-generation invalidation. They also
cover the repaired return-to-output cache bug. Leaving output bounds now
invalidates the cached hit query before its result list is cleared. Final checks
must include the later fixture and idle-allocation repairs.

The following runs passed 309 of 310 tests. The remaining virtual-focus fixture
first omitted the packet identity page, then selected a spare recycled row rather
than the requested row. The fixture now uses the real indexed tree provider and
selects the button from the realized row's scene node. Interaction checks remain
in place. The final editor build (`editor-ui-integration-build-13.log`) passed
with zero warnings/errors in 3m57.06s. The final headless build
(`headless-ui-integration-build-35.log`) passed with zero warnings/errors in
15.16s. `headless-ui-tests-16.xml` records **310/310 passes**, no skips, in 7.482s.
Evidence is under the task run's `logs` directory. The final source also retains
CPU data in private mesh copies after upload, with a regression assertion.

The zero-allocation tests cover warmed idle canvas requests and bounded input
queries with a concrete no-op profiler. They do not measure complete frames or
all layout edits. Inspector component actions compile in the editor; Windows
Undo execution and desktop shader validation remain pending.

## Standalone Text Ownership And Control Lifetime

The first build of activation-owned slots used an incomplete required render
record. Registration now takes its scalar identity inputs directly. Review then
found owner/collector races and historical packets replacing current membership.
A shared slot lock and separate current-registration map fix both cases.

The next test run passed 321 of 323 cases. Popup teardown used a guarded direct
child-list clear; it now destroys owned nodes through their lifecycle. A text
fixture supplied an element index to a byte-offset API; the fixture now uses the
correct byte offset. No production safety check was removed.

Focused review found that standalone font callbacks could run on the wrong owner
and that Math label teardown retained strong font observers. Callbacks now only
mark dirtiness. The preparation/render owner consumes it under the draw gate.
Both label teardown paths invoke full text destruction. A deterministic thread
regression covers deferred font refresh and newer text/atlas values. The follow-up
review found no remaining high-priority issue or new deadlock in this scope.

`headless-ui-integration-build-38.log` records zero warnings/errors in 1m11.89s.
`headless-ui-tests-18.xml` records **328/328 passes**, no skips, in 8.389s.
`editor-ui-integration-build-14.log` records zero warnings/errors in 2m49.10s.
These checks include the repaired popup, registration, standalone text, and
label teardown code. They do not establish desktop execution or GPU acceptance.

`desktop-ui-integration-build-2.log` records a matching full desktop test-project
build with zero warnings/errors in 49.52s. This compiles the new HarfBuzz and font
byte-ownership tests. Their runtime remains blocked by the Windows-targeted test
assembly on Linux. The platform check was not bypassed or reported as a pass.

## Atlas, Shaping, And Dock Host Integration

`headless-ui-integration-build-39.log` passed with zero warnings/errors in
3m01.68s. `headless-ui-tests-19.xml` records **343/343 passes**, no skips, in
8.964s. This is an intermediate snapshot before the following review repairs.

Review found that an atlas binding callback could destroy its owner before the
binding was tracked. Atlas teardown now waits for binding/initialization to
unwind. A property veto cannot free a still-bound source. Failed cleanup keeps
the owner registered for a later retry. Regression cases cover callback teardown,
initialization teardown, a detach veto, and successful cleanup retry.

Shaping review found callback-driven input changes, backend replacement missing
from cache validity, unbounded auto-fit, and unsupported direction mixtures.
Capture now freezes inputs and validates them after shaping. Backend generation
changes invalidate shaped owners. Bounded intrinsic measurement and explicit
bidi rejection preserve the supported single-run contract. Full asynchronous
shaping, bidi/fallback analysis, and wrapped shaping remain open.

Dock host review found direct child-list clearing and loss of panel instances.
The host now destroys obsolete structural nodes through the scene lifecycle and
retains panel slots. Detached slots deactivate and leave their world context.
Focus is not restored through an unvalidated live target. Cross-window workflow
and presented focus restoration remain open. The matching editor build is still
required for these host changes.

`headless-ui-integration-build-40.log` passed with zero warnings/errors in
1m24.59s. `headless-ui-tests-20.xml` passed 351 of 352 tests. The one failure
compared the expected 0.9f with 0.90000004f exactly. The assertion now uses a
0.00001f tolerance; the production measurement did not change. All new atlas
callback/veto and shaping cache/admission regressions passed in this run.

The final atlas repair preserves ownership when a custom material still samples
the source. It also clears stale intrinsic image state after a custom material
replacement, so later material removal cannot restore a destroyed image.
`headless-ui-integration-build-41.log` passed with zero warnings/errors in
1m30.64s. `headless-ui-tests-21.xml` records **352/352 passes**, no skips, in
8.077s.

Editor builds 15 and 16 exposed missing namespaces, a captured local used before
assignment, and nullable drag payloads in the new dock host. These compile issues
were repaired. Build 17 passed with zero warnings/errors in 49.04s. Further
review found an unsanitized first-launch layout and stale Inspector selection
after a hidden tab reactivated. The store now sanitizes default and null saved
entries; an unlocked Inspector resynchronizes scene or asset selection.
`headless-ui-integration-build-42.log` passed with zero warnings/errors in
13.11s. `headless-ui-tests-22.xml` records **353/353 passes**, no skips, in
7.657s, including the fresh-layout regression.

Editor build 18 passed with zero warnings/errors in 45.71s. Desktop test build 3
passed with zero warnings/errors in 56.92s. The next dock review found view-state
loss during structural changes, missing resize input, and commands that exceeded
stack bounds. The host now uses axis-correct drag handles and an Actions menu
under the full dock root. Inspector view restoration compares captured global
selection and retained target identity, so component inspection survives unrelated
dock operations. It clears obsolete detail nodes through their scene lifecycle.
Editor build 19 passed with zero warnings/errors in 48.17s before the final
pivot, menu-parent, and selection-snapshot repairs. These reviews are static
evidence; pointer, focus, and multi-window visual acceptance still require Windows.

Final matching compilation passed: `editor-ui-integration-build-20.log` records
zero warnings/errors in 47.53s; `desktop-ui-integration-build-4.log` records zero
warnings/errors in 43.85s. The runtime and headless test source did not change
after build 42 and the 353-test run. All review repairs are included in these
final editor and desktop builds. No Windows or GPU execution is claimed.

## Worker shaping and sparse quad integration

The next snapshot moves opt-in shaping into the bounded layout worker and adds
stable desktop quad slots with a separate painter-order index buffer. Editor
work adds all primary detached panels, asynchronous asset-reference drops,
shared animation mutations, and retained curve display. These changes do not
complete world-space publication, static composition layers, or editor parity.

Build 43 found a missing brace in an incomplete offscreen-route edit. The brace
was repaired and the incomplete route was removed. The new surface pool and
publication claims remain inactive preparation. Build 44 passed with one nullable
test warning; the test now asserts the diagnostic exists before inspecting it.
Build 45 passed with zero warnings/errors in 17.06s. Test run 23 passed all
370 tests, with no skips, in 7.801s. Editor build 21 passed with zero
warnings/errors in 2m30.74s after a local-variable shadowing repair.

Review found and repaired a sparse-upload hole: full storage clearing also must
clear slot initialization state before a removed slot is reused. A regression
checks committed buffer fields and the draw index after growth and hole reuse.
Layout review found pending-input changes compared against the wrong accepted
base, stale worker faults blocking newer input, incomplete backend generation
checks, and underestimated shaping scratch. Further review found dirty ancestor
early returns, boxed node comparison, and an unbounded language string. Focused
regressions cover these cases. The repaired source requires the next matching run.

Editor review found stale asset completion on the render queue, deferred property
invalidation, incomplete component rollback checks, and absent legacy layout
records loading a default tool set. Completion now uses the scene-edit queue and
direct property notifications. Component operations verify all targets after
callbacks and retain inverse actions after partial rollback. Missing old layout
records seed the requested panel. Follow-up review found toolbar destruction
re-entry and animation mutation-before-Undo defects; these were repaired
before publication. Windows live mutation acceptance remains pending.

Build 46 passed with zero warnings/errors in 1m09.60s. Test run 24 passed
369 of 371 tests: the added dirty guard incorrectly disabled two existing
visual-only cache reuse cases. The guard now compares layout dependencies and
backend/font generations without treating full-capture markers or paint revisions
as geometry changes. The assertions were retained. Build 47 passed with zero
warnings/errors in 1m37.01s; run 25 passed all 371 tests, no skips, in 8.883s.

Animation rollback review exposed a required lower-level repair:
`Keyframe.Remove` notified the track before clearing the detached key's links
and ownership. A throwing listener could cause rollback to decrement the count
twice. Removal now establishes its complete state before notification. A
headless regression checks links, count, ownership, and enumeration after the
throw; a desktop editor regression covers rollback. Toolbar tests cover rebuild
from destruction, deactivation during binding, and reactivation during release.

Build 48 found a test compile error: the non-generic key track required an explicit
`Cast<Keyframe>()` before `ToArray()`. Build 49 passed after this repair with zero
warnings/errors in 16.09s. Run 26 passed **391 of 391 tests**, no skips, in
8.844s. The count includes the existing animation timing tests newly linked to
the headless project, as well as the new removal regression.

The final bounded review found no remaining defect in the repaired toolbar
re-entry, animation inverse ordering, dirty marking, or key removal cases. This
is source review evidence, not Windows workflow acceptance.

Final matching builds passed: `editor-ui-integration-build-22.log` records zero
warnings/errors in 40.30s; `desktop-ui-integration-build-5.log` records zero
warnings/errors in 47.19s. Source hashes showed no source changes after the
final headless build/run. All 31 local links in the five changed UI documents
resolve, and `git diff --check` passes. No shader compiler is installed here;
shader execution, desktop test execution, and Windows/GPU acceptance did not run.

## Offscreen Receipts, Sparse Text Slots, And Editor State

The next slice separates persistent GPU run identity from painter order and adds
stable text-instance slots. Review removed quadratic run matching and retirement
scans. Cohort counts change only after successful destruction, so a failed
retirement remains owned and counted.

The offscreen route captures a reserved surface and immutable sampled quad.
Review repaired four defects before validation: newer authoring could starve
completed packets, renderer replacement could reuse invalid surface content,
successful non-window consumers could report atomic failure, and a pooled fence
could incorrectly complete an earlier failed producer. Deterministic tests cover
these state contracts. This does not validate GPU presentation.

Editor changes cancel drags when their source row identity changes, restore global
selection through the play controller, and restore each Inspector's own view.
Deferred focus checks stable target/property identity and yields to user focus.
Undo retains failed replay entries on their source stack. Its regressions are in
the desktop project and require Windows execution.

Headless build 50 failed in 1m50.43s with three errors from a missing component
namespace import and one nullable offscreen diagnostic warning. The imports and
diagnostic were repaired. Text lifetime review also found queued renderer
reattachment and detached-owned-renderer replacement gaps. Those require repair
before the next matching build and test run. No result from this slice is yet a
published or platform-accepted checkpoint.

Build 51 passed with zero warnings/errors in 1m43.14s. Run 27 passed 406 of
408 tests in 17.042s, with no skips. The drag test exposed use of the delayed
`Held` input state; dragging now checks the current `Pressed` state. The popup
fixture now waits for the root canvas's accepted size. Its teardown also exposed
child destruction inside a parent world transfer. Deactivation now closes the
popup and removes input callbacks without starting a nested child destruction;
normal Hide or the next Show clears those owned children.

Text retirement now handles queued reattachment, actual deferred-destruction
veto, and externally cleared source meshes. A final guard prevents requeueing
after a destruction callback transfers the renderer back to the caller. Focused
tests cover all four cases. Build 52 validates the combined repairs.

Build 52 passed with zero warnings/errors in 1m22.37s. Run 28 passed 408 of
409 tests in 12.549s, with no skips. The remaining popup assertion exposed a
fixture contract error: stretch anchors add the explicit height to the parent
span. The fixture now uses `UICanvasTransform.SetSize`, which sets point anchors
for the requested 200-by-80 canvas. The production layout rule is unchanged.

Build 53 passed with zero warnings/errors in 14.29s. Run 29 passed 408 of
409 tests in 9.468s. The remaining assertion exposed a production scroll defect:
extent calculation ignored content below a top-anchored viewport. The repair
measures the full unscrolled extent, removes padding and accepted scroll movement,
and makes positive vertical offsets move content upward for top-down scrolling.
The popup test now verifies actual content movement and stable extent after a
wheel event. A horizontal regression verifies leftward movement and clamping.

The next headless build (`headless-ui-integration-build-54.log`) passed with
zero warnings/errors in 1m07.66s. All 410 tests passed in 8.605s
(`headless-ui-tests-30.xml`). Editor build 23 then found one Inspector target
type-inference error and three drag utility references to the wrong
`RuntimeEngine` facade. The headless compile has a different namespace closure,
so it did not expose those editor errors. The repair uses explicit runtime
services and an object-typed target projection. Matching builds must pass before
publication.

After the facade and projection repairs, headless build 55 passed with zero
warnings/errors in 16.15s. Test run 31 passed all 410 tests, with no warnings
or skips, in 8.269s. Editor build 24 passed with zero warnings/errors in 52.46s.

Desktop test-project build 6 passed with zero warnings/errors in 47.18s.
A hash comparison of 9,865 source/project/shader files confirms that no source
changed between headless build 55, test run 31, editor build 24, and desktop
build 6. The 31 local links in the five updated UI documents resolve. The diff
whitespace check passes. Windows-only desktop tests were compiled, not run.

## Paragraph Analysis Decision

The installed Skia/HarfBuzz adapter shapes a caller-declared run. It does not
supply paragraph bidi analysis or line-break opportunities. The
[HarfBuzz contract](https://harfbuzz.github.io/what-harfbuzz-doesnt-do.html)
requires the caller to supply those results. The complete shaping/cache item
therefore remains open.

A Windows-first option is an audited values-only adapter to
[DirectWrite text analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwritetextanalyzer).
It uses a Windows system API without a package installation. It still adds a
platform boundary and requires Windows runtime validation. System font fallback
has a separate capability requirement: [MapCharacters](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_2/nf-dwrite_2-idwritefontfallback-mapcharacters)
requires Windows 8.1 or later. The existing Windows target alone does not prove
that capability. Authored fallback faces need immutable worker snapshots and
atlas ownership regardless of the analysis backend.

A portable analyzer would require a separate dependency and packaging review.
The owner choice is pending. No paragraph analyzer or new dependency has been
added. Presented-caret IME placement and the existing single-run path can
advance independently.

## Presented Controls And Media Lifetime

Headless build 56 passed with zero warnings/errors in 2m58.12s. Run 32 passed
all 424 tests, with no warnings or skips, in 8.212s. Source hashes confirmed the
frozen input, control, material-prototype, media, and world-capture snapshot.
This was an intermediate check, not a publication acceptance result.

Review found additional gaps not covered by that run:

- A queued stop followed by a blank-URL start could leave an old video session
  open. GPU upload helper destruction also needs the render owner and exclusion
  from active frame uploads. Deferred fake sessions must test these paths.
- An asynchronous scene pick must validate its pane, input, window, camera,
  world, and presentation when it completes. A tick revision alone is not
  sufficient after focus loss or popup coverage. Rectangle selection must keep
  its press owner, and right-drag camera input needs explicit capture.
- Numeric Escape and Enter must end an Alt-drag edit. Later pointer movement or
  release must not resume or commit that edit again.
- A slider or scrollbar must not combine a displayed local pointer coordinate
  with newer live geometry. The existing pointer dispatch must carry the exact
  hit record and frozen scroll metrics. Metric changes must invalidate input
  records, and a recycled or reconfigured scroll target must reject old input.

These findings remain repair work. The published checkpoint remains the prior
410-test snapshot until the repairs, matching builds, and review pass.

Headless build 57 compiled with one nullable warning and no errors in 1m39.05s.
A pointer reentry repair overlapped that build, so it is not a final matching
snapshot. The warning exposed a missing explicit local target check in scrollbar
capture. Review also found that pre-layout scroll limits can disagree with newly
arranged content. Capture must reject those metrics until the follow-up layout
settles. The next check includes these repairs and a callback-reentry regression.

Headless build 58 passed with zero warnings/errors in 1m19.74s. Run 33 passed
434 of 435 tests in 8.623s. The scrollbar test failed because its requested
pointer metrics were rejected; the press-reentry, stale-limit, and media teardown
regressions passed. The fixture and accepted thumb geometry need investigation
before this snapshot can publish. No source changed during this build or run.

The thumb coordinate bases match: accepted local plus placement translation is
the applied bottom-left translation. The fixture exited on an already-positive
thumb width and paired that old position with a newer scroll offset. It now waits
for a newer accepted layout whose thumb geometry matches the requested metrics.
The production rejection stays unchanged.

Build 59 passed with zero warnings/errors in 14.28s. Run 34 still passed only
434 of 435 tests; the same scrollbar wait timed out. The timing change alone
did not resolve the failure. The next targeted run records accepted and requested
thumb geometry, scroll limits, and interaction state to isolate the remaining
mismatch. This result does not support publication yet.

Build 60 passed with zero warnings/errors in 21.12s. Targeted run 35 failed the
same test in 6.324s. Its diagnostic showed content width grow from 240 to 336
after a 96-pixel scroll, while authored width stayed 240. `SynchronizeScroll`
used the new authored offset to recover unscrolled coordinates from old applied
child geometry. The transform now retains the offset used for that geometry;
the existing post-apply loop updates that basis after applying child outputs.
The regression checks both horizontal and vertical extents. No new tree scan
is required.

Build 61 passed with zero warnings/errors in 1m28.60s. Targeted run 36 passed
the scrollbar and horizontal/vertical extent tests (2/2) in 0.796s. Review then
identified a related recovery case: an owner callback can abort layout application
after some live child geometry changes. The next capture must preserve the last
settled scroll limits until complete geometry application succeeds. Cached pages
also need a recovery check so a rejected partial apply cannot leave stale live
values when a later result reuses older accepted output.

The reviewed recovery guard preserves settled scroll extents while owner geometry
is provisional. A force-reapply flag remains set until validated publication;
only recovery bypasses the normal changed-page skips. Build 62 passed with zero
warnings/errors in 1m45.89s. Full run 37 passed all 437 tests, with no warnings or
skips, in 11.643s. This includes callback-driven apply rejection and restoration
of cached owner geometry. Matching editor and desktop compilation follow.

Editor build 25 passed with zero warnings/errors in 3m21.83s. Desktop test
build 7 passed with zero warnings/errors in 47.16s. Source hashes matched the
9,882-file build-62 snapshot after all checks. No Windows test execution or
graphics validation is claimed. The final document link and whitespace checks
passed. The completed prototype contract advances reconciliation to 38 of 60;
22 code items and four original owner decisions remain.

## Indexed Navigation, Detached Input, And Output Admission

The next review found four additional lifecycle gaps. Toggle binding callbacks
could replace the source before the control read its result. Focus-ring replacement
could leave the old visual highlighted. Direct pawn/tool destruction did not run
the deactivation-only input cleanup. Watcher creation could install a monitor
from one backend and then capture a context from a newer backend. These require
explicit owner identities and checks at callback boundaries.

Headless build 63 stopped with zero warnings and one error in 1m11.60s.
`AssetChangeMonitor` used a diagnostic type in the wrong namespace (CS0234).
No tests ran for that snapshot. The next build includes the namespace repair
and the reviewed lifecycle fixes.

The desktop swap seam now separates native render dispatch from presentation.
A required output can veto the swap. Native indexed intent checks compare mesh,
geometry revision, topology, index type/count/offset, and instances. The direct
world proxy stays inactive until selected-pass manifests, packet retention,
terminal output proof, and persistent-target recovery are complete. The new
seams alone do not close world publication or establish GPU acceptance.

The focused Runtime.Core watcher build passed with zero warnings/errors in
21.92s after the namespace and installation/disposal repairs. This check did
not execute tests or compile the in-progress web and editor changes.

Headless build 64 stopped with zero warnings and three errors in 1m14.95s.
Two errors came from a missing web partial-file namespace import; the third was
a local-name collision in the indexed draw scope. The integration repair adds
the import and gives the direct-draw local a distinct name. No tests ran.

Headless build 65 passed with zero warnings/errors in 1m28.26s. Full run 38
passed 474 of 475 tests, with zero warnings or skips, in 9.732s. The remaining
focus-ring test found that button deactivation kept the ring highlighted. The
web owner-failure review also found that a valid old frame could hide the public
failure diagnostic. Both fixes precede the next full build and test run. The
9,904 source/project file hashes matched the build and test snapshot.

Headless build 66 compiled with one nullable warning and zero errors in
1m36.32s. The new web diagnostic assertion did not first assert a non-null
failure. The assertion now checks both contracts. No tests ran on build 66.

Headless build 67 passed with zero warnings/errors in 18.81s. Full run 39
passed all 476 tests, with zero warnings or skips, in 9.807s. Review then found
that backend initialization, resize, or navigation could replace the backend
and throw before the failure handler checked its generation. That could place
a retired backend's diagnostic on its replacement. The next repair checks the
source identity around failure publication and callback reentry.

Headless build 68 passed with zero warnings/errors in 2m58.96s. The final
web repair rejects retired failures before state publication, clears current
diagnostics when the backend changes, and retires an unused software fallback
created by a reentrant factory. It does not enable fallback by default.

Full run 40 passed all 478 tests with zero warnings or skips in 10.154s
on 2026-10-24. The matching editor and desktop test compilations follow.

Editor build 26 passed with zero warnings/errors in 2m32.24s. This is managed
Windows-target compilation on Linux. It does not establish editor execution,
Windows input, native WGL context behavior, or GPU acceptance.

Desktop test build 8 passed with zero warnings/errors in 56.13s. All three
builds and full run 40 match the same 9,905 source/project file hashes. The
32 local documentation links and whitespace check passed. The common-controls
code contract advances reconciliation to 39 of 60 original implementation items;
21 code items and four original owner decisions remain. Desktop test execution,
Windows/IME, native graphics, visual checks, and performance acceptance remain
open. The paragraph analyzer decision remains pending.

## Layer Boundaries, Splitter Input, And Deferred Editor Work

The next source set adds explicit composition owners and immutable contiguous
painter spans. Unchanged spans retain their content identity across overlay
changes and output mapping. Sibling output variants use distinct changed-content
revisions. Unchanged membership shares the structural index. Changed publication
comparisons still visit the record set; this is not sparse capture or native
command-cache acceptance. Readers must retain the parent publication while using
segment resources.

A shared splitter uses accepted divider metrics from the layout kernel, before
child constraints can alter pane geometry. The dock host uses that control.
Hierarchy edits carry the panel's world through queued work and Undo. Streaming
volumes reject delayed attachment after direct destruction and retain the exact
host for detach.

Watcher imports carry an opaque authoring ticket into queued work. Named options
and generated-root commits use its captured filesystem provider and roots. Review
found that stale-context cleanup deleted recovery backups; the repair retains
those bytes and reports the location. A second repair routes both prefab and
non-prefab root publication through captured-backend admission. Closure writes
and already-running import callbacks still need shared reservations. This is not
complete crash recovery.

The world-pass tracker freezes a closed conditional graph and exact packet claim.
It remains inactive. It currently rejects Switch and other unadapted command
shapes; the default pipeline therefore cannot activate it yet. Missing required
work differs from a proven-empty pass. Native completion, shared-resource taint
recovery, and final output admission remain separate work.


Headless build 69 failed with one missing `UIElementIdentity` namespace import in
`UISplitterPointerMetrics`. No tests ran. The import is repaired. Additional
regressions now cover provider reentry during options move/delete and retained
backup bytes after stale-context disposal. The backup test is in the desktop
project and needs Windows execution. Headless build 70 validates this source set.


Headless build 70 compiled runtime code, then failed test compilation with one
missing `UICanvasComponent` import and four nullable diagnostic assertions.
The repairs add the exact import and explicit non-null assertions. Build 71
passed with zero warnings/errors in 15.38s. Full run 41 follows.


Full run 41 passed all 508 tests with zero warnings or skips in 9.294s on
2026-10-24. Coverage includes composition span reuse and painter boundaries,
presented splitter metrics, conditional-pass accounting, source-context provider
reentry, and streaming attachment after destruction. Editor build 27 and desktop
test build 9 will validate the matching source.


Editor build 27 failed with one definite-assignment error in the dock splitter
callback. The callback was installed before its handle transform was assigned.
The repair moves callback installation after handle construction. No desktop test
build ran on that source set.


Editor build 28 passed with zero warnings/errors in 46.41s. The final dock-host
callback installs after its handle exists. This editor-only repair does not
change headless runtime or test inputs. Desktop test build 9 follows.


Desktop test build 9 passed with zero warnings/errors in 1m29.60s. Desktop tests
did not execute. Final graph review found that command identity checks did not
compare exact container counts. An append to an empty root or pruned empty branch
could evade structure validation. Publication waits for that repair and its
regressions. The direct-world route remains inactive.


The manifest now validates exact root, containing-container, and branch child
counts, including empty and pruned children. Headless build 72 passed with zero
warnings/errors in 1m29.43s. Full run 42 validates the repaired graph contract.


Full run 42 passed all 510 tests with zero warnings or skips in 9.129s. Editor
build 29 checks the matching final source before publication.


Editor build 29 passed with zero warnings/errors in 2m17.23s. Desktop test build
10 checks the same repaired source. These are managed Windows-target builds on
Linux; no Windows execution or graphics acceptance is inferred.


Desktop test build 10 passed with zero warnings/errors in 45.06s. Headless build
72, full run 42, editor build 29, and desktop test build 10 match the same 9,924
source/project file hashes. All 32 local document links and the whitespace check
pass. Reconciliation stays at 39 of 60 implementation items, with 21 code items
and four original owner decisions open. Paragraph analysis remains pending.


## Static Chrome Admission And Terminal Callbacks

The next increment adds draw-local Vulkan static chrome contracts, Switch graph
bookkeeping, hidden-inspector snapshots, and control/audio teardown. Static UI
uses singleton packets and current output receipts. Independent review found a
cache-policy exception that also bypassed resource/debug/output restrictions.
The fix permits only the dynamic UI/text bits and adds mixed-flag regressions.
Audio review found that a release callback could reactivate a tick before the
outer deactivation cleared it. The repair must preserve that new activation.
The dock host now assigns the inspector's exact window owner.

Headless build 73 compiled the runtime projects, then failed on one new test
assertion: the expected reuse counter literal had the wrong integer type. The
literal now matches the unsigned 64-bit counter. The build had zero warnings;
no tests ran. Declared headless restore 4 passed before this build after linking
the existing audio integration project. No package version changed.


Headless build 74 passed with zero warnings/errors in 18.91s. Full run 43 passed
all 527 tests with zero warnings or skips in 9.143s. The audio regression now
checks the update tick after callback reactivation and its removal on a later
normal stop. Editor build 30 checks the frozen source, including the cache-policy
repair and exact inspector window assignment.


Editor build 30 compiled Vulkan and Bootstrap, then failed on a LINQ type
inference error in inspector restoration. The distinct node sequence now names
its `SceneNode` element type. This editor-only repair does not change the
headless runtime/test inputs. Editor build 31 checks the repaired source.


Editor build 31 passed with zero warnings/errors in 44.99s. The sole source
change after headless build 74/run 43 is the editor-only `Distinct<SceneNode>`
repair, outside the headless project inputs. Desktop test build 11 checks the
final source. Native GPU cache execution and Windows editor workflows remain
unverified.


Desktop test build 11 failed on two extra closing parentheses in the new Vulkan
primitive assertions (eight syntax diagnostics, zero warnings). The assertions
are repaired. Desktop test build 12 checks that test-only correction.


Desktop test build 12 passed with zero warnings/errors in 42.47s. Final inspector
review found that a mixed node/component target list could enter a component-only
cast. Restoration now rebuilds the component drill-down owner list only when all
restored targets are components, and clears old owners otherwise. Editor build
32 and desktop test build 13 check this final editor-only guard.


Editor build 32 passed with zero warnings/errors in 43.22s. Before desktop build
13, control review found that popup replacement could destroy its control during
dismissal and then let the outer setter install a new owner. The final source
will include post-callback ownership guards and regressions. Headless build 75
and a new full run will validate that runtime repair before final editor checks.


Headless build 75 passed with zero warnings/errors in 1m28.85s. Full run 44 passed
all 531 tests with zero warnings or skips in 9.286s. Final path review found that
normal solid UI uses `TransparentForward`, outside the initial cache pass gate.
The Vulkan gate now admits only the four supported raster passes and keeps exact
pass identity in existing cache keys. Background and pre/post passes remain
dynamic. The changed Vulkan source and desktop fixture are outside the headless
inputs. Editor build 33 and desktop test build 13 check the final source.


Editor build 33 passed with zero warnings/errors in 2m12.98s. Final source hashes
remain unchanged across the 9,936-file snapshot. Desktop test build 13 follows.


Desktop test build 13 passed with zero warnings/errors in 45.41s. No desktop
tests executed. Final source hashes remain unchanged, all 32 local document links
resolve, and the whitespace check passes. Reconciliation remains 39 of 60 code
items; partial cache admission and lifecycle work do not close the broader items.


## Canvas Membership And Scene Camera Ownership

Screen, inactive, hidden, and offscreen UI now suppress their 3D registration.
Canvas mode, parent, visibility, activation, and component replacement events
restore valid direct-world membership. Authored visibility and shadow flags stay
intact. This does not activate frozen world publication. Composition owners now
separate gallery chrome, FPS overlays, dock controls, and virtual text rows.

Scene panels resolve cameras only from their exact window and runtime world.
Binding checks reject callback replacement. Mandatory viewport detachment clears
camera, world, and window owners before notifications. Review found that ordinary
property setters and camera-list removal can be vetoed; the cleanup path must
therefore retire ownership independently of those vetoes.

Headless build 76 failed on a duplicate transform-change override. The merged
override preserves the existing bounds and batch work. Build 77 passed with zero
warnings/errors in 1m34.58s; no tests ran on that snapshot. Build 78 failed with
zero warnings on the missing `partial` modifier for `XRViewport` in 24.80s. The
modifier is corrected. These failed builds are not test evidence.

The shared backdrop audit found no active retained blur panel. The existing GL
texture grab copies during texture binding. Retained texture snapshots reject
that mutable source. A shared capture needs an explicit producer lease, an exact
ordered composition boundary, and output/viewport/backend identity. Same-stream
sampling must not mark the surface GPU-complete before its fence signals. No
blur implementation or renderer default changed in this increment.

Headless build 79 passed with zero warnings/errors in 2m17.07s. Run 45 executed
541 tests: 533 passed and eight errored, with no warnings or skips. Seven viewport
fixture setup errors came from the unprovided `BvhRaycastCore` shader snippet.
The fixture now expands the actual repository snippet without changing host file
access policy or global snippet registration. The nested-canvas transform
replacement regression also failed and remains under repair before publication.

The nested failure came from transform handoff: children move while the new
canvas transform still has no `SceneNode`. Listening only for draw-space changes
missed its later owner attachment. The child now observes that attachment,
restores both render-info canvas links, and rebinds its retained batch slot. The
regression checks the exact current primitive handle after both screen and world
transform replacements. Headless build 80 validates this repair and the shader
fixture repair.

Headless build 80 passed with zero warnings/errors in 1m20.53s. Run 46 passed
534 of 541 tests in 9.129s, including all canvas membership tests. The remaining
seven setup errors came from automatic render-pipeline creation in the new
viewport fixture. These tests need real camera/viewport ownership, but no render
pipeline or graphics device. They now disable automatic pipeline adoption on
those test viewports. Headless build 81 checks this fixture correction.

Headless build 81 passed with zero warnings/errors in 15.96s. Full run 47 passed
all 541 tests with zero warnings or skips in 9.028s. The exact 3D membership item
is complete, including nested transform replacement and retained batch identity.
Reconciliation is now 40 of 60 code items. Editor build 34 checks the same source.

Editor build 34 passed with zero warnings/errors in 2m44.39s. The 9,941-file
source snapshot remains unchanged. Desktop test compilation 14 follows; no
Windows test execution is available in this environment.

Desktop test build 14 passed with zero warnings/errors in 50.22s. No desktop
tests executed. All 32 local document links resolve, the whitespace check passes,
and all 9,941 source/project hashes remain unchanged.

## Native Asset Creation Recovery Boundary

The native asset browser still has no new asset-creation command. A separate,
unbuilt core prototype added exclusive file creation, captured authoring tickets,
retained native asset/metadata pairs, and receipt-based Undo/Redo. Review found
that raw archive files cannot reliably distinguish a normal rename from an
incomplete operation. The proposed automatic metadata protection can also vanish
when a project unloads. Automatic metadata writes can overwrite a concurrent
foreign sidecar after a successful precheck.

The prototype is preserved as an unpublished patch and exact source copies in
the task evidence. Its source was removed from the integration tree before the
next validation. No creation UI was exposed. A durable recovery ownership
protocol needs a separate concrete design decision. This remains open code work;
exclusive creation or passing ordinary moves alone would not complete it.

The [recovery proposal](../../design/ui/native-asset-creation-recovery-design.md)
defines a project-local journal, immutable backups, bounded capacity, startup
gating, and explicit cleanup. Owner approval remains pending. The archived patch
is 70,710 bytes; its manifest records the base commit and per-file hashes. The
copies match those hashes and `git apply --check` passed before archiving.

## Backdrop Capture And Direct Panel Teardown

Headless build 82 passed with zero warnings/errors in 2m55.43s. Run 48 passed
556 of 557 tests in 15.045s, with no warnings or skips. The only error came from
the backdrop publication fixture waiting for a new layout result after a
visual-only property change. A valid cached layout does not submit a new job.
The fixture must accept that state rather than force layout invalidation.

OpenGL build 1 failed on three missing `BoundingRectangle` references and one
duplicate import warning. Build 2 failed on four absent `BackLeft` enum members
and four obsolete enum warnings. The code now uses the core framebuffer
attachment query names and the native `GLEnum.BackLeft` value. OpenGL build 3
passed with zero warnings/errors in 17.89s. These builds do not validate native
GL execution or compile the new fragment shader. `glslangValidator` is not
installed in this environment.

Review required exact native output proof, local consumer failure isolation,
consumer-set retention across unbind/dispose, owner-affine pool polling, and
known-idle claim retirement before receipt removal. The implementation contains
those guards. A final performance review also found repeated render-record
scans when blur was disabled; immutable publication metadata removes that work.
The next headless run checks the corrected fixture and added lifetime cases.

The hierarchy review keeps its broad completion item open. Incremental
remove/reinsert can reset provider selection while the selection mirror keeps
the old ID. Source listeners currently cover child collections, not all external
name/active-state edits. A world without scene sections can also reuse a stale
root snapshot. These are code gaps, not Windows-only acceptance checks.

Direct-world publication remains inactive. Production does not call the attempt
reservation, capture, packet binding, or packet-set sealing APIs. Reservation
must precede light/shadow and scene collection and survive the collect/render
swap. Independent packet acquisition during census and collection is not a
coherent generation. Repeated view/cascade identities, grouped native receipts,
partial-output recovery, and exact executed camera mapping remain necessary.

Headless build 83 passed with zero warnings/errors in 1m24.06s. Full run 49
passed all 567 tests with zero warnings or skips in 10.446s. This includes the
fixed visual-only capture fixture, publication presence flags, collector
consumer-set checks, copy-only writer retention, terminal list ownership,
stable hierarchy selection, drop cleanup, and toggle binding/tick retirement.
The frozen snapshot contains 10,394 source, project, and shader files. Editor
build 35 checks the same snapshot. No native shader or GPU result is claimed.

Editor build 35 failed in 2m37.62s with one error and one nullable warning.
`UIContextMenuComponent` exposes `Transform`, not `BoundableTransform`; teardown
now captures and type-checks that transform before destroying the menu. Scene
input now captures a non-null world once for both camera validation and pointer
ownership. Both corrections are editor-only and outside the headless project.
Editor build 36 validates them. The headless snapshot remains recorded separately
from the final source snapshot; only these two files differ.

Editor build 36 passed with zero warnings/errors in 1m00.76s. The final source
snapshot remains unchanged. Desktop test compilation 15 follows; desktop test
execution and the native graphics checks remain unavailable here.

Desktop test compilation 15 passed with zero warnings/errors in 57.51s. No
desktop tests executed. All 37 local links in the changed documents resolve,
the whitespace check passes, and the 10,394-file final snapshot is unchanged.

## Tree Navigation And Collect-Generation Ownership

The next integration adds shared stable-ID tree navigation and exact pending
focus ownership. A request must survive scrolling, provider replacement, and
modal changes without selecting a different incarnation of the same ID. Real
text input, caret, selection, hover, and scrolling fixtures now check retained
chrome composition. Their results are pending compilation and execution.

Radio, slider, color, text, and canvas-input teardown require direct-destruction
paths in addition to deactivation. The review checks exact stored owners,
public property notifications, stale binding delivery, and callback reactivation.
Stopping at the first callback exception would leave later subscriptions live.

The inactive world route now has a bounded collect-generation packet-set change
under review. Review found early reusable-slot publication, borrowed collection
reads, close/swap races, and missing source-world identity. The repairs retain a
generation-checked set for each traversal, defer reuse until retirement finishes,
and share the lifecycle gate for transfer and teardown. A production traversal
regression clears the source pointer during culling and checks that the retained
selection remains valid. This is not world output activation: per-output native
receipts, target recovery, frozen camera mapping, and shadow policy stay open.

Headless build 84 failed in 2m34.94s with one CS0136 error and zero warnings.
The radio group used `revision` in conflicting local scopes. The repair names
the selection revision separately. No tests ran against that failed build.

Further review found two tree navigation ownership gaps. A callback could
replace the exact list/provider while retaining equal numeric lease values.
Also, a cancelled keyboard-focus request could leave a panel item ID that later
changed a pointer selection. The repairs compare captured owners after callbacks
and expose the exact accepted virtual-focus dispatch to the hierarchy panel.

Headless build 85 compiled the runtime projects, then failed test compilation
in 1m25.49s. Ten errors came from missing `XREngine.Components` imports in
two lifetime fixtures. One nullable warning came from a sibling text value.
The fixtures now import that namespace and retain the nullable value correctly.

Headless build 86 passed with zero warnings/errors in 15.61s. Full run 50
passed 605 of 615 tests, with ten failures and zero warnings/skips, in 15.347s.
Failures covered two composition fixtures, three input-host lifetime fixtures,
one color-binding fixture, one text-binding replacement, two virtual-list
lookup-count assertions, and one tree lease-collision fixture. The new world
collection ownership tests passed. This result does not establish acceptance;
the failing behaviors and fixture assumptions remain under correction.

The text failure was a production defect: a global write guard suppressed a
new binding's initial value during an old binding's write. The guard now records
the exact writing source and restores nested write ownership in `finally`.
Fixture repairs isolate input-owned callbacks, construct a true equal-lease
collision, allow bounded constant-time identity lookups, and wait for a current
layout/publication instead of requiring a new worker completion.

Build 87 passed with zero warnings/errors in 1m10.25s. A composition fixture
changed during that build after its initial freeze. Build 88 repeats compilation
against the final frozen snapshot before the next test run; build 87 alone is
not used as evidence for that snapshot.

Build 88 passed with zero warnings/errors in 10.75s. Full run 51 passed
613 of 615 tests in 17.171s, with no warnings/skips. Only the two new real-control
composition fixtures failed. Diagnostic builds 89–92 passed; focused runs 52–55
reproduced both failures. The chrome node remained registered, but its accepted
layout output had zero size and false visibility after a caret child was added.

The output builder drops prior pages when the node count changes. The kernel
still used prior cache skips, leaving unchanged nodes unwritten. `RunPasses` now
clears both prior snapshot/output inputs for that case. A direct insertion and
removal regression covers the lost sibling. Same-size topology keeps identity
and revision validation. A separate source review confirmed this bounded fix.

Build 93 passed with zero warnings/errors in 1m17.63s. Focused run 56 passed
37 of 39 tests; all kernel checks passed and chrome remained present. The two
fixture actions still needed real visible changes: collapse the caret before
selecting text, and paint recycled rows differently by stable item identity.
Build 94 passed; run 57 passed 38 of 39. Final build 95 passed with zero warnings
and errors; focused run 58 passed 39 of 39, with no skips. Temporary diagnostics
are removed. The final full-suite snapshot contains 10,415 source/project/shader
files. No further source changes are allowed during final validation.

Full run 59 passed 616 of 616 tests with zero warnings/skips in 11.988s. Final
headless build 95 passed in 18.25s with zero warnings/errors. The source snapshot
contains 10,415 files. Editor build 37 follows. The hierarchy also gained exact
editor-scene fallback ownership and detached Undo membership checks; its desktop
workflow tests have not executed here. Hidden-scene and dual-membership action
parity remain open, as do broader inspector collection mutations and teardown.

Editor build 37 passed with zero warnings/errors in 3m03.39s. Desktop test
compilation 16 follows. No Windows execution or visual acceptance is implied.

Desktop compilation 16 failed in 59.45s with one CS9174 error and zero warnings.
The hierarchy workflow fixture used a collection expression for a
`ReadOnlyMemory<UIElementIdentity>` argument. An explicit array fixes the argument.
Only `NativeHierarchyWorkflowTests.cs` changed after the headless/editor snapshot;
it is outside both project inputs. The final source hash record keeps this exact
difference. Desktop compilation 17 validates the corrected fixture.

Desktop compilation 17 passed with zero warnings/errors in 44.86s. No desktop
tests executed. Headless/runtime inputs are unchanged from run 59 and editor
build 37. Source and document checks precede publication.

Final verification confirms all 10,415 source hashes and 35 local documentation
links. Whitespace checks pass. Author and committer identities match the existing
configuration. The remote branch still points to the prior published checkpoint.

## Native Window Dispatch And Close Ownership

Review of detached-window restore, resize, and close found ignored return values
from `Engine.InvokeOnMainThread`. Its default does not execute an action when the
caller already owns the render thread. The native window paths now request
immediate execution on that owner and retain queued execution from other threads.
`NativeAssetSelection.LoadAsync` has the same completion risk when its await
finishes synchronously; the repair retains selection revision and path admission.

A close receipt must identify one approved request and its shutdown intent.
A veto, backend exception, nested request, later manual close, or replacement
window cannot authorize an older queued cleanup. Bounds must be read on the
native window owner before its backend retires. The pinned Silk.NET 2.23.0 GLFW
and SDL backends call `Closing` synchronously from `Close`; the desktop adapter
cancels immediate Silk disposal until renderer cleanup completes.

The optional externally owned event pump has a separate terminal risk. A final
native close can stop the render timer before its queued editor cleanup and
renderer-disposal jobs execute. An editor callback cannot safely mutate the scene
on that foreign native thread. A read-only lifecycle trace is in progress to find
a bounded render-owner completion route. No shutdown-order change or native
runtime validation is claimed. The default collapsed-owner path does not require
that cross-thread handoff.

These changes are under review and validation. Primary saved-layout key migration
remains a proposal; it is not part of these lifecycle repairs.

Headless build 96 stopped with zero warnings and one CS0234 error in
`RuntimeHierarchyRootRoutesTests`: the fixture imported Jitter from a
Windows-targeted project absent from the portable graph. It took 2m31.30s; no
tests ran. The correction uses a test-only physics scene for the routing fixture,
without changing project platform targets or runtime physics behavior.

Headless build 97 passed with zero warnings/errors in 1m50.49s. Full run 60
passed 660 of 660 tests, with zero warnings, failures, or skips, in 11.726s.
The 10,468-file source snapshot stayed unchanged for all headless inputs during
compilation and execution. Seven editor-only files changed outside that graph.
Matching editor and desktop-test compilation remain pending at this point.

Headless build 98 passed with zero warnings/errors in 1m29.37s. Full run 61
passed 663 of 666 tests in 11.554s, with three failures and no skips. The source
snapshot contains 10,471 files; all headless inputs remained unchanged.
Two existing frozen-draw tests rejected inactive text because
`HasOnlyPublicationUniformHandler` required the owner's material handler to be
present. The new deactivation rule correctly removes that handler. Publication
admission must accept no callbacks or exactly the recognized callback, while
still rejecting unrelated handlers. The third failure was the new provider
teardown fixture reading a cell's scene node after teardown removed it; the
fixture must verify cleanup order without assuming retained child topology.

Headless build 99 passed with zero warnings/errors in 1m13.26s. Full run 62
passed 667 of 667 tests, with zero warnings, failures, or skips, in 10.551s.
The 10,472-file snapshot stayed unchanged for all headless inputs. Publication
capture again accepts inactive text without admitting unrelated callbacks.
The table teardown fixture verifies callback order and one cleanup while the
cell is still valid. Matching editor build 38 is the next check.

The final read-only review cleared the list receipt, shared row callback gate,
material ownership, and hierarchy replay changes for compilation. List mutation
is limited to supported BCL lists with non-overridable compiler-generated source
accessors. Hierarchy Undo restores placement before active state. Its exact
property reconciliation preserves unrelated steps. Setter or matrix-callback
failure retains the source history and an uncertain completion state; retries
cannot infer completed lifecycle work from field equality alone. This failure
requires explicit recovery or reissue. No general runtime event-order rewrite
or global Undo recording suppression was introduced.

Editor build 38 stopped after 2m18.24s with seven errors and six nullable
warnings. New inspector code used the wrong `XRWorldObjectBase` namespace,
reference-comparer inference widened typed collections to `object`, and a detail
context retirement callback lacked its method. Legacy dock setup captured its
registration before definite assignment. These are editor-only integration
repairs; the headless runtime snapshot is unchanged. No warning suppression or
platform exclusion is part of the correction.

Editor build 39 passed with zero warnings/errors in 40.18s after the namespace,
typed-comparer, retirement-method, and definite-assignment corrections. A final
read-only retirement review found an in-flight callback case: old-provider
unbind can retire a detail context while its outer Rebuild or Bind call is
still running. Entry-only guards are insufficient. The follow-up requires
captured-generation checks after callback boundaries and cleanup of incomplete
new controls before editor build 40. Runtime/headless inputs remain unchanged.

Editor build 40 passed with zero warnings/errors in 53.36s after the detail
retirement guard repair. Its 10,473-file source snapshot is unchanged. Desktop
test compilation 18 is next. The broad implementation count stays 42 of 60:
non-IList inspector builders still need callback-terminal admission, and the
other text, world publication, and editor parity items remain open. Narrow
completed subparts do not close those broader original items.

Desktop test compilation 18 stopped with one CS0051 error and zero warnings in
24.98s: a public test method exposed an internal enum parameter. The two test
cases now select Set/Remove with a Boolean parameter and retain the same test
body. Desktop compilation 19 passed with zero warnings/errors in 51.78s. This
fixture-only change does not affect production or headless inputs. No desktop
execution, Windows/GPU acceptance, or performance result is claimed.

## Displayed input and accepted text measurement

Published checkpoint `21902a31b514a0b442b9b153eae53b76bad0d3a3` preserves
native editor mutation and window ownership. The remote SHA was verified. A
read-only check found no attached check runs, Actions runs, or commit statuses.

The next input audit found that a contact refresh could replace the hit dictionary
between collection and hover dispatch. Focus callbacks could also replace packet A
with B while the outer refresh still returned A. Exact read admission and dispatch
reentry guards now reject these stale paths. Camera blocking previously used live
bounds after a displayed hit; it now uses accepted pointer geometry. Hierarchy
menu placement uses the same input contract.

Headless build 100 stopped with two CS0246 errors in new test imports and zero
warnings, after 2m36.51s. The environment transport briefly disconnected during
this build. After reconnection, the original process was still running; no second
build was started. The missing imports were corrected. Build 101 passed with zero
warnings/errors in 16.10s. Full run 63 passed 676 of 677 tests in 10.735s. Its one
failure expected the old tab ID after a visual slot was rebound. The actual move
used the current ID, as required. The assertion was corrected without changing
production code. Build 102 passed with zero warnings/errors in 17.83s; full run 64
passed 677 of 677 tests with no warnings, failures, or skips in 10.126s.

Further review added exact packet-return, collection reentry, and menu-point
regressions. Accepted text measurement now has a post-commit observer seam.
Review removed an invocation-list allocation, prevented nested layout pumping,
and replaced global canvas-dirty rejection with exact committed text-page checks.
This prevents one inspector resize from suppressing another valid measurement.
These additions require the next compilation and test run; run 64 does not cover
them. Editor menu and collection lifecycle tests also require compilation.

Build 103 passed with zero warnings/errors in 1m17.63s. Full run 65 passed
681 of 682 tests in 16.022s. The accepted-text notification fixture used an
inactive root; a later text edit did not schedule another canvas job, so its
rejection callback never ran. The fixture now explicitly invalidates measure
before requesting that job, including inside its rejection callback. The test
still requires a real rejected commit and later accepted result.

The same review found that the accepted-result predicate checked unused OpenType
features when shaping was off, although the input cache ignored that setting.
The predicate now follows the shaping policy, and shape-only settings do not
replace unshaped output. A regression requires reuse of the exact accepted
result. The bounded source review cleared both corrections before build 104.

Build 104 passed with zero warnings/errors in 1m10.59s. Full run 66 passed
682 of 682 tests, with no warnings, failures, or skips, in 10.069s. The frozen
snapshot contains 10,488 source files. Editor build 41 then succeeded with one
CS8604 warning and zero errors in 2m43.27s. Dictionary removal had already checked
`key is not null` when it computed a Boolean, but that Boolean does not carry
nullable flow proof into the removal branch. The branch now states the same
non-null condition explicitly. This editor-only correction does not change the
headless inputs. Editor build 42 verifies the warning repair.

Editor build 42 passed with zero warnings/errors in 40.99s. Desktop compilation
20 succeeded with one CS8604 warning and zero errors in 49.04s. A nullable
callback assertion selected NUnit's delegate-execution overload. The assertion
now checks the Boolean null condition explicitly. Desktop compilation 21 passed
with zero warnings/errors in 45.76s. This desktop-only test change leaves the
validated production and headless inputs unchanged. Desktop tests did not run.


## Draft replacement and exact collection replay

Checkpoint `3130bfc66b246659f357e10994fb314655e5605b` was published and the
remote SHA was verified. No check runs, Actions runs, or commit statuses were
attached. The next controls audit found that text validator/getter callbacks
could replace a binding before an old draft committed or cancelled. Exact draft
owner checks now reject that continuation. Tab updates also needed bounded
reentrant requests and a focus-intent token to avoid restoring obsolete focus.

Headless build 105 stopped with five syntax errors and zero warnings in
1m06.90s. The new world packet source used an object initializer after a factory
method call. The corrected setup uses a one-time source configuration method
before world registration. This preparation preserves legacy child collection;
it does not complete direct-world render or input publication.

Linked-list review removed staging enumeration invalidation, separated Undo
owner proofs from view leases, checked displayed binding values against frozen
node values, and guaranteed history/dirty finalization after a failed UI
notification. Dictionary direct writes and generic collection Clear/Add still
need safe canonical adapters. The new portable nested-clip tests cover frozen
records and painter order, not native surface allocation or presentation.

Build 106 compiled production projects, then stopped with six new test compile
errors and zero warnings in 2m13.05s. Three fixtures lacked the canvas component
namespace, and two menu test statements used an unavailable transform property.
The fixtures now import the existing component namespace and use `UITransform`.
No production change was needed for these errors. The reviewed construction
support still needs an exact active-object guard against foreign-thread identity
changes before publication; test compilation alone does not close that finding.

Build 107 passed with zero warnings/errors in 16.84s. Full run 67 passed
712 of 715 tests, with zero warnings or skips, in 12.404s. Two new nested-clip
fixtures failed their initial render-record admission assertion. The multiline
fixture moved only its caret after focus selected the full text; Enter correctly
replaced the selection. The fixture now collapses its selection before inserting
a newline. No production change was needed for that failure. The new tab,
combo/menu, draft-owner, and 13 construction cases all passed in this run.

Build 108 compiled only the headless test project against the already built 107
dependencies (`BuildProjectReferences=false`), with zero warnings/errors in
8.20s. Diagnostic run 68 passed three of five publication tests in 0.882s. The
two failures identified the same fallback shader: its `NormalEncoding` file
snippet was unavailable in the headless host. The fixture now uses a supported
self-contained custom fragment material and keeps its frozen fallback, crop,
and painter-order assertions. This check does not validate later Data edits.

Build 109 compiled production code, then stopped with one ambiguous constructor
call in the repaired fixture and zero warnings in 2m07.14s. An explicit empty
`ShaderVar` array resolved that overload. Build 110 passed with zero warnings
and errors in 16.04s. Full run 69 passed 726 of 728 tests with no warnings or skips
in 10.245s. All 23 owned-construction cases and the new text target-identity cases
passed. The two remaining clipping fixtures read deferred transform matrices.
They now call the normal immediate hierarchy recalculation after applying layout
and after moving the inner clip. Rendering code and assertions are unchanged.
Editor build 43 uses the same frozen production input; this last correction is
test-only.


Editor build 43 passed with zero warnings/errors in 2m37.59s. Build 111 passed
with zero warnings/errors in 15.36s. Focused publication run 70 passed five of
five tests in 0.928s. Full run 71 passed 728 of 728 tests, with no warnings,
failures, or skips, in 10.870s. The final source snapshot contains 10,519 files.
The last change after the editor build input was a headless/desktop test fixture;
production source stayed unchanged. Construction guard review cleared exact
thread/batch admission and reference-only rollback. This does not clear scene
clone adoption or recursive graph cleanup, which remain separate work.

Desktop test compilation 22 passed with zero warnings/errors in 48.02s. Its
Windows test assembly did not execute. All final checks use the frozen source;
no source changed after the successful full headless run. The three pending
proposals and four original owner decisions remain unresolved.


## Cached Diagnostics And Collection Integration (2026-10-24)

The next frozen input contains 10,556 source files. Headless build 112 passed with
zero warnings/errors in 2m26.02s. Full run 72 passed 758/758 tests in 11.663s, with
no warnings, failures, or skips. New portable tests cover Console cache identities,
bounded Profiler projection and cancellation, owned-clone adoption/publication,
world-image traversal admission, and Vulkan border contract identity. Editor-only
adapter tests compiled in desktop build 23 with zero warnings/errors in 50.98s;
they did not execute on Linux. Editor build 44 passed with zero warnings/errors
in 2m35.43s. All three builds and the headless run used identical frozen source
inputs. Build and run evidence is under `Build/_AgentValidation/20261023-080000-native-ui-completion/logs/`.

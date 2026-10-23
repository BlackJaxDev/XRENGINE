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

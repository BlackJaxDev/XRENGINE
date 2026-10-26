# Native UI Windows completion

## Source and scope

The Windows work starts at `00ac16d7a66c555aea806bb3d59d013f583e895b`.
The results below use local repair variants of that commit. They do not describe
an unchanged published commit. The original checkout and the older diagnostic
checkout remain separate. ImGui remains the default.

## Repairs and execution

The initial accumulated selection executed 385 cases: 351 passed and 34 failed.
Collection failures exposed incorrect generic grouping, cleanup that could
retire a newer binding, and vector editors whose recycled input bindings were
not restored. Nested scalar rows also created unused hidden controls.
Hierarchy rebuilds attempted structural changes during a world-transfer lease.
The repairs preserve ownership guards and defer the rebuild until that lease
ends.

The owner approved specific fixture corrections. These correct the API failure
result, physical row reuse order, callback cleanup, the newly published pane,
and the actual count-label child. The state, Undo, ownership, exception, and
node-budget checks remain.

The repaired integration selection executed 385 cases: 383 passed and two
failed, with no skipped or incomplete cases. Both failures are
`NativeDockHierarchyTransferTests.ThrowAfterScrollCommitCompensatesOnlyTheStillOwnedPartialView`
(`False` and `True`). A proposed callback-timing correction remains pending
separate fixture clearance. The failure is not suppressed.
Both cases failed at `Assert.That(threw, Is.True)`: the actual value was `False`.

## Native startup geometry

The isolated OpenGL editor reported an invalid boundable size of `(-14, 20)`.
The affected node stretched across an allocated slot. Both the value kernel and
legacy layout subtracted insets without a zero floor. The repair floors finite
computed stretched extents and content sizes at zero. Non-finite values and
invalid explicit bounds still reach the geometry guard. Failure diagnostics
identify the input and the invalid output field.

On the repaired layout variant, 47 selected layout and virtual-collection cases
and 44 inspector collection cases passed. No case was skipped or incomplete.
The desktop test build and isolated editor build passed with zero warnings and
errors. The editor reached its first rendered frame. Its startup log had no
`LayoutFailure` or invalid-geometry entry.

This is not full native-editor acceptance. The same startup threw
`AotParityViolationException` for `UIToolbarComponent` at `XRComponent.New`,
through `UIEditorComponent.RemakeChildren` and `CompleteDeferredRemake`.
The unit-world AOT guard rejects the missing registered factory. The guard
remains enabled. A first frame does not prove that editor construction finished.

## Editor factory registration

A narrow toolbar factory change moved startup to the next missing Editor
factory, `NativeDockWindowRegistration`. This confirmed an assembly-wide gap:
the existing factory generator excludes the Editor assembly.

The final repair adds an Editor-only generation mode. It scans the current
assembly and emits component and transform factories. It does not scan runtime
references, replace player defaults, or disable parity enforcement. The toolbar
keeps its normal generic construction call. The temporary explicit toolbar
factory change is not in the final source.

The isolated editor build passed with zero warnings and errors. The resulting
session reached its first frame in 17,558 ms. Its startup logs contained no
`AotParityViolationException`, `LayoutFailure`, or invalid-geometry entry.
This is startup-log evidence, not visual or performance acceptance. The session
was then stopped through its named session manager.

The final source also passed 28 existing factory, component construction,
control teardown, and hierarchy lifetime cases. Its integration selection
executed all 385 cases: 383 passed and the same two dock timing cases failed.
The groups were curve authoring 142/142, collections 44/44, scene and custody
91/93, animation transfer 100/100, and menu ownership 6/6. No case was skipped or
incomplete. The earlier 47 layout cases were run before the Editor-only factory
change; the layout source did not change afterward.

## Acceptance limits and evidence

Computer Use app access succeeded after one earlier access request timed out.
The desktop screenshot showed the Windows lock screen. No unlock action or
input acceptance was attempted. Visual, native input, IME, GPU correctness, and
performance acceptance remain open. The named editor session was stopped after
log collection.

Evidence is under
`Build/_AgentValidation/20261025-234000-asset-teardown/scratch/completion-00ac16d7/`.
`scratch/integration-03` records the 385-case integration run.
`scratch/layout-repair-04` records source hashes, command arrays, TRX files,
47/47 and 44/44 results, the editor build log, runtime logs, and stop receipt.
`scratch/startup-repair-05` preserves the intermediate factory failure.
`scratch/startup-repair-06` records the final factory repair, builds, runtime
logs, stop receipt, and focused factory/lifetime execution.
The test harness retains the 30-second inactivity bound and 180-second session
cap. Earlier variants and baseline reports remain intact.

The DirectWrite adapter and asset recovery policy are not implemented. Denied
edits were not retried. Incomplete asset work was preserved outside active
compilation. Saved-window migration and the other open implementation contracts
also remain open. No checklist completion follows from these partial results.

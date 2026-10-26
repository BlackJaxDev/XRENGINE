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

## Unlocked desktop: layout and font failures

The owner unlocked the desktop on 2026-10-26. A live run of `418767e6` showed
the scene but no native panels. The canvas had no accepted layout. A bounded
diagnostic located the first text mismatch at identity 53, beneath collapsed
menu-list identity 50. Measurement skipped that subtree, while arrangement
created text results for its descendants. The convergence check compared those
results with unmeasured raw sizes. The repair excludes collapsed ancestry from
the convergence check and rejects prior collapsed measurements when a subtree
opens. Hidden nodes still participate in layout. The three-pass guard remains.

The next run stalled inside owner-thread snapshot capture. The font log showed
repeated optional `seguisym.ttf` import failures with ImageMagick
`cache resources exhausted`. The cached PNG measured 21,190 by 18,850 pixels,
or about 1.60 GB as RGBA8. Failed direct imports were removed from the cache, so
each new hierarchy icon retried the import. Optional Windows font lookups now
retain failure for the process and log the existing fallback. The next live run
made one system-symbol import attempt. This does not establish successful
system-font import or correct rendering of every icon.

Once capture could finish, the 32 MiB snapshot guard rejected the editor. It
charged the same immutable 893-glyph metrics dictionary for every label,
228,608 bytes per label. The repair charges each metrics dictionary once by
reference, with copied reference counts for dirty incremental snapshots. It
retains per-node text preflight and the 32 MiB limit. A live run accepted 629
nodes with one metrics dictionary and an estimated 1,955,712 bytes. Layout
generation reached 314 without the earlier convergence or budget exception.

That run still showed a black window. Its logs showed repeated canvas sizes
such as 508 by 360 and 208 by 26. The scene viewport has UI rendering disabled
but shared the camera overlay and resized it. The sizing repair excludes
UI-disabled viewports and refreshes size when UI rendering is enabled.

A repository-supported RenderDoc trigger returned no response or capture.
The process exited with `0xC0000005` at `GlfwContext.SwapBuffers`. The trigger
outcome is uncertain; it was not retried. The earlier black window preceded
that request. RenderDoc tooling is installed; its Vulkan layer check failed.
No layer registration, tool installation, or system setting change was made.

Temporary capture diagnostics were preserved and removed from active source.
Evidence for these steps is in the existing run root under `scratch/`:
`text-convergence-07`, `collapsed-layout-08`, `capture-admission-09`,
`font-retry-10`, `shared-font-budget-11`, and `viewport-size-12`.
That source passed 115 layout/font/publication cases and 12 viewport-ownership
cases, with no failed, skipped, or incomplete case. The editor and test builds
had zero warnings and errors. New focused regression-test clearance is pending.
Existing fixtures and their assertions remain unchanged.

The next live run found a false callback rejection. Activation recreates the
viewport mesh, removing the constructor's uniform bridge. Publication required
that bridge even when its public event was empty. Capture now accepts an absent
handler or the exact built-in bridge. Authored callbacks and render preparation
remain rejected. The next run reached publication generation 118 with no capture
failure. The canvas still measured 508 by 96: the camera's separate viewport
resize listener also resized the shared overlay. That listener now checks UI
rendering and the selected overlay. The next run kept a 1920 by 1080 canvas and
reached publication generation 88.

Rendering then failed when the viewport recycled its initial half-float CPU
placeholder. `Mipmap2D.Resize` attempted an unsupported image resample. The pool
now drops those pixels only when the surface is unpublished and has no external
lease, before resizing it as a GPU target. The viewport also sets its internal
extent explicitly; it previously stayed at 1 by 1 with automatic sizing disabled.
The surface repair passed editor and test compilation with zero warnings and
errors. Runtime diagnostics reported matching 1320 by 746 display, internal,
and texture sizes. A saved framebuffer screenshot showed the native panels.
The scene area was black, and the enabled control gallery overlapped editor
content. This is not visual or input acceptance.

The final focused selection executed 57 cases: 54 passed and three failed.
Camera ownership, camera initialization, mipmaps, canvas initialization, canvas
publication, painter capture, and viewport binding all passed. The three failures
were in `UIDrawPayloadTests`: `SourceObserversNotifyChangesAndDetachAfterReplacementAndDispose`
and `PixelInvalidationCreatesNewTextureSnapshotAndPreservesOldPixels` observed
zero notifications; `DisabledCommandEligibilityStillInvalidatesItsCanvas`
observed revision 12 instead of a later revision. The fixture alone reproduced
the same failures, with 15 of 18 passing. A separate existing
`UIMediaTeardownTests.BlankUrlClosesPendingSessionAndRejectsDelayedOpenFailure`
failed its initial `FrameTiming.IsAppThread` assertion: false instead of true.
These results support a Windows test-host timing issue because the notification
paths queue work off the app thread. No fixture or dispatch guard was changed.
There was no skipped or incomplete case. This selection does not replace the
earlier 127-case result, and no unchanged full suite was repeated.

The first surface sample reported 55 completed frames and 2,763 incomplete-target
skips. A separate diagnostic run later showed a complete target with 242 completed
frames and 2,475 earlier skips. The first failing backend status was not captured;
the evidence does not establish a permanent framebuffer failure. A bounded
bound-versus-named status probe was preserved, then removed with matching original
and restored file hashes. No completeness guard was weakened. The diagnostic run
is separate from the clean-source tests. Its evidence is in `fbo-status-16`.

Windows Security displayed a network-access prompt over the editor. The UI tool
rejected a Cancel click before dispatch because the dialog belongs to a separate
unavailable window. No network permission was granted. The framebuffer request
timed out after 15 seconds. No screenshot or input acceptance follows from that
request. A later capture in `viewport-surface-15` succeeded and was viewed; it
does not remove the native input blocker. The publication and size evidence above
comes from runtime properties.
The runs and stop receipts are preserved in `viewport-callback-13` and
`camera-overlay-14`; the surface repair is tracked in `viewport-surface-15`.

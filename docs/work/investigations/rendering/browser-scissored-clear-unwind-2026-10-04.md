# Browser attachment-clear scope unwind

## Observed failure

Windows CI run `37199108586` built the Editor and all three browser bundles
on commit `345d3535070763ddc5dbe00c1cf5b79371eca6fc`. RollingBall Chromium job
`111430242116` stopped during startup with
`WebGPU.Renderer.OperationUnsupported: Clear: scissored attachment clears are not admitted by the engine framebuffer profile`.
RenderingParity passed its two startups and resize on that same source.
The RollingBall artifact contained the exception message but no managed stack,
target identity, or crop rectangle. Its exact failing target therefore remains
unconfirmed until the corrected source runs.

The same run's Linux renderer checks passed depth, texture, lit, HDR, GTAO,
bloom, and debug checks, but directional shadows failed. A pending
texture allocation in `VPRC_BindOutputFBO` then tried to restore an already
disposed previous framebuffer, producing the binding-rollback aggregate error.
This independently exposes the coupled framebuffer ownership leak.

## Source diagnosis

`ResolveEngineDrawArea(validateViewport: false)` already clamps the enabled crop
to the bound attachment and elides a full-attachment result. An ordinary full
viewport crop, including one larger than the attachment, cannot cause this
rejection. A non-null result represents a genuinely partial effective crop.

The command graph has a demonstrable startup unwind defect:

1. `PushMainAttributes` pushes the invocation's initial render area.
2. `VPRC_PushViewportRenderArea` pushes another render area and enables its crop.
3. WebGPU resource preparation can throw from a later command. The atomic
   command container rethrows immediately, bypassing queued pop commands.
4. `PopMainAttributes` previously popped just one render area and no crop. The
   initial area and enabled crop survived the aborted invocation.
5. The next frame executes shadow work in `GlobalPreRender` before the viewport.
   A standalone shadow pipeline sets its full target viewport but does not
   replace an inherited crop. A leaked viewport crop smaller than that target
   therefore reaches the clear adapter as a partial attachment clear.

The same interruption leaks framebuffer bindings when a bind command has
installed its framebuffer and logical target scope, then defers in its
automatic clear. A preparation failure inside `XRFrameBuffer.BindForWriting`
already rolls back; the missing case occurs after that call returns.

## Correction and preserved contracts

Main rendering scopes now retain their entry render-area and crop depths.
Normal completion and aborted execution pop only regions added by that
invocation. Enclosing entries remain present, and collection-only scopes with
no area changes do not touch renderer state.

Initial main-area setup uses the existing state-only push, because its outer
main scope already owns restoration. This avoids taking and discarding an
additional pooled scope object for every target, viewport or external-target
invocation. The viewport and crop operations themselves are unchanged.

Before an atomic container rethrows, it walks its entered framebuffer state
commands in reverse order and executes their outstanding unbind/target-scope
cleanup. Nested containers release their own entries first. Completed or
unentered bindings have no installed state and are skipped. Restoration errors
are diagnosed while cleanup continues, preserving the original command error.
Desktop command continuation and render scheduling are unchanged.

An atomic invocation first isolates any already-installed state on shared FBO
pop commands, using reusable snapshots of the framebuffer, target scope, and
read/write selection. It restores these outer acquisitions after inner cleanup
on both normal and exceptional exits. The ordinary empty-state path adds no
snapshot storage, and restoration clears retained references. This preserves
shared-command nesting without introducing a recursion restriction. The public
next-execution flag retains its existing semantics.

The pre-existing nested default-target limitation remains: a null output FBO
changes the native target directly without pushing a framebuffer stack entry.
This repair cannot restore that untracked native target transition; it does not
extend the general framebuffer binding contract.

No scissor, attachment extent, clear value, depth/stencil operation, or custom
pipeline admission was relaxed. Partial clears still fail. Their diagnostic now
includes the target name and extent, effective top-left scissor, requested
bottom-left crop/render area, and selected color/depth/stencil aspects. These
details are formatted only on the failure path.

## Validation boundary

Source inspection confirms both leaks and the restoration ownership boundaries.
`git diff --check` passes. The repair changed no tests, workflows, dependencies,
or shader programs. The local .NET toolchain was unavailable; exact-commit
[CI run 37201737929](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37201737929)
on `805dc8f513728d4c150b5327cc4b5596ac2a2ede` compiled the portable runtime,
published all three worlds through the Windows Editor, and passed the Linux
diagnostic suite including directional shadows. RollingBall and RenderingParity
also passed their browser startup and resize checks. Advanced independently
failed because its native stages lacked the frozen draw-view scope; that
failure does not invalidate the observed scope cleanup results.
The three obsolete private-field and initial-area-expression assertions in
`VulkanP1ValidationTests`
have been replaced by direct `RenderScopeOwnershipTests` against nested main
scopes, outstanding render/crop pushes, both initial-area policies, and retained
enclosing regions. Existing extent assertions remain. The Windows job runs
these checks with the existing FBO-stack, thread-isolation and VulkanP1 checks
after preserving the browser bundles. Their execution still awaits the updated
CI commit. Run 37207710914 subsequently built and published all three worlds,
but the unit-test project failed to compile because its existing linked
RollingBall diagnostics omitted the platform-host interface and registration
sources. Both existing source files are now linked into that project. The
ownership tests still await execution; the successful browser checks are not
a substitute for their result. The atomic-container exception path is covered by the successful
integrated browser flows, without a new mock renderer unit fixture.
The corrected RollingBall run establishes successful startup and resize. It
does not identify the original failing target, because the failing artifact
predates the detailed crop diagnostic.

On `85a8b680c6fe88182282bc37589726ef69bbb0cd`,
[run 37211196939](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37211196939)
compiles the test project. Both new main-scope ownership cases and all five
render-state thread-isolation cases pass. The broader 47-test selection reports
11 passed, 35 failed and one skipped. The framebuffer behavioral test fails
before starting its worker: it expects the old read/write bindings while a
general `Bind()` is active, although that operation correctly binds the same
framebuffer for all three targets. Its expectations are corrected to verify
the general binding and subsequent restoration independently on both threads.

The other 34 failures are Vulkan desktop source-text contracts expecting older
member names, source ownership or exact statement forms. They remain reported
failures; this browser repair does not establish their desktop invariants. The
new browser workflow step now selects the three relevant behavioral ownership
fixtures rather than the entire desktop Vulkan source-contract class. Those
existing Vulkan tests are retained. The corrected framebuffer test and the
focused selection await their next exact-commit execution.

Run [37214531333](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37214531333)
on `930d1be4` passes all eight focused behavioral cases with no skips, along
with all three Windows Editor publications. The separate 34 desktop source-text
failures remain outside that result. RenderingParity passes its browser run.
RollingBall renders both sessions, but its second resize exposes an independent
pending-receipt capacity failure; it is not evidence of a framebuffer-stack
regression.

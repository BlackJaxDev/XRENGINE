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
`git diff --check` passes. No tests, workflows, dependencies, or shader programs
were changed. The local .NET toolchain was unavailable, so compilation and live
RollingBall startup/resize qualification require the exact updated CI source.
`VulkanP1ValidationTests.ExternalSwapchainPlannerDisplayExtent_IsAuthoritativeWhileInternalExtentRemainsScaled`
still asserts the removed single-pop implementation text. Those two source-text
assertions need a separate permitted test update before that suite can pass.
The source-proven leak explains a possible failing crop; it is not yet proof
that the original RollingBall rejection used the shadow target described above.

# Dedicated Render Thread Window Ownership Plan

## Goal

Move engine-owned desktop rendering off the current startup or editor thread and onto a dedicated render thread that also owns window creation, graphics context ownership, swapchain presentation, and OS event pumping for engine windows.

The target outcome is:

- the app or editor thread no longer blocks in the render loop,
- OpenGL and Vulkan still render to visible desktop windows,
- the existing `CollectVisible -> SwapBuffers -> Render` handoff remains intact,
- render-thread-only GPU rules become stricter and easier to reason about,
- editor and gameplay code can continue running while render work is saturated.

This plan is intentionally a pre-ship architectural refactor. XRENGINE does not owe backward compatibility yet, so internal API cleanup and terminology changes are allowed when they materially improve correctness.

## Decision Summary

The viable design for this repo is not "render from an arbitrary worker thread while the UI thread keeps the windows." The viable design is:

1. create engine windows on a dedicated render thread,
2. make that same thread the window thread for Silk.NET or GLFW callbacks,
3. keep OpenGL contexts and Vulkan present paths owned by that thread,
4. publish input, resize, focus, and close state from the render thread to the app or editor thread,
5. route any window or GPU-affine operation through a render-thread mailbox.

On Windows this is practical. On platforms that require window creation on the process main thread it would be much harder, but this repository is explicitly Windows-first.

## Why This Refactor Exists

Today the engine already separates update, collect-visible, and fixed-update from render, but it still treats the startup thread as the render thread. That keeps all on-screen rendering, window event pumping, and any render-thread jobs anchored to the same thread that starts the engine.

That has three costs:

- editor or application orchestration work cannot stay fully independent from render-thread stalls,
- the codebase still uses "main thread" and "render thread" almost interchangeably,
- window ownership and graphics ownership are spread across code that assumes the render thread was established before startup completed.

The repo already has enough structure to support a clean split:

- `Update`, `CollectVisible`, and `FixedUpdate` already run on separate tasks,
- render dispatch is already treated as synchronous and thread-affine,
- the OpenGL backend already has shared-context background helpers,
- OpenXR code already documents that GL context ownership constrains parallelism.

## Current Repository Audit

### Current ownership model

The current engine entry path makes the startup thread the render thread.

Relevant code:

- [XRENGINE/Engine/Engine.Lifecycle.cs](../../../XRENGINE/Engine/Engine.Lifecycle.cs)
- [XRENGINE/Core/Time/EngineTimer.cs](../../../XRENGINE/Core/Time/EngineTimer.cs)
- [XRENGINE/Rendering/API/XRWindow.cs](../../../XRENGINE/Rendering/API/XRWindow.cs)

Current behavior:

- `Engine.Initialize(...)` assigns `RenderThreadId = Environment.CurrentManagedThreadId` before creating windows.
- `CreateWindows(...)` constructs `XRWindow` instances on that same thread.
- `Engine.Run(...)` then starts the background timer tasks and finally blocks that startup thread in `BlockForRendering()`.
- `EngineTimer.WaitToRender()` treats the blocked caller as the render thread and synchronously dispatches `RenderFrame` there.

The practical result is that the thread that entered the engine becomes:

- the engine render thread,
- the Silk.NET window thread,
- the OpenGL context owner,
- the Vulkan present thread,
- and the place where many existing "main thread" jobs run.

### Existing frame handoff is already multithreaded

The current frame model is documented in [../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md](../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md).

Relevant code:

- [XRENGINE/Core/Time/EngineTimer.cs](../../../XRENGINE/Core/Time/EngineTimer.cs)

Current behavior:

- `UpdateThread`, `CollectVisibleThread`, and `FixedUpdateThread` are launched via `Task.Run(...)`.
- `CollectVisibleThread` does `DispatchCollectVisible()`, waits for `_renderDone`, then runs swap publication.
- `WaitToRender()` on the render thread waits for `_swapDone`, dispatches render, then signals `_renderDone`.
- `DispatchRender()` explicitly notes that `RenderFrame` dispatch must stay synchronous to remain on the render thread.

This is good news: the engine already thinks in terms of render-thread affinity. The missing piece is that the render thread is still the startup thread.

### Window execution is render-thread-affine today

Relevant code:

- [XRENGINE/Rendering/API/XRWindow.cs](../../../XRENGINE/Rendering/API/XRWindow.cs)
- [XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs](../../../XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs)

Current behavior:

- `XRWindow.BeginTick()` subscribes `SwapBuffers` and `RenderFrame` into the timer.
- `XRWindow.RenderFrame()` calls `Window.DoEvents()` and `Window.DoRender()`.
- `Window.DoRender()` triggers the Silk.NET `Render` event, which reaches `XRWindow.RenderCallback(double delta)`.
- `RenderCallback(...)` then runs global pre-render hooks, viewport rendering, and `Renderer.RenderWindow(delta)`.

This means that visible desktop presentation is already owned by the render thread. The refactor is about moving that thread off the startup or editor thread, not changing the fact that render must stay on a single affine thread.

### "Main thread" naming is now semantic debt

Relevant code:

- [XRENGINE/Engine/Engine.Threading.cs](../../../XRENGINE/Engine/Engine.Threading.cs)

Current behavior:

- `EnqueueMainThreadTask(...)` is an alias for `EnqueueRenderThreadTask(...)`.
- logging and diagnostics still refer to "main thread" even when they mean "render thread".
- there are guardrails warning when non-GPU jobs execute during render dispatch.

This refactor should treat those aliases as transitional compatibility shims, not as the desired long-term naming.

### Backend constraints already point to the same design

Relevant code:

- [XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLSharedContext.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLSharedContext.cs)
- [XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs](../../../XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs)
- [XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs](../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs)

Current behavior:

- `GLSharedContext.Initialize(...)` must be called while the primary GL context is current on the render thread.
- OpenXR explicitly notes that `Task.Run` parallelization can break GL context ownership and runtime expectations.
- the engine already has a background secondary GPU context for limited off-thread GPU work.

This confirms the correct boundary: off-thread helper contexts are acceptable, but the primary on-screen context or swapchain still needs one owning render thread.

## Non-Goals

This refactor does not attempt to solve all rendering scalability problems at once.

Out of scope for the initial implementation:

- multi-threaded draw submission within a single OpenGL context,
- generalized multi-threaded Vulkan command recording across all passes,
- redesigning the render graph,
- changing the `CollectVisible -> SwapBuffers -> Render` sequencing model,
- replacing Silk.NET or GLFW,
- rewriting the editor UI systems before the thread split lands,
- forcing server or headless processes to spawn a render thread when they do not own windows.

## Requirements

### Functional requirements

- Desktop windows must still render correctly for OpenGL and Vulkan.
- Window close, focus, resize, title updates, HDR preference, and VSync changes must continue working.
- Multi-window startup must remain supported.
- `RenderThreadId` must refer to the dedicated render thread, not the startup thread.
- Any code that touches `IWindow`, `GLContext`, or present paths must run on the render thread.
- Render-thread job queues must still work during the transition.

### Editor requirements

- The editor thread must no longer be forced to block in `BlockForRendering()`.
- ImGui overlays, scene panel presentation, and editor camera viewports must still render.
- Input, focus, and pointer state pumped on the render or window thread must be consumable on the editor thread without races.

### XR requirements

- OpenXR startup work that requires a current graphics context must remain render-thread-affine.
- OpenVR and mirror-window rendering must keep their current swap or present semantics.
- VR-disabled desktop rendering must remain the baseline path.

## Proposed Architecture

## Thread Roles After Refactor

The target thread model is:

```text
Entry / App Thread
  - engine bootstrap orchestration
  - editor logic and non-GPU UI state preparation
  - gameplay services that are not already on Update / FixedUpdate
  - receives input snapshots from render thread

Update Thread
  - existing gameplay update loop

CollectVisible Thread
  - existing visibility collection and swap publication

FixedUpdate Thread
  - existing deterministic work

Dedicated Render Thread
  - creates and destroys engine windows
  - owns Silk.NET window callbacks
  - owns OpenGL contexts and Vulkan present path
  - pumps DoEvents / DoRender
  - executes RenderFrame and render-thread jobs
```

Important clarification:

- In operating-system terms, the dedicated render thread becomes the window thread for engine-owned windows.
- In engine terms, the startup or editor thread stops being the render thread.

That is the only coherent way to satisfy both desktop rendering and graphics API thread affinity.

## High-Level Design

### 1. Add a render-thread host that owns engine windows

Introduce a small runtime host responsible for:

- spawning the dedicated render thread,
- setting `RenderThreadId` from inside that thread,
- creating startup windows there,
- running the blocking render loop there,
- shutting the thread down cleanly after the last engine window closes.

This host should be the only code that:

- calls `CreateWindows(...)` for visible engine windows,
- invokes `BlockForRendering(...)`,
- and directly owns the render-thread lifecycle.

Conceptually:

```text
Engine.Initialize(...)
  -> start RenderThreadHost
  -> RenderThreadHost thread:
       SetRenderThreadId(current)
       CreateWindows(startupSettings.StartupWindows)
       signal startup barrier
       BlockForRendering(IsEngineStillActive)
```

The entry thread waits on a startup barrier so the rest of initialization can assume startup windows already exist.

### 2. Make visible `XRWindow` instances render-thread-owned objects

`XRWindow` should be treated as render-thread-affine for all operations that touch:

- `Window`,
- `Renderer`,
- `Input`,
- viewport subscription state,
- swapchain or GL-context state.

Safe app-thread access should be limited to immutable snapshots or explicit mailbox calls.

That means:

- app-thread callers do not directly call `Window.DoEvents()`, `Window.DoRender()`, `Window.MakeCurrent()`, or renderer cleanup methods,
- mutable window operations are marshaled through a render-thread queue,
- fields observed from other threads should be either snapshot copies or protected by clearly documented ownership.

### 3. Replace implicit window access with a render-thread mailbox

The render-thread host should expose a mailbox for operations such as:

- create window,
- destroy window,
- apply VSync or HDR preference,
- update title or size,
- force render-state recheck,
- invalidate scene-panel resources,
- request screenshot or readback.

Some of this already routes through `EnqueueRenderThreadTask(...)`. The difference is scope:

- today, callers often still hold direct `XRWindow` references and may perform thread-sensitive work themselves,
- after this refactor, the mailbox becomes the normal way to perform any window-affine mutation from outside the render thread.

### 4. Keep the frame model, move only who owns it

The design should not rewrite the timer fence model immediately.

The following invariants stay the same:

- `UpdateThread` mutates gameplay state,
- `CollectVisibleThread` builds the next frame and publishes swap state,
- the render thread waits for swap completion and synchronously dispatches render,
- world and viewport `SwapBuffers()` remain the publication boundary.

The critical change is simply that `WaitToRender()` is no longer called on the engine entry thread.

### 5. Publish input and window state snapshots to the app side

When the render thread pumps window events, game and editor systems still need to consume input and focus changes safely.

The simplest model is snapshot publication:

- render thread pumps Silk input and callbacks,
- render thread publishes immutable per-frame snapshots,
- app or update thread consumes the latest snapshot before or during update.

This avoids cross-thread direct use of Silk device objects.

Published state should include at minimum:

- keyboard snapshot,
- mouse position and buttons,
- wheel deltas,
- focus state,
- close requested state,
- framebuffer and window size,
- text input or IME events needed by editor UI.

### 6. Cleanly separate terminology: render thread vs app thread

As part of the refactor, documentation, logging, and new APIs should stop using "main thread" to mean "render thread."

Desired terminology:

- `RenderThread`: owns windows, contexts, present, and render-thread jobs.
- `AppThread`: engine entry thread or editor orchestration thread.
- `UpdateThread`: gameplay update task.
- `PhysicsThread`: fixed update or physics task.

Compatibility aliases such as `EnqueueMainThreadTask(...)` can remain for a while, but new code should avoid them.

## Backend-Specific Notes

### OpenGL

OpenGL is the strictest case because the current context can only be current on one thread at a time.

Design rules:

- the primary visible window context remains permanently owned by the dedicated render thread,
- shared contexts such as `GLSharedContext` remain valid for background shader compilation or uploads,
- any code that assumes the startup thread can restore the primary context after creating helper windows must be audited.

Likely code touchpoints:

- [XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLSharedContext.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLSharedContext.cs)
- [XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs](../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs)
- [XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs)

### Vulkan

Vulkan is less constrained by context current-ness but still requires clear window and present ownership.

Design rules:

- surface creation, swapchain recreation, and present continue to run on the dedicated render thread,
- any loops that wait on non-zero framebuffer size and call `Window.DoEvents()` remain on that thread,
- app-thread code must not directly trigger swapchain-sensitive window mutations.

Likely code touchpoints:

- [XRENGINE/Rendering/API/Rendering/Vulkan/SwapChain.cs](../../../XRENGINE/Rendering/API/Rendering/Vulkan/SwapChain.cs)
- [XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.cs](../../../XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.cs)

### OpenXR and OpenVR

XR already assumes render-thread affinity in several places, so the refactor should align with that rather than fight it.

Design rules:

- OpenXR session and swapchain work that needs a current graphics context stays on the render thread,
- desktop mirror composition remains render-thread-owned,
- OpenVR compositor submission remains in the render-thread swap path.

Likely code touchpoints:

- [XRENGINE/Rendering/API/Rendering/OpenXR](../../../XRENGINE/Rendering/API/Rendering/OpenXR)
- [XRENGINE/Engine/Engine.VRState.cs](../../../XRENGINE/Engine/Engine.VRState.cs)

## Editor-Specific Notes

### ImGui editor path

The immediate goal is not to move every editor UI data structure onto a separate thread. The immediate goal is to stop using the entry thread as the render or window thread.

Phase-1 acceptable behavior:

- app thread prepares editor state,
- render thread executes ImGui frame submission and drawing using published state or thread-safe copies,
- scene panel rendering stays render-thread-owned.

Longer term, ImGui state preparation can be cleaned up further if needed, but that is a follow-on optimization, not a prerequisite for the thread split.

### Native editor UI path

The native UI path is still under active development and should be treated as a higher-risk integration surface.

For the initial refactor:

- preserve behavior rather than redesigning it,
- isolate any required window-thread assumptions behind explicit adapters,
- do not let native UI concerns block the runtime or ImGui render-thread split.

## Detailed Refactor Plan

### Phase 0: Terminology and invariants

Before moving ownership, make the rules explicit.

Deliverables:

- document which thread owns each subsystem,
- add asserts or diagnostics where `IWindow` or renderer-affine methods are called from the wrong thread,
- mark `EnqueueMainThreadTask(...)` as compatibility terminology in docs and comments.

Why this phase matters:

- it reduces accidental regressions while the thread split is in progress,
- it clarifies whether a bug is a real render-thread problem or a stale naming problem.

### Phase 1: Render-thread bootstrap host

Introduce a dedicated render-thread host and move startup window creation plus `BlockForRendering()` into it.

Deliverables:

- render thread start and stop lifecycle,
- startup barrier so `Engine.Initialize(...)` only continues after windows exist,
- `RenderThreadId` assigned from the new dedicated thread,
- engine entry thread no longer calls `BlockForRendering()` directly.

Expected code changes:

- `Engine.Run(...)`
- `Engine.Initialize(...)`
- window creation path in `Engine.Windows.cs`
- lifecycle and cleanup sequencing

### Phase 2: Window-affinity isolation

Once windows live on the render thread, make that ownership explicit in API boundaries.

Deliverables:

- render-thread mailbox for window mutations,
- `XRWindow` methods split into render-thread-only and snapshot-safe accessors,
- any app-thread direct `Window` or `Renderer` access replaced with queued operations.

Expected code changes:

- `XRWindow`
- runtime rendering host services
- editor window-management helpers

### Phase 3: Input and event snapshot bridge

Move consumption away from direct use of thread-affine Silk objects.

Deliverables:

- input snapshot publication from render thread,
- app-thread consumption path for editor and gameplay,
- clear ordering relative to update and pause stepping.

Expected code changes:

- input wrappers and engine input state
- focus and resize routing
- text input bridging for editor widgets

### Phase 4: Editor and tool integration hardening

Audit all editor integrations that still assume the render thread is the startup thread.

Deliverables:

- ImGui overlay submission verified,
- scene panel and viewport presentation verified,
- dialog, clipboard, drag-and-drop, and profiler overlays audited.

Expected code changes:

- editor bootstrap
- scene panel adapters
- profiler or overlay callbacks that currently assume startup-thread affinity

### Phase 5: XR and multi-window hardening

Verify the refactor against the highest-risk presentation paths.

Deliverables:

- OpenXR context-affine startup still works,
- OpenVR mirror and stereo swap paths still work,
- multiple desktop windows render and close cleanly,
- swapchain recreation and framebuffer resize still behave correctly.

### Phase 6: Cleanup and API simplification

Once behavior is stable, remove transitional assumptions.

Deliverables:

- dead startup-thread render code removed,
- naming cleaned up where practical,
- final architecture docs updated from work-doc status into stable docs if the refactor lands.

## Suggested Type and Responsibility Changes

These are conceptual names, not required final names.

### `RenderThreadHost`

Responsibilities:

- start dedicated render thread,
- own render-thread message loop,
- create and destroy engine windows,
- run `BlockForRendering()`.

### `RenderThreadMailbox`

Responsibilities:

- queue window or renderer mutations onto the render thread,
- allow synchronous or async request-reply patterns when needed,
- centralize cross-thread diagnostics.

### `WindowInputSnapshot`

Responsibilities:

- immutable snapshot of input and window state,
- versioned publication per window or per frame,
- safe cross-thread consumption.

## Failure Modes To Design For

### 1. Window creation race during startup

If the engine continues initialization before the render thread has created windows, code that assumes `Engine.Windows` is populated will break.

Mitigation:

- explicit startup barrier after window creation and renderer instantiation.

### 2. App thread accidentally touching Silk window objects

This will become easier to do incorrectly once windows no longer live on the startup thread.

Mitigation:

- thread assertions,
- render-thread mailbox APIs,
- fewer public escape hatches that expose raw `IWindow` usage outside render-owned code.

### 3. Input events arrive on render thread but gameplay reads stale state

Mitigation:

- versioned snapshots,
- clear publication point relative to update dispatch,
- tests for focus, key transitions, pointer deltas, and text input.

### 4. OpenXR or GL helper contexts assume the old startup thread owns the primary context

Mitigation:

- audit all context creation and restoration helpers,
- ensure helper windows or shared contexts are initialized from render-thread-owned call sites.

### 5. Shutdown deadlocks

If cleanup waits on the wrong thread or window-close callbacks recursively tear down the render host, shutdown can hang.

Mitigation:

- single owner for render-thread shutdown,
- well-defined last-window-close path,
- no nested blocking cross-thread waits inside window callbacks.

## Validation Plan

### Build validation

- `Build-Editor`
- `Build-Server`
- `Build-VRClient`

### Smoke validation

- start editor in default world,
- start editor in unit-testing world,
- resize, minimize, restore, and close the main window,
- verify focus and keyboard or mouse input still reach the editor,
- verify multiple startup windows still render correctly.

### Backend validation

- OpenGL desktop rendering,
- Vulkan desktop rendering,
- Vulkan swapchain recreation after resize,
- VSync and HDR preference changes,
- screenshot or readback flows,
- scene-panel mode and full-window mode.

### XR validation

- OpenXR desktop mirror path,
- OpenXR context-affine startup,
- OpenVR stereo and mirror swap paths.

### Regression tests to add or extend

- render-thread host startup and shutdown tests,
- window command-queue tests,
- input snapshot publication tests,
- runtime rendering host service tests for render-thread routing,
- targeted window-close and framebuffer-resize tests.

## Open Questions

### 1. How much editor UI preparation must remain on the render thread initially?

Recommendation:

- only keep what is required for correctness in phase 1,
- move data preparation off the render thread later if profiling shows value.

### 2. Do any editor platform services require the startup thread specifically?

Examples:

- file dialogs,
- clipboard access,
- shell integration,
- drag-and-drop.

Recommendation:

- audit each one explicitly rather than assuming the answer is yes or no.

### 3. Should hidden utility windows use the same render thread host?

Recommendation:

- yes for any graphics-backed engine window,
- maybe no for pure OS utility windows that are not part of the graphics lifetime.

## Recommended Implementation Order

The least risky attack order is:

1. render-thread terminology cleanup and assertions,
2. render-thread host bootstrap with window creation moved first,
3. mailbox routing for window mutations,
4. input snapshot bridge,
5. editor and XR hardening,
6. cleanup and API simplification.

This order isolates the highest-value architectural shift first while keeping the existing frame-fence model intact.

## Final Recommendation

Proceed with a dedicated render thread that owns engine windows end-to-end.

Do not try to keep visible windows on the app or editor thread while pushing present onto some unrelated worker. That design fights OpenGL context ownership, complicates Vulkan swapchain lifetime, and makes the editor harder to reason about.

For XRENGINE, the correct split is:

- app or editor thread for orchestration and non-GPU logic,
- update, collect-visible, and fixed-update threads as they already exist,
- one dedicated render thread that also becomes the window thread for engine-owned windows.

That matches the current architecture better than any partial workaround and gives the cleanest path to a v1-quality threading model.
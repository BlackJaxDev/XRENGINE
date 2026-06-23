# Dedicated Render Thread Window Ownership Plan

## Goal

Move engine-owned desktop rendering off the current startup or editor thread and onto an explicit render ownership model. The model must separate app/editor orchestration from GPU submission, and it must also define how native window event pumping, graphics context ownership, swapchain presentation, and interactive resize cooperate.

The target outcome is:

- the app or editor thread no longer blocks in the render loop,
- OpenGL and Vulkan still render to visible desktop windows,
- the existing `CollectVisible -> SwapBuffers -> Render` handoff remains intact,
- render-thread-only GPU rules become stricter and easier to reason about,
- editor and gameplay code can continue running while render work is saturated,
- native Win32 resize can keep the window rectangle responsive while the rendered framebuffer catches up under a fixed budget.

This plan is intentionally a pre-ship architectural refactor. XRENGINE does not owe backward compatibility yet, so internal API cleanup and terminology changes are allowed when they materially improve correctness.

## Decision Summary

The viable design for this repo is not "render from an arbitrary worker thread while the UI thread keeps mutating graphics state." Graphics state still needs a strict owner. The resize work found one more requirement: the thread processing `WM_ENTERSIZEMOVE`, `WM_SIZING`, and `WM_TIMER` must stay cheap enough that Windows can keep the native rectangle moving at display cadence.

The target design therefore has two layers:

1. a baseline dedicated render-thread host that removes rendering from the app or editor thread,
2. a smooth-resize presentation path that lets native window size snapshots advance independently from expensive render-pipeline and swapchain resource rebuilds.

For ordinary rendering the viable ownership model is:

1. create or pump engine windows on the thread required by the selected windowing backend,
2. treat that thread as the native window thread for Silk.NET, GLFW, SDL, or a future Win32 backend,
3. keep OpenGL contexts and Vulkan present paths owned by the render thread when the backend bridge permits it,
4. publish input, resize, focus, and close state from the render thread to the app or editor thread,
5. route any window or GPU-affine operation through a render-thread mailbox.

Important backend constraint: GLFW's documented "main thread" rule means a GLFW/Silk.NET window path cannot simply reinterpret an arbitrary worker thread as the GLFW main thread. A fully compliant GLFW path keeps GLFW initialization, window creation, callbacks, and event processing on the process main thread or replaces that layer with a backend whose threading contract allows a dedicated window-pump thread. The render-thread split is still viable, but the doc must distinguish "render thread owns GPU work" from "windowing backend owns native event processing."

For smooth native Win32 resize, that baseline is not enough by itself. A thread that both handles the native move/size modal loop and performs full render/present work will always choose between:

- synchronous repaint work that updates the framebuffer but slows native sizing, or
- deferred repaint work that preserves native sizing but waits until mouse-up.

The smooth-resize design must therefore either split the window pump from expensive render/present work, or run an explicit resize micro-pipeline that only performs cheap presentation-scale work during the modal loop. In both cases, full render-pipeline FBO regeneration must be decoupled from every intermediate native size.

On Windows this is practical with a backend whose contract exposes a window-pump thread, especially a raw Win32 adapter. On platforms or backends that require window creation on the process main thread, it is much harder.

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

## Internet Research Validation - 2026-06-23

Primary-source research supports the core split, with two important corrections.

Validated assumptions:

- Win32 windows are tied to the thread message queue of the thread that created them. Microsoft documents that GUI threads have thread-specific queues, messages for a window are posted to the creating thread, and a thread that creates windows must provide a message loop. Sources: <https://learn.microsoft.com/en-us/windows/win32/winmsg/about-messages-and-message-queues> and <https://learn.microsoft.com/en-us/windows/win32/winmsg/using-messages-and-message-queues>.
- `WM_ENTERSIZEMOVE` and `WM_EXITSIZEMOVE` really bracket the native move/size modal loop. Source: <https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-entersizemove> and <https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-exitsizemove>.
- `WM_TIMER` is a low-priority message posted only when higher-priority messages are absent, so timer repaint work inside a move/size loop is best-effort and must stay tiny. Source: <https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-timer>.
- WGL/OpenGL context ownership is single-thread current: a rendering context can be current to only one thread at a time, and a thread needs a current context before GL calls. Source: <https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-wglmakecurrent>.
- GLFW allows GL contexts to move between threads only after detaching them from the old thread, but most GLFW functions must run on the GLFW main thread, including window/event functions. Sources: <https://www.glfw.org/docs/latest/intro_guide.html#thread_safety>, <https://www.glfw.org/docs/latest/context_guide.html#context_current>, and <https://www.glfw.org/docs/latest/group__window.html>.
- SDL2 event pumping must run on the thread that initialized the video subsystem, with the docs recommending main-thread use for safety. Source: <https://wiki.libsdl.org/SDL2/SDL_PumpEvents>.
- Win32 Vulkan swapchains normally cannot use an intentionally stale swapchain extent as the resize-lag layer. The Vulkan WSI spec says Win32 `minImageExtent`, `maxImageExtent`, and `currentExtent` must equal the window size, and a swapchain `imageExtent` can differ only if present scaling is explicitly used. Sources: <https://docs.vulkan.org/spec/latest/chapters/VK_KHR_surface/wsi.html> and <https://docs.vulkan.org/refpages/latest/refpages/source/VkSwapchainPresentScalingCreateInfoKHR.html>.
- .NET/Windows editor platform services such as common dialogs, clipboard, drag-and-drop, and some COM-backed integrations may require an STA thread. Source: <https://learn.microsoft.com/en-us/dotnet/api/system.stathreadattribute>.

Corrections applied to this plan:

- Do not assume a Silk.NET/GLFW window can be created on an arbitrary dedicated render thread. A GLFW-compliant implementation keeps GLFW windowing on the process main thread, or introduces a backend with an explicit Windows window-pump contract.
- Do not use "lagging Vulkan swapchain extent" as the default Win32 smooth-resize mechanism. Without present-scaling support, the actual Vulkan swapchain must catch up to the live Win32 surface extent before presenting, or the frame must be skipped. The cheap lag layer should be `PipelineOutputExtent` and `FullInternalExtent`, not the Win32 swapchain extent.

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
- Any code that touches graphics context state, renderer state, swapchain state, or present paths must run on the render thread.
- Any code that touches native window message pumping must run on the owning window thread and must not block on GPU work.
- Render-thread job queues must still work during the transition.
- Interactive native resize must let the native client rectangle update at the OS message cadence even when the rendered framebuffer, swapchain, and full pipeline resources lag behind.
- Full render-pipeline FBO regeneration must not run for every intermediate drag size.

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

The baseline thread model is:

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

Window / Event Thread
  - creates and destroys engine windows when required by the backend
  - owns Silk.NET / GLFW / SDL window callbacks
  - pumps DoEvents or the backend event loop
  - publishes window and input snapshots

Dedicated Render Thread
  - owns OpenGL contexts and Vulkan present path
  - pumps DoRender or backend render work only when the backend permits it
  - executes RenderFrame and render-thread jobs
```

Important baseline clarification:

- In operating-system terms, each visible window has a concrete window/event thread.
- In engine terms, the startup or editor thread stops being the render thread.
- In GLFW-compliant mode, the process main thread remains the window/event thread and the render thread is a separate GPU owner.
- In a future raw Win32 or validated SDL mode, the window/event thread may be a dedicated `WindowPumpHost`.

That baseline is enough to remove app/editor thread blocking, but it is not enough for the desired Win32 native resize behavior if the window/event pump and render work remain collapsed. In that shape, `WM_ENTERSIZEMOVE` still runs on the same thread that would do render work.

The smooth-resize thread model adds one more role:

```text
Window Pump Thread
  - creates or owns the HWND / Silk window message pump for visible desktop windows
  - processes Win32 move/size/focus/input/close messages
  - publishes latest-only window snapshots to the render thread
  - never performs full render, swapchain recreate, shader compile, resource generation, or GPU waits

Render Thread
  - owns OpenGL context current-ness where supported by the backend bridge
  - owns Vulkan device, swapchain state, render pipeline state, and present
  - consumes latest window snapshots and catches presentation/output size up under budget
  - executes normal RenderFrame and render-thread jobs
```

The implementation may phase this in by keeping the baseline single render/window thread for non-resize paths, but the design target for "smooth chrome plus live trailing internal/output framebuffer" is that the native message pump and expensive render/present work do not occupy the same thread during a Win32 modal resize.

## High-Level Design

### 1. Add render and window hosts with backend-checked ownership

Introduce a small runtime host pair responsible for:

- spawning the dedicated render thread,
- setting `RenderThreadId` from inside that thread,
- creating startup windows on the backend-required window thread,
- running the blocking render loop there,
- shutting the thread down cleanly after the last engine window closes.

These hosts should be the only code that:

- calls `CreateWindows(...)` for visible engine windows on a backend-compliant window thread,
- coordinates `WindowPumpHost` creation in smooth-resize mode,
- invokes `BlockForRendering(...)`,
- and directly owns the render-thread lifecycle.

Backend gate:

- GLFW/Silk.NET compliant mode must keep GLFW windowing calls on the process main thread. The entry thread can become a lightweight window/event pump while editor/app orchestration runs outside the render loop.
- A dedicated `WindowPumpHost` is valid only after the selected backend proves that creating and pumping windows on that thread is supported. Raw Win32 is the clean Windows-first target for this; SDL2 may be viable if the video subsystem is initialized and pumped on the same thread, but it needs a prototype because Silk.NET may add stricter rules.
- The render thread may own OpenGL only after the context is detached from the window thread and made current on the render thread. If that transfer is not reliable for a backend, keep that backend on the baseline collapsed window/render model until a native backend exists.

Conceptually:

```text
Engine.Initialize(...)
  -> start WindowEventHost on backend-required thread
  -> WindowEventHost:
       CreateWindows(startupSettings.StartupWindows)
       signal native-window-ready
  -> start RenderThreadHost
  -> RenderThreadHost thread:
       SetRenderThreadId(current)
       attach render ownership where backend permits
       signal startup barrier
       BlockForRendering(IsEngineStillActive)
```

The entry thread waits on a startup barrier so the rest of initialization can assume startup windows already exist.

In smooth-resize mode, the same startup barrier must wait until both sides exist:

```text
Engine.Initialize(...)
  -> start WindowPumpHost
  -> WindowPumpHost thread:
       create native windows / HWNDs
       publish initial WindowSurfaceSnapshot
       signal native-window-ready
  -> start RenderThreadHost
  -> RenderThreadHost thread:
       SetRenderThreadId(current)
       attach renderer/swapchain state to native window handles
       signal render-ready
       BlockForRendering(IsEngineStillActive)
```

### 2. Make visible `XRWindow` ownership explicit

`XRWindow` currently mixes native window ownership, input callbacks, renderer state, swapchain state, and viewport presentation. The refactor should split those responsibilities instead of preserving one ambiguous affinity.

Baseline mode may keep the whole `XRWindow` on one thread only for backends where that is compliant and intentional. GLFW-compliant mode should split native window/event ownership from render ownership even before smooth resize is enabled. Smooth native resize mode should treat the responsibilities separately:

- native `Window` / `HWND` message pumping is window-pump-thread-affine,
- `Renderer`, graphics context state, swapchain state, and present are render-thread-affine,
- viewport subscription and render-pipeline state are render-thread-affine,
- input and window state observed elsewhere are snapshot data, not direct native-object access.

Safe app-thread access should be limited to immutable snapshots or explicit mailbox calls.

That means:

- app-thread callers do not directly call `Window.DoEvents()`, `Window.DoRender()`, `Window.MakeCurrent()`, or renderer cleanup methods,
- native window mutations are marshaled through the window-pump mailbox,
- renderer, swapchain, and viewport mutations are marshaled through the render-thread mailbox,
- fields observed from other threads should be either snapshot copies or protected by clearly documented ownership.

### 3. Replace implicit window access with window and render mailboxes

The window host should expose a mailbox for native window operations such as:

- create window,
- destroy window,
- update title or size,
- focus, minimize, restore, or close requests,
- cursor and raw-input configuration.

The render-thread host should expose a mailbox for GPU-affine operations such as:

- apply VSync or HDR preference when it affects swap/present state,
- force render-state recheck,
- invalidate scene-panel resources,
- request screenshot or readback.

Some of this already routes through `EnqueueRenderThreadTask(...)`. The difference is scope:

- today, callers often still hold direct `XRWindow` references and may perform thread-sensitive work themselves,
- after this refactor, the matching mailbox becomes the normal way to perform any window-affine or render-affine mutation from the wrong thread.

### 4. Keep the frame model, move only who owns it

The design should not rewrite the timer fence model immediately.

The following invariants stay the same:

- `UpdateThread` mutates gameplay state,
- `CollectVisibleThread` builds the next frame and publishes swap state,
- the render thread waits for swap completion and synchronously dispatches render,
- world and viewport `SwapBuffers()` remain the publication boundary.

The critical change is simply that `WaitToRender()` is no longer called on the engine entry thread.

### 5. Publish input and window state snapshots to the app side

When the render or window-pump thread receives window events, game and editor systems still need to consume input and focus changes safely.

The simplest model is snapshot publication:

- the owning window thread pumps Silk/Win32 input and callbacks,
- the owning window thread publishes immutable snapshots,
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

- `RenderThread`: owns graphics contexts, render-side window adapters, present, and render-thread jobs.
- `WindowThread`: owns native window creation, event pumping, callbacks, and backend-specific window APIs.
- `AppThread`: engine entry thread or editor orchestration thread.
- `UpdateThread`: gameplay update task.
- `PhysicsThread`: fixed update or physics task.

Compatibility aliases such as `EnqueueMainThreadTask(...)` can remain for a while, but new code should avoid them.

## Smooth Resize Mechanics

The interactive resize target is not "rebuild everything at every mouse delta."
The target is:

1. the native window rectangle changes immediately,
2. the render thread consumes only the newest size snapshot it can afford,
3. presentation/backbuffer resources catch up as soon as safe,
4. the full render-pipeline internal graph catches up after resize settles or after a bounded catch-up budget,
5. frames between those points render through a cheap output-scale path.

This requires treating resize as several different extents instead of one global size.

### Resize Extent Model

Use four explicit extents:

| Extent | Owner | Update cadence | Purpose |
|---|---|---:|---|
| `NativeClientExtent` | Window pump thread | Every native message | The actual Win32 client size. This drives chrome, hit testing, input coordinates, and window snapshots. |
| `PresentationExtent` | Render thread | Opportunistic, budgeted | Swapchain/backbuffer/default-framebuffer size currently safe to render and present. On Win32 Vulkan without present scaling, this must match the current surface extent before presenting; lag is expressed by skipped/coalesced present ticks, not by presenting a stale swapchain. |
| `PipelineOutputExtent` | Render thread / pipeline instance | Fast path during drag | The final output image or post-process output size used to fill the current presentation target. This is the "elastic" resize layer. |
| `FullInternalExtent` | Pipeline resource generation scheduler | Debounced / incremental | Size of expensive internal graph resources such as GBuffer, lighting accumulation, history, bloom, AO, depth, velocity, and feature-local FBOs. |

Rules:

- `NativeClientExtent` is latest-only. Intermediate sizes are allowed to be dropped.
- `PresentationExtent` never blocks the window pump. If swapchain/default framebuffer catch-up is busy, skip or coalesce the present tick unless the backend explicitly supports presenting a differently sized target.
- `PipelineOutputExtent` is allowed to change more often than `FullInternalExtent`, but it must be limited to the minimal resources needed to composite to the current presentation target.
- `FullInternalExtent` is allowed to lag significantly during live resize. It changes only when a pending generation is ready to commit, or when a hard bound says the old size is no longer acceptable.
- On Win32 Vulkan, intentional visual lag should live in `PipelineOutputExtent` and `FullInternalExtent`; the swapchain extent itself should be recreated to the newest live surface size at a bounded cadence or the frame should be skipped.

### Latest-Only Window Snapshot Mailbox

The window pump thread publishes a compact immutable snapshot:

```csharp
readonly record struct WindowSurfaceSnapshot(
    ulong Sequence,
    int ClientWidth,
    int ClientHeight,
    float DpiScaleX,
    float DpiScaleY,
    bool IsMinimized,
    bool IsInteractiveResize,
    long Timestamp);
```

Publication is overwrite-latest, not queue-all. The render thread reads the newest sequence at each frame boundary or resize micro-frame. This avoids doing work for stale intermediate sizes and lets the native drag stay smooth.

The snapshot drives:

- input coordinate normalization,
- camera aspect for editor overlays and final presentation,
- presentation catch-up requests,
- full internal generation requests,
- diagnostics showing native size, presentation size, output size, and internal size separately.

### Render Thread Resize Controller

Add a `WindowResizeController` owned by `XRWindow` or by the render-thread host.
It consumes `WindowSurfaceSnapshot` and produces three requests:

1. `RequestPresentationCatchUp(snapshot)` for swapchain/backbuffer size.
2. `RequestPipelineOutputResize(snapshot)` for the cheap output-scale layer.
3. `RequestFullInternalResize(snapshot)` for the generation scheduler.

The controller must be allowed to skip or coalesce requests:

- During `IsInteractiveResize`, consume at most the newest snapshot per render tick.
- Drop any snapshot with a sequence older than the last consumed sequence.
- Do not block on a previous presentation recreate; mark it pending and continue rendering only if the backend can still present a valid target. On Win32 Vulkan, mismatch normally means recreate or skip.
- Do not request a full internal generation for every snapshot.

### Cheap Output-Scale Layer

The current pipeline resize behavior treats display-region changes and internal-resolution changes as reasons to invalidate broad resource sets. Smooth resize needs a narrower path.

During live resize, the pipeline should keep the committed `FullInternalExtent` generation active and render the scene into that stable internal graph. The final pipeline output then maps to the current `PresentationExtent` through one of these cheap operations:

- dynamic viewport/scissor and UV scale into the current present target,
- a final fullscreen copy/resolve from the stable scene color to `PipelineOutputExtent`,
- letterbox/pillarbox or crop when aspect changes too quickly,
- temporal/upscale history reset only when the output layer actually changes in a way that invalidates it.

This should be a new presentation/output extent API, not a call to the existing full internal-resolution resize path for every drag sample. Conceptually:

```text
XRViewport.SetPresentationOutputExtent(...)    // cheap, live resize path
XRViewport.SetInternalResolution(...)          // full graph generation path
```

Only the "present chain" may resize at this cadence. This includes resources whose sole purpose is to bridge the internal graph to the window:

- final post-process output,
- final AA/upscale output,
- swapchain/default-framebuffer blit target,
- small per-output metadata buffers such as display rect, UV scale, and jitter scale.

It must not recreate broad internal resources on every drag tick:

- GBuffer,
- depth/stencil prepass resources,
- velocity,
- transform ID,
- lighting accumulation,
- AO,
- bloom chains,
- shadow atlases,
- volumetric/fog/atmosphere feature FBOs,
- temporal history pairs except where the present-chain mode requires a dedicated output history.

This is the core compromise: the framebuffer can be slightly soft, scaled, or cropped during drag, but the window stays responsive and the image still refreshes.

### Full Internal Generation Catch-Up

`FullInternalExtent` follows a staged policy:

- First live-resize event: keep current internal generation.
- While dragging: request a pending generation only when the size delta crosses a threshold, a maximum lag timer expires, or the user pauses long enough.
- Mouse-up or resize-settled: request the exact final internal extent immediately.
- Pending generation builds incrementally and atomically commits only after required resources and FBOs validate.

Suggested initial policy:

- no full internal generation more often than 8-15 Hz during live drag,
- always commit the final size after `WM_EXITSIZEMOVE`,
- cap visible lag by area ratio, for example rebuild if native area exceeds committed internal area by more than 2x or falls below it by more than 50 percent,
- expose the policy through diagnostics before making it user-configurable.

This should reuse the existing resource-generation contract described in
[Render Pipeline Resource Lifecycle](../../../architecture/rendering/render-pipeline-resource-lifecycle.md):
the active generation keeps rendering until the pending generation is complete.

### Presentation Catch-Up

Presentation catch-up is backend-specific but must obey the same high-level rule: never block the native window pump.

For Vulkan:

- the render thread owns surface, swapchain, acquire, present, and swapchain recreation;
- swapchain recreate uses latest `NativeClientExtent`, not every intermediate size;
- acquire and present during interactive resize use non-blocking or bounded waits;
- Win32 swapchain extent must match the current surface extent before a normal present unless `VkSwapchainPresentScalingCreateInfoKHR` support is explicitly queried, enabled, and validated;
- if the swapchain is temporarily out-of-date, render only into backend-independent output/internal resources or skip only that present tick;
- a stale `FullInternalExtent` can be scaled into a freshly recreated swapchain; a stale Win32 swapchain should not be treated as the default lag buffer;
- old swapchain images and dependent resources retire through existing frame-slot synchronization.

For OpenGL:

- the primary context must be current only on the render thread;
- default framebuffer size catch-up depends on the platform backend and context/window bridge;
- if OpenGL cannot decouple native pump and context ownership safely in the first implementation, smooth native resize can be Vulkan-first while OpenGL continues using baseline behavior.

For both:

- the render loop should present the most recent completed output into the current valid presentation target, even if that output was rendered from an older internal extent;
- scaling/cropping metadata must be visible in diagnostics so a lagging internal/output framebuffer is intentional, not mistaken for a resize bug.

### Resize Frame Timeline

During a native drag:

```text
Window Pump Thread
  WM_SIZING / WM_WINDOWPOSCHANGED
    -> publish NativeClientExtent snapshot
    -> return immediately

Render Thread
  frame boundary or resize micro-frame
    -> read newest snapshot
    -> update camera/display aspect metadata
    -> opportunistically catch PresentationExtent up or skip present if no valid target exists
    -> resize only PipelineOutputExtent/present-chain resources if cheap enough
    -> render stable FullInternalExtent graph
    -> composite/scale to PresentationExtent
    -> present if acquire/present is ready, otherwise skip this present tick

Resource Generation Scheduler
  live resize budget
    -> maybe build pending FullInternalExtent generation incrementally
  mouse-up / settled
    -> build exact final generation
    -> commit atomically
```

This gives the desired user-facing behavior:

- the OS window rectangle moves smoothly,
- rendering continues during drag,
- the internal/output framebuffer may be lower resolution, scaled, cropped, or one or more size samples behind,
- the full-quality internal graph catches up when the system has time.

### Diagnostics

Add resize-specific diagnostics before tuning heuristics:

- `NativeClientExtent`
- `PresentationExtent`
- `PipelineOutputExtent`
- `FullInternalExtent`
- latest native sequence consumed by render thread
- latest native sequence committed by presentation catch-up
- full generation requested/started/committed counts
- dropped native snapshot count
- presentation recreate time and result
- present skipped reason during interactive resize
- output-scale mode: exact, upscale, downscale, crop, letterbox

These diagnostics belong in `log_rendering.log` / `log_vulkan.log` and in the editor Engine State window next to the existing interactive resize counters.

## Backend-Specific Notes

### OpenGL

OpenGL is the strictest case because the current context can only be current on one thread at a time.

Design rules:

- the primary visible window context remains permanently current only on the render thread,
- the window pump thread must never call GL, `Window.DoRender()`, or any path that might touch GL state,
- GLFW window and event APIs must still obey GLFW main-thread rules even if the GL context is moved to a render thread,
- if Silk.NET/GLFW cannot safely create a window on one thread and bind its context on the render thread, the first smooth-resize implementation should keep OpenGL on the baseline render/window thread model and mark native smooth resize as unsupported for OpenGL,
- shared contexts such as `GLSharedContext` remain valid for background shader compilation or uploads,
- any code that assumes the startup thread can restore the primary context after creating helper windows must be audited.

Likely code touchpoints:

- [XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLSharedContext.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLSharedContext.cs)
- [XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs](../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs)
- [XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs)

### Vulkan

Vulkan is less constrained by context current-ness but still requires clear window and present ownership.

Design rules:

- the render thread owns surface, swapchain recreation, acquire, queue submit, and present,
- the window pump thread owns Win32/Silk event processing and publishes latest-only size snapshots,
- any loops that wait on non-zero framebuffer size must consume snapshots or bounded waits rather than calling `Window.DoEvents()` from the render path during interactive resize,
- app-thread code must not directly trigger swapchain-sensitive window mutations,
- swapchain recreation is a presentation catch-up operation, not a full pipeline resource-generation trigger.
- On Win32, the Vulkan swapchain extent normally has to match the live surface extent. Validate optional present scaling before allowing a deliberately mismatched swapchain.
- Vulkan should be the first backend for validating smooth native resize with lagging internal/output resources because it does not have OpenGL's current-context constraint.

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

The immediate goal is not to move every editor UI data structure onto a separate thread. The immediate goal is to stop using the entry thread as the render thread. If GLFW remains the active backend, the entry thread may still be the window/event thread, but it should be a lightweight pump instead of the GPU submission thread.

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

Introduce a dedicated render-thread host and move `BlockForRendering()` into it. Startup window creation moves only to the backend-required window thread; smooth-resize mode coordinates with `WindowPumpHost` so native window pumping can stay lightweight.

Deliverables:

- render thread start and stop lifecycle,
- startup barrier so `Engine.Initialize(...)` only continues after native windows and render attachment are ready,
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

### Phase 3: Window pump snapshot bridge

Introduce the data path needed for smooth native resize before moving expensive rendering away from the window pump.

Deliverables:

- latest-only `WindowSurfaceSnapshot` publication,
- sequence-numbered native size, DPI, focus, minimized, and interactive-resize state,
- render-thread consumption of snapshots without calling `Window.DoEvents()` from the resize catch-up path,
- diagnostics for native size versus presentation size versus output size versus full internal size.

Expected code changes:

- `XRWindow`
- `Win32ModalLoopTimerInteractiveResizeStrategy` or successor Win32 pump adapter
- runtime rendering host services
- editor Engine State diagnostics

### Phase 4: Presentation catch-up controller

Add the render-thread controller that lets the framebuffer lag behind the native window while still presenting fresh frames during resize.

Deliverables:

- `NativeClientExtent`, `PresentationExtent`, `PipelineOutputExtent`, and `FullInternalExtent` tracked separately,
- Vulkan presentation catch-up that uses newest size snapshots and bounded/non-blocking waits,
- no full render-pipeline generation request for every intermediate native size,
- explicit present skipped reasons and catch-up timing diagnostics.

Expected code changes:

- `XRWindow`
- Vulkan swapchain resize/acquire/present path
- `XRViewport` resize routing
- render-pipeline instance resize APIs

### Phase 5: Cheap pipeline output resize path

Split live resize presentation from full internal graph resize.

Deliverables:

- present-chain/output resources can resize or scale under a small live-resize budget,
- stable `FullInternalExtent` generation keeps rendering while dragging,
- output-scale metadata drives final blit/composite/crop/letterbox,
- temporal/upscale/history resources reset only when their own contract requires it.

Expected code changes:

- `XRRenderPipelineInstance`
- `DefaultRenderPipeline` / `DefaultRenderPipeline2` present-chain resources
- render pipeline resource lifecycle scheduler
- Vulkan and OpenGL final-output binding paths

### Phase 6: Full internal generation catch-up policy

Wire live-resize heuristics into the existing pending-generation model.

Deliverables:

- debounce plus hard-lag thresholds for full internal resource generation,
- final exact-size generation on mouse-up or resize-settled,
- incremental generation build slices stay within the render-thread budget,
- generation commits remain atomic and validated.

Expected code changes:

- render pipeline resource lifecycle scheduler
- `XRRenderPipelineInstance`
- default pipeline declared-resource profiles
- tests for resize request coalescing and generation commit ordering

### Phase 7: Input and event snapshot bridge

Move consumption away from direct use of thread-affine Silk objects.

Deliverables:

- input snapshot publication from render thread,
- app-thread consumption path for editor and gameplay,
- clear ordering relative to update and pause stepping.

Expected code changes:

- input wrappers and engine input state
- focus and resize routing
- text input bridging for editor widgets

### Phase 8: Editor and tool integration hardening

Audit all editor integrations that still assume the render thread is the startup thread.

Deliverables:

- ImGui overlay submission verified,
- scene panel and viewport presentation verified,
- dialog, clipboard, drag-and-drop, and profiler overlays audited.

Expected code changes:

- editor bootstrap
- scene panel adapters
- profiler or overlay callbacks that currently assume startup-thread affinity

### Phase 9: XR and multi-window hardening

Verify the refactor against the highest-risk presentation paths.

Deliverables:

- OpenXR context-affine startup still works,
- OpenVR mirror and stereo swap paths still work,
- multiple desktop windows render and close cleanly,
- swapchain recreation and framebuffer resize still behave correctly.

### Phase 10: Cleanup and API simplification

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
- attach renderer and presentation state to windows created by the backend-required window thread,
- coordinate with `WindowPumpHost` in smooth-resize mode,
- run `BlockForRendering()`.

### `RenderThreadMailbox`

Responsibilities:

- queue window or renderer mutations onto the render thread,
- allow synchronous or async request-reply patterns when needed,
- centralize cross-thread diagnostics.

### `WindowPumpHost`

Responsibilities:

- own native window creation and message pumping when smooth native resize mode is enabled,
- publish latest-only `WindowSurfaceSnapshot` records,
- avoid renderer calls, swapchain recreation, resource generation, and GPU waits.

### `WindowSurfaceSnapshot`

Responsibilities:

- carry the latest native client size, DPI, focus/minimized state, and interactive-resize flag,
- use a monotonically increasing sequence so the render thread can drop stale snapshots,
- be immutable and safe to read from render, app, and editor code.

### `WindowResizeController`

Responsibilities:

- consume latest window snapshots on the render thread,
- maintain `NativeClientExtent`, `PresentationExtent`, `PipelineOutputExtent`, and `FullInternalExtent`,
- route cheap output resize requests separately from full internal generation requests,
- expose diagnostics for lag, skipped presents, dropped snapshots, and catch-up timing.

### `PresentationCatchUpController`

Responsibilities:

- own backend-specific swapchain/default-framebuffer catch-up,
- avoid blocking native window message processing,
- choose whether to present, skip, or continue with the previous presentation extent on each resize tick.

### `WindowInputSnapshot`

Responsibilities:

- immutable snapshot of input and window state,
- versioned publication per window or per frame,
- safe cross-thread consumption.

## Failure Modes To Design For

### 1. Window creation race during startup

If the engine continues initialization before the window host has created windows and the render thread has attached renderer state, code that assumes `Engine.Windows` is populated will break.

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

### 4. Render tries to process every native resize size

If the render thread queues work for every `WM_SIZING` size, it will fall behind and either consume stale sizes or stall on resource generation.

Mitigation:

- latest-only resize snapshots,
- sequence numbers and dropped-snapshot diagnostics,
- newest-size wins for presentation catch-up,
- full internal generation coalescing.

### 5. Full pipeline resources rebuild during every drag tick

Rebuilding GBuffer, depth, history, bloom, AO, and other FBOs for every intermediate client size recreates the original stutter in a different layer.

Mitigation:

- separate `PipelineOutputExtent` from `FullInternalExtent`,
- allow only present-chain/output resources to resize during live drag,
- defer full internal generation until thresholds, pause, or mouse-up.

### 6. Presentation output lags too far behind native size

A lagging internal/output framebuffer is acceptable, but unbounded lag becomes blurry, cropped, or visually broken. On Win32 Vulkan, the actual swapchain extent should not be the unbounded lag layer unless present scaling has been explicitly enabled and validated.

Mitigation:

- area-ratio and elapsed-time hard limits,
- output-scale mode diagnostics,
- optional temporary low-resolution catch-up generation for very large deltas.

### 6a. Windowing backend main-thread rules are violated

GLFW and Silk.NET windowing can fail or become undefined if window creation, event pumping, or callbacks move to a thread that the backend does not consider its main/window thread.

Mitigation:

- keep GLFW window/event functions on the process main thread in the GLFW-compliant implementation,
- introduce a backend capability flag before allowing dedicated `WindowPumpHost` creation,
- prototype raw Win32 or SDL window-pump ownership separately before making it a default path,
- assert when window APIs are called from a thread that does not own that backend.

### 7. OpenXR or GL helper contexts assume the old startup thread owns the primary context

Mitigation:

- audit all context creation and restoration helpers,
- ensure helper windows or shared contexts are initialized from render-thread-owned call sites.

### 8. Shutdown deadlocks

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
- GLFW-compliant mode where all GLFW window/event calls stay on the process main thread,
- SDL or raw Win32 prototype mode where window creation and event pumping are proven safe on the intended window-pump thread,
- Vulkan live native resize with fast chrome and continuously refreshed lagging internal/output framebuffer,
- resize drag where native size changes faster than presentation catch-up and frames skip cleanly,
- Win32 Vulkan swapchain recreation to the current surface extent, including explicit handling of `VK_ERROR_OUT_OF_DATE_KHR`, `VK_SUBOPTIMAL_KHR`, and minimized `(0, 0)` surface extents,
- resize drag where full internal graph intentionally stays at the previous committed generation,
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
- latest-only window snapshot coalescing tests,
- resize extent state-machine tests,
- present-chain resize versus full internal generation tests,
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
- keep the process entry thread STA-capable for Windows Forms/common-dialog, clipboard, drag-and-drop, and COM-backed integrations until each service has an explicit thread-affinity adapter.

### 3. Which backend owns smooth-resize visible windows?

Recommendation:

- GLFW-compliant mode should keep window/event APIs on the process main thread and use a render thread for GPU ownership only where context/present transfer is proven safe.
- For a true dedicated `WindowPumpHost`, prefer a raw Win32 windowing adapter or a separately validated SDL path over assuming Silk.NET/GLFW can run there.
- Treat the selected backend's thread rules as an implementation gate, not a preference.

### 4. Does baseline dedicated render-thread ownership alone solve Win32 native modal resize repainting?

Recommendation:

- treat dedicated render-thread ownership as the required cleanup for app/editor
  independence and render-thread affinity, not as a complete native modal resize
  fix by itself.
- if the dedicated render thread is also the Win32 window thread, `WM_TIMER`
  repaint work during `WM_ENTERSIZEMOVE` still competes with native sizing
  messages on that same thread.
- `EngineBorderlessResize` currently behaves like the default path for this
  problem and should not be counted as a complete realtime-resize solution until
  it has a real engine-owned resize pump.
- for "smooth native chrome plus live trailing internal/output framebuffer", use the snapshot,
  presentation catch-up, and cheap output-scale design above so native event
  pumping and expensive render/present work do not occupy the same blocking
  path during `WM_ENTERSIZEMOVE`.

### 5. Should hidden utility windows use the same render thread host?

Recommendation:

- yes for any graphics-backed engine window,
- maybe no for pure OS utility windows that are not part of the graphics lifetime.

## Recommended Implementation Order

The least risky attack order is:

1. render-thread terminology cleanup and assertions,
2. backend thread-affinity audit for GLFW, SDL, and the Windows editor services,
3. render-thread host bootstrap while keeping window creation on the backend-required window thread,
4. raw Win32 or validated SDL `WindowPumpHost` prototype for smooth resize,
5. mailbox routing for window mutations,
6. latest-only window surface snapshot bridge,
7. Vulkan presentation catch-up with non-blocking resize present and current-surface swapchain recreation,
8. cheap pipeline output resize path,
9. full internal generation catch-up policy,
10. input snapshot bridge,
11. editor and XR hardening,
12. cleanup and API simplification.

This order isolates the highest-value architectural shift first, then solves the resize-specific presentation problem before broader editor and XR hardening.

## Final Recommendation

Proceed with a dedicated render-thread architecture, but do not stop at "the render thread owns the window."

Do not try to keep visible windows on the app or editor thread while arbitrary code mutates graphics state from another worker. That design fights OpenGL context ownership, complicates Vulkan swapchain lifetime, and makes the editor harder to reason about.

For smooth native resize, also do not run full render/present work directly inside the Win32 modal resize pump. The correct design is a latest-only native window snapshot path plus a render-thread presentation controller:

- native window size can advance immediately,
- Vulkan swapchain presentation catches up to the current Win32 surface extent or skips that tick,
- the pipeline's cheap output layer resizes/scales for in-between frames,
- full internal FBO generations rebuild only under a controlled catch-up policy.

For XRENGINE, the correct split is:

- app or editor thread for orchestration and non-GPU logic,
- update, collect-visible, and fixed-update threads as they already exist,
- a render thread that owns graphics context state, swapchain state, render pipeline state, and present,
- a backend-compliant lightweight window pump path for native message processing when smooth Win32 resize is enabled.

That matches the current architecture better than the current timer workaround and gives the cleanest path to a v1-quality threading model with smooth native window resizing.

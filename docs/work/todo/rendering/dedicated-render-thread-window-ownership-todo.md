# Dedicated Render Thread Window Ownership TODO

Last Updated: 2026-06-23
Owner: Rendering
Status: In progress. SDL/Vulkan split window-pump and dedicated render-thread
prototype are wired; startup attachment, async window mailbox posting, input
snapshots, and full-internal resize catch-up policy are implemented. Raw Win32
ownership, swapchain frame-slot retirement, direct input-consumer migration, and
full editor/XR smoke remain pending.
Source Design: [Dedicated Render Thread Window Ownership Plan](../../design/rendering/dedicated-render-thread-window-ownership-plan.md)
Target Branch: `feature/dedicated-render-thread-window-ownership`

## Objective

Move engine-owned desktop rendering off the startup/editor thread and onto a
strict render ownership model, while keeping native window event pumping,
graphics context ownership, swapchain presentation, input publication, and
interactive resize behavior explicit.

The end state should support:

- a dedicated render thread that owns graphics context state, renderer state,
  swapchain state, render-pipeline state, and present;
- backend-compliant native window event pumping;
- snapshot-based input, focus, close, and resize publication;
- Vulkan live native resize where the Win32 client rectangle stays responsive
  while presentation/output/internal extents catch up under budget;
- OpenGL and XR paths that remain correct even when they cannot initially use
  the full smooth-resize split.

## Current Implementation Notes

- `XRE_WINDOW_PUMP_HOST=sdl-prototype` enables the experimental split native
  event pump.
- The prototype is gated to Windows, Vulkan, and startup windows resolving
  `EInteractiveWindowResizeStrategy.SdlBackend` through startup settings,
  editor preferences, or `XRE_INTERACTIVE_RESIZE_STRATEGY=sdl`.
- In prototype mode, SDL-backed `XRWindow` creation runs synchronously on the
  pump thread. The render loop still enters through `EngineRenderThreadHost`,
  and logs `SplitWindowPumpPrototype` while the native pump is active.
- Externally pumped windows no longer call `Window.DoEvents()` from
  `XRWindow.RenderFrame()` or `XRWindow.EndTick()`. The pump thread publishes
  latest-overwrite surface snapshots after event processing.
- Vulkan swapchain recreation no longer pumps native events while waiting for a
  non-zero surface. It defers recreation while the latest surface snapshot is
  minimized or zero-sized, leaving the frame path to skip/coalesce.
- Close/dispose is split for externally pumped windows: renderer teardown runs
  on the render thread, native input/window teardown runs on the window pump
  thread, and engine window removal is queued back to the render thread.
- Shutdown now flushes the window-thread mailbox before completing the pump
  queue, and externally pumped windows execute render-resource teardown inline
  when disposal is already on the render thread.
- `XRViewport.SetPresentationOutputExtent(...)` is the cheap live-resize path;
  `XRViewport.SetFullInternalExtent(...)` remains the full internal resize path.
- The ordinary collapsed GLFW/OpenGL/Vulkan path remains the default. Vulkan
  fallback to OpenGL is preserved there, but disabled in the split prototype so
  ownership assumptions do not change silently.

## Hard Constraints

- Do not treat "main thread" and "render thread" as interchangeable terms in
  new code.
- Do not move GLFW/Silk.NET window creation, event pumping, or callbacks to an
  arbitrary worker thread. Keep GLFW-compliant window/event APIs on the process
  main thread unless a backend explicitly proves another thread is legal.
- Do not block the native window pump on GPU waits, full render-pipeline
  resource generation, shader compilation, or swapchain teardown.
- Do not use a stale Win32 Vulkan swapchain extent as the normal resize-lag
  layer. Without validated present scaling, the swapchain catches up to the
  current surface extent or the present tick is skipped/coalesced.
- Keep lag in `PipelineOutputExtent` and `FullInternalExtent`, not in the
  Win32 Vulkan surface contract.
- Keep the process entry thread STA-capable until editor services such as file
  dialogs, clipboard, shell integration, and drag-and-drop have explicit
  thread-affinity adapters.
- Avoid per-frame hot-path allocations in new render, resize, and snapshot
  code.

## Phase 0 - Branch, Baseline Audit, And Invariants

- [x] Create a dedicated branch for this todo before starting implementation
  work.
- [x] Inventory every current call site that assigns or reads
  `RenderThreadId`, especially `Engine.Initialize(...)`,
  `Engine.Run(...)`, `EngineTimer.WaitToRender()`, and render-thread task
  dispatch.
- [x] Inventory current direct uses of `IWindow`, `GLContext`, renderer,
  swapchain, present, and `Window.DoEvents()` outside render/window-owned
  code.
- [x] Inventory editor/platform services that may require STA or process-entry
  affinity: dialogs, clipboard, shell integration, drag-and-drop, profiler UI,
  and native UI bootstrap.
- [x] Inventory backend rules for active windowing paths: Silk.NET GLFW,
  Silk.NET SDL, raw Win32 feasibility, OpenGL context transfer, Vulkan WSI,
  OpenXR, and OpenVR.
- [x] Add or tighten diagnostics/assertions for wrong-thread access to
  renderer, swapchain, context, and native window pump operations.
- [x] Mark `EnqueueMainThreadTask(...)` and related names as compatibility
  terminology in comments/docs where touched.
- [x] Record the initial thread ownership map in the todo or a linked work note.

## Phase 1 - Backend Thread-Affinity Gates

- [x] Add explicit backend capability flags for native window pump ownership,
  render-context transfer, and separate render/present ownership.
- [x] Add a GLFW-compliant mode where GLFW initialization, window creation,
  callbacks, and event processing remain on the process main thread.
- [x] Gate any dedicated `WindowPumpHost` path behind a backend capability check.
- [x] Prefer raw Win32 as the Windows-first prototype target for true dedicated
  window-pump ownership.
- [x] Treat SDL as experimental until video initialization, event pumping, and
  Silk.NET wrapper behavior are validated on the intended thread.
- [x] Add startup diagnostics that log selected backend, window thread,
  render thread, context owner, and whether smooth resize split is available.

## Phase 2 - RenderThreadHost Bootstrap

- [x] Introduce `RenderThreadHost` or equivalent runtime service.
- [x] Move `BlockForRendering()` ownership into the render-thread host.
- [x] Assign `RenderThreadId` from inside the dedicated render thread.
- [x] Keep startup window creation on the backend-required window thread.
- [x] Add a startup barrier that waits for native windows and render attachment
  before `Engine.Initialize(...)` continues.
- [x] Preserve the existing `CollectVisible -> SwapBuffers -> Render` handoff.
- [x] Ensure server/headless paths do not spawn visible-window render threads
  unnecessarily.
- [x] Add clean shutdown sequencing for last-window-close, app shutdown, and
  renderer teardown.
  - Externally pumped `XRWindow` close/dispose now performs render-thread GPU
    teardown and window-thread native teardown before removal. Shutdown flushes
    queued native teardown work before the pump queue is completed.

## Phase 3 - Window And Render Mailboxes

- [x] Add a window-thread mailbox for native window operations:
  create/destroy, title, size, focus, minimize, restore, close, cursor, and raw
  input configuration.
  - `IRuntimeRenderingHostServices.EnqueueWindowThreadTask` and
    `InvokeWindowThreadTask<T>` route native work through `EngineWindowPumpHost`
    when active; callers still need to migrate remaining direct mutations.
- [x] Add or formalize a render-thread mailbox for GPU-affine operations:
  renderer mutations, render-state rechecks, VSync/HDR present state,
  scene-panel resources, screenshot/readback, and swapchain-sensitive work.
  - `Engine.EnqueueRenderThreadTask`, `InvokeOnRenderThread`, and runtime host
    service bridges are the formal render mailbox. Remaining work is caller
    migration and more specific sync tests.
- [ ] Replace app/editor direct native window mutations with mailbox calls.
- [ ] Replace app/editor direct renderer or swapchain mutations with mailbox
  calls.
- [x] Support request/reply operations where startup and shutdown need ordering.
- [x] Add mailbox diagnostics for queue depth, wait time, wrong-thread calls,
  blocked synchronous waits, flushes, flush timeouts, shutdown drains, and
  stopping state.
- [x] Avoid nested blocking waits from native callbacks into render shutdown.

## Phase 4 - XRWindow Ownership Split

- [x] Split `XRWindow` responsibilities into native window ownership,
  render/present ownership, input snapshot publication, and viewport/pipeline
  presentation state.
- [x] Make render-thread-only methods explicit by name, visibility, or
  assertions.
- [x] Expose app/editor-safe state as immutable snapshots rather than direct
  `IWindow`, renderer, or Silk input objects.
- [ ] Audit viewport subscription and render-pipeline state so those remain
  render-thread-affine.
- [ ] Keep `XRBase` property mutation paths on `SetField(...)` when touching
  existing stateful types.
- [ ] Remove or quarantine public escape hatches that expose raw backend objects
  outside the owning thread.
  - Partial: `IRuntimeLocalPlayerViewport` now exposes `WindowInputSnapshot`
    directly and quarantines the remaining device binding path behind
    `GetThreadAffinedDeviceSourceForBinding()`.

## Phase 5 - WindowSurfaceSnapshot Bridge

- [x] Add an immutable `WindowSurfaceSnapshot` carrying sequence, client extent,
  DPI scale, focus, minimized state, interactive-resize state, and timestamp.
- [x] Publish snapshots as overwrite-latest rather than queue-all.
- [x] Use monotonically increasing sequence numbers so render can drop stale
  native sizes.
- [x] Publish focus, close, minimize/restore, and input-relevant state through
  the same snapshot family or a clearly paired input snapshot path.
- [x] Ensure window snapshot publication does not allocate in the resize hot
  path.
- [x] Add diagnostics for latest published sequence, latest consumed sequence,
  dropped snapshot count, and publication thread.

## Phase 6 - WindowResizeController

- [x] Add `WindowResizeController` or equivalent render-thread-owned state.
- [x] Track `NativeClientExtent`, `PresentationExtent`,
  `PipelineOutputExtent`, and `FullInternalExtent` separately.
- [x] Consume at most the newest `WindowSurfaceSnapshot` per render tick during
  interactive resize.
- [x] Drop older snapshots and report the dropped count.
- [x] Route presentation catch-up, cheap output resize, and full internal
  generation through separate request paths.
- [x] Ensure `NativeClientExtent` changes update camera/display aspect metadata
  without forcing full internal graph regeneration.
- [x] Add editor Engine State fields for all four extents and current resize
  mode.

## Phase 7 - Vulkan Presentation Catch-Up

- [x] Update Vulkan resize/acquire/present code to consume latest native extent
  snapshots instead of calling `Window.DoEvents()` from render resize paths.
- [x] Recreate the Win32 Vulkan swapchain to the current surface extent when a
  normal present is attempted without present scaling.
- [x] Skip or coalesce present ticks when presentation catch-up is busy, the
  window is minimized, or the current extent is `(0, 0)`.
- [x] Use bounded or non-blocking waits during interactive resize.
- [x] Handle `VK_ERROR_OUT_OF_DATE_KHR`, `VK_SUBOPTIMAL_KHR`, and minimized
  extents explicitly.
- [ ] Retire old swapchain images and dependent resources through existing
  frame-slot synchronization.
- [x] Log presentation recreate time, result, skipped-present reason, and
  native/presentation extent divergence.
- [ ] Gate any future mismatched-swapchain present path behind validated
  `VkSwapchainPresentScalingCreateInfoKHR` support.

## Phase 8 - Cheap Pipeline Output Resize Path

- [x] Add a cheap presentation/output extent API, for example
  `XRViewport.SetPresentationOutputExtent(...)`.
- [x] Keep the existing full internal-resolution resize path separate, for
  example `XRViewport.SetInternalResolution(...)`.
- [x] Limit live-resize fast-path changes to present-chain/output resources.
- [ ] Support dynamic viewport/scissor, UV scale, final fullscreen copy/resolve,
  crop, letterbox, or pillarbox modes as needed.
- [x] Ensure GBuffer, depth/stencil, velocity, transform ID, lighting
  accumulation, AO, bloom, shadows, volumetrics, and broad temporal histories do
  not rebuild on every drag tick.
- [x] Add output-scale metadata and diagnostics: exact, upscale, downscale,
  crop, letterbox.
- [ ] Validate Vulkan final blit/composite regions clamp to live source and
  destination extents.
- [x] Keep OpenGL on baseline behavior until context/window ownership is proven
  safe for the split.

## Phase 9 - Full Internal Generation Catch-Up

- [ ] Reuse the render-pipeline resource lifecycle generation model for
  `FullInternalExtent`.
- [x] During live resize, keep the current committed full internal generation
  active by default.
- [x] Request pending full internal generations only on threshold crossing,
  pause, maximum lag timer, or resize-settled/mouse-up.
- [x] Commit the exact final internal extent after `WM_EXITSIZEMOVE` or an
  equivalent settled signal.
- [x] Add initial policy constants for live generation rate, hard lag elapsed
  time, and area-ratio limits.
- [ ] Build pending generation work incrementally where possible and commit
  atomically after resources and FBOs validate.
- [x] Add tests for resize request coalescing, generation ordering, and stale
  generation rejection.

## Phase 10 - Input And Event Snapshots

- [x] Add `WindowInputSnapshot` or equivalent immutable per-window/per-frame
  input state.
- [x] Publish key, mouse, text input, focus, pointer delta, scroll, and capture
  state from the owning window thread.
- [x] Define the publication point relative to update, fixed update, and pause
  stepping.
- [ ] Replace gameplay/editor direct use of thread-affine Silk input objects
  with snapshot consumption.
  - Partial: public viewport contracts now expose `WindowInputSnapshot`; the
    current `LocalInputInterface` still binds to Silk devices through an
    explicitly named thread-affine transitional hook.
- [ ] Add tests for focus transitions, key transitions, pointer deltas, scroll,
  and text input ordering.
  - Partial prototype: source-contract tests now assert that `XRWindow`
    subscribes key, mouse, text, pointer, and scroll events into
    `WindowInputSnapshot`; live transition tests still need a synthetic window
    or backend test harness.

## Phase 11 - Editor And Tool Integration

- [ ] Verify ImGui overlay submission after the entry thread stops being the
  render thread.
- [ ] Verify scene panel and full-window viewport presentation after the
  ownership split.
- [ ] Audit editor camera controls, selection, gizmos, drag-and-drop, clipboard,
  and common dialogs.
- [ ] Keep the process entry thread STA-capable until each STA-sensitive service
  has a documented adapter.
- [ ] Verify profiler overlays and render diagnostics still appear in the
  editor.
- [x] Update editor Engine State UI with render/window thread IDs, backend
  ownership mode, resize extents, and skipped-present diagnostics.

## Phase 12 - XR And Multi-Window Hardening

- [ ] Verify OpenXR context-affine startup still runs on the correct render
  owner.
- [ ] Verify OpenVR stereo and mirror swap paths keep their current semantics.
- [ ] Verify VR-disabled desktop rendering remains the baseline path.
- [ ] Verify multiple startup windows render, resize, minimize, restore, and
  close cleanly.
- [ ] Verify hidden utility windows either use the graphics-backed render host
  or remain pure OS utility windows with no graphics lifetime coupling.
- [ ] Audit helper/shared GL contexts and any primary-context restoration logic.

## Phase 13 - Cleanup And Documentation

- [ ] Remove dead startup-thread render-loop code after the new host is stable.
- [ ] Rename or document remaining `MainThread` APIs that actually target the
  render thread.
- [ ] Remove transitional compatibility shims that are no longer needed before
  v1.
- [ ] Update stable docs if behavior lands:
  - `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`
  - `docs/architecture/rendering/render-pipeline-resource-lifecycle.md`
  - `docs/architecture/rendering/default-render-pipeline-notes.md`
  - `docs/architecture/rendering/window-creation-and-renderer-init.md` if it
    exists or is added during the work.
- [ ] Update VS Code launch/tasks docs only if workflow or launch flags change.
- [ ] Merge the dedicated branch back into `main` after implementation,
  validation, and documentation are complete.

## Implementation Notes

- Created branch `feature/dedicated-render-thread-window-ownership`.
- Process-entry/STA affinity inventory:
  - The day-to-day ImGui editor uses ImGui file/folder browser dialogs,
    ImGui clipboard APIs, and ImGui drag/drop payload plumbing. These remain
    tied to the editor/window input path and are safe only while the entry
    thread stays STA-capable.
  - The profiler path is primarily UDP/external-profiler plus in-editor ImGui
    panels; it should not require native modal UI affinity, but its overlays
    still need render-thread validation.
  - Native UI bootstrap and native drag/drop remain the main unvalidated
    process-entry-affine areas.
- Added runtime window ownership primitives:
  `RuntimeWindowBackendOwnershipInfo`, `WindowSurfaceSnapshot`,
  `WindowResizeExtents`, and `WindowResizeController`.
- Extended `IRuntimeRenderWindowHost` so runtime rendering code can see
  native window thread, render-owner thread, backend ownership, latest surface
  snapshot, and resize extents.
- `XRWindow` now records native window thread and render-owner thread IDs,
  resolves backend ownership capabilities after native window initialization,
  publishes latest-only surface snapshots during resize paths, tracks all four
  resize extents, and warns on wrong-thread native window/render ownership use.
- Added `EngineRenderThreadHost` as the render-loop lifecycle owner. It keeps
  today's collapsed window/render mode and starts a dedicated render thread for
  the SDL/Vulkan split prototype, stamping `RenderThreadId` inside that thread.
- Added `EngineWindowPumpHost` as an opt-in SDL/Vulkan prototype window pump,
  including synchronous request/reply calls and mailbox diagnostics.
- Added paired `WindowEventSnapshot` and `WindowInputSnapshot` publication for
  focus, minimize, close/dispose, and input-device availability.
- Extended `WindowInputSnapshot` with focused/captured state, pointer position,
  per-publication pointer delta, per-publication scroll delta, and cumulative
  key/mouse/text transition counters. `XRWindow` publishes these from Silk input
  events on the owning window thread and unsubscribes during both collapsed and
  split-pump disposal.
- `EngineWindowPumpHost.EnqueueWindowTask(...)` now posts void window-thread
  work asynchronously. Blocking request/reply is limited to
  `InvokeWindowTask<T>(...)` and startup window creation.
- `EngineWindowPumpHost.Stop()` now flushes queued window-thread work before
  completing the queue, records flush/timeout/drain diagnostics, and exposes
  stopping state in `WindowMailboxDiagnostics`.
- Externally pumped `XRWindow.Dispose()` executes render-resource teardown
  immediately when already on the render thread, then queues native input/window
  teardown to the owning pump thread.
- `Engine.CreateWindows(...)` now waits on an explicit startup attachment
  barrier before `Engine.Initialize(...)` continues.
- The render frame consumes at most the newest native surface snapshot and
  updates presentation/output extent without forcing full internal generation.
  Already-applied snapshots are consumed for diagnostics but do not queue a
  self-triggered full framebuffer resize.
- `WindowResizeController` now tracks pending and committed full-internal
  generations, output-scale mode, and initial catch-up policy constants. During
  live resize, native snapshots update cheap presentation/output extents first;
  full-internal resize work is coalesced until the policy accepts a generation
  or the resize settles.
- Vulkan swapchain recreation now defers on minimized or zero-sized snapshots
  instead of calling `Window.DoEvents()` from the swapchain path.
- Vulkan present/resize logs now include skipped-present reason, recreate
  elapsed time/result, live/swapchain divergence, and current
  native/presentation/output/internal extents.
- Editor Engine State now shows window/render thread IDs, backend capability
  flags, resize extents, pending full-internal generation, output-scale
  diagnostics, snapshot sequence diagnostics, event/input snapshots, mailbox
  diagnostics including shutdown flush state, input deltas/counters, and
  publication thread.
- `IRuntimeLocalPlayerViewport` now exposes `WindowInputSnapshot` and no longer
  exposes a generic raw input-context property. The remaining Silk device
  binding is named `GetThreadAffinedDeviceSourceForBinding()` to keep the
  transitional thread-affine dependency visible.
- Validation on 2026-06-23:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` passed with 0
    warnings and 0 errors.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter
    "FullyQualifiedName~WindowResizeControllerTests"` passed 7/7.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter
    "FullyQualifiedName~RuntimeRenderingHostServicesTests"` passed 10/10.
  - One earlier parallel verification attempt hit a transient compiler file
    lock because editor build and test build wrote the same `obj` output
    concurrently; rerunning the test alone passed.
- Validation on 2026-06-23 after mailbox/input/full-internal catch-up pass:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
    passed with 0 warnings and 0 errors.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter
    "FullyQualifiedName~WindowResizeControllerTests|FullyQualifiedName~WindowOwnershipContractTests"
    --no-restore` passed 14/14.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter
    "FullyQualifiedName~ImportedTextureStreamingContractTests|FullyQualifiedName~SkyboxAmbientContractTests"
    --no-restore --no-build` passed 24/24.
- Validation on 2026-06-23 after shutdown flush and input-contract quarantine:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter
    "FullyQualifiedName~WindowResizeControllerTests|FullyQualifiedName~WindowOwnershipContractTests"
    --no-restore` passed 17/17.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
    passed with 0 warnings and 0 errors.
  - `dotnet build .\XRENGINE.slnx --no-restore` passed with 0 errors and
    32 existing warnings from `XREngine.Benchmarks` Magick.NET vulnerability
    advisories/version conflicts. Server and VRClient projects built as part of
    this solution build.

## Validation Checklist

### Builds

- [ ] `dotnet restore`
- [x] `dotnet build XRENGINE.slnx`
- [x] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [x] Build Server via task or project build.
- [x] Build VRClient via task or project build.

### Focused Tests

- [ ] Render-thread host startup/shutdown tests.
- [x] Window command-queue tests.
- [x] Latest-only window snapshot coalescing tests.
- [x] Resize extent state-machine tests.
- [x] Present-chain resize versus full internal generation tests.
- [ ] Input snapshot publication and ordering tests.
  - Partial: `WindowOwnershipContractTests` covers snapshot event
    publication wiring; live ordering still needs a backend/synthetic-window
    harness.
- [x] Runtime rendering host service routing tests.
- [ ] Targeted window-close and framebuffer-resize tests.
- [ ] Existing Vulkan P1/regression tests relevant to swapchain resize.

### Manual Smoke

- [ ] Start editor in the default world.
- [ ] Start editor with `--unit-testing`.
- [ ] Start editor with OpenGL and verify render, input, resize, minimize,
  restore, and close.
- [ ] Start editor with Vulkan and verify render, input, resize, minimize,
  restore, and close.
- [ ] Drag native resize borders continuously and verify the native rectangle
  stays responsive.
- [ ] Verify Vulkan live resize either presents refreshed lagging
  internal/output content or skips ticks with clear diagnostics.
- [ ] Verify final exact-size internal graph catches up after mouse-up.
- [ ] Verify scene-panel mode and full-window mode.
- [ ] Verify VSync and HDR preference changes.
- [ ] Verify screenshot or readback flows.
- [ ] Verify multiple windows render and close cleanly.

### XR Smoke

- [ ] OpenXR desktop mirror path.
- [ ] OpenXR context-affine startup.
- [ ] OpenVR stereo and mirror swap paths.

## Done Criteria

- [ ] The startup/editor thread no longer blocks in the render loop for visible
  desktop rendering.
- [ ] `RenderThreadId` is assigned by the dedicated render thread.
- [ ] Backend-required native window event pumping has an explicit owner.
- [ ] GLFW-compliant mode keeps GLFW window/event APIs on the process main
  thread unless the backend is replaced.
- [ ] Renderer, swapchain, graphics context, and present operations are
  render-thread-owned.
- [ ] App/editor systems consume window/input state through snapshots or
  mailbox replies, not direct backend objects.
- [ ] Vulkan Win32 interactive resize does not use stale swapchain extent as
  the default lag mechanism.
- [ ] Full internal render-pipeline resources are not regenerated for every
  intermediate drag size.
- [ ] Diagnostics make ownership and resize state visible enough to debug.
- [ ] Targeted tests and manual validation pass, or remaining failures are
  documented with owner-approved follow-up tasks.

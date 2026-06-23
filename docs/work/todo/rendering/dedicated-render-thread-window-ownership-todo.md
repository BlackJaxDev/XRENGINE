# Dedicated Render Thread Window Ownership TODO

Last Updated: 2026-06-23
Owner: Rendering
Status: Draft. Derived from the dedicated render-thread/window-ownership design doc.
Source Design: [Dedicated Render Thread Window Ownership Plan](../../design/rendering/dedicated-render-thread-window-ownership-plan.md)
Target Branch: create during Phase 0.

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

- [ ] Create a dedicated branch for this todo before starting implementation
  work.
- [ ] Inventory every current call site that assigns or reads
  `RenderThreadId`, especially `Engine.Initialize(...)`,
  `Engine.Run(...)`, `EngineTimer.WaitToRender()`, and render-thread task
  dispatch.
- [ ] Inventory current direct uses of `IWindow`, `GLContext`, renderer,
  swapchain, present, and `Window.DoEvents()` outside render/window-owned
  code.
- [ ] Inventory editor/platform services that may require STA or process-entry
  affinity: dialogs, clipboard, shell integration, drag-and-drop, profiler UI,
  and native UI bootstrap.
- [ ] Inventory backend rules for active windowing paths: Silk.NET GLFW,
  Silk.NET SDL, raw Win32 feasibility, OpenGL context transfer, Vulkan WSI,
  OpenXR, and OpenVR.
- [ ] Add or tighten diagnostics/assertions for wrong-thread access to
  renderer, swapchain, context, and native window pump operations.
- [ ] Mark `EnqueueMainThreadTask(...)` and related names as compatibility
  terminology in comments/docs where touched.
- [ ] Record the initial thread ownership map in the todo or a linked work note.

## Phase 1 - Backend Thread-Affinity Gates

- [ ] Add explicit backend capability flags for native window pump ownership,
  render-context transfer, and separate render/present ownership.
- [ ] Add a GLFW-compliant mode where GLFW initialization, window creation,
  callbacks, and event processing remain on the process main thread.
- [ ] Gate any dedicated `WindowPumpHost` path behind a backend capability check.
- [ ] Prefer raw Win32 as the Windows-first prototype target for true dedicated
  window-pump ownership.
- [ ] Treat SDL as experimental until video initialization, event pumping, and
  Silk.NET wrapper behavior are validated on the intended thread.
- [ ] Add startup diagnostics that log selected backend, window thread,
  render thread, context owner, and whether smooth resize split is available.

## Phase 2 - RenderThreadHost Bootstrap

- [ ] Introduce `RenderThreadHost` or equivalent runtime service.
- [ ] Move `BlockForRendering()` ownership into the render-thread host.
- [ ] Assign `RenderThreadId` from inside the dedicated render thread.
- [ ] Keep startup window creation on the backend-required window thread.
- [ ] Add a startup barrier that waits for native windows and render attachment
  before `Engine.Initialize(...)` continues.
- [ ] Preserve the existing `CollectVisible -> SwapBuffers -> Render` handoff.
- [ ] Ensure server/headless paths do not spawn visible-window render threads
  unnecessarily.
- [ ] Add clean shutdown sequencing for last-window-close, app shutdown, and
  renderer teardown.

## Phase 3 - Window And Render Mailboxes

- [ ] Add a window-thread mailbox for native window operations:
  create/destroy, title, size, focus, minimize, restore, close, cursor, and raw
  input configuration.
- [ ] Add or formalize a render-thread mailbox for GPU-affine operations:
  renderer mutations, render-state rechecks, VSync/HDR present state,
  scene-panel resources, screenshot/readback, and swapchain-sensitive work.
- [ ] Replace app/editor direct native window mutations with mailbox calls.
- [ ] Replace app/editor direct renderer or swapchain mutations with mailbox
  calls.
- [ ] Support request/reply operations where startup and shutdown need ordering.
- [ ] Add mailbox diagnostics for queue depth, wait time, wrong-thread calls,
  and blocked synchronous waits.
- [ ] Avoid nested blocking waits from native callbacks into render shutdown.

## Phase 4 - XRWindow Ownership Split

- [ ] Split `XRWindow` responsibilities into native window ownership,
  render/present ownership, input snapshot publication, and viewport/pipeline
  presentation state.
- [ ] Make render-thread-only methods explicit by name, visibility, or
  assertions.
- [ ] Expose app/editor-safe state as immutable snapshots rather than direct
  `IWindow`, renderer, or Silk input objects.
- [ ] Audit viewport subscription and render-pipeline state so those remain
  render-thread-affine.
- [ ] Keep `XRBase` property mutation paths on `SetField(...)` when touching
  existing stateful types.
- [ ] Remove or quarantine public escape hatches that expose raw backend objects
  outside the owning thread.

## Phase 5 - WindowSurfaceSnapshot Bridge

- [ ] Add an immutable `WindowSurfaceSnapshot` carrying sequence, client extent,
  DPI scale, focus, minimized state, interactive-resize state, and timestamp.
- [ ] Publish snapshots as overwrite-latest rather than queue-all.
- [ ] Use monotonically increasing sequence numbers so render can drop stale
  native sizes.
- [ ] Publish focus, close, minimize/restore, and input-relevant state through
  the same snapshot family or a clearly paired input snapshot path.
- [ ] Ensure window snapshot publication does not allocate in the resize hot
  path.
- [ ] Add diagnostics for latest published sequence, latest consumed sequence,
  dropped snapshot count, and publication thread.

## Phase 6 - WindowResizeController

- [ ] Add `WindowResizeController` or equivalent render-thread-owned state.
- [ ] Track `NativeClientExtent`, `PresentationExtent`,
  `PipelineOutputExtent`, and `FullInternalExtent` separately.
- [ ] Consume at most the newest `WindowSurfaceSnapshot` per render tick during
  interactive resize.
- [ ] Drop older snapshots and report the dropped count.
- [ ] Route presentation catch-up, cheap output resize, and full internal
  generation through separate request paths.
- [ ] Ensure `NativeClientExtent` changes update camera/display aspect metadata
  without forcing full internal graph regeneration.
- [ ] Add editor Engine State fields for all four extents and current resize
  mode.

## Phase 7 - Vulkan Presentation Catch-Up

- [ ] Update Vulkan resize/acquire/present code to consume latest native extent
  snapshots instead of calling `Window.DoEvents()` from render resize paths.
- [ ] Recreate the Win32 Vulkan swapchain to the current surface extent when a
  normal present is attempted without present scaling.
- [ ] Skip or coalesce present ticks when presentation catch-up is busy, the
  window is minimized, or the current extent is `(0, 0)`.
- [ ] Use bounded or non-blocking waits during interactive resize.
- [ ] Handle `VK_ERROR_OUT_OF_DATE_KHR`, `VK_SUBOPTIMAL_KHR`, and minimized
  extents explicitly.
- [ ] Retire old swapchain images and dependent resources through existing
  frame-slot synchronization.
- [ ] Log presentation recreate time, result, skipped-present reason, and
  native/presentation extent divergence.
- [ ] Gate any future mismatched-swapchain present path behind validated
  `VkSwapchainPresentScalingCreateInfoKHR` support.

## Phase 8 - Cheap Pipeline Output Resize Path

- [ ] Add a cheap presentation/output extent API, for example
  `XRViewport.SetPresentationOutputExtent(...)`.
- [ ] Keep the existing full internal-resolution resize path separate, for
  example `XRViewport.SetInternalResolution(...)`.
- [ ] Limit live-resize fast-path changes to present-chain/output resources.
- [ ] Support dynamic viewport/scissor, UV scale, final fullscreen copy/resolve,
  crop, letterbox, or pillarbox modes as needed.
- [ ] Ensure GBuffer, depth/stencil, velocity, transform ID, lighting
  accumulation, AO, bloom, shadows, volumetrics, and broad temporal histories do
  not rebuild on every drag tick.
- [ ] Add output-scale metadata and diagnostics: exact, upscale, downscale,
  crop, letterbox.
- [ ] Validate Vulkan final blit/composite regions clamp to live source and
  destination extents.
- [ ] Keep OpenGL on baseline behavior until context/window ownership is proven
  safe for the split.

## Phase 9 - Full Internal Generation Catch-Up

- [ ] Reuse the render-pipeline resource lifecycle generation model for
  `FullInternalExtent`.
- [ ] During live resize, keep the current committed full internal generation
  active by default.
- [ ] Request pending full internal generations only on threshold crossing,
  pause, maximum lag timer, or resize-settled/mouse-up.
- [ ] Commit the exact final internal extent after `WM_EXITSIZEMOVE` or an
  equivalent settled signal.
- [ ] Add initial policy constants for live generation rate, hard lag elapsed
  time, and area-ratio limits.
- [ ] Build pending generation work incrementally where possible and commit
  atomically after resources and FBOs validate.
- [ ] Add tests for resize request coalescing, generation ordering, and stale
  generation rejection.

## Phase 10 - Input And Event Snapshots

- [ ] Add `WindowInputSnapshot` or equivalent immutable per-window/per-frame
  input state.
- [ ] Publish key, mouse, text input, focus, pointer delta, scroll, and capture
  state from the owning window thread.
- [ ] Define the publication point relative to update, fixed update, and pause
  stepping.
- [ ] Replace gameplay/editor direct use of thread-affine Silk input objects
  with snapshot consumption.
- [ ] Add tests for focus transitions, key transitions, pointer deltas, scroll,
  and text input ordering.

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
- [ ] Update editor Engine State UI with render/window thread IDs, backend
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

## Validation Checklist

### Builds

- [ ] `dotnet restore`
- [ ] `dotnet build XRENGINE.slnx`
- [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [ ] Build Server via task or project build.
- [ ] Build VRClient via task or project build.

### Focused Tests

- [ ] Render-thread host startup/shutdown tests.
- [ ] Window command-queue tests.
- [ ] Latest-only window snapshot coalescing tests.
- [ ] Resize extent state-machine tests.
- [ ] Present-chain resize versus full internal generation tests.
- [ ] Input snapshot publication and ordering tests.
- [ ] Runtime rendering host service routing tests.
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

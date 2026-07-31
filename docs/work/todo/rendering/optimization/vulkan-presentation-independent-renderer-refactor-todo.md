# Vulkan Presentation-Independent Renderer Refactor TODO

Status: Active  
Scope: Refactor the production Vulkan renderer so the existing XRENGINE render
graph can execute against desktop WSI, presentationless, headless WSI, and
OpenXR targets without constructing a synthetic `XRWindow`.

Related work:

- [Vulkan Headless MCP Component Profiling TODO](vulkan-headless-mcp-component-profiling-todo.md)
- [Vulkan renderer architecture](../../../../architecture/rendering/vulkan-renderer.md)
- [Default render-pipeline notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
- [Mesh-submission strategies](../../../../architecture/rendering/mesh-submission-strategies.md)

## Goal

Make `VulkanRenderer` a target-first production renderer rather than a
window-first renderer.

The same renderer implementation, allocator, resource wrappers, render graph,
command-recording system, synchronization backend, retirement queues, and
profiling instrumentation must run in every supported execution mode. Only the
final-output acquisition and completion policy should vary by target.

Completing this document closes the remaining Phase 1.2 item:

> Run the normal render graph and Vulkan command recording against
> presentationless outputs.

## Why This Is A Refactor

The Phase 1 bootstrap hosts proved that Vulkan can create targets and submit
deterministic work without a window. Phase 3 removed those standalone
implementations after moving their final-output resources and policies into
the production `VulkanRenderer` target drivers.

The production path still has these structural assumptions:

- `AbstractRenderer` is constructed with an `XRWindow`.
- `VulkanRenderer` derives from that window-oriented base contract.
- Vulkan instance extensions, surface creation, swapchain creation, resize
  policy, frame acquisition, frame submission, and presentation are interleaved
  across the renderer.
- Render-pipeline frame context obtains output dimensions, viewports, and
  lifecycle state through window-owned objects.
- Direct `XRWindow` access remains in Vulkan bootstrap, swapchain, frame-loop,
  ImGui, diagnostics, resource-planner, and optional-feature code.

A callback adapter alone would create a second renderer implementation and
would not exercise the production allocator, descriptor caches, command
recording, retirement, or render graph. This work must instead remove the
window assumption from the production renderer.

## Non-Goals

- Do not implement the benchmark executable, MCP profiling recipes, or fixture
  catalog tracked by later phases of the headless profiling TODO.
- Do not make ImGui available in presentationless mode.
- Do not emulate a desktop window or create an invisible `XRWindow`.
- Do not silently substitute presentationless execution when headless WSI was
  explicitly requested.
- Do not rewrite the render graph, command-chain architecture, or resource
  allocator unless a focused change is required to remove target coupling.
- Do not require identical final bytes across formats or color spaces that are
  intentionally different.

## Architectural Invariants

- A presentationless renderer creates no native application window,
  `VkSurfaceKHR`, swapchain, acquire operation, or present operation.
- Desktop WSI retains its existing window, resize, surface-loss, swapchain, and
  compositor-presentation behavior.
- Headless WSI uses `VK_EXT_headless_surface` only when supported and retains
  real acquire/present semantics.
- OpenXR remains runtime-owned where required by the active OpenXR Vulkan
  binding and must not be forced through desktop swapchain policy.
- Renderer module generation and API-wrapper ownership remain unchanged.
- Every frame owns an explicit target lease containing output images, views,
  extent, layouts, synchronization dependencies, and completion policy.
- The production render graph records the same passes before the final-output
  boundary in each execution mode.
- Presentationless steady state performs no managed allocation, shader
  compilation, resource creation, synchronous readback, or device-wide wait.
- GPU resources are retired through the production lifetime system. Target
  changes must not introduce immediate destruction of in-flight resources.
- Current-frame readback remains forbidden in zero-readback recipes.
- Device loss, initialization failure, and unsupported target capabilities
  produce explicit diagnostics.

## Proposed Ownership Model

### Stable renderer host context

Introduce a target-first context used by `AbstractRenderer` and backend
renderers. The final type name may change, but it must provide:

- The required `IRendererPresentationTarget`.
- `RenderExecutionMode`.
- Optional desktop-window services, available only for
  `DesktopWindowRenderTarget`.
- Fixed output properties for non-window targets.
- Module generation and renderer-to-host lifecycle ownership.
- Explicit accessors that fail clearly when window-only services are requested
  in a non-window mode.

Do not make dozens of existing window properties nullable and rely on
call-site discipline. Window-only behavior should be accessed through a
capability or mode-specific service.

### Vulkan target driver

Extract final-output behavior behind a Vulkan-owned target-driver contract.
The contract should cover:

- Instance extensions required by the target.
- Surface creation, if any.
- Present-capable queue requirements, if any.
- Device extensions required by the target.
- Target images, image views, formats, extent, layers, and sample policy.
- Per-frame target acquisition.
- Submission wait/signal dependencies.
- Required initial and final image layouts.
- Frame completion or presentation.
- Resize, out-of-date, surface-loss, and target recreation policy.
- Target-generation retirement and teardown.
- Optional post-measurement output readback.

Expected implementations:

- Desktop swapchain target driver.
- Presentationless image-ring target driver.
- Headless-WSI swapchain target driver.
- OpenXR target adapter or an explicit bridge to the existing OpenXR
  swapchain-image flow.

### Frame target lease

Use a value-type frame lease with enough data for render-graph recording and
submission without asking the target driver or window for mutable state:

- Target generation and frame-slot index.
- Color/depth images and views.
- Extent, layers, formats, and samples.
- Initial and required final layouts.
- Acquire result and image index where applicable.
- Submission wait semaphores and stage masks.
- Submission signal semaphores.
- Completion fence or timeline value.
- Presentation/completion disposition.

The lease must not allocate in the per-frame path.

## Phase 0 - Freeze Baselines And Inventory Coupling

- [ ] Capture a clean desktop Vulkan baseline for startup, one deterministic
  scene, resize, shutdown, and device-lifetime diagnostics.
- [ ] Record current presentationless deterministic-clear hash evidence.
- [ ] Inventory every production `XRWindow`, `Window`, `VkSurface`,
  swapchain, acquire, and present dependency in `VulkanRenderer`.
- [ ] Classify each dependency as device execution, final-output policy,
  desktop UI/input, diagnostics, optional feature, or teardown.
- [ ] Identify render-pipeline APIs that obtain dimensions or viewports from a
  window instead of an explicit frame context.
- [ ] Record current initialization and teardown order for desktop Vulkan and
  OpenXR.
- [ ] Add a focused progress ledger under
  `docs/work/progress/rendering/` before implementation begins.

Exit criteria:

- [ ] Every direct window dependency has an intended owner after the refactor.
- [ ] Desktop and presentationless baseline evidence is reproducible.

## Phase 1 - Make `AbstractRenderer` Target-First

- [x] Add the stable renderer host context described above.
- [x] Change `AbstractRenderer` construction to accept the host context rather
  than requiring `XRWindow`.
- [x] Preserve a compatibility constructor for desktop renderers only where it
  reduces migration churn.
- [x] Expose `PresentationTarget` and `ExecutionMode` from the renderer.
- [x] Move desktop window access behind an explicit optional capability.
- [x] Keep window render-loop ownership in the desktop host, not in the
  generic renderer contract.
- [x] Replace generic renderer naming that depends on the window title with a
  target-safe diagnostic identity.
- [x] Preserve `AbstractRenderer.Current`, render-object cache ownership,
  backend generation, quiescing, and teardown behavior.
- [x] Update OpenGL construction without changing its current desktop-only
  runtime behavior.
- [x] Add focused tests for desktop and non-window renderer host contexts.

Exit criteria:

- [x] A minimal test renderer can derive from `AbstractRenderer` without an
  `XRWindow`.
- [x] Existing desktop renderer creation remains source-compatible at the
  application composition boundary.
- [x] Non-window access to desktop services fails with an actionable message.

## Phase 2 - Separate Vulkan Device Core From Target Policy

- [x] Change `VulkanRenderer` construction to accept the target-first host
  context.
- [x] Select a Vulkan target driver before instance creation.
- [x] Resolve instance extensions through the target driver.
- [x] Move desktop surface creation out of the common initialization sequence.
- [x] Select graphics, compute, transfer, and optional present queues from
  target requirements.
- [x] Resolve device extensions through common renderer requirements plus
  target-driver requirements.
- [x] Keep physical-device scoring and production feature probing common.
- [x] Keep the production VMA/managed/legacy allocator selection common.
- [x] Keep canonical samplers, descriptor layouts, command pools, timing query
  pools, synchronization backend, and resource managers common.
- [x] Replace `CreateAllSwapChainObjects` as the common initialization boundary
  with target-neutral final-output initialization.
- [x] Preserve OpenXR Vulkan instance/device ownership rules.
- [x] Remove the standalone presentationless host's duplicate device bootstrap
  after the production path supersedes it.

Exit criteria:

- [x] Production `VulkanRenderer` initializes a device in presentationless
  mode with no surface extensions.
- [x] Desktop and OpenXR initialize through their existing required extension
  sets.
- [x] The same allocator and backend-object registry are active in desktop and
  presentationless modes.

## Phase 3 - Implement Target Drivers

### 3.1 Desktop WSI driver

- [x] Move current surface and swapchain creation behind the desktop target
  driver.
- [x] Move framebuffer-size, interactive-resize, present-scaling, HDR surface
  selection, out-of-date, and surface-loss behavior with it.
- [x] Preserve Streamline proxy-swapchain integration.
- [x] Preserve per-swapchain-generation retirement.
- [x] Preserve desktop acquire/present diagnostics and recovery.

### 3.2 Presentationless driver

- [x] Move engine-owned color/depth image-ring creation into the production
  renderer allocator.
- [x] Allocate one explicit target generation with fixed output properties.
- [x] Acquire a target by waiting only for the frame-slot completion primitive.
- [x] Provide the render graph with color/depth images and views plus layout
  requirements.
- [x] Complete a frame with real queue submission and no acquire or present.
- [x] Retire replaced target generations through the production retirement
  system.
- [x] Keep preallocated readback staging outside measured intervals.
- [x] Expose explicit final-image hash and bounded readback operations.

### 3.3 Headless WSI driver

- [x] Reuse the existing `VK_EXT_headless_surface` probe.
- [x] Create the headless surface only after the extension is enabled.
- [x] Require a valid graphics/present queue and `VK_KHR_swapchain`.
- [x] Require compatible surface format, image usage, layer count, extent, and
  present mode.
- [x] Use swapchain-image-scoped render-finished semaphores so semaphore reuse
  is proven by reacquisition.
- [x] Label presentation as headless WSI no-op presentation.
- [x] Report unsupported devices without disabling presentationless support.
- [x] Remove the standalone headless-WSI host after the production driver
  supersedes it.

### 3.4 OpenXR target adapter

- [x] Map acquired OpenXR swapchain images into the common frame target lease.
- [x] Preserve runtime-required image ownership and release ordering.
- [x] Preserve multiview/layer and hidden-area-mask behavior.
- [x] Keep OpenXR session loss separate from desktop surface loss.
- [x] Avoid introducing a second acquire or present operation around
  runtime-owned images.

Exit criteria:

- [x] Target drivers are the only owners of surface, swapchain, acquire,
  present, and presentationless image-ring policy.
- [x] Common Vulkan initialization and rendering code contains no desktop
  surface assumption.

## Phase 4 - Make The Frame Loop Target-Neutral

Partial implementation note (2026-07-30): presentationless/component and
headless-WSI explicit frames now acquire a lease and submit through the
renderer-owned allocation-free queue-submit primitive. Desktop still uses its
existing submit path, so the phase and common-path exit criterion remain open.

- [ ] Replace direct swapchain acquisition in the common frame loop with
  target-lease acquisition.
- [ ] Pass the lease through preflight, command recording, submission,
  diagnostics, and completion.
- [ ] Build wait stages and signal semaphores from the lease without allocating.
- [ ] Replace unconditional presentation with target completion.
- [ ] Keep queue submission common across presentationless, desktop WSI, and
  headless WSI.
- [ ] Preserve rejected-frame and dirty-abort synchronization safety.
- [ ] Preserve device-loss diagnostics and first-failing Vulkan API evidence.
- [x] Ensure presentationless frame-slot reuse never calls
  `vkDeviceWaitIdle`.
- [ ] Move resize and out-of-date recovery entirely into applicable target
  drivers.
- [ ] Make frame timing and profiler statistics label the execution mode.
- [ ] Keep target generation in command-buffer dependency signatures where
  final-output identity affects reuse.

Exit criteria:

- [ ] One common frame-loop submission path services all target modes.
- [x] Presentationless frames have no acquire/present branches in their
  executed path.
- [ ] Desktop rejected-frame and recovery behavior remains unchanged.

## Phase 5 - Bind The Production Render Graph To The Lease

- [ ] Add an explicit external final-output description to the render-pipeline
  frame context.
- [ ] Source render extent, layers, formats, samples, and final target from the
  frame lease rather than the window.
- [ ] Provide a windowless viewport/frame context for deterministic fixtures.
- [ ] Keep camera, scene, collect generation, and render-package validation
  independent from presentation mode.
- [ ] Bind the presentationless color/depth views through the existing Vulkan
  render-target wrappers or a target-safe external-image wrapper.
- [ ] Ensure wrapper ownership does not destroy target-driver-owned images.
- [ ] Preserve dynamic-rendering and render-pass format signatures.
- [ ] Preserve Deferred and Uber pass ordering and resource planning.
- [ ] Keep ImGui and desktop overlays disabled when no desktop UI capability
  exists.
- [ ] Replace diagnostic window-title mutation with structured diagnostics in
  non-window modes.
- [ ] Verify command-chain caching includes every target-dependent signature
  and does not rebuild merely because presentation is absent.

Exit criteria:

- [ ] The unmodified production Deferred or Uber render graph records against a
  presentationless frame lease.
- [ ] The same deterministic fixture reaches the same pre-presentation pass
  sequence in presentationless and desktop modes.
- [ ] No render-graph command requires an `XRWindow` merely to obtain output
  state.

## Phase 6 - Consolidate Lifetime And Teardown

- [ ] Define one teardown order covering target quiesce, GPU completion,
  render-graph resources, target resources, allocator, device, surface, and
  instance.
- [ ] Retire target generations before destroying their image views or images.
- [ ] Drain post-measurement readbacks before destroying staging resources.
- [ ] Preserve forced retirement behavior after device loss.
- [ ] Make partial initialization unwind in strict reverse order.
- [ ] Ensure the renderer destroys only the target resources it owns.
- [ ] Delete superseded duplicate bootstrap-host resource management.
- [ ] Verify module unload does not retain Vulkan API wrappers or target
  drivers.

Exit criteria:

- [ ] Validation reports no live target, image, view, semaphore, fence,
  command-pool, query-pool, allocator, surface, or swapchain object at device
  destruction.
- [ ] Each target mode survives repeated create/render/destroy cycles.

## Phase 7 - Validation

### Focused automated tests

- [ ] Test target-first renderer context validation.
- [ ] Test target-driver extension and queue requirements.
- [ ] Test presentationless creation without `XRWindow`.
- [ ] Test deterministic production render-graph submission and output hash.
- [ ] Test fixed frame-slot rotation and fence/timeline ownership.
- [ ] Test that zero-readback submission never calls the readback path.
- [ ] Test explicit unsupported headless-WSI diagnostics.
- [ ] Test partial-initialization cleanup using injected failures.
- [ ] Test renderer module generation propagation in every target mode.
- [ ] Test target-generation invalidation of reusable command buffers.

### GPU integration validation

- [ ] Run presentationless Deferred and Uber fixtures.
- [ ] Run desktop equivalents with the same scene, camera, resolution, format,
  deterministic seed, and frame count.
- [ ] Compare final output identity within documented format/color-space
  differences.
- [ ] Run standard Vulkan validation.
- [ ] Run synchronization validation.
- [ ] Verify presentationless logs contain no surface, swapchain, acquire, or
  present operation.
- [ ] Verify headless WSI logs contain acquire and no-op present operations.
- [ ] Verify desktop logs still contain compositor presentation.
- [ ] Exercise desktop resize, minimize/restore, HDR selection, and
  surface-loss recovery.
- [ ] Exercise OpenXR session start, frame acquisition/release, and shutdown.

### Performance validation

- [ ] Warm the presentationless renderer to steady state.
- [ ] Measure managed allocations across the submission interval.
- [ ] Verify no per-frame resource or shader creation.
- [ ] Verify no per-frame `vkDeviceWaitIdle`.
- [ ] Verify no current-frame GPU-to-CPU readback.
- [ ] Compare command-buffer cache hit/rebuild behavior with desktop.
- [ ] Record CPU and GPU frame-time distributions for the same fixture.

Exit criteria:

- [ ] Targeted tests pass.
- [ ] Validation and synchronization validation introduce no new messages.
- [ ] Presentationless steady-state allocation and churn gates pass.
- [ ] Desktop and OpenXR regressions are ruled out.

## Phase 8 - Documentation And Closeout

- [ ] Update the Vulkan renderer architecture with the final target-driver
  ownership and frame-lease sequence.
- [ ] Update renderer initialization documentation for every execution mode.
- [ ] Document presentationless output formats, layout contract, and readback
  restrictions.
- [ ] Document headless WSI capability and unsupported behavior.
- [ ] Update the headless profiling TODO and check its remaining Phase 1.2
  render-graph item.
- [ ] Record validation commands, hardware, driver, output hashes, and log
  locations in the progress ledger.
- [ ] Remove obsolete compatibility constructors and bootstrap hosts once all
  composition roots use the target-first path.

## Suggested Implementation Slices

1. Target-first `AbstractRenderer` host context with compatibility coverage.
2. Vulkan target-driver and frame-lease contracts; desktop behavior moved
   without semantic changes.
3. Production presentationless target using the existing allocator and common
   Vulkan device core.
4. Target-neutral frame acquisition, submission, completion, and retirement.
5. Production render-graph external-output binding and windowless frame
   context.
6. Headless WSI and OpenXR adapters.
7. Consolidation, validation, performance gates, and documentation.

Keep each slice buildable. Preserve desktop behavior before adding a new target
to the common path.

## Risk Register

| Risk | Mitigation |
|---|---|
| Broad nullable-window propagation hides invalid access | Use an explicit desktop-window capability and fail at the access boundary |
| Desktop swapchain behavior regresses during extraction | Move existing behavior mechanically first and validate before changing policy |
| Presentationless path becomes a second renderer | Require the production allocator, backend wrappers, render graph, command recorder, and retirement system |
| OpenXR ownership is forced into WSI semantics | Keep an explicit OpenXR adapter and runtime-owned acquire/release policy |
| Reusable command buffers reference an old target generation | Include target generation and output signature in dependency validation |
| Present semaphore is reused before presentation consumes it | Scope render-finished semaphores to swapchain images or prove completion with an applicable extension |
| Target-owned images are destroyed by render wrappers | Distinguish borrowed external images from renderer-allocated resources |
| Performance evidence includes readback or target churn | Keep correctness operations explicit and outside the measured interval |

## Final Completion Gate

- [ ] `VulkanRenderer` can initialize and run the production render graph
  without an `XRWindow`.
- [ ] Presentationless mode uses the production allocator, wrappers, render
  graph, command recorder, synchronization backend, retirement system, and
  profiler.
- [ ] Presentationless mode creates no surface or swapchain and performs no
  acquire or present.
- [ ] Desktop WSI, headless WSI, and OpenXR retain their required target
  semantics.
- [ ] Deterministic Deferred or Uber output is stable and comparable across
  presentationless and desktop modes.
- [ ] Standard and synchronization validation pass.
- [ ] Steady-state zero-allocation, zero-churn, zero-current-frame-readback, and
  no-device-wide-wait gates pass.
- [ ] Duplicate bootstrap hosts are removed.
- [ ] The remaining Phase 1.2 checkbox in the parent TODO is checked.

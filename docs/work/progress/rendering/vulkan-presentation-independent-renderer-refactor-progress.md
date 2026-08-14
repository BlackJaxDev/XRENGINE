# Vulkan Presentation-Independent Renderer Refactor Progress

Status: Implementation Complete; validation transferred

Started: 2026-07-30

Completed: 2026-08-13

Execution plan:
[Completed Vulkan Presentation-Independent Renderer Refactor](../../todo/COMPLETED/vulkan-presentation-independent-renderer-refactor-todo.md)

Remaining validation:
[Vulkan Presentation-Independent Renderer Validation](../../testing/rendering/vulkan-presentation-independent-renderer-validation.md)

## Objective

Refactor the production Vulkan renderer so its device, resource, render-graph,
command-recording, synchronization, retirement, and profiling systems can run
against desktop WSI, presentationless, headless WSI, and OpenXR targets without
constructing a synthetic `XRWindow`.

## Phase Status

| Phase | Status | Notes |
|---|---|---|
| Phase 0 - Baselines and coupling inventory | Complete | Coupling inventory and reproducible desktop/presentationless evidence recorded in this ledger |
| Phase 1 - Target-first `AbstractRenderer` | Complete | Stable host context, explicit desktop capability, migration adapters, and tests landed |
| Phase 2 - Vulkan device core and target policy | Complete | Target-selected extensions, queues, lifecycle hooks, and shared production device core |
| Phase 3 - Target drivers | Complete | Production presentationless image ring, headless WSI, desktop policy ownership, and OpenXR lease adapter |
| Phase 4 - Target-neutral frame loop | Complete | Desktop and explicit targets freeze acquired output into one lease and use the common tracked submit gateway |
| Phase 5 - Production render-graph binding | Complete | Portable frame-output state drives windowless production graph recording against borrowed target views |
| Phase 6 - Lifetime and teardown | Complete | Quiescing, staged reverse unwind, ordered retirement/readback drain, and idempotent cleanup implemented |
| Phase 7 - Validation | Transferred | Remaining validation matrix moved to the dedicated testing plan without claiming completion |
| Phase 8 - Documentation and closeout | Complete | Architecture, initialization, output contracts, parent workstream, and closeout links updated |

## Phase 1 Work Log

### 2026-07-30 - Target-first context completed

Implemented:

- Added `RendererHostContext` as the validated, renderer-owned target context.
  It carries the presentation target, execution mode, fixed output properties,
  desktop-link policy, and backend generation.
- Added `IRendererDesktopWindowServices` as an optional target capability.
  `DesktopWindowRenderTarget` implements it; presentationless, component,
  headless-WSI, and OpenXR targets do not.
- Added non-throwing desktop capability detection and explicit
  `RequireDesktopWindowHost`/`RequireDesktopWindow<TWindow>` boundaries.
- Changed `AbstractRenderer` and `AbstractRenderer<TAPI>` to accept
  `RendererHostContext` while retaining desktop `XRWindow` compatibility
  constructors.
- Exposed `HostContext`, `PresentationTarget`, `ExecutionMode`, and
  `HasDesktopWindowServices` from `AbstractRenderer`.
- Replaced the window-dependent fallback API-wrapper name with a target-safe
  execution-mode/output identity. Desktop window titles remain preferred when
  available.
- Replaced the base `WindowRenderCallback` contract with target-neutral
  `RenderFrameCallback`/`RenderFrame`.
- Removed generic renderer ownership of a native window main loop.
  `XRWindow.RenderWindow` remains a compatibility dispatch boundary and
  validates desktop services before rendering.
- Updated OpenGL and Vulkan factories to freeze
  `RendererBackendCreateContext` into `RendererHostContext`.
- Updated OpenGL and Vulkan constructors to accept the target-first context
  while preserving their current desktop constructors and desktop-only
  behavior.
- Preserved renderer generation, render-object cache initialization,
  `AbstractRenderer.Current`, quiescing, and teardown code.

## Phase 2 Work Log

### 2026-07-30 - Production device core separated from target policy

Implemented:

- Added an internal `IVulkanRendererTargetDriver` bootstrap contract and
  selected desktop WSI, presentationless/component, headless WSI, or OpenXR
  policy in the `VulkanRenderer` constructor before instance creation.
- Moved desktop Vulkan instance-extension discovery, surface lifecycle, and
  swapchain-output initialization behind the desktop target driver.
- Split common and target-required device extensions. Presentationless and
  OpenXR require neither `VK_KHR_surface` nor `VK_KHR_swapchain`; desktop WSI
  retains its window-system instance extensions and swapchain requirement.
- Made present queues optional throughout physical-device selection,
  logical-device queue requests, and `VulkanDeviceContext`. Graphics, dedicated
  compute, and dedicated transfer families remain common.
- Kept physical-device capability queries, feature enablement, OpenXR
  instance/device creation, allocator selection, canonical samplers,
  descriptors, command pools, timing pools, synchronization, and backend-object
  registry creation in the production renderer.
- Replaced the common swapchain bootstrap call with target-neutral
  `InitializeFinalOutput`/teardown hooks. Presentationless Phase 2 performs a
  device-core-only no-op at this boundary; Phase 3 owns its image ring.
- Removed the presentationless bootstrap host's duplicate instance,
  physical-device, logical-device, and queue bootstrap. The transitional
  deterministic-clear host now composes an initialized production
  `VulkanRenderer` device core while retaining its Phase 1.2 frame-slot images
  until Phase 3 moves those images behind the production target driver.
- Declared headless-WSI target requirements in its production driver. Native
  `VK_EXT_headless_surface` creation intentionally remains in the existing
  standalone host until Phase 3.

## Phase 3 Work Log

### 2026-07-30 - Final-output target drivers completed

Implemented:

- Expanded the production target boundary with allocation-free
  `VulkanFrameTargetLease` metadata and an explicit-frame target contract used
  by deterministic presentationless and headless validation.
- Moved the fixed presentationless color/depth image ring, command pools,
  slot fences, timestamp queries, and readback staging into
  `VulkanPresentationlessTargetDriver`.
- Allocated presentationless images and readback buffers through the
  production renderer allocator. Frame acquisition waits only the selected
  slot fence; submission uses the production graphics queue without Vulkan
  acquire/present or a device-wide wait.
- Added explicit bounded final-color readback and SHA-256 hashing. Readback
  staging is created with the target generation and no readback occurs during
  frame submission.
- Implemented `VulkanHeadlessWsiTargetDriver` on the production
  instance/device/allocator path. It reuses the extension probe, creates its
  surface only after instance extension enablement, validates format, usage,
  layers, extent, FIFO present mode, and graphics/present queues, and uses one
  render-finished semaphore per swapchain image.
- Removed both standalone bootstrap hosts. Fixed-output backends now use the
  thin `VulkanExplicitTargetRendererHost`, which composes only a production
  `VulkanRenderer` and delegates frame work to its selected target driver.
- Routed desktop surface/swapchain lifecycle, framebuffer and resize state,
  HDR preference, present scaling, acquire/present dispatch, result
  classification, recreation, and surface-loss policy through
  `VulkanDesktopWsiTargetDriver`. Existing Streamline proxy acquire/present,
  diagnostics, rejected-frame recovery, and swapchain-generation retirement
  remain intact.
- Mapped runtime-acquired OpenXR images into `VulkanFrameTargetLease` with
  external ownership, view/layer metadata, hidden-area-mask support, and
  `OpenXrRuntimeRelease` completion. The adapter introduces no Vulkan acquire
  or present operation; the existing OpenXR binding retains acquire/wait/
  submit/release ordering and session-loss handling.
- Fixed output properties are immutable for a renderer lifetime. Replacing
  them creates a new renderer target generation; the old renderer follows the
  production GPU-idle/retirement teardown boundary rather than immediately
  destroying in-flight target resources.

Phase 2 exit evidence:

- A production presentationless renderer initialized a real logical device and
  graphics queue with no present queue.
- Its enabled instance extensions excluded `VK_KHR_surface`, and enabled device
  extensions excluded `VK_KHR_swapchain`.
- The configured production memory allocator and the normal
  `VulkanBackendObjectRegistry` were both active.
- The deterministic presentationless clear/readback/hash smoke continued to
  pass after its host switched to the production device core.

## Phase 4 Work Log

### 2026-07-30 - Explicit-target submission slice

Status: Partial; wrapped at the user's requested stopping point.

Implemented:

- Extended `VulkanFrameTargetLease` with the target acquire result and made
  explicit-target drivers expose acquire, recording-boundary, submitted,
  completion, and abort hooks.
- Moved presentationless/component and headless-WSI queue submission into the
  renderer-owned `SubmitFrameTargetLease` primitive.
- The common primitive builds optional binary wait/signal semaphore arrays and
  optional graphics-timeline signaling entirely with `stackalloc`.
- Presentationless completion is an explicit renderer-owned no-op. Headless WSI
  completion performs the required no-op WSI present through its target driver.
- Added rejected-recording/submission cleanup. Presentationless restores a
  signaled slot fence; headless WSI consumes and presents an acquired image
  through a bounded recovery submit.
- Routed explicit-target submissions through tracked queue submission so
  device-loss diagnostics retain the first failing submit evidence.
- Added stable execution-mode-specific profiler scope and diagnostic labels
  without constructing per-frame label strings.
- Confirmed that presentationless execution contains no
  `vkAcquireNextImageKHR`, `vkQueuePresentKHR`, or `vkDeviceWaitIdle` branch.

### 2026-08-13 - Target-neutral production submission completed

Implemented:

- Desktop WSI now freezes every accepted acquired image, view, extent, layout,
  target generation, and synchronization dependency into
  `VulkanFrameTargetLease`, matching the explicit-target contract.
- Desktop and explicit targets submit through `SubmitFrameTargetLease`, which
  stack-allocates the bounded wait/signal sets and routes the result through
  the sole tracked queue-submit gateway. Device-loss disposition and the first
  failing native operation remain attached to the real submission.
- Command-recording policy now carries the lease-required final layout instead
  of assuming `PresentSrcKHR`. Target generation participates in prepared
  render-target identity and reusable-primary dependency validation.
- Frame-slot counts are derived from the selected target. Three-slot explicit
  targets no longer index desktop-sized retirement or arena state.
- Removed a diagnostic string allocation from the successful queue-submit hot
  path; detailed native-operation text is constructed only for device loss.

## Phase 5 Work Log

### 2026-08-13 - Production render graph bound to portable frame output

Implemented:

- Added `RenderFrameOutputDescription`, a backend-neutral, frame-scoped value
  carrying output properties, target generation, slot, view, execution mode,
  and host capabilities. Native Vulkan handles remain exclusively in the
  backend lease.
- Added an allocation-free renderer scope that publishes the acquired output
  to `IRuntimeRenderPipelineFrameContext.FinalOutput` while ordinary viewport
  and pipeline code builds the frame.
- Render-pipeline resource planning now sources display/internal extent,
  layers, samples, output formats, external-target kind, and view identity from
  the frame output. Color/depth formats participate in generation keys so
  canvas or target reconfiguration cannot reuse incompatible resources.
- Added the lease-backed production entry point on
  `VulkanExplicitTargetRendererHost`. It runs the ordinary Deferred/Uber graph,
  records with the production primary-command runtime, submits through the
  common gateway, and completes or aborts the target lease.
- Explicit target views are borrowed by command recording; target drivers
  retain destruction authority. Desktop ImGui composition is absent when the
  output lacks `DesktopOverlays`, while engine-owned render-graph UI remains
  ordinary portable primary-command work.

## Phase 6 Work Log

### 2026-08-13 - Lifetime and teardown consolidated

Implemented:

- Added explicit initialization stages. Failed initialization invokes the same
  cleanup path and unwinds only the stages that were reached, preserving both
  initialization and cleanup failures when necessary.
- Cleanup publishes quiescing before target shutdown, rejects new frame
  admission, drains active frame owners, optionally establishes GPU completion, retires target
  generations, drains readbacks before staging, and preserves forced
  retirement after device loss.
- Logical-device resources, target final output, allocator, device, target
  instance resources, and Vulkan instance are destroyed in a single ordered
  path. Cleanup is idempotent and continues collecting failures instead of
  abandoning later ownership boundaries.
- Output-service references and renderer-owned wrappers are detached after
  native destruction. Repeated presentationless production host lifecycles
  complete without retaining target-driver or Vulkan wrapper state.

## Phase 8 Work Log

### 2026-08-13 - Documentation and closeout completed

Implemented:

- Updated Vulkan architecture and renderer initialization documentation for
  desktop WSI, presentationless, component, headless WSI, OpenXR, and the
  reserved future browser-canvas contract.
- Documented fixed-output format/layout/readback rules and explicit unsupported
  headless-WSI behavior.
- Removed the obsolete Vulkan `XRWindow` compatibility constructor after all
  Vulkan composition roots were confirmed to use `RendererHostContext`.
  OpenGL's active desktop constructor remains.
- Checked the headless profiling workstream's Phase 1.2 production render-graph
  item and linked its remaining acceptance work to the dedicated validation
  plan.
- Moved the Phase 7 validation matrix to
  [Vulkan Presentation-Independent Renderer Validation](../../testing/rendering/vulkan-presentation-independent-renderer-validation.md)
  without claiming unrun gates as complete, then retired the implementation
  TODO to `docs/work/todo/COMPLETED/`.

## Validation Evidence

Validated on 2026-07-30:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  - Passed with zero errors and no C# compiler warnings.
- `dotnet build .\XREngine.Runtime.Rendering.OpenGL\XREngine.Runtime.Rendering.OpenGL.csproj --no-restore`
  - Passed with zero errors and no C# compiler warnings.
- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -p:XREngineUseExistingNativeBridges=true`
  - Passed with zero errors and no C# compiler warnings.
- Focused NUnit filter:
  `RendererHostContextTests|RendererBackendCatalogTests|VulkanPresentationIndependentHostTests`
  - Passed: 21
  - Failed: 0
  - Skipped: 0
  - Results: `Build/_AgentValidation/phase1-target-first-test-results/`

Existing `Magick.NET-Q16-HDRI-AnyCPU` NuGet vulnerability advisories remain
unrelated to this refactor.

Phase 2 validation on 2026-07-30:

- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`
  - Passed with zero errors.
  - Existing `VulkanRenderer.ForwardLightingBindings.cs` nullable warning and
    existing NuGet vulnerability advisories remain unrelated.
- Focused NUnit fixture: `VulkanPresentationIndependentHostTests`
  - Passed: 7
  - Failed: 0
  - Skipped: 0
  - Includes target-policy requirement checks, surface-independent dedicated
    queue selection, production presentationless device-core initialization,
    deterministic clear/readback/hash, and headless-WSI explicit-support
    behavior.
- Combined Phase 1/2 regression filter:
  `RendererHostContextTests|RendererBackendCatalogTests|VulkanPresentationIndependentHostTests`
  - Passed: 24
  - Failed: 0
  - Skipped: 0
- Vulkan/backend architecture guardrail filter:
  - Passed: 21
  - Failed: 5
  - The two adjacent presentation-host visibility/type-layout violations found
    on the first run were fixed.
  - Remaining failures are existing dirty-worktree changes outside Phase 2:
    editor concrete-backend references, the backend-generation source-test
    parser, `VulkanCpuSpanProfiler` thread-static state, and
    `VulkanRenderer.ForwardLightingBindings` state-owner/partial-budget
    violations.

Phase 3 validation on 2026-07-30:

- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore -p:XREngineUseExistingNativeBridges=true`
  - Passed with zero errors.
  - Existing NuGet vulnerability advisories and the pre-existing
    `RendererHostContext.cs` nullable warning remain unrelated.
- `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --no-dependencies -p:XREngineUseExistingNativeBridges=true`
  - Passed with zero errors using already-built dependency assemblies.
- Focused NUnit fixture: `VulkanPresentationIndependentHostTests`
  - Passed: 8
  - Failed: 0
  - Skipped: 0
  - Covers production presentationless allocation/submission/hash,
    bounded-readback rejection, target requirements, headless-WSI explicit
    support, and OpenXR external-image lease semantics.
- Combined Phase 1-3 regression filter:
  `RendererHostContextTests|RendererBackendCatalogTests|VulkanPresentationIndependentHostTests`
  - Passed: 25
  - Failed: 0
  - Skipped: 0
- A normal dependency-building test invocation remains blocked by three
  unrelated dirty-worktree OpenGL references to missing
  `XREngineEnvironmentVariables` members. Reusing the existing dependency
  assemblies isolates and passes the Phase 3 tests.

Phase 4 partial-slice validation on 2026-07-30:

- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore --no-dependencies -p:XREngineUseExistingNativeBridges=true`
  - Passed with zero errors.
- `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --no-dependencies -p:XREngineUseExistingNativeBridges=true`
  - Passed with zero errors.
- Focused NUnit fixture: `VulkanPresentationIndependentHostTests`
  - Passed: 8
  - Failed: 0
  - Skipped: 0
- Existing Magick.NET advisory warnings remain unrelated.

Phases 4-6 implementation validation on 2026-08-13:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore --no-dependencies`
  - Passed with zero warnings and zero errors.
- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore --no-dependencies`
  - Passed with zero warnings and zero errors.
- `dotnet build .\XREngine.RenderBench\XREngine.RenderBench.csproj --no-restore`
  - Passed with zero warnings and zero errors.
- Presentationless RenderBench deterministic-clear run, 64x64, three frame
  slots, six warmup, three stability, and six capture frames:
  - NVIDIA GeForce RTX 4070 Laptop GPU.
  - Six submissions and six command buffers; all stability gates passed.
  - Capture-thread and fixture-worker allocations: 0 bytes.
  - Output SHA-256:
    `DEF598687A136FABA64832EF05E1E7DFAC2B0E0A703DA047C2E9556107726318`.
  - Evidence:
    `Build/_AgentValidation/20260813-180927-vulkan-presentation-refactor/reports/presentationless-smoke-5/`.
- A disposable production-graph harness ran the unmodified
  `DefaultRenderPipeline` through `SubmitProductionFrame` with a windowless
  viewport and three-slot presentationless target:
  - Four frames per renderer lifecycle and three complete create/render/destroy
    lifecycles.
  - Every lifecycle produced
    `62FB561C59D0CEA247FC588F3311EE665375F35D8675B186E2792CB7DFCFF88C`.
- An isolated desktop Vulkan editor session built and ran successfully. Two
  viewport readbacks from different camera positions completed on queue slots
  0 and 1 and produced visibly distinct images. The session emitted no Vulkan
  validation, device-loss, fatal, or unhandled-exception diagnostics; the only
  warning was the unrelated optional Steam Audio fallback.
- `rdc doctor` passed with RenderDoc 1.44 and a registered Vulkan layer.
  Presentationless execution has no WSI frame boundary, so external automatic
  capture did not produce an `.rdc`; the deterministic hash, allocation gates,
  desktop readbacks, and renderer logs remain the validation evidence for this
  slice.
- Per the repository feature-first testing policy, no tests were added,
  modified, or run before explicit user clearance. Phase 7 retains the focused
  automated and remaining cross-target integration matrix.

## Decisions

- The renderer-owned context is distinct from the transient backend factory
  context. `RendererBackendCreateContext.ToRendererHostContext()` is the
  explicit ownership boundary.
- Desktop services are a target capability rather than nullable generic
  renderer state.
- Compatibility `XRWindow`/`Window` accessors remain during migration, but
  resolve through the capability boundary and fail immediately for non-window
  modes.
- OpenGL accepts the new context but still requires a concrete desktop
  `XRWindow`; presentation-independent OpenGL behavior was not added.
- Vulkan accepts desktop and non-window contexts; target-specific native
  requirements are selected by its immutable target driver.
- Vulkan target policy is immutable for the renderer lifetime and is selected
  before any native instance work.
- Absence of a present queue is represented by an empty Vulkan queue handle;
  it is not aliased to the graphics queue.
- Presentationless/component and OpenXR share the surface-independent device
  core. OpenXR runtime requirements continue to be merged by the existing
  `XR_KHR_vulkan_enable2`/runtime-requirements path.
- Fixed-output runtime hosts use a single thin adapter over the production
  renderer; target drivers own all native final-output resources and policy.
- Generic pipeline code consumes `RenderFrameOutputDescription`; native image,
  synchronization, JavaScript, and backend handle identities stay inside the
  concrete renderer. This is also the future WebGL2/WebGPU canvas boundary.
- Resource-generation identity includes target class, dimensions, views,
  samples, and color/depth formats. Presentation absence alone is not a cache
  invalidation reason.

## Open Risks

- Direct compatibility `XRWindow` usage remains in desktop-only Vulkan and
  OpenGL services; portable final-output state no longer depends on it.
- Phase 0 desktop/OpenXR baselines remain outstanding because the user
  requested Phase 1 directly.
- Phase 7 still needs the full headless-WSI/OpenXR runtime matrix, injected
  partial-initialization failure coverage, synchronization validation, and
  repeated lifecycle coverage for every platform-available target mode.
- Existing unrelated worktree changes must remain untouched.

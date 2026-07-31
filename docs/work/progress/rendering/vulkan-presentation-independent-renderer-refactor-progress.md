# Vulkan Presentation-Independent Renderer Refactor Progress

Status: Active  
Started: 2026-07-30  
Execution plan:
[Vulkan Presentation-Independent Renderer Refactor TODO](../../todo/rendering/optimization/vulkan-presentation-independent-renderer-refactor-todo.md)

## Objective

Refactor the production Vulkan renderer so its device, resource, render-graph,
command-recording, synchronization, retirement, and profiling systems can run
against desktop WSI, presentationless, headless WSI, and OpenXR targets without
constructing a synthetic `XRWindow`.

## Phase Status

| Phase | Status | Notes |
|---|---|---|
| Phase 0 - Baselines and coupling inventory | Not started | Baseline capture remains required before target-driver extraction |
| Phase 1 - Target-first `AbstractRenderer` | Complete | Stable host context, explicit desktop capability, compatibility constructors, and tests landed |
| Phase 2 - Vulkan device core and target policy | Complete | Target-selected extensions, queues, lifecycle hooks, and shared production device core |
| Phase 3 - Target drivers | Complete | Production presentationless image ring, headless WSI, desktop policy ownership, and OpenXR lease adapter |
| Phase 4 - Target-neutral frame loop | Partial | Explicit presentationless/component and headless-WSI frames share the lease-driven renderer submit primitive; desktop integration remains |
| Phase 5 - Production render-graph binding | Not started | |
| Phase 6 - Lifetime and teardown | Not started | |
| Phase 7 - Validation | Not started | |
| Phase 8 - Documentation and closeout | Not started | |

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

Remaining:

- Populate and thread a desktop WSI lease through preflight, recording,
  submission, diagnostics, completion, and dirty-abort recovery.
- Make the desktop path call `SubmitFrameTargetLease` and remove its duplicate
  submit assembly.
- Thread target generation into the command-recording dependency signature.
- Finish mode-labelled lifecycle statistics and validate desktop recovery
  behavior before checking the Phase 4 exit criteria.

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

## Open Risks

- Direct compatibility `XRWindow` usage remains in Vulkan and OpenGL and must
  be classified/moved behind target drivers in later phases.
- Phase 4 still needs to thread `VulkanFrameTargetLease` through the common
  production frame loop. Phase 3 provides the target-owned resources and
  acquisition/completion policy without prematurely rewriting that loop.
- Phase 0 desktop/OpenXR baselines remain outstanding because the user
  requested Phase 1 directly.
- Existing unrelated worktree changes must remain untouched.

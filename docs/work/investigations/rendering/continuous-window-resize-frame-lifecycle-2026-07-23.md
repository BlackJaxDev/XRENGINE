# Continuous Window Resize Frame Lifecycle

Status: implemented and locally validated; user retest pending
Last updated: 2026-07-23

## Problem

While a native Windows border is held and dragged, the window surface changes
but the scene and retained native UI stop publishing new frame data until
mouse-up. The last presented UI is stretched to the transient client size, and
the FPS overlay neither lays itself out at that size nor reports new timing
information.

The required behavior is continuous scene rendering, native UI layout,
collection, buffer publication, and presentation during the mouse-held resize,
using the same render cadence and callbacks as an ordinary frame.

## Validation Correction

The user retested the first attempted fix and reported the same freeze. That
report was correct. The earlier synthetic validator was misleading for three
reasons:

- it issued in-process MCP requests during the held interval;
- it paused about 300 ms after each cursor movement, which gave low-priority
  `WM_PAINT` work time to run;
- it mixed DPI-virtualized window coordinates with physical screen captures.

A corrected per-monitor-DPI-aware validator continuously moved the real cursor
for three seconds with the left button held and used only out-of-process
`CopyFromScreen` captures and process CPU samples during that interval.

The corrected baseline reproduced the failure in
`Build/_AgentValidation/mcp-sessions/continuous-resize-retry-20260723/mcp-captures/continuous-mouse-baseline/`.
The native outer rectangle shrank from `1355x850` to `1201x786`, but every
active-motion capture contained the same clipped rendered pixels and the same
overlay text:

`render: 049hz 029.30ms | cpu 012.87ms | gpu 001.07ms`

## Root Causes

In collapsed GLFW/Silk.NET mode, `XRWindow.RenderFrame` called
`Window.DoEvents()` from inside `EngineTimer.DispatchRender`. Win32 enters its
modal size/move loop from that event pump, so the outer engine render dispatch
remained active for the entire mouse-held resize.

The Win32 modal timer then called `Window.DoRender()` directly. Those nested
window-only renders bypassed `EngineTimer.DispatchRender`, including:

- render timestamp and delta updates;
- `BeginRenderFrame` and render frame IDs;
- the normal `RenderFrame` subscriber chain;
- visibility-generation consumption;
- the per-frame reset of the collect-release latch;
- the normal collect/swap publication boundary.

The first nested render could release one collect pass, but
`_renderReadyForNextCollectSignaled` remained set because only a normal
`DispatchRender` resets it. Subsequent resize ticks consequently reused stale
scene/UI buffers. The FPS text also samples the engine render clock, which the
direct `Window.DoRender()` calls never advanced.

Two additional problems remained after correcting that frame-lifecycle issue:

1. `WM_PAINT` is deliberately lower priority than input and sizing messages.
   It was starved while the mouse moved continuously. The earlier paused
   validator hid this; a stationary held mouse allowed paint frames to resume.
2. Once `WM_SIZING` dispatched normal frames, Vulkan still skipped every
   present. Automatic internal-resolution selection changed the viewport's
   internal resolution at every transient pixel before the existing
   interactive-resize resource freeze ran. The active generation therefore
   never matched, pending generations chased the mouse, and the swapchain
   logged `presentation remains paused while generation catches up`.

## Implemented Fix

- The collapsed render host now pumps native window events before
  `EngineTimer.WaitToRender`, so the Win32 modal loop is entered outside any
  active render dispatch.
- Interactive resize callbacks now request a complete EngineTimer frame. That
  request enters the same `EngineTimer.WaitToRender` method as the ordinary
  outer loop, preserving its cadence wait, visibility late policy, render
  subscribers, frame IDs, collect/swap publication, and next-collect release.
- `XRWindow.RenderFrame` no longer owns native event pumping.
- Win32 and GLFW resize helpers no longer impose a separate fixed 60 Hz render
  gate. The normal EngineTimer cadence is authoritative.
- Every synchronous `WM_SIZING` position now applies the latest cheap
  presentation extent and requests a complete normal EngineTimer frame. This
  message cannot be starved by continuous border input.
- Self-invalidated `WM_PAINT` remains the idle/held-mouse driver. The 1 ms
  Win32 timer only watchdogs that paint sequence;
  `XRE_WIN32_INTERACTIVE_RESIZE_TIMER_MS` remains an explicit 1-250 ms
  watchdog override.
- Interactive snapshots do not admit full internal render-resource
  generations. Automatic internal-resolution mutation is also frozen at its
  source during the drag. The last complete internal generation renders
  through the ordinary pipeline while the live presentation extent, camera
  aspect, UI layout, collection, swapchain, and final composite follow the
  mouse.
- `WM_EXITSIZEMOVE` queues the exact final full-internal resize, restoring the
  normal settled resource generation after mouse-up.

## Validation

Focused automated validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore
  --filter "FullyQualifiedName~WindowOwnershipContractTests" /m:1
  /nodeReuse:false /p:UseSharedCompilation=false`
- Result: 18 passed, 0 failed. The build compiled the editor, engine, runtime
  rendering assembly, and unit-test assembly with 0 warnings and 0 errors.
- The related
  `VulkanP1ValidationTests.SwapchainResizeAndPresentation_HaveRecoveryAndPresentTransitionDiagnostics`
  source contract also passed (1 passed, 0 failed).
- `RuntimeRenderingHostServicesTests`: 13 passed, 0 failed.

Live validation used isolated Vulkan and OpenGL editor sessions, the Physics
Testing World, and the Win32 modal-loop strategy. There were no in-process MCP
calls during either held interval.

Vulkan evidence:

- Captures:
  `Build/_AgentValidation/mcp-sessions/continuous-resize-retry-20260723/mcp-captures/continuous-mouse-stable-internal/`
- While `MouseDown=True` and the cursor was continuously moving, inspected
  captures changed from `1333x841` / `95hz`, through `1282x820` / `80hz`, to
  `1209x789` / `68hz`.
- The hierarchy/inspector split, scene composition, UI panels, and centered
  debug overlay all re-laid out at the captured sizes before mouse-up.
- Vulkan logs show swapchain convergence with zero extent divergence and
  `Allowing presentation-only display mismatch during interactive resize`
  while the stable `1338x796` internal generation remained active.
- The resize interval contains no VUID or Vulkan validation error.

Final OpenGL evidence after removing the redundant live
`WM_WINDOWPOSCHANGED` render:

- Captures:
  `Build/_AgentValidation/mcp-sessions/continuous-resize-opengl-20260723/mcp-captures/post-cleanup-warmup/`
- While `MouseDown=True` and the cursor was continuously moving, inspected
  captures changed from `1307x830` / `50hz`, through `1246x804` / `36hz`, to
  `1198x784` / `38hz`.
- Scene content, camera framing, native panels, splitter position, hierarchy
  clipping, and debug-text layout all changed with the dragged resolution.
- The OpenGL resize interval contains no renderer error.

Both named sessions were stopped through `Manage-McpEditorSession.ps1` after
validation.

## Vulkan Stress-Resize Regression

The user confirmed that OpenGL behaves correctly, then reproduced a slower
Vulkan drag followed by a renderer exception in the 14:39:34 run. The drag
changed direction and extent repeatedly for about eleven seconds, which was
substantially more stressful than the earlier single-direction validation.

The logs establish two coupled failures:

- eight old swapchain generations accumulated and never retired; subsequent
  recreates remained permanently deferred at
  `PendingGenerations=8/8`, leaving the live surface at `1952x1126` while the
  swapchain remained at `1498x720`;
- after mouse-up, the settled forward-plus generation threw
  `Failed to map Vulkan buffer memory` while publishing a 3072-byte
  host-visible buffer. Its failed retirement fence then remained at the head
  of the queue and emitted the same full warning stack thousands of times.

The corrective direction is to use validated Vulkan present scaling during
the held drag so one swapchain follows the same normal render callbacks
without per-pixel swapchain churn, recreate once after resize settles, keep
host-visible VMA upload allocations persistently mapped, and allow a failed
generation fence to hand resources to Vulkan's completion-aware retirement
rather than permanently blocking the queue.

### Vulkan Corrective Implementation

- Vulkan now enables `VK_KHR_get_surface_capabilities2`,
  `VK_EXT_surface_maintenance1`, and `VK_EXT_swapchain_maintenance1` when the
  driver exposes them, including the device feature in the logical-device
  feature chain.
- Swapchain creation queries present-scaling capabilities for the selected
  present mode. When stretch scaling is supported for the swapchain image
  extent, it chains `VkSwapchainPresentScalingCreateInfoEXT` with centered
  gravity into swapchain creation.
- During a mouse-held resize, a validated scaling swapchain remains active
  across surface-extent changes. `VK_SUBOPTIMAL_KHR` from image acquisition
  or presentation is treated as the expected scaled-present result during
  that interval instead of scheduling another swapchain rebuild.
- Mouse-up still admits the exact full-internal resource generation and
  performs one exact-size swapchain convergence rebuild.
- Host-visible VMA allocations now request
  `VMA_ALLOCATION_CREATE_MAPPED_BIT`; the native bridge returns
  `pMappedData`, and managed uploads use that persistent pointer instead of a
  per-upload `vmaMapMemory` / `vmaUnmapMemory` cycle. The fallback mapping
  path now logs the actual Vulkan result and allocation details.
- A failed render-resource retirement fence no longer remains permanently at
  the queue head. The pipeline first establishes a physical-destruction
  completion boundary, then hands the generation to the backend's
  completion-aware retirement and dequeues it.

### Final Vulkan Stress Validation

The first scaled-present pass exposed one remaining policy bug: NVIDIA
returned `VK_SUBOPTIMAL_KHR` while scaling, and the generic recovery branch
still recreated the swapchain repeatedly. That partial attempt was therefore
not accepted as the final result. The final pass included the explicit
interactive-scaled-present handling above.

Final isolated session:

`Build/_AgentValidation/mcp-sessions/continuous-resize-vulkan-scaling-20260723/`

- A per-monitor-DPI-aware validator oscillated the physical bottom-right
  window border for 14 seconds with the left mouse button continuously held.
  Captures and process CPU samples were taken out of process; no MCP request
  ran during the held interval.
- The swapchain remained `1338x794` for the entire drag while the live surface
  repeatedly ranged from approximately `1030x660` through `1592x941`.
  There were zero swapchain recreations during mouse-down.
- `WM_EXITSIZEMOVE` admitted `1592x941`; exactly one post-release swapchain
  recreate converged to that extent with zero divergence in `46.792 ms`.
- Inspected drag captures showed continuously changing scene framing and
  current debug text. After cursor motion stopped but the mouse remained
  down, consecutive captures reported `82hz` then `76hz`, changed CPU/GPU
  timing values, and kept the text centered and fully inside the current
  `1614x997` outer window.
- The settled post-release capture reported `79hz`; the scene, editor panels,
  and native debug overlay were correctly laid out at the final extent.
- The final interval contains no `Failed to map Vulkan buffer memory`,
  renderer-command exception, device loss, VUID, Vulkan validation error, or
  `PendingGenerations=8/8`. Both settled retired generations reached
  `Fence=Signaled` and `RemainingQueue=0`.

Focused regression validation:

- `XREngine.Runtime.Rendering.csproj`: build succeeded with 0 warnings and
  0 errors.
- `WindowOwnershipContractTests`: 19 passed, 0 failed.
- Present-scaling, persistent-VMA-mapping, and failed-retirement-queue
  contracts: 3 passed, 0 failed.

## Scaled-Present Composition Regression

The user retested the scaled-present implementation and reported that it made
the ImGui editor disappear and stretched/transformed the scene away from the
current window bounds. That report is correct, and the prior result must not be
treated as a successful fix.

The existing validation captures already reproduce the failure:

- `before_00_1360x850.png` and `drag_01_1274x801.png` contain the ImGui editor;
- `drag_02_1142x724.png` and later held captures contain the scene and native
  FPS text but no ImGui editor;
- `after_05_1614x997.png` contains ImGui again after mouse-up.

The first confirmed cause is an invalid Vulkan overlay contract:
`TryConsumeRenderableImGuiOverlaySnapshot` rejects live ImGui draw data whenever
its framebuffer extent differs from the fixed scaled-present swapchain extent.
The final scene present quad has the complementary error: it uses the transient
presentation-space viewport rectangle directly as a fixed-swapchain raster
rectangle. WSI then scales the entire fixed image, so only a partial,
incorrectly positioned part of the swapchain contains the newly composed
scene.

The corrected contract must keep live camera/UI layout coordinates while
mapping their raster viewport and scissors to the fixed swapchain extent.
Validation is not complete until ImGui panels, scene framing, native UI, and
debug text all remain present and aligned in multiple captures taken before
mouse-up.

### Composition Fix

- `AbstractRenderer` now exposes a window-presentation-to-backbuffer region
  mapping hook. The default renderer keeps the region unchanged.
- Vulkan maps the live presentation-space region into the fixed scaled-present
  swapchain only while interactive present scaling is active. It rounds and
  clamps rectangle edges independently, preserving shared edges for split
  viewports without gaps.
- `VPRC_RenderToWindow` applies that mapping only for the real window
  swapchain. External swapchains and explicitly bound output FBOs retain their
  existing coordinate contract.
- Vulkan ImGui accepts a live framebuffer/swapchain extent mismatch only
  during the validated scaled-present interactive-resize interval. ImGui keeps
  the current logical layout extent, while its viewport and clip rectangles
  are transformed into the fixed swapchain raster extent.
- Unrelated stale or mismatched ImGui snapshots are still rejected. This does
  not weaken the ordinary settled-frame ownership check.

### Final Composition Validation

Current held-resize captures:

`Build/_AgentValidation/mcp-sessions/continuous-resize-vulkan-scaling-20260723/mcp-captures-compose-held/`

- The per-monitor-DPI-aware harness oscillated the physical bottom-right
  border for 14 seconds without releasing the left mouse button.
- It recorded 30 mouse-down screenshots across 26 distinct window extents.
  All 30 image hashes were distinct, and the editor consumed another
  20,312.5 ms of process CPU time during those samples.
- Inspected captures at `1131x718`, `1087x693`, `1538x953`, and `1617x998`
  all retain the Hierarchy, Inspector, toolbar, and dock layout. The scene axes
  and geometry stay centered and proportionally projected in the current
  client area rather than occupying an offset subset of the fixed swapchain.
- The native overlay stays centered at every inspected extent. Consecutive
  `1617x998` captures taken while the cursor was stationary but the mouse was
  still down changed from `053hz 022.63ms` to `061hz 018.83ms`, including new
  CPU/GPU timing values. This directly proves that ordinary frame callbacks,
  UI publication, and presentation continue before mouse-up.
- `after_05_1617x998.png` matches the held layout after release; there is no
  one-frame positional jump when the exact-size swapchain converges.
- Two Vulkan MCP viewport captures from opposite camera positions contain
  different current scene views, ruling out a stale readback:
  `mcp-captures-compose/Screenshot_20260723_160027_826_438003d9167e48eda80fbde772c32ea7.png`
  and
  `mcp-captures-compose/Screenshot_20260723_160339_923_a1feac3da50f4aeab775720b6c344e4b.png`.
- The editor remained responsive through the stress interval. Its captured
  stdout/stderr contain no exception, device loss, Vulkan error, VUID, or
  validation-error text, and the named session stopped normally.

Focused validation:

- `XREngine.Runtime.Rendering.csproj`: 0 warnings, 0 errors.
- `WindowOwnershipContractTests`: 20 passed, 0 failed, including full-window
  and split-region edge mapping plus the scaled ImGui raster contract.
- The fresh full isolated editor build was temporarily blocked by unrelated
  in-progress physics work introducing an
  `XREngine.Scene.Physics.Debug` namespace that shadows the engine `Debug`
  type in PhysX files. To avoid altering that work, the already built current
  rendering assembly was hash-verified into the prior stopped isolated
  session before `-NoBuild` launch. The resize fix is entirely within that
  refreshed rendering assembly.

## Direct-Backbuffer Scene And Native-UI Regression

The user correctly reported that the composition pass above was still
incomplete: the 3D scene cropped incorrectly, and the native FPS overlay
retained the settled raster scale instead of re-layouting at the live dragged
extent.

The remaining path bypassed `VPRC_RenderToWindow`. The default pipeline's
final scene composition and its nested native screen-space UI pipeline both
push a display-resolution viewport directly onto the window backbuffer.
During Vulkan present scaling, that live rectangle occupied only a subset of
the fixed swapchain raster; WSI then stretched the entire raster. The earlier
region mapping therefore covered an alternate presentation command but not
the default pipeline used by this test.

`VPRC_PushViewportRenderArea` now applies the renderer's
presentation-to-backbuffer mapping when, and only when, a
display-resolution pass targets the real window backbuffer. Internal
resolution passes, explicit FBOs, and external/OpenXR swapchains retain their
existing target-coordinate contracts. This shared boundary covers the final
3D composite, native screen-space UI, and direct debug overlays.

Held-resize evidence:

- `mcp-captures-renderdoc-held/drag_03_1069x683.png` and
  `drag_25_1617x998.png` show the scene centered and fully composed at both
  ends of the drag, with ImGui still aligned to the current client area.
- Image analysis measured the native overlay at `896x112` pixels in the small
  capture and `868x108` in the large capture. Before this fix, equivalent
  captures measured `692x86` and `1036x129`, directly showing the old
  window-dependent glyph stretch.
- Consecutive mouse-down screenshots have different hashes and timing text;
  rendering and UI publication continue before mouse-up.
- `XREngine.Runtime.Rendering.csproj` and `XREngine.UnitTests.csproj` both
  built with 0 warnings and 0 errors using `--no-dependencies`, preserving
  unrelated in-progress physics files.
- `WindowOwnershipContractTests`: 20 passed, 0 failed.
- `rdc doctor` passes. Startup-layer and late-injection target control did not
  yield a transferable `.rdc` in this run, so no GPU-capture claim is made;
  the physical mouse-down screenshots are the validation evidence.

The named isolated editor session was stopped through
`Manage-McpEditorSession.ps1`.

## RenderDoc Tooling Follow-up

`Tools/Dependencies/Install-RenderDoc.ps1` now installs and validates RenderDoc
and the pinned, MIT-licensed `rdc-cli` 0.5.6 together. It persists both command
directories on the user `PATH`, bootstraps the replay Python module when
needed, and finishes with `rdc doctor`. The installer is available in the
`ExecTool.bat` dependency menu.

The current machine passes the full doctor check with RenderDoc 1.44,
`rdc-cli` 0.5.6, replay support, Visual Studio Build Tools, and a registered
Vulkan layer. A GPU capture was unnecessary for this final regression because
the out-of-process held-mouse screenshots directly prove the presentation and
layout behavior.

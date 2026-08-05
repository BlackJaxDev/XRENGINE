# ImGui Detached Platform Viewports

Date: 2026-08-04  
Status: completed and locally validated

## Problem

- OpenGL created detached ImGui windows, but their contents were clipped or
  scaled against the main editor window instead of the detached framebuffer.
- Vulkan did not create detached windows because its ImGui backend had no
  platform-window callbacks or per-window WSI/rendering resources.

## Root causes

The OpenGL renderer established the detached framebuffer viewport in the
platform callback, then entered `PushUiClipSpacePolicy` in the renderer callback.
Disposing that policy restored the engine's primary render region into the
secondary OpenGL context immediately before the ImGui draw.

The Vulkan backend's `RenderPlatformWindows` method was intentionally empty.
Supporting Dear ImGui viewports requires a native window and Vulkan surface,
swapchain, synchronization objects, command buffers, draw buffers, input
routing, and presentation for every detached viewport; none of that ownership
existed.

## Implemented solution

- OpenGL now enters the UI clip-space policy in the renderer callback, then
  re-establishes the detached framebuffer viewport immediately before drawing.
  It also disables framebuffer sRGB for the detached ImGui pass, matching the
  main ImGui target's color behavior.
- Vulkan now installs the shared ImGui platform/renderer callback bridge,
  enumerates monitors and DPI, routes detached-window input, and owns one native
  window plus WSI resource bundle per detached viewport.
- Vulkan renderer callbacks capture immutable ImGui draw snapshots. The frame
  loop submits them after the primary scene submission and before primary
  presentation, preserving graphics-queue ordering for sampled engine textures.
- Detached Vulkan command/upload buffers follow frame slots and their fences;
  image views and present semaphores follow acquired swapchain images. Fence
  completion is published to the engine lifetime tracker before reuse.
- Resize, out-of-date, and failed post-acquire recording paths request swapchain
  recreation so an acquired image cannot remain stranded.
- Command-pool teardown clears renderer bind/lifetime state without retaining a
  destroyed-handle tombstone; Vulkan drivers may immediately recycle the native
  command-buffer handle during swapchain recreation.
- Restored layout callbacks recover platform windows by viewport ID when
  `PlatformUserData` is transiently unavailable. Windows visibility is verified
  natively and restored without activation when GLFW leaves the Vulkan window
  hidden.
- Vulkan multi-viewports are enabled only for desktop dynamic-rendering targets
  with a compatible primary swapchain format. Unsupported modes emit a visible
  diagnostic.

## Validation evidence

- `rdc doctor`: RenderDoc CLI, replay, Vulkan layer, and OpenGL injection checks
  passed before the investigation.
- `dotnet build XREngine.Runtime.Rendering.OpenGL/XREngine.Runtime.Rendering.OpenGL.csproj --no-restore`:
  passed with zero warnings and zero errors.
- `dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`:
  passed with zero warnings and zero errors.
- Isolated OpenGL session `imgui-viewports-opengl`: detached Hierarchy panel was
  moved/resized to 800x600 and visually inspected at
  `Build/_AgentValidation/20260804-imgui-multi-viewport/mcp-captures/opengl-detached-hierarchy.png`.
- Isolated Vulkan session `imgui-viewports-vulkan`: the saved detached Hierarchy
  window restored visibly without manual intervention, survived four
  consecutive resize/swapchain recreations and a return to its saved 2096x1016
  size, and rendered continuously. The final exact-code image is
  `Build/_AgentValidation/20260804-imgui-multi-viewport/mcp-captures/vulkan-detached-hierarchy-final-native-size.png`.
- Final Vulkan logs contained no detached-window acquire, present,
  command-buffer reuse, teardown, or multi-viewport validation errors. Ten
  pre-existing `VUID-VkImageMemoryBarrier2-image-03320` depth/stencil barrier
  messages came from the scene renderer and are unrelated to platform windows.
  The final OpenGL/Vulkan rendering and API logs were copied to
  `Build/_AgentValidation/20260804-imgui-multi-viewport/logs/`.
- Both named editor sessions were stopped through the session manager. The
  ignored Unit Testing World backend setting was restored to OpenGL.

No automated tests were added because repository policy requires explicit user
clearance after the feature's live/runtime path is functionally validated. User
confirmation on their normal editor layout is not yet recorded.

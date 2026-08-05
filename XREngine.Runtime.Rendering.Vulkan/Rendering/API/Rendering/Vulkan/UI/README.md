# Vulkan UI

Owns Vulkan-specific ImGui input routing, clipboard integration, immutable draw
snapshots, GPU resources/rendering, and texture registration.
`VulkanImGuiBackend` reuses the shared descriptor, upload, command, and
retirement authorities; it must not create parallel caches or use
`VulkanRenderer` as a service locator.

## Platform viewports

`VulkanImGuiMultiViewportController` installs Dear ImGui's platform and
renderer callbacks for desktop swapchain targets. Each detached viewport owns a
native Silk.NET window plus its surface, swapchain, image views, command pool,
frame fences, and acquire/present semaphores. The renderer callback captures an
immutable draw snapshot; `PresentSubmittedDesktopFrame` submits those snapshots
after the primary scene submission so same-queue ordering makes renderer-owned
textures available to detached panels.

Command buffers and upload buffers are indexed by frame slot and reused only
after their matching fence completes. Swapchain image views and
render-finished semaphores remain indexed by acquired image. Resize or a failed
post-acquire recording path recreates the viewport swapchain so an acquired
image cannot be stranded.

Callback lookup primarily uses `PlatformUserData`, with the core-owned viewport
ID registry as the recovery authority for restored layouts. On Windows, the
show callback also verifies native visibility and uses a no-activation Win32
show when GLFW leaves a restored Vulkan window hidden.

Detached Vulkan viewports currently require dynamic rendering and the primary
swapchain's format/color space. When either requirement is unavailable, the
backend leaves multi-viewports disabled and emits a diagnostic instead of
silently creating an incompatible path.

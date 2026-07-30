# Vulkan UI

Owns Vulkan-specific ImGui input routing, clipboard integration, immutable draw
snapshots, GPU resources/rendering, and texture registration.
`VulkanImGuiBackend` reuses the shared descriptor, upload, command, and
retirement authorities; it must not create parallel caches or use
`VulkanRenderer` as a service locator.

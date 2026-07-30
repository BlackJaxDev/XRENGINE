# Vulkan OpenXR Backend

Owns Vulkan graphics binding, external swapchain image resources, per-eye
recording, mirror/preview copies, Vulkan submission gating, and immutable XR
diagnostics. Generic OpenXR session, input, pose, and pacing policy remains
outside the Vulkan leaf.

`VulkanOpenXrBackend` uses the shared device, command, render-graph, descriptor,
resource-lifetime, and desktop activity contracts. Per-thread execution state
uses an explicit `ThreadLocal<VulkanOpenXrThreadExecutionState>` owner; ordinary
XR recording does not use thread-static ambient fields.

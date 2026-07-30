# Vulkan Command Synchronization

Owns native barrier emission from immutable `VulkanBarrierPlan` data and
tracked command-buffer synchronization state. Backend-neutral dependency
collection and Vulkan usage mapping belong to `RenderGraph/`; this folder only
records the resulting Vulkan barriers and ownership transitions.

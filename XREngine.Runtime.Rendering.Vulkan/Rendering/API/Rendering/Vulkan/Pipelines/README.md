# Vulkan Pipelines

Owns render-pass compatibility, graphics/compute pipeline creation, shared
pipeline and library caches, compile queues, prewarm database policy, and
render-target mode selection. `VulkanPipelineManager` owns device-lifetime
cache, deferred-link, and prewarm autosave state. Wrappers access those services
through `VulkanBackendObjectContext.Pipelines`.

# Vulkan Command Recording

Namespace-level recording owners live in `XREngine.Rendering.Vulkan.Commands`.
`VulkanRenderScopeController` owns active render-scope compatibility and
lifetime. Domain partials emit draw/compute, clear/publication, and related
commands from `VulkanCommandRecordingContext` without hidden thread-static
state. `VulkanRenderer.ComputePreparation.cs` materializes compute pipelines,
persistent uniform buffers, and reusable descriptor sets before the guarded
recording scope begins. Transfer and barrier emission remain in their sibling
folders.
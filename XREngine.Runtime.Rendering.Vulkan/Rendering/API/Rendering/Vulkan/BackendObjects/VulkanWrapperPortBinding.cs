namespace XREngine.Rendering.Vulkan;

/// <summary>Exact deferred port set retained by a single backend wrapper.</summary>
internal sealed class VulkanWrapperPortBinding(
    VulkanProgramCreationPort? programCreation,
    VulkanProgramPlannerPort? programPlanner,
    VulkanProgramCommandOperations? programCommandOperations,
    VulkanProgramTelemetryPort? programTelemetry,
    VulkanResourceCommandWrapperPort? resourceCommands,
    VulkanResourcePublicationPort? resourcePublications,
    VulkanFinalPresentationDescriptorPort? finalPresentationDescriptors,
    VulkanMeshOperationRequestQueue? meshRequests,
    VulkanWrapperLookupPort lookup)
{
    internal void AttachPlannerOperationHandlers(VkRenderProgram program)
        => programPlanner?.Attach(program);
    internal VulkanProgramCreationPort? TryGetProgramCreation() => programCreation;
    internal VulkanProgramPlannerPort? TryGetProgramPlanner() => programPlanner;
    internal VulkanProgramCommandOperations? TryGetProgramCommandOperations()
        => programCommandOperations;
    internal VulkanProgramTelemetryPort? TryGetProgramTelemetry() => programTelemetry;
    internal VulkanResourceCommandWrapperPort? TryGetResourceCommands() => resourceCommands;
    internal VulkanResourcePublicationPort? TryGetResourcePublications() => resourcePublications;
    internal VulkanFinalPresentationDescriptorPort? TryGetFinalPresentationDescriptors()
        => finalPresentationDescriptors;
    internal VulkanMeshOperationRequestQueue? TryGetMeshRequests() => meshRequests;
    internal VulkanWrapperLookupPort Lookup => lookup;
}

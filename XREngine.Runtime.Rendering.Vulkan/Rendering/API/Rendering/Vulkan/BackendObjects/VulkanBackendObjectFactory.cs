using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Generation-local wrapper factory. It owns deferred behavior ports so the
/// identity-only backend context cannot become an all-authorities locator.
/// </summary>
internal sealed class VulkanBackendObjectFactory
{
    // Deliberately stateless.  Wrapper creation receives its generation-local
    // context at the call boundary so this helper cannot retain a cold
    // composition graph through a resource runtime.


    internal AbstractRenderAPIObject GetOrCreate(
        VulkanBackendObjectContext context,
        GenericRenderObject renderObject,
        VulkanWrapperPortBinding binding,
        bool generateNow = false)
    {
        ArgumentNullException.ThrowIfNull(renderObject);
        AbstractRenderAPIObject wrapper = context.Resources.BackendObjects.Get(renderObject) ?? Create(context, renderObject, binding);
        if (generateNow && !wrapper.IsGenerated)
            wrapper.Generate();
        return wrapper;
    }

    internal static void Remove(VulkanBackendObjectContext context, GenericRenderObject renderObject)
        => context.Resources.BackendObjects.Remove(renderObject);

    internal static void ConfigureDeviceServices(
        VulkanBackendObjectContext context,
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commandRuntime,
        RenderGraph.VulkanFramePlanner framePlanner,
        VulkanFrameTelemetry telemetry,
        bool allowSynchronousResourceUploads)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(commandRuntime);
        ArgumentNullException.ThrowIfNull(framePlanner);
        ArgumentNullException.ThrowIfNull(telemetry);
        VulkanResourceRuntime resources = context.Resources;
        VulkanWrapperLookupPort lookup = resources.WrapperLookup;
        VulkanWrapperColdComposition composition = resources.WrapperColdComposition;
        resources.Descriptors.ConfigureDeviceServices(context, telemetry, lookup);
        resources.PublishSynchronousUploadPolicy(allowSynchronousResourceUploads);
        context.PublishDeviceContext(deviceContext);
        resources.Queries.BindBackendContext(context);
        resources.PipelineManager.PublishDeviceContext(context.Api, deviceContext);
        VulkanProgramCreationPort programCreation = new(context);
        VulkanResourceCommandWrapperPort resourceCommands = new(context, commandRuntime, resources, telemetry);
        VulkanResourcePublicationPort resourcePublications = new(
            framePlanner.ResourcePublications,
            commandRuntime.ThreadWorkspace);
        composition.PublishProgramCreation(programCreation);
        composition.PublishProgramPlanner(new(framePlanner));
        composition.PublishProgramCommandOperations(commandRuntime);
        composition.PublishProgramTelemetry(telemetry);
        composition.PublishResourcePublications(resourcePublications);
        resources.ConfigureWrapperOperationServices(
            resourceCommands,
            framePlanner.ResourcePublications);
        resources.PipelineManager.PublishProgramServices(programCreation);
        composition.PublishResourceCommands(resourceCommands);
    }

    internal static void ConfigureMeshServices(
        VulkanWrapperColdComposition composition,
        VulkanMeshOperationRequestQueue meshRequests,
        VulkanFinalPresentationDescriptorPort finalPresentationDescriptors)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(meshRequests);
        ArgumentNullException.ThrowIfNull(finalPresentationDescriptors);
        composition.PublishFinalPresentationDescriptors(finalPresentationDescriptors);
        composition.PublishMeshRequests(meshRequests);
    }

    private static AbstractRenderAPIObject Create(
        VulkanBackendObjectContext context,
        GenericRenderObject renderObject,
        VulkanWrapperPortBinding binding)
    {
        AbstractRenderAPIObject wrapper = context.CreateIdentityWrapper(renderObject);
        VkObjectBase vulkanWrapper = (VkObjectBase)wrapper;
        vulkanWrapper.BindDeferredPorts(binding);
        context.Resources.BackendObjects.PublishIdentity(renderObject, vulkanWrapper);
        try
        {
            vulkanWrapper.CompleteConstruction();
        }
        catch
        {
            context.Resources.BackendObjects.RemoveIdentity(renderObject, vulkanWrapper);
            throw;
        }
        return wrapper;
    }
}

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
        if (renderObject is XRDataBuffer buffer)
            buffer.EnsureOwnerFirstConstructionCompleted();
        if (!renderObject.IsApiWrapperPublicationReady)
            throw new InvalidOperationException(
                $"Render object '{renderObject.GetType().Name}' cannot be wrapped before CPU construction is published.");
        AbstractRenderAPIObject wrapper;
        VulkanBackendObjectRegistry registry = context.Resources.BackendObjects;
        lock (registry.IdentityCreationLock)
            wrapper = registry.Get(renderObject) ?? Create(context, renderObject, binding);
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
        composition.PublishProgramPlanner(new(framePlanner, commandRuntime.ThreadWorkspace));
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
        bool recordMeshPublication = renderObject is XRDataBuffer { IsMeshOwnedBuffer: true };
        long publicationStart = recordMeshPublication ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        bool succeeded = false;
        try
        {
            AbstractRenderAPIObject wrapper = context.CreateIdentityWrapper(renderObject);
            VkObjectBase vulkanWrapper = (VkObjectBase)wrapper;
            vulkanWrapper.BindDeferredPorts(binding);
            try
            {
                vulkanWrapper.CompleteConstruction();
                context.Resources.BackendObjects.PublishIdentity(renderObject, vulkanWrapper);
                if (vulkanWrapper.IsRetired || renderObject.IsDestroyed || !renderObject.IsApiWrapperPublicationReady)
                {
                    context.Resources.BackendObjects.RemoveIdentity(renderObject, vulkanWrapper);
                    vulkanWrapper.Retire();
                    throw new InvalidOperationException(
                        $"Render object '{renderObject.GetType().Name}' was destroyed while its Vulkan wrapper was being published.");
                }
            }
            catch
            {
                context.Resources.BackendObjects.RemoveIdentity(renderObject, vulkanWrapper);
                vulkanWrapper.Retire();
                throw;
            }
            succeeded = true;
            return wrapper;
        }
        finally
        {
            if (recordMeshPublication)
            {
                XRMeshCpuPreparationTelemetry.RecordWrapperCreation(
                    EMeshWrapperBackend.Vulkan,
                    System.Diagnostics.Stopwatch.GetTimestamp() - publicationStart,
                    succeeded);
            }
        }
    }
}

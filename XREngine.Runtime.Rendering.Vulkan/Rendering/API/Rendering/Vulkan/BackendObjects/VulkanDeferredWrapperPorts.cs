namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One-time publication cell for wrapper behavior ports. The cell is created
/// with wrapper identity, before logical-device publication, and is owned by
/// the wrapper factory rather than <see cref="VulkanBackendObjectContext"/>.
/// </summary>
internal sealed class VulkanWrapperColdComposition(VulkanWrapperLookupPort lookup)
{
    private readonly VulkanWrapperLookupPort _lookup = lookup;
    private VulkanProgramCreationPort? _programCreation;
    private VulkanProgramPlannerPort? _programPlanner;
    private VulkanProgramCommandOperations? _programCommandOperations;
    private readonly VulkanProgramTelemetryPort _programTelemetry = new();
    private VulkanResourceCommandWrapperPort? _resourceCommands;
    private VulkanResourcePublicationPort? _resourcePublications;
    private VulkanFinalPresentationDescriptorPort? _finalPresentationDescriptors;
    private VulkanMeshOperationRequestQueue? _meshRequests;

    /// <summary>Pipeline/layout/descriptor-creation services only.</summary>
    internal VulkanProgramCreationPort ProgramCreation
        => Volatile.Read(ref _programCreation) ?? throw new InvalidOperationException("Vulkan program creation port has not been published.");
    /// <summary>Command binding and recording services only.</summary>
    internal VulkanProgramCommandOperations ProgramCommandOperations
        => Volatile.Read(ref _programCommandOperations) ?? throw new InvalidOperationException("Vulkan program command operations have not been published.");
    internal VulkanProgramPlannerPort ProgramPlanner
        => Volatile.Read(ref _programPlanner) ?? throw new InvalidOperationException("Vulkan program planner port has not been published.");
    /// <summary>CPU profiling services only.</summary>
    internal VulkanProgramTelemetryPort ProgramTelemetry => _programTelemetry;
    internal VulkanResourceCommandWrapperPort ResourceCommands
        => Volatile.Read(ref _resourceCommands) ?? throw new InvalidOperationException("Vulkan resource command wrapper port has not been published.");
    internal VulkanMeshOperationRequestQueue MeshRequests
        => Volatile.Read(ref _meshRequests) ?? throw new InvalidOperationException("Vulkan mesh request queue has not been published.");

    internal void PublishProgramCreation(VulkanProgramCreationPort port)
        => Publish(ref _programCreation, port, "program creation");

    internal void PublishProgramCommandOperations(VulkanCommandRuntime commandRuntime)
        => Publish(ref _programCommandOperations, new VulkanProgramCommandOperations(commandRuntime), "program command operations");

    internal void PublishProgramPlanner(VulkanProgramPlannerPort port)
        => Publish(ref _programPlanner, port, "program planner");

    internal void PublishProgramTelemetry(VulkanFrameTelemetry telemetry)
        => _programTelemetry.Publish(telemetry);

    internal void PublishResourceCommands(VulkanResourceCommandWrapperPort port)
        => Publish(ref _resourceCommands, port, "resource command");

    internal void PublishResourcePublications(VulkanResourcePublicationPort port)
        => Publish(ref _resourcePublications, port, "resource publication");
    internal void PublishFinalPresentationDescriptors(VulkanFinalPresentationDescriptorPort port)
        => Publish(ref _finalPresentationDescriptors, port, "final presentation descriptor");
    internal void PublishMeshRequests(VulkanMeshOperationRequestQueue queue)
        => Publish(ref _meshRequests, queue, "mesh requests");

    private static void Publish<T>(ref T? destination, T value, string name) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        T? current = Interlocked.CompareExchange(ref destination, value, null);
        if (current is not null && !ReferenceEquals(current, value))
            throw new InvalidOperationException($"The Vulkan {name} wrapper port was already published for this generation.");
    }

    /// <summary>
    /// Captures only the deferred ports required by one wrapper family. The
    /// factory owns the aggregate publisher; individual wrappers never retain
    /// that aggregate as a service locator.
    /// </summary>
    internal VulkanWrapperPortBinding CreateBinding(GenericRenderObject renderObject)
        => renderObject switch
        {
            XRRenderProgram => new(ProgramCreation, ProgramPlanner, ProgramCommandOperations, ProgramTelemetry, null, null, null, null, _lookup),
            XRMeshRenderer.BaseVersion => new(ProgramCreation, ProgramPlanner, ProgramCommandOperations, ProgramTelemetry, null, null, _finalPresentationDescriptors, MeshRequests, _lookup),
            XRRenderProgramPipeline or XRShader => new(ProgramCreation, null, null, null, null, null, null, null, _lookup),
            XRTransformFeedback => new(null, ProgramPlanner, null, null, null, null, null, null, _lookup),
            XRFrameBuffer => new(null, null, ProgramCommandOperations, null, null, null, null, null, _lookup),
            _ => new(null, null, null, null, ResourceCommands, _resourcePublications, null, null, _lookup),
        };

    /// <summary>Explicit cold-boundary creation; never exposed by a retained lookup.</summary>
    internal AbstractRenderAPIObject GetOrCreate(
        VulkanBackendObjectFactory factory,
        VulkanBackendObjectContext context,
        GenericRenderObject renderObject,
        bool generateNow = false)
        => factory.GetOrCreate(context, renderObject, CreateBinding(renderObject), generateNow);
}

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

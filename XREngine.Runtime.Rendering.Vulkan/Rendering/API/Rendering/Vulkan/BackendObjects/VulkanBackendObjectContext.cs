using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow device and identity services shared by backend wrappers from one
/// renderer generation.
/// </summary>
internal sealed class VulkanBackendObjectContext(
    Vk api,
    VulkanDeviceContext? deviceContext,
    VulkanBackendObjectRegistry registry,
    VulkanResourceLifetimeTracker lifetime,
    VulkanDescriptorManager descriptors,
    VulkanBufferResourceService buffers,
    VulkanImageResourceService images,
    VulkanResourcePlannerService planner,
    VulkanSamplerResourceService samplers,
    VulkanQueryAuthority queries,
    VulkanPipelineManager pipelines,
    VulkanResourceRuntime resources,
    bool allowSynchronousResourceUploads) : IRenderApiWrapperOwner
{
    private VulkanDeviceContext? _deviceContext = deviceContext;

    public Vk Api { get; } = api;
    internal VulkanDeviceContext DeviceContext => RequireDeviceContext();
    public Device Device => RequireDeviceContext().Device;
    public PhysicalDevice PhysicalDevice => RequireDeviceContext().PhysicalDevice;
    public bool IsLogicalDeviceReady => _deviceContext?.IsReady == true;
    public bool IsDeviceOperational => _deviceContext?.IsOperational == true;
    internal bool UsesDynamicRenderingRenderTargets
        => _deviceContext?.MutableCapabilities._useDynamicRenderingRenderTargets == true;
    public bool Supports(EVulkanDeviceCapability capability)
        => _deviceContext?.Capabilities.Supports(capability) == true;
    public ExtTransformFeedback? TransformFeedbackExtension
        => _deviceContext?.ExtensionFunctions.ExtTransformFeedback;
    public bool SupportsTransformFeedback
        => Supports(EVulkanDeviceCapability.TransformFeedback) &&
           TransformFeedbackExtension is not null;
    public bool SupportsTransformFeedbackGeometryStreams
        => SupportsTransformFeedback &&
           DeviceContext.MutableCapabilities._supportsTransformFeedbackGeometryStreams;
    public bool SupportsTransformFeedbackQueries
        => SupportsTransformFeedback &&
           DeviceContext.MutableCapabilities._supportsTransformFeedbackQueries;
    public bool SupportsTransformFeedbackDraw
        => SupportsTransformFeedback &&
           DeviceContext.MutableCapabilities._supportsTransformFeedbackDraw;
    public PhysicalDeviceTransformFeedbackPropertiesEXT TransformFeedbackProperties
        => DeviceContext.MutableCapabilities._transformFeedbackProperties;
    public VulkanBackendObjectRegistry Registry { get; } = registry;
    public VulkanBindingAllocator BindingAllocator => Registry.BindingAllocator;
    public VulkanResourceLifetimeTracker Lifetime { get; } = lifetime;
    public VulkanDescriptorManager Descriptors { get; } = descriptors;
    public VulkanDescriptorLifetimeAuthority DescriptorLifetime => Resources.DescriptorLifetime;
    public VulkanFallbackTextureAuthority FallbackTexture => Resources.FallbackTexture;
    public VulkanBufferResourceService Buffers { get; } = buffers;
    public VulkanImageResourceService Images { get; } = images;
    public VulkanResourcePlannerService Planner { get; } = planner;
    public VulkanQueryAuthority Queries
    {
        get
        {
            queries.BindBackendContext(this);
            return queries;
        }
    }
    public VulkanPipelineManager Pipelines
    {
        get
        {
            pipelines.PublishDeviceContext(Api, _deviceContext);
            return pipelines;
        }
    }
    public VulkanSamplerResourceService Samplers { get; } = samplers;
    /// <summary>
    /// Generation-local framebuffer and legacy render-pass ownership.  Wrappers
    /// use this authority rather than retaining the renderer facade.
    /// </summary>
    internal VulkanResourceRuntime Resources { get; } = resources;
    /// <summary>
    /// Immutable resource-publication policy captured for this backend generation.
    /// Wrapper code reads it from the context rather than retaining the renderer.
    /// </summary>
    internal bool AllowSynchronousResourceUploads { get; } = allowSynchronousResourceUploads;
    internal VulkanFrameBufferResourceService Framebuffers => Resources.Framebuffers;
    /// <summary>
    /// Program-wrapper operations that need command or frame identity services.
    /// The service is bound during renderer composition and retains only concrete
    /// generation authorities, never the renderer facade.
    /// </summary>
    internal VulkanProgramBackendServices ProgramServices
        => _programServices ?? throw new InvalidOperationException(
            "The Vulkan program services have not been configured.");

    /// <summary>
    /// Renderer-free synchronous command service for wrapper-owned uploads and
    /// layout work.
    /// </summary>
    internal VulkanResourceCommandService ResourceCommands
        => _resourceCommands ?? throw new InvalidOperationException(
            "The Vulkan resource command service has not been configured.");

    /// <summary>
    /// Mesh-wrapper-specific descriptor and presentation observation services.
    /// These are configured from concrete generation authorities after frame
    /// composition is available; they never retain the renderer facade.
    /// </summary>
    internal VulkanMeshBackendServices MeshServices
        => _meshServices ?? throw new InvalidOperationException(
            "The Vulkan mesh services have not been configured.");

    private VulkanProgramBackendServices? _programServices;
    private VulkanResourceCommandService? _resourceCommands;
    private VulkanMeshBackendServices? _meshServices;

    /// <summary>
    /// Gets the generation currently published for a native resource. Wrapper
    /// fingerprints use this generation-local lifetime state instead of asking
    /// the renderer facade.
    /// </summary>
    public ulong GetResourceGeneration(ObjectType type, ulong handle)
        => Lifetime.GetPublishedGeneration(new VulkanResourceLifetimeKey(type, handle));

    /// <summary>
    /// Resolves the buffer retained by an interned buffer view from the lifetime
    /// ledger. Descriptor fingerprints must include both generations.
    /// </summary>
    public bool TryGetBufferViewBackingBuffer(BufferView bufferView, out Silk.NET.Vulkan.Buffer buffer)
    {
        if (bufferView.Handle != 0)
        {
            lock (Lifetime.SyncRoot)
            {
                if (Lifetime.BufferViewBackingBuffers.TryGetValue(bufferView.Handle, out ulong handle) &&
                    handle != 0)
                {
                    buffer = new Silk.NET.Vulkan.Buffer(handle);
                    return true;
                }
            }
        }

        buffer = default;
        return false;
    }

    public string RenderApiWrapperOwnerName => "VulkanBackendObjectContext";

    /// <summary>
    /// Creates generation-local wrapper objects without retaining a renderer
    /// reference. The renderer cache may still choose this factory while the
    /// generic cache migration is in progress.
    /// </summary>
    public AbstractRenderAPIObject? GetOrCreateAPIRenderObject(
        GenericRenderObject renderObject,
        bool generateNow = false)
    {
        ArgumentNullException.ThrowIfNull(renderObject);
        AbstractRenderAPIObject wrapper = Registry.Get(renderObject) ?? CreateWrapper(renderObject);
        if (generateNow && !wrapper.IsGenerated)
            wrapper.Generate();
        return wrapper;
    }

    public void RemoveAPIRenderObject(GenericRenderObject renderObject)
        => Registry.Remove(renderObject);

    internal AbstractRenderAPIObject CreateWrapper(GenericRenderObject renderObject)
        => renderObject switch
        {
            XRMaterial data => new VkMaterial(this, data),
            XRMeshRenderer.BaseVersion data => new VkMeshRenderer(this, data),
            XRRenderProgramPipeline data => new VkRenderProgramPipeline(this, data),
            XRRenderProgram data => new VkRenderProgram(this, data),
            XRDataBuffer data => new VkDataBuffer(this, data),
            XRSampler data => new VkSampler(this, data),
            XRShader data => new VkShader(this, data),
            XRRenderBuffer data => new VkRenderBuffer(this, data),
            XRFrameBuffer data => new VkFrameBuffer(this, data),
            XRTexture1D data => new VkTexture1D(this, this, data),
            XRTexture1DArray data => new VkTexture1DArray(this, this, data),
            XRTextureViewBase data => new VkTextureView(this, this, data),
            XRTexture2D data => new VkTexture2D(this, this, data),
            XRTexture2DArray data => new VkTexture2DArray(this, this, data),
            XRTextureRectangle data => new VkTextureRectangle(this, this, data),
            XRTexture3D data => new VkTexture3D(this, this, data),
            XRTextureCube data => new VkTextureCube(this, this, data),
            XRTextureCubeArray data => new VkTextureCubeArray(this, this, data),
            XRTextureBuffer data => new VkTextureBuffer(this, this, data),
            XRRenderQuery data => new VkRenderQuery(this, data),
            XRTransformFeedback data => new VkTransformFeedback(this, this, data),
            _ => throw new InvalidOperationException($"Render object type {renderObject.GetType()} is not supported."),
        };

    /// <summary>
    /// Compatibility entry point for texture-owned samplers. New standalone sampler
    /// wrappers consume <see cref="Samplers"/> directly.
    /// </summary>
    public void RegisterSampler(Sampler sampler, in SamplerCreateInfo createInfo, string owner)
        => Samplers.Register(sampler, in createInfo, owner);

    /// <summary>
    /// Completes staged bootstrap when wrappers are requested by the base renderer
    /// constructor before the Vulkan device authority has been created.
    /// </summary>
    public void PublishDeviceContext(VulkanDeviceContext deviceContext)
    {
        ArgumentNullException.ThrowIfNull(deviceContext);
        VulkanDeviceContext? current = Interlocked.CompareExchange(
            ref _deviceContext,
            deviceContext,
            comparand: null);
        if (current is not null && !ReferenceEquals(current, deviceContext))
            throw new InvalidOperationException("The Vulkan backend object context already owns a different device context.");
        Pipelines.PublishDeviceContext(Api, deviceContext);
    }

    internal void ConfigureProgramServices(
        VulkanCommandRuntime commandRuntime,
        RenderGraph.VulkanFramePlanner framePlanner,
        VulkanFrameTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(commandRuntime);
        ArgumentNullException.ThrowIfNull(framePlanner);
        ArgumentNullException.ThrowIfNull(telemetry);

        VulkanProgramBackendServices services = new(this, commandRuntime, framePlanner, telemetry);
        VulkanProgramBackendServices? current = Interlocked.CompareExchange(
            ref _programServices,
            services,
            comparand: null);
        if (current is not null && !ReferenceEquals(current.CommandRuntime, commandRuntime))
            throw new InvalidOperationException("The Vulkan program services already own a different command runtime.");

        Pipelines.PublishProgramServices(current ?? services);

        Planner.BindFramePlanner(framePlanner);

        VulkanResourceCommandService resourceCommands = new(this, commandRuntime, Resources, telemetry);
        VulkanResourceCommandService? existingCommands = Interlocked.CompareExchange(
            ref _resourceCommands,
            resourceCommands,
            comparand: null);
        if (existingCommands is not null && !ReferenceEquals(existingCommands.CommandRuntime, commandRuntime))
            throw new InvalidOperationException("The Vulkan resource command service already owns a different command runtime.");
    }

    internal void ConfigureMeshServices(
        VulkanCommandRuntime commandRuntime,
        RenderGraph.VulkanFramePlanner framePlanner,
        VulkanOutputRuntime outputRuntime,
        VulkanFrameLoop frameLoop,
        VulkanFrameOperationQueue operationQueue,
        VulkanFrameTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(commandRuntime);
        ArgumentNullException.ThrowIfNull(framePlanner);
        ArgumentNullException.ThrowIfNull(outputRuntime);
        ArgumentNullException.ThrowIfNull(frameLoop);
        ArgumentNullException.ThrowIfNull(operationQueue);
        ArgumentNullException.ThrowIfNull(telemetry);

        VulkanMeshBackendServices services = new(
            this,
            commandRuntime,
            framePlanner,
            outputRuntime,
            frameLoop,
            operationQueue,
            telemetry);
        VulkanMeshBackendServices? current = Interlocked.CompareExchange(
            ref _meshServices,
            services,
            comparand: null);
        if (current is not null && !ReferenceEquals(current, services))
            throw new InvalidOperationException("The Vulkan mesh services are already configured.");
    }

    private VulkanDeviceContext RequireDeviceContext()
        => _deviceContext
            ?? throw new InvalidOperationException("The Vulkan device context has not been published to backend objects yet.");
}

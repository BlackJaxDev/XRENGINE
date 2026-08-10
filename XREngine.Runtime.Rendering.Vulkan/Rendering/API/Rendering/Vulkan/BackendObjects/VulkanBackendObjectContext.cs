using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow device and identity services shared by backend wrappers from one
/// renderer generation.
/// </summary>
internal sealed class VulkanBackendObjectContext(
    Vk api,
    VulkanDeviceContext? deviceContext,
    VulkanResourceRuntime resources) : IRenderApiWrapperOwner
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
    /// <summary>
    /// Generation-local framebuffer and legacy render-pass ownership.  Wrappers
    /// use this authority rather than retaining the renderer facade.
    /// </summary>
    internal VulkanResourceRuntime Resources { get; } = resources;
    /// <summary>
    /// Gets the generation currently published for a native resource. Wrapper
    /// fingerprints use this generation-local lifetime state instead of asking
    /// the renderer facade.
    /// </summary>
    public ulong GetResourceGeneration(ObjectType type, ulong handle)
        => Resources.Lifetime.Tracker.GetPublishedGeneration(new VulkanResourceLifetimeKey(type, handle));

    /// <summary>
    /// Resolves the buffer retained by an interned buffer view from the lifetime
    /// ledger. Descriptor fingerprints must include both generations.
    /// </summary>
    public bool TryGetBufferViewBackingBuffer(BufferView bufferView, out Silk.NET.Vulkan.Buffer buffer)
    {
        if (bufferView.Handle != 0)
        {
            lock (Resources.Lifetime.Tracker.SyncRoot)
            {
                if (Resources.Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(bufferView.Handle, out ulong handle) &&
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
    /// Compatibility lookup for wrapper-owned engine callbacks. Creation is
    /// deliberately performed only by <see cref="VulkanBackendObjectFactory"/>,
    /// which supplies the explicit deferred behavior-port cell. Returning a
    /// published wrapper here therefore cannot recreate the former locator path.
    /// </summary>
    public AbstractRenderAPIObject? GetOrCreateAPIRenderObject(
        GenericRenderObject renderObject,
        bool generateNow = false)
    {
        ArgumentNullException.ThrowIfNull(renderObject);
        AbstractRenderAPIObject? wrapper = Resources.BackendObjects.Get(renderObject);
        if (wrapper is not null && generateNow && !wrapper.IsGenerated)
            wrapper.Generate();
        return wrapper;
    }

    public void RemoveAPIRenderObject(GenericRenderObject renderObject)
        => Resources.BackendObjects.Remove(renderObject);

    /// <summary>Creates wrapper identity only; behavior ports are factory-owned.</summary>
    internal AbstractRenderAPIObject CreateIdentityWrapper(GenericRenderObject renderObject)
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
    /// wrappers consume the resource authority directly.
    /// </summary>
    public void RegisterSampler(Sampler sampler, in SamplerCreateInfo createInfo, string owner)
        => Resources.Samplers.Register(sampler, in createInfo, owner);

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
    }

    private VulkanDeviceContext RequireDeviceContext()
        => _deviceContext
            ?? throw new InvalidOperationException("The Vulkan device context has not been published to backend objects yet.");
}

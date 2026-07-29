namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The native Vulkan object category represented by a backend wrapper.
/// </summary>
public enum VkObjectType
{
    Buffer,
    ShaderModule,
    BufferView,
    Device, //Internally handled
    DescriptorPool, //Internally handled
    CommandPool, //Internally handled
    DescriptorUpdateTemplate, //Internally handled
    Sampler,
    Image,
    DescriptorSetLayout,
    Framebuffer,
    Event,
    Fence, //Internally handled
    ImageView,
    Instance,
    Pipeline,
    PipelineCache,
    PipelineLayout,
    PrivateDataSlot, //Internally handled
    QueryPool,
    RenderPass, //Internally handled
    SamplerYcbcrConversion, //Internally handled
    Semaphore, //Internally handled
    Program,
    ProgramPipeline,
    Renderbuffer,
    Query,
    Texture,
    TransformFeedback,
    Material,
    MeshRenderer,
}

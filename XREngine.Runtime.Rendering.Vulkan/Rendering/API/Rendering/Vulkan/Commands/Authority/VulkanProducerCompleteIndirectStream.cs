namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanProducerCompleteIndirectStream(
    XRDataBuffer IndirectBuffer,
    XRDataBuffer? ParameterBuffer,
    ulong IndirectBufferIdentity,
    ulong ParameterBufferIdentity);

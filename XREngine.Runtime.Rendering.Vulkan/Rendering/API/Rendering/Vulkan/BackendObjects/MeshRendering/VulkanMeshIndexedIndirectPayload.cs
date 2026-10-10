using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Captures one indexed indirect command and its input topology.</summary>
internal readonly record struct VulkanMeshIndexedIndirectPayload(
    XRDataBuffer Arguments,
    nuint ByteOffset,
    EPrimitiveType Topology,
    IRenderResourceLeaseOwner? AuthoringLease);

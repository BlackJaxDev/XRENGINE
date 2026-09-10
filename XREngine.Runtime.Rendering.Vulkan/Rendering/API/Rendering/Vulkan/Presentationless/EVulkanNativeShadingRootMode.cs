namespace XREngine.Rendering.Vulkan;

/// <summary>Parameter source for the native Advanced opaque shading pilot.</summary>
public enum EVulkanNativeShadingRootMode
{
    /// <summary>Push the existing 64-byte parameter block for each dispatch.</summary>
    Immediate,
    /// <summary>Push a typed GPU address and dispatch constants into a retained frame allocation.</summary>
    BufferDeviceAddress,
}

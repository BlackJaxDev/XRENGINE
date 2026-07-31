namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes the conversion selected while compiling a reflected value write.
/// </summary>
internal enum EVulkanUniformWriteConversion : byte
{
    Unsupported = 0,
    DirectTyped,
    CompatibleTyped,
    TypedArray,
    StructSnapshot,
}

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Native command binding role carried by a recorded buffer dependency.
/// The role and slot are part of identity because exchanging two handles
/// between bindings changes the recorded command stream.
/// </summary>
internal enum EVulkanRecordedBufferBindingKind : byte
{
    Index,
    Vertex,
    Descriptor,
    Indirect,
    IndirectCount,
    DispatchArguments,
}

namespace XREngine.Rendering.Vulkan;

/// <summary>Ends one required producer insertion after a mesh request is materialized.</summary>
internal readonly ref struct VulkanRequiredProducerInsertionScope(FrameOpCapture? capture)
{
    public void Dispose() => capture?.EndInsertion();
}

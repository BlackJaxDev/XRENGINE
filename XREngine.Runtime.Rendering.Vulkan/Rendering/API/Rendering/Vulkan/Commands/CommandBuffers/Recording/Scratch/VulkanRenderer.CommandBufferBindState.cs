using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal struct CommandBufferBindState
{
    public ulong RecordingGeneration;
    public ulong GraphicsPipeline;
    public ulong ComputePipeline;
    public ulong GraphicsDescriptorSignature;
    public ulong ComputeDescriptorSignature;
    public DescriptorHeapBindingIdentity DescriptorHeapBinding;
    public bool HasDescriptorHeapBinding;
    public ulong VertexBufferSignature;
    public ulong ViewportScissorSignature;
    public ulong IndexBuffer;
    public ulong IndexOffset;
    public IndexType IndexType;
    public bool HasViewportScissorState;
    public bool InheritsDescriptorHeaps;
    public DescriptorHeapBindingIdentity InheritedDescriptorHeapBinding;
}

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private struct CommandBufferBindState
        {
            public ulong RecordingGeneration;
            public ulong GraphicsPipeline;
            public ulong ComputePipeline;
            public ulong GraphicsDescriptorSignature;
            public ulong ComputeDescriptorSignature;
            public ulong DescriptorHeapSignature;
            public ulong VertexBufferSignature;
            public ulong ViewportScissorSignature;
            public ulong IndexBuffer;
            public ulong IndexOffset;
            public IndexType IndexType;
            public bool HasViewportScissorState;
        }

    }
}

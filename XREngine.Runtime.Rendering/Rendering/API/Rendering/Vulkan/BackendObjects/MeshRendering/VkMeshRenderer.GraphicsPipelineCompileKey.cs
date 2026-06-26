namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    public partial class VkMeshRenderer
    {
        internal readonly record struct GraphicsPipelineCompileKey(
            int OwnerIdentity,
            PipelineKey Pipeline);
    }
}

// Remaining VkMeshRenderer implementation lives in partial files:
// - VkMeshRenderer.Buffers.cs
// - VkMeshRenderer.Pipeline.cs
// - VkMeshRenderer.Drawing.cs
// - VkMeshRenderer.Descriptors.cs
// - VkMeshRenderer.Uniforms.cs
// - VkMeshRenderer.Cleanup.cs

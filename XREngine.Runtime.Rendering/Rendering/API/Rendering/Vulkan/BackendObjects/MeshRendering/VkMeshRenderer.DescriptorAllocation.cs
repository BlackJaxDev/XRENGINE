using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    public partial class VkMeshRenderer
    {
        private sealed class DescriptorAllocation
        {
            public VkRenderProgram? Program;
            public XRMaterial? Material;
            public ulong MaterialBindingLayoutVersion;
            public int DescriptorFrameSlotCount;
            public int SetCount;
            public DescriptorPool Pool;
            public DescriptorSet[][] Sets = [];
            public ulong SchemaFingerprint;
            public ulong ResourceFingerprint;
            public string ResourceFingerprintDetails = string.Empty;
        }
    }
}

// Remaining VkMeshRenderer implementation lives in partial files:
// - VkMeshRenderer.Buffers.cs
// - VkMeshRenderer.Pipeline.cs
// - VkMeshRenderer.Drawing.cs
// - VkMeshRenderer.Descriptors.cs
// - VkMeshRenderer.Uniforms.cs
// - VkMeshRenderer.Cleanup.cs

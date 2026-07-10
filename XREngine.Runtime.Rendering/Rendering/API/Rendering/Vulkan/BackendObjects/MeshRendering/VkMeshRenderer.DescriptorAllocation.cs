using System.Collections.Generic;

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
            public DescriptorHeapPushDataPayload[] DescriptorHeapPushData = [];
            public DescriptorSetLayout[] Layouts = [];
            public uint[] VariableDescriptorCounts = [];
            public ulong LayoutFingerprint;
            public ulong SchemaFingerprint;
            public ulong ResourceFingerprint;
            public ulong[] SlotResourceFingerprints = [];
            public string ResourceFingerprintDetails = string.Empty;
            public ulong LastUsedSerial;
            public readonly Dictionary<FrameSourceDescriptorWriteKey, ulong> FrameSourceDescriptorWriteSignatures = new();
        }

        private readonly record struct FrameSourceDescriptorWriteKey(
            int DescriptorSlotIndex,
            uint Set,
            uint Binding,
            DescriptorType DescriptorType,
            uint DescriptorCount);
    }
}

// Remaining VkMeshRenderer implementation lives in partial files:
// - VkMeshRenderer.Buffers.cs
// - VkMeshRenderer.Pipeline.cs
// - VkMeshRenderer.Drawing.cs
// - VkMeshRenderer.Descriptors.cs
// - VkMeshRenderer.Uniforms.cs
// - VkMeshRenderer.Cleanup.cs

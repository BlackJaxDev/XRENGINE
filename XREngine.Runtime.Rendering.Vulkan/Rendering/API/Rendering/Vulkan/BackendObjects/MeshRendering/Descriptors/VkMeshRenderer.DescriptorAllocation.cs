using System.Collections.Generic;

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    internal sealed class DescriptorAllocation
    {
        public VkRenderProgram? Program;
        public XRMaterial? Material;
        public ulong MaterialBindingLayoutVersion;
        public int DescriptorFrameSlotCount;
        public int SetCount;
        public uint ActiveSetMask;
        public VkMaterial? SharedMaterial;
        public bool UsesSharedMaterialTier;
        public int AllocatedLocalSetCount;
        public int ReservedLocalSetCount;
        public bool OwnershipTelemetryRegistered;
        public DescriptorPool Pool;
        public MeshDescriptorPoolSlabLease? PoolSlabLease;
        public DescriptorSet[][] Sets = [];
        public DescriptorHeapPushDataPayload[] DescriptorHeapPushData = [];
        public DescriptorSetLayout[] Layouts = [];
        public uint[] VariableDescriptorCounts = [];
        public ulong LayoutFingerprint;
        public ulong SchemaFingerprint;
        public uint ProgramBindingId;
        public int ViewFamilyIdentity;
        public int DrawUniformSlot;
        public ulong BindingIdentityFingerprint;
        public ulong ResourceFingerprint;
        public ulong[] SlotResourceFingerprints = [];
        public ulong[] SlotFrameSourceSamplerSignatures = [];
        public bool[] SlotFrameSourceSamplerSignaturesValid = [];
        public string ResourceFingerprintDetails = string.Empty;
        public bool HasFrameSourceDescriptors;
        public bool FrameSourceDescriptorClassificationInitialized;
        public ulong LastUsedSerial;
        public int SharedReferenceCount;
        public readonly Dictionary<DescriptorWriteKey, ulong> DescriptorWriteSignatures = new();
    }
}

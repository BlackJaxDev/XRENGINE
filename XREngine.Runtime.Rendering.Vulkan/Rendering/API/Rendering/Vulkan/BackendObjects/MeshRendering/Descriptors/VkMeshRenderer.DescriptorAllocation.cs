using System.Collections.Generic;
using System.Threading;

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
        public int DescriptorOwnerSlot;
        public ulong BindingIdentityFingerprint;
        public ulong ResourceFingerprint;
        public ulong StableResourceFingerprint;
        public ulong[] SlotResourceFingerprints = [];
        public ulong TopologyGeneration = 1;
        private long _contentGeneration = 1;
        public ulong[] SlotPublishedTopologyGenerations = [];
        public ulong[] SlotPublishedContentGenerations = [];
        public ulong[] SlotPublishedMaterialResourceVersions = [];
        public ulong[] SlotFrameSourceSamplerSignatures = [];
        public bool[] SlotFrameSourceSamplerSignaturesValid = [];
        public string ResourceFingerprintDetails = string.Empty;
        public bool HasFrameSourceDescriptors;
        public bool FrameSourceDescriptorClassificationInitialized;
        public ulong LastUsedSerial;
        public int SharedReferenceCount;
        public readonly Dictionary<DescriptorWriteKey, ulong> DescriptorWriteSignatures = new();

        public ulong ContentGeneration
            => unchecked((ulong)Volatile.Read(ref _contentGeneration));

        public ulong AdvanceContentGeneration()
            => VulkanGeneration.IncrementNonZero(ref _contentGeneration);

        public ulong AdvanceTopologyGeneration()
            => TopologyGeneration =
                VulkanGeneration.NextNonZero(TopologyGeneration);

        public void PublishOwnerGeneration(int descriptorSlotIndex)
        {
            if ((uint)descriptorSlotIndex >= (uint)SlotPublishedTopologyGenerations.Length ||
                (uint)descriptorSlotIndex >= (uint)SlotPublishedContentGenerations.Length)
            {
                return;
            }

            Volatile.Write(
                ref SlotPublishedTopologyGenerations[descriptorSlotIndex],
                TopologyGeneration);
            Volatile.Write(
                ref SlotPublishedContentGenerations[descriptorSlotIndex],
                ContentGeneration);
            if ((uint)descriptorSlotIndex <
                (uint)SlotPublishedMaterialResourceVersions.Length)
            {
                Volatile.Write(
                    ref SlotPublishedMaterialResourceVersions[descriptorSlotIndex],
                    Material?.BindingResourceVersion ?? 0UL);
            }
        }

        public bool IsOwnerGenerationPublished(
            int descriptorSlotIndex,
            ulong materialResourceVersion)
            => (uint)descriptorSlotIndex < (uint)SlotPublishedTopologyGenerations.Length &&
               (uint)descriptorSlotIndex < (uint)SlotPublishedContentGenerations.Length &&
               (uint)descriptorSlotIndex < (uint)SlotPublishedMaterialResourceVersions.Length &&
               Volatile.Read(ref SlotPublishedTopologyGenerations[descriptorSlotIndex]) ==
                   TopologyGeneration &&
               Volatile.Read(ref SlotPublishedContentGenerations[descriptorSlotIndex]) ==
                   ContentGeneration &&
               Volatile.Read(ref SlotPublishedMaterialResourceVersions[descriptorSlotIndex]) ==
                   materialResourceVersion;
    }
}

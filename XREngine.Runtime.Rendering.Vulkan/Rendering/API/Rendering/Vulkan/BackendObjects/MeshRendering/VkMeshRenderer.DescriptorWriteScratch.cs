using System.Collections.Generic;

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	private sealed class DescriptorWriteScratch
	{
		public readonly VulkanDescriptorScratchBuffer<WriteDescriptorSet> Writes = new();
		public readonly VulkanDescriptorScratchBuffer<DescriptorBufferInfo> BufferInfos = new();
		public readonly VulkanDescriptorScratchBuffer<DescriptorImageInfo> ImageInfos = new();
		public readonly VulkanDescriptorScratchBuffer<BufferView> TexelBufferViews = new();
		public readonly VulkanDescriptorScratchBuffer<(int writeIndex, int bufferIndex, DescriptorBindingInfo binding, uint descriptorCount)> BufferMap = new();
		public readonly VulkanDescriptorScratchBuffer<(int writeIndex, int imageIndex, DescriptorBindingInfo binding, uint descriptorCount)> ImageMap = new();
		public readonly VulkanDescriptorScratchBuffer<(int writeIndex, int texelIndex, DescriptorBindingInfo binding, uint descriptorCount)> TexelMap = new();
		public readonly VulkanDescriptorScratchBuffer<(DescriptorWriteKey key, ulong signature)> Signatures = new();
		public readonly VulkanDescriptorScratchBuffer<WriteDescriptorSet> TemplateWrites = new();

		public void Clear()
		{
			Writes.Clear();
			BufferInfos.Clear();
			ImageInfos.Clear();
			TexelBufferViews.Clear();
			BufferMap.Clear();
			ImageMap.Clear();
			TexelMap.Clear();
			Signatures.Clear();
			TemplateWrites.Clear();
		}
	}
}

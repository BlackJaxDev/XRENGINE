using System.Collections.Concurrent;
using System.Collections.Generic;

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	// Shared by every mesh renderer. A workspace is held only for one descriptor publication,
	// so the pool is bounded by concurrent publications; one fixed-capacity workspace per
	// renderer cost hundreds of kilobytes for each of thousands of renderers.
	private static readonly ConcurrentBag<DescriptorWriteScratch> s_descriptorWriteScratchPool = [];

	private static DescriptorWriteScratch RentDescriptorWriteScratch()
	{
		if (!s_descriptorWriteScratchPool.TryTake(out DescriptorWriteScratch? scratch))
			scratch = new DescriptorWriteScratch();
		scratch.Clear();
		return scratch;
	}

	private static void ReturnDescriptorWriteScratch(DescriptorWriteScratch scratch)
		=> s_descriptorWriteScratchPool.Add(scratch);

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

using System.Collections.Generic;

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
		private sealed class DescriptorWriteScratch
		{
			public readonly List<WriteDescriptorSet> Writes = [];
			public readonly List<DescriptorBufferInfo> BufferInfos = [];
			public readonly List<DescriptorImageInfo> ImageInfos = [];
			public readonly List<BufferView> TexelBufferViews = [];
			public readonly List<(int writeIndex, int bufferIndex, DescriptorBindingInfo binding, uint descriptorCount)> BufferMap = [];
			public readonly List<(int writeIndex, int imageIndex, DescriptorBindingInfo binding, uint descriptorCount)> ImageMap = [];
			public readonly List<(int writeIndex, int texelIndex, DescriptorBindingInfo binding, uint descriptorCount)> TexelMap = [];

			public void Clear()
			{
				Writes.Clear();
				BufferInfos.Clear();
				ImageInfos.Clear();
				TexelBufferViews.Clear();
				BufferMap.Clear();
				ImageMap.Clear();
				TexelMap.Clear();
			}
		}
	}
}

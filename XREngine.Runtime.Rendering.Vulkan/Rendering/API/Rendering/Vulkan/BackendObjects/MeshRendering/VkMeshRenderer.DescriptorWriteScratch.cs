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
	{
		Exception? firstFailure = null;
		try
		{
			scratch.ReleaseRetainedTextureBindings();
		}
		catch (Exception exception)
		{
			firstFailure = exception;
		}
		try
		{
			scratch.ReleaseRetainedBufferBindings();
		}
		catch (Exception exception)
		{
			firstFailure ??= exception;
		}
		s_descriptorWriteScratchPool.Add(scratch);
		if (firstFailure is not null)
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
	}

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
		public readonly VulkanDescriptorScratchBuffer<XRDataBuffer> RetainedBufferBindings = new();
		public readonly VulkanDescriptorScratchBuffer<XRTexture> RetainedTextureBindings = new();

		public void ReleaseRetainedBufferBindings()
		{
			Exception? firstFailure = null;
			for (int index = 0; index < RetainedBufferBindings.Count; index++)
			{
				XRDataBuffer buffer = RetainedBufferBindings[index];
				RetainedBufferBindings[index] = null!;
				try
				{
					buffer.ReleaseBindingUse();
				}
				catch (Exception exception)
				{
					firstFailure ??= exception;
				}
			}
			RetainedBufferBindings.Clear();
			if (firstFailure is not null)
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
		}

		public void Clear()
		{
			Exception? firstFailure = null;
			try
			{
				ReleaseRetainedTextureBindings();
			}
			catch (Exception exception)
			{
				firstFailure = exception;
			}
			try
			{
				ReleaseRetainedBufferBindings();
			}
			catch (Exception exception)
			{
				firstFailure ??= exception;
			}
			Writes.Clear();
			BufferInfos.Clear();
			ImageInfos.Clear();
			TexelBufferViews.Clear();
			BufferMap.Clear();
			ImageMap.Clear();
			TexelMap.Clear();
			Signatures.Clear();
			TemplateWrites.Clear();
			if (firstFailure is not null)
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
		}

		public void ReleaseRetainedTextureBindings()
		{
			Exception? firstFailure = null;
			for (int index = 0; index < RetainedTextureBindings.Count; index++)
			{
				XRTexture texture = RetainedTextureBindings[index];
				RetainedTextureBindings[index] = null!;
				try
				{
					texture.ReleaseBindingUse();
				}
				catch (Exception exception)
				{
					firstFailure ??= exception;
				}
			}
			RetainedTextureBindings.Clear();
			if (firstFailure is not null)
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
		}
	}
}

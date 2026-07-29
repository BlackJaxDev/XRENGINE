// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Descriptors.cs  – partial class: Descriptor Set Management
//
// Allocates and writes Vulkan descriptor sets for each swapchain frame.
// Resolves buffer, image, and texel-buffer descriptors from the buffer cache,
// material textures, and engine/auto uniform buffers.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
private bool TryResolveEngineUniformBuffer(DescriptorBindingInfo binding, int frameIndex, int drawUniformSlot, out DescriptorBufferInfo bufferInfo)
			{
				bufferInfo = default;
				string bindingName = binding.Name ?? string.Empty;
				if (string.IsNullOrWhiteSpace(bindingName))
					return false;
				string name = NormalizeEngineUniformName(bindingName);

				uint size = GetEngineUniformSize(bindingName);
				if (size == 0)
				{
					if (!IsOptionalPipelineStorageBuffer(binding))
						WarnOnce($"Descriptor binding '{name}' could not be matched to an engine uniform.");
					return false;
				}

				if (!EnsureEngineUniformBuffer(name, size))
					return false;

				if (!_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? buffers) || buffers.Length == 0)
					return false;

				int idx = ResolveUniformBufferIndex(frameIndex, drawUniformSlot, buffers.Length);
				EngineUniformBuffer target = buffers[idx];
				if (target.Buffer.Handle == 0)
					return false;

				bufferInfo = new DescriptorBufferInfo
				{
					Buffer = target.Buffer,
					Offset = binding.DescriptorType == DescriptorType.UniformBufferDynamic &&
						!Renderer.IsDescriptorHeapDrawBindingActive ? 0UL : target.Offset,
					Range = size,
				};

				return true;
			}

			/// <summary>
			/// Resolves a descriptor buffer binding for a reflection-driven auto uniform
			/// block. Creates the per-frame UBO on demand.
			/// </summary>
			private bool TryResolveAutoUniformBuffer(DescriptorBindingInfo binding, int frameIndex, int drawUniformSlot, out DescriptorBufferInfo bufferInfo)
			{
				bufferInfo = default;
				if (_program is null)
					return false;

				if (!_program.TryGetAutoUniformBlockFuzzy(binding.Name ?? string.Empty, binding.Set, binding.Binding, out AutoUniformBlockInfo block))
					return false;

				uint size = Math.Max(block.Size, 1u);
				if (!EnsureAutoUniformBuffer(block.InstanceName, size))
					return false;

				if (!_autoUniformBuffers.TryGetValue(block.InstanceName, out AutoUniformBuffer[]? buffers) || buffers.Length == 0)
					return false;

				int idx = ResolveUniformBufferIndex(frameIndex, drawUniformSlot, buffers.Length);
				AutoUniformBuffer target = buffers[idx];
				if (target.Buffer.Handle == 0)
					return false;

				bufferInfo = new DescriptorBufferInfo
				{
					Buffer = target.Buffer,
					Offset = binding.DescriptorType == DescriptorType.UniformBufferDynamic &&
						!Renderer.IsDescriptorHeapDrawBindingActive ? 0UL : target.Offset,
					Range = size,
				};

				return true;
			}
	}
}

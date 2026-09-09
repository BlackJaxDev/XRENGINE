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

internal unsafe partial class VkMeshRenderer
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
					BackendContext.Resources.Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ? 0UL : target.Offset,
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

			if (!_program.TryGetAutoUniformBlock(
					binding.Set,
					binding.Binding,
					out AutoUniformBlockInfo block))
				return false;

			uint size = Math.Max(block.Size, 1u);
			if (!EnsureAutoUniformBuffer(block.InstanceName, size))
				return false;

			if (!_autoUniformBuffers.TryGetValue(block.InstanceName, out AutoUniformBuffer[]? buffers) || buffers.Length == 0)
				return false;

			int idx = ResolvePublishedAutoUniformBufferIndex(
				block,
				frameIndex,
				drawUniformSlot,
				buffers.Length);
			AutoUniformBuffer target = buffers[idx];
			if (target.Buffer.Handle == 0)
				return false;

			bufferInfo = new DescriptorBufferInfo
			{
				Buffer = target.Buffer,
				Offset = binding.DescriptorType == DescriptorType.UniformBufferDynamic &&
					BackendContext.Resources.Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ? 0UL : target.Offset,
				Range = size,
			};

			return true;
		}

	/// <summary>
	/// Rewrites the native heap payload after frequency-owned auto
	/// uniform publication selects this draw's mapped-frame range. Descriptor-set
	/// mode carries that range through dynamic offsets; native heap mode
	/// must publish the resolved offset in the pushed descriptor bytes.
	/// </summary>
	private bool RefreshDescriptorHeapAutoUniformBindings(
		int frameIndex,
		int drawUniformSlot,
		out string reason)
	{
		reason = string.Empty;
		if (_program is null ||
			BackendContext.Resources.Descriptors.Heap.ActiveBackend !=
				EVulkanDescriptorBackend.DescriptorHeap)
		{
			return true;
		}

		if (_activeDescriptorAllocation?.DescriptorHeapPushData is not
			{ Length: > 0 } payloads ||
			_descriptorSets is not { Length: > 0 })
		{
			reason = "descriptor heap payload is unavailable after auto-uniform publication";
			return false;
		}

		int descriptorSlotIndex = ResolveDescriptorFrameIndex(
			frameIndex,
			_descriptorSets.Length);
		if ((uint)descriptorSlotIndex >= (uint)payloads.Length ||
			payloads[descriptorSlotIndex] is not { } payload)
		{
			reason = $"descriptor heap payload is unavailable for frame slot {descriptorSlotIndex}";
			return false;
		}

		IReadOnlyList<DescriptorBindingInfo> bindings = _program.DescriptorBindings;
		for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
		{
			DescriptorBindingInfo binding = bindings[bindingIndex];
			if (binding.DescriptorType is not
				(DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic) ||
				!_program.TryGetAutoUniformBlock(binding.Set, binding.Binding, out _))
			{
				continue;
			}

			if (VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding) != 1 ||
				!TryResolveAutoUniformBuffer(
					binding,
					frameIndex,
					drawUniformSlot,
					out DescriptorBufferInfo bufferInfo))
			{
				reason = $"auto-uniform descriptor '{binding.Name}' could not resolve its published range";
				return false;
			}

			if (!BackendContext.Resources.DescriptorLifetime.TryWriteDescriptorHeapBinding(
					_program,
					binding,
					payload,
					&bufferInfo,
					null,
					null,
					descriptorCount: 1,
					out string heapReason))
			{
				reason = $"auto-uniform descriptor '{binding.Name}' heap write failed: {heapReason}";
				return false;
			}
		}

		return true;
	}

	private bool TryRefreshGlobalMaterialTextureArrayHeapPayload(
		DescriptorAllocation allocation,
		int frameIndex,
		ComputeDispatchSnapshot? bindingSnapshot)
	{
		if (_program is null ||
			BackendContext.Resources.Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap)
		{
			return true;
		}

		if (allocation.DescriptorHeapPushData.Length == 0)
			return FailDescriptorPreparation("descriptor heap payload is unavailable for the global material texture array");

		int descriptorSlotIndex = ResolveDescriptorFrameIndex(frameIndex, allocation.DescriptorHeapPushData.Length);
		if ((uint)descriptorSlotIndex >= (uint)allocation.DescriptorHeapPushData.Length)
			return FailDescriptorPreparation("descriptor heap payload is unavailable for the global material texture array");
		if (!BackendContext.Resources.Descriptors.TryWriteGlobalMaterialTextureArrayHeapPayload(
				_program,
				allocation.DescriptorHeapPushData[descriptorSlotIndex],
				bindingSnapshot?.MaterialTablePublication,
				out string reason))
		{
			return FailDescriptorPreparation($"global material texture array heap write failed: {reason}");
		}

		return true;
	}
}

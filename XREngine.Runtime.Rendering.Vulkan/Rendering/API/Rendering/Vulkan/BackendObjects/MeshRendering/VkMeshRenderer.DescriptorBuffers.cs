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
	private bool TryResolveBuffer(
		DescriptorBindingInfo binding,
		int frameIndex,
		int drawUniformSlot,
		out DescriptorBufferInfo bufferInfo,
		ComputeDispatchSnapshot? bindingSnapshot = null)
	{
		bufferInfo = default;

		// A captured draw owns the exact native buffer generation that its command
		// signature describes. Resolve it before ambient pipeline state so shadow
		// fallback and main Forward+ variants cannot overwrite one descriptor set.
		if (bindingSnapshot is not null &&
			bindingSnapshot.Buffers.TryGetValue(binding.Binding, out VulkanComputeBufferBinding capturedBuffer))
		{
			if (!IsDescriptorCompatibleBufferTarget(binding.DescriptorType, capturedBuffer.Data.Target) ||
				capturedBuffer.Buffer.Handle == 0 ||
				capturedBuffer.Range == 0)
			{
				return false;
			}

			bufferInfo = new DescriptorBufferInfo
			{
				Buffer = capturedBuffer.Buffer,
				Offset = 0,
				Range = capturedBuffer.Range,
			};
			return true;
		}

		// Step 1: Exact name match from the mesh renderer's buffer cache.
		VkDataBuffer? buffer = null;
		if (!string.IsNullOrWhiteSpace(binding.Name) && TryResolveCachedBufferByName(binding.Name, out buffer))
		{
			// found by name — use it directly
		}
		else if (TryResolvePipelineResourceBuffer(binding, out buffer))
		{
			// found by render-pipeline resource name
		}
		else
		{
			// Step 1.5: For SSBOs, prefer explicit binding-index mapping from XRDataBuffer.
			// This is more robust than name matching because SPIR-V reflection names can
			// vary by compiler/optimization path.
			if (binding.DescriptorType == DescriptorType.StorageBuffer)
			{
				if (TryResolveProgramBoundBuffer(binding, out buffer))
					goto BufferResolved;

				lock (_bufferStateSync)
				{
					foreach (VkDataBuffer candidate in _bufferCache.Values)
					{
						if (IsStorageBufferCompatibleTarget(candidate.Data.Target) &&
							candidate.Data.BindingIndexOverride == binding.Binding)
						{
							buffer = candidate;
							break;
						}
					}
				}
			}

			if (buffer is not null)
				goto BufferResolved;

			// Step 2: Name lookup missed. Try auto/engine uniform resolution
			// before resorting to the generic cache scan. This prevents an
			// unrelated SSBO (e.g. LinesBuffer) from being returned for a UBO
			// binding that should resolve to an auto-uniform block.
			if (TryResolveAutoUniformBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo))
				return true;

			if (TryResolveEngineUniformBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo))
				return true;

			// Step 3: Generic fallback — only match buffers whose target type
			// is compatible with the descriptor's expected type.
			if (binding.DescriptorType is DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic)
			{
				lock (_bufferStateSync)
				{
					foreach (VkDataBuffer candidate in _bufferCache.Values)
					{
						if (candidate.Data.Target == EBufferTarget.UniformBuffer)
						{
							buffer = candidate;
							break;
						}
					}
				}
			}
		}

		if (buffer is null)
		{
			if (binding.DescriptorType is DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic or DescriptorType.StorageBuffer)
				return TryResolveFallbackDescriptorBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo);

			string cacheKeys;
			lock (_bufferStateSync)
				cacheKeys = string.Join(", ", _bufferCache.Keys);
			WarnOnce($"[BufferResolve] Failed to resolve buffer for binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}). Cache keys: [{cacheKeys}]");
			return false;
		}

	BufferResolved:

		if (buffer is null)
			return TryResolveFallbackDescriptorBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo);

		bool allowSynchronousBufferUpload = Renderer.AllowSynchronousResourceUploads;
		if (!buffer.TryEnsureReadyForRendering(allowSynchronousBufferUpload))
		{
			if (IsOptionalPipelineStorageBuffer(binding))
				return TryResolveFallbackDescriptorBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo);

			WarnOnce($"[BufferResolve] Buffer '{binding.Name}' resolved (set={binding.Set}, binding={binding.Binding}) but is not ready for Vulkan descriptor use (Length={buffer.Data.Length}, Target={buffer.Data.Target}).");
			return false;
		}

		if (buffer.BufferHandle is not { } bufferHandle || bufferHandle.Handle == 0)
		{
			if (IsOptionalPipelineStorageBuffer(binding))
				return TryResolveFallbackDescriptorBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo);

			WarnOnce($"[BufferResolve] Buffer '{binding.Name}' resolved (set={binding.Set}, binding={binding.Binding}) but VkBuffer is not allocated (Length={buffer.Data.Length}, Resizable={buffer.Data.Resizable}, Target={buffer.Data.Target}).");
			return false;
		}

		ulong requestedRange = Math.Max((ulong)buffer.Data.Length, 1UL);
		if (buffer.AllocatedByteSize < requestedRange)
		{
			if (!allowSynchronousBufferUpload)
			{
				if (IsOptionalPipelineStorageBuffer(binding))
					return TryResolveFallbackDescriptorBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo);

				WarnOnce($"[BufferResolve] Buffer '{binding.Name}' resolved (set={binding.Set}, binding={binding.Binding}) but allocation is too small and external swapchain rendering cannot upload it synchronously (Requested={requestedRange}, Allocated={buffer.AllocatedByteSize}, Target={buffer.Data.Target}).");
				return false;
			}

			buffer.PushData();
			bufferHandle = buffer.BufferHandle ?? default;
		}

		if (bufferHandle.Handle == 0 || buffer.AllocatedByteSize < requestedRange)
		{
			if (IsOptionalPipelineStorageBuffer(binding))
				return TryResolveFallbackDescriptorBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo);

			WarnOnce($"[BufferResolve] Buffer '{binding.Name}' resolved (set={binding.Set}, binding={binding.Binding}) but allocation is too small (Requested={requestedRange}, Allocated={buffer.AllocatedByteSize}, Target={buffer.Data.Target}).");
			return false;
		}

		bufferInfo = new DescriptorBufferInfo
		{
			Buffer = bufferHandle,
			Offset = 0,
			Range = requestedRange,
		};

		return true;
	}

	private bool TryResolvePipelineResourceBuffer(DescriptorBindingInfo binding, out VkDataBuffer? buffer)
	{
		buffer = null;
		if (string.IsNullOrWhiteSpace(binding.Name))
			return false;

		XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
		if (pipeline is null)
			return false;

		if (!TryResolvePipelineResourceDataBuffer(pipeline, binding.Name, binding.DescriptorType, out XRDataBuffer dataBuffer))
		{
			string trimmedName = TrimDescriptorBufferSuffix(binding.Name);
			if (string.Equals(trimmedName, binding.Name, StringComparison.Ordinal) ||
				!TryResolvePipelineResourceDataBuffer(pipeline, trimmedName, binding.DescriptorType, out dataBuffer))
			{
				return false;
			}
		}

		Renderer.TrackBufferBinding(dataBuffer);
		bool allowSynchronousBufferUpload = Renderer.AllowSynchronousResourceUploads;
		if (Renderer.GetOrCreateAPIRenderObject(dataBuffer, generateNow: allowSynchronousBufferUpload) is not VkDataBuffer vkBuffer)
			return false;

		buffer = vkBuffer;
		return true;
	}

	private static bool TryResolvePipelineResourceDataBuffer(
		XRRenderPipelineInstance pipeline,
		string name,
		DescriptorType descriptorType,
		out XRDataBuffer dataBuffer)
	{
		dataBuffer = null!;
		if (!pipeline.TryGetBuffer(name, out XRDataBuffer? buffer) || buffer is null)
			return false;

		if (!IsDescriptorCompatibleBufferTarget(descriptorType, buffer.Target))
			return false;

		dataBuffer = buffer;
		return true;
	}

	private static bool IsDescriptorCompatibleBufferTarget(DescriptorType descriptorType, EBufferTarget target)
		=> descriptorType switch
		{
			DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => IsStorageBufferCompatibleTarget(target),
			DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => target == EBufferTarget.UniformBuffer,
			_ => false,
		};

	private static bool IsStorageBufferCompatibleTarget(EBufferTarget target)
		=> target is EBufferTarget.ShaderStorageBuffer
			or EBufferTarget.DrawIndirectBuffer
			or EBufferTarget.DispatchIndirectBuffer;

	private bool TryResolveProgramBoundBuffer(DescriptorBindingInfo binding, out VkDataBuffer? buffer)
	{
		buffer = null;
		if (_program is null || !_program.TryGetBoundBuffer(binding.Binding, out XRDataBuffer? dataBuffer) || dataBuffer is null)
			return false;

		bool targetMatches = binding.DescriptorType switch
		{
			DescriptorType.StorageBuffer => IsStorageBufferCompatibleTarget(dataBuffer.Target),
			DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => dataBuffer.Target == EBufferTarget.UniformBuffer,
			_ => false,
		};

		if (!targetMatches)
			return false;

		Renderer.TrackBufferBinding(dataBuffer);
		bool allowSynchronousBufferUpload = Renderer.AllowSynchronousResourceUploads;
		if (Renderer.GetOrCreateAPIRenderObject(dataBuffer, generateNow: allowSynchronousBufferUpload) is not VkDataBuffer vkBuffer)
			return false;

		buffer = vkBuffer;
		return true;
	}

	private bool TryResolveCachedBufferByName(string bindingName, out VkDataBuffer? buffer)
	{
		lock (_bufferStateSync)
		{
			if (_bufferCache.TryGetValue(bindingName, out buffer))
				return true;

			string trimmedName = TrimDescriptorBufferSuffix(bindingName);
			if (!string.Equals(trimmedName, bindingName, StringComparison.Ordinal) &&
				_bufferCache.TryGetValue(trimmedName, out buffer))
				return true;

			string aliasName = string.Empty;
			if (TryGetDebugPrimitiveBufferAlias(bindingName, out aliasName) &&
				_bufferCache.TryGetValue(aliasName, out buffer))
				return true;

			foreach (VkDataBuffer candidate in _bufferCache.Values)
			{
				string attributeName = candidate.Data.AttributeName;
				if (string.Equals(attributeName, bindingName, StringComparison.Ordinal) ||
					(!string.Equals(trimmedName, bindingName, StringComparison.Ordinal) &&
					 string.Equals(attributeName, trimmedName, StringComparison.Ordinal)) ||
					(!string.IsNullOrEmpty(aliasName) &&
					 string.Equals(attributeName, aliasName, StringComparison.Ordinal)))
				{
					buffer = candidate;
					return true;
				}
			}

			buffer = null;
			return false;
		}
	}

	private static bool TryGetDebugPrimitiveBufferAlias(string bindingName, out string aliasName)
	{
		aliasName = bindingName switch
		{
			"PointData" or "Points" => "PointsBuffer",
			"LineData" or "Lines" => "LinesBuffer",
			"TriData" or "TriangleData" or "Triangles" => "TrianglesBuffer",
			_ => string.Empty,
		};

		return aliasName.Length > 0;
	}

	private static string TrimDescriptorBufferSuffix(string bindingName)
		=> DescriptorBufferTrimmedNames.GetOrAdd(
			bindingName,
			static name => name.EndsWith("Input", StringComparison.Ordinal)
				? name[..^5]
				: name.EndsWith("Buffer", StringComparison.Ordinal)
					? name[..^6]
					: name);

	private static bool IsOptionalPipelineStorageBuffer(DescriptorBindingInfo binding)
		=> binding.DescriptorType is DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic &&
		   binding.Name is ("LightProbePositions" or
			   "LightProbeTetrahedra" or
			   "LightProbeParameters" or
			   "LightProbeGridCells" or
			   "LightProbeGridIndices");

	private bool TryResolveFallbackDescriptorBuffer(DescriptorBindingInfo binding, int frameIndex, int drawUniformSlot, out DescriptorBufferInfo bufferInfo)
	{
		bufferInfo = default;
		uint requiredSize = Math.Max(FallbackDescriptorUniformSize, Math.Max(binding.Count, 1u) * 16u);
		if (!EnsureEngineUniformBuffer(FallbackDescriptorUniformName, requiredSize))
			return false;

		if (!_engineUniformBuffers.TryGetValue(FallbackDescriptorUniformName, out EngineUniformBuffer[]? buffers) || buffers.Length == 0)
			return false;

		int idx = ResolveUniformBufferIndex(frameIndex, drawUniformSlot, buffers.Length);
		EngineUniformBuffer target = buffers[idx];
		if (target.Buffer.Handle == 0)
			return false;

		if (!string.IsNullOrWhiteSpace(binding.Name) && !IsOptionalPipelineStorageBuffer(binding))
			WarnOnce($"Using fallback descriptor buffer for unresolved {binding.DescriptorType} binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
		RecordDescriptorFallback(binding);
		bufferInfo = new DescriptorBufferInfo
		{
			Buffer = target.Buffer,
			Offset = target.Offset,
			Range = target.Size,
		};

		return true;
	}

	/// <summary>
	/// Resolves an image descriptor from the material's textures. Handles
	/// combined-image-sampler, sampled-image, and storage-image types.
	/// For combined depth-stencil formats, automatically creates a depth-only view.
	/// </summary>
}

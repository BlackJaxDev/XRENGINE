// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Descriptors.cs  – partial class: Descriptor Set Management
//
// Allocates and writes Vulkan descriptor sets for each swapchain frame.
// Resolves buffer, image, and texel-buffer descriptors from the buffer cache,
// material textures, and engine/auto uniform buffers.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;

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
		#region Descriptor Set Management

		/// <summary>
		/// Ensures descriptor sets are allocated and written for all swapchain frames.
		/// Uses a schema fingerprint to detect layout changes and avoid redundant
		/// reallocation when only resource bindings change.
		/// </summary>
		private bool EnsureDescriptorSets(XRMaterial material)
		{
			if (_program is null)
				return false;

			var layouts = _program.DescriptorSetLayouts;
			var bindings = _program.DescriptorBindings;
			if (layouts is null || layouts.Count == 0 || bindings.Count == 0)
			{
				_descriptorDirty = false;
				return true;
			}

			if (Renderer.swapChainImages is null || Renderer.swapChainImages.Length == 0)
				return false;

			int frameCount = Renderer.swapChainImages.Length;
			int setCount = layouts.Count;
			ulong schemaFingerprint = ComputeDescriptorSchemaFingerprint(bindings, setCount, frameCount);

			bool canReuseDescriptorAllocation =
				_descriptorSets is { Length: > 0 } &&
				_descriptorPool.Handle != 0 &&
				_descriptorSchemaFingerprint == schemaFingerprint &&
				_descriptorSets.Length == frameCount &&
				_descriptorSets.All(s => s.Length == setCount);

			if (!_descriptorDirty && canReuseDescriptorAllocation)
				return true;

			if (canReuseDescriptorAllocation)
			{
				for (int frame = 0; frame < frameCount; frame++)
				{
					if (!WriteDescriptorSets(_descriptorSets![frame], bindings, material, frame))
						return false;
				}

				_descriptorDirty = false;
				return true;
			}

			DestroyDescriptors();

			var poolSizes = BuildDescriptorPoolSizes(bindings, frameCount);
			if (poolSizes.Length == 0)
				return false;

			fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
			{
				DescriptorPoolCreateInfo poolInfo = new()
				{
					SType = StructureType.DescriptorPoolCreateInfo,
					Flags = _program.DescriptorSetsRequireUpdateAfterBind
						? DescriptorPoolCreateFlags.UpdateAfterBindBit
						: 0,
					PoolSizeCount = (uint)poolSizes.Length,
					PPoolSizes = poolSizesPtr,
					MaxSets = (uint)(setCount * frameCount),
				};

				if (Api!.CreateDescriptorPool(Device, ref poolInfo, null, out _descriptorPool) != Result.Success)
				{
					Debug.VulkanWarning("Failed to create Vulkan descriptor pool for mesh renderer.");
					return false;
				}
			}

			DescriptorSetLayout[] layoutArray = [.. layouts];
			_descriptorSets = new DescriptorSet[frameCount][];

			for (int frame = 0; frame < frameCount; frame++)
			{
				DescriptorSet[] frameSets = new DescriptorSet[layoutArray.Length];

				fixed (DescriptorSetLayout* layoutPtr = layoutArray)
				fixed (DescriptorSet* setPtr = frameSets)
				{
					DescriptorSetAllocateInfo allocInfo = new()
					{
						SType = StructureType.DescriptorSetAllocateInfo,
						DescriptorPool = _descriptorPool,
						DescriptorSetCount = (uint)layoutArray.Length,
						PSetLayouts = layoutPtr,
					};

					if (Api!.AllocateDescriptorSets(Device, ref allocInfo, setPtr) != Result.Success)
					{
						Debug.VulkanWarning("Failed to allocate Vulkan descriptor sets for mesh renderer.");
						return false;
					}
				}

				if (!WriteDescriptorSets(frameSets, bindings, material, frame))
					return false;

				_descriptorSets[frame] = frameSets;
			}

			_descriptorSchemaFingerprint = schemaFingerprint;
			_descriptorDirty = false;
			return true;
		}

		/// <summary>
		/// Computes a fingerprint over descriptor set layout metadata (binding indices,
		/// types, stages). Used to detect when the schema changes and a full
		/// reallocation of the descriptor pool is required.
		/// </summary>
		private static ulong ComputeDescriptorSchemaFingerprint(IReadOnlyList<DescriptorBindingInfo> bindings, int setCount, int frameCount)
		{
			HashCode hash = new();
			hash.Add(setCount);
			hash.Add(frameCount);

			foreach (DescriptorBindingInfo binding in bindings.OrderBy(b => b.Set).ThenBy(b => b.Binding))
			{
				hash.Add(binding.Set);
				hash.Add(binding.Binding);
				hash.Add((int)binding.DescriptorType);
				hash.Add(binding.Count);
				hash.Add((int)binding.StageFlags);
				hash.Add(binding.Name);
			}

			return unchecked((ulong)hash.ToHashCode());
		}

		/// <summary>Resolves one or more buffer descriptors for a binding, duplicating for array bindings.</summary>
		private bool TryResolveBuffers(DescriptorBindingInfo binding, int frameIndex, uint descriptorCount, List<DescriptorBufferInfo> bufferInfos, out int bufferStart)
		{
			bufferStart = bufferInfos.Count;
			if (!TryResolveBuffer(binding, frameIndex, out DescriptorBufferInfo bufferInfo))
				return false;

			for (int i = 0; i < descriptorCount; i++)
				bufferInfos.Add(bufferInfo);

			return true;
		}

		/// <summary>Resolves one or more image descriptors for a binding from the material's textures.</summary>
		private bool TryResolveImages(DescriptorBindingInfo binding, XRMaterial material, uint descriptorCount, List<DescriptorImageInfo> imageInfos, out int imageStart)
		{
			imageStart = imageInfos.Count;
			for (int i = 0; i < descriptorCount; i++)
			{
				if (!TryResolveImage(binding, material, binding.DescriptorType, out DescriptorImageInfo info, i))
					return false;

				imageInfos.Add(info);
			}

			return true;
		}

		/// <summary>Resolves one or more texel buffer view descriptors for a binding.</summary>
		private bool TryResolveTexelBuffers(DescriptorBindingInfo binding, XRMaterial material, uint descriptorCount, List<BufferView> texelBufferViews, out int texelStart)
		{
			texelStart = texelBufferViews.Count;
			for (int i = 0; i < descriptorCount; i++)
			{
				if (!TryResolveTexelBuffer(binding, material, out BufferView view, i))
					return false;

				texelBufferViews.Add(view);
			}

			return true;
		}

		/// <summary>
		/// Aggregates descriptor type counts across all bindings and frames to
		/// determine the pool sizes needed for allocation.
		/// </summary>
		private static DescriptorPoolSize[] BuildDescriptorPoolSizes(IReadOnlyList<DescriptorBindingInfo> bindings, int frameCount)
		{
			Dictionary<DescriptorType, uint> counts = [];
			foreach (DescriptorBindingInfo binding in bindings)
			{
				uint count = Math.Max(binding.Count, 1u) * (uint)frameCount;
				if (counts.TryGetValue(binding.DescriptorType, out uint existing))
					counts[binding.DescriptorType] = existing + count;
				else
					counts[binding.DescriptorType] = count;
			}

			DescriptorPoolSize[] poolSizes = new DescriptorPoolSize[counts.Count];
			int i = 0;
			foreach (var pair in counts)
				poolSizes[i++] = new DescriptorPoolSize { Type = pair.Key, DescriptorCount = pair.Value };

			return poolSizes;
		}

		/// <summary>
		/// Writes all descriptor bindings for a single frame's descriptor sets.
		/// Resolves buffers, images, and texel buffers, then issues a batched
		/// <c>vkUpdateDescriptorSets</c> call for all pending writes.
		/// </summary>
		private bool WriteDescriptorSets(DescriptorSet[] frameSets, IReadOnlyList<DescriptorBindingInfo> bindings, XRMaterial material, int frameIndex)
		{
			List<WriteDescriptorSet> writes = [];
			List<DescriptorBufferInfo> bufferInfos = [];
			List<DescriptorImageInfo> imageInfos = [];
			List<BufferView> texelBufferViews = [];
			List<(int writeIndex, int bufferIndex)> bufferMap = [];
			List<(int writeIndex, int imageIndex)> imageMap = [];
			List<(int writeIndex, int texelIndex)> texelMap = [];

			foreach (DescriptorBindingInfo binding in bindings)
			{
				if (binding.Set >= frameSets.Length)
				{
					WarnOnce($"Descriptor set {binding.Set} is not available for pipeline layout.");
					return false;
				}

				uint descriptorCount = Math.Max(binding.Count, 1u);

				switch (binding.DescriptorType)
				{
					case DescriptorType.UniformBuffer:
					case DescriptorType.StorageBuffer:
						if (!TryResolveBuffers(binding, frameIndex, descriptorCount, bufferInfos, out int bufferStart))
						{
							WarnOnce($"[WriteDesc] FAILED to resolve buffer binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}' program '{_program?.Data?.Name ?? "?"}'");
							return false;
						}

						bufferMap.Add((writes.Count, bufferStart));
						writes.Add(new WriteDescriptorSet
						{
							SType = StructureType.WriteDescriptorSet,
							DstSet = frameSets[binding.Set],
							DstBinding = binding.Binding,
							DescriptorCount = descriptorCount,
							DescriptorType = binding.DescriptorType,
						});
						break;

					case DescriptorType.CombinedImageSampler:
						case DescriptorType.Sampler:
					case DescriptorType.SampledImage:
					case DescriptorType.StorageImage:
					case DescriptorType.InputAttachment:
						if (!TryResolveImages(binding, material, descriptorCount, imageInfos, out int imageStart))
						{
							WarnOnce($"[WriteDesc] FAILED to resolve image binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}' program '{_program?.Data?.Name ?? "?"}'");
							return false;
						}

						imageMap.Add((writes.Count, imageStart));
						writes.Add(new WriteDescriptorSet
						{
							SType = StructureType.WriteDescriptorSet,
							DstSet = frameSets[binding.Set],
							DstBinding = binding.Binding,
							DescriptorCount = descriptorCount,
							DescriptorType = binding.DescriptorType,
						});
						break;

					case DescriptorType.UniformTexelBuffer:
					case DescriptorType.StorageTexelBuffer:
						if (!TryResolveTexelBuffers(binding, material, descriptorCount, texelBufferViews, out int texelStart))
							return false;

						texelMap.Add((writes.Count, texelStart));
						writes.Add(new WriteDescriptorSet
						{
							SType = StructureType.WriteDescriptorSet,
							DstSet = frameSets[binding.Set],
							DstBinding = binding.Binding,
							DescriptorCount = descriptorCount,
							DescriptorType = binding.DescriptorType,
						});
						break;

					default:
						WarnOnce($"Unsupported descriptor type '{binding.DescriptorType}' for binding '{binding.Name}'.");
						return false;
				}
			}

			DescriptorBufferInfo[] bufferArray = bufferInfos.Count == 0 ? [] : [.. bufferInfos];
			DescriptorImageInfo[] imageArray = imageInfos.Count == 0 ? [] : [.. imageInfos];
			BufferView[] texelArray = texelBufferViews.Count == 0 ? [] : [.. texelBufferViews];
			WriteDescriptorSet[] writeArray = writes.Count == 0 ? [] : [.. writes];

			fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
			fixed (DescriptorImageInfo* imagePtr = imageArray)
			fixed (BufferView* texelPtr = texelArray)
			fixed (WriteDescriptorSet* writePtr = writeArray)
			{
				foreach (var (writeIndex, bufferIndex) in bufferMap)
					writePtr[writeIndex].PBufferInfo = bufferPtr + bufferIndex;

				foreach (var (writeIndex, imageIndex) in imageMap)
					writePtr[writeIndex].PImageInfo = imagePtr + imageIndex;

				foreach (var (writeIndex, texelIndex) in texelMap)
					writePtr[writeIndex].PTexelBufferView = texelPtr + texelIndex;

				if (writeArray.Length > 0)
					Api!.UpdateDescriptorSets(Device, (uint)writeArray.Length, writePtr, 0, null);
			}

			return true;
		}

		// ── Individual Resource Resolution ───────────────────────────────────

		/// <summary>
		/// Resolves a buffer descriptor for a single binding. Searches the buffer
		/// cache by name, then falls back to auto uniform and engine uniform buffers.
		/// </summary>
		private bool TryResolveBuffer(DescriptorBindingInfo binding, int frameIndex, out DescriptorBufferInfo bufferInfo)
		{
			bufferInfo = default;

			// Step 1: Exact name match from the mesh renderer's buffer cache.
			VkDataBuffer? buffer = null;
			if (!string.IsNullOrWhiteSpace(binding.Name) && _bufferCache.TryGetValue(binding.Name, out buffer))
			{
				// found by name — use it directly
			}
			else
			{
				// Step 1.5: For SSBOs, prefer explicit binding-index mapping from XRDataBuffer.
				// This is more robust than name matching because SPIR-V reflection names can
				// vary by compiler/optimization path.
				if (binding.DescriptorType == DescriptorType.StorageBuffer)
				{
					buffer = _bufferCache.Values.FirstOrDefault(b =>
						b.Data.Target == EBufferTarget.ShaderStorageBuffer &&
						b.Data.BindingIndexOverride == binding.Binding);
				}

				if (buffer is not null)
					goto BufferResolved;

				// Step 2: Name lookup missed. Try auto/engine uniform resolution
				// before resorting to the generic cache scan. This prevents an
				// unrelated SSBO (e.g. LinesBuffer) from being returned for a UBO
				// binding that should resolve to an auto-uniform block.
				if (TryResolveAutoUniformBuffer(binding, frameIndex, out bufferInfo))
					return true;

				if (TryResolveEngineUniformBuffer(binding, frameIndex, out bufferInfo))
					return true;

				// Step 3: Generic fallback — only match buffers whose target type
				// is compatible with the descriptor's expected type.
				EBufferTarget requiredTarget = binding.DescriptorType switch
				{
					DescriptorType.UniformBuffer => EBufferTarget.UniformBuffer,
					DescriptorType.StorageBuffer => EBufferTarget.ShaderStorageBuffer,
					_ => EBufferTarget.UniformBuffer,
				};
				buffer = _bufferCache.Values.FirstOrDefault(b => b.Data.Target == requiredTarget);
			}

			if (buffer is null)
			{
				if (binding.DescriptorType is DescriptorType.UniformBuffer or DescriptorType.StorageBuffer)
					return TryResolveFallbackDescriptorBuffer(binding, frameIndex, out bufferInfo);

				WarnOnce($"[BufferResolve] Failed to resolve buffer for binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}). Cache keys: [{string.Join(", ", _bufferCache.Keys)}]");
				return false;
			}

		BufferResolved:

			buffer.Generate();
			if (buffer.BufferHandle is not { } bufferHandle || bufferHandle.Handle == 0)
			{
				WarnOnce($"[BufferResolve] Buffer '{binding.Name}' resolved (set={binding.Set}, binding={binding.Binding}) but VkBuffer is not allocated (Length={buffer.Data.Length}, Resizable={buffer.Data.Resizable}, Target={buffer.Data.Target}).");
				return false;
			}

			bufferInfo = new DescriptorBufferInfo
			{
				Buffer = bufferHandle,
				Offset = 0,
				Range = Math.Max(buffer.Data.Length, 1u),
			};

			return true;
		}

		private bool TryResolveFallbackDescriptorBuffer(DescriptorBindingInfo binding, int frameIndex, out DescriptorBufferInfo bufferInfo)
		{
			bufferInfo = default;
			uint requiredSize = Math.Max(FallbackDescriptorUniformSize, Math.Max(binding.Count, 1u) * 16u);
			if (!EnsureEngineUniformBuffer(FallbackDescriptorUniformName, requiredSize))
				return false;

			if (!_engineUniformBuffers.TryGetValue(FallbackDescriptorUniformName, out EngineUniformBuffer[]? buffers) || buffers.Length == 0)
				return false;

			int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
			EngineUniformBuffer target = buffers[idx];
			if (target.Buffer.Handle == 0)
				return false;

			if (!string.IsNullOrWhiteSpace(binding.Name))
				WarnOnce($"Using fallback descriptor buffer for unresolved {binding.DescriptorType} binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
			bufferInfo = new DescriptorBufferInfo
			{
				Buffer = target.Buffer,
				Offset = 0,
				Range = target.Size,
			};

			return true;
		}

		/// <summary>
		/// Resolves an image descriptor from the material's textures. Handles
		/// combined-image-sampler, sampled-image, and storage-image types.
		/// For combined depth-stencil formats, automatically creates a depth-only view.
		/// </summary>
		private bool TryResolveImage(DescriptorBindingInfo binding, XRMaterial material, DescriptorType descriptorType, out DescriptorImageInfo imageInfo, int arrayIndex = 0)
		{
			imageInfo = default;
			XRTexture? texture = null;

			if (material.Textures is { Count: > 0 })
			{
				int idx = (int)binding.Binding + arrayIndex;
				if (idx >= 0 && idx < material.Textures.Count)
					texture = material.Textures[idx];
			}

			if (texture is null)
			{
				// Use a 1×1 magenta placeholder to satisfy the descriptor binding
				// instead of failing the entire descriptor set write.
				imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType);
				if (imageInfo.ImageView.Handle != 0)
					return true;

				WarnOnce($"No texture available for descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				return false;
			}

			if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is not a Vulkan texture.");
				return false;
			}

			bool requiresSampledUsage = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.Sampler or DescriptorType.InputAttachment;
			if (requiresSampledUsage && (source.DescriptorUsage & ImageUsageFlags.SampledBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_SAMPLED_BIT.");
				return false;
			}

			if (descriptorType == DescriptorType.StorageImage && (source.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_STORAGE_BIT.");
				return false;
			}

			if (IsCombinedDepthStencilFormat(source.DescriptorFormat) &&
				(source.DescriptorAspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
			{
				// Use a depth-only view for combined depth-stencil descriptors.
				ImageView depthOnlyView = source.GetDepthOnlyDescriptorView();
				if (depthOnlyView.Handle != 0)
				{
					imageInfo = new DescriptorImageInfo
					{
						ImageLayout = descriptorType == DescriptorType.StorageImage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal,
						ImageView = depthOnlyView,
						Sampler = descriptorType == DescriptorType.CombinedImageSampler ? source.DescriptorSampler : default,
					};
					return true;
				}

				WarnOnce($"Texture for descriptor binding '{binding.Name}' uses a combined depth-stencil format and no depth-only view is available.");
				return false;
			}

			imageInfo = new DescriptorImageInfo
			{
				ImageLayout = descriptorType == DescriptorType.StorageImage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal,
				ImageView = source.DescriptorView,
				Sampler = descriptorType == DescriptorType.CombinedImageSampler ? source.DescriptorSampler : default,
			};
			return imageInfo.ImageView.Handle != 0;
		}

		/// <summary>Returns true if the Vulkan format is a combined depth+stencil format.</summary>
		private static bool IsCombinedDepthStencilFormat(Format format)
			=> format is Format.D24UnormS8Uint
				or Format.D32SfloatS8Uint
				or Format.D16UnormS8Uint;

		/// <summary>
		/// Resolves a texel buffer view descriptor from the material's textures.
		/// The texture must implement <see cref="IVkTexelBufferDescriptorSource"/>.
		/// </summary>
		private bool TryResolveTexelBuffer(DescriptorBindingInfo binding, XRMaterial material, out BufferView texelView, int arrayIndex = 0)
		{
			texelView = default;
			XRTexture? texture = null;

			if (material.Textures is { Count: > 0 })
			{
				int idx = (int)binding.Binding + arrayIndex;
				if (idx >= 0 && idx < material.Textures.Count)
					texture = material.Textures[idx];
			}

			if (texture is null)
			{
				WarnOnce($"No texture available for texel descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				return false;
			}

			if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkTexelBufferDescriptorSource source)
			{
				WarnOnce($"Texture for texel descriptor binding '{binding.Name}' is not a Vulkan texel-buffer texture.");
				return false;
			}

			texelView = source.DescriptorBufferView;
			return texelView.Handle != 0;
		}

		/// <summary>
		/// Resolves a descriptor buffer binding for a built-in engine uniform
		/// (e.g. ModelMatrix, ViewMatrix). Creates the per-frame UBO on demand.
		/// </summary>
			private bool TryResolveEngineUniformBuffer(DescriptorBindingInfo binding, int frameIndex, out DescriptorBufferInfo bufferInfo)
			{
				bufferInfo = default;
				string name = binding.Name ?? string.Empty;
				if (string.IsNullOrWhiteSpace(name))
					return false;

				uint size = GetEngineUniformSize(name);
				if (size == 0)
				{
					WarnOnce($"Descriptor binding '{name}' could not be matched to an engine uniform.");
					return false;
				}

				if (!EnsureEngineUniformBuffer(name, size))
					return false;

				if (!_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? buffers) || buffers.Length == 0)
					return false;

				int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
				EngineUniformBuffer target = buffers[idx];
				if (target.Buffer.Handle == 0)
					return false;

				bufferInfo = new DescriptorBufferInfo
				{
					Buffer = target.Buffer,
					Offset = 0,
					Range = size,
				};

				return true;
			}

			/// <summary>
			/// Resolves a descriptor buffer binding for a reflection-driven auto uniform
			/// block. Creates the per-frame UBO on demand.
			/// </summary>
			private bool TryResolveAutoUniformBuffer(DescriptorBindingInfo binding, int frameIndex, out DescriptorBufferInfo bufferInfo)
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

				int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
				AutoUniformBuffer target = buffers[idx];
				if (target.Buffer.Handle == 0)
					return false;

				bufferInfo = new DescriptorBufferInfo
				{
					Buffer = target.Buffer,
					Offset = 0,
					Range = size,
				};

				return true;
			}

			#endregion // Descriptor Set Management
	}
}

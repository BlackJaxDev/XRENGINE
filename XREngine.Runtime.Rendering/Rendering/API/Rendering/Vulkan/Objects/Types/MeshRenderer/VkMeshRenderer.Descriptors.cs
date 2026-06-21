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
		#region Descriptor Set Management

		private static readonly bool DescriptorResourceFingerprintDiagnosticsEnabled =
			string.Equals(Environment.GetEnvironmentVariable("XRE_VULKAN_FRAME_DATA_REUSE_DIAG"), "1", StringComparison.Ordinal) ||
			string.Equals(Environment.GetEnvironmentVariable("XRE_VULKAN_DESCRIPTOR_FINGERPRINT_DIAG"), "1", StringComparison.Ordinal);

		private static readonly bool MaterialBindingDiagnosticsEnabled =
			string.Equals(Environment.GetEnvironmentVariable("XRE_VULKAN_MATERIAL_BINDING_DIAG"), "1", StringComparison.Ordinal);

		/// <summary>
		/// Ensures descriptor sets are allocated and written for all swapchain frames.
		/// Uses a schema fingerprint to detect layout changes and avoid redundant
		/// reallocation when only resource bindings change.
		/// </summary>
		private bool EnsureDescriptorSets(XRMaterial material, int drawUniformSlot)
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

			EnsureUniformDrawSlotCapacity(drawUniformSlot + 1);
			if (!EnsureDescriptorUniformBuffers(bindings))
				return false;

			int frameCount = Renderer.swapChainImages.Length;
			int drawSlotCount = UniformBufferSlotCount;
			int descriptorFrameSlotCount = frameCount * drawSlotCount;
			int setCount = layouts.Count;
			ulong schemaFingerprint = ComputeDescriptorSchemaFingerprint(bindings, setCount);
			ulong resourceFingerprint = ComputeDescriptorResourceFingerprint(material, frameCount);
			DescriptorAllocationKey allocationKey = new(schemaFingerprint, resourceFingerprint, descriptorFrameSlotCount, setCount);

			if (_descriptorAllocations.TryGetValue(allocationKey, out DescriptorAllocation? cachedAllocation) &&
				IsDescriptorAllocationValid(cachedAllocation, descriptorFrameSlotCount, setCount))
			{
				ActivateDescriptorAllocation(cachedAllocation);
				_descriptorDirty = false;
				return true;
			}

			if (cachedAllocation is not null)
			{
				ReleaseDescriptorPool(cachedAllocation.Pool);
				_descriptorAllocations.Remove(allocationKey);
			}

			var poolSizes = BuildDescriptorPoolSizes(bindings, descriptorFrameSlotCount);
			if (poolSizes.Length == 0)
				return false;

			DescriptorPool descriptorPool = default;

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
					MaxSets = (uint)(setCount * descriptorFrameSlotCount),
				};

				if (Api!.CreateDescriptorPool(Device, ref poolInfo, null, out descriptorPool) != Result.Success)
				{
					Debug.VulkanWarning("Failed to create Vulkan descriptor pool for mesh renderer.");
					return false;
				}

				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();
			}

			DescriptorSetLayout[] layoutArray = [.. layouts];
			uint[] variableDescriptorCounts = _program.DescriptorSetsRequireVariableDescriptorCount
				? VulkanBindlessMaterialDescriptors.BuildVariableDescriptorCounts(bindings, layoutArray.Length)
				: [];
			DescriptorSet[][] descriptorSets = new DescriptorSet[descriptorFrameSlotCount][];

			for (int frame = 0; frame < frameCount; frame++)
			{
				for (int drawSlot = 0; drawSlot < drawSlotCount; drawSlot++)
				{
					DescriptorSet[] frameSets = new DescriptorSet[layoutArray.Length];

					fixed (DescriptorSetLayout* layoutPtr = layoutArray)
					fixed (DescriptorSet* setPtr = frameSets)
					fixed (uint* variableDescriptorCountPtr = variableDescriptorCounts)
					{
						DescriptorSetVariableDescriptorCountAllocateInfo variableDescriptorCountInfo = new()
						{
							SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
							DescriptorSetCount = (uint)layoutArray.Length,
							PDescriptorCounts = variableDescriptorCountPtr,
						};

						DescriptorSetAllocateInfo allocInfo = new()
						{
							SType = StructureType.DescriptorSetAllocateInfo,
							PNext = _program.DescriptorSetsRequireVariableDescriptorCount ? &variableDescriptorCountInfo : null,
							DescriptorPool = descriptorPool,
							DescriptorSetCount = (uint)layoutArray.Length,
							PSetLayouts = layoutPtr,
						};

						if (Api!.AllocateDescriptorSets(Device, ref allocInfo, setPtr) != Result.Success)
						{
							Debug.VulkanWarning("Failed to allocate Vulkan descriptor sets for mesh renderer.");
							ReleaseDescriptorPool(descriptorPool, destroyImmediately: true);
							return false;
						}
					}

					if (!WriteDescriptorSets(frameSets, bindings, material, frame, drawSlot))
					{
						ReleaseDescriptorPool(descriptorPool, destroyImmediately: true);
						return false;
					}

					descriptorSets[ResolveUniformBufferIndex(frame, drawSlot, descriptorSets.Length)] = frameSets;
				}
			}

			DescriptorAllocation allocation = new()
			{
				Pool = descriptorPool,
				Sets = descriptorSets,
				SchemaFingerprint = schemaFingerprint,
				ResourceFingerprint = resourceFingerprint,
				ResourceFingerprintDetails = DescriptorResourceFingerprintDiagnosticsEnabled
					? ComputeDescriptorResourceFingerprintDetails(material, frameCount)
					: string.Empty
			};

			_descriptorAllocations[allocationKey] = allocation;
			ActivateDescriptorAllocation(allocation);
			_descriptorDirty = false;
			return true;
		}

		private bool EnsureDescriptorUniformBuffers(IReadOnlyList<DescriptorBindingInfo> bindings)
		{
			if (_program is null)
				return false;

			foreach (DescriptorBindingInfo binding in bindings)
			{
				if (binding.DescriptorType != DescriptorType.UniformBuffer)
					continue;

				if (_program.TryGetAutoUniformBlockFuzzy(binding.Name ?? string.Empty, binding.Set, binding.Binding, out AutoUniformBlockInfo block))
				{
					if (!EnsureAutoUniformBuffer(block.InstanceName, Math.Max(block.Size, 1u)))
						return false;
					continue;
				}

				string bindingName = binding.Name ?? string.Empty;
				uint engineSize = GetEngineUniformSize(bindingName);
				if (engineSize != 0 && !EnsureEngineUniformBuffer(NormalizeEngineUniformName(bindingName), engineSize))
					return false;
			}

			return true;
		}

		internal bool CanReuseRecordedDescriptorSets(XRMaterial material, int drawUniformSlot)
			=> CanReuseRecordedDescriptorSets(material, drawUniformSlot, out _);

		internal bool CanReuseRecordedDescriptorSets(XRMaterial material, int drawUniformSlot, out string reason)
		{
			reason = "reusable";
			if (_program is null)
				return true;

			var layouts = _program.DescriptorSetLayouts;
			var bindings = _program.DescriptorBindings;
			if (layouts is null || layouts.Count == 0 || bindings.Count == 0)
				return true;

			if (Renderer.swapChainImages is null || Renderer.swapChainImages.Length == 0)
			{
				reason = "swapchain images unavailable";
				return false;
			}

			int requiredSlots = Math.Max(drawUniformSlot + 1, 1);
			if (requiredSlots > _uniformDrawSlotCapacity)
			{
				reason = $"draw slot capacity {requiredSlots}>{_uniformDrawSlotCapacity}";
				return false;
			}

			int frameCount = Renderer.swapChainImages.Length;
			int descriptorFrameSlotCount = frameCount * UniformBufferSlotCount;
			int setCount = layouts.Count;
			ulong schemaFingerprint = ComputeDescriptorSchemaFingerprint(bindings, setCount);
			ulong resourceFingerprint = ComputeDescriptorResourceFingerprint(material, frameCount);
			DescriptorAllocationKey allocationKey = new(schemaFingerprint, resourceFingerprint, descriptorFrameSlotCount, setCount);

			if (!_descriptorAllocations.TryGetValue(allocationKey, out DescriptorAllocation? allocation))
			{
				reason = "descriptor pool missing";
				return false;
			}

			if (!IsDescriptorAllocationValid(allocation, descriptorFrameSlotCount, setCount))
			{
				reason = "descriptor allocation invalid";
				return false;
			}

			if (allocation.SchemaFingerprint != schemaFingerprint)
			{
				reason = $"schema fingerprint 0x{allocation.SchemaFingerprint:X16}->0x{schemaFingerprint:X16}";
				return false;
			}

			if (allocation.ResourceFingerprint != resourceFingerprint)
			{
				if (DescriptorResourceFingerprintDiagnosticsEnabled)
				{
					string currentDetails = ComputeDescriptorResourceFingerprintDetails(material, frameCount);
					reason = $"resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}; old=[{allocation.ResourceFingerprintDetails}] new=[{currentDetails}]";
				}
				else
				{
					reason = $"resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}";
				}
				return false;
			}

			ActivateDescriptorAllocation(allocation);
			return true;
		}

		private void ActivateDescriptorAllocation(DescriptorAllocation allocation)
		{
			_descriptorPool = allocation.Pool;
			_descriptorSets = allocation.Sets;
			_descriptorSchemaFingerprint = allocation.SchemaFingerprint;
			_descriptorResourceFingerprint = allocation.ResourceFingerprint;
			_descriptorResourceFingerprintDetails = allocation.ResourceFingerprintDetails;
		}

		private static bool IsDescriptorAllocationValid(DescriptorAllocation allocation, int descriptorFrameSlotCount, int setCount)
		{
			return allocation.Pool.Handle != 0 &&
				allocation.Sets is { Length: > 0 } &&
				allocation.Sets.Length == descriptorFrameSlotCount &&
				DescriptorSetsHaveSetCount(allocation.Sets, setCount);
		}

		/// <summary>
		/// Computes a fingerprint over descriptor set layout metadata (binding indices,
		/// types, stages). Used to detect when the schema changes and a full
		/// reallocation of the descriptor pool is required.
		/// </summary>
		private static ulong ComputeDescriptorSchemaFingerprint(IReadOnlyList<DescriptorBindingInfo> bindings, int setCount)
		{
			HashCode hash = new();
			hash.Add(setCount);

			foreach (DescriptorBindingInfo binding in bindings)
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

		private string ComputeDescriptorResourceFingerprintDetails(XRMaterial material, int frameCount)
		{
			StringBuilder builder = new(256);
			AppendComponent(builder, "frames", frameCount);
			AppendComponent(builder, "skinned", MeshRenderer.SkinnedOutputVersion);
			AppendComponent(builder, "buffers", ComputeCachedBufferResourceFingerprint());
			AppendComponent(builder, "textures", ComputeMaterialTextureResourceFingerprint(material));
			AppendComponent(builder, "engineUbo", ComputeEngineUniformResourceFingerprint());
			AppendComponent(builder, "autoUbo", ComputeAutoUniformResourceFingerprint());
			if (_program is not null)
			{
				AppendComponent(builder, "programSamplers", _program.ComputeSamplerResourceFingerprint());
				AppendComponent(builder, "programBuffers", _program.ComputeBoundBufferResourceFingerprint());
			}
			else
			{
				AppendComponent(builder, "programSamplers", 0UL);
				AppendComponent(builder, "programBuffers", 0UL);
			}

			return builder.ToString();
		}

		private static void AppendComponent(StringBuilder builder, string name, int value)
			=> AppendComponent(builder, name, unchecked((ulong)value));

		private static void AppendComponent(StringBuilder builder, string name, ulong value)
		{
			if (builder.Length > 0)
				builder.Append(' ');
			builder.Append(name);
			builder.Append("=0x");
			builder.Append(value.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
		}

		private ulong ComputeCachedBufferResourceFingerprint()
		{
			return ComputeCachedBufferResourceFingerprintCore();
		}

		private ulong ComputeMaterialTextureResourceFingerprint(XRMaterial material)
		{
			HashCode hash = new();
			hash.Add(material.Textures.Count);
			for (int i = 0; i < material.Textures.Count; i++)
				AddTextureDescriptorResourceFingerprint(ref hash, material.Textures[i]);
			return unchecked((ulong)hash.ToHashCode());
		}

		private ulong ComputeEngineUniformResourceFingerprint()
		{
			return ComputeEngineUniformResourceFingerprintCore();
		}

		private ulong ComputeAutoUniformResourceFingerprint()
		{
			return ComputeAutoUniformResourceFingerprintCore();
		}

		private ulong ComputeDescriptorResourceFingerprint(XRMaterial material, int frameCount)
		{
			HashCode hash = new();
			hash.Add(frameCount);
			hash.Add(MeshRenderer.SkinnedOutputVersion);

			hash.Add(ComputeCachedBufferResourceFingerprintCore());

			hash.Add(material.Textures.Count);
			for (int i = 0; i < material.Textures.Count; i++)
			{
				XRTexture? texture = material.Textures[i];
				AddTextureDescriptorResourceFingerprint(ref hash, texture);
			}

			hash.Add(ComputeEngineUniformResourceFingerprintCore());
			hash.Add(ComputeAutoUniformResourceFingerprintCore());

			// Program-bound named samplers (material textures, engine/FBO blit bindings)
			// participate in image descriptor resolution, so changes to them must rewrite
			// the descriptor sets.
			_program?.AddSamplerResourceFingerprint(ref hash);
			_program?.AddBoundBufferResourceFingerprint(ref hash);

			return unchecked((ulong)hash.ToHashCode());
		}

		private static bool DescriptorSetsHaveSetCount(DescriptorSet[][] descriptorSets, int setCount)
		{
			for (int i = 0; i < descriptorSets.Length; i++)
			{
				if (descriptorSets[i].Length != setCount)
					return false;
			}

			return true;
		}

		private ulong ComputeCachedBufferResourceFingerprintCore()
		{
			ulong xor = 0;
			ulong sum = 0;
			foreach (KeyValuePair<string, VkDataBuffer> pair in _bufferCache)
				AddUnorderedFingerprintItem(ref xor, ref sum, ComputeCachedBufferResourceFingerprintItem(pair.Key, pair.Value));

			HashCode hash = new();
			hash.Add(_bufferCache.Count);
			hash.Add(xor);
			hash.Add(sum);
			return unchecked((ulong)hash.ToHashCode());
		}

		private static ulong ComputeCachedBufferResourceFingerprintItem(string name, VkDataBuffer buffer)
		{
			HashCode item = new();
			item.Add(name, StringComparer.Ordinal);
			item.Add(buffer.BufferHandle?.Handle ?? 0UL);
			item.Add(buffer.Data.Length);
			item.Add((int)buffer.Data.Target);
			item.Add(buffer.Data.BindingIndexOverride ?? uint.MaxValue);
			return unchecked((ulong)item.ToHashCode());
		}

		private ulong ComputeEngineUniformResourceFingerprintCore()
		{
			ulong xor = 0;
			ulong sum = 0;
			foreach (KeyValuePair<string, EngineUniformBuffer[]> pair in _engineUniformBuffers)
				AddUnorderedFingerprintItem(ref xor, ref sum, ComputeEngineUniformBufferArrayFingerprintItem(pair.Key, pair.Value));

			HashCode hash = new();
			hash.Add(_engineUniformBuffers.Count);
			hash.Add(xor);
			hash.Add(sum);
			return unchecked((ulong)hash.ToHashCode());
		}

		private ulong ComputeAutoUniformResourceFingerprintCore()
		{
			ulong xor = 0;
			ulong sum = 0;
			foreach (KeyValuePair<string, AutoUniformBuffer[]> pair in _autoUniformBuffers)
				AddUnorderedFingerprintItem(ref xor, ref sum, ComputeAutoUniformBufferArrayFingerprintItem(pair.Key, pair.Value));

			HashCode hash = new();
			hash.Add(_autoUniformBuffers.Count);
			hash.Add(xor);
			hash.Add(sum);
			return unchecked((ulong)hash.ToHashCode());
		}

		private static ulong ComputeEngineUniformBufferArrayFingerprintItem(string name, EngineUniformBuffer[] buffers)
		{
			HashCode item = new();
			item.Add(name, StringComparer.Ordinal);
			item.Add(buffers.Length);
			for (int i = 0; i < buffers.Length; i++)
				item.Add(buffers[i].Size);

			return unchecked((ulong)item.ToHashCode());
		}

		private static ulong ComputeAutoUniformBufferArrayFingerprintItem(string name, AutoUniformBuffer[] buffers)
		{
			HashCode item = new();
			item.Add(name, StringComparer.Ordinal);
			item.Add(buffers.Length);
			for (int i = 0; i < buffers.Length; i++)
				item.Add(buffers[i].Size);

			return unchecked((ulong)item.ToHashCode());
		}

		private static void AddUnorderedFingerprintItem(ref ulong xor, ref ulong sum, ulong itemHash)
		{
			unchecked
			{
				xor ^= itemHash;
				sum += System.Numerics.BitOperations.RotateLeft(itemHash, (int)(itemHash & 31));
			}
		}

		private void AddTextureDescriptorResourceFingerprint(ref HashCode hash, XRTexture? texture)
		{
			hash.Add(texture?.GetHashCode() ?? 0);
			if (texture is null)
			{
				hash.Add(0UL);
				return;
			}

			object? apiObject = Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true);
			if (apiObject is IVkImageDescriptorSource imageSource)
			{
				hash.Add(imageSource.DescriptorImage.Handle);
				hash.Add(imageSource.DescriptorView.Handle);
				hash.Add(imageSource.DescriptorSampler.Handle);
				hash.Add(imageSource.DescriptorViewType);
				hash.Add(imageSource.DescriptorFormat);
				hash.Add(imageSource.DescriptorAspect);
				hash.Add(imageSource.DescriptorUsage);
			}
			else
			{
				hash.Add(0UL);
			}

			if (apiObject is IVkTexelBufferDescriptorSource texelSource)
			{
				hash.Add(texelSource.DescriptorBufferView.Handle);
				hash.Add(texelSource.DescriptorBufferFormat);
			}
			else
			{
				hash.Add(0UL);
			}
		}

		/// <summary>Resolves one or more buffer descriptors for a binding, duplicating for array bindings.</summary>
		private bool TryResolveBuffers(DescriptorBindingInfo binding, int frameIndex, int drawUniformSlot, uint descriptorCount, List<DescriptorBufferInfo> bufferInfos, out int bufferStart)
		{
			bufferStart = bufferInfos.Count;
			if (!TryResolveBuffer(binding, frameIndex, drawUniformSlot, out DescriptorBufferInfo bufferInfo))
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
				uint count = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding) * (uint)frameCount;
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

		private static string GetDescriptorBindingClass(DescriptorType descriptorType)
			=> descriptorType switch
			{
				DescriptorType.StorageImage => "storage-image",
				DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => "uniform-buffer",
				DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => "storage-buffer",
				DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer => "texel-buffer",
				_ => "sampled-image",
			};

		private void RecordDescriptorFailure(DescriptorBindingInfo binding, string reason, bool skippedDraw = true)
			=> RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
				_program?.Data?.Name,
				GetDescriptorBindingClass(binding.DescriptorType),
				binding.Name,
				binding.Set,
				binding.Binding,
				skippedDraw,
				skippedDispatch: false,
				reason);

		private void RecordDescriptorFallback(DescriptorBindingInfo binding, int count = 1)
			=> RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorFallback(
				_program?.Data?.Name,
				GetDescriptorBindingClass(binding.DescriptorType),
				binding.Name,
				binding.Set,
				binding.Binding,
				count);

		/// <summary>
		/// Writes all descriptor bindings for a single frame's descriptor sets.
		/// Resolves buffers, images, and texel buffers, then issues a batched
		/// <c>vkUpdateDescriptorSets</c> call for all pending writes.
		/// </summary>
		private bool WriteDescriptorSets(DescriptorSet[] frameSets, IReadOnlyList<DescriptorBindingInfo> bindings, XRMaterial material, int frameIndex, int drawUniformSlot)
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

				uint descriptorCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);

				switch (binding.DescriptorType)
				{
					case DescriptorType.UniformBuffer:
					case DescriptorType.StorageBuffer:
						if (!TryResolveBuffers(binding, frameIndex, drawUniformSlot, descriptorCount, bufferInfos, out int bufferStart))
						{
							WarnOnce($"[WriteDesc] FAILED to resolve buffer binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}' program '{_program?.Data?.Name ?? "?"}'");
							RecordDescriptorFailure(binding, "buffer resolution failed");
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
							RecordDescriptorFailure(binding, "image resolution failed");
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
						{
							RecordDescriptorFailure(binding, "texel buffer resolution failed");
							return false;
						}

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
				{
					if (!ValidateDescriptorWrites(writePtr, writeArray.Length))
						return false;

					if (!TryUpdateDescriptorSetsWithTemplates(frameSets, writeArray))
						Api!.UpdateDescriptorSets(Device, (uint)writeArray.Length, writePtr, 0, null);
				}
			}

			return true;
		}

		private bool ValidateDescriptorWrites(WriteDescriptorSet* writes, int count)
		{
			for (int i = 0; i < count; i++)
			{
				WriteDescriptorSet write = writes[i];
				switch (write.DescriptorType)
				{
					case DescriptorType.CombinedImageSampler:
						if (!ValidateImageDescriptors(write, requireImageView: true, requireSampler: true, i))
							return false;
						break;
					case DescriptorType.Sampler:
						if (!ValidateImageDescriptors(write, requireImageView: false, requireSampler: true, i))
							return false;
						break;
					case DescriptorType.SampledImage:
					case DescriptorType.StorageImage:
					case DescriptorType.InputAttachment:
						if (!ValidateImageDescriptors(write, requireImageView: true, requireSampler: false, i))
							return false;
						break;
					case DescriptorType.UniformBuffer:
					case DescriptorType.StorageBuffer:
						if (write.PBufferInfo is null || HasZeroBuffer(write.PBufferInfo, write.DescriptorCount))
						{
							WarnOnce($"Skipping descriptor update for mesh '{Mesh?.Name ?? "?"}' because write[{i}] has an invalid buffer descriptor.");
							return false;
						}
						break;
					case DescriptorType.UniformTexelBuffer:
					case DescriptorType.StorageTexelBuffer:
						if (write.PTexelBufferView is null || HasZeroBufferView(write.PTexelBufferView, write.DescriptorCount))
						{
							WarnOnce($"Skipping descriptor update for mesh '{Mesh?.Name ?? "?"}' because write[{i}] has an invalid texel buffer view.");
							return false;
						}
						break;
				}
			}

			return true;
		}

		private bool ValidateImageDescriptors(WriteDescriptorSet write, bool requireImageView, bool requireSampler, int writeIndex)
		{
			if (write.PImageInfo is null)
			{
				WarnOnce($"Skipping descriptor update for mesh '{Mesh?.Name ?? "?"}' because write[{writeIndex}] has no image descriptor data.");
				return false;
			}

			for (uint i = 0; i < write.DescriptorCount; i++)
			{
				DescriptorImageInfo info = write.PImageInfo[i];
				if (requireImageView && info.ImageView.Handle == 0)
				{
					WarnOnce($"Skipping descriptor update for mesh '{Mesh?.Name ?? "?"}' because write[{writeIndex}].image[{i}] has no image view.");
					return false;
				}

				if (requireSampler && info.Sampler.Handle == 0)
				{
					WarnOnce($"Skipping descriptor update for mesh '{Mesh?.Name ?? "?"}' because write[{writeIndex}].image[{i}] has no sampler.");
					return false;
				}

				if (requireSampler && !Renderer.IsLiveSampler(info.Sampler))
				{
					WarnOnce($"Skipping descriptor update for mesh '{Mesh?.Name ?? "?"}' because write[{writeIndex}].image[{i}] references a retired sampler.");
					return false;
				}
			}

			return true;
		}

		private static bool HasZeroBuffer(DescriptorBufferInfo* buffers, uint count)
		{
			for (uint i = 0; i < count; i++)
			{
				if (buffers[i].Buffer.Handle == 0)
					return true;
			}

			return false;
		}

		private static bool HasZeroBufferView(BufferView* views, uint count)
		{
			for (uint i = 0; i < count; i++)
			{
				if (views[i].Handle == 0)
					return true;
			}

			return false;
		}

		private bool TryUpdateDescriptorSetsWithTemplates(DescriptorSet[] frameSets, WriteDescriptorSet[] writeArray)
		{
			if (RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template)
				return false;

			if (_program is null || _program.DescriptorSetLayouts.Count < frameSets.Length)
				return false;

			for (int setIndex = 0; setIndex < frameSets.Length; setIndex++)
			{
				List<WriteDescriptorSet> setWrites = [];
				for (int i = 0; i < writeArray.Length; i++)
				{
					if (writeArray[i].DstSet.Handle == frameSets[setIndex].Handle)
						setWrites.Add(writeArray[i]);
				}

				if (setWrites.Count == 0)
					continue;

				if (!Renderer.TryUpdateDescriptorSetWithTemplate(
					frameSets[setIndex],
					_program.DescriptorSetLayouts[setIndex],
					PipelineBindPoint.Graphics,
					_program.PipelineLayout,
					(uint)setIndex,
					CollectionsMarshal.AsSpan(setWrites)))
				{
					return false;
				}
			}

			return true;
		}

		// ── Individual Resource Resolution ───────────────────────────────────

		/// <summary>
		/// Resolves a buffer descriptor for a single binding. Searches the buffer
		/// cache by name, then falls back to auto uniform and engine uniform buffers.
		/// </summary>
		private bool TryResolveBuffer(DescriptorBindingInfo binding, int frameIndex, int drawUniformSlot, out DescriptorBufferInfo bufferInfo)
		{
			bufferInfo = default;

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

					foreach (VkDataBuffer candidate in _bufferCache.Values)
					{
						if (candidate.Data.Target == EBufferTarget.ShaderStorageBuffer &&
							candidate.Data.BindingIndexOverride == binding.Binding)
						{
							buffer = candidate;
							break;
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
				if (binding.DescriptorType == DescriptorType.UniformBuffer)
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

			if (buffer is null)
			{
				if (binding.DescriptorType is DescriptorType.UniformBuffer or DescriptorType.StorageBuffer)
					return TryResolveFallbackDescriptorBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo);

				WarnOnce($"[BufferResolve] Failed to resolve buffer for binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}). Cache keys: [{string.Join(", ", _bufferCache.Keys)}]");
				return false;
			}

		BufferResolved:

			if (buffer is null)
				return TryResolveFallbackDescriptorBuffer(binding, frameIndex, drawUniformSlot, out bufferInfo);

			buffer.EnsureReadyForRendering();
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
			if (Renderer.GetOrCreateAPIRenderObject(dataBuffer, generateNow: true) is not VkDataBuffer vkBuffer)
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
				DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => target == EBufferTarget.ShaderStorageBuffer,
				DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => target == EBufferTarget.UniformBuffer,
				_ => false,
			};

		private bool TryResolveProgramBoundBuffer(DescriptorBindingInfo binding, out VkDataBuffer? buffer)
		{
			buffer = null;
			if (_program is null || !_program.TryGetBoundBuffer(binding.Binding, out XRDataBuffer? dataBuffer) || dataBuffer is null)
				return false;

			bool targetMatches = binding.DescriptorType switch
			{
				DescriptorType.StorageBuffer => dataBuffer.Target == EBufferTarget.ShaderStorageBuffer,
				DescriptorType.UniformBuffer => dataBuffer.Target == EBufferTarget.UniformBuffer,
				_ => false,
			};

			if (!targetMatches)
				return false;

			Renderer.TrackBufferBinding(dataBuffer);
			if (Renderer.GetOrCreateAPIRenderObject(dataBuffer, generateNow: true) is not VkDataBuffer vkBuffer)
				return false;

			buffer = vkBuffer;
			return true;
		}

		private bool TryResolveCachedBufferByName(string bindingName, out VkDataBuffer? buffer)
		{
			if (_bufferCache.TryGetValue(bindingName, out buffer))
				return true;

			string trimmedName = TrimDescriptorBufferSuffix(bindingName);
			if (!string.Equals(trimmedName, bindingName, StringComparison.Ordinal) &&
				_bufferCache.TryGetValue(trimmedName, out buffer))
			{
				return true;
			}

			string aliasName = string.Empty;
			if (TryGetDebugPrimitiveBufferAlias(bindingName, out aliasName) &&
				_bufferCache.TryGetValue(aliasName, out buffer))
			{
				return true;
			}

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
		{
			if (bindingName.EndsWith("Input", StringComparison.Ordinal))
				return bindingName[..^5];
			if (bindingName.EndsWith("Buffer", StringComparison.Ordinal))
				return bindingName[..^6];
			return bindingName;
		}

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
			bool bindless = VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding);
			MaterialTextureBindingResolution textureBinding = MaterialTextureBindingResolver.Resolve(
				material,
				binding.Name,
				(int)binding.Binding,
				arrayIndex,
				bindless,
				samplerName =>
				{
					if (_program is not null && _program.TryGetSamplerTexture(samplerName, out XRTexture? namedTexture))
						return namedTexture;

					return null;
				});
			XRTexture? texture = textureBinding.Texture;

			if (texture is null)
			{
				// Use a 1×1 magenta placeholder to satisfy the descriptor binding
				// instead of failing the entire descriptor set write.
				imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
				if (imageInfo.ImageView.Handle != 0)
				{
					LogPostProcessDescriptor(binding, arrayIndex, null, imageInfo, "placeholder-missing-texture");
					LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, null, null, imageInfo, "placeholder-missing-texture");
					LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, null, null, imageInfo, "placeholder-missing-texture");
					RecordDescriptorFallback(binding);
					return true;
				}

				WarnOnce($"No texture available for descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				RecordDescriptorFailure(binding, "missing texture and placeholder unavailable");
				return false;
			}

			if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is not a Vulkan texture.");
				RecordDescriptorFailure(binding, "texture has no Vulkan descriptor source");
				return false;
			}

			bool requiresSampledUsage = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.Sampler or DescriptorType.InputAttachment;
			if (requiresSampledUsage && (source.DescriptorUsage & ImageUsageFlags.SampledBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_SAMPLED_BIT.");
				RecordDescriptorFailure(binding, "texture missing VK_IMAGE_USAGE_SAMPLED_BIT");
				return false;
			}

			if (descriptorType == DescriptorType.StorageImage && (source.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_STORAGE_BIT.");
				RecordDescriptorFailure(binding, "texture missing VK_IMAGE_USAGE_STORAGE_BIT");
				return false;
			}

			if (IsCombinedDepthStencilFormat(source.DescriptorFormat) &&
				(source.DescriptorAspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
			{
				bool stencilOnly = RequiresStencilOnlyDescriptor(binding);
				ImageView aspectView = stencilOnly
					? source.GetStencilOnlyDescriptorView()
					: source.GetDepthOnlyDescriptorView();
				string aspectLabel = stencilOnly ? "stencil-only" : "depth-only";
				if (aspectView.Handle != 0)
				{
					if (!TryResolveDescriptorSampler(binding, descriptorType, source, out Sampler sampler))
						return false;

					imageInfo = new DescriptorImageInfo
					{
						ImageLayout = Renderer.ResolveDescriptorImageLayout(source, descriptorType),
						ImageView = aspectView,
						Sampler = sampler,
					};
					LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, $"{source.DescriptorFormat}/{source.DescriptorAspect}/{aspectLabel}");
					LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, $"{source.DescriptorFormat}/{source.DescriptorAspect}/{aspectLabel}");
					LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, $"{source.DescriptorFormat}/{source.DescriptorAspect}/{aspectLabel}");
					return true;
				}

				WarnOnce($"Texture for descriptor binding '{binding.Name}' uses a combined depth-stencil format and no {aspectLabel} view is available.");
				RecordDescriptorFailure(binding, $"combined depth-stencil texture has no {aspectLabel} view");
				return false;
			}

			ImageView descriptorView = ResolveDescriptorView(binding, source);
			if (descriptorView.Handle == 0)
			{
				imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
				if (imageInfo.ImageView.Handle != 0)
				{
					WarnOnce($"Texture for descriptor binding '{binding.Name}' cannot provide expected view type '{binding.ExpectedImageViewType}'. Using placeholder.");
					LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, "placeholder-view-type");
					LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, "placeholder-view-type");
					LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, "placeholder-view-type");
					RecordDescriptorFallback(binding);
					return true;
				}

				WarnOnce($"Texture for descriptor binding '{binding.Name}' cannot provide expected view type '{binding.ExpectedImageViewType}'.");
				RecordDescriptorFailure(binding, "texture view type mismatch");
				return false;
			}

			if (!TryResolveDescriptorSampler(binding, descriptorType, source, out Sampler descriptorSampler))
				return false;

			imageInfo = new DescriptorImageInfo
			{
				ImageLayout = Renderer.ResolveDescriptorImageLayout(source, descriptorType),
				ImageView = descriptorView,
				Sampler = descriptorSampler,
			};
			LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, $"{source.DescriptorFormat}/{source.DescriptorAspect}");
			LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, $"{source.DescriptorFormat}/{source.DescriptorAspect}");
			LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, $"{source.DescriptorFormat}/{source.DescriptorAspect}");
			return imageInfo.ImageView.Handle != 0;
		}

		private bool TryResolveDescriptorSampler(DescriptorBindingInfo binding, DescriptorType descriptorType, IVkImageDescriptorSource source, out Sampler sampler)
		{
			sampler = default;
			if (descriptorType is not (DescriptorType.CombinedImageSampler or DescriptorType.Sampler))
				return true;

			sampler = source.DescriptorSampler;
			if (sampler.Handle != 0 && Renderer.IsLiveSampler(sampler))
				return true;

			if (sampler.Handle != 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' references a retired Vulkan sampler. Using placeholder sampler.");
				RecordDescriptorFallback(binding);
			}

			sampler = Renderer.GetPlaceholderSampler();
			if (sampler.Handle != 0 && Renderer.IsLiveSampler(sampler))
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' has no Vulkan sampler. Using placeholder sampler.");
				RecordDescriptorFallback(binding);
				return true;
			}

			WarnOnce($"Texture for descriptor binding '{binding.Name}' has no Vulkan sampler and placeholder sampler is unavailable.");
			RecordDescriptorFailure(binding, "texture sampler unavailable");
			return false;
		}

		private void LogPostProcessDescriptor(
			DescriptorBindingInfo binding,
			int arrayIndex,
			XRTexture? texture,
			DescriptorImageInfo imageInfo,
			string detail)
		{
			if (!RenderDiagnosticsFlags.DiagPostProcess || !IsPostProcessSampler(binding.Name))
				return;

			string textureLabel = texture is null
				? "<null>"
				: $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode():X8}";

			Debug.VulkanEvery(
				$"PostProcess.Descriptor.{GetHashCode()}.{binding.Name}.{binding.Binding}.{arrayIndex}",
				TimeSpan.FromSeconds(1),
				"[PostProcessDiag] Descriptor name={0} set={1} binding={2} index={3} type={4} texture={5} layout={6} view=0x{7:X} sampler=0x{8:X} detail={9}",
				binding.Name,
				binding.Set,
				binding.Binding,
				arrayIndex,
				binding.DescriptorType,
				textureLabel,
				imageInfo.ImageLayout,
				imageInfo.ImageView.Handle,
				imageInfo.Sampler.Handle,
				detail);
		}

		private void LogDeferredLightingDescriptor(
			DescriptorBindingInfo binding,
			int arrayIndex,
			MaterialTextureBindingResolution resolution,
			XRTexture? texture,
			IVkImageDescriptorSource? source,
			DescriptorImageInfo imageInfo,
			string detail)
		{
			if (!DeferredLightingDiagnostics.Enabled || !DeferredLightingDiagnostics.IsDeferredLightCombineSampler(binding.Name))
				return;

			string textureLabel = texture is null
				? "<null>"
				: $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode():X8}";
			string programName = _program?.Data?.Name ?? "<null>";
			string meshName = Mesh?.Name ?? "<null>";
			string sourceImage = source is null ? "<null>" : $"0x{source.DescriptorImage.Handle:X}";
			string sourceView = source is null ? "<null>" : $"0x{source.DescriptorView.Handle:X}";
			string sourceSampler = source is null ? "<null>" : $"0x{source.DescriptorSampler.Handle:X}";
			string sourceLayout = source is null ? "<null>" : source.TrackedImageLayout.ToString();
			string sourceUsage = source is null ? "<null>" : source.DescriptorUsage.ToString();
			string sourceAllocator = source is null ? "<null>" : source.UsesAllocatorImage.ToString();

			DeferredLightingDiagnostics.Write(
				"[VkMeshRenderer.Descriptor] " +
				$"program='{programName}' mesh='{meshName}' " +
				$"name='{binding.Name ?? "<null>"}' set={binding.Set} binding={binding.Binding} arrayIndex={arrayIndex} type={binding.DescriptorType} " +
				$"rung={resolution.Rung} resolvedIndex={resolution.TextureIndex} resolvedSampler='{resolution.SamplerName ?? "<null>"}' reason='{resolution.Reason}' " +
				$"texture={textureLabel} imageInfoLayout={imageInfo.ImageLayout} imageInfoView=0x{imageInfo.ImageView.Handle:X} imageInfoSampler=0x{imageInfo.Sampler.Handle:X} " +
				$"sourceImage={sourceImage} sourceView={sourceView} sourceSampler={sourceSampler} sourceLayout={sourceLayout} sourceUsage={sourceUsage} allocatorImage={sourceAllocator} " +
				$"detail={detail}");
		}

		private void LogMaterialDescriptor(
			DescriptorBindingInfo binding,
			XRMaterial material,
			int arrayIndex,
			MaterialTextureBindingResolution resolution,
			XRTexture? texture,
			IVkImageDescriptorSource? source,
			DescriptorImageInfo imageInfo,
			string detail)
		{
			if (!MaterialBindingDiagnosticsEnabled || !IsMaterialSampler(binding.Name))
				return;

			string textureLabel = texture is null
				? "<null>"
				: $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode():X8}";
			string sourceLayout = source is null ? "<null>" : source.TrackedImageLayout.ToString();
			string sourceUsage = source is null ? "<null>" : source.DescriptorUsage.ToString();
			string programName = _program?.Data?.Name ?? "<null>";
			string meshName = Mesh?.Name ?? "<null>";
			string materialName = material.Name ?? "<null>";

			Debug.VulkanEvery(
				$"Vulkan.MaterialDescriptor.{GetHashCode()}.{programName}.{materialName}.{binding.Name}.{arrayIndex}",
				TimeSpan.FromSeconds(1),
				"[VkMaterialDescriptor] program='{0}' mesh='{1}' material='{2}' name='{3}' set={4} binding={5} arrayIndex={6} type={7} " +
				"rung={8} resolvedIndex={9} resolvedSampler='{10}' reason='{11}' texture={12} imageLayout={13} view=0x{14:X} sampler=0x{15:X} sourceLayout={16} sourceUsage={17} detail={18}",
				programName,
				meshName,
				materialName,
				binding.Name ?? "<null>",
				binding.Set,
				binding.Binding,
				arrayIndex,
				binding.DescriptorType,
				resolution.Rung,
				resolution.TextureIndex,
				resolution.SamplerName ?? "<null>",
				resolution.Reason,
				textureLabel,
				imageInfo.ImageLayout,
				imageInfo.ImageView.Handle,
				imageInfo.Sampler.Handle,
				sourceLayout,
				sourceUsage,
				detail);
		}

		private static bool IsPostProcessSampler(string? name)
			=> string.Equals(name, "HDRSceneTex", StringComparison.Ordinal)
			|| string.Equals(name, "BloomBlurTexture", StringComparison.Ordinal)
			|| string.Equals(name, "DepthView", StringComparison.Ordinal)
			|| string.Equals(name, "StencilView", StringComparison.Ordinal)
			|| string.Equals(name, "AutoExposureTex", StringComparison.Ordinal)
			|| string.Equals(name, "AtmosphereColor", StringComparison.Ordinal)
			|| string.Equals(name, "VolumetricFogColor", StringComparison.Ordinal);

		private static bool IsMaterialSampler(string? name)
			=> name is not null &&
			   name.StartsWith("Texture", StringComparison.Ordinal);

		private static bool RequiresStencilOnlyDescriptor(DescriptorBindingInfo binding)
			=> binding.Name?.Contains("Stencil", StringComparison.OrdinalIgnoreCase) == true;

		private ImageView ResolveDescriptorView(DescriptorBindingInfo binding, IVkImageDescriptorSource source)
		{
			if (binding.ExpectedImageViewType is not { } expectedViewType)
				return source.DescriptorView;

			return source.GetDescriptorView(expectedViewType);
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
			MaterialTextureBindingResolution textureBinding = MaterialTextureBindingResolver.Resolve(
				material,
				binding.Name,
				(int)binding.Binding,
				arrayIndex,
				bindlessMaterialArray: false,
				samplerName =>
				{
					if (_program is not null && _program.TryGetSamplerTexture(samplerName, out XRTexture? namedTexture))
						return namedTexture;

					return null;
				});
			XRTexture? texture = textureBinding.Texture;

			if (texture is null)
			{
				WarnOnce($"No texture available for texel descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				RecordDescriptorFailure(binding, "missing texel buffer texture");
				return false;
			}

			if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkTexelBufferDescriptorSource source)
			{
				WarnOnce($"Texture for texel descriptor binding '{binding.Name}' is not a Vulkan texel-buffer texture.");
				RecordDescriptorFailure(binding, "texture has no Vulkan texel-buffer source");
				return false;
			}

			texelView = source.DescriptorBufferView;
			return texelView.Handle != 0;
		}

		/// <summary>
		/// Resolves a descriptor buffer binding for a built-in engine uniform
		/// (e.g. ModelMatrix, ViewMatrix). Creates the per-frame UBO on demand.
		/// </summary>
			private bool TryResolveEngineUniformBuffer(DescriptorBindingInfo binding, int frameIndex, int drawUniformSlot, out DescriptorBufferInfo bufferInfo)
			{
				bufferInfo = default;
				string name = binding.Name ?? string.Empty;
				if (string.IsNullOrWhiteSpace(name))
					return false;

				uint size = GetEngineUniformSize(name);
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
					Offset = 0,
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
					Offset = 0,
					Range = size,
				};

				return true;
			}

			#endregion // Descriptor Set Management
	}
}

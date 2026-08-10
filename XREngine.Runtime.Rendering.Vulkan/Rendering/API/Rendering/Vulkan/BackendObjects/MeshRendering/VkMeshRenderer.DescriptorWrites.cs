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
	private bool TryResolveBuffers(
		DescriptorBindingInfo binding,
		int frameIndex,
		int drawUniformSlot,
		uint descriptorCount,
		List<DescriptorBufferInfo> bufferInfos,
		out int bufferStart,
		ComputeDispatchSnapshot? bindingSnapshot = null)
	{
		bufferStart = bufferInfos.Count;
		if (!TryResolveBuffer(
			binding,
			frameIndex,
			drawUniformSlot,
			out DescriptorBufferInfo bufferInfo,
			bindingSnapshot))
			return false;

		for (int i = 0; i < descriptorCount; i++)
			bufferInfos.Add(bufferInfo);

		return true;
	}

	/// <summary>Resolves one or more image descriptors for a binding from the material's textures.</summary>
	private bool TryResolveImages(
		DescriptorBindingInfo binding,
		XRMaterial material,
		uint descriptorCount,
		List<DescriptorImageInfo> imageInfos,
		out int imageStart,
		ComputeDispatchSnapshot? bindingSnapshot = null)
	{
		imageStart = imageInfos.Count;
		bool usesNamedArrayElements = descriptorCount > 1 && !string.IsNullOrWhiteSpace(binding.Name);
		for (int i = 0; i < descriptorCount; i++)
		{
			DescriptorBindingInfo elementBinding = usesNamedArrayElements
				? binding with { Name = ResolveDescriptorArrayElementName(binding.Name, i) }
				: binding;
			if (!TryResolveImage(
					elementBinding,
					material,
					elementBinding.DescriptorType,
					out DescriptorImageInfo info,
					i,
					bindingSnapshot))
				return false;

			imageInfos.Add(info);
		}

		return true;
	}

	private static string ResolveDescriptorArrayElementName(string bindingName, int arrayIndex)
		=> DescriptorArrayElementNames.GetOrAdd(
			(bindingName, arrayIndex),
			static key => $"{key.Name}[{key.Index}]");

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
	private static DescriptorPoolSize[] BuildDescriptorPoolSizes(
		IReadOnlyList<DescriptorBindingInfo> bindings,
		int frameCount,
		uint excludedSetMask = 0)
	{
		Dictionary<DescriptorType, uint> counts = [];
		foreach (DescriptorBindingInfo binding in bindings)
		{
			if (binding.Set < 32 && (excludedSetMask & (1u << (int)binding.Set)) != 0)
				continue;
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

	private static uint ComputeActiveDescriptorSetMask(IReadOnlyList<DescriptorBindingInfo> bindings, int setCount)
	{
		uint mask = 0;
		for (int i = 0; i < bindings.Count; i++)
		{
			uint set = bindings[i].Set;
			if (set < (uint)Math.Min(setCount, 32))
				mask |= 1u << (int)set;
		}

		if (VulkanBindlessMaterialDescriptors.IsGlobalTextureArrayOnlySet(bindings) &&
			VulkanBindlessMaterialDescriptors.TextureArraySet < (uint)Math.Min(setCount, 32))
		{
			mask &= ~(1u << (int)VulkanBindlessMaterialDescriptors.TextureArraySet);
		}
		return mask;
	}

	/// <summary>
	/// Returns whether one descriptor allocation can serve every logical draw
	/// slot. Dynamic UBO offsets carry the owner-specific byte location, while
	/// image and texel identities remain protected by the allocation's resource
	/// fingerprint. Fixed or storage buffers retain exact draw-slot ownership.
	/// </summary>
	internal static bool AreDescriptorBindingsDrawSlotInvariant(
		IReadOnlyList<DescriptorBindingInfo> bindings,
		bool usesSharedMaterialTier,
		bool descriptorHeapDrawBindingActive)
	{
		if (descriptorHeapDrawBindingActive)
			return false;

		for (int bindingIndex = 0;
			 bindingIndex < bindings.Count;
			 bindingIndex++)
		{
			DescriptorBindingInfo binding = bindings[bindingIndex];
			if (usesSharedMaterialTier &&
				binding.Set == VulkanMeshRenderingConventions.DescriptorSetMaterial)
			{
				continue;
			}

			switch (binding.DescriptorType)
			{
				case DescriptorType.UniformBufferDynamic:
					if (VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(
							binding) != 1)
					{
						return false;
					}
					break;

				case DescriptorType.CombinedImageSampler:
				case DescriptorType.Sampler:
				case DescriptorType.SampledImage:
				case DescriptorType.StorageImage:
				case DescriptorType.InputAttachment:
				case DescriptorType.UniformTexelBuffer:
				case DescriptorType.StorageTexelBuffer:
					break;

				default:
					return false;
			}
		}

		return true;
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
	private bool WriteDescriptorSets(
		DescriptorSet[] frameSets,
		IReadOnlyList<DescriptorBindingInfo> bindings,
		XRMaterial material,
		int frameIndex,
		int drawUniformSlot,
		DescriptorAllocation? allocation,
		int descriptorSlotIndex,
		ComputeDispatchSnapshot? bindingSnapshot,
		bool recordDescriptorTableGeneration)
	{
		DescriptorWriteScratch scratch = _descriptorWriteScratch;
		scratch.Clear();
		List<WriteDescriptorSet> writes = scratch.Writes;
		List<DescriptorBufferInfo> bufferInfos = scratch.BufferInfos;
		List<DescriptorImageInfo> imageInfos = scratch.ImageInfos;
		List<BufferView> texelBufferViews = scratch.TexelBufferViews;
		List<(int writeIndex, int bufferIndex, DescriptorBindingInfo binding, uint descriptorCount)> bufferMap = scratch.BufferMap;
		List<(int writeIndex, int imageIndex, DescriptorBindingInfo binding, uint descriptorCount)> imageMap = scratch.ImageMap;
		List<(int writeIndex, int texelIndex, DescriptorBindingInfo binding, uint descriptorCount)> texelMap = scratch.TexelMap;
		List<(DescriptorWriteKey key, ulong signature)> signatures = scratch.Signatures;

		for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
		{
			DescriptorBindingInfo binding = bindings[bindingIndex];
			if (allocation is not null &&
				(binding.Set >= 32 || (allocation.ActiveSetMask & (1u << (int)binding.Set)) == 0))
				continue;
			if (binding.Set >= frameSets.Length)
			{
				WarnOnce($"Descriptor set {binding.Set} is not available for pipeline layout.");
				return false;
			}

			uint descriptorCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);

			switch (binding.DescriptorType)
			{
				case DescriptorType.UniformBuffer:
				case DescriptorType.UniformBufferDynamic:
				case DescriptorType.StorageBuffer:
					if (!TryResolveBuffers(
							binding,
							frameIndex,
							drawUniformSlot,
							descriptorCount,
							bufferInfos,
							out int bufferStart,
							bindingSnapshot))
					{
						WarnOnce($"[WriteDesc] FAILED to resolve buffer binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}' program '{_program?.Data?.Name ?? "?"}'");
						RecordDescriptorFailure(binding, "buffer resolution failed");
						return false;
					}

					DescriptorWriteKey bufferKey = CreateDescriptorWriteKey(
						frameSets,
						descriptorSlotIndex,
						binding,
						descriptorCount);
					ulong bufferSignature = ComputeDescriptorBufferInfoSignature(
						binding.DescriptorType,
						bufferInfos,
						bufferStart,
						descriptorCount);
					if (DescriptorWriteMatches(allocation, bufferKey, bufferSignature))
						continue;
					TraceDescriptorWriteChange(
						allocation,
						bufferKey,
						bufferSignature,
						binding,
						material,
						bufferInfos[bufferStart]);

					bufferMap.Add((writes.Count, bufferStart, binding, descriptorCount));
					signatures.Add((bufferKey, bufferSignature));
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
					if (!TryResolveImages(
							binding,
							material,
							descriptorCount,
							imageInfos,
							out int imageStart,
							bindingSnapshot))
					{
						WarnOnce($"[WriteDesc] FAILED to resolve image binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}' program '{_program?.Data?.Name ?? "?"}'");
						RecordDescriptorFailure(binding, "image resolution failed");
						return false;
					}

					DescriptorWriteKey imageKey = CreateDescriptorWriteKey(
						frameSets,
						descriptorSlotIndex,
						binding,
						descriptorCount);
					ulong imageSignature = ComputeDescriptorImageInfoSignature(
						binding.DescriptorType,
						CollectionsMarshal.AsSpan(imageInfos).Slice(imageStart, checked((int)descriptorCount)));
					if (DescriptorWriteMatches(allocation, imageKey, imageSignature))
						continue;
					TraceDescriptorWriteChange(allocation, imageKey, imageSignature, binding, material);

					imageMap.Add((writes.Count, imageStart, binding, descriptorCount));
					signatures.Add((imageKey, imageSignature));
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

					DescriptorWriteKey texelKey = CreateDescriptorWriteKey(
						frameSets,
						descriptorSlotIndex,
						binding,
						descriptorCount);
					ulong texelSignature = ComputeDescriptorTexelBufferSignature(
						binding.DescriptorType,
						texelBufferViews,
						texelStart,
						descriptorCount);
					if (DescriptorWriteMatches(allocation, texelKey, texelSignature))
						continue;
					TraceDescriptorWriteChange(allocation, texelKey, texelSignature, binding, material);

					texelMap.Add((writes.Count, texelStart, binding, descriptorCount));
					signatures.Add((texelKey, texelSignature));
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

		Span<DescriptorBufferInfo> bufferSpan =
			CollectionsMarshal.AsSpan(bufferInfos);
		Span<DescriptorImageInfo> imageSpan =
			CollectionsMarshal.AsSpan(imageInfos);
		Span<BufferView> texelSpan =
			CollectionsMarshal.AsSpan(texelBufferViews);
		Span<WriteDescriptorSet> writeSpan =
			CollectionsMarshal.AsSpan(writes);

		fixed (DescriptorBufferInfo* bufferPtr = bufferSpan)
		fixed (DescriptorImageInfo* imagePtr = imageSpan)
		fixed (BufferView* texelPtr = texelSpan)
		fixed (WriteDescriptorSet* writePtr = writeSpan)
		{
			foreach (var (writeIndex, bufferIndex, _, _) in bufferMap)
				writePtr[writeIndex].PBufferInfo = bufferPtr + bufferIndex;

			foreach (var (writeIndex, imageIndex, _, _) in imageMap)
				writePtr[writeIndex].PImageInfo = imagePtr + imageIndex;

			foreach (var (writeIndex, texelIndex, _, _) in texelMap)
				writePtr[writeIndex].PTexelBufferView = texelPtr + texelIndex;

			if (writeSpan.Length > 0)
			{
				if (!ValidateDescriptorWrites(writePtr, writeSpan.Length))
					return false;

				if (BackendContext.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
				{
					DescriptorHeapPushDataPayload? payload = allocation?.DescriptorHeapPushData is { Length: > 0 } heapPayloads &&
						(uint)descriptorSlotIndex < (uint)heapPayloads.Length
							? heapPayloads[descriptorSlotIndex]
							: null;
					if (payload is null)
					{
						WarnOnce($"Skipping descriptor heap update for mesh '{Mesh?.Name ?? "?"}' because descriptor slot {descriptorSlotIndex} has no heap push payload.");
						return false;
					}

					foreach (var (_, bufferIndex, binding, descriptorCount) in bufferMap)
					{
						if (_program is null)
						{
							WarnOnce($"[WriteDescHeap] FAILED buffer binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}': program missing");
							RecordDescriptorFailure(binding, "descriptor heap buffer write failed: program missing");
							return false;
						}

						if (!BackendContext.DescriptorLifetime.TryWriteDescriptorHeapBinding(_program, binding, payload, bufferPtr + bufferIndex, null, null, descriptorCount, out string heapReason))
						{
							WarnOnce($"[WriteDescHeap] FAILED buffer binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}': {heapReason}");
							RecordDescriptorFailure(binding, $"descriptor heap buffer write failed: {heapReason}");
							return false;
						}
					}

					foreach (var (_, imageIndex, binding, descriptorCount) in imageMap)
					{
						if (_program is null)
						{
							WarnOnce($"[WriteDescHeap] FAILED image binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}': program missing");
							RecordDescriptorFailure(binding, "descriptor heap image write failed: program missing");
							return false;
						}

						if (!BackendContext.DescriptorLifetime.TryWriteDescriptorHeapBinding(_program, binding, payload, null, imagePtr + imageIndex, null, descriptorCount, out string heapReason))
						{
							WarnOnce($"[WriteDescHeap] FAILED image binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}': {heapReason}");
							RecordDescriptorFailure(binding, $"descriptor heap image write failed: {heapReason}");
							return false;
						}
					}

					foreach (var (_, texelIndex, binding, descriptorCount) in texelMap)
					{
						if (_program is null)
						{
							WarnOnce($"[WriteDescHeap] FAILED texel binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}': program missing");
							RecordDescriptorFailure(binding, "descriptor heap texel write failed: program missing");
							return false;
						}

						if (!BackendContext.DescriptorLifetime.TryWriteDescriptorHeapBinding(_program, binding, payload, null, null, texelPtr + texelIndex, descriptorCount, out string heapReason))
						{
							WarnOnce($"[WriteDescHeap] FAILED texel binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}': {heapReason}");
							RecordDescriptorFailure(binding, $"descriptor heap texel write failed: {heapReason}");
							return false;
						}
					}
				}

				if (!TryUpdateDescriptorSetsWithTemplates(
						frameSets,
						writeSpan,
						scratch.TemplateWrites) &&
					!BackendContext.DescriptorLifetime.TryUpdateDescriptorSets((uint)writeSpan.Length, writePtr, out string updateFailureReason))
				{
					Debug.VulkanWarningEvery(
						$"Vulkan.MeshRenderer.DescriptorGenerationRace.{GetHashCode()}",
						TimeSpan.FromSeconds(1),
						"[Vulkan] Deferred mesh descriptor update because a render-resource generation retired concurrently: {0}",
						updateFailureReason);
					return false;
				}
				if (recordDescriptorTableGeneration)
					BackendContext.DescriptorLifetime.RecordTableGeneration();
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorRecordsWritten(
					writeSpan.Length);

				if (allocation is not null)
					for (int signatureIndex = 0; signatureIndex < signatures.Count; signatureIndex++)
						allocation.DescriptorWriteSignatures[signatures[signatureIndex].key] =
							signatures[signatureIndex].signature;
			}
		}

		return true;
	}

	private static DescriptorWriteKey CreateDescriptorWriteKey(
		DescriptorSet[] frameSets,
		int descriptorSlotIndex,
		DescriptorBindingInfo binding,
		uint descriptorCount)
		=> new(
			descriptorSlotIndex,
			frameSets[binding.Set].Handle,
			binding.Set,
			binding.Binding,
			binding.DescriptorType,
			descriptorCount);

	private static bool DescriptorWriteMatches(
		DescriptorAllocation? allocation,
		in DescriptorWriteKey key,
		ulong signature)
		=> allocation is not null &&
		   allocation.DescriptorWriteSignatures.TryGetValue(key, out ulong previousSignature) &&
		   previousSignature == signature;

	private void TraceDescriptorWriteChange(
		DescriptorAllocation? allocation,
		in DescriptorWriteKey key,
		ulong signature,
		DescriptorBindingInfo binding,
		XRMaterial material,
		DescriptorBufferInfo? bufferInfo = null)
	{
		if (!DescriptorResourceFingerprintDiagnosticsEnabled ||
			allocation is null ||
			!allocation.DescriptorWriteSignatures.TryGetValue(key, out ulong previousSignature) ||
			previousSignature == signature)
		{
			return;
		}

		string diagnosticKey =
			$"{_program?.BindingId ?? 0}/{binding.Set}/{binding.Binding}/{binding.DescriptorType}/{previousSignature:X16}/{signature:X16}";
		if (DescriptorWriteChangeDiagnostics.TryAdd(diagnosticKey, 0))
		{
			var context = BackendContext.MeshServices.ActiveFrameOpContext;
			int currentPipelineIdentity = RuntimeEngine.Rendering.State.CurrentRenderingPipeline is { } currentPipeline
				? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(currentPipeline)
				: 0;
			Debug.WriteAuxiliaryLog(
				"vulkan-descriptor-write-changes.log",
				$"[VulkanDescriptor] changed native write program='{_program?.Data?.Name ?? "<null>"}' mesh='{Mesh?.Name ?? "<null>"}' material='{material.Name ?? "<unnamed>"}' slot={key.DescriptorSlotIndex} set={binding.Set} binding={binding.Binding} name='{binding.Name ?? "<null>"}' type={binding.DescriptorType} count={key.DescriptorCount} descriptorSet=0x{key.DescriptorSetHandle:X} signature=0x{previousSignature:X16}->0x{signature:X16} buffer=0x{bufferInfo?.Buffer.Handle ?? 0UL:X} offset={bufferInfo?.Offset ?? 0UL} range={bufferInfo?.Range ?? 0UL} currentPipeline={currentPipelineIdentity} contextKind={context?.ContextKind} contextPipeline={context?.PipelineIdentity ?? 0} contextViewport={context?.ViewportIdentity ?? 0} viewFamily={BackendContext.MeshServices.ResolveDescriptorViewFamilyIdentity()}.");
		}

		Debug.VulkanEvery(
			$"Vulkan.Descriptor.WriteChange.{_program?.BindingId ?? 0}.{key.DescriptorSetHandle}.{binding.Set}.{binding.Binding}",
			TimeSpan.FromSeconds(1),
			"[VulkanDescriptor] changed native write program='{0}' mesh='{1}' material='{2}' slot={3} set={4} binding={5} name='{6}' type={7} count={8} descriptorSet=0x{9:X} signature=0x{10:X16}->0x{11:X16}.",
			_program?.Data?.Name ?? "<null>",
			Mesh?.Name ?? "<null>",
			material.Name ?? "<unnamed>",
			key.DescriptorSlotIndex,
			binding.Set,
			binding.Binding,
			binding.Name ?? "<null>",
			binding.DescriptorType,
			key.DescriptorCount,
			key.DescriptorSetHandle,
			previousSignature,
			signature);
	}

	private ulong ComputeDescriptorBufferInfoSignature(
		DescriptorType descriptorType,
		List<DescriptorBufferInfo> bufferInfos,
		int start,
		uint count)
	{
		FrameOpSignatureHasher hash = new();
		hash.Add((int)descriptorType);
		hash.Add(count);
		for (int i = 0; i < count; i++)
		{
			DescriptorBufferInfo info = bufferInfos[start + i];
			hash.Add(info.Buffer.Handle);
			hash.Add(GetResourceGeneration(
				ObjectType.Buffer,
				info.Buffer.Handle));
			hash.Add(info.Offset);
			hash.Add(info.Range);
		}

		return hash.ToHash();
	}

	private ulong ComputeDescriptorTexelBufferSignature(
		DescriptorType descriptorType,
		List<BufferView> bufferViews,
		int start,
		uint count)
	{
		FrameOpSignatureHasher hash = new();
		hash.Add((int)descriptorType);
		hash.Add(count);
		for (int i = 0; i < count; i++)
		{
			BufferView view = bufferViews[start + i];
			hash.Add(view.Handle);
			hash.Add(GetResourceGeneration(
				ObjectType.BufferView,
				view.Handle));
			if (BackendContext.Descriptors.TryGetBufferViewCreateInfo(view, out BufferViewCreateInfo createInfo))
			{
				hash.Add(createInfo.Buffer.Handle);
				hash.Add(GetResourceGeneration(
					ObjectType.Buffer,
					createInfo.Buffer.Handle));
				hash.Add((int)createInfo.Format);
				hash.Add(createInfo.Offset);
				hash.Add(createInfo.Range);
			}
			else
			{
				// An untracked view must never compare equal to a prior tracked payload.
				hash.Add(0UL);
			}
		}

		return hash.ToHash();
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
				case DescriptorType.UniformBufferDynamic:
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

			if (requireImageView && !IsLiveDescriptorImageView(info.ImageView))
			{
				string backing = TryGetDescriptorImageBacking(info.ImageView, out Image backingImage)
					? $" backed by image 0x{backingImage.Handle:X}"
					: string.Empty;
				WarnOnce($"Skipping descriptor update for mesh '{Mesh?.Name ?? "?"}' because write[{writeIndex}].image[{i}] references a retired image view{backing}.");
				return false;
			}

			if (requireSampler && info.Sampler.Handle == 0)
			{
				WarnOnce($"Skipping descriptor update for mesh '{Mesh?.Name ?? "?"}' because write[{writeIndex}].image[{i}] has no sampler.");
				return false;
			}

			if (requireSampler && !BackendContext.Descriptors.IsLiveSampler(info.Sampler))
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
			if (buffers[i].Buffer.Handle == 0)
				return true;

		return false;
	}

	private static bool HasZeroBufferView(BufferView* views, uint count)
	{
		for (uint i = 0; i < count; i++)
			if (views[i].Handle == 0)
				return true;

		return false;
	}

	private bool TryUpdateDescriptorSetsWithTemplates(
		DescriptorSet[] frameSets,
		ReadOnlySpan<WriteDescriptorSet> writes,
		List<WriteDescriptorSet> setWrites)
	{
		if (RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template)
			return false;

		if (_program is null || _program.DescriptorSetLayouts.Count < frameSets.Length)
			return false;

		for (int setIndex = 0; setIndex < frameSets.Length; setIndex++)
		{
			setWrites.Clear();
			for (int i = 0; i < writes.Length; i++)
			{
				if (writes[i].DstSet.Handle == frameSets[setIndex].Handle)
					setWrites.Add(writes[i]);
			}

			if (setWrites.Count == 0)
				continue;

			if (!BackendContext.DescriptorLifetime.TryUpdateDescriptorSetWithTemplate(
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
}

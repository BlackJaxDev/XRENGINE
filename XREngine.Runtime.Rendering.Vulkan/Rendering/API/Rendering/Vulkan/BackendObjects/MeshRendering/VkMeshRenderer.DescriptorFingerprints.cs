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
	private string ComputeDescriptorResourceFingerprintDetails(XRMaterial material, int frameCount, IReadOnlyList<DescriptorBindingInfo> bindings)
	{
		StringBuilder builder = new(256);
		AppendComponent(builder, "frames", frameCount);
		AppendComponent(builder, "buffers", ComputeCachedBufferResourceFingerprint());
		AppendComponent(builder, "textures", ComputeMaterialTextureResourceFingerprint(material));
		AppendComponent(builder, "engineUbo", ComputeEngineUniformResourceFingerprint());
		AppendComponent(builder, "autoUbo", ComputeAutoUniformResourceFingerprint());
		AppendComponent(builder, "resourceAllocator", unchecked((ulong)Renderer.ResourceAllocatorIdentity));
		if (_program is not null)
		{
			AppendReferencedProgramSamplerResourceFingerprintDetails(builder, material, bindings);
			AppendComponent(builder, "programSamplers", ComputeReferencedProgramSamplerResourceFingerprint(material, bindings));
			AppendComponent(builder, "programBuffers", ComputeReferencedProgramBufferResourceFingerprint(bindings));
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
		=> ComputeCachedBufferResourceFingerprintCore();

    private ulong ComputeMaterialTextureResourceFingerprint(XRMaterial material)
	{
		HashCode hash = new();
		hash.Add(material.Textures.Count);
		for (int i = 0; i < material.Textures.Count; i++)
			AddTextureDescriptorResourceFingerprint(ref hash, material.Textures[i]);
		return unchecked((ulong)hash.ToHashCode());
	}

    private ulong ComputeEngineUniformResourceFingerprint()
		=> ComputeEngineUniformResourceFingerprintCore();

    private ulong ComputeAutoUniformResourceFingerprint()
		=> ComputeAutoUniformResourceFingerprintCore();

	private ulong ComputeDescriptorBindingIdentityFingerprint(
		XRMaterial material,
		IReadOnlyList<DescriptorBindingInfo> bindings,
		int descriptorOwnerSlot,
		bool usesSharedMaterialTier)
	{
		// Vulkan handles are deliberately excluded. Replacing the backing image,
		// view, sampler, or buffer for the same engine binding is descriptor content
		// publication, not a new descriptor-set identity. The per-slot resource
		// fingerprint below decides when the completed slot must be rewritten.
		FrameOpSignatureHasher hash = new();
		hash.Add(descriptorOwnerSlot);
		for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
		{
			DescriptorBindingInfo binding = bindings[bindingIndex];
			if (usesSharedMaterialTier && binding.Set == VulkanRenderer.DescriptorSetMaterial)
				continue;

			hash.Add(binding.Set);
			hash.Add(binding.Binding);
			hash.Add((int)binding.DescriptorType);
			hash.Add(binding.Name);
			hash.Add(binding.ExpectedImageViewType.HasValue);
			if (binding.ExpectedImageViewType is { } expectedViewType)
				hash.Add((int)expectedViewType);

			if (binding.DescriptorType is not (
				DescriptorType.CombinedImageSampler or
				DescriptorType.Sampler or
				DescriptorType.SampledImage or
				DescriptorType.StorageImage or
				DescriptorType.InputAttachment or
				DescriptorType.UniformTexelBuffer or
				DescriptorType.StorageTexelBuffer))
			{
				continue;
			}

			uint descriptorCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);
			bool bindless = VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding);
			for (int arrayIndex = 0; arrayIndex < descriptorCount; arrayIndex++)
			{
				MaterialTextureBindingResolution resolution = MaterialTextureBindingResolver.Resolve(
					material,
					binding.Name,
					(int)binding.Binding,
					arrayIndex,
					bindless,
					_program,
					static (program, samplerName) =>
						program is not null && program.TryGetSamplerTexture(samplerName, out XRTexture? namedTexture)
							? namedTexture
							: null);
				XRTexture? texture = resolution.Texture;
				hash.Add(texture is not null);
				if (texture is not null)
					hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(texture));
			}
		}

		return hash.ToHash();
	}

	private ulong ComputeDescriptorResourceFingerprint(
		XRMaterial material,
		int frameCount,
		IReadOnlyList<DescriptorBindingInfo> bindings,
		int drawUniformSlot,
		bool usesSharedMaterialTier,
		ComputeDispatchSnapshot? bindingSnapshot = null,
		bool includeFrameSourceDescriptors = true)
	{
		FrameOpSignatureHasher hash = new();
		hash.Add(frameCount);
		for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
		{
			DescriptorBindingInfo binding = bindings[bindingIndex];
			if (usesSharedMaterialTier && binding.Set == VulkanRenderer.DescriptorSetMaterial)
				continue;

			uint descriptorCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);
			hash.Add(binding.Set);
			hash.Add(binding.Binding);
			hash.Add((int)binding.DescriptorType);
			hash.Add(descriptorCount);
			switch (binding.DescriptorType)
			{
				case DescriptorType.UniformBuffer:
				case DescriptorType.UniformBufferDynamic:
				case DescriptorType.StorageBuffer:
					for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
					{
						bool resolved = TryResolveBuffer(
							binding,
							frameIndex,
							drawUniformSlot,
							out DescriptorBufferInfo info,
							bindingSnapshot);
						hash.Add(resolved);
						if (!resolved)
							continue;
						hash.Add(info.Buffer.Handle);
						hash.Add(info.Offset);
						hash.Add(info.Range);
					}
					break;

				case DescriptorType.CombinedImageSampler:
				case DescriptorType.Sampler:
				case DescriptorType.SampledImage:
				case DescriptorType.StorageImage:
				case DescriptorType.InputAttachment:
					if (!includeFrameSourceDescriptors &&
						IsFrameSourceSamplerBinding(
							material,
							binding,
							bindingSnapshot))
					{
						hash.Add(VulkanRenderer.FrameSourceMutableDescriptorSignature);
						break;
					}

					for (int arrayIndex = 0; arrayIndex < descriptorCount; arrayIndex++)
					{
						bool resolved = TryResolveImage(
							binding,
							material,
							binding.DescriptorType,
							out DescriptorImageInfo info,
							arrayIndex,
							bindingSnapshot);
						hash.Add(resolved);
						if (!resolved)
							continue;
						hash.Add(info.ImageView.Handle);
						hash.Add(info.Sampler.Handle);
						hash.Add((int)info.ImageLayout);
					}
					break;

				case DescriptorType.UniformTexelBuffer:
				case DescriptorType.StorageTexelBuffer:
					for (int arrayIndex = 0; arrayIndex < descriptorCount; arrayIndex++)
					{
						bool resolved = TryResolveTexelBuffer(binding, material, out BufferView view, arrayIndex);
						hash.Add(resolved);
						if (resolved)
							hash.Add(view.Handle);
					}
					break;
			}
		}

		return hash.ToHash();
	}

	private ulong ComputeReferencedProgramSamplerResourceFingerprint(XRMaterial material, IReadOnlyList<DescriptorBindingInfo> bindings)
	{
		HashCode hash = new();
		ulong xor = 0;
		ulong sum = 0;
		int count = 0;

		for (int i = 0; i < bindings.Count; i++)
		{
			DescriptorBindingInfo binding = bindings[i];
			if (!ShouldFingerprintProgramSamplerBinding(material, binding))
				continue;

			AddUnorderedFingerprintItem(ref xor, ref sum, ComputeReferencedProgramSamplerFingerprintItem(material, binding));
			count++;
		}

		hash.Add(count);
		hash.Add(xor);
		hash.Add(sum);
		return unchecked((ulong)hash.ToHashCode());
	}

	private void AppendReferencedProgramSamplerResourceFingerprintDetails(StringBuilder builder, XRMaterial material, IReadOnlyList<DescriptorBindingInfo> bindings)
	{
		const int maxDetailedSamplers = 10;
		int detailedCount = 0;
		for (int i = 0; i < bindings.Count; i++)
		{
			DescriptorBindingInfo binding = bindings[i];
			if (!ShouldFingerprintProgramSamplerBinding(material, binding))
				continue;

			AppendComponent(
				builder,
				$"programSampler[{binding.Name}@{binding.Set}.{binding.Binding}]",
				ComputeReferencedProgramSamplerFingerprintItem(material, binding));
			detailedCount++;
			if (detailedCount >= maxDetailedSamplers)
				break;
		}
	}

	private bool ShouldFingerprintProgramSamplerBinding(XRMaterial material, DescriptorBindingInfo binding)
		=> IsImageDescriptorBinding(binding.DescriptorType) &&
			!VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding) &&
			!MaterialResolvesDescriptorBinding(material, binding) &&
			!IsFrameSourceSamplerBinding(material, binding) &&
			!string.IsNullOrWhiteSpace(binding.Name);

	private bool IsFrameSourceSamplerBinding(
		XRMaterial material,
		DescriptorBindingInfo binding,
		ComputeDispatchSnapshot? snapshot = null)
	{
		if (VulkanRenderer.IsFrameSourceSamplerName(binding.Name))
			return true;

		if (string.IsNullOrWhiteSpace(binding.Name) ||
			MaterialResolvesDescriptorBinding(material, binding, snapshot) ||
			!BindingResolvesPipelineResourceTexture(binding))
		{
			return false;
		}

		XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
		return pipeline is not null &&
			_program is not null &&
			TryGetProgramSamplerTexture(snapshot, binding.Name, out XRTexture? programTexture) &&
			pipeline.TryGetTexture(binding.Name, out XRTexture? pipelineTexture) &&
			ReferenceEquals(programTexture, pipelineTexture);
	}

	private bool MaterialResolvesDescriptorBinding(
		XRMaterial material,
		DescriptorBindingInfo binding,
		ComputeDispatchSnapshot? snapshot = null)
	{
		if (VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding))
			return material.Textures.Count > 0;

		if (!string.IsNullOrWhiteSpace(binding.Name) &&
			_program is not null &&
			TryGetProgramSamplerTexture(snapshot, binding.Name, out _))
		{
			return false;
		}

		MaterialTextureBindingResolution resolution = MaterialTextureBindingResolver.Resolve(
			material,
			binding.Name,
			(int)binding.Binding,
			arrayIndex: 0,
			bindlessMaterialArray: false);

		return resolution.HasTexture;
	}

	private bool TryGetProgramSamplerTexture(
		ComputeDispatchSnapshot? snapshot,
		string samplerName,
		out XRTexture? texture)
	{
		texture = null;
		return snapshot is not null
			? snapshot.TryGetSamplerTexture(samplerName, out texture)
			: _program is not null && _program.TryGetSamplerTexture(samplerName, out texture);
	}

	private static bool MaterialOwnsNamedSamplerBinding(XRMaterial material, string? bindingName)
	{
		if (string.IsNullOrWhiteSpace(bindingName))
			return false;

		for (int i = 0; i < material.Textures.Count; i++)
		{
			XRTexture? texture = material.Textures[i];
			if (texture is null)
				continue;

			if (string.Equals(texture.ResolveSamplerName(i, null), bindingName, StringComparison.Ordinal) ||
				string.Equals(XRTexture.GetIndexedSamplerName(i), bindingName, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private ulong ComputeReferencedProgramSamplerFingerprintItem(XRMaterial material, DescriptorBindingInfo binding)
	{
		HashCode item = new();
		item.Add(binding.Set);
		item.Add(binding.Binding);
		item.Add((int)binding.DescriptorType);
		item.Add(binding.Name, StringComparer.Ordinal);
		if (IsFrameSourceSamplerBinding(material, binding))
		{
			XRTexture? frameSource = null;
			_program?.TryGetSamplerTexture(binding.Name ?? string.Empty, out frameSource);
			AddFrameSourceSamplerDescriptorResourceFingerprint(ref item, frameSource);
		}
		else if (_program is not null && _program.TryGetSamplerTexture(binding.Name!, out XRTexture? texture))
		{
			item.Add(true);
			AddTextureDescriptorResourceFingerprint(ref item, texture);
		}
		else
		{
			item.Add(false);
		}

		return unchecked((ulong)item.ToHashCode());
	}

	private void AddFrameSourceSamplerDescriptorResourceFingerprint(ref HashCode hash, XRTexture? texture)
	{
		hash.Add(VulkanRenderer.FrameSourceMutableDescriptorSignature);
		AddTextureDescriptorResourceFingerprint(ref hash, texture);
	}

	private bool TryRefreshFrameSourceDescriptorSetsForDraw(
		int frameIndex,
		int drawUniformSlot,
		XRMaterial material,
		ComputeDispatchSnapshot? snapshot,
		CommandBuffer descriptorCommandBuffer,
		out string reason)
	{
		reason = "no frame-source sampler descriptors";

		if (_program is null ||
			_program.DescriptorSetLayouts.Count == 0 ||
			_program.DescriptorBindings.Count == 0)
		{
			return true;
		}

		DescriptorAllocation? allocation = _activeDescriptorAllocation;
		if (allocation?.FrameSourceDescriptorClassificationInitialized == true)
		{
			if (!allocation.HasFrameSourceDescriptors)
				return true;
		}
		else
		{
			XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
			if (!SnapshotHasFrameSourceSampler(snapshot, pipeline) &&
				!DescriptorBindingsHaveFrameSourceSampler(material, _program.DescriptorBindings, snapshot))
			{
				return true;
			}
		}

		if (_descriptorSets is null || _descriptorSets.Length == 0)
		{
			reason = "descriptor set array is null or empty";
			return false;
		}

		int descriptorSlotIndex = ResolveDescriptorFrameIndex(frameIndex, _descriptorSets.Length);
		DescriptorSet[] frameSets = _descriptorSets[descriptorSlotIndex];
		if (frameSets.Length == 0)
		{
			reason = $"descriptor set array at imageIndex {frameIndex}, drawSlot {drawUniformSlot} is empty";
			return false;
		}

		return TryRefreshFrameSourceSamplerDescriptors(
			_activeDescriptorAllocation,
			descriptorSlotIndex,
			frameSets,
			_program.DescriptorBindings,
			material,
			snapshot,
			descriptorCommandBuffer,
			out reason);
	}

	private static bool SnapshotHasFrameSourceSampler(ComputeDispatchSnapshot? snapshot, XRRenderPipelineInstance? pipeline)
	{
		if (snapshot is null)
			return false;

		foreach (KeyValuePair<string, XRTexture> sampler in snapshot.SamplersByName)
			if (VulkanRenderer.IsMutableFrameSourceSamplerName(sampler.Key, pipeline))
				return true;

		return false;
	}

	private bool DescriptorBindingsHaveFrameSourceSampler(
		XRMaterial material,
		IReadOnlyList<DescriptorBindingInfo> bindings,
		ComputeDispatchSnapshot? snapshot)
	{
		for (int i = 0; i < bindings.Count; i++)
			if (IsFrameSourceSamplerBinding(material, bindings[i], snapshot))
				return true;

		return false;
	}

	private bool TryRefreshFrameSourceSamplerDescriptors(
		DescriptorAllocation? allocation,
		int descriptorSlotIndex,
		DescriptorSet[] frameSets,
		IReadOnlyList<DescriptorBindingInfo> bindings,
		XRMaterial material,
		ComputeDispatchSnapshot? snapshot,
		CommandBuffer descriptorCommandBuffer,
		out string reason)
	{
		bool refreshed = false;
		reason = "no frame-source sampler descriptors";
		ulong exactSamplerResourceSignature =
			snapshot?.ExactSamplerResourceSignature ?? 0UL;
		if (snapshot is { HasPublishedBindingLayoutSignatures: true })
		{
			snapshot.ResolvePublishedResourceSignatures(
				Renderer.ResolveMeshDescriptorViewFamilyIdentity(),
				out exactSamplerResourceSignature,
				out _);
		}

		bool slotSignatureMatches = allocation is not null &&
			snapshot is { HasPublishedBindingLayoutSignatures: true } &&
			(uint)descriptorSlotIndex < (uint)allocation.SlotFrameSourceSamplerSignatures.Length &&
			(uint)descriptorSlotIndex < (uint)allocation.SlotFrameSourceSamplerSignaturesValid.Length &&
			allocation.SlotFrameSourceSamplerSignaturesValid[descriptorSlotIndex] &&
			allocation.SlotFrameSourceSamplerSignatures[descriptorSlotIndex] ==
				exactSamplerResourceSignature;
		if (VulkanRenderer.DescriptorTraceEnabled &&
			SnapshotContainsNamedSampler(snapshot, "SourceTexture"))
		{
			Debug.VulkanEvery(
				$"Vulkan.Descriptor.SourceRefresh.{GetHashCode()}.{_program?.BindingId ?? 0}.{descriptorSlotIndex}",
				TimeSpan.FromSeconds(1),
				"[VulkanDescriptor] source-refresh program='{0}' slot={1} allocation={2} mutable={3} required={4} exact=0x{5:X16} recordedValid={6} recorded=0x{7:X16} slotMatch={8}.",
				_program?.Data?.Name ?? "<null>",
				descriptorSlotIndex,
				allocation is not null,
				snapshot?.HasMutableFrameSourceSamplerBindings == true,
				snapshot?.IsSamplerReadyRequired("SourceTexture") == true,
				exactSamplerResourceSignature,
				allocation is not null &&
				(uint)descriptorSlotIndex < (uint)allocation.SlotFrameSourceSamplerSignaturesValid.Length &&
				allocation.SlotFrameSourceSamplerSignaturesValid[descriptorSlotIndex],
				allocation is not null &&
				(uint)descriptorSlotIndex < (uint)allocation.SlotFrameSourceSamplerSignatures.Length
					? allocation.SlotFrameSourceSamplerSignatures[descriptorSlotIndex]
					: 0UL,
				slotSignatureMatches);
		}

		Span<DescriptorImageInfo> imageInfos = stackalloc DescriptorImageInfo[8];
		for (int i = 0; i < bindings.Count; i++)
		{
			DescriptorBindingInfo binding = bindings[i];
			if (!IsFrameSourceSamplerBinding(material, binding, snapshot))
				continue;

			if (!IsImageDescriptorBinding(binding.DescriptorType))
				continue;

			if (binding.Set >= frameSets.Length)
			{
				reason = $"descriptor set {binding.Set} is not available for frame-source sampler '{binding.Name}'";
				return false;
			}

			uint descriptorCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);
			if (descriptorCount == 0 || descriptorCount > 8)
			{
				reason = $"unsupported frame-source descriptor count {descriptorCount} for '{binding.Name}'";
				return false;
			}

			for (int arrayIndex = 0; arrayIndex < (int)descriptorCount; arrayIndex++)
			{
				if (!TryResolveImage(binding, material, binding.DescriptorType, out imageInfos[arrayIndex], arrayIndex, snapshot))
				{
					reason = $"failed to resolve frame-source sampler '{binding.Name}'";
					return false;
				}
			}

			ReadOnlySpan<DescriptorImageInfo> resolvedImageInfos = imageInfos[..(int)descriptorCount];
			// The published sampler signature identifies the logical frame source,
			// but it does not prove that the native image view was ready when the
			// descriptor was last written. A placeholder and the subsequently ready
			// view can share that logical signature. Always resolve the current native
			// payload and use the cached descriptor-write signature as the no-write
			// fast path.
			bool writeMatched = FrameSourceDescriptorWriteMatches(
				allocation,
				descriptorSlotIndex,
				binding,
				descriptorCount,
				resolvedImageInfos);
			if (writeMatched)
			{
				Renderer.ObserveFinalPresentationDescriptor(
					descriptorSlotIndex,
					descriptorCommandBuffer,
					frameSets[binding.Set],
					binding.Set,
					binding.Binding,
					binding.Name,
					resolvedImageInfos[0],
					exactSamplerResourceSignature,
					writeMatched: true,
					writeSucceeded: true);
				continue;
			}

			fixed (DescriptorImageInfo* imageInfoPtr = imageInfos)
			{
				WriteDescriptorSet write = new()
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = frameSets[binding.Set],
					DstBinding = binding.Binding,
					DescriptorCount = descriptorCount,
					DescriptorType = binding.DescriptorType,
					PImageInfo = imageInfoPtr
				};

				if (!ValidateDescriptorWrites(&write, 1))
				{
					reason = $"invalid frame-source sampler descriptor '{binding.Name}'";
					return false;
				}

				if (Renderer.IsDescriptorHeapDrawBindingActive)
				{
					DescriptorHeapPushDataPayload? payload = allocation?.DescriptorHeapPushData is { Length: > 0 } heapPayloads &&
						(uint)descriptorSlotIndex < (uint)heapPayloads.Length
							? heapPayloads[descriptorSlotIndex]
							: null;
					if (payload is null || _program is null)
					{
						reason = $"descriptor heap frame-source payload missing for '{binding.Name}'";
						return false;
					}

					if (!Renderer.TryWriteDescriptorHeapBinding(_program, binding, payload, null, imageInfoPtr, null, descriptorCount, out string heapReason))
					{
						reason = $"descriptor heap frame-source sampler '{binding.Name}' update failed: {heapReason}";
						return false;
					}
				}

				if (!Renderer.TryUpdateDescriptorSetsTracked(1, &write, out string updateFailureReason))
				{
					reason = $"frame-source sampler '{binding.Name}' update deferred: {updateFailureReason}";
					Debug.VulkanWarningEvery(
						$"Vulkan.MeshRenderer.FrameSourceDescriptorGenerationRace.{GetHashCode()}",
						TimeSpan.FromSeconds(1),
						"[Vulkan] Deferred frame-source sampler descriptor update because a render-resource generation retired concurrently: {0}",
						updateFailureReason);
					return false;
				}
				// The completed frame slot keeps the same descriptor-set handle. Its
				// contents are data publication, not a command-buffer binding change.
				// Advancing the global descriptor generation here would unnecessarily
				// invalidate every cached primary before the next slot can reuse it.
			}

			RecordFrameSourceDescriptorWriteSignature(allocation, descriptorSlotIndex, binding, descriptorCount, resolvedImageInfos);
			Renderer.ObserveFinalPresentationDescriptor(
				descriptorSlotIndex,
				descriptorCommandBuffer,
				frameSets[binding.Set],
				binding.Set,
				binding.Binding,
				binding.Name,
				resolvedImageInfos[0],
				exactSamplerResourceSignature,
				writeMatched: false,
				writeSucceeded: true);
			refreshed = true;
		}

		if (allocation is not null &&
			snapshot is { HasPublishedBindingLayoutSignatures: true } &&
			(uint)descriptorSlotIndex < (uint)allocation.SlotFrameSourceSamplerSignatures.Length &&
			(uint)descriptorSlotIndex < (uint)allocation.SlotFrameSourceSamplerSignaturesValid.Length)
		{
			allocation.SlotFrameSourceSamplerSignatures[descriptorSlotIndex] =
				exactSamplerResourceSignature;
			allocation.SlotFrameSourceSamplerSignaturesValid[descriptorSlotIndex] = true;
		}

		if (refreshed)
		{
			allocation?.PublishOwnerGeneration(descriptorSlotIndex);
			reason = "refreshed frame-source sampler descriptors";
		}
		return true;
	}

	private static bool SnapshotContainsNamedSampler(
		ComputeDispatchSnapshot? snapshot,
		string name)
		=> snapshot?.SamplersByName.ContainsKey(name) == true;

	private static bool FrameSourceDescriptorWriteMatches(
		DescriptorAllocation? allocation,
		int descriptorSlotIndex,
		DescriptorBindingInfo binding,
		uint descriptorCount,
		ReadOnlySpan<DescriptorImageInfo> imageInfos)
	{
		if (allocation is null)
			return false;

		DescriptorWriteKey key = new(
			descriptorSlotIndex,
			allocation.Sets[descriptorSlotIndex][binding.Set].Handle,
			binding.Set,
			binding.Binding,
			binding.DescriptorType,
			descriptorCount);

		return allocation.DescriptorWriteSignatures.TryGetValue(key, out ulong previousSignature) &&
			previousSignature == ComputeDescriptorImageInfoSignature(binding.DescriptorType, imageInfos);
	}

	private static void RecordFrameSourceDescriptorWriteSignature(
		DescriptorAllocation? allocation,
		int descriptorSlotIndex,
		DescriptorBindingInfo binding,
		uint descriptorCount,
		ReadOnlySpan<DescriptorImageInfo> imageInfos)
	{
		if (allocation is null)
			return;

		DescriptorWriteKey key = new(
			descriptorSlotIndex,
			allocation.Sets[descriptorSlotIndex][binding.Set].Handle,
			binding.Set,
			binding.Binding,
			binding.DescriptorType,
			descriptorCount);

		allocation.DescriptorWriteSignatures[key] =
			ComputeDescriptorImageInfoSignature(binding.DescriptorType, imageInfos);
	}

	private static ulong ComputeDescriptorImageInfoSignature(
		DescriptorType descriptorType,
		ReadOnlySpan<DescriptorImageInfo> imageInfos)
	{
		FrameOpSignatureHasher hash = new();
		hash.Add((int)descriptorType);
		hash.Add(imageInfos.Length);
		for (int i = 0; i < imageInfos.Length; i++)
		{
			DescriptorImageInfo info = imageInfos[i];
			hash.Add((int)info.ImageLayout);
			hash.Add(info.ImageView.Handle);
			hash.Add(info.Sampler.Handle);
		}

		return hash.ToHash();
	}

	private ulong ComputeReferencedProgramBufferResourceFingerprint(IReadOnlyList<DescriptorBindingInfo> bindings)
	{
		HashCode hash = new();
		ulong xor = 0;
		ulong sum = 0;
		int count = 0;

		for (int i = 0; i < bindings.Count; i++)
		{
			DescriptorBindingInfo binding = bindings[i];
			if (binding.DescriptorType != DescriptorType.StorageBuffer ||
				BindingResolvesBeforeProgramBuffer(binding))
			{
				continue;
			}

			HashCode item = new();
			item.Add(binding.Set);
			item.Add(binding.Binding);
			item.Add((int)binding.DescriptorType);
			if (_program is not null && _program.TryGetBoundBuffer(binding.Binding, out XRDataBuffer? buffer))
			{
				item.Add(true);
				AddProgramBoundBufferDescriptorResourceFingerprint(ref item, binding.Binding, buffer);
			}
			else
			{
				item.Add(false);
			}

			AddUnorderedFingerprintItem(ref xor, ref sum, unchecked((ulong)item.ToHashCode()));
			count++;
		}

		hash.Add(count);
		hash.Add(xor);
		hash.Add(sum);
		return unchecked((ulong)hash.ToHashCode());
	}

	private bool BindingResolvesBeforeProgramBuffer(DescriptorBindingInfo binding)
	{
		if (!string.IsNullOrWhiteSpace(binding.Name) &&
			TryResolveCachedBufferByName(binding.Name, out _))
		{
			return true;
		}

		return BindingResolvesPipelineResourceBuffer(binding);
	}

	private static bool IsImageDescriptorBinding(DescriptorType descriptorType)
		=> descriptorType is DescriptorType.CombinedImageSampler
			or DescriptorType.Sampler
			or DescriptorType.SampledImage
			or DescriptorType.StorageImage
			or DescriptorType.InputAttachment;

	private static bool BindingResolvesPipelineResourceBuffer(DescriptorBindingInfo binding)
	{
		if (string.IsNullOrWhiteSpace(binding.Name))
			return false;

		XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
		if (pipeline is null)
			return false;

		if (TryResolvePipelineResourceDataBuffer(pipeline, binding.Name, binding.DescriptorType, out _))
			return true;

		string trimmedName = TrimDescriptorBufferSuffix(binding.Name);
		return !string.Equals(trimmedName, binding.Name, StringComparison.Ordinal) &&
			TryResolvePipelineResourceDataBuffer(pipeline, trimmedName, binding.DescriptorType, out _);
	}

	private static bool BindingResolvesPipelineResourceTexture(DescriptorBindingInfo binding)
	{
		if (string.IsNullOrWhiteSpace(binding.Name))
			return false;

		XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
		return pipeline is not null &&
			pipeline.TryGetTexture(binding.Name, out XRTexture? texture) &&
			texture is not null;
	}

	private void AddProgramBoundBufferDescriptorResourceFingerprint(ref HashCode hash, uint binding, XRDataBuffer? buffer)
	{
		hash.Add(binding);
		hash.Add(buffer?.GetHashCode() ?? 0);
		if (buffer is null)
		{
			hash.Add(0UL);
			return;
		}

		hash.Add(buffer.AttributeName, StringComparer.Ordinal);
		hash.Add(buffer.Name, StringComparer.Ordinal);
		hash.Add(buffer.Length);
		hash.Add((int)buffer.Target);
		hash.Add(buffer.BindingIndexOverride ?? uint.MaxValue);

		if (Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: false) is VkDataBuffer vkBuffer)
		{
			hash.Add(vkBuffer.BufferHandle?.Handle ?? 0UL);
			hash.Add(vkBuffer.AllocatedByteSize);
		}
		else
		{
			hash.Add(0UL);
		}
	}

	private static bool DescriptorSetsHaveSetCount(DescriptorSet[][] descriptorSets, int setCount)
	{
		for (int i = 0; i < descriptorSets.Length; i++)
		{
			DescriptorSet[]? sets = descriptorSets[i];
			if (sets is not null && sets.Length != 0 && sets.Length != setCount)
				return false;
		}

		return true;
	}

	private ulong ComputeCachedBufferResourceFingerprintCore()
	{
		lock (_bufferStateSync)
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
		{
			item.Add(buffers[i].Buffer.Handle);
			item.Add(buffers[i].Size);
			item.Add(buffers[i].Offset);
		}

		return unchecked((ulong)item.ToHashCode());
	}

	private static ulong ComputeAutoUniformBufferArrayFingerprintItem(string name, AutoUniformBuffer[] buffers)
	{
		HashCode item = new();
		item.Add(name, StringComparer.Ordinal);
		item.Add(buffers.Length);
		for (int i = 0; i < buffers.Length; i++)
		{
			item.Add(buffers[i].Buffer.Handle);
			item.Add(buffers[i].Size);
			item.Add(buffers[i].Offset);
		}

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
		if (texture is null)
		{
			hash.Add(0UL);
			return;
		}

		if (!Renderer.TryGetAPIRenderObject(texture, out AbstractRenderAPIObject? apiObject))
		{
			hash.Add(false);
			hash.Add(0UL);
			return;
		}

		if (apiObject is IVkImageDescriptorSource imageSource)
		{
			if (imageSource.TryGetDescriptorSnapshot(
				requestedViewType: null,
				requestedAspectMask: null,
				reason: "DescriptorResourceFingerprint",
				allowSynchronousUpload: false,
				out VkImageDescriptorSnapshot snapshot))
			{
				hash.Add(snapshot.View.Handle);
				hash.Add(snapshot.Sampler.Handle);
			}
			else
			{
				hash.Add(false);
				hash.Add(imageSource.DescriptorGeneration);
			}
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
}

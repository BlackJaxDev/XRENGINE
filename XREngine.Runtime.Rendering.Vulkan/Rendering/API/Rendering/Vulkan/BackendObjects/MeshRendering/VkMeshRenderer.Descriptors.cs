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
using System.Threading;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	private static readonly ConcurrentDictionary<string, string> MeshMaterialDescriptorReasons =
		new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<string, string> DescriptorBufferTrimmedNames =
		new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<string, byte> DescriptorWriteChangeDiagnostics =
		new(StringComparer.Ordinal);

	private static readonly bool DescriptorResourceFingerprintDiagnosticsEnabled =
		string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanFrameDataReuseDiag), "1", StringComparison.Ordinal) ||
		string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanDescriptorFingerprintDiag), "1", StringComparison.Ordinal);

	private static readonly bool MaterialBindingDiagnosticsEnabled =
		string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanMaterialBindingDiag), "1", StringComparison.Ordinal);

	private ulong _descriptorAllocationUsageSerial;

	private static readonly ThreadLocal<DescriptorWriteScratch?> DescriptorWriteScratchWorkspace = new();

	internal static void ReleaseCurrentThreadDescriptorScratch()
		=> DescriptorWriteScratchWorkspace.Value = null;

	/// <summary>
	/// Ensures the descriptor sets for one frame/draw slot are allocated and current.
	/// Pool identity is structural while local output/pass sets are immutable resource
	/// variants. Stable material sets remain shared through the material tier.
	/// </summary>
	private bool EnsureDescriptorSets(
		XRMaterial material,
		int drawUniformSlot,
		int frameIndex = 0,
		ComputeDispatchSnapshot? bindingSnapshot = null)
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

		int frameCount = BackendContext.Descriptors.FrameSlotCount;
		if (frameCount <= 0)
			return false;

		EnsureUniformDrawSlotCapacity(drawUniformSlot + 1);
		if (!EnsureDescriptorUniformBuffers(bindings))
			return false;

		int descriptorFrameSlotCount = frameCount;
		int setCount = layouts.Count;
		uint activeSetMask = ComputeActiveDescriptorSetMask(bindings, setCount);
		VkMaterial? sharedMaterial = null;
		bool usesSharedMaterialTier = false;
		if (!Renderer.IsDescriptorHeapDrawBindingActive &&
			(activeSetMask & (1u << (int)VulkanRenderer.DescriptorSetMaterial)) != 0 &&
			_program.DescriptorSetUsesUpdateAfterBind(VulkanRenderer.DescriptorSetMaterial) &&
			Renderer.GetOrCreateAPIRenderObject(material, generateNow: true) is VkMaterial materialObject &&
			materialObject.TryGetMaterialDescriptorSet(_program, frameIndex, out _, out _))
		{
			sharedMaterial = materialObject;
			usesSharedMaterialTier = true;
			activeSetMask &= ~(1u << (int)VulkanRenderer.DescriptorSetMaterial);
		}
		int activeSetCount = System.Numerics.BitOperations.PopCount(activeSetMask);
		ulong layoutFingerprint = _program.DescriptorLayoutFingerprint;
		ulong schemaFingerprint = _program.DescriptorSchemaFingerprint;
		ulong resourceFingerprint = ComputeDescriptorResourceFingerprint(
			material,
			frameCount,
			bindings,
			drawUniformSlot,
			usesSharedMaterialTier,
			bindingSnapshot);
		ulong bindingIdentityFingerprint = ComputeDescriptorBindingIdentityFingerprint(
			material,
			bindings,
			drawUniformSlot,
			usesSharedMaterialTier);
		int materialIdentity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(material);
		int viewFamilyIdentity = Renderer.ResolveMeshDescriptorViewFamilyIdentity();
		ulong immutableResourceFingerprint = ResolveDescriptorAllocationResourceVariantFingerprint(
			DescriptorSetsAreUpdateAfterBind(activeSetMask),
			bindingSnapshot is not null,
			resourceFingerprint);
		DescriptorAllocationKey allocationKey = new(
			layoutFingerprint,
			schemaFingerprint,
			_program.BindingId,
			descriptorFrameSlotCount,
			setCount,
			materialIdentity,
			material.BindingLayoutVersion,
			viewFamilyIdentity,
			drawUniformSlot,
			bindingIdentityFingerprint,
			immutableResourceFingerprint);

		if (_descriptorAllocations.TryGetValue(allocationKey, out DescriptorAllocation? cachedAllocation) &&
			DescriptorAllocationMatchesProgram(cachedAllocation) &&
			IsDescriptorAllocationValid(cachedAllocation, descriptorFrameSlotCount, setCount))
		{
			cachedAllocation.SharedMaterial = sharedMaterial;
			cachedAllocation.UsesSharedMaterialTier = usesSharedMaterialTier;
			RefreshDescriptorAllocationMetadata(cachedAllocation, _program, material, descriptorFrameSlotCount, setCount);
			if (!EnsureDescriptorSlotReady(cachedAllocation, material, bindings, frameIndex, drawUniformSlot, resourceFingerprint, bindingSnapshot))
				return false;
			ActivateDescriptorAllocation(cachedAllocation);
			_descriptorDirty = false;
			return true;
		}

		if (cachedAllocation is not null)
		{
			ReleaseDescriptorAllocationReference(allocationKey, cachedAllocation);
			_descriptorAllocations.Remove(allocationKey);
		}

		if (BackendContext.Descriptors.TryAcquireSharedMeshDescriptorAllocation(
				allocationKey,
				material,
				out DescriptorAllocation sharedAllocation))
		{
			if (DescriptorAllocationMatchesProgram(sharedAllocation) &&
				IsDescriptorAllocationValid(sharedAllocation, descriptorFrameSlotCount, setCount) &&
				EnsureDescriptorSlotReady(sharedAllocation, material, bindings, frameIndex, drawUniformSlot, resourceFingerprint, bindingSnapshot))
			{
				RefreshDescriptorAllocationMetadata(sharedAllocation, _program, material, descriptorFrameSlotCount, setCount);
				_descriptorAllocations.Add(allocationKey, sharedAllocation);
				ActivateDescriptorAllocation(sharedAllocation);
				_descriptorDirty = false;
				return true;
			}

			ReleaseDescriptorAllocationReference(allocationKey, sharedAllocation);
		}

		var poolSizes = BuildDescriptorPoolSizes(
			bindings,
			descriptorFrameSlotCount,
			usesSharedMaterialTier ? 1u << (int)VulkanRenderer.DescriptorSetMaterial : 0u);
		if (poolSizes.Length == 0 && activeSetCount != 0)
			return false;

		DescriptorPool descriptorPool = default;
		MeshDescriptorPoolSlabLease? poolSlabLease = null;

		if (activeSetCount > 0)
		{
			if (!Renderer.TryAcquireMeshDescriptorPoolSlab(
					poolSizes,
					activeSetCount * descriptorFrameSlotCount,
					_program.DescriptorSetsRequireUpdateAfterBind,
					out poolSlabLease) ||
				poolSlabLease is null)
			{
				Debug.VulkanWarning("Failed to acquire a Vulkan mesh descriptor pool slab.");
				return false;
			}
			descriptorPool = poolSlabLease.Pool;
		}

		DescriptorSetLayout[] layoutArray = [.. layouts];
		uint[] variableDescriptorCounts = _program.DescriptorSetsRequireVariableDescriptorCount
			? VulkanBindlessMaterialDescriptors.BuildVariableDescriptorCounts(bindings, layoutArray.Length)
			: [];
		DescriptorSet[][] descriptorSets = new DescriptorSet[descriptorFrameSlotCount][];
		Array.Fill(descriptorSets, Array.Empty<DescriptorSet>());
		DescriptorHeapPushDataPayload[] descriptorHeapPushData = new DescriptorHeapPushDataPayload[descriptorFrameSlotCount];
		DescriptorAllocation allocation = new()
		{
			Program = _program,
			Material = material,
			MaterialBindingLayoutVersion = material.BindingLayoutVersion,
			DescriptorFrameSlotCount = descriptorFrameSlotCount,
			SetCount = setCount,
			ActiveSetMask = activeSetMask,
			SharedMaterial = sharedMaterial,
			UsesSharedMaterialTier = usesSharedMaterialTier,
			AllocatedLocalSetCount = activeSetCount * descriptorFrameSlotCount,
			ReservedLocalSetCount = activeSetCount * descriptorFrameSlotCount,
			Pool = descriptorPool,
			PoolSlabLease = poolSlabLease,
			Sets = descriptorSets,
			DescriptorHeapPushData = descriptorHeapPushData,
			Layouts = layoutArray,
			VariableDescriptorCounts = variableDescriptorCounts,
			LayoutFingerprint = layoutFingerprint,
			SchemaFingerprint = schemaFingerprint,
			ProgramBindingId = _program.BindingId,
			ViewFamilyIdentity = viewFamilyIdentity,
			DrawUniformSlot = drawUniformSlot,
			BindingIdentityFingerprint = bindingIdentityFingerprint,
			ResourceFingerprint = resourceFingerprint,
			SlotResourceFingerprints = new ulong[descriptorFrameSlotCount]
		};

		for (int frameSlot = 0; frameSlot < descriptorFrameSlotCount; frameSlot++)
		{
			if (EnsureDescriptorSlotReady(allocation, material, bindings, frameSlot, drawUniformSlot, resourceFingerprint, bindingSnapshot))
				continue;
			Renderer.ReleaseMeshDescriptorPoolSlab(poolSlabLease, descriptorSets, activeSetMask);
			return false;
		}

		allocation.ResourceFingerprintDetails = DescriptorResourceFingerprintDiagnosticsEnabled
			? ComputeDescriptorResourceFingerprintDetails(material, frameCount, bindings)
			: string.Empty;
		DescriptorAllocation publishedAllocation = BackendContext.Descriptors.PublishSharedMeshDescriptorAllocation(
			allocationKey,
			allocation,
			out bool published);
		if (published)
		{
			RegisterDescriptorOwnershipTelemetry(allocation);
		}
		else
		{
			Renderer.ReleaseMeshDescriptorPoolSlab(
				allocation.PoolSlabLease,
				allocation.Sets,
				allocation.ActiveSetMask);
			allocation.PoolSlabLease = null;
			allocation = publishedAllocation;
		}

		_descriptorAllocations[allocationKey] = allocation;
		ActivateDescriptorAllocation(allocation);
		_descriptorDirty = false;
		return true;
	}

	private bool EnsureDescriptorSlotReady(
		DescriptorAllocation allocation,
		XRMaterial material,
		IReadOnlyList<DescriptorBindingInfo> bindings,
		int frameIndex,
		int drawUniformSlot,
		ulong resourceFingerprint,
		ComputeDispatchSnapshot? bindingSnapshot,
		bool recordDescriptorTableGeneration = true)
	{
		int descriptorSlotIndex = ResolveDescriptorFrameIndex(frameIndex, allocation.Sets.Length);
		DescriptorSet[] frameSets = allocation.Sets[descriptorSlotIndex];
		if (frameSets.Length == 0)
		{
			frameSets = new DescriptorSet[allocation.SetCount];
			for (int setIndex = 0; setIndex < allocation.SetCount; setIndex++)
			{
				if ((allocation.ActiveSetMask & (1u << setIndex)) == 0)
					continue;

				DescriptorSetLayout layout = allocation.Layouts[setIndex];
				DescriptorSet descriptorSet = default;
				uint variableDescriptorCount = allocation.VariableDescriptorCounts.Length > setIndex
					? allocation.VariableDescriptorCounts[setIndex]
					: 0u;
				DescriptorSetVariableDescriptorCountAllocateInfo variableDescriptorCountInfo = new()
				{
					SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
					DescriptorSetCount = 1,
					PDescriptorCounts = &variableDescriptorCount,
				};

				DescriptorSetAllocateInfo allocInfo = new()
				{
					SType = StructureType.DescriptorSetAllocateInfo,
					PNext = _program!.DescriptorSetsRequireVariableDescriptorCount ? &variableDescriptorCountInfo : null,
					DescriptorPool = allocation.Pool,
					DescriptorSetCount = 1,
					PSetLayouts = &layout,
				};

				if (Api!.AllocateDescriptorSets(Device, ref allocInfo, &descriptorSet) != Result.Success)
				{
					Debug.VulkanWarning("Failed to lazily allocate Vulkan descriptor sets for mesh renderer slot.");
					return false;
				}
				frameSets[setIndex] = descriptorSet;
			}

			int resolvedFrame = frameIndex % Math.Max(BackendContext.Descriptors.FrameSlotCount, 1);
			if (resolvedFrame < 0)
				resolvedFrame += Math.Max(BackendContext.Descriptors.FrameSlotCount, 1);
			string owner = $"MeshRenderer.DescriptorSet.Frame{resolvedFrame}";
			for (int setIndex = 0; setIndex < frameSets.Length; setIndex++)
			{
				if (frameSets[setIndex].Handle == 0)
					continue;
				Renderer.SetDebugDescriptorSetName(frameSets[setIndex], $"{owner}.Set{setIndex}");
				Renderer.RegisterVulkanDescriptorSet(
					allocation.Pool,
					frameSets[setIndex],
					_program!.DescriptorSetUsesUpdateAfterBind((uint)setIndex),
					owner,
					(uint)setIndex,
					bindings);
			}
			Renderer.RecordVulkanDescriptorTableGeneration("MeshRendererDescriptorSets.AllocatedLazySlot");
			allocation.Sets[descriptorSlotIndex] = frameSets;
			allocation.DescriptorHeapPushData[descriptorSlotIndex] = Renderer.CreateDescriptorHeapPushDataPayload(_program!.DescriptorHeapLayout);
		}

		if (allocation.UsesSharedMaterialTier)
		{
			if (allocation.SharedMaterial is null ||
				!allocation.SharedMaterial.TryGetMaterialDescriptorSet(_program!, frameIndex, out DescriptorSet materialSet, out _))
			{
				return false;
			}
			frameSets[VulkanRenderer.DescriptorSetMaterial] = materialSet;
		}

		if (DescriptorSlotResourceFingerprintMatches(allocation, descriptorSlotIndex, resourceFingerprint))
			return true;

		if (!WriteDescriptorSets(
			frameSets,
			bindings,
			material,
			frameIndex,
			drawUniformSlot,
			allocation,
			descriptorSlotIndex,
			bindingSnapshot,
			recordDescriptorTableGeneration))
			return false;

		SetDescriptorSlotResourceFingerprint(allocation, descriptorSlotIndex, resourceFingerprint);
		return true;
	}

	private void RegisterDescriptorOwnershipTelemetry(DescriptorAllocation allocation)
	{
		if (allocation.OwnershipTelemetryRegistered)
			return;
		allocation.OwnershipTelemetryRegistered = true;
		Renderer.RecordMeshDescriptorOwnershipDiagnostic(
			allocation.Program?.Data?.Name ?? "<unnamed>",
			allocation.Material?.Name ?? "<unnamed>",
			allocation.LayoutFingerprint,
			allocation.DescriptorFrameSlotCount,
			allocation.AllocatedLocalSetCount,
			allocation.UsesSharedMaterialTier);
		RuntimeEngine.Rendering.Stats.Vulkan.AdjustVulkanMeshDescriptorOwnership(
			allocationVariants: 1,
			pools: 0,
			allocatedSets: allocation.AllocatedLocalSetCount,
			reservedSets: allocation.ReservedLocalSetCount);
	}

	private static void ReleaseDescriptorOwnershipTelemetry(DescriptorAllocation allocation)
	{
		if (!allocation.OwnershipTelemetryRegistered)
			return;
		allocation.OwnershipTelemetryRegistered = false;
		RuntimeEngine.Rendering.Stats.Vulkan.AdjustVulkanMeshDescriptorOwnership(
			allocationVariants: -1,
			pools: 0,
			allocatedSets: -allocation.AllocatedLocalSetCount,
			reservedSets: -allocation.ReservedLocalSetCount);
	}

	private bool EnsureDescriptorUniformBuffers(IReadOnlyList<DescriptorBindingInfo> bindings)
	{
		if (_program is null)
			return false;

		for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
		{
			DescriptorBindingInfo binding = bindings[bindingIndex];
			if (binding.DescriptorType is not (DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic))
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
		=> CanReuseRecordedDescriptorSets(material, drawUniformSlot, resourcesCapturedByFrameSignature: false, out _);

	internal bool CanReuseRecordedDescriptorSets(XRMaterial material, int drawUniformSlot, out string reason)
		=> CanReuseRecordedDescriptorSets(material, drawUniformSlot, resourcesCapturedByFrameSignature: false, out reason);

	internal bool CanReuseRecordedDescriptorSets(
		XRMaterial material,
		int drawUniformSlot,
		bool resourcesCapturedByFrameSignature,
		out string reason)
		=> CanReuseRecordedDescriptorSets(
			material,
			drawUniformSlot,
			resourcesCapturedByFrameSignature,
			refreshFrameIndex: null,
			bindingSnapshot: null,
			out reason);

	internal bool CanReuseRecordedDescriptorSets(
		XRMaterial material,
		int drawUniformSlot,
		bool resourcesCapturedByFrameSignature,
		int refreshFrameIndex,
		out string reason)
		=> CanReuseRecordedDescriptorSets(
			material,
			drawUniformSlot,
			resourcesCapturedByFrameSignature,
			(int?)refreshFrameIndex,
			bindingSnapshot: null,
			out reason);

	internal bool CanReuseRecordedDescriptorSets(
		XRMaterial material,
		int drawUniformSlot,
		bool resourcesCapturedByFrameSignature,
		ComputeDispatchSnapshot? bindingSnapshot,
		out string reason)
		=> CanReuseRecordedDescriptorSets(
			material,
			drawUniformSlot,
			resourcesCapturedByFrameSignature,
			refreshFrameIndex: null,
			bindingSnapshot,
			out reason);

	internal bool CanReuseRecordedDescriptorSets(
		XRMaterial material,
		int drawUniformSlot,
		bool resourcesCapturedByFrameSignature,
		int refreshFrameIndex,
		ComputeDispatchSnapshot? bindingSnapshot,
		out string reason)
		=> CanReuseRecordedDescriptorSets(
			material,
			drawUniformSlot,
			resourcesCapturedByFrameSignature,
			(int?)refreshFrameIndex,
			bindingSnapshot,
			out reason);

	/// <summary>
	/// Activates the current descriptor allocation without re-resolving every binding
	/// when command-chain validation has already proven the captured descriptor
	/// resources are unchanged. The exact completed frame slot must still contain the
	/// allocation's currently published fingerprint; otherwise the full validation and
	/// refresh path remains authoritative.
	/// </summary>
	private bool TryActivateValidatedCapturedDescriptorSetsFast(
		XRMaterial material,
		int drawUniformSlot,
		int descriptorFrameSlotCount,
		int setCount,
		ulong layoutFingerprint,
		ulong schemaFingerprint,
		int viewFamilyIdentity,
		int refreshFrameIndex)
	{
		DescriptorAllocation? allocation = _activeDescriptorAllocation;
		if (allocation is null || _descriptorDirty)
			return false;

		if (!ReferenceEquals(allocation.Material, material) ||
			!DescriptorAllocationMatchesProgram(allocation) ||
			allocation.MaterialBindingLayoutVersion != material.BindingLayoutVersion ||
			allocation.DescriptorFrameSlotCount != descriptorFrameSlotCount ||
			allocation.SetCount != setCount ||
			allocation.LayoutFingerprint != layoutFingerprint ||
			allocation.SchemaFingerprint != schemaFingerprint ||
			allocation.ViewFamilyIdentity != viewFamilyIdentity ||
			allocation.DrawUniformSlot != drawUniformSlot ||
			!IsDescriptorAllocationValid(allocation, descriptorFrameSlotCount, setCount))
		{
			return false;
		}

		int descriptorSlotIndex = ResolveDescriptorFrameIndex(refreshFrameIndex, allocation.Sets.Length);
		if (!DescriptorSlotResourceFingerprintMatches(
				allocation,
				descriptorSlotIndex,
				allocation.ResourceFingerprint))
		{
			return false;
		}

		ActivateDescriptorAllocation(allocation);
		_descriptorDirty = false;
		return true;
	}

	/// <summary>
	/// Refreshes the exact shared-material descriptor slot consumed by a reusable
	/// command buffer. A same-handle refresh republishes the tracked resource
	/// snapshot; a replacement handle requires recording a new bind command.
	/// </summary>
	private bool TryRefreshSharedMaterialDescriptorSetForReusableFrame(
		XRMaterial material,
		int frameIndex,
		bool capturedResourcesValidated,
		out string reason)
	{
		reason = "reusable";
		DescriptorAllocation? allocation = _activeDescriptorAllocation;
		if (allocation?.UsesSharedMaterialTier != true)
			return true;
		VkRenderProgram? program = _program;

		if (allocation is null ||
			program is null ||
			!DescriptorAllocationMatchesProgram(allocation) ||
			allocation.SharedMaterial is null ||
			!ReferenceEquals(allocation.Material, material))
		{
			reason = "active shared-material descriptor allocation changed";
			return false;
		}

		if ((uint)frameIndex >= (uint)allocation.Sets.Length)
		{
			reason = $"shared-material descriptor frame {frameIndex} is outside {allocation.Sets.Length} slots";
			return false;
		}

		DescriptorSet[] frameSets = allocation.Sets[frameIndex];
		if ((uint)frameSets.Length <= VulkanRenderer.DescriptorSetMaterial)
		{
			reason = $"shared-material descriptor set {VulkanRenderer.DescriptorSetMaterial} is unavailable for frame {frameIndex}";
			return false;
		}

		DescriptorSet currentSet = default;
		bool materialSetReady =
			capturedResourcesValidated &&
			allocation.SharedMaterial.TryGetValidatedReusableMaterialDescriptorSet(
				program,
				frameIndex,
				out currentSet);
		if (!materialSetReady)
			materialSetReady = allocation.SharedMaterial.TryGetMaterialDescriptorSet(
				program,
				frameIndex,
				out currentSet,
				out _);
		if (!materialSetReady)
		{
			reason = $"shared-material descriptor refresh failed for frame {frameIndex}";
			return false;
		}

		DescriptorSet recordedSet = frameSets[VulkanRenderer.DescriptorSetMaterial];
		if (recordedSet.Handle == currentSet.Handle)
			return true;

		reason = $"shared-material descriptor-set handle changed 0x{recordedSet.Handle:X}->0x{currentSet.Handle:X}";
		return false;
	}

	internal int GetRecordedDescriptorSetCount(VkRenderProgram? preparedProgram)
	{
		VkRenderProgram? program = preparedProgram ?? _program;
		IReadOnlyList<DescriptorSetLayout>? layouts = program?.DescriptorSetLayouts;
		IReadOnlyList<DescriptorBindingInfo>? bindings = program?.DescriptorBindings;
		return layouts is { Count: > 0 } && bindings is { Count: > 0 }
			? layouts.Count
			: 0;
	}

	internal ulong ComputeRecordedDescriptorSchemaSignature(VkRenderProgram? preparedProgram)
	{
		VkRenderProgram? program = preparedProgram ?? _program;
		IReadOnlyList<DescriptorSetLayout>? layouts = program?.DescriptorSetLayouts;
		IReadOnlyList<DescriptorBindingInfo>? bindings = program?.DescriptorBindings;
		if (layouts is not { Count: > 0 } || bindings is not { Count: > 0 })
			return 0UL;

		return program!.DescriptorSchemaFingerprint;
	}

	internal ulong ComputeRecordedDescriptorResourceSignature(
		XRMaterial material,
		VkRenderProgram? preparedProgram,
		ComputeDispatchSnapshot? bindingSnapshot = null)
	{
		VkRenderProgram? program = preparedProgram ?? _program;
		IReadOnlyList<DescriptorSetLayout>? layouts = program?.DescriptorSetLayouts;
		IReadOnlyList<DescriptorBindingInfo>? bindings = program?.DescriptorBindings;
		if (layouts is not { Count: > 0 } || bindings is not { Count: > 0 })
			return 0UL;

		int frameCount = BackendContext.Descriptors.FrameSlotCount;
		if (frameCount <= 0)
			return 0UL;

		return ComputeDescriptorResourceFingerprint(
			material,
			frameCount,
			bindings,
			drawUniformSlot: 0,
			usesSharedMaterialTier: false,
			bindingSnapshot);
	}

	private bool CanReuseRecordedDescriptorSets(
		XRMaterial material,
		int drawUniformSlot,
		bool resourcesCapturedByFrameSignature,
		int? refreshFrameIndex,
		ComputeDispatchSnapshot? bindingSnapshot,
		out string reason)
	{
		reason = "reusable";
		if (_program is null)
			return true;

		var layouts = _program.DescriptorSetLayouts;
		var bindings = _program.DescriptorBindings;
		if (layouts is null || layouts.Count == 0 || bindings.Count == 0)
			return true;

		int frameCount = BackendContext.Descriptors.FrameSlotCount;
		if (frameCount <= 0)
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

		int descriptorFrameSlotCount = frameCount;
		int setCount = layouts.Count;
		int viewFamilyIdentity = Renderer.ResolveMeshDescriptorViewFamilyIdentity();
		ulong layoutFingerprint = _program.DescriptorLayoutFingerprint;
		ulong schemaFingerprint = _program.DescriptorSchemaFingerprint;
		bool usesSharedMaterialTier = _activeDescriptorAllocation is { } activeAllocation &&
			DescriptorAllocationMatchesProgram(activeAllocation) &&
			ReferenceEquals(activeAllocation.Material, material) &&
			activeAllocation.UsesSharedMaterialTier;
		// A captured binding snapshot can differ from the program's current bindings even
		// when the frame signature is otherwise unchanged. Resolve its exact immutable
		// resource variant instead of accepting whichever allocation is already active.
		if (resourcesCapturedByFrameSignature &&
			bindingSnapshot is null &&
			refreshFrameIndex is { } validatedFrameIndex &&
			TryActivateValidatedCapturedDescriptorSetsFast(
				material,
				drawUniformSlot,
				descriptorFrameSlotCount,
				setCount,
				layoutFingerprint,
				schemaFingerprint,
				viewFamilyIdentity,
				validatedFrameIndex))
		{
			return true;
		}

		ulong resourceFingerprint = ComputeDescriptorResourceFingerprint(
			material,
			frameCount,
			bindings,
			drawUniformSlot,
			usesSharedMaterialTier,
			bindingSnapshot);
		ulong bindingIdentityFingerprint = ComputeDescriptorBindingIdentityFingerprint(
			material,
			bindings,
			drawUniformSlot,
			usesSharedMaterialTier);
		if (resourcesCapturedByFrameSignature || refreshFrameIndex.HasValue)
		{
			if (TryActivateReusableDescriptorSetsForCapturedResources(
				material,
				drawUniformSlot,
				descriptorFrameSlotCount,
				setCount,
				layoutFingerprint,
				schemaFingerprint,
				viewFamilyIdentity,
				bindingIdentityFingerprint,
				resourceFingerprint,
				refreshFrameIndex,
				bindingSnapshot,
				out reason))
				return true;
		}

		if (TryActivateReusableDescriptorSetsFast(
			material,
			drawUniformSlot,
			descriptorFrameSlotCount,
			setCount,
			layoutFingerprint,
			schemaFingerprint,
			viewFamilyIdentity,
			bindingIdentityFingerprint,
			resourceFingerprint,
			out reason))
			return true;

		// The active draw can be a shadow/material override even though a compatible
		// shared-material allocation was prewarmed for this draw. Probe the shared-tier
		// binding identity and refresh only the completed frame slot when content changed.
		if (!usesSharedMaterialTier)
		{
			ulong sharedResourceFingerprint = ComputeDescriptorResourceFingerprint(
				material,
				frameCount,
				bindings,
				drawUniformSlot,
				usesSharedMaterialTier: true,
				bindingSnapshot);
			ulong sharedBindingIdentityFingerprint = ComputeDescriptorBindingIdentityFingerprint(
				material,
				bindings,
				drawUniformSlot,
				usesSharedMaterialTier: true);
			if (TryActivateReusableDescriptorSetsForCapturedResources(
				material,
				drawUniformSlot,
				descriptorFrameSlotCount,
				setCount,
				layoutFingerprint,
				schemaFingerprint,
				viewFamilyIdentity,
				sharedBindingIdentityFingerprint,
				sharedResourceFingerprint,
				refreshFrameIndex,
				bindingSnapshot,
				out reason))
			{
				return true;
			}
		}

		int materialIdentity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(material);
		uint activeSetMask = ComputeActiveDescriptorSetMask(bindings, setCount);
		if (usesSharedMaterialTier)
			activeSetMask &= ~(1u << (int)VulkanRenderer.DescriptorSetMaterial);
		ulong immutableResourceFingerprint = ResolveDescriptorAllocationResourceVariantFingerprint(
			DescriptorSetsAreUpdateAfterBind(activeSetMask),
			bindingSnapshot is not null,
			resourceFingerprint);
		DescriptorAllocationKey allocationKey = new(
			layoutFingerprint,
			schemaFingerprint,
			_program.BindingId,
			descriptorFrameSlotCount,
			setCount,
			materialIdentity,
			material.BindingLayoutVersion,
			viewFamilyIdentity,
			drawUniformSlot,
			bindingIdentityFingerprint,
			immutableResourceFingerprint);

		_descriptorAllocations.TryGetValue(allocationKey, out DescriptorAllocation? allocation);
		if (allocation is not null && !DescriptorAllocationMatchesProgram(allocation))
		{
			ReleaseDescriptorAllocationReference(allocationKey, allocation);
			_descriptorAllocations.Remove(allocationKey);
			allocation = null;
		}

		if (allocation is null &&
			BackendContext.Descriptors.TryAcquireSharedMeshDescriptorAllocation(allocationKey, material, out DescriptorAllocation cachedSharedAllocation))
		{
			if (DescriptorAllocationMatchesProgram(cachedSharedAllocation) &&
				IsDescriptorAllocationValid(cachedSharedAllocation, descriptorFrameSlotCount, setCount))
			{
				allocation = cachedSharedAllocation;
				_descriptorAllocations.Add(allocationKey, cachedSharedAllocation);
			}
			else
			{
				BackendContext.Descriptors.ReleaseSharedMeshDescriptorAllocation(allocationKey, cachedSharedAllocation);
			}
		}

		if (allocation is null)
		{
			string currentDetails = DescriptorResourceFingerprintDiagnosticsEnabled
				? ComputeDescriptorResourceFingerprintDetails(material, frameCount, bindings)
				: string.Empty;
			reason = BuildDescriptorAllocationMissReason(schemaFingerprint, resourceFingerprint, descriptorFrameSlotCount, setCount, currentDetails);
			return false;
		}

		if (!IsDescriptorAllocationValid(allocation, descriptorFrameSlotCount, setCount))
		{
			reason = "descriptor allocation invalid";
			return false;
		}

		RefreshDescriptorAllocationMetadata(allocation, _program, material, descriptorFrameSlotCount, setCount);

		if (allocation.SchemaFingerprint != schemaFingerprint)
		{
			reason = $"schema fingerprint 0x{allocation.SchemaFingerprint:X16}->0x{schemaFingerprint:X16}";
			return false;
		}

		if (allocation.LayoutFingerprint != layoutFingerprint)
		{
			reason = $"layout fingerprint 0x{allocation.LayoutFingerprint:X16}->0x{layoutFingerprint:X16}";
			return false;
		}

		if (allocation.ResourceFingerprint != resourceFingerprint)
		{
			if (DescriptorResourceFingerprintDiagnosticsEnabled)
			{
				string currentDetails = ComputeDescriptorResourceFingerprintDetails(material, frameCount, bindings);
				string diff = BuildDescriptorFingerprintDiffReason(currentDetails, allocation.ResourceFingerprintDetails);
				reason = $"resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}; {diff}";
			}
			else
			{
				reason = $"resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}";
			}
			return false;
		}

		ActivateDescriptorAllocation(allocation);
		_descriptorDirty = false;
		return true;
	}

	private string BuildDescriptorAllocationMissReason(ulong schemaFingerprint, ulong resourceFingerprint, int descriptorFrameSlotCount, int setCount, string currentDetails)
	{
		int sameSchemaCount = 0;
		int sameResourceCount = 0;
		DescriptorAllocationKey firstKey = default;
		DescriptorAllocation? firstAllocation = null;
		DescriptorAllocation? firstSameSchemaAllocation = null;
		bool hasFirstKey = false;
		foreach (KeyValuePair<DescriptorAllocationKey, DescriptorAllocation> pair in _descriptorAllocations)
		{
			DescriptorAllocationKey key = pair.Key;
			if (!hasFirstKey)
			{
				firstKey = key;
				firstAllocation = pair.Value;
				hasFirstKey = true;
			}

			if (key.SchemaFingerprint == schemaFingerprint)
			{
				sameSchemaCount++;
				firstSameSchemaAllocation ??= pair.Value;
			}
			if (pair.Value.ResourceFingerprint == resourceFingerprint)
				sameResourceCount++;
		}

		string first = hasFirstKey
			? $" first=layout0x{firstKey.LayoutFingerprint:X8}/0x{firstKey.SchemaFingerprint:X8}/{firstKey.DescriptorFrameSlotCount}/{firstKey.SetCount}"
			: string.Empty;
		DescriptorAllocation? comparisonAllocation = firstSameSchemaAllocation ?? firstAllocation;
		string details = DescriptorResourceFingerprintDiagnosticsEnabled && currentDetails.Length != 0
			? $" {BuildDescriptorFingerprintDiffReason(currentDetails, comparisonAllocation?.ResourceFingerprintDetails ?? string.Empty)}"
			: string.Empty;
		DescriptorAllocation? active = _activeDescriptorAllocation;
		if (active is null)
			return $"pool-miss key=0x{schemaFingerprint:X8}/0x{resourceFingerprint:X8}/{descriptorFrameSlotCount}/{setCount} allocs={_descriptorAllocations.Count} sameS={sameSchemaCount} sameR={sameResourceCount}{first} active=none dirty={_descriptorDirty}{details}";

		return $"pool-miss key=0x{schemaFingerprint:X8}/0x{resourceFingerprint:X8}/{descriptorFrameSlotCount}/{setCount} allocs={_descriptorAllocations.Count} sameS={sameSchemaCount} sameR={sameResourceCount}{first} active=0x{active.SchemaFingerprint:X8}/0x{active.ResourceFingerprint:X8}/{active.DescriptorFrameSlotCount}/{active.SetCount} dirty={_descriptorDirty}{details}";
	}

	private static string BuildDescriptorFingerprintDiffReason(string currentDetails, string previousDetails)
	{
		string diff = BuildDescriptorFingerprintDiff(currentDetails, previousDetails);
		if (diff.Length != 0)
			return $"changed={diff}";

		if (currentDetails.Length == 0 && previousDetails.Length == 0)
			return "changed=unknown";

		return $"changed=none current=[{currentDetails}] previous=[{previousDetails}]";
	}

	private static string BuildDescriptorFingerprintDiff(string currentDetails, string previousDetails)
	{
		if (currentDetails.Length == 0 || previousDetails.Length == 0)
			return string.Empty;

		string[] currentTokens = currentDetails.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		string[] previousTokens = previousDetails.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		StringBuilder builder = new();
		const int maxChangedComponents = 6;
		int changedCount = 0;

		for (int i = 0; i < currentTokens.Length; i++)
		{
			string currentToken = currentTokens[i];
			int currentEqualsIndex = currentToken.IndexOf('=');
			if (currentEqualsIndex <= 0)
				continue;

			string name = currentToken[..currentEqualsIndex];
			string currentValue = currentToken[(currentEqualsIndex + 1)..];
			string previousValue = string.Empty;
			for (int j = 0; j < previousTokens.Length; j++)
			{
				string previousToken = previousTokens[j];
				int previousEqualsIndex = previousToken.IndexOf('=');
				if (previousEqualsIndex != currentEqualsIndex ||
					!previousToken.AsSpan(0, previousEqualsIndex).SequenceEqual(name.AsSpan()))
				{
					continue;
				}

				previousValue = previousToken[(previousEqualsIndex + 1)..];
				break;
			}

			if (string.Equals(previousValue, currentValue, StringComparison.Ordinal))
				continue;

			if (builder.Length != 0)
				builder.Append(',');
			builder.Append(name);
			builder.Append(':');
			builder.Append(previousValue.Length == 0 ? "<missing>" : previousValue);
			builder.Append("->");
			builder.Append(currentValue);
			changedCount++;
			if (changedCount >= maxChangedComponents)
				break;
		}

		if (changedCount == maxChangedComponents && currentTokens.Length > maxChangedComponents)
			builder.Append(",...");

		return builder.ToString();
	}

	private void ActivateDescriptorAllocation(DescriptorAllocation allocation)
	{
		allocation.LastUsedSerial = ++_descriptorAllocationUsageSerial;
		_activeDescriptorAllocation = allocation;
		_descriptorPool = allocation.Pool;
		_descriptorSets = allocation.Sets;
		_descriptorSchemaFingerprint = allocation.SchemaFingerprint;
		_descriptorResourceFingerprint = allocation.ResourceFingerprint;
		_descriptorResourceFingerprintDetails = allocation.ResourceFingerprintDetails;
	}

	private static void RefreshDescriptorAllocationMetadata(
		DescriptorAllocation allocation,
		VkRenderProgram program,
		XRMaterial material,
		int descriptorFrameSlotCount,
		int setCount)
	{
		// Program identity is part of the native descriptor-set identity. Replacing
		// it here aliases material sets owned by distinct VkMaterial program states.
		if (allocation.ProgramBindingId != program.BindingId ||
			!ReferenceEquals(allocation.Program, program))
		{
			throw new InvalidOperationException("Vulkan descriptor allocation program identity changed after publication.");
		}

		allocation.Material = material;
		allocation.MaterialBindingLayoutVersion = material.BindingLayoutVersion;
		allocation.DescriptorFrameSlotCount = descriptorFrameSlotCount;
		allocation.SetCount = setCount;
	}

	private bool TryFindReusableDescriptorAllocationForCapturedResources(
		XRMaterial material,
		int drawUniformSlot,
		int descriptorFrameSlotCount,
		int setCount,
		ulong layoutFingerprint,
		ulong schemaFingerprint,
		int viewFamilyIdentity,
		ulong bindingIdentityFingerprint,
		ulong resourceFingerprint,
		bool requireImmutableResourceVariant,
		bool allowCompletedDescriptorSlotRefresh,
		out DescriptorAllocation allocation,
		out string reason)
	{
		allocation = null!;
		reason = "reusable";

		DescriptorAllocation? active = _activeDescriptorAllocation;
		if (DescriptorAllocationMatchesCapturedRequest(
			active,
			material,
			drawUniformSlot,
			descriptorFrameSlotCount,
			setCount,
			layoutFingerprint,
			schemaFingerprint,
			viewFamilyIdentity,
			bindingIdentityFingerprint,
			resourceFingerprint,
			requireImmutableResourceVariant,
			allowCompletedDescriptorSlotRefresh))
		{
			allocation = active!;
			return true;
		}

		foreach (DescriptorAllocation candidate in _descriptorAllocations.Values)
		{
			if (candidate.LayoutFingerprint != layoutFingerprint)
				continue;
			if (!DescriptorAllocationMatchesProgram(candidate))
				continue;

			if (!ReferenceEquals(candidate.Material, material) ||
				candidate.MaterialBindingLayoutVersion != material.BindingLayoutVersion)
			{
				continue;
			}

			if (candidate.DescriptorFrameSlotCount != descriptorFrameSlotCount ||
				candidate.SetCount != setCount)
			{
				continue;
			}

			if (candidate.SchemaFingerprint != schemaFingerprint)
				continue;

			if (candidate.ViewFamilyIdentity != viewFamilyIdentity)
				continue;
			if (candidate.DrawUniformSlot != drawUniformSlot)
				continue;

			if (candidate.BindingIdentityFingerprint != bindingIdentityFingerprint)
				continue;
			if (candidate.ResourceFingerprint != resourceFingerprint &&
				(requireImmutableResourceVariant ||
				 (!allowCompletedDescriptorSlotRefresh &&
				  !DescriptorSetsAreUpdateAfterBind(candidate.ActiveSetMask))))
			{
				continue;
			}

			if (!IsDescriptorAllocationValid(candidate, descriptorFrameSlotCount, setCount))
				continue;

			allocation = candidate;
			return true;
		}

		// This lookup is intentionally attempted before the shared-material tier.
		// Keep the expected miss allocation-free; the caller constructs detailed
		// diagnostics only if all reusable tiers fail.
		reason = "no captured descriptor allocation";
		return false;
	}

	private bool DescriptorAllocationMatchesCapturedRequest(
		DescriptorAllocation? allocation,
		XRMaterial material,
		int drawUniformSlot,
		int descriptorFrameSlotCount,
		int setCount,
		ulong layoutFingerprint,
		ulong schemaFingerprint,
		int viewFamilyIdentity,
		ulong bindingIdentityFingerprint,
		ulong resourceFingerprint,
		bool requireImmutableResourceVariant,
		bool allowCompletedDescriptorSlotRefresh)
		=> allocation is not null &&
			allocation.LayoutFingerprint == layoutFingerprint &&
			DescriptorAllocationMatchesProgram(allocation) &&
			ReferenceEquals(allocation.Material, material) &&
			allocation.MaterialBindingLayoutVersion == material.BindingLayoutVersion &&
			allocation.DescriptorFrameSlotCount == descriptorFrameSlotCount &&
			allocation.SetCount == setCount &&
			allocation.SchemaFingerprint == schemaFingerprint &&
			allocation.ViewFamilyIdentity == viewFamilyIdentity &&
			allocation.DrawUniformSlot == drawUniformSlot &&
			allocation.BindingIdentityFingerprint == bindingIdentityFingerprint &&
			(allocation.ResourceFingerprint == resourceFingerprint ||
				(!requireImmutableResourceVariant &&
				 (allowCompletedDescriptorSlotRefresh ||
				  DescriptorSetsAreUpdateAfterBind(allocation.ActiveSetMask)))) &&
			IsDescriptorAllocationValid(allocation, descriptorFrameSlotCount, setCount);

	private bool TryActivateReusableDescriptorSetsForCapturedResources(
		XRMaterial material,
		int drawUniformSlot,
		int descriptorFrameSlotCount,
		int setCount,
		ulong layoutFingerprint,
		ulong schemaFingerprint,
		int viewFamilyIdentity,
		ulong bindingIdentityFingerprint,
		ulong resourceFingerprint,
		int? refreshFrameIndex,
		ComputeDispatchSnapshot? bindingSnapshot,
		out string reason)
	{
		reason = "reusable";
		// An immutable captured draw snapshot is a resource variant, not a request to
		// republish one descriptor-set handle. Rewriting that handle for a shadow/fallback
		// variant and then a main-pass variant changes every command buffer that recorded
		// the first binding. Snapshotless frame-source refreshes may still update a
		// completed per-frame slot in place.
		bool allowCompletedDescriptorSlotRefresh = bindingSnapshot is null &&
			refreshFrameIndex is { } completedFrameIndex &&
			Renderer.CanUpdateCompletedDescriptorFrameSlot(completedFrameIndex);

		if (drawUniformSlot >= _uniformDrawSlotCapacity)
		{
			reason = $"draw slot capacity {drawUniformSlot + 1}>{_uniformDrawSlotCapacity}";
			return false;
		}

		if (!TryFindReusableDescriptorAllocationForCapturedResources(
			material,
			drawUniformSlot,
			descriptorFrameSlotCount,
			setCount,
			layoutFingerprint,
			schemaFingerprint,
			viewFamilyIdentity,
			bindingIdentityFingerprint,
			resourceFingerprint,
			bindingSnapshot is not null,
			allowCompletedDescriptorSlotRefresh,
			out DescriptorAllocation allocation,
			out reason))
		{
			return false;
		}
		if (_program is not { } program)
		{
			reason = "program missing after descriptor allocation lookup";
			return false;
		}

		RefreshDescriptorAllocationMetadata(allocation, program, material, descriptorFrameSlotCount, setCount);

		bool resourceMatches = false;
		if (refreshFrameIndex is { } currentFrameIndex)
		{
			// Descriptor sets are allocated per frame slot, while dynamic uniform offsets
			// select the per-draw data inside that set. Folding drawUniformSlot into this
			// lookup clamps most draws to the final swapchain slot and refreshes the wrong
			// image's descriptors during primary-command-buffer reuse.
			int descriptorSlotIndex = ResolveDescriptorFrameIndex(currentFrameIndex, allocation.Sets.Length);
			resourceMatches = allocation.Sets[descriptorSlotIndex].Length == setCount &&
				DescriptorSlotResourceFingerprintMatches(allocation, descriptorSlotIndex, resourceFingerprint);
		}
		else
		{
			resourceMatches = allocation.ResourceFingerprint == resourceFingerprint;
		}

		if (!resourceMatches)
		{
			if (refreshFrameIndex is not { } frameIndex)
			{
				if (DescriptorResourceFingerprintDiagnosticsEnabled)
				{
					IReadOnlyList<DescriptorBindingInfo> currentBindings = _program?.DescriptorBindings ?? [];
					string currentDetails = ComputeDescriptorResourceFingerprintDetails(material, BackendContext.Descriptors.FrameSlotCount, currentBindings);
					reason = _descriptorDirty
						? $"captured descriptors dirty; old=[{allocation.ResourceFingerprintDetails}] new=[{currentDetails}]"
						: $"captured resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}; old=[{allocation.ResourceFingerprintDetails}] new=[{currentDetails}]";
				}
				else
				{
					reason = _descriptorDirty
						? "captured descriptors dirty"
						: $"captured resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}";
				}
				return false;
			}

			if (!TryRefreshCapturedDescriptorAllocationResources(
					allocation,
					material,
					frameIndex,
					drawUniformSlot,
					resourceFingerprint,
					bindingSnapshot,
					out reason))
				return false;
		}

		if (!IsDescriptorAllocationValid(allocation, descriptorFrameSlotCount, setCount))
		{
			reason = "active descriptor allocation invalid";
			return false;
		}

		ActivateDescriptorAllocation(allocation);
		_descriptorDirty = false;
		return true;
	}

	private bool TryRefreshCapturedDescriptorAllocationResources(
		DescriptorAllocation allocation,
		XRMaterial material,
		int frameIndex,
		int drawUniformSlot,
		ulong resourceFingerprint,
		ComputeDispatchSnapshot? bindingSnapshot,
		out string reason)
	{
		reason = "reusable";
		if (_program is null)
			return true;
		if (bindingSnapshot is not null)
		{
			reason = "captured descriptor resource variant is immutable";
			return false;
		}

		if (!DescriptorSetsAreUpdateAfterBind(allocation.ActiveSetMask) &&
			!Renderer.CanUpdateCompletedDescriptorFrameSlot(frameIndex))
		{
			reason = $"captured descriptor frame slot {frameIndex} is still in flight";
			return false;
		}

		if (allocation.Sets is null || allocation.Sets.Length == 0)
		{
			reason = "captured descriptor set array is null or empty";
			return false;
		}

		// Keep descriptor-slot refresh aligned with the set recorded for this swapchain
		// image. drawUniformSlot belongs only to the dynamic UBO offset path.
		int descriptorSlotIndex = ResolveDescriptorFrameIndex(frameIndex, allocation.Sets.Length);
		if ((uint)descriptorSlotIndex >= (uint)allocation.Sets.Length)
		{
			reason = $"captured descriptor slot {descriptorSlotIndex} is outside allocation length {allocation.Sets.Length}";
			return false;
		}

		if (!EnsureDescriptorSlotReady(
			allocation,
			material,
			_program.DescriptorBindings,
			frameIndex,
			drawUniformSlot,
			resourceFingerprint,
			bindingSnapshot,
			recordDescriptorTableGeneration: false))
		{
			reason = "captured descriptor resource refresh failed";
			return false;
		}

		if (DescriptorResourceFingerprintDiagnosticsEnabled)
			allocation.ResourceFingerprintDetails = ComputeDescriptorResourceFingerprintDetails(material, BackendContext.Descriptors.FrameSlotCount, _program.DescriptorBindings);
		return true;
	}

	private static bool DescriptorSlotResourceFingerprintMatches(DescriptorAllocation allocation, int descriptorSlotIndex, ulong resourceFingerprint)
		=> (uint)descriptorSlotIndex < (uint)allocation.SlotResourceFingerprints.Length &&
			allocation.SlotResourceFingerprints[descriptorSlotIndex] == resourceFingerprint;

	private static void SetDescriptorSlotResourceFingerprint(DescriptorAllocation allocation, int descriptorSlotIndex, ulong resourceFingerprint)
	{
		if ((uint)descriptorSlotIndex >= (uint)allocation.SlotResourceFingerprints.Length)
			return;

		allocation.SlotResourceFingerprints[descriptorSlotIndex] = resourceFingerprint;
		allocation.ResourceFingerprint = resourceFingerprint;
	}

	private bool TryActivateReusableDescriptorSetsFast(
		XRMaterial material,
		int drawUniformSlot,
		int descriptorFrameSlotCount,
		int setCount,
		ulong layoutFingerprint,
		ulong schemaFingerprint,
		int viewFamilyIdentity,
		ulong bindingIdentityFingerprint,
		ulong resourceFingerprint,
		out string reason)
	{
		reason = "reusable";

		DescriptorAllocation? allocation = _activeDescriptorAllocation;
		if (allocation is null || _descriptorDirty)
		{
			reason = allocation is null ? "no active descriptor allocation" : "descriptors dirty";
			return false;
		}

		if (!ReferenceEquals(allocation.Material, material))
		{
			reason = "active descriptor allocation material changed";
			return false;
		}

		if (!DescriptorAllocationMatchesProgram(allocation))
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"active descriptor program {allocation.ProgramBindingId}->{_program?.BindingId ?? 0u}"
				: "active descriptor program changed";
			return false;
		}

		if (allocation.MaterialBindingLayoutVersion != material.BindingLayoutVersion)
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"material binding layout {allocation.MaterialBindingLayoutVersion}->{material.BindingLayoutVersion}"
				: "material binding layout changed";
			return false;
		}

		if (allocation.DescriptorFrameSlotCount != descriptorFrameSlotCount || allocation.SetCount != setCount)
		{
			reason = "active descriptor allocation shape changed";
			return false;
		}

		if (drawUniformSlot >= _uniformDrawSlotCapacity)
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"draw slot capacity {drawUniformSlot + 1}>{_uniformDrawSlotCapacity}"
				: "draw slot capacity exceeded";
			return false;
		}

		if (!IsDescriptorAllocationValid(allocation, descriptorFrameSlotCount, setCount))
		{
			reason = "active descriptor allocation invalid";
			return false;
		}

		if (allocation.SchemaFingerprint != schemaFingerprint)
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"active schema fingerprint 0x{allocation.SchemaFingerprint:X16}->0x{schemaFingerprint:X16}"
				: "active schema fingerprint changed";
			return false;
		}

		if (allocation.LayoutFingerprint != layoutFingerprint)
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"active layout fingerprint 0x{allocation.LayoutFingerprint:X16}->0x{layoutFingerprint:X16}"
				: "active layout fingerprint changed";
			return false;
		}
		if (allocation.DrawUniformSlot != drawUniformSlot)
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"active descriptor draw slot {allocation.DrawUniformSlot}->{drawUniformSlot}"
				: "active descriptor draw slot changed";
			return false;
		}

		if (allocation.ViewFamilyIdentity != viewFamilyIdentity)
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"active descriptor view family {allocation.ViewFamilyIdentity}->{viewFamilyIdentity}"
				: "active descriptor view family changed";
			return false;
		}

		if (allocation.BindingIdentityFingerprint != bindingIdentityFingerprint)
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"active descriptor binding identity 0x{allocation.BindingIdentityFingerprint:X16}->0x{bindingIdentityFingerprint:X16}"
				: "active descriptor binding identity changed";
			return false;
		}

		if (allocation.ResourceFingerprint != resourceFingerprint)
		{
			reason = DescriptorResourceFingerprintDiagnosticsEnabled
				? $"active resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}"
				: "active resource fingerprint changed";
			return false;
		}

		ActivateDescriptorAllocation(allocation);
		_descriptorDirty = false;
		return true;
	}

	private bool DescriptorAllocationMatchesProgram(DescriptorAllocation? allocation)
		=> allocation is not null &&
			_program is not null &&
			allocation.ProgramBindingId == _program.BindingId &&
			ReferenceEquals(allocation.Program, _program);

	private static bool IsDescriptorAllocationValid(DescriptorAllocation allocation, int descriptorFrameSlotCount, int setCount)
		=> (allocation.Pool.Handle != 0 || (allocation.ActiveSetMask == 0 && allocation.UsesSharedMaterialTier)) &&
            allocation.Sets is { Length: > 0 } &&
            allocation.Sets.Length == descriptorFrameSlotCount &&
			allocation.SlotResourceFingerprints.Length == descriptorFrameSlotCount &&
			allocation.DescriptorHeapPushData.Length == descriptorFrameSlotCount &&
			allocation.Layouts.Length == setCount &&
            DescriptorSetsHaveSetCount(allocation.Sets, setCount);

	private static int ResolveDescriptorFrameIndex(int frameIndex, int frameCount)
	{
		if (frameCount <= 1)
			return 0;
		int resolved = frameIndex % frameCount;
		return resolved < 0 ? resolved + frameCount : resolved;
	}

	/// <summary>
	/// Keeps captured draw variants keyed by their exact descriptor resources.
	/// UPDATE_AFTER_BIND permits legal updates; it does not preserve earlier
	/// descriptor contents for commands that already bound the same set handle.
	/// </summary>
	internal static ulong ResolveDescriptorAllocationResourceVariantFingerprint(
		bool allActiveSetsUpdateAfterBind,
		bool hasCapturedBindingSnapshot,
		ulong resourceFingerprint)
		=> hasCapturedBindingSnapshot || !allActiveSetsUpdateAfterBind ? resourceFingerprint : 0UL;

	private bool DescriptorSetsAreUpdateAfterBind(uint activeSetMask)
	{
		if (_program is null)
			return false;

		for (uint setIndex = 0; setIndex < 32; setIndex++)
		{
			if ((activeSetMask & (1u << (int)setIndex)) != 0 &&
				!_program.DescriptorSetUsesUpdateAfterBind(setIndex))
			{
				return false;
			}
		}

		return true;
	}

}

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
		private static readonly bool DescriptorResourceFingerprintDiagnosticsEnabled =
			string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanFrameDataReuseDiag), "1", StringComparison.Ordinal) ||
			string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanDescriptorFingerprintDiag), "1", StringComparison.Ordinal);

		private static readonly bool MaterialBindingDiagnosticsEnabled =
			string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanMaterialBindingDiag), "1", StringComparison.Ordinal);

		private ulong _descriptorAllocationUsageSerial;

		/// <summary>
		/// Ensures the descriptor sets for one frame/draw slot are allocated and current.
		/// Pool identity is structural while local output/pass sets are immutable resource
		/// variants. Stable material sets remain shared through the material tier.
		/// </summary>
		private bool EnsureDescriptorSets(XRMaterial material, int drawUniformSlot, int frameIndex = 0)
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

			int frameCount = Renderer.DescriptorFrameSlotFrameCount;
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
				(activeSetMask & (1u << (int)DescriptorSetMaterial)) != 0 &&
				Renderer.GetOrCreateAPIRenderObject(material, generateNow: true) is VkMaterial materialObject &&
				materialObject.TryGetMaterialDescriptorSet(_program, frameIndex, out _, out _))
			{
				sharedMaterial = materialObject;
				usesSharedMaterialTier = true;
				activeSetMask &= ~(1u << (int)DescriptorSetMaterial);
			}
			int activeSetCount = System.Numerics.BitOperations.PopCount(activeSetMask);
			ulong layoutFingerprint = ComputeDescriptorLayoutFingerprint(layouts);
			ulong schemaFingerprint = ComputeDescriptorSchemaFingerprint(bindings, setCount);
			ulong resourceFingerprint = ComputeDescriptorResourceFingerprint(
				material,
				frameCount,
				bindings,
				drawUniformSlot,
				usesSharedMaterialTier);
			int materialIdentity = usesSharedMaterialTier
				? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(material)
				: 0;
			int viewFamilyIdentity = Renderer.ResolveMeshDescriptorViewFamilyIdentity();
			DescriptorAllocationKey allocationKey = new(
				layoutFingerprint,
				schemaFingerprint,
				descriptorFrameSlotCount,
				setCount,
				materialIdentity,
				viewFamilyIdentity,
				resourceFingerprint);

			if (_descriptorAllocations.TryGetValue(allocationKey, out DescriptorAllocation? cachedAllocation) &&
				IsDescriptorAllocationValid(cachedAllocation, descriptorFrameSlotCount, setCount))
			{
				cachedAllocation.SharedMaterial = sharedMaterial;
				cachedAllocation.UsesSharedMaterialTier = usesSharedMaterialTier;
				RefreshDescriptorAllocationMetadata(cachedAllocation, _program, material, descriptorFrameSlotCount, setCount);
				if (!EnsureDescriptorSlotReady(cachedAllocation, material, bindings, frameIndex, drawUniformSlot, resourceFingerprint))
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

			if (Renderer.TryAcquireSharedMeshDescriptorAllocation(
					allocationKey,
					material,
					out DescriptorAllocation sharedAllocation))
			{
				if (IsDescriptorAllocationValid(sharedAllocation, descriptorFrameSlotCount, setCount))
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
				usesSharedMaterialTier ? 1u << (int)DescriptorSetMaterial : 0u);
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
				ViewFamilyIdentity = viewFamilyIdentity,
				ResourceFingerprint = resourceFingerprint,
				SlotResourceFingerprints = new ulong[descriptorFrameSlotCount]
			};

			for (int frameSlot = 0; frameSlot < descriptorFrameSlotCount; frameSlot++)
			{
				if (EnsureDescriptorSlotReady(allocation, material, bindings, frameSlot, drawUniformSlot, resourceFingerprint))
					continue;
				Renderer.ReleaseMeshDescriptorPoolSlab(poolSlabLease, descriptorSets, activeSetMask);
				return false;
			}

			allocation.ResourceFingerprintDetails = DescriptorResourceFingerprintDiagnosticsEnabled
				? ComputeDescriptorResourceFingerprintDetails(material, frameCount, bindings)
				: string.Empty;
			DescriptorAllocation publishedAllocation = Renderer.PublishSharedMeshDescriptorAllocation(
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
			ulong resourceFingerprint)
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

				int resolvedFrame = frameIndex % Math.Max(Renderer.DescriptorFrameSlotFrameCount, 1);
				if (resolvedFrame < 0)
					resolvedFrame += Math.Max(Renderer.DescriptorFrameSlotFrameCount, 1);
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
				frameSets[DescriptorSetMaterial] = materialSet;
			}

			if (DescriptorSlotResourceFingerprintMatches(allocation, descriptorSlotIndex, resourceFingerprint))
				return true;

			if (!WriteDescriptorSets(frameSets, bindings, material, frameIndex, drawUniformSlot, allocation, descriptorSlotIndex))
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

			foreach (DescriptorBindingInfo binding in bindings)
			{
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
				out reason);

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

			return ComputeDescriptorSchemaFingerprint(bindings, layouts.Count);
		}

		internal ulong ComputeRecordedDescriptorResourceSignature(XRMaterial material, VkRenderProgram? preparedProgram)
		{
			VkRenderProgram? program = preparedProgram ?? _program;
			IReadOnlyList<DescriptorSetLayout>? layouts = program?.DescriptorSetLayouts;
			IReadOnlyList<DescriptorBindingInfo>? bindings = program?.DescriptorBindings;
			if (layouts is not { Count: > 0 } || bindings is not { Count: > 0 })
				return 0UL;

			int frameCount = Renderer.DescriptorFrameSlotFrameCount;
			if (frameCount <= 0)
				return 0UL;

			return ComputeDescriptorResourceFingerprint(
				material,
				frameCount,
				bindings,
				drawUniformSlot: 0,
				usesSharedMaterialTier: false);
		}

		private bool CanReuseRecordedDescriptorSets(
			XRMaterial material,
			int drawUniformSlot,
			bool resourcesCapturedByFrameSignature,
			int? refreshFrameIndex,
			out string reason)
		{
			reason = "reusable";
			if (_program is null)
				return true;

			var layouts = _program.DescriptorSetLayouts;
			var bindings = _program.DescriptorBindings;
			if (layouts is null || layouts.Count == 0 || bindings.Count == 0)
				return true;

			int frameCount = Renderer.DescriptorFrameSlotFrameCount;
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
			ulong layoutFingerprint = ComputeDescriptorLayoutFingerprint(layouts);
			ulong schemaFingerprint = ComputeDescriptorSchemaFingerprint(bindings, setCount);
			bool usesSharedMaterialTier = _activeDescriptorAllocation is { } activeAllocation &&
				ReferenceEquals(activeAllocation.Material, material) &&
				activeAllocation.UsesSharedMaterialTier;
			ulong resourceFingerprint = ComputeDescriptorResourceFingerprint(
				material,
				frameCount,
				bindings,
				drawUniformSlot,
				usesSharedMaterialTier);
			int viewFamilyIdentity = Renderer.ResolveMeshDescriptorViewFamilyIdentity();
			if ((resourcesCapturedByFrameSignature || refreshFrameIndex.HasValue) &&
				TryActivateReusableDescriptorSetsForCapturedResources(
					material,
					drawUniformSlot,
					descriptorFrameSlotCount,
					setCount,
					layoutFingerprint,
					schemaFingerprint,
					viewFamilyIdentity,
					resourceFingerprint,
					refreshFrameIndex,
					out reason))
			{
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
				resourceFingerprint,
				out reason))
				return true;

			// The active draw can be a shadow/material override even though a compatible
			// shared-material allocation was prewarmed for this draw. Probe the exact
			// shared-tier key before reporting a pool miss; this only acquires an existing
			// immutable allocation and never performs Vulkan allocation or descriptor writes.
			if (!usesSharedMaterialTier)
			{
				ulong sharedResourceFingerprint = ComputeDescriptorResourceFingerprint(
					material,
					frameCount,
					bindings,
					drawUniformSlot,
					usesSharedMaterialTier: true);
				DescriptorAllocationKey sharedAllocationKey = new(
					layoutFingerprint,
					schemaFingerprint,
					descriptorFrameSlotCount,
					setCount,
					System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(material),
					viewFamilyIdentity,
					sharedResourceFingerprint);

				if (_descriptorAllocations.TryGetValue(sharedAllocationKey, out DescriptorAllocation? localSharedAllocation))
				{
					if (IsDescriptorAllocationValid(localSharedAllocation, descriptorFrameSlotCount, setCount))
					{
						RefreshDescriptorAllocationMetadata(localSharedAllocation, _program, material, descriptorFrameSlotCount, setCount);
						ActivateDescriptorAllocation(localSharedAllocation);
						_descriptorDirty = false;
						return true;
					}

					ReleaseDescriptorAllocationReference(sharedAllocationKey, localSharedAllocation);
					_descriptorAllocations.Remove(sharedAllocationKey);
				}

				if (Renderer.TryAcquireSharedMeshDescriptorAllocation(
						sharedAllocationKey,
						material,
						out DescriptorAllocation sharedAllocation))
				{
					if (IsDescriptorAllocationValid(sharedAllocation, descriptorFrameSlotCount, setCount))
					{
						RefreshDescriptorAllocationMetadata(sharedAllocation, _program, material, descriptorFrameSlotCount, setCount);
						_descriptorAllocations.Add(sharedAllocationKey, sharedAllocation);
						ActivateDescriptorAllocation(sharedAllocation);
						_descriptorDirty = false;
						return true;
					}

					Renderer.ReleaseSharedMeshDescriptorAllocation(sharedAllocationKey, sharedAllocation);
				}
			}

			int materialIdentity = _activeDescriptorAllocation?.UsesSharedMaterialTier == true
				? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(material)
				: 0;
			DescriptorAllocationKey allocationKey = new(
				layoutFingerprint,
				schemaFingerprint,
				descriptorFrameSlotCount,
				setCount,
				materialIdentity,
				viewFamilyIdentity,
				resourceFingerprint);

			if (!_descriptorAllocations.TryGetValue(allocationKey, out DescriptorAllocation? allocation) &&
				Renderer.TryAcquireSharedMeshDescriptorAllocation(allocationKey, material, out DescriptorAllocation cachedSharedAllocation))
			{
				if (IsDescriptorAllocationValid(cachedSharedAllocation, descriptorFrameSlotCount, setCount))
				{
					allocation = cachedSharedAllocation;
					_descriptorAllocations.Add(allocationKey, cachedSharedAllocation);
				}
				else
				{
					Renderer.ReleaseSharedMeshDescriptorAllocation(allocationKey, cachedSharedAllocation);
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
			VkRenderProgram? program,
			XRMaterial material,
			int descriptorFrameSlotCount,
			int setCount)
		{
			allocation.Program = program;
			allocation.Material = material;
			allocation.MaterialBindingLayoutVersion = material.BindingLayoutVersion;
			allocation.DescriptorFrameSlotCount = descriptorFrameSlotCount;
			allocation.SetCount = setCount;
		}

		private bool TryFindReusableDescriptorAllocationForCapturedResources(
			XRMaterial material,
			int descriptorFrameSlotCount,
			int setCount,
			ulong layoutFingerprint,
			ulong schemaFingerprint,
			int viewFamilyIdentity,
			ulong resourceFingerprint,
			out DescriptorAllocation allocation,
			out string reason)
		{
			allocation = null!;
			reason = "reusable";

			DescriptorAllocation? active = _activeDescriptorAllocation;
			if (DescriptorAllocationMatchesCapturedRequest(
				active,
				material,
				descriptorFrameSlotCount,
				setCount,
				layoutFingerprint,
				schemaFingerprint,
				viewFamilyIdentity,
				resourceFingerprint))
			{
				allocation = active!;
				return true;
			}

			int programMatches = 0;
			int materialMatches = 0;
			int shapeMatches = 0;
			int schemaMatches = 0;
			int validMatches = 0;
			foreach (DescriptorAllocation candidate in _descriptorAllocations.Values)
			{
				if (candidate.LayoutFingerprint != layoutFingerprint)
					continue;
				programMatches++;

				if (candidate.UsesSharedMaterialTier &&
					(!ReferenceEquals(candidate.Material, material) ||
					 candidate.MaterialBindingLayoutVersion != material.BindingLayoutVersion))
				{
					continue;
				}
				materialMatches++;

				if (candidate.DescriptorFrameSlotCount != descriptorFrameSlotCount ||
					candidate.SetCount != setCount)
				{
					continue;
				}
				shapeMatches++;

				if (candidate.SchemaFingerprint != schemaFingerprint)
					continue;
				schemaMatches++;

				if (candidate.ViewFamilyIdentity != viewFamilyIdentity)
					continue;

				if (candidate.ResourceFingerprint != resourceFingerprint)
					continue;

				if (!IsDescriptorAllocationValid(candidate, descriptorFrameSlotCount, setCount))
					continue;
				validMatches++;

				allocation = candidate;
				return true;
			}

			reason =
				$"no captured descriptor allocation for schema=0x{schemaFingerprint:X16} " +
				$"allocs={_descriptorAllocations.Count} programMatches={programMatches} " +
				$"materialMatches={materialMatches} shapeMatches={shapeMatches} " +
				$"schemaMatches={schemaMatches} validMatches={validMatches}";
			return false;
		}

		private bool DescriptorAllocationMatchesCapturedRequest(
			DescriptorAllocation? allocation,
			XRMaterial material,
			int descriptorFrameSlotCount,
			int setCount,
			ulong layoutFingerprint,
			ulong schemaFingerprint,
			int viewFamilyIdentity,
			ulong resourceFingerprint)
			=> allocation is not null &&
				allocation.LayoutFingerprint == layoutFingerprint &&
				(!allocation.UsesSharedMaterialTier ||
				 (ReferenceEquals(allocation.Material, material) &&
				  allocation.MaterialBindingLayoutVersion == material.BindingLayoutVersion)) &&
				allocation.DescriptorFrameSlotCount == descriptorFrameSlotCount &&
				allocation.SetCount == setCount &&
				allocation.SchemaFingerprint == schemaFingerprint &&
				allocation.ViewFamilyIdentity == viewFamilyIdentity &&
				allocation.ResourceFingerprint == resourceFingerprint &&
				IsDescriptorAllocationValid(allocation, descriptorFrameSlotCount, setCount);

		private bool TryActivateReusableDescriptorSetsForCapturedResources(
			XRMaterial material,
			int drawUniformSlot,
			int descriptorFrameSlotCount,
			int setCount,
			ulong layoutFingerprint,
			ulong schemaFingerprint,
			int viewFamilyIdentity,
			ulong resourceFingerprint,
			int? refreshFrameIndex,
			out string reason)
		{
			reason = "reusable";

			if (drawUniformSlot >= _uniformDrawSlotCapacity)
			{
				reason = $"draw slot capacity {drawUniformSlot + 1}>{_uniformDrawSlotCapacity}";
				return false;
			}

			if (!TryFindReusableDescriptorAllocationForCapturedResources(
				material,
				descriptorFrameSlotCount,
				setCount,
				layoutFingerprint,
				schemaFingerprint,
				viewFamilyIdentity,
				resourceFingerprint,
				out DescriptorAllocation allocation,
				out reason))
			{
				return false;
			}

			RefreshDescriptorAllocationMetadata(allocation, _program, material, descriptorFrameSlotCount, setCount);

			bool resourceMatches = false;
			if (refreshFrameIndex is { } currentFrameIndex)
			{
				int descriptorSlotIndex = ResolveUniformBufferIndex(currentFrameIndex, drawUniformSlot, allocation.Sets.Length);
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
						string currentDetails = ComputeDescriptorResourceFingerprintDetails(material, Renderer.DescriptorFrameSlotFrameCount, currentBindings);
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

				if (!TryRefreshCapturedDescriptorAllocationResources(allocation, material, frameIndex, drawUniformSlot, resourceFingerprint, out reason))
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
			out string reason)
		{
			reason = "reusable";
			if (_program is null)
				return true;

			if (allocation.Sets is null || allocation.Sets.Length == 0)
			{
				reason = "captured descriptor set array is null or empty";
				return false;
			}

			int descriptorSlotIndex = ResolveUniformBufferIndex(frameIndex, drawUniformSlot, allocation.Sets.Length);
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
				resourceFingerprint))
			{
				reason = "captured descriptor resource refresh failed";
				return false;
			}

			if (DescriptorResourceFingerprintDiagnosticsEnabled)
				allocation.ResourceFingerprintDetails = ComputeDescriptorResourceFingerprintDetails(material, Renderer.DescriptorFrameSlotFrameCount, _program.DescriptorBindings);
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

			if (allocation.UsesSharedMaterialTier && !ReferenceEquals(allocation.Material, material))
			{
				reason = "active descriptor allocation material changed";
				return false;
			}

			if (allocation.UsesSharedMaterialTier && allocation.MaterialBindingLayoutVersion != material.BindingLayoutVersion)
			{
				reason = $"material binding layout {allocation.MaterialBindingLayoutVersion}->{material.BindingLayoutVersion}";
				return false;
			}

			if (allocation.DescriptorFrameSlotCount != descriptorFrameSlotCount || allocation.SetCount != setCount)
			{
				reason = "active descriptor allocation shape changed";
				return false;
			}

			if (drawUniformSlot >= _uniformDrawSlotCapacity)
			{
				reason = $"draw slot capacity {drawUniformSlot + 1}>{_uniformDrawSlotCapacity}";
				return false;
			}

			if (!IsDescriptorAllocationValid(allocation, descriptorFrameSlotCount, setCount))
			{
				reason = "active descriptor allocation invalid";
				return false;
			}

			if (allocation.SchemaFingerprint != schemaFingerprint)
			{
				reason = $"active schema fingerprint 0x{allocation.SchemaFingerprint:X16}->0x{schemaFingerprint:X16}";
				return false;
			}

			if (allocation.LayoutFingerprint != layoutFingerprint)
			{
				reason = $"active layout fingerprint 0x{allocation.LayoutFingerprint:X16}->0x{layoutFingerprint:X16}";
				return false;
			}

			if (allocation.ViewFamilyIdentity != viewFamilyIdentity)
			{
				reason = $"active descriptor view family {allocation.ViewFamilyIdentity}->{viewFamilyIdentity}";
				return false;
			}

			if (allocation.ResourceFingerprint != resourceFingerprint)
			{
				reason = $"active resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}";
				return false;
			}

			ActivateDescriptorAllocation(allocation);
			_descriptorDirty = false;
			return true;
		}

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
        /// Computes a fingerprint over descriptor set layout metadata (binding indices,
        /// types, stages). Used to detect when the schema changes and a full
        /// reallocation of the descriptor pool is required.
        /// </summary>
		private static ulong ComputeDescriptorLayoutFingerprint(IReadOnlyList<DescriptorSetLayout> layouts)
		{
			const ulong offsetBasis = 14695981039346656037UL;
			const ulong prime = 1099511628211UL;
			ulong hash = offsetBasis;
			for (int i = 0; i < layouts.Count; i++)
			{
				hash ^= layouts[i].Handle;
				hash *= prime;
			}

			hash ^= unchecked((ulong)layouts.Count);
			return hash * prime;
		}

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

		private ulong ComputeDescriptorResourceFingerprint(
			XRMaterial material,
			int frameCount,
			IReadOnlyList<DescriptorBindingInfo> bindings,
			int drawUniformSlot,
			bool usesSharedMaterialTier)
		{
			FrameOpSignatureHasher hash = new();
			hash.Add(frameCount);
			for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
			{
				DescriptorBindingInfo binding = bindings[bindingIndex];
				if (usesSharedMaterialTier && binding.Set == DescriptorSetMaterial)
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
							bool resolved = TryResolveBuffer(binding, frameIndex, drawUniformSlot, out DescriptorBufferInfo info);
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
						for (int arrayIndex = 0; arrayIndex < descriptorCount; arrayIndex++)
						{
							bool resolved = TryResolveImage(binding, material, binding.DescriptorType, out DescriptorImageInfo info, arrayIndex);
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

		private bool IsFrameSourceSamplerBinding(XRMaterial material, DescriptorBindingInfo binding)
		{
			if (IsFrameSourceSamplerName(binding.Name))
				return true;

			if (string.IsNullOrWhiteSpace(binding.Name) ||
				MaterialResolvesDescriptorBinding(material, binding) ||
				!BindingResolvesPipelineResourceTexture(binding))
			{
				return false;
			}

			XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
			return pipeline is not null &&
				_program is not null &&
				_program.TryGetSamplerTexture(binding.Name, out XRTexture? programTexture) &&
				pipeline.TryGetTexture(binding.Name, out XRTexture? pipelineTexture) &&
				ReferenceEquals(programTexture, pipelineTexture);
		}

		private bool MaterialResolvesDescriptorBinding(XRMaterial material, DescriptorBindingInfo binding)
		{
			if (VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding))
				return material.Textures.Count > 0;

			if (!string.IsNullOrWhiteSpace(binding.Name) &&
				_program is not null &&
				_program.TryGetSamplerTexture(binding.Name, out _))
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
			hash.Add(FrameSourceMutableDescriptorSignature);
			AddTextureDescriptorResourceFingerprint(ref hash, texture);
		}

		private bool TryRefreshFrameSourceDescriptorSetsForDraw(
			int frameIndex,
			int drawUniformSlot,
			XRMaterial material,
			ComputeDispatchSnapshot? snapshot,
			out string reason)
		{
			reason = "no frame-source sampler descriptors";

			if (_program is null ||
				_program.DescriptorSetLayouts.Count == 0 ||
				_program.DescriptorBindings.Count == 0)
			{
				return true;
			}

			XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
			if (!SnapshotHasFrameSourceSampler(snapshot, pipeline) &&
				!DescriptorBindingsHaveFrameSourceSampler(material, _program.DescriptorBindings))
			{
				return true;
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
				out reason);
		}

		private static bool SnapshotHasFrameSourceSampler(ComputeDispatchSnapshot? snapshot, XRRenderPipelineInstance? pipeline)
		{
			if (snapshot is null)
				return false;

			foreach (string name in snapshot.SamplersByName.Keys)
				if (IsMutableFrameSourceSamplerName(name, pipeline))
					return true;

			return false;
		}

		private bool DescriptorBindingsHaveFrameSourceSampler(XRMaterial material, IReadOnlyList<DescriptorBindingInfo> bindings)
		{
			for (int i = 0; i < bindings.Count; i++)
				if (IsFrameSourceSamplerBinding(material, bindings[i]))
					return true;

			return false;
		}

		private bool TryRefreshFrameSourceSamplerDescriptors(
			DescriptorAllocation? allocation,
			int descriptorSlotIndex,
			DescriptorSet[] frameSets,
			IReadOnlyList<DescriptorBindingInfo> bindings,
			XRMaterial material,
			out string reason)
		{
			bool refreshed = false;
			reason = "no frame-source sampler descriptors";

			Span<DescriptorImageInfo> imageInfos = stackalloc DescriptorImageInfo[8];
			for (int i = 0; i < bindings.Count; i++)
			{
				DescriptorBindingInfo binding = bindings[i];
				if (!IsFrameSourceSamplerBinding(material, binding))
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
					if (!TryResolveImage(binding, material, binding.DescriptorType, out imageInfos[arrayIndex], arrayIndex))
					{
						reason = $"failed to resolve frame-source sampler '{binding.Name}'";
						return false;
					}
				}

				ReadOnlySpan<DescriptorImageInfo> resolvedImageInfos = imageInfos[..(int)descriptorCount];
				if (FrameSourceDescriptorWriteMatches(allocation, descriptorSlotIndex, binding, descriptorCount, resolvedImageInfos))
					continue;

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

					Renderer.UpdateDescriptorSetsTracked(1, &write);
					Renderer.RecordVulkanDescriptorTableGeneration("MeshRendererDescriptorSet.SingleUpdate");
				}

				RecordFrameSourceDescriptorWriteSignature(allocation, descriptorSlotIndex, binding, descriptorCount, resolvedImageInfos);
				refreshed = true;
			}

			if (refreshed)
				reason = "refreshed frame-source sampler descriptors";
			return true;
		}

		private static bool FrameSourceDescriptorWriteMatches(
			DescriptorAllocation? allocation,
			int descriptorSlotIndex,
			DescriptorBindingInfo binding,
			uint descriptorCount,
			ReadOnlySpan<DescriptorImageInfo> imageInfos)
		{
			if (allocation is null)
				return false;

			FrameSourceDescriptorWriteKey key = new(
				descriptorSlotIndex,
				binding.Set,
				binding.Binding,
				binding.DescriptorType,
				descriptorCount);

			return allocation.FrameSourceDescriptorWriteSignatures.TryGetValue(key, out ulong previousSignature) &&
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

			FrameSourceDescriptorWriteKey key = new(
				descriptorSlotIndex,
				binding.Set,
				binding.Binding,
				binding.DescriptorType,
				descriptorCount);

			allocation.FrameSourceDescriptorWriteSignatures[key] =
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
			return mask;
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
			int descriptorSlotIndex)
		{
			List<WriteDescriptorSet> writes = [];
			List<DescriptorBufferInfo> bufferInfos = [];
			List<DescriptorImageInfo> imageInfos = [];
			List<BufferView> texelBufferViews = [];
			List<(int writeIndex, int bufferIndex, DescriptorBindingInfo binding, uint descriptorCount)> bufferMap = [];
			List<(int writeIndex, int imageIndex, DescriptorBindingInfo binding, uint descriptorCount)> imageMap = [];
			List<(int writeIndex, int texelIndex, DescriptorBindingInfo binding, uint descriptorCount)> texelMap = [];

			foreach (DescriptorBindingInfo binding in bindings)
			{
				if (allocation?.UsesSharedMaterialTier == true && binding.Set == DescriptorSetMaterial)
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
						if (!TryResolveBuffers(binding, frameIndex, drawUniformSlot, descriptorCount, bufferInfos, out int bufferStart))
						{
							WarnOnce($"[WriteDesc] FAILED to resolve buffer binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}' program '{_program?.Data?.Name ?? "?"}'");
							RecordDescriptorFailure(binding, "buffer resolution failed");
							return false;
						}

						bufferMap.Add((writes.Count, bufferStart, binding, descriptorCount));
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

						imageMap.Add((writes.Count, imageStart, binding, descriptorCount));
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

						texelMap.Add((writes.Count, texelStart, binding, descriptorCount));
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
				foreach (var (writeIndex, bufferIndex, _, _) in bufferMap)
					writePtr[writeIndex].PBufferInfo = bufferPtr + bufferIndex;

				foreach (var (writeIndex, imageIndex, _, _) in imageMap)
					writePtr[writeIndex].PImageInfo = imagePtr + imageIndex;

				foreach (var (writeIndex, texelIndex, _, _) in texelMap)
					writePtr[writeIndex].PTexelBufferView = texelPtr + texelIndex;

				if (writeArray.Length > 0)
				{
					if (!ValidateDescriptorWrites(writePtr, writeArray.Length))
						return false;

					if (Renderer.IsDescriptorHeapDrawBindingActive)
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

							if (!Renderer.TryWriteDescriptorHeapBinding(_program, binding, payload, bufferPtr + bufferIndex, null, null, descriptorCount, out string heapReason))
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

							if (!Renderer.TryWriteDescriptorHeapBinding(_program, binding, payload, null, imagePtr + imageIndex, null, descriptorCount, out string heapReason))
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

							if (!Renderer.TryWriteDescriptorHeapBinding(_program, binding, payload, null, null, texelPtr + texelIndex, descriptorCount, out string heapReason))
							{
								WarnOnce($"[WriteDescHeap] FAILED texel binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}, type={binding.DescriptorType}) for mesh '{Mesh?.Name ?? "?"}': {heapReason}");
								RecordDescriptorFailure(binding, $"descriptor heap texel write failed: {heapReason}");
								return false;
							}
						}
					}

					if (!TryUpdateDescriptorSetsWithTemplates(frameSets, writeArray))
						Renderer.UpdateDescriptorSetsTracked((uint)writeArray.Length, writePtr);
					Renderer.RecordVulkanDescriptorTableGeneration("MeshRendererDescriptorSets.Update");

					foreach (var (_, imageIndex, binding, descriptorCount) in imageMap)
					{
						if (!IsFrameSourceSamplerBinding(material, binding))
							continue;

						RecordFrameSourceDescriptorWriteSignature(
							allocation,
							descriptorSlotIndex,
							binding,
							descriptorCount,
							imageArray.AsSpan(imageIndex, (int)descriptorCount));
					}
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

				if (requireImageView && !Renderer.IsLiveImageViewBackedByLiveImage(info.ImageView))
				{
					string backing = Renderer.TryGetImageViewBackingImage(info.ImageView, out Image backingImage)
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

			bool allowSynchronousTextureUpload = Renderer.AllowSynchronousResourceUploads;
			bool suppressSynchronousTextureUploadForPressure =
				allowSynchronousTextureUpload &&
				Renderer.ShouldAvoidSynchronousImageAllocationForOpenXr(out _);
			if (suppressSynchronousTextureUploadForPressure)
				allowSynchronousTextureUpload = false;

			AbstractRenderAPIObject? apiTextureObject;
			if (allowSynchronousTextureUpload)
			{
				try
				{
					apiTextureObject = Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true);
				}
				catch (VulkanOutOfMemoryException ex)
				{
					if (TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-texture-allocation-failed", out imageInfo))
						return true;

					WarnOnce($"Texture for descriptor binding '{binding.Name}' could not allocate a Vulkan image: {ex.Message}");
					RecordDescriptorFailure(binding, "texture allocation failed");
					return false;
				}
			}
			else
			{
				Renderer.TryGetAPIRenderObject(texture, out apiTextureObject);
			}

			if (apiTextureObject is not IVkImageDescriptorSource source)
			{
				if (suppressSynchronousTextureUploadForPressure &&
					TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-texture-allocation-pressure", out imageInfo))
					return true;

				if (!allowSynchronousTextureUpload &&
					TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-texture-wrapper-not-ready", out imageInfo))
					return true;

				WarnOnce($"Texture for descriptor binding '{binding.Name}' is not a Vulkan texture.");
				RecordDescriptorFailure(binding, "texture has no Vulkan descriptor source");
				return false;
			}

			string descriptorReason = $"mesh material descriptor '{binding.Name}'";
			if (!source.TryGetDescriptorSnapshot(
				binding.ExpectedImageViewType,
				requestedAspectMask: null,
				descriptorReason,
				allowSynchronousTextureUpload,
				out VkImageDescriptorSnapshot descriptorSnapshot))
			{
				if (TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-texture-not-ready", out imageInfo, source))
					return true;

				WarnOnce($"Texture for descriptor binding '{binding.Name}' is not ready for Vulkan descriptor use.");
				RecordDescriptorFailure(binding, "texture descriptor not ready");
				return false;
			}

			bool requiresSampledUsage = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.Sampler or DescriptorType.InputAttachment;
			if (requiresSampledUsage && (descriptorSnapshot.Usage & ImageUsageFlags.SampledBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_SAMPLED_BIT.");
				RecordDescriptorFailure(binding, "texture missing VK_IMAGE_USAGE_SAMPLED_BIT");
				return false;
			}

			if (descriptorType == DescriptorType.StorageImage && (descriptorSnapshot.Usage & ImageUsageFlags.StorageBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_STORAGE_BIT.");
				RecordDescriptorFailure(binding, "texture missing VK_IMAGE_USAGE_STORAGE_BIT");
				return false;
			}

			if (IsCombinedDepthStencilFormat(descriptorSnapshot.Format) &&
				(descriptorSnapshot.Aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
			{
				bool stencilOnly = RequiresStencilOnlyDescriptor(binding);
				ImageAspectFlags aspectMask = stencilOnly ? ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit;
				string aspectLabel = stencilOnly ? "stencil-only" : "depth-only";
				if (source.TryGetDescriptorSnapshot(
						binding.ExpectedImageViewType,
						aspectMask,
						descriptorReason,
						allowSynchronousTextureUpload,
						out descriptorSnapshot) &&
					descriptorSnapshot.View.Handle != 0)
				{
					if (!Renderer.IsLiveImageViewBackedByLiveImage(descriptorSnapshot.View))
					{
						if (TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-retired-image-view", out imageInfo, source))
							return true;

						WarnOnce($"Texture for descriptor binding '{binding.Name}' references a retired Vulkan image view.");
						RecordDescriptorFailure(binding, "texture image view retired");
						return false;
					}

					if (!TryResolveDescriptorSampler(binding, descriptorType, in descriptorSnapshot, out Sampler sampler))
						return false;

					imageInfo = new DescriptorImageInfo
					{
						ImageLayout = Renderer.ResolveDescriptorImageLayout(source, in descriptorSnapshot, descriptorType),
						ImageView = descriptorSnapshot.View,
						Sampler = sampler,
					};
					string detail = $"{descriptorSnapshot.Format}/{descriptorSnapshot.Aspect}/{aspectLabel}";
					LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, detail);
					LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, detail, descriptorSnapshot);
					LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, detail, descriptorSnapshot);
					return true;
				}

				WarnOnce($"Texture for descriptor binding '{binding.Name}' uses a combined depth-stencil format and no {aspectLabel} view is available.");
				RecordDescriptorFailure(binding, $"combined depth-stencil texture has no {aspectLabel} view");
				return false;
			}

			if (descriptorSnapshot.View.Handle == 0)
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

			if (!Renderer.IsLiveImageViewBackedByLiveImage(descriptorSnapshot.View))
			{
				if (TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-retired-image-view", out imageInfo, source))
					return true;

				WarnOnce($"Texture for descriptor binding '{binding.Name}' references a retired Vulkan image view.");
				RecordDescriptorFailure(binding, "texture image view retired");
				return false;
			}

			if (!TryResolveDescriptorSampler(binding, descriptorType, in descriptorSnapshot, out Sampler descriptorSampler))
				return false;

			imageInfo = new DescriptorImageInfo
			{
				ImageLayout = Renderer.ResolveDescriptorImageLayout(source, in descriptorSnapshot, descriptorType),
				ImageView = descriptorSnapshot.View,
				Sampler = descriptorSampler,
			};
			string descriptorDetail = $"{descriptorSnapshot.Format}/{descriptorSnapshot.Aspect}";
			LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, descriptorDetail);
			LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, descriptorDetail, descriptorSnapshot);
			LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, descriptorDetail, descriptorSnapshot);
			return imageInfo.ImageView.Handle != 0;
		}

		private bool TryUsePlaceholderDescriptor(
			DescriptorBindingInfo binding,
			DescriptorType descriptorType,
			int arrayIndex,
			XRMaterial material,
			MaterialTextureBindingResolution textureBinding,
			XRTexture? texture,
			string reason,
			out DescriptorImageInfo imageInfo,
			IVkImageDescriptorSource? source = null)
		{
			imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
			if (imageInfo.ImageView.Handle == 0)
				return false;

			WarnOnce($"Texture for descriptor binding '{binding.Name}' is not ready for Vulkan descriptor use ({reason}). Using placeholder.");
			if (DescriptorTraceEnabled)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.Descriptor.Placeholder.{GetHashCode()}.{binding.Name}.{binding.Binding}.{arrayIndex}.{reason}",
					TimeSpan.FromSeconds(2),
					"[VulkanDescriptor] fallback={0} program='{1}' mesh='{2}' material='{3}' binding='{4}' set={5} bindingIndex={6} arrayIndex={7} texture='{8}' sourceImage=0x{9:X} sourceView=0x{10:X} imageInfoView=0x{11:X}",
					reason,
					_program?.Data?.Name ?? "<null>",
					Mesh?.Name ?? "<null>",
					material.Name ?? "<unnamed>",
					binding.Name ?? "<null>",
					binding.Set,
					binding.Binding,
					arrayIndex,
					texture?.Name ?? texture?.GetDescribingName() ?? "<null>",
					source?.DescriptorImage.Handle ?? 0,
					source?.DescriptorView.Handle ?? 0,
					imageInfo.ImageView.Handle);
			}

			LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, reason);
			LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, reason);
			LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, reason);
			RecordDescriptorFallback(binding);
			return true;
		}

		private bool TryResolveDescriptorSampler(
			DescriptorBindingInfo binding,
			DescriptorType descriptorType,
			in VkImageDescriptorSnapshot snapshot,
			out Sampler sampler)
		{
			sampler = default;
			if (descriptorType is not (DescriptorType.CombinedImageSampler or DescriptorType.Sampler))
				return true;

			sampler = snapshot.Sampler;
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
			string detail,
			VkImageDescriptorSnapshot? snapshot = null)
		{
			if (!DeferredLightingDiagnostics.Enabled || !DeferredLightingDiagnostics.IsDeferredLightCombineSampler(binding.Name))
				return;

			string textureLabel = texture is null
				? "<null>"
				: $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode():X8}";
			string programName = _program?.Data?.Name ?? "<null>";
			string meshName = Mesh?.Name ?? "<null>";
			string sourceImage = snapshot.HasValue ? $"0x{snapshot.Value.Image.Handle:X}" : source is null ? "<null>" : $"0x{source.DescriptorImage.Handle:X}";
			string sourceView = snapshot.HasValue ? $"0x{snapshot.Value.View.Handle:X}" : source is null ? "<null>" : $"0x{source.DescriptorView.Handle:X}";
			string sourceSampler = snapshot.HasValue ? $"0x{snapshot.Value.Sampler.Handle:X}" : source is null ? "<null>" : $"0x{source.DescriptorSampler.Handle:X}";
			string sourceLayout = snapshot.HasValue ? snapshot.Value.TrackedLayout.ToString() : source is null ? "<null>" : source.TrackedImageLayout.ToString();
			string sourceUsage = snapshot.HasValue ? snapshot.Value.Usage.ToString() : source is null ? "<null>" : source.DescriptorUsage.ToString();
			string sourceAllocator = snapshot.HasValue ? snapshot.Value.UsesAllocatorImage.ToString() : source is null ? "<null>" : source.UsesAllocatorImage.ToString();

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
			string detail,
			VkImageDescriptorSnapshot? snapshot = null)
		{
			if (!MaterialBindingDiagnosticsEnabled || !IsMaterialSampler(binding.Name))
				return;

			string textureLabel = texture is null
				? "<null>"
				: $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode():X8}";
			string sourceLayout = snapshot.HasValue ? snapshot.Value.TrackedLayout.ToString() : source is null ? "<null>" : source.TrackedImageLayout.ToString();
			string sourceUsage = snapshot.HasValue ? snapshot.Value.Usage.ToString() : source is null ? "<null>" : source.DescriptorUsage.ToString();
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

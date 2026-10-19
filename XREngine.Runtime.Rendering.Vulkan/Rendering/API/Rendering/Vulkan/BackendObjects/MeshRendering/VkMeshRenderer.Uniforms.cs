using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	private static readonly ConcurrentDictionary<(string Prefix, string Field), string> StructUniformFieldNames = new();
	private static readonly ConcurrentDictionary<(string Prefix, uint Index), string> IndexedUniformNames = new();
	private static int s_capturedAutoUniformWriteFailure;
	private static string? s_firstAutoUniformWriteFailure;
	private static int s_capturedAutoUniformParityMismatch;
	private static string? s_firstAutoUniformParityMismatch;
	private static int s_capturedDeferredLightingObjectWrite;
	private static string? s_firstDeferredLightingObjectWrite;
	private static int s_capturedDeferredLightingObjectBinding;
	private static string? s_firstDeferredLightingObjectBinding;
	private static int s_capturedDeferredLightingObjectGpuReadback;
	private static string? s_firstDeferredLightingObjectGpuReadback;

	/// <summary>
	/// Gets the first rejected auto-uniform write. This remains available in
	/// Release builds so an otherwise silent stale/default UBO cannot be
	/// mistaken for a successful reusable-frame refresh.
	/// </summary>
	public static string? GetFirstAutoUniformWriteFailure()
		=> Volatile.Read(ref s_firstAutoUniformWriteFailure);

	/// <summary>
	/// Gets the first validation-only packed/legacy auto-uniform mismatch. The
	/// value is retained in Release builds because it identifies the member that
	/// must be corrected before parity repair can be disabled.
	/// </summary>
	public static string? GetFirstAutoUniformParityMismatch()
		=> Volatile.Read(ref s_firstAutoUniformParityMismatch);

	/// <summary>
	/// Gets the first opt-in deferred-light object UBO write, including the
	/// captured LightData color/intensity and mapped arena coordinates.
	/// </summary>
	public static string? GetFirstDeferredLightingObjectWrite()
		=> Volatile.Read(ref s_firstDeferredLightingObjectWrite);

	/// <summary>
	/// Gets the matching opt-in descriptor base and dynamic offset selected for
	/// a deferred-light object UBO.
	/// </summary>
	public static string? GetFirstDeferredLightingObjectBinding()
		=> Volatile.Read(ref s_firstDeferredLightingObjectBinding);

	/// <summary>
	/// Gets the first accepted-submit GPU observation of the exact deferred-light
	/// object UBO range. This is populated only while deferred-light diagnostics
	/// are enabled.
	/// </summary>
	public static string? GetFirstDeferredLightingObjectGpuReadback()
		=> Volatile.Read(ref s_firstDeferredLightingObjectGpuReadback);

	/// <summary>
	/// Writes engine-uniform data into all active engine UBOs for the current frame.
	/// Called once per draw before descriptor binding.
	/// </summary>
	private void UpdateEngineUniformBuffersForDraw(int frameIndex, int drawUniformSlot, in PendingMeshDraw draw)
	{
		if (_engineUniformBuffers.Count == 0)
			return;

		foreach (var pair in _engineUniformBuffers)
		{
			EngineUniformBuffer[] buffers = pair.Value;
			if (buffers.Length == 0)
				continue;

			int idx = ResolveUniformBufferIndex(frameIndex, drawUniformSlot, buffers.Length);
			EngineUniformBuffer buffer = buffers[idx];
			if (buffer.Buffer.Handle == 0)
				continue;

			TryWriteEngineUniform(pair.Key, draw, buffer);
		}
	}

	/// <summary>
	/// Writes auto-uniform block data into all active auto UBOs for the current frame.
	/// Auto uniforms are populated from engine state, program overrides, and material parameters.
	/// </summary>
	private bool UpdateAutoUniformBuffersForDraw(
		int frameIndex,
		int drawUniformSlot,
		XRMaterial material,
		in PendingMeshDraw draw,
		out string failureReason,
		EVulkanBindingFrequencyMask frequencyMask =
			EVulkanBindingFrequencyMask.All)
	{
		failureReason = string.Empty;
		if (_program is null || _autoUniformBuffers.Count == 0)
		{
			LogGizmoAutoUniformBlocks(material, skipped: true);
			return true;
		}

		LogGizmoAutoUniformBlocks(material, skipped: false);

		foreach (KeyValuePair<string, AutoUniformBlockInfo> pair in _program.AutoUniformBlockMap)
		{
			// The reflected frequency controls which draw owns this range.
			// A shared range keeps the same bytes for all draws with that owner.
			string name = pair.Key;
			AutoUniformBlockInfo block = pair.Value;
			if (!IncludesBindingFrequency(
					frequencyMask,
					block.Frequency))
				continue;
			if (!_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? buffers) ||
				buffers.Length == 0)
			{
				// Reusable command refresh validates immutable descriptor state without
				// allocating it. A program can therefore be reactivated after its
				// auto-uniform views were retired; allocate the reflected block before
				// publishing bytes so every recording path shares this invariant.
				if (!EnsureAutoUniformBuffer(name, Math.Max(block.Size, 1u)) ||
					!_autoUniformBuffers.TryGetValue(name, out buffers) ||
					buffers.Length == 0)
				{
					return CaptureAutoUniformWriteFailure(
						block,
						draw,
						frameIndex,
						drawUniformSlot,
						BackendContext.Resources.MappedFrameArena,
						null,
						$"the reflected block has no allocated auto-uniform buffer ({_lastDescriptorPreparationFailure})",
						out failureReason);
				}
			}

			int idx = ResolveFrequencyOwnedAutoUniformBufferIndex(
				block,
				frameIndex,
				drawUniformSlot,
				material,
				draw,
				buffers.Length,
				out ulong ownerIdentity);
			VulkanFrequencyAutoUniformReservation? frequencyReservation = null;
			if (block.Frequency != EVulkanBindingFrequency.Unknown &&
				BackendContext.Resources.MappedFrameArena is not null)
			{
				VulkanMappedFrameArena? arena = BackendContext.Resources.MappedFrameArena;
				if (arena is null ||
					!arena.TryGetOrReserveFrequencyAutoUniformRange(
						this,
						frameIndex,
						drawUniformSlot,
						_program,
						block,
						ownerIdentity,
						out frequencyReservation,
						captureOwner: draw.CaptureOwnerToken) ||
					!arena.TryGetSlice(
						frameIndex,
						frequencyReservation,
						out VulkanMappedFrameSlice sharedSlice))
				{
					RuntimeEngine.Rendering.Stats.Vulkan
						.RecordVulkanDynamicUniformExhaustion();
					return CaptureAutoUniformWriteFailure(
						block,
						draw,
						frameIndex,
						drawUniformSlot,
						arena,
						frequencyReservation,
						"the frequency-owned mapped-frame range could not be reserved or resolved for this frame",
						out failureReason);
				}

				buffers[idx] = new AutoUniformBuffer(
					sharedSlice.Buffer,
					sharedSlice.Memory,
					block.Size,
					offset: frequencyReservation.Offset,
					ownsBuffer: false,
					mappedSlice: sharedSlice);
				IncludeAutoUniformBufferReplacement(name, buffers, buffers[idx]);
			}
			AutoUniformBuffer buffer = buffers[idx];
			if (buffer.Buffer.Handle == 0)
				return CaptureAutoUniformWriteFailure(
					block,
					draw,
					frameIndex,
					drawUniformSlot,
					BackendContext.Resources.MappedFrameArena,
					frequencyReservation,
					"the selected auto-uniform buffer has no native handle",
					out failureReason);

			if (!TryWriteAutoUniformBlock(
				block,
				buffer,
				idx,
				buffers.Length,
				frameIndex,
				drawUniformSlot,
				material,
				draw,
				frequencyReservation,
				out string writeFailureReason))
			{
				return CaptureAutoUniformWriteFailure(
					block,
					draw,
					frameIndex,
					drawUniformSlot,
					BackendContext.Resources.MappedFrameArena,
					frequencyReservation,
					writeFailureReason,
					out failureReason);
			}
		}

		return true;
	}

	/// <summary>
	/// Checks whether the active program's reflected auto-uniform views can be
	/// consumed by a command buffer that was recorded previously. Reuse must not
	/// allocate a replacement view: descriptor-heap bytes and descriptor-set
	/// bindings in that command were recorded against the prior native range.
	/// </summary>
	private bool HasReusableAutoUniformBuffers(out string reason)
	{
		reason = string.Empty;
		if (_program is null)
			return true;

		bool useFrameArena = BackendContext.Resources.MappedFrameArena is not null;
		int bufferCount = UniformBufferArrayLength;
		foreach (KeyValuePair<string, AutoUniformBlockInfo> pair in _program.AutoUniformBlockMap)
		{
			AutoUniformBlockInfo block = pair.Value;
			if (!_autoUniformBuffers.TryGetValue(pair.Key, out AutoUniformBuffer[]? buffers) ||
				!AutoUniformBuffersValid(
					pair.Key,
					buffers,
					bufferCount,
					Math.Max(block.Size, 1u),
					requireMappedPointers: !(useFrameArena && block.Frequency != EVulkanBindingFrequency.Unknown)))
			{
				reason = $"auto-uniform view '{pair.Key}' is absent or retired";
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Writes a reflected block to mapped memory. A cached plan updates only
	/// changed ranges. The legacy path writes the full block.
	/// </summary>
	private bool TryWriteAutoUniformBlock(
		AutoUniformBlockInfo block,
		AutoUniformBuffer buffer,
		int bufferIndex,
		int bufferCount,
		int frameIndex,
		int drawUniformSlot,
		XRMaterial material,
		in PendingMeshDraw draw,
		VulkanFrequencyAutoUniformReservation? frequencyReservation,
		out string failureReason)
	{
		failureReason = string.Empty;
		Span<byte> data = default;
		bool wrote = false;
		VulkanMappedFrameWriteScope mappedWrite = default;
		VulkanMappedMemoryWriteLease mappedMemoryWrite = default;
		try
		{
			// Hold the write lease until the block is complete. The arena can
			// reject writes after the frame range is sealed for submission.
			if (buffer.UsesMappedFrameArena)
			{
				VulkanMappedFrameArena? arena =
					BackendContext.Resources.MappedFrameArena;
				if (arena is null ||
					!arena.TryBeginWrite(buffer.MappedSlice, out mappedWrite))
				{
					failureReason = arena is null
						? "mapped-frame arena is unavailable for the auto-uniform write"
						: $"mapped-frame arena rejected the auto-uniform write lease: {arena.DescribeWriteRejection(buffer.MappedSlice)}";
					return false;
				}
				data = mappedWrite.Bytes;
			}
			else
			{
				VulkanMappedMemorySlice mappedMemorySlice = buffer.MappedMemorySlice;
				if (!buffer.UsesMappedMemoryLease ||
					!BackendContext.Resources.Buffers.TryAcquireWrite(BackendContext, in mappedMemorySlice, out mappedMemoryWrite))
				{
					failureReason = "mapped-memory buffer rejected the auto-uniform write lease";
					return false;
				}
				data = mappedMemoryWrite.Bytes;
			}
			EVulkanAutoUniformFallbackReason fallbackReason =
				EVulkanAutoUniformFallbackReason.BindingSnapshotIneligible;
			bool materialOwned =
				block.Frequency is EVulkanBindingFrequency.Unknown or
					EVulkanBindingFrequency.Material;
			ComputeDispatchSnapshot? bindingSnapshot =
				draw.ProgramBindingSnapshot;
			bool bindingSnapshotEligible =
				IsMaterialBindingSnapshotEligible(
					materialOwned,
					draw.ShadowUniformState.IsShadowPass,
					bindingSnapshot);
			if (bindingSnapshotEligible &&
				TryGetAutoUniformMaterialWritePlan(
					block,
					buffer.Size,
					material,
					bindingSnapshot,
					out AutoUniformMaterialWritePlan? plan,
					out fallbackReason) &&
				plan is not null)
			{
				int staticBytesCopied = 0;
				VulkanAutoUniformPublicationState[] publicationStates;
				int publicationStateIndex;
				if (frequencyReservation is not null)
				{
					publicationStates =
						frequencyReservation.PublicationStates;
					publicationStateIndex = Math.Clamp(
						frameIndex,
						0,
						publicationStates.Length - 1);
				}
				else
				{
					if (!_publishedAutoUniformMaterialWritePlans.TryGetValue(
							block.InstanceName,
							out publicationStates!) ||
						publicationStates.Length != bufferCount)
					{
						publicationStates =
							new VulkanAutoUniformPublicationState[bufferCount];
						_publishedAutoUniformMaterialWritePlans[
							block.InstanceName] = publicationStates;
					}
					publicationStateIndex = bufferIndex;
				}

				ref VulkanAutoUniformPublicationState publicationState =
					ref publicationStates[publicationStateIndex];
				bool hasMaterialOwnedStorage =
					block.Frequency is EVulkanBindingFrequency.Unknown or
						EVulkanBindingFrequency.Material;
				if (!publicationState.IsPlanPublished(plan))
				{
				// Initialize a new range with defaults and fixed material values.
				// Draw-dependent values belong to the frequency updates below.
					plan.StaticBytes.AsSpan().CopyTo(data);
					staticBytesCopied = plan.StaticBytes.Length;
					publicationState.PublishPlan(plan);
				}
				if (hasMaterialOwnedStorage)
				{
					RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFrequencyPublication(
						(int)EVulkanBindingFrequency.Material,
						published: staticBytesCopied > 0,
						staticBytesCopied);
				}

				int dynamicBytesCleared = 0;
				int dynamicOperationsWritten = 0;
				for (EVulkanBindingFrequency frequency =
						EVulkanBindingFrequency.Frame;
					 frequency < EVulkanBindingFrequency.Count;
					 frequency++)
				{
					VulkanAutoUniformFrequencyPlan frequencyPlan =
						plan.GetFrequencyPlan(frequency);
					ReadOnlySpan<VulkanAutoUniformBindingOperation> operations =
						frequencyPlan.Operations;
					if (operations.IsEmpty)
						continue;

					ulong generation = ComputeAutoUniformFrequencyGeneration(
						frequency,
						plan,
						draw,
						drawUniformSlot);
					ReadOnlySpan<VulkanAutoUniformDirtyRange> dirtyRanges =
						frequencyPlan.DirtyRanges;
					if (!publicationState.TryBeginFrequencyPublication(
							frequency,
							generation,
							dirtyRanges,
							buffer.Size))
					{
						RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFrequencyPublication(
							(int)frequency,
							published: false,
							publishedBytes: 0);
						continue;
					}

					int publishedBytes = 0;
				// Clear only changed ranges. Other frequencies can share this block,
				// and their published bytes must stay valid.
					for (int rangeIndex = 0;
						 rangeIndex <
							publicationState.PendingDirtyRangeCount;
						 rangeIndex++)
					{
						VulkanAutoUniformDirtyRange dirtyRange =
							publicationState.GetPendingDirtyRange(
								rangeIndex);
						data.Slice(
							checked((int)dirtyRange.Offset),
							checked((int)dirtyRange.Size)).Clear();
						dynamicBytesCleared += checked((int)dirtyRange.Size);
						publishedBytes += checked((int)dirtyRange.Size);
					}

					for (int operationIndex = 0;
						 operationIndex < operations.Length;
						 operationIndex++)
					{
						VulkanAutoUniformBindingOperation operation =
							operations[operationIndex];
						if (!TryWriteAutoUniformOperation(
								data,
								operation,
								material,
								draw,
								out EVulkanAutoUniformFallbackReason operationFallbackReason))
						{
							WarnAutoUniformFallback(
								block,
								operationFallbackReason,
								operation.Member.Name);
						// A partial typed write is not a valid published plan. Reset
						// its state before the full legacy write replaces the bytes.
							publicationState.Invalidate();
							RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFallbackReason(
								operationFallbackReason);
							wrote = WriteLegacyAutoUniformBlock(
								data,
								block,
								buffer.Size,
								material,
								draw);
							return wrote;
						}
						dynamicOperationsWritten++;
					}

					publicationState.CompleteFrequencyPublication(
						frequency,
						generation);
					RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFrequencyPublication(
						(int)frequency,
						published: true,
						publishedBytes);
				}
				if (!ValidateAutoUniformPayloadParity(
						data,
						block,
						buffer.Size,
						plan,
						material,
						draw,
						ref publicationState))
				{
					wrote = true;
					return true;
				}

				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFastWrite(
					staticBytesCopied,
					dynamicBytesCleared,
					dynamicOperationsWritten);

				wrote = true;
				return true;
			}

			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFallbackReason(
				fallbackReason);
			wrote = WriteLegacyAutoUniformBlock(data, block, buffer.Size, material, draw);
			return wrote;
		}
		finally
		{
			// An early return still releases the mapped write lease.
			if (wrote)
				CaptureDeferredLightingObjectWrite(
					block,
					buffer,
					frameIndex,
					drawUniformSlot,
					draw,
					data);
			mappedMemoryWrite.Dispose();
			mappedWrite.Dispose();
		}
	}

	private bool WriteLegacyAutoUniformBlock(
		Span<byte> data,
		AutoUniformBlockInfo block,
		uint bufferSize,
		XRMaterial material,
		in PendingMeshDraw draw)
	{
		data.Clear();
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformLegacyWrite(
			(int)bufferSize,
			block.Members.Count);
		WriteLegacyAutoUniformBlockData(data, block, bufferSize, material, draw);
		return true;
	}

	private void WriteLegacyAutoUniformBlockData(
		Span<byte> data,
		AutoUniformBlockInfo block,
		uint bufferSize,
		XRMaterial material,
		in PendingMeshDraw draw)
	{
		for (int memberIndex = 0; memberIndex < block.Members.Count; memberIndex++)
		{
			AutoUniformMember member = block.Members[memberIndex];
			if (member.Offset > bufferSize ||
				member.Size > bufferSize - member.Offset)
				continue;

			TryWriteAutoUniformMember(data, member, material, draw);
		}
	}

	private static readonly ConcurrentDictionary<string, string> NormalizedEngineUniformNames =
		new(StringComparer.Ordinal);
}

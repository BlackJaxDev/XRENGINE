using System;
using System.Buffers;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
	/// <summary>
	/// Records the first failed block write and returns the draw failure reason.
	/// </summary>
	private bool CaptureAutoUniformWriteFailure(
		AutoUniformBlockInfo block,
		in PendingMeshDraw draw,
		int frameIndex,
		int drawUniformSlot,
		VulkanMappedFrameArena? arena,
		VulkanFrequencyAutoUniformReservation? reservation,
		string reason,
		out string failureReason)
	{
		failureReason = reason;
		if (Interlocked.CompareExchange(ref s_capturedAutoUniformWriteFailure, 1, 0) != 0)
			return false;

		var targetBinding = RuntimeEngine.Rendering.State.RenderingPipelineState?
			.CurrentRenderTargetBinding;
		string target = targetBinding?.FrameBuffer?.Name ??
			targetBinding?.Name ?? "<unavailable>";
		string diagnostic = string.Format(
			System.Globalization.CultureInfo.InvariantCulture,
			"[Vulkan.AutoUniformWrite] first rejected write: block='{0}' program='{1}' slot={2} " +
			"frame={3} pass={4} target='{5}' viewPacket={6} shaders=[{7}] arenaGeneration={8} " +
			"reservationOffset={9} reservationSize={10} reservationFrequency={11} " +
			"reservationOwner={12} reason='{13}'.",
			block.InstanceName,
			_program?.Data?.Name ?? "<unavailable>",
			drawUniformSlot,
			frameIndex,
			RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
			target,
			draw.ViewSnapshot?.PacketId ?? 0UL,
			DescribeActiveProgramShaderPaths(),
			arena?.Generation ?? 0UL,
			reservation?.Offset ?? 0UL,
			reservation?.Size ?? 0U,
			reservation?.Key.Frequency.ToString() ?? "<none>",
			reservation?.Key.OwnerIdentity ?? 0UL,
			reason);
		diagnostic = string.Concat(diagnostic, Environment.NewLine, "Stack:", Environment.NewLine, Environment.StackTrace);
		Volatile.Write(ref s_firstAutoUniformWriteFailure, diagnostic);
		Debug.VulkanWarning("{0}", diagnostic);
		return false;
	}

	private string DescribeActiveProgramShaderPaths()
	{
		if (_program?.Data is not { } program || program.Shaders.Count == 0)
			return "<unavailable>";

		return string.Join(", ", program.Shaders.Select(
			static shader => $"{shader.Type}:{shader.Source.FilePath ?? "<memory>"}"));
	}

	/// <summary>
	/// Compares packed bytes with legacy bytes when validation is enabled.
	/// </summary>
	private bool ValidateAutoUniformPayloadParity(
		Span<byte> packed,
		AutoUniformBlockInfo block,
		uint bufferSize,
		AutoUniformMaterialWritePlan plan,
		XRMaterial material,
		in PendingMeshDraw draw,
		ref VulkanAutoUniformPublicationState publicationState)
	{
		if (!XREnvironment.IsEnabled(
				XREngineEnvironmentVariables.VulkanAutoUniformParity))
		{
			return true;
		}

		byte[] rented = ArrayPool<byte>.Shared.Rent(
			checked((int)bufferSize));
		try
		{
			Span<byte> legacy = rented.AsSpan(0, checked((int)bufferSize));
			legacy.Clear();
			WriteLegacyAutoUniformBlockData(
				legacy,
				block,
				bufferSize,
				material,
				draw);
			if (!VulkanAutoUniformParityValidator.TryFindMismatch(
					legacy,
					packed,
					plan.Schema,
					out VulkanAutoUniformParityMismatch mismatch))
			{
				return true;
			}

			RecordAutoUniformSchemaMismatch(
				EVulkanAutoUniformSchemaMismatchSite.Parity,
				block,
				bufferSize,
				plan.Schema,
				mismatch.SchemaEntry,
				mismatch.Frequency,
				mismatch.ByteOffset,
				mismatch.LegacyValue,
				mismatch.PackedValue);
			CaptureAutoUniformParityMismatch(block, bufferSize, mismatch);

			Debug.VulkanWarning(
				"[Vulkan.AutoUniformParity] mesh='{0}' program='{1}' " +
				"block='{2}' frequency={3} entry='{4}' offset={5} " +
				"legacy=0x{6:X2} packed=0x{7:X2}. " +
				"Using authoritative legacy bytes.",
				Mesh?.Name ?? "<unnamed>",
				_program?.Data?.Name ?? "<unavailable>",
				block.InstanceName,
				mismatch.Frequency,
				mismatch.SchemaEntry,
				mismatch.ByteOffset,
				mismatch.LegacyValue,
				mismatch.PackedValue);
			legacy.CopyTo(packed);
			publicationState.Invalidate();
			RuntimeEngine.Rendering.Stats.Vulkan
				.RecordVulkanAutoUniformFallbackReason(
					EVulkanAutoUniformFallbackReason.BindingSchemaMismatch);
			RuntimeEngine.Rendering.Stats.Vulkan
				.RecordVulkanAutoUniformLegacyWrite(
					checked((int)bufferSize),
					block.Members.Count);
			return false;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	private void CaptureAutoUniformParityMismatch(
		AutoUniformBlockInfo block,
		uint bufferSize,
		in VulkanAutoUniformParityMismatch mismatch)
	{
		if (Interlocked.CompareExchange(
				ref s_capturedAutoUniformParityMismatch,
				1,
				0) != 0)
		{
			return;
		}

		string diagnostic = string.Format(
			System.Globalization.CultureInfo.InvariantCulture,
			"[Vulkan.AutoUniformParity] first mismatch: mesh='{0}' program='{1}' " +
			"block='{2}' set={3} binding={4} size={5} blockFrequency={6} " +
			"entry='{7}' entryFrequency={8} offset={9} legacy=0x{10:X2} packed=0x{11:X2} shaders=[{12}].",
			Mesh?.Name ?? "<unnamed>",
			_program?.Data?.Name ?? "<unavailable>",
			block.InstanceName,
			block.Set,
			block.Binding,
			bufferSize,
			block.Frequency,
			mismatch.SchemaEntry,
			mismatch.Frequency,
			mismatch.ByteOffset,
			mismatch.LegacyValue,
			mismatch.PackedValue,
			DescribeActiveProgramShaderPaths());
		Volatile.Write(ref s_firstAutoUniformParityMismatch, diagnostic);
		Debug.VulkanWarning("{0}", diagnostic);
	}

	private static bool IsDeferredLightingObjectBlock(
		AutoUniformBlockInfo block)
	{
		if (!DeferredLightingDiagnostics.Enabled ||
			block.Frequency != EVulkanBindingFrequency.Object)
		{
			return false;
		}

		for (int index = 0; index < block.Members.Count; index++)
		{
			if (string.Equals(
					block.Members[index].Name,
					RuntimeEngine.Rendering.Constants.LightsStructName,
					StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private void CaptureDeferredLightingObjectWrite(
		AutoUniformBlockInfo block,
		AutoUniformBuffer buffer,
		int frameIndex,
		int drawUniformSlot,
		in PendingMeshDraw draw,
		ReadOnlySpan<byte> data)
	{
		if (!IsDeferredLightingObjectBlock(block) ||
			Interlocked.CompareExchange(
				ref s_capturedDeferredLightingObjectWrite,
				1,
				0) != 0)
		{
			return;
		}

		TryReadDeferredLightData(
			block,
			data,
			out Vector3 color,
			out float intensity,
			out bool hasPackedLightData);
		ComputeDispatchSnapshot? snapshot = draw.ProgramBindingSnapshot;
		bool hasCapturedColor = snapshot?.HasRuntimeUniform("LightData.Color") == true;
		bool hasCapturedIntensity =
			snapshot?.HasRuntimeUniform("LightData.DiffuseIntensity") == true;
		string diagnostic = string.Format(
			System.Globalization.CultureInfo.InvariantCulture,
			"[Vulkan.DeferredLightingObject] write program='{0}' block='{1}' frame={2} slot={3} " +
			"buffer=0x{4:X} offset={5} range={6} packedLightData={7} color=({8:R},{9:R},{10:R}) intensity={11:R} " +
			"capturedColor={12} capturedIntensity={13} snapshot={14}.",
			_program?.Data?.Name ?? "<unavailable>",
			block.InstanceName,
			frameIndex,
			drawUniformSlot,
			buffer.Buffer.Handle,
			buffer.Offset,
			buffer.Size,
			hasPackedLightData,
			color.X,
			color.Y,
			color.Z,
			intensity,
			hasCapturedColor,
			hasCapturedIntensity,
			snapshot is not null);
		Volatile.Write(ref s_firstDeferredLightingObjectWrite, diagnostic);
		DeferredLightingDiagnostics.Write(diagnostic);
		if (hasPackedLightData &&
			BackendContext.Resources.MappedFrameArena is { SupportsDiagnosticTransferSource: true } &&
			TryGetDeferredLightDataOffsets(block, out uint colorOffset, out uint intensityOffset))
		{
			uint range = Math.Min(buffer.Size, checked((uint)data.Length));
			CommandOperations.CaptureDeferredLightingObjectReadback(
				buffer.Buffer,
				GetResourceGeneration(ObjectType.Buffer, buffer.Buffer.Handle),
				buffer.Offset,
				range,
				frameIndex,
				drawUniformSlot,
				RuntimeHelpers.GetHashCode(this),
				_program?.BindingId ?? 0UL,
				ComputeDeferredLightingByteHash(data[..checked((int)range)]),
				color,
				intensity,
				colorOffset,
				intensityOffset);
		}
	}

	private void CaptureDeferredLightingObjectBinding(
		VkRenderProgram program,
		AutoUniformBlockInfo block,
		int frameIndex,
		int drawUniformSlot,
		ulong bufferHandle,
		ulong descriptorBaseOffset,
		uint range,
		uint dynamicOffset,
		CommandBuffer commandBuffer)
	{
		if (!IsDeferredLightingObjectBlock(block) ||
			Interlocked.CompareExchange(
				ref s_capturedDeferredLightingObjectBinding,
				1,
				0) != 0)
		{
			return;
		}

		string diagnostic = string.Format(
			System.Globalization.CultureInfo.InvariantCulture,
			"[Vulkan.DeferredLightingObject] descriptor program='{0}' block='{1}' set={2} binding={3} " +
			"frame={4} slot={5} buffer=0x{6:X} descriptorBaseOffset={7} dynamicOffset={8} range={9} finalOffset={10}.",
			program.Data.Name ?? "<unavailable>",
			block.InstanceName,
			block.Set,
			block.Binding,
			frameIndex,
			drawUniformSlot,
			bufferHandle,
			descriptorBaseOffset,
			dynamicOffset,
			range,
			checked(descriptorBaseOffset + dynamicOffset));
		Volatile.Write(ref s_firstDeferredLightingObjectBinding, diagnostic);
		DeferredLightingDiagnostics.Write(diagnostic);
		CommandOperations.ConfirmDeferredLightingObjectReadbackBinding(
			new Silk.NET.Vulkan.Buffer(bufferHandle),
			GetResourceGeneration(ObjectType.Buffer, bufferHandle),
			checked(descriptorBaseOffset + dynamicOffset),
			range,
			frameIndex,
			drawUniformSlot,
			RuntimeHelpers.GetHashCode(this),
			program.BindingId,
			commandBuffer);
	}

	internal static void PublishDeferredLightingObjectGpuReadback(string diagnostic)
	{
		if (Interlocked.CompareExchange(ref s_capturedDeferredLightingObjectGpuReadback, 1, 0) != 0)
			return;

		Volatile.Write(ref s_firstDeferredLightingObjectGpuReadback, diagnostic);
		DeferredLightingDiagnostics.Write(diagnostic);
	}

	private static void TryReadDeferredLightData(
		AutoUniformBlockInfo block,
		ReadOnlySpan<byte> data,
		out Vector3 color,
		out float intensity,
		out bool hasPackedLightData)
	{
		color = default;
		intensity = 0.0f;
		hasPackedLightData = false;
		for (int memberIndex = 0; memberIndex < block.Members.Count; memberIndex++)
		{
			AutoUniformMember member = block.Members[memberIndex];
			if (!string.Equals(member.Name, RuntimeEngine.Rendering.Constants.LightsStructName, StringComparison.Ordinal) ||
				member.StructMembers is not { } fields)
			{
				continue;
			}

			for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
			{
				AutoUniformMember field = fields[fieldIndex];
				int offset = checked((int)(member.Offset + field.Offset));
				if (string.Equals(field.Name, "Color", StringComparison.Ordinal) &&
					offset <= data.Length - 12)
				{
					color = MemoryMarshal.Read<Vector3>(data.Slice(offset, 12));
					hasPackedLightData = true;
				}
				else if (string.Equals(field.Name, "DiffuseIntensity", StringComparison.Ordinal) &&
					offset <= data.Length - sizeof(float))
				{
					intensity = MemoryMarshal.Read<float>(data.Slice(offset, sizeof(float)));
					hasPackedLightData = true;
				}
			}

			return;
		}
	}

	private static bool TryGetDeferredLightDataOffsets(
		AutoUniformBlockInfo block,
		out uint colorOffset,
		out uint intensityOffset)
	{
		colorOffset = uint.MaxValue;
		intensityOffset = uint.MaxValue;
		for (int memberIndex = 0; memberIndex < block.Members.Count; memberIndex++)
		{
			AutoUniformMember member = block.Members[memberIndex];
			if (!string.Equals(member.Name, RuntimeEngine.Rendering.Constants.LightsStructName, StringComparison.Ordinal) ||
				member.StructMembers is not { } fields)
			{
				continue;
			}

			for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
			{
				AutoUniformMember field = fields[fieldIndex];
				uint offset = checked(member.Offset + field.Offset);
				if (string.Equals(field.Name, "Color", StringComparison.Ordinal))
					colorOffset = offset;
				else if (string.Equals(field.Name, "DiffuseIntensity", StringComparison.Ordinal))
					intensityOffset = offset;
			}

			return colorOffset != uint.MaxValue && intensityOffset != uint.MaxValue;
		}

		return false;
	}

	private static ulong ComputeDeferredLightingByteHash(ReadOnlySpan<byte> bytes)
	{
		const ulong offsetBasis = 14695981039346656037UL;
		const ulong prime = 1099511628211UL;
		ulong hash = offsetBasis;
		for (int index = 0; index < bytes.Length; index++)
		{
			hash ^= bytes[index];
			hash *= prime;
		}
		return hash;
	}

	private void RecordAutoUniformSchemaMismatch(
		EVulkanAutoUniformSchemaMismatchSite site,
		AutoUniformBlockInfo block,
		uint bufferSize,
		VulkanAutoUniformBindingSchema schema,
		string? entryName,
		EVulkanBindingFrequency runtimeFrequency,
		int byteOffset = -1,
		byte legacyValue = 0,
		byte packedValue = 0)
	{
		AutoUniformBlockInfo schemaBlock = schema.Block;
		VulkanAutoUniformSchemaMismatchSample sample = new(
			site,
			_program?.Data?.Name,
			block.InstanceName,
			entryName,
			schema.ProgramLinkGeneration,
			block.Set,
			block.Binding,
			schemaBlock.Size,
			block.Size,
			bufferSize,
			ReferenceEquals(schemaBlock, block),
			(int)schemaBlock.Frequency,
			(int)runtimeFrequency,
			byteOffset,
			legacyValue,
			packedValue);
		RuntimeEngine.Rendering.Stats.Vulkan
			.RecordVulkanAutoUniformSchemaMismatch(sample);
	}

	private void WarnAutoUniformFallback(
		AutoUniformBlockInfo block,
		EVulkanAutoUniformFallbackReason reason,
		string? detail)
	{
		if (!_autoUniformWarnings.Add(
				(block.InstanceName, reason, detail)))
		{
			return;
		}

		Debug.VulkanWarning(
			"[Vulkan.AutoUniformFallback] mesh='{0}' program='{1}' block='{2}' " +
			"frequency={3} reason={4} detail='{5}'.",
			Mesh?.Name ?? "<unnamed>",
			_program?.Data?.Name ?? "<unavailable>",
			block.InstanceName,
			block.Frequency,
			reason,
			detail ?? string.Empty);
	}

	private void LogMaterialAutoUniform(
		AutoUniformMember member,
		XRMaterial material,
		string source,
		object? value,
		EShaderVarType type,
		bool wrote)
	{
		if (!MaterialBindingDiagnosticsEnabled || !IsMaterialAutoUniform(member.Name))
			return;

		Debug.MeshesWarningEvery(
			$"Vulkan.MaterialAutoUniform.{GetHashCode()}.{_program?.Data?.Name}.{material.Name}.{member.Name}",
			TimeSpan.FromSeconds(1),
			"[VkMaterialAutoUniform] program='{0}' mesh='{1}' material='{2}' member='{3}' type={4} source={5} wrote={6} offset={7} size={8} value={9}",
			_program?.Data?.Name ?? "<null>",
			Mesh?.Name ?? "<null>",
			material.Name ?? "<null>",
			member.Name,
			type,
			source,
			wrote,
			member.Offset,
			member.Size,
			FormatMaterialUniformDiagnosticValue(value));
	}

	private static bool IsMaterialAutoUniform(string name)
		=> name is "BaseColor" or "Opacity" or "Specular" or "Roughness" or "Metallic" or "Emission" or "AlphaCutoff"
		or "MatColor" or "LineWidth" or "ArrowHeadLengthPixels" or "ArrowHeadHalfWidthPixels"
		or "TextAtlasType" or "MsdfDistanceRange" or "MsdfDistanceRangeMiddle" or "MsdfFillBias" or "TextDebugMode" or "TextRenderLayer" or "TextRenderLayer_VTX"
		or "ModelMatrix" or "PrevModelMatrix" or "CurrViewProjection" or "PrevViewProjection"
		or "CurrViewProjectionStereo" or "PrevViewProjectionStereo";

	private bool IsGizmoDiagnosticProgram()
	{
		string? name = _program?.Data?.Name;
		return !string.IsNullOrWhiteSpace(name) &&
			(name.Contains("Gizmo", StringComparison.OrdinalIgnoreCase) ||
			 name.Contains("TransformTool", StringComparison.OrdinalIgnoreCase));
	}

	private void LogGizmoAutoUniformBlocks(XRMaterial material, bool skipped)
	{
		if (!MaterialBindingDiagnosticsEnabled || !IsGizmoDiagnosticProgram())
			return;

		Debug.MeshesWarningEvery(
			$"Vulkan.GizmoAutoUniformBlocks.{GetHashCode()}.{_program?.Data?.Name}.{material.Name}",
			TimeSpan.FromSeconds(1),
			"[VkGizmoAutoUniformBlocks] program='{0}' mesh='{1}' material='{2}' skipped={3} blockCount={4} bufferCount={5} blocks='{6}'",
			_program?.Data?.Name ?? "<null>",
			Mesh?.Name ?? "<null>",
			material.Name ?? "<null>",
			skipped,
			_program?.AutoUniformBlocks.Count ?? 0,
			_autoUniformBuffers.Count,
			FormatGizmoAutoUniformBlocks());
	}

	private string FormatGizmoAutoUniformBlocks()
		=> _program is null || _program.AutoUniformBlocks.Count == 0
			? "<none>"
			: string.Join("; ", _program.AutoUniformBlocks.Select(pair =>
				$"{pair.Key}[{string.Join(",", pair.Value.Members.Select(static member => member.Name))}]"));

	private static string FormatMaterialUniformDiagnosticValue(object? value)
		=> value switch
		{
			null => "<null>",
			float f => f.ToString("G4", System.Globalization.CultureInfo.InvariantCulture),
			int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
			uint u => u.ToString(System.Globalization.CultureInfo.InvariantCulture),
			Vector2 v => $"({v.X:G4},{v.Y:G4})",
			Vector3 v => $"({v.X:G4},{v.Y:G4},{v.Z:G4})",
			Vector4 v => $"({v.X:G4},{v.Y:G4},{v.Z:G4},{v.W:G4})",
			Matrix4x4 m => FormatMatrixDiagnosticValue(in m),
			Matrix4x4[] matrices => string.Join(",", matrices.Select(static m => FormatMatrixDiagnosticValue(in m))),
			ColorF3 c => $"({c.R:G4},{c.G:G4},{c.B:G4})",
			ColorF4 c => $"({c.R:G4},{c.G:G4},{c.B:G4},{c.A:G4})",
			_ => value.ToString() ?? "<null>",
		};

	private static string FormatMatrixDiagnosticValue(in Matrix4x4 matrix)
		=> $"[{matrix.M11:G4},{matrix.M12:G4},{matrix.M13:G4},{matrix.M14:G4};" +
		   $"{matrix.M21:G4},{matrix.M22:G4},{matrix.M23:G4},{matrix.M24:G4};" +
		   $"{matrix.M31:G4},{matrix.M32:G4},{matrix.M33:G4},{matrix.M34:G4};" +
		   $"{matrix.M41:G4},{matrix.M42:G4},{matrix.M43:G4},{matrix.M44:G4}]";
}

// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Uniforms.cs  – partial class: Uniform Buffer Management
//
// Allocates per-frame host-visible UBOs for engine and auto uniform blocks,
// writes typed values (scalars, vectors, matrices) into mapped buffer memory,
// and uploads legacy per-binding engine uniforms to Vulkan descriptor buffers.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	private readonly object _liveMeshUniformBuffersLock = new();
	private readonly Dictionary<ulong, DeviceMemory> _liveMeshUniformBuffers = new();

	internal void TrackMeshUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
	{
		if (buffer.Handle == 0)
			return;

		lock (_liveMeshUniformBuffersLock)
			_liveMeshUniformBuffers[(ulong)buffer.Handle] = memory;
	}

	internal void DestroyTrackedMeshUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
	{
		if (buffer.Handle == 0 && memory.Handle == 0)
			return;

		lock (_liveMeshUniformBuffersLock)
		{
			if (buffer.Handle != 0)
				_liveMeshUniformBuffers.Remove((ulong)buffer.Handle);
		}

		if (buffer.Handle != 0)
			RetireBuffer(buffer, memory);
		else if (memory.Handle != 0)
			RetireBuffer(default, memory);
	}

	private void DestroyRemainingTrackedMeshUniformBuffers()
	{
		KeyValuePair<ulong, DeviceMemory>[] remaining;
		lock (_liveMeshUniformBuffersLock)
		{
			if (_liveMeshUniformBuffers.Count == 0)
				return;

			remaining = [.. _liveMeshUniformBuffers];
			_liveMeshUniformBuffers.Clear();
		}

		foreach (KeyValuePair<ulong, DeviceMemory> entry in remaining)
		{
			Silk.NET.Vulkan.Buffer buffer = new() { Handle = entry.Key };
			DestroyBufferRaw(buffer, entry.Value);
		}
	}

	public partial class VkMeshRenderer
	{
		#region Uniform Buffer Allocation

		private int UniformBufferSlotCount => Math.Max(_uniformDrawSlotCapacity, 1);

		private int UniformBufferFrameCount => Math.Max(Renderer.DescriptorFrameSlotFrameCount, 1);

		private int UniformBufferArrayLength => UniformBufferFrameCount * UniformBufferSlotCount;

		internal void EnsureUniformDrawSlotCapacity(int requiredSlots)
		{
			requiredSlots = Math.Max(requiredSlots, 1);
			if (requiredSlots <= _uniformDrawSlotCapacity)
				return;

			int newCapacity = Math.Max(_uniformDrawSlotCapacity, 1);
			while (newCapacity < requiredSlots)
				newCapacity <<= 1;

			// Descriptor allocations are already keyed by frame/slot count. Keep the old
			// allocation and uniform-buffer generations live: a desktop or eye command
			// buffer may have completed recording and still be waiting to submit while a
			// different output discovers a larger repeated-renderer count. Retiring the old
			// generation here makes that otherwise valid command buffer fail lifetime
			// validation. The geometric capacity growth bounds retained storage to less than
			// the current generation, and a matching generation is reused if the frame layout
			// is encountered again.
			_uniformDrawSlotCapacity = newCapacity;
			_descriptorDirty = true;
			Renderer.MarkCommandBuffersDirtyForLegacyMeshState();
		}

		private int ResolveUniformBufferIndex(int frameIndex, int drawUniformSlot, int bufferCount)
		{
			if (bufferCount <= 1)
				return 0;

			int frame = Math.Clamp(frameIndex, 0, UniformBufferFrameCount - 1);
			int slot = Math.Clamp(drawUniformSlot, 0, UniformBufferSlotCount - 1);
			int index = frame * UniformBufferSlotCount + slot;
			return Math.Clamp(index, 0, bufferCount - 1);
		}

		/// <summary>
		/// Ensures per-frame/per-draw-slot engine uniform buffers exist and are large enough.
		/// Destroys and recreates if the frame count, slot count, or size has changed.
		/// </summary>
		private bool EnsureEngineUniformBuffer(string name, uint size)
		{
			size = Math.Max(size, 1u);
			int bufferCount = UniformBufferArrayLength;
			if (_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? existing))
			{
				bool valid = EngineUniformBuffersValid(existing, bufferCount, size);
				if (valid)
					return true;

				RetainEngineUniformBufferGeneration(name, existing);
				_engineUniformBuffers.Remove(name);
			}

			if (_retainedEngineUniformBufferGenerations.TryTake(name, bufferCount, size, out EngineUniformBuffer[] retained))
			{
				_engineUniformBuffers[name] = retained;
				return true;
			}

			EngineUniformBuffer[] buffers = new EngineUniformBuffer[bufferCount];
			BufferUsageFlags usage = string.Equals(name, FallbackDescriptorUniformName, StringComparison.Ordinal)
				? BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit
				: BufferUsageFlags.UniformBufferBit;
			ulong stride = ResolveUniformBufferStride(size);
			if (!TryComputeUniformBufferByteSize(stride, bufferCount, out ulong totalSize) ||
				!CreateHostVisibleBuffer(totalSize, usage, out var buffer, out var memory))
			{
				return false;
			}

			if (!Renderer.TryMapBufferMemory(buffer, memory, 0, totalSize, out void* mappedPtr))
			{
				Renderer.DestroyTrackedMeshUniformBuffer(buffer, memory);
				return false;
			}

			for (int i = 0; i < bufferCount; i++)
			{
				ulong offset = stride * (ulong)i;
				void* slotPtr = (byte*)mappedPtr + checked((nint)offset);
				buffers[i] = new EngineUniformBuffer(buffer, memory, size, slotPtr, offset, ownsBuffer: i == 0);
			}

			_engineUniformBuffers[name] = buffers;
			return true;
		}

		/// <summary>
		/// Ensures per-frame/per-draw-slot auto uniform buffers exist and are large enough.
		/// Destroys and recreates if the frame count, slot count, or size has changed.
		/// </summary>
		private bool EnsureAutoUniformBuffer(string name, uint size)
		{
			size = Math.Max(size, 1u);
			int bufferCount = UniformBufferArrayLength;
			if (_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? existing))
			{
				bool valid = AutoUniformBuffersValid(existing, bufferCount, size);
				if (valid)
					return true;

				RetainAutoUniformBufferGeneration(name, existing);
				_autoUniformBuffers.Remove(name);
			}

			if (_retainedAutoUniformBufferGenerations.TryTake(name, bufferCount, size, out AutoUniformBuffer[] retained))
			{
				_autoUniformBuffers[name] = retained;
				return true;
			}

			AutoUniformBuffer[] buffers = new AutoUniformBuffer[bufferCount];
			ulong stride = ResolveUniformBufferStride(size);
			if (!TryComputeUniformBufferByteSize(stride, bufferCount, out ulong totalSize) ||
				!CreateHostVisibleBuffer(totalSize, BufferUsageFlags.UniformBufferBit, out var buffer, out var memory))
			{
				return false;
			}

			if (!Renderer.TryMapBufferMemory(buffer, memory, 0, totalSize, out void* mappedPtr))
			{
				Renderer.DestroyTrackedMeshUniformBuffer(buffer, memory);
				return false;
			}

			for (int i = 0; i < bufferCount; i++)
			{
				ulong offset = stride * (ulong)i;
				void* slotPtr = (byte*)mappedPtr + checked((nint)offset);
				buffers[i] = new AutoUniformBuffer(buffer, memory, size, slotPtr, offset, ownsBuffer: i == 0);
			}

			_autoUniformBuffers[name] = buffers;
			return true;
		}

		private ulong ResolveUniformBufferStride(uint size)
		{
			ulong alignment = Math.Max(Renderer._uniformBufferOffsetAlignment, 1UL);
			ulong value = Math.Max(size, 1u);
			ulong remainder = value % alignment;
			return remainder == 0 ? value : value + alignment - remainder;
		}

		private static bool TryComputeUniformBufferByteSize(ulong stride, int bufferCount, out ulong totalSize)
		{
			totalSize = 0;
			if (bufferCount <= 0 || stride == 0)
				return false;

			ulong count = (ulong)bufferCount;
			if (stride > ulong.MaxValue / count)
				return false;

			totalSize = stride * count;
			return true;
		}

		private static bool EngineUniformBuffersValid(EngineUniformBuffer[] buffers, int expectedCount, uint requiredSize)
		{
			if (buffers.Length != expectedCount)
				return false;

			for (int i = 0; i < buffers.Length; i++)
			{
				if (buffers[i].Buffer.Handle == 0 || buffers[i].MappedPtr == null || buffers[i].Size < requiredSize)
					return false;
			}

			return true;
		}

		private static bool AutoUniformBuffersValid(AutoUniformBuffer[] buffers, int expectedCount, uint requiredSize)
		{
			if (buffers.Length != expectedCount)
				return false;

			for (int i = 0; i < buffers.Length; i++)
			{
				if (buffers[i].Buffer.Handle == 0 || buffers[i].MappedPtr == null || buffers[i].Size < requiredSize)
					return false;
			}

			return true;
		}

		/// <summary>
		/// Allocates a host-visible, host-coherent Vulkan buffer with the given usage flags.
		/// Used for engine and auto uniform buffers that are updated every frame via map/unmap.
		/// </summary>
		private bool CreateHostVisibleBuffer(ulong size, BufferUsageFlags usage, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
		{
			buffer = default;
			memory = default;
			size = Math.Max(size, 1UL);
			bool enableDeviceAddress = Renderer.IsDescriptorHeapDrawBindingActive;
			if (enableDeviceAddress)
				usage |= BufferUsageFlags.ShaderDeviceAddressBit;

			MemoryPropertyFlags props = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
			try
			{
				(buffer, memory) = Renderer.CreateBufferRaw(size, usage, props, enableDeviceAddress);
				Renderer.TrackMeshUniformBuffer(buffer, memory);
				return true;
			}
			catch (Exception ex)
			{
				WarnOnce($"Failed to create engine uniform buffer '{size}' bytes: {ex.Message}");
				buffer = default;
				memory = default;
				return false;
			}
		}

		#endregion // Uniform Buffer Allocation

		#region Uniform Buffer Updates

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
		private void UpdateAutoUniformBuffersForDraw(int frameIndex, int drawUniformSlot, XRMaterial material, in PendingMeshDraw draw)
		{
			if (_program is null || _autoUniformBuffers.Count == 0)
			{
				LogGizmoAutoUniformBlocks(material, skipped: true);
				return;
			}

			LogGizmoAutoUniformBlocks(material, skipped: false);

			foreach (var pair in _program.AutoUniformBlocks)
			{
				string name = pair.Key;
				AutoUniformBlockInfo block = pair.Value;
				if (!_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? buffers) || buffers.Length == 0)
					continue;

				int idx = ResolveUniformBufferIndex(frameIndex, drawUniformSlot, buffers.Length);
				AutoUniformBuffer buffer = buffers[idx];
				if (buffer.Buffer.Handle == 0)
					continue;

				TryWriteAutoUniformBlock(block, buffer, material, draw);
			}
		}

		/// <summary>
		/// Clears an auto uniform buffer and writes each member from engine state,
		/// program overrides, and material parameters.
		/// </summary>
		private bool TryWriteAutoUniformBlock(AutoUniformBlockInfo block, AutoUniformBuffer buffer, XRMaterial material, in PendingMeshDraw draw)
		{
			if (buffer.MappedPtr == null)
				return false;

			Span<byte> data = new(buffer.MappedPtr, (int)buffer.Size);
			data.Clear();

			foreach (AutoUniformMember member in block.Members)
			{
				if (member.Offset + member.Size > buffer.Size)
					continue;

				TryWriteAutoUniformMember(data, member, material, draw);
			}

			return true;
		}

		/// <summary>
		/// Attempts to write a single auto uniform member. Resolution priority:
		/// engine uniform value > program override > material parameter > array defaults > default value.
		/// </summary>
		private bool TryWriteAutoUniformMember(Span<byte> data, AutoUniformMember member, XRMaterial material, in PendingMeshDraw draw)
		{
			if (member.StructMembers is { Count: > 0 })
				return TryWriteStructUniformValue(data, member, member.Name, member.Offset);

			bool wrote;

			if (TryResolveEngineUniformValue(member.Name, draw, out object? engineValue, out EShaderVarType engineType))
			{
				wrote = engineValue is not null && TryWriteAutoUniformValue(data, member, engineValue, engineType);
				LogMaterialAutoUniform(member, material, "engine", engineValue, engineType, wrote);
				return wrote;
			}

			if (_program is not null && _program.TryGetUniformValue(member.Name, out ProgramUniformValue programValue))
			{
				wrote = TryWriteProgramUniformValue(data, member, programValue);
				LogMaterialAutoUniform(member, material, "program", programValue.Value, programValue.Type, wrote);
				return wrote;
			}

			if (member.IsArray && TryWriteIndexedProgramUniformArray(data, member, member.Name))
				return true;

			ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
			if (parameter is not null)
			{
				if (member.IsArray)
				{
					wrote = TryWriteAutoUniformArray(data, member, parameter);
					LogMaterialAutoUniform(member, material, "material-array", parameter.GenericValue, parameter.TypeName, wrote);
					return wrote;
				}

				wrote = TryWriteAutoUniformValue(data, member, parameter.GenericValue, parameter.TypeName);
				LogMaterialAutoUniform(member, material, "material", parameter.GenericValue, parameter.TypeName, wrote);
				return wrote;
			}

			if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
			{
				wrote = TryWriteAutoUniformArrayDefaults(data, member);
				LogMaterialAutoUniform(member, material, "default-array", $"count={member.DefaultArrayValues.Count}", member.EngineType ?? EShaderVarType._float, wrote);
				return wrote;
			}

			if (member.DefaultValue is { } defaultValue)
			{
				wrote = TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);
				LogMaterialAutoUniform(member, material, "default", defaultValue.Value, defaultValue.Type, wrote);
				return wrote;
			}

			LogMaterialAutoUniform(member, material, "missing", null, member.EngineType ?? EShaderVarType._float, false);
			return false;
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
			or "TextAtlasType" or "MsdfDistanceRange" or "MsdfDistanceRangeMiddle" or "MsdfFillBias" or "TextDebugMode" or "TextRenderLayer" or "TextRenderLayer_VTX";

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
				ColorF3 c => $"({c.R:G4},{c.G:G4},{c.B:G4})",
				ColorF4 c => $"({c.R:G4},{c.G:G4},{c.B:G4},{c.A:G4})",
				_ => value.ToString() ?? "<null>",
			};

		private bool TryWriteStructUniformValue(Span<byte> data, AutoUniformMember member, string uniformPrefix, uint baseOffset)
		{
			if (member.StructMembers is not { Count: > 0 } fields)
				return false;

			bool wroteAny = false;
			foreach (AutoUniformMember field in fields)
			{
				uint fieldOffset = baseOffset + field.Offset;
				string fieldName = $"{uniformPrefix}.{field.Name}";
				AutoUniformMember absoluteField = field with { Offset = fieldOffset };

				if (field.StructMembers is { Count: > 0 })
				{
					if (field.IsArray)
						wroteAny |= TryWriteStructUniformArray(data, absoluteField, fieldName);
					else
						wroteAny |= TryWriteStructUniformValue(data, absoluteField, fieldName, fieldOffset);
					continue;
				}

				if (field.IsArray)
				{
					wroteAny |= TryWriteProgramUniformArray(data, absoluteField, fieldName);
					continue;
				}

				if (_program is not null && _program.TryGetUniformValue(fieldName, out ProgramUniformValue fieldValue))
				{
					wroteAny |= TryWriteProgramUniformValue(data, absoluteField, fieldValue);
					continue;
				}

				if (field.DefaultValue is { } defaultValue)
					wroteAny |= TryWriteAutoUniformValue(data, absoluteField, defaultValue.Value, defaultValue.Type);
			}

			return wroteAny;
		}

		private bool TryWriteStructUniformArray(Span<byte> data, AutoUniformMember member, string uniformPrefix)
		{
			if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
				return false;

			bool wroteAny = false;
			for (uint i = 0; i < member.ArrayLength; i++)
			{
				uint elementOffset = member.Offset + i * member.ArrayStride;
				AutoUniformMember element = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
				wroteAny |= TryWriteStructUniformValue(data, element, $"{uniformPrefix}[{i}]", elementOffset);
			}

			return wroteAny;
		}

        private bool TryWriteProgramUniformArray(Span<byte> data, AutoUniformMember member, string uniformName)
			=> _program is not null && _program.TryGetUniformValue(uniformName, out ProgramUniformValue programValue)
                ? TryWriteProgramUniformValue(data, member, programValue)
                : TryWriteIndexedProgramUniformArray(data, member, uniformName);

        private bool TryWriteIndexedProgramUniformArray(Span<byte> data, AutoUniformMember member, string uniformName)
		{
			if (_program is null || !member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
				return false;

			bool wroteAny = false;
			for (uint i = 0; i < member.ArrayLength; i++)
			{
				string elementName = $"{uniformName}[{i}]";
				if (!_program.TryGetUniformValue(elementName, out ProgramUniformValue elementValue) || elementValue.IsArray || elementValue.Value is Array)
					continue;

				uint elementOffset = member.Offset + i * member.ArrayStride;
				AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
				wroteAny |= TryWriteAutoUniformValue(data, elementMember, elementValue.Value, elementValue.Type);
			}

			return wroteAny;
		}

		/// <summary>Writes a program uniform value into auto uniform buffer memory (scalar or array).</summary>
		private bool TryWriteProgramUniformValue(Span<byte> data, AutoUniformMember member, ProgramUniformValue value)
		{
			if (member.IsArray)
			{
				if (!value.IsArray || value.Value is not Array array || member.ArrayStride == 0 || member.ArrayLength == 0)
					return false;

				int max = Math.Min(array.Length, (int)member.ArrayLength);
				for (int i = 0; i < max; i++)
				{
					object? element = array.GetValue(i);
					if (element is null)
						continue;

					uint elementOffset = member.Offset + (uint)i * member.ArrayStride;
					AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
					TryWriteAutoUniformValue(data, elementMember, element, value.Type);
				}

				return true;
			}

			if (value.IsArray || value.Value is Array)
				return false;

			return TryWriteAutoUniformValue(data, member, value.Value, value.Type);
		}

		/// <summary>Writes an array-typed material parameter into auto uniform buffer memory.</summary>
		private bool TryWriteAutoUniformArray(Span<byte> data, AutoUniformMember member, ShaderVar parameter)
		{
			if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
				return false;

			if (parameter.GenericValue is not IUniformableArray array)
				return false;

			var valuesProp = array.GetType().GetProperty("Values");
			if (valuesProp?.GetValue(array) is not Array values)
				return false;

			uint stride = member.ArrayStride;
			uint baseOffset = member.Offset;
			int max = (int)Math.Min((uint)values.Length, member.ArrayLength);

			for (int i = 0; i < max; i++)
			{
				if (values.GetValue(i) is not ShaderVar element)
					continue;

				uint elementOffset = baseOffset + (uint)i * stride;
				AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
				TryWriteAutoUniformValue(data, elementMember, element.GenericValue, element.TypeName);
			}

			return true;
		}

		/// <summary>Writes default array values into auto uniform buffer memory when no runtime data is available.</summary>
		private bool TryWriteAutoUniformArrayDefaults(Span<byte> data, AutoUniformMember member)
		{
			if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
				return false;

			if (member.DefaultArrayValues is null || member.DefaultArrayValues.Count == 0)
				return false;

			uint stride = member.ArrayStride;
			uint baseOffset = member.Offset;
			int max = (int)Math.Min((uint)member.DefaultArrayValues.Count, member.ArrayLength);

			for (int i = 0; i < max; i++)
			{
				AutoUniformDefaultValue def = member.DefaultArrayValues[i];
				uint elementOffset = baseOffset + (uint)i * stride;
				AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
				TryWriteAutoUniformValue(data, elementMember, def.Value, def.Type);
			}

			return true;
		}

		/// <summary>
		/// Resolves an engine uniform value by name from the current rendering state.
		/// Handles matrices, camera properties, screen dimensions, UI bounds, etc.
		/// Engine-owned values come from the draw snapshot; UI bounds may still come
		/// from program-level overrides because they are authored per program.
		/// </summary>
		private bool TryResolveEngineUniformValue(string name, in PendingMeshDraw draw, out object? value, out EShaderVarType type)
		{
			value = null;
			type = EShaderVarType._float;
			string normalized = NormalizeEngineUniformName(name);
			// Use camera state captured at enqueue time; by the time the command
			// buffer is recorded the pipeline camera stack has already been popped.
			XRCamera? camera = draw.Camera;
			bool stereoPass = draw.IsStereoPass;

			Matrix4x4 prevModelMatrix = draw.PreviousModelMatrix;
			if (IsApproximatelyIdentity(prevModelMatrix) && !IsApproximatelyIdentity(draw.ModelMatrix))
				prevModelMatrix = draw.ModelMatrix;

			Matrix4x4 inverseModel = Matrix4x4.Identity;
			Matrix4x4.Invert(draw.ModelMatrix, out inverseModel);

			// Camera matrices/vectors come from the draw snapshot captured at enqueue time.
			// Reading live camera state here can be stale because the pipeline camera stack
			// has already been popped.
			Matrix4x4 viewMatrix = draw.ViewMatrix;
			Matrix4x4 inverseViewMatrix = draw.InverseViewMatrix;
			Matrix4x4 projMatrix = draw.ProjectionMatrix;
			Matrix4x4 inverseProjMatrix = draw.InverseProjectionMatrix;
			Matrix4x4 rightEyeViewMatrix = draw.RightEyeViewMatrix;
			Matrix4x4 rightEyeProjMatrix = draw.RightEyeProjectionMatrix;
			Matrix4x4 rightEyeInverseProjMatrix = draw.RightEyeInverseProjectionMatrix;

			switch (normalized)
			{
				case nameof(EEngineUniform.UpdateDelta):
					value = RuntimeEngine.Time.Timer.Update.Delta;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.RenderTime):
					value = this.Renderer._materialUniformSecondsLive;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.EngineTime):
					value = RuntimeEngine.ElapsedTime;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.DeltaTime):
					value = RuntimeEngine.Time.Timer.Render.Delta;
					type = EShaderVarType._float;
					return true;
				case TransformIdUniformName:
					value = draw.TransformId;
					type = EShaderVarType._uint;
					return true;
				case SkinPaletteBaseUniformName:
					value = MeshRenderer.ActiveSkinPaletteBase;
					type = EShaderVarType._uint;
					return true;
				case SkinPaletteCountUniformName:
					value = MeshRenderer.ActiveSkinPaletteCount;
					type = EShaderVarType._uint;
					return true;
				case SkinningInfluenceCapUniformName:
					value = MeshRenderer.ActiveSkinningInfluenceCap;
					type = EShaderVarType._int;
					return true;
				case BlendshapeActiveCountUniformName:
					value = MeshRenderer.ActiveBlendshapeCount;
					type = EShaderVarType._int;
					return true;
				case BlendshapeWeightThresholdUniformName:
					value = MeshRenderer.BlendshapeActiveWeightThreshold;
					type = EShaderVarType._float;
					return true;
				case UsePrecombinedBlendshapeDeltasUniformName:
					value = RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass
						&& !RuntimeEngine.Rendering.State.IsVulkan
						&& MeshRenderer.HasValidPrecombinedBlendshapeDeltas
							? 1
							: 0;
					type = EShaderVarType._int;
					return true;
				case nameof(EEngineUniform.ModelMatrix):
					value = draw.ModelMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.PrevModelMatrix):
					value = prevModelMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.RootInvModelMatrix):
					value = inverseModel;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.ViewMatrix):
				case nameof(EEngineUniform.LeftEyeViewMatrix):
					value = viewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.PrevViewMatrix):
				case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
					value = draw.PreviousViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.RightEyeViewMatrix):
					value = rightEyeViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.PrevRightEyeViewMatrix):
					value = draw.PreviousRightEyeViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.InverseViewMatrix):
					value = inverseViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.InverseProjMatrix):
					value = inverseProjMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.ViewProjectionMatrix):
					value = draw.ViewProjectionMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.ProjMatrix):
					value = projMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.PrevProjMatrix):
				case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
					value = draw.PreviousProjectionMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.PrevRightEyeProjMatrix):
					value = draw.PreviousRightEyeProjectionMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.LeftEyeViewProjectionMatrix):
					value = draw.ViewProjectionMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
					value = inverseViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.LeftEyeInverseProjMatrix):
					value = inverseProjMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.RightEyeInverseViewMatrix):
					value = draw.RightEyeInverseViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.RightEyeInverseProjMatrix):
					value = rightEyeInverseProjMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.RightEyeViewProjectionMatrix):
					value = draw.RightEyeViewProjectionMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.LeftEyeProjMatrix):
					value = projMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.RightEyeProjMatrix):
					value = rightEyeProjMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.CameraPosition):
					value = ToVector4(draw.CameraPosition);
					type = EShaderVarType._vec4;
					return true;
				case nameof(EEngineUniform.CameraForward):
					value = ToVector4(draw.CameraForward);
					type = EShaderVarType._vec4;
					return true;
				case nameof(EEngineUniform.CameraUp):
					value = ToVector4(draw.CameraUp);
					type = EShaderVarType._vec4;
					return true;
				case nameof(EEngineUniform.CameraRight):
					value = ToVector4(draw.CameraRight);
					type = EShaderVarType._vec4;
					return true;
				case nameof(EEngineUniform.CameraNearZ):
					value = camera?.NearZ ?? 0f;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.CameraFarZ):
					value = camera?.FarZ ?? 0f;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.CameraFovX):
					value = camera?.Parameters is XRPerspectiveCameraParameters persp ? persp.HorizontalFieldOfView : 0f;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.CameraFovY):
					value = camera?.Parameters is XRPerspectiveCameraParameters perspY ? perspY.VerticalFieldOfView : 0f;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.CameraAspect):
					value = camera?.Parameters is XRPerspectiveCameraParameters perspA ? perspA.AspectRatio : 0f;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.DepthMode):
					value = (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal);
					type = EShaderVarType._int;
					return true;
				case nameof(EEngineUniform.ClipSpaceYDirection):
					value = (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection;
					type = EShaderVarType._int;
					return true;
				case nameof(EEngineUniform.ClipDepthRange):
					value = (int)RuntimeEngine.Rendering.EffectiveClipDepthRange;
					type = EShaderVarType._int;
					return true;
				case nameof(EEngineUniform.FramebufferTextureYDirection):
					value = (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan);
					type = EShaderVarType._int;
					return true;
				case nameof(EEngineUniform.ScreenWidth):
				case nameof(EEngineUniform.ScreenHeight):
					// Resolve from the render-area snapshotted at enqueue time. Reading the live
					// RuntimeEngine.Rendering.State.RenderArea here (deferred record time) yields
					// 0 because the pipeline render-region stack has already been popped, which
					// would collapse the debug-line geometry-shader viewport to (1,1) and
					// explode every line into a screen-spanning quad.
					float screenW = draw.RenderAreaWidth;
					float screenH = draw.RenderAreaHeight;
					if (screenW <= 0f || screenH <= 0f)
					{
						screenW = draw.Viewport.Width;
						screenH = MathF.Abs(draw.Viewport.Height);
					}
					if (screenW <= 0f || screenH <= 0f)
					{
						var area = RuntimeEngine.Rendering.State.RenderArea;
						screenW = area.Width;
						screenH = area.Height;
					}
					value = normalized.Equals(nameof(EEngineUniform.ScreenWidth), StringComparison.Ordinal) ? screenW : screenH;
					type = EShaderVarType._float;
					return true;
				case nameof(EEngineUniform.ScreenOrigin):
					value = new Vector2(0f, 0f);
					type = EShaderVarType._vec2;
					return true;
				case nameof(EEngineUniform.BillboardMode):
					value = (int)draw.BillboardMode;
					type = EShaderVarType._int;
					return true;
				case nameof(EEngineUniform.VRMode):
					value = stereoPass ? 1 : 0;
					type = EShaderVarType._int;
					return true;
				case nameof(EEngineUniform.UIXYWH):
					if (_program is not null && _program.TryGetUniformValue(nameof(EEngineUniform.UIXYWH), out ProgramUniformValue bounds))
					{
						value = bounds.Value;
						type = bounds.Type;
						return true;
					}
					value = Vector4.Zero;
					type = EShaderVarType._vec4;
					return true;
				case nameof(EEngineUniform.UIWidth):
				case nameof(EEngineUniform.UIHeight):
				case nameof(EEngineUniform.UIX):
				case nameof(EEngineUniform.UIY):
					if (_program is not null && _program.TryGetUniformValue(normalized, out ProgramUniformValue uiScalar))
					{
						value = uiScalar.Value;
						type = uiScalar.Type;
						return true;
					}
					if (_program is not null && _program.TryGetUniformValue(nameof(EEngineUniform.UIXYWH), out ProgramUniformValue uiBounds) &&
						uiBounds.Value is Vector4 b)
					{
						value = normalized switch
						{
							nameof(EEngineUniform.UIX) => b.X,
							nameof(EEngineUniform.UIY) => b.Y,
							nameof(EEngineUniform.UIWidth) => b.Z,
							nameof(EEngineUniform.UIHeight) => b.W,
							_ => 0f
						};
						type = EShaderVarType._float;
						return true;
					}
					value = 0f;
					type = EShaderVarType._float;
					return true;
			}

			return false;
		}

		#endregion // Uniform Buffer Updates

		#region Uniform Data Writing Helpers

		/// <summary>
		/// Writes a typed value into auto uniform buffer memory at the member's offset.
		/// Supports float, int, uint, bool, vec2–vec4, ivec2–ivec4, uvec2–uvec4, and mat4.
		/// </summary>
		private bool TryWriteAutoUniformValue(Span<byte> data, AutoUniformMember member, object value, EShaderVarType valueType)
		{
			if (member.EngineType is null)
				return false;

			if (!AreCompatible(member.EngineType.Value, valueType))
				return false;

			uint offset = member.Offset;
			switch (member.EngineType.Value)
			{
				case EShaderVarType._float:
					return TryWriteScalar(data, offset, value, Convert.ToSingle);
				case EShaderVarType._int:
					return TryWriteScalar(data, offset, value, Convert.ToInt32);
				case EShaderVarType._uint:
					return TryWriteScalar(data, offset, value, Convert.ToUInt32);
				case EShaderVarType._bool:
					return TryWriteScalar(data, offset, value, v => Convert.ToBoolean(v) ? 1 : 0);
				case EShaderVarType._vec2:
					return TryWriteVector2(data, offset, value);
				case EShaderVarType._vec3:
					return TryWriteVector3(data, offset, value);
				case EShaderVarType._vec4:
					return TryWriteVector4(data, offset, value);
				case EShaderVarType._ivec2:
					return TryWriteIVector2(data, offset, value);
				case EShaderVarType._ivec3:
					return TryWriteIVector3(data, offset, value);
				case EShaderVarType._ivec4:
					return TryWriteIVector4(data, offset, value);
				case EShaderVarType._uvec2:
					return TryWriteUVector2(data, offset, value);
				case EShaderVarType._uvec3:
					return TryWriteUVector3(data, offset, value);
				case EShaderVarType._uvec4:
					return TryWriteUVector4(data, offset, value);
				case EShaderVarType._mat4:
					return TryWriteMatrix4(data, offset, value);
				default:
					return false;
			}
		}

        /// <summary>
        /// Checks whether two shader variable types are compatible for writing.
        /// Allows common promotions (vec3↔vec4, int↔bool, uint↔bool).
        /// </summary>
        private static bool AreCompatible(EShaderVarType expected, EShaderVarType actual)
			=> expected == actual || (expected, actual) switch
			{
				(EShaderVarType._vec4, EShaderVarType._vec3) => true,
				(EShaderVarType._vec3, EShaderVarType._vec4) => true,
				(EShaderVarType._int, EShaderVarType._bool) => true,
				(EShaderVarType._uint, EShaderVarType._bool) => true,
				(EShaderVarType._bool, EShaderVarType._int) => true,
				(EShaderVarType._bool, EShaderVarType._uint) => true,
				_ => false
			};

        // ── Scalar and Vector Write Helpers ───────────────────────────────────
        // Each helper writes a specific type into a byte span at the given offset.
        // std140 aligns vec3 members to 16 bytes but still lets the next scalar use
        // the fourth lane, so vec3 writes must only touch xyz.

        private static bool TryWriteScalar<T>(Span<byte> data, uint offset, object value, Func<object, T> converter) where T : unmanaged
		{
			if (value is null || value is Array)
				return false;

			if (value is T typed)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], typed);
				return true;
			}

			if (value is not IConvertible)
				return false;

			try
			{
				T converted = converter(value);
				Unsafe.WriteUnaligned(ref data[(int)offset], converted);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool TryWriteVector2(Span<byte> data, uint offset, object value)
		{
			if (value is Vector2 v)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v);
				return true;
			}
			return false;
		}

		private static bool TryWriteVector3(Span<byte> data, uint offset, object value)
		{
			if (TryConvertVector3(value, out Vector3 v3))
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v3);
				return true;
			}
			if (TryConvertVector4(value, out Vector4 v4b))
			{
				Vector3 v3b = new(v4b.X, v4b.Y, v4b.Z);
				Unsafe.WriteUnaligned(ref data[(int)offset], v3b);
				return true;
			}
			return false;
		}

		private static bool TryWriteVector4(Span<byte> data, uint offset, object value)
		{
			if (TryConvertVector4(value, out Vector4 v))
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v);
				return true;
			}
			if (TryConvertVector3(value, out Vector3 v3))
			{
				Vector4 v4 = new(v3, 0f);
				Unsafe.WriteUnaligned(ref data[(int)offset], v4);
				return true;
			}
			return false;
		}

		private static bool TryConvertVector3(object value, out Vector3 vector)
		{
			switch (value)
			{
				case Vector3 v:
					vector = v;
					return true;
				case Vector4 v:
					vector = new Vector3(v.X, v.Y, v.Z);
					return true;
				case ColorF3 c:
					vector = new Vector3(c.R, c.G, c.B);
					return true;
				case ColorF4 c:
					vector = new Vector3(c.R, c.G, c.B);
					return true;
				default:
					vector = default;
					return false;
			}
		}

		private static bool TryConvertVector4(object value, out Vector4 vector)
		{
			switch (value)
			{
				case Vector4 v:
					vector = v;
					return true;
				case Vector3 v:
					vector = new Vector4(v, 0f);
					return true;
				case ColorF4 c:
					vector = new Vector4(c.R, c.G, c.B, c.A);
					return true;
				case ColorF3 c:
					vector = new Vector4(c.R, c.G, c.B, 0f);
					return true;
				default:
					vector = default;
					return false;
			}
		}

		private static bool TryWriteIVector2(Span<byte> data, uint offset, object value)
		{
			if (value is IVector2 v)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v);
				return true;
			}
			return false;
		}

		private static bool TryWriteIVector3(Span<byte> data, uint offset, object value)
		{
			if (value is IVector3 v3)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v3);
				return true;
			}
			if (value is IVector4 v4b)
			{
				IVector3 v3b = new(v4b.X, v4b.Y, v4b.Z);
				Unsafe.WriteUnaligned(ref data[(int)offset], v3b);
				return true;
			}
			return false;
		}

		private static bool TryWriteIVector4(Span<byte> data, uint offset, object value)
		{
			if (value is IVector4 v)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v);
				return true;
			}
			return false;
		}

		private static bool TryWriteUVector2(Span<byte> data, uint offset, object value)
		{
			if (value is UVector2 v)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v);
				return true;
			}
			return false;
		}

		private static bool TryWriteUVector3(Span<byte> data, uint offset, object value)
		{
			if (value is UVector3 v3)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v3);
				return true;
			}
			if (value is UVector4 v4b)
			{
				UVector3 v3b = new(v4b.X, v4b.Y, v4b.Z);
				Unsafe.WriteUnaligned(ref data[(int)offset], v3b);
				return true;
			}
			return false;
		}

		private static bool TryWriteUVector4(Span<byte> data, uint offset, object value)
		{
			if (value is UVector4 v)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v);
				return true;
			}
			return false;
		}

		private static bool TryWriteMatrix4(Span<byte> data, uint offset, object value)
		{
			if (value is Matrix4x4 m)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], m);
				return true;
			}
			return false;
		}

		#endregion // Uniform Data Writing Helpers

		#region Engine Uniform Upload

		/// <summary>
		/// Resolves and uploads a named engine uniform to a host-visible UBO.
		/// This is the legacy per-binding path; auto uniform blocks use the
		/// TryWriteAutoUniformBlock path instead.
		/// </summary>
		private bool TryWriteEngineUniform(string name, in PendingMeshDraw draw, EngineUniformBuffer buffer)
		{
			string normalized = NormalizeEngineUniformName(name);
			if (normalized.Equals(FallbackDescriptorUniformName, StringComparison.Ordinal))
				return ClearEngineUniformBuffer(buffer);

			XRCamera? camera = draw.Camera;
			bool stereoPass = draw.IsStereoPass;

			// Fallback: if previous model matrix was never captured, assume static
			// to avoid injecting false motion into the velocity buffer (causes
			// diagonal blur on objects that are actually still). Use a loose
			// comparison to tolerate floating-point jitter around identity.
			Matrix4x4 prevModelMatrix = draw.PreviousModelMatrix;
			if (IsApproximatelyIdentity(prevModelMatrix) && !IsApproximatelyIdentity(draw.ModelMatrix))
				prevModelMatrix = draw.ModelMatrix;

			Matrix4x4 inverseModel = Matrix4x4.Identity;
			Matrix4x4.Invert(draw.ModelMatrix, out inverseModel);

			// Camera matrices/vectors come from the draw snapshot captured at enqueue time.
			// Reading live camera state here can be stale because the pipeline camera stack
			// has already been popped.
			Matrix4x4 viewMatrix = draw.ViewMatrix;
			Matrix4x4 inverseViewMatrix = draw.InverseViewMatrix;
			Matrix4x4 projMatrix = draw.ProjectionMatrix;
			Matrix4x4 inverseProjMatrix = draw.InverseProjectionMatrix;
			Matrix4x4 rightEyeViewMatrix = draw.RightEyeViewMatrix;
			Matrix4x4 rightEyeInverseProjMatrix = draw.RightEyeInverseProjectionMatrix;
			Matrix4x4 rightEyeProjMatrix = draw.RightEyeProjectionMatrix;

			switch (normalized)
			{
				case nameof(EEngineUniform.UpdateDelta):
					return UploadUniform(buffer, RuntimeEngine.Time.Timer.Update.Delta);
				case nameof(EEngineUniform.RenderTime):
					return UploadUniform(buffer, this.Renderer._materialUniformSecondsLive);
				case nameof(EEngineUniform.EngineTime):
					return UploadUniform(buffer, RuntimeEngine.ElapsedTime);
				case nameof(EEngineUniform.DeltaTime):
					return UploadUniform(buffer, RuntimeEngine.Time.Timer.Render.Delta);
				case TransformIdUniformName:
					return UploadUniform(buffer, draw.TransformId);
				case SkinPaletteBaseUniformName:
					return UploadUniform(buffer, MeshRenderer.ActiveSkinPaletteBase);
				case SkinPaletteCountUniformName:
					return UploadUniform(buffer, MeshRenderer.ActiveSkinPaletteCount);
				case SkinningInfluenceCapUniformName:
					return UploadUniform(buffer, MeshRenderer.ActiveSkinningInfluenceCap);
				case BlendshapeActiveCountUniformName:
					return UploadUniform(buffer, MeshRenderer.ActiveBlendshapeCount);
				case BlendshapeWeightThresholdUniformName:
					return UploadUniform(buffer, MeshRenderer.BlendshapeActiveWeightThreshold);
				case UsePrecombinedBlendshapeDeltasUniformName:
					return UploadUniform(
						buffer,
						RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass
						&& !RuntimeEngine.Rendering.State.IsVulkan
						&& MeshRenderer.HasValidPrecombinedBlendshapeDeltas
							? 1
							: 0);
				case nameof(EEngineUniform.ModelMatrix):
					return UploadUniform(buffer, draw.ModelMatrix);
				case nameof(EEngineUniform.PrevModelMatrix):
					return UploadUniform(buffer, prevModelMatrix);
				case nameof(EEngineUniform.RootInvModelMatrix):
					return UploadUniform(buffer, inverseModel);
				case nameof(EEngineUniform.ViewMatrix):
				case nameof(EEngineUniform.LeftEyeViewMatrix):
					return UploadUniform(buffer, viewMatrix);
				case nameof(EEngineUniform.PrevViewMatrix):
				case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
					return UploadUniform(buffer, draw.PreviousViewMatrix);
				case nameof(EEngineUniform.RightEyeViewMatrix):
					return UploadUniform(buffer, rightEyeViewMatrix);
				case nameof(EEngineUniform.PrevRightEyeViewMatrix):
					return UploadUniform(buffer, draw.PreviousRightEyeViewMatrix);
				case nameof(EEngineUniform.InverseViewMatrix):
					return UploadUniform(buffer, inverseViewMatrix);
				case nameof(EEngineUniform.InverseProjMatrix):
					return UploadUniform(buffer, inverseProjMatrix);
				case nameof(EEngineUniform.ViewProjectionMatrix):
					return UploadUniform(buffer, draw.ViewProjectionMatrix);
				case nameof(EEngineUniform.ProjMatrix):
					return UploadUniform(buffer, projMatrix);
				case nameof(EEngineUniform.PrevProjMatrix):
				case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
					return UploadUniform(buffer, draw.PreviousProjectionMatrix);
				case nameof(EEngineUniform.PrevRightEyeProjMatrix):
					return UploadUniform(buffer, draw.PreviousRightEyeProjectionMatrix);
				case nameof(EEngineUniform.LeftEyeViewProjectionMatrix):
					return UploadUniform(buffer, draw.ViewProjectionMatrix);
				case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
					return UploadUniform(buffer, inverseViewMatrix);
				case nameof(EEngineUniform.LeftEyeInverseProjMatrix):
					return UploadUniform(buffer, inverseProjMatrix);
				case nameof(EEngineUniform.RightEyeInverseViewMatrix):
					return UploadUniform(buffer, draw.RightEyeInverseViewMatrix);
				case nameof(EEngineUniform.RightEyeInverseProjMatrix):
					return UploadUniform(buffer, rightEyeInverseProjMatrix);
				case nameof(EEngineUniform.RightEyeViewProjectionMatrix):
					return UploadUniform(buffer, draw.RightEyeViewProjectionMatrix);
				case nameof(EEngineUniform.LeftEyeProjMatrix):
					return UploadUniform(buffer, projMatrix);
				case nameof(EEngineUniform.RightEyeProjMatrix):
					return UploadUniform(buffer, rightEyeProjMatrix);
				case nameof(EEngineUniform.CameraPosition):
					return UploadUniform(buffer, ToVector4(draw.CameraPosition));
				case nameof(EEngineUniform.CameraForward):
					return UploadUniform(buffer, ToVector4(draw.CameraForward));
				case nameof(EEngineUniform.CameraUp):
					return UploadUniform(buffer, ToVector4(draw.CameraUp));
				case nameof(EEngineUniform.CameraRight):
					return UploadUniform(buffer, ToVector4(draw.CameraRight));
				case nameof(EEngineUniform.CameraNearZ):
					return UploadUniform(buffer, camera?.NearZ ?? 0f);
				case nameof(EEngineUniform.CameraFarZ):
					return UploadUniform(buffer, camera?.FarZ ?? 0f);
				case nameof(EEngineUniform.CameraFovX):
					return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters persp ? persp.HorizontalFieldOfView : 0f);
				case nameof(EEngineUniform.CameraFovY):
					return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters perspY ? perspY.VerticalFieldOfView : 0f);
				case nameof(EEngineUniform.CameraAspect):
					return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters perspA ? perspA.AspectRatio : 0f);
				case nameof(EEngineUniform.DepthMode):
					return UploadUniform(buffer, (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal));
				case nameof(EEngineUniform.ClipSpaceYDirection):
					return UploadUniform(buffer, (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
				case nameof(EEngineUniform.ClipDepthRange):
					return UploadUniform(buffer, (int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
				case nameof(EEngineUniform.FramebufferTextureYDirection):
					return UploadUniform(buffer, (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan));
				case nameof(EEngineUniform.ScreenWidth):
				case nameof(EEngineUniform.ScreenHeight):
					// Prefer the enqueue-time render-area snapshot; the live RenderArea is empty
					// at deferred record time (see the matching note in TryResolveEngineUniformValue).
					float screenW = draw.RenderAreaWidth;
					float screenH = draw.RenderAreaHeight;
					if (screenW <= 0f || screenH <= 0f)
					{
						screenW = draw.Viewport.Width;
						screenH = MathF.Abs(draw.Viewport.Height);
					}
					if (screenW <= 0f || screenH <= 0f)
					{
						var area = RuntimeEngine.Rendering.State.RenderArea;
						screenW = area.Width;
						screenH = area.Height;
					}
					return UploadUniform(buffer, normalized.Equals(nameof(EEngineUniform.ScreenWidth), StringComparison.Ordinal) ? screenW : screenH);
				case nameof(EEngineUniform.ScreenOrigin):
					return UploadUniform(buffer, new Vector2(0f, 0f));
				case nameof(EEngineUniform.BillboardMode):
					return UploadUniform(buffer, (int)draw.BillboardMode);
				case nameof(EEngineUniform.VRMode):
					return UploadUniform(buffer, stereoPass ? 1 : 0);
				case nameof(EEngineUniform.UIXYWH):
					if (_program is not null && _program.TryGetUniformValue(nameof(EEngineUniform.UIXYWH), out ProgramUniformValue uiBounds))
						return UploadProgramUniform(buffer, uiBounds);
					return UploadUniform(buffer, Vector4.Zero);
				case nameof(EEngineUniform.UIX):
				case nameof(EEngineUniform.UIY):
				case nameof(EEngineUniform.UIWidth):
				case nameof(EEngineUniform.UIHeight):
					if (_program is not null && _program.TryGetUniformValue(normalized, out ProgramUniformValue uiScalar))
						return UploadProgramUniform(buffer, uiScalar);
					if (_program is not null && _program.TryGetUniformValue(nameof(EEngineUniform.UIXYWH), out ProgramUniformValue packedBounds) && packedBounds.Value is Vector4 b)
					{
						float scalar = normalized switch
						{
							nameof(EEngineUniform.UIX) => b.X,
							nameof(EEngineUniform.UIY) => b.Y,
							nameof(EEngineUniform.UIWidth) => b.Z,
							nameof(EEngineUniform.UIHeight) => b.W,
							_ => 0f
						};
						return UploadUniform(buffer, scalar);
					}
					return UploadUniform(buffer, 0f);
			}

			if (_engineUniformWarnings.Add(normalized))
				Debug.VulkanWarning($"Unhandled engine uniform '{normalized}' for Vulkan descriptors.");

			return false;
		}

        /// <summary>Strips the vertex-stage suffix ("_VTX") from engine uniform names.</summary>
        private static string NormalizeEngineUniformName(string name)
			=> string.IsNullOrWhiteSpace(name)
                ? string.Empty
                : name.EndsWith(VertexUniformSuffix, StringComparison.Ordinal)
                ? name[..^VertexUniformSuffix.Length]
                : name;

        /// <summary>
        /// Returns the byte size of a named engine uniform.
        /// Returns 0 for unrecognized names (e.g. user-defined uniforms).
        /// </summary>
        private static uint GetEngineUniformSize(string name)
		{
			string normalized = NormalizeEngineUniformName(name);
			return normalized switch
			{
				nameof(EEngineUniform.ModelMatrix) or nameof(EEngineUniform.PrevModelMatrix) or nameof(EEngineUniform.ViewMatrix) or nameof(EEngineUniform.LeftEyeViewMatrix) or nameof(EEngineUniform.RightEyeViewMatrix) or nameof(EEngineUniform.InverseViewMatrix) or nameof(EEngineUniform.InverseProjMatrix) or nameof(EEngineUniform.ProjMatrix) or nameof(EEngineUniform.ViewProjectionMatrix) or nameof(EEngineUniform.LeftEyeViewProjectionMatrix) or nameof(EEngineUniform.RightEyeViewProjectionMatrix) or nameof(EEngineUniform.LeftEyeInverseViewMatrix) or nameof(EEngineUniform.RightEyeInverseViewMatrix) or nameof(EEngineUniform.LeftEyeInverseProjMatrix) or nameof(EEngineUniform.RightEyeInverseProjMatrix) or nameof(EEngineUniform.LeftEyeProjMatrix) or nameof(EEngineUniform.RightEyeProjMatrix) or nameof(EEngineUniform.PrevViewMatrix) or nameof(EEngineUniform.PrevLeftEyeViewMatrix) or nameof(EEngineUniform.PrevRightEyeViewMatrix) or nameof(EEngineUniform.PrevProjMatrix) or nameof(EEngineUniform.PrevLeftEyeProjMatrix) or nameof(EEngineUniform.PrevRightEyeProjMatrix) or nameof(EEngineUniform.RootInvModelMatrix) => (uint)Unsafe.SizeOf<Matrix4x4>(),
				nameof(EEngineUniform.CameraPosition) or nameof(EEngineUniform.CameraForward) or nameof(EEngineUniform.CameraUp) or nameof(EEngineUniform.CameraRight) => 16u,
				nameof(EEngineUniform.CameraNearZ) or nameof(EEngineUniform.CameraFarZ) or nameof(EEngineUniform.CameraFovX) or nameof(EEngineUniform.CameraFovY) or nameof(EEngineUniform.CameraAspect) or nameof(EEngineUniform.ScreenWidth) or nameof(EEngineUniform.ScreenHeight) or nameof(EEngineUniform.UpdateDelta) or nameof(EEngineUniform.RenderTime) or nameof(EEngineUniform.EngineTime) or nameof(EEngineUniform.DeltaTime) or nameof(EEngineUniform.DepthMode) or nameof(EEngineUniform.ClipSpaceYDirection) or nameof(EEngineUniform.ClipDepthRange) or nameof(EEngineUniform.FramebufferTextureYDirection) or nameof(EEngineUniform.UIX) or nameof(EEngineUniform.UIY) or nameof(EEngineUniform.UIWidth) or nameof(EEngineUniform.UIHeight) or TransformIdUniformName or SkinPaletteBaseUniformName or SkinPaletteCountUniformName or SkinningInfluenceCapUniformName or BlendshapeActiveCountUniformName or BlendshapeWeightThresholdUniformName or UsePrecombinedBlendshapeDeltasUniformName => 4u,
				nameof(EEngineUniform.ScreenOrigin) => 8u,
				nameof(EEngineUniform.BillboardMode) or nameof(EEngineUniform.VRMode) => 4u,
				nameof(EEngineUniform.UIXYWH) => 16u,
				_ => 0u,
			};
		}

		/// <summary>Converts a Vector3 to Vector4 with W=0 for shader upload.</summary>
		private static Vector4 ToVector4(in Vector3 v) => new(v, 0f);

		/// <summary>
		/// Checks whether a matrix is approximately identity (within epsilon).
		/// Used to detect un-initialized previous-frame model matrices and
		/// suppress false motion vectors.
		/// </summary>
		private static bool IsApproximatelyIdentity(in Matrix4x4 m)
		{
			const float eps = 1e-4f;
			return MathF.Abs(m.M11 - 1f) < eps && MathF.Abs(m.M22 - 1f) < eps && MathF.Abs(m.M33 - 1f) < eps && MathF.Abs(m.M44 - 1f) < eps
				&& MathF.Abs(m.M12) < eps && MathF.Abs(m.M13) < eps && MathF.Abs(m.M14) < eps
				&& MathF.Abs(m.M21) < eps && MathF.Abs(m.M23) < eps && MathF.Abs(m.M24) < eps
				&& MathF.Abs(m.M31) < eps && MathF.Abs(m.M32) < eps && MathF.Abs(m.M34) < eps
				&& MathF.Abs(m.M41) < eps && MathF.Abs(m.M42) < eps && MathF.Abs(m.M43) < eps;
		}

		/// <summary>Maps and uploads a single unmanaged value to a host-visible UBO.</summary>
		private bool UploadUniform<T>(EngineUniformBuffer buffer, in T value) where T : unmanaged
		{
			if (buffer.MappedPtr == null)
				return false;

			uint size = (uint)Unsafe.SizeOf<T>();
			uint copySize = Math.Min(buffer.Size, size);

			T localValue = value;
			Unsafe.CopyBlock(buffer.MappedPtr, Unsafe.AsPointer(ref localValue), copySize);
			return true;
		}

		private bool ClearEngineUniformBuffer(EngineUniformBuffer buffer)
		{
			if (buffer.MappedPtr == null)
				return false;

			new Span<byte>(buffer.MappedPtr, (int)buffer.Size).Clear();
			return true;
		}

		/// <summary>Uploads a boxed program uniform value to a host-visible UBO, dispatching by type.</summary>
		private bool UploadProgramUniform(EngineUniformBuffer buffer, ProgramUniformValue value)
		{
			return value.Type switch
			{
				EShaderVarType._float => UploadUniform(buffer, Convert.ToSingle(value.Value)),
				EShaderVarType._int => UploadUniform(buffer, Convert.ToInt32(value.Value)),
				EShaderVarType._uint => UploadUniform(buffer, Convert.ToUInt32(value.Value)),
				EShaderVarType._bool => UploadUniform(buffer, Convert.ToBoolean(value.Value) ? 1 : 0),
				EShaderVarType._vec2 when value.Value is Vector2 v2 => UploadUniform(buffer, v2),
				EShaderVarType._vec3 when TryConvertVector3(value.Value, out Vector3 v3) => UploadUniform(buffer, new Vector4(v3, 0f)),
				EShaderVarType._vec4 when TryConvertVector4(value.Value, out Vector4 v4) => UploadUniform(buffer, v4),
				EShaderVarType._ivec2 when value.Value is IVector2 iv2 => UploadUniform(buffer, iv2),
				EShaderVarType._ivec3 when value.Value is IVector3 iv3 => UploadUniform(buffer, new IVector4(iv3.X, iv3.Y, iv3.Z, 0)),
				EShaderVarType._ivec4 when value.Value is IVector4 iv4 => UploadUniform(buffer, iv4),
				EShaderVarType._uvec2 when value.Value is UVector2 uv2 => UploadUniform(buffer, uv2),
				EShaderVarType._uvec3 when value.Value is UVector3 uv3 => UploadUniform(buffer, new UVector4(uv3.X, uv3.Y, uv3.Z, 0)),
				EShaderVarType._uvec4 when value.Value is UVector4 uv4 => UploadUniform(buffer, uv4),
				EShaderVarType._mat4 when value.Value is Matrix4x4 mat => UploadUniform(buffer, mat),
				_ => false
			};
		}

		#endregion // Engine Uniform Upload
	}
}

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
			Api!.DestroyBuffer(device, buffer, null);

		if (memory.Handle != 0)
			Api!.FreeMemory(device, memory, null);
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
			Api!.DestroyBuffer(device, buffer, null);
			if (entry.Value.Handle != 0)
				Api!.FreeMemory(device, entry.Value, null);
		}
	}

	public partial class VkMeshRenderer
	{
		#region Uniform Buffer Allocation

		/// <summary>
		/// Ensures a per-frame engine uniform buffer exists and is large enough.
		/// Destroys and recreates if the frame count or size has changed.
		/// </summary>
		private bool EnsureEngineUniformBuffer(string name, uint size)
		{
			int frames = Renderer.swapChainImages?.Length ?? 1;
			if (_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? existing))
			{
				bool valid = existing.Length == frames && existing.All(e => e.Buffer.Handle != 0 && e.Size >= size);
				if (valid)
					return true;

				DestroyEngineUniformBuffers(name);
			}

			EngineUniformBuffer[] buffers = new EngineUniformBuffer[frames];
			for (int i = 0; i < frames; i++)
			{
				if (!CreateHostVisibleBuffer(size, BufferUsageFlags.UniformBufferBit, out var buffer, out var memory))
					return false;

				buffers[i] = new EngineUniformBuffer(buffer, memory, size);
			}

			_engineUniformBuffers[name] = buffers;
			return true;
		}

		/// <summary>
		/// Ensures a per-frame auto uniform buffer exists and is large enough.
		/// Destroys and recreates if the frame count or size has changed.
		/// </summary>
		private bool EnsureAutoUniformBuffer(string name, uint size)
		{
			int frames = Renderer.swapChainImages?.Length ?? 1;
			if (_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? existing))
			{
				bool valid = existing.Length == frames && existing.All(e => e.Buffer.Handle != 0 && e.Size >= size);
				if (valid)
					return true;

				DestroyAutoUniformBuffers(name);
			}

			AutoUniformBuffer[] buffers = new AutoUniformBuffer[frames];
			for (int i = 0; i < frames; i++)
			{
				if (!CreateHostVisibleBuffer(size, BufferUsageFlags.UniformBufferBit, out var buffer, out var memory))
					return false;

				buffers[i] = new AutoUniformBuffer(buffer, memory, size);
			}

			_autoUniformBuffers[name] = buffers;
			return true;
		}

		/// <summary>
		/// Allocates a host-visible, host-coherent Vulkan buffer with the given usage flags.
		/// Used for engine and auto uniform buffers that are updated every frame via map/unmap.
		/// </summary>
		private bool CreateHostVisibleBuffer(uint size, BufferUsageFlags usage, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
		{
			buffer = default;
			memory = default;
			size = Math.Max(size, 1u);

			BufferCreateInfo bufferInfo = new()
			{
				SType = StructureType.BufferCreateInfo,
				Size = size,
				Usage = usage,
				SharingMode = SharingMode.Exclusive,
			};

			if (Api!.CreateBuffer(Device, ref bufferInfo, null, out buffer) != Result.Success)
			{
				WarnOnce($"Failed to create engine uniform buffer '{size}' bytes.");
				return false;
			}

			Api.GetBufferMemoryRequirements(Device, buffer, out MemoryRequirements memReqs);

			MemoryAllocateInfo allocInfo = new()
			{
				SType = StructureType.MemoryAllocateInfo,
				AllocationSize = memReqs.Size,
				MemoryTypeIndex = Renderer.FindMemoryType(memReqs.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
			};

			if (Api.AllocateMemory(Device, ref allocInfo, null, out memory) != Result.Success)
			{
				Api.DestroyBuffer(Device, buffer, null);
				WarnOnce("Failed to allocate memory for engine uniform buffer.");
				buffer = default;
				return false;
			}

			Api.BindBufferMemory(Device, buffer, memory, 0);
			Renderer.TrackMeshUniformBuffer(buffer, memory);
			return true;
		}

		#endregion // Uniform Buffer Allocation

		#region Uniform Buffer Updates

		/// <summary>
		/// Writes engine-uniform data into all active engine UBOs for the current frame.
		/// Called once per draw before descriptor binding.
		/// </summary>
		private void UpdateEngineUniformBuffersForDraw(int frameIndex, in PendingMeshDraw draw)
		{
			if (_engineUniformBuffers.Count == 0)
				return;

			foreach (var pair in _engineUniformBuffers)
			{
				EngineUniformBuffer[] buffers = pair.Value;
				if (buffers.Length == 0)
					continue;

				int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
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
		private void UpdateAutoUniformBuffersForDraw(int frameIndex, XRMaterial material, in PendingMeshDraw draw)
		{
			if (_program is null || _autoUniformBuffers.Count == 0)
				return;

			foreach (var pair in _program.AutoUniformBlocks)
			{
				string name = pair.Key;
				AutoUniformBlockInfo block = pair.Value;
				if (!_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? buffers) || buffers.Length == 0)
					continue;

				int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
				AutoUniformBuffer buffer = buffers[idx];
				if (buffer.Buffer.Handle == 0)
					continue;

				TryWriteAutoUniformBlock(block, buffer, material, draw);
			}
		}

		/// <summary>
		/// Maps an auto uniform buffer, clears it, and writes each member
		/// from engine state, program overrides, and material parameters.
		/// </summary>
		private bool TryWriteAutoUniformBlock(AutoUniformBlockInfo block, AutoUniformBuffer buffer, XRMaterial material, in PendingMeshDraw draw)
		{
			void* mapped;
			if (Api!.MapMemory(Device, buffer.Memory, 0, buffer.Size, 0, &mapped) != Result.Success)
				return false;

			try
			{
				Span<byte> data = new(mapped, (int)buffer.Size);
				data.Clear();

				foreach (AutoUniformMember member in block.Members)
				{
					if (member.Offset + member.Size > buffer.Size)
						continue;

					TryWriteAutoUniformMember(data, member, material, draw);
				}
			}
			finally
			{
				Api.UnmapMemory(Device, buffer.Memory);
			}

			return true;
		}

		/// <summary>
		/// Attempts to write a single auto uniform member. Resolution priority:
		/// engine uniform value > program override > material parameter > array defaults > default value.
		/// </summary>
		private bool TryWriteAutoUniformMember(Span<byte> data, AutoUniformMember member, XRMaterial material, in PendingMeshDraw draw)
		{
			if (member.EngineType is null)
				return false;

			if (TryResolveEngineUniformValue(member.Name, draw, out object? engineValue, out EShaderVarType engineType))
				return engineValue is not null && TryWriteAutoUniformValue(data, member, engineValue, engineType);

			if (_program is not null && _program.TryGetUniformValue(member.Name, out ProgramUniformValue programValue))
				return TryWriteProgramUniformValue(data, member, programValue);

			ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
			if (parameter is not null)
			{
				if (member.IsArray)
					return TryWriteAutoUniformArray(data, member, parameter);

				return TryWriteAutoUniformValue(data, member, parameter.GenericValue, parameter.TypeName);
			}

			if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
				return TryWriteAutoUniformArrayDefaults(data, member);

			if (member.DefaultValue is { } defaultValue)
				return TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);

			return false;
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
		/// Falls back to program-level uniform overrides if present.
		/// </summary>
		private bool TryResolveEngineUniformValue(string name, in PendingMeshDraw draw, out object? value, out EShaderVarType type)
		{
			value = null;
			type = EShaderVarType._float;
			string normalized = NormalizeEngineUniformName(name);
			XRCamera? camera = Engine.Rendering.State.RenderingCamera;
			XRCamera? rightEyeCamera = Engine.Rendering.State.RenderingStereoRightEyeCamera;
			bool stereoPass = Engine.Rendering.State.IsStereoPass;
			bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;

			Matrix4x4 prevModelMatrix = draw.PreviousModelMatrix;
			if (IsApproximatelyIdentity(prevModelMatrix) && !IsApproximatelyIdentity(draw.ModelMatrix))
				prevModelMatrix = draw.ModelMatrix;

			Matrix4x4 inverseModel = Matrix4x4.Identity;
			Matrix4x4.Invert(draw.ModelMatrix, out inverseModel);

			Matrix4x4 viewMatrix = camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
			Matrix4x4 inverseViewMatrix = camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
			Matrix4x4 projMatrix = useUnjittered && camera is not null
				? camera.ProjectionMatrixUnjittered
				: camera?.ProjectionMatrix ?? Matrix4x4.Identity;

			if (_program is not null && _program.TryGetUniformValue(normalized, out ProgramUniformValue programValue))
			{
				value = programValue.Value;
				type = programValue.Type;
				return true;
			}

			switch (normalized)
			{
				case nameof(EEngineUniform.UpdateDelta):
					value = Engine.Time.Timer.Update.Delta;
					type = EShaderVarType._float;
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
				case nameof(EEngineUniform.PrevViewMatrix):
				case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
				case nameof(EEngineUniform.PrevRightEyeViewMatrix):
					value = viewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.InverseViewMatrix):
					value = inverseViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.ProjMatrix):
				case nameof(EEngineUniform.PrevProjMatrix):
				case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
				case nameof(EEngineUniform.PrevRightEyeProjMatrix):
					value = projMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
					value = inverseViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.RightEyeInverseViewMatrix):
					value = rightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.LeftEyeProjMatrix):
					value = projMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.RightEyeProjMatrix):
					value = rightEyeCamera?.ProjectionMatrix ?? projMatrix;
					type = EShaderVarType._mat4;
					return true;
				case nameof(EEngineUniform.CameraPosition):
					value = ToVector4(camera?.Transform.RenderTranslation ?? Vector3.Zero);
					type = EShaderVarType._vec4;
					return true;
				case nameof(EEngineUniform.CameraForward):
					value = ToVector4(camera?.Transform.RenderForward ?? Vector3.UnitZ);
					type = EShaderVarType._vec4;
					return true;
				case nameof(EEngineUniform.CameraUp):
					value = ToVector4(camera?.Transform.RenderUp ?? Vector3.UnitY);
					type = EShaderVarType._vec4;
					return true;
				case nameof(EEngineUniform.CameraRight):
					value = ToVector4(camera?.Transform.RenderRight ?? Vector3.UnitX);
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
				case nameof(EEngineUniform.ScreenWidth):
				case nameof(EEngineUniform.ScreenHeight):
					var area = Engine.Rendering.State.RenderArea;
					value = normalized.Equals(nameof(EEngineUniform.ScreenWidth), StringComparison.Ordinal) ? (float)area.Width : (float)area.Height;
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
		{
			if (expected == actual)
				return true;

			return (expected, actual) switch
			{
				(EShaderVarType._vec4, EShaderVarType._vec3) => true,
				(EShaderVarType._vec3, EShaderVarType._vec4) => true,
				(EShaderVarType._int, EShaderVarType._bool) => true,
				(EShaderVarType._uint, EShaderVarType._bool) => true,
				(EShaderVarType._bool, EShaderVarType._int) => true,
				(EShaderVarType._bool, EShaderVarType._uint) => true,
				_ => false
			};
		}

		// ── Scalar and Vector Write Helpers ───────────────────────────────────
		// Each helper writes a specific type into a byte span at the given offset.
		// Vector3 types write as Vector4 (std140 alignment) with W=0.

		private static bool TryWriteScalar<T>(Span<byte> data, uint offset, object value, Func<object, T> converter) where T : unmanaged
		{
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
			if (value is Vector3 v3)
			{
				Vector4 v4 = new(v3, 0f);
				Unsafe.WriteUnaligned(ref data[(int)offset], v4);
				return true;
			}
			if (value is Vector4 v4b)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v4b);
				return true;
			}
			return false;
		}

		private static bool TryWriteVector4(Span<byte> data, uint offset, object value)
		{
			if (value is Vector4 v)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v);
				return true;
			}
			if (value is Vector3 v3)
			{
				Vector4 v4 = new(v3, 0f);
				Unsafe.WriteUnaligned(ref data[(int)offset], v4);
				return true;
			}
			return false;
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
				IVector4 v4 = new(v3.X, v3.Y, v3.Z, 0);
				Unsafe.WriteUnaligned(ref data[(int)offset], v4);
				return true;
			}
			if (value is IVector4 v4b)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v4b);
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
				UVector4 v4 = new(v3.X, v3.Y, v3.Z, 0);
				Unsafe.WriteUnaligned(ref data[(int)offset], v4);
				return true;
			}
			if (value is UVector4 v4b)
			{
				Unsafe.WriteUnaligned(ref data[(int)offset], v4b);
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
			XRCamera? camera = Engine.Rendering.State.RenderingCamera;
			XRCamera? rightEyeCamera = Engine.Rendering.State.RenderingStereoRightEyeCamera;
			bool stereoPass = Engine.Rendering.State.IsStereoPass;
			bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;

			// Fallback: if previous model matrix was never captured, assume static
			// to avoid injecting false motion into the velocity buffer (causes
			// diagonal blur on objects that are actually still). Use a loose
			// comparison to tolerate floating-point jitter around identity.
			Matrix4x4 prevModelMatrix = draw.PreviousModelMatrix;
			if (IsApproximatelyIdentity(prevModelMatrix) && !IsApproximatelyIdentity(draw.ModelMatrix))
				prevModelMatrix = draw.ModelMatrix;

			Matrix4x4 inverseModel = Matrix4x4.Identity;
			Matrix4x4.Invert(draw.ModelMatrix, out inverseModel);

			Matrix4x4 viewMatrix = camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
			Matrix4x4 inverseViewMatrix = camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
			Matrix4x4 projMatrix = useUnjittered && camera is not null
				? camera.ProjectionMatrixUnjittered
				: camera?.ProjectionMatrix ?? Matrix4x4.Identity;

			if (_program is not null && _program.TryGetUniformValue(normalized, out ProgramUniformValue programValue))
				return UploadProgramUniform(buffer, programValue);

			switch (normalized)
			{
				case nameof(EEngineUniform.UpdateDelta):
					return UploadUniform(buffer, Engine.Time.Timer.Update.Delta);
				case nameof(EEngineUniform.ModelMatrix):
					return UploadUniform(buffer, draw.ModelMatrix);
				case nameof(EEngineUniform.PrevModelMatrix):
					return UploadUniform(buffer, prevModelMatrix);
				case nameof(EEngineUniform.RootInvModelMatrix):
					return UploadUniform(buffer, inverseModel);
				case nameof(EEngineUniform.ViewMatrix):
				case nameof(EEngineUniform.PrevViewMatrix):
				case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
				case nameof(EEngineUniform.PrevRightEyeViewMatrix):
					return UploadUniform(buffer, viewMatrix);
				case nameof(EEngineUniform.InverseViewMatrix):
					return UploadUniform(buffer, inverseViewMatrix);
				case nameof(EEngineUniform.ProjMatrix):
				case nameof(EEngineUniform.PrevProjMatrix):
				case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
				case nameof(EEngineUniform.PrevRightEyeProjMatrix):
					return UploadUniform(buffer, projMatrix);
				case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
					return UploadUniform(buffer, inverseViewMatrix);
				case nameof(EEngineUniform.RightEyeInverseViewMatrix):
					return UploadUniform(buffer, rightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrix);
				case nameof(EEngineUniform.LeftEyeProjMatrix):
					return UploadUniform(buffer, projMatrix);
				case nameof(EEngineUniform.RightEyeProjMatrix):
					return UploadUniform(buffer, rightEyeCamera?.ProjectionMatrix ?? projMatrix);
				case nameof(EEngineUniform.CameraPosition):
					return UploadUniform(buffer, ToVector4(camera?.Transform.RenderTranslation ?? Vector3.Zero));
				case nameof(EEngineUniform.CameraForward):
					return UploadUniform(buffer, ToVector4(camera?.Transform.RenderForward ?? Vector3.UnitZ));
				case nameof(EEngineUniform.CameraUp):
					return UploadUniform(buffer, ToVector4(camera?.Transform.RenderUp ?? Vector3.UnitY));
				case nameof(EEngineUniform.CameraRight):
					return UploadUniform(buffer, ToVector4(camera?.Transform.RenderRight ?? Vector3.UnitX));
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
				case nameof(EEngineUniform.ScreenWidth):
				case nameof(EEngineUniform.ScreenHeight):
					var area = Engine.Rendering.State.RenderArea;
					return UploadUniform(buffer, normalized.Equals(nameof(EEngineUniform.ScreenWidth), StringComparison.Ordinal) ? (float)area.Width : (float)area.Height);
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
		{
			if (string.IsNullOrWhiteSpace(name))
				return string.Empty;

			return name.EndsWith(VertexUniformSuffix, StringComparison.Ordinal)
				? name[..^VertexUniformSuffix.Length]
				: name;
		}

		/// <summary>
		/// Returns the byte size of a named engine uniform.
		/// Returns 0 for unrecognized names (e.g. user-defined uniforms).
		/// </summary>
		private static uint GetEngineUniformSize(string name)
		{
			string normalized = NormalizeEngineUniformName(name);
			return normalized switch
			{
				nameof(EEngineUniform.ModelMatrix) or nameof(EEngineUniform.PrevModelMatrix) or nameof(EEngineUniform.ViewMatrix) or nameof(EEngineUniform.InverseViewMatrix) or nameof(EEngineUniform.ProjMatrix) or nameof(EEngineUniform.LeftEyeInverseViewMatrix) or nameof(EEngineUniform.RightEyeInverseViewMatrix) or nameof(EEngineUniform.LeftEyeProjMatrix) or nameof(EEngineUniform.RightEyeProjMatrix) or nameof(EEngineUniform.PrevViewMatrix) or nameof(EEngineUniform.PrevLeftEyeViewMatrix) or nameof(EEngineUniform.PrevRightEyeViewMatrix) or nameof(EEngineUniform.PrevProjMatrix) or nameof(EEngineUniform.PrevLeftEyeProjMatrix) or nameof(EEngineUniform.PrevRightEyeProjMatrix) or nameof(EEngineUniform.RootInvModelMatrix) => (uint)Unsafe.SizeOf<Matrix4x4>(),
				nameof(EEngineUniform.CameraPosition) or nameof(EEngineUniform.CameraForward) or nameof(EEngineUniform.CameraUp) or nameof(EEngineUniform.CameraRight) => 16u,
				nameof(EEngineUniform.CameraNearZ) or nameof(EEngineUniform.CameraFarZ) or nameof(EEngineUniform.CameraFovX) or nameof(EEngineUniform.CameraFovY) or nameof(EEngineUniform.CameraAspect) or nameof(EEngineUniform.ScreenWidth) or nameof(EEngineUniform.ScreenHeight) or nameof(EEngineUniform.UpdateDelta) or nameof(EEngineUniform.DepthMode) or nameof(EEngineUniform.UIX) or nameof(EEngineUniform.UIY) or nameof(EEngineUniform.UIWidth) or nameof(EEngineUniform.UIHeight) => 4u,
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
			uint size = (uint)Unsafe.SizeOf<T>();
			uint copySize = Math.Min(buffer.Size, size);

			void* mapped;
			if (Api!.MapMemory(Device, buffer.Memory, 0, buffer.Size, 0, &mapped) != Result.Success)
				return false;

			T localValue = value;
			Unsafe.CopyBlock(mapped, Unsafe.AsPointer(ref localValue), copySize);
			Api.UnmapMemory(Device, buffer.Memory);
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
				EShaderVarType._vec3 when value.Value is Vector3 v3 => UploadUniform(buffer, new Vector4(v3, 0f)),
				EShaderVarType._vec4 when value.Value is Vector4 v4 => UploadUniform(buffer, v4),
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

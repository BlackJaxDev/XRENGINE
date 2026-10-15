using System;
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
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	/// <summary>
	/// Writes a typed value into auto uniform buffer memory at the member's offset.
	/// Supports scalar, vector, and matrix types in the reflected layout.
	/// </summary>
	private bool TryWriteAutoUniformValue(Span<byte> data, AutoUniformMember member, object value, EShaderVarType valueType)
	{
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformGenericConversion();
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

	private bool TryWriteAutoUniformValue(
		Span<byte> data,
		AutoUniformMember member,
		in EngineUniformValue value,
		EShaderVarType valueType)
	{
		if (value.Reference is { } reference)
			return TryWriteAutoUniformValue(data, member, reference, valueType);

		if (member.EngineType is not { } engineType || !AreCompatible(engineType, valueType))
			return false;

		int offset = (int)member.Offset;
		switch (engineType)
		{
			case EShaderVarType._float:
				Unsafe.WriteUnaligned(ref data[offset], value.Float);
				return true;
			case EShaderVarType._int:
				Unsafe.WriteUnaligned(ref data[offset], value.Int);
				return true;
			case EShaderVarType._uint:
				Unsafe.WriteUnaligned(ref data[offset], value.UInt);
				return true;
			case EShaderVarType._bool:
				int booleanValue = valueType == EShaderVarType._uint
					? value.UInt != 0u ? 1 : 0
					: value.Int != 0 ? 1 : 0;
				Unsafe.WriteUnaligned(ref data[offset], booleanValue);
				return true;
			case EShaderVarType._vec2:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector2);
				return true;
			case EShaderVarType._vec3:
				Unsafe.WriteUnaligned(ref data[offset], new Vector4(value.Vector3, 0f));
				return true;
			case EShaderVarType._vec4:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector4);
				return true;
			case EShaderVarType._mat4:
				Unsafe.WriteUnaligned(ref data[offset], value.Matrix4x4);
				return true;
			default:
				return false;
		}
	}

	/// <summary>
	/// Accepts vector size changes and conversions between Boolean and integer types.
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

	// A vec3 has a 16-byte alignment in std140. The next scalar can use
	// its fourth lane, so write only the three vector values.

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
}

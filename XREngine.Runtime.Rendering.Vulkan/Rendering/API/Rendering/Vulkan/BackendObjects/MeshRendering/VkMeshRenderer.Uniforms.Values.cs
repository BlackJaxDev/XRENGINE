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
	/// Attempts to write a single auto uniform member. Resolution priority:
	/// engine uniform value > program override > material parameter > array defaults > default value.
	/// </summary>
	private bool TryWriteAutoUniformMember(Span<byte> data, AutoUniformMember member, XRMaterial material, in PendingMeshDraw draw)
	{
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformReflectedNameLookup();
		if (member.StructMembers is { Count: > 0 })
			return TryWriteStructUniformValue(data, member, member.Name, member.Offset, draw.ProgramBindingSnapshot);

		bool wrote;
		if (TryWriteTemporalViewProjectionUniform(data, member, draw, out wrote))
			return wrote;
		if (member.Name is "LeftEyeHistoryReady" or "RightEyeHistoryReady")
		{
			EngineUniformValue ready = member.Name == "LeftEyeHistoryReady"
				? draw.LeftEyeHistoryReady ? 1 : 0
				: draw.RightEyeHistoryReady ? 1 : 0;
			return TryWriteAutoUniformValue(data, member, in ready, EShaderVarType._bool);
		}

		if (TryResolveEngineUniformValue(member.Name, draw, out EngineUniformValue engineValue, out EShaderVarType engineType))
		{
			wrote = TryWriteAutoUniformValue(data, member, in engineValue, engineType);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "engine", engineValue.ToDiagnosticObject(engineType), engineType, wrote);
			return wrote;
		}

		if (_program is not null &&
			_program.TryGetUniformValue(
				draw.ProgramBindingSnapshot,
				member.Name,
				out ProgramUniformValue programValue))
		{
			wrote = TryWriteProgramUniformValue(data, member, programValue);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "program", programValue.Value, programValue.Type, wrote);
			return wrote;
		}

		if (member.IsArray &&
			TryWriteIndexedProgramUniformArray(
				data,
				member,
				member.Name,
				draw.ProgramBindingSnapshot))
			return true;
		if (draw.ProgramBindingSnapshot is not null &&
			IsPhysicsVertexEffectControl(member.Name))
			return member.DefaultValue is not { } declared ||
				TryWriteAutoUniformValue(data, member, declared.Value, declared.Type);

		ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
		if (parameter is not null)
		{
			if (member.IsArray)
			{
				wrote = TryWriteAutoUniformArray(data, member, parameter);
				if (MaterialBindingDiagnosticsEnabled)
					LogMaterialAutoUniform(member, material, "material-array", parameter.GenericValue, parameter.TypeName, wrote);
				return wrote;
			}

			wrote = TryWriteMaterialUniformValue(data, member, parameter);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "material", parameter.GenericValue, parameter.TypeName, wrote);
			return wrote;
		}

		if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
		{
			wrote = TryWriteAutoUniformArrayDefaults(data, member);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "default-array", $"count={member.DefaultArrayValues.Count}", member.EngineType ?? EShaderVarType._float, wrote);
			return wrote;
		}

		if (member.DefaultValue is { } defaultValue)
		{
			wrote = TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "default", defaultValue.Value, defaultValue.Type, wrote);
			return wrote;
		}

		if (MaterialBindingDiagnosticsEnabled)
			LogMaterialAutoUniform(member, material, "missing", null, member.EngineType ?? EShaderVarType._float, false);
		return false;
	}

	private bool TryWriteMaterialUniformValue(Span<byte> data, AutoUniformMember member, ShaderVar parameter)
	{
		EngineUniformValue value;
		switch (parameter)
		{
			case ShaderFloat shaderFloat:
				value = shaderFloat.Value;
				break;
			case ShaderInt shaderInt:
				value = shaderInt.Value;
				break;
			case ShaderUInt shaderUInt:
				value = shaderUInt.Value;
				break;
			case ShaderVector2 shaderVector2:
				value = shaderVector2.Value;
				break;
			case ShaderVector3 shaderVector3:
				value = shaderVector3.Value;
				break;
			case ShaderVector4 shaderVector4:
				value = shaderVector4.Value;
				break;
			case ShaderMat4 shaderMatrix:
				value = shaderMatrix.Value;
				break;
			default:
				return TryWriteAutoUniformValue(data, member, parameter.GenericValue, parameter.TypeName);
		}

		return TryWriteAutoUniformValue(data, member, in value, parameter.TypeName);
	}

	private bool TryWriteTemporalViewProjectionUniform(
		Span<byte> data,
		AutoUniformMember member,
		in PendingMeshDraw draw,
		out bool wrote)
	{
		EVulkanTemporalUniformSource source = member.Name switch
		{
			"CurrViewProjection" => EVulkanTemporalUniformSource.CurrentViewProjection,
			"PrevViewProjection" => EVulkanTemporalUniformSource.PreviousViewProjection,
			"CurrViewProjectionStereo" => EVulkanTemporalUniformSource.CurrentStereoViewProjection,
			"PrevViewProjectionStereo" => EVulkanTemporalUniformSource.PreviousStereoViewProjection,
			_ => EVulkanTemporalUniformSource.None,
		};
		if (source == EVulkanTemporalUniformSource.None)
		{
			wrote = false;
			return false;
		}

		wrote = TryWriteTemporalViewProjectionUniform(data, member, source, draw);
		return true;
	}

	private bool TryWriteTemporalViewProjectionUniform(
		Span<byte> data,
		AutoUniformMember member,
		EVulkanTemporalUniformSource source,
		in PendingMeshDraw draw)
		=> source switch
		{
			EVulkanTemporalUniformSource.CurrentViewProjection =>
				TryWriteTemporalMatrix(
					data,
					member,
					draw.ViewProjectionMatrixUnjittered),
			EVulkanTemporalUniformSource.PreviousViewProjection =>
				TryWriteTemporalMatrix(
					data,
					member,
					draw.PreviousViewProjectionMatrixUnjittered),
			EVulkanTemporalUniformSource.CurrentStereoViewProjection =>
				TryWriteTemporalStereoViewProjectionUniform(
					data,
					member,
					draw.ViewProjectionMatrixUnjittered,
					draw.RightEyeViewProjectionMatrixUnjittered),
			EVulkanTemporalUniformSource.PreviousStereoViewProjection =>
				TryWriteTemporalStereoViewProjectionUniform(
					data,
					member,
					draw.PreviousViewProjectionMatrixUnjittered,
					draw.PreviousRightEyeViewProjectionMatrixUnjittered),
			_ => false,
		};

	private bool TryWriteTemporalStereoViewProjectionUniform(
		Span<byte> data,
		AutoUniformMember member,
		in Matrix4x4 left,
		in Matrix4x4 right)
	{
		if (!member.IsArray || member.ArrayLength < 2 || member.ArrayStride == 0)
			return false;

		AutoUniformMember element = member with
		{
			IsArray = false,
			ArrayLength = 0,
			ArrayStride = 0,
		};
		bool wroteLeft = TryWriteTemporalMatrix(data, element, left);
		element = element with { Offset = member.Offset + member.ArrayStride };
		bool wroteRight = TryWriteTemporalMatrix(data, element, right);
		return wroteLeft && wroteRight;
	}

	private static bool TryWriteTemporalMatrix(
		Span<byte> data,
		AutoUniformMember member,
		in Matrix4x4 matrix)
	{
		if (member.EngineType != EShaderVarType._mat4 || member.Offset + 64u > (uint)data.Length)
			return false;

		Unsafe.WriteUnaligned(ref data[(int)member.Offset], matrix);
		return true;
	}

	private bool TryWriteStructUniformValue(
		Span<byte> data,
		AutoUniformMember member,
		string uniformPrefix,
		uint baseOffset,
		ComputeDispatchSnapshot? snapshot)
	{
		if (member.StructMembers is not { Count: > 0 } fields)
			return false;

		bool wroteAny = false;
		for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
		{
			AutoUniformMember field = fields[fieldIndex];
			uint fieldOffset = baseOffset + field.Offset;
			string fieldName = StructUniformFieldNames.GetOrAdd(
				(uniformPrefix, field.Name),
				static key => $"{key.Prefix}.{key.Field}");
			AutoUniformMember absoluteField = field with { Offset = fieldOffset };

			if (field.StructMembers is { Count: > 0 })
			{
				if (field.IsArray)
					wroteAny |= TryWriteStructUniformArray(data, absoluteField, fieldName, snapshot);
				else
					wroteAny |= TryWriteStructUniformValue(data, absoluteField, fieldName, fieldOffset, snapshot);
				continue;
			}

			if (field.IsArray)
			{
				wroteAny |= TryWriteProgramUniformArray(data, absoluteField, fieldName, snapshot);
				continue;
			}

			if (_program is not null &&
				_program.TryGetUniformValue(snapshot, fieldName, out ProgramUniformValue fieldValue))
			{
				wroteAny |= TryWriteProgramUniformValue(data, absoluteField, fieldValue);
				continue;
			}

			if (field.DefaultValue is { } defaultValue)
				wroteAny |= TryWriteAutoUniformValue(data, absoluteField, defaultValue.Value, defaultValue.Type);
		}

		// Missing fields keep their zero-initialized UBO bytes, matching loose
		// GLSL struct-uniform defaults. A valid reflected struct is publishable
		// even when the current callback overrides no fields.
		return true;
	}

	private bool TryWriteStructUniformArray(
		Span<byte> data,
		AutoUniformMember member,
		string uniformPrefix,
		ComputeDispatchSnapshot? snapshot)
	{
		if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
			return false;

		bool wroteAny = false;
		for (uint i = 0; i < member.ArrayLength; i++)
		{
			uint elementOffset = member.Offset + i * member.ArrayStride;
			AutoUniformMember element = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
			string elementName = IndexedUniformNames.GetOrAdd(
				(uniformPrefix, i),
				static key => $"{key.Prefix}[{key.Index}]");
			wroteAny |= TryWriteStructUniformValue(data, element, elementName, elementOffset, snapshot);
		}

		return true;
	}

	private bool TryWriteProgramUniformArray(
		Span<byte> data,
		AutoUniformMember member,
		string uniformName,
		ComputeDispatchSnapshot? snapshot)
		=> _program is not null && _program.TryGetUniformValue(snapshot, uniformName, out ProgramUniformValue programValue)
			? TryWriteProgramUniformValue(data, member, programValue)
			: TryWriteIndexedProgramUniformArray(data, member, uniformName, snapshot);

	private bool TryWriteIndexedProgramUniformArray(
		Span<byte> data,
		AutoUniformMember member,
		string uniformName,
		ComputeDispatchSnapshot? snapshot)
	{
		if (_program is null || !member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
			return false;

		bool wroteAny = false;
		for (uint i = 0; i < member.ArrayLength; i++)
		{
			string elementName = IndexedUniformNames.GetOrAdd(
				(uniformName, i),
				static key => $"{key.Prefix}[{key.Index}]");
			if (!_program.TryGetUniformValue(snapshot, elementName, out ProgramUniformValue elementValue) || elementValue.IsArray)
				continue;

			uint elementOffset = member.Offset + i * member.ArrayStride;
			AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
			wroteAny |= TryWriteProgramUniformValue(data, elementMember, elementValue);
		}

		return wroteAny;
	}

	/// <summary>Writes a program uniform value into auto uniform buffer memory (scalar or array).</summary>
	private bool TryWriteProgramUniformValue(Span<byte> data, AutoUniformMember member, ProgramUniformValue value)
	{
		if (member.IsArray)
		{
			if (!value.IsArray || value.ReferenceValue is not Array array || member.ArrayStride == 0 || member.ArrayLength == 0)
				return false;

			if (TryWriteInlineProgramUniformArray(data, member, array, value.Type))
				return true;

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

		if (value.IsArray)
			return false;

		if (!value.HasInlineValue)
			return value.ReferenceValue is { } reference &&
				TryWriteAutoUniformValue(data, member, reference, value.Type);

		return TryWriteInlineProgramUniformValue(data, member, in value);
	}

	private bool TryWriteInlineProgramUniformArray(
		Span<byte> data,
		AutoUniformMember member,
		Array array,
		EShaderVarType valueType)
	{
		int max = Math.Min(array.Length, (int)member.ArrayLength);
		switch (array)
		{
			case float[] values when valueType == EShaderVarType._float:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case int[] values when valueType is EShaderVarType._int or EShaderVarType._bool:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case bool[] values when valueType == EShaderVarType._bool:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i] ? 1 : 0;
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case uint[] values when valueType == EShaderVarType._uint:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case Vector2[] values when valueType == EShaderVarType._vec2:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case Vector3[] values when valueType == EShaderVarType._vec3:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case Vector4[] values when valueType == EShaderVarType._vec4:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case Matrix4x4[] values when valueType == EShaderVarType._mat4:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case double[] values when valueType == EShaderVarType._double:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case DVector2[] values when valueType == EShaderVarType._dvec2:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case DVector3[] values when valueType == EShaderVarType._dvec3:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case DVector4[] values when valueType == EShaderVarType._dvec4:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case IVector2[] values when valueType == EShaderVarType._ivec2:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case IVector3[] values when valueType == EShaderVarType._ivec3:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case IVector4[] values when valueType == EShaderVarType._ivec4:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case UVector2[] values when valueType == EShaderVarType._uvec2:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case UVector3[] values when valueType == EShaderVarType._uvec3:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case UVector4[] values when valueType == EShaderVarType._uvec4:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case BoolVector2[] values when valueType == EShaderVarType._bvec2:
				for (int i = 0; i < max; i++)
				{
					int offset = (int)(member.Offset + (uint)i * member.ArrayStride);
					Unsafe.WriteUnaligned(ref data[offset], new IVector2(values[i].X ? 1 : 0, values[i].Y ? 1 : 0));
				}
				return true;
			case BoolVector3[] values when valueType == EShaderVarType._bvec3:
				for (int i = 0; i < max; i++)
				{
					int offset = (int)(member.Offset + (uint)i * member.ArrayStride);
					Unsafe.WriteUnaligned(ref data[offset], new IVector3(values[i].X ? 1 : 0, values[i].Y ? 1 : 0, values[i].Z ? 1 : 0));
				}
				return true;
			case BoolVector4[] values when valueType == EShaderVarType._bvec4:
				for (int i = 0; i < max; i++)
				{
					int offset = (int)(member.Offset + (uint)i * member.ArrayStride);
					Unsafe.WriteUnaligned(ref data[offset], new IVector4(values[i].X ? 1 : 0, values[i].Y ? 1 : 0, values[i].Z ? 1 : 0, values[i].W ? 1 : 0));
				}
				return true;
			default:
				return false;
		}
	}

	private static bool TryWriteUnmanagedProgramUniformArray<T>(
		Span<byte> data,
		AutoUniformMember member,
		T[] values,
		EShaderVarType valueType)
		where T : unmanaged
	{
		if (member.EngineType != valueType)
			return false;

		int max = Math.Min(values.Length, (int)member.ArrayLength);
		for (int i = 0; i < max; i++)
		{
			int offset = (int)(member.Offset + (uint)i * member.ArrayStride);
			Unsafe.WriteUnaligned(ref data[offset], values[i]);
		}
		return true;
	}

	private bool TryWriteInlineProgramUniformArrayElement(
		Span<byte> data,
		AutoUniformMember member,
		int index,
		in EngineUniformValue value,
		EShaderVarType valueType)
	{
		uint elementOffset = member.Offset + (uint)index * member.ArrayStride;
		AutoUniformMember elementMember = member with
		{
			Offset = elementOffset,
			IsArray = false,
			ArrayLength = 0,
			ArrayStride = 0
		};
		return TryWriteAutoUniformValue(data, elementMember, in value, valueType);
	}

	private static bool TryWriteInlineProgramUniformValue(
		Span<byte> data,
		AutoUniformMember member,
		in ProgramUniformValue value)
	{
		if (member.EngineType is not { } engineType || !AreCompatible(engineType, value.Type))
			return false;

		int offset = (int)member.Offset;
		switch (engineType)
		{
			case EShaderVarType._float when value.Type == EShaderVarType._float:
				Unsafe.WriteUnaligned(ref data[offset], value.Float);
				return true;
			case EShaderVarType._int when value.Type is EShaderVarType._int or EShaderVarType._bool:
				Unsafe.WriteUnaligned(ref data[offset], value.Int);
				return true;
			case EShaderVarType._uint when value.Type is EShaderVarType._uint or EShaderVarType._bool:
				Unsafe.WriteUnaligned(ref data[offset], value.Type == EShaderVarType._uint ? value.UInt : (uint)value.Int);
				return true;
			case EShaderVarType._bool when value.Type is EShaderVarType._int or EShaderVarType._bool:
				Unsafe.WriteUnaligned(ref data[offset], value.Int != 0 ? 1 : 0);
				return true;
			case EShaderVarType._bool when value.Type == EShaderVarType._uint:
				Unsafe.WriteUnaligned(ref data[offset], value.UInt != 0u ? 1 : 0);
				return true;
			case EShaderVarType._double when value.Type == EShaderVarType._double:
				Unsafe.WriteUnaligned(ref data[offset], value.Double);
				return true;
			case EShaderVarType._vec2 when value.Type == EShaderVarType._vec2:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector2);
				return true;
			case EShaderVarType._vec3 when value.Type == EShaderVarType._vec3:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector3);
				return true;
			case EShaderVarType._vec3 when value.Type == EShaderVarType._vec4:
				Unsafe.WriteUnaligned(ref data[offset], new Vector3(value.Vector4.X, value.Vector4.Y, value.Vector4.Z));
				return true;
			case EShaderVarType._vec4 when value.Type == EShaderVarType._vec3:
				Unsafe.WriteUnaligned(ref data[offset], new Vector4(value.Vector3, 0f));
				return true;
			case EShaderVarType._vec4 when value.Type == EShaderVarType._vec4:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector4);
				return true;
			case EShaderVarType._mat4 when value.Type == EShaderVarType._mat4:
				Unsafe.WriteUnaligned(ref data[offset], value.Matrix4x4);
				return true;
			case EShaderVarType._dvec2 when value.Type == EShaderVarType._dvec2:
				Unsafe.WriteUnaligned(ref data[offset], new DVector2(value.DVector4.X, value.DVector4.Y));
				return true;
			case EShaderVarType._dvec3 when value.Type == EShaderVarType._dvec3:
			case EShaderVarType._dvec4 when value.Type == EShaderVarType._dvec4:
				Unsafe.WriteUnaligned(ref data[offset], value.DVector4);
				return true;
			case EShaderVarType._ivec2 when value.Type == EShaderVarType._ivec2:
				Unsafe.WriteUnaligned(ref data[offset], new IVector2(value.IVector4.X, value.IVector4.Y));
				return true;
			case EShaderVarType._ivec3 when value.Type == EShaderVarType._ivec3:
			case EShaderVarType._ivec4 when value.Type == EShaderVarType._ivec4:
				Unsafe.WriteUnaligned(ref data[offset], value.IVector4);
				return true;
			case EShaderVarType._uvec2 when value.Type == EShaderVarType._uvec2:
				Unsafe.WriteUnaligned(ref data[offset], new UVector2(value.UVector4.X, value.UVector4.Y));
				return true;
			case EShaderVarType._uvec3 when value.Type == EShaderVarType._uvec3:
			case EShaderVarType._uvec4 when value.Type == EShaderVarType._uvec4:
				Unsafe.WriteUnaligned(ref data[offset], value.UVector4);
				return true;
			default:
				return false;
		}
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
	private bool TryResolveEngineUniformValue(
		string name,
		in PendingMeshDraw draw,
		out EngineUniformValue value,
		out EShaderVarType type)
	{
		string normalized = NormalizeEngineUniformName(name);
		if (TryResolveMeshStateSource(
				normalized,
				out EVulkanAutoUniformSpecialSource meshSource))
		{
			return TryResolveMeshStateUniformValue(
				meshSource,
				draw,
				out value,
				out type);
		}

		if (Enum.TryParse(
				normalized,
				ignoreCase: false,
				out EEngineUniform uniform))
		{
			return TryResolveEngineUniformValue(
				uniform,
				draw,
				out value,
				out type);
		}

		value = default;
		type = EShaderVarType._float;
		return false;
	}

	private static bool TryResolveMeshStateSource(
		string name,
		out EVulkanAutoUniformSpecialSource source)
	{
		source = name switch
		{
			TransformIdUniformName => EVulkanAutoUniformSpecialSource.TransformId,
			SkinPaletteBaseUniformName => EVulkanAutoUniformSpecialSource.SkinPaletteBase,
			SkinPaletteCountUniformName => EVulkanAutoUniformSpecialSource.SkinPaletteCount,
			SkinningInfluenceCapUniformName => EVulkanAutoUniformSpecialSource.SkinningInfluenceCap,
			BlendshapeActiveCountUniformName => EVulkanAutoUniformSpecialSource.BlendshapeActiveCount,
			BlendshapeWeightThresholdUniformName => EVulkanAutoUniformSpecialSource.BlendshapeWeightThreshold,
			UsePrecombinedBlendshapeDeltasUniformName => EVulkanAutoUniformSpecialSource.UsePrecombinedBlendshapeDeltas,
			"u_SourceFlatDirection" => EVulkanAutoUniformSpecialSource.SourceFlatDirection,
			_ => EVulkanAutoUniformSpecialSource.None,
		};
		return source != EVulkanAutoUniformSpecialSource.None;
	}

	private bool TryResolveMeshStateUniformValue(
		EVulkanAutoUniformSpecialSource source,
		in PendingMeshDraw draw,
		out EngineUniformValue value,
		out EShaderVarType type)
	{
		value = default;
		type = EShaderVarType._float;
		switch (source)
		{
			case EVulkanAutoUniformSpecialSource.TransformId:
				value = draw.TransformId;
				type = EShaderVarType._uint;
				return true;
			case EVulkanAutoUniformSpecialSource.SourceFlatDirection:
				value = draw.SourceFlatDirection;
				type = EShaderVarType._vec4;
				return true;
			case EVulkanAutoUniformSpecialSource.SkinPaletteBase:
				value = draw.AutoUniformPublication.SkinPaletteBase;
				type = EShaderVarType._uint;
				return true;
			case EVulkanAutoUniformSpecialSource.SkinPaletteCount:
				value = draw.AutoUniformPublication.SkinPaletteCount;
				type = EShaderVarType._uint;
				return true;
			case EVulkanAutoUniformSpecialSource.SkinningInfluenceCap:
				value = draw.AutoUniformPublication.SkinningInfluenceCap;
				type = EShaderVarType._int;
				return true;
			case EVulkanAutoUniformSpecialSource.BlendshapeActiveCount:
				value = draw.AutoUniformPublication.BlendshapeActiveCount;
				type = EShaderVarType._int;
				return true;
			case EVulkanAutoUniformSpecialSource.BlendshapeWeightThreshold:
				value = draw.AutoUniformPublication.BlendshapeWeightThreshold;
				type = EShaderVarType._float;
				return true;
			case EVulkanAutoUniformSpecialSource.UsePrecombinedBlendshapeDeltas:
				value = RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass &&
					!RuntimeEngine.Rendering.State.IsVulkan &&
					draw.AutoUniformPublication.HasValidPrecombinedBlendshapeDeltas
						? 1
						: 0;
				type = EShaderVarType._int;
				return true;
			default:
				return false;
		}
	}

	private bool TryResolveEngineUniformValue(
		EEngineUniform uniform,
		in PendingMeshDraw draw,
		out EngineUniformValue value,
		out EShaderVarType type)
	{
		value = default;
		type = EShaderVarType._float;

		switch (uniform)
		{
			case EEngineUniform.UpdateDelta:
				value = CommandOperations.MaterialUniformUpdateDelta;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.RenderTime:
				value = CommandOperations.MaterialUniformSeconds;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.EngineTime:
				value = CommandOperations.MaterialUniformSeconds;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.DeltaTime:
				value = CommandOperations.MaterialUniformRenderDelta;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.ModelMatrix:
				value = draw.ModelMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevModelMatrix:
				value = draw.PreviousModelMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RootInvModelMatrix:
				Matrix4x4.Invert(draw.ModelMatrix, out Matrix4x4 inverseModel);
				value = inverseModel;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.ViewMatrix:
			case EEngineUniform.LeftEyeViewMatrix:
				value = draw.ViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevViewMatrix:
			case EEngineUniform.PrevLeftEyeViewMatrix:
				value = draw.PreviousViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeViewMatrix:
				value = draw.RightEyeViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevRightEyeViewMatrix:
				value = draw.PreviousRightEyeViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.InverseViewMatrix:
				value = draw.InverseViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.InverseProjMatrix:
				value = draw.InverseProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.ViewProjectionMatrix:
				value = draw.ViewProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.ProjMatrix:
				value = draw.ProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevProjMatrix:
			case EEngineUniform.PrevLeftEyeProjMatrix:
				value = draw.PreviousProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevRightEyeProjMatrix:
				value = draw.PreviousRightEyeProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.LeftEyeViewProjectionMatrix:
				value = draw.ViewProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.LeftEyeInverseViewMatrix:
				value = draw.InverseViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.LeftEyeInverseProjMatrix:
				value = draw.InverseProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeInverseViewMatrix:
				value = draw.RightEyeInverseViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeInverseProjMatrix:
				value = draw.RightEyeInverseProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeViewProjectionMatrix:
				value = draw.RightEyeViewProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.LeftEyeProjMatrix:
				value = draw.ProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeProjMatrix:
				value = draw.RightEyeProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.CameraPosition:
				value = ToVector4(draw.CameraPosition);
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.CameraForward:
				value = ToVector4(draw.CameraForward);
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.CameraUp:
				value = ToVector4(draw.CameraUp);
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.CameraRight:
				value = ToVector4(draw.CameraRight);
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.CameraNearZ:
				value = draw.AutoUniformPublication.CameraNearZ;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.CameraFarZ:
				value = draw.AutoUniformPublication.CameraFarZ;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.CameraFovX:
				value = draw.AutoUniformPublication.CameraFovX;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.CameraFovY:
				value = draw.AutoUniformPublication.CameraFovY;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.CameraAspect:
				value = draw.AutoUniformPublication.CameraAspect;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.DepthMode:
				value = (int)draw.AutoUniformPublication.CameraDepthMode;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.ClipSpaceYDirection:
				value = (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.ClipDepthRange:
				value = (int)RuntimeEngine.Rendering.EffectiveClipDepthRange;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.FramebufferTextureYDirection:
				value = (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan);
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.ScreenWidth:
			case EEngineUniform.ScreenHeight:
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
				value = uniform == EEngineUniform.ScreenWidth ? screenW : screenH;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.ScreenOrigin:
				value = new Vector2(0f, 0f);
				type = EShaderVarType._vec2;
				return true;
			case EEngineUniform.BillboardMode:
				value = (int)draw.BillboardMode;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.VRMode:
				value = draw.IsStereoPass ? 1 : 0;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.UIXYWH:
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						nameof(EEngineUniform.UIXYWH),
						out ProgramUniformValue bounds))
				{
					value = EngineUniformValue.FromProgramValue(in bounds);
					type = bounds.Type;
					return true;
				}
				value = Vector4.Zero;
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.UIWidth:
			case EEngineUniform.UIHeight:
			case EEngineUniform.UIX:
			case EEngineUniform.UIY:
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						uniform.ToStringFast(),
						out ProgramUniformValue uiScalar))
				{
					value = EngineUniformValue.FromProgramValue(in uiScalar);
					type = uiScalar.Type;
					return true;
				}
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						nameof(EEngineUniform.UIXYWH),
						out ProgramUniformValue uiBounds) &&
					uiBounds.HasInlineValue && uiBounds.Type == EShaderVarType._vec4)
				{
					Vector4 b = uiBounds.Vector4;
					value = uniform switch
					{
						EEngineUniform.UIX => b.X,
						EEngineUniform.UIY => b.Y,
						EEngineUniform.UIWidth => b.Z,
						EEngineUniform.UIHeight => b.W,
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
}

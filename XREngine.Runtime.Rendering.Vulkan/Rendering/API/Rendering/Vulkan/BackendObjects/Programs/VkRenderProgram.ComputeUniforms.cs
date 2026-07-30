using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Vectors;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram
{
    private bool TryWriteAutoUniformMember(Span<byte> destination, AutoUniformMember member, ComputeDispatchSnapshot snapshot)
    {
        if (member.Offset >= (uint)destination.Length)
            return false;

        if (snapshot.Uniforms.TryGetValue(member.Name, out ProgramUniformValue value))
            return TryWriteUniformValue(destination, member, value);

        if (TryResolveEngineUniform(member.Name, out ProgramUniformValue engineValue))
            return TryWriteUniformValue(destination, member, engineValue);

        if (member.DefaultValue is AutoUniformDefaultValue defaultValue)
        {
            ProgramUniformValue val = new(defaultValue.Type, defaultValue.Value, false);
            return TryWriteUniformValue(destination, member, val);
        }

        if (member.DefaultArrayValues is { Count: > 0 } defaults)
        {
            if (!member.IsArray || member.ArrayLength == 0 || member.ArrayStride == 0)
                return false;

            int count = Math.Min(defaults.Count, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                AutoUniformDefaultValue defaultElement = defaults[i];
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                TryWriteSingleUniform(destination, offset, defaultElement.Type, defaultElement.Value);
            }

            return true;
        }

        return false;
    }

    private bool TryResolveEngineUniform(string name, out ProgramUniformValue value)
    {
        value = default;

        ReadOnlySpan<char> uniform = name.AsSpan();
        const string vertexStageSuffix = "_VTX";
        if (uniform.EndsWith(vertexStageSuffix, StringComparison.Ordinal))
            uniform = uniform[..^vertexStageSuffix.Length];

        XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;
        XRCamera? rightCamera = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
        bool stereo = RuntimeEngine.Rendering.State.IsStereoPass;
        var area = RuntimeEngine.Rendering.State.RenderArea;

        switch (uniform)
        {
            case nameof(EEngineUniform.UpdateDelta):
                value = new ProgramUniformValue(EShaderVarType._float, RuntimeEngine.Time.Timer.Update.Delta, false);
                return true;
            case nameof(EEngineUniform.ViewMatrix):
            case nameof(EEngineUniform.LeftEyeViewMatrix):
                value = new ProgramUniformValue(EShaderVarType._mat4, camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity, false);
                return true;
            case nameof(EEngineUniform.PrevViewMatrix):
            case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
                value = new ProgramUniformValue(
                    EShaderVarType._mat4,
                    VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalViewData) && temporalViewData.HistoryReady
                        ? temporalViewData.PrevViewMatrix
                        : camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity,
                    false);
                return true;
            case nameof(EEngineUniform.RightEyeViewMatrix):
                value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.Transform.InverseRenderMatrix ?? camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity, false);
                return true;
            case nameof(EEngineUniform.PrevRightEyeViewMatrix):
                value = new ProgramUniformValue(
                    EShaderVarType._mat4,
                    VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalRightViewData) && temporalRightViewData.HistoryReady
                        ? temporalRightViewData.RightEyePrevViewMatrix
                        : rightCamera?.Transform.InverseRenderMatrix ?? camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity,
                    false);
                return true;
            case nameof(EEngineUniform.InverseViewMatrix):
            case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
                value = new ProgramUniformValue(EShaderVarType._mat4, camera?.Transform.RenderMatrix ?? Matrix4x4.Identity, false);
                return true;
            case nameof(EEngineUniform.RightEyeInverseViewMatrix):
                value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.Transform.RenderMatrix ?? camera?.Transform.RenderMatrix ?? Matrix4x4.Identity, false);
                return true;
            case nameof(EEngineUniform.InverseProjMatrix):
            case nameof(EEngineUniform.LeftEyeInverseProjMatrix):
                value = new ProgramUniformValue(
                    EShaderVarType._mat4,
                    camera?.InverseProjectionMatrix ?? Matrix4x4.Identity,
                    false);
                return true;
            case nameof(EEngineUniform.RightEyeInverseProjMatrix):
                value = new ProgramUniformValue(
                    EShaderVarType._mat4,
                    rightCamera?.InverseProjectionMatrix ?? camera?.InverseProjectionMatrix ?? Matrix4x4.Identity,
                    false);
                return true;
            case nameof(EEngineUniform.ViewProjectionMatrix):
            case nameof(EEngineUniform.LeftEyeViewProjectionMatrix):
                value = new ProgramUniformValue(EShaderVarType._mat4, camera?.ViewProjectionMatrix ?? Matrix4x4.Identity, false);
                return true;
            case nameof(EEngineUniform.RightEyeViewProjectionMatrix):
                value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.ViewProjectionMatrix ?? camera?.ViewProjectionMatrix ?? Matrix4x4.Identity, false);
                return true;
            case nameof(EEngineUniform.ProjMatrix):
            case nameof(EEngineUniform.LeftEyeProjMatrix):
                value = new ProgramUniformValue(EShaderVarType._mat4, camera?.ProjectionMatrix ?? Matrix4x4.Identity, false);
                return true;
            case nameof(EEngineUniform.PrevProjMatrix):
            case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
                value = new ProgramUniformValue(
                    EShaderVarType._mat4,
                    VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalProjectionData) && temporalProjectionData.HistoryReady
                        ? temporalProjectionData.PrevProjection
                        : camera?.ProjectionMatrix ?? Matrix4x4.Identity,
                    false);
                return true;
            case nameof(EEngineUniform.RightEyeProjMatrix):
                value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.ProjectionMatrix ?? camera?.ProjectionMatrix ?? Matrix4x4.Identity, false);
                return true;
            case nameof(EEngineUniform.PrevRightEyeProjMatrix):
                value = new ProgramUniformValue(
                    EShaderVarType._mat4,
                    VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalRightProjectionData) && temporalRightProjectionData.HistoryReady
                        ? temporalRightProjectionData.RightEyePrevProjection
                        : rightCamera?.ProjectionMatrix ?? camera?.ProjectionMatrix ?? Matrix4x4.Identity,
                    false);
                return true;
            case nameof(EEngineUniform.CameraPosition):
                value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderTranslation ?? Vector3.Zero, false);
                return true;
            case nameof(EEngineUniform.CameraForward):
                value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderForward ?? Vector3.UnitZ, false);
                return true;
            case nameof(EEngineUniform.CameraUp):
                value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderUp ?? Vector3.UnitY, false);
                return true;
            case nameof(EEngineUniform.CameraRight):
                value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderRight ?? Vector3.UnitX, false);
                return true;
            case nameof(EEngineUniform.CameraNearZ):
                value = new ProgramUniformValue(EShaderVarType._float, camera?.NearZ ?? 0f, false);
                return true;
            case nameof(EEngineUniform.CameraFarZ):
                value = new ProgramUniformValue(EShaderVarType._float, camera?.FarZ ?? 0f, false);
                return true;
            case nameof(EEngineUniform.ScreenWidth):
                value = new ProgramUniformValue(EShaderVarType._float, (float)area.Width, false);
                return true;
            case nameof(EEngineUniform.ScreenHeight):
                value = new ProgramUniformValue(EShaderVarType._float, (float)area.Height, false);
                return true;
            case nameof(EEngineUniform.ScreenOrigin):
                value = new ProgramUniformValue(EShaderVarType._vec2, Vector2.Zero, false);
                return true;
            case nameof(EEngineUniform.DepthMode):
                value = new ProgramUniformValue(EShaderVarType._int, (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal), false);
                return true;
            case nameof(EEngineUniform.ClipSpaceYDirection):
                value = new ProgramUniformValue(EShaderVarType._int, (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection, false);
                return true;
            case nameof(EEngineUniform.ClipDepthRange):
                value = new ProgramUniformValue(EShaderVarType._int, (int)RuntimeEngine.Rendering.EffectiveClipDepthRange, false);
                return true;
            case nameof(EEngineUniform.FramebufferTextureYDirection):
                value = new ProgramUniformValue(EShaderVarType._int, (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan), false);
                return true;
            case nameof(EEngineUniform.VRMode):
                value = new ProgramUniformValue(EShaderVarType._int, stereo ? 1 : 0, false);
                return true;
        }

        return false;
    }

    private static bool TryWriteUniformValue(Span<byte> destination, AutoUniformMember member, ProgramUniformValue value)
        => member.IsArray
            ? TryWriteUniformArray(destination, member, value)
            : TryWriteSingleUniform(destination, member.Offset, value);

    private static bool TryWriteSingleUniform(Span<byte> destination, uint offset, in ProgramUniformValue value)
    {
        if (!value.HasInlineValue)
            return value.ReferenceValue is { } reference &&
                TryWriteSingleUniform(destination, offset, value.Type, reference);

        if (offset >= (uint)destination.Length)
            return false;

        ref byte start = ref destination[(int)offset];
        switch (value.Type)
        {
            case EShaderVarType._float:
                Unsafe.WriteUnaligned(ref start, value.Float);
                return true;
            case EShaderVarType._int:
                Unsafe.WriteUnaligned(ref start, value.Int);
                return true;
            case EShaderVarType._uint:
                Unsafe.WriteUnaligned(ref start, value.UInt);
                return true;
            case EShaderVarType._bool:
                Unsafe.WriteUnaligned(ref start, value.Int != 0 ? 1 : 0);
                return true;
            case EShaderVarType._double:
                Unsafe.WriteUnaligned(ref start, value.Double);
                return true;
            case EShaderVarType._vec2:
                Unsafe.WriteUnaligned(ref start, value.Vector2);
                return true;
            case EShaderVarType._vec3:
                Unsafe.WriteUnaligned(ref start, new Vector4(value.Vector3, 0f));
                return true;
            case EShaderVarType._vec4:
                Unsafe.WriteUnaligned(ref start, value.Vector4);
                return true;
            case EShaderVarType._dvec2:
                Unsafe.WriteUnaligned(ref start, new DVector2(value.DVector4.X, value.DVector4.Y));
                return true;
            case EShaderVarType._dvec3:
            case EShaderVarType._dvec4:
                Unsafe.WriteUnaligned(ref start, value.DVector4);
                return true;
            case EShaderVarType._ivec2:
                Unsafe.WriteUnaligned(ref start, new IVector2(value.IVector4.X, value.IVector4.Y));
                return true;
            case EShaderVarType._ivec3:
            case EShaderVarType._ivec4:
                Unsafe.WriteUnaligned(ref start, value.IVector4);
                return true;
            case EShaderVarType._uvec2:
                Unsafe.WriteUnaligned(ref start, new UVector2(value.UVector4.X, value.UVector4.Y));
                return true;
            case EShaderVarType._uvec3:
            case EShaderVarType._uvec4:
                Unsafe.WriteUnaligned(ref start, value.UVector4);
                return true;
            case EShaderVarType._mat4:
                Unsafe.WriteUnaligned(ref start, value.Matrix4x4);
                return true;
            default:
                return false;
        }
    }

    private static bool TryWriteUniformArray(Span<byte> destination, AutoUniformMember member, ProgramUniformValue value)
    {
        if (!value.IsArray || member.ArrayLength == 0 || member.ArrayStride == 0)
            return false;

        object? arrayValue = value.ReferenceValue;
        return arrayValue switch
        {
            float[] values when value.Type == EShaderVarType._float
                => TryWriteUnmanagedUniformArray(destination, member, values),
            int[] values when value.Type is EShaderVarType._int or EShaderVarType._bool
                => TryWriteUnmanagedUniformArray(destination, member, values),
            uint[] values when value.Type == EShaderVarType._uint
                => TryWriteUnmanagedUniformArray(destination, member, values),
            bool[] values when value.Type == EShaderVarType._bool
                => TryWriteBooleanUniformArray(destination, member, values),
            double[] values when value.Type == EShaderVarType._double
                => TryWriteUnmanagedUniformArray(destination, member, values),
            Vector2[] values when value.Type == EShaderVarType._vec2
                => TryWriteUnmanagedUniformArray(destination, member, values),
            Vector3[] values when value.Type == EShaderVarType._vec3
                => TryWriteVector3UniformArray(destination, member, values),
            Vector4[] values when value.Type == EShaderVarType._vec4
                => TryWriteUnmanagedUniformArray(destination, member, values),
            Matrix4x4[] values when value.Type == EShaderVarType._mat4
                => TryWriteUnmanagedUniformArray(destination, member, values),
            DVector2[] values when value.Type == EShaderVarType._dvec2
                => TryWriteUnmanagedUniformArray(destination, member, values),
            DVector3[] values when value.Type == EShaderVarType._dvec3
                => TryWriteDVector3UniformArray(destination, member, values),
            DVector4[] values when value.Type == EShaderVarType._dvec4
                => TryWriteUnmanagedUniformArray(destination, member, values),
            IVector2[] values when value.Type == EShaderVarType._ivec2
                => TryWriteUnmanagedUniformArray(destination, member, values),
            IVector3[] values when value.Type == EShaderVarType._ivec3
                => TryWriteIVector3UniformArray(destination, member, values),
            IVector4[] values when value.Type == EShaderVarType._ivec4
                => TryWriteUnmanagedUniformArray(destination, member, values),
            UVector2[] values when value.Type == EShaderVarType._uvec2
                => TryWriteUnmanagedUniformArray(destination, member, values),
            UVector3[] values when value.Type == EShaderVarType._uvec3
                => TryWriteUVector3UniformArray(destination, member, values),
            UVector4[] values when value.Type == EShaderVarType._uvec4
                => TryWriteUnmanagedUniformArray(destination, member, values),
            BoolVector2[] values when value.Type == EShaderVarType._bvec2
                => TryWriteBoolVector2UniformArray(destination, member, values),
            BoolVector3[] values when value.Type == EShaderVarType._bvec3
                => TryWriteBoolVector3UniformArray(destination, member, values),
            BoolVector4[] values when value.Type == EShaderVarType._bvec4
                => TryWriteBoolVector4UniformArray(destination, member, values),
            object?[] values => TryWriteReferenceUniformArray(destination, member, value.Type, values),
            _ => false,
        };
    }

    private static bool TryWriteUnmanagedUniformArray<T>(Span<byte> destination, AutoUniformMember member, T[] values)
        where T : unmanaged
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            if (!TryWriteUniformArrayElement(destination, offset, values[i]))
                return false;
        }

        return true;
    }

    private static bool TryWriteBooleanUniformArray(Span<byte> destination, AutoUniformMember member, bool[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            if (!TryWriteUniformArrayElement(destination, offset, values[i] ? 1 : 0))
                return false;
        }

        return true;
    }

    private static bool TryWriteVector3UniformArray(Span<byte> destination, AutoUniformMember member, Vector3[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            if (!TryWriteUniformArrayElement(destination, offset, new Vector4(values[i], 0f)))
                return false;
        }

        return true;
    }

    private static bool TryWriteDVector3UniformArray(Span<byte> destination, AutoUniformMember member, DVector3[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            DVector3 vector = values[i];
            if (!TryWriteUniformArrayElement(destination, offset, new DVector4(vector.X, vector.Y, vector.Z, 0.0)))
                return false;
        }

        return true;
    }

    private static bool TryWriteIVector3UniformArray(Span<byte> destination, AutoUniformMember member, IVector3[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            IVector3 vector = values[i];
            if (!TryWriteUniformArrayElement(destination, offset, new IVector4(vector.X, vector.Y, vector.Z, 0)))
                return false;
        }

        return true;
    }

    private static bool TryWriteUVector3UniformArray(Span<byte> destination, AutoUniformMember member, UVector3[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            UVector3 vector = values[i];
            if (!TryWriteUniformArrayElement(destination, offset, new UVector4(vector.X, vector.Y, vector.Z, 0)))
                return false;
        }

        return true;
    }

    private static bool TryWriteBoolVector2UniformArray(Span<byte> destination, AutoUniformMember member, BoolVector2[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            BoolVector2 vector = values[i];
            if (!TryWriteUniformArrayElement(destination, offset, new IVector2(vector.X ? 1 : 0, vector.Y ? 1 : 0)))
                return false;
        }

        return true;
    }

    private static bool TryWriteBoolVector3UniformArray(Span<byte> destination, AutoUniformMember member, BoolVector3[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            BoolVector3 vector = values[i];
            if (!TryWriteUniformArrayElement(destination, offset, new IVector4(vector.X ? 1 : 0, vector.Y ? 1 : 0, vector.Z ? 1 : 0, 0)))
                return false;
        }

        return true;
    }

    private static bool TryWriteBoolVector4UniformArray(Span<byte> destination, AutoUniformMember member, BoolVector4[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            uint offset = member.Offset + (uint)i * member.ArrayStride;
            BoolVector4 vector = values[i];
            if (!TryWriteUniformArrayElement(destination, offset, new IVector4(vector.X ? 1 : 0, vector.Y ? 1 : 0, vector.Z ? 1 : 0, vector.W ? 1 : 0)))
                return false;
        }

        return true;
    }

    private static bool TryWriteReferenceUniformArray(
        Span<byte> destination,
        AutoUniformMember member,
        EShaderVarType type,
        object?[] values)
    {
        int count = Math.Min(values.Length, (int)member.ArrayLength);
        for (int i = 0; i < count; i++)
        {
            object? element = values[i];
            if (element is null)
                continue;

            uint offset = member.Offset + (uint)i * member.ArrayStride;
            if (!TryWriteSingleUniform(destination, offset, type, element))
                return false;
        }

        return true;
    }

    private static bool TryWriteUniformArrayElement<T>(Span<byte> destination, uint offset, T value)
        where T : unmanaged
    {
        if (offset > (uint)destination.Length || Unsafe.SizeOf<T>() > destination.Length - (int)offset)
            return false;

        Unsafe.WriteUnaligned(ref destination[(int)offset], value);
        return true;
    }

    private static bool TryWriteSingleUniform(Span<byte> destination, uint offset, EShaderVarType type, object value)
    {
        if (offset >= (uint)destination.Length)
            return false;

        ref byte start = ref destination[(int)offset];
        switch (type)
        {
            case EShaderVarType._float:
                Unsafe.WriteUnaligned(ref start, Convert.ToSingle(value));
                return true;
            case EShaderVarType._int:
                Unsafe.WriteUnaligned(ref start, Convert.ToInt32(value));
                return true;
            case EShaderVarType._uint:
                Unsafe.WriteUnaligned(ref start, Convert.ToUInt32(value));
                return true;
            case EShaderVarType._bool:
                Unsafe.WriteUnaligned(ref start, Convert.ToBoolean(value) ? 1 : 0);
                return true;
            case EShaderVarType._vec2:
                if (value is Vector2 v2)
                {
                    Unsafe.WriteUnaligned(ref start, v2);
                    return true;
                }
                break;
            case EShaderVarType._vec3:
                if (value is Vector3 v3)
                {
                    Unsafe.WriteUnaligned(ref start, new Vector4(v3, 0f));
                    return true;
                }
                if (value is Vector4 v3From4)
                {
                    Unsafe.WriteUnaligned(ref start, v3From4);
                    return true;
                }
                if (value is ColorF3 c3)
                {
                    Unsafe.WriteUnaligned(ref start, new Vector4(c3.R, c3.G, c3.B, 0f));
                    return true;
                }
                if (value is ColorF4 c3From4)
                {
                    Unsafe.WriteUnaligned(ref start, new Vector4(c3From4.R, c3From4.G, c3From4.B, 0f));
                    return true;
                }
                break;
            case EShaderVarType._vec4:
                if (value is Vector4 v4)
                {
                    Unsafe.WriteUnaligned(ref start, v4);
                    return true;
                }
                if (value is Vector3 v4From3)
                {
                    Unsafe.WriteUnaligned(ref start, new Vector4(v4From3, 0f));
                    return true;
                }
                if (value is ColorF4 c4)
                {
                    Unsafe.WriteUnaligned(ref start, new Vector4(c4.R, c4.G, c4.B, c4.A));
                    return true;
                }
                if (value is ColorF3 c4From3)
                {
                    Unsafe.WriteUnaligned(ref start, new Vector4(c4From3.R, c4From3.G, c4From3.B, 0f));
                    return true;
                }
                break;
            case EShaderVarType._ivec2:
                if (value is IVector2 iv2)
                {
                    Unsafe.WriteUnaligned(ref start, iv2);
                    return true;
                }
                break;
            case EShaderVarType._ivec3:
                if (value is IVector3 iv3)
                {
                    Unsafe.WriteUnaligned(ref start, new IVector4(iv3.X, iv3.Y, iv3.Z, 0));
                    return true;
                }
                break;
            case EShaderVarType._ivec4:
                if (value is IVector4 iv4)
                {
                    Unsafe.WriteUnaligned(ref start, iv4);
                    return true;
                }
                break;
            case EShaderVarType._uvec2:
                if (value is UVector2 uv2)
                {
                    Unsafe.WriteUnaligned(ref start, uv2);
                    return true;
                }
                break;
            case EShaderVarType._uvec3:
                if (value is UVector3 uv3)
                {
                    Unsafe.WriteUnaligned(ref start, new UVector4(uv3.X, uv3.Y, uv3.Z, 0));
                    return true;
                }
                break;
            case EShaderVarType._uvec4:
                if (value is UVector4 uv4)
                {
                    Unsafe.WriteUnaligned(ref start, uv4);
                    return true;
                }
                break;
            case EShaderVarType._mat4:
                if (value is Matrix4x4 mat)
                {
                    Unsafe.WriteUnaligned(ref start, mat);
                    return true;
                }
                break;
            case EShaderVarType._dvec2:
                if (value is DVector2 dv2)
                {
                    Unsafe.WriteUnaligned(ref start, dv2);
                    return true;
                }
                break;
            case EShaderVarType._dvec3:
                if (value is DVector3 dv3)
                {
                    Unsafe.WriteUnaligned(ref start, new DVector4(dv3.X, dv3.Y, dv3.Z, 0.0));
                    return true;
                }
                break;
            case EShaderVarType._dvec4:
                if (value is DVector4 dv4)
                {
                    Unsafe.WriteUnaligned(ref start, dv4);
                    return true;
                }
                break;
            case EShaderVarType._double:
                Unsafe.WriteUnaligned(ref start, Convert.ToDouble(value));
                return true;
        }

        return false;
    }

    private void WarnComputeOnce(string message)
    {
        if (_computeWarnings.TryAdd(message, 0))
            Debug.VulkanWarning($"[VkCompute:{Data.Name ?? "UnnamedProgram"}] {message}");
    }

}

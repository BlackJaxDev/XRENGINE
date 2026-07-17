using System.Numerics;

using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Stores common scalar program uniforms inline so publishing per-draw values
    /// does not box them. Arrays and uncommon value types retain their existing
    /// reference representation.
    /// </summary>
    internal readonly struct ProgramUniformValue
    {
        private readonly object? _referenceValue;

        public ProgramUniformValue(EShaderVarType type, object value, bool isArray)
        {
            Type = type;
            IsArray = isArray;
            _referenceValue = value;
        }

        public ProgramUniformValue(EShaderVarType type, float value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            Float = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, int value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            Int = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, uint value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            UInt = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, bool value, bool isArray = false)
            : this(type, value ? 1 : 0, isArray)
        {
        }

        public ProgramUniformValue(EShaderVarType type, Vector2 value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            Vector2 = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, Vector3 value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            Vector3 = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, Vector4 value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            Vector4 = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, Matrix4x4 value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            Matrix4x4 = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, double value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            Double = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, DVector2 value, bool isArray = false)
            : this(type, new DVector4(value.X, value.Y, 0.0, 0.0), isArray)
        {
        }

        public ProgramUniformValue(EShaderVarType type, DVector3 value, bool isArray = false)
            : this(type, new DVector4(value.X, value.Y, value.Z, 0.0), isArray)
        {
        }

        public ProgramUniformValue(EShaderVarType type, DVector4 value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            DVector4 = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, IVector2 value, bool isArray = false)
            : this(type, new IVector4(value.X, value.Y, 0, 0), isArray)
        {
        }

        public ProgramUniformValue(EShaderVarType type, IVector3 value, bool isArray = false)
            : this(type, new IVector4(value.X, value.Y, value.Z, 0), isArray)
        {
        }

        public ProgramUniformValue(EShaderVarType type, IVector4 value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            IVector4 = value;
            HasInlineValue = true;
        }

        public ProgramUniformValue(EShaderVarType type, UVector2 value, bool isArray = false)
            : this(type, new UVector4(value.X, value.Y, 0, 0), isArray)
        {
        }

        public ProgramUniformValue(EShaderVarType type, UVector3 value, bool isArray = false)
            : this(type, new UVector4(value.X, value.Y, value.Z, 0), isArray)
        {
        }

        public ProgramUniformValue(EShaderVarType type, UVector4 value, bool isArray = false)
        {
            Type = type;
            IsArray = isArray;
            UVector4 = value;
            HasInlineValue = true;
        }

        public EShaderVarType Type { get; }
        public bool IsArray { get; }
        public bool HasInlineValue { get; }
        public object? ReferenceValue => _referenceValue;
        public float Float { get; }
        public int Int { get; }
        public uint UInt { get; }
        public double Double { get; }
        public Vector2 Vector2 { get; }
        public Vector3 Vector3 { get; }
        public Vector4 Vector4 { get; }
        public Matrix4x4 Matrix4x4 { get; }
        public DVector4 DVector4 { get; }
        public IVector4 IVector4 { get; }
        public UVector4 UVector4 { get; }

        public object Value
            => _referenceValue ?? Type switch
            {
                EShaderVarType._float => Float,
                EShaderVarType._int or EShaderVarType._bool => Int,
                EShaderVarType._uint => UInt,
                EShaderVarType._double => Double,
                EShaderVarType._vec2 => Vector2,
                EShaderVarType._vec3 => Vector3,
                EShaderVarType._vec4 => Vector4,
                EShaderVarType._mat4 => Matrix4x4,
                EShaderVarType._dvec2 => new DVector2(DVector4.X, DVector4.Y),
                EShaderVarType._dvec3 => new DVector3(DVector4.X, DVector4.Y, DVector4.Z),
                EShaderVarType._dvec4 => DVector4,
                EShaderVarType._ivec2 => new IVector2(IVector4.X, IVector4.Y),
                EShaderVarType._ivec3 => new IVector3(IVector4.X, IVector4.Y, IVector4.Z),
                EShaderVarType._ivec4 => IVector4,
                EShaderVarType._uvec2 => new UVector2(UVector4.X, UVector4.Y),
                EShaderVarType._uvec3 => new UVector3(UVector4.X, UVector4.Y, UVector4.Z),
                EShaderVarType._uvec4 => UVector4,
                _ => throw new InvalidOperationException($"Program uniform type '{Type}' has no stored value."),
            };
    }
}

using System.Numerics;

using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stores common scalar program uniforms inline so publishing per-draw values
/// does not box them. Arrays and uncommon value types retain their existing
/// reference representation.
/// </summary>
internal readonly struct ProgramUniformValue
{
    private readonly object? _referenceValue;
    private readonly ProgramUniformInlineValue _inlineValue;
    private readonly EProgramUniformInlineKind _inlineKind;

    public ProgramUniformValue(EShaderVarType type, object value, bool isArray)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _referenceValue = value;
    }

    public ProgramUniformValue(EShaderVarType type, float value, bool isArray = false)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { Float = value };
        _inlineKind = EProgramUniformInlineKind.Float;
    }

    public ProgramUniformValue(EShaderVarType type, int value, bool isArray = false)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { Int = value };
        _inlineKind = EProgramUniformInlineKind.Int;
    }

    public ProgramUniformValue(EShaderVarType type, uint value, bool isArray = false)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { UInt = value };
        _inlineKind = EProgramUniformInlineKind.UInt;
    }

    public ProgramUniformValue(EShaderVarType type, bool value, bool isArray = false)
        : this(type, value ? 1 : 0, isArray)
    {
    }

    public ProgramUniformValue(EShaderVarType type, Vector2 value, bool isArray = false)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { Vector2 = value };
        _inlineKind = EProgramUniformInlineKind.Vector2;
    }

    public ProgramUniformValue(EShaderVarType type, Vector3 value, bool isArray = false)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { Vector3 = value };
        _inlineKind = EProgramUniformInlineKind.Vector3;
    }

    public ProgramUniformValue(EShaderVarType type, Vector4 value, bool isArray = false)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { Vector4 = value };
        _inlineKind = EProgramUniformInlineKind.Vector4;
    }

    public ProgramUniformValue(EShaderVarType type, Matrix4x4 value, bool isArray = false)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { Matrix4x4 = value };
        _inlineKind = EProgramUniformInlineKind.Matrix4x4;
    }

    public ProgramUniformValue(EShaderVarType type, double value, bool isArray = false)
    {
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { Double = value };
        _inlineKind = EProgramUniformInlineKind.Double;
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
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { DVector4 = value };
        _inlineKind = EProgramUniformInlineKind.DVector4;
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
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { IVector4 = value };
        _inlineKind = EProgramUniformInlineKind.IVector4;
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
        this = default;
        Type = type;
        IsArray = isArray;
        _inlineValue = new() { UVector4 = value };
        _inlineKind = EProgramUniformInlineKind.UVector4;
    }

    public EShaderVarType Type { get; }
    public bool IsArray { get; }
    public bool HasInlineValue => _inlineKind != EProgramUniformInlineKind.None;
    public object? ReferenceValue => _referenceValue;
    public float Float => _inlineKind == EProgramUniformInlineKind.Float ? _inlineValue.Float : default;
    public int Int => _inlineKind == EProgramUniformInlineKind.Int ? _inlineValue.Int : default;
    public uint UInt => _inlineKind == EProgramUniformInlineKind.UInt ? _inlineValue.UInt : default;
    public double Double => _inlineKind == EProgramUniformInlineKind.Double ? _inlineValue.Double : default;
    public Vector2 Vector2 => _inlineKind == EProgramUniformInlineKind.Vector2 ? _inlineValue.Vector2 : default;
    public Vector3 Vector3 => _inlineKind == EProgramUniformInlineKind.Vector3 ? _inlineValue.Vector3 : default;
    public Vector4 Vector4 => _inlineKind == EProgramUniformInlineKind.Vector4 ? _inlineValue.Vector4 : default;
    public Matrix4x4 Matrix4x4 => _inlineKind == EProgramUniformInlineKind.Matrix4x4 ? _inlineValue.Matrix4x4 : default;
    public DVector4 DVector4 => _inlineKind == EProgramUniformInlineKind.DVector4 ? _inlineValue.DVector4 : default;
    public IVector4 IVector4 => _inlineKind == EProgramUniformInlineKind.IVector4 ? _inlineValue.IVector4 : default;
    public UVector4 UVector4 => _inlineKind == EProgramUniformInlineKind.UVector4 ? _inlineValue.UVector4 : default;

    public bool TryGetVector4(out Vector4 value)
    {
        value = Vector4;
        return HasInlineValue && Type == EShaderVarType._vec4;
    }

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

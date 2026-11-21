using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Stores ray value data. Direction normalization is not yet qualified for the selected profile.</summary>
public struct Ray : IFormattable
{
    private Vector3 _origin;
    private Vector3 _direction;

    /// <summary>Rejects construction until direction normalization has a qualified native mapping.</summary>
    public Ray(Vector3 origin, Vector3 direction)
    {
        _origin = origin;
        _direction = default;
        throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Ray..ctor(UnityEngine.Vector3,UnityEngine.Vector3)");
    }

    /// <summary>Gets or sets the stored origin.</summary>
    public Vector3 origin
    {
        readonly get => _origin;
        set => _origin = value;
    }

    /// <summary>Gets the stored direction. Changes require a qualified normalization mapping.</summary>
    public Vector3 direction
    {
        readonly get => _direction;
        set => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Ray.set_direction(UnityEngine.Vector3)");
    }

    /// <summary>Evaluates the stored ray with scalar and vector arithmetic.</summary>
    public readonly Vector3 GetPoint(float distance) => _origin + _direction * distance;

    /// <summary>Reports unsupported until source-compatible formatting is available.</summary>
    public override readonly string ToString()
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Ray.ToString()");

    /// <summary>Reports unsupported until source-compatible formatting is available.</summary>
    public readonly string ToString(string? format)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Ray.ToString(System.String)");

    /// <summary>Reports unsupported until source-compatible formatting is available.</summary>
    public readonly string ToString(string? format, IFormatProvider? formatProvider)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Ray.ToString(System.String,System.IFormatProvider)");
}

using XREngine.UnityScriptHost.Execution;

namespace UnityEngine;

/// <summary>Stores quaternion components. Rotation operations report unsupported until qualified.</summary>
[Serializable]
public struct Quaternion
{
    public float x;
    public float y;
    public float z;
    public float w;

    /// <summary>Stores the supplied quaternion components.</summary>
    public Quaternion(float x, float y, float z, float w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public static Quaternion identity => new(0f, 0f, 0f, 1f);

    /// <summary>Combines rotations after a qualified implementation is available.</summary>
    public static Quaternion operator *(Quaternion left, Quaternion right)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Quaternion.op_Multiply(UnityEngine.Quaternion,UnityEngine.Quaternion)");

    /// <summary>Rotates a vector after a qualified implementation is available.</summary>
    public static Vector3 operator *(Quaternion rotation, Vector3 point)
        => throw UnityCompatibilityRuntime.Unsupported("UnityEngine.Quaternion.op_Multiply(UnityEngine.Quaternion,UnityEngine.Vector3)");
}

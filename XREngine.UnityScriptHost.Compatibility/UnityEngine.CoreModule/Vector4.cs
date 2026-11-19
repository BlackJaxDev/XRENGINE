namespace UnityEngine;

/// <summary>Stores four scalar components for Unity source data.</summary>
[Serializable]
public struct Vector4
{
    public float x;
    public float y;
    public float z;
    public float w;

    /// <summary>Stores the supplied scalar components.</summary>
    public Vector4(float x, float y, float z, float w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public static Vector4 zero => new(0f, 0f, 0f, 0f);
    public static Vector4 one => new(1f, 1f, 1f, 1f);

    public static Vector4 operator +(Vector4 left, Vector4 right)
        => new(left.x + right.x, left.y + right.y, left.z + right.z, left.w + right.w);
    public static Vector4 operator -(Vector4 left, Vector4 right)
        => new(left.x - right.x, left.y - right.y, left.z - right.z, left.w - right.w);
    public static Vector4 operator *(Vector4 value, float scalar)
        => new(value.x * scalar, value.y * scalar, value.z * scalar, value.w * scalar);
    public static Vector4 operator *(float scalar, Vector4 value) => value * scalar;
    public static Vector4 operator /(Vector4 value, float scalar)
        => new(value.x / scalar, value.y / scalar, value.z / scalar, value.w / scalar);
}

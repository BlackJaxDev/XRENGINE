namespace UnityEngine;

/// <summary>Stores three scalar components for Unity source data.</summary>
[Serializable]
public struct Vector3
{
    public float x;
    public float y;
    public float z;

    /// <summary>Stores the supplied scalar components.</summary>
    public Vector3(float x, float y, float z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public static Vector3 zero => new(0f, 0f, 0f);
    public static Vector3 one => new(1f, 1f, 1f);
    public static Vector3 up => new(0f, 1f, 0f);
    public static Vector3 down => new(0f, -1f, 0f);
    public static Vector3 left => new(-1f, 0f, 0f);
    public static Vector3 right => new(1f, 0f, 0f);
    public static Vector3 forward => new(0f, 0f, 1f);
    public static Vector3 back => new(0f, 0f, -1f);

    public static Vector3 operator +(Vector3 left, Vector3 right) => new(left.x + right.x, left.y + right.y, left.z + right.z);
    public static Vector3 operator -(Vector3 left, Vector3 right) => new(left.x - right.x, left.y - right.y, left.z - right.z);
    public static Vector3 operator -(Vector3 value) => new(-value.x, -value.y, -value.z);
    public static Vector3 operator *(Vector3 value, float scalar) => new(value.x * scalar, value.y * scalar, value.z * scalar);
    public static Vector3 operator *(float scalar, Vector3 value) => value * scalar;
    public static Vector3 operator /(Vector3 value, float scalar) => new(value.x / scalar, value.y / scalar, value.z / scalar);

    /// <summary>Computes the scalar dot product without engine object access.</summary>
    public static float Dot(Vector3 left, Vector3 right) => left.x * right.x + left.y * right.y + left.z * right.z;
}

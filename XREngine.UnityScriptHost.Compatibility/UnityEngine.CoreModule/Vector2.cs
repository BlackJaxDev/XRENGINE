namespace UnityEngine;

/// <summary>Stores two scalar components for Unity source data.</summary>
[Serializable]
public struct Vector2
{
    public float x;
    public float y;

    /// <summary>Stores the supplied scalar components.</summary>
    public Vector2(float x, float y)
    {
        this.x = x;
        this.y = y;
    }

    public static Vector2 zero => new(0f, 0f);
    public static Vector2 one => new(1f, 1f);
    public static Vector2 up => new(0f, 1f);
    public static Vector2 down => new(0f, -1f);
    public static Vector2 left => new(-1f, 0f);
    public static Vector2 right => new(1f, 0f);

    public static Vector2 operator +(Vector2 left, Vector2 right) => new(left.x + right.x, left.y + right.y);
    public static Vector2 operator -(Vector2 left, Vector2 right) => new(left.x - right.x, left.y - right.y);
    public static Vector2 operator *(Vector2 value, float scalar) => new(value.x * scalar, value.y * scalar);
    public static Vector2 operator *(float scalar, Vector2 value) => value * scalar;
    public static Vector2 operator /(Vector2 value, float scalar) => new(value.x / scalar, value.y / scalar);
}

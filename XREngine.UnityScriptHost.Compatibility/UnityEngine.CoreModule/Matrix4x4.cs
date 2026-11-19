namespace UnityEngine;

/// <summary>Stores four columns of scalar data. It does not apply a scene transform.</summary>
[Serializable]
public struct Matrix4x4
{
    public float m00;
    public float m10;
    public float m20;
    public float m30;
    public float m01;
    public float m11;
    public float m21;
    public float m31;
    public float m02;
    public float m12;
    public float m22;
    public float m32;
    public float m03;
    public float m13;
    public float m23;
    public float m33;

    /// <summary>Stores the supplied columns in column-major field order.</summary>
    public Matrix4x4(Vector4 column0, Vector4 column1, Vector4 column2, Vector4 column3)
    {
        m00 = column0.x;
        m10 = column0.y;
        m20 = column0.z;
        m30 = column0.w;
        m01 = column1.x;
        m11 = column1.y;
        m21 = column1.z;
        m31 = column1.w;
        m02 = column2.x;
        m12 = column2.y;
        m22 = column2.z;
        m32 = column2.w;
        m03 = column3.x;
        m13 = column3.y;
        m23 = column3.z;
        m33 = column3.w;
    }

    /// <summary>Gets a stored column. Valid indices are zero through three.</summary>
    public readonly Vector4 GetColumn(int index)
        => index switch
        {
            0 => new(m00, m10, m20, m30),
            1 => new(m01, m11, m21, m31),
            2 => new(m02, m12, m22, m32),
            3 => new(m03, m13, m23, m33),
            _ => throw new IndexOutOfRangeException("A matrix column index must be between zero and three."),
        };

    public static Matrix4x4 zero => default;
    public static Matrix4x4 identity => new(new(1, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0), new(0, 0, 0, 1));
}

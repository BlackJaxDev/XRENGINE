namespace UnityEngine;

/// <summary>Stores red, green, blue, and alpha channels for Unity source data.</summary>
[Serializable]
public struct Color
{
    public float r;
    public float g;
    public float b;
    public float a;

    /// <summary>Stores the supplied channels without color-space conversion.</summary>
    public Color(float r, float g, float b, float a = 1f)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    public static Color white => new(1f, 1f, 1f);
    public static Color black => new(0f, 0f, 0f);
    public static Color clear => new(0f, 0f, 0f, 0f);
    public static Color red => new(1f, 0f, 0f);
    public static Color green => new(0f, 1f, 0f);
    public static Color blue => new(0f, 0f, 1f);
}

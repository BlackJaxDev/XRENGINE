using System.Numerics;

namespace XREngine.Rendering.PostProcessing;

/// <summary>One parameter of an authored post-process stage.</summary>
public sealed class AuthoredPostProcessParameter
{
    /// <summary>Gets or sets the parameter name. Shaders and commands read the value by this name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the label that the camera editor shows, or an empty string to show the name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the value type.</summary>
    public PostProcessParameterKind Kind { get; set; } = PostProcessParameterKind.Float;

    /// <summary>
    /// Gets or sets the default value as up to four numbers. A bool uses the first number (not zero is true), an
    /// integer rounds the first number, and a vector uses the first two, three, or four numbers. This is the asset
    /// default that a new camera starts with.
    /// </summary>
    public float[] Default { get; set; } = [0f, 0f, 0f, 0f];

    /// <summary>Gets or sets the minimum value for the editor slider, or null.</summary>
    public float? Min { get; set; }

    /// <summary>Gets or sets the maximum value for the editor slider, or null.</summary>
    public float? Max { get; set; }

    /// <summary>Gets or sets the editor step, or null.</summary>
    public float? Step { get; set; }

    /// <summary>Gets or sets whether a vector parameter is a color.</summary>
    public bool IsColor { get; set; }

    /// <summary>Returns the default as a value of <see cref="Kind"/>.</summary>
    public object GetDefaultValue()
    {
        float x = Component(0);
        return Kind switch
        {
            PostProcessParameterKind.Bool => x != 0f,
            PostProcessParameterKind.Int => (int)MathF.Round(x),
            PostProcessParameterKind.Vector2 => new Vector2(x, Component(1)),
            PostProcessParameterKind.Vector3 => new Vector3(x, Component(1), Component(2)),
            PostProcessParameterKind.Vector4 => new Vector4(x, Component(1), Component(2), Component(3)),
            _ => x,
        };
    }

    /// <summary>Stores a value of <see cref="Kind"/> as the default.</summary>
    public void SetDefaultValue(object value)
        => Default = value switch
        {
            bool b => [b ? 1f : 0f, 0f, 0f, 0f],
            int i => [i, 0f, 0f, 0f],
            float f => [f, 0f, 0f, 0f],
            Vector2 v => [v.X, v.Y, 0f, 0f],
            Vector3 v => [v.X, v.Y, v.Z, 0f],
            Vector4 v => [v.X, v.Y, v.Z, v.W],
            _ => throw new ArgumentException(string.Concat("Type ", value?.GetType().Name ?? "null", " is not a post-process parameter type."), nameof(value)),
        };

    /// <summary>Returns a deep copy.</summary>
    public AuthoredPostProcessParameter Clone()
    {
        AuthoredPostProcessParameter copy = (AuthoredPostProcessParameter)MemberwiseClone();
        copy.Default = [.. Default];
        return copy;
    }

    private float Component(int index)
        => Default is not null && index < Default.Length ? Default[index] : 0f;
}

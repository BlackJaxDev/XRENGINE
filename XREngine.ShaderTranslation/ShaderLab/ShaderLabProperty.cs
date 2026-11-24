namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Describes one entry of a ShaderLab <c>Properties</c> block.
/// </summary>
public sealed class ShaderLabProperty
{
    /// <summary>Gets the shader variable name, for example <c>_MainTex</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the inspector display text.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the declared property type.</summary>
    public required ShaderLabPropertyKind Kind { get; init; }

    /// <summary>Gets the raw attribute text of each <c>[...]</c> prefix, without brackets.</summary>
    public required IReadOnlyList<string> Attributes { get; init; }

    /// <summary>Gets the minimum of a <c>Range</c> property. It is zero for other kinds.</summary>
    public float RangeMin { get; init; }

    /// <summary>Gets the maximum of a <c>Range</c> property. It is zero for other kinds.</summary>
    public float RangeMax { get; init; }

    /// <summary>Gets the numeric default. Scalars use only the first component.</summary>
    public float[] DefaultValue { get; init; } = [];

    /// <summary>Gets the default texture name, for example <c>white</c> or <c>bump</c>. It is null for non-texture kinds.</summary>
    public string? DefaultTexture { get; init; }

    /// <summary>Gets the one-based source line of the property name.</summary>
    public int Line { get; init; }

    /// <summary>Returns true when an attribute has the given name, with or without arguments.</summary>
    public bool HasAttribute(string name)
    {
        foreach (string attribute in Attributes)
        {
            ReadOnlySpan<char> head = attribute.AsSpan();
            int open = head.IndexOf('(');
            if (open >= 0)
                head = head[..open];
            if (head.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

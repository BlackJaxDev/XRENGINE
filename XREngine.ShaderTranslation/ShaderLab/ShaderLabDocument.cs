namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Holds the parsed structure of one ShaderLab <c>.shader</c> file.
/// </summary>
public sealed class ShaderLabDocument
{
    /// <summary>Gets the shader name from the <c>Shader "..."</c> declaration.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the source path used in diagnostics and line directives.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Gets the material properties in source order.</summary>
    public required IReadOnlyList<ShaderLabProperty> Properties { get; init; }

    /// <summary>Gets the include blocks declared at shader level, in source order.</summary>
    public required IReadOnlyList<ShaderLabProgramBlock> IncludeBlocks { get; init; }

    /// <summary>Gets the subshaders in source order.</summary>
    public required IReadOnlyList<ShaderLabSubShader> SubShaders { get; init; }

    /// <summary>Gets the <c>Fallback</c> shader name, or null for none or <c>Fallback Off</c>.</summary>
    public string? Fallback { get; init; }

    /// <summary>Gets the <c>CustomEditor</c> class name, or null.</summary>
    public string? CustomEditor { get; init; }

    /// <summary>Returns the property with the given name, or null.</summary>
    public ShaderLabProperty? FindProperty(string name)
    {
        foreach (ShaderLabProperty property in Properties)
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
                return property;

        return null;
    }
}

namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Describes one <c>SubShader</c> block.
/// </summary>
public sealed class ShaderLabSubShader
{
    /// <summary>Gets the subshader tags. Keys compare without case.</summary>
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    /// <summary>Gets the <c>LOD</c> value, or null when the subshader has none.</summary>
    public int? Lod { get; init; }

    /// <summary>Gets the subshader-level render state commands. Each pass inherits them.</summary>
    public required IReadOnlyList<ShaderLabCommand> Commands { get; init; }

    /// <summary>Gets the include blocks declared at subshader level, in source order.</summary>
    public required IReadOnlyList<ShaderLabProgramBlock> IncludeBlocks { get; init; }

    /// <summary>
    /// Gets a program block written directly in the subshader. Unity uses this form for surface shaders
    /// (<c>#pragma surface</c>). It is null when the subshader has none.
    /// </summary>
    public ShaderLabProgramBlock? SurfaceProgram { get; init; }

    /// <summary>Gets the passes in source order.</summary>
    public required IReadOnlyList<ShaderLabPass> Passes { get; init; }

    /// <summary>Gets the one-based source line of the <c>SubShader</c> keyword.</summary>
    public int Line { get; init; }
}

namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Maps HLSL vertex input semantics to the engine-wide vertex attribute locations.
/// </summary>
/// <remarks>
/// The mesh renderers bind each vertex buffer to the shader input with the same name, for example
/// <c>Position</c> or <c>TexCoord0</c> (see <c>Build/CommonAssets/Shaders/Uber/UberShader.vert</c>).
/// A translated vertex stage must use the same names and locations.
/// </remarks>
public static class EngineVertexAttributes
{
    /// <summary>Returns the engine location of a semantic, or -1 when the engine has no matching attribute.</summary>
    /// <param name="semantic">The upper-case semantic name without index.</param>
    /// <param name="semanticIndex">The semantic index.</param>
    public static int GetLocation(string semantic, int semanticIndex)
        => (semantic, semanticIndex) switch
        {
            ("POSITION", 0) or ("SV_POSITION", 0) => 0,
            ("NORMAL", 0) => 1,
            ("TANGENT", 0) => 2,
            ("TEXCOORD", >= 0 and <= 3) => 3 + semanticIndex,
            ("COLOR", 0) => 7,
            _ => -1,
        };

    /// <summary>Returns the engine attribute name of a semantic, or null when the engine has no matching attribute.</summary>
    /// <param name="semantic">The upper-case semantic name without index.</param>
    /// <param name="semanticIndex">The semantic index.</param>
    /// <remarks>The name is the mesh buffer name, so the mesh renderers bind the buffer to the input.</remarks>
    public static string? GetName(string semantic, int semanticIndex)
        => (semantic, semanticIndex) switch
        {
            ("POSITION", 0) or ("SV_POSITION", 0) => "Position",
            ("NORMAL", 0) => "Normal",
            ("TANGENT", 0) => "Tangent",
            ("TEXCOORD", 0) => "TexCoord0",
            ("TEXCOORD", 1) => "TexCoord1",
            ("TEXCOORD", 2) => "TexCoord2",
            ("TEXCOORD", 3) => "TexCoord3",
            ("COLOR", 0) => "Color0",
            _ => null,
        };
}

namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Maps HLSL vertex input semantics to the engine-wide vertex attribute locations.
/// </summary>
/// <remarks>
/// The mesh renderer binds vertex buffers to fixed locations for every shader
/// (see <c>Build/CommonAssets/Shaders/Uber/UberShader.vert</c>). A translated vertex stage must use the same locations.
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
}

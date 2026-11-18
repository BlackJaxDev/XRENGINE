namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Describes one vertex shader input from compiler reflection.
/// </summary>
/// <param name="Semantic">The upper-case semantic name, for example <c>TEXCOORD</c>.</param>
/// <param name="SemanticIndex">The semantic index, for example 1 for <c>TEXCOORD1</c>.</param>
/// <param name="Location">The location that the compiler assigned in SPIR-V.</param>
/// <param name="ComponentCount">The vector width of the input.</param>
public sealed record VertexInputReflection(string Semantic, int SemanticIndex, int Location, int ComponentCount)
{
    /// <summary>Returns the semantic with its index, for example <c>TEXCOORD1</c>, or the bare name for index zero.</summary>
    public string FullSemantic => SemanticIndex == 0 ? Semantic : Semantic + SemanticIndex;
}

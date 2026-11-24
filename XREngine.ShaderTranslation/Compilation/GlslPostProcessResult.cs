namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Holds post-processed GLSL and the problems found while rewriting it.
/// </summary>
/// <param name="Glsl">The rewritten GLSL text.</param>
/// <param name="UnsupportedVertexInputs">Vertex input semantics that have no engine attribute.</param>
/// <param name="Notes">Rewrite decisions worth reporting, such as split sampler names.</param>
public sealed record GlslPostProcessResult(string Glsl, IReadOnlyList<string> UnsupportedVertexInputs, IReadOnlyList<string> Notes);

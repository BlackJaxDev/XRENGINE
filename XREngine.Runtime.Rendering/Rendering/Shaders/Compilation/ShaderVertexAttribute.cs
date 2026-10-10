namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>A cooked vertex input and the engine stream semantic that supplies it.</summary>
public sealed record ShaderVertexAttribute(int Location, int Offset, string Format, string Semantic);

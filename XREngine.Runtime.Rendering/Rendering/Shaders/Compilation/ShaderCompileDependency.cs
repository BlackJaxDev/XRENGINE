namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Immutable content snapshot for one source dependency used by a shader compilation.
/// </summary>
public readonly record struct ShaderCompileDependency(string Path, string Sha256);

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>A frontend failure retaining diagnostics at original authored source locations.</summary>
public sealed class ShaderCompilationException(string message, IReadOnlyList<ShaderCompileDiagnostic> diagnostics)
    : InvalidOperationException(message)
{
    public IReadOnlyList<ShaderCompileDiagnostic> Diagnostics { get; } = diagnostics.ToArray();
}

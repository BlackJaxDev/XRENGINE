namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Physical matrix-major order reported by SPIR-V decorations.
/// </summary>
public enum ShaderAbiMatrixOrder
{
    None,
    RowMajor,
    ColumnMajor,
}
